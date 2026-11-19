using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>
/// Native connection definition CRUD. Never opens a connection, tests credentials,
/// imports a table, or applies database DDL. Persistence belongs to document SaveAsync.
/// Metadata uses native save-data JSON. C/U may instead supply PlainConnectionString;
/// Read returns the actual native save representation without custom crypto claims.
/// </summary>
public sealed class NativeConnections
{
    private const string SourceFile = "DataConnectionSet.json";
    private const string ModuleTypeName = "DataContectionSet"; // Native enum spelling, retained intentionally.
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly Assembly assembly;
    private readonly object settings;
    private readonly object? document;
    private readonly string source;
    private readonly Action? componentDirty;
    private readonly Func<ModuleAddress, ReferenceResult>? componentReferences;
    private readonly object loader;
    private readonly object mapper;
    private readonly Type connectionType;
    private readonly Type modelListType;
    private readonly Type saveListType;
    private readonly Type generatorType;
    private IList Connections => (IList)Get(settings, "DataConnectionSet")!;

    public NativeConnections(AdapterHostContext context) : this(context.DesignerAssembly,
        Get(context.NativeDocument, "ForguncySettings")!, context.CaptureSnapshot().Target.ProjectPath, null, null)
    { document = context.NativeDocument; }

    // The optional reference probe is only a component-fixture seam. Production
    // always enumerates the native sf + IReferenceDataConnection graph below.
    public NativeConnections(Assembly assembly, object nativeSettings, string sourceProject,
        Action? dirty = null, Func<ModuleAddress, ReferenceResult>? referenceProbe = null)
    {
        this.assembly = assembly; settings = nativeSettings; source = sourceProject;
        componentDirty = dirty; componentReferences = referenceProbe;
        if (assembly.GetName().Version?.ToString() != V12AdapterFactory.Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != V12AdapterFactory.DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild", "Connection definitions require the verified V12 native components.");
        if (nativeSettings.GetType() != assembly.GetType("lt", true)) throw new ArgumentException("Expected the native V12 settings model.");
        connectionType = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.DataConnectionInfo", true)!;
        modelListType = typeof(List<>).MakeGenericType(connectionType);
        loader = RuntimeHelpers.GetUninitializedObject(assembly.GetType("cg", true)!);
        saveListType = loader.GetType().GetMethod("ConvertToT", Flags, null, [typeof(MemoryStream)], null)!.ReturnType;
        if (saveListType.GetGenericArguments().Single().FullName != "Forguncy.SaveLoad.DataConnectionSaveData" || Connections.GetType() != modelListType)
            throw new BridgeException("NativeConnectionContractChanged", "The native connection loader or list model changed.");
        mapper = Static(assembly.GetType("hk", true)!, "get_Mapper")!;
        generatorType = assembly.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
        var moduleType = ModuleCreate().GetParameters()[0].ParameterType;
        if (Convert.ToInt32(Enum.Parse(moduleType, ModuleTypeName)) != 25)
            throw new BridgeException("NativeConnectionContractChanged", "The native connection save-module identifier changed.");
        if (assembly.GetType("sf", true)!.GetMethod("EnumReferenceDataConnection", Flags, null, Type.EmptyTypes, null) is null)
            throw new BridgeException("NativeConnectionContractChanged", "The native connection reference enumerator changed.");
    }

    public bool Supports(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.DataConnection;
    public IReadOnlyList<ModuleCapability> Capabilities() => Enum.GetValues<ModuleOperation>().Select(operation =>
        new ModuleCapability(ModuleKind.DataConnection, operation, true, "implemented-native-model-and-serializer; live-designer-save-and-reference-context-pending",
            "Definition maintenance only; never connects to the database or changes external schema.")).ToArray();

    public string GetModuleName(ModuleAddress address)
    {
        ValidateAddress(address);
        var (module, _) = NativeModule();
        return (string)Get(module, "ModuleName")!;
    }

    public ModulePage List(ModuleQuery query)
    {
        if (query.Kind != ModuleKind.DataConnection || query.FolderPath is not null) throw new BridgeException("InvalidConnectionQuery", "Connection definitions have no project folders.");
        if (query.Limit is < 1 or > 1000) throw new BridgeException("InvalidPageSize", "Connection page size must be between 1 and 1000.");
        var offset = query.Cursor is null ? 0 : int.TryParse(query.Cursor, out var parsed) && parsed >= 0 ? parsed : throw new BridgeException("InvalidCursor", "Invalid connection cursor.");
        ValidateCollection();
        var entries = Connections.Cast<object>().OrderBy(Id, StringComparer.Ordinal).ToArray();
        var items = entries.Skip(offset).Take(query.Limit).Select(value =>
        { var address = new ModuleAddress(ModuleKind.DataConnection, Id(value)); return new ModuleSummary(address, Revision(value), Get(value, "Remark") as string); }).ToArray();
        return new(items, offset + items.Length < entries.Length ? (offset + items.Length).ToString() : null);
    }

    public ModuleDocument Read(ModuleAddress address)
    {
        ValidateAddress(address); ValidateCollection();
        var current = Find(address.Name) ?? throw new BridgeException("ModuleNotFound", "The connection definition does not exist.");
        if (Id(current) != address.Name) throw new BridgeException("ConnectionIdCaseMismatch", "Read the canonical connection ID returned by the native list.");
        using var json = JsonDocument.Parse(Capture([current]));
        return new(address, json.RootElement[0].Clone(), [], Revision(current));
    }

    public ReferenceResult References(ModuleAddress address)
    {
        ValidateAddress(address);
        if (componentReferences is not null) return componentReferences(address);
        try
        {
            return ReferencesFromNativeObjects((IEnumerable)Static(assembly.GetType("sf", true)!, "EnumReferenceDataConnection")!, address);
        }
        catch { return new([], false, "The complete native connection reference graph is unavailable in this document context."); }
    }

    internal ReferenceResult ReferencesFromNativeObjects(IEnumerable nativeObjects, ModuleAddress address)
    {
        var references = new List<ModuleReference>();
        try
        {
            foreach (var located in nativeObjects)
            {
                var value = Get(located!, "Object")!;
                var contract = value.GetType().GetInterfaces().Single(t => t.FullName == "GrapeCity.Forguncy.Plugin.IReferenceDataConnection");
                var ids = (IEnumerable<string>)contract.GetMethod("EnumConnectionId")!.Invoke(value, null)!;
                if (!ids.Contains(address.Name, StringComparer.Ordinal)) continue;
                var location = Get(located!, "Location")!;
                var page = location.GetType().GetProperty("PageName", Flags)?.GetValue(location) as string;
                references.Add(new(new(ModuleKind.Page, page ?? location.ToString() ?? "native-location"), address,
                    location.ToString() ?? "native-connection-reference"));
            }
            return new(references, true);
        }
        catch { return new(references, false, "The native connection reference graph could not be fully enumerated."); }
    }

    public ModuleValidationResult Validate(ModuleMutationRequest request)
    {
        try
        {
            ValidateAddress(request.Address); ValidateCollection();
            if (request.Scope != EffectScope.ProjectDefinition) throw new BridgeException("EffectScopeDenied", "Connection definition maintenance uses project-definition scope.");
            if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)) throw new BridgeException("InvalidMutation", "Connection mutation must be Create, Update or Delete.");
            if (request.Document is not null && request.Document.Address != request.Address) throw new BridgeException("ModuleAddressMismatch", "Connection document address differs from its mutation target.");
            if (request.Document?.Companions.Count > 0) throw new BridgeException("ConnectionCompanionsNotAllowed", "A connection definition does not accept companion files.");
            var current = Find(request.Address.Name);
            if (request.Operation == ModuleOperation.Create && current is not null) throw new BridgeException("ModuleAlreadyExists", "Connection identifiers are unique ignoring case.");
            if (request.Operation != ModuleOperation.Create && current is null) throw new BridgeException("ModuleNotFound", "The connection definition does not exist.");
            if (current is not null && Id(current) != request.Address.Name) throw new BridgeException("ConnectionIdImmutable", "Use the exact native connection ID; identifier renaming is not an update.");
            if (request.ExpectedModuleRevision is not null && current is not null && request.ExpectedModuleRevision != Revision(current))
                throw new BridgeException("ModuleRevisionChanged", "Connection definition or its saved source changed.");
            RequireNativeTracking(request.Address);
            if (request.Operation == ModuleOperation.Delete)
            {
                var references = References(request.Address);
                if (!references.Complete) return new(false, [new("ReferencesUnverified", references.Reason ?? "Connection references are unverified.")], references);
                if (references.References.Count != 0) return new(false, [new("ConnectionReferenced", "A referenced connection identifier cannot be deleted.")], references);
                return new(true, [], references);
            }
            if (request.Document is null) throw new BridgeException("ModuleDocumentRequired", "Connection create/update needs a definition document.");
            _ = Prepare(request.Address, request.Document.Metadata);
            return new(true, []);
        }
        catch (BridgeException error) { return new(false, [new(error.Code, error.Message)]); }
        catch { return new(false, [new("NativeConnectionConversionFailed", "The native connection definition could not be converted; connection contents are omitted from diagnostics.")]); }
    }

    public IReadOnlyList<string> Apply(ModuleMutationRequest request)
    {
        var validation = Validate(request);
        if (!validation.Valid) throw new BridgeException("NativeConnectionValidation", string.Join("; ", validation.Issues.Select(i => i.Code + ": " + i.Message)));
        var current = Find(request.Address.Name);
        var incoming = request.Operation == ModuleOperation.Delete ? null : Prepare(request.Address, request.Document!.Metadata);
        // Retain the settings list and existing connection object identity. Native
        // table bindings refer to immutable IDs; renaming them is not a patch.
        Dirty();
        if (request.Operation == ModuleOperation.Create) Connections.Add(incoming!);
        else if (request.Operation == ModuleOperation.Delete) Connections.Remove(current!);
        else
        {
            var connectionChanged = !string.Equals(Get(current!, "ConnectionString") as string, Get(incoming!, "ConnectionString") as string, StringComparison.Ordinal) ||
                !string.Equals(Get(current!, "DataBaseType") as string, Get(incoming!, "DataBaseType") as string, StringComparison.Ordinal);
            foreach (var property in new[] { "ConnectionString", "DataBaseType", "Remark" }) Set(current!, property, Get(incoming!, property));
            if (connectionChanged) Set(current!, "EConnectionString", Get(incoming!, "EConnectionString"));
        }
        if (incoming is not null && Find(request.Address.Name) is { } readback && !EquivalentModels(incoming, readback))
            throw new BridgeException("NativeConnectionReadbackFailed", "The native connection definition did not retain its requested values.");
        if (request.Operation == ModuleOperation.Delete && Find(request.Address.Name) is not null)
            throw new BridgeException("NativeConnectionDeleteReadbackFailed", "The connection remains registered after native model deletion.");
        return [SourceFile];
    }

    public bool VerifySource(ModuleMutationRequest request)
    {
        try
        {
            var bytes = NativeSaveVerification.ReadSource(source, SourceFile);
            if (bytes is null) return false;
            var saved = Parse(bytes).Cast<object>().Where(value => Id(value) == request.Address.Name).ToArray();
            if (request.Operation == ModuleOperation.Delete) return saved.Length == 0 && Find(request.Address.Name) is null;
            return saved.Length == 1 && Find(request.Address.Name) is { } memory && EquivalentModels(saved[0], memory);
        }
        catch { return false; }
    }

    // Used by isolated component tests to feed the real native SaveFile. Production
    // never writes this byte array to the source; document SaveAsync owns that work.
    internal byte[] CaptureSaveData() => Capture(Connections.Cast<object>());

    internal NativeModuleFileGroup CaptureOwnerGroup()
    {
        ValidateCollection();
        var identity = Connections;
        string SemanticHash(IEnumerable<object> entries) => Wire.Hash(JsonSerializer.Serialize(entries.OrderBy(Id, StringComparer.Ordinal)
            .Select(value => new[] { Id(value), Get(value, "ConnectionString") as string, Get(value, "DataBaseType") as string, Get(value, "Remark") as string })));
        var expected = SemanticHash(identity.Cast<object>());
        bool Current() => ReferenceEquals(Connections, identity) && SemanticHash(Connections.Cast<object>()) == expected;
        bool Matches(string file, byte[] bytes)
        {
            if (file != SourceFile) return false;
            try { return SemanticHash(Parse(bytes).Cast<object>()) == expected; }
            catch { return false; }
        }
        var (module, _) = NativeModule();
        return new(new(ModuleKind.DataConnection, "Current"), (string)Get(module, "ModuleName")!, [SourceFile], [], [],
            new Dictionary<string,string>(), Current, Matches, identity, () => ReferenceEquals(Connections, identity));
    }

    private object Prepare(ModuleAddress address, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidConnectionDefinition", "Connection definition metadata must be an object.");
        var allowed = new HashSet<string>(["ID", "ConnectionString", "PlainConnectionString", "Remark", "DataBaseType"], StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadata.EnumerateObject())
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name) || property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new BridgeException("InvalidConnectionDefinition", "Connection definition contains an unknown, duplicate, or non-text property.");
        if (!metadata.TryGetProperty("ID", out var id) || id.GetString() != address.Name)
            throw new BridgeException("ConnectionIdImmutable", "Connection ID must match its address and cannot be renamed by update.");
        var existing = Find(address.Name);
        // A metadata-only update must not erase a connection string or its native
        // encrypted cache. Start from the current native save object and overlay
        // only fields explicitly supplied by the caller.
        using var existingJson = existing is null ? null : JsonDocument.Parse(Capture([existing]));
        var merged = existingJson is null ? new JsonObject() : JsonNode.Parse(existingJson.RootElement[0].GetRawText())!.AsObject();
        foreach (var property in metadata.EnumerateObject())
            if (property.Name != "PlainConnectionString") merged[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        if (merged["DataBaseType"] is not JsonValue dbValue || !dbValue.TryGetValue<string>(out var dbType) || string.IsNullOrWhiteSpace(dbType))
            throw new BridgeException("ConnectionDatabaseTypeRequired", "The native database type is required.");
        object prepared;
        if (metadata.TryGetProperty("PlainConnectionString", out var plain))
        {
            if (metadata.TryGetProperty("ConnectionString", out _)) throw new BridgeException("AmbiguousConnectionString", "Supply either native saved ConnectionString or PlainConnectionString.");
            prepared = existing is null ? Activator.CreateInstance(connectionType)! : Call(existing, "Clone")!;
            Set(prepared, "ID", address.Name); Set(prepared, "DataBaseType", dbType); Set(prepared, "ConnectionString", plain.GetString()); Set(prepared, "EConnectionString", null);
            if (metadata.TryGetProperty("Remark", out var remark)) Set(prepared, "Remark", remark.GetString());
        }
        else prepared = Parse(Encoding.UTF8.GetBytes("[" + merged.ToJsonString() + "]")).Cast<object>().Single();
        var roundTrip = Parse(Capture([prepared])).Cast<object>().Single();
        if (!EquivalentModels(prepared, roundTrip)) throw new BridgeException("NativeConnectionRoundtripFailed", "The native connection save profile cannot preserve this definition.");
        return roundTrip;
    }

    private byte[] Capture(IEnumerable<object> values)
    {
        var list = (IList)Activator.CreateInstance(modelListType)!; foreach (var value in values) list.Add(value);
        var data = Map(list, saveListType);
        return Encoding.UTF8.GetBytes((string)Static(generatorType, "SaveToStr", data)!);
    }
    private IList Parse(byte[] bytes)
    {
        using var input = new MemoryStream(bytes, false);
        var data = Call(loader, "ConvertToT", input)!;
        return (IList)Map(data, modelListType);
    }
    private object Map(object value, Type target) => Call(mapper, "Map", value, value.GetType(), target)!;
    private object? Find(string id) => Connections.Cast<object>().SingleOrDefault(value => Id(value).Equals(id, StringComparison.OrdinalIgnoreCase));
    private static string Id(object value) => (string?)Get(value, "ID") ?? "";
    private void ValidateCollection()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Connections)
            if (entry is null || entry.GetType() != connectionType || string.IsNullOrWhiteSpace(Id(entry)) || !ids.Add(Id(entry)))
                throw new BridgeException("InvalidNativeConnectionList", "Native connection IDs are empty or ambiguous.");
    }
    private string Revision(object value)
    {
        var bytes = NativeSaveVerification.ReadSource(source, SourceFile);
        var baseline = bytes is null ? "absent" : Convert.ToHexString(SHA256.HashData(bytes));
        return Wire.Hash(JsonSerializer.Serialize(new[] { Id(value), Get(value, "ConnectionString") as string, Get(value, "EConnectionString") as string,
            Get(value, "DataBaseType") as string, Get(value, "Remark") as string }) + "\n" + baseline);
    }
    private static bool EquivalentModels(object left, object right) => new[] { "ID", "ConnectionString", "DataBaseType", "Remark" }
        .All(name => string.Equals(Get(left, name) as string, Get(right, name) as string, StringComparison.Ordinal));
    private (object Module, Type ModuleType) NativeModule()
    {
        var create = ModuleCreate();
        var moduleType = create.GetParameters()[0].ParameterType;
        var module = create.Invoke(null, [Enum.Parse(moduleType, ModuleTypeName), Enum.ToObject(create.GetParameters()[1].ParameterType, 31)])!;
        return (module, moduleType);
    }
    private MethodInfo ModuleCreate() => assembly.GetType("Forguncy.Common.SimpleModule", true)!.GetMethods(Flags)
        .Single(m => m.Name == "Create" && m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.IsEnum &&
            m.GetParameters()[0].ParameterType.Name == "ModuleType");
    internal void RegisterDirty()
    {
        var cache = Static(assembly.GetType("pb", true)!, "get_Instance")!;
        var method = cache.GetType().GetMethod("DirtyModules", Flags)!;
        var tuple = method.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var (module, moduleType) = NativeModule();
        var changes = Array.CreateInstance(tuple, 1);
        changes.SetValue(Activator.CreateInstance(tuple, module, Enum.Parse(tuple.GetGenericArguments()[1], "Edit")), 0);
        method.Invoke(cache, [null, changes]); Call(cache, "DirtyAllPages");
        if (Call(cache, "IsSaveDataDirty", Enum.Parse(moduleType, ModuleTypeName)) is not true)
            throw new BridgeException("NativeConnectionDirtyFailed", "The native connection save-data cache was not marked dirty.");
    }
    private void Dirty()
    {
        if (componentDirty is not null) { componentDirty(); return; }
        if (document is not null) Set(document, "ContainsUnsavedChanges", true);
        RegisterDirty();
    }
    private void RequireNativeTracking(ModuleAddress address)
    {
        if (document is null) return;
        var manager = Static(assembly.GetType("Forguncy.Common.CollaborationManager", true)!, "get_Instance")!;
        if (Get(manager, "InCollaborationMode") is not true) return;
        var module = GetModuleName(address);
        if (Call(manager, "IsModuleCheckoutByCurrentUser", module) is not true || Get(manager, "SaveLoadTrackedFiles") is not IDictionary tracking ||
            tracking[module] is not IEnumerable files || !files.Cast<string>().Any(p => NativeSaveVerification.Relative(source, p) == SourceFile))
            throw new BridgeException("NativeSaveTrackingMissing", "The connection settings module requires the current user's native checkout and source-save tracking.");
    }
    private static void ValidateAddress(ModuleAddress address)
    {
        if (address.Kind != ModuleKind.DataConnection || address.FolderPath is not null || string.IsNullOrWhiteSpace(address.Name) ||
            address.Name.Length > 256 || address.Name.Any(char.IsControl))
            throw new BridgeException("InvalidConnectionAddress", "A connection address uses one non-empty immutable native ID and no folder.");
    }
    private static object? Get(object value, string name)
    {
        // LocatedObject<T> hides LocatedObject.Object with a more specific return
        // type. Resolve the nearest declared property instead of an ambiguous
        // GetProperty lookup across the full inheritance chain.
        for (var type = value.GetType(); type is not null; type = type.BaseType)
            if (type.GetProperty(name, Flags | BindingFlags.DeclaredOnly) is { } property) return property.GetValue(value);
        throw new MissingMemberException(value.GetType().FullName, name);
    }
    private static void Set(object value, string name, object? incoming) => NativeReflection.Set(value, name, incoming);
    private static object? Call(object value, string method, params object?[] arguments) => NativeReflection.Call(value, method, arguments);
    private static object? Static(Type type, string method, params object?[] arguments) => NativeReflection.Static(type, method, arguments);
}
