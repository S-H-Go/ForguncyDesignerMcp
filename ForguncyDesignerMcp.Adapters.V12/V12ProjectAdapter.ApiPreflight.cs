using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter : IProjectApiDispatchAdapter
{
    public async Task ValidateProjectApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            await OnUi(async () =>
            {
                if (uncertain) throw new BridgeException("NativeExecutionUncertain", "A previous native operation is unconfirmed.");
                ValidateOperationId(request.OperationId);
                var entry = registry.Entry(request.Namespace, request.Method);
                if (!entry.MutatesProject || entry.Scope != EffectScope.ProjectDefinition)
                    throw new BridgeException("EffectScopeDenied", "This preflight only validates registered project-definition mutations.");
                var mutation = PlatformJson.Read<PlatformApiMutationGuard>(request.Parameters.GetProperty("guard"));
                if (mutation.OperationId != request.OperationId || mutation.Target != request.Target || mutation.Scope != entry.Scope)
                    throw new BridgeException("InvalidMutationGuard", "The original API ID, target and scope must match the mutation guard.");
                var state = guard.Value.RequireWrite(request.Target, mutation.ExpectedProjectRevision, mutation.ExpectedUser,
                    allowDirty: (pending.Count != 0 || apiPending) && mutation.ExpectedProjectRevision == ownDirtyRevision);
                RequirePageLoadModuleGuard(request, mutation, state);
                if (mutation.Address is not null)
                {
                    if (mutation.ExpectedModuleRevision is null || !issued.TryGetValue(mutation.Address, out var baseline) ||
                        baseline.Revision != mutation.ExpectedModuleRevision || DateTimeOffset.UtcNow - baseline.Created > TimeSpan.FromMinutes(30) ||
                        (await ReadCore(mutation.Address, cancellationToken)).Revision != mutation.ExpectedModuleRevision)
                        throw new BridgeException("ModuleRevisionChanged", "The API module guard must identify a fresh unchanged module read.");
                }
                var parameters = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();
                parameters.Remove("guard");
                var nativeParameters = JsonSerializer.SerializeToElement(parameters);
                if (mutation.Address is not null)
                    RegistryModules.ValidateApiAddressGuard(request.Namespace, request.Method, nativeParameters, mutation.Address);
                registry.ValidateRequestParameters(request.Namespace, request.Method, nativeParameters);
                if (NativePermissionGroupGuard.IsBindingMutation(request.Namespace, request.Method))
                {
                    _ = NativePermissionGroupGuard.RequestedBindings(request.Method, nativeParameters);
                    _ = NativePermissionGroupGuard.ReadBindings(NativePermissionGroupGuard.BoundConfig(context.DesignerAssembly, NativeReflection.Get(context.NativeDocument, "ForguncySettings")!));
                }
                var capture = targetFiles.Value.CaptureApiBefore(request.Namespace, request.Method, nativeParameters);
                ValidateApiCapture(request.Namespace, request.Method, capture);
                RequirePreownedCapture(capture);
                _ = await PrepareApiOwnerAsync(capture, state, cancellationToken);
                return true;
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    private static void RequirePageLoadModuleGuard(PlatformApiRequest request, PlatformApiMutationGuard mutation, ProjectSnapshot state)
    {
        if (state.InCollaborationMode && request.Namespace == "page" && request.Method == "updatePageLoadCommands" &&
            (mutation.Address?.Kind != ModuleKind.Page || string.IsNullOrWhiteSpace(mutation.ExpectedModuleRevision)))
            throw new BridgeException("PageModuleRevisionRequired", "Read the normal page with module.read and supply its address and revision when updating collaborative page load commands.");
    }
}
