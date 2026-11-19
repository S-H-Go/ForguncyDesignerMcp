using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter:IProjectRuntimeApiAdapter
{
    public Task ValidateRuntimeApiAsync(PlatformApiRequest request,CancellationToken cancellationToken=default)=>
        request.Namespace=="command" && request.Method=="listTypes"?OnUi(()=>
        {
            _=ValidateCommandTypeContext(request);
            return Task.FromResult(true);
        },cancellationToken):ValidateFrontendRuntimeApiAsync(request,cancellationToken);

    public Task<RuntimeApiReceipt> InvokeRuntimeApiAsync(PlatformApiRequest request,CancellationToken cancellationToken=default)=>
        request.Namespace=="command" && request.Method=="listTypes"?InvokeCommandTypesAsync(request,cancellationToken):
        InvokeFrontendRuntimeApiAsync(request,cancellationToken);

    private (PlatformApiMutationGuard Guard,ProjectSnapshot Before,JsonElement Parameters) ValidateCommandTypeContext(PlatformApiRequest request)
    {
        ValidateOperationId(request.OperationId);
        if(uncertain || pending.Count!=0 || apiPending || pendingAccountExport is not null)
            throw new BridgeException("CommandMetadataPendingWork","Complete pending project work before initializing native command metadata.");
        if(request.Parameters.ValueKind!=JsonValueKind.Object || !request.Parameters.TryGetProperty("guard",out var input))
            throw new BridgeException("MutationGuardRequired","Command metadata initialization requires the runtime mutation guard.");
        var mutation=input.Deserialize<PlatformApiMutationGuard>(GuardJson) ?? throw new BridgeException("InvalidMutationGuard","The runtime guard is unavailable.");
        if(mutation.Target!=request.Target || mutation.OperationId!=request.OperationId || mutation.Scope!=EffectScope.RuntimeDeployment ||
            mutation.Address is not null || mutation.ExpectedModuleRevision is not null || mutation.ExpectedServiceBinding is not null)
            throw new BridgeException("RuntimeApiGuardMismatch","Native command metadata requires an exact runtime target, revision and operation ID.");
        guard.Value.CheckTarget(request.Target);
        var before=guard.Value.Capture();
        if(before.Revision!=mutation.ExpectedProjectRevision || mutation.ExpectedUser is not null && before.CurrentUser!=mutation.ExpectedUser)
            throw new BridgeException("RuntimeApiGuardMismatch","The reviewed command metadata context changed.");
        // This runtime operation initializes metadata, without editing or
        // saving the project. Leave read/license permission to native aao;
        // a document's save-only demo restriction does not govern this read.
        if(before.Busy || before.HasUnsavedChanges) throw new BridgeException("RuntimeApiCleanProjectRequired","Initialize native command metadata in a clean, idle project before starting edits.");
        var parameters=JsonNode.Parse(request.Parameters.GetRawText())!.AsObject();parameters.Remove("guard");
        var native=JsonSerializer.SerializeToElement(parameters);
        registry.ValidateRequestParameters("command","listTypes",native);
        NativeCommandTypes.ValidateParameters(native);
        _=NativeCommandTypes.HasInitializedRegistryEvidence(context.DesignerAssembly);
        return(mutation,before,native);
    }

    private async Task<RuntimeApiReceipt> InvokeCommandTypesAsync(PlatformApiRequest request,CancellationToken cancellationToken)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            return await OnUi<RuntimeApiReceipt>(async()=>
            {
                var (_,before,parameters)=ValidateCommandTypeContext(request);
                if(startedApiOperations.Contains(request.OperationId!))
                    throw new BridgeException("OperationAlreadyStarted","Query the original native command discovery receipt instead of replaying it.");
                var source=NativeProjectGuard.SourceRevision(request.Target.ProjectPath,includeCollaboration:false);
                var initialized=NativeCommandTypes.HasInitializedRegistryEvidence(context.DesignerAssembly);
                cancellationToken.ThrowIfCancellationRequested();
                startedApiOperations.Add(request.OperationId!);
                try
                {
                    var response=await NativeCommandTypes.ListAsync(registry,parameters,CancellationToken.None);
                    var after=guard.Value.Capture();
                    if(response.ValueKind!=JsonValueKind.Array || !NativeCommandTypes.HasInitializedRegistryEvidence(context.DesignerAssembly) ||
                        after.Target!=before.Target || after.Busy || after.HasUnsavedChanges || after.CurrentUser!=before.CurrentUser ||
                        after.InCollaborationMode!=before.InCollaborationMode ||
                        NativeProjectGuard.SourceRevision(request.Target.ProjectPath,includeCollaboration:false)!=source)
                        throw new BridgeException("CommandMetadataReadbackFailed","Native command discovery did not preserve its reviewed project context.");
                    return new(request.OperationId!,"native_accepted","NativeCommandMetadataRead",
                        "The native command registry returned its current command type metadata.",true,true,false,false,true,false,false,false,response,
                        initialized?"Existing native command metadata was read; no project save or runtime activation is asserted.":
                        "Native command and existing-plugin registries may have initialized. No business command or plugin installation was requested; no project save or runtime activation is asserted.");
                }
                catch
                {
                    uncertain=true;
                    return new(request.OperationId!,"verification_failed","NativeCommandMetadataUnconfirmed",
                        "Native metadata initialization was dispatched but its final context could not be confirmed; do not replay it.",
                        null,false,false,false,!guard.Value.Capture().HasUnsavedChanges,false,false,false,null,
                        "Native registry initialization may have loaded existing plugins; inspect the original result and current context.");
                }
            },cancellationToken);
        }
        finally{writer.Release();}
    }
}
