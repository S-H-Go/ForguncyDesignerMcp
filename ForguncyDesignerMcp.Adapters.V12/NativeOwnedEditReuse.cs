using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Per-adapter evidence only. A successful source save is necessary but never
// sufficient: every later reuse rechecks the complete selected native groups,
// actual SDK ownership, persisted registry, Git and the entire source baseline.
internal sealed class NativeOwnedEditReuse
{
    private readonly ProjectTarget target;
    private readonly UserIdentity user;
    private readonly string sourceRevision;
    private readonly string gitFingerprint;
    private readonly NativeModuleFileGroup[] saved;

    internal NativeOwnedEditReuse(ProjectTarget target, UserIdentity user, string sourceRevision, string gitFingerprint, IEnumerable<NativeModuleFileGroup> saved)
    { this.target = target; this.user = user; this.sourceRevision = sourceRevision; this.gitFingerprint = gitFingerprint; this.saved = saved.ToArray(); }

    internal IReadOnlyList<NativeModuleFileGroup> Saved => saved;

    internal IReadOnlyList<NativeModuleFileGroup> SavedFor(ProjectSnapshot snapshot) =>
        snapshot.Target == target && snapshot.CurrentUser == user ? saved : [];

    internal bool CanReuse(ProjectSnapshot snapshot, CollaborationStatus status, NativeModuleFilePlan plan, string source, string git,
        Func<string, bool> nativeOwned, Func<NativeModuleFileGroup, bool> verify, bool registryChangeProven = false)
    {
        if (string.IsNullOrWhiteSpace(user.UserName) || string.IsNullOrWhiteSpace(user.Email) || !snapshot.InCollaborationMode ||
            snapshot.Target != target || snapshot.CurrentUser != user || snapshot.Busy || snapshot.HasUnsavedChanges ||
            status.Target != target || status.CurrentUser != user || !status.InCollaborationMode || status.Revision != snapshot.Revision ||
            source != sourceRevision || git != gitFingerprint ||
            plan.BeforeGroups.Count == 0 || plan.ExpectedNewModuleNames.Count != 0 || status.Git is not { IndexClean: true, ChangedFiles: not null } repository ||
            repository.RemoteName is null || repository.UpstreamHead is null || repository.Head != repository.UpstreamHead)
            return false;
        var selected = plan.BeforeGroups.Select(group => group.NativeModuleName).ToHashSet(StringComparer.Ordinal);
        if (selected.Count != plan.BeforeGroups.Count || saved.Select(group => group.NativeModuleName).Distinct(StringComparer.Ordinal).Count() != saved.Length) return false;
        foreach (var group in saved)
        {
            var owner = status.Modules.Where(item => item.NativeModuleName == group.NativeModuleName).ToArray();
            if (owner.Length != 1 || owner[0].NativeState is not (1 or 3 or 4) || owner[0].PendingDeletion ||
                !owner[0].OwnedByCurrentUser || owner[0].LockedBy != user || !nativeOwned(group.NativeModuleName) || !verify(group)) return false;
        }
        foreach (var current in plan.BeforeGroups)
        {
            var evidence = saved.Where(item => item.NativeModuleName == current.NativeModuleName && ReferenceEquals(item.NativeIdentity, current.NativeIdentity)).ToArray();
            if (evidence.Length != 1 ||
                current.Paths.Any(path => !evidence[0].Paths.Contains(path, StringComparer.Ordinal)) ||
                current.RequiredDirectories.Any(path => !evidence[0].RequiredDirectories.Contains(path, StringComparer.Ordinal)) ||
                current.AbsentDirectories.Any(path => !evidence[0].AbsentDirectories.Contains(path, StringComparer.Ordinal)) ||
                !verify(current)) return false;
        }
        var allowed = saved.SelectMany(group => group.Paths).ToHashSet(StringComparer.Ordinal);
        return repository.ChangedFiles.Keys.All(path => path == ".collaboration" ? registryChangeProven :
            !path.Equals(".collaboration", StringComparison.OrdinalIgnoreCase) && allowed.Contains(path));
    }
}
