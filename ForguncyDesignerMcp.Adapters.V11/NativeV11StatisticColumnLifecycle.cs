using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Restricted native statistic definitions over an existing direct relation
// between two root built-in tables. No remote connection or DDL is requested.
internal static class NativeV11StatisticColumnLifecycle
{
    internal sealed class RolledBackException(Exception cause) : Exception("The native statistic update failed and its model was restored.", cause);
    internal sealed record UpdateSnapshot(object Column, string Name, object Aggregate, string DataColumn, string Detail, string? Schema,
        Type Type, string? Comment, object Binding);
    internal static UpdateSnapshot CaptureUpdate(object column) => new(column,
        (string)NativeV11Reflection.Get(column, "Name")!, NativeV11Reflection.Get(column, "AggregateFunction")!,
        (string)NativeV11Reflection.Get(column, "DataColumnName")!, (string)NativeV11Reflection.Get(column, "DetailTableName")!,
        NativeV11Reflection.Get(column, "DetailTableSchema") as string, NativeV11Reflection.Get(column, "ColumnType") as Type ?? throw new InvalidDataException("The original statistic type is unavailable for rollback."),
        NativeV11Reflection.Get(column, "Comment") as string, NativeV11Reflection.Get(column, "BindingInfo")!);

    internal static async Task RestoreUpdateAsync(MethodInfo method, object table, UpdateSnapshot before)
    {
        var columns = (NativeV11Reflection.Get(table, "Columns") as IEnumerable)?.Cast<object>().ToArray() ?? [];
        if (!columns.Any(column => ReferenceEquals(column, before.Column))) throw new InvalidOperationException("The statistic column identity changed during the failed update.");
        var currentName = NativeV11Reflection.Get(before.Column, "Name") as string ?? throw new InvalidOperationException("The statistic column name is unavailable.");
        var task = method.Invoke(null, [table, currentName, before.Name, before.Aggregate, before.DataColumn, before.Detail, before.Schema,
            before.Type, before.Comment, before.Binding]) as Task ?? throw new InvalidOperationException("Native statistic restoration returned no task.");
        await task.ConfigureAwait(true);
        // Native update deliberately skips an empty comment; restore the exact value.
        NativeV11Reflection.Set(before.Column, "Comment", before.Comment);
    }
    internal sealed record Plan(NativeV11BuiltInTableLifecycle.DeletePlan Baseline, NativeV11Item Detail, string DetailJson,
        MethodInfo Method, string Operation, string Name, string ResultName, string DataColumn, object? Aggregate, Type? ColumnType, string Comment, object? Binding,
        NativeV11BuiltInTableLifecycle.RelatedTableSnapshot? PreviousDetail);

    internal static bool Supports(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.Table || request.Operation != ModuleOperation.Update || request.Document?.Metadata.ValueKind != JsonValueKind.Object ||
            !request.Document.Metadata.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() != 1 ||
            operations[0].ValueKind != JsonValueKind.Object || !operations[0].TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return false;
        return method.GetString() is "addStatisticColumn" or "updateStatisticColumn" or "deleteStatisticColumn" or "renameStatisticColumn";
    }

    internal static Plan Prepare(NativeV11Session session, ModuleMutationRequest request)
    {
        if (!Supports(request) || request.Document!.Companions.Count != 0 || request.Document.Metadata.EnumerateObject().Count() != 1)
            throw new InvalidDataException("Exactly one native statistic operation is required.");
        var operation = request.Document.Metadata.GetProperty("operations")[0];
        if (operation.EnumerateObject().Count() != 2 || !operation.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object ||
            p.EnumerateObject().Any(x => x.Name is not ("columnName" or "newName" or "detailTable" or "dataColumn" or "function" or "type" or "comment")) ||
            p.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != p.EnumerateObject().Count())
            throw new InvalidDataException("The statistic operation parameters are invalid.");
        string Required(string name)
        {
            if (!p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new InvalidDataException("A statistic identity or type is missing.");
            return value.GetString()!;
        }
        var op = operation.GetProperty("method").GetString()!; var name = Required("columnName");
        V11Safety.Address(new ModuleAddress(ModuleKind.Table, name));
        var add = op == "addStatisticColumn"; var update = op == "updateStatisticColumn"; var rename = op == "renameStatisticColumn";
        var resultName = p.TryGetProperty("newName", out _) ? Required("newName") : name;
        V11Safety.Address(new ModuleAddress(ModuleKind.Table, resultName));
        if ((add && resultName != name) || (rename && (p.EnumerateObject().Count() != 2 || resultName.Equals(name, StringComparison.OrdinalIgnoreCase))) ||
            (op == "deleteStatisticColumn" && p.EnumerateObject().Count() != 1)) throw new InvalidDataException("The statistic operation shape differs.");
        var baseline = NativeV11BuiltInTableLifecycle.PrepareDelete(session, request with { Operation = ModuleOperation.Delete, Document = null }, addingColumn: true);
        var columns = (NativeV11Reflection.Get(baseline.Item.Model, "Columns") as IEnumerable)?.Cast<object>().ToArray() ?? throw new InvalidDataException("Native columns are unavailable.");
        var existing = columns.Where(column => NativeV11Reflection.Get(column, "Name") as string == name).ToArray();
        if (add ? existing.Length != 0 : existing.Length != 1 || existing[0].GetType().FullName != "Forguncy.Model.Tables.StatisticColumn")
            throw new InvalidDataException("The statistic column is absent, occupied or has another native type.");
        if (resultName != name && columns.Any(column => string.Equals(NativeV11Reflection.Get(column, "Name") as string, resultName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("The new statistic column name is occupied.");
        var detailName = add || update ? Required("detailTable") : NativeV11Reflection.Get(existing[0], "DetailTableName") as string ?? throw new InvalidDataException("The statistic detail table is unavailable.");
        var dataColumn = add || update ? Required("dataColumn") : NativeV11Reflection.Get(existing[0], "DataColumnName") as string ?? throw new InvalidDataException("The statistic source column is unavailable.");
        V11Safety.Address(new ModuleAddress(ModuleKind.Table, detailName)); V11Safety.Address(new ModuleAddress(ModuleKind.Table, dataColumn));
        if (detailName.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A statistic needs a distinct detail table.");
        var detail = session.Find(new ModuleAddress(ModuleKind.Table, detailName));
        if (NativeV11Reflection.Get(detail.Model, "ExternalDatabaseInfo") is not null || NativeV11Reflection.Get(detail.Model, "IsProcessTable") is true ||
            !ReferenceEquals(detail.Parent, baseline.Item.Parent)) throw new NotSupportedException("Only root built-in detail tables are supported.");
        var detailSheet = session.Sheet(detailName);
        NativeV11TableWriter.VerifyOwners(baseline.TableSet, detail.Model, detail.Parent, detailSheet,
            detailSheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(detailSheet, "Tag")!, "TableInfo"));
        var detailColumns = (NativeV11Reflection.Get(detail.Model, "Columns") as IEnumerable)?.Cast<object>().ToArray() ?? [];
        var sourceColumn = detailColumns.SingleOrDefault(column => NativeV11Reflection.Get(column, "Name") as string == dataColumn && column.GetType().FullName == "Forguncy.Model.Tables.BindingColumn")
            ?? throw new InvalidDataException("The statistic source must be an existing native binding column.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (!NativeV11BuiltInTableLifecycle.ContainsPhysicalTable(database, detailName) || !NativeV11BuiltInTableLifecycle.ContainsPhysicalColumn(database, detailName, dataColumn))
            throw new InvalidDataException("The statistic detail table or source column is absent in native SQLite.");
        var relations = (NativeV11Reflection.Get(detail.Model, "Relations") as IEnumerable)?.Cast<object>().ToArray() ?? [];
        if (relations.Count(relation => NativeV11Reflection.Get(relation, "ForeignTable") as string == detailName &&
            NativeV11Reflection.Get(relation, "PrimaryTable") as string == request.Address.Name) != 1)
            throw new InvalidDataException("Exactly one existing native detail-to-primary relation is required.");
        var detailJson = session.Serialize(detail.Route, detail.Model);
        var detailSource = NativeV11PendingSave.ReadFiles(session.SourcePath, [detail.RelativePath])[detail.RelativePath] ?? throw new InvalidDataException("The detail table source is unavailable.");
        if (!V11Safety.Equivalent(V11Safety.Body(Encoding.UTF8.GetBytes(detailJson)), V11Safety.Body(detailSource))) throw new InvalidDataException("The detail table differs from source.");
        NativeV11BuiltInTableLifecycle.RelatedTableSnapshot? previousDetail = null;
        var oldName = update ? NativeV11Reflection.Get(existing[0], "DetailTableName") as string ?? throw new InvalidDataException("The previous statistic detail is unavailable.") : detailName;
        if (update && oldName != detailName)
        {
            V11Safety.Address(new ModuleAddress(ModuleKind.Table, oldName));
            var old = session.Find(new ModuleAddress(ModuleKind.Table, oldName));
            if (NativeV11Reflection.Get(old.Model, "ExternalDatabaseInfo") is not null || NativeV11Reflection.Get(old.Model, "IsProcessTable") is true ||
                !ReferenceEquals(old.Parent, baseline.Item.Parent)) throw new NotSupportedException("The previous detail must be a root built-in table.");
            var oldSheet = session.Sheet(oldName);
            NativeV11TableWriter.VerifyOwners(baseline.TableSet, old.Model, old.Parent, oldSheet,
                oldSheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(oldSheet, "Tag")!, "TableInfo"));
            var oldColumn = NativeV11Reflection.Get(existing[0], "DataColumnName") as string ?? throw new InvalidDataException("The previous statistic source column is unavailable.");
            var oldBinding = NativeV11Reflection.Get(existing[0], "BindingInfo") ?? throw new InvalidDataException("The previous statistic binding is unavailable.");
            var oldColumns = (NativeV11Reflection.Get(old.Model, "Columns") as IEnumerable)?.Cast<object>().ToArray() ?? [];
            if (NativeV11Reflection.Get(oldBinding, "TableName") as string != oldName || NativeV11Reflection.Get(oldBinding, "ColumnName") as string != oldColumn ||
                oldColumns.Count(column => NativeV11Reflection.Get(column, "Name") as string == oldColumn && column.GetType().FullName == "Forguncy.Model.Tables.BindingColumn") != 1)
                throw new InvalidDataException("The previous statistic binding differs from its detail definition.");
            var oldRelations = (NativeV11Reflection.Get(old.Model, "Relations") as IEnumerable)?.Cast<object>().ToArray() ?? [];
            if (oldRelations.Count(relation => NativeV11Reflection.Get(relation, "ForeignTable") as string == oldName &&
                    NativeV11Reflection.Get(relation, "PrimaryTable") as string == request.Address.Name) != 1 ||
                !NativeV11BuiltInTableLifecycle.ContainsPhysicalTable(database, oldName) || !NativeV11BuiltInTableLifecycle.ContainsPhysicalColumn(database, oldName, oldColumn))
                throw new InvalidDataException("The previous statistic detail relation or physical source is unavailable.");
            var oldJson = session.Serialize(old.Route, old.Model);
            var oldSource = NativeV11PendingSave.ReadFiles(session.SourcePath, [old.RelativePath])[old.RelativePath] ?? throw new InvalidDataException("The previous detail source is unavailable.");
            if (!V11Safety.Equivalent(V11Safety.Body(Encoding.UTF8.GetBytes(oldJson)), V11Safety.Body(oldSource))) throw new InvalidDataException("The previous detail differs from source.");
            previousDetail = new(old, oldJson);
        }
        var dataAssembly = Assembly.Load("ForguncyDataAccess"); var aggregateType = dataAssembly.GetType("ForguncyDataAccess.AggregateFunction", true)!;
        var bindingType = dataAssembly.GetType("ForguncyDataAccess.BindingInfo", true)!;
        object? aggregate = null; object? binding = null; Type? columnType = null; var comment = "";
        if (add || update)
        {
            var function = Required("function");
            if (function is not ("AVG" or "COUNT" or "MAX" or "MIN" or "SUM" or "LIST")) throw new InvalidDataException("The native aggregate is unsupported.");
            aggregate = Enum.Parse(aggregateType, function);
            columnType = Required("type") switch { "Text" => typeof(string), "Number" => typeof(double), "Integer" => typeof(long),
                "Boolean" => typeof(bool), "DateTime" => typeof(DateTime), _ => throw new InvalidDataException("The statistic result type is unsupported.") };
            if (function is "AVG" or "SUM" && NativeV11Reflection.Get(sourceColumn, "ColumnType") is Type sourceType &&
                sourceType != typeof(int) && sourceType != typeof(long) && sourceType != typeof(float) && sourceType != typeof(double) && sourceType != typeof(decimal))
                throw new InvalidDataException("This aggregate requires a numeric native source column.");
            if (p.TryGetProperty("comment", out var c)) { if (c.ValueKind != JsonValueKind.String || (comment = c.GetString()!).Length > 4096) throw new InvalidDataException("Invalid statistic comment."); }
            binding = Activator.CreateInstance(bindingType, [detailName, dataColumn]) ?? throw new InvalidOperationException("The native statistic binding is unavailable.");
        }
        if (!add && !(update && resultName == name && NativeV11Reflection.Get(existing[0], "ColumnType") as Type == columnType))
        {
            var refs = NativeV11References.Find(session, request.Address);
            if (!refs.Complete || refs.References.Count != 0) throw new InvalidOperationException("Statistic deletion, renaming or type changes require a complete incoming-reference scan.");
        }
        var native = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var tableType = baseline.Item.Model.GetType();
        var signature = add ? new[] { typeof(string), typeof(string), aggregateType, typeof(string), typeof(string), typeof(string), typeof(Type), typeof(string), bindingType } :
            update ? [tableType, typeof(string), typeof(string), aggregateType, typeof(string), typeof(string), typeof(string), typeof(Type), typeof(string), bindingType] :
            rename ? [typeof(string), typeof(string), typeof(string)] : [typeof(string), typeof(string)];
        var method = native.GetMethod(add ? "AddStatisticColumnAsync" : update ? "UpdateStatisticColumnAsync" : rename ? "RenameColumnAsync" : "DeleteColumnAsync",
            NativeV11Reflection.Flags, null, signature, null) ?? throw new MissingMethodException("The pinned native statistic operation is unavailable.");
        if (!method.IsStatic || method.ReturnType != (rename ? typeof(Task<bool>) : typeof(Task))) throw new InvalidDataException("The native statistic method signature differs.");
        return new(baseline, detail, detailJson, method, op, name, resultName, dataColumn, aggregate, columnType, comment, binding, previousDetail);
    }

    internal static async Task<NativeV11BuiltInTableLifecycle.CreatedTable> ApplyAsync(Plan plan)
    {
        var session = plan.Baseline.Session; session.VerifyContext();
        if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.Baseline.SourceRevision ||
            !ReferenceEquals(session.Find(plan.Baseline.Item.Address).Model, plan.Baseline.Item.Model) ||
            !ReferenceEquals(session.Find(plan.Detail.Address).Model, plan.Detail.Model) ||
            !NativeV11DefinitionWriter.Equivalent(plan.DetailJson, session.Serialize(plan.Detail.Route, plan.Detail.Model)) ||
            plan.PreviousDetail is { } previous && !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, previous))
            throw new InvalidOperationException("The statistic owner baseline changed.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        var before = NativeV11FirstProcessSaveJournal.SnapshotDatabase(database);
        var originalJson = session.Serialize(plan.Baseline.Item.Route, plan.Baseline.Item.Model);
        var wasDirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var originalColumn = plan.PreviousDetail is null ? null : ((IEnumerable)NativeV11Reflection.Get(plan.Baseline.Item.Model, "Columns")!).Cast<object>()
            .Single(column => NativeV11Reflection.Get(column, "Name") as string == plan.Name);
        var rollback = originalColumn is null ? null : CaptureUpdate(originalColumn);
        try
        {
            var target = plan.Baseline.Item; var add = plan.Operation == "addStatisticColumn"; var update = plan.Operation == "updateStatisticColumn";
            object?[] args = add ? [target.Address.Name, plan.Name, plan.Aggregate, plan.DataColumn, plan.Detail.Address.Name, null, plan.ColumnType, plan.Comment, plan.Binding] :
                update ? [target.Model, plan.Name, plan.ResultName, plan.Aggregate, plan.DataColumn, plan.Detail.Address.Name, null, plan.ColumnType, plan.Comment, plan.Binding] :
                plan.Operation == "renameStatisticColumn" ? [target.Address.Name, plan.Name, plan.ResultName] : [target.Address.Name, plan.Name];
            var task = plan.Method.Invoke(null, args) as Task ?? throw new InvalidOperationException("The native statistic operation returned no task.");
            await task.ConfigureAwait(true);
            if (task is Task<bool> renamed && !renamed.Result) throw new InvalidOperationException("The native statistic rename failed.");
            session.VerifyContext();
            var current = session.Find(target.Address);
            var columns = (NativeV11Reflection.Get(current.Model, "Columns") as IEnumerable)?.Cast<object>().ToArray() ?? [];
            var matches = columns.Where(column => NativeV11Reflection.Get(column, "Name") as string == plan.ResultName && column.GetType().FullName == "Forguncy.Model.Tables.StatisticColumn").ToArray();
            if (!ReferenceEquals(current.Model, target.Model) || (plan.Operation == "deleteStatisticColumn" ? columns.Any(column => NativeV11Reflection.Get(column, "Name") as string == plan.Name) : matches.Length != 1) ||
                plan.ResultName != plan.Name && columns.Any(column => NativeV11Reflection.Get(column, "Name") as string == plan.Name) ||
                (add || update) && (NativeV11Reflection.Get(matches[0], "DetailTableName") as string != plan.Detail.Address.Name || NativeV11Reflection.Get(matches[0], "DataColumnName") as string != plan.DataColumn ||
                    !Equals(NativeV11Reflection.Get(matches[0], "AggregateFunction"), plan.Aggregate) || NativeV11Reflection.Get(matches[0], "ColumnType") as Type != plan.ColumnType ||
                    NativeV11Reflection.Get(matches[0], "BindingInfo") is not object actualBinding ||
                    NativeV11Reflection.Get(actualBinding, "TableName") as string != plan.Detail.Address.Name || NativeV11Reflection.Get(actualBinding, "ColumnName") as string != plan.DataColumn) ||
                !NativeV11DefinitionWriter.Equivalent(plan.DetailJson, session.Serialize(plan.Detail.Route, plan.Detail.Model)) ||
                plan.PreviousDetail is { } oldOwner && !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, oldOwner) || NativeV11FirstProcessSaveJournal.SnapshotDatabase(database) != before)
                throw new InvalidOperationException("The statistic operation's native model or unaffected owner differs.");
            NativeV11Reflection.Call(current.Route.Colleague, "Edit", current.Model, current.Address.Name); NativeV11Reflection.Call(current.Route.Colleague, "SaveToCache", true);
            if (plan.PreviousDetail is { } priorDetail)
            {
                VerifyMemento(session, current);
                VerifyMemento(session, plan.Detail);
                VerifyMemento(session, priorDetail.Item);
            }
            NativeV11Generation.MarkModules(session, current.Route, "Edit"); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            var proof = new NativeV11BuiltInTableLifecycle.CreatePlan(session, current.Parent, plan.Baseline.TableSet, plan.Method, current.Address, 0,
                plan.Baseline.SourceRevision, plan.Baseline.SourceBefore, null);
            return new(proof, current.Model, session.Serialize(current.Route, current.Model), ExpectedUnchangedDatabase: before,
                RelatedTable: new(plan.Detail, plan.DetailJson), PreviousRelatedTable: plan.PreviousDetail);
        }
        catch (Exception error) when (error is not NativeV11BuiltInTableLifecycle.NativeCreateUncertainException)
        {
            if (rollback is not null && plan.PreviousDetail is { } prior)
            {
                try
                {
                    session.VerifyContext();
                    if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.Baseline.SourceRevision ||
                        !ReferenceEquals(session.Find(plan.Baseline.Item.Address).Model, plan.Baseline.Item.Model) ||
                        !ReferenceEquals(session.Find(plan.Detail.Address).Model, plan.Detail.Model) ||
                        !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, prior) ||
                        !NativeV11DefinitionWriter.Equivalent(plan.DetailJson, session.Serialize(plan.Detail.Route, plan.Detail.Model)))
                        throw new InvalidOperationException("A statistic rollback baseline changed.");
                    await RestoreUpdateAsync(plan.Method, plan.Baseline.Item.Model, rollback);
                    var item = session.Find(plan.Baseline.Item.Address);
                    if (!ReferenceEquals(item.Model, plan.Baseline.Item.Model) || !NativeV11DefinitionWriter.Equivalent(originalJson, session.Serialize(item.Route, item.Model)) ||
                        NativeV11FirstProcessSaveJournal.SnapshotDatabase(database) != before)
                        throw new InvalidOperationException("The native statistic rollback did not restore its model and database.");
                    NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name);
                    NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
                    VerifyMemento(session, item); VerifyMemento(session, plan.Detail); VerifyMemento(session, prior.Item);
                    if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.Baseline.SourceRevision ||
                        !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, prior) ||
                        !NativeV11DefinitionWriter.Equivalent(plan.DetailJson, session.Serialize(plan.Detail.Route, plan.Detail.Model)) ||
                        NativeV11FirstProcessSaveJournal.SnapshotDatabase(database) != before)
                        throw new InvalidOperationException("The statistic rollback changed another owner, source, or database.");
                    NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty);
                }
                catch (Exception restoreError) { throw new NativeV11BuiltInTableLifecycle.NativeCreateUncertainException(new AggregateException(error, restoreError)); }
                throw new RolledBackException(error);
            }
            throw new NativeV11BuiltInTableLifecycle.NativeCreateUncertainException(error);
        }
    }

    internal static void VerifyMemento(NativeV11Session session, NativeV11Item item)
    {
        var current = session.Find(item.Address);
        if (!ReferenceEquals(current.Model, item.Model)) throw new InvalidOperationException("A statistic table owner changed.");
        var key = NativeV11Reflection.Call(item.Route.Colleague, "GetKey", item.Model) as string ?? throw new InvalidDataException("A statistic table cache key is unavailable.");
        var cache = NativeV11CommandSaveSnapshot.SaveItems(item.Route);
        if (cache[key] is not object saved || !V11Safety.Equivalent(NativeV11SourceMetadata.SerializeSaveData(session.Context.DesignerAssembly, item.Route, saved),
                NativeV11SourceMetadata.Serialize(session.Context.DesignerAssembly, item.Route, item.Model)))
            throw new InvalidOperationException("A statistic table memento differs from its native model.");
    }
}
