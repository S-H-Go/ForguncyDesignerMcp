using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>Project-owned theme, style and public-language definitions. Never installs user themes or writes source files.</summary>
public sealed class NativeModules
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly Assembly assembly;
    private readonly object settings;
    private readonly object? document;
    private readonly string? source;
    private readonly Action<string>? componentDirty;
    private readonly Func<ModuleAddress, ReferenceResult>? componentReferences;
    private readonly object? componentThemeManager;
    private static readonly ModuleKind[] Kinds = [ModuleKind.Theme, ModuleKind.CellStyle, ModuleKind.ListViewStyle, ModuleKind.LanguageResource, ModuleKind.GlobalVariable, ModuleKind.ApplicationSettings];
    private static readonly HashSet<string> ProtectedApplicationProperties = new(StringComparer.Ordinal)
    {
        "WebSitePort", "UserServicePort", "BpmServerSchemaId", "AuthenticationMode", "PermissionMode", "ServerPropertySecurityID", "AIHistoryProjectID",
        "AppMCPConfigSettingEncrypt", "OfflinePublishPassword", "StatusCacheTableName", "NameConflictPolicy", "StartPage", "ReportViewerTheme"
    };

    public NativeModules(AdapterHostContext context)
    {
        assembly = context.DesignerAssembly;
        document = context.NativeDocument;
        settings = Get(document, "ForguncySettings")!;
        source = context.CaptureSnapshot().Target.ProjectPath;
        CheckVersion();
    }

    // Isolated native-component harness: the supplied settings must be the real pinned lt model.
    // This overload performs no document save and cannot report persisted source verification.
    public NativeModules(Assembly assembly, object nativeSettings, Action<string> dirty,
        Func<ModuleAddress, ReferenceResult> references, string? sourceProject = null, object? nativeThemeManager = null)
    {
        this.assembly = assembly; settings = nativeSettings; componentDirty = dirty; componentReferences = references;
        source = sourceProject;
        componentThemeManager = nativeThemeManager;
        CheckVersion();
        if (settings.GetType() != assembly.GetType("lt", true)) throw new ArgumentException("Expected the pinned native project settings model.");
    }

    private void CheckVersion()
    {
        if (assembly.GetName().Version != new Version(12, 0, 102, 0) ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != V12AdapterFactory.DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild", "Style and language adapters require the exact V12 component fingerprint.");
    }

    public IReadOnlyList<ModuleCapability> Capabilities() => Kinds.SelectMany(kind => Enum.GetValues<ModuleOperation>().Select(operation =>
        new ModuleCapability(kind, operation, Supports(kind, operation),
            "native-model-file-saver-and-dirty-components-verified; full-reference-scan-collaboration-and-live-designer-save-pending",
            kind is ModuleKind.Theme or ModuleKind.ApplicationSettings && operation is ModuleOperation.Create or ModuleOperation.Delete
                ? kind == ModuleKind.Theme ? "Current project theme is a required singleton. User-level theme installation/removal is outside project CRUD." :
                    "Application settings are a required singleton. Root deletion is unavailable; supported scalar properties can be updated." : null))).ToArray();

    public bool Supports(ModuleKind kind, ModuleOperation operation) => Kinds.Contains(kind) &&
        (kind is not (ModuleKind.Theme or ModuleKind.ApplicationSettings) || operation is ModuleOperation.List or ModuleOperation.Read or ModuleOperation.Update);

    public string GetModuleName(ModuleAddress address)
    {
        var simple = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var create = simple.GetMethods(Flags).Single(m => m.Name == "Create" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.FullName == "Forguncy.Collaboration.ModuleType");
        var moduleType = create.GetParameters()[0].ParameterType;
        var module = create.Invoke(null, [Enum.Parse(moduleType, IsLanguage(address.Kind) ? "ApplicationSetting" : "Theme"),
            Enum.ToObject(create.GetParameters()[1].ParameterType, 0)])!;
        return (string)Get(module, "ModuleName")!;
    }

    public ModulePage List(ModuleQuery query)
    {
        if (!Supports(query.Kind, ModuleOperation.List)) throw new BridgeException("ModuleNotImplemented", "This module does not have a native settings adapter.");
        if (query.Limit is < 1 or > 1000) throw new BridgeException("InvalidPageSize", "Module limit must be between 1 and 1000.");
        var entries = Addresses(query.Kind).Where(a => query.FolderPath is null || a.FolderPath == query.FolderPath)
            .OrderBy(a => a.FolderPath, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal).ToArray();
        var offset = 0;
        if (query.Cursor is not null && (!int.TryParse(query.Cursor, out offset) || offset < 0)) throw new BridgeException("InvalidCursor", "Invalid settings cursor.");
        var selected = entries.Skip(offset).Take(query.Limit).Select(a => { var d = Read(a); return new ModuleSummary(a, d.Revision); }).ToArray();
        return new ModulePage(selected, offset + selected.Length < entries.Length ? (offset + selected.Length).ToString() : null);
    }

    public ModuleDocument Read(ModuleAddress address)
    {
        ValidateAddress(address);
        var value = Find(address) ?? throw new BridgeException("ModuleNotFound", "The project-owned style or language entry was not found.");
        var json = address.Kind == ModuleKind.ApplicationSettings ? SerializeApplication(value) : Serialize(value);
        using var parsed = JsonDocument.Parse(json);
        return new(address, parsed.RootElement.Clone(), [], Hash(json + "\n" + SourceBaseline(address)));
    }

    public ReferenceResult References(ModuleAddress address)
    {
        ValidateAddress(address);
        if (componentReferences is not null) return componentReferences(address);
        try
        {
            var result = new List<ModuleReference>();
            if (address.Kind == ModuleKind.GlobalVariable)
            {
                // Match the native GlobalPropertyIsUsed guard exactly: inspect typed command/plugin references and
                // call the single-name formula enumerator (a same-name rename map can omit unchanged formulas).
                var formulas = (IDictionary)Static(assembly.GetType("Forguncy.Common.FormulaReferArgumentHelper", true)!,
                    "GetFormulaLocationsToNewFormulaWhenArgNameChanged", address.Name)!;
                foreach (var located in formulas.Keys) result.Add(LocatedReference(located!, address));
                foreach (var located in (IEnumerable)Static(assembly.GetType("sf", true)!, "EnumReferenceGlobalProperty")!)
                {
                    var value = Get(located!, "Object")!;
                    var contract = value.GetType().GetInterfaces().Single(t => t.Name == "IReferenceGlobalProperty");
                    var names = (IEnumerable<string>)contract.GetMethod("EnumGlobalPropertyName")!.Invoke(value, null)!;
                    if (names.Contains(address.Name, StringComparer.Ordinal)) result.Add(LocatedReference(located!, address));
                }
            }
            else if (address.Kind == ModuleKind.LanguageResource)
            {
                var type = assembly.GetType("Forguncy.Common.ReferenceHelper.MultipleLanguageResourceReferenceHelper", true)!;
                foreach (var item in (IEnumerable)Static(type, "EnumResource", new List<string> { address.Name })!)
                    result.Add(LocatedReference(item!, address));
            }
            else if (address.Kind == ModuleKind.ListViewStyle)
            {
                var type = assembly.GetType("Forguncy.Common.ListViewStyleHelper", true)!;
                foreach (var tuple in (IEnumerable)Static(type, "EnumAllStyleReferedPages", address.Name)!)
                {
                    var page = tuple!.GetType().GetField("Item1")!.GetValue(tuple)!;
                    result.Add(new(new(ModuleKind.Page, (string)Get(page, "Name")!), address, "Native ListViewInfo.Style"));
                }
            }
            else if (address.Kind == ModuleKind.CellStyle)
            {
                // Native removal executors enumerate every page affected by this style, including plugin nested styles.
                var cellType = ResolveStyleCellType(address.FolderPath!);
                var executor = Activator.CreateInstance(assembly.GetType("Forguncy.Common.CellTypeStyleRemoveExecutor", true)!, [null, address.Name, cellType])!;
                foreach (var tuple in (IEnumerable)Call(executor, "GetRelatedModules")!)
                {
                    var module = tuple!.GetType().GetField("Item1")!.GetValue(tuple)!;
                    var name = (string)Get(module, "ModuleName")!;
                    if (name != GetModuleName(address)) result.Add(new(new(ModuleKind.Page, name), address, "Native style removal dependency"));
                }
            }
            else return new([], false, "Required root settings can affect all project modules; root deletion is unavailable.");
            return new(result, true);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new([], false, "Native reference enumeration could not complete: " + error.GetType().Name); }
    }

    public ModuleValidationResult Validate(ModuleMutationRequest request) => Validate(request, null);

    internal ModuleValidationResult Validate(ModuleMutationRequest request, NativeCollaboration.EditRegistration? reviewedTracking)
    {
        var issues = new List<ValidationIssue>();
        ReferenceResult? references = null;
        try
        {
            ValidateAddress(request.Address);
            if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
                throw new BridgeException("InvalidMutationOperation", "Only Create, Update and Delete are mutations.");
            if (!Supports(request.Address.Kind, request.Operation)) throw new BridgeException("ModuleOperationUnavailable", "Operation is unavailable for this native settings module.");
            if (request.Scope != EffectScope.ProjectDefinition) throw new BridgeException("EffectScopeMismatch", "Settings definitions use ProjectDefinition scope.");
            // A read-only preview can verify native ownership before the transient save registration
            // is restored. Apply always uses Validate(request) and checks the actual SDK registration.
            RequireNativeTracking(request.Address, reviewedTracking);
            var existing = Find(request.Address);
            if (request.Address.Kind == ModuleKind.CellStyle && request.Operation == ModuleOperation.Create)
                _ = ResolveStyleAttribute(request.Address.FolderPath!);
            if (request.Operation == ModuleOperation.Create && existing is not null) throw new BridgeException("ModuleAlreadyExists", "An entry with that exact native key already exists.");
            if (request.Operation != ModuleOperation.Create && existing is null) throw new BridgeException("ModuleNotFound", "The native entry does not exist.");
            if (request.Operation != ModuleOperation.Delete)
            {
                if (request.Document is null || request.Document.Address != request.Address || request.Document.Companions.Count != 0)
                    throw new BridgeException("InvalidSettingsDocument", "Settings require matching metadata and no file assets.");
                var prepared = Prepare(request.Address, request.Document.Metadata);
                if (request.Address.Kind == ModuleKind.ApplicationSettings) _ = ApplicationChanges(prepared);
                if (request.Address.Kind == ModuleKind.Theme)
                    foreach (var name in new[] { "Name", "BasePathName", "Category", "Images", "Thumbnail" })
                        if (!Equivalent(Serialize(Get(existing!, name)), Serialize(Get(prepared, name))))
                            throw new BridgeException("ProtectedThemeResource", "Theme identity and image references must be preserved; typed resource import is required to change them.");
            }
            if (request.Operation == ModuleOperation.Delete)
            {
                RejectDefault(request.Address);
                references = References(request.Address);
                if (!references.Complete || references.References.Count != 0)
                    throw new BridgeException("ModuleHasReferences", "Deletion requires a complete native reference scan with no remaining references.");
            }
        }
        catch (Exception error)
        { issues.Add(new(error is BridgeException b ? b.Code : "NativeSettingsValidationFailed", error is BridgeException ? error.Message : "Native settings validation failed (" + error.GetType().Name + ").")); }
        return new(issues.Count == 0, issues, references);
    }

    public IReadOnlyList<string> Apply(ModuleMutationRequest request)
    {
        var validation = Validate(request);
        if (!validation.Valid) throw new BridgeException(validation.Issues[0].Code, validation.Issues[0].Message);
        var address = request.Address;
        var old = Find(address);
        var value = request.Operation == ModuleOperation.Delete ? null : Prepare(address, request.Document!.Metadata);
        // Register dirty state before the first model change. On a partial native failure the document remains visibly dirty.
        Dirty(address);
        if (address.Kind == ModuleKind.ApplicationSettings)
        {
            // Apply only scalar fields proven to map one-to-one to the native model; collections keep their dedicated CRUD paths.
            foreach (var change in ApplicationChanges(value!)) change.Property.SetValue(old, change.Value);
        }
        else if (address.Kind == ModuleKind.Theme)
        {
            var manager = ThemeManager;
            // Preserve live ThemeItem identity and native editor references.
            foreach (var name in new[] { "FontFamily", "FontSize", "ThemeColors" }) Set(old!, name, Get(value!, name));
            Call(manager, "RaiseThemeChangedEvent", (object?)null);
        }
        else if (address.Kind == ModuleKind.GlobalVariable)
        {
            var previous = GlobalEntries;
            var list = (IList)Activator.CreateInstance(previous.GetType())!;
            foreach (var item in previous) list.Add(item);
            if (old is not null) { var index = list.IndexOf(old); if (value is null) list.RemoveAt(index); else list[index] = value; }
            else list.Add(value);
            Set(Get(settings, "ApplicationSettings")!, "GlobalProperties", list);
        }
        else if (address.Kind == ModuleKind.LanguageResource)
        {
            var dictionary = LanguageEntries;
            if (value is null) dictionary.Remove(address.Name); else dictionary[address.Name] = value;
            if (document is not null) Static(assembly.GetType("Forguncy.Common.MultipleLanguageHelper", true)!, "RefreshWhenLanguageResourceChanged");
        }
        else
        {
            var list = StyleList(address, request.Operation == ModuleOperation.Create)!;
            if (old is not null) { var index = list.IndexOf(old); if (value is null) list.RemoveAt(index); else list[index] = value; }
            else list.Add(value);
            if (document is not null) Call(ThemeManager, "RaiseThemeChangedEvent", (object?)null);
        }
        var after = Find(address);
        if (value is null ? after is not null : after is null || !Equivalent(
                address.Kind == ModuleKind.ApplicationSettings ? SerializeApplication(after) : Serialize(after),
                address.Kind == ModuleKind.ApplicationSettings ? SerializeApplicationData(value) : Serialize(value)))
            throw new BridgeException("NativeSettingsReadbackMismatch", "Native settings model readback differs after mutation; persistence was not attempted.");
        return [SourceFile(address)];
    }

    public bool VerifySource(ModuleMutationRequest request)
    {
        if (source is null) return false;
        var bytes = ReadSource(SourceFile(request.Address));
        if (bytes is null) return false;
        var root = JsonNode.Parse(bytes)!;
        JsonNode? actual;
        var a = request.Address;
        if (a.Kind == ModuleKind.ApplicationSettings)
        {
            var expected = CaptureApplication();
            var defaults = Activator.CreateInstance(ApplicationSaveType)!;
            return ApplicationSaveType.GetProperties().Where(IsMutableApplicationProperty).All(p =>
            {
                var value = root[p.Name] is { } scalar ? Deserialize(scalar.ToJsonString(), p.PropertyType) :
                    p.GetCustomAttribute<System.ComponentModel.DefaultValueAttribute>()?.Value ?? p.GetValue(defaults);
                if (value is not null && p.PropertyType.IsEnum && value.GetType() != p.PropertyType) value = Enum.ToObject(p.PropertyType, value);
                return Equivalent(Serialize(value), Serialize(p.GetValue(expected)));
            });
        }
        if (a.Kind == ModuleKind.Theme) actual = root;
        else if (a.Kind == ModuleKind.GlobalVariable) actual = root["GlobalProperties"]?.AsArray().SingleOrDefault(n => n?["Name"]?.GetValue<string>() == a.Name);
        else if (a.Kind == ModuleKind.LanguageResource) actual = root["MultipleLanguageSettings"]?["PublicResource"]?[a.Name];
        else if (a.Kind == ModuleKind.ListViewStyle)
            actual = root.AsArray().SingleOrDefault(n => n?["Name"]?.GetValue<string>() == a.Name);
        else actual = root[a.FolderPath!]?.AsArray().SingleOrDefault(n => n?["Key"]?.GetValue<string>() == a.Name);
        return request.Operation == ModuleOperation.Delete ? actual is null : actual is not null && Find(a) is { } value && Equivalent(actual.ToJsonString(), Serialize(value));
    }

    private void Dirty(ModuleAddress address)
    {
        var typeName = IsLanguage(address.Kind) ? "ApplicationSetting" : "Theme";
        if (componentDirty is not null) { componentDirty(typeName); return; }
        Set(document!, "ContainsUnsavedChanges", true);
        RegisterDirty(assembly, typeName, refreshPages: address.Kind != ModuleKind.GlobalVariable);
    }

    internal static void RegisterDirty(Assembly assembly, string typeName) => RegisterDirty(assembly, typeName, true);

    private static void RegisterDirty(Assembly assembly, string typeName, bool refreshPages)
    {
        if (typeName is not ("ApplicationSetting" or "Theme" or "CustomLibrary")) throw new ArgumentOutOfRangeException(nameof(typeName));
        var cache = Static(assembly.GetType("pb", true)!, "get_Instance")!;
        var method = cache.GetType().GetMethod("DirtyModules", Flags)!;
        var tuple = method.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var parameters = tuple.GetGenericArguments();
        var create = parameters[0].GetMethods(Flags).Single(m => m.Name == "Create" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.FullName == "Forguncy.Collaboration.ModuleType");
        var moduleType = create.GetParameters()[0].ParameterType;
        var module = create.Invoke(null, [Enum.Parse(moduleType, typeName), Enum.ToObject(create.GetParameters()[1].ParameterType, 0)])!;
        var changes = Array.CreateInstance(tuple, 1);
        changes.SetValue(Activator.CreateInstance(tuple, module, Enum.Parse(parameters[1], "Edit")), 0);
        method.Invoke(cache, [null, changes]);
        if (refreshPages && typeName != "CustomLibrary") Call(cache, "DirtyAllPages");
        if (Call(cache, "IsSaveDataDirty", Enum.Parse(moduleType, typeName)) is not true)
            throw new BridgeException("NativeDirtyRegistrationFailed", "Native module dirty registration was not accepted.");
    }

    internal void RequireNativeTracking(ModuleAddress address, NativeCollaboration.EditRegistration? reviewedTracking = null)
    {
        if (document is null) return;
        var manager = Static(assembly.GetType("Forguncy.Common.CollaborationManager", true)!, "get_Instance")!;
        if (Get(manager, "InCollaborationMode") is not true) return;
        var name = GetModuleName(address);
        bool registered = reviewedTracking is null
            ? Get(manager, "SaveLoadTrackedFiles") is IDictionary tracked && tracked[name] is IEnumerable files &&
                files.Cast<string>().Any(path => NativeSaveVerification.Relative(source!, path) == SourceFile(address))
            : address.Kind == ModuleKind.GlobalVariable && reviewedTracking.Owner == name && reviewedTracking.Files.Contains(SourceFile(address));
        if (Call(manager, "IsModuleCheckoutByCurrentUser", name) is not true || !registered)
            throw new BridgeException("NativeSaveTrackingMissing", "The settings root must be checked out by the current user with its exact native file-save tracking registered.");
    }

    private object Prepare(ModuleAddress address, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidSettingsDocument", "Settings entry metadata must be an object.");
        if (address.Kind == ModuleKind.ApplicationSettings) return PrepareApplication(metadata);
        RejectTypeMetadata(metadata);
        Type type;
        if (address.Kind == ModuleKind.ApplicationSettings) type = ApplicationSaveType;
        else if (address.Kind == ModuleKind.Theme) type = assembly.GetType("Forguncy.ThemeItem", true)!;
        else if (address.Kind == ModuleKind.GlobalVariable) type = GlobalEntries.GetType().GetGenericArguments()[0];
        else if (address.Kind == ModuleKind.LanguageResource) type = LanguageEntries.GetType().GetGenericArguments()[1];
        else if (address.Kind == ModuleKind.ListViewStyle) type = ListStyles.GetType().GetGenericArguments()[0];
        else type = ((IDictionary)Get(settings, "CustomCellTypeStyleTemplates")!).GetType().GetGenericArguments()[1].GetGenericArguments()[0];
        var value = Deserialize(metadata.GetRawText(), type);
        if (address.Kind is ModuleKind.CellStyle or ModuleKind.ListViewStyle)
        {
            if (!Equals(Get(value, address.Kind == ModuleKind.CellStyle ? "Key" : "Name"), address.Name))
                throw new BridgeException("SettingsKeyMismatch", "Native style key/name must match the requested address.");
            if (address.Kind == ModuleKind.CellStyle && Convert.ToInt32(Get(value, "Scope")) is < 1 or > 3)
                throw new BridgeException("InvalidStyleScope", "Cell-style scope must contain native Listview and/or NormalCell flags.");
        }
        if (address.Kind == ModuleKind.GlobalVariable && !Equals(Get(value, "Name"), address.Name))
            throw new BridgeException("SettingsKeyMismatch", "Native global-variable name must match the requested address.");
        return value;
    }

    private void RejectDefault(ModuleAddress address)
    {
        if (address.Kind == ModuleKind.ListViewStyle && Equals(Get(settings, "DefaultListViewStyleTemplateName"), address.Name))
            throw new BridgeException("DefaultStyleProtected", "The current default list style cannot be deleted.");
        if (address.Kind == ModuleKind.CellStyle)
        {
            var defaults = (IDictionary)Get(settings, "DefaultStyleKeys")!;
            if (Equals(defaults[address.FolderPath!], address.Name)) throw new BridgeException("DefaultStyleProtected", "The current default cell style cannot be deleted.");
        }
    }

    private object? Find(ModuleAddress address) => address.Kind switch
    {
        ModuleKind.Theme => Get(ThemeManager, "CurrentTheme"),
        ModuleKind.ApplicationSettings => Get(settings, "ApplicationSettings"),
        ModuleKind.LanguageResource => LanguageEntries[address.Name],
        ModuleKind.GlobalVariable => GlobalEntries.Cast<object>().SingleOrDefault(v => Equals(Get(v, "Name"), address.Name)),
        ModuleKind.CellStyle or ModuleKind.ListViewStyle => StyleList(address, false)?.Cast<object>().SingleOrDefault(v =>
            Equals(Get(v, address.Kind == ModuleKind.CellStyle ? "Key" : "Name"), address.Name)),
        _ => null
    };

    private IEnumerable<ModuleAddress> Addresses(ModuleKind kind)
    {
        if (kind is ModuleKind.Theme or ModuleKind.ApplicationSettings) { yield return new(kind, "Current"); yield break; }
        if (kind == ModuleKind.LanguageResource)
        { foreach (var key in LanguageEntries.Keys.Cast<string>()) yield return new(kind, key); yield break; }
        if (kind == ModuleKind.GlobalVariable)
        { foreach (var value in GlobalEntries.Cast<object>()) yield return new(kind, (string)Get(value, "Name")!); yield break; }
        if (kind == ModuleKind.ListViewStyle)
        { foreach (var value in ListStyles.Cast<object>()) yield return new(kind, (string)Get(value, "Name")!); yield break; }
        foreach (DictionaryEntry category in (IDictionary)Get(settings, "CustomCellTypeStyleTemplates")!)
            foreach (var value in ((IEnumerable)category.Value!).Cast<object>())
                yield return new(kind, (string)Get(value, "Key")!, (string)category.Key);
    }

    private IList? StyleList(ModuleAddress address, bool create)
    {
        if (address.Kind == ModuleKind.ListViewStyle) return ListStyles;
        var dictionary = (IDictionary)Get(settings, "CustomCellTypeStyleTemplates")!;
        if (dictionary[address.FolderPath!] is IList list) return list;
        if (!create) return null;
        ResolveStyleAttribute(address.FolderPath!); // Native category must identify an installed style attribute.
        var result = (IList)Activator.CreateInstance(dictionary.GetType().GetGenericArguments()[1])!;
        dictionary.Add(address.FolderPath!, result);
        return result;
    }

    private Type ResolveStyleAttribute(string name)
    {
        var matches = AppDomain.CurrentDomain.GetAssemblies().SelectMany(a => { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null).Cast<Type>(); } })
            .Where(t => t.FullName == name && InheritsStyleAttribute(t)).ToArray();
        if (matches.Length != 1) throw new BridgeException("UnknownStyleCategory", "Cell-style folder must be the full name of one loaded native style-support attribute.");
        return matches[0];
    }
    private static bool InheritsStyleAttribute(Type type) { for (var p = type; p is not null; p = p.BaseType) if (p.FullName == "GrapeCity.Forguncy.CellTypes.CellTypeStyleTemplateSupportAttribute") return true; return false; }
    private Type ResolveStyleCellType(string category)
    {
        var nativeHelper = assembly.GetType("qo", true)!;
        foreach (var candidate in AppDomain.CurrentDomain.GetAssemblies().SelectMany(a =>
                     { try { return a.GetTypes(); } catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null).Cast<Type>(); } }))
        {
            var isCell = false;
            for (var p = candidate; p is not null; p = p.BaseType)
                if (p.FullName == "GrapeCity.Forguncy.CellTypes.CellType") { isCell = true; break; }
            if (!isCell || candidate.IsAbstract || candidate.ContainsGenericParameters) continue;
            var attribute = Static(nativeHelper, "GetSingletonCellTypeStyleAttribute", candidate, false);
            if (attribute?.GetType().FullName == category) return candidate;
        }
        throw new BridgeException("StyleReferenceTypeUnavailable", "No loaded native cell type resolves to this exact style category; deletion is blocked.");
    }
    private IList ListStyles => (IList)Get(settings, "ListViewStyleTemplates")!;
    private IList GlobalEntries => (IList)Get(Get(settings, "ApplicationSettings")!, "GlobalProperties")!;
    private IDictionary LanguageEntries => (IDictionary)Get(Get(Get(settings, "ApplicationSettings")!, "MultipleLanguageSettings")!, "PublicResource")!;
    private object ThemeManager => componentThemeManager ?? Static(assembly.GetType("Forguncy.ThemesManager", true)!, "get_Instance")!;

    private void ValidateAddress(ModuleAddress address)
    {
        if (!Kinds.Contains(address.Kind)) throw new BridgeException("ModuleNotImplemented", "Native settings adapter does not own this kind.");
        if (string.IsNullOrWhiteSpace(address.Name) || address.Name.Length > 256 || address.Name.Any(char.IsControl))
            throw new BridgeException("InvalidSettingsKey", "Settings key is empty, too long or contains control characters.");
        if (address.Kind == ModuleKind.Theme && (address.Name != "Current" || address.FolderPath is not null))
            throw new BridgeException("InvalidThemeAddress", "Current project theme is addressed as Theme/Current.");
        if (address.Kind == ModuleKind.ApplicationSettings && (address.Name != "Current" || address.FolderPath is not null))
            throw new BridgeException("InvalidSettingsAddress", "Application settings root is addressed as ApplicationSettings/Current.");
        if (address.Kind == ModuleKind.CellStyle && string.IsNullOrWhiteSpace(address.FolderPath))
            throw new BridgeException("StyleCategoryRequired", "Cell-style addresses require their native category in FolderPath.");
        if (address.Kind is ModuleKind.LanguageResource or ModuleKind.ListViewStyle or ModuleKind.GlobalVariable && address.FolderPath is not null)
            throw new BridgeException("InvalidSettingsFolder", "This collection has no nested folders.");
    }

    private string SourceFile(ModuleAddress address)
    {
        if (IsLanguage(address.Kind)) return "ApplicationSettings.json";
        var method = address.Kind switch { ModuleKind.Theme => "GetThemeConfigPath", ModuleKind.CellStyle => "GetCellTypeStylePath", _ => "GetListViewStylePath" };
        // Derive the fixed relative filename using the pinned native path constructor, never wire paths.
        var manager = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(assembly.GetType("Forguncy.ThemesManager", true)!);
        var path = (string)Call(manager, method, "Theme")!;
        return path.Replace('\\', '/');
    }
    private string SourceBaseline(ModuleAddress address) => source is null ? "component-only" : ReadSource(SourceFile(address)) is { } data ? Convert.ToHexString(SHA256.HashData(data)) : "absent";
    private byte[]? ReadSource(string relative)
    {
        if (source is null) return null;
        if (relative.Contains(':') || relative.Split('/').Any(p => p is "" or "." or "..")) throw new BridgeException("InvalidNativeSettingsPath", "Native settings path is invalid.");
        if (Directory.Exists(source))
        {
            var path = Path.GetFullPath(Path.Combine(source, relative));
            for (var p = path; p is not null; p = Path.GetDirectoryName(p))
            { try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("SettingsPathIsLink", "Settings source cannot traverse links."); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
            return File.Exists(path) ? ReadLimited(File.OpenRead(path)) : null;
        }
        using var archive = ZipFile.OpenRead(source);
        var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/') == relative).ToArray();
        if (entries.Length == 0) return null;
        if (entries.Length != 1) throw new BridgeException("DuplicateSettingsSource", "Duplicate native settings archive entry.");
        return ReadLimited(entries[0].Open());
    }
    private static byte[] ReadLimited(Stream input) { using (input) { using var output = new MemoryStream(); var buffer = new byte[8192]; int count; while ((count = input.Read(buffer)) != 0) { if (output.Length + count > 8 * 1024 * 1024) throw new BridgeException("SettingsTooLarge", "Settings source is too large."); output.Write(buffer, 0, count); } return output.ToArray(); } }
    private ModuleReference LocatedReference(object located, ModuleAddress target)
    {
        var location = Get(located, "Location")!;
        var name = location.GetType().GetProperty("PageName", Flags)?.GetValue(location) as string;
        return new(new(ModuleKind.Page, name ?? location.ToString() ?? "native-location"), target, location.ToString() ?? "native-location");
    }
    private object Deserialize(string json, Type type)
    {
        var newtonsoft = Assembly.Load("Newtonsoft.Json");
        var settingsType = newtonsoft.GetType("Newtonsoft.Json.JsonSerializerSettings", true)!;
        var options = Activator.CreateInstance(settingsType)!;
        var missing = settingsType.GetProperty("MissingMemberHandling")!;
        missing.SetValue(options, Enum.Parse(missing.PropertyType, "Error"));
        using var parsed = JsonDocument.Parse(json);
        var resolver = Activator.CreateInstance(newtonsoft.GetType("Newtonsoft.Json.Serialization.DefaultContractResolver", true)!)!;
        ValidateContractProperties(parsed.RootElement, type, resolver);
        return Static(newtonsoft.GetType("Newtonsoft.Json.JsonConvert", true)!, "DeserializeObject", json, type, options)!;
    }
    private static void ValidateContractProperties(JsonElement value, Type type, object resolver)
    {
        var contract = Call(resolver, "ResolveContract", type)!;
        var contractName = contract.GetType().Name;
        if (value.ValueKind == JsonValueKind.Object && contractName == "JsonDictionaryContract")
        {
            var itemType = (Type)Get(contract, "DictionaryValueType")!;
            foreach (var item in value.EnumerateObject()) ValidateContractProperties(item.Value, itemType, resolver);
        }
        else if (value.ValueKind == JsonValueKind.Array && contractName == "JsonArrayContract")
        {
            var itemType = (Type)Get(contract, "CollectionItemType")!;
            foreach (var item in value.EnumerateArray()) ValidateContractProperties(item, itemType, resolver);
        }
        else if (value.ValueKind == JsonValueKind.Object && contractName == "JsonObjectContract")
        {
            var properties = ((IEnumerable)Get(contract, "Properties")!).Cast<object>().ToArray();
            foreach (var item in value.EnumerateObject())
            {
                var property = properties.SingleOrDefault(p => Equals(Get(p, "PropertyName"), item.Name));
                // MissingMemberHandling alone silently accepts [JsonIgnore] properties; reject them explicitly.
                if (property is null || Get(property, "Ignored") is true)
                    throw new BridgeException("UnsupportedSettingsProperty", "Native settings JSON contains an unknown, ignored or noncanonical property: " + item.Name);
                ValidateContractProperties(item.Value, (Type)Get(property, "PropertyType")!, resolver);
            }
        }
    }
    private string Serialize(object? value) => (string)Static(Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!, "GetJsonString", value, true)!;
    private static bool IsLanguage(ModuleKind kind) => kind is ModuleKind.LanguageResource or ModuleKind.GlobalVariable or ModuleKind.ApplicationSettings;
    private Type ApplicationSaveType => Assembly.Load("ServerDesignerCommon").GetType("Forguncy.SaveLoad.ApplicationSettingsSaveData", true)!;
    private object CaptureApplication() => Map(Get(settings, "ApplicationSettings")!, ApplicationSaveType);
    private string SerializeApplicationData(object data) => (string)Static(assembly.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!, "SaveToStr", data)!;
    private string SerializeApplication(object model) => SerializeApplicationData(Map(model, ApplicationSaveType));
    private object Map(object value, Type target) => Call(Static(assembly.GetType("hk", true)!, "get_Mapper")!, "Map", value, value.GetType(), target)!;
    private bool IsMutableApplicationProperty(PropertyInfo property)
    {
        var t = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return property.CanRead && !ProtectedApplicationProperties.Contains(property.Name) &&
            (t.IsEnum || t.IsPrimitive || t == typeof(string) || t == typeof(decimal)) &&
            Get(settings, "ApplicationSettings")!.GetType().GetProperty(property.Name, Flags)?.CanWrite is true;
    }
    private IReadOnlyList<(PropertyInfo Property, object? Value)> ApplicationChanges(object incoming)
    {
        var before = CaptureApplication();
        var model = Get(settings, "ApplicationSettings")!;
        var changes = new List<(PropertyInfo, object?)>();
        foreach (var property in ApplicationSaveType.GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            var next = property.GetValue(incoming);
            if (Equivalent(Serialize(property.GetValue(before)), Serialize(next))) continue;
            if (!IsMutableApplicationProperty(property))
                throw new BridgeException("ProtectedApplicationSetting", "This application-setting field needs its dedicated native editing path or a document reopen: " + property.Name);
            var destination = model.GetType().GetProperty(property.Name, Flags)!;
            if (next is not null && !destination.PropertyType.IsInstanceOfType(next))
                throw new BridgeException("ApplicationSettingTypeMismatch", "Native application-setting type does not match the saved scalar.");
            changes.Add((destination, next));
        }
        return changes;
    }
    private object PrepareApplication(JsonElement metadata)
    {
        // Do not deserialize a caller's whole application settings graph. Existing native command/AI payloads can contain
        // polymorphic metadata; preserve those objects and accept only explicitly supported scalar changes.
        var value = CaptureApplication();
        var before = JsonNode.Parse(SerializeApplicationData(value))!.AsObject();
        var incoming = JsonNode.Parse(metadata.GetRawText())!.AsObject();
        foreach (var name in before.Select(x => x.Key).Union(incoming.Select(x => x.Key), StringComparer.Ordinal))
        {
            if (JsonNode.DeepEquals(before[name], incoming[name])) continue;
            var property = ApplicationSaveType.GetProperty(name);
            if (property is null || !property.CanWrite || !IsMutableApplicationProperty(property) || !incoming.ContainsKey(name))
                throw new BridgeException("ProtectedApplicationSetting", "This application-setting field needs its dedicated native editing path or a document reopen: " + name);
            var next = incoming[name] is null ? null : Deserialize(incoming[name]!.ToJsonString(), property.PropertyType);
            if (next is null && property.PropertyType.IsValueType && Nullable.GetUnderlyingType(property.PropertyType) is null)
                throw new BridgeException("InvalidApplicationSetting", "A non-nullable setting cannot be null.");
            property.SetValue(value, next);
        }
        return value;
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool Equivalent(string a, string b) => JsonNode.DeepEquals(JsonNode.Parse(a), JsonNode.Parse(b));
    private static void RejectTypeMetadata(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in element.EnumerateObject())
            {
                if (!names.Add(p.Name)) throw new BridgeException("DuplicateSettingsProperty", "Duplicate settings property names are not accepted.");
                if (p.Name.StartsWith('$')) throw new BridgeException("NativeTypeMetadataForbidden", "Type and reference metadata are not accepted by style/settings CRUD.");
                RejectTypeMetadata(p.Value);
            }
        }
        if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) RejectTypeMetadata(item);
    }
    private static object? Get(object value, string name)
    {
        for (var type = value.GetType(); type is not null; type = type.BaseType)
            if (type.GetProperty(name, Flags | BindingFlags.DeclaredOnly) is { } property) return property.GetValue(value);
        throw new MissingMemberException(value.GetType().FullName, name);
    }
    private static void Set(object value, string name, object? incoming) => (value.GetType().GetProperty(name, Flags) ?? throw new MissingMemberException(value.GetType().FullName, name)).SetValue(value, incoming);
    private static object? Call(object value, string name, params object?[] args) => Invoke(value.GetType(), value, name, args);
    private static object? Static(Type type, string name, params object?[] args) => Invoke(type, null, name, args);
    private static object? Invoke(Type type, object? instance, string name, object?[] args)
    {
        var methods = type.GetMethods(Flags).Where(m => m.Name == name && !m.ContainsGenericParameters && m.IsStatic == (instance is null) && m.GetParameters().Length == args.Length &&
            m.GetParameters().Select((p, i) => args[i] is null ? !p.ParameterType.IsValueType : p.ParameterType.IsInstanceOfType(args[i])).All(x => x)).ToArray();
        if (methods.Length != 1) throw new MissingMethodException(type.FullName, name);
        try { return methods[0].Invoke(instance, args); } catch (TargetInvocationException error) when (error.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
