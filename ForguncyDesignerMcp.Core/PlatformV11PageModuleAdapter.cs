namespace ForguncyDesignerMcp.Core.Platform;

/// <summary>Checks the existing page-read token without reading or refreshing it.</summary>
public interface IProjectPageBaselineGuardAdapter
{
    Task RequireFreshPageBaselineAsync(ProjectTarget target,ModuleAddress address,string revision,CancellationToken token=default);
}

public sealed partial class PlatformProjectRouter
{
    // Prepares a detached mutation; all guard rechecks and writes remain in InvokeModuleApiAsync/ApplyAsync.
    private sealed class V11PagePreparedAdapter(IProjectAdapter adapter) : IProjectModuleApiAdapter
    {
        public bool SupportsModuleApi(string ns,string method)=>ns=="page" && method=="updateCells";
        public async Task<ModuleMutationRequest> PrepareModuleApiAsync(PlatformApiRequest request,CancellationToken token=default)
        {
            if(!SupportsModuleApi(request.Namespace,request.Method))throw new NotSupportedException("Unsupported page module API.");
            var guard=PlatformJson.Read<PlatformApiMutationGuard>(request.Parameters.GetProperty("guard"));
            var cell=PlatformV11CellWrites.ReadInput(request.Parameters,guard);
            var document=await adapter.InspectModuleAsync(request.Target,guard.Address!,token);
            var prepared=PlatformV11CellWrites.PrepareValueUpdate(guard,document,cell);
            if(adapter is not IProjectPageProjectionAdapter projector)throw new InvalidOperationException("Native detached page projection is unavailable.");
            var projected=await projector.ProjectPageDocumentAsync(request.Target,prepared.Document!,token);
            if(projected.Address!=document.Address || projected.Revision!=document.Revision)throw new InvalidOperationException("Detached page projection changed address or baseline.");
            return prepared with {Document=projected};
        }
    }
}
