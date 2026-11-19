using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    private PlatformApiMutationGuard ReadApiGuard(PlatformApiRequest request, PlatformApiDescriptor descriptor)
    {
        if (!request.Parameters.TryGetProperty("guard", out var node))
            throw new BridgeException("MutationGuardRequired", "Mutating native APIs require parameters.guard as shown in their actual catalog schema.");
        var guard = PlatformJson.Read<PlatformApiMutationGuard>(node);
        ValidateApiGuard(guard);
        if (!Guid.TryParseExact(guard.OperationId, "D", out _))
            throw new BridgeException("InvalidOperationId", "guard.operationId must be a UUID in D format.");
        CheckTarget(guard.Target);
        CheckOperationId(request.OperationId, guard.OperationId);
        if (guard.Scope != descriptor.Scope) throw new BridgeException("EffectScopeMismatch", "Declared effect differs from the registered API scope.");
        return guard;
    }

    private async Task<IProjectModuleApiAdapter?> PreparedApiAsync(PlatformApiDescriptor descriptor, CancellationToken token)
    {
        if (descriptor.Namespace == "page" && descriptor.Method == "updateCells" &&
            PlatformV11PageReads.SupportsVersion(Target.DesignerVersion) &&
            !(await adapter.GetApiCatalogAsync(token)).Any(x => x.Namespace == "page" && x.Method == "updateCells"))
            return new V11PagePreparedAdapter(adapter);
        return adapter is IProjectModuleApiAdapter prepared && prepared.SupportsModuleApi(descriptor.Namespace, descriptor.Method) ? prepared : null;
    }

    private async Task CheckNativeApiAsync(PlatformApiRequest request, PlatformApiMutationGuard guard, CancellationToken token)
    {
        if (adapter is IProjectApiPreflightAdapter preflight)
        {
            // Check the adapter's original module read token before a generic module read can renew it.
            await CheckApiMutationAsync(guard, token, readModule: false);
            await preflight.ValidateProjectApiAsync(request with { OperationId = guard.OperationId }, token);
        }
        await CheckApiMutationAsync(guard, token);
    }

    private async Task<JsonElement> PreviewApiAsync(PlatformApiRequest preview, IReadOnlyList<PlatformApiDescriptor> catalog, CancellationToken token)
    {
        var envelope = preview.Parameters;
        var ns = RequiredString(envelope, "namespace");
        var method = RequiredString(envelope, "method");
        if (!envelope.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidArguments", "parameters must be the proposed request object.");
        var descriptor = catalog.SingleOrDefault(x => x.Namespace == ns && x.Method == method)
            ?? throw new BridgeException("UnknownApi", "The requested API is not registered for this target.");
        if (!descriptor.MutatesProject || descriptor.Scope != EffectScope.ProjectDefinition ||
            ns is "module" or "project" or "catalog" or "plugin" or "collaboration")
            throw new BridgeException("ApiPreflightUnavailable", "Use module.validate for module mutations. Only project-definition adapter APIs support this preflight.");
        var request = new PlatformApiRequest(preview.Target, ns, method, parameters);
        var guard = ReadApiGuard(request, descriptor);
        if (HasUncertainExecution) throw new BridgeException("NativeExecutionUncertain", "Reconcile the previous write before previewing another mutation.");
        if (await PreparedApiAsync(descriptor, token) is { } prepared)
        {
            var mutation = await PrepareCheckedModuleApiAsync(prepared, request, guard, token);
            await CheckMutationAsync(mutation, token);
            var result = await adapter.ValidateAsync(mutation, token);
            if (result.Valid && await HasExternalDeleteReferencesAsync(mutation, result, token))
                result = result with { Valid = false, Issues = [new ValidationIssue("ModuleReferenced", "Other modules still reference the delete target.")] };
            return PlatformJson.Element(result);
        }
        if (adapter is not IProjectApiPreflightAdapter)
            throw new BridgeException("ApiPreflightUnavailable", "The adapter has no deterministic preflight for this API.");
        await CheckNativeApiAsync(request, guard, token);
        return PlatformJson.Element(new ModuleValidationResult(true, []));
    }
}
