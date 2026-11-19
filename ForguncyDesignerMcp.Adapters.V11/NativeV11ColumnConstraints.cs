using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Uses the same non-UI native database operation and cache/model updates as
// the V11 table editor. The native SQLite rebuild explicitly resets the
// concurrency token; update-help data remains protected.
internal static class NativeV11ColumnConstraints
{
    internal sealed record Plan(NativeV11BuiltInTableLifecycle.DeletePlan Baseline, MethodInfo Method, object Manager,
        object Column, DataColumn CachedColumn, string Name, string Kind, object? Value, string Database);

    internal static bool Supports(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.Table || request.Operation != ModuleOperation.Update || request.Document?.Metadata.ValueKind != JsonValueKind.Object ||
            !request.Document.Metadata.TryGetProperty("operations", out var ops) || ops.ValueKind != JsonValueKind.Array || ops.GetArrayLength() != 1 ||
            ops[0].ValueKind != JsonValueKind.Object || !ops[0].TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return false;
        return method.GetString() is "setColumnDefault" or "setColumnRequired";
    }

    internal static Plan Prepare(NativeV11Session session, ModuleMutationRequest request)
    {
        if (!Supports(request) || request.Document!.Companions.Count != 0 || request.Document.Metadata.EnumerateObject().Count() != 1)
            throw new InvalidDataException("One native column constraint operation is required.");
        var operation = request.Document.Metadata.GetProperty("operations")[0];
        if (operation.EnumerateObject().Count() != 2 || !operation.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object ||
            p.EnumerateObject().Count() != 2 || p.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 2 ||
            p.EnumerateObject().Any(x => x.Name is not ("columnName" or "value")) ||
            !p.TryGetProperty("columnName", out var nameValue) || nameValue.ValueKind != JsonValueKind.String || !p.TryGetProperty("value", out var value))
            throw new InvalidDataException("A column constraint needs columnName and value.");
        var name = nameValue.GetString()!; V11Safety.Address(new ModuleAddress(ModuleKind.Table, name));
        if (name.Equals("ID", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FGC_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native identity and management fields cannot be changed here.");
        var required = operation.GetProperty("method").GetString() == "setColumnRequired";
        var baseline = NativeV11BuiltInTableLifecycle.PrepareDelete(session, request with { Operation = ModuleOperation.Delete, Document = null }, addingColumn: true);
        NativeV11TableLifecycle.ValidateRelationships(baseline.TableSet, request.Address.Name);
        var columns = (NativeV11Reflection.Get(baseline.Item.Model, "Columns") as IEnumerable)?.Cast<object>().ToArray() ?? [];
        var column = columns.SingleOrDefault(item => NativeV11Reflection.Get(item, "Name") as string == name && item.GetType().FullName == "Forguncy.Model.Tables.BindingColumn")
            ?? throw new InvalidDataException("The native binding column is unavailable.");
        var type = NativeV11Reflection.Get(column, "ColumnType") as Type;
        if (type != typeof(bool) && type != typeof(long) && type != typeof(double) && type != typeof(string) && type != typeof(DateTime))
            throw new NotSupportedException("This native column type has not been verified for constraint updates.");
        var parsed = ParseValue(type, value, required);
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (!NativeV11BuiltInTableLifecycle.PhysicalColumnEligible(database, request.Address.Name, name))
            throw new InvalidOperationException("The native column has an unsupported physical dependency.");
        var manager = NativeV11Reflection.Get(session.Document, "TableSpreadManager") ?? throw new InvalidOperationException("The native table cache is unavailable.");
        if (!ReferenceEquals(NativeV11Reflection.Get(manager, "Workbook"), session.Workbook) || session.Sheet(request.Address.Name) is null)
            throw new InvalidOperationException("The native table worksheet must be loaded.");
        var data = manager.GetType().GetProperties(NativeV11Reflection.Flags).Single(property => property.PropertyType.FullName == "Forguncy.LinkedDataSet" && property.GetIndexParameters().Length == 0).GetValue(manager) as DataSet;
        var cached = data?.Tables[request.Address.Name]?.Columns[name] ?? throw new InvalidOperationException("The native column cache is unavailable.");
        var helper = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "md" : "lx", true)!;
        var method = helper.GetMethods(NativeV11Reflection.Flags).Single(m => m.Name == "UpdateColumnAsync" && m.IsStatic && m.GetParameters().Length == 5 &&
            m.GetParameters()[0].ParameterType.FullName == "ForguncyDataAccess.DataAccess" && m.GetParameters()[3].ParameterType.FullName == "GrapeCity.Forguncy.DataBaseApi.ColumnUpdateType");
        if (method.ReturnType != typeof(Task<object>)) throw new InvalidDataException("The native constraint operation differs from the pinned contract.");
        var plan = new Plan(baseline, method, manager, column, cached, name, required ? "Require" : "DefaultValue", parsed, database);
        VerifyBaseline(plan);
        return plan;
    }

    internal static object? ParseValue(Type type, JsonElement value, bool required)
    {
        object? parsed;
        if (required) parsed = value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException("Required must be a boolean.") };
        else if (value.ValueKind == JsonValueKind.Null) parsed = null;
        else if (type == typeof(bool)) parsed = value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new InvalidDataException("Boolean default must be boolean or null.") };
        else if (type == typeof(long)) parsed = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) ? integer : throw new InvalidDataException("Integer default must be an integer or null.");
        else if (type == typeof(double)) parsed = value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw new InvalidDataException("Number default must be finite or null.");
        else if (type == typeof(DateTime))
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Date/time default must be an ISO local date/time or null.");
            if (!DateTime.TryParseExact(text, ["yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFF"], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date)) throw new InvalidDataException("Date/time default must be an ISO local date/time with at most millisecond precision.");
            try { if (DateTime.FromOADate(date.ToOADate()) != date) throw new InvalidDataException("Date/time default cannot round-trip through the native SQLite date representation."); }
            catch (Exception error) when (error is OverflowException or ArgumentException) { throw new InvalidDataException("Date/time default is outside native SQLite date range.", error); }
            parsed = date;
        }
        else
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException("Text default must be text or null.");
            if (text.Length > 4096 || text.Any(char.IsControl) || text.Contains('"'))
                throw new InvalidDataException("The native SQLite default writer cannot safely quote this text.");
            parsed = text;
        }
        return parsed;
    }

    private static string? Canonical(object? value, Type type)
    {
        if (value is null || value is DBNull) return null;
        try
        {
            if (type == typeof(string)) return value as string ?? throw new InvalidCastException();
            if (type == typeof(DateTime))
            {
                var date = value is DateTime actual ? actual : value is double number ? DateTime.FromOADate(number) : throw new InvalidCastException();
                if (date.Kind != DateTimeKind.Unspecified || date.Ticks % TimeSpan.TicksPerMillisecond != 0) throw new InvalidCastException();
                return date.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);
            }
            if (type == typeof(bool)) return Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "1" : "0";
            if (type == typeof(double)) { var number = Convert.ToDouble(value, CultureInfo.InvariantCulture); return double.IsFinite(number) ? number.ToString("R", CultureInfo.InvariantCulture) : throw new InvalidCastException(); }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException) { throw new InvalidDataException("The native default is not representable for this column.", error); }
    }

    private static string? PhysicalDefault(object? value, Type type)
    {
        if (value is null or DBNull) return null;
        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? throw new InvalidDataException("The SQLite default is unavailable.");
        if (type != typeof(string))
        {
            if (type == typeof(DateTime) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && double.IsFinite(serial))
            {
                try { return Canonical(DateTime.FromOADate(serial), type); }
                catch (ArgumentException) { throw new InvalidDataException("The SQLite date default is outside the native range."); }
            }
            if (type == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number.ToString("R", CultureInfo.InvariantCulture);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer.ToString(CultureInfo.InvariantCulture);
            throw new InvalidDataException("The SQLite default is not a native scalar literal.");
        }
        if (text.Length < 2 || text[0] is not ('\'' or '"') || text[^1] != text[0]) throw new InvalidDataException("The SQLite text default is not a native literal.");
        var quote = text[0]; var body = text[1..^1];
        for (var i = 0; i < body.Length; i++) if (body[i] == quote && (++i >= body.Length || body[i] != quote)) throw new InvalidDataException("The SQLite text default has an invalid quote.");
        return body.Replace(new string(quote, 2), quote.ToString(), StringComparison.Ordinal);
    }

    private static object? Scalar(string database, string sql, string table, string? column = null)
    {
        var sqlite = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Microsoft.Data.Sqlite");
        var builder = Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!)!;
        NativeV11Reflection.Set(builder, "DataSource", database); NativeV11Reflection.Set(builder, "Pooling", false);
        var mode = builder.GetType().GetProperty("Mode")!; mode.SetValue(builder, Enum.Parse(mode.PropertyType, "ReadOnly"));
        using var connection = (IDisposable)Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!, [builder.ToString()])!;
        NativeV11Reflection.Call(connection, "Open"); using var command = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!;
        NativeV11Reflection.Set(command, "CommandText", sql); var parameters = NativeV11Reflection.Get(command, "Parameters")!;
        NativeV11Reflection.Call(parameters, "AddWithValue", "$table", table);
        if (column is not null) NativeV11Reflection.Call(parameters, "AddWithValue", "$column", column);
        return NativeV11Reflection.Call(command, "ExecuteScalar");
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
    internal static bool OnlyConstraintChanged(string before, string after, string name, string kind)
    {
        JsonObject Clean(string json)
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("The native table definition is unavailable.");
            var columns = root["Columns"] switch { JsonArray array => array, JsonObject wrapper when wrapper["$values"] is JsonArray values => values,
                _ => throw new InvalidDataException("The native column definitions are unavailable.") };
            var matching = columns.OfType<JsonObject>().Where(column => column["Name"]?.GetValue<string>() == name).ToArray();
            if (matching.Length != 1) throw new InvalidDataException("The native constraint column is absent or ambiguous.");
            matching[0].Remove(kind == "Require" ? "Required" : "DefaultValue");
            return root;
        }
        return JsonNode.DeepEquals(Clean(before), Clean(after));
    }
    private static void VerifyBaseline(Plan plan)
    {
        var managerData = plan.Manager.GetType().GetProperties(NativeV11Reflection.Flags).Single(property => property.PropertyType.FullName == "Forguncy.LinkedDataSet" && property.GetIndexParameters().Length == 0).GetValue(plan.Manager) as DataSet;
        if (!ReferenceEquals(managerData?.Tables[plan.Baseline.Item.Address.Name]?.Columns[plan.Name], plan.CachedColumn) ||
            (NativeV11Reflection.Get(plan.Baseline.Item.Model, "Columns") as IEnumerable)?.Cast<object>().Any(column => ReferenceEquals(column, plan.Column)) != true)
            throw new InvalidOperationException("The native column cache or model identity changed.");
        VerifyData(plan.Database, plan.Baseline.Item.Address.Name, plan.Name, plan.Column, plan.CachedColumn, plan.Kind, plan.Value);
    }
    internal static void VerifyData(string database, string table, string name, object column, DataColumn cachedColumn, string kind, object? value)
    {
        if (!NativeV11BuiltInTableLifecycle.PhysicalColumnEligible(database, table, name) ||
            !NativeV11BuiltInTableLifecycle.ContainsPhysicalColumn(database, table, "FGC_Rowversion") ||
            !NativeV11BuiltInTableLifecycle.ContainsPhysicalColumn(database, table, "FGC_UpdateHelp") ||
            Convert.ToInt64(Scalar(database, "SELECT COUNT(*) FROM " + Quote(table) + " WHERE \"FGC_UpdateHelp\" IS NOT NULL", table)) != 0 ||
            Convert.ToInt64(Scalar(database, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name='ID' COLLATE BINARY AND pk=1", table)) != 1)
            throw new InvalidOperationException("The native SQLite rebuild would discard non-null update-help data or lacks a verified row key.");
        var type = (Type)NativeV11Reflection.Get(column, "ColumnType")!;
        var modelDefault = Canonical(NativeV11Reflection.Get(column, "DefaultValue"), type);
        var cachedDefault = Canonical(cachedColumn.DefaultValue, type);
        var sqlDefault = Scalar(database, "SELECT dflt_value FROM pragma_table_info($table) WHERE name=$column COLLATE BINARY", table, name);
        var physicalDefault = PhysicalDefault(sqlDefault, type);
        var required = NativeV11Reflection.Get(column, "Required") is true;
        var physicalRequired = Convert.ToInt64(Scalar(database, "SELECT \"notnull\" FROM pragma_table_info($table) WHERE name=$column COLLATE BINARY", table, name)) != 0;
        if (modelDefault != cachedDefault || modelDefault != physicalDefault || required != physicalRequired || required == cachedColumn.AllowDBNull)
            throw new InvalidOperationException("The native model, loaded cache, and SQLite constraint baseline differ.");
        if (kind == "Require" ? required == (bool)value! : modelDefault == Canonical(value, type))
            throw new InvalidOperationException("The native column constraint already has this value.");
        if (kind == "Require" && (bool)value! && (Convert.ToInt64(Scalar(database, "SELECT COUNT(*) FROM " + Quote(table) + " WHERE " + Quote(name) + " IS NULL", table)) != 0 ||
            cachedColumn.Table!.Rows.Cast<DataRow>().Any(row => row.RowState != DataRowState.Deleted && row.IsNull(cachedColumn))))
            throw new InvalidOperationException("A required column cannot contain null values.");
    }

    internal static async Task<object?> DispatchAsync(Assembly designer, MethodInfo method, object manager, object column, string table, string name, string kind, object? value)
    {
        var access = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.Utilities", true)!, "GetDesignerDataAccess") ?? throw new InvalidOperationException("Native data access is unavailable.");
        object? result;
        try
        {
            var task = method.Invoke(null, [access, table, name, Enum.Parse(method.GetParameters()[3].ParameterType, kind), value]) as Task<object>
                ?? throw new InvalidOperationException("The native column operation returned no task.");
            result = await task.ConfigureAwait(true);
        }
        finally { if (access is IAsyncDisposable asyncAccess) await asyncAccess.DisposeAsync().ConfigureAwait(true); else (access as IDisposable)?.Dispose(); }
        NativeV11Reflection.Call(manager, kind == "Require" ? "ChangeColumnRequired" : "ChangeColumnDefaultValue", table, name, kind == "Require" ? value : result);
        NativeV11Reflection.Set(column, kind == "Require" ? "Required" : "DefaultValue", kind == "Require" ? value : result);
        return result;
    }

    internal static IReadOnlyDictionary<string, string> DateTypeNormalization(object table)
    {
        var result = new Dictionary<string, string>(NativeV11BuiltInTableLifecycle.NativeDateTypeNormalization(table), StringComparer.Ordinal);
        var columns = NativeV11Reflection.Get(table, "Columns") as IEnumerable ?? throw new InvalidDataException("The native columns are unavailable.");
        foreach (var column in columns.Cast<object>())
            if (column.GetType().FullName == "Forguncy.Model.Tables.BindingColumn" && NativeV11Reflection.Get(column, "ColumnType") as Type == typeof(DateTime))
            {
                var name = NativeV11Reflection.Get(column, "Name") as string ?? throw new InvalidDataException("A native date column has no identity.");
                if (!result.TryAdd(name, "real")) throw new InvalidDataException("A native date column identity is duplicated.");
            }
        return result;
    }

    internal static async Task<NativeV11BuiltInTableLifecycle.CreatedTable> ApplyAsync(Plan plan)
    {
        var session = plan.Baseline.Session; session.VerifyContext(); NativeV11BuiltInTableLifecycle.VerifyPrivateNativeProvider(session);
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        if (!Path.GetFullPath((string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!).Equals(Path.GetFullPath(plan.Database), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native constraint database binding changed.");
        if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.Baseline.SourceRevision || !ReferenceEquals(session.Find(plan.Baseline.Item.Address).Model, plan.Baseline.Item.Model))
            throw new InvalidOperationException("The native constraint baseline changed.");
        VerifyBaseline(plan);
        var normalization = DateTypeNormalization(plan.Baseline.Item.Model);
        var modelBefore = session.Serialize(plan.Baseline.Item.Route, plan.Baseline.Item.Model);
        using var preservation = NativeV11SqlitePreservation.Capture(plan.Database);
        try
        {
            await DispatchAsync(session.Context.DesignerAssembly, plan.Method, plan.Manager, plan.Column, plan.Baseline.Item.Address.Name, plan.Name, plan.Kind, plan.Value);
            session.VerifyContext();
            var item = session.Find(plan.Baseline.Item.Address);
            if (!ReferenceEquals(item.Model, plan.Baseline.Item.Model) || !OnlyConstraintChanged(modelBefore, session.Serialize(item.Route, item.Model), plan.Name, plan.Kind))
                throw new InvalidOperationException("The native table owner or unrelated model data changed.");
            var expected = plan.Kind == "Require" ? null : Canonical(plan.Value, (Type)NativeV11Reflection.Get(plan.Column, "ColumnType")!);
            if (plan.Kind == "Require" ? (NativeV11Reflection.Get(plan.Column, "Required") is true) != (bool)plan.Value! || plan.CachedColumn.AllowDBNull == (bool)plan.Value! ||
                (Convert.ToInt64(Scalar(plan.Database, "SELECT \"notnull\" FROM pragma_table_info($table) WHERE name=$column COLLATE BINARY", item.Address.Name, plan.Name)) != 0) != (bool)plan.Value! :
                Canonical(NativeV11Reflection.Get(plan.Column, "DefaultValue"), (Type)NativeV11Reflection.Get(plan.Column, "ColumnType")!) != expected ||
                Canonical(plan.CachedColumn.DefaultValue, (Type)NativeV11Reflection.Get(plan.Column, "ColumnType")!) != expected ||
                PhysicalDefault(Scalar(plan.Database, "SELECT dflt_value FROM pragma_table_info($table) WHERE name=$column COLLATE BINARY", item.Address.Name, plan.Name), (Type)NativeV11Reflection.Get(plan.Column, "ColumnType")!) != expected)
                throw new InvalidOperationException("The native column constraint readback differs.");
            preservation.Verify(plan.Database, changedTable: item.Address.Name, oldColumn: plan.Name, newColumn: plan.Name, allowLiveWal: true,
                nativeTypeNormalization: normalization, changedConstraint: plan.Kind == "Require" ? "required" : "default");
            var database = NativeV11FirstProcessSaveJournal.SnapshotDatabase(plan.Database, allowLiveWal: true);
            NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name); NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
            NativeV11Generation.MarkModules(session, item.Route, "Edit"); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            var proof = new NativeV11BuiltInTableLifecycle.CreatePlan(session, item.Parent, plan.Baseline.TableSet, plan.Method, item.Address, 0, plan.Baseline.SourceRevision, plan.Baseline.SourceBefore, null);
            return new(proof, item.Model, session.Serialize(item.Route, item.Model), ExpectedColumn: plan.Name, ExpectedModifiedDatabase: database, NativeRowversionReset: true);
        }
        catch (Exception error) when (error is not NativeV11BuiltInTableLifecycle.NativeCreateUncertainException)
        { throw new NativeV11BuiltInTableLifecycle.NativeCreateUncertainException(error); }
    }
}
