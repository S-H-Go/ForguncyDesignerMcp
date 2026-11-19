using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Collections;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Physical static subtree only. The caller must separately validate the native
// tree, owner locks, descendant references and the complete source journal.
// This component never deletes running process instances or the process DB.
internal static class NativeV11StaticFolderLifecycle
{
    internal sealed record Snapshot(string Root, IReadOnlyDictionary<string, string> Files, IReadOnlySet<string> Directories);

    internal sealed record CacheWitness(object Model, bool Present, object? Value, object[]? Path);
    internal sealed record DeletePlan(NativeV11Session Session, ModuleAddress Address, NativeV11FolderInventory Inventory,
        Snapshot Snapshot, object Manager, IDictionary PathCache, IReadOnlyList<CacheWitness> Cache, IReadOnlyDictionary<string, byte[]?> Database);

    // The caller still must prove native executor/owner checkout and capture its
    // complete source journal. Repeat this preflight immediately before the
    // physical delete; a saved source witness is not mutation authorization.
    internal static DeletePlan PrepareDelete(NativeV11Session session, ModuleAddress address)
    {
        session.VerifyContext();
        var route = new NativeV11Folders(session).OwnerRoute(address);
        if (route.Kind is not (ModuleKind.Report or ModuleKind.BusinessProcess))
            throw new InvalidDataException("A native report or process folder is required.");
        NativeV11StaticFiles.VerifyConfigOwner(session);
        var binding = NativeV11StaticFiles.Resolve(session);
        var root = route.Kind == ModuleKind.Report ? "Reports" : "Process";
        if (!binding.Folders.Any(value => value.Replace('\\', '/').Trim('/') == root))
            throw new InvalidOperationException("The bound native static saver does not own this root.");
        var inventory = NativeV11FolderInventory.Capture(session, address, checkReferences: true);
        var manager = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType(route.Kind == ModuleKind.Report
            ? "Forguncy.Common.ReportsManager" : "Forguncy.Common.BusinessProcessManager", true)!, "get_Instance")
            ?? throw new InvalidOperationException("The native static manager is unavailable.");
        var cache = VerifyManager(session, route.Kind, manager);
        var snapshot = Capture(session.LocalPath, inventory.Root);
        if (!snapshot.Files.Keys.Select(path => inventory.Root + "/" + path).ToHashSet(StringComparer.Ordinal)
                .SetEquals(inventory.Files) || !snapshot.Directories.Select(path => path.Length == 0 ? inventory.Root : inventory.Root + "/" + path)
                .ToHashSet(StringComparer.Ordinal).SetEquals(inventory.Directories))
            throw new InvalidOperationException("The static subtree differs from the reviewed native owner inventory.");
        var witnesses = inventory.Items.Select(item =>
        {
            var present = cache.Contains(item.Model); var value = present ? cache[item.Model] : null;
            return new CacheWitness(item.Model, present, value, value is IEnumerable sequence ? sequence.Cast<object>().ToArray() : null);
        }).ToArray();
        return new(session, address, inventory, snapshot, manager, cache, witnesses, DatabaseState(session));
    }

    internal static Action Delete(DeletePlan plan)
    {
        var current = PrepareDelete(plan.Session, plan.Address);
        if (!ReferenceEquals(current.Manager, plan.Manager) || !ReferenceEquals(current.PathCache, plan.PathCache) ||
            !current.Inventory.Items.Select(item => item.Model).SequenceEqual(plan.Inventory.Items.Select(item => item.Model), ReferenceEqualityComparer.Instance) ||
            !current.Inventory.Files.SequenceEqual(plan.Inventory.Files, StringComparer.Ordinal) ||
            !current.Inventory.Directories.SequenceEqual(plan.Inventory.Directories, StringComparer.Ordinal) ||
            !CacheMatches(plan.Cache, current.Cache))
            throw new InvalidOperationException("The native static folder plan changed before deletion.");
        RequireSame(plan.Snapshot, current.Snapshot);
        RequireDatabase(plan.Database, current.Database);
        var undo = DeleteCore(plan.Session.Context.DesignerAssembly, plan.Session.LocalPath, plan.Snapshot,
            plan.PathCache, plan.Inventory.Items.Select(item => item.Model).ToArray());
        try { RequireDatabase(plan.Database, DatabaseState(plan.Session)); }
        catch (Exception primary)
        {
            try { undo(); } catch (Exception rollback) { throw new AggregateException("Static delete and rollback failed; do not save.", primary, rollback); }
            throw;
        }
        return () => { undo(); RequireDatabase(plan.Database, DatabaseState(plan.Session)); };
    }

    private static bool CacheMatches(IReadOnlyList<CacheWitness> left, IReadOnlyList<CacheWitness> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => ReferenceEquals(pair.First.Model, pair.Second.Model) &&
            pair.First.Present == pair.Second.Present && ReferenceEquals(pair.First.Value, pair.Second.Value) &&
            (pair.First.Path is null ? pair.Second.Path is null : pair.Second.Path is not null &&
                pair.First.Path.SequenceEqual(pair.Second.Path, ReferenceEqualityComparer.Instance)));

    private static IDictionary VerifyManager(NativeV11Session session, ModuleKind kind, object manager)
    {
        var report = kind == ModuleKind.Report;
        if (report ? NativeV11Reflection.Get(manager, "InARDesignerMode") is not false ||
                     NativeV11Reflection.Get(manager, "IsReportDataSourceEditingMode") is not false ||
                     manager.GetType().GetField("_checkingDirtyState", NativeV11Reflection.Flags)?.GetValue(manager) is true ||
                     manager.GetType().GetField("_updateLocalRootPathTask", NativeV11Reflection.Flags)?.GetValue(manager) is Task { IsCompleted: false }
                   : NativeV11Reflection.Get(manager, "InBPMDesignerMode") is not false ||
                     NativeV11Reflection.Get(manager, "CurrentActiveBPM") is not null ||
                     NativeV11Reflection.Get(manager, "DeployProcessServerTask") is Task { IsCompleted: false })
            throw new InvalidOperationException("The native report or process editor is active or busy.");
        var local = NativeV11Reflection.Get(manager, report ? "LocalReportsPath" : "LocalProcessPath") as string;
        if (local is null || !Path.GetFullPath(local).TrimEnd('\\', '/').Equals(
                Path.GetFullPath(V11Safety.Under(session.LocalPath, report ? "Reports" : "Process")).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native static manager belongs to another project.");
        return NativeV11Reflection.Get(manager, report ? "ReportPathCache" : "ProcessPathCache") as IDictionary
            ?? throw new InvalidOperationException("The native static manager path cache is unavailable.");
    }

    private static IReadOnlyDictionary<string, byte[]?> DatabaseState(NativeV11Session session)
    {
        var database = NativeV11ProcessInitialization.Database;
        var names = new[] { database, database + "-wal", database + "-shm", database + "-journal" };
        return new[] { session.LocalPath, session.LastPath, session.SourcePath }.SelectMany((root, index) =>
            NativeV11PendingSave.ReadFiles(root, names).Select(pair => new KeyValuePair<string, byte[]?>(index + ":" + pair.Key, pair.Value)))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }

    private static void RequireDatabase(IReadOnlyDictionary<string, byte[]?> before, IReadOnlyDictionary<string, byte[]?> after)
    {
        if (before.Count != after.Count || before.Any(pair => !after.TryGetValue(pair.Key, out var value) ||
            (pair.Value is null ? value is not null : value is null || !pair.Value.AsSpan().SequenceEqual(value))))
            throw new InvalidOperationException("Process database or sidecar changed during the static folder operation; do not save.");
    }

    // Purely the native static cache part of deletion. This does not mutate
    // the model tree, workflow data, runtime files, or collaboration state.
    // The callback is valid only until save and refuses to overwrite any
    // unreviewed replacement. The caller must mark the document unsavable if
    // this callback or an AggregateException reports incomplete rollback.
    internal static Action DeleteCore(Assembly designer, string local, Snapshot expected, IDictionary cache, IReadOnlyList<object> models)
    {
        _ = V11BuildProfile.Resolve(designer);
        RequireSame(expected, Capture(local, expected.Root));
        if (models.Distinct(ReferenceEqualityComparer.Instance).Count() != models.Count)
            throw new InvalidDataException("Duplicate native static model identity.");
        var root = V11Safety.Under(local, expected.Root);
        var originals = expected.Files.ToDictionary(pair => pair.Key,
            pair => File.ReadAllBytes(V11Safety.Under(root, pair.Key)), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in originals)
            if (Convert.ToHexString(SHA256.HashData(pair.Value)) != expected.Files[pair.Key])
                throw new InvalidOperationException("The native static subtree changed while capturing rollback bytes.");
        var cached = models.Where(cache.Contains).ToDictionary(model => model, model => cache[model], ReferenceEqualityComparer.Instance);
        var removed = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var common = AssemblyLoadContext.GetLoadContext(designer)!.Assemblies.Single(a => a.GetName().Name == "CommonUtilities");
        var utilities = common.GetType("Forguncy.Common.FileUtilities", true)!;
        var restored = false;
        void Restore()
        {
            if (restored) return;
            if (File.Exists(root)) throw new InvalidOperationException("A file replaced the static subtree during rollback.");
            if (Directory.Exists(root))
            {
                var remaining = Capture(local, expected.Root);
                if (remaining.Directories.Any(path => !expected.Directories.Contains(path)) ||
                    remaining.Files.Any(pair => !expected.Files.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
                    throw new InvalidOperationException("The static subtree gained unreviewed data during rollback.");
            }
            foreach (var model in models)
                if (cache.Contains(model) && (removed.Contains(model) || !cached.TryGetValue(model, out var value) || !ReferenceEquals(cache[model], value)))
                    throw new InvalidOperationException("The native static path cache changed during rollback.");
            foreach (var directory in expected.Directories.OrderBy(path => path.Length))
                Directory.CreateDirectory(directory.Length == 0 ? root : V11Safety.Under(root, directory));
            foreach (var pair in originals)
            {
                var path = V11Safety.Under(root, pair.Key);
                if (File.Exists(path)) continue;
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(pair.Value);
            }
            foreach (var model in removed) cache.Add(model, cached[model]);
            RequireSame(expected, Capture(local, expected.Root));
            restored = true;
        }
        try
        {
            RequireSame(expected, Capture(local, expected.Root));
            foreach (var model in models)
            {
                if (cache.Contains(model) != cached.ContainsKey(model) || cached.TryGetValue(model, out var value) && !ReferenceEquals(cache[model], value))
                    throw new InvalidOperationException("The native static path cache changed before deletion.");
            }
            NativeV11Reflection.Static(utilities, "DeleteFolder", root);
            if (Directory.Exists(root) || File.Exists(root)) throw new InvalidOperationException("The native static folder delete was incomplete.");
            foreach (var model in cached.Keys) { cache.Remove(model); removed.Add(model); }
            return Restore;
        }
        catch (Exception primary)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new AggregateException("Native static delete and rollback failed; do not save.", primary, rollback); }
            throw;
        }
    }

    internal static Snapshot Capture(string local, string relative)
    {
        var parts = relative.Replace('\\', '/').Trim('/').Split('/');
        if (parts.Length < 2 || parts[0] is not ("Reports" or "Process") ||
            parts.Any(p => p.Length == 0 || p is "." or ".."))
            throw new InvalidDataException("A static subtree must be a non-root report or process folder.");
        var root = V11Safety.Under(local, string.Join('/', parts));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException(root);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string path, string name)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Static subtree reparse points are prohibited.");
            if (!directories.Add(name)) throw new InvalidDataException("Ambiguous static subtree directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Static subtree reparse points are prohibited.");
                var child = name.Length == 0 ? Path.GetFileName(entry) : name + "/" + Path.GetFileName(entry);
                if (Directory.Exists(entry)) Walk(entry, child);
                else if (!files.TryAdd(child, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)))))
                    throw new InvalidDataException("Ambiguous static subtree file.");
            }
        }
        Walk(root, "");
        return new(string.Join('/', parts), files, directories);
    }

    internal static Action Move(Assembly designer, string local, Snapshot expected, string destination)
    {
        _ = V11BuildProfile.Resolve(designer);
        var target = destination.Replace('\\', '/').Trim('/');
        var original = expected.Root;
        if (target.Split('/')[0] != original.Split('/')[0] || target.Equals(original, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(original + "/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Invalid static folder destination.");
        var from = V11Safety.Under(local, original); var to = V11Safety.Under(local, target);
        if (!Directory.Exists(Path.GetDirectoryName(to)!) || Directory.Exists(to) || File.Exists(to)) throw new InvalidDataException("The static destination must have an existing parent and be absent.");
        RequireSame(expected, Capture(local, original));
        var common = AssemblyLoadContext.GetLoadContext(designer)!.Assemblies.Single(a => a.GetName().Name == "CommonUtilities");
        var utilities = common.GetType("Forguncy.Common.FileUtilities", true)!;
        void NativeMove(string source, string dest)
        {
            if (Path.GetDirectoryName(source)!.Equals(Path.GetDirectoryName(dest), StringComparison.OrdinalIgnoreCase))
                NativeV11Reflection.Static(utilities, "RenameFolder", source, Path.GetFileName(dest));
            else NativeV11Reflection.Static(utilities, "MoveFolder", source, dest, false);
        }
        var restored = false;
        void Restore()
        {
            if (restored) return;
            if (Directory.Exists(from) || File.Exists(from)) throw new InvalidOperationException("The original static path is occupied during rollback.");
            RequireSame(expected, Capture(local, target));
            NativeMove(to, from);
            if (Directory.Exists(to)) throw new InvalidOperationException("The native static folder rollback retained the destination.");
            RequireSame(expected, Capture(local, original));
            restored = true;
        }
        try
        {
            NativeMove(from, to);
            if (Directory.Exists(from)) throw new InvalidOperationException("The native static folder move retained the old path.");
            RequireSame(expected, Capture(local, target));
            return Restore;
        }
        catch (Exception primary)
        {
            if (!Directory.Exists(from) && Directory.Exists(to))
            {
                try { Restore(); }
                catch (Exception rollback) { throw new AggregateException("Native static move and guarded rollback failed; do not save.", primary, rollback); }
            }
            throw;
        }
    }

    internal static void RequireSame(Snapshot expected, Snapshot actual)
    {
        if (!expected.Directories.SetEquals(actual.Directories) || expected.Files.Count != actual.Files.Count ||
            expected.Files.Any(pair => !actual.Files.TryGetValue(pair.Key, out var hash) || hash != pair.Value))
            throw new InvalidOperationException("The native static folder contents changed after review.");
    }
}
