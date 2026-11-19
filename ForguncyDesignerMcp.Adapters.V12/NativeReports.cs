using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Exact-version primitive. The adapter owns UI scheduling, document identity, baseline,
// reference, native permission/check-out and save receipts before calling Apply.
public sealed class NativeReports
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly Assembly assembly;
    private readonly object colleague, mediator, loader, root;
    private object saveFile;
    private readonly object? reportsManager;
    private readonly bool process;
    private ModuleKind Kind => process ? ModuleKind.BusinessProcess : ModuleKind.Report;
    private string Folder => process ? "Process" : "Reports";
    private string Suffix => process ? ".bpmn" : ".rdlx";
    private string ModelType => process ? "Forguncy.Model.BusinessProcessModel" : "ServerDesignerCommon.Model.ReportModel";
    private readonly Dictionary<string, ModuleDocument> expectedSaves = new(StringComparer.Ordinal);
    private (string Local,string Source)? paths;
    public string LocalRoot => Paths().Local;
    public string SourcePath => Paths().Source;
    private (string Local,string Source) Paths()
    {
        if(paths is { } existing) return existing;
        saveFile=Require(Get(loader,"SaveLoadFile"));
        var local=(string?)Get(saveFile,"LocalPath"); var source=(string?)Call(saveFile,"GetDocumentPath");
        if(string.IsNullOrWhiteSpace(local)||string.IsNullOrWhiteSpace(source))
            throw new BridgeException("DefinitionCacheNotReady","Native definition source/cache paths are not initialized; reopen or wait for native initialization before reading files or writing.");
        var result=(Local:Path.GetFullPath(local),Source:Path.GetFullPath(source));
        if(string.Equals(result.Local,result.Source,StringComparison.OrdinalIgnoreCase)) throw new BridgeException("ReportCacheMismatch","Report working cache cannot be its source project.");
        return (paths=result).Value;
    }
    public sealed record Item(object Model, IList Parent, int Index, ModuleAddress Address, string JsonFile, string RdlxFile);

    public NativeReports(Assembly assembly, object nativeDocument)
        : this(assembly, Require(Get(Require(Get(nativeDocument, "ForguncyColleagueManager")), "ReportModel")),
            assembly.GetType("Forguncy.Common.ReportsManager", true)!.GetProperty("Instance", Flags)!.GetValue(null)) { }

    public NativeReports(Assembly assembly, object colleague, object? reportsManager)
        : this(assembly, colleague, reportsManager, false) { }

    internal NativeReports(Assembly assembly, object colleague, object? reportsManager, bool process)
    {
        if (assembly.GetName().Version != new Version(12, 0, 102, 0))
            throw new BridgeException("UnsupportedDesignerVersion", "NativeReports only supports 12.0.102.0.");
        this.assembly = assembly; this.colleague = colleague; this.reportsManager = reportsManager; this.process = process;
        mediator = Require(Get(colleague, "Mediator"));
        loader = Require(Get(mediator, "SaveDataColleague"));
        saveFile = Require(Get(loader, "SaveLoadFile"));
        root = Require(Get(colleague, "Root"));
    }

    public IReadOnlyList<Item> Items()
    {
        var result = new List<Item>();
        void Visit(object node, string? folder)
        {
            if (Get(node, "Loaded") is false) throw new BridgeException("ReportTreeNotLoaded", "Load the full report tree before using the adapter.");
            if (Get(node, "Items") is not IList items) throw new BridgeException("InvalidReportTree", "The native report folder is unavailable.");
            for (var i = 0; i < items.Count; i++)
            {
                var model = items[i]!;
                var name = (string)Require(Get(model, "Name"));
                if (Get(model, "Items") is IList) { Visit(model, folder is null ? name : folder + "/" + name); continue; }
                if (model.GetType().FullName != ModelType) throw new BridgeException("InvalidReportModel", "Unexpected object in the native definition tree.");
                if (Get(model, "Loaded") is false) throw new BridgeException("ReportTreeNotLoaded", "Load the report before reading it.");
                var path = ((string)Require(Call(colleague, "GetKey", model))).Replace('\\', '/');
                var expected = ReportPath(new(Kind, name, folder));
                if (path != expected) throw new BridgeException("ReportPathMismatch", "Native report address and file registration disagree.");
                result.Add(new(model, items, i, new(Kind, name, folder), path, Path.ChangeExtension(path, Suffix)));
            }
        }
        Visit(root, null);
        return result;
    }

    public ModulePage List(ModuleQuery query)
    {
        if (query.Kind != Kind || query.Limit is < 1 or > 1000) throw new BridgeException("InvalidReportQuery", "Invalid native definition query.");
        var items = Items().Where(x => query.FolderPath is null || x.Address.FolderPath == query.FolderPath)
            .OrderBy(x => x.JsonFile, StringComparer.Ordinal).ToArray();
        var offset = query.Cursor is null ? 0 : int.TryParse(query.Cursor, out var value) && value >= 0 ? value : throw new BridgeException("InvalidCursor", "Invalid report cursor.");
        return new(items.Skip(offset).Take(query.Limit).Select(x => new ModuleSummary(x.Address, Read(x.Address).Revision)).ToArray(),
            offset + query.Limit < items.Length ? (offset + query.Limit).ToString() : null);
    }

    public ModuleDocument Read(ModuleAddress address)
    {
        var item = Find(address);
        var json = Serialize(item.Model);
        var bytes = File.ReadAllBytes(Under(item.RdlxFile));
        if (bytes.Length > NativeFiles.MaximumAssetBytes) throw new BridgeException("ReportTooLarge", "Report layout exceeds the asset size limit.");
        var revision = NativeFiles.Hash(json + "\n" + NativeFiles.Hash(bytes) + "\n" + NativeFiles.SourceHash(SourcePath, item.JsonFile) + "\n" + NativeFiles.SourceHash(SourcePath, item.RdlxFile));
        using var data = JsonDocument.Parse(json);
        return new(item.Address, data.RootElement.Clone(), [new(item.RdlxFile, Convert.ToBase64String(bytes), NativeFiles.Hash(bytes), "application/xml")], revision);
    }

    public ModuleValidationResult Validate(ModuleMutationRequest request)
    {
        try { Prepare(request); return new(true, []); }
        catch (Exception error) { return new(false, [new(error is BridgeException b ? b.Code : "NativeReportValidation", error.Message)]); }
    }
    internal ModuleValidationResult ValidateGroupedDelete(ModuleMutationRequest request,IReadOnlySet<ModuleAddress> deletingOwners)
    {
        try { Prepare(request,deletingOwners); return new(true, []); }
        catch (Exception error) { return new(false, [new(error is BridgeException b ? b.Code : "NativeReportValidation", error.Message)]); }
    }

    public string GetModuleName(ModuleAddress address)
    {
        var model = Find(address).Model;
        return (string)Require(Static(assembly.GetType("Forguncy.Common.ModuleExtensions", true)!, "GetModuleName", model, null));
    }

    public ReferenceResult References(ModuleAddress address)
    {
        var model = Find(address).Model;
        var finder = Activator.CreateInstance(assembly.GetType("Forguncy.Common.ReferenceFinder", true)!, [model])!;
        // GetReferences also includes the target and collaboration-exclusive modules.
        // Only native incoming locations are deletion blockers; map those locations
        // through the native finder to retain its page/command ownership semantics.
        var scanner = assembly.GetType("sf", true)!;
        var incoming = Static(scanner, process ? "EnumBusinessProcessReference" : "EnumReportCommandReference", address.Name) as IEnumerable
            ?? throw new BridgeException("NativeReferencesUnavailable", "The native incoming-reference scanner is unavailable.");
        var mapping = finder.GetType().GetMethod("EnumEditedModulesByReferences", Flags)!;
        var locations = (IList)Activator.CreateInstance(mapping.GetParameters()[0].ParameterType)!;
        foreach (var entry in incoming)
        {
            var location = Require(Get(entry!, "Location"));
            locations.Add(Require(Get(location, "Root")));
        }
        var values = new List<object>();
        foreach (var rootLocation in locations.Cast<object>().Distinct())
        {
            var single = (IList)Activator.CreateInstance(mapping.GetParameters()[0].ParameterType)!;
            single.Add(rootLocation);
            var mappedOwners = (mapping.Invoke(finder, [single]) as IEnumerable)?.Cast<object>().ToArray()
                ?? throw new BridgeException("NativeReferenceMappingUnavailable", "Native reference location mapping is unavailable.");
            if (mappedOwners.Length == 0)
                return new([], false, "A native incoming reference root could not be mapped to its owning module: " + rootLocation.GetType().FullName);
            values.AddRange(mappedOwners);
        }
        var own = Require(Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model));
        var result = new List<ModuleReference>();
        foreach (var entry in values)
        {
            if (own.Equals(entry)) continue; // Native finder includes the target module itself.
            var name = (string)Require(Get(entry!, "ModuleName"));
            var moduleType = Require(Get(entry!, "ModuleType")).ToString();
            var kind = moduleType switch
            {
                "PCPage" or "MobilePage" or "TemplatePage" => ModuleKind.Page,
                "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage,
                "ServerCommand" or "ServerCommandPrivate" or "AppService" => ModuleKind.ServerCommand,
                "TaskScheduler" => ModuleKind.ScheduledTask,
                "ShareCommand" or "ShareCommandContainer" => ModuleKind.SharedCommand,
                "PageReport" or "RdlReport" => ModuleKind.Report,
                "ProcessItem" => ModuleKind.BusinessProcess, "Notification" => ModuleKind.Notification,
                "Table" or "TableSqlite" => ModuleKind.Table,
                "TableView" or "TableProcessView" => ModuleKind.View,
                "UserControlPage" => ModuleKind.UserControlPage,
                _ => (ModuleKind?)null
            };
            // A source classification failure must never make deletion appear safe.
            if (kind is null) return new(result, false, "Native reference source type has no cross-version mapping: " + moduleType + " / " + name);
            result.Add(new(new(kind.Value, name), address, "native:" + moduleType + "/" + name));
        }
        return new(result.Distinct().ToArray(), true);
    }

    internal NativeModuleFilePlan CaptureReportLifecycle(ModuleMutationRequest request,NativeModuleFiles files)
    {
        if(process || request.Address.Kind!=ModuleKind.Report || request.Operation is not (ModuleOperation.Create or ModuleOperation.Delete))
            throw new BridgeException("ReportLifecycleUnavailable","Only native report creation and deletion have a lifecycle collector.");
        var prepared=Prepare(request);
        var model=request.Operation==ModuleOperation.Create ? prepared.Model! : Find(request.Address).Model;
        var simple=Require(Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",model));
        object executor;
        if(request.Operation==ModuleOperation.Create)
        {
            var type=assembly.GetType("Forguncy.Views.ObjectViews.CreateItemOrFolderExecutor",true)!;
            var constructor=type.GetConstructors(Flags).Single(c=>c.GetParameters().Length==6);
            executor=constructor.Invoke([null,null,request.Address.Name,Get(simple,"ModuleType"),Get(simple,"IconType"),Get(simple,"ExclusiveType")]);
        }
        else
        {
            var type=assembly.GetType("Forguncy.Common.DeleteReportExecutor",true)!;
            var constructor=type.GetConstructors(Flags).Single(c=>c.GetParameters().Length==2 && c.GetParameters()[1].ParameterType.IsInstanceOfType(model));
            executor=constructor.Invoke([null,model]);
        }
        var values=(Call(executor,"GetRelatedModules") as IEnumerable)?.Cast<object>().ToArray();
        if(values is not { Length:1 } || values[0].GetType().GetField("Item2")?.GetValue(values[0])?.ToString() != (request.Operation==ModuleOperation.Create ? "New" : "Remove"))
            throw new BridgeException("ReportLifecyclePlanUnavailable","The native report collector did not return the single reviewed lifecycle owner.");
        var collected=values[0].GetType().GetField("Item1")?.GetValue(values[0]);
        if(collected is null || !simple.Equals(collected)) throw new BridgeException("ReportLifecyclePlanUnavailable","The native report owner differs from the validated model.");
        var plan=files.CaptureNativeModulesCore(values,false) ?? throw new BridgeException("ReportLifecyclePlanUnavailable","The complete report source group could not be captured.");
        if(request.Operation==ModuleOperation.Create)
        {
            if(plan.BeforeGroups.Count!=0 || plan.ExpectedNewModules.Count!=1) throw new BridgeException("ReportLifecyclePlanUnavailable","The report New owner is already present or ambiguous.");
            return new(plan.BeforeGroups,plan.ExpectedNewModules,plan.ExpectedNewModules.ToDictionary(x=>x.Name,_=>request.Address,StringComparer.Ordinal),plan.NativeChanges);
        }
        if(plan.ExpectedNewModules.Count!=0 || plan.BeforeGroups.Count!=1 || !ReferenceEquals(plan.BeforeGroups[0].NativeIdentity,model) || plan.BeforeGroups[0].RequiredFiles.Count!=2)
            throw new BridgeException("ReportLifecyclePlanUnavailable","The report Remove owner lacks its original complete native source pair.");
        return plan;
    }

    // Does not claim source persistence; callers must await native document SaveAsync
    // and call VerifySource. On partial failure native dirty state must remain visible.
    public IReadOnlyList<string> Apply(ModuleMutationRequest request) => Apply(request,null);
    internal IReadOnlyList<string> Apply(ModuleMutationRequest request,IReadOnlySet<ModuleAddress>? deletingOwners)
    {
        var prepared = Prepare(request,deletingOwners,groupedReferencesPrevalidated:deletingOwners is not null);
        var path = ReportPath(request.Address);
        var layout = Path.ChangeExtension(path, Suffix);
        if (request.Operation == ModuleOperation.Delete)
        {
            var item = Find(request.Address);
            InvalidateGeneration(item.Model, "Remove");
            // Native report UI uses FileUtilities.DeleteFile for these cache files.
            var files = Assembly.Load("CommonUtilities").GetType("Forguncy.Common.FileUtilities", true)!;
            Static(files, "DeleteFile", Under(item.RdlxFile));
            Static(files, "DeleteFile", Under(item.JsonFile));
            item.Parent.RemoveAt(item.Index);
            ClearPathCache(item.Model);
        }
        else
        {
            var model = prepared.Model!;
            Call(saveFile, "SaveFile", Under(layout), prepared.Layout!);
            if (request.Operation == ModuleOperation.Create) FolderItems(request.Address.FolderPath).Add(model);
            else
            {
                var item = Find(request.Address);
                foreach (var field in new[] { "Weight", "DesignerSetting", "Loaded" })
                    if (model.GetType().GetProperty(field, Flags) is { CanWrite: true } property) property.SetValue(model, Get(item.Model, field));
                item.Parent[item.Index] = model;
                Call(colleague, "Edit", model, Get(item.Model, "Name"));
                ClearPathCache(item.Model);
            }
            InvalidateGeneration(model, request.Operation == ModuleOperation.Create ? "New" : "Edit");
            // ea (native static-files colleague) mirrors the entire Reports/Process
            // cache directory during document save. Keep native metadata in that
            // directory current so it cannot overwrite the memento's new JSON.
            Call(saveFile, "SaveFile", Under(path), Serialize(model));
        }
        Call(colleague, "SaveToCache", true);
        if (request.Operation == ModuleOperation.Delete)
        {
            if (Items().Any(x => x.Address == request.Address)) throw new BridgeException("ReportDeleteReadbackFailed", "Native report registration still exists.");
        }
        else
        {
            var result = Read(request.Address);
            if (!ReportDocuments.JsonEquivalent(Serialize(prepared.Model!), result.Metadata.GetRawText()) ||
                !LayoutEquivalent(prepared.Layout!, NativeFiles.Decode(Convert.FromBase64String(result.Companions[0].ContentBase64))))
                throw new BridgeException("ReportMemoryMismatch", "Native report readback differs after mutation.");
            expectedSaves[request.OperationId] = result;
        }
        return [path, layout];
    }

    public bool VerifySource(ModuleMutationRequest request)
    {
        var path = ReportPath(request.Address);
        var json = NativeFiles.ReadSource(SourcePath, path);
        var rdlx = NativeFiles.ReadSource(SourcePath, Path.ChangeExtension(path, Suffix));
        if (request.Operation == ModuleOperation.Delete) return json is null && rdlx is null && !Items().Any(x => x.Address == request.Address);
        if (json is null || rdlx is null || request.Document is null) return false;
        var expected = expectedSaves.GetValueOrDefault(request.OperationId) ?? request.Document;
        return ReportDocuments.JsonEquivalent(expected.Metadata.GetRawText(), NativeFiles.Decode(json)) &&
            LayoutEquivalent(NativeFiles.Decode(Convert.FromBase64String(expected.Companions.Single().ContentBase64)), NativeFiles.Decode(rdlx));
    }

    public Item Find(ModuleAddress address)
    {
        ReportPath(address);
        var matches = Items().Where(x => x.Address == address).ToArray();
        if(matches.Length==0) throw new BridgeException(process?"ProcessNotFound":"ReportNotFound","The native definition at this address does not exist.");
        if(matches.Length>1) throw new BridgeException(process?"ProcessNotUnique":"ReportNotUnique","Multiple native definitions share this address.");
        return matches[0];
    }

    public string Serialize(object model)
    {
        var saveData = Require(Call(mediator, "ConvertToSaveData", model));
        return (string)Require(Static(assembly.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!, "SaveToStr", saveData));
    }

    private (object? Model, string? Layout) Prepare(ModuleMutationRequest request,IReadOnlySet<ModuleAddress>? deletingOwners=null,bool groupedReferencesPrevalidated=false)
    {
        var path = ReportPath(request.Address);
        if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
            throw new BridgeException("InvalidMutation", "Report mutation must create, update or delete.");
        if (reportsManager is not null && (!process && (Get(reportsManager, "InARDesignerMode") is not false || Get(reportsManager, "IsReportDataSourceEditingMode") is not false ||
                reportsManager.GetType().GetField("_checkingDirtyState", Flags)?.GetValue(reportsManager) is true || reportsManager.GetType().GetField("_updateLocalRootPathTask", Flags)?.GetValue(reportsManager) is Task { IsCompleted: false }) ||
            process && (Get(reportsManager, "InBPMDesignerMode") is not false || Get(reportsManager, "CanNotChangeForguncyContext") is true)))
            throw new BridgeException("ReportEditorOpen", "Close the report editor before mutating report definitions.");
        var found = Items().Where(x => x.Address == request.Address).ToArray();
        if (request.Operation == ModuleOperation.Create)
        {
            if (found.Length != 0 || Items().Any(x => x.Address.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                NativeFiles.ReadSource(SourcePath, path) is not null || NativeFiles.ReadSource(SourcePath, Path.ChangeExtension(path, Suffix)) is not null)
                throw new BridgeException("ReportAlreadyExists", "A report with this name or saved file group already exists.");
            if (File.Exists(Under(path)) || File.Exists(Under(Path.ChangeExtension(path, Suffix))))
                throw new BridgeException("NativeCacheCollision", "The native working cache already contains an unregistered definition file group.");
            FolderItems(request.Address.FolderPath);
        }
        else
        {
            var existing = Read(request.Address);
            if (request.ExpectedModuleRevision != existing.Revision) throw new BridgeException("ReportBaselineChanged", "The report memory or source baseline has changed.");
            if (request.Operation == ModuleOperation.Delete)
            {
                if(groupedReferencesPrevalidated) return (null,null);
                var references = References(request.Address);
                if (!references.Complete || references.References.Any(reference=>deletingOwners is null || !deletingOwners.Contains(reference.Source)))
                    throw new BridgeException("ReportHasReferences", references.Reason ?? "The report still has native references.");
                return (null, null);
            }
        }
        var document = request.Document ?? throw new BridgeException("MissingDocument", "Report metadata and RDLX are required.");
        if (document.Address != request.Address || document.Companions.Count != 1) throw new BridgeException("InvalidReportPair", "Exactly one same-address report RDLX companion is required.");
        var asset = document.Companions[0];
        if (asset.RelativePath != Path.ChangeExtension(path, Suffix)) throw new BridgeException("InvalidReportPair", "The companion must have the native same-name path.");
        var bytes = Convert.FromBase64String(asset.ContentBase64);
        if (bytes.Length > NativeFiles.MaximumAssetBytes || Encoding.UTF8.GetByteCount(document.Metadata.GetRawText()) + bytes.Length >
            (process ? NativeFiles.MaximumAssetBytes : ReportDocuments.MaximumPairBytes))
            throw new BridgeException("DefinitionTooLarge", "The native definition and companion exceed the supported input size.");
        if (NativeFiles.Hash(bytes) != asset.Sha256) throw new BridgeException("AssetHashMismatch", "RDLX asset hash differs from its payload.");
        var layout = NativeFiles.Decode(bytes);
        var json = document.Metadata.GetRawText();
        using var parsed = ReportDocuments.ParseMetadata(json);
        if (request.Operation == ModuleOperation.Create) RejectClrTypes(parsed.RootElement);
        if (!parsed.RootElement.TryGetProperty("Name", out var name) || name.GetString() != request.Address.Name)
            throw new BridgeException("InvalidReportName", "Report metadata name differs from its address.");
        var reportType = parsed.RootElement.TryGetProperty("ReportType", out var type) ? type.GetInt32() : 0;
        var xml = process ? ParseBpmn(layout) : ReportDocuments.ParseRdlx(layout);
        if (!process && (reportType is not (0 or 1) || xml.Descendants().Any(x => x.Name.LocalName == "FixedPage") != (reportType == 0)))
            throw new BridgeException("ReportLayoutTypeMismatch", "RDLX layout and ReportType differ.");
        if (request.Operation == ModuleOperation.Update)
        {
            var current = Read(request.Address);
            if (!process)
            {
                ReportDocuments.ValidateUpdate(new(request.Address.Name, "", current.Metadata.GetRawText(), NativeFiles.Decode(Convert.FromBase64String(current.Companions[0].ContentBase64)), current.Revision, path, asset.RelativePath, "", "", false), json, layout);
                ValidateLegacyRoles(current.Metadata, parsed.RootElement);
            }
            else
            {
                ValidateProcessMetadata(current.Metadata, parsed.RootElement);
                var original = ParseBpmn(NativeFiles.Decode(Convert.FromBase64String(current.Companions[0].ContentBase64)));
                XNamespace bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
                if ((string?)original.Root!.Element(bpmn + "process")!.Attribute("id") != (string?)xml.Root!.Element(bpmn + "process")!.Attribute("id"))
                    throw new BridgeException("ProtectedProcessIdentity", "The BPMN process key must remain unchanged during definition update.");
            }
        }
        else if (parsed.RootElement.EnumerateObject().Any(x => x.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("ReportPermissionInitializationRequired", "New report permission data must be initialized by the native permission service.");
        if (request.Operation == ModuleOperation.Create)
            foreach (var property in parsed.RootElement.EnumerateObject().Where(x => x.Name is "AllowAccessRoleList" or "AllowStartRoleList" or "AllowEditRoleList"))
                if (property.Value.ValueKind != JsonValueKind.Null && (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() != 0))
                    throw new BridgeException("NativePermissionInitializationRequired", "Legacy role grants cannot be supplied through definition creation.");
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json), false);
        var data = Require(Call(loader, "GenerateObject", path.Replace('/', '\\'), stream));
        var model = Require(Call(mediator, "ConvertToModel", data));
        if (model.GetType().FullName != ModelType || !ReportDocuments.JsonEquivalent(json, Serialize(model)))
            throw new BridgeException("ReportNativeRoundtripMismatch", "Native report mapping cannot preserve this metadata.");
        if (request.Operation == ModuleOperation.Create)
        {
            // Same factory/default binding and resource type setup as native CreateReportItem.
            var factory = Require(assembly.GetType(process ? "ke" : "kd", true)!.GetProperty("Instance", Flags)!.GetValue(null));
            var permission = Require(Call(factory, "CreateForDesigner", true));
            model.GetType().GetProperty("PermissionData", Flags)!.SetValue(model, permission);
            if (!process)
            {
                var resource = Require(Get(permission, "PermissionResource"));
                var property = resource.GetType().GetProperty("ActiveReportType", Flags)
                    ?? resource.GetType().GetInterfaces().Select(x => x.GetProperty("ActiveReportType")).First(x => x is not null)!;
                property.SetValue(resource, Enum.ToObject(property.PropertyType, reportType));
            }
        }
        if (process) UpdateProcessBindings(model, layout);
        // Ensure the exact native module/connection context can classify this model
        // before the first cache or tree mutation (DirtyModules uses this same path).
        Require(Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model));
        return (model, layout);
    }

    private IList FolderItems(string? folder)
    {
        object node = root;
        if (folder is not null)
            foreach (var part in folder.Split('/'))
            {
                var candidates = ((IList)Require(Get(node, "Items"))).Cast<object>().Where(x => Get(x, "Items") is IList && (string?)Get(x, "Name") == part).ToArray();
                if (candidates.Length != 1) throw new BridgeException("ReportFolderNotFound", "The native report folder must already exist.");
                node = candidates[0];
            }
        return (IList)Require(Get(node, "Items"));
    }

    private string ReportPath(ModuleAddress address)
    {
        if (address.Kind != Kind) throw new BridgeException("InvalidModuleKind", "Unexpected native definition kind.");
        ReportDocuments.ValidateName(address.Name);
        if (address.FolderPath is not null)
            foreach (var segment in address.FolderPath.Split('/')) ReportDocuments.ValidateName(segment);
        var result = Folder + "/" + (address.FolderPath is null ? "" : address.FolderPath + "/") + address.Name + ".json";
        return result;
    }
    private static void RejectClrTypes(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var p in node.EnumerateObject())
            {
                if (p.Name.Equals("$type", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("UnsupportedReportClrType", "CLR type discriminators are not accepted by this definition interface.");
                RejectClrTypes(p.Value);
            }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) RejectClrTypes(item);
    }
    private string Under(string relative) => process ? NativeFiles.UnderProcess(LocalRoot, relative) : NativeFiles.UnderReports(LocalRoot, relative);
    private bool LayoutEquivalent(string left, string right) => process ? XNode.DeepEquals(ParseBpmn(left), ParseBpmn(right)) : ReportDocuments.RdlxEquivalent(left, right);
    private static XDocument ParseBpmn(string text)
    {
        using var source = new StringReader(text);
        using var reader = XmlReader.Create(source, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = NativeFiles.MaximumAssetBytes, MaxCharactersFromEntities = 0 });
        var xml = XDocument.Load(reader);
        XNamespace bpmn = "http://www.omg.org/spec/BPMN/20100524/MODEL";
        if (xml.Root?.Name != bpmn + "definitions" || xml.Root.Elements(bpmn + "process").Count() != 1)
            throw new BridgeException("InvalidBpmn", "A native BPMN definition must contain exactly one process.");
        if (xml.Declaration?.Encoding is { } encoding && !encoding.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidBpmnEncoding", "Native BPMN cache writes use UTF-8.");
        var ids = xml.Descendants().Attributes("id").Select(x => x.Value).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new BridgeException("DuplicateBpmnId", "BPMN identifiers must be nonempty and unique.");
        foreach (var reference in xml.Descendants(bpmn + "sequenceFlow").Attributes().Where(x => x.Name is { LocalName: "sourceRef" or "targetRef" }))
            if (!ids.Contains(reference.Value, StringComparer.Ordinal)) throw new BridgeException("InvalidBpmnReference", "A BPMN sequence flow points to a missing node.");
        // Command payload type expansion needs its own registered native command contract.
        if (text.Contains("$type", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("BpmnCommandTypeNotRegistered", "CLR-discriminated BPMN command payloads are not enabled by this definition interface.");
        return xml;
    }
    private static void ValidateProcessMetadata(JsonElement original, JsonElement candidate)
    {
        ValidateKnownClrTypes(original, candidate);
        foreach (var name in new[] { "Name", "ID", "Id", "GUID", "Guid", "PermissionData", "AllowStartRoleList", "AllowEditRoleList", "FolderPath", "Path" })
        {
            var before = original.TryGetProperty(name, out var a);
            var after = candidate.TryGetProperty(name, out var b);
            if (before != after || before && !ReportDocuments.JsonEquivalent(a.GetRawText(), b.GetRawText()))
                throw new BridgeException("ProtectedProcessMetadata", "Process identity and permissions must be preserved: " + name);
        }
        ValidateLegacyRoles(original, candidate);
    }
    private static void ValidateKnownClrTypes(JsonElement original, JsonElement candidate)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        void Visit(JsonElement node, bool baseline)
        {
            if (node.ValueKind == JsonValueKind.Object)
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name.Equals("$type", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Name != "$type" || property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                            throw new BridgeException("InvalidNativeTypeMetadata", "CLR type metadata must use an exact $type key and nonempty string.");
                        var name = property.Value.GetString()!;
                        if (baseline) known.Add(name);
                        else if (!known.Contains(name)) throw new BridgeException("UnsupportedNativeClrType", "Definition update cannot introduce a CLR type absent from its native baseline.");
                    }
                    Visit(property.Value, baseline);
                }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) Visit(item, baseline);
        }
        Visit(original, true); Visit(candidate, false);
    }
    private static void ValidateLegacyRoles(JsonElement original, JsonElement candidate)
    {
        foreach (var name in new[] { "AllowAccessRoleList", "AllowStartRoleList", "AllowEditRoleList" })
        {
            var before = original.TryGetProperty(name, out var a); var after = candidate.TryGetProperty(name, out var b);
            if (before != after || before && !ReportDocuments.JsonEquivalent(a.GetRawText(), b.GetRawText()))
                throw new BridgeException("ProtectedNativeRoleGrants", "Legacy native role grants must be preserved: " + name);
        }
    }
    private void UpdateProcessBindings(object model, string layout)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "forguncy-v12-bpmn-" + Guid.NewGuid().ToString("N") + ".bpmn");
        try
        {
            File.WriteAllText(temporary, layout, new UTF8Encoding(false));
            Static(assembly.GetType("Forguncy.Common.BpmnHelper", true)!, "UpdateProcessBindSource", model, temporary);
        }
        finally { File.Delete(temporary); }
    }
    private void ClearPathCache(object model) { if (reportsManager is not null && Get(reportsManager, process ? "ProcessPathCache" : "ReportPathCache") is IDictionary cache) cache.Remove(model); }
    private void InvalidateGeneration(object model, string state)
    {
        var cacheType = assembly.GetType("pb", true)!;
        var cache = Require(cacheType.GetProperty("Instance", Flags)!.GetValue(null));
        var method = cacheType.GetMethod("DirtyModules", Flags)!;
        var tuple = method.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var generic = tuple.GetGenericArguments();
        var module = generic[0].GetMethods(Flags).Single(m => m.Name == "Create" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsInstanceOfType(model)).Invoke(null, [model]);
        var changes = Array.CreateInstance(tuple, 1);
        changes.SetValue(Activator.CreateInstance(tuple, module, Enum.Parse(generic[1], state)), 0);
        method.Invoke(cache, [null, changes]);
    }
    private static object Require(object? value) => value ?? throw new MissingMemberException("Required V12 report contract is missing.");
    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Flags)?.GetValue(value);
    private static object? Call(object value, string name, params object?[] args) => Invoke(value.GetType(), value, name, args);
    private static object? Static(Type type, string name, params object?[] args) => Invoke(type, null, name, args);
    private static object? Invoke(Type type, object? target, string name, object?[] args)
    {
        var methods = type.GetMethods(Flags).Where(m => m.Name == name && m.IsStatic == (target is null) && m.GetParameters().Length == args.Length &&
            m.GetParameters().Select((p, i) => args[i] is null || p.ParameterType.IsInstanceOfType(args[i])).All(x => x)).ToArray();
        if (methods.Length != 1) throw new MissingMethodException(type.FullName, name);
        try { return methods[0].Invoke(target, args); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
