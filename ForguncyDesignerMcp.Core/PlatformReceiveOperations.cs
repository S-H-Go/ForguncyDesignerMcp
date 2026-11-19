using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    private sealed class ReceiveEvidenceException(string code, string message, JsonElement? evidence, bool mayContinue) : Exception(message)
    {
        internal string Code { get; } = code;
        internal JsonElement? Evidence { get; } = evidence;
        internal bool MayContinue { get; } = mayContinue;
    }

    private JsonElement SubmitReceive(PlatformApiRequest request)
    {
        var receive = PlatformJson.Read<CollaborationReceiveRequest>(request.Parameters);
        CheckTarget(receive.Target); CheckOperationId(request.OperationId, receive.OperationId);
        if (string.IsNullOrWhiteSpace(receive.ExpectedProjectRevision) || receive.ExpectedUser is null ||
            string.IsNullOrWhiteSpace(receive.ExpectedUser.UserName) || string.IsNullOrWhiteSpace(receive.ExpectedUser.Email) ||
            string.IsNullOrWhiteSpace(receive.Branch) || receive.Branch.Any(char.IsWhiteSpace) || receive.Branch.Contains("..", StringComparison.Ordinal) ||
            receive.ExpectedRemoteHead is null || receive.ExpectedRemoteHead.Length is not (40 or 64) || !receive.ExpectedRemoteHead.All(Uri.IsHexDigit) ||
            !Uri.TryCreate(receive.RemoteUrl, UriKind.Absolute, out var remote) || remote.Scheme is not ("file" or "https" or "ssh") || !string.IsNullOrEmpty(remote.UserInfo) ||
            string.IsNullOrWhiteSpace(receive.DestinationParent) || !Path.IsPathFullyQualified(receive.DestinationParent))
            throw new BridgeException("InvalidReceiveArguments", "An exact target, revision, identity, credential-free remote URI, branch, commit and absolute fresh destination are required.");
        var destination = Path.GetFullPath(receive.DestinationParent);
        return Submit(request, receive.OperationId, EffectScope.ProjectDefinition, async started =>
        {
            var snapshot = await SnapshotAsync(receive.Target, default);
            if (snapshot.InCollaborationMode || snapshot.Busy || snapshot.HasUnsavedChanges)
                throw new BridgeException("ReceiveRequiresCleanStandalone", "Receive requires a clean, idle standalone starting document.");
            if (snapshot.Revision != receive.ExpectedProjectRevision) throw new BridgeException("BaselineChanged", "Starting project revision changed.");
            if (adapter is not IProjectCollaborationReceiver native) throw new BridgeException("ReceiveUnavailable", "This exact adapter does not provide a policy-bound native receiver.");
            if (Directory.Exists(destination) || File.Exists(destination)) throw new BridgeException("ReceiveDestinationExists", "Receiver destination must be fresh.");
            for (var path = destination; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("ReceiveDestinationLinked", "Receiver destination cannot traverse a filesystem link.");
            started();
            CollaborationReceiveReceipt receipt;
            try { receipt = await native.ReceiveAsync(receive, default); }
            catch (Exception error) { throw new ReceiveEvidenceException("ReceiveOutcomeUnconfirmed", error.Message, null, true); }
            var evidence = PlatformJson.Element(receipt);
            if (receipt.OperationId != receive.OperationId) throw new ReceiveEvidenceException("ReceiveReceiptIdMismatch", "The adapter returned another operation ID; evidence is retained without replay.", evidence, receipt.ExecutionMayContinue);
            if (receipt.NativeDispatched == false && receipt.State == "failed" && !receipt.ExecutionMayContinue)
            {
                try { var unchanged = await SnapshotAsync(receive.Target, default); if (unchanged != snapshot) throw new InvalidOperationException("Starting snapshot changed."); }
                catch (Exception error) { throw new ReceiveEvidenceException("ReceiveOutcomeUnconfirmed", error.Message, evidence, false); }
                return evidence;
            }
            var after = receipt.NewSnapshot;
            var target = after?.Target;
            var valid = receipt.State == "succeeded" && receipt.NativeDispatched == true && !receipt.ExecutionMayContinue &&
                receipt.NativeOpenVerified && receipt.NativeRegistryVerified && receipt.DestinationVerified && receipt.OriginalSourceUnchanged &&
                receipt.RemoteUrl == receive.RemoteUrl && receipt.Branch == receive.Branch && string.Equals(receipt.ObservedRemoteHead, receive.ExpectedRemoteHead, StringComparison.OrdinalIgnoreCase) &&
                target is not null && target.InstanceId == Target.InstanceId && target.DesignerVersion == Target.DesignerVersion &&
                !string.IsNullOrWhiteSpace(target.SessionId) && target.SessionId != Target.SessionId && Path.IsPathFullyQualified(target.ProjectPath) &&
                Path.GetFullPath(target.ProjectPath).StartsWith(Path.TrimEndingDirectorySeparator(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                after is { InCollaborationMode: true, Busy: false, HasUnsavedChanges: false } && after.CurrentUser == receive.ExpectedUser && !string.IsNullOrWhiteSpace(after.Revision);
            if (!valid) throw new ReceiveEvidenceException("ReceiveOutcomeUnconfirmed", "Native receive returned incomplete or inconsistent evidence; preserve the original receipt and do not replay.", evidence, receipt.ExecutionMayContinue);
            ProjectSnapshot actual;
            try { actual = await native.GetCurrentSnapshotAfterReceiveAsync(default); }
            catch (Exception error) { throw new ReceiveEvidenceException("ReceiveOutcomeUnconfirmed", "The new live document could not be independently read: " + error.Message, evidence, false); }
            if (actual != after) throw new ReceiveEvidenceException("ReceiveOutcomeUnconfirmed", "The independently observed live document differs from the reported new snapshot.", evidence, false);
            invalidated = true; // Old router remains available for original-ID receipt queries only.
            return evidence;
        });
    }
}
