using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Existing table/view definition editing. Read-only external bindings can be
// retargeted only to a registered connection with their data cache unloaded.
// No remote schema or data operation is dispatched by this writer.
internal static class NativeV11TableWriter
{
    internal static bool Supports(ModuleKind kind, ModuleOperation operation) =>
        kind is ModuleKind.Table or ModuleKind.View && operation == ModuleOperation.Update;

    internal static void Validate(NativeV11Session session, ModuleMutationRequest request) => Prepare(session, request);

    internal static Action Apply(NativeV11Session session, ModuleMutationRequest request)
    {
        var prepared = Prepare(session, request);
        session.VerifyContext();
        var wasDirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var originalJson = session.Serialize(prepared.Item.Route, prepared.Item.Model);
        var undo = ApplyCore(prepared.TableSet, prepared.Item.Model, prepared.Incoming,
            () =>
            {
                session.VerifyContext();
                if (!ReferenceEquals(prepared.Item.Parent[prepared.Item.Index], prepared.Item.Model))
                    throw new InvalidOperationException("The native table tree changed during definition editing.");
                NativeV11Reflection.Call(prepared.Item.Route.Colleague, "Edit", prepared.Item.Model, request.Address.Name);
                NativeV11Reflection.Call(prepared.Item.Route.Colleague, "SaveToCache", true);
            },
            () => NativeV11PageWriter.Mark(session, prepared.Item.Model, "Edit"), prepared.BindingChanged, prepared.PermissionChanged);
        try
        {
            VerifyOwners(prepared.TableSet, prepared.Item.Model, prepared.Owner, prepared.Sheet, prepared.TableInfo);
            if (!NativeV11DefinitionWriter.Equivalent(prepared.Normalized, session.Serialize(prepared.Item.Route, prepared.Item.Model)))
                throw new InvalidOperationException("The native table definition differs after editing.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception primary)
        {
            try { undo(); }
            catch (Exception rollback) { throw new AggregateException("Native table editing failed and rollback is incomplete; do not save this document.", primary, rollback); }
            throw;
        }
        var restored = false;
        return () =>
        {
            if (restored) return;
            session.VerifyContext();
            undo();
            VerifyOwners(prepared.TableSet, prepared.Item.Model, prepared.Owner, prepared.Sheet, prepared.TableInfo);
            if (!NativeV11DefinitionWriter.Equivalent(originalJson, session.Serialize(prepared.Item.Route, prepared.Item.Model)))
                throw new InvalidOperationException("Native table rollback did not restore the original definition.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty);
            restored = true;
        };
    }

    private sealed record Prepared(NativeV11Item Item, object Incoming, string Normalized, object TableSet,
        object Owner, object? Sheet, object? TableInfo, bool BindingChanged, bool PermissionChanged);

    private static Prepared Prepare(NativeV11Session session, ModuleMutationRequest request)
    {
        session.VerifyContext();
        if (!Supports(request.Address.Kind, request.Operation) || request.Scope != EffectScope.ProjectDefinition)
            throw new NotSupportedException("Only existing native table/view definition updates are currently implemented.");
        var document = request.Document ?? throw new InvalidDataException("A complete native table definition is required.");
        if (document.Address != request.Address || document.Metadata.ValueKind != JsonValueKind.Object || document.Companions.Count != 0)
            throw new InvalidDataException("The table definition address, metadata, or companion list is invalid.");
        var item = session.Find(request.Address);
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")
            ?? throw new MissingMemberException("Document.ForguncySettings");
        var tableSet = NativeV11Reflection.Get(settings, "TableSet") ?? throw new MissingMemberException("Settings.TableSet");
        if (!ReferenceEquals(NativeV11Reflection.Get(tableSet, "Root"), item.Route.Root))
            throw new InvalidOperationException("The table route does not belong to the current document's table set.");
        var owner = NativeV11Reflection.Get(item.Model, "Owner") ?? throw new InvalidOperationException("The native table has no owner.");
        var sheet = session.Sheet(request.Address.Name);
        object? tableInfo = null;
        if (sheet is not null)
        {
            var tag = NativeV11Reflection.Get(sheet, "Tag") ?? throw new InvalidOperationException("The native table worksheet has no attach data.");
            tableInfo = NativeV11Reflection.Get(tag, "TableInfo") ?? throw new InvalidOperationException("The native table worksheet is not a table projection.");
        }
        VerifyOwners(tableSet, item.Model, owner, sheet, tableInfo);
        // SaveToCache(true) traverses all native tables. Require a complete tree.
        _ = session.Items(ModuleKind.Table); _ = session.Items(ModuleKind.View);
        var json = document.Metadata.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new InvalidDataException("The table definition exceeds the 16 MB limit.");
        NativeV11DefinitionWriter.CheckTypes(document.Metadata, session.Context.DesignerAssembly.GetName().Version!);
        var incoming = NativeV11DefinitionWriter.Load(item.Route, item.RelativePath, json);
        if (incoming.GetType() != item.Model.GetType() ||
            NativeV11Reflection.Get(incoming, "Name") as string != request.Address.Name)
            throw new InvalidDataException("The native table type or identity does not match its address.");
        var normalized = session.Serialize(item.Route, incoming);
        if (!NativeV11DefinitionWriter.Equivalent(json, normalized))
            throw new InvalidDataException("The native table loader did not preserve all supplied metadata; read and edit a complete normalized definition.");
        var oldJson = session.Serialize(item.Route, item.Model);
        var bindingChanged = BindingChanged(oldJson, normalized);
        var permissionChanged = PermissionChanged(oldJson, normalized);
        if (permissionChanged)
        {
            NativeV11DefinitionWriter.VerifyPermissionComponents(session.Context.DesignerAssembly);
            if (IsPermissionTransition(item.Model, incoming)) ValidatePermissionTransition(item.Model, incoming);
            else _ = PermissionLists(item.Model, incoming);
        }
        if (bindingChanged)
        {
            NativeV11TableLifecycle.ValidateExternal(item.Model, request.Address);
            NativeV11TableLifecycle.ValidateExternal(incoming, request.Address);
            NativeV11TableLifecycle.ValidateRelationships(tableSet, request.Address.Name);
            var connections = NativeV11Reflection.Get(settings, "DataConnectionSet") as IEnumerable
                ?? throw new InvalidOperationException("The native data-connection registry is unavailable.");
            _ = NativeV11TableLifecycle.ValidateRegisteredConnection(connections, incoming);
            var manager = NativeV11Reflection.Get(session.Document, "TableSpreadManager") ?? throw new InvalidOperationException("The native table manager is unavailable.");
            var data = manager.GetType().GetProperties(NativeV11Reflection.Flags).Single(property =>
                property.PropertyType.FullName == "Forguncy.LinkedDataSet" && property.GetIndexParameters().Length == 0).GetValue(manager) as System.Data.DataSet
                ?? throw new InvalidOperationException("The native linked-table cache is unavailable.");
            if (data.Tables.Contains(request.Address.Name) || sheet is not null && NativeV11Reflection.Get(sheet, "Visible") is not false)
                throw new InvalidOperationException("Close and unload the external table data before changing its connection binding.");
        }
        ValidateChanges(item.Model, incoming, oldJson, normalized, bindingChanged, permissionChanged);
        return new(item, incoming, normalized, tableSet, owner, sheet, tableInfo, bindingChanged, permissionChanged);
    }

    internal static bool BindingChanged(string before, string after)
    {
        var oldObject = JsonNode.Parse(before)?.AsObject() ?? throw new InvalidDataException("A native table definition is required.");
        var newObject = JsonNode.Parse(after)?.AsObject() ?? throw new InvalidDataException("A native table definition is required.");
        return !JsonNode.DeepEquals(oldObject["ExternalDatabaseInfo"], newObject["ExternalDatabaseInfo"]);
    }

    internal static bool PermissionChanged(string before, string after)
    {
        var left = JsonNode.Parse(before)?.AsObject() ?? throw new InvalidDataException("A native table definition is required.");
        var right = JsonNode.Parse(after)?.AsObject() ?? throw new InvalidDataException("A native table definition is required.");
        return !JsonNode.DeepEquals(left["PermissionData"], right["PermissionData"]);
    }

    private static (IList Existing, object[] Before, object[] After) PermissionLists(object original, object incoming)
    {
        var permission = NativeV11Reflection.Get(original, "PermissionData");
        var replacement = NativeV11Reflection.Get(incoming, "PermissionData");
        var nativeAssembly = Assembly.Load("Forguncy.RbacPermission.Core");
        bool IsNative(object? value) => value is not null && value.GetType().Assembly == nativeAssembly &&
            value.GetType().FullName == "Forguncy.RbacPermission.Core.Impl.Data.DataPermissionData";
        if (!IsNative(permission) || !IsNative(replacement))
            throw new InvalidDataException("Permission binding edits require existing, exact native DataPermissionData; default/null transitions are separate.");
        var before = NativeV11Reflection.Get(permission!, "PermissionBindings") as IList;
        var after = NativeV11Reflection.Get(replacement!, "PermissionBindings") as IList;
        if (before is null || after is null || before.IsReadOnly || before.IsFixedSize || after.Count > 10000)
            throw new InvalidDataException("The native table permission binding collection is unavailable.");
        var expected = after.Cast<object>().ToArray();
        if (expected.Any(value => value is null || value.GetType().Assembly != nativeAssembly ||
            !value.GetType().GetInterfaces().Any(type => type.FullName == "Forguncy.RbacPermission.Core.Impl.Data.IDataPermissionBinding")))
            throw new InvalidDataException("A table permission binding is not a verified native data permission type.");
        var resource = NativeV11Reflection.Get(permission!, "PermissionResource") ?? throw new InvalidDataException("The native permission resource is unavailable.");
        var resourceName = NativeV11Reflection.Get(resource, "Name") as string;
        if (string.IsNullOrWhiteSpace(resourceName) || resourceName != NativeV11Reflection.Get(original, "Name") as string || expected.Any(value =>
            NativeV11Reflection.Get(value, "ResourceName") is string supplied && supplied != resourceName || NativeV11Reflection.Get(value, "RoleNames") is not IEnumerable roles ||
            roles.Cast<object>().Any(role => role is not string text || string.IsNullOrWhiteSpace(text))))
            throw new InvalidDataException("A native permission binding has an invalid resource or role identity.");
        // ResourceName is intentionally omitted by the native permission DTO.
        // Reattach the existing native resource identity before registration.
        foreach (var value in expected) NativeV11Reflection.Set(value, "ResourceName", resourceName);
        return (before, before.Cast<object>().ToArray(), expected);
    }

    private static bool IsPermissionTransition(object original, object incoming) =>
        (NativeV11Reflection.Get(original, "PermissionData") is null) != (NativeV11Reflection.Get(incoming, "PermissionData") is null);

    private static void ValidatePermissionTransition(object original, object incoming)
    {
        if (!IsPermissionTransition(original, incoming) || NativeV11Reflection.Get(original, "ExternalDatabaseInfo") is not null)
            throw new NotSupportedException("Only explicit built-in table permission null transitions are supported.");
        var value = NativeV11Reflection.Get(incoming, "PermissionData") ?? NativeV11Reflection.Get(original, "PermissionData")!;
        var native = Assembly.Load("Forguncy.RbacPermission.Core");
        if (value.GetType().Assembly != native || value.GetType().FullName != "Forguncy.RbacPermission.Core.Impl.Data.DataPermissionData")
            throw new InvalidDataException("The permission transition requires the exact native permission type.");
        var resource = NativeV11Reflection.Get(value, "PermissionResource") ?? throw new InvalidDataException("The native permission resource is unavailable.");
        if (resource.GetType().Assembly != native || resource.GetType().FullName != "Forguncy.RbacPermission.Core.Impl.Data.DataPermissionResource" ||
            NativeV11Reflection.Get(resource, "IsExternal") is not false)
            throw new InvalidDataException("The native built-in permission resource differs.");
        var tableName = NativeV11Reflection.Get(original, "Name") as string ?? throw new InvalidDataException("The native table identity is unavailable.");
        if (NativeV11Reflection.Get(resource, "Name") is string resourceName && !string.IsNullOrEmpty(resourceName) && resourceName != tableName)
            throw new InvalidDataException("The native permission resource belongs to a different table.");
        var bindings = NativeV11Reflection.Get(value, "PermissionBindings");
        if (bindings is null)
            throw new NotSupportedException("The native permission serializer reloads null binding collections as empty; this transition cannot preserve uninitialized permissions.");
        if (bindings is not null && (bindings is not IList list || list.Count > 10000 || list.Cast<object>().Any(binding => binding is null ||
            binding.GetType().Assembly != native || !binding.GetType().GetInterfaces().Any(type => type.FullName == "Forguncy.RbacPermission.Core.Impl.Data.IDataPermissionBinding") ||
            NativeV11Reflection.Get(binding, "ResourceName") is string supplied && !string.IsNullOrEmpty(supplied) && supplied != tableName ||
            NativeV11Reflection.Get(binding, "RoleNames") is not IEnumerable roles || roles.Cast<object>().Any(role => role is not string text || string.IsNullOrWhiteSpace(text)))))
            throw new InvalidDataException("A native permission binding has an invalid resource or role identity.");
        if (NativeV11Reflection.Get(incoming, "PermissionData") is not null)
        {
            NativeV11Reflection.Set(resource, "Name", tableName);
            if (bindings is IList targetBindings) foreach (var binding in targetBindings.Cast<object>()) NativeV11Reflection.Set(binding, "ResourceName", tableName);
        }
    }

    // Works on real native Table/ColumnCollection instances. No replacement of
    // the table or its columns means TableSet's owner/name indices, workbook tag
    // and native permission objects retain their exact identity.
    internal static Action ApplyCore(object tableSet, object original, object incoming, Action stageSave, Action invalidate, bool bindingChanged = false, bool permissionChanged = false)
    {
        var owner = NativeV11Reflection.Get(original, "Owner") ?? throw new InvalidOperationException("The native table has no owner.");
        VerifyOwners(tableSet, original, owner, null, null);
        var columns = Columns(original);
        var replacements = Columns(incoming);
        if (columns.Length != replacements.Length || columns.Select((column, index) =>
                column.GetType() != replacements[index].GetType() ||
                !Equals(NativeV11Reflection.Get(column, "Name"), NativeV11Reflection.Get(replacements[index], "Name"))).Any(value => value))
            throw new InvalidDataException("This operation cannot add, remove, rename or reorder physical/formula columns.");
        var assignments = new List<(object Model, string Property, object? Before, object? After)>();
        Capture(original, incoming, "Comment"); Capture(original, incoming, "ShowBuiltInFields");
        if (bindingChanged) Capture(original, incoming, "ExternalDatabaseInfo");
        var permissionTransition = permissionChanged && IsPermissionTransition(original, incoming);
        if (permissionTransition) ValidatePermissionTransition(original, incoming);
        var permission = permissionChanged && !permissionTransition ? PermissionLists(original, incoming) : default;
        var oldPermission = permissionTransition ? NativeV11Reflection.Get(original, "PermissionData") : null;
        var newPermission = permissionTransition ? NativeV11Reflection.Get(incoming, "PermissionData") : null;
        for (var index = 0; index < columns.Length; index++)
        {
            Capture(columns[index], replacements[index], "Alias");
            Capture(columns[index], replacements[index], "Comment");
            if (columns[index].GetType().FullName == "Forguncy.Model.Tables.FormulaColumn")
                Capture(columns[index], replacements[index], "Formula");
        }
        void Capture(object before, object after, string property)
        {
            var oldValue = NativeV11Reflection.Get(before, property);
            var newValue = NativeV11Reflection.Get(after, property);
            if (!Equals(oldValue, newValue)) assignments.Add((before, property, oldValue, newValue));
        }
        var wasUpdating = NativeV11Reflection.Get(tableSet, "IsUpdating") is true;
        if (wasUpdating) throw new InvalidOperationException("The native table set is already inside an update transaction.");
        var applied = new List<(object Model, string Property, object? Value)>();
        var restored = false;
        void Restore()
        {
            if (restored) return;
            NativeV11Reflection.Call(tableSet, "UpdatingBegin");
            var errors = new List<Exception>();
            try
            {
                for (var index = applied.Count - 1; index >= 0; index--)
                    try { NativeV11Reflection.Set(applied[index].Model, applied[index].Property, applied[index].Value); }
                    catch (Exception error) { errors.Add(error); }
                if (permissionTransition)
                    try { NativeV11Reflection.Set(original, "PermissionData", oldPermission); }
                    catch (Exception error) { errors.Add(error); }
                else if (permissionChanged)
                    try { permission.Existing.Clear(); foreach (var value in permission.Before) permission.Existing.Add(value); }
                    catch (Exception error) { errors.Add(error); }
                try { stageSave(); invalidate(); } catch (Exception error) { errors.Add(error); }
            }
            finally
            {
                try { NativeV11Reflection.Call(tableSet, "UpdatingEnd"); }
                catch (Exception error) { errors.Add(error); }
            }
            VerifyOwners(tableSet, original, owner, null, null);
            if (errors.Count > 0) throw new AggregateException("Native table rollback was incomplete.", errors);
            restored = true;
        }
        NativeV11Reflection.Call(tableSet, "UpdatingBegin");
        Exception? failure = null;
        try
        {
            foreach (var assignment in assignments)
            {
                applied.Add((assignment.Model, assignment.Property, assignment.Before));
                NativeV11Reflection.Set(assignment.Model, assignment.Property, assignment.After);
            }
            if (permissionTransition) NativeV11Reflection.Set(original, "PermissionData", newPermission);
            else if (permissionChanged) { permission.Existing.Clear(); foreach (var value in permission.After) permission.Existing.Add(value); }
            stageSave(); invalidate();
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { NativeV11Reflection.Call(tableSet, "UpdatingEnd"); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
        if (failure is not null)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new AggregateException("Native table editing failed and rollback is incomplete; do not save this document.", failure, rollback); }
            throw new InvalidOperationException("Native table definition editing failed and was rolled back.", failure);
        }
        VerifyOwners(tableSet, original, owner, null, null);
        return Restore;
    }

    internal static void ValidateChanges(object original, object incoming, string before, string after, bool bindingChanged = false, bool permissionChanged = false)
    {
        if (original.GetType().FullName != "Forguncy.Model.Tables.Table" || incoming.GetType() != original.GetType() ||
            NativeV11Reflection.Get(original, "Name") as string != NativeV11Reflection.Get(incoming, "Name") as string)
            throw new InvalidDataException("The native table identity cannot change.");
        var external = NativeV11Reflection.Get(original, "ExternalDatabaseInfo");
        if (external is not null && NativeV11Reflection.Get(external, "AllowModifySchema") is not false)
            throw new InvalidDataException("External schema modification must already be disabled before editing a binding definition.");
        if (NativeV11Reflection.Get(original, "IsProcessTable") is true ||
            NativeV11Reflection.Get(original, "IsBuiltInViewUserInfo") is true ||
            NativeV11Reflection.Get(original, "IsBuiltInViewWorkFlowHistoryInfo") is true)
            throw new InvalidDataException("System/process tables and built-in identity/history views require their native management workflow.");
        var left = JsonNode.Parse(before) as JsonObject ?? throw new InvalidDataException("A native table object is required.");
        var right = JsonNode.Parse(after) as JsonObject ?? throw new InvalidDataException("A native table object is required.");
        var oldColumns = Columns(original); var newColumns = Columns(incoming);
        if (oldColumns.Length != newColumns.Length) throw new InvalidDataException("Adding or deleting columns requires a separately verified native schema operation.");
        var leftColumns = ColumnJson(left); var rightColumns = ColumnJson(right);
        if (leftColumns.Count != oldColumns.Length || rightColumns.Count != newColumns.Length)
            throw new InvalidDataException("The native table's column JSON does not match its model.");
        RemoveEditable(left, "Comment", "ShowBuiltInFields"); RemoveEditable(right, "Comment", "ShowBuiltInFields");
        if (bindingChanged) { left.Remove("ExternalDatabaseInfo"); right.Remove("ExternalDatabaseInfo"); }
        if (permissionChanged)
        {
            if (IsPermissionTransition(original, incoming))
            {
                ValidatePermissionTransition(original, incoming);
                var value = (left["PermissionData"] ?? right["PermissionData"]) as JsonObject ?? throw new InvalidDataException("The native permission DTO is unavailable.");
                if (value.Any(property => property.Key is not ("$type" or "permissionResource" or "permissionBindings")) ||
                    value["permissionResource"] is not JsonObject resource || resource.Any(property => property.Key != "$type"))
                    throw new InvalidDataException("The native permission resource DTO contains unreviewed fields.");
                left.Remove("PermissionData"); right.Remove("PermissionData");
            }
            else
            {
                var oldPermission = left["PermissionData"] as JsonObject ?? throw new InvalidDataException("The existing native table permission is required.");
                var newPermission = right["PermissionData"] as JsonObject ?? throw new InvalidDataException("The replacement native table permission is required.");
                if (!oldPermission.Remove("permissionBindings") || !newPermission.Remove("permissionBindings"))
                    throw new InvalidDataException("The native permission binding DTO shape differs.");
            }
        }
        for (var index = 0; index < oldColumns.Length; index++)
        {
            if (oldColumns[index].GetType() != newColumns[index].GetType() ||
                !Equals(NativeV11Reflection.Get(oldColumns[index], "Name"), NativeV11Reflection.Get(newColumns[index], "Name")))
                throw new InvalidDataException("Column identity, type and order cannot change in this operation.");
            var first = leftColumns[index] as JsonObject ?? throw new InvalidDataException("Invalid native table column JSON.");
            var second = rightColumns[index] as JsonObject ?? throw new InvalidDataException("Invalid native table column JSON.");
            RemoveEditable(first, "Alias", "Comment"); RemoveEditable(second, "Alias", "Comment");
            if (oldColumns[index].GetType().FullName == "Forguncy.Model.Tables.FormulaColumn")
            {
                first.Remove("Formula"); second.Remove("Formula");
                var formula = NativeV11Reflection.Get(newColumns[index], "Formula") as string;
                if (formula?.Contains("#REF!", StringComparison.OrdinalIgnoreCase) is true)
                    throw new InvalidDataException("The replacement table formula contains an invalid reference.");
            }
        }
        if (!JsonNode.DeepEquals(left, right))
            throw new InvalidDataException("Only table comments/field visibility and existing column aliases, comments and formulas can change. Connection, binding, schema, keys, relations and permissions remain frozen.");
    }

    internal static void VerifyOwners(object tableSet, object table, object owner, object? sheet, object? tableInfo)
    {
        if (!ReferenceEquals(NativeV11Reflection.Get(table, "Owner"), owner) ||
            NativeV11Reflection.Call(owner, "TryGetTable", NativeV11Reflection.Get(table, "Name")) is not object indexed ||
            !ReferenceEquals(indexed, table))
            throw new InvalidOperationException("The native table's owner or name index does not match the document table set.");
        VerifyCollectionOwner(tableSet, owner);
        var columns = NativeV11Reflection.Get(table, "Columns")!;
        foreach (var column in Columns(table))
            if (!ReferenceEquals(NativeV11Reflection.Get(column, "Owner"), columns) ||
                !ReferenceEquals(NativeV11Reflection.Get(column, "Table"), table))
                throw new InvalidOperationException("The native column owner is inconsistent with its table.");
        if (sheet is not null && (tableInfo is null ||
            !ReferenceEquals(NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "TableInfo"), tableInfo) ||
            !ReferenceEquals(NativeV11Reflection.Get(tableInfo, "Table"), table)))
            throw new InvalidOperationException("The native worksheet table projection has changed.");
    }

    internal static void VerifyCollectionOwner(object tableSet, object owner)
    {
        var root = NativeV11Reflection.Get(tableSet, "Root") ?? throw new InvalidOperationException("The native table root is unavailable.");
        var rootItems = NativeV11Reflection.Get(root, "Items")!;
        if (!ReferenceEquals(NativeV11Reflection.Get(rootItems, "TableSet"), tableSet))
            throw new InvalidOperationException("The native root collection belongs to a different table set.");
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var matches = 0;
        void Visit(object folder)
        {
            if (!visited.Add(folder) || NativeV11Reflection.Get(folder, "Items") is not IList items)
                throw new InvalidOperationException("The native table folder graph is invalid or cyclic.");
            // Native child folders use TableCollection() with a null TableSet
            // pointer. Their actual parent/child graph proves ownership.
            var directOwner = NativeV11Reflection.Get(items, "TableSet");
            if (directOwner is not null && !ReferenceEquals(directOwner, tableSet))
                throw new InvalidOperationException("A native descendant collection belongs to a different table set.");
            if (ReferenceEquals(items, owner)) matches++;
            foreach (var child in items.Cast<object>())
            {
                if (!ReferenceEquals(NativeV11Reflection.Get(child, "Owner"), items))
                    throw new InvalidOperationException("A native table node has an inconsistent collection owner.");
                if (NativeV11Reflection.Get(child, "Items") is IList) Visit(child);
            }
        }
        Visit(root);
        if (matches != 1) throw new InvalidOperationException("The native collection is not uniquely owned by this table set.");
    }

    private static object[] Columns(object table) =>
        ((IEnumerable)(NativeV11Reflection.Get(table, "Columns") ?? throw new MissingMemberException("Table.Columns"))).Cast<object>().ToArray();

    private static JsonArray ColumnJson(JsonObject table) => table["Columns"] switch
    {
        JsonArray array => array,
        JsonObject wrapper when wrapper["$values"] is JsonArray values => values,
        null => new(),
        _ => throw new InvalidDataException("The native table Columns representation is invalid.")
    };

    private static void RemoveEditable(JsonObject value, params string[] names)
    {
        foreach (var name in names) value.Remove(name);
    }
}
