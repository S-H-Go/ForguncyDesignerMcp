namespace ForguncyDesignerMcp.Core.Platform;

// Optional cross-version collaboration capability. Standalone projects report
// InCollaborationMode=false; they never acquire repository locks or commit.
public sealed record CollaborationModuleState(ModuleAddress Address, string NativeModuleName,
    int? NativeState, UserIdentity? LockedBy, bool OwnedByCurrentUser,
    bool PendingDeletion = false);
public sealed record CollaborationStatus(ProjectTarget Target, bool InCollaborationMode,
    UserIdentity? CurrentUser, string Revision, IReadOnlyList<CollaborationModuleState> Modules,
    GitState? Git = null);
public sealed record CollaborationMutationRequest(string OperationId, ProjectTarget Target,
    string ExpectedProjectRevision, UserIdentity ExpectedUser,
    IReadOnlyList<ModuleAddress> Modules, string? Comment = null,
    string? ExpectedRemoteUrl = null, string? ExpectedBranch = null,
    string? ExpectedRemoteHead = null);
public sealed record CollaborationMutationReceipt(string OperationId, string State, string Code,
    string Message, CollaborationStatus? Status = null, bool? NativeAccepted = null,
    bool LockStateVerified = false, bool SourceVerified = false, string? CommitId = null,
    bool? RemoteSynchronized = null, string? EvidenceDirectory = null,
    bool ExecutionMayContinue = false)
{
    public bool? NativeSaveAccepted { get; init; }
}

public interface IProjectCollaborationAdapter
{
    Task<CollaborationStatus> GetCollaborationStatusAsync(CancellationToken cancellationToken = default);
    Task<CollaborationMutationReceipt> CheckoutAsync(CollaborationMutationRequest request,
        CancellationToken cancellationToken = default);
    Task<CollaborationMutationReceipt> CheckinAsync(CollaborationMutationRequest request,
        CancellationToken cancellationToken = default);
}
