using System.Diagnostics;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V11;

// Read-only reconstruction for reopening a saved native tombstone. It never
// acquires a lock and only accepts differences consisting of owned removals.
internal static class NativeV11AssetRegistryRecovery
{
    internal static async Task<bool> VerifyAsync(NativeV11Collaboration.Plan plan, GitState git, string target)
    {
        if (!git.IndexClean || string.IsNullOrEmpty(git.Head) || git.ChangedFiles is null ||
            !NativeV11AssetRemovalRegistration.IsAssetPath(target) || plan.Owners.Any(owner => owner.State != "Edit")) return false;
        var root = plan.Session.SourcePath;
        var sourceRevision = NativeV11SourceRevision.Capture(root);
        var names = plan.Owners.Select(owner => (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!).ToHashSet(StringComparer.Ordinal);
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0"; start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var argument in new[] { "show", git.Head + ":.collaboration" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git registry inspection is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var oldText = await output; _ = await error;
        if (process.ExitCode != 0 || oldText.Length > 16 * 1024 * 1024) return false;
        var currentPath = V11Safety.Under(root, ".collaboration");
        if (!File.Exists(currentPath) || new FileInfo(currentPath).Length > 16 * 1024 * 1024) return false;
        var additions = InspectRegistry(oldText, await File.ReadAllTextAsync(currentPath), names, plan.Files);
        if (additions is null || !additions.Contains(target, StringComparer.Ordinal)) return false;
        if (git.ChangedFiles.Keys.Any(path => !path.Equals(".collaboration", StringComparison.Ordinal) && !additions.Contains(path.Replace('\\', '/'), StringComparer.Ordinal))) return false;
        var tracked = await NativeV11AssetRemovalRegistration.TrackedAtHeadAsync(root, additions);
        if (!tracked.SequenceEqual(additions, StringComparer.Ordinal) || additions.Any(path =>
            new[] { root, plan.Session.LocalPath, plan.Session.LastPath }.Any(basePath => File.Exists(V11Safety.Under(basePath, path))))) return false;
        var afterGit = await new GitProbe(root).CaptureAsync();
        if (afterGit.Head != git.Head || afterGit.Branch != git.Branch || afterGit.WorktreeHash != git.WorktreeHash || !afterGit.IndexClean ||
            sourceRevision != NativeV11SourceRevision.Capture(root)) return false;
        NativeV11Collaboration.VerifyOwned(plan, true);
        return true;
    }

    internal static string[]? InspectRegistry(string previousJson, string currentJson, IReadOnlySet<string> names,
        IReadOnlyDictionary<string, string[]> files)
    {
        var old = JsonNode.Parse(previousJson) as JsonArray;
        var current = JsonNode.Parse(currentJson) as JsonArray;
        if (old is null || current is null || old.Count != current.Count) return null;
        var oldOwners = old.OfType<JsonObject>().ToDictionary(node => node["ModuleName"]?.GetValue<string>() ?? "", StringComparer.Ordinal);
        var nowOwners = current.OfType<JsonObject>().ToDictionary(node => node["ModuleName"]?.GetValue<string>() ?? "", StringComparer.Ordinal);
        if (oldOwners.Count != old.Count || nowOwners.Count != current.Count || oldOwners.Keys.Any(key => !nowOwners.ContainsKey(key))) return null;
        var additions = new List<string>();
        foreach (var (name, previous) in oldOwners)
        {
            var actual = nowOwners[name];
            if (!names.Contains(name)) { if (!JsonNode.DeepEquals(previous, actual)) return null; continue; }
            static string[] Removed(JsonObject value) => value["ToRemoveFiles"] is JsonArray array
                ? array.Select(node => node?.GetValue<string>()?.Replace('\\', '/') ?? "").ToArray() : [];
            var before = Removed(previous); var after = Removed(actual);
            if (before.Distinct(StringComparer.OrdinalIgnoreCase).Count() != before.Length ||
                after.Distinct(StringComparer.OrdinalIgnoreCase).Count() != after.Length || before.Any(path => !after.Contains(path, StringComparer.Ordinal))) return null;
            var left = (JsonObject)previous.DeepClone(); var right = (JsonObject)actual.DeepClone();
            left.Remove("ToRemoveFiles"); right.Remove("ToRemoveFiles");
            if (!JsonNode.DeepEquals(left, right)) return null;
            var ownerAdditions = after.Where(path => !before.Contains(path, StringComparer.Ordinal)).ToArray();
            if (!files.TryGetValue(name, out var group) || ownerAdditions.Any(path => !group.Any(owned =>
                owned == path || owned.EndsWith('/') && path.StartsWith(owned, StringComparison.Ordinal)))) return null;
            additions.AddRange(ownerAdditions);
        }
        if (additions.Count == 0 ||
            additions.Any(path => !NativeV11AssetRemovalRegistration.IsAssetPath(path)) ||
            additions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != additions.Count) return null;
        return additions.ToArray();
    }
}
