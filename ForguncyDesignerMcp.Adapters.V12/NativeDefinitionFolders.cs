using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed record NativeFolderLifecyclePlan(NativeModuleFilePlan Plan, NativeModuleFileGroup? Before,
    NativeModuleFilePlan? OtherOwners, ModuleAddress Destination, IReadOnlyList<NativeModuleFileGroup>? FolderBefore = null,
    NativeReports? DeleteStore = null, IReadOnlyList<ModuleMutationRequest>? DeleteLeaves = null, IReadOnlySet<ModuleAddress>? DeleteOwners = null,
    IReadOnlyDictionary<object,Func<NativeModuleFileGroup,NativeModuleFileGroup>>? RetainedDatabases = null,NativeModuleFiles? PageFileContext = null,
    IReadOnlyList<object>? PageDeleteModels = null, IReadOnlyList<string>? RequiredOwnedModules = null);

// Native report/process directories. Rename and move capture every descendant
// owner and the exact physical subtree; deletion remains limited to empty folders.
public sealed class NativeDefinitionFolders
{
    private sealed class Environment(object colleague,object mediator,object save,object root,ModuleKind owner)
    {
        private (string Local,string Source)? paths;
        public object Colleague { get; }=colleague; public object Mediator { get; }=mediator; public object Save { get; private set; }=save; public object Root { get; }=root; public ModuleKind Owner { get; }=owner;
        public string Local=>Paths().Local; public string Source=>Paths().Source;
        private (string Local,string Source) Paths()
        {
            if(paths is { } existing) return existing;
            Save=R.Get(R.Get(Mediator,"SaveDataColleague")!,"SaveLoadFile")!;
            var local=(string?)R.Get(Save,"LocalPath"); var source=(string?)R.Call(Save,"GetDocumentPath");
            if(string.IsNullOrWhiteSpace(local)||string.IsNullOrWhiteSpace(source)) throw new BridgeException("DefinitionCacheNotReady","Native folder source/cache paths are not initialized; wait for native initialization before reading files or writing.");
            var result=(Local:Path.GetFullPath(local),Source:Path.GetFullPath(source));
            if(result.Local.Equals(result.Source,StringComparison.OrdinalIgnoreCase)) throw new BridgeException("FolderCacheMismatch","The native working cache cannot be its source project.");
            return (paths=result).Value;
        }
    }
    private sealed record Item(Environment Environment, object Model, IList Parent, int Index, ModuleAddress Address, string RelativePath);
    private readonly Assembly assembly;
    private readonly object? document;
    private readonly Dictionary<ModuleKind, Environment> environments = [];
    private sealed record Result(ModuleAddress Address, IReadOnlyList<string> Lineage);
    private readonly Dictionary<string, Result> results = new(StringComparer.Ordinal);
    private readonly Dictionary<ModuleKind, HashSet<string>> touchedPaths = [];
    private readonly HashSet<string> deletedResults = new(StringComparer.Ordinal);

    public NativeDefinitionFolders(Assembly assembly, object document) { this.assembly = assembly; this.document = document; }
    public NativeDefinitionFolders(Assembly assembly, object colleague, ModuleKind owner)
    { this.assembly = assembly; environments.Add(owner, Bind(colleague, owner)); }
    public static bool Supports(ModuleAddress address) => address.Kind == ModuleKind.Folder && address.FolderPath?.Split('/')[0] is "Report" or "BusinessProcess";
    public static bool Supports(ModuleQuery query) => query.Kind == ModuleKind.Folder &&
        (query.FolderPath is null || query.FolderPath.Split('/')[0] is "Report" or "BusinessProcess");

    public ModulePage List(ModuleQuery query)
    {
        if (!Supports(query) || query.Limit is < 1 or > 1000) throw new BridgeException("InvalidFolderQuery", "FolderPath must be omitted or start with Report or BusinessProcess.");
        IEnumerable<Item> selected;
        if (query.FolderPath is null)
        {
            // An unfiltered module query enumerates both supported definition
            // trees, retaining the owner on every address. Native binding errors
            // must propagate; a missing tree cannot become an empty result.
            var owners = document is null ? environments.Keys.ToArray() : [ModuleKind.Report, ModuleKind.BusinessProcess];
            selected = owners.SelectMany(owner => Items(Env(owner)));
        }
        else
        {
            var (owner, parent) = ParseOwner(query.FolderPath);
            selected = Items(Env(owner)).Where(x => ParentPath(x.Address) == parent);
        }
        var all = selected.OrderBy(x => x.RelativePath, StringComparer.Ordinal).ToArray();
        var offset = query.Cursor is null ? 0 : int.TryParse(query.Cursor, out var cursor) && cursor >= 0 ? cursor : throw new BridgeException("InvalidCursor", "Invalid folder cursor.");
        return new(all.Skip(offset).Take(query.Limit).Select(x => new ModuleSummary(x.Address, Read(x.Address).Revision)).ToArray(),
            offset + query.Limit < all.Length ? (offset + query.Limit).ToString() : null);
    }
    public ModuleDocument Read(ModuleAddress address)
    {
        var item = Find(address);
        var children = ((IList)R.Get(item.Model, "Items")!).Cast<object>().Select(x => new { Name = R.Get(x, "Name"), Type = x.GetType().FullName }).ToArray();
        var metadata = JsonSerializer.SerializeToElement(new { Name = R.Get(item.Model, "Name"), ParentFolderPath = ParentPath(address), OwnerKind = item.Environment.Owner.ToString(), Children = children });
        var source = SourceState(item.Environment.Source, item.RelativePath);
        return new(address, metadata, [], NativeFiles.Hash(metadata.GetRawText() + "\n" + R.Get(item.Model, "Weight") + "\n" + source));
    }
    public string GetModuleName(ModuleAddress address) => NativeFolderName(Env(ParseOwner(address.FolderPath!).Owner),address);
    public ReferenceResult References(ModuleAddress address)
    {
        var item = Find(address);
        return ((IList)R.Get(item.Model, "Items")!).Count == 0 ? new([], true) :
            new([], false, "Nonempty definition folders require a grouped child-reference and checkout operation.");
    }
    public ModuleValidationResult Validate(ModuleMutationRequest request)
    {
        try { Prepare(request,true); return new(true, []); }
        catch (Exception error) { return new(false, [new(error is BridgeException b ? b.Code : "NativeFolderValidation", error.Message)]); }
    }

    // The actual Designer folder executors are used only as read-only effect
    // collectors here. Their actions, checkout and UI commands are not run.
    internal object[] CaptureNativeFolderChanges(ModuleMutationRequest request, bool grouped = false)
    {
        var (environment,current,destination)=Prepare(request,grouped);
        var type=assembly.GetType(environment.Owner==ModuleKind.Report
            ? "Forguncy.Views.ObjectViews.ReportFolderViewModel"
            : "Forguncy.Views.ObjectViews.BusinessProcessFolderViewModel",true)!;
        object Vm(object? parent,object model) => Activator.CreateInstance(type,[parent,model,false])!;
        object ParentVm(string path)
        {
            var folder=environment.Root; var vm=Vm(null,folder);
            foreach(var part in path.Length==0 ? [] : path.Split('/'))
            {
                folder=((IList)R.Get(folder,"Items")!).Cast<object>().Single(item=>item.GetType().GetProperty("Items",R.Flags) is not null && (string?)R.Get(item,"Name")==part);
                vm=Vm(vm,folder);
            }
            return vm;
        }
        object executor;
        if(request.Operation==ModuleOperation.Create)
        {
            var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
            var nativeType=R.Static(extensions,"GetModuleType",environment.Root)!;
            var icon=R.Static(extensions,"GetIconType",environment.Root)!;
            executor=Activator.CreateInstance(assembly.GetType("Forguncy.Views.ObjectViews.CreateItemOrFolderExecutor",true)!,
                [null,ParentVm(ParentPath(destination)),destination.Name,nativeType,icon,null])!;
        }
        else
        {
            var parent=ParentVm(ParentPath(request.Address)); var vm=Vm(parent,current!.Model);
            var itemBase=assembly.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel",true)!;
            var selected=Array.CreateInstance(itemBase,1); selected.SetValue(vm,0);
            if(request.Operation==ModuleOperation.Delete)
                executor=R.Call(vm,"DeleteItemsCollaborationExecutor",selected)!;
            else if(ParentPath(request.Address)==ParentPath(destination))
                executor=R.Call(vm,"RenameCollaborationExecutor",destination.Name)!;
            else
            {
                if(destination.Name!=request.Address.Name)
                    throw new BridgeException("FolderCombinedMoveRenameUnsupported","Move and rename an empty folder in separate operations.");
                executor=Activator.CreateInstance(assembly.GetType("Forguncy.Views.ObjectViews.MoveItemOrFolderExecutor",true)!,
                    [null,selected,ParentVm(ParentPath(destination))])!;
            }
        }
        return (R.Call(executor,"GetRelatedModules") as IEnumerable)?.Cast<object>().ToArray()
            ?? throw new BridgeException("NativeFolderPlanUnavailable","The native folder executor returned no effect plan.");
    }

    internal NativeFolderLifecyclePlan CaptureCollaborationLifecycle(ModuleMutationRequest request, NativeModuleFiles files, NativeReports? deleteStore=null, NativeProcessLifecycle? processLifecycle=null)
    {
        var (environment,current,destination)=Prepare(request,true);
        var changes=CaptureNativeFolderChanges(request,true);
        var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        var nativeType=R.Static(extensions,"GetModuleType",environment.Root)!;
        (string Name,string Type,string State) Token(object tuple)
        {
            var native=tuple.GetType().GetField("Item1",R.Flags)?.GetValue(tuple) ?? throw new BridgeException("NativeFolderPlanUnavailable","Native folder owner is absent.");
            return ((string)R.Get(native,"ModuleName")!,R.Get(native,"ModuleType")!.ToString()!,tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!);
        }
        var oldName=current is null?null:NativeFolderName(environment,request.Address); var newName=request.Operation==ModuleOperation.Delete?null:NativeFolderName(environment,destination);
        var folderType=nativeType.ToString()!;
        if(current is not null && ((IList)R.Get(current.Model,"Items")!).Count>0)
            return CaptureGroupedFolder(request,files,environment,current,destination,changes,folderType,deleteStore,processLifecycle);
        var folderChanges=changes.Where(change=>Token(change).Type==folderType).ToArray();
        var tokens=folderChanges.Select(Token).ToArray();
        if(tokens.Select(token=>token.Name).Distinct(StringComparer.Ordinal).Count()!=tokens.Length ||
            tokens.Any(token=>token.Name!=oldName && token.Name!=newName) ||
            (request.Operation==ModuleOperation.Create && (tokens.Length!=1 || tokens[0].Name!=newName || tokens[0].State!="New")) ||
            (request.Operation==ModuleOperation.Delete && (tokens.Length!=1 || tokens[0].Name!=oldName || tokens[0].State!="Remove")) ||
            (request.Operation==ModuleOperation.Update && (oldName==newName || tokens.Length!=2 ||
                !tokens.Any(token=>token.Name==oldName && token.State=="Remove") ||
                !tokens.Any(token=>token.Name==newName && token.State=="New"))))
            throw new BridgeException("NativeFolderPlanUnavailable","The native folder executor returned an unexpected target owner set.");
        var before=current is null?null:FolderGroup(current,oldName!,[],[current.RelativePath],true);
        if(before is not null && !files.Verify(before)) throw new BridgeException("NativeFolderSourceChanged","The existing native folder does not match local, staging and source directories.");
        var others=changes.Except(folderChanges).ToArray();
        var otherPlan=others.Length==0?null:files.CaptureNativeModulesCore(others,false);
        if(others.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group))))
            throw new BridgeException("NativeFolderPlanUnavailable","Additional native folder owners do not have complete saved source groups.");
        var created=tokens.Where(token=>token.State=="New").Select(token=>(token.Name,token.Type)).ToArray();
        var createdTargets=created.ToDictionary(token=>token.Name,_=>destination,StringComparer.Ordinal);
        var beforeGroups=(before is null?Array.Empty<NativeModuleFileGroup>():[before]).Concat(otherPlan?.BeforeGroups ?? []).ToArray();
        return new(new(beforeGroups,created,createdTargets,changes),before,otherPlan,destination);
    }

    private NativeFolderLifecyclePlan CaptureGroupedFolder(ModuleMutationRequest request,NativeModuleFiles files,Environment environment,
        Item current,ModuleAddress destination,object[] changes,string folderType,NativeReports? deleteStore,NativeProcessLifecycle? processLifecycle)
    {
        var deleting=request.Operation==ModuleOperation.Delete;
        var prefix=current.RelativePath+"/";
        var descendants=Items(environment).Where(item=>item.RelativePath==current.RelativePath || item.RelativePath.StartsWith(prefix,StringComparison.Ordinal)).ToArray();
        ModuleAddress Target(Item item)
        {
            if(item.Address==current.Address) return destination;
            var parent=ParentPath(item.Address);
            var original=ParentPath(current.Address); var oldRoot=(original.Length==0?"":original+"/")+current.Address.Name;
            if(parent!=oldRoot && !parent.StartsWith(oldRoot+"/",StringComparison.Ordinal)) throw new BridgeException("NativeFolderPlanUnavailable","A child folder lies outside its source parent.");
            var newParent=ParentPath(destination); var newRoot=(newParent.Length==0?"":newParent+"/")+destination.Name;
            return new(ModuleKind.Folder,item.Address.Name,environment.Owner+"/"+newRoot+parent[oldRoot.Length..]);
        }
        var old=descendants.ToDictionary(item=>NativeFolderName(environment,item.Address),StringComparer.Ordinal);
        var next=deleting ? new Dictionary<string,Item>(StringComparer.Ordinal) : descendants.ToDictionary(item=>NativeFolderName(environment,Target(item)),StringComparer.Ordinal);
        var folderChanges=changes.Where(tuple=>R.Get(tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!,"ModuleType")!.ToString()==folderType).ToArray();
        var tokens=folderChanges.Select(tuple=>(Name:(string)R.Get(tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!,"ModuleName")!,
            State:tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!)).ToArray();
        if(tokens.Length!=old.Count+next.Count || tokens.Select(token=>token.Name).Distinct(StringComparer.Ordinal).Count()!=tokens.Length ||
            tokens.Any(token=>token.State=="Remove" ? !old.ContainsKey(token.Name) : token.State=="New" ? !next.ContainsKey(token.Name) : true))
            throw new BridgeException("NativeFolderPlanUnavailable","Native grouped folder owners do not exactly match the subtree.");
        var others=changes.Except(folderChanges).ToArray();
        var otherPlan=others.Length==0?null:files.CaptureNativeModulesCore(others,false);
        if(others.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group))))
            throw new BridgeException("NativeFolderPlanUnavailable","A grouped child or related native owner has no complete saved source group.");
        var leaves=descendants.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>())
            .Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
        var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        foreach(var leaf in leaves)
        {
            var name=(string)R.Static(extensions,"GetModuleName",leaf,null)!;
            if(otherPlan is null || !otherPlan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,leaf)) ||
                others.Count(tuple=>(string)R.Get(tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!,"ModuleName")! == name &&
                    tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()==(deleting?"Remove":"Move"))!=1)
                throw new BridgeException("NativeFolderPlanUnavailable","Every descendant definition must have its exact native owner and file group.");
        }
        var dirs=descendants.Select(item=>item.RelativePath).ToArray();
        var paths=(otherPlan?.BeforeGroups ?? []).Where(group=>leaves.Any(leaf=>ReferenceEquals(leaf,group.NativeIdentity)))
            .SelectMany(group=>group.RequiredFiles.Concat(group.ReferencedFiles)).Where(path=>path.StartsWith(prefix,StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        bool TreeCurrent()=>NativeFolderTreeEvidence.Matches(environment.Local,(string)R.Get(environment.Save,"LastPath")!,environment.Source,current.RelativePath,dirs,paths) &&
            (otherPlan is null || otherPlan.BeforeGroups.All(files.Verify));
        if(!TreeCurrent()) throw new BridgeException("NativeFolderSourceChanged","The complete grouped subtree differs from its native owner and source inventory.");
        var groups=descendants.Select(item=>
        {
            var baseGroup=FolderGroup(item,NativeFolderName(environment,item.Address),[],[item.RelativePath]);
            return new NativeModuleFileGroup(baseGroup.Address,baseGroup.NativeModuleName,[],[],[],new Dictionary<string,string>(),
                ()=>baseGroup.Current() && TreeCurrent(),nativeIdentity:baseGroup.NativeIdentity,isPresent:baseGroup.IsPresent,requiredDirectories:baseGroup.RequiredDirectories);
        }).ToArray();
        if(groups.Any(group=>!files.Verify(group))) throw new BridgeException("NativeFolderSourceChanged","A native subtree folder differs from its saved baseline.");
        var created=next.Select(pair=>(pair.Key,folderType)).ToArray();
        var targets=next.ToDictionary(pair=>pair.Key,pair=>Target(pair.Value),StringComparer.Ordinal);
        var all=groups.Concat(otherPlan?.BeforeGroups ?? []).ToArray();
        if(!deleting) return new(new(all,created,targets,changes),groups.Single(group=>ReferenceEquals(group.NativeIdentity,current.Model)),otherPlan,destination,groups);
        if(deleteStore is null) throw new BridgeException("NativeFolderPlanUnavailable","Grouped deletion requires the native definition store.");
        var leafGroups=(otherPlan?.BeforeGroups ?? []).Where(group=>leaves.Any(leaf=>ReferenceEquals(leaf,group.NativeIdentity))).ToArray();
        var ownerSet=leafGroups.Select(group=>new ModuleAddress(group.Address.Kind,group.NativeModuleName)).ToHashSet();
        var leafAddresses=descendants.SelectMany(folder=>((IList)R.Get(folder.Model,"Items")!).Cast<object>()
            .Where(model=>model.GetType().GetProperty("Items",R.Flags) is null)
            .Select(model=>(Model:model,Address:new ModuleAddress(environment.Owner,(string)R.Get(model,"Name")!,
                (ParentPath(folder.Address) is {Length:>0} parent ? parent+"/" : "")+folder.Address.Name)))).ToArray();
        var deleteLeaves=leafGroups.Select((group,index)=>
        {
            var address=leafAddresses.Single(entry=>ReferenceEquals(entry.Model,group.NativeIdentity)).Address;
            return request with {OperationId=request.OperationId+"_leaf_"+index,Address=address,ExpectedModuleRevision=deleteStore.Read(address).Revision,Document=null};
        }).ToArray();
        foreach(var leafRequest in deleteLeaves)
        {
            var validation=deleteStore.ValidateGroupedDelete(leafRequest,ownerSet);
            if(!validation.Valid) throw new BridgeException("NativeFolderChildDeleteRejected",string.Join("; ",validation.Issues.Select(issue=>issue.Code+": "+issue.Message)));
        }
        var retained=new Dictionary<object,Func<NativeModuleFileGroup,NativeModuleFileGroup>>(ReferenceEqualityComparer.Instance);
        if(environment.Owner==ModuleKind.BusinessProcess)
        {
            if(processLifecycle is null) throw new BridgeException("NativeFolderPlanUnavailable","Grouped process deletion requires shared database protection.");
            foreach(var group in leafGroups) if(processLifecycle.CaptureRetainedDatabase(group) is { } keep) retained.Add(group.NativeIdentity,keep);
        }
        return new(new(all,created,targets,changes),groups.Single(group=>ReferenceEquals(group.NativeIdentity,current.Model)),otherPlan,destination,groups,
            deleteStore,deleteLeaves,ownerSet,retained);
    }

    internal IReadOnlyList<NativeModuleFileGroup> ResolveCollaborationLifecycleAfter(ModuleMutationRequest request, NativeFolderLifecyclePlan capture, NativeModuleFiles files)
    {
        var result=new List<NativeModuleFileGroup>();
        if(capture.OtherOwners is not null)
        {
            var owners=files.ResolveNativeModulesAfter(capture.OtherOwners) ?? throw new BridgeException("NativeFolderReadbackUnavailable","An additional native owner could not be read back.");
            result.AddRange(owners.Select(group=>capture.RetainedDatabases is not null && capture.RetainedDatabases.TryGetValue(group.NativeIdentity,out var retain) ? retain(group) : group));
        }
        if(capture.FolderBefore is not null)
        {
            var environment=Env(ParseOwner(capture.Destination.FolderPath!).Owner);
            foreach(var before in capture.FolderBefore)
            {
                if(request.Operation==ModuleOperation.Delete)
                {
                    if(before.IsPresent()) throw new BridgeException("NativeFolderReadbackUnavailable","A deleted descendant folder remains in the native tree.");
                    result.Add(new(before.Address,before.NativeModuleName,[],[],[],new Dictionary<string,string>(),()=>!before.IsPresent(),
                        nativeIdentity:before.NativeIdentity,isPresent:before.IsPresent,absentDirectories:before.RequiredDirectories));
                    continue;
                }
                var matches=capture.Plan.CreatedTargets!.Where(pair=>Items(environment).Any(item=>item.Address==pair.Value && ReferenceEquals(item.Model,before.NativeIdentity))).ToArray();
                if(matches.Length!=1) throw new BridgeException("NativeFolderReadbackUnavailable","A grouped child folder identity did not reach its planned address.");
                var target=Find(matches[0].Value);
                var oldToken=capture.Plan.NativeChanges!.Select(change=>change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!)
                    .Single(native=>(string)R.Get(native,"ModuleName")! == before.NativeModuleName);
                bool OldPresent()=>Items(environment).Any(item=>item.Address==before.Address && ReferenceEquals(item.Model,before.NativeIdentity));
                result.Add(new(before.Address,before.NativeModuleName,[],[],[],new Dictionary<string,string>(),()=>!OldPresent(),
                    nativeIdentity:oldToken,isPresent:OldPresent,absentDirectories:before.RequiredDirectories));
                result.Add(FolderGroup(target,matches[0].Key,[],[target.RelativePath]));
            }
            return result;
        }
        var oldPath=capture.Before?.RequiredDirectories.SingleOrDefault();
        if(request.Operation==ModuleOperation.Delete)
        {
            var before=capture.Before ?? throw new BridgeException("NativeFolderReadbackUnavailable","The deleted folder baseline is missing.");
            if(before.IsPresent()) throw new BridgeException("NativeFolderReadbackUnavailable","The deleted native folder is still present.");
            result.Add(new(before.Address,before.NativeModuleName,[],[],[],new Dictionary<string,string>(),()=>!before.IsPresent(),
                nativeIdentity:before.NativeIdentity,isPresent:before.IsPresent,absentDirectories:[oldPath!]));
        }
        else
        {
            var item=Find(capture.Destination);
            if(capture.Before is not null && !ReferenceEquals(capture.Before.NativeIdentity,item.Model))
                throw new BridgeException("NativeFolderReadbackUnavailable","The renamed or moved native folder identity changed.");
            if(request.Operation==ModuleOperation.Update)
            {
                var before=capture.Before ?? throw new BridgeException("NativeFolderReadbackUnavailable","The original native folder baseline is missing.");
                var oldToken=capture.Plan.NativeChanges!.Select(change=>change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!)
                    .Single(native=>(string)R.Get(native,"ModuleName")! == before.NativeModuleName);
                bool OldOwnerPresent()=>Items(item.Environment).Any(candidate=>ReferenceEquals(candidate.Model,before.NativeIdentity) && candidate.Address==before.Address);
                // Rename/move creates distinct native Remove and New owners.
                // Keep a separately addressable old-owner tombstone so check-in
                // can verify both registrations, without fabricating a module.
                result.Add(new(before.Address,before.NativeModuleName,[],[],[],new Dictionary<string,string>(),()=>!OldOwnerPresent(),
                    nativeIdentity:oldToken,isPresent:OldOwnerPresent,absentDirectories:[oldPath!]));
            }
            var targetName=capture.Plan.ExpectedNewModules.Single().Name;
            result.Add(FolderGroup(item,targetName,[],[item.RelativePath]));
        }
        return result;
    }

    private NativeModuleFileGroup FolderGroup(Item item,string nativeName,IReadOnlyList<string> absent,IReadOnlyList<string> required,bool savedBaseline=false)
    {
        var originalChildren=((IList)R.Get(item.Model,"Items")!).Cast<object>().ToArray();
        bool Present()=>Items(item.Environment).Any(candidate=>ReferenceEquals(candidate.Model,item.Model));
        bool Current()
        {
            if(!Items(item.Environment).Any(candidate=>ReferenceEquals(candidate.Model,item.Model) && candidate.Address==item.Address &&
                ((IList)R.Get(candidate.Model,"Items")!).Cast<object>().SequenceEqual(originalChildren,ReferenceEqualityComparer.Instance))) return false;
            if(!savedBaseline) return true;
            var local=Under(item.Environment,item.RelativePath);
            var staging=NativeSaveVerification.Under((string)R.Get(item.Environment.Save,"LastPath")!,item.RelativePath);
            return Directory.Exists(local) && !Directory.EnumerateFileSystemEntries(local).Any() &&
                Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any() &&
                SourceState(item.Environment.Source,item.RelativePath).StartsWith("directory:",StringComparison.Ordinal) && SourceEmpty(item.Environment.Source,item.RelativePath);
        }
        return new(item.Address,nativeName,[],[],[],new Dictionary<string,string>(),Current,
            nativeIdentity:item.Model,isPresent:Present,requiredDirectories:required,absentDirectories:absent);
    }
    private string NativeFolderName(Environment environment,ModuleAddress address)
    {
        var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        var nativeType=R.Static(extensions,"GetModuleType",environment.Root)!;
        var icon=R.Static(extensions,"GetIconType",environment.Root)!;
        var root=(string)R.Get(environment.Root,"Name")!; var parent=ParentPath(address);
        var token=R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"CreateNewFolder",
            address.Name,nativeType,icon,root+(parent.Length==0?"":"/"+parent))!;
        return (string)R.Get(token,"ModuleName")!;
    }
    internal bool RequiresGrouped(ModuleMutationRequest request) => request.Operation is ModuleOperation.Update or ModuleOperation.Delete &&
        ((IList)R.Get(Find(request.Address).Model,"Items")!).Count>0;
    public IReadOnlyList<string> Apply(ModuleMutationRequest request) => Apply(request,null);
    internal IReadOnlyList<string> Apply(ModuleMutationRequest request, NativeFolderLifecyclePlan? groupedPlan)
    {
        var (environment, current, destination) = Prepare(request,groupedPlan?.FolderBefore is not null);
        if(current is not null && ((IList)R.Get(current.Model,"Items")!).Count>0 &&
            (groupedPlan?.FolderBefore is null || groupedPlan.FolderBefore.Any(group=>!group.Current())))
            throw new BridgeException("NativeFolderPlanUnavailable","A current complete grouped folder plan is required before mutation.");
        var path = Relative(destination);
        var oldPath = current?.RelativePath;
        var lineage = results.Values.Where(x => x.Address == request.Address).SelectMany(x => x.Lineage)
            .Concat(new[] { oldPath, path }.Where(x => x is not null).Select(x => x!)).Distinct(StringComparer.Ordinal).ToArray();
        var utilities = Assembly.Load("CommonUtilities").GetType("Forguncy.Common.FileUtilities", true)!;
        if (request.Operation == ModuleOperation.Create)
        {
            var folder = R.Call(environment.Mediator, "CreateFolder", destination.Name)!;
            R.Set(folder, "Loaded", true);
            R.Static(utilities, "EnsureExistFolder", Under(environment, path));
            Parent(environment, ParentPath(destination)).Add(folder);
            R.Call(environment.Colleague, "InsertFolder", folder);
            Dirty(folder, "New");
        }
        else if (request.Operation == ModuleOperation.Delete)
        {
            if(groupedPlan?.DeleteLeaves is { } leaves)
            {
                if(groupedPlan.DeleteStore is null || groupedPlan.DeleteOwners is null) throw new BridgeException("NativeFolderPlanUnavailable","The native child deletion plan is incomplete.");
                foreach(var leaf in leaves)
                {
                    var validation=groupedPlan.DeleteStore.ValidateGroupedDelete(leaf,groupedPlan.DeleteOwners);
                    if(!validation.Valid) throw new BridgeException("NativeFolderChildDeleteRejected",string.Join("; ",validation.Issues.Select(issue=>issue.Code+": "+issue.Message)));
                }
                foreach(var leaf in leaves) groupedPlan.DeleteStore.Apply(leaf,groupedPlan.DeleteOwners);
                foreach(var child in groupedPlan.FolderBefore!.Where(group=>!ReferenceEquals(group.NativeIdentity,current!.Model))) Dirty(child.NativeIdentity,"Remove");
            }
            Dirty(current!.Model, "Remove");
            R.Static(utilities, "DeleteFolder", Under(environment, current.RelativePath));
            current.Parent.RemoveAt(current.Index);
            R.Call(environment.Colleague, "DeleteFolder", current.Model);
        }
        else
        {
            var nextParent = Parent(environment, ParentPath(destination));
            var currentModel = current!.Model;
            if (ParentPath(destination) == ParentPath(request.Address))
                R.Static(utilities, "RenameFolder", Under(environment, current.RelativePath), destination.Name);
            else
                R.Static(utilities, "MoveFolder", Under(environment, current.RelativePath), Under(environment, path), false);
            if (!ReferenceEquals(current.Parent, nextParent))
            {
                current.Parent.RemoveAt(current.Index);
                R.Set(currentModel, "Name", destination.Name);
                nextParent.Add(currentModel);
            }
            else R.Set(currentModel, "Name", destination.Name);
            R.Call(environment.Colleague, "RenameFolder", currentModel, request.Address.Name);
            Dirty(currentModel, ParentPath(destination) == ParentPath(request.Address) ? "Edit" : "Move");
        }
        R.Call(environment.Colleague, "SaveToCache", true);
        // A known unsaved sequence can rename/move the same native folder several
        // times. Earlier pending operations verify the final location at save.
        foreach (var previous in results.Where(x => x.Value.Address == request.Address).ToArray())
        {
            results[previous.Key] = new(destination, lineage);
            if (request.Operation == ModuleOperation.Delete) deletedResults.Add(previous.Key);
        }
        results[request.OperationId] = new(destination, lineage);
        if (!touchedPaths.TryGetValue(environment.Owner, out var touched)) touchedPaths.Add(environment.Owner, touched = new(StringComparer.Ordinal));
        touched.UnionWith(lineage);
        if (request.Operation == ModuleOperation.Delete) deletedResults.Add(request.OperationId);
        var present = Items(environment).Any(x => x.Address == destination);
        if (present != (request.Operation != ModuleOperation.Delete)) throw new BridgeException("FolderMemoryReadbackMismatch", "Native folder tree mutation did not match the requested state.");
        return lineage.Select(x => x + "/").ToArray();
    }
    public ModuleDocument? ReadMutationResult(ModuleMutationRequest request) => request.Operation == ModuleOperation.Delete || deletedResults.Contains(request.OperationId) ? null :
        Read(results.TryGetValue(request.OperationId, out var result) ? result.Address : Destination(request));
    public bool VerifySource(ModuleMutationRequest request)
    {
        if (!results.TryGetValue(request.OperationId, out var result)) return false;
        var environment = Env(ParseOwner(result.Address.FolderPath!).Owner);
        var present = Items(environment).Select(x => x.RelativePath).ToHashSet(StringComparer.Ordinal);
        // Keep every touched path as a tombstone. A later legitimate recreation
        // temporarily requires presence; deleting it restores the absence check.
        // This also verifies the complete A -> B -> C lineage when the caller
        // coalesces pending mutations to only the latest request.
        return touchedPaths.TryGetValue(environment.Owner, out var touched) && touched.All(path =>
            present.Contains(path) ? SourceState(environment.Source, path).StartsWith("directory:", StringComparison.Ordinal) : SourceState(environment.Source, path) == "absent");
    }

    private (Environment Environment, Item? Current, ModuleAddress Destination) Prepare(ModuleMutationRequest request, bool grouped = false)
    {
        if (!Supports(request.Address) || request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
            throw new BridgeException("InvalidFolderMutation", "Only Report/BusinessProcess folder definition CUD is supported here.");
        var (owner, _) = ParseOwner(request.Address.FolderPath!);
        var environment = Env(owner);
        ValidateAddress(request.Address);
        if (request.Operation != ModuleOperation.Delete && request.Document is null) throw new BridgeException("MissingDocument", "Folder metadata is required.");
        if (request.Document?.Companions.Count > 0) throw new BridgeException("UnexpectedCompanion", "Folders have no caller-specified file companions.");
        var destination = Destination(request);
        ValidateAddress(destination);
        if (request.Operation == ModuleOperation.Create && request.Document!.Metadata.TryGetProperty("Name", out var createName) && createName.GetString() != request.Address.Name)
            throw new BridgeException("FolderNameMismatch", "Created folder metadata must match its requested name.");
        if (request.Operation == ModuleOperation.Update)
        {
            var currentLogical = ParentPath(request.Address) is { Length: > 0 } currentParent ? currentParent + "/" + request.Address.Name : request.Address.Name;
            var newParent = ParentPath(destination);
            if (newParent == currentLogical || newParent.StartsWith(currentLogical + "/", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("FolderCycle", "A folder cannot be moved into itself or its descendants.");
        }
        var same = destination == request.Address;
        var current = request.Operation == ModuleOperation.Create ? null : Find(request.Address);
        if (current is not null)
        {
            if (Read(request.Address).Revision != request.ExpectedModuleRevision) throw new BridgeException("FolderBaselineChanged", "The native folder or source has changed.");
            var hasChildren=((IList)R.Get(current.Model, "Items")!).Count != 0;
            if (hasChildren && !grouped) throw new BridgeException("FolderNotEmpty", "Nonempty folder changes require a complete native child-owner plan.");
            if (!Directory.Exists(Under(environment, current.RelativePath))) throw new BridgeException("FolderCacheMissing", "The native folder cache is missing.");
            if (!hasChildren && (Directory.EnumerateFileSystemEntries(Under(environment, current.RelativePath)).Any() || !SourceEmpty(environment.Source, current.RelativePath)))
                throw new BridgeException("FolderPhysicalContentPresent", "The native folder cache/source contains files or directories not represented by this empty-folder mutation.");
        }
        if (request.Operation != ModuleOperation.Delete)
        {
            if (request.Document!.Metadata.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidFolderMetadata", "Folder metadata must be an object.");
            foreach (var property in request.Document.Metadata.EnumerateObject())
                if (property.Name is not ("Name" or "ParentFolderPath" or "OwnerKind" or "Children")) throw new BridgeException("UnsupportedFolderProperty", "Unsupported folder property: " + property.Name);
            if (request.Document.Metadata.TryGetProperty("OwnerKind", out var typedOwner) && typedOwner.GetString() != owner.ToString()) throw new BridgeException("FolderOwnerMismatch", "Folder owner cannot change.");
            if (request.Document.Metadata.TryGetProperty("Children", out var children))
            {
                if(children.ValueKind!=JsonValueKind.Array) throw new BridgeException("FolderChildrenNotEmpty", "Child modules cannot be supplied through a directory mutation.");
                if(children.GetArrayLength()!=0)
                {
                    var expected=current is null ? JsonSerializer.SerializeToElement(Array.Empty<object>()) :
                        JsonSerializer.SerializeToElement(((IList)R.Get(current.Model,"Items")!).Cast<object>().Select(item=>new {Name=R.Get(item,"Name"),Type=item.GetType().FullName}).ToArray());
                    if(!System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(children.GetRawText()),System.Text.Json.Nodes.JsonNode.Parse(expected.GetRawText()))) throw new BridgeException("FolderChildrenNotEmpty", "Folder children must match the current native tree and cannot be changed through folder metadata.");
                }
            }
            Parent(environment, ParentPath(destination));
            if (request.Operation == ModuleOperation.Create || !same)
                if (Items(environment).Any(x => x.Address.Name.Equals(destination.Name, StringComparison.OrdinalIgnoreCase) && ParentPath(x.Address).Equals(ParentPath(destination), StringComparison.OrdinalIgnoreCase)) ||
                    Directory.Exists(Under(environment, Relative(destination))) || File.Exists(Under(environment, Relative(destination))) ||
                    (SourceState(environment.Source, Relative(destination)) != "absent" && !ReusablePath(environment, Relative(destination))))
                    throw new BridgeException("FolderAlreadyExists", "Destination native directory or source already exists.");
        }
        return (environment, current, destination);
    }
    private bool ReusablePath(Environment environment, string path) => touchedPaths.TryGetValue(environment.Owner, out var touched) && touched.Contains(path) &&
        !Items(environment).Any(x => x.RelativePath == path) && SourceEmpty(environment.Source, path);
    private static bool SourceEmpty(string source, string path)
    {
        if (Directory.Exists(source))
        {
            var directory = path.StartsWith("Reports/", StringComparison.Ordinal) ? NativeFiles.UnderReports(source, path) : NativeFiles.UnderProcess(source, path);
            return !File.Exists(directory) && (!Directory.Exists(directory) || !Directory.EnumerateFileSystemEntries(directory).Any());
        }
        using var archive = ZipFile.OpenRead(source);
        var prefix = path + "/";
        var entries = archive.Entries.Where(x => x.FullName.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase) || x.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Any(x => x.FullName.Replace('\\', '/') != prefix)) return false;
        return entries.Length <= 1 && (entries.Length == 0 || entries[0].FullName.Replace('\\', '/') == prefix);
    }
    private static ModuleAddress Destination(ModuleMutationRequest request)
    {
        if (request.Operation != ModuleOperation.Update || request.Document is null) return request.Address;
        var (owner, parent) = ParseOwner(request.Address.FolderPath!);
        var data = request.Document.Metadata;
        var name = data.TryGetProperty("Name", out var value) ? value.GetString() ?? "" : request.Address.Name;
        var newParent = data.TryGetProperty("ParentFolderPath", out var path) ? path.GetString() ?? "" : parent;
        return new(ModuleKind.Folder, name, owner + (newParent.Length == 0 ? "" : "/" + newParent));
    }
    private Environment Env(ModuleKind owner)
    {
        if (environments.TryGetValue(owner, out var found)) return found;
        if (document is null) throw new BridgeException("FolderOwnerUnavailable", "The folder owner is not bound in this adapter.");
        var colleagues = R.Get(document, "ForguncyColleagueManager")!;
        return environments[owner] = Bind(R.Get(colleagues, owner == ModuleKind.Report ? "ReportModel" : "ProcessModel")!, owner);
    }
    private Environment Bind(object colleague, ModuleKind owner)
    {
        if (owner is not (ModuleKind.Report or ModuleKind.BusinessProcess) || assembly.GetName().Version != new Version(12, 0, 102, 0)) throw new BridgeException("UnsupportedFolderOwner", "Only exact V12 report/process folders are supported.");
        var mediator = R.Get(colleague, "Mediator")!;
        var save = R.Get(R.Get(mediator, "SaveDataColleague")!, "SaveLoadFile")!;
        return new(colleague, mediator, save, R.Get(colleague, "Root")!, owner);
    }
    private static (ModuleKind Owner, string Parent) ParseOwner(string path)
    {
        var parts = path.Split('/');
        var owner = parts[0] switch { "Report" => ModuleKind.Report, "BusinessProcess" => ModuleKind.BusinessProcess, _ => throw new BridgeException("InvalidFolderOwner", "FolderPath must begin with Report or BusinessProcess.") };
        foreach (var item in parts.Skip(1)) ReportDocuments.ValidateName(item);
        return (owner, string.Join('/', parts.Skip(1)));
    }
    private static string ParentPath(ModuleAddress address) => ParseOwner(address.FolderPath!).Parent;
    private static void ValidateAddress(ModuleAddress address)
    { if (!Supports(address)) throw new BridgeException("InvalidFolderAddress", "Invalid definition folder address."); ReportDocuments.ValidateName(address.Name); ParseOwner(address.FolderPath!); }
    private static string Relative(ModuleAddress address)
    { var (owner, parent) = ParseOwner(address.FolderPath!); return (owner == ModuleKind.Report ? "Reports/" : "Process/") + (parent.Length == 0 ? "" : parent + "/") + address.Name; }
    private static string Under(Environment environment, string path) => environment.Owner == ModuleKind.Report ? NativeFiles.UnderReports(environment.Local, path) : NativeFiles.UnderProcess(environment.Local, path);
    private Item Find(ModuleAddress address) => Items(Env(ParseOwner(address.FolderPath!).Owner)).SingleOrDefault(x => x.Address == address) ?? throw new BridgeException("FolderNotFound", "The unique native definition folder was not found.");
    private static IList Parent(Environment environment, string path)
    {
        object current = environment.Root;
        foreach (var part in path.Length == 0 ? [] : path.Split('/'))
            current = ((IList)R.Get(current, "Items")!).Cast<object>().SingleOrDefault(x => x.GetType().GetProperty("Items", R.Flags) is not null && (string?)R.Get(x, "Name") == part)
                ?? throw new BridgeException("FolderParentMissing", "The native parent directory must already exist.");
        return (IList)R.Get(current, "Items")!;
    }
    private static IReadOnlyList<Item> Items(Environment environment)
    {
        var values = new List<Item>();
        void Visit(object node, string parent)
        {
            if (R.Get(node, "Loaded") is false) throw new BridgeException("DefinitionFolderTreeNotLoaded", "Load the complete native definition tree first.");
            var items = (IList)R.Get(node, "Items")!;
            for (var i = 0; i < items.Count; i++)
            {
                var model = items[i]!;
                if (model.GetType().GetProperty("Items", R.Flags) is null) continue;
                var name = (string)R.Get(model, "Name")!;
                var address = new ModuleAddress(ModuleKind.Folder, name, environment.Owner + (parent.Length == 0 ? "" : "/" + parent));
                values.Add(new(environment, model, items, i, address, Relative(address)));
                Visit(model, parent.Length == 0 ? name : parent + "/" + name);
            }
        }
        Visit(environment.Root, ""); return values;
    }
    private static string SourceState(string source, string path)
    {
        if (Directory.Exists(source))
        {
            var directory = path.StartsWith("Reports/", StringComparison.Ordinal) ? NativeFiles.UnderReports(source, path) : NativeFiles.UnderProcess(source, path);
            if (File.Exists(directory)) { using var file = File.OpenRead(directory); return "file:" + Convert.ToHexString(SHA256.HashData(file)); }
            if (!Directory.Exists(directory)) return "absent";
            var records = new List<string>();
            void Visit(string current)
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.Ordinal))
                {
                    var relative = Path.GetRelativePath(source, entry).Replace('\\', '/');
                    if (path.StartsWith("Reports/", StringComparison.Ordinal)) NativeFiles.UnderReports(source, relative); else NativeFiles.UnderProcess(source, relative);
                    if (Directory.Exists(entry)) { records.Add(relative + "/"); Visit(entry); }
                    else { using var input = File.OpenRead(entry); records.Add(relative + ":" + Convert.ToHexString(SHA256.HashData(input))); }
                }
            }
            Visit(directory);
            return "directory:" + NativeFiles.Hash(string.Join("\n", records));
        }
        if (!File.Exists(source)) throw new BridgeException("FolderSourceUnavailable", "Native source is unavailable.");
        using var archive = ZipFile.OpenRead(source);
        var prefix = path + "/";
        var entries = archive.Entries.Where(x => x.FullName.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase) || x.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length == 0) return "absent";
        if (entries.Any(x => x.FullName.Replace('\\', '/') != path && !x.FullName.Replace('\\', '/').StartsWith(prefix, StringComparison.Ordinal)) ||
            entries.Any(x => x.FullName.Replace('\\', '/') == path) && entries.Length != 1 ||
            entries.GroupBy(x => x.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            throw new BridgeException("AmbiguousNativeFolderArchive", "Native folder archive paths contain a case alias or duplicate entry.");
        var hashes = new List<string>();
        foreach (var entry in entries.OrderBy(x => x.FullName, StringComparer.Ordinal))
        { using var input = entry.Open(); hashes.Add(entry.FullName + ":" + Convert.ToHexString(SHA256.HashData(input))); }
        return (entries[0].FullName.Replace('\\', '/') == path ? "file:" : "directory:") + NativeFiles.Hash(string.Join("\n", hashes));
    }
    private void Dirty(object model, string state)
    {
        var cacheType = assembly.GetType("pb", true)!;
        var cache = cacheType.GetProperty("Instance", R.Flags)!.GetValue(null)!;
        var method = cacheType.GetMethod("DirtyModules", R.Flags)!;
        var tuple = method.GetParameters()[1].ParameterType.GetGenericArguments()[0]; var types = tuple.GetGenericArguments();
        var module = R.Static(types[0], "Create", model);
        var changes = Array.CreateInstance(tuple, 1); changes.SetValue(Activator.CreateInstance(tuple, module, Enum.Parse(types[1], state)), 0);
        method.Invoke(cache, [null, changes]);
    }
}
