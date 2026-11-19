using System.Collections;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    private static bool IsProcessInitFolder(string type) => type is "TableFolder" or "Folder" or "MasterPageFolder";

    // First-process initialization creates nonempty native folders along with
    // the leaf owners. A folder is verified using an actual planned native child
    // whose persisted group physically resides in that folder; no invented
    // empty-folder file or artificial collaboration owner is introduced.
    private NativeModuleFileGroup? ResolveProcessInitFolder(NativeModuleFilePlan plan, string nativeName, string type)
    {
        var owner = type switch { "TableFolder" => ModuleKind.Table, "Folder" => ModuleKind.Page,
            "MasterPageFolder" => ModuleKind.MasterPage, _ => throw new InvalidOperationException() };
        var colleague = Colleague(owner);
        if (colleague is null || R.Get(colleague,"Root") is not { } root) return null;
        var extensions = assembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        var simple = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var expected = plan.ExpectedNewModules.ToHashSet();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var matches = new List<(object Folder,string Parent)>();
        void Visit(object folder, string parentNative, string relative)
        {
            if (!visited.Add(folder) || R.Get(folder,"Loaded") is not true) throw new InvalidOperationException("The native initialization tree is not fully loaded or contains a cycle.");
            foreach (var item in ((IEnumerable)R.Get(folder,"Items")!).Cast<object>())
            {
                if (item.GetType().GetProperty("Items", R.Flags) is null) continue;
                var itemName = (string)R.Get(item,"Name")!;
                var itemType = R.Static(extensions,"GetModuleType",item)!.ToString();
                var full = (string)R.Static(extensions,"GetModuleName",item,parentNative)!;
                if (itemType==type && full==nativeName) matches.Add((item,relative));
                Visit(item,full,relative.Length==0 ? itemName : relative+"/"+itemName);
            }
        }
        Visit(root,(string)R.Get(root,"Name")!,"");
        if (matches.Count!=1) return null;
        var (model,parent)=matches[0];
        var folderName=(string)R.Get(model,"Name")!;
        var folderSegments=(parent.Length==0 ? folderName : parent+"/"+folderName).Split('/');
        bool InNativeFolder(string path)
        {
            var directories=path.Split('/').SkipLast(1).ToArray();
            return Enumerable.Range(0,Math.Max(0,directories.Length-folderSegments.Length+1))
                .Any(index=>directories.Skip(index).Take(folderSegments.Length).SequenceEqual(folderSegments,StringComparer.Ordinal));
        }
        NativeModuleFileGroup? child=null;
        foreach (var item in Nodes(model).Where(IsLeaf))
        {
            var token=Token(R.Static(simple,"Create",item)!);
            if (token is null || !expected.Contains(token.Value) || Kind(token.Value.Type) is not { } childKind) continue;
            var candidate=Resolve(new(childKind,(string)R.Get(item,"Name")!));
            if (candidate is null || !candidate.RequiredFiles.Any(InNativeFolder)) continue;
            child=candidate; break;
        }
        if (child is null) return null;
        var capturedChild=child;
        bool Present() => Nodes(root).Any(item=>ReferenceEquals(item,model));
        var address=new ModuleAddress(ModuleKind.Folder,folderName,owner+(parent.Length==0 ? "" : "/"+parent));
        return new(address,nativeName,capturedChild.RequiredFiles,capturedChild.AbsentFiles,capturedChild.ReferencedFiles,
            capturedChild.ExpectedJson,()=>Present() && capturedChild.Current(),capturedChild.MatchesContent,model,Present);
    }
}
