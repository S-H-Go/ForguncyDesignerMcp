using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

// Pure package inspection shared by native version adapters. It never extracts
// content, loads code, reads a machine plugin registry or invokes a lifecycle.
public static class NativePluginLifecyclePackage
{
    private const int MaxPackageBytes = 64 * 1024 * 1024;
    private const long MaxExpandedBytes = 256L * 1024 * 1024;
    private const string OwnPluginGuid = "8b34e735-b71d-48cb-b943-c6eb5a0f1432";
    public sealed record Inspection(string Guid, string Version, string DependenceVersion, string Name,
        string ConfigPath, string Sha256, int EntryCount, long ExpandedBytes);

    // Only call for an explicitly requested lifecycle operation. The reviewed
    // bytes are rechecked immediately before extracting to a fresh private path.
    public static StagedPackage Stage(byte[] package, Inspection reviewed)
    {
        ArgumentNullException.ThrowIfNull(reviewed);
        if (Inspect(package, reviewed.Guid, reviewed.Version, reviewed.Sha256) != reviewed)
            throw Invalid("PluginPackageChanged", "The plugin package changed after review.");
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        var root = Path.Combine(temporaryRoot, "ForguncyDesignerMcp-plugin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var staged = new StagedPackage(temporaryRoot, root, reviewed.ConfigPath);
        try
        {
            using var input = new MemoryStream(package, writable: false);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            long totalWritten = 0;
            foreach (var entry in archive.Entries)
            {
                var relative = Normalize(entry.FullName, out var directory).Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(root, relative));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw Invalid("PluginPackagePathInvalid", "A staged plugin path escaped its private root.");
                if (directory) { Directory.CreateDirectory(destination); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = entry.Open(); using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                var buffer = new byte[64 * 1024]; long written = 0; int count;
                while ((count = source.Read(buffer, 0, buffer.Length)) != 0)
                {
                    written += count; totalWritten += count;
                    if (written > entry.Length || totalWritten > MaxExpandedBytes) throw Invalid("PluginPackageSizeInvalid", "The expanded package exceeds its reviewed size.");
                    output.Write(buffer, 0, count);
                }
                if (written != entry.Length) throw Invalid("PluginPackageSizeInvalid", "An extracted package entry differs from its reviewed size.");
            }
            return staged;
        }
        catch { staged.Dispose(); throw; }
    }

    public sealed class StagedPackage : IDisposable
    {
        private readonly string temporaryRoot;
        public string Root { get; }
        public string ConfigPath { get; }
        internal StagedPackage(string temporaryRoot, string root, string relativeConfig)
        {
            this.temporaryRoot = temporaryRoot; Root = root;
            ConfigPath = Path.GetFullPath(Path.Combine(root, relativeConfig.Replace('/', Path.DirectorySeparatorChar)));
        }
        public void Dispose()
        {
            if (!Directory.Exists(Root)) return;
            var resolved = Path.GetFullPath(Root);
            var prefix = Path.TrimEndingDirectorySeparator(temporaryRoot) + Path.DirectorySeparatorChar + "ForguncyDesignerMcp-plugin-";
            if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0)
                throw Invalid("PluginStageCleanupUnsafe", "The temporary plugin staging path cannot be safely removed.");
            var pending = new Stack<string>(); pending.Push(resolved);
            while (pending.TryPop(out var parent))
                foreach (var directory in Directory.EnumerateDirectories(parent, "*", SearchOption.TopDirectoryOnly))
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        throw Invalid("PluginStageCleanupUnsafe", "The temporary plugin staging path contains a link.");
                    pending.Push(directory);
                }
            Directory.Delete(resolved, recursive: true);
        }
    }

    public static Inspection Inspect(byte[] package, string expectedGuid, string expectedVersion, string expectedSha256)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Length is 0 or > MaxPackageBytes) throw Invalid("PluginPackageSizeInvalid", "The plugin package is empty or exceeds its allowed size.");
        if (!Guid.TryParseExact(expectedGuid, "D", out var guid) || !expectedGuid.Equals(guid.ToString("D"), StringComparison.OrdinalIgnoreCase) || expectedGuid.Equals(OwnPluginGuid, StringComparison.OrdinalIgnoreCase))
            throw Invalid("PluginIdentityInvalid", "Use an exact plugin GUID other than this running MCP plugin.");
        if (!Version.TryParse(expectedVersion, out _) || string.IsNullOrEmpty(expectedSha256) || expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw Invalid("PluginPackageExpectationInvalid", "A valid expected plugin version and package SHA-256 are required.");
        var hash = Convert.ToHexString(SHA256.HashData(package));
        if (!hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase)) throw Invalid("PluginPackageHashMismatch", "The plugin package hash differs from the reviewed hash.");
        using var input = new MemoryStream(package, writable: false);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is 0 or > 10000) throw Invalid("PluginPackageSizeInvalid", "The package has no files or exceeds its entry limit.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var path = Normalize(entry.FullName, out var directory);
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000 || (entry.ExternalAttributes & 0x400) != 0) throw Invalid("PluginPackageLinkInvalid", "Plugin packages may not contain symbolic links or reparse points.");
            if (!entries.TryAdd(path, entry)) throw Invalid("PluginPackageDuplicatePath", "The package contains duplicate paths.");
            if (directory) continue;
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes || entry.Length > MaxExpandedBytes) throw Invalid("PluginPackageSizeInvalid", "The expanded package exceeds its allowed size.");
        }
        var configs = entries.Where(pair => Path.GetFileName(pair.Key).Equals("PluginConfig.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (configs.Length != 1 || configs[0].Value.Length is 0 or > 1024 * 1024)
            throw Invalid("PluginPackageConfigInvalid", "The package must contain exactly one bounded PluginConfig.json.");
        using var configStream = configs[0].Value.Open();
        using var config = JsonDocument.Parse(configStream, new JsonDocumentOptions { MaxDepth = 64 });
        if (config.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("PluginPackageConfigInvalid", "The native plugin configuration must be an object.");
        var properties = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in config.RootElement.EnumerateObject())
            if (property.Name.StartsWith('$') || !properties.TryAdd(property.Name, property.Value)) throw Invalid("PluginPackageConfigInvalid", "The plugin configuration contains forbidden or duplicate properties.");
        if (properties.ContainsKey("relativePath") || properties.ContainsKey("configFilePath") || properties.ContainsKey("PluginItemStatus"))
            throw Invalid("PluginPackageConfigInvalid", "The plugin configuration cannot set native installation or runtime state.");
        string Required(string name) => properties.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw Invalid("PluginPackageConfigInvalid", "The plugin configuration is missing a required identity field.");
        var configGuid = Required("guid"); var version = Required("version"); var dependency = Required("dependenceVersion"); var name = Required("name");
        foreach (var nameKey in new[] { "name", "name_cn", "name_ja", "name_kr" })
        {
            if (!properties.TryGetValue(nameKey, out var localized) || localized.ValueKind == JsonValueKind.Null) continue;
            if (nameKey != "name" && localized.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(localized.GetString())) continue;
            if (localized.ValueKind != JsonValueKind.String || localized.GetString() is not { } label || label.Length > 190 ||
                string.IsNullOrWhiteSpace(label) || label != label.Trim() || label.EndsWith('.') || label.Any(char.IsControl) ||
                label.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0 || ReservedName(label))
                throw Invalid("PluginPackageConfigInvalid", "A plugin display name is unsafe for the native installation folder.");
        }
        if (!Guid.TryParseExact(configGuid, "D", out var actualGuid) || actualGuid != guid || version != expectedVersion || !Version.TryParse(version, out _) || !Version.TryParse(dependency, out _))
            throw Invalid("PluginPackageIdentityMismatch", "The plugin configuration GUID or version differs from the reviewed package identity.");
        var slash = configs[0].Key.LastIndexOf('/'); var prefix = slash < 0 ? "" : configs[0].Key[..(slash + 1)];
        foreach (var key in new[] { "assembly", "serverApiAssembly", "css", "javascript" })
        {
            if (!properties.TryGetValue(key, out var files)) continue;
            if (files.ValueKind != JsonValueKind.Array) throw Invalid("PluginPackageConfigInvalid", "A native plugin file list is invalid.");
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(file.GetString())) throw Invalid("PluginPackageConfigInvalid", "A native plugin file path is invalid.");
                var relative = Normalize(file.GetString()!, out var directory);
                if (directory || !entries.TryGetValue(prefix + relative, out var found) || found.FullName.EndsWith('/') || found.FullName.EndsWith('\\'))
                    throw Invalid("PluginPackageReferenceMissing", "A plugin configuration file reference is missing from the package.");
            }
        }
        if (properties.TryGetValue("image", out var image) && image.ValueKind != JsonValueKind.Null)
        {
            if (image.ValueKind != JsonValueKind.String) throw Invalid("PluginPackageConfigInvalid", "The native plugin image path is invalid.");
            if (!string.IsNullOrEmpty(image.GetString()))
            {
                var relative = Normalize(image.GetString()!, out var directory);
                if (directory || !entries.TryGetValue(prefix + relative, out var found) || found.FullName.EndsWith('/') || found.FullName.EndsWith('\\'))
                    throw Invalid("PluginPackageReferenceMissing", "The native plugin image is missing from the package.");
            }
        }
        return new(expectedGuid, version, dependency, name, configs[0].Key, hash, entries.Count, expanded);
    }

    private static string Normalize(string path, out bool directory)
    {
        var value = path.Replace('\\', '/'); directory = value.EndsWith('/');
        if (directory) value = value[..^1];
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains(':') || value.Any(char.IsControl) ||
            value.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || ReservedName(part)))
            throw Invalid("PluginPackagePathInvalid", "The package contains an unsafe file path.");
        return value;
    }
    private static bool ReservedName(string part)
    {
        var stem = part.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
            stem[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
    private static BridgeException Invalid(string code, string message) => new(code, message);
}
