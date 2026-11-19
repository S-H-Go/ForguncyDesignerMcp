using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// The same attributed C# dispatcher used by the native assistant. This route
// reports the native service response; it does not manufacture independent
// database persistence or project-export evidence for typed account mutations.
public sealed partial class V12ProjectAdapter : IProjectNativeServiceApiAdapter
{
    private bool nativeServicePending;
    private const string NativeServiceLimitation = "Native service response only. Independent database persistence and project export were not verified; this is not a Saved receipt. The native service has no cross-client atomic revision precondition.";

    internal static bool IsNativeServiceAction(PlatformApiDescriptor api) => api.MutatesProject && api.Scope == EffectScope.AccountService &&
        api.Namespace is "userManagement.user" or "userManagement.role" or "userManagement.organization";

    public Task ValidateNativeServiceApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default) => OnUi(() =>
    {
        guard.Value.CheckTarget(request.Target);
        if (!registry.Catalog().Any(api => api.Namespace == request.Namespace && api.Method == request.Method && IsNativeServiceAction(api)))
            throw new BridgeException("NativeServiceApiNotAllowed", "This is not a registered native user-management action.");
        if (request.Parameters.ValueKind != JsonValueKind.Object || !request.Parameters.TryGetProperty("guard", out _))
            throw new BridgeException("MutationGuardRequired", "Native service actions require the standard mutation guard.");
        var parameters = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();
        parameters.Remove("guard");
        registry.ValidateRequestParameters(request.Namespace, request.Method, JsonSerializer.SerializeToElement(parameters));
        return Task.FromResult(true);
    }, cancellationToken);

    public async Task<NativeServiceApiReceipt> InvokeNativeServiceApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            return await OnUi(async () =>
            {
                ValidateOperationId(request.OperationId);
                if (uncertain || accounts.IsValueCreated && accounts.Value.WritesQuarantined)
                    throw new BridgeException("NativeExecutionUncertain", "An earlier native outcome must be reconciled before another service action.");
                if (pending.Count != 0 || apiPending || pendingAccountExport is not null)
                    throw new BridgeException("PendingMutationRequiresSave", "Complete pending project or independently verified account work before a native service action.");
                var descriptor = registry.Catalog().SingleOrDefault(api => api.Namespace == request.Namespace && api.Method == request.Method);
                if (descriptor is null || !IsNativeServiceAction(descriptor))
                    throw new BridgeException("NativeServiceApiNotAllowed", "Only the exact registered native user-management actions are supported.");
                if (request.Parameters.ValueKind != JsonValueKind.Object || !request.Parameters.TryGetProperty("guard", out var guardJson))
                    throw new BridgeException("MutationGuardRequired", "Native service actions require the standard mutation guard.");
                var mutation = guardJson.Deserialize<PlatformApiMutationGuard>(GuardJson)
                    ?? throw new BridgeException("InvalidMutationGuard", "The native service guard is unavailable.");
                if (mutation.Scope != EffectScope.AccountService || mutation.OperationId != request.OperationId || mutation.Target != request.Target)
                    throw new BridgeException("NativeServiceGuardMismatch", "Native service action scope, original ID and exact target must match the reviewed guard.");
                var before = guard.Value.RequireWrite(request.Target, mutation.ExpectedProjectRevision, mutation.ExpectedUser,
                    allowDirty: nativeServicePending && guard.Value.Capture().Revision == ownDirtyRevision);
                if (mutation.Address is not null || mutation.ExpectedModuleRevision is not null || mutation.ExpectedServiceBinding is not null)
                    throw new BridgeException("NativeServiceGuardMismatch", "Use the typed account route for authoritative entity/service bindings; native API actions use their explicit native DTO identities.");
                if (startedApiOperations.Contains(request.OperationId!))
                    throw new BridgeException("OperationAlreadyStarted", "The original native action is already reserved; query its receipt instead of replaying it.");
                var parameters = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();
                parameters.Remove("guard");
                var nativeParameters = JsonSerializer.SerializeToElement(parameters);
                registry.ValidateRequestParameters(request.Namespace, request.Method, nativeParameters);
                guard.Value.RequireWrite(request.Target, mutation.ExpectedProjectRevision, mutation.ExpectedUser,
                    allowDirty: nativeServicePending && guard.Value.Capture().Revision == ownDirtyRevision);
                cancellationToken.ThrowIfCancellationRequested();
                startedApiOperations.Add(request.OperationId!);
                try
                {
                    // Retains native authentication, EnsureCanExecute, request
                    // validation, supported-provider checks and cache clearing.
                    // There is no HTTP/assistant runtime dependency or retry.
                    var response = await registry.InvokeAsync(request.Namespace, request.Method, nativeParameters, CancellationToken.None);
                    var accepted = NativeServiceAccepted(response);
                    var after = guard.Value.Capture();
                    if (after.Target != request.Target)
                        throw new BridgeException("NativeServiceTargetChanged", "The native document changed during the service action.");
                    if (!accepted && after.Revision != before.Revision)
                        throw new BridgeException("NativeServiceRejectedContextChanged", "The native context changed despite a rejected service response.");
                    nativeServicePending = after.HasUnsavedChanges;
                    if (after.HasUnsavedChanges) ownDirtyRevision = after.Revision;
                    return new NativeServiceApiReceipt(request.OperationId!, accepted ? "native_accepted" : "failed",
                        accepted ? "NativeServiceAccepted" : "NativeServiceRejected",
                        accepted ? "The native user-management service accepted the action." : "The native user-management service rejected the action.",
                        accepted, false, SanitizeNativeServiceResponse(response), false, false, false, NativeServiceLimitation);
                }
                catch (Exception)
                {
                    uncertain = true;
                    // Native exceptions may contain request fields. Preserve
                    // correlation and quarantine without exposing their text.
                    return new NativeServiceApiReceipt(request.OperationId!, "verification_failed", "NativeServiceOutcomeUnconfirmed",
                        "The native service action was dispatched but its response could not be confirmed. Do not replay it.",
                        null, false, null, false, false, true, NativeServiceLimitation);
                }
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    internal static bool NativeServiceAccepted(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || response.EnumerateObject().Count(field => field.Name == "success") != 1 ||
            !response.TryGetProperty("success", out var accepted) || accepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new BridgeException("NativeServiceResponseUnverified", "The native service did not return an explicit acceptance result.");
        return accepted.GetBoolean();
    }

    internal static JsonElement SanitizeNativeServiceResponse(JsonElement response)
    {
        _ = NativeServiceAccepted(response);
        // The native mutation response has scalar status/generated identity.
        // Omit free-form messages and any unexpected fields from service output.
        var result = new JsonObject { ["success"] = response.GetProperty("success").GetBoolean() };
        if (response.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var key)) result["id"] = key;
        return JsonSerializer.SerializeToElement(result);
    }
}
