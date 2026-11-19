namespace ForguncyDesignerMcp.Core.Platform;

/// <summary>Designer-side pipe handler; reuse one endpoint/router per document.</summary>
public sealed class PlatformBridgeEndpoint(PlatformProjectRouter router)
{
    public static bool CanHandle(string method) => method is "platform.describe" or "platform.invoke" or "platform.cancel";

    public async Task<BridgeResponse> HandleAsync(BridgeRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ProtocolVersion != Wire.Version) throw new BridgeException("ProtocolMismatch", "Bridge protocol version mismatch.");
        return request.Method switch
        {
            "platform.describe" => BridgeResponse.Success(PlatformJson.Element(await router.DescribeAsync(
                PlatformJson.Read<PlatformDescribeRequest>(request.Arguments).Target, cancellationToken))),
            "platform.invoke" => BridgeResponse.Success(await router.InvokeAsync(
                PlatformJson.Read<PlatformApiRequest>(request.Arguments), cancellationToken)),
            "platform.cancel" => BridgeResponse.Success(PlatformJson.Element(router.Cancel(
                PlatformJson.Read<PlatformCancelRequest>(request.Arguments)))),
            _ => throw new BridgeException("UnknownPlatformMethod", "Unknown platform bridge method.")
        };
    }
}
