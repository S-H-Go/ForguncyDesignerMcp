using System.Text.Json;
namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    private async Task<JsonElement> InvokeNativeServiceAsync(IProjectNativeServiceApiAdapter native,
        PlatformApiRequest request, PlatformApiMutationGuard guard, Action started)
    {
        await CheckApiMutationAsync(guard, default);
        await native.ValidateNativeServiceApiAsync(request with { OperationId = guard.OperationId }, default);
        await CheckApiMutationAsync(guard, default);
        var before = await SnapshotAsync(request.Target, default);
        if (before.Revision != guard.ExpectedProjectRevision || before.Busy || before.CurrentUser != guard.ExpectedUser ||
            before.HasUnsavedChanges && before.Revision != ownedDirtyRevision)
            throw new BridgeException("BaselineChanged", "The reviewed native service context changed before dispatch.");
        started();
        NativeServiceApiReceipt receipt;
        try { receipt = await native.InvokeNativeServiceApiAsync(request with { OperationId = guard.OperationId }, default); }
        catch { throw new NativeServiceUncertainException("NativeServiceOutcomeUnconfirmed", null, true); }
        if (receipt is null) throw new NativeServiceUncertainException("NativeServiceOutcomeUnconfirmed", null, true);
        var evidence = PlatformJson.Element(receipt);
        if (receipt.OperationId != guard.OperationId)
            throw new NativeServiceUncertainException("NativeServiceReceiptIdMismatch", evidence, receipt.ExecutionMayContinue);
        // This limited route does not have the independently verified service
        // transaction or source export evidence used by typed module mutations.
        if (receipt.PersistenceVerified || receipt.SourceExportVerified || receipt.ExecutionMayContinue ||
            receipt.NativeAccepted is null || string.IsNullOrWhiteSpace(receipt.Code) || string.IsNullOrWhiteSpace(receipt.Message) ||
            receipt.NativeAccepted == true && (receipt.State != "native_accepted" ||
                !receipt.NativeReadbackVerified && string.IsNullOrWhiteSpace(receipt.Limitation)) ||
            receipt.NativeAccepted == false && (receipt.State != "failed" || receipt.NativeReadbackVerified))
            throw new NativeServiceUncertainException("NativeServiceOutcomeUnconfirmed", evidence, receipt.ExecutionMayContinue);
        ProjectSnapshot after;
        try { after = await SnapshotAsync(request.Target, default); }
        catch { throw new NativeServiceUncertainException("NativeServiceOutcomeUnconfirmed", evidence, false); }
        if (after.Busy || after.InCollaborationMode != before.InCollaborationMode || after.CurrentUser != before.CurrentUser ||
            receipt.NativeAccepted == false && (after.Revision != before.Revision || after.HasUnsavedChanges != before.HasUnsavedChanges))
            throw new NativeServiceUncertainException("NativeServiceOutcomeUnconfirmed", evidence, false);
        if (receipt.NativeAccepted == true)
            ownedDirtyRevision = after.HasUnsavedChanges ? after.Revision : null;
        return evidence;
    }

    private sealed class NativeServiceUncertainException(string code, JsonElement? evidence, bool mayContinue) : Exception
    {
        internal string Code { get; } = code;
        internal JsonElement? Evidence { get; } = evidence;
        internal bool MayContinue { get; } = mayContinue;
    }
}
