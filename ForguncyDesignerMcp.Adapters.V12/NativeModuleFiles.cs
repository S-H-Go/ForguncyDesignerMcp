using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>A native target's owned files, with explicit post-save existence requirements.</summary>
public sealed class NativeModuleFileGroup
{
    internal NativeModuleFileGroup(ModuleAddress address, string nativeName, IReadOnlyList<string> required,
        IReadOnlyList<string> absent, IReadOnlyList<string> referenced, IReadOnlyDictionary<string, string> json,
        Func<bool> current, Func<string, byte[], bool>? matchesContent = null,
        object? nativeIdentity = null, Func<bool>? isPresent = null,
        IReadOnlyList<string>? requiredDirectories = null, IReadOnlyList<string>? absentDirectories = null, bool directoriesRequireLocal = true)
    { Address = address; NativeModuleName = nativeName; RequiredFiles = required; AbsentFiles = absent;
      ReferencedFiles = referenced; ExpectedJson = json; Current = current;
      NativeIdentity = nativeIdentity ?? this; IsPresent = isPresent ?? (() => true);
      RequiredDirectories = requiredDirectories ?? []; AbsentDirectories = absentDirectories ?? []; DirectoriesRequireLocal = directoriesRequireLocal;
      MatchesContent = matchesContent ?? ((file, bytes) => !json.TryGetValue(file, out var expected) || NativeModuleFiles.JsonMatches(bytes, expected)); }
    public ModuleAddress Address { get; }
    public string NativeModuleName { get; }
    public IReadOnlyList<string> RequiredFiles { get; }
    public IReadOnlyList<string> AbsentFiles { get; }
    public IReadOnlyList<string> ReferencedFiles { get; }
    public IReadOnlyList<string> RequiredDirectories { get; }
    public IReadOnlyList<string> AbsentDirectories { get; }
    internal bool DirectoriesRequireLocal { get; }
    public IReadOnlyDictionary<string, string> ExpectedJson { get; }
    public IReadOnlyList<string> Paths => RequiredFiles.Concat(AbsentFiles).Concat(ReferencedFiles).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    internal Func<bool> Current { get; }
    internal Func<string, byte[], bool> MatchesContent { get; }
    internal object NativeIdentity { get; }
    internal Func<bool> IsPresent { get; }
}

public sealed record NativeApiFileCapture(string Namespace, string Method, JsonElement Parameters,
    ModuleAddress Address, ModuleOperation Operation, NativeModuleFileGroup? Before, NativeModuleFilePlan? Plan = null,
    IReadOnlyList<string>? RequiredOwnedModules = null);

public sealed class NativeModuleFilePlan
{
    internal NativeModuleFilePlan(IReadOnlyList<NativeModuleFileGroup> before, IReadOnlyList<(string Name, string Type)> created,
        IReadOnlyDictionary<string, ModuleAddress>? createdTargets = null, IReadOnlyList<object>? nativeChanges = null, NativeProcessBuiltInAssets? processAssets = null,
        NativeProcessAppDatabase? appDatabase = null)
    { BeforeGroups = before; ExpectedNewModules = created; CreatedTargets = createdTargets; NativeChanges = nativeChanges; ProcessAssets=processAssets; AppDatabase=appDatabase; }
    public IReadOnlyList<NativeModuleFileGroup> BeforeGroups { get; }
    public IReadOnlyList<string> ExpectedNewModuleNames => ExpectedNewModules.Select(x => x.Name).ToArray();
    internal IReadOnlyList<object>? NativeChanges { get; }
    internal IReadOnlyList<(string Name, string Type)> ExpectedNewModules { get; }
    internal IReadOnlyDictionary<string, ModuleAddress>? CreatedTargets { get; }
    internal NativeProcessBuiltInAssets? ProcessAssets { get; }
    internal NativeProcessAppDatabase? AppDatabase { get; }
}

/// <summary>Resolves files from the bound native model, never from a client filesystem path or a staging-directory union.</summary>
public sealed partial class NativeModuleFiles
{
    private readonly Assembly assembly;
    private readonly object colleagues, settings, save;
    private readonly object? workbook;
    private readonly object? boundDocument;
    private readonly Func<NativePlugins>? pluginRegistry;
    public NativeModuleFiles(Assembly assembly, object nativeDocument) : this(assembly, nativeDocument, null) { }
    internal NativeModuleFiles(Assembly assembly, object nativeDocument, Func<NativePlugins>? pluginRegistry)
        : this(assembly, R.Get(nativeDocument, "ForguncyColleagueManager")!, R.Get(nativeDocument, "ForguncySettings")!, SaveFile(nativeDocument), pluginRegistry)
    { workbook = R.Get(nativeDocument, "Workbook"); boundDocument=nativeDocument; }
    public NativeModuleFiles(Assembly assembly, object colleagues, object settings, object saveFile) : this(assembly, colleagues, settings, saveFile, null) { }
    internal NativeModuleFiles(Assembly assembly, object colleagues, object settings, object saveFile, Func<NativePlugins>? pluginRegistry)
    {
        this.assembly = assembly; this.colleagues = colleagues; this.settings = settings; save = saveFile;
        this.pluginRegistry = pluginRegistry;
        var state = assembly.GetType("s", true)!.GetProperty("Instance", R.Flags)!.GetValue(null)!;
        if (ReferenceEquals(R.Get(state, "et"), settings) && R.Get(state, "fa") is not null) workbook = R.Get(state, "ev");
    }

    public static bool Supports(ModuleKind kind) => kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage
        or ModuleKind.GlobalVariable or ModuleKind.ApplicationSettings or ModuleKind.ServerCommand or ModuleKind.SharedCommand
        or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.CustomLibrary or ModuleKind.PermissionGroup or ModuleKind.Image or ModuleKind.Table or ModuleKind.View
        or ModuleKind.Report or ModuleKind.BusinessProcess or ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle or ModuleKind.LanguageResource or ModuleKind.DataConnection;
    public bool CanCaptureMutation(ModuleMutationRequest request) => Supports(request.Address.Kind) &&
        (!IsPage(request.Address.Kind) || request.Operation != ModuleOperation.Delete || CaptureMutation(request) is not null) &&
        !(request.Operation == ModuleOperation.Update && request.Document?.Metadata is { ValueKind: JsonValueKind.Object } metadata &&
          metadata.EnumerateObject().Any(p => (p.Name.Equals("parameters", StringComparison.OrdinalIgnoreCase) && request.Address.Kind is not (ModuleKind.ServerCommand or ModuleKind.SharedCommand)) ||
              (p.Name.Equals("oldName", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() != request.Address.Name) || p.Name.Equals("newName", StringComparison.OrdinalIgnoreCase)));

    public NativeModuleFileGroup? Resolve(ModuleAddress address)
    {
        if (address == NativePluginCheckinFiles.Address) return PluginForCheckin();
        if(address==MapResourceOwner)return MapResources();
        if (!Supports(address.Kind)) return null;
        if (address.Kind is ModuleKind.GlobalVariable or ModuleKind.ApplicationSettings or ModuleKind.LanguageResource) return Application(address);
        if (address.Kind is ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle) return Theme();
        if (address.Kind == ModuleKind.DataConnection) return Connections();
        if (address.Kind == ModuleKind.CustomLibrary) return Libraries(address);
        if (address.Kind == ModuleKind.PermissionGroup) return PermissionGroups(address);
        if (address.Kind == ModuleKind.Image) return Image(address);
        var member = address.Kind switch
        {
            ModuleKind.Page => "PageModel", ModuleKind.MasterPage => "MasterPage", ModuleKind.UserControlPage => "UserControl",
            ModuleKind.ServerCommand => "ServerCommands", ModuleKind.SharedCommand => "ShareCommand",
            ModuleKind.ScheduledTask => "TaskScheduler", ModuleKind.Notification => "Notification", ModuleKind.Table or ModuleKind.View => "TableModel",
            ModuleKind.Report => "ReportModel", ModuleKind.BusinessProcess => "ProcessModel", _ => throw new InvalidOperationException()
        };
        var colleague = R.Get(colleagues, member) ?? throw new BridgeException("NativeFileColleagueUnavailable", "Native target file colleague is unavailable.");
        var root = R.Get(colleague, "Root") ?? throw new BridgeException("NativeFileRootUnavailable", "Native target tree is unavailable.");
        var matches = Nodes(root).Where(m => R.Get(m, "Name") as string == address.Name && IsLeaf(m)).ToArray();
        if (address.Kind == ModuleKind.Page && R.Get(colleague, "TemplatePages") is IEnumerable templates)
            matches = matches.Concat(templates.Cast<object>().Where(m => R.Get(m, "Name") as string == address.Name)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new BridgeException("NativeFileTargetAmbiguous", "Native target does not resolve to one model.");
        var model = matches[0];
        if (address.Kind is ModuleKind.Table or ModuleKind.View &&
            (model.GetType().FullName != "Forguncy.Model.Tables.Table" || (R.Get(model, "IsView") is true) != (address.Kind == ModuleKind.View)))
            throw new BridgeException("NativeTableKindMismatch", "Native table/view definition differs from the requested kind.");
        var nativeKey = (string)R.Call(colleague, "GetKey", model)!;
        var key = NativePath(nativeKey);
        var rootName = (string)R.Get(root, "Name")!;
        var expectedFolder = Path.GetDirectoryName(key.Replace('/', Path.DirectorySeparatorChar))!.Replace('\\', '/');
        expectedFolder = expectedFolder == rootName ? "" : expectedFolder.StartsWith(rootName + "/", StringComparison.Ordinal) ? expectedFolder[(rootName.Length + 1)..] : expectedFolder;
        if (address.FolderPath is not null && address.FolderPath != expectedFolder)
            throw new BridgeException("NativeFileFolderMismatch", "Requested logical folder differs from the native model's file key.");
        if (IsPage(address.Kind) && address.FolderPath is null && expectedFolder.Length > 0)
            address = address with { FolderPath = expectedFolder };
        var mediator = R.Get(colleague, "Mediator")!;
        var loader = R.Get(mediator, "SaveDataColleague")!;
        object SaveData() => R.Call(mediator, "ConvertToSaveData", model)!;
        var data = SaveData();
        var json = Serialize(data, loader);
        var hasClonedPairs = IsPage(address.Kind) && json.Contains("\"ConditionAndCommandPairList\"", StringComparison.Ordinal);
        var hasCommandClones = address.Kind == ModuleKind.ServerCommand &&
            model.GetType().FullName == "Forguncy.ServerCommands.Models.ServerCommands" &&
            json.Contains("\"ConditionAndCommandPairList\"", StringComparison.Ordinal);
        string LiveJson(object dto)
        {
            if (hasClonedPairs) return LivePageComparison(dto, loader, address.Name);
            if (hasCommandClones)
            {
                // ConvertToSaveData clones Commands and assigns new IDs to cloned
                // condition pairs. Compare the real live tree, including all IDs
                // and references, using a disposable DTO, never the save cache.
                if (dto.GetType().FullName != "Forguncy.SaveLoad.ServerCommandSaveData" ||
                    ReferenceEquals(dto, model) || R.Get(model, "Commands") is not IList liveCommands)
                    throw new BridgeException("NativeCommandComparisonUnavailable", "The exact native server command comparison model is unavailable.");
                R.Set(dto, "Commands", liveCommands);
            }
            return Serialize(dto, loader);
        }
        var liveBaseline = hasClonedPairs || hasCommandClones ? LiveJson(SaveData()) : json;
        var suffix = IsPage(address.Kind) ? ".rd" : address.Kind == ModuleKind.Report ? ".rdlx" : address.Kind == ModuleKind.BusinessProcess ? ".bpmn" : null;
        var owned = suffix is null ? new[] { key } : new[] { key, Path.ChangeExtension(key, suffix).Replace('\\', '/') };
        var expected = new Dictionary<string, string> { [key] = json };
        if (IsPage(address.Kind))
            expected[owned[1]] = Serialize(PageNative(loader, "us", data), loader);
        var simple = R.Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model)!;
        var references = (IsPage(address.Kind) ? PageReferences(model) : DirectReferences(simple)).Except(owned, StringComparer.Ordinal).ToArray();
        var absent = address.Kind == ModuleKind.BusinessProcess ? references.Where(path => path == "ProcessForguncyDB.sqlite3" &&
            !File.Exists(NativeSaveVerification.Under((string)R.Get(save, "LocalPath")!, path)) &&
            !File.Exists(NativeSaveVerification.Under((string)R.Get(save, "LastPath")!, path)) &&
            NativeSaveVerification.ReadSource((string)R.Call(save, "GetDocumentPath")!, path) is null).ToArray() : [];
        references = references.Except(absent, StringComparer.Ordinal).ToArray();
        var referenceHashes = references.Concat(IsPage(address.Kind) ? [] : owned.Skip(1)).ToDictionary(path => path, LocalHash, StringComparer.Ordinal);
        var nativeName = (string)R.Get(simple, "ModuleName")!;
        bool Present() => Nodes(root).Concat(Templates(colleague)).Any(m => ReferenceEquals(m, model));
        bool Content(string file, byte[] bytes)
        {
            if (!expected.TryGetValue(file, out var expectedContent))
                return referenceHashes.TryGetValue(file, out var hash) && hash is not null && Hash(bytes) == hash;
            if (hasClonedPairs)
            {
                // The writer serializes the cached DTO, whose clone IDs are authoritative on disk.
                if (loader is not IDictionary cache || cache[nativeKey] is not { } cached ||
                    !NativeConditionPairProjection.SamePageSaveData(Serialize(cached, loader), json)) return false;
                expectedContent = Serialize(file == key ? cached : PageNative(loader, "us", cached), loader);
            }
            using var input = new MemoryStream(bytes);
            var decoded = file == key ? R.Call(loader, "GenerateObject", key, input)! : PageNative(loader, "uv", input);
            var decodedContent = Serialize(decoded, loader);
            if (Equivalent(decodedContent, expectedContent)) return true;
            return file == key && hasClonedPairs &&
                NativePageConditionReadback.MatchesFoldedGroups(expectedContent, decodedContent, Decode(bytes));
        }
        return new(address, nativeName, owned, absent, references, expected,
            () => Present() && NativePath((string)R.Call(colleague, "GetKey", model)!) == key &&
                Equivalent(LiveJson(SaveData()), liveBaseline) && referenceHashes.All(x => LocalHash(x.Key) == x.Value),
            Content, model, Present);
    }

    // GetPageSaveData clones the worksheet PageInfo and its cell tags. Use the
    // original objects in a detached comparison DTO so even a real ID-only edit
    // invalidates checkout. This DTO is never put in the save cache or persisted.
    private string LivePageComparison(object dto, object loader, string name)
    {
        var sheet = R.Call(R.Get(workbook!, "Sheets")!, "get_Item", name)!;
        R.Set(dto, "PageInfo", R.Get(R.Get(sheet, "Tag")!, "PageInfo"));
        if (R.Get(dto, "AttachInfos") is not IDictionary tags) return Serialize(dto, loader);
        foreach (var key in tags.Keys.Cast<string>().ToArray())
        {
            var position = key.Split(',');
            if (position.Length != 2 || !int.TryParse(position[0], out var row) || !int.TryParse(position[1], out var column))
                throw new BridgeException("NativePageTagAddressInvalid", "Unexpected native page attachment address.");
            tags[key] = R.Call(sheet, "GetTag", row, column)
                ?? throw new BridgeException("NativePageTagMissing", "A native worksheet attachment disappeared during projection.");
        }
        return Serialize(dto, loader);
    }

    public NativeModuleFileGroup? ResolveAfter(ModuleMutationRequest request, NativeModuleFileGroup? before = null)
    {
        if (IsPage(request.Address.Kind) && request.Operation == ModuleOperation.Delete) return null;
        if (request.Operation != ModuleOperation.Delete)
        {
            var after = Resolve(request.Address);
            if (after is null || before is null) return after;
            return new(after.Address, after.NativeModuleName, after.RequiredFiles,
                before.RequiredFiles.Except(after.RequiredFiles, StringComparer.Ordinal).ToArray(), after.ReferencedFiles,
                after.ExpectedJson, after.Current, after.MatchesContent, after.NativeIdentity, after.IsPresent);
        }
        if (request.Address.Kind is ModuleKind.GlobalVariable or ModuleKind.ApplicationSettings or ModuleKind.PermissionGroup or ModuleKind.LanguageResource
            or ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle or ModuleKind.DataConnection) return Resolve(request.Address);
        if (request.Address.Kind == ModuleKind.CustomLibrary)
        {
            var after = Resolve(request.Address);
            if (after is null || before is null) return after;
            return new(after.Address, after.NativeModuleName, after.RequiredFiles, before.RequiredFiles.Except(after.RequiredFiles, StringComparer.Ordinal).ToArray(),
                after.ReferencedFiles, after.ExpectedJson, after.Current, after.MatchesContent, after.NativeIdentity, after.IsPresent);
        }
        return before is null ? null : ResolveDeletedIdentity(before);
    }

    // This is a single native owner's deletion primitive. Native API deletion
    // must capture all related owners before invoking it for each missing identity.
    public NativeModuleFileGroup? ResolveDeletedIdentity(NativeModuleFileGroup before)
    {
        if (before.IsPresent()) return null;
        var address = before.Address;
        if (address.Kind is ModuleKind.Table or ModuleKind.View or ModuleKind.ScheduledTask)
        {
            var hashes = before.ReferencedFiles.ToDictionary(path => path, LocalHash, StringComparer.Ordinal);
            return new(address, before.NativeModuleName, [], before.RequiredFiles.Concat(before.AbsentFiles).Distinct(StringComparer.Ordinal).ToArray(), before.ReferencedFiles, new Dictionary<string, string>(),
                () => !before.IsPresent() && hashes.All(x => LocalHash(x.Key) == x.Value),
                (file, bytes) => hashes.TryGetValue(file, out var hash) && hash is not null && Hash(bytes) == hash, before.NativeIdentity, before.IsPresent);
        }
        return new(address, before.NativeModuleName, [], before.RequiredFiles.Concat(before.AbsentFiles).Distinct(StringComparer.Ordinal).ToArray(), [], new Dictionary<string, string>(),
            () => !before.IsPresent(), null, before.NativeIdentity, before.IsPresent);
    }

    internal NativeModuleFileGroup RemovedReportForCheckin(ModuleAddress address, object module)
    {
        var source = (string)R.Call(save, "GetDocumentPath")!;
        var pair = NativeRemovedReport.ReadPair(source, address, module);
        var root = R.Get(R.Get(colleagues, "ReportModel")!, "Root")!;
        bool Present() => Nodes(root).Any(model => IsLeaf(model) && Equals(R.Get(model, "Name"), address.Name));
        bool Current() => !Present() && NativeRemovedReport.ReadPair(source, address, module).SequenceEqual(pair);
        return new(address, (string)R.Get(module, "ModuleName")!, [], pair, [], new Dictionary<string,string>(),
            Current, nativeIdentity: module, isPresent: Present);
    }

    public void RequirePureReferenceContext()
    {
        var scanner = assembly.GetType("sf", true)!;
        if (scanner.GetProperty("emo", R.Flags)?.GetValue(null) is true)
            throw new BridgeException("NativeReferenceCacheSuspended", "Wait until native reference enumeration is no longer suspended.");
        var app = R.Get(settings, "ApplicationSettings")!;
        var start = app.GetType().GetField("_startPage", R.Flags) ?? throw new BridgeException("NativeReferenceContextUnavailable", "Native start-page storage contract is unavailable.");
        var local = app.GetType().GetField("_localStartPage", R.Flags) ?? throw new BridgeException("NativeReferenceContextUnavailable", "Native local start-page storage contract is unavailable.");
        // In 12.0.102.0 StartPage only copies LocalStartPage into _startPage when the SDK
        // considers ApplicationSetting checked out. Otherwise the local override is read-only.
        if (local.GetValue(app) is string localName && !Equals(start.GetValue(app), localName))
        {
            var manager = assembly.GetType("Forguncy.Common.CollaborationManager", true)!.GetField("_instance", R.Flags)!.GetValue(null);
            if (manager is null || R.Call(app, "IsApplicationSettingCheckouted") is not false)
                throw new BridgeException("NativeStartPageNormalizationPending", "The native StartPage getter could update shared settings during reference capture.");
        }
        if (workbook is null) throw new BridgeException("NativeWorkbookUnavailable", "A bound native workbook is required for complete page references.");
        foreach (var sheet in ((IEnumerable)R.Get(workbook, "Sheets")!).Cast<object>())
            if (R.Get(sheet, "Tag")?.GetType().FullName != "Forguncy.Model.Pages.SheetAttachData")
                throw new BridgeException("NativeSheetMetadataUnavailable", "All native worksheet metadata must already be attached before pure reference enumeration.");
    }

    public IReadOnlyList<NativeModuleFileGroup>? ResolveNativeModules(IEnumerable<object> nativeModules)
    {
        var result = new List<NativeModuleFileGroup>();
        foreach (var module in nativeModules)
        {
            var token = Token(module);
            if (token is null || FindNative(token.Value.Name, token.Value.Type) is not { } group) return null;
            if (!result.Any(x => ReferenceEquals(x.NativeIdentity, group.NativeIdentity))) result.Add(group);
        }
        return result;
    }

    public NativeModuleFilePlan? CaptureNativeModules(IEnumerable nativeChanges)
        => CaptureNativeModulesCore(nativeChanges, true);

    internal NativeModuleFilePlan? CaptureNativeModulesCore(IEnumerable nativeChanges, bool pageReferences, bool allowProcessInitFolders = false)
    {
        if (pageReferences) RequirePureReferenceContext();
        var result = new List<NativeModuleFileGroup>(); var created = new List<(string Name, string Type)>();
        var tuples = nativeChanges.Cast<object>().ToArray();
        foreach (var change in tuples)
        {
            var module = change.GetType().GetField("Item1")?.GetValue(change) ?? change;
            var state = change.GetType().GetField("Item2")?.GetValue(change)?.ToString();
            var token = Token(module); if (token is null) return null;
            var group = FindNative(token.Value.Name, token.Value.Type);
            if (group is null)
            {
                if (state != "New" || (Kind(token.Value.Type) is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.Table or ModuleKind.View or ModuleKind.Report or ModuleKind.BusinessProcess or ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification) &&
                    !(allowProcessInitFolders && IsProcessInitFolder(token.Value.Type)))) return null;
                created.Add(token.Value); continue;
            }
            if (!result.Any(x => ReferenceEquals(x.NativeIdentity, group.NativeIdentity))) result.Add(group);
        }
        return result.Count == 0 && created.Count == 0 ? null : new(result, created, nativeChanges:tuples);
    }

    public IReadOnlyList<NativeModuleFileGroup>? ResolveNativeModulesAfter(NativeModuleFilePlan plan)
    {
        var result = new List<NativeModuleFileGroup>();
        foreach (var before in plan.BeforeGroups)
        {
            var after = ResolveIdentity(before.NativeIdentity);
            if (after is null) after = ResolveDeletedIdentity(before);
            if (after is null) return null;
            if(plan.AppDatabase is not null && !ProcessAppOwnerUnchanged(plan.AppDatabase,before,after)) return null;
            result.Add(V12ProjectAdapter.MergeFileGroups(before, after));
        }
        foreach (var token in plan.ExpectedNewModules)
        {
            var created = ResolveCreatedNative(plan, token.Name, token.Type); if (created is null) return null;
            if (!result.Any(x => ReferenceEquals(x.NativeIdentity, created.NativeIdentity))) result.Add(created);
        }
        if(plan.ProcessAssets is not null)
            foreach(var name in plan.ProcessAssets.Templates)
            {
                var template=ResolveImplicitProcessTemplate(name);
                if(template is null || template.NativeIdentity.GetType().Name!="TemplatePage" ||
                    !template.RequiredFiles.ToHashSet(StringComparer.Ordinal).SetEquals(new[]{"Pages/"+name+".json","Pages/"+name+".rd"}) ||
                    result.Any(group=>ReferenceEquals(group.NativeIdentity,template.NativeIdentity))) return null;
                result.Add(template);
            }
        var protectedTemplates=ProtectProcessTemplateReferences(plan,result);
        if(protectedTemplates is null) return null;
        result=protectedTemplates;
        if(plan.AppDatabase is not null)
        {
            var protectedOwners=ProtectProcessAppOwnerFiles(plan.AppDatabase,result);
            if(protectedOwners is null) return null;
            result=protectedOwners;
            var database=plan.AppDatabase; var post=database.CaptureAfter(); var covered=0;
            for(var index=0;index<result.Count;index++)
            {
                var original=result[index];
                if(original.RequiredFiles.Contains(database.Name,StringComparer.Ordinal)) return null;
                if(!original.ReferencedFiles.Contains(database.Name,StringComparer.Ordinal)) continue;
                covered++;
                result[index]=new(original.Address,original.NativeModuleName,original.RequiredFiles,original.AbsentFiles,
                    original.ReferencedFiles.Where(path=>path!=database.Name).ToArray(),original.ExpectedJson,
                    ()=>original.Current() && database.VerifyAfter(post),original.MatchesContent,original.NativeIdentity,original.IsPresent,
                    original.RequiredDirectories,original.AbsentDirectories,original.DirectoriesRequireLocal);
            }
            if(covered==0) return null;
        }
        return result;
    }

    // Native ConditionAndCommandPair.Clone regenerates its ID on every page
    // DTO projection. For these newly imported templates, compare that exact
    // typed field semantically while keeping the actual cached save DTO and
    // persisted source byte-for-byte/fully structurally verified.
    internal NativeModuleFileGroup? ResolveImplicitProcessTemplate(string name)
    {
        var original=Resolve(new(ModuleKind.Page,name));
        if(original is null || original.NativeIdentity.GetType().Name!="TemplatePage") return null;
        var colleague=Colleague(ModuleKind.Page)!; var mediator=R.Get(colleague,"Mediator")!; var loader=R.Get(mediator,"SaveDataColleague")!;
        if(loader is not IDictionary cache) return null;
        var model=original.NativeIdentity; var key=(string)R.Call(colleague,"GetKey",model)!; var normalized=NativePath(key);
        if(cache[key] is not { } nativeData || normalized!="Pages/"+name+".json") return null;
        string CacheJson()=>Serialize(cache[key]!,loader);
        string ModelJson()=>Serialize(R.Call(mediator,"ConvertToSaveData",model)!,loader);
        var frozen=Serialize(nativeData,loader); var live=ModelJson();
        if(!NativeConditionPairProjection.Equivalent(frozen,live)) return null;
        var rd=Path.ChangeExtension(normalized,".rd").Replace('\\','/');
        var rdExpected=Serialize(PageNative(loader,"us",nativeData),loader);
        var expected=new Dictionary<string,string> { [normalized]=frozen,[rd]=rdExpected };
        var refHashes=original.ReferencedFiles.ToDictionary(path=>path,LocalHash,StringComparer.Ordinal);
        bool Current()=>original.IsPresent() && NativePath((string)R.Call(colleague,"GetKey",model)!)==normalized && cache[key] is not null &&
            Equivalent(CacheJson(),frozen) && NativeConditionPairProjection.Equivalent(ModelJson(),frozen) && refHashes.All(pair=>LocalHash(pair.Key)==pair.Value);
        bool Content(string file,byte[] bytes)
        {
            if(file==normalized) return Equivalent(Serialize(R.Call(loader,"GenerateObject",key,new MemoryStream(bytes))!,loader),frozen);
            if(file==rd) return Equivalent(Serialize(PageNative(loader,"uv",new MemoryStream(bytes)),loader),rdExpected);
            return original.MatchesContent(file,bytes);
        }
        return new(original.Address,original.NativeModuleName,original.RequiredFiles,original.AbsentFiles,original.ReferencedFiles,expected,
            Current,Content,model,original.IsPresent,original.RequiredDirectories,original.AbsentDirectories,original.DirectoriesRequireLocal);
    }

    public NativeModuleFileGroup? ResolveIdentity(object nativeIdentity)
    {
        if(Token(nativeIdentity) is { Type:"MapJsonFile" })
        { var maps=MapResources();return maps is not null && ReferenceEquals(maps.NativeIdentity,nativeIdentity)?maps:null; }
        var themeType = assembly.GetType("Forguncy.ThemesManager", true)!;
        if (nativeIdentity.GetType() == themeType)
            return ReferenceEquals(themeType.GetField("_instance", R.Flags)!.GetValue(null), nativeIdentity) ? Theme() : null;
        if (ReferenceEquals(R.Get(settings, "DataConnectionSet"), nativeIdentity)) return Connections();
        foreach (var root in new[] { ("ApplicationSettings", "ApplicationSetting"), ("CustomLibraries", "CustomLibrary"), ("PermissionGroupConfig", "PermissionGroupConfig"), ("PublishConfig", "PublishConfiguration"), ("PublishSetting", "PublishSetting") })
            if (ReferenceEquals(R.Get(colleagues, root.Item1), nativeIdentity)) return RootGroup(root.Item2);
        foreach (var kind in TreeKinds)
        {
            var colleague = Colleague(kind); if (colleague is null || R.Get(colleague, "Root") is not { } root) continue;
            if (!Nodes(root).Concat(Templates(colleague)).Any(x => ReferenceEquals(x, nativeIdentity))) continue;
            if (kind == ModuleKind.Table && R.Get(nativeIdentity, "IsView") is true) return Resolve(new(ModuleKind.View, (string)R.Get(nativeIdentity, "Name")!));
            return Resolve(new(kind, (string)R.Get(nativeIdentity, "Name")!));
        }
        return null;
    }

    private static readonly ModuleKind[] TreeKinds = [ModuleKind.Page, ModuleKind.MasterPage, ModuleKind.UserControlPage, ModuleKind.Table, ModuleKind.ServerCommand, ModuleKind.SharedCommand, ModuleKind.ScheduledTask, ModuleKind.Notification, ModuleKind.Report, ModuleKind.BusinessProcess, ModuleKind.Image];
    private object? Colleague(ModuleKind kind) => R.Get(colleagues, kind switch { ModuleKind.Page => "PageModel", ModuleKind.MasterPage => "MasterPage", ModuleKind.UserControlPage => "UserControl", ModuleKind.Table or ModuleKind.View => "TableModel", ModuleKind.ServerCommand => "ServerCommands", ModuleKind.SharedCommand => "ShareCommand", ModuleKind.ScheduledTask => "TaskScheduler", ModuleKind.Notification => "Notification", ModuleKind.Report => "ReportModel", ModuleKind.BusinessProcess => "ProcessModel", ModuleKind.Image => "ImageModel", _ => throw new InvalidOperationException() });
    private static ModuleKind? Kind(string type) => type switch
    {
        "PCPage" or "MobilePage" or "TemplatePage" => ModuleKind.Page, "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage, "UserControlPage" => ModuleKind.UserControlPage,
        "Table" or "TableSqlite" => ModuleKind.Table, "TableView" or "TableProcessView" => ModuleKind.View,
        "ServerCommand" or "ServerCommandPrivate" or "AppService" => ModuleKind.ServerCommand, "ShareCommand" or "ShareCommandContainer" => ModuleKind.SharedCommand,
        "TaskScheduler" => ModuleKind.ScheduledTask, "Notification" => ModuleKind.Notification, "PageReport" or "RdlReport" => ModuleKind.Report, "ProcessItem" => ModuleKind.BusinessProcess, "Image" => ModuleKind.Image, _ => null
    };
    private static (string Name, string Type)? Token(object module) => module.GetType().FullName is "Forguncy.Common.SimpleModule" or "Forguncy.Collaboration.Module" ? ((string)R.Get(module, "ModuleName")!, R.Get(module, "ModuleType")!.ToString()!) : null;
    private NativeModuleFileGroup? FindNative(string name, string type)
    {
        if(type=="MapJsonFile") {var maps=MapResources();return maps?.NativeModuleName==name?maps:null;}
        if (type is "ApplicationSetting" or "CustomLibrary" or "PermissionGroupConfig" or "PublishConfiguration" or "PublishSetting" or "Theme" or "DataContectionSet")
        { var root = RootGroup(type); return root?.NativeModuleName == name ? root : null; }
        var kind = Kind(type); if (kind is null) return null;
        var colleague = Colleague(kind.Value); if (colleague is null || R.Get(colleague, "Root") is not { } tree) return null;
        var matches = Nodes(tree).Concat(Templates(colleague)).Where(IsLeaf).Where(model =>
        { var simple = R.Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model)!; return R.Get(simple, "ModuleName") as string == name && R.Get(simple, "ModuleType")!.ToString() == type; }).ToArray();
        return matches.Length == 1 ? Resolve(new(kind.Value, (string)R.Get(matches[0], "Name")!)) : null;
    }
    private NativeModuleFileGroup? ResolveCreatedNative(NativeModuleFilePlan plan, string name, string type)
    {
        if (IsProcessInitFolder(type)) return ResolveProcessInitFolder(plan, name, type);
        if (plan.CreatedTargets?.TryGetValue(name, out var address) is not true) return FindNative(name, type);
        // The reviewed native create executor's placeholder is bound to the
        // exact logical request target. Classify the new registered model with
        // SimpleModule.Create, rather than rewriting a module-name prefix.
        var created = Resolve(address); if (created is null || plan.BeforeGroups.Any(g => ReferenceEquals(g.NativeIdentity, created.NativeIdentity))) return null;
        var model = created.NativeIdentity;
        var actual = R.Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model)!;
        var actualType = R.Get(actual, "ModuleType")!.ToString();
        if(address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.Report or ModuleKind.BusinessProcess or ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification)
            return actualType==type && R.Get(actual,"ModuleName") as string==name ? created : null;
        if (R.Get(model, "ExternalDatabaseInfo") is not null || R.Get(model, "IsLinkedTableOrLinkedView") is true || R.Get(model, "IsCopyExternalBuiltInTable") is true)
            return NativeLinkedTablePolicy.Allows(address.Kind==ModuleKind.View?"table.view":"table",address.Kind==ModuleKind.View?"delete":"deleteTable",model) &&
                actualType==type && R.Get(actual,"ModuleName") as string==name ? created : null;
        return type == "Table" && address.Kind == ModuleKind.Table && actualType is "Table" or "TableSqlite" ||
            type == "TableView" && address.Kind == ModuleKind.View && actualType == "TableView" ? created : null;
    }
    private NativeModuleFileGroup? RootGroup(string type) => type switch
    {
        "ApplicationSetting" => Application(new(ModuleKind.ApplicationSettings, "Current")), "CustomLibrary" => Libraries(new(ModuleKind.CustomLibrary, "Current")),
        "PermissionGroupConfig" => PermissionGroups(new(ModuleKind.PermissionGroup, "Current")), "Theme" => Theme(), "DataContectionSet" => Connections(), "MapJsonFile" => MapResources(), "PublishConfiguration" => Publish(false), "PublishSetting" => Publish(true), _ => null
    };

    internal bool HasVerifiedProcessDatabase()
    {
        const string relative="ProcessForguncyDB.sqlite3";
        var local=(string?)R.Get(save,"LocalPath"); var staged=(string?)R.Get(save,"LastPath"); var source=(string?)R.Call(save,"GetDocumentPath");
        if(string.IsNullOrWhiteSpace(local)||string.IsNullOrWhiteSpace(staged)||string.IsNullOrWhiteSpace(source)||R.Get(save,"Loaded") is not true) return false;
        var cache=NativeSaveVerification.Under(local,relative); var stage=NativeSaveVerification.Under(staged,relative);
        if(!File.Exists(cache)||!File.Exists(stage)||new FileInfo(cache).Length>64*1024*1024||new FileInfo(stage).Length>64*1024*1024) return false;
        var saved=NativeSaveVerification.ReadSource(source,relative);
        static string RawHash(string path) { using var input=File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        return saved is not null && Hash(saved)==RawHash(cache) && Hash(saved)==RawHash(stage);
    }
    internal bool HasAnyProcessDatabase()
    {
        const string relative="ProcessForguncyDB.sqlite3";
        var local=(string?)R.Get(save,"LocalPath"); var staged=(string?)R.Get(save,"LastPath"); var source=(string?)R.Call(save,"GetDocumentPath");
        if(string.IsNullOrWhiteSpace(local)||string.IsNullOrWhiteSpace(staged)||string.IsNullOrWhiteSpace(source)||R.Get(save,"Loaded") is not true)
            throw new BridgeException("NativeProcessLifecyclePlanUnavailable","Native process source and cache paths must be initialized.");
        return File.Exists(NativeSaveVerification.Under(local,relative)) || File.Exists(NativeSaveVerification.Under(staged,relative)) || NativeSaveVerification.ReadSource(source,relative) is not null;
    }

    public bool Verify(NativeModuleFileGroup group) => Verify(group, null);

    internal bool Verify(NativeModuleFileGroup group, Action<ProjectSaveDiagnostic>? onFailure, string stage = "afterNativeSave")
    {
        bool Reject(string code, string? file = null)
        {
            if (group.NativeModuleName == "ApplicationSetting")
                onFailure?.Invoke(new(stage, NativeSaveVerification.ApplicationFileRole(file), code));
            return false;
        }
        if (group.Address == NativePluginCheckinFiles.Address) return group.Current();
        if(group.Address==MapResourceOwner)return VerifyMapResources(group);
        if (!group.Current()) return Reject("OwnerModelMismatch");
        // A final-path reduction can leave a deleted old identity with no
        // physical tombstones when a new identity legitimately owns those
        // paths. Its native absence remains independently required.
        if (group.Paths.Count == 0 && group.RequiredDirectories.Count == 0 && group.AbsentDirectories.Count == 0) return !group.IsPresent();
        var staging = (string)R.Get(save, "LastPath")!;
        var source = (string)R.Call(save, "GetDocumentPath")!;
        var local = (string)R.Get(save, "LocalPath")!;
        foreach (var directory in group.RequiredDirectories)
            if (group.DirectoriesRequireLocal && !Directory.Exists(NativeSaveVerification.Under(local, directory)) ||
                !Directory.Exists(NativeSaveVerification.Under(staging, directory)) || !SourceDirectoryExists(source, directory)) return false;
        foreach (var directory in group.AbsentDirectories)
            if (Directory.Exists(NativeSaveVerification.Under(local, directory)) ||
                Directory.Exists(NativeSaveVerification.Under(staging, directory)) || SourceDirectoryExists(source, directory)) return false;
        foreach (var file in group.RequiredFiles.Concat(group.ReferencedFiles).Distinct(StringComparer.Ordinal))
        {
            var staged = NativeSaveVerification.Under(staging, file);
            if (!File.Exists(staged)) return Reject("StagedFileMissing", file);
            if (new FileInfo(staged).Length > 64 * 1024 * 1024) return Reject("StagedFileTooLarge", file);
            var bytes = NativeSaveVerification.ReadSource(source, file);
            if (bytes is null) return Reject("SourceFileMissing", file);
            if (FileHash(staged) != Hash(bytes)) return Reject("StagingSourceMismatch", file);
            if (!group.MatchesContent(file, bytes)) return Reject("SavedProjectionMismatch", file);
        }
        foreach (var file in group.AbsentFiles)
            if (File.Exists(NativeSaveVerification.Under((string)R.Get(save, "LocalPath")!, file)) ||
                File.Exists(NativeSaveVerification.Under(staging, file)) || NativeSaveVerification.ReadSource(source, file) is not null) return false;
        return true;
    }

    internal static bool SourceDirectoryExists(string source, string relative)
    {
        relative = NativeSaveVerification.Relative(".", relative).TrimEnd('/');
        if (Directory.Exists(source)) return Directory.Exists(NativeSaveVerification.Under(source, relative));
        using var archive = System.IO.Compression.ZipFile.OpenRead(source);
        var prefix = relative + "/";
        var entries = archive.Entries.Select(entry => entry.FullName.Replace('\\', '/'))
            .Where(path => path.Equals(relative, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        return entries.Length > 0 && entries.All(path => path.StartsWith(prefix, StringComparison.Ordinal)) &&
            entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() == entries.Length;
    }

    internal static readonly ModuleAddress ApplicationRoot = new(ModuleKind.ApplicationSettings, "Current");
    internal static bool IsGlobalVariableEdit(string ns, string method) => ns == "app" &&
        method is "createGlobalVariable" or "updateGlobalVariable" or "deleteGlobalVariable";

    // Each API must name its native owners before dispatch. A variable edits its existing settings root.
    public NativeApiFileCapture? CaptureApiBefore(string ns, string method, JsonElement parameters)
    {
        if(IsCommandDefinitionLifecycle(ns,method)) return CaptureCommandDefinition(ns,method,parameters);
        if(ns=="app.customLibrary" && NativeLibraryLifecycle.Supports(method))
        {
            var libraryAddress=new ModuleAddress(ModuleKind.CustomLibrary,Text(parameters,"name")??"Current",Text(parameters,"type"));
            var owner=Resolve(method == "updateContent" ? new(ModuleKind.CustomLibrary, "Current") : libraryAddress);
            return owner is null?null:new(ns,method,parameters.Clone(),libraryAddress,ModuleOperation.Update,owner,new NativeModuleFilePlan([owner],[]));
        }
        if (IsGlobalVariableEdit(ns, method))
        {
            var name = parameters.EnumerateObject().FirstOrDefault(p => p.Name.Equals("name", StringComparison.OrdinalIgnoreCase)).Value;
            if (name.ValueKind != JsonValueKind.String) throw new BridgeException("InvalidGlobalVariableName", "name must be a string.");
            var variable = new ModuleAddress(ModuleKind.GlobalVariable, name.GetString()!);
            var root = Application(ApplicationRoot);
            return new(ns, method, parameters.Clone(), variable, ModuleOperation.Update, root, new NativeModuleFilePlan([root], []));
        }
        if (ns == "app.permissionGroup")
        {
            if (method == "deletePermissionGroup") NativePermissionGroupGuard.ValidateDelete(assembly, settings, parameters);
            var permissionAddress = new ModuleAddress(ModuleKind.PermissionGroup, Text(parameters, "id") ?? "Current");
            var before = Resolve(permissionAddress) ?? throw new BridgeException("PermissionGroupContextUnavailable", "The native permission-group root owner is unavailable.");
            return new(ns, method, parameters.Clone(), permissionAddress, ModuleOperation.Update, before, new NativeModuleFilePlan([before], []));
        }
        if (ns == "scheduledTask" && method == "deleteScheduledTask") return CaptureScheduledTaskDeletion(parameters);
        if (ns == "page" && method == "createPage") return CapturePageCreate(parameters);
        if (ns == "page" && method is "renamePage" or "deletePage") return CapturePageReferences(method, parameters);
        if (ns == "page" && method is "setCells" or "updateCells" or "clearCells" or "updatePageLoadCommands") return CapturePageCellWrite(method, parameters);
        if (ns == "page.definition" && method is "upsertProperty" or "upsertEvent" or "upsertMethod") return CaptureDefinitionReferences(method, parameters);
        if (ns is "table" or "table.view") return CaptureTableReferences(ns, method, parameters);
        // Other rename APIs may also rewrite incoming owners. Keep them
        // unavailable until their complete native reference plans are adapted.
        var signatureGuarded = ns is "serverCommand" or "sharedCommand";
        if (method.StartsWith("rename", StringComparison.Ordinal) || method == "rename" ||
            parameters.TryGetProperty("oldName", out var oldName) && oldName.ValueKind == JsonValueKind.String && oldName.GetString() != Text(parameters, "name") ||
            parameters.TryGetProperty("parameters", out _) && !signatureGuarded) return null;
        ModuleAddress? address = null;
        var operation = ModuleOperation.Update;
        if (ns == "page" && method == "createPage" && Text(parameters, "name") is { } created)
        {
            var kind = Text(parameters, "type") switch { "master" => ModuleKind.MasterPage, "userControl" => ModuleKind.UserControlPage, null or "normal" => ModuleKind.Page, _ => (ModuleKind?)null };
            if (kind is null) return null;
            address = new(kind.Value, created); operation = ModuleOperation.Create;
        }
        else if ((ns == "page" || ns.StartsWith("page.", StringComparison.Ordinal)) && Text(parameters, "pageName") is { } page)
        {
            var model = R.Call(R.Get(settings, "Pages")!, "TryGetPage", page);
            if (model is null) return null;
            var kind = model.GetType().Name switch { "NormalPage" or "TemplatePage" => ModuleKind.Page, "MasterPage" => ModuleKind.MasterPage, "UserControlPage" => ModuleKind.UserControlPage, _ => (ModuleKind?)null };
            if (kind is null) return null;
            address = new(kind.Value, page);
            if (ns == "page" && method == "deletePage") operation = ModuleOperation.Delete;
        }
        else if (ns == "app" && method == "renameGlobalVariable")
            address = new(ModuleKind.GlobalVariable, Text(parameters, "name") ?? "Current");
        else if (ns == "app" && method is "setStartPage" or "clearStartPage") address = new(ModuleKind.ApplicationSettings, "Current");
        else if (ns == "app.customLibrary") address = new(ModuleKind.CustomLibrary, Text(parameters, "name") ?? "Current");
        else if (ns == "app.permissionGroup") address = new(ModuleKind.PermissionGroup, Text(parameters, "id") ?? "Current");
        else if (ns == "image" && (Text(parameters, "name") ?? Text(parameters, "fileName")) is { } imageName)
        {
            address = new(ModuleKind.Image, imageName);
            if (method == "delete") operation = ModuleOperation.Delete;
            else if (method == "upload") operation = ModuleOperation.Create;
        }
        else if (Text(parameters, "name") is { } itemName && ns is "serverCommand" or "sharedCommand" or "scheduledTask" or "notification")
        {
            address = new(ns switch { "serverCommand" => ModuleKind.ServerCommand, "sharedCommand" => ModuleKind.SharedCommand,
                "scheduledTask" => ModuleKind.ScheduledTask, _ => ModuleKind.Notification }, itemName);
            if (method.StartsWith("create", StringComparison.Ordinal)) operation = ModuleOperation.Create;
            if (method.StartsWith("delete", StringComparison.Ordinal)) operation = ModuleOperation.Delete;
        }
        if (address is null) return null;
        return new(ns, method, parameters.Clone(), address, operation, Resolve(address));
    }

    public NativeModuleFileGroup? ResolveApiAfter(NativeApiFileCapture capture)
    {
        if (capture.Plan is not null)
        {
            var groups = ResolveNativeModulesAfter(capture.Plan);
            return groups?.Count == 1 ? groups[0] : null;
        }
        var address = capture.Address;
        if (capture.Namespace == "page" && capture.Method == "renamePage" && Text(capture.Parameters, "newName") is { } renamed)
            address = address with { Name = renamed };
        else if (capture.Method.StartsWith("rename", StringComparison.Ordinal) && Text(capture.Parameters, "newName") is { } newName)
            address = address with { Name = newName };
        var request = new ModuleMutationRequest("file-capture", new("", "", "", ""), capture.Operation, address, "", null, null, false);
        return ResolveAfter(request, capture.Before);
    }

    public IReadOnlyList<NativeModuleFileGroup>? ResolveApiGroupsAfter(NativeApiFileCapture capture)
    {
        if (capture.Plan is not null) return ResolveNativeModulesAfter(capture.Plan);
        return ResolveApiAfter(capture) is { } group ? [group] : null;
    }

    public NativeApiFileCapture? CaptureMutation(ModuleMutationRequest request) => request.Operation != ModuleOperation.Delete ? null : request.Address.Kind switch
    {
        ModuleKind.Table => CaptureApiBefore("table", "deleteTable", JsonSerializer.SerializeToElement(new { tableName = request.Address.Name })),
        ModuleKind.View => CaptureApiBefore("table.view", "delete", JsonSerializer.SerializeToElement(new { viewName = request.Address.Name, deleteDatabaseObject = false })),
        _ when IsPage(request.Address.Kind) => CaptureApiBefore("page", "deletePage", JsonSerializer.SerializeToElement(new { pageName = request.Address.Name })),
        _ => null
    };

    private NativeApiFileCapture CaptureScheduledTaskDeletion(JsonElement parameters)
    {
        NativeApiCatalog.ValidateParameters("scheduledTask", "deleteScheduledTask", parameters);
        var name = Text(parameters, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("ScheduledTaskNameRequired", "The exact native task name is required.");
        var address = new ModuleAddress(ModuleKind.ScheduledTask, name);
        var before = Resolve(address) ?? throw new BridgeException("ScheduledTaskNotFound", "The exact native scheduled task is absent.");
        NativeScheduledTaskLifecycle.RequireUnreferenced(assembly, settings, this, before);
        if (!before.Current()) throw new BridgeException("ScheduledTaskChanged", "The task changed during its reference scan.");
        var descriptors = ((IEnumerable)R.Static(assembly.GetType("aam", true)!, "GetCurrentAssemblyDescriptors")!).Cast<object>();
        var descriptor = descriptors.SingleOrDefault(d => R.Get(d, "Namespace") as string == "scheduledTask" && R.Get(d, "Method") as string == "deleteScheduledTask");
        if (descriptor is null || R.Get(descriptor, "MethodInfo") is not MethodInfo nativeMethod || R.Get(descriptor, "RequestType") is not Type requestType)
            throw new BridgeException("ScheduledTaskResolverUnavailable", "The registered native task deletion descriptor is unavailable.");
        var attribute = nativeMethod.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType.FullName == "abc");
        if (attribute?.ConstructorArguments.Single().Value is not Type resolverType || resolverType.Assembly != assembly || resolverType.FullName != "abe+ca")
            throw new BridgeException("ScheduledTaskResolverChanged", "The exact native task deletion resolver changed.");
        var request = Activator.CreateInstance(requestType)!; R.Set(request, "Name", name);
        var resolver = Activator.CreateInstance(resolverType)!;
        var changes = R.Call(resolver, "Resolve", request) as IEnumerable ?? throw new BridgeException("ScheduledTaskResolverUnavailable", "The native task deletion resolver returned no plan.");
        var plan = CaptureNativeModules(changes);
        if (plan?.NativeChanges is not { Count: 1 } native || plan.BeforeGroups.Count != 1 ||
            !ReferenceEquals(plan.BeforeGroups[0].NativeIdentity, before.NativeIdentity) ||
            native[0].GetType().GetField("Item2")?.GetValue(native[0])?.ToString() != "Remove")
            throw new BridgeException("ScheduledTaskResolverChanged", "The native task deletion resolver did not return the exact single Remove owner.");
        return new("scheduledTask", "deleteScheduledTask", parameters.Clone(), address, ModuleOperation.Delete, before, plan);
    }

    internal void RequireNewDesktopPageFilesAbsent(string name)
    {
        var source = (string?)R.Call(save, "GetDocumentPath");
        var working = (string?)R.Get(save, "LocalPath");
        var staged = (string?)R.Get(save, "LastPath");
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(working) || string.IsNullOrWhiteSpace(staged) ||
            R.Get(save, "Loaded") is not true)
            throw new BridgeException("NativePageCreateSourceUnavailable", "The bound native page source and cache roots are not available.");
        RequireDesktopPageFilesAbsent(name, [source, working, staged]);
    }

    internal static void RequireDesktopPageFilesAbsent(string name, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Trim() != name ||
            name.EndsWith('.') || name == "." || name == "..")
            throw new BridgeException("NativePageCreateSourceUnavailable", "The requested page name cannot be mapped to a canonical native file pair.");
        foreach (var root in roots)
            foreach (var extension in new[] { ".json", ".rd" })
            {
                var path = NativeSaveVerification.Under(root, "Pages/" + name + extension);
                var folder = Path.GetDirectoryName(path)!;
                if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any(file =>
                    Path.GetFileName(file).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)))
                    throw new BridgeException("NativePageCreateSourceCollision", "An existing page file or cache entry uses the requested name.");
            }
    }
    private NativeApiFileCapture? CapturePageCreate(JsonElement parameters)
    {
        NativeApiCatalog.ValidateParameters("page","createPage",parameters);
        RequirePureReferenceContext();
        var name=Text(parameters,"name");
        var kind=Text(parameters,"type") switch { "master"=>ModuleKind.MasterPage, "userControl"=>ModuleKind.UserControlPage, null or "normal"=>ModuleKind.Page, _=>(ModuleKind?)null };
        if(string.IsNullOrWhiteSpace(name)||kind is null || Resolve(new(kind.Value,name)) is not null) return null;
        var descriptors=((IEnumerable)R.Static(assembly.GetType("aam",true)!,"GetCurrentAssemblyDescriptors")!).Cast<object>();
        var descriptor=descriptors.SingleOrDefault(d=>R.Get(d,"Namespace") as string=="page" && R.Get(d,"Method") as string=="createPage");
        if(descriptor is null || R.Get(descriptor,"MethodInfo") is not MethodInfo method || R.Get(descriptor,"RequestType") is not Type requestType) return null;
        var attribute=method.GetCustomAttributesData().SingleOrDefault(a=>a.AttributeType.FullName=="abc");
        if(attribute?.ConstructorArguments.Single().Value is not Type resolverType || resolverType.Assembly!=assembly || resolverType.FullName!="abe+ae") return null;
        var request=JsonSerializer.Deserialize(parameters.GetRawText(),requestType,new JsonSerializerOptions { PropertyNameCaseInsensitive=true });
        if(request is null) return null;
        var changes=R.Call(Activator.CreateInstance(resolverType)!,"Resolve",request) as IEnumerable;
        if(changes is null) return null;
        var plan=CaptureNativeModules(changes);
        if(plan is null || plan.ExpectedNewModules.Count!=1) return null;
        var address=new ModuleAddress(kind.Value,name);
        plan=new(plan.BeforeGroups,plan.ExpectedNewModules,plan.ExpectedNewModules.ToDictionary(x=>x.Name,_=>address,StringComparer.Ordinal),plan.NativeChanges);
        return new("page","createPage",parameters.Clone(),address,ModuleOperation.Create,null,plan);
    }

    private NativeApiFileCapture? CapturePageCellWrite(string method, JsonElement parameters)
    {
        NativeApiCatalog.ValidateParameters("page", method, parameters);
        var descriptors = ((IEnumerable)R.Static(assembly.GetType("aam", true)!, "GetCurrentAssemblyDescriptors")!).Cast<object>();
        var descriptor = descriptors.SingleOrDefault(d => R.Get(d, "Namespace") as string == "page" && R.Get(d, "Method") as string == method);
        if (descriptor is null || R.Get(descriptor, "MethodInfo") is not MethodInfo nativeMethod ||
            R.Get(descriptor, "RequestType") is not Type requestType) return null;
        var attribute = nativeMethod.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType.FullName == "abc");
        if (attribute?.ConstructorArguments.Single().Value is not Type resolverType ||
            resolverType.Assembly != assembly || resolverType.FullName != "abe+ah") return null;

        if (method is "setCells" or "updateCells")
        {
            var cells = parameters.EnumerateObject().SingleOrDefault(property => property.Name.Equals("cells", StringComparison.OrdinalIgnoreCase)).Value;
            if (cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() == 0) return null;
        }
        var requestedPage = parameters.EnumerateObject().SingleOrDefault(property => property.Name.Equals("pageName", StringComparison.OrdinalIgnoreCase)).Value;
        if (requestedPage.ValueKind != JsonValueKind.String || requestedPage.GetString() is not string { Length: > 0 } pageName) return null;
        var page = R.Call(R.Get(settings, "Pages")!, "TryGetPage", pageName);
        if (page is null) return null;
        // The pinned resolver reads only PageName. The registry validates the actual cell DTOs.
        var request = method == "clearCells" ? ReadClearRequest(requestType, parameters, pageName) : Activator.CreateInstance(requestType)!;
        R.Set(request, "PageName", pageName);
        var resolver = Activator.CreateInstance(resolverType)!;
        if (R.Call(resolver, "Resolve", request) is not IEnumerable changes) return null;
        var plan = CaptureNativeModulesCore(changes, pageReferences: false);
        if (plan?.NativeChanges is not { Count: 1 } edits ||
            edits[0].GetType().GetField("Item2")?.GetValue(edits[0])?.ToString() != "Edit") return null;
        var primary = plan.BeforeGroups.SingleOrDefault(group => ReferenceEquals(group.NativeIdentity, page));
        if(method=="updatePageLoadCommands" && primary is not null)
        {
            var json=primary.RequiredFiles.SingleOrDefault(path=>path.EndsWith(".json",StringComparison.OrdinalIgnoreCase));
            if(json is null || !primary.ExpectedJson.TryGetValue(json,out var projected) || !NativeConditionPairProjection.SamePageSaveData(projected,projected))
                throw new BridgeException("CommandTreeReferenceUnverified","Native page command clone IDs or their same-page references cannot be safely verified.");
        }
        return primary is null ? null : new("page", method, parameters.Clone(), primary.Address, ModuleOperation.Update, primary, plan);
    }

    private object ReadClearRequest(Type requestType, JsonElement parameters, string pageName)
    {
        // Mirror ClearCells' native DTO defaults, A1 precedence and range checks without clearing the worksheet.
        var actions = assembly.GetType("Forguncy.AI.Actions.PageSandboxAIActions", true)!;
        var json = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
        try
        {
            var request = R.Static(json, "DeserializeObject", parameters.GetRawText(), requestType)!;
            var sheet = R.Static(actions, "GetWorksheetOrThrow", pageName)!;
            var range = (string?)R.Get(request, "Range");
            var (startRow, startCol, endRow, endCol) = string.IsNullOrWhiteSpace(range)
                ? ((int)R.Get(request, "StartRow")!, (int)R.Get(request, "StartCol")!,
                   (int)R.Get(request, "EndRow")!, (int)R.Get(request, "EndCol")!)
                : ((int, int, int, int))R.Static(actions, "ParseA1Range", range)!;
            R.Static(actions, "ValidateCellRange", startRow, startCol, endRow, endCol, sheet);
            return request;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or OverflowException || e.GetType().FullName == "Newtonsoft.Json.JsonSerializationException")
        { throw new BridgeException("InvalidCellRange", "clearCells range or coordinates: " + e.Message); }
    }

    private NativeApiFileCapture? CapturePageReferences(string method, JsonElement parameters)
    {
        NativeApiCatalog.ValidateParameters("page", method, parameters);
        RequirePureReferenceContext();
        if (Text(parameters, "pageName") is not { Length: > 0 } pageName || method == "renamePage" && Text(parameters, "newName") is not { Length: > 0 }) return null;
        var model = R.Call(R.Get(settings, "Pages")!, "TryGetPage", pageName); if (model is null) return null;
        var kind = model.GetType().Name switch { "NormalPage" or "TemplatePage" => ModuleKind.Page, "MasterPage" => ModuleKind.MasterPage, "UserControlPage" => ModuleKind.UserControlPage, _ => (ModuleKind?)null };
        if (kind is null) return null;
        var primary = Resolve(new(kind.Value, pageName));
        if (primary is null || !ReferenceEquals(primary.NativeIdentity, model)) return null;
        var descriptors = ((IEnumerable)R.Static(assembly.GetType("aam", true)!, "GetCurrentAssemblyDescriptors")!).Cast<object>();
        var descriptor = descriptors.SingleOrDefault(d => R.Get(d, "Namespace") as string == "page" && R.Get(d, "Method") as string == method);
        if (descriptor is null || R.Get(descriptor, "MethodInfo") is not MethodInfo nativeMethod || R.Get(descriptor, "RequestType") is not Type requestType) return null;
        var attribute = nativeMethod.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType.FullName == "abc");
        if (attribute?.ConstructorArguments.Single().Value is not Type resolverType || resolverType.Assembly != assembly ||
            resolverType.FullName != (method == "renamePage" ? "abe+ag" : "abe+af")) return null;
        // These fixed DTOs contain only scalar page identity fields. Never use
        // client CLR discriminators or invoke the mutating descriptor method.
        var request = Activator.CreateInstance(requestType)!; R.Set(request, "PageName", pageName);
        if (method == "renamePage") R.Set(request, "NewName", Text(parameters, "newName"));
        var resolver = Activator.CreateInstance(resolverType)!;
        var changes = R.Call(resolver, "Resolve", request) as IEnumerable; if (changes is null) return null;
        var plan = CaptureNativeModules(changes); if (plan is null) return null;
        var before = plan.BeforeGroups.SingleOrDefault(g => ReferenceEquals(g.NativeIdentity, model));
        return before is null ? null : new("page", method, parameters.Clone(), new(kind.Value, pageName), method == "deletePage" ? ModuleOperation.Delete : ModuleOperation.Update, before, plan);
    }

    private NativeApiFileCapture? CaptureTableReferences(string ns, string method, JsonElement parameters)
    {
        var deleting = ns == "table" && method == "deleteTable" || ns == "table.view" && method == "delete";
        var helper = new NativeTableDefinitionEffects(assembly);
        var effect = NativeTableDefinitionEffects.IsReferenceUpdate(ns, method, parameters) ? helper.TryResolveReferenceUpdate(method, parameters, settings, RequirePureReferenceContext)
            : deleting ? helper.TryResolveDeletion(ns, method, parameters, settings, RequirePureReferenceContext)
            : helper.TryResolve(ns, method, parameters, settings);
        if (effect is null)
        {
            if (ns == "table" && method == "createTable")
                throw new BridgeException("NativeTableCreatePlanUnavailable",
                    (helper.UnavailableReason ?? "The exact native table creation owners are unavailable.") +
                    " No checkout or table creation was dispatched.");
            return null;
        }
        if (effect.RequiresIncomingReferenceVeto) effect.RequireNoIncomingReferences();
        var native = effect.Effects.Select(x => x.NativeTuple).ToArray();
        var plan = deleting ? CaptureNativeModules(native) : CaptureNativeModulesCore(native, false);
        if (plan is null)
        {
            if (ns == "table" && method == "createTable")
                throw new BridgeException("NativeTableCreateOwnerFilesUnavailable",
                    "The exact native table creation owner files could not be captured. No checkout or table creation was dispatched.");
            return null;
        }
        var address = new ModuleAddress(ns == "table.view" ? ModuleKind.View : ModuleKind.Table, effect.TargetName);
        if (effect.RequiresCreatedTargetToken)
            plan = new(plan.BeforeGroups, plan.ExpectedNewModules, plan.ExpectedNewModules.ToDictionary(x => x.Name, _ => address, StringComparer.Ordinal), plan.NativeChanges);
        var before = effect.RequiresCreatedTargetToken ? null : Resolve(address);
        if (!effect.RequiresCreatedTargetToken && (before is null || !plan.BeforeGroups.Any(group => ReferenceEquals(group.NativeIdentity, before.NativeIdentity)))) return null;
        var application = R.Get(settings, "ApplicationSettings");
        var owned = deleting ? effect.Effects.Where(x => ReferenceEquals(x.Model, application)).Select(x => (string)R.Get(x.Module, "ModuleName")!).Distinct(StringComparer.Ordinal).ToArray() : [];
        if (owned.Any(name => !plan.BeforeGroups.Any(group => group.NativeModuleName == name && group.Address.Kind == ModuleKind.ApplicationSettings))) return null;
        return new(ns, method, parameters.Clone(), address, deleting ? ModuleOperation.Delete : effect.RequiresCreatedTargetToken ? ModuleOperation.Create : ModuleOperation.Update,
            before, plan, owned.Length == 0 ? null : owned);
    }

    private NativeApiFileCapture? CaptureDefinitionReferences(string method, JsonElement parameters)
    {
        var fields = UniqueFields(parameters); if (fields is null || Scalar(fields, "pageName") is not { Length: > 0 } pageName) return null;
        var spec = method switch { "upsertProperty" => ("Property", "abe+ak"), "upsertEvent" => ("Event", "abe+al"), "upsertMethod" => ("Method", "abe+am"), _ => default };
        if (spec.Item1 is null || !fields.TryGetValue(spec.Item1, out var nested)) return null;
        var definition = UniqueFields(nested); if (definition is null || Scalar(definition, "Name") is not { Length: > 0 } name) return null;
        var oldName = Scalar(definition, "OldName");
        if (definition.TryGetValue("OldName", out var old) && old.ValueKind != JsonValueKind.Null && (old.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(oldName))) return null;
        RequirePureReferenceContext();
        var model = R.Call(R.Get(settings, "Pages")!, "TryGetPage", pageName);
        var kind = model?.GetType().Name switch { "NormalPage" or "TemplatePage" => ModuleKind.Page, "MasterPage" => ModuleKind.MasterPage, "UserControlPage" => ModuleKind.UserControlPage, _ => (ModuleKind?)null };
        if (kind is null) return null;
        var primary = Resolve(new(kind.Value, pageName));
        if (primary is null || !ReferenceEquals(primary.NativeIdentity, model)) return null;
        var descriptors = ((IEnumerable)R.Static(assembly.GetType("aam", true)!, "GetCurrentAssemblyDescriptors")!).Cast<object>();
        var descriptor = descriptors.SingleOrDefault(d => R.Get(d, "Namespace") as string == "page.definition" && R.Get(d, "Method") as string == method);
        if (descriptor is null || R.Get(descriptor, "MethodInfo") is not MethodInfo nativeMethod || R.Get(descriptor, "RequestType") is not Type requestType) return null;
        var attribute = nativeMethod.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType.FullName == "abc");
        if (attribute?.ConstructorArguments.Single().Value is not Type resolverType || resolverType.Assembly != assembly || resolverType.FullName != spec.Item2) return null;
        var request = Activator.CreateInstance(requestType)!; R.Set(request, "PageName", pageName);
        var property = requestType.GetProperty(spec.Item1, R.Flags)!; var input = Activator.CreateInstance(property.PropertyType)!;
        // Resolver selection reads only these exact scalar fields. Never parse
        // nested command definitions, extension data or client CLR metadata.
        R.Set(input, "Name", name); R.Set(input, "OldName", oldName); property.SetValue(request, input);
        var changes = R.Call(Activator.CreateInstance(resolverType)!, "Resolve", request) as IEnumerable; if (changes is null) return null;
        var plan = CaptureNativeModules(changes);
        return plan is not null && plan.BeforeGroups.Any(g => ReferenceEquals(g.NativeIdentity, model))
            ? new("page.definition", method, parameters.Clone(), primary.Address, ModuleOperation.Update, primary, plan) : null;
    }

    private static Dictionary<string, JsonElement>? UniqueFields(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject()) if (!fields.TryAdd(property.Name, property.Value)) return null;
        return fields;
    }
    private static string? Scalar(IReadOnlyDictionary<string, JsonElement> fields, string name)
        => fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private NativeModuleFileGroup Connections() => new NativeConnections(assembly, settings, (string)R.Call(save, "GetDocumentPath")!).CaptureOwnerGroup();

    private NativeModuleFileGroup Theme()
    {
        var manager = R.Static(assembly.GetType("Forguncy.ThemesManager", true)!, "get_Instance")!;
        var simpleType = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var create = simpleType.GetMethods(R.Flags).Single(method => method.Name == "Create" && method.GetParameters().Length == 2 &&
            method.GetParameters()[0].ParameterType.FullName == "Forguncy.Collaboration.ModuleType");
        var simple = create.Invoke(null, [Enum.Parse(create.GetParameters()[0].ParameterType, "Theme"), Enum.ToObject(create.GetParameters()[1].ParameterType, 0)])!;
        return NativeThemeFileGroup.Capture(assembly, settings, manager, (string)R.Get(save, "LocalPath")!,
            (string)R.Get(simple, "ModuleName")!, () => DirectReferences(simple));
    }

    private NativeModuleFileGroup Application(ModuleAddress address)
    {
        var colleague = R.Get(colleagues, "ApplicationSettings")!;
        var mediator = R.Get(colleague, "ApplicationSettingsMediator")!;
        var loader = R.Get(mediator, "SaveDataColleague")!;
        string CurrentJson()
        {
            var model = R.Call(colleague, "GetSaveModel")!;
            // Native StartPage's getter can normalize its private stored field.
            // Map a shallow native clone so this read-only projection never
            // performs that normalization on the live settings object.
            model = typeof(object).GetMethod("MemberwiseClone", R.Flags)!.Invoke(model, null)!;
            var mapper = assembly.GetType("hk", true)!.GetProperty("Mapper", R.Flags)!.GetValue(null)!;
            var map = mapper.GetType().GetMethods(R.Flags).Single(m => m.Name == "Map" && !m.ContainsGenericParameters && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(Type));
            var dto = map.Invoke(mapper, [model, model.GetType(), Assembly.Load("ServerDesignerCommon").GetType("Forguncy.SaveLoad.ApplicationSettingsSaveData", true)!])!;
            // The native application loader transforms AI/MCP secrets before serialization.
            // Run that exact transform on a detached DTO/memento, never on the live model/cache.
            var detachedMediator = Activator.CreateInstance(assembly.GetType("bu", true)!)!;
            var detachedMemento = Activator.CreateInstance(assembly.GetType("cp", true)!)!;
            var detachedLoader = Activator.CreateInstance(assembly.GetType("bv", true)!, [detachedMediator, save, detachedMemento])!;
            var copy = R.Call(detachedLoader, "ConvertToT", new MemoryStream(Encoding.UTF8.GetBytes(Serialize(dto, loader))))!;
            R.Call(detachedLoader, "SaveToCache", copy);
            return Serialize(copy, loader);
        }
        const string key = "ApplicationSettings.json";
        var json = CurrentJson();
        return new(address, "ApplicationSetting", [key], [], [], new Dictionary<string, string> { [key] = json }, () => Equivalent(CurrentJson(), json),
            nativeIdentity: colleague, isPresent: () => ReferenceEquals(R.Get(colleagues, "ApplicationSettings"), colleague));
    }

    private NativeModuleFileGroup Libraries(ModuleAddress address)
    {
        var colleague = R.Get(colleagues, "CustomLibraries")!;
        var mediator = RootMediator(colleague);
        var loader = R.Get(mediator, "SaveDataColleague")!;
        var key = NativePath((string)R.Get(loader, "RootPath")!);
        var userRoot = (string)assembly.GetType("Forguncy.Common.Config", true)!.GetProperty("LocalUserFilePath", R.Flags)!.GetValue(null)!;
        if (NativePath(Path.Combine(userRoot, "native-root-probe")) != "UserFile/native-root-probe")
            throw new BridgeException("NativeLibraryCacheMismatch", "Native library directory does not belong to the bound document cache.");
        object Model() => R.Call(colleague, "GetSaveModel")!;
        string CurrentJson() => Serialize(Map(Model(), mediator.GetType().BaseType!.GetGenericArguments()[1]), loader);
        var json = CurrentJson();
        var binaries = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var listName in new[] { "UserJSFileList", "UserCSSFileList" })
            if (R.Get(Model(), listName) is IEnumerable values)
                foreach (var file in values.Cast<object>().Where(x => R.Get(x, "IsLink") is false))
                {
                    var path = NativePath(Path.Combine(userRoot, (string)R.Get(file, "Name")!));
                    if (!binaries.TryAdd(path, LocalHash(path))) throw new BridgeException("DuplicateNativeLibraryFile", "Native local-library declarations contain a duplicate file path.");
                }
        return new(address, "CustomLibrary", new[] { key }.Concat(binaries.Keys).ToArray(), [], [], new Dictionary<string, string> { [key] = json },
            () => Equivalent(CurrentJson(), json) && binaries.All(x => LocalHash(x.Key) == x.Value),
            (file, bytes) => file == key ? JsonMatches(bytes, json) : binaries.TryGetValue(file, out var hash) && hash is not null && Hash(bytes) == hash,
            colleague, () => ReferenceEquals(R.Get(colleagues, "CustomLibraries"), colleague));
    }

    private NativeModuleFileGroup? PermissionGroups(ModuleAddress address)
    {
        var app = R.Get(settings, "ApplicationSettings")!;
        if (Convert.ToInt32(R.Get(app, "PermissionMode")) != 1) return null;
        var colleague = R.Get(colleagues, "PermissionGroupConfig")!;
        var loader = R.Get(R.Get(colleague, "Mediator")!, "SaveDataColleague")!;
        var root = NativePath((string)R.Get(loader, "RootPath")!);
        string Current(string property) => Serialize(R.Get(R.Call(colleague, "GetSaveModel")!, property)!, loader);
        var expected = new Dictionary<string, string> { [root + "/PermissionGroups"] = Current("PermissionGroups"), [root + "/RolePermissionGroup"] = Current("RolePermissionGroups") };
        return new(address, "PermissionGroupConfig", expected.Keys.ToArray(), [], [], expected,
            () => Convert.ToInt32(R.Get(app, "PermissionMode")) == 1 && expected[root + "/PermissionGroups"] == Current("PermissionGroups") && expected[root + "/RolePermissionGroup"] == Current("RolePermissionGroups"),
            nativeIdentity: colleague, isPresent: () => ReferenceEquals(R.Get(colleagues, "PermissionGroupConfig"), colleague));
    }

    private NativeModuleFileGroup Publish(bool defaults)
    {
        var member = defaults ? "PublishSetting" : "PublishConfig";
        var colleague = R.Get(colleagues, member)!;
        var mediator = RootMediator(colleague); var loader = R.Get(mediator, "SaveDataColleague")!;
        var key = NativePath((string)R.Get(loader, "RootPath")!);
        string CurrentJson()
        {
            var data = Map(R.Call(colleague, "GetSaveModel")!, mediator.GetType().BaseType!.GetGenericArguments()[1]);
            var normalized = R.Call(loader, "ConvertToT", new MemoryStream(Encoding.UTF8.GetBytes(Serialize(data, loader))))!;
            return Serialize(normalized, loader);
        }
        var expected = CurrentJson();
        return new(new(ModuleKind.ApplicationSettings, defaults ? "DefaultPublishSettings" : "PublishSettingsConfig"),
            defaults ? "PublishSetting" : "PublishConfiguration", [key], [], [], new Dictionary<string, string> { [key] = expected },
            () => Equivalent(CurrentJson(), expected), (file, bytes) => file == key &&
                Equivalent(Serialize(R.Call(loader, "ConvertToT", new MemoryStream(bytes))!, loader), expected),
            colleague, () => ReferenceEquals(R.Get(colleagues, member), colleague));
    }

    private NativeModuleFileGroup? Image(ModuleAddress address)
    {
        var colleague = R.Get(colleagues, "ImageModel")!;
        var root = R.Get(colleague, "Root")!;
        var models = Nodes(root).Where(m => IsLeaf(m) && R.Get(m, "Name") as string == address.Name).ToArray();
        if (models.Length == 0) return null;
        if (models.Length != 1) throw new BridgeException("NativeImageNotUnique", "Native uploaded image name is ambiguous.");
        var model = models[0];
        if (R.Get(model, "BuiltIn") is true) return null;
        var key = NativePath((string)R.Call(colleague, "GetKey", model)!);
        var logicalFolder = Path.GetDirectoryName(key.Replace('/', Path.DirectorySeparatorChar))!.Replace('\\', '/');
        logicalFolder = logicalFolder == "image" ? "" : logicalFolder.StartsWith("image/", StringComparison.Ordinal) ? logicalFolder[6..] : logicalFolder;
        if (address.FolderPath is not null && address.FolderPath != logicalFolder)
            throw new BridgeException("NativeImageFolderMismatch", "Requested image folder differs from its native registration.");
        var manager = assembly.GetType("Forguncy.Common.ImageManager", true)!.GetProperty("Instance", R.Flags)!.GetValue(null)!;
        var localImageRoot = (string)R.Get(manager, "LocalImagesPath")!;
        if (NativePath(Path.Combine(localImageRoot, "native-root-probe")) != "image/native-root-probe" ||
            R.Get(manager, "ImageNameToFullNameCache") is not IDictionary registry || registry[address.Name] is not string registered || NativePath(registered) != key ||
            NativePath((string)R.Get(model, "FullName")!) != key)
            throw new BridgeException("NativeImageCacheMismatch", "Native image tree, filename registry and bound image cache disagree.");
        var hash = LocalHash(key);
        var simple = R.Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model)!;
        return new(address, (string)R.Get(simple, "ModuleName")!, [key], [], [], new Dictionary<string, string>(),
            () => Nodes(root).Any(m => ReferenceEquals(m, model)) && R.Get(model, "Name") as string == address.Name &&
                NativePath((string)R.Call(colleague, "GetKey", model)!) == key && NativePath((string)R.Get(model, "FullName")!) == key &&
                registry[address.Name] as string == registered && LocalHash(key) == hash,
            (_, bytes) => hash is not null && Hash(bytes) == hash, model, () => Nodes(root).Any(m => ReferenceEquals(m, model)));
    }

    private object Map(object model, Type destination)
    {
        var mapper = assembly.GetType("hk", true)!.GetProperty("Mapper", R.Flags)!.GetValue(null)!;
        var map = mapper.GetType().GetMethods(R.Flags).Single(m => m.Name == "Map" && !m.ContainsGenericParameters && m.GetParameters().Length == 3 && m.GetParameters()[1].ParameterType == typeof(Type));
        return map.Invoke(mapper, [model, model.GetType(), destination])!;
    }
    private static object RootMediator(object colleague)
    {
        for (var type = colleague.GetType(); type is not null; type = type.BaseType)
            if (type.GetProperty("ServerDesignerCommon.IColleague.Mediator", R.Flags | BindingFlags.DeclaredOnly) is { } property) return property.GetValue(colleague)!;
        throw new BridgeException("NativeRootMediatorUnavailable", "Native root colleague mediator is unavailable.");
    }
    private string? LocalHash(string relative)
    {
        var path = NativeSaveVerification.Under((string)R.Get(save, "LocalPath")!, relative);
        if (!File.Exists(path)) return null;
        if (!Path.GetExtension(path).Equals(".sqlite3", StringComparison.OrdinalIgnoreCase) && new FileInfo(path).Length > 64 * 1024 * 1024)
            throw new BridgeException("NativeTargetTooLarge", "Native target exceeds the file verification limit.");
        return FileHash(path);
    }
    private static string FileHash(string path)
    {
        if (!Path.GetExtension(path).Equals(".sqlite3", StringComparison.OrdinalIgnoreCase))
        { using var input = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(input)); }
        // The exact native archive writer backs up SQLite before creating the
        // archive entry. Compare that same native snapshot, rather than unsafe
        // reads of a pooled/WAL database or raw bytes the writer never archives.
        var temporary = Path.Combine(Path.GetTempPath(), "forguncy-v12-filegroup-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            R.Static(Assembly.Load("ForguncyDataBaseService").GetType("ForguncyDataAccess.DataAccess", true)!, "SynchronizeBuiltinDBFile", path, temporary);
            using var input = File.OpenRead(temporary); return Convert.ToHexString(SHA256.HashData(input));
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private string Serialize(object data, object loader)
    {
        var json = Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!;
        return (string)R.Static(json, "GetJsonStringNoFormat", data, false, null, R.Call(loader, "GetJsonSerializerSettings"))!;
    }
    private object PageNative(object loader, string method, object argument) =>
        assembly.GetType("cu", true)!.GetMethod(method, R.Flags)!.Invoke(loader, [argument])!;
    private IEnumerable<string> PageReferences(object model)
    {
        var extensions = assembly.GetType("Forguncy.Model.Pages.PageExtensions", true)!;
        return ((IEnumerable)R.Static(extensions, "GetReferFiles", model)!).Cast<string>().Select(NativePath).Distinct(StringComparer.Ordinal).ToArray();
    }
    private IEnumerable<string> DirectReferences(object simple)
    {
        var moduleType = Assembly.Load("Forguncy.Collaboration").GetType("Forguncy.Collaboration.Module", true)!;
        var dictionary = Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string), moduleType))!;
        var module = R.Call(simple, "ToModule", dictionary)!;
        return ((IEnumerable)R.Static(assembly.GetType("Forguncy.Common.ModuleExtensions", true)!, "GetDirectReferFiles", module,
            (string)R.Get(save, "LastPath")!, "../Upload")!).Cast<string>().Select(NativePath).Distinct(StringComparer.Ordinal).ToArray();
    }
    private static IEnumerable<object> Nodes(object root)
    { yield return root; if (root.GetType().GetProperty("Items", R.Flags)?.GetValue(root) is IEnumerable items) foreach (var item in items) foreach (var node in Nodes(item!)) yield return node; }
    private static IEnumerable<object> Templates(object colleague) => colleague.GetType().GetProperty("TemplatePages", R.Flags)?.GetValue(colleague) is IEnumerable values ? values.Cast<object>() : [];
    private static bool IsLeaf(object model) => !model.GetType().GetInterfaces().Any(i => i.FullName == "Forguncy.Model.ITreeNodeFolder");
    private static bool IsPage(ModuleKind kind) => kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage;
    private string NativePath(string value)
    {
        if (!Path.IsPathFullyQualified(value)) return NativeSaveVerification.Relative(".", value);
        var full = Path.GetFullPath(value);
        foreach (var member in new[] { "LastPath", "LocalPath" })
        {
            var root = Path.GetFullPath((string)R.Get(save, member)!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return NativeSaveVerification.Relative(root, full);
        }
        throw new BridgeException("NativeReferenceOutsideCache", "Native referenced file is outside the bound working/staging caches.");
    }
    private static string? Text(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Equivalent(string a, string b) { try { return JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b)); } catch (JsonException) { return false; } }
    internal static bool JsonMatches(byte[] bytes, string expected) => Equivalent(Decode(bytes), expected);
    private static string Decode(byte[] bytes) { using var reader = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false, true), true); return reader.ReadToEnd(); }
    private static object SaveFile(object document)
    { var colleague = R.Get(R.Get(document, "ForguncyColleagueManager")!, "ReportModel")!; return R.Get(R.Get(R.Get(colleague, "Mediator")!, "SaveDataColleague")!, "SaveLoadFile")!; }
}
