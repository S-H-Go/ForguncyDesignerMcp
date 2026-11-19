using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// One reviewed native table-folder delete, including the private SQLite owner.
// A dispatched native database operation is never replayed after uncertainty.
internal static class NativeV11TableFolderDelete
{
    internal sealed record Child(NativeV11Item Item, MethodInfo Delete, JsonElement Metadata);
    internal sealed record Plan(NativeV11Session Session, NativeV11FolderInventory Inventory,
        NativeV11FolderInventory.MutationPlan Owners, NativeV11FolderDeleteJournal Journal,
        IReadOnlyList<Child> Children, object TableSet, object[] InternalRelations, object[] OutsideRelations,
        IReadOnlyList<(NativeV11Item Item, JsonElement Metadata)> OtherTables, MethodInfo DeleteRelation,
        string Database, string SourceRevision, NativeV11UnchangedDatabaseProof DatabaseBaseline);
    internal sealed record Completed(Plan Plan, (string Hash, long Length, bool Valid) DatabaseSnapshot);

    internal static Plan Capture(NativeV11Session session, ModuleMutationRequest request)
    {
        session.VerifyContext();
        if (request.Operation != ModuleOperation.Delete || request.Scope != EffectScope.ProjectDefinition || request.Document is not null ||
            request.Address.Kind != ModuleKind.Folder || request.Address.FolderPath?.Split('/')[0] != "Tables")
            throw new InvalidDataException("A built-in table folder deletion is required.");
        var store = new NativeV11Folders(session);
        var changes = store.CaptureNativeChanges(request);
        var inventory = NativeV11FolderInventory.Capture(session, request.Address, true, changes);
        if (inventory.Items.Count == 0) throw new InvalidOperationException("A table-folder group requires at least one table or view.");
        store.ValidateGroupedBuiltInTables(inventory);
        NativeV11BuiltInTableLifecycle.VerifyPrivateNativeProvider(session);
        var database = NativeV11CollaborationOwners.TableDatabasePath();
        NativeV11FirstProcessSaveJournal.VerifySqliteOwner(session.SaveFile, database);
        var databaseBaseline = NativeV11UnchangedDatabaseProof.Capture(session, database);
        var sourceRevision = NativeV11SourceRevision.Capture(session.SourcePath);
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var tableSet = NativeV11Reflection.Get(settings, "TableSet")!;
        if (NativeV11Reflection.Get(tableSet, "IsUpdating") is true) throw new InvalidOperationException("The native table graph is busy.");
        var names = inventory.Items.Select(item => item.Address.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (names.Count != inventory.Items.Count) throw new InvalidDataException("The table group contains colliding names.");
        var relations = (NativeV11Reflection.Get(tableSet, "Relations") as IEnumerable)?.Cast<object>().ToArray()
            ?? throw new InvalidOperationException("The native table relation registry is unavailable.");
        var internalRelations = new List<object>();
        foreach (var relation in relations)
        {
            var primary = NativeV11Reflection.Get(relation, "PrimaryTable") as string;
            var foreign = NativeV11Reflection.Get(relation, "ForeignTable") as string;
            if (primary is null || foreign is null) throw new InvalidDataException("A native table relation has no complete endpoints.");
            if (!names.Contains(primary) && !names.Contains(foreign)) continue;
            if (!names.Contains(primary) || !names.Contains(foreign)) throw new InvalidOperationException("A table outside the deletion group has a native relation to it.");
            internalRelations.Add(relation);
        }
        var relationType = Assembly.Load("ForguncyDataAccess").GetType("Forguncy.ForeignkeyInfomation", true)!;
        var helper = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mf" : "lz", true)!;
        var deleteRelation = helper.GetMethod("DeleteRelation", NativeV11Reflection.Flags, null, [relationType], null)
            ?? throw new MissingMethodException("The native relation deletion method is unavailable.");
        var native = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "mg" : "ma", true)!;
        var children = new List<Child>();
        foreach (var item in inventory.Items)
        {
            var sheet = session.Sheet(item.Address.Name);
            NativeV11TableWriter.VerifyOwners(tableSet, item.Model, item.Parent, sheet,
                sheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "TableInfo"));
            var sheets = (IList)NativeV11Reflection.Get(session.Workbook, "Sheets")!;
            var active = Convert.ToInt32(NativeV11Reflection.Get(session.Workbook, "ActiveSheetIndex"));
            if (sheet is not null && active >= 0 && active < sheets.Count && ReferenceEquals(sheets[active], sheet))
                throw new InvalidOperationException("Switch to a page before deleting the active table.");
            var definition = NativeV11BuiltInTableLifecycle.CaptureSourceGroup(session, item.Address, true);
            if (definition.DatabasePath != database) throw new InvalidOperationException("The table has an unexpected database owner.");
            NativeV11BuiltInTableLifecycle.VerifyDatabaseBaseline(session, definition);
            var live = V11Safety.Under(session.LocalPath, database);
            if (!NativeV11BuiltInTableLifecycle.ContainsPhysicalTable(live, item.Address.Name, onlyView: item.Address.Kind == ModuleKind.View))
                throw new InvalidOperationException("A reviewed physical table or view is missing.");
            var method = native.GetMethod(item.Address.Kind == ModuleKind.View ? "DeleteViewAsync" : "DeleteTableAsync",
                NativeV11Reflection.Flags, null, [item.Parent.GetType(), item.Model.GetType()], null)
                ?? throw new MissingMethodException("The native nested table deletion method is unavailable.");
            children.Add(new(item, method, NativeV11SourceMetadata.Capture(session, item.Address)));
        }
        var owners = inventory.Project(request, request.Address, changes);
        var journal = NativeV11FolderDeleteJournal.Capture(session, inventory, database);
        var targetModels = inventory.Items.Select(item => item.Model).ToHashSet(ReferenceEqualityComparer.Instance);
        var otherTables = session.Items(ModuleKind.Table).Concat(session.Items(ModuleKind.View)).Where(item => !targetModels.Contains(item.Model))
            .Select(item => (item, NativeV11SourceMetadata.Capture(session, item.Address))).ToArray();
        if (sourceRevision != NativeV11SourceRevision.Capture(session.SourcePath)) throw new InvalidOperationException("The table folder source changed while planning.");
        return new(session, inventory, owners, journal, children, tableSet, internalRelations.ToArray(),
            relations.Where(relation => !internalRelations.Contains(relation, ReferenceEqualityComparer.Instance)).ToArray(), otherTables,
            deleteRelation, database, sourceRevision, databaseBaseline);
    }

    internal static async Task<Completed> ApplyAsync(Plan plan)
    {
        var session = plan.Session; session.VerifyContext();
        NativeV11BuiltInTableLifecycle.VerifyPrivateNativeProvider(session);
        if (NativeV11SourceRevision.Capture(session.SourcePath) != plan.SourceRevision || !plan.Journal.VerifyOriginal(session))
            throw new InvalidOperationException("The reviewed table folder source changed before deletion.");
        plan.DatabaseBaseline.Verify(session);
        var addresses = plan.Children.Select(child => child.Item.Address).ToHashSet();
        foreach (var child in plan.Children)
        {
            if (!ReferenceEquals(session.Find(child.Item.Address).Model, child.Item.Model) ||
                !V11Safety.Equivalent(child.Metadata, NativeV11SourceMetadata.Capture(session, child.Item.Address)))
                throw new InvalidOperationException("A reviewed table model changed.");
            var references = NativeV11References.Find(session, child.Item.Address, child.Item.Model);
            if (!references.Complete || references.References.Any(reference => !addresses.Contains(reference.Source)))
                throw new InvalidOperationException("A table has external or incomplete reference evidence.");
        }
        var relations = (NativeV11Reflection.Get(plan.TableSet, "Relations") as IEnumerable)?.Cast<object>().ToArray()
            ?? throw new InvalidOperationException("The native relation registry is unavailable.");
        if (relations.Length != plan.InternalRelations.Length + plan.OutsideRelations.Length ||
            plan.InternalRelations.Concat(plan.OutsideRelations).Any(relation => !relations.Any(current => ReferenceEquals(current, relation))))
            throw new InvalidOperationException("The reviewed internal table relations changed.");
        VerifyOtherTables(plan, beforeDeletion: true);
        var live = V11Safety.Under(session.LocalPath, plan.Database);
        using var preservation = NativeV11SqlitePreservation.Capture(live);
        try
        {
            foreach (var relation in plan.InternalRelations) plan.DeleteRelation.Invoke(null, [relation]);
            foreach (var child in plan.Children.OrderByDescending(child => child.Item.Address.Kind == ModuleKind.View))
            {
                await NativeV11BuiltInTableLifecycle.DispatchDeleteAsync(child.Delete, child.Item.Parent, child.Item.Model).ConfigureAwait(true);
                session.VerifyContext();
                if (session.Items(child.Item.Address.Kind).Any(item => ReferenceEquals(item.Model, child.Item.Model)) || session.Sheet(child.Item.Address.Name) is not null ||
                    NativeV11BuiltInTableLifecycle.ContainsPhysicalTable(live, child.Item.Address.Name, includeViews: true))
                    throw new InvalidOperationException("A native table deletion did not remove its reviewed model and SQLite object.");
                var keys = (IEnumerable)(NativeV11Reflection.Get(child.Item.Route.Loader, "Keys") ?? throw new InvalidOperationException("The native table save registry is unavailable."));
                if (keys.Cast<object>().OfType<string>().Any(key => key.Replace('\\', '/').Equals(child.Item.RelativePath, StringComparison.OrdinalIgnoreCase)))
                    NativeV11Reflection.Call(child.Item.Route.Colleague, "Delete", child.Item.Model);
                NativeV11Reflection.Call(child.Item.Route.Colleague, "SaveToCache", true);
                NativeV11Generation.MarkModules(session, child.Item.Route, "Remove");
            }
            var folderStore = new NativeV11Folders(session);
            foreach (var address in plan.Inventory.Folders.OrderByDescending(value => value.FolderPath!.Count(character => character == '/')))
                folderStore.RemoveReviewedEmptyTableFolder(address);
            VerifyOtherTables(plan);
            preservation.Verify(live, allowLiveWal: true, deletedObjects: plan.Children.Select(child => child.Item.Address.Name).ToArray());
            var expected = NativeV11FirstProcessSaveJournal.SnapshotDatabase(live, allowLiveWal: true);
            plan.Journal.VerifyMemory(session);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            return new(plan, expected);
        }
        catch (Exception error) when (error is not NativeV11BuiltInTableLifecycle.NativeCreateUncertainException)
        { throw new NativeV11BuiltInTableLifecycle.NativeCreateUncertainException(error); }
    }

    internal static void VerifyMemory(Completed completed)
    {
        var plan = completed.Plan; plan.Journal.VerifyMemory(plan.Session); VerifyOtherTables(plan);
        var live = V11Safety.Under(plan.Session.LocalPath, plan.Database);
        if (NativeV11FirstProcessSaveJournal.SnapshotDatabase(live, allowLiveWal: true) != completed.DatabaseSnapshot)
            throw new InvalidOperationException("The native SQLite database changed after the grouped deletion.");
    }

    internal static bool SourceMatches(Completed completed)
    {
        var plan = completed.Plan; VerifyMemory(completed);
        if (!plan.Journal.VerifyDeleted(plan.Session) || !NativeV11FirstProcessSaveJournal.DatabaseMatches(plan.Session.SourcePath, plan.Session.LastPath, plan.Database)) return false;
        using var staged = File.OpenRead(V11Safety.Under(plan.Session.LastPath, plan.Database));
        return NativeV11BuiltInTableLifecycle.DatabaseFingerprint(staged) == completed.DatabaseSnapshot;
    }

    private static void VerifyOtherTables(Plan plan, bool beforeDeletion = false)
    {
        foreach (var (item, metadata) in plan.OtherTables)
            if (!ReferenceEquals(plan.Session.Find(item.Address).Model, item.Model) ||
                !V11Safety.Equivalent(metadata, NativeV11SourceMetadata.Capture(plan.Session, item.Address)))
                throw new InvalidOperationException("An unrelated native table or view changed during grouped deletion.");
        var current = (NativeV11Reflection.Get(plan.TableSet, "Relations") as IEnumerable)?.Cast<object>().ToArray()
            ?? throw new InvalidOperationException("The native relation registry is unavailable.");
        var expected = beforeDeletion ? plan.OutsideRelations.Concat(plan.InternalRelations).ToArray() : plan.OutsideRelations;
        if (current.Length != expected.Length || expected.Any(relation => !current.Any(value => ReferenceEquals(value, relation))))
            throw new InvalidOperationException("An unrelated native table relation changed during grouped deletion.");
    }
}
