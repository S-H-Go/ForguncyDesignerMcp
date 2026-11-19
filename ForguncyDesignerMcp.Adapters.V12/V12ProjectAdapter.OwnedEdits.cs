using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter
{
    private NativeOwnedEditReuse? ownedEdits;

    // Capture only after the native save and all source projections succeeded.
    // Missing evidence disables the optimization, never changes the save receipt.
    private async Task RememberOwnedEditsAsync(IEnumerable<NativeModuleFileGroup> groups)
    {
        var previous = ownedEdits;
        ownedEdits = null;
        try
        {
            var state = guard.Value.Capture();
            if (!state.InCollaborationMode || state.CurrentUser is null || state.Busy || state.HasUnsavedChanges) return;
            var source = NativeProjectGuard.SourceRevision(state.Target.ProjectPath);
            var git = NativeCollaborationGit.GuardFingerprint(state.Target.ProjectPath);
            var status = await collaboration.Value.CaptureStatusAsync();
            if (!CompleteRegistryMatches() || guard.Value.Capture().Revision != state.Revision ||
                NativeProjectGuard.SourceRevision(state.Target.ProjectPath) != source ||
                NativeCollaborationGit.GuardFingerprint(state.Target.ProjectPath) != git) return;
            var prior = previous?.SavedFor(state) ?? [];
            var fresh = ReduceFileGroups(groups).ToArray();
            // A later save of the same native model must retain old paths from
            // prior moves and renames, including their source tombstones.
            var carried = fresh.Select(item => prior.SingleOrDefault(old => ReferenceEquals(old.NativeIdentity, item.NativeIdentity)) is { } old
                ? MergeFileGroups(old, item) : item)
                .Concat(prior.Where(old => !fresh.Any(item => ReferenceEquals(item.NativeIdentity, old.NativeIdentity))));
            var candidates = ReduceFileGroups(carried).Where(group => group.IsPresent() && targetFiles.Value.Verify(group)).ToArray();
            if (candidates.Length == 0 || candidates.GroupBy(x => x.NativeModuleName, StringComparer.Ordinal).Any(x => x.Count() != 1)) return;
            foreach (var group in candidates) guard.Value.RequireOwnedModule(state, group.NativeModuleName);
            var registryProven = await NativeOwnedRegistryProof.VerifyAsync(state.Target.ProjectPath, status.Git, candidates, CancellationToken.None);
            if (!registryProven || !CompleteRegistryMatches() || guard.Value.Capture().Revision != state.Revision ||
                NativeProjectGuard.SourceRevision(state.Target.ProjectPath) != source || NativeCollaborationGit.GuardFingerprint(state.Target.ProjectPath) != git) return;
            var journal = new NativeOwnedEditReuse(state.Target, state.CurrentUser, source, git, candidates);
            if (!journal.CanReuse(state, status, new NativeModuleFilePlan(candidates, []), source, git,
                name => IsOwned(state, name), targetFiles.Value.Verify, registryProven)) return;
            ownedEdits = journal;
        }
        catch (Exception error) when (error is not OperationCanceledException) { ownedEdits = null; }
    }

    private bool IsOwned(ProjectSnapshot state, string name)
    {
        try { guard.Value.RequireOwnedModule(state, name); return true; }
        catch (BridgeException) { return false; }
    }

    private async Task<bool> CanReuseOwnedEditsAsync(NativeModuleFilePlan plan, CancellationToken token)
    {
        if (ownedEdits is null) return false;
        var state = guard.Value.Capture();
        var source = NativeProjectGuard.SourceRevision(state.Target.ProjectPath);
        var git = NativeCollaborationGit.GuardFingerprint(state.Target.ProjectPath);
        var status = await collaboration.Value.CaptureStatusAsync(token);
        var selected = plan.BeforeGroups.Select(group => ownedEdits.Saved.SingleOrDefault(saved =>
            saved.NativeModuleName == group.NativeModuleName && ReferenceEquals(saved.NativeIdentity, group.NativeIdentity))).ToArray();
        if (selected.Any(group => group is null)) return false;
        var registryProven = await NativeOwnedRegistryProof.VerifyAsync(state.Target.ProjectPath, status.Git, ownedEdits.Saved, token);
        return registryProven && CompleteRegistryMatches() && guard.Value.Capture().Revision == state.Revision &&
            NativeProjectGuard.SourceRevision(state.Target.ProjectPath) == source &&
            NativeCollaborationGit.GuardFingerprint(state.Target.ProjectPath) == git &&
            ownedEdits.CanReuse(state, status, plan, source, git, name => IsOwned(state, name), targetFiles.Value.Verify, registryProven);
    }

    // Require exact agreement, including unlocked registrations and duplicate names.
    private bool CompleteRegistryMatches()
    {
        var type = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        var manager = type.GetField("_instance", NativeReflection.Flags)!.GetValue(null)!;
        var path = (string)NativeReflection.Get(manager, "ModuleFilePath")!;
        if (!string.Equals(Path.GetFullPath(path), Path.Combine(Path.GetFullPath(context.CaptureSnapshot().Target.ProjectPath), ".collaboration"), StringComparison.OrdinalIgnoreCase)) return false;
        if (NativeReflection.Get(manager, "Modules") is not IDictionary registry) return false;
        var memory = registry.Values.Cast<object>().ToArray();
        var persisted = NativeCollaboration.ReadPersistedModules(path);
        string Name(object item) => (string)NativeReflection.Get(item, "ModuleName")!;
        var json = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
        string Key(object item) => (string)NativeReflection.Static(json, "SerializeObject", item)!;
        return memory.Length == persisted.Length && memory.Select(Name).Distinct(StringComparer.Ordinal).Count() == memory.Length &&
            persisted.Select(Name).Distinct(StringComparer.Ordinal).Count() == persisted.Length &&
            memory.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(persisted.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }
}
