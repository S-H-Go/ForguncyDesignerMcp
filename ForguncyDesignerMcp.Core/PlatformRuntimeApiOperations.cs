using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    private sealed class RuntimeApiEvidenceException(string code,JsonElement? evidence,bool mayContinue):Exception
    {
        internal string Code{get;}=code;
        internal JsonElement? Evidence{get;}=evidence;
        internal bool MayContinue{get;}=mayContinue;
    }

    private async Task<JsonElement> InvokeRuntimeApiAsync(IProjectRuntimeApiAdapter native,PlatformApiRequest request,
        PlatformApiMutationGuard guard,Action started)
    {
        if(guard.Address is not null || guard.ExpectedModuleRevision is not null || guard.ExpectedServiceBinding is not null)
            throw new BridgeException("RuntimeApiGuardMismatch","Mixed runtime lifecycle uses its native package identity and exact project guard.");
        await CheckApiMutationAsync(guard,default);
        var before=await SnapshotAsync(request.Target,default);
        if(before.HasUnsavedChanges) throw new BridgeException("RuntimeApiCleanProjectRequired","Complete pending project work before a mixed runtime deployment.");
        await native.ValidateRuntimeApiAsync(request with{OperationId=guard.OperationId},default);
        await CheckApiMutationAsync(guard,default);
        if(await SnapshotAsync(request.Target,default)!=before)
            throw new BridgeException("BaselineChanged","Runtime lifecycle validation changed the reviewed project context.");
        started();
        RuntimeApiReceipt receipt;
        try{receipt=await native.InvokeRuntimeApiAsync(request with{OperationId=guard.OperationId},default);}
        catch{throw new RuntimeApiEvidenceException("RuntimeApiOutcomeUnconfirmed",null,true);}
        var evidence=PlatformJson.Element(receipt);
        if(receipt is null || receipt.OperationId!=guard.OperationId)
            throw new RuntimeApiEvidenceException("RuntimeApiReceiptIdMismatch",evidence,receipt?.ExecutionMayContinue??true);
        if(receipt.NativeAccepted is null || receipt.ExecutionMayContinue || receipt.State=="verification_failed")
            throw new RuntimeApiEvidenceException("RuntimeApiOutcomeUnconfirmed",evidence,receipt.ExecutionMayContinue);
        ProjectSnapshot after;
        try{after=await SnapshotAsync(request.Target,default);}
        catch{throw new RuntimeApiEvidenceException("RuntimeApiOutcomeUnconfirmed",evidence,false);}
        if(after.Busy || after.CurrentUser!=before.CurrentUser || after.InCollaborationMode!=before.InCollaborationMode ||
            string.IsNullOrWhiteSpace(receipt.Code) || string.IsNullOrWhiteSpace(receipt.Message) ||
            receipt.ProjectDirtyFalse==after.HasUnsavedChanges || receipt.ProjectSaveAccepted && !receipt.ProjectSourceVerified ||
            receipt.ProjectSourceVerified && (!receipt.ProjectSaveAccepted || !receipt.RegistryVerified || !receipt.ProjectDirtyFalse))
            throw new RuntimeApiEvidenceException("RuntimeApiOutcomeUnconfirmed",evidence,false);
        if(receipt.NativeAccepted==false)
        {
            if(receipt.State!="failed" || after!=before || receipt.ProjectSaveAccepted || receipt.ProjectSourceVerified || receipt.RegistryVerified)
                throw new RuntimeApiEvidenceException("RuntimeApiOutcomeUnconfirmed",evidence,false);
            return evidence;
        }
        var complete=receipt.RegistryVerified && receipt.ProjectSaveAccepted && receipt.ProjectSourceVerified &&
            receipt.ProjectDirtyFalse && receipt.RuntimeFileSynchronized && !receipt.RequiresRegenerate;
        if(receipt.State=="succeeded"?!complete:receipt.State!="native_accepted" || string.IsNullOrWhiteSpace(receipt.Limitation))
            throw new RuntimeApiEvidenceException("RuntimeApiOutcomeUnconfirmed",evidence,false);
        ownedDirtyRevision=after.HasUnsavedChanges?after.Revision:null;
        return evidence;
    }
}
