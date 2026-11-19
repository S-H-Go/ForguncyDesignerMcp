using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// This is deliberately an in-process, fixed-native-child observer, not a process
// inspector exposed through JSON. It never starts, stops, injects or suspends a
// service. An unreadable observation means unavailable, never a guessed binding.
internal sealed class NativeAccountProcessObservation
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private const string Site = "UserService Web Site";
    private readonly AdapterHostContext context;
    internal NativeAccountProcessObservation(AdapterHostContext context) => this.context = context;

    internal NativeObservedAccountService Capture()
    {
        if (!OperatingSystem.IsWindows()) throw Unavailable();
        var assembly = context.DesignerAssembly;
        var singleton = NativeReflection.Static(assembly.GetType("s", true)!, "get_Instance")!;
        if (!ReferenceEquals(NativeReflection.Get(singleton, "ew"), context.NativeDocument))
            throw new BridgeException("AccountDocumentChanged", "The account service belongs to a different current native document.");
        var settings = NativeReflection.Get(context.NativeDocument, "ForguncySettings")!;
        var application = NativeReflection.Get(settings, "ApplicationSettings")!;
        var port = Convert.ToInt32(NativeReflection.Get(application, "UserServicePort"));
        if (port is < 1 or > 65535 || Convert.ToInt32(NativeReflection.Get(application, "AuthenticationMode")) != 3) throw Unavailable();
        var utilities = Assembly.Load("CommonUtilities");
        var system = utilities.GetType("CommonUtilities.SystemConfigDef", true)!;
        var balancing = utilities.GetType("CommonUtilities.Configuration.LoadBalancingConfigurationManager", true)!;
        var builtIn = Assembly.Load("ForguncyCoreUtilities").GetType("CommonUtilities.BuiltInDataBase.UserServiceBuiltInDbConfig", true)!;
        if (StaticProperty(system, "RunAtLocal") is not true || StaticProperty(system, "UseMountStorage") is not false ||
            StaticProperty(balancing, "UseLoadBalancing") is not false || StaticProperty(balancing, "EnableLoadBalancing") is not false ||
            StaticProperty(builtIn, "BuiltInDbIsSqlite") is not true) throw Unavailable();
        // fs.ajs only performs TryGetValue. In particular, do not use the lazy
        // service client, an HTTP probe or an alive checker on this passive path.
        var child = NativeReflection.Static(assembly.GetType("fs", true)!, "ajs", Site) as Process;
        if (child is null || child.HasExited) throw Unavailable();
        var pid = child.Id; var generation = child.StartTime.ToUniversalTime().Ticks;
        var launch = child.StartInfo;
        var expectedExe = Path.GetFullPath(Path.Combine(context.DesignerRoot, "WebSite", "bin", "ForguncyUserServiceConsole.exe"));
        // DesignerRoot is normally the installation root. Some hosts pass its
        // designerBin folder; derive the fixed sibling from the verified DLL.
        var installedExe = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(assembly.Location)!, "..", "bin", "ForguncyUserServiceConsole.exe"));
        if (!File.Exists(expectedExe)) expectedExe = installedExe;
        if (!SamePath(launch.FileName, expectedExe) || !SamePath(expectedExe, installedExe)) throw Unavailable();
        using var process = OpenProcess(0x1000 | 0x0400 | 0x0040, false, pid);
        if (process.IsInvalid) throw Unavailable();
        if (!GetProcessTimes(process, out var created, out _, out _, out _) ||
            DateTime.FromFileTimeUtc(((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime).Ticks != generation) throw Unavailable();
        var actualImage = new StringBuilder(32768); var imageLength = actualImage.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, actualImage, ref imageLength) || !SamePath(actualImage.ToString(), expectedExe)) throw Unavailable();
        var parent = ParentId(process);
        if (parent != Environment.ProcessId || launch.Environment["ParentProcessId"] != parent.ToString(System.Globalization.CultureInfo.InvariantCulture)) throw Unavailable();
        var processUser = ProcessUser(process);
        using var ownProcess = OpenProcess(0x1000, false, Environment.ProcessId);
        if (ownProcess.IsInvalid || processUser != ProcessUser(ownProcess)) throw Unavailable();
        var actualArguments = CommandLine(process);
        var nativeArguments = SplitArguments("native.exe " + launch.Arguments);
        if (actualArguments.Length < 3 || nativeArguments.Length != actualArguments.Length ||
            !SamePath(actualArguments[0], expectedExe) || !nativeArguments.Skip(1).SequenceEqual(actualArguments.Skip(1), StringComparer.Ordinal)) throw Unavailable();
        if (!Uri.TryCreate(actualArguments[1], UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" ||
            !endpoint.IsLoopback || endpoint.Host != "localhost" || endpoint.Port != port || endpoint.AbsolutePath.TrimEnd('/') != "/Forguncy" ||
            endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) throw Unavailable();
        var root = CanonicalExisting(actualArguments[2], true);
        var expectedRoot = CanonicalExisting((string)StaticProperty(assembly.GetType("aa", true)!, "UserServicePath")!, true);
        if (!SamePath(root, expectedRoot)) throw Unavailable();
        var database = CanonicalExisting(Path.Combine(root, "App_Data", "v5", "UserServiceDB.sqlite3"), false);
        using var databaseFile = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var databaseIdentity = FileIdentity(databaseFile.SafeFileHandle.DangerousGetHandle());
        if (!OwnsLoopbackListener(pid, port)) throw Unavailable();
        var openedDatabases = OpenAccountDatabaseFiles(process, pid);
        if (!openedDatabases.Any(file => SamePath(file.Path, database) && file.Identity == databaseIdentity) ||
            openedDatabases.Any(file => SamePath(file.Path, database) && file.Identity != databaseIdentity) ||
            openedDatabases.Any(file => !SamePath(file.Path, database) && !SamePath(file.Path, database + "-wal") && !SamePath(file.Path, database + "-shm"))) throw Unavailable();
        // Actual disk handles bind the SQLite-only case to this child. An open
        // fallback/configuration database or merely a computed DB filename is
        // insufficient: require the exact native account filename and later the
        // complete verified V12 account schema in a read-only transaction.
        var pipe = launch.Environment.TryGetValue("PipeName", out var token) ? token : null;
        var identity = Hash(string.Join("\n", "v12-account-observed-sqlite-v1", pid, generation, parent, endpoint.AbsoluteUri.TrimEnd('/'),
            root.ToUpperInvariant(), database.ToUpperInvariant(), databaseIdentity, "Forms:3;local:true;mount:false;lb:false;sqlite:true", Hash(processUser), pipe is null ? "no-launch-token" : Hash(pipe)));
        child.Refresh();
        if (child.HasExited || child.Id != pid || child.StartTime.ToUniversalTime().Ticks != generation || !OwnsLoopbackListener(pid, port)) throw Unavailable();
        return new(identity, database, pid, generation, parent, endpoint.AbsoluteUri.TrimEnd('/'), root,
            pipe is null ? null : Hash(pipe), openedDatabases.Any(file => SamePath(file.Path, database + "-wal")));
    }

    // Public evidence uses only hashes of these observations. The launch token
    // is supporting provenance, not a claim that a child pipe handshake exists.
    private static object? StaticProperty(Type type, string name) => type.GetProperty(name, Flags)?.GetValue(null) ??
        throw new MissingMemberException(type.FullName, name);
    internal static string CanonicalExisting(string path, bool directory)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        FileSystemInfo item = directory ? new DirectoryInfo(full) : new FileInfo(full);
        if (!item.Exists) throw Unavailable();
        for (FileSystemInfo? current = item; current is not null; current = current is DirectoryInfo d ? d.Parent : ((FileInfo)current).Directory)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw Unavailable();
        return full;
    }
    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static BridgeException Unavailable() => new("AccountServiceBindingUnavailable", "The actual native child generation, loopback listener and effective SQLite account database could not all be verified.");

    private static int ParentId(Microsoft.Win32.SafeHandles.SafeProcessHandle process)
    {
        var data = new IntPtr[6];
        var size = data.Length * IntPtr.Size; var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NtQueryInformationProcess(process, 0, buffer, size, out _) != 0) throw Unavailable();
            Marshal.Copy(buffer, data, 0, data.Length); return checked((int)data[5].ToInt64());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string ProcessUser(Microsoft.Win32.SafeHandles.SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token)) throw Unavailable();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
            return identity.User?.Value ?? throw Unavailable();
    }
    private static string[] CommandLine(Microsoft.Win32.SafeHandles.SafeProcessHandle process)
    {
        _ = NtQueryInformationProcess(process, 60, IntPtr.Zero, 0, out var length);
        if (length is <= 0 or > 1024 * 1024) throw Unavailable();
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (NtQueryInformationProcess(process, 60, buffer, length, out _) != 0) throw Unavailable();
            var chars = (ushort)Marshal.ReadInt16(buffer); var pointer = Marshal.ReadIntPtr(buffer, IntPtr.Size == 8 ? 8 : 4);
            if (chars % 2 != 0 || pointer.ToInt64() < buffer.ToInt64() || pointer.ToInt64() + chars > buffer.ToInt64() + length) throw Unavailable();
            return SplitArguments(Marshal.PtrToStringUni(pointer, chars / 2) ?? throw Unavailable());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string[] SplitArguments(string command)
    {
        var arguments = CommandLineToArgvW(command, out var count);
        if (arguments == IntPtr.Zero || count is < 1 or > 100) throw Unavailable();
        try { return Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, index * IntPtr.Size)) ?? "").ToArray(); }
        finally { _ = LocalFree(arguments); }
    }
    private static bool OwnsLoopbackListener(int pid, int port)
    {
        var found = false;
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            var result = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (result is not (0 or 122) || size is < 4 or > 16 * 1024 * 1024) return false;
            var table = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(table, ref size, false, family, 3, 0) != 0) return false;
                var count = Marshal.ReadInt32(table); var rowSize = family == 2 ? 24 : 56;
                if (count < 0 || 4L + (long)count * rowSize > size) return false;
                for (var index = 0; index < count; index++)
                {
                    var row = IntPtr.Add(table, 4 + index * rowSize); var portOffset = family == 2 ? 8 : 20;
                    if ((Marshal.ReadByte(row, portOffset) << 8 | Marshal.ReadByte(row, portOffset + 1)) != port) continue;
                    var owner = Marshal.ReadInt32(row, family == 2 ? 20 : 52);
                    var address = new byte[family == 2 ? 4 : 16]; Marshal.Copy(IntPtr.Add(row, family == 2 ? 4 : 0), address, 0, address.Length);
                    if (owner != pid || !IPAddress.IsLoopback(new IPAddress(address))) return false;
                    found = true;
                }
            }
            finally { Marshal.FreeHGlobal(table); }
        }
        return found;
    }
    private static IReadOnlyList<ObservedDiskFile> OpenAccountDatabaseFiles(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int pid)
    {
        if (GetProcessId(process) != pid) throw Unavailable();
        try { return OpenAccountDatabaseFilesFromTarget(process); }
        catch (BridgeException error) when (error.Code == "AccountTargetHandleObservationUnavailable")
        { return OpenAccountDatabaseFilesFromSystem(process, pid); }
    }
    // ProcessHandleInformation=51 and the two native layouts are defined by
    // System Informer's primary PHNT header (not a copied DLL/driver):
    // https://raw.githubusercontent.com/winsiderss/phnt/master/ntpsapi.h
    // PROCESS_HANDLE_TABLE_ENTRY_INFO / PROCESS_HANDLE_SNAPSHOT_INFORMATION.
    // Prefer this target-only query so a global system-handle query denial need
    // not suppress observation of this Designer's own already-verified child.
    private static IReadOnlyList<ObservedDiskFile> OpenAccountDatabaseFilesFromTarget(Microsoft.Win32.SafeHandles.SafeProcessHandle process)
    {
        var capacity = 65536; IntPtr buffer = IntPtr.Zero;
        try
        {
            int returned;
            while (true)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(capacity);
                var status = NtQueryInformationProcess(process, 51, buffer, capacity, out returned);
                if (status == 0) break;
                if (status is not (unchecked((int)0xC0000004) or unchecked((int)0xC0000023) or unchecked((int)0x80000005)))
                    throw new BridgeException("AccountTargetHandleObservationUnavailable", "The native child handle-snapshot query is unavailable; no effective account database binding was assumed.");
                if (capacity >= 32 * 1024 * 1024 || returned > 32 * 1024 * 1024 || returned < 0) throw Unavailable();
                capacity = Math.Min(32 * 1024 * 1024, Math.Max(checked(capacity * 2), checked(returned + 4096)));
            }
            var header = 2 * IntPtr.Size; var size = Marshal.SizeOf<ProcessHandleEntry>();
            if (returned < header || returned > capacity) throw Unavailable();
            var count = Marshal.ReadIntPtr(buffer).ToInt64();
            if (count < 0 || count > 500000 || header + count * size > returned) throw Unavailable();
            var files = new Dictionary<string, ObservedDiskFile>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < count; index++)
            {
                var entry = Marshal.PtrToStructure<ProcessHandleEntry>(IntPtr.Add(buffer, checked(header + (int)index * size)));
                var file = InspectDiskHandle(process, entry.HandleValue);
                if (file is not null) files.TryAdd(file.Path + "\n" + file.Identity, file);
            }
            return files.Values.OrderBy(file => file.Path, StringComparer.Ordinal).ThenBy(file => file.Identity, StringComparer.Ordinal).ToArray();
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }
    private static IReadOnlyList<ObservedDiskFile> OpenAccountDatabaseFilesFromSystem(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int pid)
    {
        var capacity = 1024 * 1024; IntPtr buffer = IntPtr.Zero;
        try
        {
            while (true)
            {
                if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
                buffer = Marshal.AllocHGlobal(capacity);
                var status = NtQuerySystemInformation(64, buffer, capacity, out var required);
                if (status == 0) break;
                if (status != unchecked((int)0xC0000004)) throw new BridgeException("AccountHandleObservationUnavailable", "The OS handle ownership query is unavailable (NTSTATUS " + unchecked((uint)status).ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + "); account binding is unavailable.");
                if (required > 256 * 1024 * 1024 || capacity >= 256 * 1024 * 1024) throw Unavailable();
                capacity = Math.Max(checked(capacity * 2), checked(required + 65536));
            }
            var count = Marshal.ReadIntPtr(buffer).ToInt64(); var size = Marshal.SizeOf<SystemHandle>(); var header = 2 * IntPtr.Size;
            if (count < 0 || count > 4000000 || header + count * size > capacity) throw Unavailable();
            var matches = new Dictionary<string, ObservedDiskFile>(StringComparer.OrdinalIgnoreCase);
            for (long index = 0; index < count; index++)
            {
                var row = Marshal.PtrToStructure<SystemHandle>(IntPtr.Add(buffer, checked(header + (int)(index * size))));
                if (row.ProcessId.ToInt64() != pid) continue;
                var file = InspectDiskHandle(process, row.Handle);
                if (file is not null) matches.TryAdd(file.Path + "\n" + file.Identity, file);
            }
            return matches.Values.OrderBy(value => value.Path, StringComparer.Ordinal).ThenBy(value => value.Identity, StringComparer.Ordinal).ToArray();
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }
    private static ObservedDiskFile? InspectDiskHandle(Microsoft.Win32.SafeHandles.SafeProcessHandle process, IntPtr handle)
    {
        if (!DuplicateHandle(process, handle, GetCurrentProcess(), out var duplicate, 0, false, 2)) return null;
        try
        {
            if (GetFileType(duplicate) != 1) return null; // Never query a pipe, socket or named-object path.
            var path = new StringBuilder(32768);
            var length = GetFinalPathNameByHandleW(duplicate, path, path.Capacity, 0);
            if (length == 0 || length >= path.Capacity) return null;
            var text = path.ToString(); if (text.StartsWith("\\\\?\\", StringComparison.Ordinal)) text = text[4..];
            return Path.GetFileName(text) is "UserServiceDB.sqlite3" or "UserServiceDB.sqlite3-wal" or "UserServiceDB.sqlite3-shm" ?
                new(Path.GetFullPath(text), FileIdentity(duplicate)) : null;
        }
        finally { _ = CloseHandle(duplicate); }
    }
    private static string FileIdentity(IntPtr file)
    {
        if (!GetFileInformationByHandle(file, out var information)) throw Unavailable();
        return information.VolumeSerialNumber.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ":" +
            information.FileIndexHigh.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + information.FileIndexLow.ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }
    private sealed record ObservedDiskFile(string Path, string Identity);
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        internal uint Attributes; internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime; internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber; internal uint FileSizeHigh; internal uint FileSizeLow; internal uint NumberOfLinks; internal uint FileIndexHigh; internal uint FileIndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct SystemHandle
    {
        internal IntPtr Object; internal IntPtr ProcessId; internal IntPtr Handle;
        internal uint GrantedAccess; internal ushort CreatorBackTraceIndex; internal ushort ObjectTypeIndex;
        internal uint Attributes; internal uint Reserved;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessHandleEntry
    {
        internal IntPtr HandleValue; internal UIntPtr HandleCount; internal UIntPtr PointerCount;
        internal uint GrantedAccess; internal uint ObjectTypeIndex; internal uint HandleAttributes; internal uint Reserved;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll")] private static extern int GetProcessId(Microsoft.Win32.SafeHandles.SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out System.Runtime.InteropServices.ComTypes.FILETIME created, out System.Runtime.InteropServices.ComTypes.FILETIME exited, out System.Runtime.InteropServices.ComTypes.FILETIME kernel, out System.Runtime.InteropServices.ComTypes.FILETIME user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint flags, StringBuilder name, ref int length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint access, out Microsoft.Win32.SafeHandles.SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(Microsoft.Win32.SafeHandles.SafeProcessHandle source, IntPtr handle, IntPtr target, out IntPtr duplicate, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(IntPtr file, out FileInformation information);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(IntPtr file, StringBuilder path, int length, uint flags);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref int length, bool order, int family, int tableClass, uint reserved);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int informationClass, IntPtr information, int length, out int returnLength);
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass, IntPtr information, int length, out int returnLength);
}

internal sealed record NativeObservedAccountService(string Identity, string DatabasePath, int ProcessId, long StartTicks,
    int ParentId, string Endpoint, string Root, string? NativeLaunchTokenHash, bool WalHandleObserved);
