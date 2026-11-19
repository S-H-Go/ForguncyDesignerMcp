using System.Reflection;
using System.Text.Json;
namespace ForguncyDesignerMcp.Core.Platform;

internal static class CollaborationSchemas
{
    internal static JsonElement Input(string method)
    {
        if (method == "status") return PlatformJson.Element(new { type = "object", additionalProperties = false });
        if (method == "receive") return PlatformJson.Element(Describe(typeof(CollaborationReceiveRequest)));
        var schema = JsonSerializer.SerializeToNode(Describe(typeof(CollaborationMutationRequest)), PlatformJson.Options)!;
        schema["required"] = JsonSerializer.SerializeToNode(method == "checkin"
            ? new[] { "operationId", "target", "expectedProjectRevision", "expectedUser", "modules", "comment", "expectedRemoteUrl", "expectedBranch", "expectedRemoteHead" }
            : new[] { "operationId", "target", "expectedProjectRevision", "expectedUser", "modules" });
        return PlatformJson.Element(schema);
    }
    internal static JsonElement Output(string method) => method == "status" ? PlatformJson.Element(Describe(typeof(CollaborationStatus))) : PlatformApiSchemas.OperationStatus(PlatformJson.Element(Describe(method == "receive" ? typeof(CollaborationReceiveReceipt) : typeof(CollaborationMutationReceipt))));
    // Only the closed, source-owned collaboration DTO graph is described. This
    // does not expose reflection over Designer objects or accept CLR type names.
    private static object Describe(Type type)
    {
        if (Nullable.GetUnderlyingType(type) is { } underlying) return Maybe(Describe(underlying));
        if (type == typeof(string)) return new { type = "string" };
        if (type == typeof(bool)) return new { type = "boolean" };
        if (type == typeof(int)) return new { type = "integer" };
        if (type.IsEnum) return new { type = "string", @enum = Enum.GetNames(type) };
        if (type.IsArray) return new { type = "array", items = Describe(type.GetElementType()!) };
        if (type == typeof(Dictionary<string, string>)) return new { type = "object", additionalProperties = new { type = "string" } };
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)) return new { type = "array", items = Describe(type.GetGenericArguments()[0]) };
        var nullable = new NullabilityInfoContext();
        var properties = type.GetProperties().ToDictionary(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name), p =>
        {
            var schema = Describe(p.PropertyType);
            return !p.PropertyType.IsValueType && nullable.Create(p).ReadState == NullabilityState.Nullable ? Maybe(schema) : schema;
        });
        return new { type = "object", properties, required = properties.Keys.Where(x => x != "comment").ToArray(), additionalProperties = false };
    }
    private static object Maybe(object schema) => new { anyOf = new[] { schema, new { type = "null" } } };
}
