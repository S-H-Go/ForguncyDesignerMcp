using System.Collections;
using System.Data;

namespace ForguncyDesignerMcp.Adapters.V11;

// A private SQLite backup is compared with the post-initialization database.
// Every pre-existing schema object and the complete multiset of rows in each
// pre-existing table must survive; newly generated native objects are allowed.
internal sealed class NativeV11SqlitePreservation : IDisposable
{
    private readonly string snapshot;
    private readonly Type connectionType;
    private readonly Type builderType;
    private NativeV11SqlitePreservation(string snapshot, Type connectionType, Type builderType)
    { this.snapshot = snapshot; this.connectionType = connectionType; this.builderType = builderType; }

    private static string Connection(Type builderType, string path, string mode)
    {
        var builder = Activator.CreateInstance(builderType)!;
        NativeV11Reflection.Set(builder, "DataSource", path); NativeV11Reflection.Set(builder, "Pooling", false);
        var property = builderType.GetProperty("Mode")!; property.SetValue(builder, Enum.Parse(property.PropertyType, mode));
        return builder.ToString()!;
    }

    internal static NativeV11SqlitePreservation Capture(string database, bool allowLiveWal = false)
    {
        if (!File.Exists(database) || !allowLiveWal && !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(database) ||
            allowLiveWal && File.Exists(database + "-journal") && new FileInfo(database + "-journal").Length != 0)
            throw new InvalidDataException("The application database baseline is unavailable or has an active journal.");
        var assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Microsoft.Data.Sqlite");
        var connectionType = assembly.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
        var builderType = assembly.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var path = Path.Combine(Path.GetTempPath(), "forguncy-preservation-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        var result = new NativeV11SqlitePreservation(path, connectionType, builderType);
        try
        {
            using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using var source = (IDisposable)Activator.CreateInstance(connectionType, [Connection(builderType, database, "ReadOnly")])!;
            using var target = (IDisposable)Activator.CreateInstance(connectionType, [Connection(builderType, path, "ReadWrite")])!;
            NativeV11Reflection.Call(source, "Open"); NativeV11Reflection.Call(target, "Open");
            NativeV11Reflection.Call(source, "BackupDatabase", target);
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal void Verify(string database, string? changedView = null, string? changedTable = null, string? oldColumn = null, string? newColumn = null, bool allowLiveWal = false,
        IReadOnlyDictionary<string, string>? nativeTypeNormalization = null, IReadOnlyCollection<string>? deletedObjects = null, string? changedConstraint = null)
    {
        if (changedConstraint is not null && (changedConstraint is not ("default" or "required") || oldColumn is null || oldColumn != newColumn || changedTable is null))
            throw new InvalidDataException("A native constraint proof requires the same target column and a reviewed constraint kind.");
        if (!allowLiveWal && !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(database) ||
            allowLiveWal && File.Exists(database + "-journal") && new FileInfo(database + "-journal").Length != 0)
            throw new InvalidDataException("The initialized application database has an active rollback journal.");
        using var connection = (IDisposable)Activator.CreateInstance(connectionType, [Connection(builderType, database, "ReadOnly")])!;
        NativeV11Reflection.Call(connection, "Open");
        using (var attach = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!)
        {
            NativeV11Reflection.Set(attach, "CommandText", "ATTACH DATABASE $path AS baseline");
            NativeV11Reflection.Call(NativeV11Reflection.Get(attach, "Parameters")!, "AddWithValue", "$path", snapshot);
            NativeV11Reflection.Call(attach, "ExecuteNonQuery");
        }
        using var command = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!;
        NativeV11Reflection.Set(command, "CommandText", "BEGIN"); NativeV11Reflection.Call(command, "ExecuteNonQuery");
        long Scalar(string sql)
        { NativeV11Reflection.Set(command, "CommandText", sql); return Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")); }
        var predicate = "name NOT LIKE 'sqlite\\_%' ESCAPE '\\' AND type IN ('table','index','trigger','view') AND NOT (type='view' AND name=$changedView) AND NOT (type='table' AND name=$changedTable)";
        var schemaParameters = NativeV11Reflection.Get(command, "Parameters")!;
        NativeV11Reflection.Call(schemaParameters, "AddWithValue", "$changedView", changedView ?? "");
        NativeV11Reflection.Call(schemaParameters, "AddWithValue", "$changedTable", changedTable ?? "");
        string? deletedParameters = null;
        if (deletedObjects is { Count: > 0 })
        {
            if (deletedObjects.Any(string.IsNullOrWhiteSpace) || deletedObjects.Distinct(StringComparer.OrdinalIgnoreCase).Count() != deletedObjects.Count)
                throw new InvalidDataException("The deleted database object inventory is invalid.");
            var parameters = deletedObjects.Select((name, index) => (name, parameter: "$deleted" + index)).ToArray();
            foreach (var parameter in parameters) NativeV11Reflection.Call(schemaParameters, "AddWithValue", parameter.parameter, parameter.name);
            var names = string.Join(",", parameters.Select(parameter => parameter.parameter));
            deletedParameters = names;
            if (Scalar("SELECT COUNT(*) FROM main.sqlite_master WHERE type IN ('table','view') AND name COLLATE NOCASE IN (" + names + ")") != 0)
                throw new InvalidDataException("A reviewed deleted database object remains.");
            predicate += " AND NOT ((type IN ('table','view') AND name COLLATE NOCASE IN (" + names + ")) OR " +
                "(type IN ('index','trigger') AND tbl_name COLLATE NOCASE IN (" + names + ")))";
        }
        if (Scalar("SELECT COUNT(*) FROM (SELECT type,name,tbl_name,trim(sql,char(9)||char(10)||char(13)||' ') FROM baseline.sqlite_master WHERE " + predicate +
            " EXCEPT SELECT type,name,tbl_name,trim(sql,char(9)||char(10)||char(13)||' ') FROM main.sqlite_master WHERE " + predicate + ")") != 0)
            throw new InvalidDataException("An existing application database schema object changed during initialization.");
        if ((deletedObjects is { Count: > 0 } || changedConstraint is not null) && Scalar("SELECT COUNT(*) FROM (SELECT type,name,tbl_name,trim(sql,char(9)||char(10)||char(13)||' ') FROM main.sqlite_master WHERE " + predicate +
            " EXCEPT SELECT type,name,tbl_name,trim(sql,char(9)||char(10)||char(13)||' ') FROM baseline.sqlite_master WHERE " + predicate + ")") != 0)
            throw new InvalidDataException("The native schema operation introduced an unexpected database object.");
        if (deletedParameters is not null)
        {
            var beforeSequence = Scalar("SELECT COUNT(*) FROM baseline.sqlite_master WHERE type='table' AND name='sqlite_sequence'");
            var afterSequence = Scalar("SELECT COUNT(*) FROM main.sqlite_master WHERE type='table' AND name='sqlite_sequence'");
            if (beforeSequence != afterSequence) throw new InvalidDataException("The deletion changed the native SQLite sequence registry.");
            if (beforeSequence != 0)
            {
                var filter = " WHERE name COLLATE NOCASE NOT IN (" + deletedParameters + ")";
                if (Scalar("SELECT COUNT(*) FROM (SELECT name,seq FROM baseline.sqlite_sequence" + filter + " EXCEPT SELECT name,seq FROM main.sqlite_sequence" + filter + ")") != 0 ||
                    Scalar("SELECT COUNT(*) FROM (SELECT name,seq FROM main.sqlite_sequence" + filter + " EXCEPT SELECT name,seq FROM baseline.sqlite_sequence" + filter + ")") != 0)
                    throw new InvalidDataException("The deletion changed an unrelated SQLite auto-increment sequence.");
            }
        }
        NativeV11Reflection.Set(command, "CommandText", "SELECT name,sql FROM baseline.sqlite_master WHERE type='table' AND " + predicate + " ORDER BY name");
        var tables = new List<(string Name, string Sql)>();
        using (var reader = (IDisposable)NativeV11Reflection.Call(command, "ExecuteReader")!)
            while ((bool)NativeV11Reflection.Call(reader, "Read")!)
                tables.Add(((string)NativeV11Reflection.Call(reader, "GetString", 0)!, (string)NativeV11Reflection.Call(reader, "GetString", 1)!));
        NativeV11Reflection.Call(schemaParameters, "Clear");
        if (changedTable is not null)
        {
            if (oldColumn is null && newColumn is null)
                throw new InvalidDataException("The affected native table baseline is unavailable.");
            NativeV11Reflection.Call(schemaParameters, "AddWithValue", "$tableName", changedTable);
            var beforeAutoincrement = Scalar("SELECT COUNT(*) FROM baseline.sqlite_master WHERE type='table' AND name=$tableName AND instr(upper(sql),'AUTOINCREMENT')>0");
            var afterAutoincrement = Scalar("SELECT COUNT(*) FROM main.sqlite_master WHERE type='table' AND name=$tableName AND instr(upper(sql),'AUTOINCREMENT')>0");
            NativeV11Reflection.Call(schemaParameters, "Clear");
            if (beforeAutoincrement != afterAutoincrement) throw new InvalidDataException("The native column operation changed auto-increment semantics.");
            tables.Add((changedTable, ""));
        }
        static string Quote(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        foreach (var table in tables)
        {
            if (table.Sql.TrimStart().StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A virtual table requires its own preservation proof.");
            NativeV11Reflection.Set(command, "CommandText", "SELECT name,type,\"notnull\",dflt_value,pk,hidden FROM pragma_table_xinfo($name, 'baseline') WHERE hidden <> 1 ORDER BY cid");
            var parameters = NativeV11Reflection.Get(command, "Parameters")!;
            NativeV11Reflection.Call(parameters, "Clear"); NativeV11Reflection.Call(parameters, "AddWithValue", "$name", table.Name);
            var columns = new List<(string Name, string Metadata)>();
            using (var reader = (IDisposable)NativeV11Reflection.Call(command, "ExecuteReader")!)
                while ((bool)NativeV11Reflection.Call(reader, "Read")!)
                {
                    var name = (string)NativeV11Reflection.Call(reader, "GetString", 0)!;
                    var metadata = string.Join("\0", Enumerable.Range(1, 5).Select(index => Convert.ToString(NativeV11Reflection.Call(reader, "GetValue", index), System.Globalization.CultureInfo.InvariantCulture) ?? "<null>"));
                    columns.Add((name, metadata));
                }
            NativeV11Reflection.Call(parameters, "Clear");
            if (columns.Count == 0) throw new InvalidDataException("An existing application table has no verifiable columns.");
            var originalColumns = columns.Select(column => column.Name).ToList();
            var targetColumns = originalColumns.ToList();
            if (table.Name == changedTable)
            {
                if (oldColumn is not null)
                {
                    var index = originalColumns.FindIndex(name => name == oldColumn);
                    if (index < 0) throw new InvalidDataException("The physical column baseline is unavailable.");
                    if (newColumn is null) { originalColumns.RemoveAt(index); targetColumns.RemoveAt(index); columns.RemoveAt(index); }
                    else { targetColumns[index] = newColumn; columns[index] = (newColumn, columns[index].Metadata); }
                }
                NativeV11Reflection.Set(command, "CommandText", "SELECT name,type,\"notnull\",dflt_value,pk,hidden FROM pragma_table_xinfo($name, 'main') WHERE hidden <> 1 ORDER BY cid");
                NativeV11Reflection.Call(parameters, "AddWithValue", "$name", table.Name);
                var afterColumns = new List<(string Name, string Metadata)>();
                using (var reader = (IDisposable)NativeV11Reflection.Call(command, "ExecuteReader")!)
                    while ((bool)NativeV11Reflection.Call(reader, "Read")!)
                        afterColumns.Add(((string)NativeV11Reflection.Call(reader, "GetString", 0)!, string.Join("\0", Enumerable.Range(1, 5).Select(i => Convert.ToString(NativeV11Reflection.Call(reader, "GetValue", i), System.Globalization.CultureInfo.InvariantCulture) ?? "<null>"))));
                NativeV11Reflection.Call(parameters, "Clear");
                var unaffectedBefore = originalColumns.Where(name => oldColumn is null || name != oldColumn).ToArray();
                var unaffectedAfter = afterColumns.Select(column => column.Name).Where(name => newColumn is null || name != newColumn).ToArray();
                var management = new[] { "FGC_Rowversion", "FGC_UpdateHelp" };
                var nativeManagementOrder = changedConstraint is not null && management.All(name => unaffectedBefore.Count(value => value == name) == 1) &&
                    unaffectedAfter.TakeLast(2).SequenceEqual(management, StringComparer.Ordinal) &&
                    unaffectedBefore.Where(name => !management.Contains(name, StringComparer.Ordinal)).SequenceEqual(
                        unaffectedAfter.Where(name => !management.Contains(name, StringComparer.Ordinal)), StringComparer.Ordinal);
                if (!unaffectedBefore.SequenceEqual(unaffectedAfter, StringComparer.Ordinal) && !nativeManagementOrder)
                    throw new InvalidDataException("The native column operation reordered unrelated columns.");
                if (oldColumn is null)
                {
                    if (afterColumns.Count(column => column.Name == newColumn) != 1 || originalColumns.Contains(newColumn!, StringComparer.OrdinalIgnoreCase))
                        throw new InvalidDataException("The new physical column identity is invalid.");
                    afterColumns.RemoveAll(column => column.Name == newColumn);
                }
                if (originalColumns.Count == 0 || columns.Count != afterColumns.Count || columns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Count ||
                    afterColumns.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != afterColumns.Count)
                    throw new InvalidDataException("The physical column operation changed another column or constraint.");
                foreach (var expectedColumn in columns)
                {
                    var actual = afterColumns.SingleOrDefault(column => column.Name == expectedColumn.Name);
                    if (actual.Name is null) throw new InvalidDataException("A retained native column is missing.");
                    var beforeFields = expectedColumn.Metadata.Split('\0'); var afterFields = actual.Metadata.Split('\0');
                    var ignoredField = expectedColumn.Name == newColumn ? changedConstraint switch { "required" => 1, "default" => 2, _ => -1 } : -1;
                    if (beforeFields.Length != 5 || afterFields.Length != 5 || Enumerable.Range(1, 4).Any(index => index != ignoredField && beforeFields[index] != afterFields[index]) ||
                        !beforeFields[0].Equals(afterFields[0], StringComparison.OrdinalIgnoreCase) &&
                        !(nativeTypeNormalization is not null && nativeTypeNormalization.TryGetValue(expectedColumn.Name, out var nativeType) &&
                          beforeFields[0].Equals("numeric", StringComparison.OrdinalIgnoreCase) && afterFields[0].Equals(nativeType, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("A retained native column's type or constraint changed: " + expectedColumn.Name);
                }
                var raw = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "SQLitePCLRaw.core").GetType("SQLitePCL.raw", true)!;
                var method = raw.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static).Single(m => m.Name == "sqlite3_table_column_metadata" &&
                    m.GetParameters().Length == 9 && m.GetParameters()[1].ParameterType == typeof(string));
                var handle = NativeV11Reflection.Get(connection, "Handle") ?? throw new InvalidDataException("The native SQLite handle is unavailable.");
                string Collation(string schema, string name)
                {
                    object?[] args = [handle, schema, table.Name, name, null, null, 0, 0, 0];
                    if (Convert.ToInt32(method.Invoke(null, args)) != 0 || args[5] is not string collation)
                        throw new InvalidDataException("The native SQLite column collation is unavailable.");
                    return collation;
                }
                for (var index = 0; index < originalColumns.Count; index++)
                    if (!Collation("baseline", originalColumns[index]).Equals(Collation("main", targetColumns[index]), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("A retained native column's collation changed.");
            }
            string Rows(string schema, IEnumerable<string> names)
            {
                IEnumerable<string> Parts(string name)
                {
                    var column = Quote(name);
                    if (table.Name == changedTable && nativeTypeNormalization is not null && nativeTypeNormalization.ContainsKey(name))
                    {
                        var isDate = "typeof(" + column + ") IN ('integer','real') AND " + column + " BETWEEN -657434 AND 2958465";
                        return ["CASE WHEN " + isDate + " THEN 'native-date' ELSE typeof(" + column + ") END",
                            "CASE WHEN " + isDate + " THEN printf('%!.17g',CAST(" + column + " AS REAL)) ELSE hex(CAST(" + column + " AS BLOB)) END"];
                    }
                    return ["typeof(" + column + ")", "hex(CAST(" + column + " AS BLOB))"];
                }
                var expressions = string.Join(",", names.SelectMany(Parts));
                return "SELECT " + expressions + ",COUNT(*) FROM " + schema + "." + Quote(table.Name) + " GROUP BY " + expressions;
            }
            var beforeRows = originalColumns.AsEnumerable(); var afterRows = targetColumns.AsEnumerable();
            if (changedConstraint is not null && table.Name == changedTable)
            {
                if (!originalColumns.Contains("ID", StringComparer.Ordinal) || !originalColumns.Contains("FGC_Rowversion", StringComparer.Ordinal) ||
                    Scalar("SELECT COUNT(*) FROM main." + Quote(table.Name) + " WHERE \"FGC_Rowversion\" IS NOT NULL") != 0)
                    throw new InvalidDataException("The expected native rowversion reset or row identity is not verified.");
                beforeRows = originalColumns.Where(name => name != "FGC_Rowversion");
                afterRows = targetColumns.Where(name => name != "FGC_Rowversion");
            }
            if (Scalar("SELECT COUNT(*) FROM (" + Rows("baseline", beforeRows) + " EXCEPT " + Rows("main", afterRows) + ")") != 0 ||
                Scalar("SELECT COUNT(*) FROM (" + Rows("main", afterRows) + " EXCEPT " + Rows("baseline", beforeRows) + ")") != 0)
                throw new InvalidDataException("Existing application table data changed during first-process initialization: " + table.Name);
        }
    }

    public void Dispose()
    {
        foreach (var path in new[] { snapshot, snapshot + "-wal", snapshot + "-shm", snapshot + "-journal" })
            if (File.Exists(path)) File.Delete(path);
    }
}
