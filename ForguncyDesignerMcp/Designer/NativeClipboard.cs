using System.Runtime.InteropServices;
using System.Text;

namespace ForguncyDesignerMcp.Designer;

internal enum ClipboardWriteStatus
{
    Copied,
    CannotOpen,
    Failed
}

internal static class NativeClipboard
{
    private const uint MoveableMemory = 0x0002;
    private const uint UnicodeText = 13;
    private const int InvalidWindowHandle = 1400;

    internal static ClipboardWriteStatus WriteText(IntPtr owner, string value, out int errorCode)
    {
        errorCode = 0;
        if (owner == IntPtr.Zero)
        {
            errorCode = InvalidWindowHandle;
            return ClipboardWriteStatus.Failed;
        }

        byte[] bytes = Encoding.Unicode.GetBytes(value + '\0');
        IntPtr data = GlobalAlloc(MoveableMemory, (nuint)bytes.Length);
        if (data == IntPtr.Zero)
        {
            errorCode = Marshal.GetLastWin32Error();
            return ClipboardWriteStatus.Failed;
        }

        try
        {
            IntPtr buffer = GlobalLock(data);
            if (buffer == IntPtr.Zero)
            {
                errorCode = Marshal.GetLastWin32Error();
                return ClipboardWriteStatus.Failed;
            }

            try { Marshal.Copy(bytes, 0, buffer, bytes.Length); }
            finally { GlobalUnlock(data); }

            if (!OpenClipboard(owner))
            {
                errorCode = Marshal.GetLastWin32Error();
                return ClipboardWriteStatus.CannotOpen;
            }

            try
            {
                if (!EmptyClipboard() || SetClipboardData(UnicodeText, data) == IntPtr.Zero)
                {
                    errorCode = Marshal.GetLastWin32Error();
                    return ClipboardWriteStatus.Failed;
                }

                data = IntPtr.Zero;
                return ClipboardWriteStatus.Copied;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (data != IntPtr.Zero) GlobalFree(data);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
}
