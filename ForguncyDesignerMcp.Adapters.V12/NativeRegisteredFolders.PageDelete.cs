using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed partial class NativeRegisteredFolders
{
    private NativeFolderLifecyclePlan CapturePageDelete(ModuleMutationRequest request,NativeModuleFiles files,Item current,ModuleAddress destination,object[] changes)
    {
        if(owner is not ("Page" or "MasterPage" or "UserControlPage") || request.Operation!=ModuleOperation.Delete) throw new BridgeException("NativeFolderPlanUnavailable","The native page grouped deletion plan is unavailable.");
        var descendants=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
        var leaves=descendants.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
        var pageType=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Pages.PageBase",true)!;
        if(leaves.Any(model=>!pageType.IsInstanceOfType(model)) || leaves.Distinct(ReferenceEqualityComparer.Instance).Count()!=leaves.Length)
            throw new BridgeException("NativeFolderPlanUnavailable","The native folder contains an unknown or duplicate page model.");
        var checker=assembly.GetType("Forguncy.Model.Pages.PageOperationsParameterChecker",true)!;
        var result=R.Static(checker,"CheckDeleteFolder",current.Model) ?? throw new BridgeException("NativeFolderPlanUnavailable","The native page-folder checker is unavailable.");
        if(R.Get(result,"Result") is not true) throw new BridgeException("NativeFolderChildDeleteRejected",R.Get(result,"ErrorMessage") as string ?? "The native page-folder checker rejected a child.");
        (string Name,string Type,string State) Token(object tuple)
        {
            var simple=tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!;
            return ((string)R.Get(simple,"ModuleName")!,R.Get(simple,"ModuleType")!.ToString()!,tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!);
        }
        var combined=changes.ToList();
        var leafNames=leaves.Select(leaf=>(string)R.Get(R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",leaf)!,"ModuleName")!).ToHashSet(StringComparer.Ordinal);
        foreach(var leaf in leaves)
        {
            var leafRequest=request with {Address=new(Enum.Parse<ModuleKind>(owner),(string)R.Get(leaf,"Name")!)};
            var native=files.CaptureMutation(leafRequest)?.Plan?.NativeChanges ?? throw new BridgeException("NativeFolderPlanUnavailable","The native single-page deletion resolver is unavailable.");
            foreach(var change in native)
            {
                var token=Token(change); var existing=combined.Where(item=>Token(item).Name==token.Name).ToArray();
                if(existing.Length==0) combined.Add(change);
                else if(existing.Length!=1 || Token(existing[0])!=token)
                {
                    if(existing.Length!=1 || !leafNames.Contains(token.Name) || Token(existing[0]).Type!=token.Type ||
                        !new[]{Token(existing[0]).State,token.State}.ToHashSet(StringComparer.Ordinal).SetEquals(new[]{"Edit","Remove"}))
                        throw new BridgeException("NativeFolderPlanUnavailable","Native folder and single-page deletion owners disagree.");
                    if(token.State=="Remove") {combined.Remove(existing[0]); combined.Add(change);}
                }
            }
        }
        changes=combined.ToArray();
        var folderType=R.Static(assembly.GetType("Forguncy.Common.ModuleExtensions",true)!,"GetModuleType",root)!.ToString()!;
        var folderChanges=changes.Where(change=>Token(change).Type==folderType).ToArray();
        var expected=descendants.ToDictionary(item=>NativeName(item.Address),StringComparer.Ordinal);
        var tokens=folderChanges.Select(Token).ToArray();
        if(tokens.Length!=expected.Count || tokens.Any(token=>token.State!="Remove" || !expected.ContainsKey(token.Name)) || tokens.Select(token=>token.Name).Distinct(StringComparer.Ordinal).Count()!=expected.Count)
            throw new BridgeException("NativeFolderPlanUnavailable","The native page-folder Remove owners do not exactly match the loaded subtree.");
        var otherChanges=changes.Except(folderChanges).ToArray();
        var deletedPages=leaves.Concat(files.PageDependentTemplates(leaves)).Distinct(ReferenceEqualityComparer.Instance).ToArray();
        files.RequirePageFolderReferencesContained(deletedPages);
        var otherPlan=otherChanges.Length==0 ? null : files.CaptureNativeModulesCore(otherChanges,true);
        if(otherChanges.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group))))
            throw new BridgeException("NativeFolderPlanUnavailable","The native page or related owner source group is incomplete.");
        var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        foreach(var leaf in leaves)
        {
            var name=(string)R.Static(extensions,"GetModuleName",leaf,null)!;
            if(otherPlan is null || !otherPlan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,leaf)) ||
                otherChanges.Count(change=>Token(change).Name==name && Token(change).State=="Remove")!=1)
                throw new BridgeException("NativeFolderPlanUnavailable","Every descendant page requires its exact native Remove owner and source group.");
        }
        if(deletedPages.Any(model=>otherPlan is null || !otherPlan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,model))))
            throw new BridgeException("NativeFolderPlanUnavailable","The native owner set omitted a page or its implicit template.");
        var directories=descendants.Select(item=>item.Key).ToArray();
        var filePaths=(otherPlan?.BeforeGroups ?? []).SelectMany(group=>group.RequiredFiles.Concat(group.ReferencedFiles)).Where(path=>path.StartsWith(current.Key,StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        bool TreeCurrent()=>NativeRegisteredFolderEvidence.TreeMatches(Stage,current.Key,directories,filePaths) && NativeRegisteredFolderEvidence.TreeMatches(Source,current.Key,directories,filePaths) &&
            descendants.All(item=>NativeRegisteredFolderEvidence.HasRegistration(loader,item.Key)) && (otherPlan is null || otherPlan.BeforeGroups.All(files.Verify));
        if(!TreeCurrent()) throw new BridgeException("NativeFolderSourceChanged","The loaded page subtree does not match its native saved inventory.");
        var folders=descendants.Select(item=>new NativeModuleFileGroup(item.Address,NativeName(item.Address),[],[],[],new Dictionary<string,string>(),
            ()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model) && found.Address==item.Address) && TreeCurrent(),nativeIdentity:item.Model,
            isPresent:()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model)),requiredDirectories:[item.Key.TrimEnd('/')],directoriesRequireLocal:false)).ToArray();
        if(folders.Any(group=>!files.Verify(group))) throw new BridgeException("NativeFolderSourceChanged","A page subtree folder could not be verified.");
        return new(new(folders.Concat(otherPlan?.BeforeGroups ?? []).ToArray(),[],nativeChanges:changes),
            folders.Single(group=>ReferenceEquals(group.NativeIdentity,current.Model)),otherPlan,destination,folders,PageFileContext:files,PageDeleteModels:deletedPages);
    }

    private IReadOnlyList<string> ApplyPageDelete(ModuleMutationRequest request,NativeFolderLifecyclePlan capture)
    {
        var (current,_)=Prepare(request,true);
        var files=capture.PageFileContext ?? throw new BridgeException("NativeFolderPlanUnavailable","The native page-file context is unavailable.");
        if(owner is not ("Page" or "MasterPage" or "UserControlPage") || request.Operation!=ModuleOperation.Delete || current is null || capture.Before is null ||
            !ReferenceEquals(current.Model,capture.Before.NativeIdentity) || capture.Plan.BeforeGroups.Any(group=>!group.Current()))
            throw new BridgeException("NativeFolderSourceChanged","The native page subtree changed after its captured owner plan.");
        files.RequirePageFolderManagerContext(Enum.Parse<ModuleKind>(owner));
        var deleting=capture.PageDeleteModels ?? throw new BridgeException("NativeFolderPlanUnavailable","The captured page deletion set is unavailable.");
        var currentLeaves=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal))
            .SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
        var currentDeletion=currentLeaves.Concat(files.PageDependentTemplates(currentLeaves)).Distinct(ReferenceEqualityComparer.Instance).ToArray();
        if(currentDeletion.Length!=deleting.Count || currentDeletion.Any(model=>!deleting.Any(original=>ReferenceEquals(original,model))))
            throw new BridgeException("NativeFolderSourceChanged","The native pages or their dependent templates changed after checkout.");
        files.RequirePageFolderReferencesContained(deleting);
        var result=R.Static(assembly.GetType("Forguncy.Model.Pages.PageOperationsParameterChecker",true)!,"CheckDeleteFolder",current.Model)!;
        if(R.Get(result,"Result") is not true) throw new BridgeException("NativeFolderChildDeleteRejected",R.Get(result,"ErrorMessage") as string ?? "The native page-folder checker rejected a child.");
        if(R.Static(assembly.GetType("nb",true)!,"DeleteFolder",current.Model) is not true)
            throw new BridgeException("NativeFolderDeleteUnconfirmed","The native page manager did not confirm deletion; do not replay it.");
        if(owner!="Page" && current.Parent.Contains(current.Model) && deleting.All(model=>!capture.OtherOwners!.BeforeGroups.Single(group=>ReferenceEquals(group.NativeIdentity,model)).IsPresent()))
        {
            R.Call(colleague,"DeleteFolder",current.Model); current.Parent.Remove(current.Model); Dirty(current.Model,"Remove");
        }
        if(capture.FolderBefore!.Any(group=>group.IsPresent()) || deleting.Any(model=>capture.OtherOwners!.BeforeGroups.Single(group=>ReferenceEquals(group.NativeIdentity,model)).IsPresent()))
            throw new BridgeException("NativeFolderReadbackMismatch","A deleted page-folder owner remains in the native tree.");
        foreach(var group in capture.FolderBefore!) foreach(var directory in group.RequiredDirectories) touched.Add(directory+"/");
        results[request.OperationId]=null;
        return capture.Plan.BeforeGroups.SelectMany(group=>group.Paths.Concat(group.RequiredDirectories)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<NativeModuleFileGroup> PageDeleteAfter(NativeFolderLifecyclePlan capture,NativeModuleFiles files)
    {
        var result=new List<NativeModuleFileGroup>();
        if(capture.OtherOwners is not null)
            result.AddRange(files.ResolveNativeModulesAfter(capture.OtherOwners) ?? throw new BridgeException("NativeFolderReadbackUnavailable","Native page owners could not be read back."));
        foreach(var before in capture.FolderBefore!)
        {
            if(before.IsPresent()) throw new BridgeException("NativeFolderReadbackMismatch","A deleted page folder remains in the native tree.");
            result.Add(new(before.Address,before.NativeModuleName,[],[],[],new Dictionary<string,string>(),()=>!before.IsPresent() && before.RequiredDirectories.All(path=>!NativeRegisteredFolderEvidence.HasRegistration(loader,path+"/")),
                nativeIdentity:before.NativeIdentity,isPresent:before.IsPresent,absentDirectories:before.RequiredDirectories,directoriesRequireLocal:owner=="Table" || IsImage));
        }
        return result;
    }
}
