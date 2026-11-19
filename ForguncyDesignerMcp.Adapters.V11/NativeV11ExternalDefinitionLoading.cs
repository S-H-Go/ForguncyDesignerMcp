using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// A separate, external-binding-only extension to the folder-only loader.
// PreReadAsync is component-verifiable. The normal CustomItemAsync path in
// EnsureExternalTableRootAsync requires the real native application/workbook.
internal static class NativeV11ExternalDefinitionLoading
{
    private const string Code = "NativeExternalDefinitionLoadRequiresReview";
    private static readonly ConditionalWeakTable<object, object> QuarantinedRoots = new();

    internal static bool IsQuarantined(object root) => QuarantinedRoots.TryGetValue(root, out _);

    internal static void RequireNotQuarantined(object root)
    {
        if (QuarantinedRoots.TryGetValue(root, out _))
            throw Review("A previous external-definition load failed after native customization began; reopen the document before using this table root.");
    }

    internal static async Task EnsureExternalTableRootAsync(NativeV11Session session)
    {
        session.VerifyContext();
        var route = session.Route(ModuleKind.Table);
        RequireNotQuarantined(route.Root); // Must precede any Loaded shortcut.
        if (NativeV11Reflection.Get(route.Root, "Loaded") is true) return;
        var initial = session.Context.CaptureSnapshot();
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var tableSet = NativeV11Reflection.Get(settings, "TableSet")!;
        var designer = session.Context.DesignerAssembly;
        var singleton = NativeV11Reflection.Static(designer.GetType("o", true)!, "get_Instance")!;
        var cache = NativeV11Reflection.Static(designer.GetType(session.Profile.GenerationCacheType, true)!, "get_Instance")!;
        var modules = ((IEnumerable)NativeV11Reflection.Get(route.Colleague, "ModuleTypes")!).Cast<object>().ToArray();
        var dirty = modules.Select(module => (Module: module,
            Save: NativeV11Reflection.Call(cache, "IsSaveDataDirty", module),
            Metadata: NativeV11Reflection.Call(cache, "IsMetadateDirty", module))).ToArray();
        var workbookProperty = session.Profile.Version == "11.0.104.0" ? "db" : "cx";
        void VerifyContext()
        {
            session.VerifyContext();
            var current = session.Context.CaptureSnapshot();
            if (current.Target != initial.Target || current.Revision != initial.Revision || current.CurrentUser != initial.CurrentUser ||
                current.InCollaborationMode != initial.InCollaborationMode || current.HasUnsavedChanges || current.Busy ||
                NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is not false ||
                !ReferenceEquals(NativeV11Reflection.Get(settings, "TableSet"), tableSet) ||
                !ReferenceEquals(NativeV11Reflection.Get(tableSet, "Root"), route.Root) ||
                !ReferenceEquals(NativeV11Reflection.Get(singleton, workbookProperty), session.Workbook) ||
                !ReferenceEquals(NativeV11Reflection.Static(designer.GetType(session.Profile.GenerationCacheType, true)!, "get_Instance"), cache) ||
                dirty.Any(item => !Equals(item.Save, NativeV11Reflection.Call(cache, "IsSaveDataDirty", item.Module)) ||
                                  !Equals(item.Metadata, NativeV11Reflection.Call(cache, "IsMetadateDirty", item.Module))))
                throw Review("The real native workbook, clean edit epoch, table owner or generation state changed during external definition loading.");
        }
        VerifyContext();
        var proof = await PreReadAsync(designer, route, session.SaveFile, session.SourcePath, session.LocalPath, session.LastPath, VerifyContext);
        var sheets = new WorksheetProof(session.Workbook, proof);
        var customizationStarted = false;
        try
        {
            proof.Verify();
            sheets.VerifyBefore();
            if (Items(route.Root).Count != 0 || NativeV11Reflection.Get(route.Root, "Loaded") is not false)
                throw Review("The unloaded table root changed before native customization.");
            customizationStarted = true;
            // This is the real unmodified colleague. Its CustomItemAsync reads
            // local display preferences and attaches TableInfo. The reviewed
            // arq/aqe branch calls LoadDataTable only for IsBuiltInTable=true;
            // every native leaf has already been proved false below.
            var task = NativeV11Reflection.Call(route.Colleague, "LoadByModelAsync", route.Root, true) as Task
                ?? throw Review("The native external tree reader returned no awaitable task.");
            await task;
            var children = NativeV11Reflection.Get(task, "Result") as IList
                ?? throw Review("The native external tree reader returned no native result list.");
            var models = proof.VerifyTree(children, false);
            sheets.VerifyAfter(models);
            proof.Verify();
            if (Items(route.Root).Count != 0 || NativeV11Reflection.Get(route.Root, "Loaded") is not false)
                throw Review("The native table root changed before its actual load registration.");
            NativeV11Reflection.Call(route.Colleague, "FolderAddItem", route.Root, children, true);
            proof.Verify();
            if (NativeV11Reflection.Get(route.Root, "Loaded") is not true ||
                !Items(route.Root).Cast<object>().SequenceEqual(children.Cast<object>(), ReferenceEqualityComparer.Instance))
                throw Review("The native folder load hook did not retain its own external result list.");
            models = proof.VerifyTree(Items(route.Root), true);
            sheets.VerifyAfter(models);
        }
        catch (Exception error)
        {
            // CustomItemAsync can have changed native worksheet attachments
            // before an async failure. Do not roll back across user edits or
            // pretend Loaded=false is sufficient to make that state reusable.
            if (customizationStarted) QuarantinedRoots.GetValue(route.Root, _ => new object());
            if (error is BridgeException) throw;
            while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException!;
            throw Review("The reviewed external definition load did not complete (" + error.GetType().Name + ").");
        }
    }

    internal sealed class ReadProof
    {
        private readonly Assembly designer;
        private readonly NativeV11Route route;
        private readonly object saveFile;
        private readonly object tableSet;
        private readonly object memento;
        private readonly MethodInfo slotMethod;
        private readonly MethodInfo cacheOnly;
        private readonly string source;
        private readonly string local;
        private readonly string last;
        private readonly Action verifyContext;
        private readonly Snapshot baseline;
        private readonly string sourceRevision;
        internal IReadOnlyDictionary<string, object> Models { get; }
        internal IReadOnlyList<string> Keys { get; }

        internal ReadProof(Assembly designer, NativeV11Route route, object saveFile, object tableSet, object memento,
            MethodInfo slotMethod, MethodInfo cacheOnly, string source, string local, string last, Action verifyContext,
            Snapshot baseline, string sourceRevision, Dictionary<string, object> models)
        {
            this.designer = designer; this.route = route; this.saveFile = saveFile; this.tableSet = tableSet;
            this.memento = memento; this.slotMethod = slotMethod; this.cacheOnly = cacheOnly;
            this.source = source; this.local = local; this.last = last; this.verifyContext = verifyContext;
            this.baseline = baseline; this.sourceRevision = sourceRevision;
            Models = models; Keys = baseline.Cache.Keys.Where(key => key != "Tables/").ToArray();
        }

        internal void Verify()
        {
            RequireNotQuarantined(route.Root);
            verifyContext();
            VerifyRoute(route, saveFile, tableSet, memento, cacheOnly, source, local, last);
            var current = Capture(designer, route, source, local, last, memento, slotMethod);
            if (NativeV11SourceRevision.Capture(source) != sourceRevision || !baseline.Same(current))
                throw Review("The complete source/cache table definition set or readonly native memento changed during external loading.");
        }

        internal Dictionary<string, object> VerifyTree(IList children, bool registered)
        {
            var found = new Dictionary<string, object>(StringComparer.Ordinal);
            var all = new HashSet<string>(StringComparer.Ordinal);
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Visit(object node, string prefix, IList? parent)
            {
                if (!seen.Add(node) || NativeV11Reflection.Get(node, "Name") is not string name)
                    throw Review("The native external tree contains a repeated or unnamed model.");
                V11Safety.Address(new(ModuleKind.Table, name));
                if (parent is null ? NativeV11Reflection.Get(node, "Owner") is not null : !ReferenceEquals(NativeV11Reflection.Get(node, "Owner"), parent))
                    throw Review("A returned native table/folder belongs to a different parent collection.");
                if (IsFolder(node))
                {
                    var key = Canonical(prefix + name + "/", true);
                    if (!all.Add(key) || NativeV11Reflection.Get(node, "Loaded") is not true ||
                        NativeV11Reflection.Get(Items(node), "TableSet") is not null)
                        throw Review("A native external result folder has an unverified loaded state or nested collection owner.");
                    foreach (var child in Items(node).Cast<object>()) Visit(child, key, Items(node));
                }
                else
                {
                    var key = Canonical(prefix + name + ".json", false);
                    if (!all.Add(key) || !Models.TryGetValue(key, out var original))
                        throw Review("The native external result contains an unexpected table leaf.");
                    RequireExternal(designer, node, key);
                    var before = JsonNode.Parse(SerializeModel(designer, route, original))!.AsObject();
                    var after = JsonNode.Parse(SerializeModel(designer, route, node))!.AsObject();
                    // The real arp/aqd user-local preference reader is allowed
                    // to change this one display property; it must not dirty or
                    // rewrite the physical project/memento while loading.
                    before.Remove("ShowBuiltInFields"); after.Remove("ShowBuiltInFields");
                    if (!JsonNode.DeepEquals(before, after))
                        throw Review("Native customization changed binding metadata beyond its local display preference.");
                    found.Add(key, node);
                }
            }
            foreach (var child in children.Cast<object>()) Visit(child, "Tables/", registered ? Items(route.Root) : null);
            if (!all.Order(StringComparer.Ordinal).SequenceEqual(Keys, StringComparer.Ordinal) || found.Count != Models.Count)
                throw Review("The returned external tree differs from the complete native definition key set.");
            return found;
        }
    }

    // No global document, worksheet manager, CustomItemAsync or permission
    // provider is accessed here. These are the real native loader and mediator.
    internal static async Task<ReadProof> PreReadAsync(Assembly designer, NativeV11Route route, object saveFile,
        string source, string local, string last, Action verifyContext)
    {
        try { return await PreReadCoreAsync(designer, route, saveFile, source, local, last, verifyContext); }
        catch (BridgeException) { throw; }
        catch (Exception error)
        {
            while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException!;
            throw Review("Native external pre-reading could not be proved safe (" + error.GetType().Name + ").");
        }
    }

    private static async Task<ReadProof> PreReadCoreAsync(Assembly designer, NativeV11Route route, object saveFile,
        string source, string local, string last, Action verifyContext)
    {
        var newer = V11BuildProfile.Resolve(designer).Version == "11.0.104.0";
        RequireNotQuarantined(route.Root);
        if (route.Kind != ModuleKind.Table || route.Folder != "Tables/" ||
            route.Colleague.GetType() != designer.GetType(newer ? "he" : "gz", true) ||
            route.Mediator.GetType() != designer.GetType(newer ? "dt" : "dp", true) ||
            route.Loader.GetType() != designer.GetType(newer ? "du" : "dq", true) ||
            NativeV11Reflection.Get(route.Root, "Loaded") is not false || Items(route.Root).Count != 0 ||
            ReadImportMode(route))
            throw Review("An exact native, non-importing, unloaded and empty Table root is required for external pre-reading.");
        var tableSet = NativeV11Reflection.Get(Items(route.Root), "TableSet") ?? throw Review("The native Table root has no actual TableSet owner.");
        var memento = NativeV11Reflection.Get(route.Loader, "Memento") ?? throw Review("The native table memento is unavailable.");
        var slotMethod = NativeV11Reflection.Method(route.Loader.GetType(), "GetSaveData", memento);
        var cacheOnly = NativeV11Reflection.Method(route.Loader.GetType(), newer ? "po" : "pg");
        if (slotMethod.DeclaringType != route.Loader.GetType() || slotMethod.ReturnType != designer.GetType(newer ? "ci" : "ch", true) ||
            cacheOnly.DeclaringType != designer.GetType(newer ? "bs" : "br", true) || cacheOnly.ReturnType != typeof(bool) || cacheOnly.IsStatic)
            throw Review("The native cache-only reader or readonly table memento contract differs from this V11 build.");
        verifyContext();
        VerifyRoute(route, saveFile, tableSet, memento, cacheOnly, source, local, last);
        var revision = NativeV11SourceRevision.Capture(source);
        var baseline = Capture(designer, route, source, local, last, memento, slotMethod);
        var dataTask = NativeV11Reflection.Call(route.Loader, "LoadAsync", "Tables\\", true) as Task
            ?? throw Review("The native external save-data reader returned no task.");
        await dataTask;
        var loaded = NativeV11Reflection.Get(dataTask, "Result") as IDictionary ?? throw Review("The native external save-data reader returned no dictionary.");
        var loadedEntries = Registry(designer, route, loaded);
        if (!SameEntries(baseline.Cache, loadedEntries, true))
            throw Review("The actual native save-data result omitted, substituted or added a table/folder definition.");
        var modelTask = NativeV11Reflection.Call(route.Mediator, "LoadAsync", "Tables\\", true) as Task
            ?? throw Review("The actual native table mediator returned no task.");
        await modelTask;
        var nativeModels = NativeV11Reflection.Get(modelTask, "Result") as IDictionary ?? throw Review("The actual native table mediator returned no dictionary.");
        var allModelKeys = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in nativeModels)
        {
            if (entry.Key is not string raw || entry.Value is not object model) throw Review("The native model dictionary contains an invalid entry.");
            var folder = IsFolder(model); var key = Canonical(raw, folder);
            if (!allModelKeys.Add(key) || !baseline.Cache.ContainsKey(key)) throw Review("The native model dictionary contains an unexpected or ambiguous path.");
            if (NativeV11Reflection.Get(model, "Owner") is not null)
                throw Review("A native pre-read model is already owned by another table collection.");
            if (folder)
            {
                if (Items(model).Count != 0 || NativeV11Reflection.Get(Items(model), "TableSet") is not null)
                    throw Review("The detached native table folder is already populated or has an unexpected TableSet owner.");
                continue;
            }
            RequireExternal(designer, model, key);
            if (!names.Add((string)NativeV11Reflection.Get(model, "Name")!)) throw Review("The native table registry has duplicate global table identities.");
            var projected = NativeV11SourceMetadata.Serialize(designer, route, model);
            if (!V11Safety.Equivalent(projected, V11Safety.Body(baseline.Source.Files[key])))
                throw Review("The actual native table mapper discarded or changed persisted binding metadata.");
            models.Add(key, model);
        }
        if (!allModelKeys.Order(StringComparer.Ordinal).SequenceEqual(baseline.Cache.Keys, StringComparer.Ordinal) || models.Count == 0)
            throw Review("A complete nonempty external-only native model set is required; empty/folder-only projects use their separate loader.");
        var proof = new ReadProof(designer, route, saveFile, tableSet, memento, slotMethod, cacheOnly,
            source, local, last, verifyContext, baseline, revision, models);
        proof.Verify();
        if (Items(route.Root).Count != 0 || NativeV11Reflection.Get(route.Root, "Loaded") is not false)
            throw Review("Native pre-reading unexpectedly registered a table tree.");
        return proof;
    }

    private static void VerifyRoute(NativeV11Route route, object saveFile, object tableSet, object memento,
        MethodInfo cacheOnly, string source, string local, string last)
    {
        if (!ReferenceEquals(NativeV11Reflection.Get(route.Colleague, "Root"), route.Root) ||
            !ReferenceEquals(NativeV11Reflection.Get(route.Colleague, "Mediator"), route.Mediator) ||
            !ReferenceEquals(NativeV11Reflection.Get(route.Mediator, "SaveDataColleague"), route.Loader) ||
            !ReferenceEquals(NativeV11Reflection.Get(route.Loader, "SaveLoadFile"), saveFile) ||
            !ReferenceEquals(NativeV11Reflection.Get(route.Loader, "Memento"), memento) ||
            !ReferenceEquals(NativeV11Reflection.Get(tableSet, "Root"), route.Root) ||
            !ReferenceEquals(NativeV11Reflection.Get(Items(route.Root), "TableSet"), tableSet) ||
            NativeV11Reflection.Get(tableSet, "IsUpdating") is not false || ReadImportMode(route) ||
            Canonical(NativeV11Reflection.Get(route.Loader, "RootPath") as string ?? "", true) != "Tables/" ||
            Canonical(NativeV11Reflection.Call(route.Colleague, "GetKey", route.Root) as string ?? "", true) != "Tables/" ||
            !SamePath(NativeV11Reflection.Call(saveFile, "GetDocumentPath") as string, source) ||
            !SamePath(NativeV11Reflection.Get(saveFile, "LocalPath") as string, local) ||
            !SamePath(NativeV11Reflection.Get(saveFile, "LastPath") as string, last) || cacheOnly.Invoke(route.Loader, null) is not true)
            throw Review("The actual native table owner, save session, import mode or cache-only reader state is unverified.");
    }

    internal sealed record Entry(object Value, string Json, bool Folder);
    internal sealed record Image(SortedDictionary<string, byte[]> Files, string[] Directories)
    {
        internal string Fingerprint => Digest(Directories.Select(key => "D:" + key).Concat(Files.Select(pair => "F:" + pair.Key + ":" + NativeV11Session.Hash(pair.Value))));
    }
    internal sealed record Snapshot(Image Source, Image Local, Image Last, SortedDictionary<string, Entry> Cache,
        object? PendingSlot, object? PendingItems, SortedDictionary<string, Entry> Pending)
    {
        internal bool Same(Snapshot other) => Source.Fingerprint == other.Source.Fingerprint && Local.Fingerprint == other.Local.Fingerprint &&
            Last.Fingerprint == other.Last.Fingerprint && SameEntries(Cache, other.Cache, true) && SameEntries(Pending, other.Pending, true) &&
            ReferenceEquals(PendingSlot, other.PendingSlot) && ReferenceEquals(PendingItems, other.PendingItems);
    }

    private static Snapshot Capture(Assembly designer, NativeV11Route route, string source, string local, string last, object memento, MethodInfo slotMethod)
    {
        var sourceImage = ReadImage(source); var localImage = ReadImage(local); var lastImage = ReadImage(last);
        if (sourceImage.Fingerprint != localImage.Fingerprint || sourceImage.Fingerprint != lastImage.Fingerprint)
            throw Review("Source, LocalData and LastSaveFiles do not contain the same complete physical table definitions.");
        var cache = Registry(designer, route, route.Loader as IDictionary ?? throw Review("The actual native table save registry is unavailable."));
        var expected = sourceImage.Directories.Where(key => key != "Tables/").Concat(sourceImage.Files.Keys).Order(StringComparer.Ordinal).ToArray();
        if (!expected.SequenceEqual(cache.Keys.Where(key => key != "Tables/"), StringComparer.Ordinal))
            throw Review("Physical table definitions and the complete native table save registry have different keys.");
        foreach (var (key, bytes) in sourceImage.Files)
        {
            var body = V11Safety.Body(bytes);
            NativeV11DefinitionWriter.CheckTypes(body, designer.GetName().Version!);
            if (!V11Safety.Equivalent(body, NativeV11SourceMetadata.SerializeSaveData(designer, route, cache[key].Value)))
                throw Review("The native cached table save-data differs from its complete physical source metadata.");
        }
        var slot = slotMethod.Invoke(route.Loader, [memento]); // readonly native field accessor; never GetValidateSaveData
        var items = slot?.GetType().GetField("SaveDataItems", NativeV11Reflection.Flags)?.GetValue(slot);
        if (slot is not null && items is not IDictionary) throw Review("The readonly native table memento has no verified dictionary.");
        var pending = items is IDictionary dictionary ? Registry(designer, route, dictionary) : new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        if (pending.Any(pair => !cache.TryGetValue(pair.Key, out var value) || value.Json != pair.Value.Json))
            throw Review("The native table memento contains a differing or missing pending definition.");
        var nativeNames = (NativeV11Reflection.Call(route.Loader, "GetAllTableName") as IEnumerable)?.Cast<object>().OfType<string>().Order(StringComparer.Ordinal).ToArray()
            ?? throw Review("The native table filename reader is unavailable.");
        var names = sourceImage.Files.Keys.Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal).ToArray();
        if (!nativeNames.SequenceEqual(names, StringComparer.Ordinal)) throw Review("The actual native table filename enumeration is incomplete.");
        return new(sourceImage, localImage, lastImage, cache, slot, items, pending);
    }

    private static SortedDictionary<string, Entry> Registry(Assembly designer, NativeV11Route route, IDictionary dictionary)
    {
        var modelAssembly = Assembly.Load("ServerDesignerCommon");
        var table = modelAssembly.GetType("Forguncy.SaveLoad.TableSaveData", true)!;
        var folder = modelAssembly.GetType("Forguncy.SaveLoad.FolderSaveData", true)!;
        var result = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        var insensitive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var generator = designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is not string raw || entry.Value is not object value || value.GetType() != table && value.GetType() != folder)
                throw Review("The native table dictionary contains an unknown save-data type or key.");
            var isFolder = value.GetType() == folder; var key = Canonical(raw, isFolder);
            if (!insensitive.Add(key.TrimEnd('/'))) throw Review("The native table dictionary contains ambiguous keys.");
            var json = (string)NativeV11Reflection.Static(generator, "SaveToStr", value)!;
            result.Add(key, new(value, json, isFolder));
        }
        return result;
    }
    private static bool SameEntries(SortedDictionary<string, Entry> left, SortedDictionary<string, Entry> right, bool identities) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value.Json == value.Json &&
            pair.Value.Folder == value.Folder && (!identities || ReferenceEquals(pair.Value.Value, value.Value)));

    private static void RequireExternal(Assembly designer, object model, string key)
    {
        var tableType = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.Table", true)!;
        if (model.GetType() != tableType || NativeV11Reflection.Call(model, "IsBuiltInTable") is not false)
            throw Review("A built-in, unknown or data-loading table model is present in the native definition set.");
        var name = Path.GetFileNameWithoutExtension(key);
        var kind = NativeV11Reflection.Get(model, "IsView") is true ? ModuleKind.View : ModuleKind.Table;
        try { NativeV11TableLifecycle.ValidateExternal(model, new(kind, name)); }
        catch (Exception) { throw Review("Only ordinary external table/view bindings with schema changes disabled are eligible for definition loading."); }
        var columns = NativeV11Reflection.Get(model, "Columns") as IEnumerable ?? throw Review("The external model has no native column collection.");
        foreach (var column in columns.Cast<object>())
            if (!ReferenceEquals(NativeV11Reflection.Get(column, "Owner"), columns) ||
                !ReferenceEquals(NativeV11Reflection.Get(column, "Table"), model) ||
                !ReferenceEquals(NativeV11Reflection.Call(columns, "TryGetColumn", NativeV11Reflection.Get(column, "Name")), column))
                throw Review("A native external column has an inconsistent table owner or name index.");
    }

    private sealed class WorksheetProof
    {
        private readonly object workbook;
        private readonly IList sheets;
        private readonly int active;
        private readonly Sheet[] baseline;
        private readonly Dictionary<string, Sheet> targets;
        private sealed record Sheet(object Model, string Name, object? Tag, object? TableInfo, string? TagJson, object? Loaded, object? Visible);
        internal WorksheetProof(object workbook, ReadProof proof)
        {
            this.workbook = workbook;
            sheets = NativeV11Reflection.Get(workbook, "Sheets") as IList ?? throw Review("The native workbook sheets are unavailable.");
            active = Convert.ToInt32(NativeV11Reflection.Get(workbook, "ActiveSheetIndex"));
            baseline = sheets.Cast<object>().Select(sheet => new Sheet(sheet, NativeV11Reflection.Get(sheet, "Name") as string ?? "",
                NativeV11Reflection.Get(sheet, "Tag"), Value(NativeV11Reflection.Get(sheet, "Tag"), "TableInfo"), TagJson(NativeV11Reflection.Get(sheet, "Tag")),
                NativeV11Reflection.Get(sheet, "Loaded"), NativeV11Reflection.Get(sheet, "Visible"))).ToArray();
            targets = new(StringComparer.Ordinal);
            foreach (var (key, model) in proof.Models)
            {
                var name = (string)NativeV11Reflection.Get(model, "Name")!;
                var matches = baseline.Where(sheet => sheet.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1 || matches[0].Name != name || matches[0].Loaded is not false || matches[0].Visible is not false ||
                    Value(matches[0].Tag, "TableInfo") is not null || Value(matches[0].Tag, "PageInfo") is not null || active == Array.IndexOf(baseline, matches[0]))
                    throw Review("Each external table must have one existing, unopened, unloaded native placeholder worksheet.");
                targets.Add(key, matches[0]);
            }
            if (active < 0 || active >= baseline.Length || targets.Values.Any(sheet => ReferenceEquals(sheet.Model, baseline[active].Model)) ||
                Value(baseline[active].Tag, "TableInfo") is not null)
                throw Review("An existing non-table sheet must remain active while external definitions load.");
        }
        internal void VerifyBefore() => Verify(null);
        internal void VerifyAfter(IReadOnlyDictionary<string, object> models) => Verify(models);
        private void Verify(IReadOnlyDictionary<string, object>? models)
        {
            if (!ReferenceEquals(NativeV11Reflection.Get(workbook, "Sheets"), sheets) ||
                !sheets.Cast<object>().SequenceEqual(baseline.Select(sheet => sheet.Model), ReferenceEqualityComparer.Instance) ||
                Convert.ToInt32(NativeV11Reflection.Get(workbook, "ActiveSheetIndex")) != active)
                throw Review("The native worksheet set, ordering or active sheet changed during definition loading.");
            foreach (var sheet in baseline)
            {
                var target = targets.FirstOrDefault(pair => ReferenceEquals(pair.Value, sheet));
                var tag = NativeV11Reflection.Get(sheet.Model, "Tag");
                if (NativeV11Reflection.Get(sheet.Model, "Name") as string != sheet.Name || !Equals(NativeV11Reflection.Get(sheet.Model, "Visible"), sheet.Visible) ||
                    models is null || target.Key is null)
                {
                    if (NativeV11Reflection.Get(sheet.Model, "Name") as string != sheet.Name || !Equals(NativeV11Reflection.Get(sheet.Model, "Visible"), sheet.Visible) ||
                        !ReferenceEquals(tag, sheet.Tag) || !ReferenceEquals(Value(tag, "TableInfo"), sheet.TableInfo) ||
                        !Equals(NativeV11Reflection.Get(sheet.Model, "Loaded"), sheet.Loaded) || TagJson(tag) != sheet.TagJson)
                        throw Review("An unrelated or not-yet-customized native worksheet changed during definition loading.");
                    continue;
                }
                if (!models.TryGetValue(target.Key, out var model) || NativeV11Reflection.Get(sheet.Model, "Loaded") is not true ||
                    tag is null || sheet.Tag is not null && !ReferenceEquals(tag, sheet.Tag) ||
                    !ReferenceEquals(Value(Value(tag, "TableInfo"), "Table"), model) || TagJson(tag) != sheet.TagJson)
                    throw Review("The native table worksheet attachment differs from its actual returned external model.");
            }
        }
        private static string? TagJson(object? tag)
        {
            if (tag is null) return "";
            if (tag.GetType() != Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Pages.SheetAttachData", true))
                throw Review("An external placeholder has an unknown native worksheet tag.");
            // Tag serialization is not needed for the table's model: retain
            // all backing field references except TableInfo, which arq/aqe
            // intentionally initializes. No property getter/service is run.
            var fields = tag.GetType().GetFields(NativeV11Reflection.Flags).Where(field => !field.IsStatic && field.Name != "<TableInfo>k__BackingField")
                .OrderBy(field => field.Name, StringComparer.Ordinal).Select(field => (field.Name, Value: field.GetValue(tag)))
                .Where(field => field.Value is not null).Select(field => field.Name + ":" + RuntimeHelpers.GetHashCode(field.Value!).ToString());
            return string.Join("\n", fields);
        }
        private static object? Value(object? instance, string property) => instance is null ? null : NativeV11Reflection.Get(instance, property);
    }

    // Pure worksheet guard component, without invoking global/native custom
    // loading. The production path retains its WorksheetProof across awaits.
    internal static void ValidateWorkbookBefore(object workbook, ReadProof proof) => new WorksheetProof(workbook, proof).VerifyBefore();
    internal static Action CaptureWorkbookGuard(object workbook, ReadProof proof)
    {
        var guard = new WorksheetProof(workbook, proof);
        guard.VerifyBefore();
        return guard.VerifyBefore;
    }

    private static bool ReadImportMode(NativeV11Route route)
    {
        var type = route.Colleague.GetType().Assembly.GetType("Forguncy.SaveLoad.ModelColleagueBase", true)!;
        var property = type.GetProperty("_importMode", NativeV11Reflection.Flags | BindingFlags.DeclaredOnly);
        var getter = property?.GetGetMethod(true);
        if (property?.PropertyType != typeof(bool) || getter is null || getter.IsStatic || getter.DeclaringType != type || getter.GetParameters().Length != 0)
            throw Review("The exact native readonly import-mode getter contract is unavailable.");
        return (bool)getter.Invoke(route.Colleague, null)!;
    }

    private static Image ReadImage(string project)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        var directories = new SortedSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        void DirectoryKey(string key)
        {
            key = Canonical(key, true);
            var parts = key.TrimEnd('/').Split('/');
            for (var index = 1; index <= parts.Length; index++) directories.Add(string.Join('/', parts.Take(index)) + "/");
        }
        void FileKey(string key, Func<byte[]> read, long length)
        {
            key = Canonical(key, false);
            if (!seen.Add(key) || files.Count >= 2048 || length > 16 * 1024 * 1024 || (total += length) > 64 * 1024 * 1024)
                throw Review("The physical table definitions are ambiguous or exceed the reviewed read limit.");
            var bytes = read();
            if (bytes.LongLength != length) throw Review("A physical table definition changed while it was being read.");
            files.Add(key, bytes);
            DirectoryKey(key[..(key.LastIndexOf('/') + 1)]);
        }
        if (Directory.Exists(project))
        {
            if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0) throw Review("External definition loading cannot traverse a directory link.");
            var root = V11Safety.Under(project, "Tables");
            if (File.Exists(root)) throw Review("A file occupies the native Tables directory.");
            void Visit(string path, string key)
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Review("External definition loading cannot traverse a link.");
                DirectoryKey(key);
                foreach (var child in Directory.EnumerateFileSystemEntries(path))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw Review("External definition loading cannot traverse a link.");
                    var childKey = key + Path.GetFileName(child);
                    if ((attributes & FileAttributes.Directory) != 0) Visit(child, childKey + "/");
                    else
                    {
                        var length = new FileInfo(child).Length;
                        FileKey(childKey, () =>
                        {
                            using var input = new FileStream(child, FileMode.Open, FileAccess.Read, FileShare.Read);
                            return ReadBounded(input, length);
                        }, length);
                    }
                }
            }
            if (Directory.Exists(root)) Visit(root, "Tables/");
        }
        else
        {
            if (!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc", StringComparison.OrdinalIgnoreCase) ||
                (File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0) throw Review("The external source is not a verified directory or FGCC file.");
            using var archive = ZipFile.OpenRead(project);
            var archiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var key = entry.FullName.Replace('\\', '/'); var parts = key.TrimEnd('/').Split('/');
                if (key.Length == 0 || key.StartsWith('/') || key.Contains(':') || key.Contains('\0') || parts.Any(part => part is "" or "." or "..") ||
                    !archiveKeys.Add(key.TrimEnd('/')) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                    throw Review("The project archive contains unsafe, duplicate or linked entries.");
                if (key.Equals("Tables", StringComparison.OrdinalIgnoreCase)) throw Review("An archive file occupies the native Tables directory.");
                if (!key.StartsWith("Tables/", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.EndsWith('/')) { if (entry.Length != 0) throw Review("A native archive folder contains data."); DirectoryKey(key); }
                else FileKey(key, () => { using var input = entry.Open(); return ReadBounded(input, entry.Length); }, entry.Length);
            }
        }
        return new(files, directories.ToArray());
    }
    private static byte[] ReadBounded(Stream input, long expected)
    {
        if (expected < 0 || expected > 16 * 1024 * 1024) throw Review("A physical table definition exceeds the reviewed read limit.");
        using var output = new MemoryStream((int)expected);
        var buffer = new byte[81920]; long total = 0; int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
        {
            if ((total += count) > expected || total > 16 * 1024 * 1024)
                throw Review("A physical table definition exceeded its verified length while reading.");
            output.Write(buffer, 0, count);
        }
        if (total != expected) throw Review("A physical table definition changed length while reading.");
        return output.ToArray();
    }
    private static bool IsFolder(object node) => node.GetType() == Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.TableFolder", true);
    private static IList Items(object node) => NativeV11Reflection.Get(node, "Items") as IList ?? throw Review("A native table folder has no verified item collection.");
    private static string SerializeModel(Assembly designer, NativeV11Route route, object model) =>
        (string)NativeV11Reflection.Static(designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!, "SaveToStr", NativeV11Reflection.Call(route.Mediator, "ConvertToSaveData", model)!)!;
    private static string Canonical(string key, bool directory)
    {
        key = key.Replace('\\', '/');
        if (directory && !key.EndsWith('/')) key += "/";
        if (!key.StartsWith("Tables/", StringComparison.Ordinal) || key.EndsWith('/') != directory || key.Contains(':') || !directory && !key.EndsWith(".json", StringComparison.Ordinal))
            throw Review("A native external definition key is outside its exact Tables root or file contract.");
        foreach (var segment in key.TrimEnd('/').Split('/')) V11Safety.Address(new(ModuleKind.Folder, segment));
        return key;
    }
    private static bool SamePath(string? left, string right) => left is not null && Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string Digest(IEnumerable<string> values) => NativeV11Session.Hash(Encoding.UTF8.GetBytes(string.Join("\n", values.Order(StringComparer.Ordinal))));
    private static BridgeException Review(string detail) => new(Code, detail);
}
