using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.PlatformRuntime;

/// <summary>Documents built-in wire APIs from their owning record definitions rather than copied contracts.</summary>
internal static class PlatformContractSchemas
{
    internal static PlatformRuntimeContext Enrich(PlatformRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var descriptors = context.Apis.Select(EnrichDescriptor).ToArray();
        return context with { Apis = descriptors };
    }

    private static PlatformApiDescriptor EnrichDescriptor(PlatformApiDescriptor descriptor)
    {
        var (inputType, outputType) = ContractTypes(descriptor.Namespace, descriptor.Method);
        var input = descriptor.InputSchema;
        if (IsGenericObject(input))
        {
            if (inputType is not null)
            {
                var schema = new RecordSchemaBuilder().Root(inputType);
                if (descriptor.Namespace == "module" && descriptor.Method is "create" or "update" or "delete")
                {
                    var operation = Enum.Parse<ModuleOperation>(descriptor.Method, true);
                    var properties = schema["properties"]!.AsObject();
                    properties["operation"]!.AsObject()["const"] = operation.ToString();
                }
                input = Element(schema);
            }
            else if (descriptor.Namespace == "project" && descriptor.Method is "get_operation" or "cancel_operation")
            {
                input = Element(new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["operationId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } },
                    ["required"] = new JsonArray("operationId")
                });
            }
            else if ((descriptor.Namespace == "project" && descriptor.Method is "snapshot" or "capabilities") ||
                (descriptor.Namespace == "catalog" && descriptor.Method == "list"))
            {
                input = Element(new JsonObject
                {
                    ["type"] = "object", ["properties"] = new JsonObject(),
                    ["additionalProperties"] = false
                });
            }
        }
        var output = descriptor.OutputSchema;
        if (output is null && outputType is not null)
            output = Element(new RecordSchemaBuilder().Root(outputType));
        return descriptor with { InputSchema = input, OutputSchema = output };
    }

    private static (Type? Input, Type? Output) ContractTypes(string ns, string method) => (ns, method) switch
    {
        ("collaboration", "status") => (null, typeof(CollaborationStatus)),
        ("collaboration", "checkout" or "checkin") => (typeof(CollaborationMutationRequest), null),
        ("module", "list") => (typeof(ModuleQuery), typeof(ModulePage)),
        ("module", "read") => (typeof(ModuleAddress), typeof(ModuleDocument)),
        ("module", "references" or "findReferences") => (typeof(ModuleAddress), typeof(ReferenceResult)),
        ("module", "create" or "update" or "delete") => (typeof(ModuleMutationRequest), null),
        ("module", "validate") => (typeof(ModuleMutationRequest), typeof(ModuleValidationResult)),
        // The host returns an operation receipt immediately for save and mutation methods.
        // ProjectSaveResult is nested in the later receipt, so it must not be claimed as the direct output.
        ("module", "save") => (typeof(ProjectSaveRequest), null),
        ("project", "snapshot") => (null, typeof(ProjectSnapshot)),
        ("project", "capabilities") => (null, typeof(IReadOnlyList<ModuleCapability>)),
        ("project", "validate_api") => (null, typeof(ModuleValidationResult)),
        ("catalog", "list") => (null, typeof(IReadOnlyList<PlatformApiDescriptor>)),
        _ => (null, null)
    };

    private static bool IsGenericObject(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String || type.GetString() != "object") return false;
        // A vendor/adapter schema with meaningful constraints is its owner's contract and stays intact.
        foreach (var property in schema.EnumerateObject())
        {
            if (property.Name == "type") continue;
            if (property.Name == "properties" && property.Value.ValueKind == JsonValueKind.Object &&
                !property.Value.EnumerateObject().Any()) continue;
            return false;
        }
        return true;
    }

    private static JsonElement Element(JsonNode value) => JsonSerializer.SerializeToElement(value);

    private sealed class RecordSchemaBuilder
    {
        private const int MaxDepth = 16;
        private const int MaxDefinitions = 64;
        private readonly NullabilityInfoContext nullability = new();
        private readonly JsonObject definitions = new();
        private readonly Dictionary<Type, string> names = new();

        internal JsonObject Root(Type type)
        {
            var schema = Schema(type, null, 0);
            if (schema is JsonObject obj && obj["$ref"] is JsonValue reference &&
                reference.TryGetValue<string>(out var path) && path.StartsWith("#/$defs/", StringComparison.Ordinal))
                schema = definitions[path[8..]]!.DeepClone();
            var root = schema.AsObject();
            root["$schema"] = "https://json-schema.org/draft/2020-12/schema";
            if (definitions.Count > 0) root["$defs"] = definitions.DeepClone();
            return root;
        }

        private JsonNode Schema(Type type, NullabilityInfo? info, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("Wire schema exceeds its recursion depth budget.");
            var nullableValue = Nullable.GetUnderlyingType(type);
            var nullable = nullableValue is not null || (!type.IsValueType && info?.ReadState == NullabilityState.Nullable);
            var actual = nullableValue ?? type;
            JsonObject schema;
            if (actual == typeof(JsonElement) || actual == typeof(object) || typeof(JsonNode).IsAssignableFrom(actual))
                return new JsonObject(); // JSON values are opaque wire data, never reflected CLR objects.
            if (actual.IsEnum)
                schema = new JsonObject { ["type"] = "string", ["enum"] = Strings(Enum.GetNames(actual)) };
            else if (actual == typeof(string) || actual == typeof(char) || actual == typeof(Guid) ||
                actual == typeof(DateTime) || actual == typeof(DateTimeOffset) || actual == typeof(Uri))
            {
                schema = new JsonObject { ["type"] = "string" };
                if (actual == typeof(Guid)) schema["format"] = "uuid";
                if (actual == typeof(DateTime) || actual == typeof(DateTimeOffset)) schema["format"] = "date-time";
                if (actual == typeof(Uri)) schema["format"] = "uri";
            }
            else if (actual == typeof(bool)) schema = new JsonObject { ["type"] = "boolean" };
            else if (actual == typeof(byte) || actual == typeof(sbyte) || actual == typeof(short) || actual == typeof(ushort) ||
                actual == typeof(int) || actual == typeof(uint) || actual == typeof(long) || actual == typeof(ulong))
                schema = new JsonObject { ["type"] = "integer" };
            else if (actual == typeof(float) || actual == typeof(double) || actual == typeof(decimal))
                schema = new JsonObject { ["type"] = "number" };
            else if (TryDictionary(actual, out var valueType))
                schema = new JsonObject { ["type"] = "object", ["additionalProperties"] =
                    Schema(valueType, info?.GenericTypeArguments.ElementAtOrDefault(1), depth + 1) };
            else if (TrySequence(actual, out var elementType))
                schema = new JsonObject { ["type"] = "array", ["items"] =
                    Schema(elementType, actual.IsArray ? info?.ElementType : info?.GenericTypeArguments.FirstOrDefault(), depth + 1) };
            else schema = RecordReference(actual, depth);
            return nullable ? new JsonObject { ["anyOf"] = new JsonArray(schema, new JsonObject { ["type"] = "null" }) } : schema;
        }

        private JsonObject RecordReference(Type type, int depth)
        {
            if (names.TryGetValue(type, out var known)) return new JsonObject { ["$ref"] = "#/$defs/" + known };
            if (names.Count >= MaxDefinitions) throw new InvalidOperationException("Wire schema exceeds its definition budget.");
            if (type.Assembly != typeof(ModuleQuery).Assembly)
                throw new InvalidOperationException("Only frozen wire-contract records may be reflected into API schemas: " + type.FullName);
            var name = type.Name;
            if (names.Values.Contains(name, StringComparer.Ordinal)) name = (type.FullName ?? name).Replace('.', '_').Replace('+', '_');
            names[type] = name;
            definitions[name] = new JsonObject(); // Install the anchor before descending into recursive records.
            definitions[name] = Record(type, depth + 1);
            return new JsonObject { ["$ref"] = "#/$defs/" + name };
        }

        private JsonObject Record(Type type, int depth)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod is { IsPublic: true } && property.GetIndexParameters().Length == 0 &&
                    property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always).ToArray();
            if (properties.Length > 64) throw new InvalidOperationException("Wire schema exceeds its property budget.");
            var constructor = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(candidate => candidate.GetParameters().All(parameter => properties.Any(property =>
                    property.Name.Equals(parameter.Name, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(candidate => candidate.GetParameters().Length).FirstOrDefault()
                ?? throw new InvalidOperationException("Wire contract has no matching public constructor: " + type.FullName);
            var parameters = constructor.GetParameters().ToDictionary(parameter => parameter.Name!, StringComparer.OrdinalIgnoreCase);
            var schemaProperties = new JsonObject();
            var required = new JsonArray();
            foreach (var property in properties)
            {
                parameters.TryGetValue(property.Name, out var parameter);
                var propertyNullability = parameter is null ? nullability.Create(property) : nullability.Create(parameter);
                var propertySchema = Schema(property.PropertyType, propertyNullability, depth + 1).AsObject();
                if (parameter?.HasDefaultValue == true && parameter.DefaultValue is not DBNull && parameter.DefaultValue != Missing.Value)
                    propertySchema["default"] = JsonSerializer.SerializeToNode(parameter.DefaultValue, property.PropertyType, RuntimeJson.Options);
                var propertyName = property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                    JsonNamingPolicy.CamelCase.ConvertName(property.Name);
                schemaProperties[propertyName] = propertySchema;
                var acceptsNull = Nullable.GetUnderlyingType(property.PropertyType) is not null ||
                    (!property.PropertyType.IsValueType && propertyNullability.ReadState == NullabilityState.Nullable);
                if (parameter?.HasDefaultValue != true && !acceptsNull) required.Add(propertyName);
            }
            var result = new JsonObject { ["type"] = "object", ["properties"] = schemaProperties, ["additionalProperties"] = false };
            if (required.Count > 0) result["required"] = required;
            return result;
        }

        private static bool TrySequence(Type type, out Type element)
        {
            if (type.IsArray) { element = type.GetElementType()!; return true; }
            var sequence = type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            element = sequence?.GetGenericArguments()[0] ?? typeof(object);
            return sequence is not null;
        }

        private static bool TryDictionary(Type type, out Type value)
        {
            var dictionary = type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType &&
                candidate.GetGenericTypeDefinition() is var definition &&
                (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>)) &&
                candidate.GetGenericArguments()[0] == typeof(string));
            value = dictionary?.GetGenericArguments()[1] ?? typeof(object);
            return dictionary is not null;
        }

        private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }
}
