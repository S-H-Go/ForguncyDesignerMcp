using System.Collections;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed partial class NativeRegisteredFolders
{
    private void RequireImageReferencesContained(NativeModuleFiles files,IReadOnlyList<object> leaves)
    {
        if(leaves.Count==0) return;
        files.RequirePureReferenceContext();
        var settings=R.Get(R.Static(assembly.GetType("s",true)!,"get_Instance")!,"et")!;
        if(!ReferenceEquals(R.Get(settings,"ImageFolder"),root)) throw new BridgeException("NativeFolderReferenceUnavailable","The native image reference scanner belongs to another project.");
        var resources=new NativeResources(assembly,save,Source,()=>{},settings);
        var selected=leaves.Select(leaf=>(string)R.Get(R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",leaf)!,"ModuleName")!).ToHashSet(StringComparer.Ordinal);
        foreach(var leaf in leaves)
        {
            var name=(string)R.Get(leaf,"Name")!; var path=NativeRegisteredFolderEvidence.CanonicalKey((string)R.Call(colleague,"GetKey",leaf)!+"/").TrimEnd('/');
            var folder=Path.GetDirectoryName(path.Replace('/',Path.DirectorySeparatorChar))!.Replace('\\','/');
            var logical=folder=="image"?"":folder["image/".Length..];
            var references=resources.ImageReferences(new(ModuleKind.Image,name,logical),path);
            if(!references.Complete) throw new BridgeException("NativeFolderReferenceUnavailable",references.Reason ?? "The image reference inventory is incomplete.");
            if(references.References.Any(reference=>reference.Source.Kind!=ModuleKind.Image || !selected.Contains(reference.Source.Name)))
                throw new BridgeException("NativeFolderExternalReference","A module outside the image deletion set references a selected image.");
        }
    }
    private NativeFolderLifecyclePlan CaptureImageDelete(ModuleMutationRequest request,NativeModuleFiles files,Item current,ModuleAddress destination,object[] changes)
    {
        var descendants=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
        var leaves=descendants.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
        var type=assembly.GetType("Forguncy.HelpForm.ImageItem",true)!;
        if(leaves.Any(leaf=>!type.IsInstanceOfType(leaf) || R.Get(leaf,"BuiltIn") is true) || leaves.Distinct(ReferenceEqualityComparer.Instance).Count()!=leaves.Length)
            throw new BridgeException("NativeFolderPlanUnavailable","The image subtree contains an unknown, duplicate or built-in image.");
        RequireImageReferencesContained(files,leaves);
        (string Name,string Type,string State) Token(object tuple)
        {
            var simple=tuple.GetType().GetField("Item1",R.Flags)!.GetValue(tuple)!;
            return ((string)R.Get(simple,"ModuleName")!,R.Get(simple,"ModuleType")!.ToString()!,tuple.GetType().GetField("Item2",R.Flags)!.GetValue(tuple)!.ToString()!);
        }
        var folderType=R.Static(assembly.GetType("Forguncy.Common.ModuleExtensions",true)!,"GetModuleType",root)!.ToString()!;
        var folderChanges=changes.Where(change=>Token(change).Type==folderType).ToArray(); var expected=descendants.ToDictionary(item=>NativeName(item.Address),StringComparer.Ordinal);
        var tokens=folderChanges.Select(Token).ToArray();
        if(tokens.Length!=expected.Count || tokens.Any(token=>token.State!="Remove" || !expected.ContainsKey(token.Name)) || tokens.Select(token=>token.Name).Distinct(StringComparer.Ordinal).Count()!=expected.Count)
            throw new BridgeException("NativeFolderPlanUnavailable","The native image folder owners differ from the loaded subtree.");
        var otherChanges=changes.Except(folderChanges).ToArray();
        var selectedNames=leaves.Select(leaf=>(string)R.Get(R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",leaf)!,"ModuleName")!).ToHashSet(StringComparer.Ordinal);
        if(otherChanges.Any(change=>Token(change).Type!="Image" || !selectedNames.Contains(Token(change).Name) || Token(change).State!="Remove"))
            throw new BridgeException("NativeFolderExternalReference","The native image deletion collector found an owner outside the selected image subtree.");
        var otherPlan=otherChanges.Length==0?null:files.CaptureNativeModulesCore(otherChanges,true);
        if(otherChanges.Length>0 && (otherPlan is null || otherPlan.ExpectedNewModules.Count!=0 || otherPlan.BeforeGroups.Any(group=>!files.Verify(group))) ||
            leaves.Any(leaf=>otherPlan is null || !otherPlan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,leaf)) || otherChanges.Count(change=>Token(change).Name==(string)R.Get(R.Static(assembly.GetType("Forguncy.Common.SimpleModule",true)!,"Create",leaf)!,"ModuleName")! && Token(change).State=="Remove")!=1))
            throw new BridgeException("NativeFolderPlanUnavailable","A native image Remove owner or source group is missing.");
        var directories=descendants.Select(item=>item.Key).ToArray(); var paths=(otherPlan?.BeforeGroups ?? []).SelectMany(group=>group.RequiredFiles.Concat(group.ReferencedFiles)).Where(path=>path.StartsWith(current.Key,StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        bool TreeCurrent()
        {
            var now=Items().Where(item=>item.Key==current.Key || item.Key.StartsWith(current.Key,StringComparison.Ordinal)).ToArray();
            var nowLeaves=now.SelectMany(item=>((IList)R.Get(item.Model,"Items")!).Cast<object>()).Where(model=>model.GetType().GetProperty("Items",R.Flags) is null).ToArray();
            return now.Length==descendants.Length && now.All(item=>descendants.Any(old=>ReferenceEquals(old.Model,item.Model) && old.Key==item.Key)) && nowLeaves.Length==leaves.Length && nowLeaves.All(leaf=>leaves.Any(old=>ReferenceEquals(old,leaf))) &&
                NativeRegisteredFolderEvidence.TreeMatches(Local,current.Key,directories,paths) && NativeRegisteredFolderEvidence.TreeMatches(Stage,current.Key,directories,paths) && NativeRegisteredFolderEvidence.TreeMatches(Source,current.Key,directories,paths) && (otherPlan is null || otherPlan.BeforeGroups.All(files.Verify));
        }
        if(!TreeCurrent()) throw new BridgeException("NativeFolderSourceChanged","The native image subtree differs from the saved inventory.");
        var folders=descendants.Select(item=>new NativeModuleFileGroup(item.Address,NativeName(item.Address),[],[],[],new Dictionary<string,string>(),
            ()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model) && found.Address==item.Address) && TreeCurrent(),nativeIdentity:item.Model,
            isPresent:()=>Items().Any(found=>ReferenceEquals(found.Model,item.Model)),requiredDirectories:[item.Key.TrimEnd('/')],directoriesRequireLocal:true)).ToArray();
        return new(new(folders.Concat(otherPlan?.BeforeGroups ?? []).ToArray(),[],nativeChanges:changes),folders.Single(group=>ReferenceEquals(group.NativeIdentity,current.Model)),otherPlan,destination,folders,PageFileContext:files,PageDeleteModels:leaves);
    }
    private IReadOnlyList<string> ApplyImageDelete(ModuleMutationRequest request,NativeFolderLifecyclePlan capture)
    {
        var (current,_)=Prepare(request,true); var files=capture.PageFileContext!; var leaves=capture.PageDeleteModels!;
        if(current is null || !ReferenceEquals(current.Model,capture.Before?.NativeIdentity) || capture.Plan.BeforeGroups.Any(group=>!group.Current()))
            throw new BridgeException("NativeFolderSourceChanged","The image subtree changed after its owner plan.");
        RequireImageReferencesContained(files,leaves);
        var latest=Capture(request,files);
        if(latest.Plan.BeforeGroups.Count!=capture.Plan.BeforeGroups.Count || latest.Plan.BeforeGroups.Any(group=>!capture.Plan.BeforeGroups.Any(old=>ReferenceEquals(old.NativeIdentity,group.NativeIdentity))))
            throw new BridgeException("NativeFolderSourceChanged","The native image deletion owner set changed after checkout.");
        R.Call(ImageManager(),"DeleteFolder",current.Model); current.Parent.Remove(current.Model); DirtyPlan(capture.Plan);
        foreach(var group in capture.FolderBefore!) foreach(var directory in group.RequiredDirectories) touched.Add(directory+"/");
        results[request.OperationId]=null;
        return capture.Plan.BeforeGroups.SelectMany(group=>group.Paths.Concat(group.RequiredDirectories)).Distinct(StringComparer.Ordinal).ToArray();
    }
}
