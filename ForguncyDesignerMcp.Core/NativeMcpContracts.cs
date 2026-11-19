using System.Text.Json;

namespace ForguncyDesignerMcp.Core;

public sealed record NativeMcpRequest(string Method, JsonElement Parameters, string SessionId, string ProjectPath,
    string? CallId = null);

// Returns the original MCP result value. Do not wrap content/media as text.
public interface INativeMcpBackend
{
    Task<JsonElement> RequestNativeAsync(NativeMcpRequest request);
}

public sealed record NativeCancellationResult(bool Found, string State, bool ExecutionMayContinue);
