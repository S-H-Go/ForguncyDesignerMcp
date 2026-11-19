namespace ForguncyDesignerMcp.Core.Platform;

public sealed record CollaborationReceiveRequest(string OperationId, ProjectTarget Target, string ExpectedProjectRevision,
    UserIdentity ExpectedUser, string RemoteUrl, string Branch, string ExpectedRemoteHead, string DestinationParent);

public sealed record CollaborationReceiveReceipt(string OperationId, string State, string Code, string Message,
    ProjectSnapshot? NewSnapshot = null, string? RemoteUrl = null, string? Branch = null, string? ObservedRemoteHead = null,
    bool NativeOpenVerified = false, bool NativeRegistryVerified = false, bool DestinationVerified = false,
    bool OriginalSourceUnchanged = false, bool ExecutionMayContinue = false, bool? NativeDispatched = null);

// The implementation MUST use a server-owned approved receiver-root policy,
// enforce a fresh non-linked destination and exact native build, and prove the
// returned snapshot from the actual newly opened document. The caller-provided
// DestinationParent is never authorization for arbitrary filesystem access.
public interface IProjectCollaborationReceiver
{
    Task<CollaborationReceiveReceipt> ReceiveAsync(CollaborationReceiveRequest request, CancellationToken cancellationToken = default);
    // Must query the live Host/backend current document on its UI dispatcher,
    // independently of the returned receipt and without reusing the old target.
    Task<ProjectSnapshot> GetCurrentSnapshotAfterReceiveAsync(CancellationToken cancellationToken = default);
}
