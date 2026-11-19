using System.Collections;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter
{
    private sealed record FrontendPreflight(NativeFrontendLibraryLifecycle.Plan Plan,byte[]? Package,PlatformApiMutationGuard Guard);
    private readonly Dictionary<string,FrontendPreflight> frontendPreflights=new(StringComparer.Ordinal);
    private readonly Dictionary<string,NativeModuleFileGroup> frontendSavedGroups=new(StringComparer.Ordinal);

    private async Task ValidateFrontendRuntimeApiAsync(PlatformApiRequest request,CancellationToken cancellationToken=default) => await OnUi(() =>
    {
        ValidateOperationId(request.OperationId);
        if(uncertain || apiPending || pending.Count!=0 || pendingAccountExport is not null)throw new BridgeException("FrontendCleanProjectRequired","Frontend deployment requires a clean, fully settled project.");
        var mutation=FrontendGuard(request);
        guard.Value.RequireWrite(request.Target,mutation.ExpectedProjectRevision,mutation.ExpectedUser);
        _=FrontendNativeDirtyState();
        var (id,package,overwrite)=FrontendParameters(request);
        var lifecycle=new NativeFrontendLibraryLifecycle(context);
        var plan=lifecycle.Inspect(request.Method,id,package,overwrite);
        RequireFrontendFileReferences(plan);
        var group=targetFiles.Value.Resolve(new(ModuleKind.CustomLibrary,"Current")) ?? throw new BridgeException("FrontendOwnerUnavailable","The native CustomLibrary file group is unavailable.");
        if(plan.Owner!=group.NativeModuleName || !group.Current())throw new BridgeException("FrontendOwnerChanged","The native frontend owner changed during validation.");
        _=FrontendOwnerPlan(plan,group);
        frontendPreflights[request.OperationId!]=new(plan,package,mutation);
        return Task.FromResult(true);
    },cancellationToken);

    private async Task<RuntimeApiReceipt> InvokeFrontendRuntimeApiAsync(PlatformApiRequest request,CancellationToken cancellationToken=default)
    {
        await writer.WaitAsync(cancellationToken);
        try{return await OnUi<RuntimeApiReceipt>(async()=>
        {
            if(!frontendPreflights.Remove(request.OperationId??"",out var preflight))throw new BridgeException("FrontendPreflightRequired","The frontend lifecycle requires a fresh matching preflight.");
            var mutation=FrontendGuard(request);
            if(mutation!=preflight.Guard || uncertain || apiPending || pending.Count!=0 || pendingAccountExport is not null)throw new BridgeException("FrontendBaselineChanged","Frontend lifecycle guard or project state changed.");
            guard.Value.RequireWrite(request.Target,mutation.ExpectedProjectRevision,mutation.ExpectedUser);
            var (id,package,overwrite)=FrontendParameters(request);
            if(!BytesEqual(package,preflight.Package))throw new BridgeException("FrontendPackageChanged","Frontend package bytes changed after preflight.");
            var lifecycle=new NativeFrontendLibraryLifecycle(context);
            var latest=lifecycle.Inspect(request.Method,id,package,overwrite);
            if(!FrontendPlanEqual(preflight.Plan,latest))throw new BridgeException("FrontendBaselineChanged","Frontend registry or file effects changed after preflight.");
            RequireFrontendFileReferences(latest);
            var group=targetFiles.Value.Resolve(new(ModuleKind.CustomLibrary,"Current")) ?? throw new BridgeException("FrontendOwnerUnavailable","The native frontend owner is unavailable.");
            var ownerPlan=FrontendOwnerPlan(latest,group);
            await CheckoutExistingPlanAsync(request.OperationId!,request.Target,ownerPlan,cancellationToken);
            var beforeDispatch=guard.Value.Capture();
            var afterCheckout=lifecycle.Inspect(request.Method,id,package,overwrite);
            if(!FrontendPlanEqual(latest,afterCheckout) || beforeDispatch.Busy || beforeDispatch.HasUnsavedChanges)throw new BridgeException("FrontendBaselineChanged","The frontend context changed during native checkout.");
            if(beforeDispatch.InCollaborationMode && (request.Method=="deleteFrontendLibrary" || latest.Registry.Any(x=>x.Id.Equals(latest.Id,StringComparison.OrdinalIgnoreCase))))
                RequireFrontendNativeOwnerFiles(latest.Id);
            await NativeArchiveSaveBoundary.WaitAsync(request.Target.ProjectPath,null,()=>
            {
                var state=guard.Value.Capture();
                if(state.Revision!=beforeDispatch.Revision || state.Busy || state.HasUnsavedChanges ||
                   !FrontendPlanEqual(latest,lifecycle.Inspect(request.Method,id,package,overwrite)))
                    throw new BridgeException("FrontendBaselineChanged","Frontend context changed while waiting for the native archive boundary.");
                return Task.CompletedTask;
            },cancellationToken);
            var dirtyBefore=FrontendNativeDirtyState();
            cancellationToken.ThrowIfCancellationRequested();
            var saveAccepted=false;var sourceVerified=false;var dirtyFalse=false;var nativeAccepted=false;var registryVerified=false;var runtimeSynchronized=false;var requiresRegenerate=false;JsonElement? nativeResponse=null;
            try
            {
                originalSourceRevision=latest.ProjectSourceRevision;
                var result=await lifecycle.DispatchAsync(latest,package,(method,parameters)=>registry.InvokeAsync("app",method,parameters,CancellationToken.None));
                nativeAccepted=result.Accepted;registryVerified=result.RegistryVerified;runtimeSynchronized=result.RuntimeFileSynchronized;requiresRegenerate=result.RequiresRegenerate;nativeResponse=result.Response;
                if(!result.RegistryVerified)throw new BridgeException("FrontendRegistryReadbackFailed","Native registry readback does not match the requested library.");
                if(beforeDispatch.InCollaborationMode && request.Method=="uploadFrontendLibrary")RequireFrontendNativeOwnerFiles(latest.Id);
                var afterGroups=ownerPlan.BeforeGroups.Select(old=>targetFiles.Value.ResolveIdentity(old.NativeIdentity)
                    ?? throw new BridgeException("FrontendOwnerUnavailable","A native frontend owner disappeared after deployment.")).ToArray();
                if(afterGroups.Length!=ownerPlan.BeforeGroups.Count || afterGroups.Any(x=>!x.Current()))throw new BridgeException("FrontendOwnerUnavailable","A native frontend owner could not be read back.");
                apiPending=true;apiFileGroupsKnown=true;
                var journal=afterGroups.Select(afterGroup=>afterGroup.NativeModuleName==result.After.Owner ? FrontendEffectGroup(afterGroup,result.Before,result.After,lifecycle) : afterGroup).ToArray();
                foreach(var savedGroup in journal)RecordFileGroup(savedGroup);
                ownDirtyRevision=guard.Value.Capture().Revision;
                var save=await SaveCore();
                saveAccepted=save.NativeSaveAccepted;dirtyFalse=!save.ContainsUnsavedChanges;
                sourceVerified=save.SourceVerified && VerifyFrontendSource(request.Target.ProjectPath,result.After,result.Before);
                if(!sourceVerified || !dirtyFalse || !saveAccepted)
                {
                    uncertain=true;
                    return new(request.OperationId!,"verification_failed","FrontendProjectSaveUnconfirmed","Native lifecycle ran, but complete project save and source readback were not proven.",true,true,saveAccepted,sourceVerified,dirtyFalse,result.RuntimeFileSynchronized,result.RequiresRegenerate,false,result.Response,"The native result does not prove runtime activation or regeneration.");
                }
                if(beforeDispatch.InCollaborationMode)foreach(var savedGroup in journal)RetainSavedLibraryOwner(savedGroup);
                return new(request.OperationId!,"native_accepted","FrontendNativeAccepted","Native frontend library lifecycle completed and the project source was verified.",true,true,true,true,true,result.RuntimeFileSynchronized,result.RequiresRegenerate,false,result.Response,"File synchronization does not prove runtime activation; apply the native regeneration requirement before use.");
            }
            catch(Exception error) when(error is not OperationCanceledException)
            {
                try
                {
                    var observed=guard.Value.Capture();
                    if(!nativeAccepted && !saveAccepted && dirtyBefore is (false,false) && FrontendNativeDirtyState()==dirtyBefore && observed==beforeDispatch && FrontendPlanEqual(latest,lifecycle.Inspect(request.Method,id,package,overwrite)))
                    {
                        originalSourceRevision=null;
                        return new(request.OperationId!,"failed","FrontendNativeRejected",error.GetBaseException().Message,false,false,false,false,true,false,false,false,null,null);
                    }
                }
                catch(Exception verificationError) when(verificationError is not OperationCanceledException) { }
                uncertain=true;
                return new(request.OperationId!,"verification_failed","FrontendOutcomeUnconfirmed",error.GetBaseException().Message,nativeAccepted?true:null,registryVerified,saveAccepted,sourceVerified,dirtyFalse,runtimeSynchronized,requiresRegenerate,false,nativeResponse,"The native lifecycle or its project save may have partially completed; reconcile before retrying.");
            }
        },cancellationToken);}
        finally{writer.Release();}
    }

    private PlatformApiMutationGuard FrontendGuard(PlatformApiRequest request)
    {
        if(request.Namespace!="app" || request.Method is not ("uploadFrontendLibrary" or "deleteFrontendLibrary") || request.Parameters.ValueKind!=JsonValueKind.Object || !request.Parameters.TryGetProperty("guard",out var value))throw new BridgeException("FrontendApiInvalid","Frontend API and project guard are required.");
        var result=value.Deserialize<PlatformApiMutationGuard>(GuardJson) ?? throw new BridgeException("FrontendApiInvalid","Frontend project guard is invalid.");
        if(result.Scope!=EffectScope.RuntimeDeployment || result.OperationId!=request.OperationId || result.Target!=request.Target || result.Address is not null || result.ExpectedModuleRevision is not null || result.ExpectedServiceBinding is not null)
            throw new BridgeException("FrontendApiInvalid","Frontend runtime guard does not match the request.");
        return result;
    }
    private static (string? Id,byte[]? Package,bool Overwrite) FrontendParameters(PlatformApiRequest request)
    {
        var allowed=request.Method=="uploadFrontendLibrary"?new[]{"guard","packageFilePath","overwrite"}:new[]{"guard","libraryId"};
        var props=request.Parameters.EnumerateObject().ToArray();
        if(props.Any(p=>!allowed.Contains(p.Name,StringComparer.Ordinal)) || props.GroupBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).Any(g=>g.Count()!=1))throw new BridgeException("FrontendApiInvalid","Unexpected or duplicate frontend parameters.");
        if(request.Method=="deleteFrontendLibrary")
        {
            if(!request.Parameters.TryGetProperty("libraryId",out var id) || id.ValueKind!=JsonValueKind.String)throw new BridgeException("FrontendApiInvalid","A libraryId is required.");
            return(id.GetString(),null,false);
        }
        if(!request.Parameters.TryGetProperty("packageFilePath",out var path) || path.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()) || !Path.IsPathFullyQualified(path.GetString()!))throw new BridgeException("FrontendApiInvalid","An absolute packageFilePath is required.");
        var file=Path.GetFullPath(path.GetString()!);_=NativeSaveVerification.Under(Path.GetDirectoryName(file)!,Path.GetFileName(file));
        var info=new FileInfo(file);if(!info.Exists || info.Length is <=0 or >64*1024*1024)throw new BridgeException("FrontendPackageInvalid","The local frontend package must exist and be at most 64 MiB.");
        var overwrite=request.Parameters.TryGetProperty("overwrite",out var flag) && flag.ValueKind==JsonValueKind.True;
        if(request.Parameters.TryGetProperty("overwrite",out flag) && flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))throw new BridgeException("FrontendApiInvalid","Overwrite must be Boolean.");
        return(null,File.ReadAllBytes(file),overwrite);
    }
    private NativeModuleFilePlan FrontendOwnerPlan(NativeFrontendLibraryLifecycle.Plan plan,NativeModuleFileGroup root)
    {
        var groups=new List<NativeModuleFileGroup>{root};var state=guard.Value.Capture();
        if(state.InCollaborationMode)
        {
            var manager=R.Static(context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager",true)!,"get_Instance")!;
            if(R.Get(manager,"Modules") is not IDictionary modules || R.Get(manager,"SaveLoadTrackedFiles") is not IDictionary tracking)throw new BridgeException("FrontendOwnershipUnavailable","The native collaboration registry or tracked files are unavailable.");
            var localRoot=(string)R.Static(context.DesignerAssembly.GetType("Forguncy.Common.Config",true)!,"get_LocalDataFolderPath")!;
            var packageRoot=(string)R.Static(context.DesignerAssembly.GetType("Forguncy.Common.Config",true)!,"get_LocalFrontendLibraryPath")!;
            var prefixes=new[]{"UserFile/FrontendLibraries/"+plan.Id+"/",Path.GetRelativePath(localRoot,packageRoot).Replace('\\','/')+"/"+plan.Id+"/"};
            var names=new HashSet<string>(StringComparer.Ordinal){root.NativeModuleName};
            bool Intersects(string path)
            {
                var normalized=NativeResources.NormalizeNativeReferencePath(localRoot,path);
                return normalized is not null && prefixes.Any(prefix=>normalized.StartsWith(prefix,StringComparison.OrdinalIgnoreCase) || prefix.StartsWith(normalized.TrimEnd('/')+"/",StringComparison.OrdinalIgnoreCase));
            }
            var helper=context.DesignerAssembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
            foreach(DictionaryEntry entry in modules)
            {
                if(entry.Key is not string name || entry.Value is not object module || R.Get(module,"ModuleName") as string != name)
                    throw new BridgeException("FrontendOwnershipUnavailable","Native module registration is invalid.");
                if(R.Static(helper,"GetDirectReferFiles",module,localRoot,"../Upload") is not IEnumerable direct)
                    throw new BridgeException("FrontendOwnershipUnavailable","Native owner did not expose a complete file group.");
                foreach(var item in direct)
                {
                    if(item is not string path)throw new BridgeException("FrontendOwnershipUnavailable","Native provider path is invalid.");
                    if(Intersects(path))names.Add(name);
                }
            }
            foreach(DictionaryEntry entry in tracking)
            {
                if(entry.Key is not string name || entry.Value is not IEnumerable values)throw new BridgeException("FrontendOwnershipUnavailable","Native tracked-file registration is invalid.");
                foreach(var item in values)
                {
                    if(item is not string path)throw new BridgeException("FrontendOwnershipUnavailable","Native tracked-file path is invalid.");
                    if(Intersects(path))names.Add(name);
                }
            }
            var owners=names.Select(name=>modules[name] ?? throw new BridgeException("FrontendOwnershipUnavailable","A native frontend file owner is not registered.")).Cast<object>().ToArray();
            var resolved=targetFiles.Value.ResolveNativeModules(owners) ?? throw new BridgeException("FrontendOwnershipUnavailable","A native frontend owner cannot be mapped to its file group.");
            if(!resolved.Select(x=>x.NativeModuleName).ToHashSet(StringComparer.Ordinal).SetEquals(names) || resolved.Any(x=>!x.Current()))throw new BridgeException("FrontendOwnershipUnavailable","The complete native owner set could not be verified.");
            groups=resolved.ToList();
        }
        return new NativeModuleFilePlan(groups,[]);
    }
    private void RequireFrontendFileReferences(NativeFrontendLibraryLifecycle.Plan plan)
    {
        if(plan.Method!="deleteFrontendLibrary" && !plan.Overwrite)return;
        var config=context.DesignerAssembly.GetType("Forguncy.Common.Config",true)!;
        var user=(string)R.Static(config,"get_LocalUserFilePath")!;
        var files=plan.LocalFiles.Select(file=>file.Path).Append(Path.Combine(user,"FrontendLibraries",plan.Id,"bundle.js"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach(var file in files)
        {
            var path=file.Replace('/',Path.DirectorySeparatorChar);
            if(!path.StartsWith(Path.GetFullPath(user).TrimEnd('\\','/')+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
            var relative=Path.GetRelativePath(user,path).Replace('\\','/');var split=relative.LastIndexOf('/');
            if(split<0)throw new BridgeException("FrontendFileInvalid","A frontend library file has no native subfolder.");
            var references=resources.Value.References(new(ModuleKind.ResourceFile,relative[(split+1)..],"UserFile/"+relative[..split]));
            if(!references.Complete || references.References.Any(x=>x.Source.Kind!=ModuleKind.CustomLibrary))
                throw new BridgeException("FrontendFileReferenced",references.Reason??"A frontend library file has incoming native references.");
        }
    }
    private void RequireFrontendNativeOwnerFiles(string id)
    {
        var module=collaborationModules.Value.ResolveNativeModule(new(ModuleKind.CustomLibrary,"Current"));
        var config=context.DesignerAssembly.GetType("Forguncy.Common.Config",true)!;
        var local=(string)R.Static(config,"get_LocalDataFolderPath")!;
        var helper=context.DesignerAssembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        var direct=(IEnumerable<string>)R.Static(helper,"GetDirectReferFiles",module,local,"../Upload")!;
        var files=direct.Select(path=>NativeResources.NormalizeNativeReferencePath(local,path)).Where(path=>path is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected=new[]{"UserFile/FrontendLibraries/"+id+"/bundle.js","FrontendLibraries/"+id+"/library.zip"};
        if(expected.Any(path=>!files.Contains(path)))throw new BridgeException("FrontendNativeTrackingUnavailable","The real CustomLibrary owner does not enumerate both native package and bundle files.");
    }
    private (bool Metadata,bool SaveData) FrontendNativeDirtyState()
    {
        var cache=R.Static(context.DesignerAssembly.GetType("pb",true)!,"get_Instance")!;
        var type=System.Reflection.Assembly.Load("Forguncy.Collaboration").GetType("Forguncy.Collaboration.ModuleType",true)!;
        var owner=Enum.Parse(type,"CustomLibrary");
        return ((bool)R.Call(cache,"IsMetadateDirty",owner)!, (bool)R.Call(cache,"IsSaveDataDirty",owner)!);
    }
    private static bool BytesEqual(byte[]? a,byte[]? b)=>a is null?b is null:b is not null && a.AsSpan().SequenceEqual(b);
    private static bool FrontendPlanEqual(NativeFrontendLibraryLifecycle.Plan a,NativeFrontendLibraryLifecycle.Plan b)=>
        a.Method==b.Method && a.Id==b.Id && a.Name==b.Name && a.Version==b.Version && a.GlobalName==b.GlobalName && a.PackageSha256==b.PackageSha256 && a.BundleSha256==b.BundleSha256 && a.TypeDefinitionSha256==b.TypeDefinitionSha256 && a.Overwrite==b.Overwrite && a.Owner==b.Owner && a.OwnerFiles.SequenceEqual(b.OwnerFiles) && a.Registry.SequenceEqual(b.Registry) && a.LocalFiles.SequenceEqual(b.LocalFiles) && a.RuntimeFiles.SequenceEqual(b.RuntimeFiles) && a.SourceFiles.SequenceEqual(b.SourceFiles) && a.ProjectSourceRevision==b.ProjectSourceRevision;
    private bool VerifyFrontendSource(string source,NativeFrontendLibraryLifecycle.Plan after,NativeFrontendLibraryLifecycle.Plan before)
    {
        var config=context.DesignerAssembly.GetType("Forguncy.Common.Config",true)!;var localRoot=(string)R.Static(config,"get_LocalDataFolderPath")!;
        var expected=after.LocalFiles.ToDictionary(x=>Path.GetRelativePath(localRoot,x.Path.Replace('/',Path.DirectorySeparatorChar)).Replace('\\','/'),x=>x.Sha256,StringComparer.Ordinal);
        var paths=before.SourceFiles.Select(x=>x.Path).Concat(expected.Keys).Where(x=>x!="CustomLibraries.json").Distinct(StringComparer.Ordinal);
        foreach(var path in paths)
        {
            var actual=NativeSaveVerification.ReadSource(source,path);var hash=actual is null?null:Convert.ToHexString(SHA256.HashData(actual));
            if(hash!=expected.GetValueOrDefault(path))return false;
        }
        return true;
    }
    private NativeModuleFileGroup FrontendEffectGroup(NativeModuleFileGroup group,NativeFrontendLibraryLifecycle.Plan before,
        NativeFrontendLibraryLifecycle.Plan after,NativeFrontendLibraryLifecycle lifecycle)
    {
        var config=context.DesignerAssembly.GetType("Forguncy.Common.Config",true)!;var localRoot=(string)R.Static(config,"get_LocalDataFolderPath")!;
        var expected=after.LocalFiles.ToDictionary(x=>Path.GetRelativePath(localRoot,x.Path.Replace('/',Path.DirectorySeparatorChar)).Replace('\\','/'),x=>x.Sha256!,StringComparer.Ordinal);
        var affected=before.SourceFiles.Select(x=>x.Path).Concat(expected.Keys).Where(x=>x!="CustomLibraries.json").Distinct(StringComparer.Ordinal).ToArray();
        var required=group.RequiredFiles.Concat(expected.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var absent=group.AbsentFiles.Concat(affected.Where(x=>!expected.ContainsKey(x))).Except(required,StringComparer.Ordinal).ToArray();
        var referenced=group.ReferencedFiles.Except(required,StringComparer.Ordinal).Except(absent,StringComparer.Ordinal).ToArray();
        bool Current()=>group.Current() && lifecycle.CaptureAfter(after).LocalFiles.SequenceEqual(after.LocalFiles);
        bool Matches(string path,byte[] content)=>expected.TryGetValue(path,out var hash)?Convert.ToHexString(SHA256.HashData(content))==hash:group.MatchesContent(path,content);
        return new(group.Address,group.NativeModuleName,required,absent,referenced,group.ExpectedJson,Current,Matches,
            group.NativeIdentity,group.IsPresent,group.RequiredDirectories,group.AbsentDirectories);
    }
}
