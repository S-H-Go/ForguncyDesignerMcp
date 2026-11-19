using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// Non-static native folders are represented by FolderSaveData registrations;
// their loader does not mirror empty directories into the local cache.
internal sealed partial class NativeRegisteredFolders
{
    private sealed record Item(object Model,IList Parent,ModuleAddress Address,string Key);
    private readonly Assembly assembly; private readonly object colleague,loader,root,save;
    private readonly string baseFolder,owner,viewModel;
    private readonly bool wholeCache;
    private static readonly IReadOnlyDictionary<string,(string Folder,string ViewModel,bool WholeCache)> Routes =
        new Dictionary<string,(string,string,bool)>(StringComparer.Ordinal)
        {
            ["Page"]=("Pages/","PageFolderViewModel",false), ["MasterPage"]=("MasterPages/","MasterPageFolderViewModel",false),
            ["UserControlPage"]=("UserControlPages/","UserControlPageFolderViewModel",false), ["Table"]=("Tables/","TableFolderViewModel",true),
            ["ServerCommand"]=("ServerCommands/","ServerCommandFolderViewModel",false), ["SharedCommand"]=("ShareCommandContainers/","ShareCommandContainerFolderViewModel",false),
            ["ScheduledTask"]=("SchedulerTasks/","TaskSchedulerFolderViewModel",true), ["Notification"]=("Notifications/","NotificationsFolderViewModel",true),
            ["Image"]=("image/","ImageFolderViewModel",true)
        };
    private readonly Dictionary<string,ModuleAddress?> results=new(StringComparer.Ordinal);
    private readonly HashSet<string> touched=new(StringComparer.Ordinal);
    internal NativeRegisteredFolders(Assembly assembly,object colleague,string owner="Page")
    {
        this.assembly=assembly; this.colleague=colleague; this.owner=owner;
        var mediator=R.Get(colleague,"Mediator")!; loader=R.Get(mediator,"SaveDataColleague")!;
        root=R.Get(colleague,"Root")!; save=R.Get(loader,"SaveLoadFile")!;
        baseFolder=NativeRegisteredFolderEvidence.CanonicalKey((string)R.Get(colleague,"BaseFolder")!);
        if(assembly.GetName().Version!=new Version(12,0,102,0) || !Routes.TryGetValue(owner,out var route) || route.Folder!=baseFolder)
            throw new BridgeException("UnsupportedFolderOwner","The pinned native registered folder route does not match its owner.");
        viewModel=route.ViewModel; wholeCache=route.WholeCache;
    }
    private static bool Enabled(string? owner)=>owner is "Page" or "Table" or "MasterPage" or "UserControlPage" or "ServerCommand" or "SharedCommand" or "ScheduledTask" or "Notification" or "Image";
    internal static bool Supports(ModuleAddress address)=>address.Kind==ModuleKind.Folder && Enabled(address.FolderPath?.Split('/')[0]);
    internal static bool Supports(ModuleQuery query)=>query.Kind==ModuleKind.Folder && (query.FolderPath is null || Enabled(query.FolderPath.Split('/')[0]));
    private string Local=>(string)R.Get(save,"LocalPath")!;
    private string Stage=>(string)R.Get(save,"LastPath")!;
    private string Source=>(string)R.Call(save,"GetDocumentPath")!;
    private bool IsImage=>owner=="Image";
    private bool Registered(string key)=>IsImage ? NativeRegisteredFolderEvidence.DirectoryPresence(Local,key,true) : NativeRegisteredFolderEvidence.HasRegistration(loader,key);
    private bool LocalEmptyFolder(string key)=>NativeRegisteredFolderEvidence.EmptyDirectory(Local,key,IsImage) ||
        owner=="Table" && NativeRegisteredFolderEvidence.EmptyDirectory(Local,key,true);
    private object ImageManager()
    {
        var config=assembly.GetType("Forguncy.Common.Config",true)!;
        var configured=(string)R.Static(config,"get_LocalDataFolderPath")!;
        if(!Path.GetFullPath(configured).Equals(Path.GetFullPath(Local),StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("NativeImageCacheMismatch","The native image manager is bound to a different local project cache.");
        var manager=R.Static(assembly.GetType("Forguncy.Common.ImageManager",true)!,"get_Instance")!;
        if(!Path.GetFullPath((string)R.Get(manager,"LocalImagesPath")!).Equals(Path.GetFullPath(Path.Combine(Local,"image")),StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("NativeImageCacheMismatch","The native image directory differs from the bound project cache.");
        return manager;
    }
    private string ParentPath(ModuleAddress address) => address.FolderPath is { } value && value.StartsWith(owner+"/",StringComparison.Ordinal) ? value[(owner.Length+1)..] : "";
    private string Key(ModuleAddress address)=>baseFolder+(ParentPath(address) is { Length:>0 } parent ? parent+"/" : "")+address.Name+"/";
    private string Key(object model)=>NativeRegisteredFolderEvidence.CanonicalKey((string)R.Call(colleague,"GetKey",model)!);
    private void ValidateAddress(ModuleAddress address)
    {
        if(address.Kind!=ModuleKind.Folder || address.FolderPath?.Split('/')[0]!=owner) throw new BridgeException("InvalidFolderAddress","FolderPath must start with its native owner.");
        ReportDocuments.ValidateName(address.Name);
        var parts=address.FolderPath!.Split('/');
        if(parts.Skip(1).Any(string.IsNullOrEmpty)) throw new BridgeException("InvalidFolderAddress","FolderPath must be a canonical logical path.");
        foreach(var part in parts.Skip(1)) ReportDocuments.ValidateName(part);
    }
    private IReadOnlyList<Item> Items()
    {
        var result=new List<Item>(); var seen=new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object node,string path)
        {
            if(!seen.Add(node) || R.Get(node,"Loaded") is not true) throw new BridgeException("FolderTreeUnavailable","The complete native page folder tree must be loaded and acyclic.");
            var children=(IList)R.Get(node,"Items")!;
            foreach(var child in children.Cast<object>())
            {
                if(child.GetType().GetProperty("Items",R.Flags) is null) continue;
                var name=(string)R.Get(child,"Name")!; var address=new ModuleAddress(ModuleKind.Folder,name,owner+(path.Length==0?"":"/"+path));
                var key=Key(child); if(key!=Key(address)) throw new BridgeException("NativeFolderPathMismatch","Native page folder key and loaded tree disagree.");
                result.Add(new(child,children,address,key)); Walk(child,path.Length==0?name:path+"/"+name);
            }
        }
        Walk(root,""); return result;
    }
    private Item Find(ModuleAddress address)=>Items().SingleOrDefault(item=>item.Address==address) ?? throw new BridgeException("FolderNotFound","The native page folder was not found.");
    internal ModulePage List(ModuleQuery query)
    {
        if(query.Kind!=ModuleKind.Folder || query.FolderPath is not null && query.FolderPath.Split('/')[0]!=owner || query.Limit is < 1 or > 1000) throw new BridgeException("InvalidFolderQuery","FolderPath must start with its native owner.");
        var parent=query.FolderPath is null ? null : query.FolderPath==owner?"":query.FolderPath[(owner.Length+1)..];
        var all=Items().Where(item=>parent is null || ParentPath(item.Address)==parent).OrderBy(item=>item.Key,StringComparer.Ordinal).ToArray();
        var offset=query.Cursor is null?0:int.TryParse(query.Cursor,out var cursor) && cursor>=0?cursor:throw new BridgeException("InvalidCursor","Invalid folder cursor.");
        return new(all.Skip(offset).Take(query.Limit).Select(item=>new ModuleSummary(item.Address,Read(item.Address).Revision)).ToArray(),offset+query.Limit<all.Length?(offset+query.Limit).ToString():null);
    }
    internal ReferenceResult References(ModuleAddress address)=>((IList)R.Get(Find(address).Model,"Items")!).Count==0 ? new([],true) : new([],false,"Nonempty page folders require a grouped child-reference and checkout operation.");
    private IList Parent(string path)
    {
        object current=root;
        foreach(var name in path.Length==0 ? [] : path.Split('/'))
            current=((IList)R.Get(current,"Items")!).Cast<object>().SingleOrDefault(item=>item.GetType().GetProperty("Items",R.Flags) is not null && (string?)R.Get(item,"Name")==name)
                ?? throw new BridgeException("FolderParentMissing","The native parent folder does not exist.");
        return (IList)R.Get(current,"Items")!;
    }
    internal ModuleDocument Read(ModuleAddress address)
    {
        ValidateAddress(address); var item=Find(address);
        var children=((IList)R.Get(item.Model,"Items")!).Cast<object>().Select(value=>new {Name=R.Get(value,"Name"),Type=value.GetType().FullName}).ToArray();
        var metadata=JsonSerializer.SerializeToElement(new {Name=address.Name,ParentFolderPath=ParentPath(address),OwnerKind=owner,Children=children});
        var state=NativeRegisteredFolderEvidence.EmptyDirectory(Source,item.Key,true) ? "empty" : "not-empty-or-absent";
        return new(address,metadata,[],NativeFiles.Hash(metadata.GetRawText()+"\\n"+item.Key+"\\n"+state+"\\n"+Registered(item.Key)));
    }
    private ModuleAddress Destination(ModuleMutationRequest request)
    {
        if(request.Operation!=ModuleOperation.Update) return request.Address;
        var data=request.Document!.Metadata;
        var name=data.TryGetProperty("Name",out var n)?n.GetString()??"":request.Address.Name;
        var parent=data.TryGetProperty("ParentFolderPath",out var p)?p.GetString()??"":ParentPath(request.Address);
        return new(ModuleKind.Folder,name,owner+(parent.Length==0?"":"/"+parent));
    }
    private (Item? Item,ModuleAddress Destination) Prepare(ModuleMutationRequest request,bool grouped=false)
    {
        ValidateAddress(request.Address);
        if(request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)) throw new BridgeException("InvalidFolderMutation","Unsupported page-folder operation.");
        if(request.Operation!=ModuleOperation.Delete && request.Document is null) throw new BridgeException("MissingDocument","Page-folder metadata is required.");
        if(request.Document?.Companions.Count>0) throw new BridgeException("UnexpectedCompanion","Page folders do not accept file companions.");
        var destination=Destination(request); ValidateAddress(destination);
        var current=request.Operation==ModuleOperation.Create?null:Find(request.Address);
        if(current is not null)
        {
            if(Read(request.Address).Revision!=request.ExpectedModuleRevision) throw new BridgeException("FolderBaselineChanged","The native page folder changed.");
            var hasChildren=((IList)R.Get(current.Model,"Items")!).Count!=0;
            if(hasChildren && !(grouped && (request.Operation==ModuleOperation.Update || request.Operation==ModuleOperation.Delete && owner is "Page" or "MasterPage" or "UserControlPage" or "Notification" or "ServerCommand" or "SharedCommand" or "ScheduledTask" or "Table" or "Image"))) throw new BridgeException("FolderNotEmpty","Folder grouped operations require a complete child-owner plan.");
            var localMatches=(owner=="Table" || IsImage) && hasChildren ? NativeRegisteredFolderEvidence.DirectoryPresence(Local,current.Key,true) : LocalEmptyFolder(current.Key);
            if(!Registered(current.Key) || !localMatches ||
                !(hasChildren ? NativeRegisteredFolderEvidence.DirectoryPresence(Stage,current.Key,true) && NativeRegisteredFolderEvidence.DirectoryPresence(Source,current.Key,true) :
                    NativeRegisteredFolderEvidence.EmptyDirectory(Stage,current.Key,true) && NativeRegisteredFolderEvidence.EmptyDirectory(Source,current.Key,true))) throw new BridgeException("NativeFolderSourceChanged","The native folder registration and saved directory baseline disagree.");
        }
        if(request.Operation!=ModuleOperation.Delete)
        {
            var metadata=request.Document!.Metadata;
            if(metadata.ValueKind!=JsonValueKind.Object || metadata.EnumerateObject().Any(property=>property.Name is not ("Name" or "ParentFolderPath" or "OwnerKind" or "Children"))) throw new BridgeException("InvalidFolderMetadata","Invalid page-folder metadata.");
            if(metadata.TryGetProperty("OwnerKind",out var suppliedOwner) && suppliedOwner.GetString()!=owner) throw new BridgeException("FolderOwnerMismatch","Folder owner cannot change.");
            if(metadata.TryGetProperty("Children",out var children) && (children.ValueKind!=JsonValueKind.Array || children.GetArrayLength()!=0 &&
                !(grouped && current is not null && children.GetRawText()==Read(request.Address).Metadata.GetProperty("Children").GetRawText()))) throw new BridgeException("FolderNotEmpty","Folder child changes require an owner plan.");
            if(request.Operation==ModuleOperation.Create && metadata.TryGetProperty("Name",out var createName) && createName.GetString()!=request.Address.Name) throw new BridgeException("FolderNameMismatch","Created name must match its address.");
            var oldLogical=(ParentPath(request.Address) is {Length:>0} parent?parent+"/":"")+request.Address.Name;
            if(request.Operation==ModuleOperation.Update && (ParentPath(destination)==oldLogical || ParentPath(destination).StartsWith(oldLogical+"/",StringComparison.OrdinalIgnoreCase))) throw new BridgeException("FolderCycle","A folder cannot move into itself.");
            Parent(ParentPath(destination));
            if(request.Operation==ModuleOperation.Create || destination!=request.Address)
            {
                var key=Key(destination);
                if(Items().Any(item=>item.Address.Name.Equals(destination.Name,StringComparison.OrdinalIgnoreCase) && ParentPath(item.Address).Equals(ParentPath(destination),StringComparison.OrdinalIgnoreCase)) ||
                    Registered(key) || !NativeRegisteredFolderEvidence.EmptyDirectory(Local,key,false) ||
                    !NativeRegisteredFolderEvidence.EmptyDirectory(Stage,key,false) || !NativeRegisteredFolderEvidence.EmptyDirectory(Source,key,false)) throw new BridgeException("FolderAlreadyExists","The destination page folder or source path already exists.");
            }
        }
        return (current,destination);
    }
    internal ModuleValidationResult Validate(ModuleMutationRequest request)
    { try { Prepare(request,request.Operation==ModuleOperation.Update || request.Operation==ModuleOperation.Delete && owner is "Page" or "MasterPage" or "UserControlPage" or "Notification" or "ServerCommand" or "SharedCommand" or "ScheduledTask" or "Table" or "Image"); return new(true,[]); } catch(Exception error) { return new(false,[new(error is BridgeException b?b.Code:"NativeFolderValidation",error.Message)]); } }
    internal string NativeName(ModuleAddress address)
    {
        var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        var native=R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"CreateNewFolder",address.Name,
            R.Static(extensions,"GetModuleType",root),R.Static(extensions,"GetIconType",root),
            (string)R.Get(root,"Name")!+(ParentPath(address) is {Length:>0} parent?"/"+parent:""))!;
        return (string)R.Get(native,"ModuleName")!;
    }
    private NativeModuleFileGroup Group(Item item,string name,bool baseline)
    {
        bool Present()=>Items().Any(candidate=>ReferenceEquals(candidate.Model,item.Model));
        bool Current()=>Items().Any(candidate=>ReferenceEquals(candidate.Model,item.Model) && candidate.Address==item.Address && ((IList)R.Get(candidate.Model,"Items")!).Count==0) &&
            Registered(item.Key) && LocalEmptyFolder(item.Key) &&
            (!baseline || NativeRegisteredFolderEvidence.EmptyDirectory(Stage,item.Key,true) && NativeRegisteredFolderEvidence.EmptyDirectory(Source,item.Key,true));
        return new(item.Address,name,[],[],[],new Dictionary<string,string>(),Current,nativeIdentity:item.Model,isPresent:Present,
            requiredDirectories:[item.Key.TrimEnd('/')],directoriesRequireLocal:IsImage);
    }
    internal NativeFolderLifecyclePlan Capture(ModuleMutationRequest request,NativeModuleFiles files)
    {
        var (current,destination)=Prepare(request,true);
        var vmType=assembly.GetType("Forguncy.Views.ObjectViews."+viewModel,true)!;
        object Vm(object? parent,object model)
        {
            var ctor=vmType.GetConstructors(R.Flags).Single(value=>value.GetParameters().Length>=3 && value.GetParameters()[1].ParameterType.IsInstanceOfType(model) && value.GetParameters().Skip(2).All(parameter=>parameter.ParameterType==typeof(bool)));
            return ctor.Invoke(ctor.GetParameters().Select((_,index)=>index==0?parent:index==1?model:(object)false).ToArray());
        }
        object ParentVm(string path)
        {
            object model=root; object vm=Vm(null,model);
            foreach(var name in path.Length==0?[]:path.Split('/'))
            { model=((IList)R.Get(model,"Items")!).Cast<object>().Single(child=>child.GetType().GetProperty("Items",R.Flags) is not null && (string?)R.Get(child,"Name")==name); vm=Vm(vm,model); }
            return vm;
        }
        object executor;
        if(request.Operation==ModuleOperation.Create)
        {
            var extensions=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
            executor=Activator.CreateInstance(assembly.GetType("Forguncy.Views.ObjectViews.CreateItemOrFolderExecutor",true)!,
                [null,ParentVm(ParentPath(destination)),destination.Name,R.Static(extensions,"GetModuleType",root),R.Static(extensions,"GetIconType",root),null])!;
        }
        else
        {
            var vm=Vm(ParentVm(ParentPath(request.Address)),current!.Model);
            var selected=Array.CreateInstance(assembly.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel",true)!,1); selected.SetValue(vm,0);
            if(request.Operation==ModuleOperation.Delete) executor=R.Call(vm,"DeleteItemsCollaborationExecutor",selected)!;
            else if(ParentPath(request.Address)==ParentPath(destination)) executor=R.Call(vm,"RenameCollaborationExecutor",destination.Name)!;
            else
            {
                if(destination.Name!=request.Address.Name) throw new BridgeException("FolderCombinedMoveRenameUnsupported","Move and rename must be separate operations.");
                executor=Activator.CreateInstance(assembly.GetType("Forguncy.Views.ObjectViews.MoveItemOrFolderExecutor",true)!,[null,selected,ParentVm(ParentPath(destination))])!;
            }
        }
        var changes=((IEnumerable)R.Call(executor,"GetRelatedModules")!).Cast<object>().ToArray();
        if(current is not null && ((IList)R.Get(current.Model,"Items")!).Count!=0)
            return request.Operation==ModuleOperation.Delete ? owner=="Table" ? CaptureTableDelete(request,files,current,destination,changes) : owner=="Image" ? CaptureImageDelete(request,files,current,destination,changes) : owner is "Notification" or "ServerCommand" or "SharedCommand" or "ScheduledTask" ? CaptureNotificationDelete(request,files,current,destination,changes) : CapturePageDelete(request,files,current,destination,changes) : CapturePageUpdate(request,files,current,destination,changes);
        var old=current is null?null:NativeName(request.Address); var next=request.Operation==ModuleOperation.Delete?null:NativeName(destination);
        var expected=(request.Operation switch {ModuleOperation.Create=>new[]{(next!,"New")},ModuleOperation.Delete=>new[]{(old!,"Remove")},_=>new[]{(old!,"Remove"),(next!,"New")}}).ToHashSet();
        var folderType=R.Static(assembly.GetType("Forguncy.Common.ModuleExtensions",true)!,"GetModuleType",root)!.ToString();
        var target=changes.Where(tuple=>R.Get(tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!,"ModuleType")!.ToString()==folderType).ToArray();
        var actual=target.Select(tuple=>((string)R.Get(tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!,"ModuleName")!,tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!)).ToArray();
        if(actual.Length!=expected.Count || !actual.ToHashSet().SetEquals(expected)) throw new BridgeException("NativeFolderPlanUnavailable","The native page folder executor returned an unexpected owner set.");
        var before=current is null?null:Group(current,old!,true);
        if(before is not null && !files.Verify(before)) throw new BridgeException("NativeFolderSourceChanged","The saved page folder group could not be verified.");
        var others=changes.Except(target).ToArray(); var otherPlan=others.Length==0?null:files.CaptureNativeModulesCore(others,false);
        if(others.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group)))) throw new BridgeException("NativeFolderPlanUnavailable","Additional native owners lack verified source groups.");
        var created=actual.Where(value=>value.Item2=="New").Select(value=>(value.Item1,folderType!)).ToArray();
        return new(new((before is null?Array.Empty<NativeModuleFileGroup>():[before]).Concat(otherPlan?.BeforeGroups ?? []).ToArray(),created,
            created.ToDictionary(value=>value.Item1,_=>destination,StringComparer.Ordinal),changes),before,otherPlan,destination);
    }
    internal IReadOnlyList<string> Apply(ModuleMutationRequest request,NativeFolderLifecyclePlan plan)
    {
        if(owner=="Table" && request.Operation==ModuleOperation.Delete && plan.FolderBefore is not null)
            throw new BridgeException("NativeFolderAsyncRequired","Grouped native table deletion must be awaited.");
        if(plan.FolderBefore is not null) return request.Operation==ModuleOperation.Delete ? owner=="Image" ? ApplyImageDelete(request,plan) : owner is "Notification" or "ServerCommand" or "SharedCommand" or "ScheduledTask" ? ApplyNotificationDelete(request,plan) : ApplyPageDelete(request,plan) : ApplyPageUpdate(request,plan);
        var (current,destination)=Prepare(request);
        if(plan.Plan.BeforeGroups.Any(group=>!group.Current())) throw new BridgeException("NativeFolderSourceChanged","The page folder or related owners changed after checkout.");
        var oldKey=current?.Key; var newKey=Key(destination);
        if(IsImage)
        {
            var manager=ImageManager();
            if(request.Operation==ModuleOperation.Create)
            {
                var model=R.Call(colleague,"CreateFolder",destination.Name)!; R.Set(model,"Loaded",true);
                Parent(ParentPath(destination)).Add(model); R.Call(manager,"NewFolder",model); Dirty(model,"New");
            }
            else if(request.Operation==ModuleOperation.Delete)
            { R.Call(manager,"DeleteFolder",current!.Model); Dirty(current.Model,"Remove"); current.Parent.Remove(current.Model); }
            else
            {
                var parent=Parent(ParentPath(destination));
                if(!ReferenceEquals(parent,current!.Parent))
                {
                    var target=ParentPath(destination).Length==0 ? root : Find(new(ModuleKind.Folder,ParentPath(destination).Split('/').Last(),
                        owner+(ParentPath(destination).Contains('/')?"/"+ParentPath(destination)[..ParentPath(destination).LastIndexOf('/')]:""))).Model;
                    R.Call(manager,"MoveFolder",current.Model,target); current.Parent.Remove(current.Model); parent.Add(current.Model); Dirty(current.Model,"Move");
                }
                else { R.Call(manager,"RenameFolder",current.Model,destination.Name); R.Set(current.Model,"Name",destination.Name); Dirty(current.Model,"Edit"); }
            }
        }
        else if(request.Operation==ModuleOperation.Create)
        {
            var model=R.Call(colleague,"CreateFolder",destination.Name)!; R.Set(model,"Loaded",true); Parent(ParentPath(destination)).Add(model); R.Call(colleague,"InsertFolder",model); Dirty(model,"New");
        }
        else if(request.Operation==ModuleOperation.Delete)
        { Dirty(current!.Model,"Remove"); R.Call(colleague,"DeleteFolder",current.Model); current.Parent.Remove(current.Model); }
        else
        {
            var parent=Parent(ParentPath(destination));
            if(!ReferenceEquals(parent,current!.Parent))
            {
                R.Call(colleague,"DeleteFolder",current.Model);
                current.Parent.Remove(current.Model); parent.Add(current.Model);
                R.Call(colleague,"InsertFolder",current.Model);
                Dirty(current.Model,"Move");
            }
            else
            {
                R.Set(current.Model,"Name",destination.Name); R.Call(colleague,"RenameFolder",current.Model,request.Address.Name);
                Dirty(current.Model,"Edit");
            }
        }
        if(wholeCache && !IsImage) R.Call(colleague,"SaveToCache",true);
        var present=request.Operation!=ModuleOperation.Delete;
        if(present!=Registered(newKey) || oldKey is not null && oldKey!=newKey && Registered(oldKey))
            throw new BridgeException("NativeFolderReadbackMismatch","Native page-folder save registrations do not match the mutation.");
        touched.Add(newKey); if(oldKey is not null) touched.Add(oldKey);
        results[request.OperationId]=present?destination:null;
        return new[]{oldKey,newKey}.Where(value=>value is not null).Select(value=>value!).Distinct(StringComparer.Ordinal).ToArray();
    }
    internal IReadOnlyList<NativeModuleFileGroup> After(ModuleMutationRequest request,NativeFolderLifecyclePlan capture,NativeModuleFiles files)
    {
        if(capture.FolderBefore is not null) return request.Operation==ModuleOperation.Delete ? PageDeleteAfter(capture,files) : PageUpdateAfter(capture,files);
        var result=new List<NativeModuleFileGroup>();
        if(capture.OtherOwners is not null) result.AddRange(files.ResolveNativeModulesAfter(capture.OtherOwners) ?? throw new BridgeException("NativeFolderReadbackUnavailable","Additional page-folder owners could not be read back."));
        if(capture.Before is { } before)
        {
            var token=capture.Plan.NativeChanges!.Select(change=>change.GetType().GetField("Item1",R.Flags)!.GetValue(change)!).Single(native=>(string)R.Get(native,"ModuleName")! == before.NativeModuleName);
            bool OldPresent()=>Items().Any(item=>item.Address==before.Address && ReferenceEquals(item.Model,before.NativeIdentity));
            if(OldPresent()) throw new BridgeException("NativeFolderReadbackMismatch","The old page-folder owner is still present.");
            result.Add(new(before.Address,before.NativeModuleName,[],[],[],new Dictionary<string,string>(),()=>!OldPresent(),nativeIdentity:token,isPresent:OldPresent,absentDirectories:before.RequiredDirectories,directoriesRequireLocal:IsImage));
        }
        if(request.Operation!=ModuleOperation.Delete)
        {
            var item=Find(capture.Destination);
            if(capture.Before is not null && !ReferenceEquals(item.Model,capture.Before.NativeIdentity)) throw new BridgeException("NativeFolderReadbackMismatch","The page-folder model identity changed.");
            result.Add(Group(item,capture.Plan.ExpectedNewModules.Single().Name,false));
        }
        return result;
    }
    internal bool VerifySource(ModuleMutationRequest request)=>results.ContainsKey(request.OperationId) && touched.All(key=>
        NativeRegisteredFolderEvidence.DirectoryPresence(Source,key,Items().Any(item=>item.Key==key)));
    internal ModuleDocument? ReadMutationResult(ModuleMutationRequest request)=>results.TryGetValue(request.OperationId,out var address) && address is not null ? Read(address) : null;
    private void Dirty(object model,string state)
    {
        var type=assembly.GetType("pb",true)!; var cache=type.GetProperty("Instance",R.Flags)!.GetValue(null)!;
        var method=type.GetMethod("DirtyModules",R.Flags)!; var tuple=method.GetParameters()[1].ParameterType.GetGenericArguments()[0]; var types=tuple.GetGenericArguments();
        var module=R.Static(types[0],"Create",model)!; var entries=Array.CreateInstance(tuple,1); entries.SetValue(Activator.CreateInstance(tuple,module,Enum.Parse(types[1],state)),0);
        method.Invoke(cache,[null,entries]);
    }
    private void DirtyPlan(NativeModuleFilePlan plan)
    {
        var type=assembly.GetType("pb",true)!; var cache=type.GetProperty("Instance",R.Flags)!.GetValue(null)!;
        var method=type.GetMethod("DirtyModules",R.Flags)!; var tuple=method.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var changes=plan.NativeChanges ?? throw new BridgeException("NativeFolderPlanUnavailable","The native dirty owner set is unavailable.");
        var entries=Array.CreateInstance(tuple,changes.Count);
        for(var i=0;i<changes.Count;i++) { if(!tuple.IsInstanceOfType(changes[i])) throw new BridgeException("NativeFolderPlanUnavailable","The native dirty owner tuple changed."); entries.SetValue(changes[i],i); }
        method.Invoke(cache,[null,entries]);
    }
}
