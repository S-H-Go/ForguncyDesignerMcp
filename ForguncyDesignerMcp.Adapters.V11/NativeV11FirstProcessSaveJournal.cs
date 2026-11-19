using System.IO.Compression;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Captures the actual native owners produced by first-process initialization.
// This is a saved-source journal, not a replacement for collaboration checkout.
internal sealed class NativeV11FirstProcessSaveJournal
{
    private sealed record Entry(NativeV11Item Item, JsonElement Source, CompanionAsset[] Assets);
    private sealed record Folder(string Name, string Type, object Model, string Path);
    private readonly Entry[] entries;
    private readonly NativeV11TemplateSaveSnapshot templates;
    private readonly Folder[] folders;
    private readonly IReadOnlyDictionary<string, byte[]> settings;
    private readonly (string Hash, long Length, bool Valid) database;
    private readonly string appDatabase;
    private readonly (string Hash, long Length, bool Valid) appDatabaseSnapshot;
    private readonly IReadOnlyDictionary<string, string> unrelatedSource;
    private readonly HashSet<string> journalPaths;
    private readonly string sourceRevision;
    private readonly string[] paths;
    internal IReadOnlyList<string> Paths => paths;
    internal IReadOnlyList<string> TemplatePaths => templates.TemplatePaths;

    private NativeV11FirstProcessSaveJournal(Entry[] entries, NativeV11TemplateSaveSnapshot templates, Folder[] folders, IReadOnlyDictionary<string, byte[]> settings,
        (string Hash, long Length, bool Valid) database, string appDatabase, (string Hash, long Length, bool Valid) appDatabaseSnapshot,
        string[] paths, IReadOnlyDictionary<string, string> unrelatedSource, string sourceRevision)
    { this.entries = entries; this.templates = templates; this.folders = folders; this.settings = settings; this.database = database; this.paths = paths;
      this.appDatabase = appDatabase; this.appDatabaseSnapshot = appDatabaseSnapshot;
      this.unrelatedSource = unrelatedSource; this.sourceRevision = sourceRevision; journalPaths = AllowedPaths(paths); }

    internal static NativeV11FirstProcessSaveJournal Capture(NativeV11Session session, NativeV11ProcessInitialization.Candidate candidate)
    {
        session.VerifyContext();
        if (NativeV11SourceRevision.Capture(session.SourcePath) != candidate.SourceRevision)
            throw new BridgeException("ProjectSourceChanged", "流程初始化期间源工程发生变化。 ");
        var factory = session.Context.DesignerAssembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var found = new List<(NativeV11Item Item, string Name, string Type, string? Icon, string? Exclusive)>();
        var kinds = candidate.Owners.Where(x => x.State == "New").Select(x => x.Type switch
        {
            "ProcessItem" => (ModuleKind?)ModuleKind.BusinessProcess,
            "TableProcessView" or "TableView" => ModuleKind.View,
            "Table" or "TableSqlite" => ModuleKind.Table,
            "PCPage" or "MobilePage" => ModuleKind.Page,
            "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage,
            "TableFolder" or "Folder" or "MasterPageFolder" => null,
            _ => throw new InvalidOperationException("Unknown generated first-process owner type: " + x.Type)
        }).Where(x => x is not null).Select(x => x!.Value).Distinct().ToArray();
        foreach (var kind in kinds)
        foreach (var item in session.Items(kind))
        {
            var simple = NativeV11Reflection.Static(factory, "Create", item.Model) ?? throw new InvalidOperationException("Native owner classification failed.");
            var name = NativeV11Reflection.Get(simple, "ModuleName") as string ?? throw new InvalidOperationException("Native owner has no name.");
            var type = NativeV11Reflection.Get(simple, "ModuleType")?.ToString() ?? throw new InvalidOperationException("Native owner has no type.");
            found.Add((item, name, type, NativeV11Reflection.Get(simple, "IconType")?.ToString(),
                NativeV11Reflection.Get(simple, "ExclusiveType")?.ToString()));
        }
        var entries = new List<Entry>();
        var folders = new List<Folder>();
        foreach (var owner in candidate.Owners.Where(x => x.State == "New"))
        {
            if (owner.Type is "TableFolder" or "Folder" or "MasterPageFolder")
            {
                folders.Add(FindFolder(session, owner.Name, owner.Type));
                continue;
            }
            var matches = found.Where(x => x.Name == owner.Name && x.Type == owner.Type &&
                x.Icon == owner.Icon && x.Exclusive == owner.Exclusive).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("A generated native process owner is absent or ambiguous: " + owner.Type + ".");
            // The native cache snapshot owns all generated pages and masters;
            // serializing a fresh page DTO here can regenerate condition IDs.
            if (owner.Type is "PCPage" or "MobilePage" or "MasterPCPage" or "MasterMobilePage") continue;
            entries.Add(CaptureItem(session, matches[0].Item));
        }
        if (entries.Count == 0 || entries.Select(x => x.Item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Count)
            throw new InvalidOperationException("The generated native owner journal is incomplete.");
        var currentSettings = new NativeV11Settings(session);
        var settings = currentSettings.CaptureSaveFiles(ModuleKind.ApplicationSettings)
            .Concat(currentSettings.CaptureSaveFiles(ModuleKind.Theme)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        var templates = NativeV11TemplateSaveSnapshot.Capture(session, candidate);
        var generatedPaths = entries.SelectMany(e => new[] { e.Item.RelativePath }.Concat(e.Assets.Select(a => a.RelativePath)))
            .Concat(templates.Paths)
            .Concat(folders.Select(f => f.Path)).ToArray();
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var localAppDb = NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath") as string
            ?? throw new InvalidOperationException("The native application database is unavailable.");
        var appDatabase = Path.GetFileName(localAppDb);
        if (string.IsNullOrEmpty(appDatabase) || !string.Equals(Path.GetFullPath(localAppDb), V11Safety.Under(session.LocalPath, appDatabase), StringComparison.OrdinalIgnoreCase) ||
            !candidate.NewDirectGroups.Values.SelectMany(group => group).Contains(appDatabase, StringComparer.Ordinal))
            throw new InvalidOperationException("The native first-process application database owner is not established.");
        var paths = generatedPaths.Concat(settings.Keys).Append(NativeV11ProcessInitialization.Database).Append(appDatabase).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (NativeV11PendingSave.ReadFiles(session.SourcePath, generatedPaths).Values.Any(value => value is not null))
            throw new InvalidOperationException("A generated first-process owner already exists in the source project.");
        var localDb = V11Safety.Under(session.LocalPath, NativeV11ProcessInitialization.Database);
        VerifyNativeSqliteOwner(session.SaveFile);
        VerifySqliteOwner(session.SaveFile, appDatabase);
        NativeV11StaticFiles.VerifyConfigOwner(session);
        if (!File.Exists(localDb) || new FileInfo(localDb).Length == 0 ||
            NativeV11PendingSave.ReadFiles(session.SourcePath, [NativeV11ProcessInitialization.Database])[NativeV11ProcessInitialization.Database] is not null)
            throw new InvalidOperationException("The initialized native process database is absent.");
        var database = SnapshotDatabase(localDb, session.Context.DesignerAssembly);
        if (!database.Valid) throw new InvalidOperationException("The initialized native process database snapshot is invalid.");
        var appSnapshot = SnapshotDatabase(localAppDb);
        if (!appSnapshot.Valid) throw new InvalidOperationException("The native application database snapshot is invalid.");
        var unrelated = SourceInventory(session.SourcePath, AllowedPaths(paths));
        session.VerifyContext();
        if (NativeV11SourceRevision.Capture(session.SourcePath) != candidate.SourceRevision)
            throw new BridgeException("ProjectSourceChanged", "流程初始化期间源工程发生变化。 ");
        return new(entries.ToArray(), templates, folders.ToArray(), settings, database, appDatabase, appSnapshot, paths, unrelated, candidate.SourceRevision);
    }

    internal void VerifySourceUnchanged(NativeV11Session session)
    {
        if (NativeV11SourceRevision.Capture(session.SourcePath) != sourceRevision)
            throw new BridgeException("ProjectSourceChanged", "首次流程初始化期间源工程发生变化。 ");
    }

    internal void VerifyMemory(NativeV11Session session)
    {
        templates.VerifyMemory(session);
        VerifyNativeSqliteOwner(session.SaveFile);
        VerifySqliteOwner(session.SaveFile, appDatabase);
        NativeV11StaticFiles.VerifyConfigOwner(session);
        foreach (var entry in entries)
        {
            var matches = session.Items(entry.Item.Address.Kind).Where(item => ReferenceEquals(item.Model, entry.Item.Model) &&
                item.RelativePath.Equals(entry.Item.RelativePath, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1)
                throw new BridgeException("MemoryRevisionChanged", "首次流程初始化产生的原生模块已变化。 ");
            var actualEntry = CaptureItem(session, matches[0]);
            if (!V11Safety.Equivalent(entry.Source, actualEntry.Source) || !SameAssets(entry.Assets, actualEntry.Assets))
                throw new BridgeException("MemoryRevisionChanged", "首次流程初始化产生的页面或配套文件已变化。 ");
        }
        foreach (var folder in folders)
            if (!ReferenceEquals(FindFolder(session, folder.Name, folder.Type).Model, folder.Model))
                throw new BridgeException("MemoryRevisionChanged", "首次流程初始化产生的原生目录已变化。 ");
        var current = new NativeV11Settings(session);
        var actual = current.CaptureSaveFiles(ModuleKind.ApplicationSettings).Concat(current.CaptureSaveFiles(ModuleKind.Theme))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (settings.Count != actual.Count || settings.Any(p => !actual.TryGetValue(p.Key, out var bytes) || !JsonMatches(p.Value, bytes)))
            throw new BridgeException("MemoryRevisionChanged", "首次流程初始化的应用设置或主题已变化。 ");
        if (SnapshotDatabase(V11Safety.Under(session.LocalPath, NativeV11ProcessInitialization.Database), session.Context.DesignerAssembly) != database)
            throw new BridgeException("MemoryRevisionChanged", "首次流程初始化的数据库快照已变化。 ");
        if (SnapshotDatabase(V11Safety.Under(session.LocalPath, appDatabase)) != appDatabaseSnapshot)
            throw new BridgeException("MemoryRevisionChanged", "首次流程初始化的应用数据库快照已变化。 ");
    }

    internal bool SourceMatches(NativeV11Session session)
    {
        VerifyMemory(session);
        if (!templates.SourceMatches(session)) return false;
        var files = entries.SelectMany(e => new[] { e.Item.RelativePath }.Concat(e.Assets.Select(a => a.RelativePath)))
            .Concat(folders.Select(f => f.Path)).Concat(settings.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var source = NativeV11PendingSave.ReadFiles(session.SourcePath, files);
        var stage = NativeV11PendingSave.ReadFiles(session.LastPath, files);
        foreach (var entry in entries)
        {
            var primary = entry.Item.RelativePath;
            if (source[primary] is not byte[] sourceJson || stage[primary] is not byte[] stageJson ||
                !V11Safety.Equivalent(entry.Source, V11Safety.Body(sourceJson)) || !JsonMatches(sourceJson, stageJson)) return false;
            foreach (var asset in entry.Assets)
            {
                if (source[asset.RelativePath] is not byte[] a || stage[asset.RelativePath] is not byte[] b) return false;
                var expected = Convert.FromBase64String(asset.ContentBase64);
                if (asset.RelativePath.EndsWith(".rd", StringComparison.OrdinalIgnoreCase)
                    ? !JsonMatches(expected, a) || !JsonMatches(a, b)
                    : !expected.AsSpan().SequenceEqual(a) || !a.AsSpan().SequenceEqual(b)) return false;
            }
        }
        foreach (var folder in folders)
            if (source[folder.Path] is not byte[] || stage[folder.Path] is not byte[]) return false;
        foreach (var pair in settings)
            if (source[pair.Key] is not byte[] a || stage[pair.Key] is not byte[] b || !JsonMatches(pair.Value, a) || !JsonMatches(a, b)) return false;
        var currentUnrelated = SourceInventory(session.SourcePath, journalPaths);
        if (unrelatedSource.Count != currentUnrelated.Count || unrelatedSource.Any(p => !currentUnrelated.TryGetValue(p.Key, out var hash) || hash != p.Value)) return false;
        if (!ProcessDatabaseMatches(session.SourcePath, session.LastPath)) return false;
        if (!DatabaseMatches(session.SourcePath, session.LastPath, appDatabase)) return false;
        using (var stagedApp = File.OpenRead(V11Safety.Under(session.LastPath, appDatabase)))
            if (NativeV11BuiltInTableLifecycle.DatabaseFingerprint(stagedApp) != appDatabaseSnapshot) return false;
        using var staged = File.OpenRead(V11Safety.Under(session.LastPath, NativeV11ProcessInitialization.Database));
        return NativeV11BuiltInTableLifecycle.DatabaseFingerprint(staged) == database;
    }

    internal static (string Hash, long Length, bool Valid) SnapshotDatabase(string database, System.Reflection.Assembly? designer = null, bool allowLiveWal = false)
    {
        if (!File.Exists(database)) throw new FileNotFoundException("The native process database is unavailable.", database);
        if (!allowLiveWal && !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(database) ||
            allowLiveWal && File.Exists(database + "-journal") && new FileInfo(database + "-journal").Length != 0)
            throw new InvalidDataException("The native database has an active SQLite journal.");
        var sqlite = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Microsoft.Data.Sqlite");
        var connectionType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        string ConnectionString(string path, string mode)
        {
            var builder = Activator.CreateInstance(builderType)!;
            NativeV11Reflection.Set(builder, "DataSource", path); NativeV11Reflection.Set(builder, "Pooling", false);
            var property = builderType.GetProperty("Mode")!; property.SetValue(builder, Enum.Parse(property.PropertyType, mode));
            return builder.ToString()!;
        }
        var temporary = Path.Combine(Path.GetTempPath(), "forguncy-process-proof-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var source = (IDisposable)Activator.CreateInstance(connectionType, [ConnectionString(database, "ReadOnly")])!)
            using (var snapshot = (IDisposable)Activator.CreateInstance(connectionType, [ConnectionString(temporary, "ReadWrite")])!)
            {
                NativeV11Reflection.Call(source, "Open"); NativeV11Reflection.Call(snapshot, "Open");
                NativeV11Reflection.Call(source, "BackupDatabase", snapshot);
                using var command = (IDisposable)NativeV11Reflection.Call(snapshot, "CreateCommand")!;
                NativeV11Reflection.Set(command, "CommandText", "PRAGMA quick_check");
                if (NativeV11Reflection.Call(command, "ExecuteScalar") as string != "ok")
                    throw new InvalidDataException("The native process database failed SQLite quick_check.");
            }
            if (!NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(temporary))
                throw new InvalidDataException("The native process database snapshot retained a journal.");
            if (designer is not null) NativeV11ProcessInitialization.RequireTemplateSchema(designer, temporary);
            using var snapshotFile = File.OpenRead(temporary);
            return NativeV11BuiltInTableLifecycle.DatabaseFingerprint(snapshotFile);
        }
        finally
        {
            foreach (var path in new[] { temporary, temporary + "-wal", temporary + "-shm", temporary + "-journal" })
                if (File.Exists(path)) File.Delete(path);
        }
    }

    internal static bool ProcessDatabaseMatches(string source, string stage) => DatabaseMatches(source, stage, NativeV11ProcessInitialization.Database);

    internal static bool DatabaseMatches(string source, string stage, string relative)
    {
        if (relative != Path.GetFileName(relative) || string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("Invalid native database owner.");
        var staged = V11Safety.Under(stage, relative);
        if (!File.Exists(staged) || new FileInfo(staged).Length == 0 || !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(staged)) return false;
        if (Directory.Exists(source))
        {
            var saved = V11Safety.Under(source, relative);
            if (!File.Exists(saved) || !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(saved)) return false;
            using var a = File.OpenRead(staged); using var b = File.OpenRead(saved);
            return NativeV11BuiltInTableLifecycle.EquivalentDatabase(a, b);
        }
        using var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length != 1 || entries[0].Length == 0 || archive.Entries.Any(e =>
            (e.FullName.Equals(relative + "-wal", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals(relative + "-journal", StringComparison.OrdinalIgnoreCase)) && e.Length != 0)) return false;
        using var stagedFile = File.OpenRead(staged); using var savedFile = entries[0].Open();
        return NativeV11BuiltInTableLifecycle.EquivalentDatabase(stagedFile, savedFile);
    }

    internal static void VerifyNativeSqliteOwner(object saveFile)
        => VerifySqliteOwner(saveFile, NativeV11ProcessInitialization.Database);

    internal static void VerifySqliteOwner(object saveFile, string database)
    {
        if (database != Path.GetFileName(database) || string.IsNullOrWhiteSpace(database)) throw new InvalidDataException("Invalid native SQLite owner.");
        if (NativeV11Reflection.Get(saveFile, "SqliteDBLists") is not IEnumerable registered)
            throw new InvalidOperationException("The native SQLite save registry is unavailable.");
        var entries = registered.Cast<object>().Select(x => x as string ?? throw new InvalidDataException("Unknown native SQLite owner.")).ToArray();
        if (entries.Count(x => x.Equals(database, StringComparison.OrdinalIgnoreCase)) != 1 || !entries.Contains(database, StringComparer.Ordinal))
            throw new InvalidOperationException("The database is not uniquely registered with the native SQLite save path.");
    }

    private static Entry CaptureItem(NativeV11Session session, NativeV11Item item)
    {
        if (!item.Route.IsPage)
        {
            var document = session.Read(item, true);
            return new(item, NativeV11SourceMetadata.Serialize(session.Context.DesignerAssembly, item.Route, item.Model), document.Companions.ToArray());
        }
        var sheets = NativeV11Reflection.Get(session.Workbook, "Sheets") as IEnumerable
            ?? throw new InvalidOperationException("Native page worksheets are unavailable.");
        var matches = sheets.Cast<object>().Where(sheet => NativeV11Reflection.Get(sheet, "Tag") is object tag &&
            ReferenceEquals(NativeV11Reflection.Get(tag, "PageInfo"), item.Model)).ToArray();
        if (matches.Length != 1 || NativeV11Reflection.Get(matches[0], "Loaded") is not true)
            throw new InvalidOperationException("A generated PC/mobile page requires its own loaded native worksheet.");
        var data = NativeV11Reflection.Static(session.Generator, "GetPageSaveData", matches[0])
            ?? throw new InvalidOperationException("The generated page native save data is unavailable.");
        var source = NativeV11SourceMetadata.SerializeSaveData(session.Context.DesignerAssembly, item.Route, data);
        var redundant = NativeV11Reflection.Call(item.Route.Loader, session.Profile.RedundantProjection, data)
            ?? throw new InvalidOperationException("The generated page native redundant projection is unavailable.");
        var bytes = Encoding.UTF8.GetBytes(session.SerializeSaveData(redundant));
        var asset = new CompanionAsset(Path.ChangeExtension(item.RelativePath, ".rd").Replace('\\', '/'),
            Convert.ToBase64String(bytes), Convert.ToHexString(SHA256.HashData(bytes)), "application/json");
        return new(item, source, [asset]);
    }

    private static Folder FindFolder(NativeV11Session session, string nativeName, string nativeType)
    {
        var kind = nativeType switch
        {
            "TableFolder" => ModuleKind.Table, "Folder" => ModuleKind.Page,
            "MasterPageFolder" => ModuleKind.MasterPage, _ => throw new InvalidOperationException("Unknown generated folder type.")
        };
        var route = session.Route(kind);
        var extensions = session.Context.DesignerAssembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var result = new List<Folder>();
        void Visit(object parent, string parentNative, string relative)
        {
            if (!visited.Add(parent) || NativeV11Reflection.Get(parent, "Loaded") is not true || NativeV11Reflection.Get(parent, "Items") is not IEnumerable children)
                throw new InvalidOperationException("A generated folder tree is not fully loaded.");
            foreach (var child in children.Cast<object>())
            {
                if (NativeV11Reflection.Get(child, "Items") is not IList) continue;
                var childName = NativeV11Reflection.Get(child, "Name") as string ?? throw new InvalidOperationException("A native folder has no name.");
                var type = NativeV11Reflection.Static(extensions, "GetModuleType", child)?.ToString();
                var name = NativeV11Reflection.Static(extensions, "GetModuleName", child, parentNative) as string
                    ?? throw new InvalidOperationException("A native folder has no qualified identity.");
                var path = relative + childName + "/";
                if (name == nativeName && type == nativeType) result.Add(new(name, nativeType, child, route.Folder + path));
                Visit(child, name, path);
            }
        }
        var rootName = NativeV11Reflection.Get(route.Root, "Name") as string ?? throw new InvalidOperationException("The native folder root has no name.");
        Visit(route.Root, rootName, "");
        return result.Count == 1 ? result[0] : throw new InvalidOperationException("A generated native folder is absent or ambiguous.");
    }

    private static bool JsonMatches(byte[] left, byte[] right) => V11Safety.Equivalent(V11Safety.Body(left), V11Safety.Body(right));
    private static bool SameAssets(CompanionAsset[] left, CompanionAsset[] right) => left.Length == right.Length &&
        left.All(a => right.Any(b => a.RelativePath == b.RelativePath && a.Sha256 == b.Sha256));

    internal static HashSet<string> AllowedPaths(IEnumerable<string> paths)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var path = raw.Replace('\\', '/'); allowed.Add(path);
            var current = path.TrimEnd('/');
            while (current.LastIndexOf('/') is var index && index >= 0)
            { current = current[..index]; if (current.Length != 0) allowed.Add(current + "/"); }
        }
        return allowed;
    }

    internal static IReadOnlyDictionary<string, string> SourceInventory(string source, IReadOnlySet<string> allowed)
    {
        var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string raw, string value)
        {
            var path = raw.Replace('\\', '/');
            if (allowed.Contains(path)) return;
            if (!all.TryAdd(path, value) && all[path] != value) throw new InvalidDataException("Ambiguous project source path.");
        }
        void Parents(string path)
        {
            var current = path.TrimEnd('/');
            while (current.LastIndexOf('/') is var index && index >= 0)
            { current = current[..index]; if (current.Length != 0) Add(current + "/", "directory"); }
        }
        if (Directory.Exists(source))
        {
            var skip = new HashSet<string>([".git", ".local", "LocalData", "LastSaveFiles"], StringComparer.OrdinalIgnoreCase);
            void Visit(string directory, string prefix)
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Project source contains a directory link.");
                if (prefix.Length != 0) Add(prefix, "directory");
                foreach (var file in Directory.EnumerateFiles(directory))
                {
                    if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Project source contains a file link.");
                    var path = prefix + Path.GetFileName(file);
                    if (allowed.Contains(path)) continue;
                    using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                    Add(path, Convert.ToHexString(SHA256.HashData(input)));
                }
                foreach (var child in Directory.EnumerateDirectories(directory))
                    if (prefix.Length != 0 || !skip.Contains(Path.GetFileName(child))) Visit(child, prefix + Path.GetFileName(child) + "/");
            }
            Visit(source, "");
        }
        else
        {
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (path.Length == 0 || !seen.Add(path)) throw new InvalidDataException("Ambiguous project archive entry.");
                _ = V11Safety.Under(Path.GetDirectoryName(Path.GetFullPath(source))!, path.TrimEnd('/'));
                Parents(path);
                if (path.EndsWith('/')) { if (entry.Length != 0) throw new InvalidDataException("Invalid native folder marker."); Add(path, "directory"); }
                else if (!allowed.Contains(path)) { using var bytes = entry.Open(); Add(path, Convert.ToHexString(SHA256.HashData(bytes))); }
            }
        }
        return all;
    }
}
