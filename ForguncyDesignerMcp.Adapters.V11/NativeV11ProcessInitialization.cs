using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Pinned native contracts and read-only first-initialization discovery. The
// actual action is invoked only after the complete owner plan has been approved.
internal static class NativeV11ProcessInitialization
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal const string Database = "ProcessForguncyDB.sqlite3";
    private const string TemplateHash = "6AF6EB83D53AE4B2326E136FF7D2959C1C6C8C82E98CA9FA529B58E05F9C2BFC";
    internal sealed record Contract(MethodInfo Copy, MethodInfo Wait, MethodInfo Initialize, ConstructorInfo Executor, MethodInfo Related, string Template);
    internal sealed record Context(Contract Contract, object Folder, object Manager, string Destination);
    internal sealed record Owner(string Name, string Type, string State, object Native, string? Icon, string? Exclusive);
    internal sealed record Candidate(IReadOnlyList<Owner> Owners, string SourceRevision,
        IReadOnlyDictionary<string, byte[]> ExistingSettings, IReadOnlyList<string> TemplatePaths,
        IReadOnlyDictionary<string, IReadOnlyList<string>> TemplateReferences,
        IReadOnlyDictionary<string, IReadOnlyList<string>> PageDirectGroups,
        IReadOnlyDictionary<string, IReadOnlyList<string>> NewDirectGroups);

    internal static Contract Inspect(Assembly designer)
    {
        var profile = V11BuildProfile.Resolve(designer);
        var bin = Path.GetDirectoryName(designer.Location)!;
        var path = Path.Combine(bin, "ForguncyDataBaseService.dll");
        var expectedHash = profile.Version == "11.0.104.0" ? "14AF70773DC4F08BED9F9789E9A3D571926816DF665C0859D3253A8CF4540E76" : "754788FEAED6D379F77311B7DF5F76064351A9B806CE0C0C2E37A41BECF690FB";
        if (!File.Exists(path) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) != expectedHash)
            throw Error("The pinned native process database component does not match this build.");
        var loadContext = AssemblyLoadContext.GetLoadContext(designer)!;
        var databaseAssembly = loadContext.Assemblies.SingleOrDefault(a => a.GetName().Name == "ForguncyDataBaseService") ?? loadContext.LoadFromAssemblyPath(path);
        if (databaseAssembly.GetName().Version?.ToString() != profile.Version || !string.Equals(Path.GetFullPath(databaseAssembly.Location), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw Error("The native process database component was loaded from another build.");
        var copy = databaseAssembly.GetType("ForguncyDataAccess.DataAccess", true)!.GetMethod("CopySqliteDBIndirect", Flags, null, [typeof(string), typeof(string)], null);
        var wait = designer.GetType("Forguncy.Common.BusinessProcessManager", true)!.GetMethod("WaitLoadComplete", Flags, null, Type.EmptyTypes, null);
        var initialize = designer.GetType("Forguncy.Views.ObjectViews.BusinessProcessFolderViewModel", true)!.GetMethod("CreatProcessTableAndPageAsync", Flags, null, [typeof(string), typeof(Task)], null);
        var executorType = designer.GetType("Forguncy.Common.CreateProcessItemExecutor", true)!;
        var executor = executorType.GetConstructors(Flags).SingleOrDefault(c => c.GetParameters().Length == 4);
        var related = executorType.GetMethod("GetRelatedModules", Flags, null, Type.EmptyTypes, null);
        var website = Directory.GetParent(bin)!.FullName;
        var template = Path.Combine(website, "DesignerResources", "BPMN", "ProcessForguncyDB_Initial.sqlite3");
        if (copy is null || !copy.IsStatic || copy.ReturnType != typeof(void) || wait?.ReturnType != typeof(Task) || initialize?.ReturnType != typeof(Task<bool>) ||
            executor is null || related is null || !typeof(IEnumerable).IsAssignableFrom(related.ReturnType) || !File.Exists(template) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(template))) != TemplateHash)
            throw Error("The pinned native first-process initialization contract or template differs from the reviewed build.");
        return new(copy, wait, initialize, executor, related, template);
    }

    internal static bool IsFirst(NativeV11Session session)
    {
        session.VerifyContext();
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var connection = NativeV11Reflection.Get(settings, "ProcessDataConnectionSet");
        var count = session.Items(ModuleKind.BusinessProcess).Count;
        if (connection is not null && NativeV11Reflection.Get(connection, "IsBuildInDatabase") is false)
        {
            if (count == 0) throw Error("First initialization of an external process database is not supported.");
            return false;
        }
        // Existing processes follow the pre-existing lifecycle. The strict
        // three-way DB proof below selects between first initialization and a
        // retained database only when the process tree is empty.
        if (count > 0) return false;
        var files = new[] { Database, Database + "-wal", Database + "-journal", Database + "-shm" };
        var states = new[] { session.SourcePath, session.LocalPath, session.LastPath }
            .Select(root => NativeV11PendingSave.ReadFiles(root, files)).ToArray();
        return ClassifyDatabaseState(count, states);
    }

    internal static bool ClassifyDatabaseState(int count, IReadOnlyList<IReadOnlyDictionary<string, byte[]?>> states)
    {
        var files = new[] { Database, Database + "-wal", Database + "-journal", Database + "-shm" };
        if (count < 0 || states.Count != 3 || states.Any(dict => files.Any(path => !dict.ContainsKey(path))))
            throw Error("The process database baseline is incomplete.");
        var present = states.SelectMany(dict => dict.Values).Any(bytes => bytes is not null);
        if (!present && count == 0) return true;
        var saved = states[0][Database];
        if (saved is null || states.Any(dict => dict[Database] is not byte[] actual || !saved.AsSpan().SequenceEqual(actual)) ||
            states.Any(dict => files.Skip(1).Any(sidecar => dict[sidecar] is { Length: > 0 })))
            throw Error("Process database, source caches and loaded process tree do not establish a complete existing initialization.");
        return false;
    }

    internal static Context RequireContext(NativeV11Session session)
    {
        session.VerifyContext();
        if (!IsFirst(session)) throw Error("First initialization requires an empty process tree and absent process database.");
        var contract = Inspect(session.Context.DesignerAssembly);
        var designer = session.Context.DesignerAssembly;
        _ = RequireSqliteProvider(designer);
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var state = NativeV11Reflection.Static(designer.GetType("o", true)!, "get_Instance")!;
        var settingsProperty = session.Profile.Version == "11.0.104.0" ? "cz" : "cv";
        var explorerProperty = session.Profile.Version == "11.0.104.0" ? "dn" : "dj";
        if (!ReferenceEquals(NativeV11Reflection.Get(state, session.Profile.DocumentProperty), session.Document) || !ReferenceEquals(NativeV11Reflection.Get(state, settingsProperty), settings))
            throw Error("The native process initialization context does not match the bound document.");
        var connection = NativeV11Reflection.Get(settings, "ProcessDataConnectionSet");
        if (connection is not null && NativeV11Reflection.Get(connection, "IsBuildInDatabase") is not true)
            throw Error("First initialization is supported only for the built-in process database.");
        var explorer = NativeV11Reflection.Get(state, explorerProperty) ?? throw Error("The actual native process explorer is unavailable.");
        var tree = NativeV11Reflection.Get(explorer, "Processes") ?? throw Error("The actual native process tree is unavailable.");
        var folder = NativeV11Reflection.Get(tree, "Root") ?? throw Error("The actual native process root is unavailable.");
        if (folder.GetType() != designer.GetType("Forguncy.Views.ObjectViews.BusinessProcessFolderViewModel", true) ||
            !ReferenceEquals(NativeV11Reflection.Get(folder, "Model"), session.Route(ModuleKind.BusinessProcess).Root))
            throw Error("The native process root does not belong to the bound document.");
        var destination = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.Config", true)!, "get_LocalProcessDBSqliteFilePath") as string;
        var expected = V11Safety.Under(session.LocalPath, Database);
        if (destination is null || !string.Equals(Path.GetFullPath(destination), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw Error("The native process database destination does not match the bound save session.");
        var manager = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.BusinessProcessManager", true)!, "get_Instance")!;
        return new(contract, folder, manager, expected);
    }

    internal static void RequireExistingSqlitePlugin(Assembly databaseAssembly)
    {
        // Do not call CheckDbPlugin: when the entry is absent that method scans
        // and loads plugins. Read only the already loaded native registry.
        var core = Assembly.Load("ForguncyCoreUtilities");
        var configured = NativeV11Reflection.Static(core.GetType("CommonUtilities.BuiltInDataBase.AppBuiltInDbConfig", true)!, "get_DefaultDataBaseType") as string;
        if (string.IsNullOrWhiteSpace(configured)) throw Error("The native built-in database type is unavailable.");
        var converted = NativeV11Reflection.Static(databaseAssembly.GetType("ForguncyDataAccess.DataBaseConvertFactory", true)!, "ConvertToNewDatabaseType", configured) as string;
        var cache = databaseAssembly.GetType("ForguncyDataAccess.DataBaseAssemblysCache", true)!;
        var loaded = cache.GetField("_loadedAssemblies", Flags)?.GetValue(null) as IDictionary;
        RequireLoadedPlugin(loaded, converted);
    }

    internal static void RequireLoadedPlugin(IDictionary? loaded, string? type)
    {
        if (loaded is null || string.IsNullOrWhiteSpace(type) || loaded.Keys.Cast<object>().Count(key =>
                key is string text && string.Equals(text, type, StringComparison.OrdinalIgnoreCase) && loaded[key] is Assembly) != 1)
            throw Error("The native built-in database plugin is not already loaded; initialize it in Designer before first-process creation.");
    }

    internal static Assembly RequireSqliteProvider(Assembly designer)
    {
        var loadContext = AssemblyLoadContext.GetLoadContext(designer) ?? throw Error("The native assembly context is unavailable.");
        var sqlite = loadContext.Assemblies.SingleOrDefault(a => a.GetName().Name == "Microsoft.Data.Sqlite") ??
            loadContext.LoadFromAssemblyName(new AssemblyName("Microsoft.Data.Sqlite"));
        var bin = Path.GetDirectoryName(designer.Location)!;
        var web = Directory.GetParent(bin)!.FullName;
        var paths = new[] { Path.Combine(bin, "Microsoft.Data.Sqlite.dll"), Path.Combine(web, "bin", "Microsoft.Data.Sqlite.dll") };
        if (string.IsNullOrEmpty(sqlite.Location) || !paths.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(sqlite.Location), StringComparison.OrdinalIgnoreCase)))
            throw Error("The SQLite verification provider is outside the pinned Designer installation.");
        return sqlite;
    }

    // snapshot must be an isolated native SQLite backup, never the live DB.
    internal static void RequireTemplateSchema(Assembly designer, string snapshot)
    {
        var contract = Inspect(designer);
        var sqlite = RequireSqliteProvider(designer);
        var templateCopy = Path.Combine(Path.GetTempPath(), "forguncy-process-template-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            File.Copy(contract.Template, templateCopy, false);
            var required = ReadSchema(sqlite, templateCopy);
            var actual = ReadSchema(sqlite, snapshot);
            if (required.Count == 0 || required.Any(pair => !actual.TryGetValue(pair.Key, out var sql) || sql != pair.Value))
                throw Error("The initialized process database does not contain the exact native template table schema.");
        }
        finally
        {
            foreach (var file in new[] { templateCopy, templateCopy + "-wal", templateCopy + "-shm", templateCopy + "-journal" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    internal static void RequireLiveDatabaseSchema(Assembly designer, string database)
    {
        var sqlite = RequireSqliteProvider(designer);
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var connectionType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
        string ConnectionString(string path, string mode)
        {
            var builder = Activator.CreateInstance(builderType)!;
            NativeV11Reflection.Set(builder, "DataSource", path); NativeV11Reflection.Set(builder, "Pooling", false);
            var property = builderType.GetProperty("Mode")!; property.SetValue(builder, Enum.Parse(property.PropertyType, mode));
            return builder.ToString()!;
        }
        var snapshotPath = Path.Combine(Path.GetTempPath(), "forguncy-process-schema-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (new FileStream(snapshotPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using (var source = (IDisposable)Activator.CreateInstance(connectionType, [ConnectionString(database, "ReadOnly")])!)
            using (var target = (IDisposable)Activator.CreateInstance(connectionType, [ConnectionString(snapshotPath, "ReadWrite")])!)
            {
                NativeV11Reflection.Call(source, "Open"); NativeV11Reflection.Call(target, "Open");
                NativeV11Reflection.Call(source, "BackupDatabase", target);
            }
            RequireTemplateSchema(designer, snapshotPath);
        }
        finally
        {
            foreach (var file in new[] { snapshotPath, snapshotPath + "-wal", snapshotPath + "-shm", snapshotPath + "-journal" })
                if (File.Exists(file)) File.Delete(file);
        }
    }

    private static Dictionary<string, string> ReadSchema(Assembly sqlite, string path)
    {
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var builder = Activator.CreateInstance(builderType)!;
        NativeV11Reflection.Set(builder, "DataSource", Path.GetFullPath(path)); NativeV11Reflection.Set(builder, "Pooling", false);
        var mode = builderType.GetProperty("Mode")!; mode.SetValue(builder, Enum.Parse(mode.PropertyType, "ReadOnly"));
        using var connection = (IDisposable)Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!, [builder.ToString()])!;
        NativeV11Reflection.Call(connection, "Open");
        using var command = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!;
        NativeV11Reflection.Set(command, "CommandText", "SELECT name, sql FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name");
        using var reader = (IDisposable)NativeV11Reflection.Call(command, "ExecuteReader")!;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (NativeV11Reflection.Call(reader, "Read") is true)
        {
            var name = NativeV11Reflection.Call(reader, "GetString", 0) as string;
            var sql = NativeV11Reflection.Call(reader, "GetString", 1) as string;
            if (name is null || sql is null || !result.TryAdd(name, sql)) throw Error("The process database schema is invalid or ambiguous.");
        }
        return result;
    }

    internal static object[] CollectNativeOwners(Contract contract, string name)
    {
        var executor = contract.Executor.Invoke([null, name, true, false]);
        return (contract.Related.Invoke(executor, null) as IEnumerable)?.Cast<object>().ToArray() ?? throw Error("The native first-process owner collector is unavailable.");
    }

    // This is discovery and a saved-baseline proof, not a complete checkout or
    // source-save plan. The caller must additionally resolve every new owner.
    internal static Candidate CaptureCandidate(NativeV11Session session, ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.BusinessProcess || request.Operation != ModuleOperation.Create)
            throw Error("First-process discovery requires a process creation request.");
        NativeV11Processes.ValidateFirstCandidate(session, request);
        var context = RequireContext(session);
        var processConnection = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ProcessDataConnectionSet");
        if (processConnection is not null && !string.IsNullOrEmpty(NativeV11Reflection.Get(processConnection, "ExternalDatabaseId") as string))
            throw Error("The first-process view source is not the reviewed built-in database branch.");
        var revision = NativeV11SourceRevision.Capture(session.SourcePath);
        var templatePaths = NativeV11FirstProcessTemplates.Capture(session.Context.DesignerAssembly);
        foreach (var root in new[] { session.SourcePath, session.LocalPath, session.LastPath })
            if (NativeV11PendingSave.ReadFiles(root, templatePaths).Values.Any(value => value is not null))
                throw Error("A native BPM template page already exists in the source or a save cache.");
        var tuples = CollectNativeOwners(context.Contract, request.Address.Name);
        var owners = tuples.Select(tuple =>
        {
            var native = tuple.GetType().GetField("Item1", Flags)?.GetValue(tuple) ?? throw Error("A native initialization owner is missing.");
            var state = tuple.GetType().GetField("Item2", Flags)?.GetValue(tuple)?.ToString();
            var name = NativeV11Reflection.Get(native, "ModuleName") as string;
            var type = NativeV11Reflection.Get(native, "ModuleType")?.ToString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(type) || state is not ("New" or "Edit"))
                throw Error("A native initialization owner has an unknown identity or state.");
            return new Owner(name, type, state, native,
                NativeV11Reflection.Get(native, "IconType")?.ToString(), NativeV11Reflection.Get(native, "ExclusiveType")?.ToString());
        }).ToArray();
        if (owners.Length == 0 || owners.Select(o => o.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != owners.Length ||
            owners.Count(o => o.Type == "ProcessItem" && o.State == "New" && o.Name == "ProcessItem/" + request.Address.Name) != 1 ||
            owners.Any(o => o.State == "Edit" && o.Type is not ("ApplicationSetting" or "Theme" or "Table" or "TableSqlite" or "TableView" or "TableProcessView")) ||
            owners.Any(o => o.State == "New" && o.Type is not ("ProcessItem" or "TableFolder" or "TableProcessView" or "Folder" or "MasterPageFolder" or "PCPage" or "MobilePage" or "MasterPCPage" or "MasterMobilePage")))
            throw Error("The native first-process owner set is incomplete, ambiguous or includes an unreviewed existing owner.");
        foreach (var owner in owners.Where(o => o.State == "New" && o.Type is "Folder" or "MasterPageFolder" or "TableFolder"))
        {
            var path = owner.Name.Replace('\\', '/').TrimEnd('/');
            if (!(path.StartsWith("Pages/", StringComparison.Ordinal) || path.StartsWith("MasterPages/", StringComparison.Ordinal) || path.StartsWith("Tables/", StringComparison.Ordinal)))
                throw Error("A native initialization folder has an unreviewed source path.");
            foreach (var root in new[] { session.SourcePath, session.LocalPath, session.LastPath })
                if (NativeV11PendingSave.ReadFiles(root, [path + "/"])[path + "/"] is not null)
                    throw Error("A native initialization folder already exists in the source or a save cache.");
        }
        foreach (var owner in owners.Where(o => o.State == "New" && o.Type is "PCPage" or "MobilePage" or "MasterPCPage" or "MasterMobilePage" or "TableProcessView"))
        {
            var slash = owner.Name.IndexOf('/');
            if (slash < 0 || slash == owner.Name.Length - 1) throw Error("A native initialization item identity is invalid.");
            var name = owner.Name[(slash + 1)..];
            var kind = owner.Type switch { "PCPage" or "MobilePage" => ModuleKind.Page, "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage, _ => ModuleKind.View };
            if (session.Items(kind).Any(item => item.Address.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                throw Error("A native initialization item already exists in the loaded project.");
        }
        var settings = new NativeV11Settings(session);
        var expected = settings.CaptureSaveFiles(ModuleKind.ApplicationSettings)
            .Concat(settings.CaptureSaveFiles(ModuleKind.Theme)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var root in new[] { session.SourcePath, session.LocalPath, session.LastPath })
        {
            var actual = NativeV11PendingSave.ReadFiles(root, expected.Keys);
            if (expected.Any(pair => actual[pair.Key] is not byte[] bytes || !V11Safety.Equivalent(V11Safety.Body(pair.Value), V11Safety.Body(bytes))))
                throw Error("A native application or theme owner does not match its saved source and caches.");
        }
        if (NativeV11SourceRevision.Capture(session.SourcePath) != revision || !IsFirst(session))
            throw Error("The native first-process baseline changed during owner discovery.");
        var templateReferences = NativeV11FirstProcessTemplates.CaptureByOwner(session.Context.DesignerAssembly, owners);
        var pageDirectGroups = NativeV11FirstProcessTemplates.CapturePageDirectGroups(owners, templateReferences);
        var newDirectGroups = ProjectNewDirectGroups(owners, pageDirectGroups, session.NativePath(request.Address), NativeV11CollaborationOwners.TableDatabasePath());
        return new Candidate(owners, revision, expected, templatePaths, templateReferences, pageDirectGroups, newDirectGroups);
    }

    internal static void RequireSameCandidate(NativeV11Session session, ModuleMutationRequest request, Candidate expected)
    {
        var current = CaptureCandidate(session, request);
        static string Key(Owner owner) => owner.Name + "\n" + owner.Type + "\n" + owner.State + "\n" + owner.Icon + "\n" + owner.Exclusive;
        if (current.SourceRevision != expected.SourceRevision ||
            !current.TemplatePaths.SequenceEqual(expected.TemplatePaths, StringComparer.Ordinal) ||
            current.TemplateReferences.Count != expected.TemplateReferences.Count || expected.TemplateReferences.Any(pair =>
                !current.TemplateReferences.TryGetValue(pair.Key, out var paths) || !pair.Value.SequenceEqual(paths, StringComparer.Ordinal)) ||
            current.PageDirectGroups.Count != expected.PageDirectGroups.Count || expected.PageDirectGroups.Any(pair =>
                !current.PageDirectGroups.TryGetValue(pair.Key, out var files) || !pair.Value.SequenceEqual(files, StringComparer.Ordinal)) ||
            current.NewDirectGroups.Count != expected.NewDirectGroups.Count || expected.NewDirectGroups.Any(pair =>
                !current.NewDirectGroups.TryGetValue(pair.Key, out var group) || !pair.Value.SequenceEqual(group, StringComparer.Ordinal)) ||
            !current.Owners.Select(Key).Order(StringComparer.Ordinal).SequenceEqual(expected.Owners.Select(Key).Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            current.ExistingSettings.Count != expected.ExistingSettings.Count || expected.ExistingSettings.Any(pair =>
                !current.ExistingSettings.TryGetValue(pair.Key, out var value) || !V11Safety.Equivalent(V11Safety.Body(pair.Value), V11Safety.Body(value))))
            throw Error("The native first-process owners or their saved baseline changed before dispatch.");
    }

    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> ProjectNewDirectGroups(IReadOnlyList<Owner> owners,
        IReadOnlyDictionary<string, IReadOnlyList<string>> pages, string processPath, string appDatabase)
    {
        var result = pages.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var tableFolder = owners.Single(owner => owner.State == "New" && owner.Type == "TableFolder");
        if (!tableFolder.Name.StartsWith("Tables/", StringComparison.Ordinal)) throw Error("The native process view folder is invalid.");
        var slash = tableFolder.Name.IndexOf('/');
        NativeV11Session.ValidateAddress(new(ModuleKind.Folder, tableFolder.Name[(slash + 1)..], "Tables"));
        result.Add(tableFolder.Name, [tableFolder.Name.TrimEnd('/') + "/"]);
        foreach (var owner in owners.Where(owner => owner.State == "New" && owner.Type == "TableProcessView"))
        {
            var name = owner.Name[(owner.Name.IndexOf('/') + 1)..];
            NativeV11Session.ValidateAddress(new(ModuleKind.View, name));
            result.Add(owner.Name, [tableFolder.Name.TrimEnd('/') + "/" + name + ".json", appDatabase]);
        }
        var process = owners.Single(owner => owner.State == "New" && owner.Type == "ProcessItem");
        result.Add(process.Name, [processPath, Path.ChangeExtension(processPath, ".bpmn").Replace('\\', '/'), Database]);
        if (result.Count != owners.Count(owner => owner.State == "New") || result.Count != 32)
            throw Error("The native first-process direct owner projection is incomplete.");
        return result;
    }

    internal static IReadOnlyList<NativeV11CollaborationOwner> ProjectNewOwners(IReadOnlyList<Owner> owners,
        IReadOnlyDictionary<string, IReadOnlyList<string>> groups, ModuleAddress processAddress)
    {
        var result = new List<NativeV11CollaborationOwner>();
        foreach (var owner in owners.Where(owner => owner.State == "New"))
        {
            if (!groups.TryGetValue(owner.Name, out var files) || files.Count == 0) throw Error("A native new owner has no reviewed direct group.");
            ModuleAddress address;
            if (owner.Type is "Folder" or "MasterPageFolder" or "TableFolder")
            {
                var slash = owner.Name.IndexOf('/');
                if (slash <= 0 || slash == owner.Name.Length - 1) throw Error("A native initialization folder identity is invalid.");
                address = new(ModuleKind.Folder, owner.Name[(slash + 1)..], owner.Name[..slash]);
            }
            else if (owner.Type == "ProcessItem") address = processAddress;
            else
            {
                var kind = owner.Type switch
                {
                    "PCPage" or "MobilePage" => ModuleKind.Page,
                    "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage,
                    "TableProcessView" => ModuleKind.View,
                    _ => throw Error("A native new owner cannot be mapped to a reviewed logical address.")
                };
                var prefix = kind switch { ModuleKind.Page => "Pages/", ModuleKind.MasterPage => "MasterPages/", _ => "Tables/" };
                var name = owner.Name[(owner.Name.IndexOf('/') + 1)..];
                var suffix = "/" + name + ".json";
                var path = files.Single(file => file.EndsWith(suffix, StringComparison.Ordinal));
                if (!path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
                    throw Error("A native new owner file does not match its reviewed logical path.");
                var folder = path[prefix.Length..^suffix.Length];
                address = new(kind, name, folder.Length == 0 ? null : folder);
            }
            NativeV11Session.ValidateAddress(address);
            result.Add(new(address, owner.Native, owner.State));
        }
        if (result.Count != groups.Count || result.Select(owner => owner.Address).Distinct().Count() != result.Count)
            throw Error("The native new owner logical projection is incomplete or ambiguous.");
        return result;
    }

    // The caller supplies the full native owner/source revalidation and marks
    // the action uncertain before entry: database import cannot be rolled back
    // merely by undoing the new process definition.
    internal static async Task<Action> InitializeAsync(NativeV11Session session, Action revalidate, Func<Action> createDefinition)
    {
        var context = RequireContext(session);
        using var nativeResolver = SqliteResolver(session.Context.DesignerAssembly);
        var databaseAssembly = context.Contract.Copy.DeclaringType!.Assembly;
        try { RequireExistingSqlitePlugin(databaseAssembly); }
        catch (BridgeException)
        {
            // Follow the native built-in SQLite registration path, scoped to
            // this pinned installation. This loads, but never installs, it.
            NativeV11Reflection.Static(databaseAssembly.GetType("ForguncyDataAccess.DataBaseAssemblysCache", true)!,
                "LoadSqliteDatabase", Path.GetDirectoryName(session.Context.DesignerAssembly.Location)!);
            RequireExistingSqlitePlugin(databaseAssembly);
        }
        CopyInitialDatabase(context.Contract.Copy, context.Contract.Template, context.Destination, revalidate);
        var load = context.Contract.Wait.Invoke(context.Manager, null) as Task ?? throw Error("The native process load task is unavailable.");
        var initialization = context.Contract.Initialize.Invoke(context.Folder, [null, load]) as Task<bool>
            ?? throw Error("The native process initialization task is unavailable.");
        return await CompleteAsync(initialization, createDefinition);
    }

    internal static IDisposable SqliteResolver(Assembly designer)
    {
        var context = AssemblyLoadContext.GetLoadContext(designer) ?? throw Error("The native assembly context is unavailable.");
        var bin = Path.GetDirectoryName(designer.Location)!;
        var roots = new[] { bin, Path.Combine(Directory.GetParent(bin)!.FullName, "bin") };
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "win-x64", System.Runtime.InteropServices.Architecture.X86 => "win-x86",
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64", _ => throw Error("Unsupported native SQLite architecture.")
        };
        IntPtr Resolve(Assembly owner, string name)
        {
            if (name is not ("e_sqlite3" or "e_sqlite3.dll") || string.IsNullOrEmpty(owner.Location) ||
                !roots.Any(root => Path.GetFullPath(owner.Location).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) return IntPtr.Zero;
            foreach (var root in roots)
            {
                var path = Path.Combine(root, "runtimes", architecture, "native", "e_sqlite3.dll");
                if (File.Exists(path)) return System.Runtime.InteropServices.NativeLibrary.Load(path);
            }
            return IntPtr.Zero;
        }
        context.ResolvingUnmanagedDll += Resolve;
        return new ResolverLease(() => context.ResolvingUnmanagedDll -= Resolve);
    }

    private sealed class ResolverLease(Action release) : IDisposable { public void Dispose() => release(); }

    internal static void CopyInitialDatabase(MethodInfo copy, string template, string destination, Action revalidate)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        var temporary = Path.Combine(directory, ".forguncy-process-init-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        revalidate();
        if (File.Exists(destination) || Directory.Exists(destination) || File.Exists(temporary) || Directory.Exists(temporary)) throw Error("The process database destination is no longer fresh.");
        try
        {
            copy.Invoke(null, [template, temporary]);
            revalidate();
            if (!File.Exists(temporary) || new[] { "-wal", "-journal", "-shm" }.Any(suffix => File.Exists(temporary + suffix))) throw Error("The native process database copy is incomplete.");
            File.Move(temporary, destination, false);
        }
        finally { if (File.Exists(temporary) && !new[] { "-wal", "-journal", "-shm" }.Any(suffix => File.Exists(temporary + suffix))) File.Delete(temporary); }
    }

    internal static async Task<Action> CompleteAsync(Task<bool> initialization, Func<Action> createDefinition)
    {
        if (initialization.IsCompleted && !await initialization) throw Error("Native process initialization did not succeed.");
        Action? undo = null; Exception? failure = null;
        try { undo = createDefinition(); } catch (Exception error) { failure = error; }
        bool accepted;
        try { accepted = await initialization; }
        catch when (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); throw; }
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        if (!accepted) throw Error("Native process initialization did not succeed.");
        return undo!;
    }

    private static BridgeException Error(string message) => new("NativeProcessInitializationUnavailable", message);
}
