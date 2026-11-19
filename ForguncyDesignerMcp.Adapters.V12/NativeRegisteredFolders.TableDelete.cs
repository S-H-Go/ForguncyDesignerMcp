using System.Collections;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed partial class NativeRegisteredFolders
{
    private NativeFolderLifecyclePlan CaptureTableDelete(ModuleMutationRequest request,NativeModuleFiles files,Item current,ModuleAddress destination,object[] changes)
    {
        var descendants=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
        var leaves=descendants.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
        var tableType=System.Reflection.Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.Table",true)!;
        if(leaves.Any(leaf=>leaf.GetType()!=tableType || R.Get(leaf,"ExternalDatabaseInfo") is not null || R.Get(leaf,"IsLinkedTableOrLinkedView") is true || R.Get(leaf,"IsCopyExternalBuiltInTable") is true) ||
            leaves.Distinct(ReferenceEqualityComparer.Instance).Count()!=leaves.Length)
            throw new BridgeException("NativeFolderPlanUnavailable","Only distinct internal native table definitions may be deleted in this group.");
        var settings=R.Get(R.Static(assembly.GetType("s",true)!,"get_Instance")!,"et")!;
        if(!ReferenceEquals(R.Get(R.Get(settings,"TableSet")!,"Root"),root)) throw new BridgeException("NativeFolderPlanUnavailable","The native table manager belongs to another project.");
        RequireSqliteViewsContained(leaves);
        var simpleType=assembly.GetType("Forguncy.Common.SimpleModule",true)!;
        var selected=leaves.Select(leaf=>R.Static(simpleType,"Create",leaf)!).ToArray();
        var leafNames=selected.Select(simple=>(string)R.Get(simple,"ModuleName")!).ToHashSet(StringComparer.Ordinal);
        (string Name,string Type,string State) Token(object tuple)
        {
            var simple=tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!;
            return ((string)R.Get(simple,"ModuleName")!,R.Get(simple,"ModuleType")!.ToString()!,tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!);
        }
        var all=new List<object>(); var requiredOwned=new HashSet<string>(StringComparer.Ordinal);
        void MergeChange(object change)
        {
            var token=Token(change); var old=all.Where(value=>Token(value).Name==token.Name).ToArray();
            if(old.Length==0) {all.Add(change); return;}
            if(old.Length!=1 || !Equals(R.Get(change.GetType().GetField("Item1")!.GetValue(change)!,"ExclusiveType"),R.Get(old[0].GetType().GetField("Item1")!.GetValue(old[0])!,"ExclusiveType")) || !change.GetType().GetField("Item1")!.GetValue(change)!.Equals(old[0].GetType().GetField("Item1")!.GetValue(old[0])))
                throw new BridgeException("NativeFolderPlanUnavailable","Native table owner identities disagree.");
            if(Token(old[0])==token) return;
            if(!leafNames.Contains(token.Name) || Token(old[0]).Type!=token.Type || !new[]{Token(old[0]).State,token.State}.ToHashSet(StringComparer.Ordinal).SetEquals(new[]{"Edit","Remove"}))
                throw new BridgeException("NativeFolderPlanUnavailable","Native table owner effects disagree.");
            if(token.State=="Remove") {all.Remove(old[0]); all.Add(change);}
        }
        foreach(var change in changes) MergeChange(change);
        var app=R.Get(settings,"ApplicationSettings")!;
        foreach(var leaf in leaves)
        {
            var name=(string)R.Get(leaf,"Name")!; var view=R.Get(leaf,"IsView") is true;
            var helper=new NativeTableDefinitionEffects(assembly);
            var effect=helper.TryResolveDeletion(view?"table.view":"table",view?"delete":"deleteTable",
                view?JsonSerializer.SerializeToElement(new {viewName=name,deleteDatabaseObject=false}):JsonSerializer.SerializeToElement(new {tableName=name}),settings,files.RequirePureReferenceContext)
                ?? throw new BridgeException("NativeFolderPlanUnavailable",name+": "+(helper.UnavailableReason ?? "The native table deletion plan is unavailable."));
            var locations=((IEnumerable)R.Static(assembly.GetType("ul",true)!,"FindTableReference",leaf,false)!).Cast<object>().ToArray();
            if(locations.Length!=effect.NativeIncomingReferenceCount) throw new BridgeException("NativeFolderReferenceUnavailable","The native table reference inventory changed while planning.");
            foreach(var location in locations)
            {
                var locationType=location.GetType(); while(locationType.FullName!="GrapeCity.Forguncy.Plugin.LocationIndicator") locationType=locationType.BaseType ?? throw new BridgeException("NativeFolderReferenceUnavailable","Unknown table reference location.");
                var chain=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(locationType))!;
                foreach(var item in (IEnumerable)R.Call(location,"Enum")!) chain.Add(item);
                var mapped=((IEnumerable)R.Static(assembly.GetType("Forguncy.Common.ReferenceFinder",true)!,"EnumEditedModulesByReferences",chain)!).Cast<object>().ToArray();
                if(mapped.Length==0) throw new BridgeException("NativeFolderReferenceUnavailable","A native table reference has no owning module.");
                if(mapped.Any(module=>!selected.Any(value=>value.Equals(module)))) throw new BridgeException("NativeFolderExternalReference","A module outside the selected table subtree references a table being deleted.");
            }
            foreach(var entry in effect.Effects)
            {
                if(ReferenceEquals(entry.Model,app)) requiredOwned.Add((string)R.Get(entry.Module,"ModuleName")!);
                MergeChange(entry.NativeTuple);
            }
        }
        changes=all.ToArray(); var folderType=R.Static(assembly.GetType("Forguncy.Common.ModuleExtensions",true)!,"GetModuleType",root)!.ToString()!;
        var folderChanges=changes.Where(change=>Token(change).Type==folderType).ToArray(); var expected=descendants.ToDictionary(item=>NativeName(item.Address),StringComparer.Ordinal);
        var tokens=folderChanges.Select(Token).ToArray();
        if(tokens.Length!=expected.Count || tokens.Any(token=>token.State!="Remove" || !expected.ContainsKey(token.Name)) || tokens.Select(token=>token.Name).Distinct(StringComparer.Ordinal).Count()!=expected.Count)
            throw new BridgeException("NativeFolderPlanUnavailable","The native table folder owners differ from the loaded subtree.");
        var otherChanges=changes.Except(folderChanges).ToArray(); var otherPlan=otherChanges.Length==0?null:files.CaptureNativeModulesCore(otherChanges,true);
        if(otherChanges.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group))) ||
            leaves.Any(leaf=>otherPlan is null || !otherPlan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,leaf)) || otherChanges.Count(change=>Token(change).Name==(string)R.Get(R.Static(simpleType,"Create",leaf)!,"ModuleName")! && Token(change).State=="Remove")!=1))
            throw new BridgeException("NativeFolderPlanUnavailable","A native table or shared database owner is missing or stale.");
        var directories=descendants.Select(item=>item.Key).ToArray();
        var paths=(otherPlan?.BeforeGroups ?? []).SelectMany(group=>group.RequiredFiles.Concat(group.ReferencedFiles)).Where(path=>path.StartsWith(current.Key,StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        bool TreeCurrent()
        {
            var now=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
            var nowLeaves=now.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
            return now.Length==descendants.Length && now.All(item=>descendants.Any(old=>ReferenceEquals(old.Model,item.Model) && old.Key==item.Key)) && nowLeaves.Length==leaves.Length && nowLeaves.All(leaf=>leaves.Any(old=>ReferenceEquals(old,leaf))) &&
                NativeRegisteredFolderEvidence.TreeMatches(Local,current.Key,directories,paths) && NativeRegisteredFolderEvidence.TreeMatches(Stage,current.Key,directories,paths) && NativeRegisteredFolderEvidence.TreeMatches(Source,current.Key,directories,paths) &&
                descendants.All(item=>Registered(item.Key)) && (otherPlan is null || otherPlan.BeforeGroups.All(files.Verify));
        }
        if(!TreeCurrent()) throw new BridgeException("NativeFolderSourceChanged","The table subtree or shared database changed.");
        var folders=descendants.Select(item=>new NativeModuleFileGroup(item.Address,NativeName(item.Address),[],[],[],new Dictionary<string,string>(),
            ()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model) && found.Address==item.Address) && TreeCurrent(),nativeIdentity:item.Model,
            isPresent:()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model)),requiredDirectories:[item.Key.TrimEnd('/')],directoriesRequireLocal:true)).ToArray();
        return new(new(folders.Concat(otherPlan?.BeforeGroups ?? []).ToArray(),[],nativeChanges:changes),folders.Single(group=>ReferenceEquals(group.NativeIdentity,current.Model)),otherPlan,destination,folders,PageFileContext:files,PageDeleteModels:leaves,RequiredOwnedModules:requiredOwned.ToArray());
    }

    internal async Task<IReadOnlyList<string>> ApplyAsync(ModuleMutationRequest request,NativeFolderLifecyclePlan capture)
    {
        if(owner!="Table" || request.Operation!=ModuleOperation.Delete || capture.FolderBefore is null) return Apply(request,capture);
        var (current,destination)=Prepare(request,true); var files=capture.PageFileContext!; var leaves=capture.PageDeleteModels!;
        if(current is null || !ReferenceEquals(current.Model,capture.Before?.NativeIdentity) || capture.Plan.BeforeGroups.Any(group=>!group.Current()))
            throw new BridgeException("NativeFolderSourceChanged","The table subtree changed after its owner plan.");
        files.RequirePageFolderManagerContext(ModuleKind.Table);
        var latest=CaptureTableDelete(request,files,current,destination,capture.Plan.NativeChanges!.ToArray());
        if(latest.Plan.BeforeGroups.Count!=capture.Plan.BeforeGroups.Count || latest.Plan.BeforeGroups.Any(group=>!capture.Plan.BeforeGroups.Any(old=>ReferenceEquals(old.NativeIdentity,group.NativeIdentity))) ||
            !(latest.RequiredOwnedModules ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(capture.RequiredOwnedModules ?? []))
            throw new BridgeException("NativeFolderSourceChanged","The complete native table owner set changed after checkout.");
        var state=R.Static(assembly.GetType("s",true)!,"get_Instance")!;
        var manager=R.Get(state,"eq") ?? throw new BridgeException("NativeFolderPlanUnavailable","The native table status manager is unavailable.");
        foreach(var leaf in leaves)
        {
            var status=Convert.ToInt32(R.Call(manager,"GetTableStatus",(string)R.Get(leaf,"Name")!));
            // Native CheckDeleteTable rejects process use, missing and other
            // used tables (32, 2, 1). Its flag 8 is an incoming relation;
            // the complete grouped reference scan above decides that case.
            if((status & (32|2|1))!=0) throw new BridgeException("NativeFolderChildDeleteRejected","The native table status does not permit deletion.");
        }
        foreach(var leaf in OrderTableDeletion(leaves))
        {
            var parent=R.Get(leaf,"Owner") ?? throw new BridgeException("NativeFolderPlanUnavailable","The native table parent collection is unavailable.");
            var action=R.Get(leaf,"IsView") is true ? R.Static(assembly.GetType("mz",true)!,"DeleteViewAsync",parent,leaf,true,false) : R.Static(assembly.GetType("mz",true)!,"DeleteTableAsync",parent,leaf);
            await ((Task)(action ?? throw new BridgeException("NativeFolderDeleteUnconfirmed","The native asynchronous table action is unavailable.")));
        }
        R.Call(colleague,"DeleteFolder",current.Model); current.Parent.Remove(current.Model); R.Call(colleague,"SaveToCache",true); DirtyPlan(capture.Plan);
        foreach(var group in capture.FolderBefore) foreach(var directory in group.RequiredDirectories) touched.Add(directory+"/");
        results[request.OperationId]=null;
        return capture.Plan.BeforeGroups.SelectMany(group=>group.Paths.Concat(group.RequiredDirectories)).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<object> OrderTableDeletion(IReadOnlyList<object> leaves) =>
        leaves.OrderBy(leaf=>R.Get(leaf,"IsView") is true ? 0 : 1).ToArray();
}
