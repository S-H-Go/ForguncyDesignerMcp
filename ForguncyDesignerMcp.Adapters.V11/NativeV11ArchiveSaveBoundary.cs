using System.IO.Compression;

namespace ForguncyDesignerMcp.Adapters.V11;

internal static class NativeV11ArchiveSaveBoundary
{
    // Both pinned CommonUtilities builds skip a same-length ZIP member when its
    // native file and ZIP timestamps differ by <= 2 seconds. Let the next native
    // save stamp new LastPath files beyond that window; never alter their times,
    // edit the ZIP, or weaken source verification.
    internal static async Task WaitAsync(string project, IEnumerable<string> affectedPaths)
    {
        if (Directory.Exists(project) || !File.Exists(project)) return;
        var paths = affectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.Now;
        var delay = TimeSpan.Zero;
        using (var source = File.OpenRead(project))
        using (var archive = new ZipArchive(source, ZipArchiveMode.Read))
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || paths.Count != 0 && !paths.Contains(entry.FullName.Replace('\\', '/'))) continue;
                var timestamp = entry.LastWriteTime.DateTime;
                if (Math.Abs((now - timestamp).TotalSeconds) > 2.0) continue;
                var remaining = timestamp.AddSeconds(2.2) - now;
                if (remaining > delay) delay = remaining;
            }
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
    }
}
