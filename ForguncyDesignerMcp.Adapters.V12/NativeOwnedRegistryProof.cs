using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// Only ToRemoveFiles changes of the exact saved owners may differ from HEAD.
// The adapter separately verifies the complete live native registry and source
// revision before and after this read-only Git observation.
internal static class NativeOwnedRegistryProof
{
    internal static async Task<bool> VerifyAsync(string root, GitState? git, IEnumerable<NativeModuleFileGroup> owners, CancellationToken token)
    {
        if (git?.ChangedFiles is null) return false;
        if (!git.ChangedFiles.ContainsKey(".collaboration")) return true;
        if (git.Head.Length is not (40 or 64) || !git.Head.All(Uri.IsHexDigit)) return false;
        var path = NativeSaveVerification.Under(root, ".collaboration");
        if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) return false;
        var start = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_NO_REPLACE_OBJECTS"] = "1";
        start.ArgumentList.Add("--no-pager"); start.ArgumentList.Add("show"); start.ArgumentList.Add(git.Head + ":.collaboration");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Git could not be started.");
        async Task<string> ReadBounded(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[8192];
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), token);
                if (count == 0) return text.ToString();
                if (text.Length + count > 32 * 1024 * 1024)
                {
                    try { process.Kill(entireProcessTree:true); } catch (InvalidOperationException) { }
                    throw new FormatException("Git output exceeds the registry proof limit.");
                }
                text.Append(buffer, 0, count);
            }
        }
        var output = ReadBounded(process.StandardOutput); var error = ReadBounded(process.StandardError);
        try { await Task.WhenAll(output, error, process.WaitForExitAsync(token)); }
        catch { try { process.Kill(entireProcessTree:true); } catch (InvalidOperationException) { } throw; }
        var previous = await output; _ = await error;
        if (process.ExitCode != 0) return false;
        return Compare(previous, await File.ReadAllTextAsync(path, token), owners);
    }

    internal static bool Compare(string committed, string current, IEnumerable<NativeModuleFileGroup> owners)
    {
        try
        {
            Dictionary<string, JsonObject> Parse(string text)
            {
                var node = JsonNode.Parse(text.TrimStart('\uFEFF'));
                var list = node as JsonArray ?? node?["$values"] as JsonArray ?? throw new FormatException();
                var result = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
                foreach (var entry in list)
                    if (entry is not JsonObject value || value["ModuleName"]?.GetValue<string>() is not { Length: > 0 } name || !result.TryAdd(name, value)) throw new FormatException();
                return result;
            }
            string[] Removed(JsonNode? value) => value is null ? [] : value is JsonArray items
                ? items.Select(item => item?.GetValue<string>() ?? throw new FormatException()).ToArray() : throw new FormatException();
            var before = Parse(committed); var after = Parse(current);
            var selected = owners.ToArray();
            if (before.Count != after.Count || selected.GroupBy(owner => owner.NativeModuleName, StringComparer.Ordinal).Any(group => group.Count() != 1)) return false;
            foreach (var (name, old) in before)
            {
                if (!after.TryGetValue(name, out var now)) return false;
                if (JsonNode.DeepEquals(old, now)) continue;
                var owner = selected.SingleOrDefault(item => item.NativeModuleName == name);
                if (owner is null) return false;
                var oldCopy = old.DeepClone().AsObject(); var newCopy = now.DeepClone().AsObject();
                var a = Removed(oldCopy["ToRemoveFiles"]); var b = Removed(newCopy["ToRemoveFiles"]);
                oldCopy.Remove("ToRemoveFiles"); newCopy.Remove("ToRemoveFiles");
                if (!JsonNode.DeepEquals(oldCopy, newCopy) || a.Distinct(StringComparer.Ordinal).Count() != a.Length || b.Distinct(StringComparer.Ordinal).Count() != b.Length) return false;
                var allowed = owner.Paths.ToHashSet(StringComparer.Ordinal);
                if (a.Except(b, StringComparer.Ordinal).Concat(b.Except(a, StringComparer.Ordinal)).Any(file => !allowed.Contains(file))) return false;
            }
            return true;
        }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or InvalidOperationException) { return false; }
    }
}
