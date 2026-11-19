using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>Appends reviewed source tombstones to an already-owned native root, using its real ToRemoveFiles and SaveModules lifecycle.</summary>
internal static class NativeRootRemovedFiles
{
    internal static bool IsAssetPath(string path)=>path=="favicon.ico" ||
        new[]{"UserFile/","FrontendLibraries/","UserTemplateFile/","UserMapFile/","UserJaveScript/"}.Any(prefix=>path.StartsWith(prefix,StringComparison.Ordinal));
    internal static IReadOnlyList<string> RestoredPaths(AdapterHostContext context,NativeProjectGuard guard,string ownerName,
        IReadOnlyList<string> presentFiles)
    {
        var state=guard.Capture();
        if(!state.InCollaborationMode)throw Error("Native library restoration requires the bound collaboration registration.");
        guard.RequireOwnedModule(state,ownerName);
        var root=Path.GetFullPath(state.Target.ProjectPath);
        var manager=R.Static(context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager",true)!,"get_Instance")!;
        if(R.Get(manager,"Modules") is not IDictionary modules || modules[ownerName] is not object module)
            throw Error("The exact native library owner is unavailable.");
        if(!Path.GetFullPath((string)R.Get(manager,"ModuleFilePath")!).Equals(Path.Combine(root,".collaboration"),StringComparison.OrdinalIgnoreCase))
            throw Error("The native library owner belongs to another collaboration registration.");
        return RestoredPathsCore(module,root,presentFiles);
    }

    // A document can be reopened while its owner remains checked out. Read
    // persisted native removal state rather than relying on a prior adapter's
    // in-memory journal when a saved file is recreated in a new session.
    internal static IReadOnlyList<string> RestoredPathsCore(object module,string root,IReadOnlyList<string> presentFiles)
    {
        var present=presentFiles.Where(IsAssetPath)
            .Distinct(StringComparer.Ordinal).ToArray();
        var restored=new List<string>();
        foreach(var removed in Files(R.Get(module,"ToRemoveFiles")))
        {
            var path=removed.Replace('\\','/');
            var aliases=present.Where(candidate=>candidate.Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(aliases.Length==0)continue;
            if(aliases.Length!=1 || aliases[0]!=path)throw Error("A restored library file has an ambiguous native removal-path alias.");
            _=NativeSaveVerification.Under(root,path);
            if(NativeSaveVerification.ReadSource(root,path) is null)throw Error("A restored native library path is absent from the verified project source.");
            restored.Add(path);
        }
        return restored;
    }

    internal static void Register(AdapterHostContext context,NativeProjectGuard guard,string ownerName,IReadOnlyList<string> removedFiles,
        IReadOnlyList<string>? reviewedRestoredPaths=null)
    {
        var state=guard.Capture();
        if(!state.InCollaborationMode || state.CurrentUser is null || removedFiles.Count+(reviewedRestoredPaths?.Count??0)==0)throw Error("A bound collaboration owner and reviewed removal/restoration paths are required.");
        guard.RequireOwnedModule(state,ownerName);
        var root=Path.GetFullPath(state.Target.ProjectPath);
        if(!Directory.Exists(root))throw Error("Native root deletion tracking requires the bound collaboration working directory.");
        var manager=R.Static(context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager",true)!,"get_Instance")!;
        RegisterCore(manager,root,ownerName,removedFiles,reviewedRestoredPaths);
        var afterState=guard.Capture();
        if(afterState.Target!=state.Target || afterState.CurrentUser!=state.CurrentUser || afterState.Busy || afterState.HasUnsavedChanges!=state.HasUnsavedChanges)throw Error("The native project changed while saving its owner tombstones.");
        guard.RequireOwnedModule(afterState,ownerName);
    }
    // Guarded by Register in production; isolated native tests supply a private manager and working directory.
    internal static void RegisterCore(object manager,string root,string ownerName,IReadOnlyList<string> removedFiles,
        IReadOnlyList<string>? reviewedRestoredPaths=null)
    {
        var restored=reviewedRestoredPaths??[];
        var distinct=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in removedFiles)
        {
            if(!distinct.Add(path) || path.EndsWith('/') || !IsAssetPath(path))throw Error("A reviewed root tombstone is duplicated or outside the native asset file roots.");
            _=NativeSaveVerification.Under(root,path);
            if(NativeSaveVerification.ReadSource(root,path) is not null)throw Error("Every reviewed deletion must already be absent from the verified project source.");
        }
        foreach(var path in restored)
        {
            if(!distinct.Add(path) || path.EndsWith('/') || !IsAssetPath(path))
                throw Error("A restored native file is duplicated, also removed, or outside its library roots.");
            _=NativeSaveVerification.Under(root,path);
            if(NativeSaveVerification.ReadSource(root,path) is null)throw Error("Every reviewed restoration must already exist in the verified project source.");
        }
        if(R.Get(manager,"Modules") is not IDictionary modules || modules[ownerName] is not object module || R.Get(module,"ModuleName") as string!=ownerName)throw Error("The exact native root registration is unavailable.");
        var registration=Path.GetFullPath((string)R.Get(manager,"ModuleFilePath")!);
        if(!registration.Equals(Path.Combine(root,".collaboration"),StringComparison.OrdinalIgnoreCase))throw Error("The native module source is not the bound project registration.");
        var registrationBefore=File.ReadAllBytes(registration);
        var persisted=NativeCollaboration.ReadPersistedModules(registration);
        if(!registrationBefore.AsSpan().SequenceEqual(File.ReadAllBytes(registration)))throw Error("The native registration changed while its baseline was read.");
        var before=Image(persisted);if(!before.TryGetValue(ownerName,out var oldPersisted))throw Error("The native root is absent from the persisted registration.");
        var persistedOwner=persisted.Single(item=>(string)R.Get(item,"ModuleName")! == ownerName);
        if(WithoutRemoved(persistedOwner)!=WithoutRemoved(module))throw Error("The native owner differs from the persisted registration.");
        var old=Files(R.Get(module,"ToRemoveFiles"));var sourceOld=Files(R.Get(persistedOwner,"ToRemoveFiles"));
        if(!old.SequenceEqual(sourceOld,StringComparer.Ordinal))throw Error("Native owner removal registration changed before dispatch.");
        if(restored.Any(path=>old.Any(entry=>entry.Replace('\\','/').Equals(path,StringComparison.OrdinalIgnoreCase) && entry.Replace('\\','/')!=path)))
            throw Error("A restored native file has an ambiguous removal-path case alias.");
        var merged=old.Where(path=>!restored.Contains(path.Replace('\\','/'),StringComparer.Ordinal)).ToList();
        foreach(var path in removedFiles)
        {
            var aliases=merged.Where(x=>x.Replace('\\','/').Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(aliases.Any(x=>x.Replace('\\','/')!=path) || aliases.Length>1)throw Error("The native owner already contains an ambiguous removal alias.");
            if(aliases.Length==0)merged.Add(path);
        }
        R.Set(module,"ToRemoveFiles",merged);
        // The native IEnumerable overload serializes exactly the supplied set;
        // passing only the edited owner would drop every other registration.
        // Preserve the full frozen persisted list and substitute only this owner.
        var complete=Array.CreateInstance(module.GetType(),persisted.Length);
        for(var index=0;index<persisted.Length;index++)
            complete.SetValue((string)R.Get(persisted[index],"ModuleName")! == ownerName ? module : persisted[index],index);
        if(!registrationBefore.AsSpan().SequenceEqual(File.ReadAllBytes(registration)))
        {
            R.Set(module,"ToRemoveFiles",old);
            throw Error("The native registration changed before saving root tombstones.");
        }
        R.Call(manager,"SaveModules",complete);
        var current=NativeCollaboration.ReadPersistedModules(registration);var after=Image(current);
        if(after.Count!=before.Count || before.Any(pair=>pair.Key!=ownerName && (!after.TryGetValue(pair.Key,out var other)||other!=pair.Value)))throw Error("Native SaveModules changed another owner's registration.");
        var saved=current.SingleOrDefault(item=>(string)R.Get(item,"ModuleName")! == ownerName) ?? throw Error("Native SaveModules removed the root registration.");
        if(WithoutRemoved(saved)!=WithoutRemoved(persistedOwner) || !Files(R.Get(saved,"ToRemoveFiles")).SequenceEqual(merged,StringComparer.Ordinal) ||
            !Files(R.Get(module,"ToRemoveFiles")).SequenceEqual(merged,StringComparer.Ordinal))throw Error("The native removal registration did not persist exactly.");
    }
    private static Dictionary<string,string> Image(IEnumerable<object> modules)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var module in modules)if(!result.TryAdd((string)R.Get(module,"ModuleName")!,Serialize(module,false)))throw Error("Duplicate persisted native owner names.");
        return result;
    }
    private static string WithoutRemoved(object module)=>Serialize(module,true);
    private static string Serialize(object module,bool remove)
    {
        var json=Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert",true)!;
        var serialized=(string)R.Static(json,"SerializeObject",module)!;
        var node=JsonNode.Parse(serialized) as JsonObject ?? throw Error("Invalid native module serialization.");
        if(remove)node.Remove("ToRemoveFiles");
        return node.ToJsonString();
    }
    private static string[] Files(object? value)
    {
        if(value is null)return [];
        if(value is not IEnumerable values)throw Error("Native removal registration is not enumerable.");
        var result=values.Cast<object>().Select(x=>x as string ?? throw Error("Native removal path is not a string.")).ToArray();
        if(result.Select(x=>x.Replace('\\','/')).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=result.Length)throw Error("Native removal registration contains aliases.");
        return result;
    }
    private static BridgeException Error(string message)=>new("NativeRootRemovalTrackingUnverified",message);
}
