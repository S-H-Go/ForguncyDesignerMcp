using System.Collections;
using System.IO;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    private static readonly ModuleAddress MapResourceOwner=new(ModuleKind.ResourceFile,"Current","MapJsonFile");

    private object? MapOwner()
    {
        var source=(string)R.Call(save,"GetDocumentPath")!;
        if(!Directory.Exists(source) || R.Get(save,"Loaded") is not true)return null;
        var state=R.Static(assembly.GetType("s",true)!,"get_Instance")!;
        if(!ReferenceEquals(R.Get(state,"et"),settings))return null;
        var manager=R.Static(assembly.GetType("Forguncy.Common.CollaborationManager",true)!,"get_Instance")!;
        if(!Path.GetFullPath((string)R.Get(manager,"ModuleFilePath")!).Equals(Path.Combine(Path.GetFullPath(source),".collaboration"),StringComparison.OrdinalIgnoreCase))return null;
        if(R.Get(manager,"Modules") is not IDictionary modules)return null;
        var owners=modules.Values.Cast<object>().Where(module=>R.Get(module,"ModuleType")?.ToString()=="MapJsonFile").ToArray();
        if(owners.Length!=1)return null;
        var name=(string)R.Get(owners[0],"ModuleName")!;
        return ReferenceEquals(modules[name],owners[0])?owners[0]:null;
    }

    private NativeModuleFileGroup? MapResources()
    {
        var module=MapOwner();if(module is null)return null;
        var local=(string)R.Get(save,"LocalPath")!;
        var configured=(string)R.Static(assembly.GetType("Forguncy.Common.Config",true)!,"get_LocalUserMapFilePath")!;
        if(!Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured)).Equals(Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(local)),"UserMapFile"),StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("MapResourceCacheMismatch","The native map directory belongs to a different project cache.");
        var expected=MapInventory(local);
        var helper=assembly.GetType("Forguncy.Common.ModuleExtensions",true)!;
        string[] NativeFiles()=>((IEnumerable)R.Static(helper,"GetDirectReferFiles",module,local,"../Upload")!).Cast<string>()
            .Select(path=>NativeSaveVerification.Relative(local,path)).Order(StringComparer.Ordinal).ToArray();
        var paths=expected.Keys.Order(StringComparer.Ordinal).ToArray();
        if(!NativeFiles().SequenceEqual(paths,StringComparer.Ordinal))
            throw new BridgeException("MapResourceOwnerIncomplete","The native map owner provider does not exactly cover the local map tree.");
        bool Current()=>ReferenceEquals(MapOwner(),module) &&
            MapInventory(local).OrderBy(pair=>pair.Key,StringComparer.Ordinal).SequenceEqual(expected.OrderBy(pair=>pair.Key,StringComparer.Ordinal)) &&
            NativeFiles().SequenceEqual(paths,StringComparer.Ordinal);
        return new(MapResourceOwner,(string)R.Get(module,"ModuleName")!,paths,[],[],new Dictionary<string,string>(),Current,
            (path,bytes)=>expected.TryGetValue(path,out var hash) && Hash(bytes)==hash,module,()=>ReferenceEquals(MapOwner(),module));
    }

    private bool VerifyMapResources(NativeModuleFileGroup group)
    {
        if(group.Address!=MapResourceOwner || !ReferenceEquals(MapOwner(),group.NativeIdentity) || !group.Current())return false;
        var expected=group.RequiredFiles.Concat(group.ReferencedFiles).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if(expected.Any(path=>!path.StartsWith("UserMapFile/",StringComparison.Ordinal)) || group.AbsentFiles.Any(path=>!path.StartsWith("UserMapFile/",StringComparison.Ordinal)))return false;
        foreach(var rootPath in new[]{(string)R.Get(save,"LocalPath")!,(string)R.Get(save,"LastPath")!,(string)R.Call(save,"GetDocumentPath")!})
        {
            var actual=MapInventory(rootPath);
            if(!actual.Keys.Order(StringComparer.Ordinal).SequenceEqual(expected,StringComparer.Ordinal))return false;
            foreach(var path in expected)
            {
                var bytes=NativeSaveVerification.ReadSource(rootPath,path);
                if(bytes is null || !group.MatchesContent(path,bytes))return false;
            }
        }
        // An empty native aggregate remains present. Empty inventory in all
        // three bound locations is its proof, rather than a fictitious deletion.
        return true;
    }

    private static Dictionary<string,string> MapInventory(string rootPath)
    {
        var result=new Dictionary<string,string>(StringComparer.Ordinal);
        var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directory=NativeSaveVerification.Under(rootPath,"UserMapFile");
        if(File.Exists(directory))throw new BridgeException("MapResourcePathInvalid","The native map root is a file.");
        if(!Directory.Exists(directory))return result;
        void Visit(string path)
        {
            _=NativeSaveVerification.Under(rootPath,Path.GetRelativePath(rootPath,path).Replace('\\','/'));
            foreach(var file in Directory.EnumerateFiles(path))
            {
                var relative=NativeSaveVerification.Relative(rootPath,file);
                _=NativeSaveVerification.Under(rootPath,relative);
                if(!aliases.Add(relative))throw new BridgeException("MapResourcePathAlias","Native map files contain a case alias.");
                result.Add(relative,FileHash(file));
            }
            foreach(var child in Directory.EnumerateDirectories(path))Visit(child);
        }
        Visit(directory);return result;
    }
}
