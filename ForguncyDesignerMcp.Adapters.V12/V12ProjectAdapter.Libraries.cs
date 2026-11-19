using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter
{
    private readonly HashSet<string> pendingResourceOwnerNames=new(StringComparer.Ordinal);
    private void ValidateNativeLibraryLifecycle(string method,JsonElement parameters)
    {
        var deleted=ReduceFileGroups(apiTargetGroups.Values.Concat(typedTargets.Values))
            .Where(group=>group.Address.Kind==ModuleKind.CustomLibrary && group.Current()).SelectMany(group=>group.AbsentFiles).ToHashSet(StringComparer.Ordinal);
        NativeLibraryLifecycle.Validate(context,targetFiles.Value,resources.Value,method,parameters,deleted);
    }

    private async Task CompleteSavedLibraryOwners(IEnumerable<NativeModuleFileGroup> savedGroups)
    {
        foreach(var current in ReduceFileGroups(savedGroups).Where(group=>group.Address.Kind==ModuleKind.CustomLibrary || pendingResourceOwnerNames.Contains(group.NativeModuleName)))
        {
            if(!targetFiles.Value.Verify(current))
                throw new ForguncyDesignerMcp.Core.BridgeException("LibrarySavedGroupChanged","The saved native library group changed before removal tracking.");
            frontendSavedGroups.TryGetValue(current.NativeModuleName,out var previous);
            var merged=previous is null?current:MergeFileGroups(previous,current);
            if(!targetFiles.Value.Verify(merged))
                throw new ForguncyDesignerMcp.Core.BridgeException("LibrarySavedGroupChanged","The complete saved library journal no longer matches its source.");
            var candidates=merged.AbsentFiles.Where(IsLibraryBinaryPath).Distinct(StringComparer.Ordinal).ToArray();
            // A created-then-removed entry never committed to HEAD needs no
            // native index removal. Its absence remains in the source journal.
            var removed=candidates.Length==0?Array.Empty<string>():
                await new NativeCollaborationGit(context.CaptureSnapshot().Target.ProjectPath).TrackedFilesAtHeadAsync(candidates);
            if(!targetFiles.Value.Verify(merged))
                throw new ForguncyDesignerMcp.Core.BridgeException("LibrarySavedGroupChanged","The saved native library source changed during tracked-file inspection.");
            var restored=NativeRootRemovedFiles.RestoredPaths(context,guard.Value,current.NativeModuleName,
                current.RequiredFiles.Concat(current.ReferencedFiles).Distinct(StringComparer.Ordinal).ToArray());
            if(removed.Count!=0 || restored.Count!=0)
                NativeRootRemovedFiles.Register(context,guard.Value,current.NativeModuleName,removed,restored);
            RetainSavedLibraryOwner(merged);
        }
    }

    private void RetainSavedLibraryOwner(NativeModuleFileGroup current)
    {
        frontendSavedGroups[current.NativeModuleName]=frontendSavedGroups.TryGetValue(current.NativeModuleName,out var previous)
            ?MergeFileGroups(previous,current):current;
    }
    private static bool IsLibraryBinaryPath(string path)=>NativeRootRemovedFiles.IsAssetPath(path);
}
