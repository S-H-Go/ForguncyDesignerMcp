using System.Collections;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter
{
    private sealed record AssetPathEvidence(string[] Owners, string RegistrationOwner, bool TrackedAtHead, byte[]? Content);
    private sealed record AssetPending(string Path, string[] Owners, string RegistrationOwner, bool TrackedAtHead, byte[]? Before, byte[]? Expected);
    private sealed record AssetSaved(GitState Git, string SourceRevision, string[] Owners,
        Dictionary<string, AssetPathEvidence> Paths);
    private readonly Dictionary<string, AssetPending> pendingAssets = new(StringComparer.Ordinal);
    private AssetSaved? savedAssets;

    internal static string[] MergeAssetOwners(IEnumerable<string> saved, IEnumerable<string> current) =>
        saved.Concat(current).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static bool SameBytes(byte[]? left, byte[]? right) => left is null ? right is null :
        right is not null && left.AsSpan().SequenceEqual(right);

    internal static bool SameAssetGit(GitState left, GitState right) => left.Head == right.Head &&
        left.Branch == right.Branch && left.RemoteName == right.RemoteName && left.RemoteUrl == right.RemoteUrl &&
        left.Upstream == right.Upstream && left.UpstreamHead == right.UpstreamHead &&
        left.Author == right.Author && left.Committer == right.Committer &&
        left.IndexClean && right.IndexClean && left.WorktreeHash == right.WorktreeHash &&
        left.ChangedFiles is not null && right.ChangedFiles is not null && left.ChangedFiles.Count == right.ChangedFiles.Count &&
        left.ChangedFiles.All(pair => right.ChangedFiles.Keys.Contains(pair.Key, StringComparer.Ordinal) &&
            right.ChangedFiles.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private async Task<bool> ReuseAssetOwnersAsync(NativeV11Collaboration.Plan plan, GitState git)
    {
        if (savedAssets is null || !SameAssetGit(savedAssets.Git, git) ||
            savedAssets.SourceRevision != NativeV11SourceRevision.Capture(session.SourcePath)) return false;
        var names = plan.Owners.Select(owner => (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!).ToArray();
        if (!NativeV11Collaboration.CanReuseEditStates(plan)) return false;
        NativeV11Collaboration.VerifyOwned(plan, true);
        foreach (var (path, evidence) in savedAssets.Paths)
            if (!SameBytes(evidence.Content, NativeV11PendingSave.ReadFiles(session.SourcePath, [path])[path]))
                throw new BridgeException("AssetSavedEvidenceChanged", "已保存的资源文件发生变化，不能复用原生锁。 ");
        var newNames = names.Except(savedAssets.Owners, StringComparer.Ordinal).ToArray();
        if (newNames.Length != 0)
        {
            var files = new HashSet<string>(StringComparer.Ordinal);
            foreach (var group in newNames.SelectMany(name => plan.Files[name]))
            {
                if (!group.EndsWith('/')) { files.Add(group); continue; }
                var directory = V11Safety.Under(session.SourcePath, group[..^1]);
                if (!Directory.Exists(directory)) continue;
                void Walk(string current)
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new BridgeException("AssetOwnerUnverified", "资源所有者文件组包含链接。 ");
                    foreach (var entry in Directory.EnumerateFileSystemEntries(current))
                    {
                        var attributes = File.GetAttributes(entry);
                        if ((attributes & FileAttributes.ReparsePoint) != 0)
                            throw new BridgeException("AssetOwnerUnverified", "资源所有者文件组包含链接。 ");
                        if ((attributes & FileAttributes.Directory) != 0) Walk(entry);
                        else files.Add(Path.GetRelativePath(session.SourcePath, entry).Replace('\\', '/'));
                    }
                }
                Walk(directory);
            }
            if (git.ChangedFiles!.Keys.Any(path => newNames.Any(name => plan.Files[name].Any(group =>
                group == path || group.EndsWith('/') && path.StartsWith(group, StringComparison.Ordinal))) &&
                !savedAssets.Paths.ContainsKey(path)))
                throw new BridgeException("AssetSavedEvidenceChanged", "新资源所有者含未证明的源文件改动。 ");
            files.ExceptWith(savedAssets.Paths.Keys);
            var baseline = new NativeV11CollaborationRepository.Baseline(session.SourcePath, git, git.Head, [".collaboration"]);
            await NativeV11CollaborationRepository.VerifySharedFilesAtHeadAsync(session.SourcePath, baseline, files);
            if (!SameAssetGit(git, await new GitProbe(session.SourcePath).CaptureAsync()) ||
                savedAssets.SourceRevision != NativeV11SourceRevision.Capture(session.SourcePath))
                throw new BridgeException("AssetSavedEvidenceChanged", "复用资源所有者期间工程或 Git 状态已变化。 ");
            NativeV11Collaboration.VerifyOwned(plan, true);
        }
        return true;
    }

    private async Task<AssetPending?> CaptureAssetBeforeAsync(ModuleMutationRequest request,
        NativeV11Collaboration.Plan? plan, IReadOnlyList<string> affectedPaths)
    {
        if (plan is null || request.Address.Kind != ModuleKind.ResourceFile) return null;
        var paths = affectedPaths.Where(NativeV11AssetRemovalRegistration.IsAssetPath).Distinct(StringComparer.Ordinal).ToArray();
        if (paths.Length != 1) throw new BridgeException("AssetOwnerUnverified", "资源操作必须具有一个精确原生文件路径。 ");
        var path = paths[0];
        bool Includes(string group) => group == path || group.EndsWith('/') && path.StartsWith(group, StringComparison.Ordinal);
        var candidates = plan.Owners.Where(owner => owner.State == "Edit" && plan.Files[
            (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!].Any(Includes)).ToArray();
        if (candidates.Length == 0) throw new BridgeException("AssetOwnerUnverified", "资源文件没有已核实的原生编辑所有者。 ");
        var owners = candidates.Select(candidate => (string)NativeV11Reflection.Get(candidate.SimpleModule, "ModuleName")!).Order(StringComparer.Ordinal).ToArray();
        var roots = candidates.Where(candidate => NativeV11Reflection.Get(candidate.SimpleModule, "ModuleType")?.ToString() is "MapJsonFile" or "CustomLibrary").ToArray();
        var registration = candidates.Length == 1 ? candidates[0] : roots.Length == 1 ? roots[0] : null;
        if (registration is null) throw new BridgeException("AssetOwnerUnverified", "共享资源缺少唯一的原生资源登记所有者。 ");
        var owner = (string)NativeV11Reflection.Get(registration.SimpleModule, "ModuleName")!;
        var before = NativeV11PendingSave.ReadFiles(session.SourcePath, [path])[path];
        var local = NativeV11PendingSave.ReadFiles(session.LocalPath, [path])[path];
        var last = NativeV11PendingSave.ReadFiles(session.LastPath, [path])[path];
        if (!SameBytes(before, local) || !SameBytes(before, last))
            throw new BridgeException("AssetBaselineChanged", "资源源文件与原生缓存或暂存不一致。 ");
        var git = await new GitProbe(session.SourcePath).CaptureAsync();
        var tracked = (await NativeV11AssetRemovalRegistration.TrackedAtHeadAsync(session.SourcePath, [path])).Count == 1;
        if (savedAssets is not null && (!SameAssetGit(savedAssets.Git, git) ||
            savedAssets.SourceRevision != NativeV11SourceRevision.Capture(session.SourcePath)))
            throw new BridgeException("AssetSavedEvidenceChanged", "工程或 Git 状态与本会话保存证明不一致。 ");
        if (savedAssets?.Paths.TryGetValue(path, out var saved) == true)
        {
            if (saved.RegistrationOwner != owner || !saved.Owners.SequenceEqual(owners, StringComparer.Ordinal) || saved.TrackedAtHead != tracked || !SameBytes(saved.Content, before))
                throw new BridgeException("AssetSavedEvidenceChanged", "资源与本会话已保存证据不一致。 ");
        }
        else if (before is not null)
        {
            if (!tracked) throw new BridgeException("AssetBaselineUntracked", "未提交的已有资源没有本会话保存证据。 ");
            var baseline = new NativeV11CollaborationRepository.Baseline(session.SourcePath, git, git.Head, [".collaboration"]);
            await NativeV11CollaborationRepository.VerifySharedFilesAtHeadAsync(session.SourcePath, baseline, [path]);
        }
        else if (tracked)
        {
            if (NativeV11Reflection.Get(plan.Modules[owner]!, "ToRemoveFiles") is not IEnumerable removed ||
                !removed.Cast<object>().OfType<string>().Any(value => value.Replace('\\', '/') == path))
                throw new BridgeException("AssetBaselineUnverified", "HEAD 中存在该资源，但缺少原生删除登记。 ");
        }
        return new(path, owners, owner, tracked, before, null);
    }

    private async Task SaveAssetEvidenceAsync(NativeV11Collaboration.Plan plan, AssetPending pendingAsset)
    {
        var path = pendingAsset.Path;
        var source = NativeV11PendingSave.ReadFiles(session.SourcePath, [path])[path];
        var staged = NativeV11PendingSave.ReadFiles(session.LastPath, [path])[path];
        var local = NativeV11PendingSave.ReadFiles(session.LocalPath, [path])[path];
        if (!SameBytes(source, pendingAsset.Expected) || !SameBytes(source, staged) || !SameBytes(source, local))
            throw new BridgeException("AssetSaveUnconfirmed", "资源源文件与保存前模型、原生缓存或暂存不一致。 ");
        var removed = pendingAsset.Before is not null && source is null && pendingAsset.TrackedAtHead ? new[] { path } : [];
        var registered = (NativeV11Reflection.Get(plan.Modules[pendingAsset.RegistrationOwner]!, "ToRemoveFiles") as IEnumerable)?
            .Cast<object>().OfType<string>().Any(value => value.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase)) == true;
        var restored = source is null || !registered ? Array.Empty<string>() : NativeV11AssetRemovalRegistration.RestoredPaths(plan, pendingAsset.RegistrationOwner, [path]).ToArray();
        if (removed.Length != 0 || restored.Length != 0)
            await NativeV11AssetRemovalRegistration.RegisterAsync(plan, pendingAsset.RegistrationOwner, removed, restored);
        var paths = savedAssets?.Paths.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) ?? new(StringComparer.Ordinal);
        paths[path] = new(pendingAsset.Owners, pendingAsset.RegistrationOwner, pendingAsset.TrackedAtHead, source);
        var names = MergeAssetOwners(savedAssets?.Owners ?? [], plan.Owners.Select(owner => (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!));
        var git = await new GitProbe(session.SourcePath).CaptureAsync();
        savedAssets = new(git, NativeV11SourceRevision.Capture(session.SourcePath), names, paths);
    }

    private async Task SaveCustomLibraryEvidenceAsync(NativeV11Collaboration.Plan plan)
    {
        const string path = NativeV11LocalLibraries.DefinitionPath;
        var source = NativeV11PendingSave.ReadFiles(session.SourcePath, [path])[path];
        var local = NativeV11PendingSave.ReadFiles(session.LocalPath, [path])[path];
        var last = NativeV11PendingSave.ReadFiles(session.LastPath, [path])[path];
        if (source is null || !SameBytes(source, local) || !SameBytes(source, last))
            throw new BridgeException("AssetSaveUnconfirmed", "自定义库定义的源、缓存和暂存不一致。 ");
        NativeV11Collaboration.VerifyOwned(plan, true);
        var names = MergeAssetOwners(savedAssets?.Owners ?? [], plan.Owners.Select(owner =>
            (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!));
        var git = await new GitProbe(session.SourcePath).CaptureAsync();
        savedAssets = new(git, NativeV11SourceRevision.Capture(session.SourcePath), names,
            savedAssets?.Paths ?? new(StringComparer.Ordinal));
    }
}
