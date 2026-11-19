using System.Security.Cryptography;
using System.Text;

namespace ForguncyDesignerMcp.Adapters.V11;

// A normal native save may touch other modules too. Protect the entire saved
// project baseline, so an external editor's unrelated changes are not absorbed.
internal static class NativeV11SourceRevision
{
    private static readonly HashSet<string> LocalFolders = new(StringComparer.OrdinalIgnoreCase)
        { ".git", ".local", "LocalData", "LastSaveFiles" };

    internal static string Capture(string project)
    {
        if (File.Exists(project)) return HashFile(project);
        if (!Directory.Exists(project)) throw new DirectoryNotFoundException("The native V11 project source is unavailable.");
        var files = new List<(string Relative, string Full)>();
        var directories = new List<string>();
        void Visit(string path, string prefix)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Project source revisions cannot traverse directory links.");
            if (prefix.Length != 0) directories.Add(prefix);
            foreach (var file in Directory.EnumerateFiles(path)) files.Add((prefix + Path.GetFileName(file), file));
            foreach (var directory in Directory.EnumerateDirectories(path))
                if (prefix.Length != 0 || !LocalFolders.Contains(Path.GetFileName(directory))) Visit(directory, prefix + Path.GetFileName(directory) + "/");
        }
        Visit(project, "");
        using var revision = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var directory in directories.Order(StringComparer.Ordinal))
            revision.AppendData(Encoding.UTF8.GetBytes("directory\0" + directory + "\n"));
        foreach (var file in files.OrderBy(file => file.Relative, StringComparer.Ordinal))
        {
            revision.AppendData(Encoding.UTF8.GetBytes(file.Relative + "\0" + HashFile(file.Full) + "\n"));
        }
        return Convert.ToHexString(revision.GetHashAndReset());
    }

    private static string HashFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Project source revisions cannot traverse file links.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(file));
    }
}
