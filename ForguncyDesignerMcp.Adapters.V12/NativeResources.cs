using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed record NativeResourceCapability(string ResourceType, ModuleOperation Operation, bool Available,
    string Validation, string? Reason = null);

/// <summary>Typed static resources whose native representation is a file in one of three fixed project folders.</summary>
public sealed partial class NativeResources
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly string[] Types = ["UserFile", "UserTemplateFile", "UserMapFile", "UserJaveScript", "favicon"];
    private static readonly string[] UnimplementedTypes = ["Upload", "ReportTheme", "ThemeAsset"];
    private readonly Assembly assembly;
    private object saveFile;
    private readonly object? document;
    private string local;
    private readonly string source;
    private readonly Func<ProjectSnapshot>? snapshot;
    private readonly Action? componentDirty;
    private readonly Func<ModuleAddress, ReferenceResult>? componentReferences;
    private readonly object? nativeSettings;

    public NativeResources(AdapterHostContext context)
    {
        assembly = context.DesignerAssembly; document = context.NativeDocument;
        nativeSettings = NativeReflection.Get(document, "ForguncySettings");
        snapshot = context.CaptureSnapshot;
        var colleague = NativeReflection.Get(NativeReflection.Get(document, "ForguncyColleagueManager")!, "ReportModel")!;
        var mediator = NativeReflection.Get(colleague, "Mediator")!;
        saveFile = NativeReflection.Get(NativeReflection.Get(mediator, "SaveDataColleague")!, "SaveLoadFile")!;
        var localPath=(string?)NativeReflection.Get(saveFile,"LocalPath");
        var nativeSource=(string?)NativeReflection.Call(saveFile,"GetDocumentPath");
        local = string.IsNullOrWhiteSpace(localPath) || string.IsNullOrWhiteSpace(nativeSource) ? "" : Path.GetFullPath(localPath);
        source = Path.GetFullPath(context.CaptureSnapshot().Target.ProjectPath);
        CheckContract();
        if(local.Length!=0) CheckNativeDirectories();
    }

    // Component tests supply the actual pinned native SaveLoadFile and isolated filesystem roots.
    public NativeResources(Assembly assembly, object nativeSaveLoadFile, string sourceProject,
        Action dirty, Func<ModuleAddress, ReferenceResult> references, Func<ProjectSnapshot>? componentSnapshot = null)
    {
        this.assembly = assembly; saveFile = nativeSaveLoadFile; source = Path.GetFullPath(sourceProject);
        var path=(string?)NativeReflection.Get(saveFile,"LocalPath"); var nativeSource=(string?)NativeReflection.Call(saveFile,"GetDocumentPath");
        local=string.IsNullOrWhiteSpace(path)||string.IsNullOrWhiteSpace(nativeSource)?"":Path.GetFullPath(path);
        snapshot=componentSnapshot; componentDirty = dirty; componentReferences = references; CheckContract();
    }

    public NativeResources(Assembly assembly, object nativeSaveLoadFile, string sourceProject,
        Action dirty, object nativeProjectSettings)
    {
        this.assembly = assembly; saveFile = nativeSaveLoadFile; source = Path.GetFullPath(sourceProject);
        local = Path.GetFullPath((string)NativeReflection.Get(saveFile, "LocalPath")!);
        componentDirty = dirty; nativeSettings = nativeProjectSettings; CheckContract();
    }

    private void CheckContract()
    {
        if (assembly.GetName().Version?.ToString() != V12AdapterFactory.Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != V12AdapterFactory.DesignerSha256 ||
            saveFile.GetType().Assembly != assembly)
            throw new BridgeException("UnsupportedDesignerBuild", "Static resource operations require the exact V12 native component and its SaveLoadFile.");
        if(local.Length==0) return;
        var nativeSource = (string)NativeReflection.Call(saveFile, "GetDocumentPath")!;
        if (!Same(nativeSource, source) || Same(local, source) || Same(local, (string)NativeReflection.Get(saveFile, "LastPath")!))
            throw new BridgeException("ResourceCacheMismatch", "The native working cache must be distinct from the bound source and native save staging directory.");
        if (NativeReflection.Get(saveFile, "Loaded") is not true)
            throw new BridgeException("ResourceCacheNotLoaded", "The native document has not finished loading its resource cache.");
        _ = Under(local, "UserFile");
    }

    private void CheckNativeDirectories()
    {
        var config=assembly.GetType("Forguncy.Common.Config",true)!;
        foreach(var type in Types)
        {
            var property=type switch { "UserFile"=>"LocalUserFilePath", "UserTemplateFile"=>"LocalUserTemplateFilePath", "UserMapFile"=>"LocalUserMapFilePath", "UserJaveScript"=>"LocalUserPageJavaScriptPath", _=>"LocalFaviconFilePath" };
            var nativePath=(string)NativeReflection.Static(config,"get_"+property)!;
            if(!Same(nativePath,Path.Combine(local,type=="favicon"?"favicon.ico":type))) throw new BridgeException("ResourceCacheMismatch","The native resource directory does not belong to this document.");
        }
    }
    private void BindCacheIfReady()
    {
        if(local.Length!=0) return;
        if(document is not null)
        {
            var colleague=NativeReflection.Get(NativeReflection.Get(document,"ForguncyColleagueManager")!,"ReportModel")!;
            var mediator=NativeReflection.Get(colleague,"Mediator")!;
            saveFile=NativeReflection.Get(NativeReflection.Get(mediator,"SaveDataColleague")!,"SaveLoadFile")!;
        }
        var path=(string?)NativeReflection.Get(saveFile,"LocalPath"); var nativeSource=(string?)NativeReflection.Call(saveFile,"GetDocumentPath");
        if(string.IsNullOrWhiteSpace(path)||string.IsNullOrWhiteSpace(nativeSource)) return;
        local=Path.GetFullPath(path);
        try { CheckContract(); if(document is not null) CheckNativeDirectories(); }
        catch { local=""; throw; }
    }

    public IReadOnlyList<ModuleCapability> Capabilities()
    {
        BindCacheIfReady();
        if(local.Length==0) return Enum.GetValues<ModuleOperation>().Select(operation=>new ModuleCapability(ModuleKind.ResourceFile,operation,
            operation is ModuleOperation.List or ModuleOperation.Read && CanReadSourceOnly(),"source-only-read; native-cache-initialization-pending",
            "Read-only saved FGCC inventory is available only while the document is clean; resource mutation requires the initialized native cache.")).ToArray();
        var deletionReady = TryNativeInventory(out _, out var reason);
        return Enum.GetValues<ModuleOperation>().Select(operation =>
        new ModuleCapability(ModuleKind.ResourceFile, operation, operation != ModuleOperation.Delete || deletionReady,
            "native-cache-binary-crud-folder-mirror-and-fgcc-save-components-verified; full-reference-scan-collaboration-and-live-designer-save-pending",
            operation == ModuleOperation.Delete && !deletionReady ? reason :
                "UserFile, UserTemplateFile, UserMapFile, native UserJaveScript and fixed favicon update. Collaboration writes require every native owning module and exact pre-existing save tracking. Upload/report themes/theme assets remain unavailable.")).ToArray();
    }

    public IReadOnlyList<NativeResourceCapability> SubtypeCapabilities()
    {
        BindCacheIfReady();
        if(local.Length==0) return Types.Concat(UnimplementedTypes).SelectMany(type=>Enum.GetValues<ModuleOperation>().Select(operation=>
            new NativeResourceCapability(type,operation,Types.Contains(type) && operation is ModuleOperation.List or ModuleOperation.Read && CanReadSourceOnly(),
                "source-only-read; native-cache-initialization-pending","Resource writes and reference scans require the initialized native cache."))).ToArray();
        var deletionReady = TryNativeInventory(out _, out var reason);
        return Types.Concat(UnimplementedTypes).SelectMany(type =>
        Enum.GetValues<ModuleOperation>().Select(operation => new NativeResourceCapability(type, operation, Types.Contains(type) && (type!="favicon" || operation is ModuleOperation.List or ModuleOperation.Read or ModuleOperation.Update) && (operation != ModuleOperation.Delete || deletionReady),
            Types.Contains(type) ? "native-cache-and-fgcc-source-crud-components-verified; full-reference-scan-collaboration-and-live-designer-pending" : "not-implemented",
            Types.Contains(type) ? operation == ModuleOperation.Delete && !deletionReady ? reason : "Definition-only binary resources; native file cache and source save/readback. No implicit preview or execution." :
                type == "Upload" ? "Upload has a separate native sidecar, owner aliases and account/runtime attachments; not part of this resource implementation." :
                type == "ReportTheme" ? "Generated by the native ReportsManager from the current Theme; manage the owning ModuleKind.Theme rather than editing the generated report theme file." :
                "This subtype needs its own native registration, editor or sidecar lifecycle and is not routed through generic file CRUD."))).ToArray();
    }

    public bool Supports(ModuleAddress address, ModuleOperation operation)
    {
        try { _ = Relative(address); return Enum.IsDefined(operation) && (TypeOf(address)!="favicon" || operation is ModuleOperation.List or ModuleOperation.Read or ModuleOperation.Update); } catch (BridgeException) { return false; }
    }

    public string GetModuleName(ModuleAddress address)
    {
        var relative = Relative(address);
        if (snapshot is null || !snapshot().InCollaborationMode) return "ResourceFile:" + relative;
        var owners = Owners(relative);
        if (owners.Length == 0) throw new BridgeException("ResourceOwnershipUnavailable", "The resource has no native save owner. Bind it through its defining module and obtain native save registration first.");
        return owners[0];
    }

    public ModulePage List(ModuleQuery query)
    {
        if (query.Kind != ModuleKind.ResourceFile || query.Limit is < 1 or > 1000) throw new BridgeException("InvalidResourceQuery", "Resource query kind or page size is invalid.");
        var folders = query.FolderPath is null ? Types : [NormalizeFolder(query.FolderPath)];
        BindCacheIfReady();
        if(local.Length==0) return ListSavedSource(query,folders);
        var entries = new List<ModuleSummary>();
        foreach (var folder in folders)
        {
            if(folder=="favicon")
            {
                var address=new ModuleAddress(ModuleKind.ResourceFile,"favicon.ico","favicon");
                if(CacheBytes("favicon.ico") is not null){var value=Read(address);entries.Add(new(address,value.Revision,"Native fixed favicon"));}
                continue;
            }
            var path = Under(local, folder);
            if (!Directory.Exists(path)) continue;
            foreach (var file in EnumerateFiles(path))
            {
                var relative = Path.GetRelativePath(local, file).Replace('\\', '/');
                var split = relative.LastIndexOf('/');
                var address = new ModuleAddress(ModuleKind.ResourceFile, relative[(split + 1)..], relative[..split]);
                var value = Read(address);
                entries.Add(new(address, value.Revision, "Native " + TypeOf(address) + " static resource"));
            }
        }
        var offset = 0;
        if (query.Cursor is not null && (!int.TryParse(query.Cursor, out offset) || offset < 0)) throw new BridgeException("InvalidCursor", "Resource cursor is invalid.");
        var all = entries.OrderBy(e => e.Address.FolderPath, StringComparer.Ordinal).ThenBy(e => e.Address.Name, StringComparer.Ordinal).ToArray();
        var items = all.Skip(offset).Take(query.Limit).ToArray();
        return new(items, offset + items.Length < all.Length ? (offset + items.Length).ToString() : null);
    }

    private bool CanReadSourceOnly()
    {
        if(snapshot is null) return false;
        var current=snapshot();
        return !current.HasUnsavedChanges && !current.Busy && string.Equals(Path.GetFullPath(current.Target.ProjectPath),source,StringComparison.OrdinalIgnoreCase) &&
            (document is null || NativeReflection.Get(document,"ContainsUnsavedChanges") is false && NativeReflection.Get(document,"Opening") is false && NativeReflection.Get(document,"IsImporting") is false) &&
            File.Exists(source) && Path.GetExtension(source).Equals(".fgcc",StringComparison.OrdinalIgnoreCase);
    }
    private void RequireSourceOnly()
    {
        if(!CanReadSourceOnly()) throw new BridgeException("ResourceCacheNotReady","Saved resource inventory requires a clean document and an available FGCC source while the native cache is uninitialized.");
    }
    private ModulePage ListSavedSource(ModuleQuery query,string[] folders)
    {
        RequireSourceOnly();
        RejectLinks(source);
        using var archive=ZipFile.OpenRead(source);
        var allPaths=archive.Entries.Select(entry=>entry.FullName).Where(name=>(name.Equals("favicon.ico",StringComparison.OrdinalIgnoreCase) || Types.Where(type=>type!="favicon").Any(type=>name.Replace('\\','/').StartsWith(type+"/",StringComparison.OrdinalIgnoreCase))) && !name.EndsWith("/",StringComparison.Ordinal)).ToArray();
        if(allPaths.Any(name=>name.Contains('\\') || !(name=="favicon.ico" || Types.Where(type=>type!="favicon").Any(type=>name.StartsWith(type+"/",StringComparison.Ordinal)))) ||
           allPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=allPaths.Length)
            throw new BridgeException("InvalidSourceArchive","Saved resource paths are ambiguous or use noncanonical separators.");
        var items=new List<ModuleSummary>();
        foreach(var relative in allPaths)
        {
            var split=relative.LastIndexOf('/');
            var address=relative=="favicon.ico"?new ModuleAddress(ModuleKind.ResourceFile,"favicon.ico","favicon"):new ModuleAddress(ModuleKind.ResourceFile,relative[(split+1)..],relative[..split]);
            if(Relative(address)!=relative) throw new BridgeException("InvalidSourceArchive","Saved resource path is not canonical.");
            if(!folders.Any(folder=>address.FolderPath==folder || address.FolderPath!.StartsWith(folder+"/",StringComparison.Ordinal))) continue;
            var value=Read(address); items.Add(new(address,value.Revision,"Saved FGCC resource; native cache pending"));
        }
        if(query.Cursor is not null && (!int.TryParse(query.Cursor,out var parsed) || parsed<0)) throw new BridgeException("InvalidCursor","Resource cursor is invalid.");
        var offset=query.Cursor is null?0:int.Parse(query.Cursor);
        var ordered=items.OrderBy(x=>x.Address.FolderPath,StringComparer.Ordinal).ThenBy(x=>x.Address.Name,StringComparer.Ordinal).ToArray();
        var page=ordered.Skip(offset).Take(query.Limit).ToArray();
        RequireSourceOnly();
        return new(page,offset+page.Length<ordered.Length?(offset+page.Length).ToString():null);
    }

    public ModuleDocument Read(ModuleAddress address)
    {
        var relative = Relative(address);
        BindCacheIfReady();
        if(local.Length==0) RequireSourceOnly();
        var bytes = (local.Length==0 ? SourceBytes(relative) : CacheBytes(relative)) ?? throw new BridgeException("ResourceNotFound", "The typed resource is absent from the verified source or native working cache.");
        var hash = NativeFiles.Hash(bytes);
        var origin = SourceBytes(relative);
        if(local.Length==0) RequireSourceOnly();
        return new(address, JsonSerializer.SerializeToElement(new { resourceType = TypeOf(address) }),
            [new(relative, Convert.ToBase64String(bytes), hash, MediaType(address.Name))],
            NativeFiles.Hash(hash + "\n" + (origin is null ? "absent" : NativeFiles.Hash(origin))));
    }

    public ReferenceResult References(ModuleAddress address)
    {
        return ReferencesRelative(Relative(address),address);
    }

    internal ReferenceResult ImageReferences(ModuleAddress address,string relative)
    {
        if(address.Kind!=ModuleKind.Image || relative.Replace('\\','/')!=relative || !relative.StartsWith("image/",StringComparison.Ordinal) ||
            NativeSaveVerification.Relative(".",relative)!=relative || Path.GetFileName(relative)!=address.Name)
            throw new BridgeException("InvalidImageAddress","A canonical image path matching the image owner is required.");
        return ReferencesRelative(relative,address);
    }

    private ReferenceResult ReferencesRelative(string relative,ModuleAddress address)
    {
        BindCacheIfReady();
        if(local.Length==0) return new([],false,"Native resource cache and complete reference inventory are not initialized.");
        if (componentReferences is not null) return componentReferences(address);
        try
        {
            if (!TryNativeInventory(out var modules, out var reason)) return new([], false, reason);
            var helper = assembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
            var result = new List<ModuleReference>();
            foreach (var entry in modules)
            {
                var model = entry.Value!;
                var moduleType = NativeReflection.Get(model, "ModuleType")!.ToString();
                // MapJsonFile is the resource ownership root; its own directory enumeration is not a usage reference.
                if (moduleType is "MapJsonFile" or "Folder" or "Upload") continue;
                var files = (IEnumerable<string>)NativeReflection.Static(helper, "GetDirectReferFiles", model, local, "../Upload")!;
                if (files.Select(file => NormalizeNativeReferencePath(local, file)).Any(file => string.Equals(file, relative, StringComparison.OrdinalIgnoreCase)))
                {
                    var sourceAddress = ReferenceSource(moduleType!, (string)entry.Key);
                    if (sourceAddress is null) return new(result, false, "Native resource reference source type has no platform module mapping: " + moduleType);
                    result.Add(new(sourceAddress, address, "Native GetDirectReferFiles(" + moduleType + ") module=" + entry.Key + " asset=" + relative));
                }
            }
            // In this exact build TreeNodeReferFilesProvider.OfType is applied to LocatedObject<Command>, not its Object.
            // Supplement it with the native command enumerator and native file interfaces on the actual command payload.
            foreach (var located in EnumerateNativeCommands(modules))
            {
                var command = LocatedValue(located!);
                var used = new List<string>();
                foreach (var contract in command.GetType().GetInterfaces())
                {
                    var method = contract.FullName switch
                    {
                        "GrapeCity.Forguncy.Plugin.IReferFiles" => "GetFilesFromIReferFiles",
                        "GrapeCity.Forguncy.Plugin.INeedUploadFileByUser" => "GetFilesFromNeedUploadFileByUser",
                        _ => null
                    };
                    if (method is null) continue;
                    var values = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(contract))!; values.Add(command);
                    used.AddRange((IEnumerable<string>)NativeReflection.Static(helper, method, values)!);
                }
                if (!used.Select(file => NormalizeNativeReferencePath(local, file)).Any(file => string.Equals(file, relative, StringComparison.OrdinalIgnoreCase))) continue;
                var location = NativeReflection.Get(located!, "Location")!;
                var locationType = location.GetType();
                while (locationType.FullName != "GrapeCity.Forguncy.Plugin.LocationIndicator") locationType = locationType.BaseType ?? throw new BridgeException("UnknownNativeResourceLocation", "Native command location has no recognized base contract.");
                var locations = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(locationType))!;
                // ReferenceFinder consumes the flattened native indicator chain, including the owning root indicator.
                foreach (var segment in (IEnumerable)NativeReflection.Call(location, "Enum")!) locations.Add(segment);
                var owners = ((IEnumerable)NativeReflection.Static(assembly.GetType("Forguncy.Common.ReferenceFinder", true)!, "EnumEditedModulesByReferences", locations)!).Cast<object>().ToArray();
                if (owners.Length == 0) return new(result, false, "A native command file dependency has no native module location mapping.");
                foreach (var owner in owners)
                {
                    var kind = NativeReflection.Get(owner, "ModuleType")!.ToString()!;
                    var name = (string)NativeReflection.Get(owner, "ModuleName")!;
                    var sourceAddress = ReferenceSource(kind, name);
                    if (sourceAddress is null) return new(result, false, "Native command dependency source type has no platform mapping: " + kind);
                    result.Add(new(sourceAddress, address, "Native command file interface: " + NativeReflection.Get(location, "FullString")));
                }
            }
            return new(result, true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new([], false, "Native resource dependency enumeration failed: " + error.GetType().Name); }
    }

    private IEnumerable<object> EnumerateNativeCommands(IReadOnlyDictionary<string, object> modules)
    {
        var helper = assembly.GetType("sf", true)!;
        var singleton = NativeReflection.Static(assembly.GetType("s", true)!, "get_Instance")!;
        var hasWorkbook = NativeReflection.Get(singleton, "ev") is not null;
        // An empty native component fixture has no workbook carrier. This branch is valid only after proving
        // that the complete loaded definition inventory contains no page/table/view that could carry commands.
        if (!hasWorkbook && HasWorkbookCommandCarriers(modules))
            throw new BridgeException("NativeResourceWorkbookMissing", "Native page/table commands require the bound document workbook; deletion is unavailable in this context.");
        var ignore = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase", true)!
            .GetProperty("IgnoreDisableCommands", NativeReflection.Flags)!;
        var previous = ignore.GetValue(null);
        try
        {
            // Disabled command definitions still own their files. Restore the native reference-search preference exactly.
            ignore.SetValue(null, false);
            var methods = hasWorkbook ? new[] { "EnumCommandListInternal" } :
                new[] { "EnumCommandListInShareCommand", "EnumCommandListInServerCommand", "EnumCommandListInTaskScheduler" };
            foreach (var method in methods)
                foreach (var list in (IEnumerable)NativeReflection.Static(helper, method)!)
                {
                    var commands = LocatedValue(list!);
                    var location = NativeReflection.Get(list!, "Location");
                    foreach (var command in (IEnumerable)NativeReflection.Static(helper, "EnumCommandInList", commands, location, true, null)!) yield return command!;
                }
        }
        finally { ignore.SetValue(null, previous); }
    }

    private static object LocatedValue(object located)
    {
        // LocatedObject<T> hides its base Object property with a typed property in this build.
        // GetProperty without DeclaredOnly is ambiguous; use the closest native declaration.
        for (var type = located.GetType(); type is not null; type = type.BaseType)
            if (type.GetProperty("Object", NativeReflection.Flags | BindingFlags.DeclaredOnly) is { } property)
                return property.GetValue(located) ?? throw new BridgeException("NativeResourceCommandMissing", "A native located command has no payload.");
        throw new MissingMemberException(located.GetType().FullName, "Object");
    }

    private static bool HasWorkbookCommandCarriers(IReadOnlyDictionary<string, object> modules) => modules.Values.Any(module =>
        NativeReflection.Get(module, "ModuleType")!.ToString() is "PCPage" or "MobilePage" or "MasterPCPage" or "MasterMobilePage" or
            "TemplatePage" or "UserControlPage" or "Table" or "TableSqlite" or "TableView" or "TableProcessView");

    private bool TryNativeInventory(out IReadOnlyDictionary<string, object> inventory, out string? reason)
    {
        var modules = new Dictionary<string, object>(StringComparer.Ordinal);
        inventory = modules; reason = null;
        try
        {
            if (nativeSettings is null) throw new BridgeException("ResourceNativeInventoryMissing", "This binding has no native project-settings inventory; production deletion is unavailable.");
            var singleton = NativeReflection.Static(assembly.GetType("s", true)!, "get_Instance")!;
            if (!ReferenceEquals(NativeReflection.Get(singleton, "et"), nativeSettings))
                throw new BridgeException("ResourceReferenceDocumentMismatch", "Native reference services are bound to a different project-settings model.");
            var managerType = assembly.GetType("Forguncy.Common.CollaborationManager", true)!;
            var manager = NativeReflection.Static(managerType, "get_Instance")!;
            foreach (var root in NativeDefinitionRoots(nativeSettings))
            {
                RequireLoadedTree(root, new HashSet<object>(ReferenceEqualityComparer.Instance));
                // This exact native iterator is read-only; InitModulesForCurrentDocument/ReloadModules are deliberately not called.
                foreach (var module in (IEnumerable)NativeReflection.Call(manager, "EnumFolderAndFolderItems", root)!) Add(module!);
            }
            var simpleType = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
            var create = simpleType.GetMethods(NativeReflection.Flags).Single(m => m.Name == "Create" && m.GetParameters().Length == 2 &&
                m.GetParameters()[0].ParameterType.FullName == "Forguncy.Collaboration.ModuleType");
            var enumType = create.GetParameters()[0].ParameterType;
            var moduleClass = enumType.Assembly.GetType("Forguncy.Collaboration.Module", true)!;
            var emptyNativeMap = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), moduleClass))!;
            foreach (var name in new[] { "ApplicationSetting", "Theme", "UserService", "CustomLibrary", "MapJsonFile", "DataContectionSet", "Upload",
                         "PublishConfiguration", "PublishSetting", "PermissionGroupConfig", "AIAssistantRules" })
            {
                var simple = create.Invoke(null, [Enum.Parse(enumType, name), Enum.ToObject(create.GetParameters()[1].ParameterType, 0)])!;
                Add(NativeReflection.Call(simple, "ToModule", emptyNativeMap)!);
            }
            if (NativeReflection.Static(Assembly.Load("CommonUtilities").GetType("CommonUtilities.PluginFeatureHelper", true)!, "IsPluginFeatureEnabled") is true)
            {
                var simple = create.Invoke(null, [Enum.Parse(enumType, "Plugin"), Enum.ToObject(create.GetParameters()[1].ParameterType, 0)])!;
                Add(NativeReflection.Call(simple, "ToModule", emptyNativeMap)!);
            }
            // Preserve any persisted collaboration-only native modules as additional scan roots, without rewriting their identity or locks.
            if (snapshot?.Invoke().InCollaborationMode is true && NativeReflection.Get(manager, "Modules") is IDictionary registered)
                foreach (DictionaryEntry entry in registered) if (!modules.ContainsKey((string)entry.Key)) Add(entry.Value!);
            if (NativeReflection.Get(singleton, "ev") is null && HasWorkbookCommandCarriers(modules))
                throw new BridgeException("NativeResourceWorkbookMissing", "The loaded definition inventory contains page/table command carriers but the bound native workbook is unavailable.");
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { reason = error is BridgeException ? error.Message : "Native module inventory could not be completed: " + error.GetType().Name; return false; }

        void Add(object module)
        {
            var name = (string)NativeReflection.Get(module, "ModuleName")!;
            if (!modules.TryAdd(name, module)) throw new BridgeException("DuplicateNativeResourceModule", "The native definition inventory contains a duplicate module identity.");
        }
    }

    internal static IEnumerable<object> NativeDefinitionRoots(object settings)
    {
        var pages = NativeReflection.Get(settings, "Pages")!;
        foreach (var name in new[] { "NormalPageRoot", "MasterPageRoot", "UserControlPageRoot" })
            yield return NativeReflection.Get(pages, name) ?? throw new BridgeException("NativeResourceTreeMissing", "A native page-definition root is missing.");
        yield return NativeReflection.Get(NativeReflection.Get(settings, "TableSet")!, "Root") ?? throw new BridgeException("NativeResourceTreeMissing", "The native table-definition root is missing.");
        foreach (var name in new[] { "ImageFolder", "ServerCommandsList", "TaskSchedulerList", "NotificationList", "ActiveReportsList", "BusinessProcessList", "ShareCommandContainerList" })
            yield return NativeReflection.Get(settings, name) ?? throw new BridgeException("NativeResourceTreeMissing", "A native definition root is missing: " + name);
    }

    private static void RequireLoadedTree(object model, HashSet<object> visited)
    {
        if (!visited.Add(model)) throw new BridgeException("NativeResourceTreeCycle", "The native definition tree contains a repeated or cyclic node.");
        var property = model.GetType().GetProperty("Loaded", NativeReflection.Flags);
        var children = model.GetType().GetProperty("Items", NativeReflection.Flags)?.GetValue(model) as IEnumerable;
        // Loaded belongs to FolderModelBase in this build; resident leaf models (e.g. ShareCommandContainer) have no such property.
        if (children is not null && property is null || property is not null && property.GetValue(model) is not true)
            throw new BridgeException("NativeResourceTreeNotLoaded", "Load all native definition trees before resource deletion; this binding currently has unloaded definitions.");
        if (children is not null)
            foreach (var child in children) RequireLoadedTree(child!, visited);
    }

    public ModuleValidationResult Validate(ModuleMutationRequest request)
    {
        var issues = new List<ValidationIssue>(); ReferenceResult? references = null;
        try
        {
            BindCacheIfReady();
            if(local.Length==0) throw new BridgeException("ResourceCacheNotReady","Resource mutation requires initialized native save and cache paths.");
            var relative = Relative(request.Address);
            if(TypeOf(request.Address)=="favicon" && request.Operation!=ModuleOperation.Update)throw new BridgeException("FaviconOperationUnavailable","The native fixed favicon supports update only; creation and deletion are unavailable.");
            if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) || request.Scope != EffectScope.ProjectDefinition)
                throw new BridgeException("InvalidResourceMutation", "Only definition Create/Update/Delete is permitted for this resource adapter.");
            RequireTracking(relative);
            var current = CacheBytes(relative);
            if (request.Operation == ModuleOperation.Create ? current is not null || SourceBytes(relative) is not null : current is null)
                throw new BridgeException(request.Operation == ModuleOperation.Create ? "ResourceAlreadyExists" : "ResourceNotFound", "Resource existence differs from the requested operation.");
            if (request.Operation != ModuleOperation.Create && request.ExpectedModuleRevision is not null && Read(request.Address).Revision != request.ExpectedModuleRevision)
                throw new BridgeException("ResourceRevisionChanged", "Native working-cache or source bytes changed since the resource read.");
            if (request.Operation == ModuleOperation.Delete)
            {
                references = References(request.Address);
                if (!references.Complete || references.References.Count != 0) throw new BridgeException("ResourceHasReferences", "Deletion requires a complete native dependency scan with no remaining references.");
            }
            else _ = Payload(request);
        }
        catch (Exception error)
        { issues.Add(new(error is BridgeException b ? b.Code : "NativeResourceValidation", error is BridgeException ? error.Message : "Native resource validation failed (" + error.GetType().Name + ").")); }
        return new(issues.Count == 0, issues, references);
    }

    public IReadOnlyList<string> Apply(ModuleMutationRequest request)
    {
        var validation = Validate(request);
        if (!validation.Valid) throw new BridgeException(validation.Issues[0].Code, validation.Issues[0].Message);
        var relative = Relative(request.Address);
        var bytes = request.Operation == ModuleOperation.Delete ? null : Payload(request);
        var target = Under(local, relative);
        if (componentDirty is not null) componentDirty(); else NativeReflection.Set(document!, "ContainsUnsavedChanges", true);
        if (bytes is null)
        {
            // Exact file only. The native saver subsequently mirrors its folder and owns source deletion.
            var utilities = Assembly.Load("CommonUtilities").GetType("Forguncy.Common.FileUtilities", true)!;
            NativeReflection.Static(utilities, "MakeFileNotReadOnly", target);
            NativeReflection.Static(utilities, "DeleteFile", target);
        }
        else
        {
            var stage = Path.Combine(Path.GetTempPath(), "ForguncyDesignerMcp", "resource-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var staged = Under(stage, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.WriteAllBytes(staged, bytes); // Agent-owned input staging only; never native cache/LastPath/source.
            // The native FGCC updater can reuse an archive entry when length and ZIP's
            // two-second timestamp match, even if bytes changed. Set the private input
            // timestamp beyond the existing native cache before native CopyFolder.
            var priorWrite = File.Exists(target) ? File.GetLastWriteTimeUtc(target) : DateTime.MinValue;
            var anchor = priorWrite > DateTime.UtcNow ? priorWrite : DateTime.UtcNow;
            if(anchor > DateTime.MaxValue.AddSeconds(-4))throw new BridgeException("ResourceTimestampInvalid","Existing resource timestamp cannot be advanced safely.");
            File.SetLastWriteTimeUtc(staged,anchor.AddSeconds(4));
            if (!Directory.Exists(Path.GetDirectoryName(target)!)) NativeReflection.Call(saveFile, "EnsureDirectory", Path.GetDirectoryName(target)!);
            if (File.Exists(target)) NativeReflection.Static(Assembly.Load("CommonUtilities").GetType("Forguncy.Common.FileUtilities", true)!, "MakeFileNotReadOnly", target);
            NativeReflection.Call(saveFile, "CopyFolder", new List<string>(), new List<string> { relative.Replace('/', '\\') }, local, stage);
        }
        var after = CacheBytes(relative);
        if (bytes is null ? after is not null : after is null || !bytes.AsSpan().SequenceEqual(after))
            throw new BridgeException("NativeResourceReadbackMismatch", "The native file operation did not produce the exact requested cache bytes. No source save is claimed.");
        return [relative];
    }

    public bool VerifySource(ModuleMutationRequest request)
    {
        var actual = SourceBytes(Relative(request.Address));
        return request.Operation == ModuleOperation.Delete ? actual is null : actual is not null && actual.AsSpan().SequenceEqual(Payload(request));
    }

    private byte[] Payload(ModuleMutationRequest request)
    {
        var value = request.Document;
        var relative = Relative(request.Address);
        if (value is null || value.Address != request.Address || value.Companions.Count != 1 || value.Companions[0].RelativePath != relative ||
            value.Metadata.ValueKind != JsonValueKind.Object || value.Metadata.EnumerateObject().Count() != 1 ||
            !value.Metadata.TryGetProperty("resourceType", out var type) || type.GetString() != TypeOf(request.Address))
            throw new BridgeException("InvalidResourceDocument", "A typed resource requires matching resourceType metadata and exactly its native-resolved companion path.");
        var asset = value.Companions[0];
        if (asset.ContentBase64.Length > (MaximumBytes + 2L) / 3 * 4) throw new BridgeException("ResourceTooLarge", "Resource exceeds the 8 MiB binary limit.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(asset.ContentBase64); } catch (FormatException) { throw new BridgeException("InvalidResourceEncoding", "Resource content is not valid Base64."); }
        if (bytes.Length > MaximumBytes || !string.Equals(NativeFiles.Hash(bytes), asset.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ResourceHashMismatch", "Resource byte count or SHA256 does not match the binary asset.");
        if (TypeOf(request.Address) == "UserMapFile")
        {
            if (!request.Address.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("InvalidMapResource", "Map resource definitions must use a .json filename.");
            try { using var parsed = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF')); if (parsed.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException(); }
            catch (Exception error) when (error is JsonException or DecoderFallbackException) { throw new BridgeException("InvalidMapResource", "Map resource content must be a valid UTF-8 JSON object."); }
        }
        return bytes;
    }

    internal bool IsRegisteredLocalLibrary(ModuleAddress address)
    {
        if (address.FolderPath != "UserFile" || nativeSettings is null) return false;
        var libraries = NativeReflection.Get(nativeSettings, "CustomLibraries")!;
        return new[] { "UserJSFileList", "UserCSSFileList" }.SelectMany(list => ((IEnumerable)NativeReflection.Get(libraries, list)!).Cast<object>())
            .Any(item => NativeReflection.Get(item, "IsLink") is false &&
                string.Equals((string?)NativeReflection.Get(item, "Name"), address.Name, StringComparison.OrdinalIgnoreCase));
    }

    private string[] Owners(string relative)
    {
        var manager = NativeReflection.Static(assembly.GetType("Forguncy.Common.CollaborationManager", true)!, "get_Instance")!;
        var owners = new List<string>();
        if(NativeReflection.Get(manager, "SaveLoadTrackedFiles") is IDictionary tracking)
        foreach (DictionaryEntry entry in tracking)
            if (entry.Value is IEnumerable<string> files && files.Select(file => NormalizeNativeReferencePath(local, file)).Any(file => string.Equals(file, relative, StringComparison.OrdinalIgnoreCase)))
                owners.Add((string)entry.Key);
        // The pinned native MapJsonFile provider owns the entire UserMapFile
        // subtree, including a new file not yet present in tracked-file lists.
        if(relative.StartsWith("UserMapFile/",StringComparison.Ordinal) && NativeReflection.Get(manager,"Modules") is IDictionary modules)
        {
            var maps=modules.Values.Cast<object>().Where(module=>NativeReflection.Get(module,"ModuleType")?.ToString()=="MapJsonFile").ToArray();
            if(maps.Length!=1)throw new BridgeException("ResourceOwnershipUnavailable","The exact native map aggregate owner is unavailable.");
            var registration=(string)NativeReflection.Get(manager,"ModuleFilePath")!;
            if(!Path.GetFullPath(registration).Equals(Path.Combine(Path.GetFullPath(source),".collaboration"),StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ResourceOwnershipUnavailable","The native map owner belongs to another collaboration project.");
            owners.Add((string)NativeReflection.Get(maps[0],"ModuleName")!);
        }
        return owners.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal static ModuleAddress? ReferenceSource(string moduleType, string nativeName)
    {
        ModuleKind? kind = moduleType switch
        {
            "PCPage" or "MobilePage" or "TemplatePage" => ModuleKind.Page,
            "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage,
            "UserControlPage" => ModuleKind.UserControlPage,
            "Table" or "TableSqlite" => ModuleKind.Table,
            "TableView" or "TableProcessView" => ModuleKind.View,
            "ServerCommand" or "ServerCommandPrivate" => ModuleKind.ServerCommand,
            "ShareCommand" or "ShareCommandContainer" => ModuleKind.SharedCommand,
            "TaskScheduler" => ModuleKind.ScheduledTask,
            "Notification" => ModuleKind.Notification,
            "PageReport" or "RdlReport" => ModuleKind.Report,
            "ProcessItem" => ModuleKind.BusinessProcess,
            "Image" => ModuleKind.Image,
            "Theme" => ModuleKind.Theme,
            "ApplicationSetting" => ModuleKind.ApplicationSettings,
            "CustomLibrary" => ModuleKind.CustomLibrary,
            "DataContectionSet" => ModuleKind.DataConnection,
            "PermissionGroupConfig" => ModuleKind.PermissionGroup,
            "Folder" or "ReportFolder" or "ServerCommandFolder" or "TaskSchedulerFolder" or "TableFolder" or "MasterPageFolder" or
                "ProcessFolder" or "UserControlPageFolder" or "NotificationFolder" or "ShareCommandContainerFolder" or "ImageFolder" => ModuleKind.Folder,
            _ => null
        };
        return kind is null ? null : new(kind.Value, kind is ModuleKind.ApplicationSettings or ModuleKind.Theme ? "Current" : nativeName);
    }

    internal static string? NormalizeNativeReferencePath(string nativeLocalRoot, string nativePath)
    {
        var path = nativePath.Replace('\\', '/');
        // Native Upload entries are the known sibling alias and are outside these resource subtypes.
        if (path.StartsWith("../Upload/", StringComparison.OrdinalIgnoreCase)) return null;
        var root = Path.GetFullPath(nativeLocalRoot).TrimEnd('\\', '/');
        var full = Path.GetFullPath(Path.IsPathRooted(nativePath) ? nativePath : Path.Combine(root, nativePath));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("NativeResourceReferenceOutsideCache", "Native file-reference enumeration returned an unrecognized path outside the bound cache.");
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        foreach (var segment in relative.Split('/')) ValidateSegment(segment);
        return relative;
    }
    private void RequireTracking(string relative)
    {
        if (snapshot is null || !snapshot().InCollaborationMode) return;
        var manager = NativeReflection.Static(assembly.GetType("Forguncy.Common.CollaborationManager", true)!, "get_Instance")!;
        var owners = Owners(relative);
        if (owners.Length == 0 || owners.Any(owner => NativeReflection.Call(manager, "IsModuleCheckoutByCurrentUser", owner) is not true))
            throw new BridgeException("ResourceNativeOwnershipRequired", "Every native resource owner must already have current-user checkout and exact file-save registration. Unregistered collaboration resources cannot be created by generic file CRUD.");
    }

    private byte[]? CacheBytes(string relative)
    {
        var path = Under(local, relative);
        return File.Exists(path) ? Limited(File.OpenRead(path)) : null;
    }
    private byte[]? SourceBytes(string relative)
    {
        _ = Under(Directory.Exists(source) ? source : Path.GetDirectoryName(source)!, relative);
        if (Directory.Exists(source)) { var path = Under(source, relative); return File.Exists(path) ? Limited(File.OpenRead(path)) : null; }
        if (!source.EndsWith(".fgcc", StringComparison.OrdinalIgnoreCase) || !File.Exists(source)) throw new BridgeException("ResourceSourceMissing", "The resource source must be an existing collaboration directory or .fgcc archive.");
        RejectLinks(source);
        using var archive = ZipFile.OpenRead(source);
        var matches = archive.Entries.Where(e => e.FullName.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].FullName.Replace('\\', '/') != relative || matches[0].Length > MaximumBytes) throw new BridgeException("InvalidResourceArchive", "Resource archive entries are duplicate, use a case alias or exceed the binary limit.");
        return Limited(matches[0].Open());
    }
    private static byte[] Limited(Stream input)
    {
        using (input) { using var output = new MemoryStream(); var buffer = new byte[8192]; int n; while ((n = input.Read(buffer)) != 0) { if (output.Length + n > MaximumBytes) throw new BridgeException("ResourceTooLarge", "Resource exceeds the 8 MiB binary limit."); output.Write(buffer, 0, n); } return output.ToArray(); }
    }
    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("ResourcePathIsLink", "Native resources cannot traverse linked directories.");
        foreach (var file in Directory.EnumerateFiles(directory)) { if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("ResourcePathIsLink", "Native resources cannot read linked files."); yield return file; }
        foreach (var child in Directory.EnumerateDirectories(directory)) foreach (var file in EnumerateFiles(child)) yield return file;
    }
    private static string TypeOf(ModuleAddress address) => NormalizeFolder(address.FolderPath ?? "").Split('/')[0];
    private static string Relative(ModuleAddress address)
    {
        if (address.Kind != ModuleKind.ResourceFile) throw new BridgeException("UnsupportedResourceKind", "This adapter owns ResourceFile only.");
        ValidateSegment(address.Name);
        if(address.FolderPath=="favicon")return address.Name=="favicon.ico"?"favicon.ico":throw new BridgeException("InvalidResourceName","The native favicon has the fixed name favicon.ico.");
        return NormalizeFolder(address.FolderPath ?? "") + "/" + address.Name;
    }
    private static string NormalizeFolder(string folder)
    {
        var parts = folder.Split('/');
        if (parts.Length == 0 || !Types.Contains(parts[0], StringComparer.Ordinal)) throw new BridgeException("UnsupportedResourceSubtype", "Resource FolderPath must begin with UserFile, UserTemplateFile, UserMapFile, UserJaveScript or favicon.");
        if(parts[0]=="favicon" && parts.Length!=1)throw new BridgeException("InvalidResourceName","The native favicon is a fixed singleton without folders.");
        foreach (var part in parts) ValidateSegment(part);
        return string.Join('/', parts);
    }
    private static void ValidateSegment(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ') ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(name.Split('.')[0], StringComparer.OrdinalIgnoreCase))
            throw new BridgeException("InvalidResourceName", "Resource names must be ordinary logical path segments without traversal, device names or filesystem syntax.");
    }
    private static string Under(string root, string relative)
    {
        var parts = relative.Split('/'); foreach (var part in parts) ValidateSegment(part);
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ResourcePathEscaped", "Resource path escaped its native root.");
        var parent = fullRoot;
        foreach (var part in parts)
        {
            if (Directory.Exists(parent))
            {
                var matches = Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName).Where(name => string.Equals(name, part, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length > 1 || matches.Length == 1 && matches[0] != part)
                    throw new BridgeException("ResourcePathCaseAlias", "Use the native resource's exact path spelling; ambiguous or differently cased aliases are rejected.");
            }
            parent = Path.Combine(parent, part);
        }
        RejectLinks(full);
        return full;
    }
    private static void RejectLinks(string full)
    {
        for (var path = full; path is not null; path = Path.GetDirectoryName(path))
        { try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("ResourcePathIsLink", "Resource paths cannot traverse filesystem links."); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
    }
    private static bool Same(string a, string b) => Path.GetFullPath(a).TrimEnd('\\', '/').Equals(Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string MediaType(string name) => Path.GetExtension(name).ToLowerInvariant() switch { ".ico" => "image/x-icon", ".json" => "application/json", ".txt" => "text/plain", ".pdf" => "application/pdf", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", _ => "application/octet-stream" };
}

