using System.Runtime.InteropServices;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// Caller supplied paths are never themselves an authorization. This helper only
// validates/allocates a fresh parent; native initialization owns its contents.
internal static class NativeReceiveDestination
{
    internal static string Validate(string trustedRoot, string requestedParent, string source, string remote)
    {
        if (!Path.IsPathFullyQualified(trustedRoot) || !Path.IsPathFullyQualified(requestedParent) ||
            !Path.IsPathFullyQualified(source) || !Path.IsPathFullyQualified(remote))
            throw Error("ReceivePathInvalid", "Receiver paths must be absolute.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(trustedRoot));
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedParent));
        var sourcePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        var remotePath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(remote));
        if (!Directory.Exists(root)) throw Error("ReceiveRootUnavailable", "The preconfigured receiver root must already exist.");
        RequireUnlinkedExistingChain(root);
        RequireUnlinkedExistingChain(sourcePath);
        RequireUnlinkedExistingChain(remotePath);
        if (!File.Exists(sourcePath) || !sourcePath.EndsWith(".fgcc", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(remotePath))
            throw Error("ReceiveSourceInvalid", "A local standalone FGCC and existing local bare repository are required.");
        if (!Same(Path.GetDirectoryName(destination), root)) throw Error("ReceiveDestinationOutsideRoot", "Destination must be an immediate child of the trusted receiver root.");
        var name = Path.GetFileName(destination);
        if (!name.StartsWith("receiver-", StringComparison.Ordinal) || !Guid.TryParseExact(name[9..], "N", out _))
            throw Error("ReceiveDestinationInvalid", "Destination must use receiver- followed by a fresh UUID.");
        if (Path.Exists(destination)) throw Error("ReceiveDestinationExists", "The receiver destination already exists.");
        if (Overlaps(destination, sourcePath) || Overlaps(destination, remotePath) || Overlaps(root, sourcePath) || Overlaps(root, remotePath))
            throw Error("ReceivePathOverlap", "The receiver destination/root cannot overlap the source or remote repository.");
        return destination;
    }

    internal static void AllocateExclusive(string destination)
    {
        if (!CreateDirectoryW(destination, IntPtr.Zero))
            throw Error("ReceiveDestinationAllocationFailed", "The new receiver directory could not be exclusively created.");
        RequireUnlinkedExistingChain(destination);
    }

    internal static void RequireUnlinkedExistingChain(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (!Path.Exists(current)) throw Error("ReceivePathMissing", "A required receiver path component is missing.");
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Error("ReceivePathLinked", "Receiver paths cannot traverse links or junctions.");
            current = Path.GetDirectoryName(current);
        }
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Overlaps(string a, string b) => Same(a,b) ||
        a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static BridgeException Error(string code, string message) => new(code, message);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}


