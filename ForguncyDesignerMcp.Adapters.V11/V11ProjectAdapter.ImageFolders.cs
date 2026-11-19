using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter
{
    private static bool IsImageFolderUpdate(ModuleMutationRequest request) =>
        NativeV11ImageFolderLifecycle.Supports(request.Address) && request.Operation == ModuleOperation.Update;
    private bool IsImageFolderGroupedDelete(ModuleMutationRequest request) =>
        NativeV11ImageFolderLifecycle.Supports(request.Address) && request.Operation == ModuleOperation.Delete &&
        NativeV11ImageFolderLifecycle.ExistingAddresses(session, request.Address).Count > 1;

    private NativeV11ImageFolderLifecycle.UpdatePlan PrepareImageFolderUpdate(ModuleMutationRequest request)
    {
        if (!IsImageFolderUpdate(request)) throw new BridgeException("ImageFolderScopeUnavailable", "需要图片目录更新请求。");
        if (!context.CaptureSnapshot().InCollaborationMode) return NativeV11ImageFolderLifecycle.PrepareUpdate(session, request, null);
        var addresses = NativeV11ImageFolderLifecycle.ExistingAddresses(session, request.Address);
        var registered = NativeV11CollaborationOwners.Resolve(session, addresses);
        var closure = NativeV11CollaborationOwners.ExpandExistingOwners(session, registered);
        if (closure.Count != registered.Count)
            throw new BridgeException("ImageFolderSharedOwner", "图片目录文件组包含其他原生 owner，不能仅修改该目录。");
        return NativeV11ImageFolderLifecycle.PrepareUpdate(session, request, registered);
    }

    private NativeV11Collaboration.Plan BuildImageFolderPlan(ModuleMutationRequest request, NativeV11ImageFolderLifecycle.UpdatePlan plan) =>
        NativeV11Collaboration.CreatePlan(session, plan.Owners.Owners,
            request.ExpectedUser ?? throw new BridgeException("CurrentUserMismatch", "协同写入需要当前用户身份。"),
            mutation: request, reviewedFutureGroups: plan.Owners.FutureGroups, reviewedSharedFiles: plan.Journal.OldFiles);

    private NativeV11ImageFolderLifecycle.DeletePlan PrepareImageFolderDelete(ModuleMutationRequest request)
    {
        if (!IsImageFolderGroupedDelete(request)) throw new BridgeException("ImageFolderScopeUnavailable", "需要非空图片目录删除请求。");
        if (!context.CaptureSnapshot().InCollaborationMode) return NativeV11ImageFolderLifecycle.PrepareDelete(session, request, null);
        var registered = NativeV11CollaborationOwners.Resolve(session, NativeV11ImageFolderLifecycle.ExistingAddresses(session, request.Address));
        var closure = NativeV11CollaborationOwners.ExpandExistingOwners(session, registered);
        if (closure.Count != registered.Count) throw new BridgeException("ImageFolderSharedOwner", "图片目录文件组包含其他原生 owner。");
        return NativeV11ImageFolderLifecycle.PrepareDelete(session, request, registered);
    }

    private NativeV11Collaboration.Plan BuildImageFolderDeletePlan(ModuleMutationRequest request, NativeV11ImageFolderLifecycle.DeletePlan plan) =>
        NativeV11Collaboration.CreatePlan(session, plan.Owners.Owners,
            request.ExpectedUser ?? throw new BridgeException("CurrentUserMismatch", "协同写入需要当前用户身份。"),
            mutation: request, reviewedFutureGroups: plan.Owners.FutureGroups, reviewedSharedFiles: plan.Journal.OldFiles);
}
