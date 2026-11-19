using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

public sealed record DevelopmentPreviewRequest(string OperationId, ProjectTarget Target, string ExpectedProjectRevision);
public delegate Task<string?> StartNativeDevelopmentPreview(DevelopmentPreviewRequest request, Action dispatched);

public sealed partial class PlatformProjectRouter
{
    private readonly StartNativeDevelopmentPreview? startDevelopmentPreview;

    private static PlatformApiDescriptor DevelopmentPreviewDescriptor() =>
        new("preview", "start", PlatformSchemas.Input("preview", "start"),
            PlatformApiSchemas.OperationStatus(PlatformJson.Element(new
            {
                type = "object", properties = new
                {
                    state = new { type = "string" },
                    code = new { type = "string" },
                    message = new { type = "string" },
                    url = new { type = new[] { "string", "null" }, format = "uri" }
                }
            })), true, EffectScope.DevelopmentPreview,
            "Start the bound clean document's local development preview using the installed Designer, not vendor MCP. " +
            "Requires a clean document and does not request a save or publication. Query project.get_operation with the original ID. " +
            "Success returns result.url; the client separately verifies HTTP and business behavior.");

    private JsonElement SubmitDevelopmentPreview(PlatformApiRequest api)
    {
        if (startDevelopmentPreview is null)
            throw new BridgeException("DevelopmentPreviewUnsupported", "This Designer build has no supported native preview contract.");
        var properties = api.Parameters.EnumerateObject().Select(p => p.Name).ToArray();
        if (properties.Length != 3 || properties.Distinct(StringComparer.Ordinal).Count() != 3 ||
            properties.Any(name => name is not ("operationId" or "target" or "expectedProjectRevision")))
            throw new BridgeException("InvalidArguments", "Preview accepts only operationId, target and expectedProjectRevision.");
        var input = PlatformJson.Read<DevelopmentPreviewRequest>(api.Parameters);
        CheckTarget(input.Target);
        CheckOperationId(api.OperationId, input.OperationId);
        if (string.IsNullOrWhiteSpace(input.ExpectedProjectRevision))
            throw new BridgeException("BaselineRequired", "Read the current project snapshot before requesting preview.");
        return Submit(api, input.OperationId, EffectScope.DevelopmentPreview, async started =>
        {
            var before = await SnapshotAsync(input.Target, default, issueBaseline: false);
            RequireCleanPreviewSnapshot(before, input);
            var url = await startDevelopmentPreview(input, started);
            var after = await SnapshotAsync(input.Target, default, issueBaseline: false);
            if (after.HasUnsavedChanges)
                throw new BridgeException("PreviewProjectChanged", "The document became dirty during preview. Inspect the original receipt without replay.");
            if (url is null)
                return PlatformJson.Element(new { state = "failed", code = "NativePreviewRejected",
                    message = "The native Designer reported that preview startup did not complete.", url });
            if (!TryNormalizeDevelopmentPreviewUrl(url, out var normalized))
                throw new BridgeException("PreviewUrlUnconfirmed", "The native startup result did not provide a safe local development URL.");
            return PlatformJson.Element(new { state = "succeeded", code = "PreviewStarted",
                message = "Designer confirmed native preview startup; HTTP readiness has not been checked.", url = normalized });
        });
    }

    public static void RequireCleanPreviewSnapshot(ProjectSnapshot snapshot, DevelopmentPreviewRequest request)
    {
        if (!SameTarget(snapshot.Target, request.Target))
            throw new BridgeException("SessionInvalidated", "The bound native document changed.");
        if (snapshot.Busy) throw new BridgeException("DesignerBusy", "Designer is busy.");
        if (snapshot.HasUnsavedChanges)
            throw new BridgeException("PreviewRequiresCleanDocument", "Save and verify the bound document separately if authorized, then request preview from a fresh snapshot.");
        if (!string.Equals(snapshot.Revision, request.ExpectedProjectRevision, StringComparison.Ordinal))
            throw new BridgeException("ProjectRevisionMismatch", "The project revision no longer matches the preview request.");
    }

    private static bool TryNormalizeDevelopmentPreviewUrl(string value, out string? normalized)
    {
        normalized = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            url.Scheme is not ("http" or "https") || !url.IsLoopback ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) ||
            !string.IsNullOrEmpty(url.Fragment)) return false;
        normalized = url.AbsoluteUri;
        return true;
    }
}
