using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ForguncyDesignerMcp.Designer;

internal static class TaskbarIcon
{
    private static readonly int[] FrameSizes = [16, 20, 24, 32, 40, 48, 64];

    internal static int? ReadPixelSize()
    {
        // Shell_TrayWnd owns Explorer's notification area. Reacquire the HWND after Explorer restarts.
        var taskbar = FindWindowW("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;
        try
        {
            var dpi = GetDpiForWindow(taskbar);
            if (dpi == 0) return null;
            const int smallIconWidth = 49; // SM_CXSMICON
            int pixels = GetSystemMetricsForDpi(smallIconWidth, dpi);
            return pixels > 0 ? pixels : null;
        }
        catch (EntryPointNotFoundException)
        {
            return SystemInformation.SmallIconSize.Width;
        }
    }

    internal static void Update(NotifyIcon tray, int? pixels)
    {
        var previous = tray.Icon;
        int target = pixels ?? previous?.Width ?? 16;
        // For an intermediate scale, let the shell downsample the next larger authored frame.
        int size = FrameSizes.FirstOrDefault(frame => frame >= target, FrameSizes[^1]);
        if (previous?.Width == size) return;

        using var stream = typeof(TaskbarIcon).Assembly.GetManifestResourceStream(typeof(TaskbarIcon), "Tray.ico")
            ?? throw new InvalidDataException("The embedded tray icon is missing.");
        var replacement = new Icon(stream, size, size);
        try { tray.Icon = replacement; }
        catch { replacement.Dispose(); throw; }
        previous?.Dispose();
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);
}
