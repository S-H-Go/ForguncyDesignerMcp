using System.Text.Json;
using System.Text.Json.Serialization;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.PlatformRuntime;

internal static class RuntimeJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter() }, MaxDepth = 64 };
    internal static JsonElement Serialize(object? value) => JsonSerializer.SerializeToElement(value, Options);
    internal static string? String(JsonElement args, string key, string? defaultValue = null) =>
        !args.TryGetProperty(key, out var value) ? defaultValue : value.ValueKind == JsonValueKind.String
            ? value.GetString() : throw new ArgumentException($"{key} must be a string.");
    internal static string RequiredString(JsonElement args, string key) =>
        String(args, key) is { Length: > 0 } value ? value : throw new ArgumentException($"{key} is required.");
    internal static int Int(JsonElement args, string key, int defaultValue, int min, int max) =>
        !args.TryGetProperty(key, out var value) ? defaultValue : value.TryGetInt32(out var result) && result >= min && result <= max
            ? result : throw new ArgumentException($"{key} must be an integer between {min} and {max}.");
    internal static bool Bool(JsonElement args, string key, bool defaultValue = false) =>
        !args.TryGetProperty(key, out var value) ? defaultValue : value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : throw new ArgumentException($"{key} must be boolean.");
    internal static PlatformApiDescriptor RequireApi(PlatformRuntimeContext context, string ns, string method) =>
        context.Apis.SingleOrDefault(d => d.Namespace == ns && d.Method == method)
        ?? throw new InvalidOperationException($"API api.{ns}.{method} is not registered for Designer {context.Target.DesignerVersion}.");
    internal static JsonElement ToolResult(JsonElement value, bool isError = false) => Serialize(new
    {
        content = new[] { new { type = "text", text = value.GetRawText() } },
        structuredContent = value.ValueKind == JsonValueKind.Object ? value : Serialize(new { value }),
        isError
    });
}
