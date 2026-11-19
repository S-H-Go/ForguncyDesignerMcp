using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal sealed record NativeV11Route(ModuleKind Kind, string Name, object Colleague, object Mediator,
    object Loader, object Root, string Folder, bool IsPage, bool WholeCache);

internal sealed record NativeV11Item(NativeV11Route Route, object Model, IList Parent, int Index,
    ModuleAddress Address, string RelativePath);

internal sealed partial class NativeV11Session
{
    internal readonly AdapterHostContext Context;
    internal readonly V11BuildProfile Profile;
    internal readonly object Document;
    internal readonly object Workbook;
    internal readonly Type Generator;
    internal readonly object SaveFile;
    internal readonly IReadOnlyList<NativeV11Route> Routes;
    internal readonly string SourcePath;
    internal readonly string LocalPath;
    internal readonly string LastPath;
    private readonly object manager;
    private readonly object singleton;
    internal object NativeApplication => singleton;
    private readonly ProjectTarget openingTarget;

    internal NativeV11Session(AdapterHostContext context)
    {
        Context = context;
        Profile = V11BuildProfile.Resolve(context.DesignerAssembly);
        Document = context.NativeDocument;
        if (Document.GetType() != context.DesignerAssembly.GetType(Profile.DocumentType, true))
            throw new NotSupportedException("The native V11 document does not match the fingerprinted build.");
        singleton = context.DesignerAssembly.GetType("o", true)!.GetProperty("Instance", NativeV11Reflection.Flags)!.GetValue(null)
            ?? throw new InvalidOperationException("The native V11 application instance is unavailable.");
        openingTarget = context.CaptureSnapshot().Target;
        SourcePath = FullPath(NativeV11Reflection.Get(Document, "CurrentFilePath") as string, "CurrentFilePath");
        if (!File.Exists(SourcePath) && !Directory.Exists(SourcePath))
            throw new InvalidOperationException("The V11 project must have an existing saved source.");
        Workbook = NativeV11Reflection.Get(Document, "Workbook") ?? throw new MissingMemberException("Document.Workbook");
        manager = NativeV11Reflection.Get(Document, "ForguncyColleagueManager") ?? throw new MissingMemberException("Document.ForguncyColleagueManager");
        Generator = context.DesignerAssembly.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
        var routes = new List<NativeV11Route>();
        foreach (var (property, kind) in RouteNames)
        {
            var colleague = NativeV11Reflection.Get(manager, property);
            if (colleague is null) continue;
            var mediator = NativeV11Reflection.Get(colleague, "Mediator");
            var loader = mediator is null ? null : NativeV11Reflection.Get(mediator, "SaveDataColleague");
            var root = NativeV11Reflection.Get(colleague, "Root");
            var folder = NativeV11Reflection.Get(colleague, "BaseFolder") as string;
            if (mediator is null || loader is null || root is null || string.IsNullOrEmpty(folder))
                throw new MissingMemberException("Incomplete native V11 module route: " + property);
            folder = NormalizeRelative(folder).TrimEnd('/') + "/";
            routes.Add(new(kind, property, colleague, mediator, loader, root, folder,
                kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage,
                kind is ModuleKind.Table or ModuleKind.ScheduledTask or ModuleKind.Report or ModuleKind.BusinessProcess or ModuleKind.Notification));
        }
        Routes = routes;
        var first = routes.FirstOrDefault() ?? throw new InvalidOperationException("No native V11 module routes were found.");
        SaveFile = NativeV11Reflection.Get(first.Loader, "SaveLoadFile") ?? throw new MissingMemberException("Module.SaveLoadFile");
        if (routes.Any(route => !ReferenceEquals(NativeV11Reflection.Get(route.Loader, "SaveLoadFile"), SaveFile)))
            throw new InvalidOperationException("The V11 module routes do not share one native save session.");
        LocalPath = FullPath(NativeV11Reflection.Get(SaveFile, "LocalPath") as string, "LocalPath");
        LastPath = FullPath(NativeV11Reflection.Get(SaveFile, "LastPath") as string, "LastPath");
        if (!Directory.Exists(LocalPath) || !Directory.Exists(LastPath) || PathsEqual(LocalPath, SourcePath) || PathsEqual(LocalPath, LastPath))
            throw new InvalidOperationException("The V11 native save-session paths are invalid or overlap.");
        VerifyContext();
    }

    internal void VerifyContext()
    {
        var snapshot = Context.CaptureSnapshot();
        if (snapshot.Target != openingTarget || !PathsEqual(SourcePath, snapshot.Target.ProjectPath) ||
            snapshot.Target.DesignerVersion != Profile.Version ||
            !ReferenceEquals(NativeV11Reflection.Get(singleton, Profile.DocumentProperty), Document) ||
            !ReferenceEquals(NativeV11Reflection.Get(Document, "Workbook"), Workbook) ||
            !ReferenceEquals(NativeV11Reflection.Get(Document, "ForguncyColleagueManager"), manager) ||
            !PathsEqual(SourcePath, NativeV11Reflection.Get(Document, "CurrentFilePath") as string))
            throw new InvalidOperationException("The native V11 document, project, or session changed; rebind the adapter.");
        if (snapshot.Busy || NativeV11Reflection.Get(Document, "Opening") is true || NativeV11Reflection.Get(Document, "IsImporting") is true)
            throw new InvalidOperationException("The V11 document is opening, importing, or otherwise busy.");
        if (Routes.Any(route => !ReferenceEquals(NativeV11Reflection.Get(route.Loader, "SaveLoadFile"), SaveFile)))
            throw new InvalidOperationException("The native V11 save session changed; rebind the adapter.");
    }

    internal NativeV11Route Route(ModuleKind kind) => Routes.SingleOrDefault(route => route.Kind == (kind == ModuleKind.View ? ModuleKind.Table : kind))
        ?? throw new NotSupportedException("No native V11 route is implemented for " + kind + ".");

    internal IReadOnlyList<NativeV11Item> Items(ModuleKind kind)
    {
        VerifyContext();
        var route = Route(kind);
        var result = new List<NativeV11Item>();
        void Walk(object folder, string prefix)
        {
            EnsureLoaded(folder, route.Folder + prefix);
            if (NativeV11Reflection.Get(folder, "Items") is not IList list)
                throw new NotSupportedException("The native V11 module tree does not expose an editable item collection.");
            for (var index = 0; index < list.Count; index++)
            {
                var model = list[index] ?? throw new InvalidDataException("A native V11 module node is null.");
                var name = NativeV11Reflection.Get(model, "Name") as string ?? throw new InvalidDataException("A native V11 module name is missing.");
                ValidateSegment(name);
                if (NativeV11Reflection.Get(model, "Items") is IList)
                {
                    Walk(model, prefix + name + "/");
                    continue;
                }
                EnsureLoaded(model, route.Folder + prefix + name);
                var actualKind = route.Kind == ModuleKind.Table ? TableKind(model) : route.Kind;
                if (actualKind != kind) continue;
                var address = new ModuleAddress(actualKind, name, prefix.Length == 0 ? null : prefix.TrimEnd('/'));
                var expected = route.Folder + prefix + name + ".json";
                var nativeKey = NativeV11Reflection.Call(route.Colleague, "GetKey", model) as string;
                if (nativeKey is null || !NormalizeRelative(nativeKey).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The native V11 save registration does not match the module tree: " + expected);
                result.Add(new(route, model, list, index, address, NormalizeRelative(nativeKey)));
            }
        }
        Walk(route.Root, "");
        return result;
    }

    internal NativeV11Item Find(ModuleAddress address)
    {
        VerifyContext();
        ValidateAddress(address);
        var route = Route(address.Kind);
        var node = route.Root;
        var folder = NormalizeFolder(address.FolderPath);
        EnsureLoaded(node, route.Folder);
        foreach (var segment in folder.Length == 0 ? Array.Empty<string>() : folder.Split('/'))
        {
            var children = NativeV11Reflection.Get(node, "Items") as IList
                ?? throw new InvalidDataException("The native V11 parent node is not a folder.");
            var matching = children.Cast<object>().Where(child => NativeV11Reflection.Get(child, "Name") as string == segment &&
                NativeV11Reflection.Get(child, "Items") is IList).ToArray();
            if (matching.Length != 1) throw new KeyNotFoundException("No unique native V11 folder matches the module address.");
            node = matching[0];
            EnsureLoaded(node, route.Folder + folder);
        }
        var list = NativeV11Reflection.Get(node, "Items") as IList
            ?? throw new InvalidDataException("The native V11 parent node is not a folder.");
        var matches = list.Cast<object>().Select((model, index) => (Model: model, Index: index))
            .Where(candidate => NativeV11Reflection.Get(candidate.Model, "Name") as string == address.Name &&
                NativeV11Reflection.Get(candidate.Model, "Items") is not IList).ToArray();
        if (matches.Length != 1) throw new KeyNotFoundException("No unique native V11 module matches the requested address.");
        var match = matches[0];
        EnsureLoaded(match.Model, NativePath(address));
        if (route.Kind == ModuleKind.Table && TableKind(match.Model) != address.Kind)
            throw new KeyNotFoundException("The native V11 table/view kind does not match the requested address.");
        var key = NativeV11Reflection.Call(route.Colleague, "GetKey", match.Model) as string;
        if (key is null || !NormalizeRelative(key).Equals(NativePath(address), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The native V11 module save registration changed.");
        return new(route, match.Model, list, match.Index,
            new(address.Kind, address.Name, folder.Length == 0 ? null : folder), NormalizeRelative(key));
    }

    internal ModuleDocument Read(NativeV11Item item) => Read(item, false);

    // Only the mutation transaction may bypass the dirty check, immediately after
    // it has written and rechecked the complete native metadata/layout pair.
    internal ModuleDocument Read(NativeV11Item item, bool allowDirtyLinkedReadback)
    {
        VerifyContext();
        if (item.Index >= item.Parent.Count || !ReferenceEquals(item.Parent[item.Index], item.Model))
            throw new InvalidOperationException("The native V11 module tree changed during the read.");
        EnsureLoaded(item.Model, item.RelativePath);
        var companions = new List<CompanionAsset>();
        string json;
        if (item.Route.IsPage)
        {
            var sheet = Sheet(item.Address.Name) ?? throw new InvalidOperationException("Open the native V11 page before reading its full contents.");
            if (NativeV11Reflection.Get(sheet, "Loaded") is not true)
                throw new InvalidOperationException("The native V11 worksheet is not fully loaded.");
            var tag = NativeV11Reflection.Get(sheet, "Tag");
            if (tag is null || !ReferenceEquals(NativeV11Reflection.Get(tag, "PageInfo"), item.Model))
                throw new InvalidOperationException("The native V11 worksheet and module tree no longer refer to the same page.");
            var data = NativeV11Reflection.Static(Generator, "GetPageSaveData", sheet)
                ?? throw new InvalidOperationException("The native V11 page serializer returned no data.");
            json = SerializeSaveData(data);
            // Derive the redundant page from current native page data, never from a stale disk cache.
            var redundant = NativeV11Reflection.Call(item.Route.Loader, Profile.RedundantProjection, data)
                ?? throw new InvalidOperationException("The native V11 redundant-page projection returned no data.");
            companions.Add(Asset(Path.ChangeExtension(item.RelativePath, ".rd").Replace('\\', '/'), Encoding.UTF8.GetBytes(SerializeSaveData(redundant)), "application/json"));
        }
        else
        {
            json = Serialize(item.Route, item.Model);
            if (item.Address.Kind is ModuleKind.Report or ModuleKind.BusinessProcess)
            {
                EnsureLinkedEditorClosed(item);
                if (!allowDirtyLinkedReadback && NativeV11Reflection.Get(Document, "ContainsUnsavedChanges") is not false)
                    throw new InvalidOperationException("Save current native V11 edits before reading a report or process with a file-backed layout.");
                var extension = item.Address.Kind == ModuleKind.Report ? ".rdlx" : ".bpmn";
                var relative = Path.ChangeExtension(item.RelativePath, extension).Replace('\\', '/');
                var path = Under(LocalPath, relative);
                if (!File.Exists(path)) throw new FileNotFoundException("The native V11 companion file is missing.", relative);
                companions.Add(Asset(relative, File.ReadAllBytes(path), "application/xml"));
            }
        }
        using var parsed = JsonDocument.Parse(json);
        var metadata = parsed.RootElement.Clone();
        var revision = Hash(Encoding.UTF8.GetBytes(metadata.GetRawText() + string.Concat(companions.Select(asset => "\n" + asset.RelativePath + "\n" + asset.Sha256))));
        return new(item.Address, metadata, companions, revision);
    }

    internal string Serialize(NativeV11Route route, object model)
    {
        if (route.IsPage)
        {
            var name = NativeV11Reflection.Get(model, "Name") as string ?? throw new InvalidDataException("The native V11 page name is missing.");
            var sheet = Sheet(name) ?? throw new InvalidOperationException("Open the native V11 page before serializing it.");
            if (NativeV11Reflection.Get(sheet, "Loaded") is not true)
                throw new InvalidOperationException("The native V11 worksheet is not fully loaded.");
            return SerializeSaveData(NativeV11Reflection.Static(Generator, "GetPageSaveData", sheet)!);
        }
        return SerializeSaveData(NativeV11Reflection.Call(route.Mediator, "ConvertToSaveData", model)
            ?? throw new InvalidOperationException("The native V11 module serializer returned no data."));
    }

    internal object? Sheet(string name)
    {
        var sheets = NativeV11Reflection.Get(Workbook, "Sheets") ?? throw new MissingMemberException("Workbook.Sheets");
        var indexer = sheets.GetType().GetProperty("Item", [typeof(string)])
            ?? throw new MissingMemberException("Workbook.Sheets[string]");
        return indexer.GetValue(sheets, [name]);
    }

    internal string NativePath(ModuleAddress address)
    {
        ValidateAddress(address);
        return Route(address.Kind).Folder + (NormalizeFolder(address.FolderPath) is { Length: > 0 } folder ? folder + "/" : "") + address.Name + ".json";
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    internal string SerializeSaveData(object data)
    {
        var save = Generator.GetMethod("SaveToStr", NativeV11Reflection.Flags, null, [typeof(object)], null)
            ?? throw new MissingMethodException(Generator.FullName, "SaveToStr(object)");
        return (string)(save.Invoke(null, [data]) ?? throw new InvalidOperationException("The native V11 serializer returned no JSON."));
    }

    internal static string Under(string root, string relative)
    {
        relative = NormalizeRelative(relative);
        var absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Path.GetFullPath(Path.Combine(absoluteRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(absoluteRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A native V11 path escaped its project root.");
        for (var cursor = new FileInfo(path).Directory; cursor is not null && cursor.FullName.StartsWith(absoluteRoot, StringComparison.OrdinalIgnoreCase); cursor = cursor.Parent)
            if (cursor.Exists && (cursor.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Native V11 companion paths cannot traverse reparse points.");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("A native V11 companion cannot be a reparse point.");
        return path;
    }

    internal static void ValidateAddress(ModuleAddress address)
    {
        ValidateSegment(address.Name);
        _ = NormalizeFolder(address.FolderPath);
    }

    internal static string NormalizeFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return "";
        return NormalizeRelative(folder).TrimEnd('/');
    }

    private static string NormalizeRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("A native V11 relative path is invalid.");
        var normalized = relative.Replace('\\', '/').TrimEnd('/');
        foreach (var segment in normalized.Split('/')) ValidateSegment(segment);
        return normalized;
    }

    private static void ValidateSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            value.EndsWith(' ') || value.EndsWith('.') || value.Contains('/') || value.Contains('\\'))
            throw new InvalidDataException("A native V11 module or folder name is invalid.");
    }

    private static ModuleKind TableKind(object model)
    {
        if (NativeV11Reflection.Get(model, "IsView") is bool isView) return isView ? ModuleKind.View : ModuleKind.Table;
        var external = NativeV11Reflection.Get(model, "ExternalDatabaseInfo");
        if (external is not null && NativeV11Reflection.Get(external, "ViewInfo") is not null) return ModuleKind.View;
        return ModuleKind.Table;
    }

    private void EnsureLinkedEditorClosed(NativeV11Item item)
    {
        var report = item.Address.Kind == ModuleKind.Report;
        var type = Context.DesignerAssembly.GetType(report ? "Forguncy.Common.ReportsManager" : "Forguncy.Common.BusinessProcessManager", true)!;
        var field = type.GetField("_instance", NativeV11Reflection.Flags)
            ?? throw new MissingFieldException(type.FullName, "_instance");
        if (field.GetValue(null) is not object nativeManager) return;
        // Conservative across native editor transitions: avoid serializing a stale layout.
        if (NativeV11Reflection.Get(nativeManager, report ? "InARDesignerMode" : "InBPMDesignerMode") is not false ||
            report && NativeV11Reflection.Get(nativeManager, "IsReportDataSourceEditingMode") is not false)
            throw new InvalidOperationException("Close the native V11 report/process editor before reading its file-backed layout.");
    }

    private static void EnsureLoaded(object model, string path)
    {
        if (NativeV11Reflection.Get(model, "Loaded") is false)
            throw new InvalidOperationException("The native V11 tree is not completely loaded: " + path);
    }

    private static CompanionAsset Asset(string relative, byte[] bytes, string mediaType) => new(relative, Convert.ToBase64String(bytes), Hash(bytes), mediaType);
    private static string FullPath(string? value, string field) => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException("The native V11 " + field + " is unavailable.") : Path.GetFullPath(value);
    private static bool PathsEqual(string? left, string? right) => !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static readonly (string Property, ModuleKind Kind)[] RouteNames =
    [
        ("PageModel", ModuleKind.Page), ("MasterPage", ModuleKind.MasterPage), ("UserControl", ModuleKind.UserControlPage),
        ("TableModel", ModuleKind.Table), ("ServerCommands", ModuleKind.ServerCommand), ("ShareCommand", ModuleKind.SharedCommand),
        ("TaskScheduler", ModuleKind.ScheduledTask), ("Notification", ModuleKind.Notification),
        ("ReportModel", ModuleKind.Report), ("ProcessModel", ModuleKind.BusinessProcess)
    ];
}
