namespace ForguncyDesignerMcp.Core.Platform;

public interface IProjectCollaborationCancellationAdapter
{
    // Reads a same-session native New->Remove journal and saved source absence.
    // Must never checkout, save, checkin or otherwise mutate the native project.
    Task<CollaborationMutationReceipt?> InspectCancelledNewAsync(CollaborationMutationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed partial class PlatformProjectRouter
{
    private async Task<System.Text.Json.JsonElement?> InspectCancelledNewAsync(IProjectCollaborationAdapter native,
        CollaborationStatus before, CollaborationMutationRequest request)
    {
        if (native is not IProjectCollaborationCancellationAdapter inspector || request.Modules.Count != 1 ||
            before.Modules.Any(module => module.Address == request.Modules[0])) return null;
        if (before.Git is null || before.Git.RemoteUrl != request.ExpectedRemoteUrl || before.Git.Branch != request.ExpectedBranch ||
            before.Git.UpstreamHead != request.ExpectedRemoteHead || before.Git.Head != before.Git.UpstreamHead || !before.Git.IndexClean)
            throw new BridgeException("RemoteBaselineChanged", "Cancellation inspection requires the unchanged synchronized native Git baseline.");
        var receipt = await inspector.InspectCancelledNewAsync(request, default);
        if (receipt is null) return null;
        var after = await native.GetCollaborationStatusAsync(default);
        var snapshot = await SnapshotAsync(request.Target, default);
        bool SameModules(IReadOnlyList<CollaborationModuleState> left, IReadOnlyList<CollaborationModuleState> right) =>
            left.Count == right.Count && left.All(module => right.Count(other => other == module) == 1);
        bool SameStatus(CollaborationStatus status) => status.Target == before.Target && status.InCollaborationMode &&
            status.CurrentUser == before.CurrentUser && status.Revision == before.Revision && SameModules(before.Modules, status.Modules) &&
            status.Git is { IndexClean: true } git && git.Head == before.Git.Head && git.UpstreamHead == before.Git.UpstreamHead &&
            git.RemoteUrl == before.Git.RemoteUrl && git.Branch == before.Git.Branch && git.WorktreeHash == before.Git.WorktreeHash;
        if (receipt.OperationId != request.OperationId || receipt.State != "cancelled" || receipt.Code != "NewModuleCancelled" ||
            receipt.NativeAccepted == true || !receipt.SourceVerified || !receipt.LockStateVerified || receipt.CommitId is not null ||
            receipt.ExecutionMayContinue || receipt.RemoteSynchronized != true || receipt.Status is null || !SameStatus(receipt.Status) || !SameStatus(after) ||
            snapshot.Target != request.Target || snapshot.Revision != request.ExpectedProjectRevision || snapshot.CurrentUser != request.ExpectedUser ||
            snapshot.HasUnsavedChanges || snapshot.Busy)
            throw new BridgeException("CancellationEvidenceUnverified", "Saved cancellation evidence or the unchanged native context could not be verified; no check-in was dispatched.");
        return PlatformJson.Element(receipt);
    }
}
