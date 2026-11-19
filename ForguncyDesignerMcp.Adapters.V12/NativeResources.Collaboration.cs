using System.Collections;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed record NativeResourceOwnerCapture(NativeModuleFilePlan Plan, string RelativePath, ModuleOperation Operation);

public sealed partial class NativeResources
{
    internal NativeResourceOwnerCapture CaptureCollaborationOwners(ModuleMutationRequest request, NativeModuleFiles files)
    {
        BindCacheIfReady();
        if (snapshot?.Invoke().InCollaborationMode != true)
            throw new BridgeException("CollaborationNotApplicable", "Resource owner capture requires the actual collaboration document.");
        var relative = Relative(request.Address);
        RequireTracking(relative);
        var names = Owners(relative);
        var manager = NativeReflection.Static(assembly.GetType("Forguncy.Common.CollaborationManager", true)!, "get_Instance")!;
        var registry = NativeReflection.Get(manager, "Modules") as IDictionary
            ?? throw new BridgeException("ResourceOwnershipUnavailable", "The genuine native module registry is unavailable.");
        var modules = names.Select(name => registry[name] ?? throw new BridgeException("ResourceOwnershipUnavailable", "A resource save owner is no longer registered.")).ToArray();
        var groups = files.ResolveNativeModules(modules)
            ?? throw new BridgeException("ResourceOwnerFilesUnavailable", "Every native resource owner must have a complete file-group mapping.");
        if (groups.Count == 0 || !groups.Select(group => group.NativeModuleName).ToHashSet(StringComparer.Ordinal).SetEquals(names) || groups.Any(group => !group.Current()))
            throw new BridgeException("ResourceOwnerFilesUnavailable", "The native resource owner set or model/file baseline is incomplete.");
        return new(new(groups, []), relative, request.Operation);
    }

    internal IReadOnlyList<NativeModuleFileGroup> CaptureOwnersAfter(NativeResourceOwnerCapture capture, NativeModuleFiles files)
    {
        var bytes = CacheBytes(capture.RelativePath);
        if (capture.Operation == ModuleOperation.Delete ? bytes is not null : bytes is null)
            throw new BridgeException("NativeResourceReadbackMismatch", "The resource cache differs from the requested operation.");
        var expected = bytes is null ? null : NativeFiles.Hash(bytes);
        var result = new List<NativeModuleFileGroup>();
        foreach (var before in capture.Plan.BeforeGroups)
        {
            var group = files.ResolveIdentity(before.NativeIdentity)
                ?? throw new BridgeException("ResourceOwnerChanged", "A resource owner changed identity during the native file mutation.");
            var required = group.RequiredFiles.Concat(expected is null ? [] : new[] { capture.RelativePath }).Distinct(StringComparer.Ordinal).ToArray();
            var referenced = group.ReferencedFiles.Except(required, StringComparer.Ordinal)
                .Where(path => expected is not null || path != capture.RelativePath).ToArray();
            var absent = group.AbsentFiles.Concat(expected is null ? new[] { capture.RelativePath } : [])
                .Except(required, StringComparer.Ordinal).Except(referenced, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).ToArray();
            bool Current()
            {
                var current = CacheBytes(capture.RelativePath);
                return group.Current() && (expected is null ? current is null : current is not null && NativeFiles.Hash(current) == expected);
            }
            result.Add(new(group.Address, group.NativeModuleName, required, absent, referenced, group.ExpectedJson, Current,
                (path, content) => path == capture.RelativePath ? expected is not null && NativeFiles.Hash(content) == expected : group.MatchesContent(path, content),
                group.NativeIdentity, group.IsPresent, group.RequiredDirectories, group.AbsentDirectories));
        }
        return result;
    }
}
