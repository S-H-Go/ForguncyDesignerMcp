using System.Collections;
using System.IO;
using System.Reflection;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Loader;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Narrow native creation primitive. A failure after dispatch is uncertain
// because the native call can change the built-in database before returning.
internal static class NativeV11BuiltInTableLifecycle
{
    internal static bool IsBuiltInCreate(ModuleMutationRequest request) =>
        request.Address.Kind is ModuleKind.Table or ModuleKind.View && request.Operation == ModuleOperation.Create &&
        request.Document?.Metadata.ValueKind == JsonValueKind.Object && request.Document.Metadata.TryGetProperty("name", out _);

    internal static void ValidateRequestShape(ModuleMutationRequest request)
    {
        if (!IsBuiltInCreate(request) || request.Document is null || request.Document.Companions.Count != 0 ||
            request.Document.Metadata.EnumerateObject().Count() != (request.Address.Kind == ModuleKind.View ? 2 : 1) ||
            request.Document.Metadata.GetProperty("name").ValueKind != JsonValueKind.String ||
            !string.Equals(request.Document.Metadata.GetProperty("name").GetString(), request.Address.Name, StringComparison.Ordinal))
            throw new InvalidDataException("Built-in Create accepts the exact name, an explicit sqlBody for views, and no companions.");
        if (request.Address.Kind == ModuleKind.View) _ = ViewSql(request);
    }

    internal static string ViewSql(ModuleMutationRequest request)
    {
        if (request.Document is null || !request.Document.Metadata.TryGetProperty("sqlBody", out var value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("A built-in view requires an explicit sqlBody.");
        var sql = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(sql) || sql.Length > 32768 || !sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) ||
            sql.Length == 6 || !char.IsWhiteSpace(sql[6]) || sql.IndexOfAny([';', '\0']) >= 0 || sql.Contains("--", StringComparison.Ordinal) ||
            sql.Contains("/*", StringComparison.Ordinal) || sql.Contains("*/", StringComparison.Ordinal))
            throw new InvalidDataException("A built-in view requires a single SELECT body without comments or statement separators.");
        return sql;
    }

    internal sealed class NativeCreateUncertainException(Exception inner)
        : Exception("Native table creation may have changed the built-in database; freeze the project and do not retry.", inner) { }

    internal sealed record SourceGroup(string DefinitionPath, string DatabasePath, string? DatabaseSha256);
    internal sealed record RelatedTableSnapshot(NativeV11Item Item, string Json);
    internal sealed record RelationGraphProof(string ForeignTable, string ForeignColumn, string PrimaryTable, string PrimaryColumn, string? OtherRelations = null);
    internal sealed record CreatedTable(CreatePlan Plan, object Model, string ExpectedNativeJson, string? ExpectedColumn = null, string? ExpectedViewSql = null,
        (string Hash, long Length, bool Valid)? ExpectedUnchangedDatabase = null, string? ExpectedAbsentColumn = null, RelatedTableSnapshot? RelatedTable = null,
        (string Hash, long Length, bool Valid)? ExpectedModifiedDatabase = null, RelatedTableSnapshot? PreviousRelatedTable = null,
        RelationGraphProof? ExpectedRelationGraph = null, bool NativeRowversionReset = false);
    internal sealed record CreatePlan(NativeV11Session Session, object Parent, object TableSet,
        MethodInfo NativeCreate, ModuleAddress Address, int InsertIndex, string SourceRevision, SourceGroup SourceBefore, string? ViewSql);
    internal sealed record DeletePlan(NativeV11Session Session, NativeV11Item Item, object TableSet,
        MethodInfo NativeDelete, string SourceRevision, SourceGroup SourceBefore);
    internal sealed record DeletedTable(DeletePlan Plan);
    internal sealed record ColumnAdd(string Name, Type Type, string Comment);
    internal sealed record ColumnAddPlan(DeletePlan Baseline, MethodInfo NativeAdd, ColumnAdd Column, int Index);
    internal sealed record ViewEditPlan(DeletePlan Baseline, MethodInfo NativeEdit, string Sql);
    internal sealed record FormulaOperation(string Method, string Name, string Formula, Type Type, string Comment, string? NewName = null);
    internal sealed record FormulaPlan(DeletePlan Baseline, MethodInfo NativeMethod, FormulaOperation Operation, int Index);
    internal sealed record PhysicalColumnPlan(DeletePlan Baseline, MethodInfo NativeMethod, string Name, string? NewName, NativeV11ColumnConstraints.Plan? Constraint = null);

    internal static bool IsPhysicalColumnMutation(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.Table || request.Operation != ModuleOperation.Update || request.Document?.Metadata.ValueKind != JsonValueKind.Object ||
            !request.Document.Metadata.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() != 1 ||
            operations[0].ValueKind != JsonValueKind.Object || !operations[0].TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return false;
        return method.GetString() is "renameColumn" or "deleteColumn" or "setColumnDefault" or "setColumnRequired";
    }

    internal static PhysicalColumnPlan PreparePhysicalColumnMutation(NativeV11Session session, ModuleMutationRequest request)
    {
        if (!IsPhysicalColumnMutation(request) || request.Document!.Companions.Count != 0 || request.Document.Metadata.EnumerateObject().Count() != 1)
            throw new InvalidDataException("Exactly one physical column operation is required.");
        if (NativeV11ColumnConstraints.Supports(request))
        {
            var constraint = NativeV11ColumnConstraints.Prepare(session, request);
            return new(constraint.Baseline, constraint.Method, constraint.Name, null, constraint);
        }
        var operation = request.Document.Metadata.GetProperty("operations")[0];
        var rename = operation.GetProperty("method").GetString() == "renameColumn";
        if (operation.EnumerateObject().Count() != 2 || !operation.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            parameters.EnumerateObject().Count() != (rename ? 2 : 1) || parameters.EnumerateObject().Any(p => p.Name is not ("columnName" or "newName")) ||
            !parameters.TryGetProperty("columnName", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            rename && (!parameters.TryGetProperty("newName", out var value) || value.ValueKind != JsonValueKind.String))
            throw new InvalidDataException("Physical column operation parameters are invalid.");
        var name = nameValue.GetString()!; V11Safety.Address(new ModuleAddress(ModuleKind.Table, name));
        if (name.Equals("id", StringComparison.OrdinalIgnoreCase) || name.StartsWith("FGC_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Built-in identity and management columns require their native management workflow.");
        var newName = rename ? parameters.GetProperty("newName").GetString()! : null;
        if (newName is not null) { V11Safety.Address(new ModuleAddress(ModuleKind.Table, newName)); if (name.Equals(newName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The new name must differ."); }
        var baseline = PrepareDelete(session, request with { Operation = ModuleOperation.Delete, Document = null }, addingColumn: true);
        NativeV11TableLifecycle.ValidateRelationships(baseline.TableSet, request.Address.Name);
        var references = NativeV11References.Find(session, request.Address);
        if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException("Physical column changes require a complete incoming-reference scan.");
        var columns = NativeV11Reflection.Get(baseline.Item.Model, "Columns") as IList ?? throw new InvalidOperationException("The native columns are unavailable.");
        var matches = columns.Cast<object>().Where(column => string.Equals(NativeV11Reflection.Get(column, "Name") as string, name, StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0].GetType().FullName != "Forguncy.Model.Tables.BindingColumn" ||
            newName is not null && columns.Cast<object>().Any(column => string.Equals(NativeV11Reflection.Get(column, "Name") as string, newName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The native physical column is absent, unsupported or its new name is occupied.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (!PhysicalColumnEligible(database, request.Address.Name, name) || newName is not null && ContainsPhysicalColumn(database, request.Address.Name, newName))
            throw new InvalidOperationException("The physical column has a key/index/trigger/foreign-key dependency or the new name is occupied.");
        var manager = NativeV11Reflection.Get(session.Document, "TableSpreadManager") ?? throw new InvalidOperationException("The native table manager is unavailable.");
        if (!ReferenceEquals(NativeV11Reflection.Get(manager, "Workbook"), session.Workbook) || session.Sheet(request.Address.Name) is null)
            throw new InvalidOperationException("The native table worksheet must be loaded for a physical column operation.");
        var dataProperty = manager.GetType().GetProperties(NativeV11Reflection.Flags).Single(property =>
            property.PropertyType.FullName == "Forguncy.LinkedDataSet" && property.GetIndexParameters().Length == 0);
        if (dataProperty.GetValue(manager) is not System.Data.DataSet data || !data.Tables.Contains(request.Address.Name))
            throw new InvalidOperationException("The native table data cache must be loaded for a physical column operation.");
        var native = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var signature = rename ? new[] { typeof(string), typeof(string), typeof(string) } : [typeof(string), typeof(string)];
        var method = native.GetMethod(rename ? "RenameColumnAsync" : "DeleteColumnAsync", NativeV11Reflection.Flags, null, signature, null)
            ?? throw new MissingMethodException("The pinned native physical column operation is unavailable.");
        if (!method.IsStatic || method.ReturnType != (rename ? typeof(Task<bool>) : typeof(Task))) throw new NotSupportedException("The native physical column signature differs.");
        return new(baseline, method, name, newName);
    }

    internal static async Task<CreatedTable> ApplyPhysicalColumnMutationAsync(PhysicalColumnPlan plan)
    {
        if (plan.Constraint is not null) return await NativeV11ColumnConstraints.ApplyAsync(plan.Constraint).ConfigureAwait(true);
        var baseline = plan.Baseline; var session = baseline.Session;
        session.VerifyContext(); VerifyPrivateNativeProvider(session);
        if (NativeV11SourceRevision.Capture(session.SourcePath) != baseline.SourceRevision || !ReferenceEquals(session.Find(baseline.Item.Address).Model, baseline.Item.Model))
            throw new InvalidOperationException("The physical column baseline changed.");
        var references = NativeV11References.Find(session, baseline.Item.Address);
        if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException("The physical column reference baseline changed.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (!PhysicalColumnEligible(database, baseline.Item.Address.Name, plan.Name)) throw new InvalidOperationException("The physical column dependency baseline changed.");
        var normalization = NativeDateTypeNormalization(baseline.Item.Model);
        using var preservation = NativeV11SqlitePreservation.Capture(database);
        try
        {
            object[] args = plan.NewName is null ? [baseline.Item.Address.Name, plan.Name] : [baseline.Item.Address.Name, plan.Name, plan.NewName];
            var task = plan.NativeMethod.Invoke(null, args) as Task ?? throw new InvalidOperationException("The native column operation returned no task.");
            await task.ConfigureAwait(true);
            if (plan.NewName is not null && task is Task<bool> renamed && !renamed.Result) throw new InvalidOperationException("Native column rename did not report success.");
            session.VerifyContext(); var item = session.Find(baseline.Item.Address);
            if (!ReferenceEquals(item.Model, baseline.Item.Model) || NativeV11Reflection.Get(item.Model, "Columns") is not IEnumerable columns ||
                columns.Cast<object>().Any(column => NativeV11Reflection.Get(column, "Name") as string == plan.Name) ||
                plan.NewName is not null && columns.Cast<object>().Count(column => NativeV11Reflection.Get(column, "Name") as string == plan.NewName && column.GetType().FullName == "Forguncy.Model.Tables.BindingColumn") != 1 ||
                ContainsPhysicalColumn(database, item.Address.Name, plan.Name) || plan.NewName is not null && !ContainsPhysicalColumn(database, item.Address.Name, plan.NewName))
                throw new InvalidOperationException("The native physical column readback differs.");
            preservation.Verify(database, changedTable: item.Address.Name, oldColumn: plan.Name, newColumn: plan.NewName, allowLiveWal: true,
                nativeTypeNormalization: normalization);
            var expectedDatabase = NativeV11FirstProcessSaveJournal.SnapshotDatabase(database, allowLiveWal: true);
            NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name); NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
            NativeV11Generation.MarkModules(session, item.Route, "Edit"); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            var proof = new CreatePlan(session, item.Parent, baseline.TableSet, plan.NativeMethod, item.Address, 0, baseline.SourceRevision, baseline.SourceBefore, null);
            return new(proof, item.Model, session.Serialize(item.Route, item.Model), ExpectedColumn: plan.NewName, ExpectedAbsentColumn: plan.Name,
                ExpectedModifiedDatabase: expectedDatabase);
        }
        catch (Exception error) when (error is not NativeCreateUncertainException) { throw new NativeCreateUncertainException(error); }
    }

    internal static IReadOnlyDictionary<string, string> NativeDateTypeNormalization(object table)
    {
        var extension = Assembly.Load("ServerDesignerCommon").GetType("ServerDesignerCommon.Services.TableExtensions", true)!;
        var databaseType = NativeV11Reflection.Static(extension, "DataBaseType", table, null) as string;
        if (string.IsNullOrEmpty(databaseType)) throw new InvalidOperationException("The native table provider is unavailable.");
        var factory = Assembly.Load("ForguncyDataBaseService").GetType("ForguncyDataAccess.DBUtilitiesFactory", true)!;
        var utility = NativeV11Reflection.Static(factory, "CreateInstance", databaseType) ?? throw new InvalidOperationException("The native database type mapper is unavailable.");
        if (NativeV11Reflection.Call(utility, "GetDatabaseType", typeof(DateTime)) as Type != typeof(double))
            throw new InvalidOperationException("The native SQLite DateTime storage type is not the pinned double mapping.");
        var columns = NativeV11Reflection.Get(table, "Columns") as IEnumerable ?? throw new InvalidOperationException("The native table columns are unavailable.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var column in columns.Cast<object>())
        {
            var name = NativeV11Reflection.Get(column, "Name") as string;
            if (name is not ("FGC_CreateDate" or "FGC_LastModifyDate")) continue;
            if (column.GetType().FullName != "Forguncy.Model.Tables.DateExtraBingingColumn" || NativeV11Reflection.Get(column, "ColumnType") as Type != typeof(DateTime) ||
                !result.TryAdd(name, "real"))
                throw new InvalidOperationException("The native date column differs from its pinned model.");
        }
        return result;
    }

    internal static bool IsFormulaOperation(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.Table || request.Operation != ModuleOperation.Update ||
            request.Document?.Metadata.ValueKind != JsonValueKind.Object || !request.Document.Metadata.TryGetProperty("operations", out var operations) ||
            operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() != 1 || operations[0].ValueKind != JsonValueKind.Object ||
            !operations[0].TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return false;
        return method.GetString() is "addFormulaColumn" or "deleteFormulaColumn" or "renameFormulaColumn";
    }

    internal static FormulaOperation ParseFormulaOperation(ModuleMutationRequest request)
    {
        if (!IsFormulaOperation(request) || request.Document!.Companions.Count != 0 || request.Document.Metadata.EnumerateObject().Count() != 1)
            throw new InvalidDataException("Exactly one native formula column operation is required.");
        var operation = request.Document.Metadata.GetProperty("operations")[0];
        if (operation.EnumerateObject().Count() != 2 || !operation.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            parameters.EnumerateObject().Any(p => p.Name is not ("columnName" or "newName" or "formula" or "type" or "comment")) ||
            parameters.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != parameters.EnumerateObject().Count() ||
            !parameters.TryGetProperty("columnName", out var nameValue) || nameValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("The formula column parameters are invalid.");
        var name = nameValue.GetString()!; V11Safety.Address(new ModuleAddress(ModuleKind.Table, name));
        var method = operation.GetProperty("method").GetString()!;
        if (method == "deleteFormulaColumn")
        {
            if (parameters.EnumerateObject().Count() != 1) throw new InvalidDataException("Formula deletion accepts only columnName.");
            return new(method, name, "", typeof(string), "");
        }
        if (method == "renameFormulaColumn")
        {
            if (parameters.EnumerateObject().Count() != 2 || !parameters.TryGetProperty("newName", out var value) || value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Formula rename requires columnName and newName.");
            var newName = value.GetString()!; V11Safety.Address(new ModuleAddress(ModuleKind.Table, newName));
            if (name.Equals(newName, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Formula rename requires a distinct name.");
            return new(method, name, "", typeof(string), "", newName);
        }
        if (parameters.TryGetProperty("newName", out _) || !parameters.TryGetProperty("formula", out var formulaValue) || formulaValue.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("A formula and type are required.");
        var formula = formulaValue.GetString()!;
        if (string.IsNullOrWhiteSpace(formula) || formula.Length > 32768 || formula.Contains("#REF!", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The formula is invalid.");
        var type = typeValue.GetString() switch { "Text" => typeof(string), "Number" => typeof(double), "Integer" => typeof(long),
            "Boolean" => typeof(bool), "DateTime" => typeof(DateTime), _ => throw new NotSupportedException("Unsupported formula column type.") };
        var comment = parameters.TryGetProperty("comment", out var commentValue) && commentValue.ValueKind == JsonValueKind.String ? commentValue.GetString()! : "";
        if (parameters.TryGetProperty("comment", out commentValue) && commentValue.ValueKind != JsonValueKind.String || comment.Length > 4096)
            throw new InvalidDataException("The formula column comment is invalid.");
        return new(method, name, formula, type, comment);
    }

    internal static FormulaPlan PrepareFormula(NativeV11Session session, ModuleMutationRequest request)
    {
        var operation = ParseFormulaOperation(request);
        var baseline = PrepareDelete(session, request with { Operation = ModuleOperation.Delete, Document = null }, addingColumn: true);
        var columns = NativeV11Reflection.Get(baseline.Item.Model, "Columns") as IList ?? throw new InvalidOperationException("The native column collection is unavailable.");
        var matches = columns.Cast<object>().Where(column => string.Equals(NativeV11Reflection.Get(column, "Name") as string, operation.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (operation.Method == "addFormulaColumn" ? matches.Length != 0 : matches.Length != 1 || matches[0].GetType().FullName != "Forguncy.Model.Tables.FormulaColumn")
            throw new InvalidOperationException("The native formula column identity is unavailable or occupied.");
        if (operation.Method is "deleteFormulaColumn" or "renameFormulaColumn")
        {
            var references = NativeV11References.Find(session, request.Address);
            if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException("Formula deletion requires a complete incoming-reference scan.");
        }
        if (operation.NewName is not null && columns.Cast<object>().Any(column => string.Equals(NativeV11Reflection.Get(column, "Name") as string, operation.NewName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The new formula column name is already occupied.");
        var manager = NativeV11Reflection.Get(session.Document, "TableSpreadManager") ?? throw new InvalidOperationException("The native table manager is unavailable.");
        if (!ReferenceEquals(NativeV11Reflection.Get(manager, "Workbook"), session.Workbook) || session.Sheet(request.Address.Name) is null)
            throw new InvalidOperationException("The native table worksheet must be loaded for formula operations.");
        var native = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var adding = operation.Method == "addFormulaColumn";
        var signature = adding ? new[] { typeof(string), typeof(string), typeof(string), typeof(Type), typeof(string), typeof(int) } :
            operation.NewName is not null ? [typeof(string), typeof(string), typeof(string)] : [typeof(string), typeof(string)];
        var method = native.GetMethod(adding ? "AddFormulaColumnAsync" : operation.NewName is not null ? "RenameColumnAsync" : "DeleteColumnAsync", NativeV11Reflection.Flags, null, signature, null)
            ?? throw new MissingMethodException("The pinned native formula column method is unavailable.");
        if (!method.IsStatic || method.ReturnType != (operation.NewName is not null ? typeof(Task<bool>) : typeof(Task))) throw new NotSupportedException("The native formula column signature differs.");
        return new(baseline, method, operation, columns.Count);
    }

    internal static async Task<CreatedTable> ApplyFormulaAsync(FormulaPlan plan)
    {
        var baseline = plan.Baseline; var session = baseline.Session; var operation = plan.Operation;
        session.VerifyContext(); VerifyPrivateNativeProvider(session);
        if (NativeV11SourceRevision.Capture(session.SourcePath) != baseline.SourceRevision || !ReferenceEquals(session.Find(baseline.Item.Address).Model, baseline.Item.Model))
            throw new InvalidOperationException("The native formula column baseline changed.");
        if (operation.Method is "deleteFormulaColumn" or "renameFormulaColumn")
        {
            var references = NativeV11References.Find(session, baseline.Item.Address);
            if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException("The formula reference baseline changed.");
        }
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        var databaseBefore = NativeV11FirstProcessSaveJournal.SnapshotDatabase(database);
        try
        {
            var args = operation.Method == "addFormulaColumn" ? new object[] { baseline.Item.Address.Name, operation.Name, operation.Formula, operation.Type, operation.Comment, plan.Index }
                : operation.NewName is not null ? [baseline.Item.Address.Name, operation.Name, operation.NewName] : [baseline.Item.Address.Name, operation.Name];
            var task = plan.NativeMethod.Invoke(null, args) as Task ?? throw new InvalidOperationException("The native formula operation returned no task.");
            await task.ConfigureAwait(true);
            if (operation.NewName is not null && task is Task<bool> renamed && !renamed.Result)
                throw new InvalidOperationException("The native formula rename did not report success.");
            session.VerifyContext(); var item = session.Find(baseline.Item.Address);
            if (!ReferenceEquals(item.Model, baseline.Item.Model) || NativeV11Reflection.Get(item.Model, "Columns") is not IEnumerable columns)
                throw new InvalidOperationException("The native table identity changed.");
            var matches = columns.Cast<object>().Where(column => string.Equals(NativeV11Reflection.Get(column, "Name") as string, operation.Name, StringComparison.Ordinal)).ToArray();
            if (operation.Method == "addFormulaColumn" ? matches.Length != 1 || matches[0].GetType().FullName != "Forguncy.Model.Tables.FormulaColumn" ||
                NativeV11Reflection.Get(matches[0], "Formula") as string != operation.Formula : matches.Length != 0)
                throw new InvalidOperationException("The native formula column readback differs.");
            if (operation.NewName is not null && columns.Cast<object>().Count(column => column.GetType().FullName == "Forguncy.Model.Tables.FormulaColumn" &&
                NativeV11Reflection.Get(column, "Name") as string == operation.NewName) != 1)
                throw new InvalidOperationException("The renamed native formula column is unavailable.");
            if (NativeV11FirstProcessSaveJournal.SnapshotDatabase(database) != databaseBefore)
                throw new InvalidOperationException("A definition-only formula operation changed the native SQLite database.");
            NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name);
            NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
            NativeV11Generation.MarkModules(session, item.Route, "Edit"); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            var proof = new CreatePlan(session, item.Parent, baseline.TableSet, plan.NativeMethod, item.Address, plan.Index, baseline.SourceRevision, baseline.SourceBefore, null);
            return new(proof, item.Model, session.Serialize(item.Route, item.Model), ExpectedUnchangedDatabase: databaseBefore);
        }
        catch (Exception error) when (error is not NativeCreateUncertainException) { throw new NativeCreateUncertainException(error); }
    }

    internal static bool IsViewEdit(ModuleMutationRequest request) => request.Address.Kind == ModuleKind.View &&
        request.Operation == ModuleOperation.Update && request.Document?.Metadata.ValueKind == JsonValueKind.Object &&
        request.Document.Metadata.TryGetProperty("sqlBody", out _);

    internal static ViewEditPlan PrepareViewEdit(NativeV11Session session, ModuleMutationRequest request)
    {
        if (!IsViewEdit(request) || request.Document!.Companions.Count != 0 || request.Document.Metadata.EnumerateObject().Count() != 1)
            throw new InvalidDataException("A native view update requires only sqlBody.");
        var sql = ViewSql(request);
        var baseline = PrepareDelete(session, request with { Operation = ModuleOperation.Delete, Document = null }, addingColumn: true);
        var references = NativeV11References.Find(session, request.Address);
        if (!references.Complete || references.References.Count != 0)
            throw new InvalidOperationException("A view SQL update requires a complete scan proving there are no incoming references.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (ViewSqlMatches(database, request.Address.Name, sql)) throw new InvalidOperationException("The native view already has this SQL body.");
        var native = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var method = native.GetMethod("EditViewContentAsync", NativeV11Reflection.Flags, null, [typeof(string), typeof(string)], null)
            ?? throw new MissingMethodException("The pinned native view update signature is unavailable.");
        if (!method.IsStatic || method.ReturnType != typeof(Task)) throw new NotSupportedException("The native view update signature differs.");
        return new(baseline, method, sql);
    }

    internal static async Task<CreatedTable> EditViewAsync(ViewEditPlan plan)
    {
        var baseline = plan.Baseline; var session = baseline.Session;
        session.VerifyContext(); VerifyPrivateNativeProvider(session);
        if (NativeV11SourceRevision.Capture(session.SourcePath) != baseline.SourceRevision ||
            !ReferenceEquals(session.Find(baseline.Item.Address).Model, baseline.Item.Model))
            throw new InvalidOperationException("The native view changed before update.");
        var references = NativeV11References.Find(session, baseline.Item.Address);
        if (!references.Complete || references.References.Count != 0)
            throw new InvalidOperationException("The native view reference baseline changed before update.");
        var beforeConfig = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var beforeDatabase = (string)NativeV11Reflection.Static(beforeConfig, "get_LocalAppDBSqliteFilePath")!;
        using var preservation = NativeV11SqlitePreservation.Capture(beforeDatabase);
        try
        {
            var task = plan.NativeEdit.Invoke(null, [baseline.Item.Address.Name, plan.Sql]) as Task
                ?? throw new InvalidOperationException("The native view update did not return a task.");
            await task.ConfigureAwait(true);
            session.VerifyContext();
            var item = session.Find(baseline.Item.Address);
            if (!ReferenceEquals(item.Model, baseline.Item.Model)) throw new InvalidOperationException("The native view model identity changed.");
            var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
            var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
            if (!ViewSqlMatches(database, item.Address.Name, plan.Sql)) throw new InvalidOperationException("The native view SQL readback differs.");
            preservation.Verify(database, item.Address.Name, allowLiveWal: true);
            var expectedDatabase = NativeV11FirstProcessSaveJournal.SnapshotDatabase(database, allowLiveWal: true);
            var sheet = session.Sheet(item.Address.Name);
            NativeV11TableWriter.VerifyOwners(baseline.TableSet, item.Model, item.Parent, sheet,
                sheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "TableInfo"));
            NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name);
            NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
            NativeV11Generation.MarkModules(session, item.Route, "Edit");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            var proof = new CreatePlan(session, item.Parent, baseline.TableSet, plan.NativeEdit, item.Address, 0,
                baseline.SourceRevision, baseline.SourceBefore, null);
            return new(proof, item.Model, session.Serialize(item.Route, item.Model), ExpectedViewSql: plan.Sql, ExpectedModifiedDatabase: expectedDatabase);
        }
        catch (Exception error) when (error is not NativeCreateUncertainException) { throw new NativeCreateUncertainException(error); }
    }

    internal static bool IsColumnAdd(ModuleMutationRequest request) => request.Address.Kind == ModuleKind.Table &&
        request.Operation == ModuleOperation.Update && request.Document?.Metadata.ValueKind == JsonValueKind.Object &&
        request.Document.Metadata.TryGetProperty("operations", out _);

    internal static ColumnAdd ParseColumnAdd(ModuleMutationRequest request)
    {
        var metadata = request.Document?.Metadata ?? throw new InvalidDataException("A column operation is required.");
        if (!IsColumnAdd(request) || request.Document!.Companions.Count != 0 || metadata.EnumerateObject().Count() != 1 ||
            metadata.GetProperty("operations") is not { ValueKind: JsonValueKind.Array } operations || operations.GetArrayLength() != 1)
            throw new InvalidDataException("One built-in table column operation is required.");
        var operation = operations[0];
        if (operation.ValueKind != JsonValueKind.Object || operation.EnumerateObject().Count() != 2 ||
            !operation.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || method.GetString() != "addColumn" ||
            !operation.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object ||
            parameters.EnumerateObject().Any(p => p.Name is not ("columnName" or "type" or "comment" or "required" or "unique")) ||
            parameters.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != parameters.EnumerateObject().Count())
            throw new InvalidDataException("Only the native addColumn operation is currently supported.");
        if (!parameters.TryGetProperty("columnName", out var column) || column.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("A column name and type are required.");
        var name = column.GetString()!;
        V11Safety.Address(new ModuleAddress(ModuleKind.Table, name));
        foreach (var flag in new[] { "required", "unique" })
            if (parameters.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                throw new NotSupportedException("New required or unique columns need their separate native constraint lifecycle.");
        var columnType = type.GetString() switch
        {
            "Text" => typeof(string), "Number" => typeof(double), "Integer" => typeof(long),
            "Boolean" => typeof(bool), "DateTime" => typeof(DateTime), _ => throw new NotSupportedException("Unsupported native column type.")
        };
        var comment = "";
        if (parameters.TryGetProperty("comment", out var commentValue))
        {
            if (commentValue.ValueKind != JsonValueKind.String || (comment = commentValue.GetString()!).Length > 4096)
                throw new InvalidDataException("The column comment is invalid.");
        }
        return new(name, columnType, comment);
    }

    internal static CreatePlan PrepareCreate(NativeV11Session session, ModuleAddress address, string? viewSql = null)
    {
        session.VerifyContext();
        if (address.Kind is not (ModuleKind.Table or ModuleKind.View) || address.FolderPath is not null ||
            (address.Kind == ModuleKind.View) != (viewSql is not null))
            throw new NotSupportedException("Only a root built-in table or explicit-query view is supported.");
        V11Safety.Address(address);
        var collaboration = session.Context.CaptureSnapshot().InCollaborationMode;
        if (collaboration && !Directory.Exists(session.SourcePath) || !collaboration && !File.Exists(session.SourcePath))
            throw new NotSupportedException("Built-in table creation requires its saved native source.");
        VerifyPrivateNativeProvider(session);
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        if (ContainsPhysicalTable((string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!, address.Name, includeViews: true))
            throw new InvalidOperationException("A native SQLite table or view already uses this name.");
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var tableSet = NativeV11Reflection.Get(settings, "TableSet")!;
        var parent = NativeV11Reflection.Get(tableSet, "Tables")!;
        if (parent is not IList list || parent.GetType().FullName != "Forguncy.Model.Tables.TableCollection")
            throw new InvalidOperationException("The native root table collection is unavailable.");
        if (session.Items(ModuleKind.Table).Concat(session.Items(ModuleKind.View)).Any(item =>
            string.Equals(item.Address.Name, address.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A native table with this name already exists.");
        var sheets = NativeV11Reflection.Get(session.Workbook, "Sheets") as IEnumerable
            ?? throw new InvalidOperationException("The native worksheet collection is unavailable.");
        if (sheets.Cast<object>().Any(sheet => string.Equals(NativeV11Reflection.Get(sheet, "Name") as string, address.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A native worksheet already uses the requested table name.");
        var method = address.Kind == ModuleKind.Table ? ResolveNativeCreate(session.Context.DesignerAssembly, parent.GetType()) : ResolveNativeViewCreate(session.Context.DesignerAssembly);
        var revision = NativeV11SourceRevision.Capture(session.SourcePath);
        var before = CaptureSourceGroup(session, address);
        VerifyDatabaseBaseline(session, before);
        if (!string.Equals(revision, NativeV11SourceRevision.Capture(session.SourcePath), StringComparison.Ordinal))
            throw new InvalidOperationException("The source changed while capturing the native table owner group.");
        return new(session, parent, tableSet, method, address, list.Count, revision, before, viewSql);
    }

    internal static MethodInfo ResolveNativeViewCreate(Assembly designer)
    {
        var profile = V11BuildProfile.Resolve(designer);
        var native = designer.GetType(profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var viewInfo = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.CreateViewInfo", true)!;
        var model = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.Table", true)!;
        var method = native.GetMethod("CreateViewInDBAsync", NativeV11Reflection.Flags, null, [viewInfo, typeof(string), typeof(bool), typeof(bool)], null)
            ?? throw new MissingMethodException(native.FullName, "CreateViewInDBAsync");
        if (!method.IsStatic || method.ReturnType != typeof(Task<>).MakeGenericType(model))
            throw new NotSupportedException("The pinned native built-in view creation signature differs.");
        return method;
    }

    internal static SourceGroup CaptureSourceGroup(NativeV11Session session, ModuleAddress address, bool requireDefinition = false)
    {
        var common = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "CommonUtilities")
            ?? throw new InvalidOperationException("Native common utilities are not loaded.");
        var config = common.GetType("CommonUtilities.SystemConfigDef", true)!;
        var database = config.GetField("ForguncyDB_sqlite3_File", NativeV11Reflection.Flags)?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(database) || database != Path.GetFileName(database) || database.Contains(':'))
            throw new InvalidOperationException("The native built-in database owner filename is invalid.");
        var definition = session.NativePath(address);
        return CaptureSourceGroup(session.SourcePath, definition, database, requireDefinition);
    }

    internal static SourceGroup CaptureSourceGroup(string project, string definition, string database, bool requireDefinition = false)
    {
        if (string.IsNullOrWhiteSpace(database) || database != Path.GetFileName(database) || database.Contains(':'))
            throw new InvalidOperationException("The native built-in database owner filename is invalid.");
        _ = V11Safety.Under(Path.GetDirectoryName(Path.GetFullPath(project))!, definition);
        if (Directory.Exists(project))
        {
            var definitionPath = V11Safety.Under(project, definition);
            var databasePath = V11Safety.Under(project, database);
            if (File.Exists(definitionPath) != requireDefinition || Directory.Exists(definitionPath) || Directory.Exists(databasePath) ||
                requireDefinition && !File.Exists(databasePath))
                throw new InvalidOperationException("The native table source group is missing or occupied.");
            if (!File.Exists(databasePath)) return new(definition, database, null);
            using var sourceDatabase = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (sourceDatabase.Length == 0) throw new InvalidOperationException("The native database owner is empty.");
            return new(definition, database, Convert.ToHexString(SHA256.HashData(sourceDatabase)));
        }
        using var stream = new FileStream(project, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries.Where(entry => entry.FullName.Replace('\\', '/').Equals(database, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (entries.Length > 1 || entries.Any(entry => entry.Length == 0))
            throw new InvalidOperationException("The native built-in database owner is invalid or ambiguous in the source FGCC.");
        var definitions = archive.Entries.Count(entry => entry.FullName.Replace('\\', '/').Equals(definition, StringComparison.OrdinalIgnoreCase));
        if (definitions != (requireDefinition ? 1 : 0))
            throw new InvalidOperationException("The native table definition baseline is missing or ambiguous.");
        if (requireDefinition && entries.Length != 1)
            throw new InvalidOperationException("The native table database is missing from the source FGCC.");
        if (entries.Length == 0) return new(definition, database, null);
        using var input = entries[0].Open();
        return new(definition, database, Convert.ToHexString(SHA256.HashData(input)));
    }

    internal static void VerifyDatabaseBaseline(NativeV11Session session, SourceGroup before)
    {
        var local = V11Safety.Under(session.LocalPath, before.DatabasePath);
        if (!File.Exists(local) || !NoPendingSqliteJournal(local))
            throw new InvalidOperationException("The native local database is absent or has an active journal.");
        if (before.DatabaseSha256 is null) return;
        using var localStream = File.OpenRead(local);
        if (Directory.Exists(session.SourcePath))
        {
            var path = V11Safety.Under(session.SourcePath, before.DatabasePath);
            if (!NoPendingSqliteJournal(path)) throw new InvalidOperationException("The source database has an active journal.");
            using var source = File.OpenRead(path);
            if (!EquivalentDatabase(localStream, source)) throw new InvalidOperationException("The native local and source database baselines differ.");
            return;
        }
        using var input = File.OpenRead(session.SourcePath);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        var matches = archive.Entries.Where(e => e.FullName.Replace('\\', '/').Equals(before.DatabasePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || archive.Entries.Any(e =>
            (e.FullName.Equals(before.DatabasePath + "-wal", StringComparison.OrdinalIgnoreCase) ||
             e.FullName.Equals(before.DatabasePath + "-journal", StringComparison.OrdinalIgnoreCase)) && e.Length != 0))
            throw new InvalidOperationException("The source database owner is ambiguous or has an active journal.");
        using var sourceEntry = matches[0].Open();
        if (!EquivalentDatabase(localStream, sourceEntry)) throw new InvalidOperationException("The native local and source database baselines differ.");
    }

    internal static MethodInfo ResolveNativeCreate(Assembly designer, Type collectionType)
    {
        var profile = V11BuildProfile.Resolve(designer);
        if (collectionType.FullName != "Forguncy.Model.Tables.TableCollection" ||
            collectionType.Assembly.GetName().Version?.ToString() != profile.Version)
            throw new NotSupportedException("The native table collection differs from the pinned build.");
        var nativeType = designer.GetType(profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var method = nativeType.GetMethod("CreateTableAsync", NativeV11Reflection.Flags, null,
            [typeof(string), collectionType, typeof(string), typeof(int)], null)
            ?? throw new MissingMethodException(nativeType.FullName, "CreateTableAsync");
        if (!method.IsStatic || method.ReturnType != typeof(Task<string>))
            throw new NotSupportedException("The pinned native built-in table creation signature differs.");
        return method;
    }

    internal static void VerifyPrivateNativeProvider(NativeV11Session session)
    {
        var designer = session.Context.DesignerAssembly;
        var config = designer.GetType("Forguncy.Common.Config", true)!;
        var core = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "ForguncyCoreUtilities")
            ?? throw new InvalidOperationException("The actual native built-in provider assembly is not loaded.");
        var provider = core.GetType("CommonUtilities.BuiltInDataBase.AppBuiltInDbConfig", true)!;
        if (NativeV11Reflection.Static(provider, "get_BuiltInDbIsSqlite") is not true)
            throw new NotSupportedException("Only the native built-in SQLite provider is allowed.");
        // The native CreateTableAsync asks the live database service for both
        // type and connection string using (null, null); checking Config alone
        // would not prove that this service resolves to that private database.
        var service = NativeV11Reflection.Get(session.NativeApplication, "DatabaseService")
            ?? throw new InvalidOperationException("The actual native database service is unavailable.");
        if (service.GetType() != designer.GetType("Forguncy.Common.DesignerDatabaseService", true))
            throw new NotSupportedException("The native database service implementation differs from the pinned Designer service.");
        var expectedConnection = NativeV11Reflection.Static(config, "get_BuiltInDbConnectStr") as string;
        var expectedType = NativeV11Reflection.Static(provider, "get_DefaultDataBaseType") as string;
        var actualConnection = NativeV11Reflection.Call(service, "GetConnectionString", null, null) as string;
        var actualType = NativeV11Reflection.Call(service, "GetConnectionDbType", null, null) as string;
        if (string.IsNullOrEmpty(expectedConnection) || string.IsNullOrEmpty(expectedType) ||
            !string.Equals(actualConnection, expectedConnection, StringComparison.Ordinal) ||
            !string.Equals(actualType, expectedType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native database service does not resolve the bound built-in SQLite provider.");
        var database = NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath") as string;
        if (string.IsNullOrWhiteSpace(database)) throw new InvalidOperationException("Native built-in database path is unavailable.");
        var loadContext = AssemblyLoadContext.GetLoadContext(designer)
            ?? throw new InvalidOperationException("The Designer assembly load context is unavailable.");
        var sqlite = loadContext.Assemblies.SingleOrDefault(a => a.GetName().Name == "Microsoft.Data.Sqlite")
            ?? loadContext.LoadFromAssemblyName(new AssemblyName("Microsoft.Data.Sqlite"));
        var designerRoot = Path.GetDirectoryName(designer.Location)!;
        var webRoot = Directory.GetParent(designerRoot)!.FullName;
        var allowedProviderRoots = new[] { designerRoot, Path.Combine(webRoot, "bin") };
        if (string.IsNullOrEmpty(sqlite.Location) || !allowedProviderRoots.Any(root =>
            Path.GetFullPath(sqlite.Location).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The SQLite provider is not from the pinned Designer installation.");
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var builder = Activator.CreateInstance(builderType, [actualConnection])
            ?? throw new InvalidOperationException("The native SQLite connection cannot be parsed.");
        var source = NativeV11Reflection.Get(builder, "DataSource") as string;
        var mode = NativeV11Reflection.Get(builder, "Mode")?.ToString();
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source) ||
            !string.Equals(Path.GetFullPath(source), Path.GetFullPath(database), StringComparison.OrdinalIgnoreCase) ||
            mode is "Memory" or "ReadOnly")
            throw new InvalidOperationException("The effective SQLite connection does not target the bound writable native database.");
        VerifyPrivateDatabasePath(session.LocalPath, database);
    }

    internal static void VerifyPrivateDatabasePath(string localPath, string database)
    {
        var local = Path.GetFullPath(localPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(database);
        if (!full.StartsWith(local, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new InvalidOperationException("The actual native SQLite database is not inside the bound private save workspace.");
        for (var path = full; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The native database path contains a reparse point.");
    }

    internal static bool IsBuiltInDelete(NativeV11Session session, ModuleMutationRequest request)
    {
        if (request.Address.Kind is not (ModuleKind.Table or ModuleKind.View) || request.Operation != ModuleOperation.Delete) return false;
        var model = session.Find(request.Address).Model;
        return model.GetType().FullName == "Forguncy.Model.Tables.Table" &&
            NativeV11Reflection.Get(model, "ExternalDatabaseInfo") is null;
    }

    internal static DeletePlan PrepareDelete(NativeV11Session session, ModuleMutationRequest request, bool addingColumn = false)
    {
        session.VerifyContext();
        var collaboration = session.Context.CaptureSnapshot().InCollaborationMode;
        if (!IsBuiltInDelete(session, request) || request.Document is not null || request.Scope != EffectScope.ProjectDefinition ||
            request.Address.FolderPath is not null || collaboration && !Directory.Exists(session.SourcePath) ||
            !collaboration && !File.Exists(session.SourcePath))
            throw new NotSupportedException("A root built-in table or custom view requires its saved native source.");
        VerifyPrivateNativeProvider(session);
        var item = session.Find(request.Address);
        var model = item.Model;
        var isView = request.Address.Kind == ModuleKind.View;
        if (NativeV11Reflection.Get(model, "IsView") is not bool actualView || actualView != isView ||
            isView && NativeV11Reflection.Get(model, "IsBuiltInViewCustom") is not true || NativeV11Reflection.Get(model, "IsProcessTable") is true ||
            NativeV11Reflection.Get(model, "IsViewProcessInfo") is true || NativeV11Reflection.Get(model, "IsCopyExternalBuiltInTable") is true ||
            NativeV11Reflection.Get(model, "FlowProcess") is not null || NativeV11Reflection.Get(model, "Name") as string != request.Address.Name)
            throw new NotSupportedException("Views, process and nonmatching tables require their native lifecycle.");
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var tableSet = NativeV11Reflection.Get(settings, "TableSet")!;
        if (!ReferenceEquals(NativeV11Reflection.Get(tableSet, "Tables"), item.Parent) || NativeV11Reflection.Get(tableSet, "IsUpdating") is true)
            throw new InvalidOperationException("The native root table collection is not stable.");
        var application = NativeV11Reflection.Get(settings, "ApplicationSettings")!;
        if (NativeV11Reflection.Get(application, "AuditingList") is IDictionary auditing &&
            auditing.Keys.Cast<object>().OfType<string>().Any(n => n.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(NativeV11Reflection.Get(application, "StatusCacheTableName") as string, request.Address.Name, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("Audit and status-cache tables require their native management workflow.");
        if (!addingColumn) NativeV11TableLifecycle.ValidateRelationships(tableSet, request.Address.Name);
        // In 11.0.3 IsInternalTableModule is exactly ExternalDatabaseInfo == null:
        // it classifies every ordinary built-in table as internal. The 104
        // IsSystemTable/IsSystemView checks have a narrower native meaning.
        if (session.Profile.Version == "11.0.104.0")
            NativeV11TableLifecycle.ValidateSystemIdentity(session.Context.DesignerAssembly, application, model);
        var sheet = session.Sheet(request.Address.Name);
        NativeV11TableWriter.VerifyOwners(tableSet, model, item.Parent, sheet,
            sheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "TableInfo"));
        var sheets = (IList)NativeV11Reflection.Get(session.Workbook, "Sheets")!;
        var active = Convert.ToInt32(NativeV11Reflection.Get(session.Workbook, "ActiveSheetIndex"));
        if (!addingColumn && sheet is not null && active >= 0 && active < sheets.Count && ReferenceEquals(sheets[active], sheet))
            throw new InvalidOperationException("Switch to an existing page before deleting a table.");
        var designer = session.Context.DesignerAssembly;
        var native = designer.GetType(session.Profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var method = native.GetMethod(isView ? "DeleteViewAsync" : "DeleteTableAsync", NativeV11Reflection.Flags, null, [item.Parent.GetType(), model.GetType()], null)
            ?? throw new MissingMethodException("The pinned native table delete signature is unavailable.");
        if (!method.IsStatic || method.ReturnType != typeof(Task)) throw new NotSupportedException("The native delete signature differs.");
        var revision = NativeV11SourceRevision.Capture(session.SourcePath);
        var before = CaptureSourceGroup(session, request.Address, true);
        VerifyDatabaseBaseline(session, before);
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var liveDatabase = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (!ContainsPhysicalTable(liveDatabase, request.Address.Name, onlyView: isView))
            throw new InvalidOperationException("The native built-in physical table is absent.");
        var bytes = NativeV11PendingSave.ReadFiles(session.SourcePath, [before.DefinitionPath])[before.DefinitionPath]
            ?? throw new InvalidOperationException("The native source definition is unavailable.");
        using var expected = JsonDocument.Parse(session.Serialize(item.Route, model));
        if (!V11Safety.Equivalent(expected.RootElement, V11Safety.Body(bytes)) || revision != NativeV11SourceRevision.Capture(session.SourcePath))
            throw new InvalidOperationException("The table source and native memory baseline differ.");
        return new(session, item, tableSet, method, revision, before);
    }

    internal static ColumnAddPlan PrepareColumnAdd(NativeV11Session session, ModuleMutationRequest request)
    {
        var column = ParseColumnAdd(request);
        var baseline = PrepareDelete(session, request with { Operation = ModuleOperation.Delete, Document = null }, addingColumn: true);
        var manager = NativeV11Reflection.Get(session.Document, "TableSpreadManager")
            ?? throw new InvalidOperationException("The native table manager is unavailable.");
        if (!ReferenceEquals(NativeV11Reflection.Get(manager, "Workbook"), session.Workbook) || session.Sheet(request.Address.Name) is null)
            throw new InvalidOperationException("The native table worksheet must be loaded before adding a column.");
        var dataProperty = manager.GetType().GetProperties(NativeV11Reflection.Flags).Single(property =>
            property.PropertyType.FullName == "Forguncy.LinkedDataSet" && property.GetIndexParameters().Length == 0);
        if (dataProperty.GetValue(manager) is not System.Data.DataSet data || !data.Tables.Contains(request.Address.Name))
            throw new InvalidOperationException("The native table data cache must be loaded before adding a column.");
        var columns = NativeV11Reflection.Get(baseline.Item.Model, "Columns") as IList
            ?? throw new InvalidOperationException("The native column collection is unavailable.");
        if (columns.Cast<object>().Any(c => string.Equals(NativeV11Reflection.Get(c, "Name") as string, column.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The native table already contains the column name.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (ContainsPhysicalColumn(database, request.Address.Name, column.Name))
            throw new InvalidOperationException("The native SQLite table already contains the column name.");
        var native = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var method = native.GetMethod("AddBindingColumnAsync", NativeV11Reflection.Flags, null,
            [typeof(string), typeof(string), typeof(Type), typeof(int), typeof(string)], null)
            ?? throw new MissingMethodException("The pinned native add-column signature is unavailable.");
        if (!method.IsStatic || method.ReturnType != typeof(Task<bool>)) throw new NotSupportedException("The native add-column signature differs.");
        return new(baseline, method, column, columns.Count);
    }

    internal static async Task<CreatedTable> AddColumnAsync(ColumnAddPlan plan)
    {
        var baseline = plan.Baseline; var session = baseline.Session;
        session.VerifyContext(); VerifyPrivateNativeProvider(session);
        if (NativeV11SourceRevision.Capture(session.SourcePath) != baseline.SourceRevision ||
            !ReferenceEquals(session.Find(baseline.Item.Address).Model, baseline.Item.Model))
            throw new InvalidOperationException("The native table changed before the column operation.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (ContainsPhysicalColumn(database, baseline.Item.Address.Name, plan.Column.Name))
            throw new InvalidOperationException("The native SQLite column name became occupied.");
        using var preservation = NativeV11SqlitePreservation.Capture(database);
        try
        {
            if (!await DispatchColumnAddAsync(plan.NativeAdd, baseline.Item.Address.Name, plan.Column, plan.Index).ConfigureAwait(true))
                throw new InvalidOperationException("Native add-column did not report success.");
            session.VerifyContext();
            var item = session.Find(baseline.Item.Address);
            if (!ReferenceEquals(item.Model, baseline.Item.Model) ||
                NativeV11Reflection.Get(item.Model, "Columns") is not IEnumerable columns ||
                columns.Cast<object>().Count(c => string.Equals(NativeV11Reflection.Get(c, "Name") as string, plan.Column.Name, StringComparison.Ordinal)) != 1 ||
                !ContainsPhysicalColumn(database, item.Address.Name, plan.Column.Name))
                throw new InvalidOperationException("Native add-column model and SQLite readback disagree.");
            var nativeColumn = columns.Cast<object>().Single(c => NativeV11Reflection.Get(c, "Name") as string == plan.Column.Name);
            if (NativeV11Reflection.Get(nativeColumn, "ColumnType") as Type != plan.Column.Type)
                throw new InvalidOperationException("The native added column type differs from the requested type.");
            preservation.Verify(database, changedTable: item.Address.Name, newColumn: plan.Column.Name, allowLiveWal: true);
            var sheet = session.Sheet(item.Address.Name);
            NativeV11TableWriter.VerifyOwners(baseline.TableSet, item.Model, item.Parent, sheet,
                sheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "TableInfo"));
            NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name);
            NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
            NativeV11Generation.MarkModules(session, item.Route, "Edit");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            var expectedDatabase = NativeV11FirstProcessSaveJournal.SnapshotDatabase(database, allowLiveWal: true);
            var proof = new CreatePlan(session, item.Parent, baseline.TableSet, plan.NativeAdd, item.Address, plan.Index,
                baseline.SourceRevision, baseline.SourceBefore, null);
            return new(proof, item.Model, session.Serialize(item.Route, item.Model), plan.Column.Name, ExpectedModifiedDatabase: expectedDatabase);
        }
        catch (Exception error) when (error is not NativeCreateUncertainException) { throw new NativeCreateUncertainException(error); }
    }

    internal static async Task<bool> DispatchColumnAddAsync(MethodInfo method, string table, ColumnAdd column, int index)
    {
        var task = method.Invoke(null, [table, column.Name, column.Type, index, column.Comment]) as Task<bool>
            ?? throw new InvalidOperationException("Native add-column did not return its expected task.");
        return await task.ConfigureAwait(true);
    }

    internal static async Task<DeletedTable> DeleteAsync(DeletePlan plan)
    {
        var session = plan.Session;
        session.VerifyContext();
        VerifyPrivateNativeProvider(session);
        if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.SourceRevision ||
            !ReferenceEquals(session.Find(plan.Item.Address).Model, plan.Item.Model))
            throw new InvalidOperationException("The native table changed before deletion.");
        var references = NativeV11References.Find(session, plan.Item.Address);
        if (!references.Complete || references.References.Count != 0)
            throw new InvalidOperationException("The native inbound reference scan is incomplete or nonempty.");
        var beforeConfig = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        if (!ContainsPhysicalTable((string)NativeV11Reflection.Static(beforeConfig, "get_LocalAppDBSqliteFilePath")!, plan.Item.Address.Name, onlyView: plan.Item.Address.Kind == ModuleKind.View))
            throw new InvalidOperationException("The native physical table changed before deletion.");
        try
        {
            await DispatchDeleteAsync(plan.NativeDelete, plan.Item.Parent, plan.Item.Model).ConfigureAwait(true);
            session.VerifyContext();
            if (session.Items(plan.Item.Address.Kind).Any(x => x.Address == plan.Item.Address) || session.Sheet(plan.Item.Address.Name) is not null)
                throw new InvalidOperationException("The native table or worksheet still exists after deletion.");
            var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
            if (ContainsPhysicalTable((string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!, plan.Item.Address.Name, includeViews: true))
                throw new InvalidOperationException("The native physical table still exists after deletion.");
            bool Registered() => ((IEnumerable)(NativeV11Reflection.Get(plan.Item.Route.Loader, "Keys")
                ?? throw new InvalidOperationException("The native table save registry is unavailable."))).Cast<object>().OfType<string>().Any(key =>
                string.Equals(key.Replace('\\', '/'), plan.SourceBefore.DefinitionPath, StringComparison.OrdinalIgnoreCase));
            if (Registered()) NativeV11Reflection.Call(plan.Item.Route.Colleague, "Delete", plan.Item.Model);
            NativeV11Reflection.Call(plan.Item.Route.Colleague, "SaveToCache", true);
            if (Registered()) throw new InvalidOperationException("The native table save registration still exists.");
            NativeV11Generation.MarkModules(session, plan.Item.Route, "Remove");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            return new(plan);
        }
        catch (Exception error) when (error is not NativeCreateUncertainException) { throw new NativeCreateUncertainException(error); }
    }

    internal static async Task DispatchDeleteAsync(MethodInfo method, object parent, object model)
    {
        var task = method.Invoke(null, [parent, model]) as Task
            ?? throw new InvalidOperationException("The native delete did not return its expected task.");
        await task.ConfigureAwait(true);
    }

    internal static async Task<string> DispatchCreateAsync(Assembly designer, MethodInfo method, object parent,
        ModuleAddress address, int index, string? viewSql)
    {
        if (address.Kind == ModuleKind.Table)
        {
            var task = method.Invoke(null, [null, parent, address.Name, index]) as Task<string>
                ?? throw new InvalidOperationException("The native create call did not return its expected task.");
            return await task.ConfigureAwait(true);
        }
        var info = Activator.CreateInstance(method.GetParameters()[0].ParameterType)!;
        NativeV11Reflection.Set(info, "ViewName", address.Name);
        NativeV11Reflection.Set(info, "CreateViewSql", viewSql!);
        var viewTask = method.Invoke(null, [info, null, true, true]) as Task
            ?? throw new InvalidOperationException("The native view creation did not return its expected task.");
        await viewTask.ConfigureAwait(true);
        var view = viewTask.GetType().GetProperty("Result")?.GetValue(viewTask)
            ?? throw new InvalidOperationException("The native view creation returned no model.");
        var name = NativeV11Reflection.Get(view, "Name") as string ?? throw new InvalidOperationException("The native view has no name.");
        var insert = designer.GetType(V11BuildProfile.Resolve(designer).Version == "11.0.104.0" ? "mf" : "lz", true)!;
        NativeV11Reflection.Static(insert, "InsertTableOrFolderToCollection", parent, index, view);
        return name;
    }

    // The null connection ID is the native UI's built-in provider branch. A
    // failure after invocation may have changed the private database; callers
    // must quarantine the fixture rather than retry or assume rollback.
    internal static async Task<CreatedTable> CreateAsync(CreatePlan plan)
    {
        plan.Session.VerifyContext();
        VerifyPrivateNativeProvider(plan.Session);
        var nativeConfig = plan.Session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        if (ContainsPhysicalTable((string)NativeV11Reflection.Static(nativeConfig, "get_LocalAppDBSqliteFilePath")!, plan.Address.Name, includeViews: true))
            throw new InvalidOperationException("The native SQLite name became occupied before dispatch.");
        if (!string.Equals(NativeV11SourceRevision.Capture(plan.Session.SourcePath), plan.SourceRevision, StringComparison.Ordinal))
            throw new InvalidOperationException("The source project changed before native table creation.");
        if (plan.Session.Items(ModuleKind.Table).Concat(plan.Session.Items(ModuleKind.View)).Any(item =>
            string.Equals(item.Address.Name, plan.Address.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("A native table or view acquired the requested name before dispatch.");
        var settings = NativeV11Reflection.Get(plan.Session.Document, "ForguncySettings")!;
        if (!ReferenceEquals(NativeV11Reflection.Get(settings, "TableSet"), plan.TableSet) ||
            !ReferenceEquals(NativeV11Reflection.Get(plan.TableSet, "Tables"), plan.Parent) ||
            ((IList)plan.Parent).Count != plan.InsertIndex)
            throw new InvalidOperationException("The native table baseline changed before dispatch.");
        var database = (string)NativeV11Reflection.Static(nativeConfig, "get_LocalAppDBSqliteFilePath")!;
        using var preservation = NativeV11SqlitePreservation.Capture(database);
        try
        {
        var name = await DispatchCreateAsync(plan.Session.Context.DesignerAssembly, plan.NativeCreate, plan.Parent,
            plan.Address, plan.InsertIndex, plan.ViewSql).ConfigureAwait(true);
        if (!string.Equals(name, plan.Address.Name, StringComparison.Ordinal))
            throw new InvalidOperationException("The native create result differs; preserve and quarantine the fixture.");
        plan.Session.VerifyContext();
        var matching = plan.Session.Items(plan.Address.Kind).Where(item => item.Address == plan.Address).ToArray();
        if (matching.Length != 1 || NativeV11Reflection.Get(matching[0].Model, "ExternalDatabaseInfo") is not null ||
            plan.Address.Kind == ModuleKind.View && NativeV11Reflection.Get(matching[0].Model, "IsBuiltInViewCustom") is not true)
            throw new InvalidOperationException("The resulting native built-in table is not uniquely registered; preserve the fixture.");
        var config = plan.Session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        if (!ContainsPhysicalTable((string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!, name, onlyView: plan.Address.Kind == ModuleKind.View))
            throw new InvalidOperationException("The native physical table is absent after creation; preserve the fixture.");
        if (plan.ViewSql is not null && !ViewSqlMatches(database, name, plan.ViewSql))
            throw new InvalidOperationException("The native view definition differs from the requested SQL; preserve the fixture.");
        preservation.Verify(database, allowLiveWal: true);
        var expectedDatabase = NativeV11FirstProcessSaveJournal.SnapshotDatabase(database, allowLiveWal: true);
        var model = matching[0].Model;
        var route = matching[0].Route;
        bool Registered() => ((IEnumerable)(NativeV11Reflection.Get(route.Loader, "Keys")
            ?? throw new InvalidOperationException("Native table save registry is unavailable; preserve the fixture."))).Cast<object>().OfType<string>().Any(key =>
            string.Equals(key.Replace('\\', '/'), plan.SourceBefore.DefinitionPath, StringComparison.OrdinalIgnoreCase));
        if (!Registered()) NativeV11Reflection.Call(route.Colleague, "Insert", model);
        NativeV11Reflection.Call(route.Colleague, "SaveToCache", true);
        if (!Registered()) throw new InvalidOperationException("Native table save registration was not created; preserve the fixture.");
        NativeV11Generation.MarkModules(plan.Session, route, "New");
        NativeV11Reflection.Set(plan.Session.Document, "ContainsUnsavedChanges", true);
        var sheets = NativeV11Reflection.Get(plan.Session.Workbook, "Sheets")!;
        var indexer = sheets.GetType().GetProperty("Item", [typeof(string)])
            ?? throw new MissingMemberException("Native worksheet name index is unavailable.");
        var sheet = indexer.GetValue(sheets, [name])
            ?? throw new InvalidOperationException("The native table worksheet is missing; preserve the fixture.");
        var tag = NativeV11Reflection.Get(sheet, "Tag")!;
        var tableInfo = NativeV11Reflection.Get(tag, "TableInfo")!;
        NativeV11TableWriter.VerifyOwners(plan.TableSet, model, plan.Parent, sheet, tableInfo);
        // CreateNewTableInternalAsync does not initialize PermissionData after
        // CreateTableAsync. Preserve the native value, including null.
        return new(plan, model, plan.Session.Serialize(matching[0].Route, model), ExpectedViewSql: plan.ViewSql, ExpectedModifiedDatabase: expectedDatabase);
        }
        catch (Exception error) when (error is not NativeCreateUncertainException)
        {
            throw new NativeCreateUncertainException(error);
        }
    }
    // Called only after the native save completed. LastPath is native staging,
    // not an independently invented database backup. This checks its exact
    // bytes against the saved FGCC and the staged definition against the model.
    internal static string? VerifySavedSourceHash(CreatedTable created)
    {
        var plan = created.Plan;
        plan.Session.VerifyContext();
        var items = plan.Session.Items(plan.Address.Kind).Where(x => x.Address == plan.Address).ToArray();
        if (items.Length != 1 || !ReferenceEquals(items[0].Model, created.Model) ||
            !NativeV11DefinitionWriter.Equivalent(created.ExpectedNativeJson, plan.Session.Serialize(items[0].Route, items[0].Model)))
            return null;
        foreach (var related in new[] { created.RelatedTable, created.PreviousRelatedTable }.OfType<RelatedTableSnapshot>())
        {
            if (!RelatedTableMemoryMatches(plan.Session, related)) return null;
            var source = NativeV11PendingSave.ReadFiles(plan.Session.SourcePath, [related.Item.RelativePath])[related.Item.RelativePath];
            var stage = NativeV11PendingSave.ReadFiles(plan.Session.LastPath, [related.Item.RelativePath])[related.Item.RelativePath];
            if (source is null || stage is null || !V11Safety.Equivalent(V11Safety.Body(System.Text.Encoding.UTF8.GetBytes(related.Json)), V11Safety.Body(source)) ||
                !V11Safety.Equivalent(V11Safety.Body(System.Text.Encoding.UTF8.GetBytes(related.Json)), V11Safety.Body(stage))) return null;
        }
        var stagedDatabase = V11Safety.Under(plan.Session.LastPath, plan.SourceBefore.DatabasePath);
        if (!NoPendingSqliteJournal(stagedDatabase)) return null;
        var hash = VerifyAndHashSavedSource(plan.Session.SourcePath, plan.Session.LastPath, plan.SourceBefore, created.ExpectedNativeJson,
            requireDatabaseChange: created.ExpectedUnchangedDatabase is null);
        if (created.ExpectedUnchangedDatabase is not null)
        {
            using var staged = File.OpenRead(stagedDatabase);
            if (DatabaseFingerprint(staged) != created.ExpectedUnchangedDatabase) return null;
        }
        if (created.ExpectedModifiedDatabase is not null)
        {
            using var staged = File.OpenRead(stagedDatabase);
            if (DatabaseFingerprint(staged) != created.ExpectedModifiedDatabase) return null;
        }
        return hash is not null && ContainsPhysicalTable(stagedDatabase, plan.Address.Name, onlyView: plan.Address.Kind == ModuleKind.View) &&
            (created.ExpectedColumn is null || ContainsPhysicalColumn(stagedDatabase, plan.Address.Name, created.ExpectedColumn)) &&
            (created.ExpectedAbsentColumn is null || !ContainsPhysicalColumn(stagedDatabase, plan.Address.Name, created.ExpectedAbsentColumn)) &&
            (created.ExpectedViewSql is null || ViewSqlMatches(stagedDatabase, plan.Address.Name, created.ExpectedViewSql)) &&
            NoPendingSqliteJournal(stagedDatabase) ? hash : null;
    }

    internal static bool RelatedTableMemoryMatches(NativeV11Session session, RelatedTableSnapshot related)
    {
        var item = session.Find(related.Item.Address);
        return ReferenceEquals(item.Model, related.Item.Model) && NativeV11DefinitionWriter.Equivalent(related.Json, session.Serialize(item.Route, item.Model));
    }

    internal static bool VerifyDeletedSource(DeletedTable deleted)
    {
        var plan = deleted.Plan;
        var session = plan.Session;
        session.VerifyContext();
        if (session.Items(plan.Item.Address.Kind).Any(x => x.Address == plan.Item.Address) || session.Sheet(plan.Item.Address.Name) is not null)
            return false;
        var stagedDatabase = V11Safety.Under(session.LastPath, plan.SourceBefore.DatabasePath);
        return NoPendingSqliteJournal(stagedDatabase) && VerifyDeletedSourceGroup(session.SourcePath, session.LastPath, plan.SourceBefore) &&
            !ContainsPhysicalTable(stagedDatabase, plan.Item.Address.Name, includeViews: true) &&
            NoPendingSqliteJournal(stagedDatabase);
    }

    internal static bool NoPendingSqliteJournal(string database) => new[] { database + "-wal", database + "-journal" }
        .All(path => !File.Exists(path) || new FileInfo(path).Length == 0);

    internal static bool ContainsPhysicalTable(string database, string name, bool includeViews = false, bool onlyView = false)
    {
        var sqlite = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Microsoft.Data.Sqlite");
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var builder = Activator.CreateInstance(builderType)!;
        NativeV11Reflection.Set(builder, "DataSource", Path.GetFullPath(database));
        // Native CopyIfSourceExists swallows copy failures. Do not retain a
        // pooled read handle on staging across subsequent native saves.
        NativeV11Reflection.Set(builder, "Pooling", false);
        var mode = builderType.GetProperty("Mode")!;
        mode.SetValue(builder, Enum.Parse(mode.PropertyType, "ReadOnly"));
        using var connection = (IDisposable)Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!, [builder.ToString()])!;
        NativeV11Reflection.Call(connection, "Open");
        using var command = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!;
        NativeV11Reflection.Set(command, "CommandText", onlyView
            ? "SELECT COUNT(*) FROM sqlite_master WHERE type='view' AND name=$name COLLATE NOCASE"
            : includeViews
            ? "SELECT COUNT(*) FROM sqlite_master WHERE type IN ('table','view') AND name=$name COLLATE NOCASE"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name COLLATE NOCASE");
        NativeV11Reflection.Call(NativeV11Reflection.Get(command, "Parameters")!, "AddWithValue", "$name", name);
        return Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) != 0;
    }

    internal static bool ContainsPhysicalColumn(string database, string table, string column)
    {
        var sqlite = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Microsoft.Data.Sqlite");
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var builder = Activator.CreateInstance(builderType)!;
        NativeV11Reflection.Set(builder, "DataSource", Path.GetFullPath(database)); NativeV11Reflection.Set(builder, "Pooling", false);
        var mode = builderType.GetProperty("Mode")!; mode.SetValue(builder, Enum.Parse(mode.PropertyType, "ReadOnly"));
        using var connection = (IDisposable)Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!, [builder.ToString()])!;
        NativeV11Reflection.Call(connection, "Open");
        using var command = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!;
        NativeV11Reflection.Set(command, "CommandText", "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column COLLATE NOCASE");
        var parameters = NativeV11Reflection.Get(command, "Parameters")!;
        NativeV11Reflection.Call(parameters, "AddWithValue", "$table", table);
        NativeV11Reflection.Call(parameters, "AddWithValue", "$column", column);
        return Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) != 0;
    }

    internal static bool PhysicalColumnEligible(string database, string table, string column)
    {
        var sqlite = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Microsoft.Data.Sqlite");
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var builder = Activator.CreateInstance(builderType)!;
        NativeV11Reflection.Set(builder, "DataSource", Path.GetFullPath(database)); NativeV11Reflection.Set(builder, "Pooling", false);
        var mode = builderType.GetProperty("Mode")!; mode.SetValue(builder, Enum.Parse(mode.PropertyType, "ReadOnly"));
        using var connection = (IDisposable)Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!, [builder.ToString()])!;
        NativeV11Reflection.Call(connection, "Open");
        using var command = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!;
        var parameters = NativeV11Reflection.Get(command, "Parameters")!;
        NativeV11Reflection.Call(parameters, "AddWithValue", "$table", table); NativeV11Reflection.Call(parameters, "AddWithValue", "$column", column);
        NativeV11Reflection.Set(command, "CommandText", "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name=$column COLLATE BINARY AND pk=0");
        if (Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) != 1) return false;
        NativeV11Reflection.Set(command, "CommandText", "SELECT COUNT(*) FROM sqlite_master WHERE tbl_name=$table COLLATE BINARY AND type='trigger' AND NOT (name=$table||'_rowversion_Trigger' AND instr(upper(sql),'FGC_ROWVERSION')>0)");
        if (Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) != 0) return false;
        NativeV11Reflection.Set(command, "CommandText", "SELECT sql FROM sqlite_master WHERE tbl_name=$table COLLATE BINARY AND type='trigger' AND name=$table||'_rowversion_Trigger'");
        var trigger = NativeV11Reflection.Call(command, "ExecuteScalar") as string;
        if (trigger is not null)
        {
            static string Normalize(string sql) => string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var expectedTrigger = $"CREATE TRIGGER [{table}_rowversion_Trigger] AFTER UPDATE ON [main].[{table}] BEGIN UPDATE \"{table}\" SET \"FGC_Rowversion\"=current_timestamp WHERE \"rowid\"= new.rowid; END";
            if (!Normalize(trigger).Equals(Normalize(expectedTrigger), StringComparison.OrdinalIgnoreCase)) return false;
        }
        NativeV11Reflection.Set(command, "CommandText", "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$table COLLATE BINARY AND (instr(upper(sql),'CHECK')>0 OR instr(upper(sql),'WITHOUT ROWID')>0 OR instr(upper(sql),'GENERATED')>0)");
        if (Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) != 0) return false;
        NativeV11Reflection.Set(command, "CommandText", "SELECT COUNT(*) FROM pragma_index_list($table) i, pragma_index_info(i.name) c WHERE c.name=$column COLLATE NOCASE");
        if (Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) != 0) return false;
        NativeV11Reflection.Set(command, "CommandText", "SELECT COUNT(*) FROM pragma_foreign_key_list($table)");
        if (Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) != 0) return false;
        NativeV11Reflection.Set(command, "CommandText", "SELECT COUNT(*) FROM sqlite_master m, pragma_foreign_key_list(m.name) f WHERE m.type='table' AND f.\"table\"=$table COLLATE NOCASE");
        return Convert.ToInt64(NativeV11Reflection.Call(command, "ExecuteScalar")) == 0;
    }

    internal static bool ViewSqlMatches(string database, string view, string expected)
    {
        var sqlite = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Microsoft.Data.Sqlite");
        var builderType = sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnectionStringBuilder", true)!;
        var builder = Activator.CreateInstance(builderType)!;
        NativeV11Reflection.Set(builder, "DataSource", Path.GetFullPath(database)); NativeV11Reflection.Set(builder, "Pooling", false);
        var mode = builderType.GetProperty("Mode")!; mode.SetValue(builder, Enum.Parse(mode.PropertyType, "ReadOnly"));
        using var connection = (IDisposable)Activator.CreateInstance(sqlite.GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!, [builder.ToString()])!;
        NativeV11Reflection.Call(connection, "Open");
        using var command = (IDisposable)NativeV11Reflection.Call(connection, "CreateCommand")!;
        NativeV11Reflection.Set(command, "CommandText", "SELECT sql FROM sqlite_master WHERE type='view' AND name=$name COLLATE NOCASE");
        NativeV11Reflection.Call(NativeV11Reflection.Get(command, "Parameters")!, "AddWithValue", "$name", view);
        var sql = (NativeV11Reflection.Call(command, "ExecuteScalar") as string)?.Trim();
        return sql is not null && sql.EndsWith(expected, StringComparison.Ordinal) && sql.Length > expected.Length + 3 &&
            sql[..^expected.Length].TrimEnd().EndsWith(" AS", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool VerifyDeletedSourceGroup(string project, string staging, SourceGroup before)
    {
        var definition = V11Safety.Under(staging, before.DefinitionPath);
        var database = V11Safety.Under(staging, before.DatabasePath);
        if (File.Exists(definition) || !File.Exists(database) || before.DatabaseSha256 is null || !DatabaseChanged(database, before.DatabaseSha256)) return false;
        if (Directory.Exists(project))
        {
            var sourceDefinition = V11Safety.Under(project, before.DefinitionPath);
            var sourceDatabase = V11Safety.Under(project, before.DatabasePath);
            if (File.Exists(sourceDefinition) || Directory.Exists(sourceDefinition) || !File.Exists(sourceDatabase)) return false;
            using var directoryStaged = File.OpenRead(database); using var directorySaved = File.OpenRead(sourceDatabase);
            return EquivalentDatabase(directoryStaged, directorySaved);
        }
        using var source = new FileStream(project, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        if (archive.Entries.Any(e => e.FullName.Replace('\\', '/').Equals(before.DefinitionPath, StringComparison.OrdinalIgnoreCase))) return false;
        var db = archive.Entries.Where(e => e.FullName.Replace('\\', '/').Equals(before.DatabasePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (db.Length != 1 || db[0].Length == 0) return false;
        using var staged = File.OpenRead(database); using var saved = db[0].Open();
        return EquivalentDatabase(staged, saved);
    }

    internal static string? VerifyAndHashSavedSource(string project, string staging, SourceGroup before, string expectedJson, bool requireDatabaseChange = true)
    {
        if (Directory.Exists(project))
        {
            if (!VerifyStagedSource(project, staging, before.DefinitionPath, before.DatabasePath, expectedJson)) return null;
            var stagedDatabase = V11Safety.Under(staging, before.DatabasePath);
            if (requireDatabaseChange && before.DatabaseSha256 is not null && !DatabaseChanged(stagedDatabase, before.DatabaseSha256)) return null;
            using var savedDatabase = File.OpenRead(V11Safety.Under(project, before.DatabasePath));
            return Convert.ToHexString(SHA256.HashData(savedDatabase));
        }
        // Hold the same source archive open without write sharing through both
        // source-group verification and the final whole-archive fingerprint.
        using var archiveLock = new FileStream(project, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (!VerifyStagedSource(project, staging, before.DefinitionPath, before.DatabasePath, expectedJson)) return null;
        if (requireDatabaseChange && before.DatabaseSha256 is not null)
        {
            var stagedDatabase = V11Safety.Under(staging, before.DatabasePath);
            if (!DatabaseChanged(stagedDatabase, before.DatabaseSha256)) return null;
        }
        archiveLock.Position = 0;
        return Convert.ToHexString(SHA256.HashData(archiveLock));
    }

    internal static bool DatabaseChanged(string stagedDatabase, string oldSha256)
    {
        using var file = File.OpenRead(stagedDatabase);
        return !string.Equals(Convert.ToHexString(SHA256.HashData(file)), oldSha256, StringComparison.Ordinal);
    }

    internal static bool VerifyStagedSource(string project, string staging, string definition, string database, string expectedJson)
    {
        var definitionPath = V11Safety.Under(staging, definition);
        var databasePath = V11Safety.Under(staging, database);
        if (!File.Exists(definitionPath) || !File.Exists(databasePath) ||
            new FileInfo(definitionPath).Length > 32 * 1024 * 1024) return false;
        if (Directory.Exists(project))
        {
            var savedDefinition = V11Safety.Under(project, definition);
            var savedDatabase = V11Safety.Under(project, database);
            if (!File.Exists(savedDefinition) || !File.Exists(savedDatabase) || new FileInfo(savedDefinition).Length > 32 * 1024 * 1024) return false;
            using var directoryExpected = JsonDocument.Parse(expectedJson);
            if (!V11Safety.Equivalent(directoryExpected.RootElement, V11Safety.Body(File.ReadAllBytes(definitionPath))) ||
                !V11Safety.Equivalent(V11Safety.Body(File.ReadAllBytes(definitionPath)), V11Safety.Body(File.ReadAllBytes(savedDefinition)))) return false;
            using var directoryStagedDb = File.OpenRead(databasePath); using var directorySavedDb = File.OpenRead(savedDatabase);
            return EquivalentDatabase(directoryStagedDb, directorySavedDb);
        }
        using var source = new FileStream(project, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read);
        ZipArchiveEntry? Unique(string relative)
        {
            var entries = archive.Entries.Where(e => e.FullName.Replace('\\', '/').Equals(relative, StringComparison.OrdinalIgnoreCase)).ToArray();
            return entries.Length == 1 ? entries[0] : null;
        }
        var json = Unique(definition); var db = Unique(database);
        if (json is null || db is null || json.Length > 32 * 1024 * 1024 || db.Length == 0) return false;
        using var jsonStream = json.Open(); using var jsonBytes = new MemoryStream(); jsonStream.CopyTo(jsonBytes);
        var stagedJson = File.ReadAllBytes(definitionPath);
        using var expected = JsonDocument.Parse(expectedJson);
        if (!V11Safety.Equivalent(expected.RootElement, V11Safety.Body(stagedJson)) ||
            !V11Safety.Equivalent(V11Safety.Body(stagedJson), V11Safety.Body(jsonBytes.ToArray()))) return false;
        using var stagedDb = File.OpenRead(databasePath); using var savedDb = db.Open();
        return EquivalentDatabase(stagedDb, savedDb);
    }

    // Native FGCC packaging can change SQLite's file-change counter, schema
    // cookie and version-valid-for header fields while leaving every other
    // byte unchanged. Compare every byte except those three version fields;
    // each file must still have internally consistent change-counter fields.
    internal static bool EquivalentDatabase(Stream first, Stream second)
    {
        var left = DatabaseFingerprint(first); var right = DatabaseFingerprint(second);
        return left.Valid && right.Valid && left.Length == right.Length && left.Hash == right.Hash;
    }

    internal static (string Hash, long Length, bool Valid) DatabaseFingerprint(Stream input)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024]; var header = new byte[100]; var headerLength = 0;
        while (headerLength < header.Length)
        {
            var count = input.Read(header, headerLength, header.Length - headerLength);
            if (count == 0) break;
            headerLength += count;
        }
        var sqlite = headerLength == 100 && header.AsSpan(0, 16).SequenceEqual("SQLite format 3\0"u8);
        var valid = !sqlite || header.AsSpan(24, 4).SequenceEqual(header.AsSpan(92, 4));
        if (sqlite) { header.AsSpan(24, 4).Clear(); header.AsSpan(40, 4).Clear(); header.AsSpan(92, 4).Clear(); }
        digest.AppendData(header.AsSpan(0, headerLength));
        long length = headerLength;
        int n; while ((n = input.Read(buffer, 0, buffer.Length)) > 0) { digest.AppendData(buffer.AsSpan(0, n)); length += n; }
        return (Convert.ToHexString(digest.GetHashAndReset()), length, valid);
    }

}
