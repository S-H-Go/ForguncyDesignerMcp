using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// The native relation helper edits the foreign table and refreshes the table
// graph. This narrow route permits built-in tables sharing the native DB only.
internal static class NativeV11TableRelationLifecycle
{
    internal sealed record Plan(NativeV11BuiltInTableLifecycle.DeletePlan Baseline, NativeV11Item Primary,
        string PrimaryJson, object Relation, MethodInfo Method, bool Delete, bool Replace, string ForeignColumn, string PrimaryColumn,
        NativeV11BuiltInTableLifecycle.RelatedTableSnapshot? PreviousPrimary, object? PreviousRelation, string? OtherRelations);

    internal static void RestoreRelation(MethodInfo method, object previousRelation) => method.Invoke(null, [previousRelation, true]);
    internal static bool GraphMatches(object tableSet, NativeV11BuiltInTableLifecycle.RelationGraphProof proof)
    {
        var graph = (NativeV11Reflection.Get(tableSet, "Relations") as IEnumerable)?.Cast<object>().Where(relation =>
            NativeV11Reflection.Get(relation, "ForeignTable") as string == proof.ForeignTable &&
            NativeV11Reflection.Get(relation, "ForeignColumn") as string == proof.ForeignColumn).ToArray() ?? [];
        return graph.Length == 1 && NativeV11Reflection.Get(graph[0], "PrimaryTable") as string == proof.PrimaryTable &&
            NativeV11Reflection.Get(graph[0], "PrimaryColumn") as string == proof.PrimaryColumn &&
            (proof.OtherRelations is null || OtherRelations(tableSet, proof.ForeignTable, proof.ForeignColumn) == proof.OtherRelations);
    }
    internal static string OtherRelations(object tableSet, string foreignTable, string foreignColumn)
    {
        var graph = NativeV11Reflection.Get(tableSet, "Relations") as IEnumerable ?? throw new InvalidDataException("The native relation graph is unavailable.");
        return System.Text.Json.JsonSerializer.Serialize(graph.Cast<object>().Where(relation =>
                NativeV11Reflection.Get(relation, "ForeignTable") as string != foreignTable || NativeV11Reflection.Get(relation, "ForeignColumn") as string != foreignColumn)
            .Select(relation => System.Text.Json.JsonSerializer.Serialize(new[] { NativeV11Reflection.Get(relation, "ForeignTable") as string,
                NativeV11Reflection.Get(relation, "ForeignColumn") as string, NativeV11Reflection.Get(relation, "PrimaryTable") as string,
                NativeV11Reflection.Get(relation, "PrimaryColumn") as string })).OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    internal static bool Supports(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.Table || request.Operation != ModuleOperation.Update || request.Document?.Metadata.ValueKind != JsonValueKind.Object ||
            !request.Document.Metadata.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() != 1 ||
            operations[0].ValueKind != JsonValueKind.Object || !operations[0].TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String) return false;
        return method.GetString() is "setRelation" or "deleteRelation";
    }

    internal static Plan Prepare(NativeV11Session session, ModuleMutationRequest request)
    {
        if (!Supports(request) || request.Document!.Companions.Count != 0 || request.Document.Metadata.EnumerateObject().Count() != 1)
            throw new InvalidDataException("Exactly one native table relation operation is required.");
        var operation = request.Document.Metadata.GetProperty("operations")[0];
        if (operation.EnumerateObject().Count() != 2 || !operation.TryGetProperty("parameters", out var p) || p.ValueKind != JsonValueKind.Object ||
            p.EnumerateObject().Count() != 3 || p.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
            p.EnumerateObject().Any(x => x.Name is not ("foreignColumn" or "primaryTable" or "primaryColumn")))
            throw new InvalidDataException("A relation requires foreignColumn, primaryTable and primaryColumn.");
        string Value(string name)
        {
            if (!p.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid native relation identity.");
            var text = value.GetString()!; V11Safety.Address(new ModuleAddress(ModuleKind.Table, text)); return text;
        }
        var foreignColumn = Value("foreignColumn"); var primaryName = Value("primaryTable"); var primaryColumn = Value("primaryColumn");
        if (primaryName.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Self relations need a separate native owner proof.");
        var baseline = NativeV11BuiltInTableLifecycle.PrepareDelete(session, request with { Operation = ModuleOperation.Delete, Document = null }, addingColumn: true);
        var primary = session.Find(new ModuleAddress(ModuleKind.Table, primaryName));
        if (NativeV11Reflection.Get(primary.Model, "ExternalDatabaseInfo") is not null || NativeV11Reflection.Get(primary.Model, "IsProcessTable") is true ||
            !ReferenceEquals(primary.Parent, baseline.Item.Parent)) throw new NotSupportedException("Only root built-in table relations are supported.");
        var primarySheet = session.Sheet(primaryName);
        NativeV11TableWriter.VerifyOwners(baseline.TableSet, primary.Model, primary.Parent, primarySheet,
            primarySheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(primarySheet, "Tag")!, "TableInfo"));
        object Column(object table, string name)
        {
            var columns = NativeV11Reflection.Get(table, "Columns") as IEnumerable ?? throw new InvalidDataException("Native columns are unavailable.");
            var matches = columns.Cast<object>().Where(c => NativeV11Reflection.Get(c, "Name") as string == name && c.GetType().FullName == "Forguncy.Model.Tables.BindingColumn").ToArray();
            return matches.Length == 1 ? matches[0] : throw new InvalidDataException("A native binding column is absent or ambiguous.");
        }
        if (!Equals(NativeV11Reflection.Get(Column(baseline.Item.Model, foreignColumn), "ColumnType"), NativeV11Reflection.Get(Column(primary.Model, primaryColumn), "ColumnType")))
            throw new InvalidDataException("The native relation column types differ.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        if (!NativeV11BuiltInTableLifecycle.ContainsPhysicalTable(database, primaryName) ||
            !NativeV11BuiltInTableLifecycle.ContainsPhysicalColumn(database, primaryName, primaryColumn) ||
            !NativeV11BuiltInTableLifecycle.ContainsPhysicalColumn(database, request.Address.Name, foreignColumn))
            throw new InvalidDataException("The native relation's physical table or columns are unavailable.");
        var primaryJson = session.Serialize(primary.Route, primary.Model);
        var primarySource = NativeV11PendingSave.ReadFiles(session.SourcePath, [primary.RelativePath])[primary.RelativePath]
            ?? throw new InvalidDataException("The primary table has no saved source definition.");
        if (!V11Safety.Equivalent(V11Safety.Body(System.Text.Encoding.UTF8.GetBytes(primaryJson)), V11Safety.Body(primarySource)))
            throw new InvalidDataException("The primary table differs from its saved source.");
        var delete = operation.GetProperty("method").GetString() == "deleteRelation";
        var relations = (NativeV11Reflection.Get(baseline.Item.Model, "Relations") as IEnumerable)?.Cast<object>().ToArray() ?? [];
        var existing = relations.Where(r => NativeV11Reflection.Get(r, "ForeignColumn") as string == foreignColumn).ToArray();
        if (existing.Length > 1 || delete && (existing.Length != 1 || NativeV11Reflection.Get(existing[0], "PrimaryTable") as string != primaryName ||
            NativeV11Reflection.Get(existing[0], "PrimaryColumn") as string != primaryColumn) || !delete && existing.Length == 1 &&
            NativeV11Reflection.Get(existing[0], "PrimaryTable") as string == primaryName && NativeV11Reflection.Get(existing[0], "PrimaryColumn") as string == primaryColumn)
            throw new InvalidDataException("The requested relation is absent, occupied or ambiguous.");
        var replace = !delete && existing.Length == 1;
        if (replace && NativeV11Reflection.Get(existing[0], "ForeignTable") as string != request.Address.Name)
            throw new InvalidDataException("The previous relation foreign table differs from its owner.");
        NativeV11BuiltInTableLifecycle.RelatedTableSnapshot? previousPrimary = null;
        if (replace && NativeV11Reflection.Get(existing[0], "PrimaryTable") as string is string oldName && oldName != primaryName)
        {
            V11Safety.Address(new ModuleAddress(ModuleKind.Table, oldName));
            if (oldName.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("Self relations require their native owner proof.");
            var old = session.Find(new ModuleAddress(ModuleKind.Table, oldName));
            if (NativeV11Reflection.Get(old.Model, "ExternalDatabaseInfo") is not null || NativeV11Reflection.Get(old.Model, "IsProcessTable") is true ||
                !ReferenceEquals(old.Parent, baseline.Item.Parent)) throw new NotSupportedException("The previous primary must be a root built-in table.");
            var oldSheet = session.Sheet(oldName);
            NativeV11TableWriter.VerifyOwners(baseline.TableSet, old.Model, old.Parent, oldSheet,
                oldSheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(oldSheet, "Tag")!, "TableInfo"));
            var oldColumn = NativeV11Reflection.Get(existing[0], "PrimaryColumn") as string ?? throw new InvalidDataException("The previous relation primary column is unavailable.");
            if (!GraphMatches(baseline.TableSet, new(request.Address.Name, foreignColumn, oldName, oldColumn)))
                throw new InvalidDataException("The native relation graph differs from the previous relationship.");
            if (!Equals(NativeV11Reflection.Get(Column(old.Model, oldColumn), "ColumnType"), NativeV11Reflection.Get(Column(baseline.Item.Model, foreignColumn), "ColumnType")) ||
                !NativeV11BuiltInTableLifecycle.ContainsPhysicalTable(database, oldName) || !NativeV11BuiltInTableLifecycle.ContainsPhysicalColumn(database, oldName, oldColumn))
                throw new InvalidDataException("The previous relation primary column or physical table differs.");
            var oldJson = session.Serialize(old.Route, old.Model);
            var oldSource = NativeV11PendingSave.ReadFiles(session.SourcePath, [old.RelativePath])[old.RelativePath] ?? throw new InvalidDataException("The previous primary source is unavailable.");
            if (!V11Safety.Equivalent(V11Safety.Body(System.Text.Encoding.UTF8.GetBytes(oldJson)), V11Safety.Body(oldSource))) throw new InvalidDataException("The previous primary differs from source.");
            previousPrimary = new(old, oldJson);
        }
        else if (replace && NativeV11Reflection.Get(existing[0], "PrimaryTable") is not string)
            throw new InvalidDataException("The previous relation primary table is unavailable.");
        if (delete || replace)
        {
            var references = NativeV11References.Find(session, request.Address);
            if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException("Relation deletion requires a complete incoming-reference scan.");
        }
        var relationType = Assembly.Load("ForguncyDataAccess").GetType("Forguncy.ForeignkeyInfomation", true)!;
        var relation = delete ? existing[0] : Activator.CreateInstance(relationType)!;
        if (!delete)
        {
            NativeV11Reflection.Set(relation, "ForeignTable", request.Address.Name); NativeV11Reflection.Set(relation, "ForeignColumn", foreignColumn);
            NativeV11Reflection.Set(relation, "PrimaryTable", primaryName); NativeV11Reflection.Set(relation, "PrimaryColumn", primaryColumn);
        }
        var helper = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mf" : "lz", true)!;
        var method = helper.GetMethod(delete ? "DeleteRelation" : "SetRelation", NativeV11Reflection.Flags, null,
            delete ? [relationType] : [relationType, typeof(bool)], null) ?? throw new MissingMethodException("The pinned native relation method is unavailable.");
        if (!method.IsStatic || method.ReturnType != typeof(void)) throw new NotSupportedException("The native relation signature differs.");
        return new(baseline, primary, primaryJson, relation, method, delete, replace, foreignColumn, primaryColumn, previousPrimary, replace ? existing[0] : null,
            previousPrimary is null ? null : OtherRelations(baseline.TableSet, request.Address.Name, foreignColumn));
    }

    internal static NativeV11BuiltInTableLifecycle.CreatedTable Apply(Plan plan)
    {
        var session = plan.Baseline.Session; session.VerifyContext();
        if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.Baseline.SourceRevision ||
            !ReferenceEquals(session.Find(plan.Baseline.Item.Address).Model, plan.Baseline.Item.Model) ||
            !ReferenceEquals(session.Find(plan.Primary.Address).Model, plan.Primary.Model) ||
            !NativeV11DefinitionWriter.Equivalent(plan.PrimaryJson, session.Serialize(plan.Primary.Route, plan.Primary.Model)) ||
            plan.PreviousPrimary is { } previous && !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, previous) ||
            plan.OtherRelations is not null && OtherRelations(plan.Baseline.TableSet, plan.Baseline.Item.Address.Name, plan.ForeignColumn) != plan.OtherRelations)
            throw new InvalidOperationException("The native relation owner baseline changed.");
        var config = session.Context.DesignerAssembly.GetType("Forguncy.Common.Config", true)!;
        var database = (string)NativeV11Reflection.Static(config, "get_LocalAppDBSqliteFilePath")!;
        var before = NativeV11FirstProcessSaveJournal.SnapshotDatabase(database);
        var originalJson = session.Serialize(plan.Baseline.Item.Route, plan.Baseline.Item.Model);
        var wasDirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        try
        {
            plan.Method.Invoke(null, plan.Delete ? [plan.Relation] : [plan.Relation, plan.Replace]);
            session.VerifyContext();
            var relations = (NativeV11Reflection.Get(plan.Baseline.Item.Model, "Relations") as IEnumerable)?.Cast<object>().ToArray() ?? [];
            var matches = relations.Where(r => NativeV11Reflection.Get(r, "ForeignColumn") as string == plan.ForeignColumn &&
                NativeV11Reflection.Get(r, "PrimaryTable") as string == plan.Primary.Address.Name && NativeV11Reflection.Get(r, "PrimaryColumn") as string == plan.PrimaryColumn).ToArray();
            if ((plan.Delete ? matches.Length != 0 : matches.Length != 1 || !ReferenceEquals(matches[0], plan.Relation)) ||
                !NativeV11DefinitionWriter.Equivalent(plan.PrimaryJson, session.Serialize(plan.Primary.Route, plan.Primary.Model)) ||
                plan.PreviousPrimary is { } oldOwner && !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, oldOwner) ||
                plan.PreviousPrimary is not null && !GraphMatches(plan.Baseline.TableSet, new(plan.Baseline.Item.Address.Name, plan.ForeignColumn, plan.Primary.Address.Name, plan.PrimaryColumn, plan.OtherRelations)) ||
                NativeV11FirstProcessSaveJournal.SnapshotDatabase(database) != before)
                throw new InvalidOperationException("Native relation readback or its unaffected owner differs.");
            var item = plan.Baseline.Item;
            NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name); NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
            if (plan.PreviousPrimary is { } prior)
            {
                NativeV11StatisticColumnLifecycle.VerifyMemento(session, item);
                NativeV11StatisticColumnLifecycle.VerifyMemento(session, plan.Primary);
                NativeV11StatisticColumnLifecycle.VerifyMemento(session, prior.Item);
            }
            NativeV11Generation.MarkModules(session, item.Route, "Edit"); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            var proof = new NativeV11BuiltInTableLifecycle.CreatePlan(session, item.Parent, plan.Baseline.TableSet, plan.Method, item.Address, 0,
                plan.Baseline.SourceRevision, plan.Baseline.SourceBefore, null);
            return new(proof, item.Model, session.Serialize(item.Route, item.Model), ExpectedUnchangedDatabase: before,
                RelatedTable: new(plan.Primary, plan.PrimaryJson), PreviousRelatedTable: plan.PreviousPrimary,
                ExpectedRelationGraph: plan.PreviousPrimary is null ? null : new(item.Address.Name, plan.ForeignColumn, plan.Primary.Address.Name, plan.PrimaryColumn, plan.OtherRelations));
        }
        catch (Exception error) when (error is not NativeV11BuiltInTableLifecycle.NativeCreateUncertainException)
        {
            if (plan.PreviousPrimary is { } prior && plan.PreviousRelation is { } originalRelation)
            {
                try
                {
                    session.VerifyContext();
                    if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.Baseline.SourceRevision ||
                        !ReferenceEquals(session.Find(plan.Baseline.Item.Address).Model, plan.Baseline.Item.Model) ||
                        !ReferenceEquals(session.Find(plan.Primary.Address).Model, plan.Primary.Model) ||
                        !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, prior) ||
                        OtherRelations(plan.Baseline.TableSet, plan.Baseline.Item.Address.Name, plan.ForeignColumn) != plan.OtherRelations ||
                        !NativeV11DefinitionWriter.Equivalent(plan.PrimaryJson, session.Serialize(plan.Primary.Route, plan.Primary.Model)))
                        throw new InvalidOperationException("A relation rollback baseline changed.");
                    RestoreRelation(plan.Method, originalRelation);
                    var item = session.Find(plan.Baseline.Item.Address);
                    if (!NativeV11DefinitionWriter.Equivalent(originalJson, session.Serialize(item.Route, item.Model)) ||
                        !GraphMatches(plan.Baseline.TableSet, new(item.Address.Name, plan.ForeignColumn, prior.Item.Address.Name,
                            (string)NativeV11Reflection.Get(originalRelation, "PrimaryColumn")!, plan.OtherRelations)) ||
                        NativeV11FirstProcessSaveJournal.SnapshotDatabase(database) != before)
                        throw new InvalidOperationException("The native relation rollback did not restore the model and database.");
                    NativeV11Reflection.Call(item.Route.Colleague, "Edit", item.Model, item.Address.Name);
                    NativeV11Reflection.Call(item.Route.Colleague, "SaveToCache", true);
                    NativeV11StatisticColumnLifecycle.VerifyMemento(session, item); NativeV11StatisticColumnLifecycle.VerifyMemento(session, plan.Primary);
                    NativeV11StatisticColumnLifecycle.VerifyMemento(session, prior.Item);
                    if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.Baseline.SourceRevision ||
                        !NativeV11BuiltInTableLifecycle.RelatedTableMemoryMatches(session, prior) ||
                        !NativeV11DefinitionWriter.Equivalent(plan.PrimaryJson, session.Serialize(plan.Primary.Route, plan.Primary.Model)) ||
                        NativeV11FirstProcessSaveJournal.SnapshotDatabase(database) != before)
                        throw new InvalidOperationException("The relation rollback changed another owner, source, or database.");
                    NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty);
                }
                catch (Exception restoreError) { throw new NativeV11BuiltInTableLifecycle.NativeCreateUncertainException(new AggregateException(error, restoreError)); }
            }
            // The adapter keeps the operation frozen even if restoration was verified.
            throw new NativeV11BuiltInTableLifecycle.NativeCreateUncertainException(error);
        }
    }
}
