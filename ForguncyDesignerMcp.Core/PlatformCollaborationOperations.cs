using System.Text.Json;
namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    private sealed class CollaborationCorrelationException(CollaborationMutationReceipt receipt, string code = "ReceiptIdMismatch", string? detail = null) : Exception(detail ?? "Collaboration adapter changed the original operation ID.")
    { internal CollaborationMutationReceipt Receipt { get; } = receipt; internal string Code { get; } = code; }
    private async Task<JsonElement> InvokeCollaborationAsync(PlatformApiRequest request, CancellationToken token)
    {
        if (request.Method == "receive") return SubmitReceive(request);
        if (request.Method == "status") return await SerializedAsync(async () =>
        {
            var snapshot = await SnapshotAsync(request.Target, token);
            if (!snapshot.InCollaborationMode) return PlatformJson.Element(new CollaborationStatus(Target, false, snapshot.CurrentUser, snapshot.Revision, []));
            var status = await CollaborationAdapter().GetCollaborationStatusAsync(token);
            if (!SameTarget(status.Target, Target)) throw new BridgeException("TargetMismatch", "Collaboration status target differs.");
            return PlatformJson.Element(status);
        }, token);
        if (request.Method is not ("checkout" or "checkin")) throw new BridgeException("UnknownApi", "Unknown collaboration method.");
        var mutation = PlatformJson.Read<CollaborationMutationRequest>(request.Parameters);
        CheckTarget(mutation.Target); CheckOperationId(request.OperationId, mutation.OperationId);
        if (mutation.ExpectedUser is null || string.IsNullOrWhiteSpace(mutation.ExpectedUser.UserName) || string.IsNullOrWhiteSpace(mutation.ExpectedUser.Email) ||
            string.IsNullOrWhiteSpace(mutation.ExpectedProjectRevision) || mutation.Modules is null || mutation.Modules.Count == 0 || mutation.Modules.Distinct().Count() != mutation.Modules.Count)
            throw new BridgeException("InvalidArguments", "Exact identity, project baseline and unique nonempty module set are required.");
        foreach (var address in mutation.Modules) ValidateAddress(address);
        if (request.Method == "checkin" && string.IsNullOrWhiteSpace(mutation.Comment)) throw new BridgeException("CommentRequired", "Check-in requires a comment.");
        if (request.Method == "checkin" && (string.IsNullOrWhiteSpace(mutation.ExpectedRemoteUrl) || string.IsNullOrWhiteSpace(mutation.ExpectedBranch) || string.IsNullOrWhiteSpace(mutation.ExpectedRemoteHead)))
            throw new BridgeException("RemoteBaselineRequired", "Check-in requires the exact remote URL, branch and remote head from fresh status.");
        return Submit(request, mutation.OperationId, EffectScope.ProjectDefinition, async started =>
        {
            var snapshot = await SnapshotAsync(mutation.Target, default);
            if (!snapshot.InCollaborationMode) throw new BridgeException("CollaborationNotApplicable", "Standalone projects cannot checkout or checkin.");
            if (snapshot.Busy) throw new BridgeException("DesignerBusy", "Designer is busy.");
            if (snapshot.Revision != mutation.ExpectedProjectRevision) throw new BridgeException("BaselineChanged", "Project revision changed.");
            if (snapshot.CurrentUser != mutation.ExpectedUser) throw new BridgeException("IdentityChanged", "Collaboration identity changed.");
            var native = CollaborationAdapter();
            var before = await native.GetCollaborationStatusAsync();
            if (!SameTarget(before.Target, Target) || !before.InCollaborationMode || before.CurrentUser != mutation.ExpectedUser || before.Revision != mutation.ExpectedProjectRevision)
                throw new BridgeException("CollaborationBaselineChanged", "Fresh native collaboration status differs from the reviewed target/identity/revision.");
            if (request.Method == "checkin" && UniqueModuleIdentities(before.Modules) &&
                mutation.Modules.Any(address => before.Modules.All(module => module.Address != address)) &&
                await InspectCancelledNewAsync(native, before, mutation) is { } cancellation)
                return cancellation;
            if (!UniqueModuleIdentities(before.Modules) || mutation.Modules.Any(address => before.Modules.Count(x => x.Address == address) != 1))
                throw new BridgeException("CollaborationModuleIdentityInvalid", "Reviewed modules must have unique address/native identities.");
            if (request.Method == "checkin" && mutation.Modules.Any(address => !before.Modules.Any(module =>
                module.Address == address && module.OwnedByCurrentUser && module.LockedBy == mutation.ExpectedUser)))
                throw new BridgeException("CollaborationOwnershipRequired", "Check-in requires every selected native module lock to belong to the current reviewed user.");
            if ((mutation.ExpectedRemoteUrl is not null && before.Git?.RemoteUrl != mutation.ExpectedRemoteUrl) ||
                (mutation.ExpectedBranch is not null && before.Git?.Branch != mutation.ExpectedBranch) ||
                (mutation.ExpectedRemoteHead is not null && before.Git?.UpstreamHead != mutation.ExpectedRemoteHead))
                throw new BridgeException("RemoteBaselineChanged", "Remote URL, branch or head changed; no native mutation was dispatched.");
            var fresh = await SnapshotAsync(mutation.Target, default);
            if (fresh.Revision != mutation.ExpectedProjectRevision || fresh.CurrentUser != mutation.ExpectedUser || fresh.Busy)
                throw new BridgeException("BaselineChanged", "Project changed during collaboration validation.");
            started();
            var receipt = request.Method == "checkout" ? await native.CheckoutAsync(mutation) : await native.CheckinAsync(mutation);
            if (receipt.OperationId != mutation.OperationId) throw new CollaborationCorrelationException(receipt);
            ProjectSnapshot after;
            try { after = await SnapshotAsync(mutation.Target, default); }
            catch (Exception error) { throw new CollaborationCorrelationException(receipt, "CollaborationPostReadFailed", error.Message); }
            var verifiedNoop = request.Method == "checkout" && receipt.Code == "AlreadyOwned" && receipt.NativeAccepted is null && !receipt.ExecutionMayContinue &&
                before.Revision == mutation.ExpectedProjectRevision && after.Revision == mutation.ExpectedProjectRevision && receipt.Status?.Revision == mutation.ExpectedProjectRevision &&
                mutation.Modules.All(address => before.Modules.Any(x => x.Address == address && !x.PendingDeletion && x.OwnedByCurrentUser && x.LockedBy == mutation.ExpectedUser));
            var verified = receipt.OperationId == mutation.OperationId && (receipt.NativeAccepted == true || verifiedNoop) && receipt.LockStateVerified &&
                receipt.Status is { InCollaborationMode: true } status && SameTarget(status.Target, Target) && status.CurrentUser == mutation.ExpectedUser && after.CurrentUser == mutation.ExpectedUser && status.Revision == after.Revision &&
                UniqueModuleIdentities(status.Modules) &&
                mutation.Modules.All(address => RequestedIdentityVerified(before, status, address, request.Method)) &&
                mutation.Modules.All(address => request.Method == "checkout"
                    ? status.Modules.Count(module => module.Address == address && module.OwnedByCurrentUser && module.LockedBy == mutation.ExpectedUser) == 1
                    : !status.Modules.Any(module => module.Address == address && (module.OwnedByCurrentUser || module.LockedBy is not null))) &&
                UnrelatedLocksUnchanged(before, status, mutation.Modules) &&
                (request.Method != "checkin" || receipt.SourceVerified && !string.IsNullOrWhiteSpace(receipt.CommitId) && receipt.RemoteSynchronized == true &&
                    status.Git?.RemoteUrl == mutation.ExpectedRemoteUrl && status.Git?.Branch == mutation.ExpectedBranch &&
                    status.Git?.Head == receipt.CommitId && status.Git?.UpstreamHead == receipt.CommitId &&
                    status.Git?.Author == mutation.ExpectedUser && status.Git?.Committer == mutation.ExpectedUser);
            if (receipt.State is "succeeded" or "Succeeded" && !verified)
                receipt = receipt with { State = "verification_failed", Code = "CollaborationUnconfirmed", Message = "Native collaboration evidence is incomplete." };
            if (receipt.State == "Succeeded" && verified) receipt = receipt with { State = "succeeded" };
            if (receipt.OperationId != mutation.OperationId || receipt.ExecutionMayContinue || receipt.State == "verification_failed") executionUncertain = true;
            return PlatformJson.Element(receipt);
        });
    }
    private IProjectCollaborationAdapter CollaborationAdapter() => adapter as IProjectCollaborationAdapter ??
        throw new BridgeException("CollaborationUnavailable", "This exact adapter does not implement collaboration.");
    private static bool UnrelatedLocksUnchanged(CollaborationStatus before, CollaborationStatus after, IReadOnlyList<ModuleAddress> requested)
    {
        var old = before.Modules.Where(x => !requested.Contains(x.Address)).ToArray();
        var current = after.Modules.Where(x => !requested.Contains(x.Address)).ToArray();
        return old.Length == current.Length && old.All(x => current.Count(y => y.Address == x.Address &&
            y.NativeModuleName == x.NativeModuleName && y.LockedBy == x.LockedBy && y.OwnedByCurrentUser == x.OwnedByCurrentUser) == 1);
    }
    private static bool UniqueModuleIdentities(IReadOnlyList<CollaborationModuleState> modules) =>
        modules.All(x => x.Address is not null && !string.IsNullOrWhiteSpace(x.NativeModuleName)) &&
        modules.Select(x => x.Address).Distinct().Count() == modules.Count &&
        modules.Select(x => x.NativeModuleName).Distinct(StringComparer.Ordinal).Count() == modules.Count;
    private static bool RequestedIdentityVerified(CollaborationStatus before, CollaborationStatus after, ModuleAddress address, string method)
    {
        var original = before.Modules.Single(x => x.Address == address);
        var observed = after.Modules.Where(x => x.Address == address).ToArray();
        if (observed.Length == 0) return method == "checkin" && original.PendingDeletion &&
            !after.Modules.Any(x => x.NativeModuleName == original.NativeModuleName);
        return observed.Length == 1 && observed[0].NativeModuleName == original.NativeModuleName;
    }
}
