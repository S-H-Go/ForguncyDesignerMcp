using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed class NativeReceivePathLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    internal void AddChain(string path)
    {
        var chain = new Stack<string>();
        for (var p = Path.GetFullPath(path); !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p)) chain.Push(p);
        while (chain.TryPop(out var part))
        {
            if (!Directory.Exists(part)) throw new BridgeException("ReceivePathMissing", "A leased receiver directory is unavailable.");
            var handle = CreateFileW(part, 0x80000000u, 0x1 | 0x2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); throw new BridgeException("ReceivePathLeaseUnavailable", "Cannot protect a receiver path from replacement."); }
            handles.Add(handle);
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("ReceivePathLinked", "A leased receiver path is a link or junction.");
        }
    }
    // Atomically create and open the directory with FILE_CREATE; no window
    // exists between the creation and acquisition of its non-delete-shared handle.
    internal void CreateExclusive(string path)
    {
        var ntPath = @"\??\" + Path.GetFullPath(path);
        var buffer = Marshal.StringToHGlobalUni(ntPath);
        var name = new UnicodeString { Length = checked((ushort)(ntPath.Length * 2)), MaximumLength = checked((ushort)((ntPath.Length + 1) * 2)), Buffer = buffer };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            Marshal.StructureToPtr(name, pointer, false);
            var attributes = new ObjectAttributes { Length = Marshal.SizeOf<ObjectAttributes>(), ObjectName = pointer, Attributes = 0x40 };
            var status = NtCreateFile(out var handle, 0x80100000u, ref attributes, out _, IntPtr.Zero, 0x10, 3, 2, 0x00200021, IntPtr.Zero, 0);
            if (status < 0 || handle.IsInvalid) { handle.Dispose(); throw new BridgeException("ReceiveDestinationAllocationFailed", "The receiver directory could not be exclusively created and leased."); }
            handles.Add(handle);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new BridgeException("ReceivePathLinked", "The created receiver directory is linked.");
        }
        finally { Marshal.FreeHGlobal(pointer); Marshal.FreeHGlobal(buffer); }
    }
    internal void EnsureServerRoot(string root)
    {
        if (!Path.IsPathFullyQualified(root)) throw new BridgeException("ReceiveRootUnavailable", "The server receive root must be absolute.");
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (Directory.Exists(path)) { AddChain(path); return; }
        if (File.Exists(path)) throw new BridgeException("ReceiveRootUnavailable", "The server receive root is not a directory.");
        var parent = Path.GetDirectoryName(path) ?? throw new BridgeException("ReceiveRootUnavailable", "The server receive root has no parent.");
        EnsureServerRoot(parent);
        CreateExclusive(path);
    }
    public void Dispose() { foreach (var handle in handles) handle.Dispose(); handles.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectAttributes { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }
    [DllImport("ntdll.dll")] private static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}

