using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Freeze every other native owner in an existing shared-table checkout plan.
// The target and the changed SQLite database have their own mutation proof.
internal sealed class NativeV11TableSharedSaveJournal
{
    private sealed record Entry(ModuleAddress Address, object Model, string Primary, JsonElement Expected,
        IReadOnlyDictionary<string, byte[]> Files);
    private readonly Entry[] entries;
    private readonly string? unchangedDatabase;
    private readonly (string Hash, long Length, bool Valid)? databaseBefore;
    internal IReadOnlyList<string> Paths { get; }

    private NativeV11TableSharedSaveJournal(Entry[] entries, string? database,
        (string Hash, long Length, bool Valid)? databaseBefore, string[] paths)
    { this.entries = entries; unchangedDatabase = database; this.databaseBefore = databaseBefore; Paths = paths; }

    internal static NativeV11TableSharedSaveJournal Capture(NativeV11Session session, NativeV11Collaboration.Plan plan,
        ModuleAddress target, bool databaseChanges)
    {
        session.VerifyContext();
        if (!plan.AllowSharedTableDatabase || !ReferenceEquals(plan.Session, session) || !Directory.Exists(session.SourcePath))
            throw new InvalidOperationException("The native shared-table owner plan is unavailable.");
        var database = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!,
            "get_LocalAppDBSqliteFilePath") as string ?? throw new InvalidOperationException("The native table database is unavailable.");
        var databaseName = Path.GetFileName(database);
        if (string.IsNullOrEmpty(databaseName)) throw new InvalidOperationException("The native table database name is invalid.");
        var entries = new List<Entry>();
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in plan.Owners.Where(x => x.Address != target && x.State == "Edit"))
        {
            if (target.Kind == ModuleKind.BusinessProcess && owner.Address.Kind is (ModuleKind.ApplicationSettings or ModuleKind.Theme))
                continue; // FirstProcessJournal independently freezes and verifies these singleton models.
            if (owner.Address.Kind is not (ModuleKind.Table or ModuleKind.View))
                throw new InvalidOperationException("A shared table file overlaps a non-table owner whose native save projection is not established.");
            var item = session.Find(owner.Address);
            var ownerName = NativeV11Reflection.Get(owner.SimpleModule, "ModuleName") as string
                ?? throw new InvalidOperationException("A native table owner has no identity.");
            if (!plan.Files.TryGetValue(ownerName, out var nativeFiles)) throw new InvalidOperationException("A native table owner has no file group.");
            var paths = nativeFiles.Where(path => !path.Equals(databaseName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (!paths.Contains(item.RelativePath, StringComparer.Ordinal)) throw new InvalidOperationException("A native table owner omits its definition file.");
            var source = NativeV11PendingSave.ReadFiles(session.SourcePath, paths);
            if (source.Values.Any(value => value is null)) throw new InvalidOperationException("A shared native owner source file is absent.");
            var expected = NativeV11SourceMetadata.Capture(session, owner.Address);
            if (!V11Safety.Equivalent(expected, V11Safety.Body(source[item.RelativePath]!)))
                throw new InvalidOperationException("A shared native table model differs from its saved source.");
            foreach (var path in paths) allPaths.Add(path);
            entries.Add(new(owner.Address, item.Model, item.RelativePath, expected,
                source.ToDictionary(pair => pair.Key, pair => pair.Value!, StringComparer.Ordinal)));
        }
        var touchesDatabase = plan.Files.Values.SelectMany(x => x).Any(path => path.Equals(databaseName, StringComparison.OrdinalIgnoreCase));
        (string Hash, long Length, bool Valid)? fingerprint = null;
        if (touchesDatabase)
        {
            allPaths.Add(databaseName);
            if (!databaseChanges)
            {
                var sourceDatabase = V11Safety.Under(session.SourcePath, databaseName);
                if (!File.Exists(sourceDatabase) || !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(sourceDatabase))
                    throw new InvalidOperationException("The shared source database is unavailable or has an active journal.");
                using var stream = File.OpenRead(sourceDatabase);
                fingerprint = NativeV11BuiltInTableLifecycle.DatabaseFingerprint(stream);
                if (!fingerprint.Value.Valid) throw new InvalidDataException("The shared source database header is inconsistent.");
            }
        }
        return new(entries.ToArray(), touchesDatabase && !databaseChanges ? databaseName : null, fingerprint, allPaths.ToArray());
    }

    internal void VerifyMemory(NativeV11Session session)
    {
        foreach (var entry in entries)
        {
            var item = session.Find(entry.Address);
            if (!ReferenceEquals(item.Model, entry.Model) || !V11Safety.Equivalent(entry.Expected, NativeV11SourceMetadata.Capture(session, entry.Address)))
                throw new BridgeException("MemoryRevisionChanged", "共享数据库的其他原生表或视图已变化。 ");
        }
    }

    internal bool SourceMatches(NativeV11Session session)
    {
        VerifyMemory(session);
        foreach (var entry in entries)
        {
            if (!SourceFilesMatch(session.SourcePath, session.LastPath, entry.Primary, entry.Expected, entry.Files)) return false;
        }
        if (unchangedDatabase is not null)
        {
            var source = V11Safety.Under(session.SourcePath, unchangedDatabase);
            var stage = V11Safety.Under(session.LastPath, unchangedDatabase);
            if (!File.Exists(source) || !File.Exists(stage) || !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(source) ||
                !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(stage)) return false;
            using var sourceFile = File.OpenRead(source); using var stageFile = File.OpenRead(stage);
            if (NativeV11BuiltInTableLifecycle.DatabaseFingerprint(sourceFile) != databaseBefore ||
                NativeV11BuiltInTableLifecycle.DatabaseFingerprint(stageFile) != databaseBefore) return false;
        }
        return true;
    }

    internal static bool SourceFilesMatch(string project, string staging, string primary, JsonElement expected,
        IReadOnlyDictionary<string, byte[]> files)
    {
        var source = NativeV11PendingSave.ReadFiles(project, files.Keys);
        var stage = NativeV11PendingSave.ReadFiles(staging, files.Keys);
        foreach (var pair in files)
        {
            if (source[pair.Key] is not byte[] a || stage[pair.Key] is not byte[] b) return false;
            if (pair.Key.Equals(primary, StringComparison.OrdinalIgnoreCase))
            {
                if (!V11Safety.Equivalent(expected, V11Safety.Body(a)) || !V11Safety.Equivalent(V11Safety.Body(a), V11Safety.Body(b))) return false;
            }
            else if (!pair.Value.AsSpan().SequenceEqual(a) || !a.AsSpan().SequenceEqual(b)) return false;
        }
        return files.ContainsKey(primary);
    }
}
