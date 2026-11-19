using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// Read-only source verification. Cache writes remain owned by native SaveLoadFile.
public static class NativeFiles
{
    public const int MaximumAssetBytes = 8 * 1024 * 1024;
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));
    public static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    public static string UnderReports(string root, string relative) => Under(root, relative, "Reports");
    public static string UnderProcess(string root, string relative) => Under(root, relative, "Process");
    private static string Under(string root, string relative, string prefix)
    {
        var normalized = relative.Replace('\\', '/');
        if (!normalized.StartsWith(prefix + "/", StringComparison.Ordinal) || normalized.Contains(':') ||
            normalized.IndexOf('\0') >= 0 || normalized.Split('/').Any(x => x is "" or "." or ".." ||
                x.EndsWith(' ') || x.EndsWith('.') || x.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0))
            throw new BridgeException("InvalidReportPath", "Only native report-relative paths are accepted.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var result = Path.GetFullPath(Path.Combine(fullRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidReportPath", "Report path escapes its native root.");
        for (var path = result; path is not null; path = Path.GetDirectoryName(path))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("ReportPathIsLink", "Report paths cannot contain filesystem links.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    public static byte[]? ReadSource(string project, string relative)
    {
        string SafePath(string root) => relative.StartsWith("Process/", StringComparison.Ordinal) ? UnderProcess(root, relative) : UnderReports(root, relative);
        SafePath(Directory.Exists(project) ? project : Path.GetDirectoryName(Path.GetFullPath(project))!);
        if (Directory.Exists(project))
        {
            var path = SafePath(project);
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaximumAssetBytes)
                throw new BridgeException("ReportTooLarge", "Report source exceeds the supported asset size.");
            return File.ReadAllBytes(path);
        }
        if (!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ReportSourceUnavailable", "The source must be a collaboration directory or FGCC archive.");
        using var stream = File.OpenRead(project);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var matches = archive.Entries.Where(x => string.Equals(x.FullName.Replace('\\', '/'), relative, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].FullName.Replace('\\', '/') != relative || matches[0].Length > MaximumAssetBytes)
            throw new BridgeException("InvalidReportArchive", "A report archive entry is duplicate or too large.");
        using var entry = matches[0].Open();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = entry.Read(buffer)) != 0)
        {
            if (output.Length + count > MaximumAssetBytes) throw new BridgeException("ReportTooLarge", "Report exceeds the supported asset size.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }

    public static string SourceHash(string project, string relative) =>
        ReadSource(project, relative) is { } bytes ? Hash(bytes) : "absent";
}
