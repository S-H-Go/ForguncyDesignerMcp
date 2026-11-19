using System.IO;
using System.IO.Compression;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V11;

// Frozen streaming proof for an existing shared SQLite file that a native
// definition-only operation must not modify. No database API is invoked.
internal sealed class NativeV11UnchangedDatabaseProof
{
    private readonly (string Hash, long Length, bool Valid) expected;
    internal string Path { get; }

    private NativeV11UnchangedDatabaseProof(string path, (string Hash, long Length, bool Valid) expected)
    { Path = path; this.expected = expected; }

    internal static NativeV11UnchangedDatabaseProof Capture(NativeV11Session session, string path)
    {
        session.VerifyContext();
        var proof = new NativeV11UnchangedDatabaseProof(path, ReadFingerprint(session.SourcePath, path));
        proof.Verify(session);
        return proof;
    }

    internal void Verify(NativeV11Session session)
    {
        session.VerifyContext();
        foreach (var root in new[] { session.SourcePath, session.LocalPath, session.LastPath })
            if (ReadFingerprint(root, Path) != expected)
                throw new BridgeException("SourceRevisionChanged", "共享数据库在定义操作期间发生变化。 ");
        session.VerifyContext();
    }

    internal static (string Hash, long Length, bool Valid) ReadFingerprint(string root, string path)
    {
        if (File.Exists(root) && System.IO.Path.GetExtension(root).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(root);
            var matches = archive.Entries.Where(entry => entry.FullName.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) throw new BridgeException("NativeDatabaseUnavailable", "共享数据库归档条目不存在或不唯一。");
            using (var headerStream = matches[0].Open())
            {
                Span<byte> archiveHeader = stackalloc byte[16];
                if (headerStream.Read(archiveHeader) != archiveHeader.Length || !archiveHeader.SequenceEqual("SQLite format 3\0"u8))
                    throw new BridgeException("NativeDatabaseUnavailable", "共享数据库不是有效的 SQLite 文件。 ");
            }
            using var archiveStream = matches[0].Open();
            var archiveFingerprint = NativeV11BuiltInTableLifecycle.DatabaseFingerprint(archiveStream);
            if (!archiveFingerprint.Valid) throw new BridgeException("NativeDatabaseUnavailable", "共享数据库格式无效。 ");
            return archiveFingerprint;
        }
        var file = NativeV11Session.Under(root, path);
        if (!File.Exists(file) || !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(file))
            throw new BridgeException("NativeDatabaseUnavailable", "共享数据库不存在或存在未合并的日志。 ");
        using var stream = File.OpenRead(file);
        Span<byte> header = stackalloc byte[16];
        if (stream.Read(header) != header.Length || !header.SequenceEqual("SQLite format 3\0"u8))
            throw new BridgeException("NativeDatabaseUnavailable", "共享数据库不是有效的 SQLite 文件。 ");
        stream.Position = 0;
        var fingerprint = NativeV11BuiltInTableLifecycle.DatabaseFingerprint(stream);
        if (!fingerprint.Valid) throw new BridgeException("NativeDatabaseUnavailable", "共享数据库格式无效。 ");
        return fingerprint;
    }
}
