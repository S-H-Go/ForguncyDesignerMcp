using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V11.NativeV11Reflection;

namespace ForguncyDesignerMcp.Adapters.V11;

// Native deletion registration belongs to the exact owner in the reviewed plan.
internal static class NativeV11AssetRemovalRegistration
{
    internal static bool IsAssetPath(string path)=>new[]{"UserFile/","UserTemplateFile/","UserMapFile/","UserJaveScript/"}.Any(prefix=>path.StartsWith(prefix,StringComparison.Ordinal));
    internal static bool CoversAssetPath(string root,IReadOnlyList<string> groups,string path)
    {
        if(!IsAssetPath(path) || path.Contains('\\') || path.EndsWith('/') ||
            path.Split('/').Any(part=>part!=part.TrimEnd(' ','.')))throw Error("The reviewed asset path is not canonical.");
        _=V11Safety.Under(root,path);
        var covered=false;
        foreach(var group in groups)
        {
            var directory=group.EndsWith('/');
            var relative=directory?group[..^1]:group;
            if(relative.Contains('\\') || relative.Split('/').Any(part=>part!=part.TrimEnd(' ','.')))
                throw Error("The native owner file group is not canonical.");
            _=V11Safety.Under(root,relative);
            var matches=directory?path.StartsWith(group,StringComparison.OrdinalIgnoreCase):path.Equals(group,StringComparison.OrdinalIgnoreCase);
            if(!matches)continue;
            if(!(directory?path.StartsWith(group,StringComparison.Ordinal):path.Equals(group,StringComparison.Ordinal)))
                throw Error("The reviewed asset has a native owner path case alias.");
            covered=true;
        }
        return covered;
    }
    internal static IReadOnlyList<string> RestoredPaths(NativeV11Collaboration.Plan plan,string ownerName,IReadOnlyList<string> presentFiles)
    {
        NativeV11Collaboration.VerifyOwned(plan,true);
        var owner=plan.Owners.SingleOrDefault(value=>(string?)R.Get(value.SimpleModule,"ModuleName")==ownerName)
            ?? throw Error("The asset owner is outside the frozen native plan.");
        if(owner.State!="Edit" || plan.Modules[ownerName] is not object module || R.Get(module,"State")?.ToString()!="Edit")
            throw Error("Asset restoration requires the existing native Edit owner.");
        var current=NativeV11CollaborationOwners.GetDirectFiles(plan.Session,owner with { State="Edit" });
        var present=presentFiles.Distinct(StringComparer.Ordinal).ToArray();
        if(present.Length!=presentFiles.Count || present.Any(path=>!CoversAssetPath(plan.Session.SourcePath,current,path) || !File.Exists(V11Safety.Under(plan.Session.SourcePath,path))))
            throw Error("A reviewed restoration is absent or outside its native owner group.");
        var result=new List<string>();
        foreach(var entry in Files(R.Get(module,"ToRemoveFiles")))
        {
            var old=entry.Replace('\\','/');var aliases=present.Where(path=>path.Equals(old,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(aliases.Length>1 || aliases.Length==1 && aliases[0]!=old)throw Error("A restored asset has an ambiguous removal alias.");
            if(aliases.Length==1)result.Add(old);
        }
        return result;
    }
    internal static async Task RegisterAsync(NativeV11Collaboration.Plan plan,string ownerName,
        IReadOnlyList<string> removedFiles,IReadOnlyList<string> restoredFiles)
    {
        NativeV11Collaboration.VerifyOwned(plan,true);
        var owner=plan.Owners.SingleOrDefault(value=>(string?)R.Get(value.SimpleModule,"ModuleName")==ownerName)
            ?? throw Error("The asset owner is outside the frozen native plan.");
        if(owner.State!="Edit" || plan.Modules[ownerName] is not object module || R.Get(module,"State")?.ToString()!="Edit")
            throw Error("Asset removal requires the existing native Edit owner.");
        if(!plan.Files.TryGetValue(ownerName,out var reviewed) || removedFiles.Any(path=>!CoversAssetPath(plan.Session.SourcePath,reviewed,path)))
            throw Error("A deletion is outside the owner's reviewed native file group.");
        if(restoredFiles.Count!=0)
        {
            var currentFiles=NativeV11CollaborationOwners.GetDirectFiles(plan.Session,owner with { State="Edit" });
            if(restoredFiles.Any(path=>!CoversAssetPath(plan.Session.SourcePath,currentFiles,path)))
                throw Error("A restored asset is outside the owner's current native file group.");
        }
        var root=plan.Session.SourcePath;
        var tracked=await TrackedAtHeadAsync(root,removedFiles);
        if(!tracked.SequenceEqual(removedFiles,StringComparer.Ordinal))
            throw Error("A native deletion registration must already exist at HEAD.");
        NativeV11Collaboration.VerifyOwned(plan,true);
        RegisterCore(plan.Manager,root,ownerName,removedFiles,restoredFiles);
        NativeV11Collaboration.VerifyOwned(plan,true);
    }

    // Classify reviewed exact files, including an unsaved-to-HEAD new file.
    // The caller must separately prove the original bytes or its own prior save.
    internal static async Task<IReadOnlyList<string>> TrackedAtHeadAsync(string root,IReadOnlyList<string> paths)
    {
        var distinct=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in paths)
        {
            if(!IsAssetPath(path) || path.EndsWith('/') || !distinct.Add(path))throw Error("Invalid or duplicate reviewed asset path.");
            _=V11Safety.Under(root,path);
        }
        var probe=new GitProbe(root);
        var before=await probe.CaptureAsync();
        if(string.IsNullOrEmpty(before.Head) || !before.IndexClean)throw Error("A clean index and verified HEAD are required.");
        var start=new ProcessStartInfo("git") { WorkingDirectory=root,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.Environment["GIT_OPTIONAL_LOCKS"]="0";start.Environment["GIT_TERMINAL_PROMPT"]="0";
        foreach(var argument in new[]{"ls-tree","-r","--name-only","-z",before.Head})start.ArgumentList.Add(argument);
        using var process=Process.Start(start) ?? throw Error("Git HEAD inspection is unavailable.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output=process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors=process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch(OperationCanceledException) { process.Kill(true);throw Error("Git HEAD inspection timed out."); }
        var tree=await output;await errors;
        if(process.ExitCode!=0 || tree.Length>16*1024*1024)throw Error("Git HEAD inspection failed or exceeded its limit.");
        var tracked=tree.Split('\0',StringSplitOptions.RemoveEmptyEntries);
        var result=new List<string>();
        foreach(var path in paths)
        {
            var matches=tracked.Where(value=>value.Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(matches.Length>1 || matches.Length==1 && matches[0]!=path)throw Error("A reviewed asset has an ambiguous HEAD path alias.");
            if(matches.Length==1)result.Add(path);
        }
        var after=await probe.CaptureAsync();
        if(before.Head!=after.Head || before.Branch!=after.Branch || before.IndexClean!=after.IndexClean || before.WorktreeHash!=after.WorktreeHash)
            throw Error("The repository changed during the HEAD proof.");
        return result;
    }

    // Isolated native tests supply a private manager and working directory.
    internal static void RegisterCore(object manager,string root,string ownerName,IReadOnlyList<string> removedFiles,
        IReadOnlyList<string>? reviewedRestoredPaths=null)
    {
        var restored=reviewedRestoredPaths??[];
        var distinct=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var path in removedFiles)
        {
            if(!distinct.Add(path) || path.EndsWith('/') || !IsAssetPath(path))throw Error("A reviewed root tombstone is duplicated or outside the native asset file roots.");
            _=V11Safety.Under(root,path);
            if(File.Exists(V11Safety.Under(root,path)) || Directory.Exists(V11Safety.Under(root,path)))throw Error("Every reviewed deletion must already be absent from the verified project source.");
        }
        foreach(var path in restored)
        {
            if(!distinct.Add(path) || path.EndsWith('/') || !IsAssetPath(path))
                throw Error("A restored native file is duplicated, also removed, or outside its library roots.");
            _=V11Safety.Under(root,path);
            if(!File.Exists(V11Safety.Under(root,path)))throw Error("Every reviewed restoration must already exist in the verified project source.");
        }
        if(R.Get(manager,"Modules") is not IDictionary modules || modules[ownerName] is not object module || R.Get(module,"ModuleName") as string!=ownerName)throw Error("The exact native root registration is unavailable.");
        var registration=Path.GetFullPath((string)R.Get(manager,"ModuleFilePath")!);
        if(!registration.Equals(Path.Combine(root,".collaboration"),StringComparison.OrdinalIgnoreCase))throw Error("The native module source is not the bound project registration.");
        var registrationBefore=File.ReadAllBytes(registration);
        var persisted=ReadPersistedModules(registration);
        if(!registrationBefore.AsSpan().SequenceEqual(File.ReadAllBytes(registration)))throw Error("The native registration changed while its baseline was read.");
        var before=Image(persisted);if(!before.TryGetValue(ownerName,out var oldPersisted))throw Error("The native root is absent from the persisted registration.");
        var persistedOwner=persisted.Single(item=>(string)R.Get(item,"ModuleName")! == ownerName);
        if(WithoutRemoved(persistedOwner)!=WithoutRemoved(module))throw Error("The native owner differs from the persisted registration.");
        var old=Files(R.Get(module,"ToRemoveFiles"));var sourceOld=Files(R.Get(persistedOwner,"ToRemoveFiles"));
        if(!old.SequenceEqual(sourceOld,StringComparer.Ordinal))throw Error("Native owner removal registration changed before dispatch.");
        if(restored.Any(path=>!old.Any(entry=>entry.Replace('\\','/')==path)))
            throw Error("A reviewed restoration must already have an exact native removal registration.");
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
        if(removedFiles.Any(path=>File.Exists(V11Safety.Under(root,path)) || Directory.Exists(V11Safety.Under(root,path))) ||
           restored.Any(path=>!File.Exists(V11Safety.Under(root,path))))
        {
            R.Set(module,"ToRemoveFiles",old);
            throw Error("An asset source changed before saving its native registration.");
        }
        try { R.Call(manager,"SaveModules",complete); }
        catch { R.Set(module,"ToRemoveFiles",old);throw; }
        var current=ReadPersistedModules(registration);var after=Image(current);
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
    private static object[] ReadPersistedModules(string path)
    {
        var type=Assembly.Load("Forguncy.Collaboration").GetType("Forguncy.Collaboration.Module",true)!;
        var list=typeof(List<>).MakeGenericType(type);
        var utility=Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities",true)!;
        var read=utility.GetMethods(R.Flags).Single(method=>method.Name=="ReadObjectFromFile" && method.IsGenericMethodDefinition && method.GetParameters().Length==2 && method.GetParameters()[0].ParameterType==typeof(string));
        return ((IEnumerable)read.MakeGenericMethod(list).Invoke(null,[path,null])!).Cast<object>().ToArray();
    }
    private static BridgeException Error(string message)=>new("NativeRootRemovalTrackingUnverified",message);
}
