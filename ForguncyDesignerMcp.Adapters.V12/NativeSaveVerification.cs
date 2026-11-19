using System.Collections;
using System.IO.Compression;
using System.Security.Cryptography;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Reads native staging and source only. The document's SaveAsync performs every source write.
internal sealed class NativeSaveVerification(AdapterHostContext context)
{
    internal object SaveFile
    {
        get
        {
            var colleagues = NativeReflection.Get(context.NativeDocument, "ForguncyColleagueManager")!;
            var report = NativeReflection.Get(colleagues, "ReportModel")!;
            var mediator = NativeReflection.Get(report, "Mediator")!;
            var loader = NativeReflection.Get(mediator, "SaveDataColleague")!;
            return NativeReflection.Get(loader, "SaveLoadFile")!;
        }
    }

    internal IReadOnlyList<string> NativePendingFiles()
    {
        var managerType = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        var manager = managerType.GetProperty("Instance", NativeReflection.Flags)!.GetValue(null)!;
        var root = context.CaptureSnapshot().Target.ProjectPath;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (NativeReflection.Get(manager, "SaveLoadTrackedFiles") is IDictionary tracking)
            foreach (DictionaryEntry item in tracking)
                if (item.Value is IEnumerable paths)
                    foreach (string path in paths)
                        files.Add(Relative(root, path));
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal IReadOnlyList<string> NativeStagedFiles()
    {
        var staging = Path.GetFullPath((string)NativeReflection.Get(SaveFile, "LastPath")!);
        if (!Directory.Exists(staging)) return [];
        var files = new List<string>();
        void Visit(string directory)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("NativePathIsLink", "Native staging folder contains a link.");
            foreach (var file in Directory.EnumerateFiles(directory))
                files.Add(Relative(staging, file));
            foreach (var child in Directory.EnumerateDirectories(directory)) Visit(child);
        }
        Visit(staging);
        return files.Order(StringComparer.Ordinal).ToArray();
    }

    internal bool VerifyFiles(IReadOnlyList<string> nativeFiles)
    {
        if (nativeFiles.Count == 0) return false;
        var source = context.CaptureSnapshot().Target.ProjectPath;
        var staging = (string)NativeReflection.Get(SaveFile, "LastPath")!;
        foreach (var file in nativeFiles)
        {
            var stagedFile = Under(staging, file);
            var sourceBytes = ReadSource(source, file);
            if (!File.Exists(stagedFile))
            {
                if (sourceBytes is not null) return false;
            }
            else
            {
                using var input = File.OpenRead(stagedFile);
                if (sourceBytes is null || !SHA256.HashData(input).AsSpan().SequenceEqual(SHA256.HashData(sourceBytes))) return false;
            }
        }
        return true;
    }

    internal static string Relative(string project, string nativePath)
    {
        var path = Path.IsPathFullyQualified(nativePath) ? Path.GetRelativePath(project, nativePath) : nativePath;
        var normalized = path.Replace('\\', '/');
        Validate(normalized);
        return normalized;
    }

    internal static string Under(string root, string relative)
    {
        Validate(relative);
        var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidNativePath", "Native file path is outside its root.");
        for (var item = result; item is not null; item = Path.GetDirectoryName(item))
        {
            try { if ((File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("NativePathIsLink", "Native file path contains a link."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return result;
    }

    private static void Validate(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains(':') || relative.Contains('\\') ||
            relative.Contains('\0') || relative.Split('/').Any(p => p is "" or "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new BridgeException("InvalidNativePath", "Native file path must be a canonical relative project path.");
    }

    internal static byte[]? ReadSource(string project, string relative)
    {
        Validate(relative);
        if (Directory.Exists(project))
        {
            var path = Under(project, relative);
            return File.Exists(path) ? Limited(File.OpenRead(path)) : null;
        }
        if (!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ProjectSourceUnavailable", "Bound source is neither a collaboration directory nor FGCC file.");
        using var input = File.OpenRead(project);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var matches = archive.Entries.Where(e => e.FullName.Replace('\\','/').Equals(relative,StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1 || matches[0].FullName.Replace('\\','/') != relative || matches[0].Length > 64 * 1024 * 1024)
            throw new BridgeException("InvalidSourceArchive", "Native source entry is duplicated or exceeds verification limits.");
        return Limited(matches[0].Open());
    }

    private static byte[] Limited(Stream input)
    {
        using(input)
        using(var output = new MemoryStream())
        {
            var buffer = new byte[8192]; int count;
            while((count=input.Read(buffer))!=0)
            {
                if(output.Length+count>64*1024*1024) throw new BridgeException("SourceFileTooLarge","Native source entry exceeds verification limits.");
                output.Write(buffer,0,count);
            }
            return output.ToArray();
        }
    }
}
