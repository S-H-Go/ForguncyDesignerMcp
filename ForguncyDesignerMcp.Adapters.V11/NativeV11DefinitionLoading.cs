using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Loading a table leaf through the normal colleague can load built-in data.
// This narrowly reviewed path loads only a definition tree proved to contain
// folders and no files/table leaves in every native/source/cache location.
internal static class NativeV11DefinitionLoading
{
    private const string Code = "NativeDefinitionLoadRequiresReview";
    private static readonly ConditionalWeakTable<object, object> QuarantinedRoots = new();

    internal static async Task EnsureEmptyTableRootAsync(NativeV11Session session)
    {
        session.VerifyContext();
        var route = session.Route(ModuleKind.Table);
        RequireNotQuarantined(route.Root);
        if (NativeV11Reflection.Get(route.Root, "Loaded") is true) return;
        var initial = session.Context.CaptureSnapshot();
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var tableSet = NativeV11Reflection.Get(settings, "TableSet")!;
        var sheets = NativeV11Reflection.Get(session.Workbook, "Sheets") as IList ?? throw Review("The native workbook sheet collection is unavailable.");
        var sheetCount = sheets.Count;
        void Verify()
        {
            session.VerifyContext();
            var current = session.Context.CaptureSnapshot();
            if (current.Target != initial.Target || current.Revision != initial.Revision || current.CurrentUser != initial.CurrentUser ||
                current.InCollaborationMode != initial.InCollaborationMode || current.HasUnsavedChanges || current.Busy ||
                NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is not false ||
                !ReferenceEquals(NativeV11Reflection.Get(settings, "TableSet"), tableSet) ||
                !ReferenceEquals(NativeV11Reflection.Get(tableSet, "Root"), route.Root) ||
                NativeV11Reflection.Get(tableSet, "IsUpdating") is true ||
                !ReferenceEquals(NativeV11Reflection.Get(session.Workbook, "Sheets"), sheets) || sheets.Count != sheetCount)
                throw Review("The native document, clean edit epoch, table owner or worksheet set changed while loading definitions.");
        }
        Verify();
        await EnsureCoreAsync(session.Context.DesignerAssembly, route, session.SaveFile,
            session.SourcePath, session.LocalPath, session.LastPath, Verify);
    }

    // The component fixture calls this same native loading core with real
    // TableSet/colleague/mediator/loader/memento and isolated physical paths.
    // verifyContext is a host-state check, not a replacement native backend.
    internal static async Task EnsureCoreAsync(Assembly designer, NativeV11Route route, object saveFile,
        string source, string local, string last, Action verifyContext)
    {
        var profile = V11BuildProfile.Resolve(designer);
        var newer = profile.Version == "11.0.104.0";
        RequireNotQuarantined(route.Root);
        if (route.Kind != ModuleKind.Table || route.Folder != "Tables/" ||
            route.Colleague.GetType() != designer.GetType(newer ? "he" : "gz", true) ||
            route.Mediator.GetType() != designer.GetType(newer ? "dt" : "dp", true) ||
            route.Loader.GetType() != designer.GetType(newer ? "du" : "dq", true))
            throw Review("The table definition-loading route differs from this exact V11 build.");
        if (NativeV11Reflection.Get(route.Root, "Loaded") is true) return;
        if (NativeV11Reflection.Get(route.Root, "Loaded") is not false)
            throw Review("The native table root has no verified unloaded state.");
        if (Items(route.Root).Count != 0)
            throw Review("An unloaded table root already has native children; its partial load requires review.");
        var tableSet = NativeV11Reflection.Get(Items(route.Root), "TableSet") ?? throw Review("The native table root has no table-set owner.");
        var memento = NativeV11Reflection.Get(route.Loader, "Memento") ?? throw Review("The native table memento is unavailable.");
        var slotMethod = NativeV11Reflection.Method(route.Loader.GetType(), "GetSaveData", memento);
        if (slotMethod.DeclaringType != route.Loader.GetType() || slotMethod.ReturnType != designer.GetType(newer ? "ci" : "ch", true) || slotMethod.IsStatic)
            throw Review("The native readonly table-memento accessor differs from this V11 build.");
        var cacheOnly = NativeV11Reflection.Method(route.Loader.GetType(), newer ? "po" : "pg");
        if (cacheOnly.DeclaringType != designer.GetType(newer ? "bs" : "br", true) || cacheOnly.ReturnType != typeof(bool) || cacheOnly.IsStatic)
            throw Review("The native table cache-only loading predicate differs from this V11 build.");
        var baselineSource = NativeV11SourceRevision.Capture(source);
        // New -> SaveAs may leave SaveLoadFileBase._path as the native empty
        // unbound marker. The session callback independently binds the actual
        // document CurrentFilePath; never substitute a guessed path here.
        var documentPath = NativeV11Reflection.Call(saveFile, "GetDocumentPath") as string;
        if (documentPath is null || (documentPath.Length != 0 && !SamePath(documentPath, source)))
            throw Review("The native save-file document path is neither the bound source nor the empty unbound marker.");
        Proof? baseline = null;
        void Verify()
        {
            verifyContext();
            if (!ReferenceEquals(NativeV11Reflection.Get(route.Colleague, "Root"), route.Root) ||
                !ReferenceEquals(NativeV11Reflection.Get(route.Colleague, "Mediator"), route.Mediator) ||
                !ReferenceEquals(NativeV11Reflection.Get(route.Mediator, "SaveDataColleague"), route.Loader) ||
                !ReferenceEquals(NativeV11Reflection.Get(route.Loader, "SaveLoadFile"), saveFile) ||
                !ReferenceEquals(NativeV11Reflection.Get(route.Loader, "Memento"), memento) ||
                !ReferenceEquals(NativeV11Reflection.Get(tableSet, "Root"), route.Root) ||
                !ReferenceEquals(NativeV11Reflection.Get(Items(route.Root), "TableSet"), tableSet) ||
                NativeV11Reflection.Get(tableSet, "IsUpdating") is true ||
                Normalize(NativeV11Reflection.Get(route.Loader, "RootPath") as string) != "Tables/" ||
                Normalize(NativeV11Reflection.Call(route.Colleague, "GetKey", route.Root) as string) != "Tables/" ||
                !SameDocumentPath(NativeV11Reflection.Call(saveFile, "GetDocumentPath") as string, documentPath, source) ||
                !SamePath(NativeV11Reflection.Get(saveFile, "LocalPath") as string, local) ||
                !SamePath(NativeV11Reflection.Get(saveFile, "LastPath") as string, last) ||
                cacheOnly.Invoke(route.Loader, null) is not true)
                throw Review("The native table owner, save session or cache-only loading state is not verified.");
            RequireFolderTree(route.Root, tableSet, true);
            var proof = CaptureProof(route, source, local, last, memento, slotMethod);
            if (NativeV11SourceRevision.Capture(source) != baselineSource || baseline is not null &&
                (proof.SourceDirectories != baseline.SourceDirectories || proof.LocalDirectories != baseline.LocalDirectories ||
                 proof.LastDirectories != baseline.LastDirectories || proof.Cache != baseline.Cache || proof.Pending != baseline.Pending ||
                 !ReferenceEquals(proof.PendingSlot, baseline.PendingSlot) || !ReferenceEquals(proof.PendingItems, baseline.PendingItems)))
                throw Review("The source, native table registry or readonly memento changed while loading definitions.");
            baseline ??= proof;
        }
        var registrationAttempted = false;
        var failureStage = "baseline";
        try
        {
            Verify();
            // bs/br.LoadAsync takes its cache-search branch because the actual
            // native po/pg predicate above is true. It does not call InitAsync,
            // table CustomItemAsync, a worksheet manager or a DB adapter.
            failureStage = "native-save-data-load";
            var dataTask = NativeV11Reflection.Call(route.Loader, "LoadAsync", "Tables\\", true) as Task
                ?? throw Review("The native save-data reader returned no awaitable task.");
            await dataTask;
            var loadedData = NativeV11Reflection.Get(dataTask, "Result") as IDictionary
                ?? throw Review("The native save-data reader returned no verified dictionary.");
            var loadedFolders = FolderKeys(Registry(loadedData));
            failureStage = "post-save-data-verification";
            Verify();
            var expectedFolders = FolderKeys(Registry((IDictionary)route.Loader));
            if (!loadedFolders.SequenceEqual(expectedFolders, StringComparer.Ordinal))
                throw Review("The actual native save-data result omitted or added table directory definitions.");
            // No non-folder can reach the native GetDirectoriesAsync
            // CustomItemAsync branch. Pass the real native result onward.
            failureStage = "native-tree-load";
            var treeTask = NativeV11Reflection.Call(route.Colleague, "LoadByModelAsync", route.Root, true) as Task
                ?? throw Review("The native table-tree reader returned no awaitable task.");
            await treeTask;
            var children = NativeV11Reflection.Get(treeTask, "Result") as IList
                ?? throw Review("The native table-tree reader returned no verified list.");
            foreach (var child in children.Cast<object>()) RequireFolderTree(child, null, false);
            if (!TreePaths(children).SequenceEqual(expectedFolders, StringComparer.Ordinal))
                throw Review("The actual native tree result differs from the complete table directory registry.");
            Verify();
            if (Items(route.Root).Count != 0) throw Review("The native table root gained children before its native load registration.");
            failureStage = "native-folder-registration";
            registrationAttempted = true;
            NativeV11Reflection.Call(route.Colleague, "FolderAddItem", route.Root, children, true);
            Verify();
            if (NativeV11Reflection.Get(route.Root, "Loaded") is not true ||
                !Items(route.Root).Cast<object>().SequenceEqual(children.Cast<object>(), ReferenceEqualityComparer.Instance) ||
                !TreePaths(Items(route.Root)).SequenceEqual(expectedFolders, StringComparer.Ordinal))
                throw Review("The native folder-load hook did not retain its actual result and loaded state.");
        }
        catch (Exception error)
        {
            // Do not undo native read registration over a concurrent user edit.
            // A failed final verification instead quarantines this exact root;
            // a later Loaded=true shortcut must not trust the failed attempt.
            if (registrationAttempted) QuarantinedRoots.GetValue(route.Root, _ => new object());
            if (error is BridgeException) throw;
            while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException!;
            throw Review("The reviewed empty table definition load did not complete; stage=" + failureStage + "; " + SafeFailure(error));
        }
    }

    internal static string SafeFailure(Exception error)
    {
        var root = error.GetBaseException();
        var methods = new StackTrace(root, false).GetFrames()?.Take(5).Select(frame => frame.GetMethod()).Where(method => method is not null)
            .Select(method => (method!.DeclaringType?.FullName ?? "<unknown>") + "/" + method.Name).ToArray() ?? [];
        return "exception=" + root.GetType().Name + "; hresult=0x" + unchecked((uint)root.HResult).ToString("X8") +
            "; methods=" + string.Join(",", methods);
    }

    private sealed record Proof(string SourceDirectories, string LocalDirectories, string LastDirectories,
        string Cache, string Pending, object? PendingSlot, object? PendingItems);

    private static Proof CaptureProof(NativeV11Route route, string source, string local, string last, object memento, MethodInfo slotMethod)
    {
        var sourceFolders = DirectoriesOnly(source);
        var localFolders = DirectoriesOnly(local);
        var lastFolders = DirectoriesOnly(last);
        var cache = route.Loader as IDictionary ?? throw Review("The native table save registry is unavailable.");
        var cached = Registry(cache);
        var pendingSlot = slotMethod.Invoke(route.Loader, [memento]); // pure ldfld Tables; never GetValidateSaveData
        IDictionary? pendingItems = null;
        var pending = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (pendingSlot is not null)
        {
            pendingItems = pendingSlot.GetType().GetField("SaveDataItems", NativeV11Reflection.Flags)?.GetValue(pendingSlot) as IDictionary
                ?? throw Review("The native readonly table-memento dictionary is unavailable.");
            pending = Registry(pendingItems);
        }
        if (sourceFolders.Concat(localFolders).Concat(lastFolders).Concat(pending.Keys).Any(folder => folder != "Tables/" && !cached.ContainsKey(folder)))
            throw Review("A physical/pending table folder is absent from the native table save registry.");
        var names = NativeV11Reflection.Call(route.Loader, "GetAllTableName") as IEnumerable
            ?? throw Review("The native table filename enumeration is unavailable.");
        if (names.Cast<object>().Any()) throw Review("The native save-file registry contains table leaf filenames.");
        return new(Digest(sourceFolders), Digest(localFolders), Digest(lastFolders), Digest(cached.Select(pair => pair.Key + "\0" + pair.Value)),
            Digest(pending.Select(pair => pair.Key + "\0" + pair.Value)), pendingSlot, pendingItems);
    }

    private static SortedDictionary<string, string> Registry(IDictionary entries)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in entries)
        {
            if (entry.Key is not string key || entry.Value?.GetType().FullName != "Forguncy.SaveLoad.FolderSaveData")
                throw Review("The native table registry or memento contains a table leaf or an unknown entry.");
            key = CanonicalDirectory(key);
            if (!result.TryAdd(key, NativeV11Reflection.Get(entry.Value, "Name") as string ?? ""))
                throw Review("The native table registry contains ambiguous directory keys.");
        }
        return result;
    }

    private static SortedSet<string> DirectoriesOnly(string project)
    {
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(project))
        {
            if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0) throw Review("Table loading cannot traverse a directory link.");
            var root = V11Safety.Under(project, "Tables");
            if (File.Exists(root)) throw Review("A file occupies the native Tables directory.");
            void Visit(string path, string key)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Review("Table loading cannot traverse a directory link.");
                directories.Add(CanonicalDirectory(key));
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if ((File.GetAttributes(child) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != FileAttributes.Directory)
                        throw Review("The native Tables source/cache subtree contains a file or a link.");
                    Visit(child, key + Path.GetFileName(child) + "/");
                }
            }
            if (Directory.Exists(root)) Visit(root, "Tables/");
            return directories;
        }
        if (!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc", StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0)
            throw Review("The native table source is not a verified directory or FGCC file.");
        using var archive = ZipFile.OpenRead(project);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var parts = name.TrimEnd('/').Split('/');
            if (name.Length == 0 || name.StartsWith('/') || name.Contains(':') || name.Contains('\0') ||
                parts.Any(part => part is "" or "." or "..") || !seen.Add(name.TrimEnd('/')) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                throw Review("The project archive contains unsafe, duplicate or linked entries.");
            if (name.Equals("Tables", StringComparison.OrdinalIgnoreCase)) throw Review("An archive file occupies the native Tables directory.");
            if (!name.StartsWith("Tables/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith('/') || entry.Length != 0) throw Review("The native Tables archive subtree contains a file.");
            for (var index = 1; index <= parts.Length; index++) directories.Add(CanonicalDirectory(string.Join('/', parts.Take(index)) + "/"));
        }
        return directories;
    }

    private static void RequireFolderTree(object folder, object? tableSet, bool checkOwners)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object node)
        {
            if (node.GetType().FullName != "Forguncy.Model.Tables.TableFolder" || !visited.Add(node))
                throw Review("The native table tree contains a table leaf, an unknown model or a cycle.");
            var entries = Items(node);
            // The native root uses TableFolder(TableSet). The native table
            // colleague's CreateFolder uses TableFolder(), whose Items owns
            // no TableSet. Native collection events set each child.Owner;
            // they do not propagate a TableSet into nested collections.
            if (checkOwners && (ReferenceEquals(node, folder)
                    ? !ReferenceEquals(NativeV11Reflection.Get(entries, "TableSet"), tableSet)
                    : NativeV11Reflection.Get(entries, "TableSet") is not null))
                throw Review("A native table folder has an unverified table-set owner.");
            foreach (var child in entries.Cast<object>())
            {
                if (checkOwners && !ReferenceEquals(NativeV11Reflection.Get(child, "Owner"), entries))
                    throw Review("A native table folder has a different collection owner.");
                Visit(child);
            }
        }
        Visit(folder);
    }
    private static string[] FolderKeys(SortedDictionary<string, string> registry) => registry.Keys.Where(key => key != "Tables/").ToArray();
    private static string[] TreePaths(IList children)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object folder, string parent)
        {
            if (!visited.Add(folder) || NativeV11Reflection.Get(folder, "Name") is not string name)
                throw Review("The native table folder result contains a repeated or unnamed model.");
            V11Safety.Address(new(ModuleKind.Folder, name));
            var path = CanonicalDirectory(parent + name + "/");
            if (!paths.Add(path)) throw Review("The native table tree contains ambiguous folder paths.");
            foreach (var child in Items(folder).Cast<object>()) Visit(child, path);
        }
        foreach (var child in children.Cast<object>()) Visit(child, "Tables/");
        return paths.ToArray();
    }
    private static IList Items(object folder) => NativeV11Reflection.Get(folder, "Items") as IList ?? throw Review("The native table folder item collection is unavailable.");
    private static string CanonicalDirectory(string key)
    {
        key = key.Replace('\\', '/');
        if (!key.StartsWith("Tables/", StringComparison.Ordinal) || !key.EndsWith('/') || key.Contains(':')) throw Review("A native table directory key is outside its verified root.");
        foreach (var part in key.TrimEnd('/').Split('/')) V11Safety.Address(new(ModuleKind.Folder, part));
        return key;
    }
    private static string? Normalize(string? key) => key is null ? null : key.Replace('\\', '/').TrimEnd('/') + "/";
    private static string Digest(IEnumerable<string> values) => NativeV11Session.Hash(Encoding.UTF8.GetBytes(string.Join("\n", values.Order(StringComparer.Ordinal))));
    private static bool SamePath(string? left, string right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) && Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    internal static bool SameDocumentPath(string? actual, string initial, string source) =>
        initial.Length == 0 ? actual == "" : SamePath(actual, source) && SamePath(actual, initial);
    internal static bool IsQuarantined(object root) => QuarantinedRoots.TryGetValue(root, out _);

    internal static void RequireNotQuarantined(object root)
    {
        if (QuarantinedRoots.TryGetValue(root, out _))
            throw Review("A previous table-definition registration failed final verification; this native root requires a fresh reviewed document context.");
    }
    private static BridgeException Review(string detail) => new(Code, detail);
}
