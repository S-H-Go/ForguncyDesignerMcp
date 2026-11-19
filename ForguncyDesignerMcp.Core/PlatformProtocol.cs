using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForguncyDesignerMcp.Core.Platform;

public static class PlatformJson
{
    public static readonly JsonSerializerOptions Options = Create();
    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(Wire.Json);
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Options);
    public static T Read<T>(JsonElement value) => value.Deserialize<T>(Options)
        ?? throw new BridgeException("InvalidArguments", "A non-null typed parameter object is required.");
}

public sealed record PlatformDescription(ProjectSnapshot Snapshot, PlatformRuntimeContext Runtime);
public sealed record PlatformDescribeRequest(ProjectTarget Target);
public sealed record PlatformCancelRequest(ProjectTarget Target, string OperationId);
public sealed record PlatformCancellationResult(bool Found, string State, bool ExecutionMayContinue);

// Host-owned ledger; Result preserves typed adapter evidence and host-verified
// receipt state. Generic API payloads are retained without receipt interpretation.
public sealed record PlatformOperationStatus(string OperationId, string State, string Code, string Message,
    EffectScope Scope, bool ExecutionMayContinue, JsonElement? Result = null);
