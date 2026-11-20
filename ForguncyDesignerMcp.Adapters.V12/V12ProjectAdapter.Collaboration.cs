using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter
{
    internal static async Task<string> CheckoutExistingOwnersAsync(string beforeSource,Func<Task<bool>> checkout,
        Func<string> sourceAfter,Func<string?> changedOwner,Func<string> revisionAfter)
    {
        if(!await checkout()) throw new BridgeException("NativeCheckoutUnverified","Native checkout was not verified; the requested mutation was not dispatched.");
        try
        {
            if (sourceAfter() != beforeSource) throw new CheckoutBaselineException(sourceChanged: true);
            if (changedOwner() is { } owner) throw new CheckoutBaselineException(sourceChanged: false, owner);
            return revisionAfter();
        }
        catch (Exception error) when (error is not (CheckoutBaselineException or OutOfMemoryException or StackOverflowException))
        {
            throw new CheckoutBaselineException(error);
        }
    }

    internal static ModuleAddress[] MapExistingOwners(NativeModuleFilePlan plan,Func<ModuleAddress,ModuleAddress> map)
    {
        var owners=plan.BeforeGroups.Select(group=>map(group.Address)).Distinct().ToArray();
        if(owners.Length!=plan.BeforeGroups.Count) throw new BridgeException("CollaborationEffectPlanAmbiguous","Affected native owners do not map uniquely.");
        return owners;
    }

    internal static CollaborationMutationRequest ExistingCheckoutRequest(string operationId,ProjectTarget target,string revision,UserIdentity user,NativeModuleFilePlan plan,Func<ModuleAddress,ModuleAddress> map) =>
        new(operationId,target,revision,user,MapExistingOwners(plan,map));

    private async Task CheckoutExistingPlanAsync(string operationId,ProjectTarget target,NativeModuleFilePlan? plan,CancellationToken token,Action? startingNativeWrite=null)
    {
        var state=guard.Value.Capture();
        if(!state.InCollaborationMode) return;
        if(plan is null||plan.BeforeGroups.Count==0||plan.ExpectedNewModuleNames.Count!=0)
            throw new BridgeException("CollaborationEffectPlanUnavailable","A complete existing-owner effect plan is required before mutation; new registrations require their own native plan.");
        if(await CanReuseOwnedEditsAsync(plan,token)) { ownDirtyRevision=guard.Value.Capture().Revision; return; }
        var source=NativeProjectGuard.SourceRevision(target.ProjectPath,includeCollaboration:false);
        var request=ExistingCheckoutRequest(operationId,target,state.Revision,state.CurrentUser??throw new BridgeException("CollaborationIdentityUnavailable","Native collaboration identity is unavailable."),plan,address=>collaborationModules.Value.MapNativeModule(collaborationModules.Value.ResolveNativeModule(address)));
        var revision=await CheckoutExistingOwnersAsync(source,async()=>
        {
            var receipt=await collaboration.Value.CheckoutAsync(request,cancellationToken:token,startingNativeWrite:startingNativeWrite);
            if(receipt.ExecutionMayContinue||receipt.State=="verification_failed")
            {
                startingNativeWrite?.Invoke();
                uncertain=true;
            }
            if (receipt.State == "rejected" && !receipt.ExecutionMayContinue && receipt.NativeAccepted == false)
                throw new BridgeException(receipt.Code, receipt.Message);
            return !receipt.ExecutionMayContinue&&receipt.State=="succeeded"&&receipt.NativeAccepted==true&&receipt.LockStateVerified;
        },()=>NativeProjectGuard.SourceRevision(target.ProjectPath,includeCollaboration:false),
            ()=>plan.BeforeGroups.FirstOrDefault(group=>!group.Current())?.NativeModuleName,()=>guard.Value.Capture().Revision);
        ownDirtyRevision=revision;
    }
    private async Task CheckoutLifecyclePlanAsync(string operationId,ProjectTarget target,NativeModuleFilePlan? plan,CancellationToken token,Action? startingNativeWrite=null)
    {
        var state=guard.Value.Capture();
        if(!state.InCollaborationMode) return;
        if(plan?.NativeChanges is not { Count:>0 } changes)
            throw new BridgeException("CollaborationLifecyclePlanUnavailable","A complete native lifecycle resolver plan is required before mutation.");
        // Lifecycle operations that only request existing Edit owners can use
        // the same proof. New/Remove/Move registrations still go through native checkout.
        if(plan.ExpectedNewModuleNames.Count==0 && changes.Count==plan.BeforeGroups.Count &&
            changes.All(change=>Convert.ToInt32(change.GetType().GetField("Item2")!.GetValue(change))==1) &&
            changes.Select(change=>(string)NativeReflection.Get(change.GetType().GetField("Item1")!.GetValue(change)!,"ModuleName")!)
                .Order(StringComparer.Ordinal).SequenceEqual(plan.BeforeGroups.Select(group=>group.NativeModuleName).Order(StringComparer.Ordinal),StringComparer.Ordinal) &&
            await CanReuseOwnedEditsAsync(plan,token)) { ownDirtyRevision=guard.Value.Capture().Revision; return; }
        var owners=changes.Select(change=>collaborationModules.Value.MapNativeModule(change.GetType().GetField("Item1")!.GetValue(change)!)).ToArray();
        if(owners.Distinct().Count()!=owners.Length) throw new BridgeException("CollaborationEffectPlanAmbiguous","The native resolver returned duplicate owners.");
        var source=NativeProjectGuard.SourceRevision(target.ProjectPath,includeCollaboration:false);
        var request=new CollaborationMutationRequest(operationId,target,state.Revision,state.CurrentUser??throw new BridgeException("CollaborationIdentityUnavailable","Native collaboration identity is unavailable."),owners);
        ownDirtyRevision=await CheckoutExistingOwnersAsync(source,async()=>
        {
            var groups=plan.BeforeGroups.ToDictionary(group=>group.NativeModuleName,group=>(IReadOnlyList<string>)group.Paths,StringComparer.Ordinal);
            var directories=plan.BeforeGroups.Where(group=>group.Address.Kind==ModuleKind.Folder).ToDictionary(group=>group.NativeModuleName,group=>(IReadOnlyList<string>)group.RequiredDirectories,StringComparer.Ordinal);
            var receipt=await collaboration.Value.CheckoutAsync(request,changes,token,groups,directories,startingNativeWrite);
            if(receipt.ExecutionMayContinue||receipt.State=="verification_failed")
            {
                startingNativeWrite?.Invoke();
                uncertain=true;
            }
            if (receipt.State == "rejected" && !receipt.ExecutionMayContinue && receipt.NativeAccepted == false)
                throw new BridgeException(receipt.Code, receipt.Message);
            return !receipt.ExecutionMayContinue&&receipt.State=="succeeded"&&receipt.NativeAccepted==true&&receipt.LockStateVerified;
        },()=>NativeProjectGuard.SourceRevision(target.ProjectPath,includeCollaboration:false),
            ()=>plan.BeforeGroups.FirstOrDefault(group=>!group.Current())?.NativeModuleName,()=>guard.Value.Capture().Revision);
    }
    public Task<CollaborationStatus> GetCollaborationStatusAsync(CancellationToken cancellationToken=default) => OnUi(async()=>
    {
        var state=guard.Value.Capture();
        return !state.InCollaborationMode?new(state.Target,false,state.CurrentUser,state.Revision,[]):await collaboration.Value.CaptureStatusAsync(cancellationToken);
    },cancellationToken);

    public Task<CollaborationMutationReceipt> CheckoutAsync(CollaborationMutationRequest request,CancellationToken cancellationToken=default) =>
        RunCollaborationAsync(request,false,cancellationToken);

    public Task<CollaborationMutationReceipt> CheckinAsync(CollaborationMutationRequest request,CancellationToken cancellationToken=default) =>
        RunCollaborationAsync(request,true,cancellationToken);

    private async Task<CollaborationMutationReceipt> RunCollaborationAsync(CollaborationMutationRequest request,bool checkin,CancellationToken token)
    {
        await writer.WaitAsync(token);
        try
        {
            return await OnUi(async()=>
            {
                ValidateOperationId(request.OperationId);
                if(uncertain) throw new BridgeException("NativeExecutionUncertain","Reconcile the existing native outcome before another collaboration write.");
                var observed=guard.Value.Capture();
                if(!observed.InCollaborationMode)
                    return new CollaborationMutationReceipt(request.OperationId,"rejected","CollaborationNotApplicable","Standalone documents have no native collaboration registration.",
                        new(observed.Target,false,observed.CurrentUser,observed.Revision,[]),false);
                guard.Value.RequireWrite(request.Target,request.ExpectedProjectRevision,request.ExpectedUser,
                    allowDirty:(pending.Count>0||apiPending)&&observed.Revision==ownDirtyRevision);
                var frontendNames=checkin ? request.Modules.Select(address=>(string)NativeReflection.Get(collaborationModules.Value.ResolveCheckinModule(address),"ModuleName")!).ToArray() : [];
                var frontendFiles=frontendNames.Where(frontendSavedGroups.ContainsKey).SelectMany(name=>frontendSavedGroups[name].Paths).Distinct(StringComparer.Ordinal).ToArray();
                if(checkin && frontendNames.Contains("CustomLibrary",StringComparer.Ordinal))
                {
                    var state=await collaboration.Value.CaptureStatusAsync(token);
                    var changed=state.Git?.ChangedFiles ?? throw new BridgeException("FrontendCheckinEvidenceUnavailable","The native Git file inventory is unavailable.");
                    var current=targetFiles.Value.Resolve(new(ModuleKind.CustomLibrary,"Current")) ?? throw new BridgeException("FrontendOwnerUnavailable","The native CustomLibrary owner is unavailable.");
                    var covered=current.Paths.Concat(frontendFiles).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if(changed.Keys.Any(path=>(path.StartsWith("FrontendLibraries/",StringComparison.OrdinalIgnoreCase) || path.StartsWith("UserFile/FrontendLibraries/",StringComparison.OrdinalIgnoreCase)) && !covered.Contains(path)))
                        throw new BridgeException("FrontendCheckinJournalRequired","A changed frontend file is absent from the native owner and the current saved journal; reconcile its native check-in scope before dispatch.");
                }
                NativePluginCheckinFiles? pluginFiles = null;
                ProjectSaveResult? nativeSave = null;
                void VerifySavedProjection(NativeCollaborationGitBaseline baseline)
                {
                    if (pluginFiles is null) return;
                    var unregistered = baseline.Local.Changes.Keys.FirstOrDefault(path =>
                        path.StartsWith(plugins.Value.ProjectPluginFolderName + "/", StringComparison.OrdinalIgnoreCase) &&
                        !pluginFiles.Hashes.ContainsKey(path) && !pluginFiles.Removed.Contains(path, StringComparer.Ordinal));
                    if (unregistered is not null)
                        throw new BridgeException("PluginSourceProjectionUnverified", "Changed plugin path is outside the saved and historical owner registration: " + unregistered);
                    if (!pluginFiles.Matches(targetFiles.Value.CapturePluginForCheckin(), exactBytes: true) ||
                        pluginFiles.Hashes.Any(pair => !baseline.ExpectedFiles.TryGetValue(pair.Key, out var file) || file.RawHash != pair.Value) ||
                        pluginFiles.Removed.Any(path => !baseline.ExpectedFiles.TryGetValue(path, out var file) || file.RawHash is not null))
                        throw new BridgeException("PluginSaveBaselineChanged", "Plugin registration or package bytes differ from the verified native save and Git baseline.");
                }
                var result=checkin?await collaboration.Value.CheckinAsync(request,ct=>SaveForCheckinAsync(request,ct,files=>pluginFiles=files,saved=>nativeSave=saved),token,frontendFiles,VerifySavedProjection):
                    await collaboration.Value.CheckoutAsync(request,cancellationToken:token);
                if (nativeSave is not null) result = result with { NativeSaveAccepted = nativeSave.NativeSaveAccepted };
                if(result.State=="verification_failed"||result.ExecutionMayContinue) uncertain=true;
                if(checkin && result.State=="succeeded" && result.NativeAccepted is true)
                    foreach(var name in frontendNames)frontendSavedGroups.Remove(name);
                if(result.NativeAccepted is true) ownDirtyRevision=guard.Value.Capture().Revision;
                return result;
            },token);
        }
        finally{writer.Release();}
    }

    private async Task<bool> SaveForCheckinAsync(CollaborationMutationRequest request,CancellationToken token,
        Action<NativePluginCheckinFiles> recordPluginFiles, Action<ProjectSaveResult> recordSave)
    {
        token.ThrowIfCancellationRequested();
        var selected=new List<NativeModuleFileGroup>();
        NativePluginCheckinFiles? pluginBefore = null;
        foreach(var address in request.Modules)
        {
            var native=collaborationModules.Value.ResolveCheckinModule(address);
            if (address == NativePluginCheckinFiles.Address)
            {
                pluginBefore = targetFiles.Value.CapturePluginForCheckin();
                if (!ReferenceEquals(native, pluginBefore.Owner))
                    throw new BridgeException("PluginOwnerChanged", "The selected check-in owner differs from the registered Plugin module.");
                continue;
            }
            if (address.Kind == ModuleKind.Report && NativeRemovedReport.IsRemoval(native))
            {
                selected.Add(targetFiles.Value.RemovedReportForCheckin(address, native));
                continue;
            }
            var name=(string)NativeReflection.Get(native,"ModuleName")!;
            var group=apiTargetGroups.Values.LastOrDefault(item=>item.NativeModuleName==name);
            if(group is null)frontendSavedGroups.TryGetValue(name,out group);
            group??=targetFiles.Value.Resolve(address);
            if(group is null) throw new BridgeException("CollaborationSourceProjectionUnavailable","The selected native owner has no complete source projection for check-in.");
            selected.Add(group);
        }
        if(pendingAccountExport is not null) throw new BridgeException("AccountServiceBindingRequired","Account exports require their separately reviewed service binding before check-in.");
        var saved=await SaveCore();
        recordSave(saved);
        if (!saved.NativeSaveAccepted || saved.ContainsUnsavedChanges || !ReduceFileGroups(selected).All(targetFiles.Value.Verify)) return false;
        if (pluginBefore is not null)
        {
            var pluginAfter = targetFiles.Value.CapturePluginForCheckin();
            if (!pluginBefore.Matches(pluginAfter, exactBytes: false))
                throw new BridgeException("PluginSaveBaselineChanged", "Plugin owner, declarations, paths or package contents changed during the native save.");
            recordPluginFiles(pluginAfter);
        }
        return true;
    }
}
