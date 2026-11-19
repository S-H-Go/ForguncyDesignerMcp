using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Read-only Git proofs shared by native checkout and checkin. This class never
// stages, commits, fetches, pulls, pushes, changes Git configuration or writes
// .collaboration. The native operation remains the only repository writer.
internal static class NativeV11CollaborationRepository
{
    internal sealed record Baseline(string ProjectPath, GitState Git, string RemoteHead,
        IReadOnlyList<string> AllowedFiles);
    internal sealed record Outcome(GitState Git, string? FreshRemoteHead,
        IReadOnlyList<string> CommittedFiles, bool? RemoteSynchronized, string? RemoteReadFailure);

    internal static async Task<Baseline> CaptureAsync(string projectPath,
        CollaborationMutationRequest request, IReadOnlyList<string> allowedNativeFiles)
    {
        var root = RepositoryRoot(projectPath);
        if (request.ExpectedRemoteUrl is not { Length: > 0 } || request.ExpectedBranch is not { Length: > 0 } ||
            !CommitId(request.ExpectedRemoteHead))
            throw Review("ExpectedRemoteUrl, ExpectedBranch and ExpectedRemoteHead are required before native collaboration dispatch.");
        var allowed = Files(root, allowedNativeFiles);
        if (!allowed.Contains(".collaboration", StringComparer.Ordinal))
            throw Review("The complete native commit group must include .collaboration.");
        var probe = new GitProbe(root);
        var git = Freeze(await probe.CaptureAsync());
        RequireExpected(git, request);
        var remote = await probe.RemoteHeadAsync(git);
        if (!CommitId(remote) || remote != request.ExpectedRemoteHead || git.Head != remote)
            throw Review("The fresh remote branch, expected head and local HEAD must identify the same commit.");
        var second = Freeze(await probe.CaptureAsync());
        if (!Same(git, second)) throw Review("The repository changed during the asynchronous baseline read.");
        return new(root, git with { UpstreamHead = remote }, remote!, Array.AsReadOnly(allowed));
    }

    internal static async Task VerifyBeforeAsync(string projectPath, Baseline baseline,
        CollaborationMutationRequest request)
    {
        var root = RepositoryRoot(projectPath);
        if (!SamePath(root, baseline.ProjectPath)) throw Review("The repository differs from the reviewed baseline.");
        var probe = new GitProbe(root);
        var git = Freeze(await probe.CaptureAsync());
        RequireExpected(git, request);
        if (!Same(baseline.Git, git)) throw Review("HEAD, branch, index, remote or worktree changed after collaboration planning.");
        var remote = await probe.RemoteHeadAsync(git);
        if (remote != baseline.RemoteHead || remote != request.ExpectedRemoteHead)
            throw Review("The remote branch advanced after collaboration planning.");
        var last = Freeze(await probe.CaptureAsync());
        if (!Same(git, last)) throw Review("The repository changed while the fresh remote head was being verified.");
    }

    internal static Task<Outcome> VerifyAfterCheckoutAsync(string projectPath, Baseline baseline,
        UserIdentity expectedUser) => VerifyAfterAsync(projectPath, baseline, expectedUser, true);

    internal static Task<Outcome> VerifyAfterCheckinAsync(string projectPath, Baseline baseline,
        UserIdentity expectedUser) => VerifyAfterAsync(projectPath, baseline, expectedUser, false);

    // New registration alone does not prove the source files are absent from
    // the current commit. Compare the reviewed tree using Windows path casing.
    internal static async Task VerifyNewFilesAbsentAtHeadAsync(string projectPath, Baseline baseline, IEnumerable<string> sourceFiles)
    {
        var root = RepositoryRoot(projectPath);
        if (!SamePath(root, baseline.ProjectPath) || !CommitId(baseline.Git.Head))
            throw Review("The cancellation source proof has no verified repository HEAD.");
        var files = Files(root, sourceFiles.ToArray());
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "ls-tree", "-r", "--name-only", "-z", baseline.Git.Head! }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw Review("Git source history inspection is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw Review("Git source history inspection timed out."); }
        var tree = await output;
        if (process.ExitCode != 0 || tree.Length > 16 * 1024 * 1024 ||
            tree.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(path => files.Any(file =>
                file.EndsWith('/') ? path.Equals(file[..^1], StringComparison.OrdinalIgnoreCase) || path.StartsWith(file, StringComparison.OrdinalIgnoreCase) :
                path.Equals(file, StringComparison.OrdinalIgnoreCase))))
            throw Review("A proposed new-module cancellation has source files already present in the reviewed HEAD.");
        await error;
        var current = Freeze(await new GitProbe(root).CaptureAsync());
        if (!Same(current, baseline.Git)) throw Review("The repository changed during cancellation source-history inspection.");
    }

    internal static async Task VerifyRegistryAtHeadAsync(string projectPath, GitState expected)
    {
        var root = RepositoryRoot(projectPath);
        var probe = new GitProbe(root);
        var before = Freeze(await probe.CaptureAsync());
        if (!Same(before, expected) || !before.IndexClean || before.ChangedFiles is null ||
            before.ChangedFiles.Keys.Any(path => path.Equals(".collaboration", StringComparison.OrdinalIgnoreCase)))
            throw Review("The native module registry must already be committed before an existing lock can be reused.");
        var path = V11Safety.Under(root, ".collaboration");
        if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw Review("The native module registry is absent or exceeds its read bound.");
        var disk = File.ReadAllBytes(path);
        var committed = await RegistryBlobAsync(root);
        if (!V11Safety.Equivalent(V11Safety.Body(committed), V11Safety.Body(disk)))
            throw Review("The current native module registry differs from its HEAD blob.");
        if (await probe.RemoteHeadAsync(before) != before.Head)
            throw Review("The existing lock is not proven at the current remote HEAD.");
        var after = Freeze(await probe.CaptureAsync());
        if (!Same(before, after) || !File.ReadAllBytes(path).AsSpan().SequenceEqual(disk))
            throw Review("The repository or native module registry changed during existing-lock verification.");
    }

    private static Task<byte[]> RegistryBlobAsync(string root) => GitBlobAsync(root, ".collaboration", 16 * 1024 * 1024);

    internal static async Task VerifySharedFilesAtHeadAsync(string projectPath, Baseline baseline, IEnumerable<string> paths)
    {
        var root = RepositoryRoot(projectPath);
        if (!SamePath(root, baseline.ProjectPath)) throw Review("The shared file proof belongs to another repository.");
        var names = Files(root, paths).ToArray();
        if (names.Any(path => path.EndsWith('/'))) throw Review("A shared effect must be an exact file, not a directory.");
        var before = Freeze(await new GitProbe(root).CaptureAsync());
        if (!Same(before, baseline.Git)) throw Review("The repository changed before shared-file history verification.");
        foreach (var name in names)
        {
            var source = V11Safety.Under(root, name);
            if (!File.Exists(source)) throw Review("A shared source file is absent.");
            string SourceHash() { using var stream = File.OpenRead(source); return Convert.ToHexString(SHA256.HashData(stream)); }
            var sourceHash = SourceHash();
            var committed = await GitBlobHashAsync(root, name);
            if (sourceHash != committed || SourceHash() != sourceHash)
                throw Review("A shared source file differs from its committed Git baseline.");
        }
        if (!Same(before, Freeze(await new GitProbe(root).CaptureAsync())))
            throw Review("The repository changed during shared-file history verification.");
    }

    private static async Task<string> GitBlobHashAsync(string root, string name)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "cat-file", "blob", "HEAD:" + name }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw Review("Git source blob inspection is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) != 0) hash.AppendData(buffer, 0, count);
            await process.WaitForExitAsync(timeout.Token); await error;
            if (process.ExitCode != 0) throw Review("The current HEAD does not contain the requested shared source blob.");
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch { if (!process.HasExited) process.Kill(true); throw; }
    }

    private static async Task<byte[]> GitBlobAsync(string root, string name, int maximum)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        foreach (var argument in new[] { "cat-file", "blob", "HEAD:" + name }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw Review("Git registry blob inspection is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        using var output = new MemoryStream();
        try
        {
            var buffer = new byte[64 * 1024];
            int count;
            while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) != 0)
            {
                if (output.Length + count > maximum) throw Review("The committed native source exceeds its read bound.");
                output.Write(buffer, 0, count);
            }
            await process.WaitForExitAsync(timeout.Token); await error;
            if (process.ExitCode != 0) throw Review("The current HEAD does not contain the requested native source blob.");
            return output.ToArray();
        }
        catch
        {
            if (!process.HasExited) process.Kill(true);
            throw;
        }
    }

    private static async Task<Outcome> VerifyAfterAsync(string projectPath, Baseline baseline, UserIdentity expectedUser, bool checkout)
    {
        var root = RepositoryRoot(projectPath);
        if (!SamePath(root, baseline.ProjectPath)) throw Review("The repository changed after native dispatch.");
        var probe = new GitProbe(root);
        var git = Freeze(await probe.CaptureAsync());
        if (!git.IndexClean || !SameBinding(git, baseline.Git) || !CommitId(git.Head) || git.Head == baseline.Git.Head)
            throw Review("Native collaboration did not leave a distinct commit on the reviewed branch with a clean index.");
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in new[] { "rev-list", "--parents", "-n", "1", git.Head }) start.ArgumentList.Add(argument);
        using (var process = Process.Start(start) ?? throw Review("Git commit parent inspection is unavailable."))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(true); throw Review("Git commit parent inspection timed out."); }
            var parents = (await output).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            await error;
            if (process.ExitCode != 0 || !IsDirectChild(parents, git.Head, baseline.Git.Head))
                throw Review("The native commit is not a single direct child of the reviewed baseline.");
        }
        RequireCommitIdentity(git.Author, git.Committer, expectedUser, checkout);
        var files = Files(root, await probe.CommittedFilesAsync(baseline.Git.Head, git.Head));
        var allowed = baseline.AllowedFiles.ToHashSet(StringComparer.Ordinal);
        if (!files.Contains(".collaboration", StringComparer.Ordinal) || files.Any(path => !IsExplicitlyAllowed(allowed, path)))
            throw Review("The native commit changed files outside the complete reviewed owner group.");
        var before = baseline.Git.ChangedFiles ?? throw Review("The baseline worktree proof is unavailable.");
        var after = git.ChangedFiles ?? throw Review("The final worktree proof is unavailable.");
        var beforeForeign = before.Where(pair => !allowed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var afterForeign = after.Where(pair => !allowed.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (!SameFiles(beforeForeign, afterForeign) || after.Keys.Any(allowed.Contains))
            throw Review("Native collaboration changed unrelated worktree content or left a requested owner uncommitted.");

        // A local commit is an independent fact. Failure to read the remote
        // must not erase it or claim that push succeeded/failed conclusively.
        string? remote = null; string? remoteFailure = null;
        try
        {
            remote = await probe.RemoteHeadAsync(git);
            if (!CommitId(remote)) remoteFailure = "RemoteHeadUnavailable";
        }
        catch (Exception error) when (error is BridgeException or IOException or UnauthorizedAccessException)
        { remoteFailure = error is BridgeException bridge ? bridge.Code : error.GetType().Name; }
        var last = Freeze(await probe.CaptureAsync());
        if (!Same(git, last)) throw Review("The repository changed during the final asynchronous remote read.");
        return new(git with { UpstreamHead = remote }, remote, Array.AsReadOnly(files),
            remoteFailure is null ? remote == git.Head : null, remoteFailure);
    }

    internal static bool IsExplicitlyAllowed(IReadOnlySet<string> allowed, string path) => allowed.Contains(path);

    internal static void RequireCommitIdentity(UserIdentity? author, UserIdentity? committer, UserIdentity expected, bool checkout)
    {
        var system = expected.UserName is "HZG System" or "Forguncy System" && expected.Email == expected.UserName;
        if (checkout != system || author != expected || committer != expected)
            throw Review("The native commit author or committer differs from the reviewed checkout or business identity.");
    }

    private static void RequireExpected(GitState git, CollaborationMutationRequest request)
    {
        if (!git.IndexClean) throw Review("The Git index must be clean before native collaboration dispatch.");
        if (!CommitId(git.Head) || git.Head != request.ExpectedRemoteHead || git.RemoteUrl != request.ExpectedRemoteUrl ||
            git.Branch != request.ExpectedBranch || string.IsNullOrWhiteSpace(git.RemoteName) || string.IsNullOrWhiteSpace(git.Upstream))
            throw Review("The current branch, remote binding or local HEAD differs from the complete expected repository binding.");
    }

    private static string RepositoryRoot(string path)
    {
        var root = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw Review("A physical collaboration working directory is required.");
        var git = V11Safety.Under(root, ".git");
        if (!Directory.Exists(git) || File.Exists(Path.Combine(git, "commondir")) || File.Exists(Path.Combine(git, "objects", "info", "alternates")))
            throw Review("Linked/shared Git worktrees and alternate object stores require a separate native ownership review.");
        return root;
    }

    private static string[] Files(string root, IEnumerable<string> paths)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.StartsWith('/') || path != path.Trim())
                throw Review("The native commit file group contains a noncanonical relative path.");
            _ = V11Safety.Under(root, path.EndsWith('/') ? path[..^1] : path);
            var first = path.Split('/')[0];
            if (first.Equals(".git", StringComparison.OrdinalIgnoreCase) || first.Equals(".local", StringComparison.OrdinalIgnoreCase) ||
                first.Equals("LocalData", StringComparison.OrdinalIgnoreCase) || first.Equals("LastSaveFiles", StringComparison.OrdinalIgnoreCase) ||
                !result.Add(path)) throw Review("The native commit file group crosses a control/cache boundary or contains duplicate paths.");
        }
        if (result.Count is < 1 or > 16384) throw Review("A bounded complete native commit file group is required.");
        return result.Order(StringComparer.Ordinal).ToArray();
    }

    private static GitState Freeze(GitState value) => value with
    { ChangedFiles = value.ChangedFiles is null ? null : new(value.ChangedFiles, StringComparer.Ordinal) };
    private static bool Same(GitState left, GitState right) => left.Head == right.Head && SameBinding(left, right) &&
        left.IndexClean == right.IndexClean && left.WorktreeHash == right.WorktreeHash && left.Author == right.Author && left.Committer == right.Committer &&
        left.ChangedFiles is not null && right.ChangedFiles is not null && SameFiles(left.ChangedFiles, right.ChangedFiles);
    private static bool SameBinding(GitState left, GitState right) => left.Branch == right.Branch && left.RemoteName == right.RemoteName &&
        left.RemoteUrl == right.RemoteUrl && left.Upstream == right.Upstream;
    private static bool SameFiles(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);
    private static bool CommitId(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    internal static bool IsDirectChild(string[] parents, string head, string baseline) =>
        parents.Length == 2 && parents[0] == head && parents[1] == baseline && head != baseline;
    private static bool SamePath(string left, string right) => Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static BridgeException Review(string message) => new("NativeCollaborationRepositoryRequiresReview", message);
}

// This proof accepts expected bytes produced by the actual native serializers
// or native owned companion models. The adapter must retain its mutation
// journal for deleted/renamed paths; null is a reviewed absence, not a missing
// dictionary entry. It must NOT construct expected bytes by rereading source.
internal sealed class NativeV11CollaborationSourceProof
{
    private readonly string root;
    private readonly IReadOnlyDictionary<string, byte[]?> expected;
    private readonly IReadOnlyDictionary<string, string> captured;
    private readonly HashSet<string> jsonPaths;
    internal IReadOnlyList<string> Paths { get; }

    private NativeV11CollaborationSourceProof(string root, Dictionary<string, byte[]?> expected,
        Dictionary<string, string> captured, HashSet<string> jsonPaths)
    {
        this.root = root; this.expected = new ReadOnlyDictionary<string, byte[]?>(expected);
        this.captured = new ReadOnlyDictionary<string, string>(captured); this.jsonPaths = jsonPaths;
        Paths = Array.AsReadOnly(expected.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    internal static NativeV11CollaborationSourceProof Capture(string projectPath,
        IReadOnlyDictionary<string, byte[]?> actualNativeExpected, Func<string, bool> isJson)
    {
        var root = Path.GetFullPath(projectPath).TrimEnd('\\', '/');
        if (!Directory.Exists(root) || actualNativeExpected.Count is < 1 or > 16384)
            throw Review("A complete bounded native owner source projection is required.");
        var expected = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
        var captured = new Dictionary<string, string>(StringComparer.Ordinal);
        var json = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        foreach (var pair in actualNativeExpected)
        {
            var path = pair.Key;
            RequirePath(root, path);
            if (!expected.TryAdd(path, pair.Value?.ToArray())) throw Review("The native source proof contains duplicate paths.");
            if (pair.Value is not null && ((total += pair.Value.Length) > 256L * 1024 * 1024 || pair.Value.Length > 32 * 1024 * 1024))
                throw Review("The native source proof exceeds its reviewed byte bound.");
            if (isJson(path))
            {
                if (path.EndsWith('/')) throw Review("Directory markers cannot be native JSON files.");
                if (pair.Value is not null) _ = Json(pair.Value);
                json.Add(path);
            }
            captured.Add(path, Verify(root, path, pair.Value, json.Contains(path)));
        }
        var proof = new NativeV11CollaborationSourceProof(root, expected, captured, json);
        proof.VerifyUnchanged(projectPath);
        return proof;
    }

    internal void VerifyUnchanged(string projectPath)
    {
        if (!Path.GetFullPath(projectPath).TrimEnd('\\', '/').Equals(root, StringComparison.OrdinalIgnoreCase))
            throw Review("The saved native owner proof belongs to a different collaboration directory.");
        foreach (var pair in expected)
            if (Verify(root, pair.Key, pair.Value, jsonPaths.Contains(pair.Key)) != captured[pair.Key])
                throw Review("A source file changed after its native save readback was verified.");
    }

    private static string Verify(string root, string path, byte[]? native, bool json)
    {
        var directory = path.EndsWith('/');
        var full = V11Safety.Under(root, directory ? path[..^1] : path);
        if (native is null)
        {
            if (File.Exists(full) || Directory.Exists(full)) throw Review("A native deleted/renamed owner path still exists in the saved project.");
            return "absent";
        }
        if (directory)
        {
            if (native.Length != 0 || !Directory.Exists(full) || File.Exists(full)) throw Review("A native directory marker was not saved at its exact path.");
            return "directory";
        }
        if (!File.Exists(full) || Directory.Exists(full) || new FileInfo(full).Length > 32 * 1024 * 1024)
            throw Review("A complete native owner source file is missing or exceeds its read bound.");
        var source = File.ReadAllBytes(full);
        if (json ? !V11Safety.Equivalent(Json(native), Json(source)) : !source.AsSpan().SequenceEqual(native))
            throw Review("Saved source does not match the actual native owner serialization or companion bytes.");
        return Convert.ToHexString(SHA256.HashData(source));
    }

    private static void RequirePath(string root, string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path != path.Trim() || path.StartsWith('/'))
            throw Review("A source-proof path must be canonical and relative.");
        var leaf = path.EndsWith('/') ? path[..^1] : path;
        _ = V11Safety.Under(root, leaf);
        if (new[] { ".git", ".local", ".collaboration", "LocalData", "LastSaveFiles" }.Contains(leaf.Split('/')[0], StringComparer.OrdinalIgnoreCase))
            throw Review("Native source proof cannot own repository controls or designer caches.");
    }

    private static JsonElement Json(byte[] bytes)
    {
        var span = bytes.AsSpan(); if (span.StartsWith(new byte[] { 239, 187, 191 })) span = span[3..];
        using var document = JsonDocument.Parse(span.ToArray());
        void Unique(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                { if (!names.Add(property.Name)) throw Review("A native source JSON projection has duplicate properties."); Unique(property.Value); }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Unique(item);
        }
        Unique(document.RootElement); return document.RootElement.Clone();
    }

    private static BridgeException Review(string message) => new("NativeCollaborationSourceRequiresReview", message);
}

internal static class NativeV11CollaborationCheckin
{
    // Exact V11 CheckInAsync(string,IEnumerable<string>,TaskInfo) internally
    // calls ExecuteRetryAsync(...,3). Both builds catch NonFastForwardException,
    // call OnCommitFailedAsync, then repeat an action which pulls before save /
    // unlock / commit / push. There is no native expected-head/CAS parameter.
    // A fresh ls-remote is an observed precondition, not a remote write lease.
    // Keep the production dispatcher closed until that explicit contract gap
    // has a reviewed native resolution. Never replace it with Git.Commit.
    internal static bool SupportsAtomicExpectedRemoteHead => false;
    internal static string DeferredCode => "NativeRemoteCasUnavailable";

    internal static Task RequireDispatchAsync(NativeV11Session session,
        CollaborationMutationRequest request, NativeV11CollaborationSourceProof savedSources)
    {
        session.VerifyContext();
        V11Safety.SameTarget(request.Target, session.Context.CaptureSnapshot().Target);
        savedSources.VerifyUnchanged(session.SourcePath);
        var snapshot = session.Context.CaptureSnapshot();
        if (!snapshot.InCollaborationMode || snapshot.HasUnsavedChanges || snapshot.Busy ||
            NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is not false || snapshot.CurrentUser != request.ExpectedUser)
            throw new BridgeException("NativeCheckinRequiresReview", "Native checkin requires the same clean, idle collaboration document and exact native user after source verification.");
        throw new BridgeException(DeferredCode,
            "The installed native checkin retries a non-fast-forward push by pulling and repeating the operation; it cannot enforce the required atomic ExpectedRemoteHead contract.");
    }
}
