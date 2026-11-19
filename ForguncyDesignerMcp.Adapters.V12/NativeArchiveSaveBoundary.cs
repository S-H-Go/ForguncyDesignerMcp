using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// 12.0.102.0 CompressHelper.UpdateZipFile compares staged-file timestamps with
// DOS-granularity ZIP timestamps and skips same-length files within two seconds.
// Wait BEFORE a genuine native model/cache write, then perform that write and
// native save. Waiting only after an old cache write cannot change its timestamp.
// This helper never edits source, cache, archive entries or timestamps.
public static class NativeArchiveSaveBoundary
{
    private static readonly long WindowTicks = TimeSpan.FromMilliseconds(2200).Ticks;
    public static async Task<TimeSpan> WaitAsync(string projectPath, IEnumerable<string>? relativePaths,
        Func<Task> revalidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revalidate);
        cancellationToken.ThrowIfCancellationRequested();
        await revalidate();
        if (Directory.Exists(projectPath)) return TimeSpan.Zero;
        if (!File.Exists(projectPath) || !Path.GetExtension(projectPath).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ArchiveSourceUnavailable", "The bound native archive source is unavailable.");
        if ((File.GetAttributes(projectPath) & FileAttributes.ReparsePoint) != 0)
            throw new BridgeException("ArchiveSourceIsLink", "Archive save boundaries cannot traverse a source link.");
        var paths = relativePaths?.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var delay = Remaining(projectPath, paths);
            if (delay <= TimeSpan.Zero) break;
            await revalidate();
            await Task.Delay(delay, cancellationToken);
            await revalidate();
        }
        await revalidate();
        return elapsed.Elapsed;
    }

    private static TimeSpan Remaining(string archivePath, string[]? paths)
    {
        using var stream = File.OpenRead(archivePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => !entry.FullName.Replace('\\', '/').EndsWith('/') &&
            (paths is null || paths.Length == 0 || paths.Any(path => path.EndsWith('/') ?
                entry.FullName.Replace('\\', '/').StartsWith(path, StringComparison.OrdinalIgnoreCase) :
                entry.FullName.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase)))).ToArray();
        if (entries.GroupBy(x => x.FullName.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase).Any(x => x.Count() != 1))
            throw new BridgeException("AmbiguousArchiveEntries", "Native archive entries contain a case alias or duplicate.");
        if (paths is { Length: > 0 } && entries.Any(entry => !paths.Any(path => path.EndsWith('/') ?
                entry.FullName.Replace('\\', '/').StartsWith(path, StringComparison.Ordinal) : entry.FullName.Replace('\\', '/').Equals(path, StringComparison.Ordinal))))
            throw new BridgeException("ArchiveEntryCaseAlias", "Native archive entry case differs from its registered path.");
        var now = DateTime.Now.Ticks; // Match native FileInfo.LastWriteTime / ZIP wall-clock ticks.
        var maximum = 0L;
        foreach (var entry in entries)
        {
            var timestamp = entry.LastWriteTime.Ticks;
            var difference = now - timestamp;
            if (difference >= -WindowTicks && difference <= WindowTicks)
                maximum = Math.Max(maximum, timestamp + WindowTicks - now);
        }
        return TimeSpan.FromTicks(maximum);
    }
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new BridgeException("InvalidArchiveEntryPath", "Archive paths must be native relative file groups.");
        var result = path.Replace('\\', '/');
        var body = result.EndsWith('/') ? result[..^1] : result;
        if (body.Contains(':') || body.IndexOf('\0') >= 0 || body.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith(' ') || x.EndsWith('.')))
            throw new BridgeException("InvalidArchiveEntryPath", "Archive paths must be native relative file groups.");
        return result;
    }
}
