using System.IO;
using System.IO.Compression;

namespace ForguncyDesignerMcp.Adapters.V11;

// A grouped folder move may only touch exactly the descendants represented by
// the native models and their complete resolved file groups. Unknown cache,
// staging or source entries must prevent recursive filesystem mutation.
internal static class NativeV11FolderTreeEvidence
{
    internal static bool Matches(string local, string staging, string source, string root,
        IReadOnlyCollection<string> directories, IReadOnlyCollection<string> files, bool requireLocal = true)
    {
        root = Canonical(root);
        var expectedDirectories = directories.Select(Canonical).ToArray();
        var expectedFiles = files.Select(Canonical).ToArray();
        if (expectedDirectories.Length == 0 || !expectedDirectories.Contains(root, StringComparer.Ordinal) ||
            expectedDirectories.Concat(expectedFiles).Any(path => path != root && !path.StartsWith(root + "/", StringComparison.Ordinal)) ||
            expectedDirectories.Concat(expectedFiles).Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedDirectories.Length + expectedFiles.Length)
            return false;
        var dirs = expectedDirectories.ToHashSet(StringComparer.Ordinal);
        var content = expectedFiles.ToHashSet(StringComparer.Ordinal);
        foreach (var path in dirs.Concat(content).Where(path => path != root))
        {
            var parent = path[..path.LastIndexOf('/')];
            if (!dirs.Contains(parent)) return false;
        }
        return Disk(local, root, dirs, content, allowSubset: !requireLocal) && Disk(staging, root, dirs, content) &&
            (Directory.Exists(source) ? Disk(source, root, dirs, content) : Archive(source, root, dirs, content));
    }

    private static string Canonical(string path)
    {
        var value = path.Replace('\\', '/').TrimEnd('/');
        if (string.IsNullOrEmpty(value) || Path.IsPathRooted(value) || value.Contains(':') ||
            value.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or ".."))
            throw new InvalidDataException("A native folder subtree path is not a safe relative path.");
        return value;
    }

    private static bool Disk(string basePath, string root, HashSet<string> expectedDirs, HashSet<string> expectedFiles, bool allowSubset = false)
    {
        var rootPath = V11Safety.Under(basePath, root);
        if (!Directory.Exists(rootPath)) return allowSubset && !File.Exists(rootPath);
        var directories = new HashSet<string>(StringComparer.Ordinal); var files = new HashSet<string>(StringComparer.Ordinal);
        bool Visit(string path, string relative)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !directories.Add(relative)) return false;
            foreach (var child in Directory.EnumerateFileSystemEntries(path))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) return false;
                var item = relative + "/" + Path.GetFileName(child);
                if (Directory.Exists(child)) { if (!Visit(child, item)) return false; }
                else if (!File.Exists(child) || !files.Add(item)) return false;
            }
            return true;
        }
        return Visit(rootPath, root) && (allowSubset
            ? directories.IsSubsetOf(expectedDirs) && files.IsSubsetOf(expectedFiles)
            : directories.SetEquals(expectedDirs) && files.SetEquals(expectedFiles));
    }

    private static bool Archive(string source, string root, HashSet<string> expectedDirs, HashSet<string> expectedFiles)
    {
        using var archive = ZipFile.OpenRead(source);
        var dirs = new HashSet<string>(StringComparer.Ordinal); var files = new HashSet<string>(StringComparer.Ordinal);
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!entries.Add(path) || !path.StartsWith(root + "/", StringComparison.Ordinal)) return false;
            var directory = path.EndsWith('/'); var trimmed = path.TrimEnd('/');
            if (directory) dirs.Add(trimmed); else if (!files.Add(trimmed)) return false;
            var parent = directory ? trimmed : trimmed[..trimmed.LastIndexOf('/')];
            while (parent.Length >= root.Length)
            {
                dirs.Add(parent);
                if (parent == root) break;
                var split = parent.LastIndexOf('/'); if (split < 0) return false;
                parent = parent[..split];
            }
        }
        return dirs.SetEquals(expectedDirs) && files.SetEquals(expectedFiles);
    }
}

