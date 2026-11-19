using System.Collections;
using System.Data;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// External binding definitions only. The native asynchronous import/refresh/
// delete-table helpers also perform database work and are deliberately not used.
// This lifecycle is separate from the existing in-place column/formula editor.
internal static class NativeV11TableLifecycle
{
    internal static bool Supports(ModuleKind kind, ModuleOperation operation) =>
        kind is ModuleKind.Table or ModuleKind.View && operation is ModuleOperation.Create or ModuleOperation.Delete;

    internal static void Validate(NativeV11Session session, ModuleMutationRequest request) => Prepare(session, request);

    private sealed record Prepared(NativeV11Route Route, object TableSet, object Manager, IList Parent,
        object Model, object? Existing, string? Json, object? Sheet, int SheetIndex,
        object? ActiveSheet, DataSet DataSet, DataTable? CachedData, IReadOnlyList<(string Name, object? Value)> Permissions, object Binding);

    internal static Action Apply(NativeV11Session session, ModuleMutationRequest request)
    {
        var prepared = Prepare(session, request);
        session.VerifyContext();
        var wasDirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var create = request.Operation == ModuleOperation.Create;
        var sheets = (IList)NativeV11Reflection.Get(session.Workbook, "Sheets")!;
        Action? undoTree = null;
        var workspaceAttempted = false;
        var restored = false;
        void Invalidate()
        {
            var change = NativeV11DefinitionWriter.GenerationChange(prepared.Parent, prepared.Existing,
                create ? prepared.Model : null);
            if (create && prepared.Parent.Contains(prepared.Model)) change.State = "New";
            NativeV11Generation.MarkModules(session, prepared.Route, change.State);
        }
        void RestoreWorkspace()
        {
            var current = session.Sheet(request.Address.Name);
            if (create)
            {
                if (current is not null)
                {
                    RequireProjection(current, prepared.Model);
                    NativeV11Reflection.Call(prepared.Manager, "RemoveTable", request.Address.Name);
                }
                else if (prepared.DataSet.Tables.Contains(request.Address.Name))
                    NativeV11Reflection.Call(prepared.Manager, "RemoveTableFromLinkedDataSet", request.Address.Name);
            }
            else
            {
                if (prepared.CachedData is not null)
                {
                    var cached = prepared.DataSet.Tables[request.Address.Name];
                    if (cached is not null && !ReferenceEquals(cached, prepared.CachedData))
                        throw new InvalidOperationException("The native table data cache changed during rollback.");
                    if (cached is null) NativeV11Reflection.Call(prepared.Manager, "AddTableToLinkedDataSet", prepared.CachedData);
                }
                if (prepared.Sheet is not null)
                {
                    if (current is not null && !ReferenceEquals(current, prepared.Sheet))
                        throw new InvalidOperationException("The native table worksheet changed during rollback.");
                    if (current is null) sheets.Insert(Math.Min(prepared.SheetIndex, sheets.Count), prepared.Sheet);
                    RequireProjection(prepared.Sheet, prepared.Model);
                }
            }
            if (prepared.ActiveSheet is not null && sheets.IndexOf(prepared.ActiveSheet) is var activeIndex && activeIndex >= 0 &&
                Convert.ToInt32(NativeV11Reflection.Get(session.Workbook, "ActiveSheetIndex")) != activeIndex)
                NativeV11Reflection.Set(session.Workbook, "ActiveSheetIndex", activeIndex);
        }
        void Restore()
        {
            if (restored) return;
            session.VerifyContext();
            NativeV11Reflection.Call(prepared.TableSet, "UpdatingBegin");
            var errors = new List<Exception>();
            try
            {
                if (create && workspaceAttempted)
                    try { RestoreWorkspace(); } catch (Exception error) { errors.Add(error); }
                if (undoTree is not null)
                    try { undoTree(); } catch (Exception error) { errors.Add(error); }
                if (!create && workspaceAttempted)
                    try { RestoreWorkspace(); } catch (Exception error) { errors.Add(error); }
                try { NativeV11Reflection.Call(prepared.Route.Colleague, "SaveToCache", true); Invalidate(); }
                catch (Exception error) { errors.Add(error); }
            }
            finally
            {
                try { NativeV11Reflection.Call(prepared.TableSet, "UpdatingEnd"); }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count > 0) throw new AggregateException("Native binding rollback is incomplete; do not save this document.", errors);
            if (!create)
            {
                VerifyIdentity(prepared);
                NativeV11TableWriter.VerifyOwners(prepared.TableSet, prepared.Model, prepared.Parent, prepared.Sheet,
                    prepared.Sheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(prepared.Sheet, "Tag")!, "TableInfo"));
            }
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty);
            restored = true;
        }
        Exception? failure = null;
        NativeV11Reflection.Call(prepared.TableSet, "UpdatingBegin");
        try
        {
            if (!create)
            {
                workspaceAttempted = true;
                // Native RemoveTable changes ActiveSheetIndex even when no
                // worksheet exists. A loaded definition can legitimately have
                // no worksheet projection; remove only its actual data cache.
                if (prepared.Sheet is not null)
                    NativeV11Reflection.Call(prepared.Manager, "RemoveTable", request.Address.Name);
                else if (prepared.CachedData is not null)
                    NativeV11Reflection.Call(prepared.Manager, "RemoveTableFromLinkedDataSet", request.Address.Name);
            }
            undoTree = ApplyTreeCore(prepared.Route, request.Operation, prepared.Parent, prepared.Existing,
                create ? prepared.Model : null, Invalidate, create ? model =>
                {
                    var permission = LoadNativePermission(session, model);
                    var normalized = AssignCreationPermission(session.Context.DesignerAssembly, session.Generator,
                        prepared.Route, model, prepared.Json!, permission);
                    prepared = prepared with { Json = normalized, Permissions = PermissionIdentities(model) };
                } : null);
            if (create)
            {
                workspaceAttempted = true;
                // AddWorkSheet's second parameter is forwarded directly to
                // Worksheet.Visible. Do not open/query an external table.
                var sheet = NativeV11Reflection.Call(prepared.Manager, "AddWorkSheet", prepared.Model, false)
                    ?? throw new InvalidOperationException("The native table manager returned no worksheet.");
                RequireProjection(sheet, prepared.Model);
                NativeV11TableWriter.VerifyOwners(prepared.TableSet, prepared.Model, prepared.Parent, sheet,
                    NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "TableInfo"));
                if (NativeV11Reflection.Get(sheet, "Visible") is not false)
                    throw new InvalidOperationException("The new binding worksheet must remain unopened.");
                var current = session.Find(request.Address);
                if (!ReferenceEquals(current.Model, prepared.Model) ||
                    !NativeV11DefinitionWriter.Equivalent(prepared.Json!, session.Serialize(prepared.Route, prepared.Model)))
                    throw new InvalidOperationException("Native binding readback differs after creation.");
                VerifyIdentity(prepared);
            }
            else if (prepared.Parent.Contains(prepared.Model) || session.Sheet(request.Address.Name) is not null ||
                prepared.DataSet.Tables.Contains(request.Address.Name))
                throw new InvalidOperationException("The deleted native binding still has a model or workspace projection.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            try { NativeV11Reflection.Call(prepared.TableSet, "UpdatingEnd"); }
            catch (Exception error) { failure = failure is null ? error : new AggregateException(failure, error); }
        }
        if (failure is not null)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new AggregateException("Native binding mutation failed and rollback is incomplete; do not save this document.", failure, rollback); }
            throw new InvalidOperationException("Native binding mutation failed and was rolled back.", failure);
        }
        return Restore;
    }

    // The same real native colleague/loader transaction is exercised by the
    // isolated fixture. Native workbook-manager acceptance needs a bound document.
    internal static Action ApplyTreeCore(NativeV11Route route, ModuleOperation operation, IList parent,
        object? existing, object? incoming, Action invalidate, Action<object>? initialize = null)
    {
        if (route.Kind != ModuleKind.Table || operation is not (ModuleOperation.Create or ModuleOperation.Delete))
            throw new InvalidOperationException("Only native table binding registration and removal belong to this lifecycle.");
        var initialized = false;
        return NativeV11DefinitionWriter.ApplyCore(route, operation, parent, existing, incoming, () =>
        {
            if (operation == ModuleOperation.Create && incoming is not null && parent.Contains(incoming) && !initialized)
            {
                initialized = true;
                initialize?.Invoke(incoming);
                NativeV11Reflection.Call(route.Colleague, "Edit", incoming, NativeV11Reflection.Get(incoming, "Name"));
                NativeV11Reflection.Call(route.Colleague, "SaveToCache", true);
            }
            invalidate();
        });
    }

    // Component-only empty binding data. This is not the production/default UI
    // initializer: null and an empty DataPermissionData binding list differ.
    internal static object CreateComponentPermission(Assembly designer)
    {
        var profile = V11BuildProfile.Resolve(designer);
        NativeV11DefinitionWriter.VerifyPermissionComponents(designer);
        var factoryType = designer.GetType(profile.Version == "11.0.104.0" ? "ju" : "jo", true)!;
        var factory = NativeV11Reflection.Static(factoryType, "get_Instance")!;
        // The actual native false branch is safe to exercise without the global
        // role/group provider, and supplies real typed DTOs for persistence tests.
        var permission = NativeV11Reflection.Call(factory, "CreateForDesigner", false)
            ?? throw new InvalidOperationException("The native data permission factory returned no default permission data.");
        if (permission.GetType().FullName != "Forguncy.RbacPermission.Core.Impl.Data.DataPermissionData" ||
            NativeV11Reflection.Get(permission, "PermissionResource") is null ||
            NativeV11Reflection.Get(permission, "PermissionBindings") is not IList { Count: 0 })
            throw new InvalidOperationException("The native component permission factory did not produce typed empty binding data.");
        return permission;
    }

    internal static (object Model, string Normalized) LoadCreation(Assembly designer, Type generator, NativeV11Route route,
        string path, JsonElement metadata, ModuleAddress address)
    {
        if (metadata.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(metadata.GetRawText()) > 16 * 1024 * 1024)
            throw new InvalidDataException("A complete native binding JSON object within 16 MB is required.");
        NativeV11DefinitionWriter.CheckTypes(metadata, designer.GetName().Version!);
        var model = NativeV11DefinitionWriter.Load(route, path, metadata.GetRawText());
        var normalized = Serialize(generator, route, model);
        if (!NativeV11DefinitionWriter.Equivalent(metadata.GetRawText(), normalized))
            throw new InvalidDataException("The native binding loader did not preserve all supplied metadata.");
        if (metadata.EnumerateObject().Any(property => property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A new binding must obtain permission data from the actual native permission provider.");
        ValidateExternal(model, address);
        return (model, normalized);
    }

    internal static string AssignCreationPermission(Assembly designer, Type generator, NativeV11Route route,
        object model, string baseline, object? permission)
    {
        NativeV11DefinitionWriter.VerifyPermissionComponents(designer);
        if (permission is not null)
        {
            if (permission.GetType().FullName != "Forguncy.RbacPermission.Core.Impl.Data.DataPermissionData" ||
                permission.GetType().Assembly != Assembly.Load("Forguncy.RbacPermission.Core") ||
                !permission.GetType().GetInterfaces().Any(type => type.FullName == "Forguncy.RbacPermission.Core.Impl.Data.IDataPermissionData"))
                throw new InvalidOperationException("The native binding permission data does not match the verified V11 build.");
        }
        NativeV11Reflection.Set(model, "PermissionData", permission);
        var normalized = Serialize(generator, route, model);
        var before = JsonNode.Parse(baseline)!.AsObject(); var after = JsonNode.Parse(normalized)!.AsObject();
        before.Remove("PermissionData"); after.Remove("PermissionData");
        if (!JsonNode.DeepEquals(before, after))
            throw new InvalidOperationException("Native permission initialization changed unrelated binding metadata.");
        return normalized;
    }

    private static object? LoadNativePermission(NativeV11Session session, object model)
    {
        NativeV11DefinitionWriter.VerifyPermissionComponents(session.Context.DesignerAssembly);
        var designer = session.Context.DesignerAssembly;
        var helper = designer.GetType(session.Profile.Version == "11.0.104.0" ? "jb" : "iw", true)!;
        var name = (string)NativeV11Reflection.Get(model, "Name")!;
        if (!ReferenceEquals(NativeV11Reflection.Static(helper, "ResolveTableModelByResourceName", name), model))
            throw new InvalidOperationException("The native permission provider does not own the new table binding.");
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var connection = ValidateRegisteredConnection((IEnumerable)NativeV11Reflection.Get(settings, "DataConnectionSet")!, model);
        if (NativeV11Reflection.Get(model, "IsLinkedView") is true)
            VerifySqlServerUtilityRegistry(designer, NativeV11Reflection.Get(connection, "DataBaseType") as string);
        var eligible = PermissionEligible(designer, model);
        if (!eligible) return null; // Actual native registry exclusion, not a fabricated empty grant set.
        var provider = NativeV11Reflection.Static(designer.GetType(session.Profile.Version == "11.0.104.0" ? "ij" : "id", true)!, "get_Instance")!;
        // Load follows the native UI/default path: existing data is null, so
        // CreateForServer(name,null) runs the actual default binding creator.
        var data = NativeV11Reflection.Call(provider, "Load", name)
            ?? throw new InvalidOperationException("The native table permission provider returned no default data.");
        var resource = NativeV11Reflection.Get(data, "PermissionResource");
        if (resource is null || NativeV11Reflection.Get(resource, "Name") as string != name ||
            NativeV11Reflection.Get(resource, "IsExternal") is not true)
            throw new InvalidOperationException("The native default permission resource does not match this external binding.");
        return data;
    }

    internal static bool PermissionEligible(Assembly designer, object model)
    {
        var profile = V11BuildProfile.Resolve(designer);
        var helper = designer.GetType(profile.Version == "11.0.104.0" ? "jb" : "iw", true)!;
        return NativeV11Reflection.Static(helper, "DataPermissionFilter", model) as bool?
            ?? throw new InvalidOperationException("The native table permission eligibility contract is unavailable.");
    }

    private static string Serialize(Type generator, NativeV11Route route, object model) =>
        (string)NativeV11Reflection.Static(generator, "SaveToStr", NativeV11Reflection.Call(route.Mediator, "ConvertToSaveData", model)!)!;

    private static Prepared Prepare(NativeV11Session session, ModuleMutationRequest request)
    {
        session.VerifyContext();
        if (!Supports(request.Address.Kind, request.Operation) || request.Scope != EffectScope.ProjectDefinition)
            throw new NotSupportedException("Only external binding creation/removal in project-definition scope belongs to this lifecycle.");
        var route = session.Route(ModuleKind.Table);
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var tableSet = NativeV11Reflection.Get(settings, "TableSet")!;
        if (!ReferenceEquals(NativeV11Reflection.Get(tableSet, "Root"), route.Root) ||
            NativeV11Reflection.Get(tableSet, "IsUpdating") is true)
            throw new InvalidOperationException("The native table set is unavailable or already being updated.");
        var all = session.Items(ModuleKind.Table).Concat(session.Items(ModuleKind.View)).ToArray();
        var manager = NativeV11Reflection.Get(session.Document, "TableSpreadManager")
            ?? throw new MissingMemberException("Document.TableSpreadManager");
        if (!ReferenceEquals(NativeV11Reflection.Get(manager, "Workbook"), session.Workbook))
            throw new InvalidOperationException("The native table manager belongs to a different workbook.");
        var dataProperty = manager.GetType().GetProperties(NativeV11Reflection.Flags).Single(property =>
            property.PropertyType.FullName == "Forguncy.LinkedDataSet" && property.GetIndexParameters().Length == 0);
        var dataSet = dataProperty.GetValue(manager) as DataSet ?? throw new InvalidOperationException("The native linked data set is unavailable.");
        var create = request.Operation == ModuleOperation.Create;
        NativeV11Item? existing = null;
        IList parent;
        object model;
        string? normalized = null;
        if (create)
        {
            if (all.Any(item => item.Address.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                session.Sheet(request.Address.Name) is not null || dataSet.Tables.Contains(request.Address.Name))
                throw new InvalidOperationException("The native binding name is already in use.");
            var document = request.Document ?? throw new InvalidDataException("A complete native binding definition is required.");
            if (document.Address != request.Address || document.Metadata.ValueKind != JsonValueKind.Object || document.Companions.Count != 0)
                throw new InvalidDataException("The native binding metadata, address or companion list is invalid.");
            var loaded = LoadCreation(session.Context.DesignerAssembly, session.Generator, route,
                session.NativePath(request.Address), document.Metadata, request.Address);
            model = loaded.Model; normalized = loaded.Normalized;
            parent = Parent(route.Root, request.Address.FolderPath);
        }
        else
        {
            if (request.Document is not null) throw new InvalidDataException("Binding removal must not include replacement metadata.");
            existing = session.Find(request.Address); model = existing.Model; parent = existing.Parent;
        }
        ValidateExternal(model, request.Address);
        NativeV11TableWriter.VerifyCollectionOwner(tableSet, parent);
        NativeV11DefinitionWriter.VerifyPermissionComponents(session.Context.DesignerAssembly);
        if (NativeV11Reflection.Get(model, "PermissionData") is object permission &&
            (permission.GetType().FullName != "Forguncy.RbacPermission.Core.Impl.Data.DataPermissionData" ||
             permission.GetType().Assembly != Assembly.Load("Forguncy.RbacPermission.Core")))
            throw new InvalidOperationException("The existing binding permission data does not belong to the verified native component.");
        var binding = NativeV11Reflection.Get(model, "ExternalDatabaseInfo")!;
        if (NativeV11Reflection.Get(settings, "DataConnectionSet") is not IEnumerable connections)
            throw new InvalidOperationException("The native data-connection registry is unavailable.");
        var connection = ValidateRegisteredConnection(connections, model);
        // Eligible views reach the native utility factory even during ACL
        // eligibility checks. Never let it discover or activate new plugins.
        if (create && NativeV11Reflection.Get(model, "IsLinkedView") is true)
            VerifySqlServerUtilityRegistry(session.Context.DesignerAssembly, NativeV11Reflection.Get(connection, "DataBaseType") as string);
        var application = NativeV11Reflection.Get(settings, "ApplicationSettings")!;
        if (NativeV11Reflection.Get(application, "AuditingList") is IDictionary auditing &&
            auditing.Keys.Cast<object>().OfType<string>().Any(name => name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
            string.Equals(NativeV11Reflection.Get(application, "StatusCacheTableName") as string, request.Address.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Auditing/status-cache bindings require their native management workflow.");
        ValidateRelationships(tableSet, request.Address.Name);
        ValidateSystemIdentity(session.Context.DesignerAssembly, application, model);
        var sheet = session.Sheet(request.Address.Name);
        var sheets = (IList)NativeV11Reflection.Get(session.Workbook, "Sheets")!;
        var activeIndex = Convert.ToInt32(NativeV11Reflection.Get(session.Workbook, "ActiveSheetIndex"));
        var activeSheet = activeIndex >= 0 && activeIndex < sheets.Count ? sheets[activeIndex] : null;
        if (sheet is not null)
        {
            RequireProjection(sheet, model);
            if (ReferenceEquals(sheet, activeSheet))
                throw new InvalidOperationException("Switch to an existing page before removing its table binding.");
        }
        if (existing is not null)
            NativeV11TableWriter.VerifyOwners(tableSet, model, parent, sheet,
                sheet is null ? null : NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "TableInfo"));
        var cached = dataSet.Tables[request.Address.Name];
        if (cached is not null && (cached.ParentRelations.Count != 0 || cached.ChildRelations.Count != 0))
            throw new InvalidDataException("A cached relational table cannot be removed by the binding-only lifecycle.");
        var tableType = model.GetType();
        if (manager.GetType().GetMethod("AddWorkSheet", NativeV11Reflection.Flags, null, [tableType, typeof(bool)], null) is null ||
            manager.GetType().GetMethod("RemoveTable", NativeV11Reflection.Flags, null, [typeof(string)], null) is null)
            throw new MissingMethodException("The native binding worksheet lifecycle does not match the verified V11 build.");
        return new(route, tableSet, manager, parent, model, existing?.Model, normalized, sheet,
            sheet is null ? -1 : sheets.IndexOf(sheet), activeSheet, dataSet, cached,
            PermissionIdentities(model), binding);
    }

    internal static void ValidateExternal(object model, ModuleAddress address)
    {
        if (model.GetType().FullName != "Forguncy.Model.Tables.Table" ||
            NativeV11Reflection.Get(model, "Name") as string != address.Name ||
            NativeV11Reflection.Get(model, "ExternalDatabaseInfo") is not object binding ||
            NativeV11Reflection.Get(binding, "AllowModifySchema") is not false ||
            NativeV11Reflection.Get(model, "IsLinkedTableOrLinkedView") is not true ||
            NativeV11Reflection.Get(model, "IsView") is not bool view || view != (address.Kind == ModuleKind.View) ||
            NativeV11Reflection.Get(model, "IsProcessTable") is true || NativeV11Reflection.Get(model, "IsViewProcessInfo") is true ||
            NativeV11Reflection.Get(model, "BuiltInViewInfo") is not null || NativeV11Reflection.Get(model, "CopyExternalBuiltInTableInfo") is not null ||
            NativeV11Reflection.Get(model, "FlowProcess") is not null)
            throw new InvalidDataException("Only ordinary external table/view bindings with schema modification disabled can be created or removed.");
        if (NativeV11Reflection.Get(model, "Relations") is IEnumerable relations && relations.Cast<object>().Any())
            throw new InvalidDataException("A binding with native table relations requires a separately verified relation lifecycle.");
        if (string.IsNullOrWhiteSpace(NativeV11Reflection.Get(binding, "SourceTableName") as string))
            throw new InvalidDataException("An external binding requires its native source table name.");
        var columns = ((IEnumerable)NativeV11Reflection.Get(model, "Columns")!).Cast<object>().ToArray();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var types = new HashSet<Type> { typeof(string), typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
            typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(decimal), typeof(char),
            typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid), typeof(byte[]) };
        foreach (var column in columns)
        {
            var name = NativeV11Reflection.Get(column, "Name") as string;
            if (column.GetType().FullName is not ("Forguncy.Model.Tables.BindingColumn" or "Forguncy.Model.Tables.FormulaColumn") ||
                string.IsNullOrWhiteSpace(name) || !names.Add(name) ||
                NativeV11Reflection.Get(column, "ColumnType") is not Type type || !types.Contains(Nullable.GetUnderlyingType(type) ?? type) ||
                NativeV11Reflection.Get(column, "Formula") is string formula && formula.Contains("#REF!", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The binding contains an unsupported, duplicate or invalid native field definition.");
        }
    }

    internal static object ValidateRegisteredConnection(IEnumerable connections, object model)
    {
        var binding = NativeV11Reflection.Get(model, "ExternalDatabaseInfo")
            ?? throw new InvalidDataException("An external binding definition is required.");
        var id = NativeV11Reflection.Get(binding, "ConnectionId") as string;
        var matches = connections.Cast<object>().Where(connection => NativeV11Reflection.Get(connection, "ID") as string == id).ToArray();
        if (string.IsNullOrWhiteSpace(id) || matches.Length != 1)
            throw new InvalidDataException("The external binding requires one existing native data-connection definition.");
        return matches[0];
    }

    internal static void ValidateRelationships(object tableSet, string name)
    {
        if (NativeV11Reflection.Get(tableSet, "Relations") is not IEnumerable relations)
            throw new InvalidOperationException("The native table relationship registry is unavailable.");
        if (relations.Cast<object>().Any(relation =>
            string.Equals(NativeV11Reflection.Get(relation, "PrimaryTable") as string, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(NativeV11Reflection.Get(relation, "ForeignTable") as string, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A referenced table binding requires its native relationship management workflow.");
    }

    internal static void ValidateSystemIdentity(Assembly designer, object application, object model)
    {
        var profile = V11BuildProfile.Resolve(designer);
        if (profile.Version == "11.0.104.0")
        {
            var extensions = model.GetType().Assembly.GetType("ServerDesignerCommon.Services.TableExtensions", true)!;
            if (NativeV11Reflection.Static(extensions, "IsSystemTable", model) is true ||
                NativeV11Reflection.Static(extensions, "IsSystemView", model) is true)
                throw new InvalidDataException("A native system table/view requires its own management workflow.");
        }
        else if (NativeV11Reflection.Static(designer.GetType("Forguncy.Common.ModuleExtensions", true)!, "IsInternalTableModule", model) is not false)
            throw new InvalidDataException("A native internal table requires its own management workflow.");
        var statusName = NativeV11Reflection.Get(application, "StatusCacheTableName") as string;
        // The 104 default-status service does not exist in 11.0.3.0. That build
        // classifies ordinary external modules by ExternalDatabaseInfo; retain
        // its native classification and protect any explicitly configured name.
        if (statusName is null && profile.Version == "11.0.104.0")
        {
            var status = Assembly.Load("ForguncyDataAccess").GetType("ForguncyDataAccess.StatusCacheDataTableManager", true)!;
            statusName = status.GetField("DefaultTableName", NativeV11Reflection.Flags)?.GetValue(null) as string
                ?? throw new InvalidOperationException("The native default status table identity is unavailable.");
        }
        if (string.Equals(NativeV11Reflection.Get(model, "Name") as string, statusName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The native status-cache table cannot be edited as an external binding.");
    }

    internal static void VerifySqlServerUtilityRegistry(Assembly designer, string? databaseType)
    {
        var profile = V11BuildProfile.Resolve(designer);
        if (string.IsNullOrWhiteSpace(databaseType))
            throw new NotSupportedException("Binding-only view creation requires the verified native SqlServer utility provider.");
        var service = Assembly.Load("ForguncyDataBaseService");
        VerifyUtilityComponent(service, profile.Version);
        // Native provider IDs may be GUIDs and old names may be aliases. This
        // exact converter reads only the already loaded alias dictionary.
        var canonical = NativeV11Reflection.Static(service.GetType("ForguncyDataAccess.DataBaseConvertFactory", true)!,
            "ConvertToNewDatabaseType", databaseType) as string;
        if (string.IsNullOrWhiteSpace(canonical))
            throw new NotSupportedException("The native database-provider identity is unavailable.");
        var cache = service.GetType("ForguncyDataAccess.DataBaseAssemblysCache", true)!;
        var registry = cache.GetProperty("AllDataBaseTypeAssemblies", NativeV11Reflection.Flags)?.GetValue(null) as IDictionary
            ?? throw new InvalidOperationException("The native database-provider registry is unavailable.");
        if (!registry.Contains(canonical) || registry[canonical] is not Assembly sql)
            throw new NotSupportedException("The native SqlServer provider must already be registered; binding creation does not discover or install providers.");
        if (sql.GetName().Name != "SqlServerWrapper")
            throw new NotSupportedException("This view binding provider has not been verified for permission initialization without database activation.");
        VerifyUtilityComponent(sql, profile.Version);
        var utility = sql.GetType("SqlServerWrapper.SqlServerDButilities", true)!;
        var api = utility.BaseType?.Assembly ?? throw new InvalidOperationException("The native SqlServer utility base type is unavailable.");
        VerifyUtilityComponent(api, profile.Version);
        var baseType = api.GetType("GrapeCity.Forguncy.DataBaseApi.DBUtilities", true)!;
        var selector = cache.GetMethods(NativeV11Reflection.Flags).Single(method => method.Name == "GetSpecialType" &&
            method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1 && method.ReturnType == typeof(Type) &&
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual([typeof(string)]));
        // This selector reads the already registered assembly/type cache. It
        // does not call CheckDbPlugin or instantiate a database utility.
        if (!ReferenceEquals(selector.MakeGenericMethod(baseType).Invoke(null, [canonical]), utility))
            throw new NotSupportedException("The native SqlServer utility cache resolves a different implementation.");
    }

    private static void VerifyUtilityComponent(Assembly assembly, string version)
    {
        var expected = (version, assembly.GetName().Name) switch
        {
            ("11.0.104.0", "ForguncyDataBaseService") => "14AF70773DC4F08BED9F9789E9A3D571926816DF665C0859D3253A8CF4540E76",
            ("11.0.104.0", "SqlServerWrapper") => "47F7CC27937C141715923FF6E563462BFC012D48FE691766E744746B0DB1B987",
            ("11.0.104.0", "GrapeCity.Forguncy.DataBaseApi") => "94844EEA30233B3F038E30CBA0F405508007B965C669465B0077B64533D4E0DD",
            ("11.0.3.0", "ForguncyDataBaseService") => "754788FEAED6D379F77311B7DF5F76064351A9B806CE0C0C2E37A41BECF690FB",
            ("11.0.3.0", "SqlServerWrapper") => "3A0310CBD8C320140DDEDAA991646E8DC251889B111D791FA829C86CA9D59DBF",
            ("11.0.3.0", "GrapeCity.Forguncy.DataBaseApi") => "9D1B979FE3BE7F24746CE03A64D9502FB4467CE0854915B924A99D0F28A33532",
            _ => null
        };
        if (expected is null || assembly.GetName().Version?.ToString() != version || string.IsNullOrEmpty(assembly.Location))
            throw new NotSupportedException("The native database utility component does not match this verified V11 build.");
        using var stream = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(stream)) != expected)
            throw new NotSupportedException("The native database utility component fingerprint differs from the verified build.");
    }

    private static IList Parent(object root, string? folder)
    {
        var current = root;
        foreach (var segment in (folder ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") throw new InvalidDataException("Invalid native table folder.");
            var list = (IList)NativeV11Reflection.Get(current, "Items")!;
            var matches = list.Cast<object>().Where(item => NativeV11Reflection.Get(item, "Name") as string == segment &&
                NativeV11Reflection.Get(item, "Items") is IList).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Create the native table folder before inserting its binding.");
            current = matches[0];
        }
        return (IList)NativeV11Reflection.Get(current, "Items")!;
    }

    private static void RequireProjection(object sheet, object model)
    {
        var attach = NativeV11Reflection.Get(sheet, "Tag") ?? throw new InvalidOperationException("The native binding worksheet has no attach data.");
        var info = NativeV11Reflection.Get(attach, "TableInfo") ?? throw new InvalidOperationException("The native worksheet is not a table projection.");
        if (!ReferenceEquals(NativeV11Reflection.Get(info, "Table"), model))
            throw new InvalidOperationException("The native binding worksheet refers to a different table identity.");
    }

    private static void VerifyIdentity(Prepared prepared)
    {
        if (prepared.Permissions.Any(permission => !ReferenceEquals(NativeV11Reflection.Get(prepared.Model, permission.Name), permission.Value)) ||
            !ReferenceEquals(NativeV11Reflection.Get(prepared.Model, "ExternalDatabaseInfo"), prepared.Binding))
            throw new InvalidOperationException("The native permission or external binding identity changed during the lifecycle.");
    }

    private static IReadOnlyList<(string Name, object? Value)> PermissionIdentities(object model) =>
        new[] { "PermissionData", "CreateRecordPermissions", "RowPermissions", "ColumnPermissions" }
            .Select(name => (name, NativeV11Reflection.Get(model, name))).ToArray();
}
