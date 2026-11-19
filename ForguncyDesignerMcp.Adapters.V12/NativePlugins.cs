using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>
/// Reads the native current-project plugin dependency registry. This is separate
/// from the machine/user plugin installation registry and never initializes it.
/// Native plugin C/U/D are installation/runtime lifecycles, not metadata edits.
/// </summary>
public sealed class NativePlugins
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly Assembly assembly;
    private readonly AdapterHostContext? context;
    private readonly string source;
    private readonly object? componentItems;
    private readonly Type pluginListType;
    private readonly Type saveInfoType;
    private readonly Type saveInfoListType;
    public string ManifestName { get; }
    public string ProjectPluginFolderName { get; }

    public NativePlugins(AdapterHostContext context) : this(context.DesignerAssembly, context.CaptureSnapshot().Target.ProjectPath)
    { this.context = context; }

    // Real native metadata-only fixture input. It does not register these items in
    // ma, load assemblies, run Java/.NET startup hooks, or copy plugin packages.
    public NativePlugins(Assembly assembly, object nativePluginItems, string sourceProject) : this(assembly, sourceProject)
    {
        if (nativePluginItems.GetType() != pluginListType) throw new ArgumentException("Expected a real native List<CommonUtilities.PluginItem>.");
        componentItems = nativePluginItems;
    }

    private NativePlugins(Assembly assembly, string sourceProject)
    {
        this.assembly = assembly; source = sourceProject;
        if (assembly.GetName().Version?.ToString() != V12AdapterFactory.Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != V12AdapterFactory.DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild", "Plugin dependency reads require the verified V12 native components.");
        var utility = assembly.GetType("gz", true)!;
        pluginListType = utility.GetMethod("GetUsedPluginItems", Flags)!.ReturnType;
        saveInfoListType = utility.GetMethod("GetUsedPlugInItemsSaveInfo", Flags)!.ReturnType;
        saveInfoType = saveInfoListType.GetGenericArguments().Single();
        if (pluginListType.GetGenericArguments().Single().FullName != "CommonUtilities.PluginItem" || saveInfoType.FullName != "Forguncy.SaveLoad.PlugInItemSaveInfo")
            throw new BridgeException("NativePluginContractChanged", "Native plugin metadata registry types changed.");
        ManifestName = (string)Static(utility, "get_ata")!;
        ProjectPluginFolderName = (string)Static(utility, "get_atb")!;
        RequireLeaf(ManifestName); RequireLeaf(ProjectPluginFolderName);
    }

    public bool Supports(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.Plugin && operation is ModuleOperation.List or ModuleOperation.Read;
    public IReadOnlyList<ModuleCapability> Capabilities() => Enum.GetValues<ModuleOperation>().Select(operation =>
        new ModuleCapability(ModuleKind.Plugin, operation, operation is ModuleOperation.List or ModuleOperation.Read,
            operation is ModuleOperation.List or ModuleOperation.Read ? "native-project-dependency-metadata-implemented; live-designer-registry-pending" : "not-implemented",
            operation is ModuleOperation.List or ModuleOperation.Read ? "Current native project dependencies; saved declarations are separately available from ReadSavedManifest()." :
                "Native plugin creation/replacement/removal alters shared installation and loaded runtime registries; metadata or file deletion is not an implementation of that lifecycle.",
            operation is ModuleOperation.List or ModuleOperation.Read ? EffectScope.ProjectDefinition : EffectScope.RuntimeDeployment)).ToArray();

    public ModulePage List(ModuleQuery query)
    {
        if (query.Kind != ModuleKind.Plugin || query.FolderPath is not null) throw new BridgeException("InvalidPluginQuery", "Plugin dependencies have no addressable installation folders.");
        if (query.Limit is < 1 or > 1000) throw new BridgeException("InvalidPageSize", "Plugin page size must be between 1 and 1000.");
        var offset = query.Cursor is null ? 0 : int.TryParse(query.Cursor, out var parsed) && parsed >= 0 ? parsed : throw new BridgeException("InvalidCursor", "Invalid plugin dependency cursor.");
        var records = Capture().OrderBy(Identity, StringComparer.Ordinal).ToArray();
        var selected = records.Skip(offset).Take(query.Limit).Select(record =>
        {
            var address = new ModuleAddress(ModuleKind.Plugin, Identity(record));
            return new ModuleSummary(address, Revision(record), Text(record, "name") + " " + Text(record, "version"));
        }).ToArray();
        return new(selected, offset + selected.Length < records.Length ? (offset + selected.Length).ToString() : null);
    }

    public ModuleDocument Read(ModuleAddress address)
    {
        ValidateAddress(address);
        var matches = Capture().Where(record => Identity(record) == address.Name).ToArray();
        if (matches.Length != 1) throw new BridgeException("PluginDependencyNotUnique", "The current native project dependency does not resolve to exactly one plugin identity.");
        return new(address, matches[0].Clone(), [], Revision(matches[0]));
    }

    // This explicitly reads a saved declaration snapshot; it is never substituted
    // for a failed current-memory dependency read and never executes plugin code.
    public JsonElement ReadSavedManifest() => ReadSavedManifest(source);

    internal JsonElement ReadSavedManifest(string project)
    {
        var bytes = NativeSaveVerification.ReadSource(project, ManifestName);
        if (bytes is null) return JsonSerializer.SerializeToElement(Array.Empty<object>());
        using var parsed = JsonDocument.Parse(bytes);
        ValidateSaveRecords(parsed.RootElement);
        var jsonAssembly = Assembly.Load("Newtonsoft.Json");
        var native = Static(jsonAssembly.GetType("Newtonsoft.Json.JsonConvert", true)!, "DeserializeObject", parsed.RootElement.GetRawText(), saveInfoListType)!;
        return SerializeNative(native);
    }

    public ReferenceResult References(ModuleAddress address)
    {
        ValidateAddress(address);
        return new([], false, "The native dependency registry identifies required plugins but does not itself provide a complete normalized module-reference graph. Native unload/removal is disabled.");
    }

    public ModuleValidationResult Validate(ModuleMutationRequest request)
    {
        try
        {
            ValidateAddress(request.Address);
            if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
                throw new BridgeException("InvalidMutation", "Plugin mutation must be Create, Update or Delete.");
            if (request.Scope != EffectScope.RuntimeDeployment)
                return new(false, [new("PluginRuntimeScopeRequired", "Native plugin installation, replacement and removal require explicit runtime-deployment scope.")]);
            return new(false, [new("PluginLifecycleNotImplemented", "The shared-installation and loaded-runtime plugin lifecycle is not implemented by this metadata adapter; no plugin operation ran.")]);
        }
        catch (BridgeException error) { return new(false, [new(error.Code, error.Message)]); }
    }

    public IReadOnlyList<string> Apply(ModuleMutationRequest request)
    {
        var validation = Validate(request);
        throw new BridgeException("PluginLifecycleNotImplemented", string.Join("; ", validation.Issues.Select(issue => issue.Code + ": " + issue.Message)));
    }
    public bool VerifySource(ModuleMutationRequest request) => false;

    /// <summary>Audited native lifecycle inventory, deliberately not an executable API.</summary>
    public IReadOnlyList<NativePluginLifecycle> Lifecycle() =>
    [
        new(ModuleOperation.Create, "ma.AddNewPluginItem(string, PluginItem)", EffectScope.RuntimeDeployment,
            "Copies package files to the shared plugin store, registers loaded plugin types and raises PluginOperation.Add."),
        new(ModuleOperation.Update, "ma.ReplacePluginItem(string, PluginItem, PluginItem)", EffectScope.RuntimeDeployment,
            "Replaces the shared installation, calls native add/delete lifecycle and refreshes plugin resources; may require restart."),
        new(ModuleOperation.Delete, "ma.DeleteItem(PluginItem, bool)", EffectScope.RuntimeDeployment,
            "Removes the native installed item, records DeletedPlugins and raises PluginOperation.Delete even when file removal is false."),
        new(ModuleOperation.Update, "gz.GetCheckInPluginExceptionalInfos / gz.SavePluginZipToWorkingFolder", EffectScope.RuntimeDeployment,
            "Compares native saved/working plugin versions and records complete plugin ZIP changes during native collaboration check-in.")
    ];

    internal JsonElement[] Capture()
    {
        object items;
        if (componentItems is not null) items = componentItems;
        else
        {
            if (context is null) throw new BridgeException("NativePluginContextMissing", "A current native document is required.");
            var singleton = Static(assembly.GetType("s", true)!, "get_Instance")!;
            if (!ReferenceEquals(NativeReflection.Get(singleton, "ew"), context.NativeDocument))
                throw new BridgeException("NativePluginDocumentChanged", "The native plugin dependency graph belongs to a different document.");
            // Reading ma.Instance would initialize a global AssemblyResolve hook.
            // A loaded designer already owns this object. Never create one here.
            var pluginManager = assembly.GetType("ma", true)!;
            if (pluginManager.GetField("bjt", Flags)?.GetValue(null) is null)
                throw new BridgeException("NativePluginRegistryNotInitialized", "The designer has not initialized its native plugin registry; metadata reads will not load plugins.");
            items = Static(assembly.GetType("gz", true)!, "GetUsedPluginItems")!;
        }
        if (items.GetType() != pluginListType) throw new BridgeException("NativePluginContractChanged", "Unexpected native project plugin list model.");
        var saved = Static(assembly.GetType("gz", true)!, "GetUsedPlugInItemsSaveInfo", items)!;
        var result = SerializeNative(saved);
        ValidateSaveRecords(result);
        var records = result.EnumerateArray().Select(record => record.Clone()).ToArray();
        if (records.Select(Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Length)
            throw new BridgeException("NativePluginIdentityAmbiguous", "Native project plugin dependencies contain duplicate identities or conflicting versions.");
        return records;
    }

    internal string? PackageAssemblyName(JsonElement config)
    {
        var files = new List<string>();
        if (config.TryGetProperty("assembly", out var declared) && declared.ValueKind != JsonValueKind.Null)
        {
            if (declared.ValueKind != JsonValueKind.Array || declared.EnumerateArray().Any(p => p.ValueKind != JsonValueKind.String))
                throw new BridgeException("InvalidPluginManifest", "PluginConfig.json assembly must be an array of assembly filenames.");
            files.AddRange(declared.EnumerateArray().Select(p => p.GetString()!));
        }
        // Use the SDK's own joining and default conventions for the persisted assemblyName.
        var row = Activator.CreateInstance(pluginListType.GetGenericArguments()[0])!;
        NativeReflection.Set(row, "assembly", files);
        var list = (IList)Activator.CreateInstance(pluginListType)!;
        list.Add(row);
        var projected = (IList)Static(assembly.GetType("gz", true)!, "GetUsedPlugInItemsSaveInfo", list)!;
        return NativeReflection.Get(projected[0]!, "assemblyName") as string;
    }
    private JsonElement SerializeNative(object value)
    {
        var serializer = Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!.GetMethods(Flags)
            .Single(method => method.Name == "GetJsonString" && !method.ContainsGenericParameters &&
                method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(object), typeof(bool) }));
        var json = (string)serializer.Invoke(null, [value, true])!;
        using var parsed = JsonDocument.Parse(json); return parsed.RootElement.Clone();
    }
    private void ValidateSaveRecords(JsonElement records)
    {
        if (records.ValueKind != JsonValueKind.Array) throw new BridgeException("InvalidPluginManifest", "The native plugin declaration root must be an array.");
        var expected = saveInfoType.GetProperties(BindingFlags.Public | BindingFlags.Instance).ToDictionary(property => property.Name, property => property.PropertyType, StringComparer.Ordinal);
        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidPluginManifest", "Plugin declarations must be native metadata objects.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in record.EnumerateObject())
                if (!seen.Add(property.Name) || !expected.TryGetValue(property.Name, out var type) ||
                    (type == typeof(bool) ? property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) : property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                    throw new BridgeException("InvalidPluginManifest", "Plugin declaration contains unknown, duplicate or incompatible native metadata fields.");
        }
    }
    private string Revision(JsonElement record)
    {
        var bytes = NativeSaveVerification.ReadSource(source, ManifestName);
        return Wire.Hash(record.GetRawText() + "\n" + (bytes is null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes))));
    }
    private static string Identity(JsonElement record) => Text(record, "guid") is { Length: > 0 } guid ? guid : "legacy-" + Wire.Hash(record.GetRawText());
    private static string? Text(JsonElement value, string property) => value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static void ValidateAddress(ModuleAddress address)
    {
        if (address.Kind != ModuleKind.Plugin || address.FolderPath is not null || string.IsNullOrWhiteSpace(address.Name) || address.Name.Any(char.IsControl))
            throw new BridgeException("InvalidPluginAddress", "Plugin dependency addresses use a native GUID or adapter-issued legacy identity, without installation paths.");
    }
    private static void RequireLeaf(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path is "." or ".." || path.IndexOfAny(['/', '\\', ':', '\0']) >= 0)
            throw new BridgeException("NativePluginContractChanged", "Native plugin declaration paths are not fixed leaf names.");
    }
    private static object? Static(Type type, string method, params object?[] args) => NativeReflection.Static(type, method, args);
}

public sealed record NativePluginLifecycle(ModuleOperation Operation, string NativeMethod, EffectScope Scope, string Effects);
