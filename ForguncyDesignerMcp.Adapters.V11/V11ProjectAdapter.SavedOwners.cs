using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter
{
    // This witness is created only after the native save and pending model/source
    // readback succeeded. It belongs to this adapter instance and source tree.
    internal sealed record SavedOwnerEvidence(GitState Git, string SourceRevision, string[] Owners,
        IReadOnlyDictionary<string, byte[]?> Files);
    private SavedOwnerEvidence? savedOwners;

    private static bool SupportsSavedOwnerReuse(ModuleKind kind) => kind is ModuleKind.Page or ModuleKind.Report or
        ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.Image;

    internal static bool SavedOwnerEvidenceMatches(SavedOwnerEvidence witness, GitState git, string sourceRevision,
        IEnumerable<string> ownerNames, IReadOnlyDictionary<string, byte[]?> files)
    {
        var requested = ownerNames.ToArray();
        return requested.Length != 0 && requested.Distinct(StringComparer.Ordinal).Count() == requested.Length &&
            requested.All(name => witness.Owners.Contains(name, StringComparer.Ordinal)) &&
            SameAssetGit(witness.Git, git) && witness.SourceRevision == sourceRevision &&
            witness.Files.Count == files.Count && witness.Files.All(pair => files.TryGetValue(pair.Key, out var actual) && SameBytes(pair.Value, actual));
    }

    private bool ReuseSavedOwners(NativeV11Collaboration.Plan plan, GitState git)
    {
        if (savedOwners is null || !NativeV11Collaboration.CanReuseEditStates(plan)) return false;
        var names = plan.Owners.Select(owner => (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!).ToArray();
        var current = NativeV11PendingSave.ReadFiles(session.SourcePath, savedOwners.Files.Keys);
        if (!SavedOwnerEvidenceMatches(savedOwners, git, NativeV11SourceRevision.Capture(session.SourcePath), names, current)) return false;
        NativeV11Collaboration.VerifyOwned(plan, true);
        return true;
    }

    private async Task SaveOwnerEvidenceAsync(NativeV11PendingSave item, NativeV11Collaboration.Plan plan)
    {
        if (!SupportsSavedOwnerReuse(item.Address.Kind) || item.Operation == ModuleOperation.Delete ||
            plan.Owners.Any(owner => owner.State is not ("Edit" or "New"))) return;
        NativeV11Collaboration.VerifyOwned(plan, true, requireRegisteredModel: true);
        var revision = NativeV11SourceRevision.Capture(session.SourcePath);
        var prior = savedOwners is not null && savedOwners.SourceRevision == item.ProjectSourceRevision ? savedOwners : null;
        var paths = (prior?.Files.Keys ?? Enumerable.Empty<string>()).Concat(item.Paths).Distinct(StringComparer.Ordinal).ToArray();
        var files = NativeV11PendingSave.ReadFiles(session.SourcePath, paths);
        var git = await new GitProbe(session.SourcePath).CaptureAsync();
        if (prior is not null && (prior.Git.Head != git.Head || prior.Git.Branch != git.Branch || prior.Git.RemoteUrl != git.RemoteUrl))
            throw new BridgeException("SavedOwnerEvidenceChanged", "保存前已保存所有者的 Git 绑定发生变化。 ");
        if (revision != NativeV11SourceRevision.Capture(session.SourcePath) ||
            !SameAssetGit(git, await new GitProbe(session.SourcePath).CaptureAsync()))
            throw new BridgeException("SavedOwnerEvidenceChanged", "保存后的工程或 Git 状态发生变化，无法建立连续编辑证明。 ");
        var names = MergeAssetOwners(prior?.Owners ?? [], plan.Owners.Select(owner =>
            (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!));
        savedOwners = new(git, revision, names, files);
    }
}
