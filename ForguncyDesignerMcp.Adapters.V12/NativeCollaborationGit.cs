using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// This class only observes Git. Native CollaborationManager owns all checkout,
// save, index, commit, pull and push mutations.
public sealed class NativeCollaborationGit
{
    private const int MaximumOutput = 32 * 1024 * 1024;
    private readonly string root;
    private readonly string gitDirectory;
    private string[] filterOverrides = [];

    public NativeCollaborationGit(string projectPath)
    {
        root = Path.GetFullPath(projectPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root)) throw Error("GitRepositoryUnavailable", "The bound collaboration source must be an existing directory.");
        gitDirectory = Path.Combine(root, ".git");
        // Native collaboration uses a normal repository. Linked worktrees and
        // indirection files need their own native ownership validation.
        if (!Directory.Exists(gitDirectory)) throw Error("GitRepositoryUnavailable", "A normal .git directory at the exact bound project root is required.");
        if (File.Exists(Path.Combine(gitDirectory, "commondir"))) throw Error("GitRepositoryUnavailable", "Shared Git directory indirection has no native collaboration ownership adapter.");
        RequireDirectoryChain(root); RequireNoLinks(gitDirectory);
    }

    // Local-only revision ingredient for NativeProjectGuard. Reads raw metadata
    // into a digest; neither Git processes nor config values escape this method.
    public static string GuardFingerprint(string projectPath)
    {
        var project = Path.GetFullPath(projectPath).TrimEnd('\\', '/'); RequireDirectoryChain(project);
        var metadata = Path.Combine(project, ".git");
        if (!Directory.Exists(metadata)) throw Error("GitRepositoryUnavailable", "A normal bound .git directory is required for the collaboration revision.");
        if (File.Exists(Path.Combine(metadata, "commondir"))) throw Error("GitRepositoryUnavailable", "Shared Git directory indirection cannot bind the local collaboration revision.");
        RequireNoLinks(metadata);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Append(string value) => digest.AppendData(Encoding.UTF8.GetBytes(value));
        void Add(string file, string name)
        {
            Append(name); Append("\0");
            if (!File.Exists(file)) { Append("missing\0"); return; }
            RequireNoLinks(file); using var stream = File.OpenRead(file); digest.AppendData(SHA256.HashData(stream)); Append("\0");
        }
        foreach (var name in new[] { "HEAD", "config", "index", "packed-refs", "commondir", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "shallow", "index.lock", "HEAD.lock" })
            Add(Path.Combine(metadata, name), name);
        void Refs(string folder, string logical)
        {
            Append(logical); Append("\0");
            if (!Directory.Exists(folder)) { Append("missing\0"); return; }
            RequireDirectoryChain(folder);
            foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal)) Add(file, logical + "/" + Path.GetFileName(file));
            foreach (var child in Directory.EnumerateDirectories(folder).Order(StringComparer.Ordinal)) Refs(child, logical + "/" + Path.GetFileName(child));
        }
        Refs(Path.Combine(metadata, "refs", "heads"), "refs/heads"); Refs(Path.Combine(metadata, "refs", "remotes"), "refs/remotes");
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    public async Task<GitState> CaptureAsync(CancellationToken cancellationToken = default) =>
        (await CaptureDetailed(cancellationToken)).Git;

    internal async Task<IReadOnlyList<string>> TrackedFilesAtHeadAsync(IEnumerable<string> candidates,CancellationToken token=default)
    {
        var paths=candidates.Distinct(StringComparer.Ordinal).ToArray();
        foreach(var path in paths)_=NativeSaveVerification.Under(root,path);
        var head=(await Run(token,"rev-parse","--verify","HEAD")).Trim();
        if(!ObjectId(head))throw Error("GitHeadUnavailable","The native library source has no reviewed HEAD.");
        var tree=await Tree(head,token);var result=new List<string>();
        foreach(var path in paths)
        {
            var aliases=tree.Keys.Where(key=>key.Equals(path,StringComparison.OrdinalIgnoreCase)).ToArray();
            if(aliases.Length>1 || aliases.Length==1 && aliases[0]!=path)
                throw Error("GitFileCaseAlias","A native library removal has an ambiguous tracked path.");
            if(aliases.Length==0)continue;
            if(tree[path].Mode is not ("100644" or "100755"))throw Error("GitFileTypeUnsupported","Only tracked regular native library files may enter the removal list.");
            result.Add(path);
        }
        if((await Run(token,"rev-parse","--verify","HEAD")).Trim()!=head)
            throw Error("GitBaselineChanged","The native library HEAD changed while identifying saved removals.");
        return result;
    }

    public Task<NativeCollaborationGitBaseline> PrepareCheckinAsync(CollaborationMutationRequest request, UserIdentity nativeActor,
        IReadOnlyCollection<string> allowedFiles, IReadOnlyList<CollaborationModuleState> currentModules,
        CancellationToken cancellationToken = default) => Prepare(request, nativeActor, allowedFiles, currentModules, false, cancellationToken, null, null);

    public Task<NativeCollaborationGitBaseline> PrepareCheckoutAsync(CollaborationMutationRequest request, UserIdentity nativeActor,
        IReadOnlyList<CollaborationModuleState> currentModules, CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string,IReadOnlyList<string>>? cancelledNewFiles = null, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancelledNewDirectories = null) =>
        Prepare(request, nativeActor, [], currentModules, true, cancellationToken, cancelledNewFiles, cancelledNewDirectories);

    public async Task RequireCurrentAsync(NativeCollaborationGitBaseline baseline, CancellationToken cancellationToken = default)
    {
        RequireOwner(baseline);
        var now = await CaptureDetailed(cancellationToken);
        if (Fingerprint(now) != baseline.Fingerprint)
            throw Error("GitBaselineChanged", "Repository, branch, remote, index or source files changed before native dispatch.");
        if (now.Git.UpstreamHead != baseline.Git.UpstreamHead)
            throw Error("RemoteBaselineChanged", "The reviewed remote head is no longer current.");
    }

    // Call even when the native operation returned false or threw after dispatch.
    // A local commit or remote push may already have happened.
    public async Task<NativeCollaborationGitVerification> VerifyAfterNativeAsync(NativeCollaborationGitBaseline baseline,
        IReadOnlyList<CollaborationModuleState> currentModules, CancellationToken cancellationToken = default)
    {
        RequireOwner(baseline);
        GitState? after = null; string? commit = null; bool? remote = null;
        var localVerified = false; var sourceVerified = false; var locksVerified = false;
        IReadOnlyList<string> committed = [];
        try
        {
            var current = await CaptureDetailed(cancellationToken); after = current.Git;
            commit = after.Head == baseline.Git.Head ? null : after.Head;
            remote = after.UpstreamHead is null ? null : after.UpstreamHead == after.Head;
            if (after.Branch != baseline.Git.Branch || after.RemoteName != baseline.Git.RemoteName || after.RemoteUrl != baseline.Git.RemoteUrl ||
                current.RemoteRef != baseline.RemoteRef || after.Upstream != baseline.Git.Upstream)
                return Result("GitDestinationChanged", "The native operation changed its repository destination.");
            var unchangedOther = baseline.Local.Changes.Where(x => !baseline.AllowedSet.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal)
                .SequenceEqual(current.Changes.Where(x => !baseline.AllowedSet.Contains(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal));
            if (!unchangedOther) return Result("OtherWorktreeFilesChanged", "Files outside the approved native module set changed during collaboration.");
            if (!after.IndexClean || current.Changes.Keys.Any(baseline.AllowedSet.Contains))
                return Result("CollaborationFilesStillDirty", "The native index or approved module source files still contain uncommitted changes.");
            var reuse = baseline.Checkout && baseline.CancelledNewNames.Count==0 && baseline.SelectedBefore.All(x => x.OwnedByCurrentUser);
            if (commit is null && !reuse) return Result("NativeCommitMissing", "No new native collaboration commit was observed.");
            if (commit is not null)
            {
                var parents = Words(await Run(cancellationToken, "rev-list", "--parents", "-n", "1", after.Head));
                if (parents.Length != 2 || parents[1] != baseline.Git.Head)
                    return Result("NativeCommitLineageChanged", "The observed head is not one direct native commit above the reviewed head.");
                committed = SplitPaths(await Run(cancellationToken, "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", baseline.Git.Head, after.Head, "--"));
                if (committed.Count == 0 || !committed.Contains(".collaboration", StringComparer.Ordinal) || committed.Any(path => !baseline.AllowedSet.Contains(path)))
                    return Result("NativeCommitScopeChanged", "The native commit omitted collaboration registration or included files outside the approved module set.");
                if (after.Author != baseline.CommitIdentity || after.Committer != baseline.CommitIdentity)
                    return Result("NativeCommitIdentityChanged", "The native commit author or committer differs from the pinned identity for this collaboration operation.");
                if (!await CommitMessageMatches(baseline, after.Head, cancellationToken))
                    return Result("NativeCommitMessageUnverified", "The native commit message does not identify the reviewed checkout or exact check-in module set.");
                localVerified = true;
            }
            else localVerified = true; // Reuse only: current owned locks still need source and remote proof below.

            var records = ReadLocks();
            locksVerified = VerifyLocks(baseline, currentModules, records);
            if (!locksVerified) return Result("NativeLocksUnverified", "Selected locks or unrelated collaboration records did not match the required native transition.");
            var tree = await Tree(after.Head, cancellationToken);
            foreach (var expected in baseline.ExpectedFiles)
            {
                if (expected.Key == ".collaboration") continue;
                if (!tree.TryGetValue(expected.Key, out var entry))
                { if (expected.Value.ObjectId is not null) return Result("NativeSourceMissing", "An approved saved module file is absent from the native commit tree."); }
                else if (expected.Value.ObjectId is null || entry.ObjectId != expected.Value.ObjectId || entry.Mode is not ("100644" or "100755"))
                    return Result("NativeSourceMismatch", "An approved source file differs from the saved module baseline in the native commit tree.");
                if (FileHash(expected.Key) != expected.Value.RawHash)
                    return Result("NativeSourceChanged", "An approved module file changed after the saved source baseline.");
            }
            if (!tree.TryGetValue(".collaboration", out var lockEntry) || lockEntry.Mode != "100644" ||
                lockEntry.ObjectId != await BlobId(".collaboration", cancellationToken))
                return Result("NativeLockSourceMismatch", "Saved lock registration does not match the native commit tree.");
            sourceVerified = true;
            return Result(remote == true ? "NativeCollaborationVerified" : remote == false ? "RemoteNotSynchronized" : "RemoteOutcomeUnknown",
                remote == true ? commit is null ? "Existing owned lock registration, source tree and fresh remote tip were verified; no new commit was created." :
                "Native commit, exact source tree, selected locks and fresh remote tip were verified." :
                "Local native commit, source and lock evidence were verified; remote synchronization is not confirmed.");
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return Result(error is BridgeException bridge ? bridge.Code : "CollaborationGitVerificationFailed",
                "Collaboration evidence could not be completed; do not replay the native operation.");
        }
        NativeCollaborationGitVerification Result(string code, string message) =>
            new(after, localVerified, sourceVerified, locksVerified, commit, remote, committed, code, message);
    }

    private async Task<NativeCollaborationGitBaseline> Prepare(CollaborationMutationRequest request, UserIdentity nativeActor, IReadOnlyCollection<string> files,
        IReadOnlyList<CollaborationModuleState> modules, bool checkout, CancellationToken token, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancelledNewFiles, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancelledNewDirectories)
    {
        if (!SamePath(request.Target.ProjectPath, root)) throw Error("GitProjectMismatch", "Git inspection is bound to another project root.");
        if (nativeActor is null || string.IsNullOrWhiteSpace(nativeActor.UserName) || string.IsNullOrWhiteSpace(nativeActor.Email) || request.ExpectedUser != nativeActor)
            throw Error("CollaborationIdentityMismatch", "A complete native collaboration identity must match the reviewed identity.");
        if (!checkout && (string.IsNullOrWhiteSpace(request.ExpectedRemoteUrl) || string.IsNullOrWhiteSpace(request.ExpectedBranch) || !ObjectId(request.ExpectedRemoteHead)))
            throw Error("RemoteBaselineRequired", "The exact reviewed remote URL, branch and remote head are required before native collaboration.");
        var commitIdentity=checkout ? NativeCollaboration.NativeCheckoutCommitIdentity() : nativeActor;
        var local = await CaptureDetailed(token); var git = local.Git;
        if (request.ExpectedRemoteUrl is not null && git.RemoteUrl != request.ExpectedRemoteUrl || request.ExpectedBranch is not null && git.Branch != request.ExpectedBranch ||
            request.ExpectedRemoteHead is not null && git.UpstreamHead != request.ExpectedRemoteHead)
            throw Error("RemoteBaselineChanged", "The current Git destination or fresh remote head differs from the reviewed baseline.");
        if (git.RemoteName is null || git.UpstreamHead is null || local.RemoteRef != "refs/heads/" + git.Branch || git.UpstreamHead != git.Head)
            throw Error("RemoteHeadNotAligned", "Native collaboration requires the exact configured remote branch to equal the reviewed local head.");
        var allowed = files.Append(".collaboration").Select(Relative).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (!checkout && allowed.Length == 1) throw Error("NativeModuleFilesRequired", "Check-in needs the exact native source and companion file group.");
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        if (local.Staged.Any(path => !allowedSet.Contains(path)))
            throw Error("UnrelatedStagedChanges", "The native commit would include unrelated staged files; the index was left unchanged.");
        if (request.Modules is null || request.Modules.Count == 0 || request.Modules.Distinct().Count() != request.Modules.Count)
            throw Error("CollaborationModulesRequired", "A unique nonempty native module set is required.");
        var selected = request.Modules.Select(address =>
        {
            var found = modules.Where(x => x.Address == address).ToArray();
            if (found.Length != 1) throw Error("NativeModuleAmbiguous", "A requested address does not resolve to exactly one registered native module.");
            var value = found[0];
            if (value.PendingDeletion != (value.NativeState == 2))
                throw Error("NativeDeletionStateMismatch", "Native removal state and its semantic pending-deletion flag disagree.");
            if (!checkout && (!value.OwnedByCurrentUser || value.LockedBy != nativeActor || value.NativeState is not (1 or 2 or 3 or 4)))
                throw Error("NativeOwnedLockRequired", "Check-in requires the selected native module lock to belong to the reviewed user.");
            if (checkout && value.LockedBy is not null && (!value.OwnedByCurrentUser || value.LockedBy != nativeActor))
                throw Error("NativeOtherUserLock", "A selected native module is locked by another user.");
            return value;
        }).ToArray();
        if (selected.Select(x => x.NativeModuleName).Distinct(StringComparer.Ordinal).Count() != selected.Length)
            throw Error("NativeModuleAmbiguous", "Requested addresses alias the same native collaboration module.");
        var cancelled=cancelledNewFiles ?? new Dictionary<string,IReadOnlyList<string>>();
        var directories=cancelledNewDirectories ?? new Dictionary<string,IReadOnlyList<string>>();
        if(directories.Keys.Any(name=>!cancelled.ContainsKey(name) || selected.Count(x=>x.NativeModuleName==name && x.Address.Kind==ModuleKind.Folder)!=1))
            throw Error("NativeNewRemovalPlanInvalid","Directory cancellation proof is limited to selected folder owners.");
        if(!checkout && cancelled.Count!=0 || cancelled.Keys.Any(name=>selected.Count(x=>x.NativeModuleName==name && x.NativeState==3 && x.OwnedByCurrentUser && x.LockedBy==nativeActor)!=1))
            throw Error("NativeNewRemovalPlanInvalid","Cancellation is limited to selected, current-user native New owners.");
        if(cancelled.Count>0)
        {
            var committedTree=await Tree(git.Head,token);
            var committedPaths=committedTree.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach(var entry in cancelled)
            {
                var paths=entry.Value.Select(Relative).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var isFolder=selected.Single(x=>x.NativeModuleName==entry.Key).Address.Kind==ModuleKind.Folder;
                var dirs=directories.TryGetValue(entry.Key,out var suppliedDirs) ? suppliedDirs.Select(Relative).ToArray() : [];
                if((isFolder ? paths.Length!=0 || dirs.Length!=1 : paths.Length==0 || dirs.Length!=0) ||
                    paths.Length!=entry.Value.Count || paths.Contains(".collaboration",StringComparer.OrdinalIgnoreCase) || paths.Any(committedPaths.Contains) ||
                    dirs.Any(dir=>dir.Equals(".collaboration",StringComparison.OrdinalIgnoreCase) || committedPaths.Any(path=>path.Equals(dir,StringComparison.OrdinalIgnoreCase) || path.StartsWith(dir+"/",StringComparison.OrdinalIgnoreCase) || dir.StartsWith(path+"/",StringComparison.OrdinalIgnoreCase))))
                    throw Error("NativeNewRemovalSourceConflict","Cancellation requires the complete new source group to be absent from the committed baseline.");
            }
        }
        var records = ReadLocks();
        foreach (var module in selected)
        {
            if (!records.TryGetValue(module.NativeModuleName, out var record) && !(checkout && module.NativeState is null && !module.OwnedByCurrentUser && module.LockedBy is null))
                throw Error("NativeRegistrationMissing", "A selected native module has no source registration or genuine new-module checkout placeholder.");
            if (record is not null && (module.NativeState is null || (record["State"]?.GetValue<int>() ?? 0) != module.NativeState ||
                (record["LockedBy"] is null ? null : new UserIdentity(record["LockedBy"]!["UserName"]!.GetValue<string>(), record["LockedBy"]!["Email"]!.GetValue<string>())) != module.LockedBy))
                throw Error("NativeRegistrationMismatch", "A selected source registration differs from the current native module state or owner.");
        }
        var committedLocks = ParseLocks(await Run(token, "show", git.Head + ":.collaboration"));
        var selectedNames = selected.Select(x => x.NativeModuleName).ToHashSet(StringComparer.Ordinal);
        if (!SameOtherLocks(committedLocks, records, selectedNames))
            throw Error("UnrelatedLockSourceChanges", "Uncommitted collaboration registration changes include modules outside the approved selection.");
        var expected = new Dictionary<string, NativeCollaborationGitFile>(StringComparer.Ordinal);
        foreach (var path in allowed)
        {
            var hash = FileHash(path);
            expected[path] = new(hash, hash is null ? null : await BlobId(path, token));
        }
        // Git/native source reads must still refer to exactly the same baseline.
        var end = await CaptureDetailed(token);
        if (Fingerprint(local) != Fingerprint(end) || local.Git.UpstreamHead != end.Git.UpstreamHead)
            throw Error("GitBaselineChanged", "Git or source state changed while preparing native collaboration.");
        return new(this, git, checkout, allowed, selected, modules.ToArray(), nativeActor, local, expected, records, Fingerprint(local),cancelled.Keys.ToHashSet(StringComparer.Ordinal),commitIdentity);
    }

    private bool VerifyLocks(NativeCollaborationGitBaseline baseline, IReadOnlyList<CollaborationModuleState> modules,
        Dictionary<string, JsonNode> records)
    {
        var names = baseline.SelectedModuleNames.ToHashSet(StringComparer.Ordinal);
        if (!SameOtherLocks(baseline.LockRecords, records, names)) return false;
        var oldOthers = baseline.ModulesBefore.Where(x => !names.Contains(x.NativeModuleName)).OrderBy(x => x.NativeModuleName, StringComparer.Ordinal);
        var newOthers = modules.Where(x => !names.Contains(x.NativeModuleName)).OrderBy(x => x.NativeModuleName, StringComparer.Ordinal);
        if (!oldOthers.SequenceEqual(newOthers)) return false;
        foreach (var before in baseline.SelectedBefore)
        {
            var after = modules.Where(x => x.NativeModuleName == before.NativeModuleName).ToArray();
            if ((!baseline.Checkout && before.PendingDeletion) || (baseline.Checkout && baseline.CancelledNewNames.Contains(before.NativeModuleName)))
            { if (after.Length != 0 || records.ContainsKey(before.NativeModuleName)) return false; continue; }
            if (after.Length != 1 || after[0].Address != before.Address) return false;
            var value = after[0];
            if (value.PendingDeletion != (value.NativeState == 2)) return false;
            if (baseline.Checkout ? !value.OwnedByCurrentUser || value.LockedBy != baseline.User || value.NativeState is not (1 or 2 or 3 or 4) :
                value.OwnedByCurrentUser || value.LockedBy is not null || value.NativeState is not (null or 0) || value.PendingDeletion) return false;
            if (!records.TryGetValue(before.NativeModuleName, out var record)) return false;
            if (baseline.LockRecords.TryGetValue(before.NativeModuleName, out var previous))
            { if (!JsonNode.DeepEquals(StableLock(previous), StableLock(record))) return false; }
            else if (!baseline.Checkout || before.NativeState is not null || value.NativeState != 3) return false;
            if (baseline.Checkout)
            {
                if (record["State"]?.GetValue<int>() != value.NativeState || record["LockedBy"]?["UserName"]?.GetValue<string>() != baseline.User.UserName ||
                    record["LockedBy"]?["Email"]?.GetValue<string>() != baseline.User.Email) return false;
            }
            else if (record["State"] is { } state && state.GetValue<int>() != 0 || record["LockedBy"] is not null) return false;
        }
        return true;
    }

    private static JsonNode StableLock(JsonNode node)
    {
        var copy = node.DeepClone().AsObject();
        foreach (var field in new[] { "State", "LockedBy", "LockDateTime", "ToRemoveFiles" }) copy.Remove(field);
        return copy;
    }

    private Dictionary<string, JsonNode> ReadLocks()
    {
        var file = Under(".collaboration");
        if (!File.Exists(file) || new FileInfo(file).Length > MaximumOutput) throw Error("NativeLockSourceUnavailable", "The native collaboration source registration is unavailable or too large.");
        return ParseLocks(File.ReadAllText(file));
    }
    private static bool SameOtherLocks(Dictionary<string, JsonNode> first, Dictionary<string, JsonNode> second, HashSet<string> excluded)
    {
        var a = first.Where(x => !excluded.Contains(x.Key)).ToArray(); var b = second.Where(x => !excluded.Contains(x.Key)).ToArray();
        return a.Length == b.Length && a.All(entry => second.TryGetValue(entry.Key, out var other) && JsonNode.DeepEquals(entry.Value, other));
    }
    private static Dictionary<string, JsonNode> ParseLocks(string text)
    {
        var node = JsonNode.Parse(text.TrimStart('\uFEFF'));
        var list = node as JsonArray ?? node?["$values"] as JsonArray ?? throw Error("NativeLockSourceInvalid", "Native collaboration registration must be an array.");
        var result = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var entry in list)
        {
            if (entry is not JsonObject obj || obj["ModuleName"] is not JsonValue name || !name.TryGetValue<string>(out var key) || string.IsNullOrWhiteSpace(key) || !result.TryAdd(key, obj))
                throw Error("NativeLockSourceInvalid", "Native collaboration registration has missing or ambiguous module names.");
        }
        return result;
    }

    private async Task<bool> CommitMessageMatches(NativeCollaborationGitBaseline baseline, string head, CancellationToken token)
    {
        try
        {
            using var message = JsonDocument.Parse(await Run(token, "show", "-s", "--format=%B", head)); var value = message.RootElement;
            if (!value.TryGetProperty("MessageType", out var type) || type.GetInt32() != (baseline.Checkout ? 1 : 2)) return false;
            if (baseline.Checkout) return !value.TryGetProperty("Modules", out var empty) || empty.ValueKind == JsonValueKind.Array && empty.GetArrayLength() == 0;
            if (!value.TryGetProperty("Modules", out var modules) || modules.ValueKind != JsonValueKind.Array) return false;
            var entries = modules.EnumerateArray().ToArray();
            return entries.Length == baseline.SelectedBefore.Count && baseline.SelectedBefore.All(expected => entries.Count(entry =>
                entry.TryGetProperty("Name", out var name) && name.GetString() == expected.NativeModuleName && entry.TryGetProperty("State", out var state) && state.GetInt32() == expected.NativeState) == 1);
        }
        catch (JsonException) { return false; }
    }

    private async Task<NativeCollaborationGitLocal> CaptureDetailed(CancellationToken token)
    {
        RequireDirectoryChain(root); RequireNoLinks(gitDirectory);
        var local = await CaptureLocal(token);
        string? remoteHead = null;
        if (local.Git.RemoteName is not null && local.Git.RemoteUrl is not null && local.RemoteRef is not null)
        {
            try
            {
                var localRemote = LocalRemote(local.Git.RemoteUrl);
                var output = localRemote is null
                    ? await Execute(token, "ls-remote", "--exit-code", "--heads", local.Git.RemoteName, local.RemoteRef)
                    : await Execute(token, "--git-dir=" + localRemote, "show-ref", "--verify", local.RemoteRef);
                if (output.Code == 0)
                {
                    var rows = output.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => Words(line)).ToArray();
                    if (rows.Length == 1 && rows[0].Length == 2 && ObjectId(rows[0][0]) && rows[0][1] == local.RemoteRef) remoteHead = rows[0][0];
                }
            }
            catch (BridgeException) { token.ThrowIfCancellationRequested(); }
        }
        var end = await CaptureLocal(token);
        if (Fingerprint(local) != Fingerprint(end)) throw Error("GitBaselineChangedDuringRead", "The local repository changed while its remote baseline was being inspected.");
        return local with { Git = local.Git with { UpstreamHead = remoteHead } };
    }

    private async Task<NativeCollaborationGitLocal> CaptureLocal(CancellationToken token)
    {
        var metadataBefore = GuardFingerprint(root);
        // Read-only diff can otherwise execute configured clean/process filters.
        // Disable those subprocesses for observation; selected attributed files
        // are separately refused before content-to-tree verification.
        var configured = await Optional(token, "config", "--name-only", "--get-regexp", @"^filter\..*\.(clean|smudge|process|required)$");
        var filterNames = (configured ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(key => key.TrimEnd('\r'))
            .Select(key => key[..key.LastIndexOf('.')]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (filterNames.Any(name => name.Any(char.IsControl))) throw Error("GitFilterConfigurationInvalid", "Git filter configuration has an ambiguous name.");
        filterOverrides = filterNames.SelectMany(name => new[] { name + ".clean=", name + ".smudge=", name + ".process=", name + ".required=false" }).ToArray();
        if (!SamePath((await Run(token, "rev-parse", "--show-toplevel")).Trim(), root) ||
            !SamePath((await Run(token, "rev-parse", "--absolute-git-dir")).Trim(), gitDirectory))
            throw Error("GitRepositoryMismatch", "Git repository discovery differs from the fixed native collaboration root.");
        foreach (var marker in new[] { "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD", "rebase-merge", "rebase-apply", "BISECT_START", "index.lock", "HEAD.lock", "shallow" })
            if (File.Exists(Path.Combine(gitDirectory, marker)) || Directory.Exists(Path.Combine(gitDirectory, marker)))
                throw Error("GitOperationInProgress", "The repository has an unfinished Git operation or index lock.");
        var head = (await Run(token, "rev-parse", "--verify", "HEAD")).Trim();
        if (!ObjectId(head)) throw Error("GitHeadUnavailable", "A committed native repository head is required.");
        var branch = (await Run(token, "symbolic-ref", "--quiet", "--short", "HEAD")).Trim();
        if (string.IsNullOrWhiteSpace(branch)) throw Error("GitDetachedHead", "Native collaboration requires a named local branch.");
        var remote = await Optional(token, "config", "--get", "branch." + branch + ".remote");
        var remoteRef = await Optional(token, "config", "--get", "branch." + branch + ".merge");
        var upstream = await Optional(token, "rev-parse", "--abbrev-ref", "@{upstream}");
        var localUpstream = await Optional(token, "rev-parse", "--verify", "@{upstream}");
        string? url = null;
        if (remote is not null && remote != ".")
        {
            if (remote.Any(char.IsControl) || remote.StartsWith('-')) throw Error("GitRemoteAmbiguous", "The native remote name is not canonical.");
            var urls = (await Run(token, "remote", "get-url", "--push", "--all", remote)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToArray();
            var fetchUrls = (await Run(token, "remote", "get-url", "--all", remote)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.TrimEnd('\r')).ToArray();
            if (urls.Length != 1 || fetchUrls.Length != 1 || urls[0] != fetchUrls[0]) throw Error("GitRemoteAmbiguous", "One identical native fetch/push destination is required.");
            url = urls[0]; RequireSafeRemote(url);
            if (remoteRef is null || !remoteRef.StartsWith("refs/heads/", StringComparison.Ordinal) ||
                (await Execute(token, "check-ref-format", remoteRef)).Code != 0) throw Error("GitRemoteRefUnavailable", "The native remote branch has no unambiguous configured head ref.");
        }
        else remote = null;
        var tree = await Tree(head, token);
        if (tree.Values.Any(x => x.Mode is "160000" or "120000")) throw Error("GitSpecialEntryUnsupported", "Submodules and symbolic-link entries have no native collaboration file ownership adapter.");
        var staged = SplitPaths(await Run(token, "diff", "--cached", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", "HEAD", "--"));
        if ((await Run(token, "ls-files", "--unmerged", "-z")).Length != 0)
            throw Error("GitIndexConflicted", "The native collaboration index contains unresolved merge entries.");
        var paths = SplitPaths(await Run(token, "diff", "--no-ext-diff", "--no-textconv", "--no-renames", "--name-only", "-z", "HEAD", "--"))
            .Concat(SplitPaths(await Run(token, "ls-files", "--others", "--exclude-standard", "-z"))).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var changes = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (!aliases.Add(path)) throw Error("GitPathAlias", "The native repository contains Windows path aliases.");
            changes.Add(path, FileHash(path) ?? "deleted");
        }
        var identity = (await Run(token, "show", "-s", "--format=%an%x00%ae%x00%cn%x00%ce", "HEAD")).TrimEnd('\r', '\n').Split('\0');
        if (identity.Length != 4) throw Error("GitCommitIdentityUnavailable", "Native head author and committer could not be read.");
        var hash = Wire.Hash(string.Join("\n", changes.Select(x => x.Key + "\0" + x.Value)) + "\nindex\n" + string.Join("\n", staged));
        var state = new GitState(head, branch, remote, url, upstream, null, staged.Count == 0, hash,
            new(identity[0], identity[1]), new(identity[2], identity[3]), new(changes, StringComparer.Ordinal));
        if (GuardFingerprint(root) != metadataBefore) throw Error("GitBaselineChangedDuringRead", "Git metadata changed during local collaboration inspection.");
        return new(state, remoteRef, localUpstream, staged, changes, metadataBefore);
    }

    private async Task<Dictionary<string, NativeCollaborationGitTreeEntry>> Tree(string revision, CancellationToken token)
    {
        var result = new Dictionary<string, NativeCollaborationGitTreeEntry>(StringComparer.Ordinal);
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (await Run(token, "ls-tree", "-r", "-z", "--full-tree", revision)).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t'); if (tab < 0) throw Error("GitTreeInvalid", "Git returned an invalid native source tree entry.");
            var metadata = Words(entry[..tab]); var path = Relative(entry[(tab + 1)..]);
            if (metadata.Length != 3 || !ObjectId(metadata[2]) || !aliases.Add(path) || !result.TryAdd(path, new(metadata[0], metadata[2])))
                throw Error("GitTreeInvalid", "The native source tree has malformed or ambiguous entries.");
        }
        return result;
    }

    private async Task<string> BlobId(string path, CancellationToken token)
    {
        // Attribute inspection never executes a filter. Unsupported custom
        // transforms cannot silently change the approved source bytes.
        var attributes = (await Run(token, "check-attr", "-z", "filter", "--", path)).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (attributes.Length != 3 || attributes[2] is not ("unspecified" or "unset"))
            throw Error("GitCustomFilterUnsupported", "A selected source file uses a custom Git filter that has not been adapted for native check-in verification.");
        var hash = (await Run(token, "hash-object", "--path=" + path, "--", path)).Trim();
        if (!ObjectId(hash)) throw Error("GitBlobUnavailable", "The selected native source file has no verified Git representation.");
        if (path.StartsWith("Plugin/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var raw = (await Run(token, "hash-object", "--no-filters", "--", path)).Trim();
            if (hash != raw) throw Error("PluginPackageBytesChanged", "Git conversion would change the saved plugin package bytes: " + path);
        }
        return hash;
    }

    private string? FileHash(string path)
    {
        var full = Under(path);
        if (Directory.Exists(full)) throw Error("GitDirectoryTargetUnsupported", "Native module file ownership must resolve to individual files.");
        if (!File.Exists(full)) return null;
        using var stream = File.OpenRead(full); return Convert.ToHexString(SHA256.HashData(stream));
    }
    private string Under(string path)
    {
        var relative = Relative(path); var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        for (var value = full; !SamePath(value, root); value = Path.GetDirectoryName(value)!)
        { if (File.Exists(value) || Directory.Exists(value)) RequireNoLinks(value); }
        return full;
    }
    private static string Relative(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Any(char.IsControl) || Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(x => x.Length == 0 || x is "." or ".." || x.EndsWith('.') || x.EndsWith(' ') || x.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw Error("InvalidNativeGitPath", "A native Git source path must be a canonical relative file path.");
        return path;
    }
    private static void RequireNoLinks(string path)
    { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Error("GitPathIsLink", "Native collaboration Git inspection cannot follow linked repository paths."); }
    private static void RequireDirectoryChain(string path)
    { for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current)) if (Directory.Exists(current)) RequireNoLinks(current); }
    private static bool SamePath(string a, string b) => string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string[] Words(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static bool ObjectId(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    private static string Fingerprint(NativeCollaborationGitLocal value) => Wire.Hash(value.Git.Head + "\n" + value.Git.Branch + "\n" + value.Git.RemoteName + "\n" + value.Git.RemoteUrl + "\n" + value.Git.Upstream + "\n" + value.RemoteRef + "\n" + value.LocalUpstream + "\n" + value.Git.WorktreeHash + "\n" + value.MetadataFingerprint);
    private void RequireOwner(NativeCollaborationGitBaseline baseline)
    { if (!ReferenceEquals(baseline.Owner, this)) throw Error("GitBaselineOwnerMismatch", "The collaboration baseline belongs to another fixed repository binding."); }
    private IReadOnlyList<string> SplitPaths(string value) => value.Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(Relative).ToArray();
    private static void RequireSafeRemote(string value)
    {
        if (value.Any(char.IsControl) || value.Contains("::", StringComparison.Ordinal) || value.StartsWith('-')) throw Error("GitRemoteUnsupported", "Native collaboration requires a fixed ordinary Git remote destination.");
        if (Path.IsPathFullyQualified(value)) return;
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" or "ssh" or "git" or "file")
        {
            if ((uri.Scheme is "https" or "http") && !string.IsNullOrEmpty(uri.UserInfo) || uri.UserInfo.Contains(':'))
                throw Error("CredentialInRemoteUrl", "Embedded remote credentials cannot be exposed in collaboration status.");
            return;
        }
        // Ordinary SCP-style SSH addresses; remote-helper command syntax is refused above.
        if (value.Contains('@') && value.IndexOf(':') > value.IndexOf('@') && !value.Contains(' ')) return;
        throw Error("GitRemoteUnsupported", "The configured Git transport has no safe read-only remote-tip adapter.");
    }
    private static string? LocalRemote(string value)
    {
        var path = Path.IsPathFullyQualified(value) ? value : Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null;
        if (path is null) return null;
        path = Path.GetFullPath(path); RequireDirectoryChain(path);
        // Native cloning accepts both bare and ordinary local repositories.
        var metadata = Directory.Exists(Path.Combine(path, ".git")) ? Path.Combine(path, ".git") : path;
        if (!Directory.Exists(metadata)) return metadata; // Git returns unavailable, never a guessed tip.
        RequireNoLinks(metadata);
        foreach (var name in new[] { "HEAD", "packed-refs", "config", "refs" })
            if (File.Exists(Path.Combine(metadata, name)) || Directory.Exists(Path.Combine(metadata, name))) RequireNoLinks(Path.Combine(metadata, name));
        void CheckRefs(string folder)
        {
            if (!Directory.Exists(folder)) return;
            RequireNoLinks(folder);
            foreach (var file in Directory.EnumerateFiles(folder)) RequireNoLinks(file);
            foreach (var child in Directory.EnumerateDirectories(folder)) CheckRefs(child);
        }
        CheckRefs(Path.Combine(metadata, "refs"));
        return metadata;
    }

    private async Task<string> Run(CancellationToken token, params string[] args)
    { var result = await Execute(token, args); if (result.Code != 0) throw Error("GitReadFailed", "Read-only Git inspection failed (" + args[0] + ", exit " + result.Code + ")."); return result.Output; }
    private async Task<string?> Optional(CancellationToken token, params string[] args)
    { var result = await Execute(token, args); return result.Code == 0 ? result.Output.TrimEnd('\r', '\n') : null; }
    private async Task<(int Code, string Output)> Execute(CancellationToken token, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        foreach (var key in start.Environment.Keys.Where(x => x.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GCM_INTERACTIVE"] = "never"; start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in new[] { "--no-optional-locks", "-c", "core.quotepath=false", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false", "-c", "diff.autoRefreshIndex=false", "-c", "protocol.ext.allow=never" }) start.ArgumentList.Add(argument);
        foreach (var config in filterOverrides) { start.ArgumentList.Add("-c"); start.ArgumentList.Add(config); }
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw Error("GitUnavailable", "Read-only Git inspection could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            throw Error("GitReadTimeout", "Read-only Git evidence is unavailable; no native collaboration operation was dispatched by this helper.");
        }
        var output = await stdout; _ = await stderr;
        if (output.Length > MaximumOutput) throw Error("GitEvidenceTooLarge", "Read-only Git evidence exceeds the supported limit.");
        return (process.ExitCode, output);
    }
    private static BridgeException Error(string code, string message) => new(code, message);
}

public sealed class NativeCollaborationGitBaseline
{
    internal NativeCollaborationGitBaseline(NativeCollaborationGit owner, GitState git, bool checkout, IReadOnlyList<string> allowed,
        IReadOnlyList<CollaborationModuleState> selected, IReadOnlyList<CollaborationModuleState> modules, UserIdentity user,
        NativeCollaborationGitLocal local, Dictionary<string, NativeCollaborationGitFile> expected, Dictionary<string, JsonNode> locks, string fingerprint, HashSet<string> cancelledNewNames, UserIdentity commitIdentity)
    { Owner = owner; Git = git; Checkout = checkout; AllowedFiles = allowed; SelectedBefore = selected; ModulesBefore = modules; User = user;
      Local = local; ExpectedFiles = expected; LockRecords = locks; Fingerprint = fingerprint; CancelledNewNames=cancelledNewNames; CommitIdentity=commitIdentity; AllowedSet = allowed.ToHashSet(StringComparer.Ordinal); }
    public GitState Git { get; }
    public bool Checkout { get; }
    public IReadOnlyList<string> AllowedFiles { get; }
    public IReadOnlyList<string> SelectedModuleNames => SelectedBefore.Select(x => x.NativeModuleName).ToArray();
    internal NativeCollaborationGit Owner { get; }
    internal UserIdentity User { get; }
    internal UserIdentity CommitIdentity { get; }
    internal IReadOnlyList<CollaborationModuleState> SelectedBefore { get; }
    internal IReadOnlyList<CollaborationModuleState> ModulesBefore { get; }
    internal NativeCollaborationGitLocal Local { get; }
    internal Dictionary<string, NativeCollaborationGitFile> ExpectedFiles { get; }
    internal Dictionary<string, JsonNode> LockRecords { get; }
    internal string Fingerprint { get; }
    internal HashSet<string> AllowedSet { get; }
    internal HashSet<string> CancelledNewNames { get; }
    internal string? RemoteRef => Local.RemoteRef;
    internal string? LocalUpstream => Local.LocalUpstream;
}

public sealed record NativeCollaborationGitVerification(GitState? Git, bool LocalCommitVerified, bool SourceVerified,
    bool LockStateVerified, string? CommitId, bool? RemoteSynchronized, IReadOnlyList<string> CommittedFiles, string Code, string Message);
internal sealed record NativeCollaborationGitLocal(GitState Git, string? RemoteRef, string? LocalUpstream,
    IReadOnlyList<string> Staged, Dictionary<string, string> Changes, string MetadataFingerprint);
internal sealed record NativeCollaborationGitFile(string? RawHash, string? ObjectId);
internal sealed record NativeCollaborationGitTreeEntry(string Mode, string ObjectId);
