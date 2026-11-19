using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed partial class NativeRegisteredFolders
{
    private NativeFolderLifecyclePlan CapturePageUpdate(ModuleMutationRequest request,NativeModuleFiles files,Item current,ModuleAddress destination,object[] changes)
    {
        if(request.Operation!=ModuleOperation.Update) throw new BridgeException("NativeFolderPlanUnavailable","The native grouped folder update is unavailable.");
        var descendants=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
        var leaves=descendants.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
        var pageType=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Pages.PageBase",true)!;
        if((owner is "Page" or "MasterPage" or "UserControlPage") && leaves.Any(model=>!pageType.IsInstanceOfType(model)) || leaves.Distinct(ReferenceEqualityComparer.Instance).Count()!=leaves.Length)
            throw new BridgeException("NativeFolderPlanUnavailable","The native folder contains an unknown or duplicate child model.");
        ModuleAddress Target(Item item)
        {
            if(item.Address==current.Address) return destination;
            var oldLogical=(ParentPath(current.Address) is {Length:>0} oldParent ? oldParent+"/" : "")+current.Address.Name;
            var newLogical=(ParentPath(destination) is {Length:>0} newParent ? newParent+"/" : "")+destination.Name;
            var parent=ParentPath(item.Address);
            if(parent!=oldLogical && !parent.StartsWith(oldLogical+"/",StringComparison.Ordinal)) throw new BridgeException("NativeFolderPlanUnavailable","A descendant folder lies outside the original subtree.");
            return new(ModuleKind.Folder,item.Address.Name,owner+"/"+newLogical+parent[oldLogical.Length..]);
        }
        (string Name,string Type,string State) Token(object tuple)
        {
            var simple=tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!;
            return ((string)R.Get(simple,"ModuleName")!,R.Get(simple,"ModuleType")!.ToString()!,tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!);
        }
        if(owner=="Table")
        {
            var selected=leaves.Select(leaf=>(string)R.Get(R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",leaf)!,"ModuleName")!).ToHashSet(StringComparer.Ordinal);
            var normalized=new Dictionary<(string Name,string Type),object>();
            foreach(var change in changes)
            {
                var token=Token(change); var key=(token.Name,token.Type);
                if(!normalized.TryGetValue(key,out var previous)) normalized.Add(key,change);
                else if(!Equals(R.Get(change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!,"ExclusiveType"),R.Get(previous.GetType().GetField("Item1",R.Flags)!.GetValue(previous)!,"ExclusiveType")) || !change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!.Equals(previous.GetType().GetField("Item1",R.Flags)!.GetValue(previous)))
                    throw new BridgeException("NativeFolderPlanUnavailable","Native table move owner identities disagree.");
                else if(Token(previous)!=token)
                {
                    if(!selected.Contains(token.Name) || !new[]{Token(previous).State,token.State}.ToHashSet(StringComparer.Ordinal).SetEquals(new[]{"Move","Edit"}))
                        throw new BridgeException("NativeFolderPlanUnavailable","Native table move owners disagree.");
                    if(token.State=="Move") normalized[key]=change;
                }
            }
            changes=normalized.Values.ToArray();
        }
        var old=descendants.ToDictionary(item=>NativeName(item.Address),StringComparer.Ordinal);
        var next=descendants.ToDictionary(item=>NativeName(Target(item)),StringComparer.Ordinal);
        var folderType=R.Static(assembly.GetType("Forguncy.Common.ModuleExtensions",true)!,"GetModuleType",root)!.ToString()!;
        var folders=changes.Where(change=>Token(change).Type==folderType).ToArray(); var tokens=folders.Select(Token).ToArray();
        if(tokens.Length!=old.Count+next.Count || tokens.Select(token=>token.Name).Distinct(StringComparer.Ordinal).Count()!=tokens.Length ||
            tokens.Any(token=>token.State=="Remove" ? !old.ContainsKey(token.Name) : token.State=="New" ? !next.ContainsKey(token.Name) : true))
            throw new BridgeException("NativeFolderPlanUnavailable","The native page-folder update owner set does not match its complete subtree.");
        var otherChanges=changes.Except(folders).ToArray(); var otherPlan=otherChanges.Length==0?null:files.CaptureNativeModulesCore(otherChanges,true);
        if(otherChanges.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group))))
            throw new BridgeException("NativeFolderPlanUnavailable","A native page or related owner lacks a verified source group.");
        var extension=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        foreach(var leaf in leaves)
        {
            var name=(string)R.Static(extension,"GetModuleName",leaf,null)!;
            if(otherPlan is null || !otherPlan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,leaf)) ||
                otherChanges.Count(change=>Token(change).Name==name && Token(change).State=="Move")!=1)
                throw new BridgeException("NativeFolderPlanUnavailable","Every descendant page requires its actual native Move owner and source group.");
        }
        var directories=descendants.Select(item=>item.Key).ToArray();
        var paths=(otherPlan?.BeforeGroups ?? []).SelectMany(group=>group.RequiredFiles.Concat(group.ReferencedFiles)).Where(path=>path.StartsWith(current.Key,StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        bool TreeCurrent()=>(owner!="Table" && !IsImage || NativeRegisteredFolderEvidence.TreeMatches(Local,current.Key,directories,paths)) && NativeRegisteredFolderEvidence.TreeMatches(Stage,current.Key,directories,paths) && NativeRegisteredFolderEvidence.TreeMatches(Source,current.Key,directories,paths) &&
            descendants.All(item=>Registered(item.Key)) && (otherPlan is null || otherPlan.BeforeGroups.All(files.Verify));
        if(!TreeCurrent()) throw new BridgeException("NativeFolderSourceChanged","The page-folder subtree differs from its saved native inventory.");
        var before=descendants.Select(item=>new NativeModuleFileGroup(item.Address,NativeName(item.Address),[],[],[],new Dictionary<string,string>(),
            ()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model) && found.Address==item.Address) && TreeCurrent(),nativeIdentity:item.Model,
            isPresent:()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model)),requiredDirectories:[item.Key.TrimEnd('/')],directoriesRequireLocal:owner=="Table" || IsImage)).ToArray();
        if(before.Any(group=>!files.Verify(group))) throw new BridgeException("NativeFolderSourceChanged","A page-folder subtree owner does not match its saved source.");
        var created=next.Keys.Select(name=>(name,folderType)).ToArray(); var targets=next.ToDictionary(pair=>pair.Key,pair=>Target(pair.Value),StringComparer.Ordinal);
        return new(new(before.Concat(otherPlan?.BeforeGroups ?? []).ToArray(),created,targets,changes),before.Single(group=>ReferenceEquals(group.NativeIdentity,current.Model)),otherPlan,destination,before,PageFileContext:files);
    }

    private IReadOnlyList<string> ApplyPageUpdate(ModuleMutationRequest request,NativeFolderLifecyclePlan capture)
    {
        var (current,destination)=Prepare(request,true);
        if(current is null || capture.Before is null || !ReferenceEquals(current.Model,capture.Before.NativeIdentity) || capture.Plan.BeforeGroups.Any(group=>!group.Current()))
            throw new BridgeException("NativeFolderSourceChanged","The page-folder subtree changed after checkout.");
        if(!IsImage) capture.PageFileContext!.RequirePageFolderManagerContext(Enum.Parse<ModuleKind>(owner));
        var parent=Parent(ParentPath(destination));
        if(IsImage)
        {
            var manager=ImageManager();
            if(ReferenceEquals(parent,current.Parent)) { R.Call(manager,"RenameFolder",current.Model,destination.Name); R.Set(current.Model,"Name",destination.Name); Dirty(current.Model,"Edit"); }
            else
            {
                var target=ParentPath(destination).Length==0 ? root : Find(new(ModuleKind.Folder,ParentPath(destination).Split('/').Last(),owner+(ParentPath(destination).Contains('/')?"/"+ParentPath(destination)[..ParentPath(destination).LastIndexOf('/')]:""))).Model;
                R.Call(manager,"MoveFolder",current.Model,target); current.Parent.Remove(current.Model); parent.Add(current.Model); Dirty(current.Model,"Move");
            }
        }
        else if(ReferenceEquals(parent,current.Parent))
        { R.Set(current.Model,"Name",destination.Name); R.Call(colleague,"RenameFolder",current.Model,request.Address.Name); if(wholeCache) R.Call(colleague,"SaveToCache",true); Dirty(current.Model,"Edit"); }
        else
        {
            R.Call(colleague,"DeleteFolder",current.Model); current.Parent.Remove(current.Model); parent.Add(current.Model);
            R.Call(colleague,"InsertFolder",current.Model); R.Call(colleague,"SaveToCache",true); Dirty(current.Model,"Move");
        }
        foreach(var group in capture.FolderBefore!) foreach(var path in group.RequiredDirectories) touched.Add(path+"/");
        foreach(var target in capture.Plan.CreatedTargets!.Values) touched.Add(Key(target));
        results[request.OperationId]=destination;
        return capture.Plan.BeforeGroups.SelectMany(group=>group.Paths.Concat(group.RequiredDirectories)).Concat(capture.Plan.CreatedTargets.Values.Select(Key)).Distinct(StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<NativeModuleFileGroup> PageUpdateAfter(NativeFolderLifecyclePlan capture,NativeModuleFiles files)
    {
        var result=new List<NativeModuleFileGroup>();
        if(capture.OtherOwners is not null) result.AddRange(files.ResolveNativeModulesAfter(capture.OtherOwners) ?? throw new BridgeException("NativeFolderReadbackUnavailable","Native page owners could not be read back."));
        foreach(var before in capture.FolderBefore!)
        {
            var matches=capture.Plan.CreatedTargets!.Where(pair=>Items().Any(item=>item.Address==pair.Value && ReferenceEquals(item.Model,before.NativeIdentity))).ToArray();
            if(matches.Length!=1) throw new BridgeException("NativeFolderReadbackMismatch","A native descendant folder did not reach its planned address.");
            var target=Find(matches[0].Value); var oldToken=capture.Plan.NativeChanges!.Select(change=>change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!).Single(native=>(string)R.Get(native,"ModuleName")! == before.NativeModuleName);
            bool OldPresent()=>Items().Any(item=>item.Address==before.Address && ReferenceEquals(item.Model,before.NativeIdentity));
            bool NewPresent()=>Items().Any(item=>item.Address==target.Address && ReferenceEquals(item.Model,target.Model)) && Registered(target.Key);
            result.Add(new(before.Address,before.NativeModuleName,[],[],[],new Dictionary<string,string>(),()=>!OldPresent() && before.RequiredDirectories.All(path=>!Registered(path+"/")),nativeIdentity:oldToken,isPresent:OldPresent,absentDirectories:before.RequiredDirectories,directoriesRequireLocal:owner=="Table" || IsImage));
            result.Add(new(target.Address,matches[0].Key,[],[],[],new Dictionary<string,string>(),NewPresent,nativeIdentity:target.Model,isPresent:NewPresent,requiredDirectories:[target.Key.TrimEnd('/')],directoriesRequireLocal:owner=="Table" || IsImage));
        }
        return result;
    }
}
