using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Designer.Native;

// Source access is read-only. All persistence is performed by the document's native save.
internal static class NativeReportFiles
{
    internal sealed record Pair(byte[] Json, byte[] Rdlx)
    {
        internal string JsonText => Decode(Json);
        internal string RdlxText => Decode(Rdlx);
        internal string JsonHash => Hash(Json);
        internal string RdlxHash => Hash(Rdlx);
    }

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    internal static string Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        return reader.ReadToEnd();
    }

    internal static string Under(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\0'))
            throw new BridgeException("InvalidReportPath", "报表文件路径无效。");
        var normalized = relative.Replace('\\', '/');
        if (!normalized.StartsWith("Reports/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new BridgeException("InvalidReportPath", "报表文件必须属于原生 Reports 目录。");
        var result = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (SessionGuard.RelativeFile(root, result) != normalized)
            throw new BridgeException("InvalidReportPath", "报表文件路径与原生路径不一致。");
        // Refuse links at every existing level, including the project/cache root.
        for (var path = result; path is not null; path = Path.GetDirectoryName(path))
        {
            try
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new BridgeException("ReportPathIsLink", "报表目录含链接，未访问文件。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    internal static Pair ReadSource(string project, string jsonFile, string rdlxFile)
    {
        if (Path.ChangeExtension(jsonFile, ".rdlx") != rdlxFile)
            throw new BridgeException("InvalidReportPair", "报表 JSON 和 RDLX 必须同路径同名。");
        if (Directory.Exists(project))
            return new(File.ReadAllBytes(Under(project, jsonFile)), File.ReadAllBytes(Under(project, rdlxFile)));
        if (!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ReportSourceUnavailable", "报表源工程不是现有协同目录或 FGCC 文件。");
        // Validate entry names using the same relative-path contract as a directory.
        Under(Path.GetDirectoryName(project)!, jsonFile);
        Under(Path.GetDirectoryName(project)!, rdlxFile);
        using var input = File.OpenRead(project);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        byte[] Read(string name)
        {
            var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/') == name).ToArray();
            if (entries.Length != 1 || entries[0].Length > 8 * 1024 * 1024)
                throw new BridgeException("InvalidReportArchive", "报表归档条目缺失、重复或过大。");
            using var stream = entries[0].Open();
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = stream.Read(buffer)) != 0)
            {
                if (output.Length + count > 8 * 1024 * 1024)
                    throw new BridgeException("InvalidReportArchive", "报表归档条目过大。");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        return new(Read(jsonFile), Read(rdlxFile));
    }

    internal static void CheckSource(ReportSnapshot baseline, Pair source)
    {
        if (baseline.JsonFileHash != source.JsonHash || baseline.RdlxFileHash != source.RdlxHash)
            throw new BridgeException("ReportSourceChanged", "报表源文件已变化，请重新导出基线。");
    }
}
