using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>
/// Binds only the installed, attributed C# API registry. This does not create an
/// assistant view, a WebView, an HTTP listener, or a JavaScript/VFS runtime.
/// The owning project adapter must run calls on its UI scheduler and apply its
/// target, baseline, identity and side-effect checks before entering this class.
/// </summary>
public sealed partial class NativeApiRegistry
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private readonly Dictionary<string, NativeApiEntry> entries = new(StringComparer.Ordinal);
    private readonly object dispatcher;
    private readonly MethodInfo nativeInvoke;
    private readonly MethodInfo parseObject;
    private readonly MethodInfo serialize;
    private readonly object serializerSettings;
    private readonly IReadOnlyList<PlatformApiDescriptor> catalog;
    private readonly Assembly designer;
    private readonly Func<object>? boundSettings;
    private readonly Action<string,string,JsonElement>? requireUnreferencedSignature;
    private readonly Action<string,JsonElement>? libraryLifecycleValidator;

    public int NativeDescriptorCount { get; }
    public IReadOnlyList<NativeApiExclusion> ExcludedApis { get; }
    public IReadOnlyList<NativeApiEntry> Entries => entries.Values.OrderBy(e => e.Namespace, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal).ToArray();

    public NativeApiRegistry(Assembly designer):this(designer,null) { }

    public NativeApiRegistry(Assembly designer,Func<object>? boundSettings,Action<string,string,JsonElement>? requireUnreferencedSignature=null,
        Action<string,JsonElement>? libraryLifecycleValidator=null)
    {
        ArgumentNullException.ThrowIfNull(designer);
        this.designer = designer;
        this.boundSettings = boundSettings;
        this.requireUnreferencedSignature=requireUnreferencedSignature;
        this.libraryLifecycleValidator=libraryLifecycleValidator;
        if (designer.GetName().Version?.ToString() != V12AdapterFactory.Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(designer.Location))) != V12AdapterFactory.DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild", "The native API registry requires the verified 12.0.102.0 designer build.");
        var discovery = designer.GetType("aam", true)!;
        var descriptors = Invoke(discovery.GetMethod("GetCurrentAssemblyDescriptors", Flags)!, null, [])!;
        var all = ((IEnumerable)descriptors).Cast<object>().ToArray();
        NativeDescriptorCount = all.Length;
        if (NativeDescriptorCount != 306) throw new BridgeException("NativeApiContractChanged", "The verified V12 registry must contain exactly 306 registered APIs.");
        var metadata = ((IEnumerable)Invoke(designer.GetType("aan", true)!.GetMethod("CreateMetadata", Flags)!, null, [descriptors])!).Cast<object>()
            .ToDictionary(m => Key(Text(m, "Namespace"), Text(m, "Method")), StringComparer.Ordinal);
        var dispatcherType = designer.GetType("aao", true)!;
        dispatcher = Invoke(dispatcherType.GetMethod("CreateForAllNamespaces", Flags)!, null, [])!;
        nativeInvoke = dispatcherType.GetMethods(Flags).Single(m => m.Name == "InvokeAsync" && m.GetParameters().Length == 3);
        if (nativeInvoke.GetParameters()[2].ParameterType.FullName != "Newtonsoft.Json.Linq.JObject" || nativeInvoke.ReturnType != typeof(Task<object>))
            throw new BridgeException("NativeApiContractChanged", "The installed native dispatcher signature changed.");
        var jsonAssembly = nativeInvoke.GetParameters()[2].ParameterType.Assembly;
        parseObject = nativeInvoke.GetParameters()[2].ParameterType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, null, [typeof(string)], null)!;
        var settingsType = jsonAssembly.GetType("Newtonsoft.Json.JsonSerializerSettings", true)!;
        serializerSettings = dispatcherType.GetField("gaw", Flags)!.GetValue(null)!;
        serialize = jsonAssembly.GetType("Newtonsoft.Json.JsonConvert", true)!.GetMethod("SerializeObject", BindingFlags.Public | BindingFlags.Static, null,
            [typeof(object), settingsType], null)!;
        var excluded = new List<NativeApiExclusion>();
        foreach (var descriptor in all)
        {
            var ns = Text(descriptor, "Namespace"); var method = Text(descriptor, "Method");
            if (!NativeApiCatalog.IsAllowed(ns, method, Text(descriptor, "Access")))
            {
                excluded.Add(new(ns, method, NativeApiCatalog.ExclusionReason(ns, method, Text(descriptor, "Access"))!));
                continue;
            }
            var nativeMetadata = JsonSerializer.SerializeToElement(metadata[Key(ns, method)], metadata[Key(ns, method)].GetType());
            var access = Text(descriptor, "Access");
            var entry = new NativeApiEntry(ns, method, NativeApiCatalog.IsRuntimeAction(ns,method) || !access.Equals("Read", StringComparison.OrdinalIgnoreCase), Text(descriptor, "Risk"),
                NativeApiCatalog.Scope(ns, method), nativeMetadata,
                Get(descriptor, "RequestType") as Type ?? throw new MissingMemberException("RequestType"),
                Get(descriptor, "ResponseType") as Type ?? throw new MissingMemberException("ResponseType"));
            entries.Add(Key(ns, method), entry);
        }
        ExcludedApis = excluded.AsReadOnly();
        catalog = entries.Values.Select(e => new PlatformApiDescriptor(e.Namespace, e.Method,
                ConvertSchema(e.Metadata.GetProperty("RequestTypeSchema")),
                ConvertResponseSchema(e.Metadata.GetProperty("ResponseTypeSchema"), e.ResponseType), e.MutatesProject, e.Scope,
                Description(e)))
            .OrderBy(e => e.Namespace, StringComparer.Ordinal).ThenBy(e => e.Method, StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<PlatformApiDescriptor> Catalog() => catalog;
    private static string? Description(NativeApiEntry entry)
    {
        var text = entry.Metadata.TryGetProperty("Description", out var description) ? description.GetString() : null;
        return PageCommandTreePreflight.AppliesTo(entry.Namespace, entry.Method)
            ? text + """ Each Condition branch requires a predicate, including {"key":"else","predicate":{"type":"else"},"commands":[]}. For a pageElement location that reads a record field, use a formula such as "=Record.Field". """
            : text;
    }
    public bool Contains(string @namespace, string method) => entries.ContainsKey(Key(@namespace, method));
    public bool Mutates(string @namespace, string method) => Entry(@namespace, method).MutatesProject;
    public NativeApiEntry Entry(string @namespace, string method) => entries.TryGetValue(Key(@namespace, method), out var entry) ? entry :
        throw new BridgeException("ApiNotAllowed", "This API is not registered as an allowed project-definition operation: " + @namespace + "." + method);

    // Pre-dispatch DTO boundary. Schema names are fixed by the native registry;
    // no CLR reflection target or plugin-command-node shape comes from clients.
    // Newtonsoft's case-insensitive DTO matching is preserved, but unknown and
    // duplicate top-level keys cannot silently become a successful no-op patch.
    public void ValidateRequestParameters(string @namespace, string method, JsonElement parameters)
    {
        var entry = Entry(@namespace, method);
        NativeApiCatalog.ValidateParameters(@namespace, method, parameters);
        var schema = catalog.Single(item => item.Namespace == @namespace && item.Method == method).InputSchema;
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw new BridgeException("NativeRequestSchemaUnavailable", "The registered native request has no verified top-level DTO property schema.");
        var allowed = properties.EnumerateObject().ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase);
        var supplied = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
        {
            if (!allowed.TryGetValue(property.Name, out var field)) throw new BridgeException("UnknownApiParameter", "Unknown native request property: " + property.Name);
            if (!supplied.TryAdd(property.Name, property.Value)) throw new BridgeException("AmbiguousApiParameter", "Duplicate or differently-cased request property: " + property.Name);
            if (entry.MutatesProject && entry.Scope == EffectScope.ProjectDefinition)
                ValidateContainerShape(field, property.Value, property.Name);
        }
        if (schema.TryGetProperty("required", out var required))
            foreach (var name in required.EnumerateArray())
                if (!supplied.TryGetValue(name.GetString()!, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    throw new BridgeException("RequiredApiParameterMissing", "Required native request DTO property is missing or null: " + name.GetString());
        if (PageCommandTreePreflight.AppliesTo(@namespace, method) && supplied.TryGetValue("cells", out var cells))
            new PageCommandTreePreflight(designer, supplied["pageName"].GetString()!).Validate(cells);
        if (@namespace == "page" && method == "updatePageLoadCommands")
        {
            if (!supplied.TryGetValue("commands", out var commands) || commands.ValueKind != JsonValueKind.Array)
                throw new BridgeException("InvalidCommandTree", "commands must be supplied as an array; an empty array explicitly clears page load commands.");
        }
        var signatureEdit=method=="updateServerCommandParameterSettings"||supplied.Keys.Any(name=>name.Equals("parameters",StringComparison.OrdinalIgnoreCase)||name.Equals("returnProperties",StringComparison.OrdinalIgnoreCase));
        if(@namespace=="app.customLibrary" && NativeLibraryLifecycle.Supports(method))
        {
            if(libraryLifecycleValidator is not null) libraryLifecycleValidator(method,parameters);
            else if(method is "delete" or "renameLocal") throw new BridgeException("NativeLibraryContextUnavailable","Library deletion/rename requires the bound native root and complete dependency validation.");
        }
        if(NativeCommandSignatures.Supports(@namespace,method)&&signatureEdit)
        {
            if(boundSettings is null) throw new BridgeException("NativeSignatureContextUnavailable","Command metadata validation requires the explicitly bound native document settings.");
            var settings=boundSettings();
            try { NativeCommandSignatures.Validate(@namespace,method,parameters,settings); }
            catch(BridgeException error) when(error.Code=="CommandSignatureChangeNotSupported" && requireUnreferencedSignature is not null)
            {
                requireUnreferencedSignature(@namespace,method,parameters);
                NativeCommandSignatures.ValidateUnreferencedDefinition(@namespace,method,parameters,settings);
            }
        }
    }

    // Reject incompatible containers before entering the native dispatcher. Preserve native
    // scalar conversions, optional nulls and open-ended plugin command property bags.
    private static void ValidateContainerShape(JsonElement schema, JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Null || !schema.TryGetProperty("type", out var type)) return;
        switch (type.GetString())
        {
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                    throw new BridgeException("InvalidApiParameter", path + " must be an array.");
                if (schema.TryGetProperty("items", out var items))
                {
                    int index = 0;
                    foreach (var item in value.EnumerateArray()) ValidateContainerShape(items, item, $"{path}[{index++}]");
                }
                break;
            case "string" or "integer" or "number" or "boolean" when value.ValueKind is JsonValueKind.Array or JsonValueKind.Object:
                throw new BridgeException("InvalidApiParameter", path + " requires a scalar " + type.GetString() + " value.");
        }
    }

    public async Task<JsonElement> InvokeAsync(string @namespace, string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        var entry = Entry(@namespace, method);
        if(@namespace=="app.customLibrary" && NativeLibraryLifecycle.Supports(method) && libraryLifecycleValidator is null)
            throw new BridgeException("NativeLibraryContextUnavailable","Library mutation requires a bound native lifecycle validator.");
        ValidateRequestParameters(@namespace, method, parameters);
        cancellationToken.ThrowIfCancellationRequested();
        var pageLoadEdit = await PreparePageLoadAsync(@namespace, method, parameters, cancellationToken);
        if (pageLoadEdit is not null) return await ApplyPageLoadAsync(pageLoadEdit);
        if (entry.MutatesProject && (@namespace == "table" || @namespace == "table.view"))
            ValidateTableDefinitionScope(@namespace, method, parameters);
        cancellationToken.ThrowIfCancellationRequested();
        if ((@namespace == "table.view" && method is "delete" or "rename") || (@namespace == "table" && method == "renameTable"))
        {
            var args = JsonNode.Parse(parameters.GetRawText())!.AsObject();
            args[method is "delete" ? "deleteDatabaseObject" : "renameDatabaseObject"] = false;
            parameters = JsonSerializer.SerializeToElement(args);
        }
        var nativeParameters = Invoke(parseObject, null, [parameters.GetRawText()]);
        // aao performs native request validation, abe.EnsureCanExecute (including
        // checkout/permission handling), dirty-module registration and the native
        // ContainsUnsavedChanges update. Never call descriptor.MethodInfo directly.
        var preference=@namespace=="notification" && method=="updateNotification" &&
            parameters.EnumerateObject().Any(p=>p.Name.Equals("parameters",StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind==JsonValueKind.Array)
            ? Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase",true)!.GetProperty("IgnoreDisableCommands",Flags):null;
        var previousPreference=preference?.GetValue(null);
        object result;
        try
        {
            // Match the complete caller inventory used by the owner plan,
            // including disabled definitions, during native parameter rewrites.
            preference?.SetValue(null,false);
            var pending = (Task<object>)Invoke(nativeInvoke, dispatcher, [@namespace, method, nativeParameters])!;
            // The native API has no CancellationToken. Keep awaiting after dispatch:
            // caller cancellation cannot prove the native action has stopped.
            result = await pending;
        }
        finally { preference?.SetValue(null,previousPreference); }
        var json = (string)Invoke(serialize, null, [result, serializerSettings])!;
        using var document = JsonDocument.Parse(json);
        var response = document.RootElement.Clone();
        return @namespace == "page" && method == "getCells"
            ? NativeCellCommandReadback.RestoreFieldFormulas(designer, response, cancellationToken)
            : response;
    }

    private void ValidateTableDefinitionScope(string ns, string method, JsonElement parameters)
    {
        // These lookups only read the current native model. Permission/checkout
        // and actual edits still happen inside the registered native dispatcher.
        foreach (var parameter in parameters.EnumerateObject())
        {
            var nameKey = parameter.Name.ToLowerInvariant();
            if (nameKey == "connectionid" && parameter.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(parameter.Value.GetString()))
                throw new BridgeException("ExternalDatabaseSchemaNotAllowed", "This project-definition adapter does not create or alter objects through external database connections.");
            if (nameKey is not ("tablename" or "viewname" or "foreigntable" or "primarytable") || parameter.Value.ValueKind != JsonValueKind.String) continue;
            var name = parameter.Value.GetString(); if (string.IsNullOrEmpty(name)) continue;
            var actions = designer.GetType("Forguncy.AI.Actions.TableSandboxAIActions", true)!;
            var lookup = actions.GetMethod("GetTableOrThrow", Flags, null, [typeof(string)], null)
                ?? throw new BridgeException("NativeApiContractChanged", "The fixed native table lookup contract changed.");
            var table = Invoke(lookup, null, [name])!;
            if ((Get(table, "ExternalDatabaseInfo") is not null || Get(table, "IsLinkedTableOrLinkedView") is true || Get(table, "IsCopyExternalBuiltInTable") is true) &&
                !NativeLinkedTablePolicy.Allows(ns,method,table))
                throw new BridgeException("ExternalDatabaseSchemaNotAllowed", "Mutating linked or external-copy table/view definitions is withheld until its external database effects are separately authorized and verified.");
        }
    }

    private static object? Get(object value, string name) => value.GetType().GetProperty(name, Flags)?.GetValue(value);
    private static string Text(object value, string name) => Get(value, name)?.ToString() ?? "";
    private static string Key(string ns, string method) => ns + "." + method;
    private static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    // aan exposes documentation schemas, not JSON Schema. Translate their
    // structural portion ourselves and retain the original as an extension so
    // derived command/value types and their native documentation are not lost.
    private static JsonElement ConvertSchema(JsonElement native)
    {
        if (native.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return JsonSerializer.SerializeToElement(new { type = "object" });
        return JsonSerializer.SerializeToElement(ConvertObject(native, 0));
    }
    private static JsonElement ConvertResponseSchema(JsonElement native, Type responseType)
    {
        if (responseType == typeof(string) || responseType.IsPrimitive || responseType == typeof(decimal) || responseType.IsEnum)
            return JsonSerializer.SerializeToElement(Primitive(responseType.Name));
        if (typeof(IEnumerable).IsAssignableFrom(responseType) && !typeof(IDictionary).IsAssignableFrom(responseType))
        {
            var itemType = responseType.IsArray ? responseType.GetElementType() : responseType.GetInterfaces().Append(responseType)
                .FirstOrDefault(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            var primitive = itemType is not null && (itemType == typeof(string) || itemType.IsPrimitive || itemType == typeof(decimal) || itemType.IsEnum);
            return JsonSerializer.SerializeToElement(new Dictionary<string, object?>
            {
                ["type"] = "array", ["items"] = primitive ? Primitive(itemType!.Name) : native.ValueKind == JsonValueKind.Object ? ConvertObject(native, 0) : new Dictionary<string, object?>()
            });
        }
        return ConvertSchema(native);
    }
    private static Dictionary<string, object?> ConvertObject(JsonElement native, int depth)
    {
        var result = new Dictionary<string, object?> { ["type"] = "object" };
        if (depth > 24) return result;
        var properties = new Dictionary<string, object?>(); var required = new List<string>();
        void Append(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object) return;
            if (schema.TryGetProperty("baseSchema", out var parent) && parent.ValueKind == JsonValueKind.Object) Append(parent);
            if (!schema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Array) return;
            foreach (var p in props.EnumerateArray())
            {
                var name = p.GetProperty("name").GetString()!;
                properties[name] = ConvertProperty(p, depth + 1);
                if (p.TryGetProperty("isRequired", out var r) && r.ValueKind == JsonValueKind.True) required.Add(name);
            }
        }
        Append(native); result["properties"] = properties;
        if (required.Count != 0) result["required"] = required.Distinct().ToArray();
        if (depth == 0) result["x-forguncy-nativeSchema"] = native.Clone();
        return result;
    }
    private static Dictionary<string, object?> ConvertProperty(JsonElement property, int depth)
    {
        var name = property.TryGetProperty("typeName", out var t) ? t.GetString() ?? "object" : "object";
        var result = new Dictionary<string, object?>();
        if (name.StartsWith("list:", StringComparison.OrdinalIgnoreCase) || name.EndsWith("[]", StringComparison.Ordinal))
        {
            result["type"] = "array";
            result["items"] = property.TryGetProperty("itemSchema", out var item) && item.ValueKind == JsonValueKind.Object ? ConvertObject(item, depth) : Primitive(name.StartsWith("list:", StringComparison.OrdinalIgnoreCase) ? name[5..] : name[..^2]);
        }
        else if (property.TryGetProperty("objectSchema", out var obj) && obj.ValueKind == JsonValueKind.Object)
            result = ConvertObject(obj, depth);
        else result = Primitive(name);
        if (property.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) result["description"] = d.GetString();
        if (property.TryGetProperty("enumValues", out var values) && values.ValueKind == JsonValueKind.Array)
            result["x-forguncy-enumValues"] = values.Clone();
        return result;
    }
    private static Dictionary<string, object?> Primitive(string name)
    {
        var result = new Dictionary<string, object?> { ["x-forguncy-type"] = name };
        var type = name.TrimEnd('?').ToLowerInvariant() switch
        {
            "string" or "guid" or "datetime" or "timespan" => "string",
            "bool" or "boolean" => "boolean",
            "byte" or "short" or "int16" or "int" or "int32" or "long" or "int64" => "integer",
            "decimal" or "double" or "float" or "single" => "number",
            _ => null
        };
        if (type is not null) result["type"] = type;
        return result;
    }
}

public sealed record NativeApiEntry(string Namespace, string Method, bool MutatesProject, string Risk,
    EffectScope Scope, JsonElement Metadata, Type RequestType, Type ResponseType);
public sealed record NativeApiExclusion(string Namespace, string Method, string Reason);
