using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed record NativeProcessBuiltInAssets(IReadOnlyList<string> Templates, IReadOnlyList<string> ResourceHashes)
{
    internal static NativeProcessBuiltInAssets Capture(Assembly assembly,object settings,string local,string staging,string source)
    {
        var web=Directory.GetParent(Path.GetDirectoryName(assembly.Location)!)!.FullName;
        var root=Path.Combine(web,"DesignerResources","BuiltInPages");
        var directories=new[]{Path.Combine(root,"BPMPages"),Path.Combine(root,"BPMPages_Mobile")};
        if(directories.Any(path=>!Directory.Exists(path))) throw Denied("The installed native BPM page resources are unavailable.");
        var files=directories.SelectMany(path=>Directory.EnumerateFiles(path,"*.json",SearchOption.AllDirectories)).Order(StringComparer.Ordinal).ToArray();
        if(files.Length==0) throw Denied("The installed native BPM page resource set is empty.");
        var hashes=new List<string>(); var templates=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var resources=new Dictionary<string,(string Raw,List<string> PairIds)>(StringComparer.Ordinal);
        void Scan(JsonElement value)
        {
            if(value.ValueKind==JsonValueKind.Object)
                foreach(var property in value.EnumerateObject())
                {
                    if(property.Name=="TemplatePageName" && property.Value.ValueKind==JsonValueKind.String && property.Value.GetString() is {Length:>0} name)
                    {
                        if(name.Length is not (31 or 32) || name.Any(character=>!Uri.IsHexDigit(character))) throw Denied("A native BPM template identifier is not a canonical native hexadecimal name.");
                        templates.Add(name);
                    }
                    Scan(property.Value);
                }
            else if(value.ValueKind==JsonValueKind.Array) foreach(var item in value.EnumerateArray()) Scan(item);
        }
        void PairIds(JsonElement value,List<string> result)
        {
            if(value.ValueKind==JsonValueKind.Object)
            {
                if(value.TryGetProperty("$type",out var type) && type.ValueKind==JsonValueKind.String && type.GetString()=="Forguncy.Model.ConditionCommand, ServerDesignerCommon" &&
                    value.TryGetProperty("ConditionAndCommandPairList",out var pairs) && pairs.ValueKind==JsonValueKind.Array)
                    foreach(var pair in pairs.EnumerateArray())
                        if(pair.ValueKind==JsonValueKind.Object && pair.TryGetProperty("ID",out var id) && id.ValueKind==JsonValueKind.String && id.GetString() is {Length:>0} text)
                        { if(!Guid.TryParse(text,out _)) throw Denied("A native condition pair has an invalid identity."); result.Add(text); }
                foreach(var property in value.EnumerateObject()) PairIds(property.Value,result);
            }
            else if(value.ValueKind==JsonValueKind.Array) foreach(var item in value.EnumerateArray()) PairIds(item,result);
        }
        foreach(var file in files)
        {
            var bytes=File.ReadAllBytes(file); hashes.Add(Path.GetRelativePath(root,file).Replace('\\','/')+":"+Convert.ToHexString(SHA256.HashData(bytes)));
            using var reader=new StreamReader(new MemoryStream(bytes),new System.Text.UTF8Encoding(false,true),true);
            var raw=reader.ReadToEnd(); using var json=JsonDocument.Parse(raw,new JsonDocumentOptions {CommentHandling=JsonCommentHandling.Skip,AllowTrailingCommas=true});
            Scan(json.RootElement); var pairIds=new List<string>(); PairIds(json.RootElement,pairIds); resources.Add(file,(raw,pairIds));
        }
        foreach(var name in templates)
        {
            var templateFile=files.SingleOrDefault(file=>Path.GetFileNameWithoutExtension(file).Equals(name,StringComparison.OrdinalIgnoreCase));
            if(templateFile is null) throw Denied("A native referenced BPM template source is missing or ambiguous.");
            foreach(var id in resources[templateFile].PairIds)
            {
                var occurrences=0;
                foreach(var resource in resources.Values)
                    for(var position=0;(position=resource.Raw.IndexOf(id,position,StringComparison.OrdinalIgnoreCase))>=0;position+=id.Length) occurrences++;
                if(occurrences!=1) throw Denied("A native template condition-pair identity is externally referenced or ambiguous.");
            }
        }
        foreach(var name in templates)
        {
            if(files.Count(file=>Path.GetFileNameWithoutExtension(file).Equals(name,StringComparison.OrdinalIgnoreCase))!=1)
                throw Denied("A native referenced BPM template source is missing or ambiguous.");
            foreach(var suffix in new[]{".json",".rd"})
            {
                var path="Pages/"+name+suffix;
                if(Exists(local,path) || Exists(staging,path) || SourceExists(source,path))
                    throw Denied("A native BPM template destination already exists in local, staging or saved source.");
            }
        }
        var pages=R.Get(settings,"Pages")!;
        var existing=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var visited=new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object node)
        {
            if(!visited.Add(node)) throw Denied("The loaded native page tree is cyclic or ambiguous.");
            if(node.GetType().GetProperty("Items",R.Flags) is not null)
            {
                if(R.Get(node,"Loaded") is not true) throw Denied("All native page trees must be loaded before first initialization.");
                foreach(var child in ((IEnumerable)R.Get(node,"Items")!).Cast<object>()) Visit(child);
            }
            else if(R.Get(node,"Name") is string name) existing.Add(name);
        }
        foreach(var property in new[]{"NormalPageRoot","MasterPageRoot","UserControlPageRoot"}) Visit(R.Get(pages,property)!);
        foreach(var template in ((IEnumerable)R.Get(pages,"TemplatePages")!).Cast<object>()) Visit(template);
        if(templates.Any(existing.Contains)) throw Denied("A native BPM template conflicts with an existing loaded page owner.");
        return new(templates.Order(StringComparer.Ordinal).ToArray(),hashes);
    }
    private static bool Exists(string root,string relative)
    {
        var ancestor=NativeSaveVerification.Under(root,"Pages"); if(File.Exists(ancestor)) return true;
        var path=NativeSaveVerification.Under(root,relative); return File.Exists(path) || Directory.Exists(path);
    }
    private static bool SourceExists(string source,string relative)
    {
        if(Directory.Exists(source)) return Exists(source,relative);
        using var archive=ZipFile.OpenRead(source);
        return archive.Entries.Any(entry=> {var path=entry.FullName.Replace('\\','/'); return path.Equals("Pages",StringComparison.OrdinalIgnoreCase) ||
            path.Equals(relative,StringComparison.OrdinalIgnoreCase) || path.StartsWith(relative+"/",StringComparison.OrdinalIgnoreCase);});
    }
    private static BridgeException Denied(string message)=>new("NativeProcessLifecyclePlanUnavailable",message);
}
