using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    // Referencing built-in pages use the same real native template owner
    // groups. Their references cannot depend on LocalPath: page templates are
    // saved through FolderSaveData/PageSaveData and may never exist there.
    internal List<NativeModuleFileGroup>? ProtectProcessTemplateReferences(NativeModuleFilePlan plan,List<NativeModuleFileGroup> groups)
    {
        if(plan.ProcessAssets is null) return groups;
        var assets=groups.Where(group=>group.NativeIdentity.GetType().Name=="TemplatePage" && plan.ProcessAssets.Templates.Contains(group.Address.Name,StringComparer.Ordinal)).ToArray();
        if(assets.Length!=plan.ProcessAssets.Templates.Count) return null;
        var files=new Dictionary<string,NativeModuleFileGroup>(StringComparer.Ordinal);
        foreach(var asset in assets)
            foreach(var path in asset.RequiredFiles)
                if(!files.TryAdd(path,asset)) return null;
        var result=new List<NativeModuleFileGroup>();
        foreach(var original in groups)
        {
            if(assets.Any(asset=>ReferenceEquals(asset.NativeIdentity,original.NativeIdentity))) {result.Add(original); continue;}
            if(original.RequiredFiles.Any(files.ContainsKey)) return null;
            var references=original.ReferencedFiles.Where(files.ContainsKey).Select(path=>files[path]).DistinctBy(group=>group.NativeIdentity,ReferenceEqualityComparer.Instance).ToArray();
            if(references.Length==0) {result.Add(original); continue;}
            result.Add(new(original.Address,original.NativeModuleName,original.RequiredFiles,original.AbsentFiles,original.ReferencedFiles,original.ExpectedJson,
                ()=>original.Current() && references.All(group=>group.Current()),
                (path,bytes)=>files.TryGetValue(path,out var owner)?owner.MatchesContent(path,bytes):original.MatchesContent(path,bytes),
                original.NativeIdentity,original.IsPresent,original.RequiredDirectories,original.AbsentDirectories,original.DirectoriesRequireLocal));
        }
        return result;
    }
}
