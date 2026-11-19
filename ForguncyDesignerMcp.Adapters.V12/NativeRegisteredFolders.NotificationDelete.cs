using System.Collections;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed partial class NativeRegisteredFolders
{
    private NativeFolderLifecyclePlan CaptureNotificationDelete(ModuleMutationRequest request,NativeModuleFiles files,Item current,ModuleAddress destination,object[] changes)
    {
        var descendants=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
        var leaves=descendants.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
        var type=System.Reflection.Assembly.Load("ServerDesignerCommon").GetType(owner switch {"Notification"=>"Forguncy.Notification.NotificationModel","SharedCommand"=>"Forguncy.Model.ShareCommandContainer",_=>"Forguncy.ServerCommands.Models.ServerCommands"},true)!;
        if(leaves.Any(leaf=>!type.IsInstanceOfType(leaf)) || leaves.Distinct(ReferenceEqualityComparer.Instance).Count()!=leaves.Length)
            throw new BridgeException("NativeFolderPlanUnavailable","The notification subtree contains an unknown or duplicate definition.");
        (string Name,string Type,string State) Token(object tuple)
        {
            var simple=tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!;
            return ((string)R.Get(simple,"ModuleName")!,R.Get(simple,"ModuleType")!.ToString()!,tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!);
        }
        var combined=changes.ToList();
        var leafNames=leaves.Select(leaf=>(string)R.Get(R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",leaf)!,"ModuleName")!).ToHashSet(StringComparer.Ordinal);
        if(owner!="Notification") RequireCommandReferencesContained(files,leaves);
        foreach(var leaf in leaves)
        {
            var name=(string)R.Get(leaf,"Name")!;
            object[] leafChanges;
            if(owner=="Notification")
            {
                var capture=files.CaptureApiBefore("notification","deleteNotification",JsonSerializer.SerializeToElement(new {name}))
                    ?? throw new BridgeException("NativeFolderPlanUnavailable","The native notification deletion resolver is unavailable.");
                leafChanges=(capture.Plan?.NativeChanges ?? throw new BridgeException("NativeFolderPlanUnavailable","The native notification deletion owner is unavailable.")).ToArray();
            }
            else
            {
                var (ns,method,resolverName)=owner switch {"ServerCommand"=>("serverCommand","deleteServerCommand","abe+az"),"SharedCommand"=>("sharedCommand","deleteSharedCommand","abe+bi"),_=>("scheduledTask","deleteScheduledTask","abe+ca")};
                var descriptors=((IEnumerable)R.Static(assembly.GetType("aam",true)!,"GetCurrentAssemblyDescriptors")!).Cast<object>();
                var descriptor=descriptors.Single(d=>R.Get(d,"Namespace") as string==ns && R.Get(d,"Method") as string==method);
                var methodInfo=(System.Reflection.MethodInfo)R.Get(descriptor,"MethodInfo")!;
                var resolver=methodInfo.GetCustomAttributesData().Single(a=>a.AttributeType.FullName=="abc").ConstructorArguments.Single().Value as Type;
                if(resolver?.Assembly!=assembly || resolver.FullName!=resolverName) throw new BridgeException("NativeFolderPlanUnavailable","The native definition resolver changed.");
                var dto=Activator.CreateInstance((Type)R.Get(descriptor,"RequestType")!)!; R.Set(dto,"Name",name);
                leafChanges=((IEnumerable)R.Call(Activator.CreateInstance(resolver)!,"Resolve",dto)!).Cast<object>().ToArray();
            }
            foreach(var change in leafChanges)
            {
                var token=Token(change); var existing=combined.Where(item=>Token(item).Name==token.Name).ToArray();
                if(existing.Length==0) combined.Add(change);
                else if(existing.Length!=1 || !Equals(R.Get(change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!,"ExclusiveType"),R.Get(existing[0].GetType().GetField("Item1",R.Flags)!.GetValue(existing[0])!,"ExclusiveType")) || !change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!.Equals(existing[0].GetType().GetField("Item1",R.Flags)!.GetValue(existing[0])))
                    throw new BridgeException("NativeFolderPlanUnavailable","Native deletion owner identities disagree.");
                else if(Token(existing[0])!=token)
                {
                    if(existing.Length!=1 || !leafNames.Contains(token.Name) || Token(existing[0]).Type!=token.Type || !new[]{Token(existing[0]).State,token.State}.ToHashSet(StringComparer.Ordinal).SetEquals(new[]{"Edit","Remove"}))
                        throw new BridgeException("NativeFolderPlanUnavailable","Native deletion owners disagree.");
                    if(token.State=="Remove") { combined.Remove(existing[0]); combined.Add(change); }
                }
            }
        }
        changes=combined.ToArray();
        var folderType=R.Static(assembly.GetType("Forguncy.Common.ModuleExtensions",true)!,"GetModuleType",root)!.ToString()!;
        var folderChanges=changes.Where(change=>Token(change).Type==folderType).ToArray();
        var expected=descendants.ToDictionary(item=>NativeName(item.Address),StringComparer.Ordinal);
        var tokens=folderChanges.Select(Token).ToArray();
        if(tokens.Length!=expected.Count || tokens.Any(token=>token.State!="Remove" || !expected.ContainsKey(token.Name)) || tokens.Select(token=>token.Name).Distinct(StringComparer.Ordinal).Count()!=expected.Count)
            throw new BridgeException("NativeFolderPlanUnavailable","The notification folder owners do not match the loaded subtree.");
        var otherChanges=changes.Except(folderChanges).ToArray();
        var otherPlan=otherChanges.Length==0?null:files.CaptureNativeModulesCore(otherChanges,true);
        if(otherChanges.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group))) ||
            leaves.Any(leaf=>otherPlan is null || !otherPlan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,leaf)) ||
                otherChanges.Count(change=>Token(change).Name==(string)R.Get(R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",leaf)!,"ModuleName")! && Token(change).State=="Remove")!=1))
            throw new BridgeException("NativeFolderPlanUnavailable","A notification leaf lacks its native verified source owner.");
        var directories=descendants.Select(item=>item.Key).ToArray();
        var paths=(otherPlan?.BeforeGroups ?? []).SelectMany(group=>group.RequiredFiles.Concat(group.ReferencedFiles)).Where(path=>path.StartsWith(current.Key,StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        bool TreeCurrent()
        {
            var now=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
            var nowLeaves=now.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
            return now.Length==descendants.Length && now.All(item=>descendants.Any(before=>ReferenceEquals(before.Model,item.Model) && before.Key==item.Key)) &&
                nowLeaves.Length==leaves.Length && nowLeaves.All(model=>leaves.Any(before=>ReferenceEquals(before,model))) &&
                NativeRegisteredFolderEvidence.TreeMatches(Stage,current.Key,directories,paths) && NativeRegisteredFolderEvidence.TreeMatches(Source,current.Key,directories,paths) &&
                descendants.All(item=>Registered(item.Key)) && (otherPlan is null || otherPlan.BeforeGroups.All(files.Verify));
        }
        if(!TreeCurrent()) throw new BridgeException("NativeFolderSourceChanged","The notification subtree differs from its saved native inventory.");
        var folders=descendants.Select(item=>new NativeModuleFileGroup(item.Address,NativeName(item.Address),[],[],[],new Dictionary<string,string>(),
            ()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model) && found.Address==item.Address) && TreeCurrent(),nativeIdentity:item.Model,
            isPresent:()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model)),requiredDirectories:[item.Key.TrimEnd('/')],directoriesRequireLocal:false)).ToArray();
        return new(new(folders.Concat(otherPlan?.BeforeGroups ?? []).ToArray(),[],nativeChanges:changes),folders.Single(group=>ReferenceEquals(group.NativeIdentity,current.Model)),otherPlan,destination,folders,PageFileContext:files,PageDeleteModels:leaves);
    }

    private IReadOnlyList<string> ApplyNotificationDelete(ModuleMutationRequest request,NativeFolderLifecyclePlan capture)
    {
        var (current,_)=Prepare(request,true);
        var files=capture.PageFileContext!; var leaves=capture.PageDeleteModels!;
        if(current is null || !ReferenceEquals(current.Model,capture.Before?.NativeIdentity) || capture.Plan.BeforeGroups.Any(group=>!group.Current()))
            throw new BridgeException("NativeFolderSourceChanged","The notification subtree changed after checkout.");
        if(owner=="Notification") foreach(var leaf in leaves)
            _=files.CaptureApiBefore("notification","deleteNotification",JsonSerializer.SerializeToElement(new {name=(string)R.Get(leaf,"Name")!}));
        else RequireCommandReferencesContained(files,leaves);
        foreach(var item in Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)))
        {
            var children=(IList)R.Get(item.Model,"Items")!;
            foreach(var leaf in children.Cast<object>().Where(child=>leaves.Any(value=>ReferenceEquals(value,child))).ToArray()) { R.Call(colleague,"Delete",leaf); children.Remove(leaf); }
        }
        R.Call(colleague,"DeleteFolder",current.Model); current.Parent.Remove(current.Model); R.Call(colleague,"SaveToCache",true); DirtyPlan(capture.Plan);
        foreach(var group in capture.FolderBefore!) foreach(var directory in group.RequiredDirectories) touched.Add(directory+"/");
        results[request.OperationId]=null;
        return capture.Plan.BeforeGroups.SelectMany(group=>group.Paths.Concat(group.RequiredDirectories)).Distinct(StringComparer.Ordinal).ToArray();
    }
}
