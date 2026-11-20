using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// Exact native application SQLite owner for first-process initialization. The
// live DB is never opened writable; backups and integrity checks use a private
// scratch file, and source/staging are compared as complete streams.
internal sealed class NativeProcessAppDatabase
{
    private readonly object save; private readonly string local,stage,source;
    private readonly DatabaseFingerprint? before;
    private readonly Dictionary<string,IReadOnlyDictionary<string,string>> ownerFiles=new(StringComparer.Ordinal);
    internal IReadOnlyDictionary<string,IReadOnlyDictionary<string,string>> OwnerFiles=>ownerFiles;
    internal void RegisterOwnerFiles(string owner,IReadOnlyDictionary<string,string> files)
    { if(!ownerFiles.TryAdd(owner,files)) throw Denied("Duplicate shared application database owner files."); }
    internal string Name { get; }
    internal bool HasExisting=>before is not null;
    internal sealed record DatabaseFingerprint(string Hash,long Length);
    private NativeProcessAppDatabase(object save,string local,string stage,string source,string name,DatabaseFingerprint? before)
    {this.save=save;this.local=local;this.stage=stage;this.source=source;Name=name;this.before=before;}
    internal static NativeProcessAppDatabase Capture(object save,string local,string stage,string source)
    {
        var config=Assembly.Load("CommonUtilities").GetType("CommonUtilities.SystemConfigDef",true)!;
        var field=config.GetField("ForguncyDB_sqlite3_File",R.Flags) ?? throw Denied("Native application SQLite path is unavailable.");
        var name=(field.IsLiteral?field.GetRawConstantValue():field.GetValue(null)) as string;
        if(name!="ForguncyDB.sqlite3") throw Denied("Native application SQLite path differs from the pinned contract.");
        static bool Same(string first,string second)=>string.Equals(Path.GetFullPath(first).TrimEnd('\\','/'),Path.GetFullPath(second).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase);
        if(!Same((string)R.Get(save,"LocalPath")!,local) || !Same((string)R.Get(save,"LastPath")!,stage) || !Same((string)R.Call(save,"GetDocumentPath")!,source))
            throw Denied("The application database proof is not bound to the active native save session.");
        var nativeConfig=save.GetType().Assembly.GetType("Forguncy.Common.Config",true)!;
        var configured=R.Static(nativeConfig,"get_LocalAppDBSqliteFilePath") as string;
        if(string.IsNullOrWhiteSpace(configured) || !Same(configured,NativeSaveVerification.Under(local,name)))
            throw Denied("The native application database action and save cache paths do not match.");
        var proof=new NativeProcessAppDatabase(save,local,stage,source,name,null); proof.RequireRegistry();
        var existing=proof.CaptureBaseline();
        return new(save,local,stage,source,name,existing);
    }
    internal void RequireEmptyTableSchema()
    {
        if (before is null || !BeforeCurrent()) throw Denied("The independently verified native SQLite baseline is missing or changed.");
        var schema = ReadTableSchema(null);
        if (schema.Objects.Count != 0) throw Denied("The verified application SQLite schema is not empty.");
        if (!BeforeCurrent()) throw Denied("The application SQLite baseline changed during schema inspection.");
    }

    internal bool StageAndSourceBeforeCurrent()
    {
        try
        {
            RequireRegistry();
            RejectSidecars(stage, Name); RejectSourceSidecars(source, Name);
            var path = NativeSaveVerification.Under(stage, Name);
            using var saved = OpenSource(source, Name);
            if (saved is null || !File.Exists(path)) return false;
            using var staged = File.OpenRead(path);
            return Fingerprint(staged) == before && Fingerprint(saved) == before;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { return false; }
    }

    internal (IReadOnlyList<(string Kind, string Name, string Owner)> Objects, IReadOnlyList<(string Name, string Type)> Columns, bool EmptyRows) ReadTableSchema(string? table)
    {
        RequireRegistry();
        var path = NativeSaveVerification.Under(local, Name);
        RejectSidecars(local, Name);
        var result = InspectReadOnlyTableSchema(path, table);
        RejectSidecars(local, Name);
        return result;
    }

    internal static (IReadOnlyList<(string Kind, string Name, string Owner)> Objects, IReadOnlyList<(string Name, string Type)> Columns, bool EmptyRows)
        InspectReadOnlyTableSchema(string path, string? table)
    {
        using var db = AccountAuthoritySnapshot.OpenSqlite(path, true);
        using var query = db.CreateCommand();
        query.CommandText = "PRAGMA query_only=ON";
        query.ExecuteNonQuery();
        query.CommandText = "PRAGMA quick_check";
        if (query.ExecuteScalar() as string != "ok") throw Denied("Application SQLite integrity check failed.");
        query.CommandText = "SELECT type,name,tbl_name FROM sqlite_master ORDER BY type,name";
        var objects = new List<(string, string, string)>();
        using (var cursor = query.ExecuteReader())
            while (cursor.Read()) objects.Add((cursor.GetString(0), cursor.GetString(1), cursor.GetString(2)));
        var columns = new List<(string, string)>();
        var emptyRows = false;
        if (table is not null)
        {
            query.CommandText = "SELECT name,type FROM pragma_table_xinfo(@name) ORDER BY cid";
            var param = query.CreateParameter(); param.ParameterName = "@name"; param.Value = table; query.Parameters.Add(param);
            using (var cursor = query.ExecuteReader())
                while (cursor.Read()) columns.Add((cursor.GetString(0), cursor.GetString(1)));
            query.Parameters.Clear();
            // Identifier can never close its quoted identifier even if the
            // strongly restricted public planner changes in the future.
            query.CommandText = "SELECT NOT EXISTS(SELECT 1 FROM \"" + table.Replace("\"", "\"\"") + "\" LIMIT 1)";
            emptyRows = Convert.ToInt64(query.ExecuteScalar()) == 1;
        }
        return (objects, columns, emptyRows);
    }

    internal bool SameBaseline(NativeProcessAppDatabase other)=>Name==other.Name && Equals(before,other.before) && ownerFiles.Count==other.ownerFiles.Count &&
        ownerFiles.All(pair=>other.ownerFiles.TryGetValue(pair.Key,out var otherFiles) && pair.Value.Count==otherFiles.Count &&
            pair.Value.All(file=>otherFiles.TryGetValue(file.Key,out var hash) && hash==file.Value));
    internal bool BeforeCurrent()
    {
        try {RequireRegistry(); return Equals(CaptureBaseline(),before);} catch(Exception error) when(error is not OutOfMemoryException and not StackOverflowException) {return false;}
    }
    internal DatabaseFingerprint CaptureAfter()
    {
        RequireRegistry(); var path=NativeSaveVerification.Under(local,Name); RejectSidecars(local,Name);
        if(!File.Exists(path)) throw Denied("Native application database is missing after first initialization.");
        return Snapshot(path);
    }
    internal bool VerifyAfter(DatabaseFingerprint expected)
    {
        try
        {
            RequireRegistry(); RejectSidecars(local,Name); RejectSidecars(stage,Name); RejectSourceSidecars(source,Name);
            var path=NativeSaveVerification.Under(local,Name); var staged=NativeSaveVerification.Under(stage,Name);
            if(!File.Exists(path) || !File.Exists(staged) || Snapshot(path)!=expected) return false;
            using var first=File.OpenRead(staged); var stagedFingerprint=Fingerprint(first);
            using var saved=OpenSource(source,Name);
            if(saved is null || stagedFingerprint!=expected || Fingerprint(saved)!=expected) return false;
            RejectSidecars(local,Name); RejectSidecars(stage,Name); RejectSourceSidecars(source,Name); return true;
        }
        catch(Exception error) when(error is not OutOfMemoryException and not StackOverflowException) {return false;}
    }
    private DatabaseFingerprint? CaptureBaseline()
    {
        RejectSidecars(local,Name); RejectSidecars(stage,Name); RejectSourceSidecars(source,Name);
        var live=NativeSaveVerification.Under(local,Name); var staged=NativeSaveVerification.Under(stage,Name);
        if(Directory.Exists(live) || Directory.Exists(staged)) throw Denied("A directory collides with the native application database path.");
        var hasLocal=File.Exists(live); var hasStage=File.Exists(staged);
        using var saved=OpenSource(source,Name); var hasSource=saved is not null;
        if(!hasLocal && !hasStage && !hasSource) return null;
        if(!hasLocal || !hasStage || !hasSource) throw Denied("Native application database cache, staging and source presence differ.");
        var localFingerprint=Snapshot(live); using var stageStream=File.OpenRead(staged);
        if(Fingerprint(stageStream)!=localFingerprint || Fingerprint(saved!)!=localFingerprint) throw Denied("Native application database cache, staging and source differ.");
        RejectSidecars(local,Name); RejectSidecars(stage,Name); RejectSourceSidecars(source,Name);
        return localFingerprint;
    }
    private void RequireRegistry()
    {
        if(R.Get(save,"SqliteDBLists") is not IEnumerable registered) throw Denied("Native SQLite save registry is unavailable.");
        var names=registered.Cast<object>().Select(value=>value as string ?? throw Denied("Unknown native SQLite save registration.")).ToArray();
        if(names.Count(value=>value.Equals(Name,StringComparison.OrdinalIgnoreCase))!=1 || !names.Contains(Name,StringComparer.Ordinal))
            throw Denied("The application database is not uniquely registered with the native SQLite saver.");
    }
    private static void RejectSidecars(string root,string name)
    {
        foreach(var suffix in new[]{"-wal","-journal","-shm"})
        {var file=NativeSaveVerification.Under(root,name+suffix); if(Directory.Exists(file) || File.Exists(file) && new FileInfo(file).Length>0) throw Denied("A native application database sidecar is active or collides with a directory.");}
    }
    private static void RejectSourceSidecars(string source,string name)
    {
        foreach(var suffix in new[]{"-wal","-journal","-shm"})
        {using var sidecar=OpenSource(source,name+suffix); if(sidecar is not null && sidecar.ReadByte()!=-1) throw Denied("A saved application database sidecar is active.");}
    }
    private static Stream? OpenSource(string source,string name)
    {
        if(Directory.Exists(source)) {var path=NativeSaveVerification.Under(source,name); if(Directory.Exists(path)) throw Denied("A source directory collides with a native database file."); return File.Exists(path)?File.OpenRead(path):null;}
        using var input=File.OpenRead(source); using var archive=new ZipArchive(input,ZipArchiveMode.Read);
        if(archive.Entries.Any(entry=>entry.FullName.Replace('\\','/').StartsWith(name+"/",StringComparison.OrdinalIgnoreCase)))
            throw Denied("An archived directory collides with a native database file.");
        var entries=archive.Entries.Where(entry=>entry.FullName.Replace('\\','/').Equals(name,StringComparison.OrdinalIgnoreCase)).ToArray();
        if(entries.Length==0) return null;
        if(entries.Length!=1 || entries[0].FullName.Replace('\\','/')!=name) throw Denied("The application database source entry is ambiguous.");
        // The archive must remain open while streamed; return a private temp-backed
        // stream rather than buffering potentially large project databases.
        var temporary=Path.Combine(Path.GetTempPath(),"forguncy-appdb-source-"+Guid.NewGuid().ToString("N")+".tmp");
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) using(var entry=entries[0].Open()) entry.CopyTo(output);
            return new FileStream(temporary,FileMode.Open,FileAccess.Read,FileShare.Read,4096,FileOptions.DeleteOnClose);
        }
        catch {try {File.Delete(temporary);} catch {} throw;}
    }
    private static DatabaseFingerprint Snapshot(string path)
    {
        var temporary=Path.Combine(Path.GetTempPath(),"forguncy-appdb-snapshot-"+Guid.NewGuid().ToString("N")+".sqlite3");
        try
        {
            using(new FileStream(temporary,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None)) { }
            using(var input=AccountAuthoritySnapshot.OpenSqlite(path,true)) using(var output=AccountAuthoritySnapshot.OpenSqlite(temporary,false))
            {
                R.Call(input,"BackupDatabase",output); using var command=output.CreateCommand(); command.CommandText="PRAGMA quick_check";
                if(command.ExecuteScalar() as string!="ok") throw Denied("Native application SQLite integrity check failed.");
            }
            RejectSidecars(Path.GetDirectoryName(temporary)!,Path.GetFileName(temporary));
            using var snapshot=File.OpenRead(temporary); var fingerprint=Fingerprint(snapshot);
            RejectSidecars(Path.GetDirectoryName(path)!,Path.GetFileName(path)); return fingerprint;
        }
        finally {foreach(var suffix in new[]{"","-wal","-journal","-shm"}) try {File.Delete(temporary+suffix);} catch {}}
    }
    private static DatabaseFingerprint Fingerprint(Stream input)
    {
        var header=new byte[100]; var count=0;
        while(count<header.Length) {var read=input.Read(header,count,header.Length-count); if(read==0) break; count+=read;}
        if(count!=100 || !header.AsSpan(0,16).SequenceEqual("SQLite format 3\0"u8) || !header.AsSpan(24,4).SequenceEqual(header.AsSpan(92,4)))
            throw Denied("A native application database snapshot is not a complete SQLite file.");
        header.AsSpan(24,4).Clear(); header.AsSpan(40,4).Clear(); header.AsSpan(92,4).Clear();
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(header);
        var buffer=new byte[65536]; long length=100; int n; while((n=input.Read(buffer,0,buffer.Length))>0) {hash.AppendData(buffer.AsSpan(0,n)); length+=n;}
        return new(Convert.ToHexString(hash.GetHashAndReset()),length);
    }
    private static BridgeException Denied(string message)=>new("NativeProcessLifecyclePlanUnavailable",message);
}
