using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    internal NativePluginCheckinFiles CapturePluginForCheckin()
    {
        var registry = pluginRegistry?.Invoke() ?? throw new BridgeException("NativePluginContextMissing", "The bound project plugin registry is required for check-in.");
        var source = (string)R.Call(save, "GetDocumentPath")!;
        var manager = assembly.GetType("Forguncy.Common.CollaborationManager", true)!.GetField("_instance", R.Flags)!.GetValue(null);
        if (manager is null || R.Get(manager, "InCollaborationMode") is not true ||
            R.Get(manager, "Collaboration") is not { } provider || R.Get(provider, "WorkingDirectory") is not string directory ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)).Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("CollaborationProjectMismatch", "Plugin registration must belong to the bound collaboration working directory.");
        var owner = ((IDictionary)R.Get(manager, "Modules")!)["Plugin"];
        if (owner is null || R.Get(owner, "ModuleType")?.ToString() != "Plugin" || R.Get(owner, "ModuleName") as string != "Plugin" ||
            R.Get(owner, "State")?.ToString() != "Edit" || R.Get(owner, "LockedBy") is not { } locked || R.Get(manager, "CurrentUser") is not { } user ||
            !Equals(R.Get(locked, "UserName"), R.Get(user, "UserName")) || !Equals(R.Get(locked, "Email"), R.Get(user, "Email")))
            throw new BridgeException("NativeOwnedLockRequired", "Check-in requires the current user's registered Plugin edit lock.");
        var extensions = assembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        var files = (IEnumerable)R.Static(extensions, "GetReferFilesFromWorkingDirectory", owner)!;
        return NativePluginCheckinFiles.Capture(registry, owner, source, (string)R.Get(save, "LastPath")!,
            files.Cast<string>(), ((IEnumerable?)R.Get(owner, "ToRemoveFiles"))?.Cast<string>() ?? []);
    }

    private NativeModuleFileGroup PluginForCheckin()
    {
        var frozen = CapturePluginForCheckin();
        return new(NativePluginCheckinFiles.Address, "Plugin", frozen.Hashes.Keys.ToArray(), frozen.Removed, [], new Dictionary<string,string>(),
            () =>
            {
                try { return frozen.Matches(CapturePluginForCheckin(), exactBytes: true); }
                catch (Exception error) when (error is BridgeException or IOException or InvalidDataException or JsonException) { return false; }
            }, (path, bytes) => frozen.Hashes.TryGetValue(path, out var hash) && hash == Convert.ToHexString(SHA256.HashData(bytes)), frozen.Owner);
    }
}

internal sealed class NativePluginCheckinFiles
{
    internal static readonly ModuleAddress Address = new(ModuleKind.Plugin, "Current");
    internal required object Owner { get; init; }
    internal required SortedDictionary<string,string> Hashes { get; init; }
    internal required string[] Removed { get; init; }
    private string Contents { get; init; } = "";
    internal bool Matches(NativePluginCheckinFiles next, bool exactBytes) => ReferenceEquals(Owner,next.Owner) &&
        Contents == next.Contents && Removed.SequenceEqual(next.Removed) && (!exactBytes || Hashes.SequenceEqual(next.Hashes));

    internal static NativePluginCheckinFiles Capture(NativePlugins registry, object owner, string source, string last, IEnumerable<string> current, IEnumerable<string> historical)
    {
        var folder = registry.ProjectPluginFolderName;
        bool IsPackage(string path) => path.StartsWith(folder + "/", StringComparison.Ordinal) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
            !path[(folder.Length + 1)..].Contains('/');
        string[] Paths(IEnumerable<string> values)
        {
            var paths = values.Select(path => NativeSaveVerification.Relative(source, path)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            foreach (var path in paths)
                if (!IsPackage(path) && path != registry.ManifestName) throw Error("Unexpected plugin owner path: " + path);
            return paths;
        }
        var paths = Paths(current);
        var history = Paths(historical);
        var union = paths.Concat(history).Distinct(StringComparer.Ordinal).ToArray();
        if (union.Distinct(StringComparer.OrdinalIgnoreCase).Count() != union.Length) throw Error("Plugin registration contains conflicting path casing.");
        var removed = history.Except(paths, StringComparer.Ordinal).ToArray();
        foreach (var path in removed)
        {
            var file = NativeSaveVerification.Under(source, path);
            if (File.Exists(file) || Directory.Exists(file)) throw Error("Historical plugin path still exists: " + path);
        }

        // The native check-in helper reads unpacked LastSaveFiles as well as the
        // working ZIPs. An extra staged package could be introduced by that helper.
        var pluginRoot = NativeSaveVerification.Under(last, folder);
        var registered = paths.Where(IsPackage).ToHashSet(StringComparer.Ordinal);
        if (Directory.Exists(pluginRoot))
            foreach (var item in Directory.EnumerateFileSystemEntries(pluginRoot))
            {
                var name = Path.GetFileName(item);
                var staged = NativeSaveVerification.Under(pluginRoot, name);
                var package = Directory.Exists(staged) ? folder + "/" + name + ".zip" : folder + "/" + name;
                if ((Directory.Exists(staged) || IsPackage(package)) && !registered.Contains(package))
                    throw Error("LastSaveFiles contains an unregistered plugin: " + name);
            }

        var live = registry.Capture();
        string[] Records(IEnumerable<JsonElement> declarations) => declarations.Select(x => x.GetRawText()).Order(StringComparer.Ordinal).ToArray();
        var records = Records(live);
        foreach (var root in new[] { source, last }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (File.Exists(NativeSaveVerification.Under(root, registry.ManifestName)) &&
                !records.SequenceEqual(Records(registry.ReadSavedManifest(root).EnumerateArray())))
                throw Error("Saved plugin declarations differ from the current project registry: " + registry.ManifestName);

        var hashes = new SortedDictionary<string,string>(StringComparer.Ordinal);
        var content = new List<string>(records);
        var matched = new HashSet<int>();
        foreach (var path in paths)
        {
            var file = NativeSaveVerification.Under(source, path);
            if (!File.Exists(file)) throw Error("Missing saved plugin file: " + path);
            using var stream = File.OpenRead(file);
            hashes.Add(path, Convert.ToHexString(SHA256.HashData(stream)));
            content.Add(path);
            if (!IsPackage(path)) continue;
            stream.Position = 0;
            var stem = Path.GetFileNameWithoutExtension(file);
            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                using var config = ReadConfig(archive, path);
                if (config.RootElement.ValueKind != JsonValueKind.Object)
                    throw Error("PluginConfig.json must contain a plugin declaration: " + path);
                var assemblyName = registry.PackageAssemblyName(config.RootElement);
                int[] owners = Enumerable.Range(0,live.Length).Where(i => MatchesDeclaration(config.RootElement, live[i], stem) &&
                    Field(live[i], "assemblyName") == assemblyName &&
                    StringComparer.OrdinalIgnoreCase.Equals(Field(live[i], "pluginType"), Field(config.RootElement, "pluginType"))).ToArray();
                if (owners.Length == 0 || owners.Any(i => !matched.Add(i))) throw Error("The saved package has no unique matching project declaration: " + path);
                var entries = EntryHashes(archive, path);
                VerifyStaged(last, folder, path, stem, entries, hashes[path]);
                content.Add(JsonSerializer.Serialize(entries));
            }
            catch (Exception error) when (error is InvalidDataException or JsonException)
            { throw Error("Invalid saved plugin ZIP or PluginConfig.json: " + path); }
        }
        for (var i = 0; i < live.Length; i++)
            if (!matched.Contains(i)) throw Error("No saved ZIP for project plugin: " + Field(live[i], "relativePath") + " " + Field(live[i], "name"));
        return new() { Owner = owner, Hashes = hashes, Removed = removed, Contents = Wire.Hash(JsonSerializer.Serialize(content)) };
    }

    private static string? Field(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool MatchesDeclaration(JsonElement config, JsonElement declaration, string stem)
    {
        var relative = Field(declaration, "relativePath");
        if (!string.IsNullOrEmpty(relative) && !StringComparer.OrdinalIgnoreCase.Equals(relative, stem)) return false;
        if (Field(config, "version") != Field(declaration, "version")) return false;
        var guid = Field(declaration, "guid");
        if (!string.IsNullOrWhiteSpace(guid)) return StringComparer.OrdinalIgnoreCase.Equals(guid, Field(config, "guid"));
        var name = Field(declaration, "name");
        return !string.IsNullOrEmpty(name) && string.IsNullOrEmpty(Field(config, "guid")) &&
            (Field(config, "name") == name || Field(config, "name_cn") == name || Field(config, "name_en") == name);
    }

    private static JsonDocument ReadConfig(ZipArchive zip, string path)
    {
        var configs = zip.Entries.Where(e => StringComparer.OrdinalIgnoreCase.Equals(e.FullName, "PluginConfig.json")).ToArray();
        if (configs.Length != 1 || configs[0].Length > 4 * 1024 * 1024) throw Error("Missing or ambiguous PluginConfig.json: " + path);
        using var reader = new StreamReader(configs[0].Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
    }

    private static SortedDictionary<string,string> EntryHashes(ZipArchive zip, string path)
    {
        var hashes = new SortedDictionary<string,string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            if (name.Contains(':') || name.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) ||
                !names.Add(name)) throw Error("Ambiguous package entry in " + path + ": " + name);
            using var data = entry.Open();
            hashes.Add(name, Convert.ToHexString(SHA256.HashData(data)));
        }
        return hashes;
    }

    private static void VerifyStaged(string last, string folder, string path, string stem, SortedDictionary<string,string> expected, string rawHash)
    {
        var staged = NativeSaveVerification.Under(last, path);
        var extracted = NativeSaveVerification.Under(last, folder + "/" + stem);
        if (File.Exists(staged))
        {
            using var input = File.OpenRead(staged);
            if (rawHash != Convert.ToHexString(SHA256.HashData(input)))
            {
                input.Position = 0;
                using var zip = new ZipArchive(input, ZipArchiveMode.Read);
                if (!expected.SequenceEqual(EntryHashes(zip, path))) throw Error("Staged package differs from the saved ZIP: " + path);
            }
        }
        else if (!Directory.Exists(extracted)) throw Error("Missing saved plugin contents in LastSaveFiles: " + path);
        if (!Directory.Exists(extracted)) return;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Inspect(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var relative = Path.GetRelativePath(extracted, entry).Replace('\\', '/');
                var file = NativeSaveVerification.Under(extracted, relative);
                if (Directory.Exists(file)) { Inspect(file); continue; }
                using var input = File.OpenRead(file);
                if (!expected.TryGetValue(relative, out var hash) || hash != Convert.ToHexString(SHA256.HashData(input)))
                    throw Error("Saved ZIP differs from LastSaveFiles at " + path + "/" + relative);
                seen.Add(relative);
            }
        }
        Inspect(extracted);
        if (seen.Count != expected.Count) throw Error("The saved plugin directory is incomplete: " + path);
    }

    private static BridgeException Error(string message) => new("PluginSourceProjectionUnverified", message);
}
