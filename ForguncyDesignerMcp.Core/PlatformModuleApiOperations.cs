namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    private async Task<System.Text.Json.JsonElement> InvokeModuleApiAsync(IProjectModuleApiAdapter prepared,
        PlatformApiRequest request, PlatformApiMutationGuard guard, Action started)
    {
        var mutation = await PrepareCheckedModuleApiAsync(prepared, request, guard, default);
        return await ApplyAsync(mutation, started);
    }

    private async Task<ModuleMutationRequest> PrepareCheckedModuleApiAsync(IProjectModuleApiAdapter prepared,
        PlatformApiRequest request, PlatformApiMutationGuard guard, CancellationToken token)
    {
        // ReadAsync can renew a page baseline. Test the original lease before any such read.
        if (prepared is V11PagePreparedAdapter)
        {
            if (adapter is not IProjectPageBaselineGuardAdapter pages || guard.Address is null || string.IsNullOrWhiteSpace(guard.ExpectedModuleRevision))
                throw new BridgeException("PageBaselineGuardUnavailable", "A fresh page module baseline is required.");
            await pages.RequireFreshPageBaselineAsync(guard.Target, guard.Address, guard.ExpectedModuleRevision, token);
        }
        await CheckApiMutationAsync(guard, token);
        var mutation = await prepared.PrepareModuleApiAsync(request with { OperationId = guard.OperationId }, token);
        if (mutation.OperationId != guard.OperationId || mutation.Target != guard.Target ||
            mutation.ExpectedProjectRevision != guard.ExpectedProjectRevision ||
            mutation.ExpectedUser != guard.ExpectedUser || mutation.Scope != guard.Scope ||
            mutation.ExpectedServiceBinding != guard.ExpectedServiceBinding ||
            mutation.ExpectedModuleRevision != guard.ExpectedModuleRevision ||
            guard.Address is not null && mutation.Address != guard.Address)
            throw new ForguncyDesignerMcp.Core.BridgeException("PreparedMutationGuardMismatch",
                "The detached module mutation differs from the original reviewed API guard.");
        ValidateMutationShape(mutation);
        await CheckApiMutationAsync(guard, token);
        return mutation;
    }
}
