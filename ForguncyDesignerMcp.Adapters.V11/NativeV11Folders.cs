using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Folder addresses contain the native root in FolderPath, for example
// Folder("Child", "Pages/Parent"). Update uses a same-parent rename: request
// and Document.Address identify the reviewed OLD folder; Metadata.Name is the
// new leaf; ResultAddress is the real NEW address returned in the receipt.
// SourcePaths(request) includes both real directory keys for a rename. A key
// ending '/' represents an actual native directory, NOT a synthetic JSON file.
internal sealed class NativeV11Folders
{
    private readonly NativeV11Session session;
    private readonly Dictionary<string, NativeV11FolderMoveJournal> approvedGroupedRenames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, GroupedDelete> approvedGroupedDeletes = new(StringComparer.Ordinal);
    internal NativeV11Folders(NativeV11Session session) => this.session = session;
    internal IReadOnlyList<ModuleKind> Kinds { get; } = [ModuleKind.Folder];
    internal bool CanWrite(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.Folder &&
        operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete;

    internal sealed record Entry(NativeV11Route Route, object Model, IList Parent, int Index, ModuleAddress Address, string Marker);
    internal sealed record Change(object Model, Action Rollback, string? OldMarker, string? NewMarker);
    internal sealed record GroupedRename(NativeV11FolderInventory Inventory, NativeV11FolderInventory.MutationPlan Owners,
        NativeV11FolderMoveJournal Journal, ModuleAddress Destination);
    internal sealed record GroupedDelete(NativeV11FolderInventory Inventory, NativeV11FolderInventory.MutationPlan Owners,
        NativeV11FolderDeleteJournal Journal, NativeV11StaticFolderLifecycle.DeletePlan? StaticPlan);

    internal bool IsGroupedNativeRename(ModuleMutationRequest request) => request.Address.Kind == ModuleKind.Folder &&
        request.Operation == ModuleOperation.Update && !NativeV11ImageFolderLifecycle.Supports(request.Address) &&
        (session.Context.CaptureSnapshot().InCollaborationMode || request.Address.FolderPath?.Split('/')[0] == "Tables") &&
        Find(request.Address) is { } entry && Items(entry.Model).Count != 0;

    internal bool IsGroupedNativeDelete(ModuleMutationRequest request) => request.Address.Kind == ModuleKind.Folder &&
        request.Operation == ModuleOperation.Delete && !NativeV11ImageFolderLifecycle.Supports(request.Address) && session.Context.CaptureSnapshot().InCollaborationMode &&
        Route(request.Address).Kind is (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.Report or ModuleKind.BusinessProcess) &&
        Find(request.Address) is { } entry && Items(entry.Model).Count != 0;

    internal GroupedDelete CaptureGroupedDelete(ModuleMutationRequest request)
    {
        if (!IsGroupedNativeDelete(request) || request.Scope != EffectScope.ProjectDefinition || request.Document is not null)
            throw new InvalidOperationException("A grouped native folder deletion requires its exact collaboration scope.");
        var current = Find(request.Address)!;
        CheckNativeDeleteProtection(current.Route, current.Model);
        if (!HasStaticLayout(current.Route) && SourceState(session.LocalPath, current.Marker).Exists)
            throw new NotSupportedException("A grouped loader-backed folder deletion requires an absent LocalData subtree.");
        var inventory = NativeV11FolderInventory.Capture(session, request.Address, checkReferences: true);
        if (inventory.SharedOwners.Count != 0)
            throw new NotSupportedException("A grouped deletion with files shared by other native owners requires a separately reviewed preservation plan.");
        foreach (var owner in inventory.Owners.Where(owner => owner.Address.Kind != ModuleKind.Folder))
            if (NativeV11CollaborationOwners.GetDirectFiles(session, owner, allowSharedTableDatabase: true)
                .Any(path => !path.StartsWith(inventory.Root + "/", StringComparison.Ordinal)))
                throw new NotSupportedException("A grouped deletion with a descendant file outside its folder requires a separately reviewed preservation plan.");
        var pageItems = inventory.Items.Count(item => item.Route.IsPage);
        if (pageItems != 0 && ((IEnumerable)NativeV11Reflection.Get(session.Workbook, "Sheets")!).Cast<object>().Count() <= pageItems)
            throw new InvalidOperationException("A grouped deletion cannot remove the last native worksheet.");
        var owners = inventory.Project(request, request.Address, CaptureNativeChanges(request));
        return new(inventory, owners, NativeV11FolderDeleteJournal.Capture(session, inventory),
            HasStaticLayout(current.Route) ? NativeV11StaticFolderLifecycle.PrepareDelete(session, request.Address) : null);
    }

    internal void ApproveGroupedDelete(ModuleMutationRequest request, GroupedDelete group)
    {
        if (!IsGroupedNativeDelete(request) || !group.Journal.VerifyOriginal(session))
            throw new InvalidOperationException("The grouped deletion witness no longer matches its saved native subtree.");
        approvedGroupedDeletes[request.OperationId] = group;
    }

    internal void RevokeGroupedDelete(string operationId) => approvedGroupedDeletes.Remove(operationId);

    internal GroupedRename CaptureGroupedRename(ModuleMutationRequest request)
    {
        if (request.Operation != ModuleOperation.Update || request.Address.Kind != ModuleKind.Folder ||
            request.Scope != EffectScope.ProjectDefinition || !session.Context.CaptureSnapshot().InCollaborationMode && request.Address.FolderPath?.Split('/')[0] != "Tables")
            throw new InvalidOperationException("Grouped folder rename requires a supported project definition update.");
        var current = Find(request.Address) ?? throw new KeyNotFoundException("The native folder is missing.");
        if (Items(current.Model).Count == 0)
            throw new NotSupportedException("This grouped rename plan requires a nonempty native folder.");
        if (!HasStaticLayout(current.Route) && SourceState(session.LocalPath, current.Marker).Exists)
            throw new NotSupportedException("A grouped folder rename requires the native LocalData subtree to be absent until its physical move lifecycle is verified.");
        var destination = ResultAddress(request);
        var moved = destination.FolderPath != request.Address.FolderPath;
        if (moved ? destination.Name != request.Address.Name : destination.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A grouped folder operation must rename or move it, but not both.");
        var siblings = Parent(current.Route, destination.FolderPath!);
        if (siblings.Cast<object>().Any(model => !ReferenceEquals(model, current.Model) && Name(model).Equals(destination.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The destination native folder name is already in use.");
        var changes = CaptureNativeChanges(request, destination);
        var inventory = NativeV11FolderInventory.Capture(session, request.Address, checkReferences: false, changes);
        if (current.Route.Kind == ModuleKind.Table) ValidateGroupedBuiltInTables(inventory);
        var owners = inventory.Project(request, destination, changes);
        var journal = NativeV11FolderMoveJournal.Capture(session, inventory, destination);
        if (session.Context.CaptureSnapshot().InCollaborationMode && !journal.AllDirectoriesRepresentedInGit)
            throw new InvalidOperationException("分组目录包含无法由 Git 保存的空子目录，未派发协同签出。");
        return new(inventory, owners, journal, destination);
    }

    internal void ApproveGroupedRename(ModuleMutationRequest request, GroupedRename group)
    {
        if (!IsGroupedNativeRename(request) || group.Destination != ResultAddress(request) ||
            !group.Journal.CoversTransition(group.Inventory.Root, Marker(group.Destination)) ||
            !group.Journal.VerifyOriginal(session.LocalPath, session.LastPath, session.SourcePath))
            throw new InvalidOperationException("The grouped folder mutation witness no longer matches its native target.");
        approvedGroupedRenames[request.OperationId] = group.Journal;
    }

    internal void RevokeGroupedRename(string operationId) => approvedGroupedRenames.Remove(operationId);

    internal void ValidateGroupedBuiltInTables(NativeV11FolderInventory inventory)
    {
        var application = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ApplicationSettings")!;
        foreach (var item in inventory.Items)
        {
            var model = item.Model;
            if (model.GetType().FullName != "Forguncy.Model.Tables.Table" || NativeV11Reflection.Get(model, "ExternalDatabaseInfo") is not null ||
                NativeV11Reflection.Get(model, "IsView") is not bool isView || isView != (item.Address.Kind == ModuleKind.View) ||
                isView && NativeV11Reflection.Get(model, "IsBuiltInViewCustom") is not true ||
                NativeV11Reflection.Get(model, "IsProcessTable") is true || NativeV11Reflection.Get(model, "IsViewProcessInfo") is true ||
                NativeV11Reflection.Get(model, "IsCopyExternalBuiltInTable") is true || NativeV11Reflection.Get(model, "FlowProcess") is not null)
                throw new NotSupportedException("Grouped table folders accept ordinary built-in tables and custom views only; external and process objects are excluded.");
            if (NativeV11Reflection.Get(application, "AuditingList") is IDictionary auditing &&
                auditing.Keys.Cast<object>().OfType<string>().Any(name => name.Equals(item.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                string.Equals(NativeV11Reflection.Get(application, "StatusCacheTableName") as string, item.Address.Name, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Audit and status-cache tables require their native management workflow.");
            if (session.Profile.Version == "11.0.104.0") NativeV11TableLifecycle.ValidateSystemIdentity(session.Context.DesignerAssembly, application, model);
        }
    }

    internal IReadOnlyList<ModuleDocument> List(ModuleKind kind)
    {
        if (kind != ModuleKind.Folder) throw new NotSupportedException("The native folder adapter accepts Folder documents only.");
        session.VerifyContext();
        var result = new List<ModuleDocument>();
        foreach (var route in session.Routes)
        {
            void Walk(object node, string parent)
            {
                EnsureLoaded(node);
                var children = Items(node);
                for (var index = 0; index < children.Count; index++)
                {
                    var child = children[index]!;
                    if (NativeV11Reflection.Get(child, "Items") is not IList) continue;
                    var name = Name(child); Leaf(name);
                    var address = new ModuleAddress(ModuleKind.Folder, name, parent);
                    var marker = PathForModel(route, child);
                    if (marker != Marker(address)) throw new InvalidDataException("The native folder path does not match its object tree.");
                    result.Add(Document(new(route, child, children, index, address, marker)));
                    Walk(child, parent + "/" + name);
                }
            }
            Walk(route.Root, route.Folder.TrimEnd('/'));
        }
        result.AddRange(NativeV11ImageFolderLifecycle.List(session));
        return result;
    }

    internal ModuleDocument Read(ModuleAddress address)
    {
        session.VerifyContext();
        return NativeV11ImageFolderLifecycle.Supports(address) ? NativeV11ImageFolderLifecycle.Read(session, address) :
            Document(Find(address) ?? throw new KeyNotFoundException("The native folder does not exist in the loaded tree."));
    }

    internal NativeV11Route OwnerRoute(ModuleAddress address) => Route(address);
    internal object NativeModel(ModuleAddress address) => NativeV11ImageFolderLifecycle.Supports(address) ? NativeV11ImageFolderLifecycle.Model(session, address) : (Find(address) ?? throw new KeyNotFoundException("The native folder does not exist.")).Model;
    internal object FolderModuleType(ModuleAddress address) => NativeV11Reflection.Get(Route(address).Colleague, "FolderModuleType")
        ?? throw new MissingMemberException("The native colleague does not declare its folder module type.");

    // Collect the real native executor effects without executing its action.
    // The caller must independently validate the subtree and every returned
    // owner before any checkout or mutation.
    internal IReadOnlyList<(object Simple, string State)> CaptureNativeChanges(ModuleMutationRequest request, ModuleAddress? destination = null)
    {
        if (request.Address.Kind != ModuleKind.Folder || request.Operation is not (ModuleOperation.Update or ModuleOperation.Delete))
            throw new InvalidDataException("A folder rename or deletion is required for native subtree collection.");
        session.VerifyContext();
        var entry = Find(request.Address) ?? throw new KeyNotFoundException("The native folder is missing.");
        var route = entry.Route;
        var root = route.Folder.TrimEnd('/');
        object ViewForParent(string parentPath)
        {
            if (parentPath != root && !parentPath.StartsWith(root + "/", StringComparison.Ordinal))
                throw new InvalidDataException("A native destination parent belongs to another module root.");
            object model = route.Root;
            object view = CreateFolderView(session.Context.DesignerAssembly, route.Kind, null, model);
            foreach (var segment in parentPath[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var matches = Items(model).Cast<object>().Where(child => NativeV11Reflection.Get(child, "Items") is IList && Name(child) == segment).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("The native folder view parent is missing or ambiguous.");
                model = matches[0]; EnsureLoaded(model);
                view = CreateFolderView(session.Context.DesignerAssembly, route.Kind, view, model);
            }
            return view;
        }
        var targetView = CreateFolderView(session.Context.DesignerAssembly, route.Kind, ViewForParent(request.Address.FolderPath!), entry.Model);
        object executor;
        var target = destination ?? ResultAddress(request);
        if (request.Operation == ModuleOperation.Update && target.FolderPath != request.Address.FolderPath)
        {
            if (target.Name != request.Address.Name || target.FolderPath is null ||
                target.FolderPath == entry.Marker.TrimEnd('/') || target.FolderPath.StartsWith(entry.Marker, StringComparison.Ordinal))
                throw new InvalidDataException("A native folder move must retain its name and cannot target its own subtree.");
            var itemType = session.Context.DesignerAssembly.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!;
            var selected = Array.CreateInstance(itemType, 1); selected.SetValue(targetView, 0);
            var move = session.Context.DesignerAssembly.GetType("Forguncy.Views.ObjectViews.MoveItemOrFolderExecutor", true)!;
            executor = Activator.CreateInstance(move, [null, selected, ViewForParent(target.FolderPath)])!;
        }
        else if (request.Operation == ModuleOperation.Update)
            executor = NativeV11Reflection.Call(targetView, "RenameCollaborationExecutor", target.Name)!;
        else
        {
            CheckNativeDeleteProtection(route, entry.Model);
            var itemType = session.Context.DesignerAssembly.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!;
            var selected = Array.CreateInstance(itemType, 1); selected.SetValue(targetView, 0);
            executor = NativeV11Reflection.Call(targetView, "DeleteItemsCollaborationExecutor", selected)!;
        }
        if (NativeV11Reflection.Call(executor, "GetRelatedModules") is not IEnumerable values)
            throw new InvalidDataException("The native folder executor returned no collaboration owners.");
        var result = new List<(object Simple, string State)>();
        foreach (var value in values)
        {
            if (value is null) throw new InvalidDataException("A native folder owner is null.");
            var type = value.GetType();
            var simple = type.GetField("Item1")?.GetValue(value) ?? throw new InvalidDataException("A native folder owner identity is missing.");
            var state = type.GetField("Item2")?.GetValue(value)?.ToString() ?? throw new InvalidDataException("A native folder owner state is missing.");
            result.Add((simple, state));
        }
        if (result.Count == 0) throw new InvalidDataException("The native folder executor returned an empty owner set.");
        session.VerifyContext();
        return result;
    }

    internal static object CreateFolderView(Assembly designer, ModuleKind kind, object? parent, object model)
    {
        var name = kind switch
        {
            ModuleKind.Page => "PageFolderViewModel", ModuleKind.MasterPage => "MasterPageFolderViewModel",
            ModuleKind.UserControlPage => "UserControlPageFolderViewModel", ModuleKind.Table => "TableFolderViewModel",
            ModuleKind.ServerCommand => "ServerCommandFolderViewModel", ModuleKind.SharedCommand => "ShareCommandContainerFolderViewModel",
            ModuleKind.ScheduledTask => "TaskSchedulerFolderViewModel", ModuleKind.Notification => "NotificationsFolderViewModel",
            ModuleKind.Report => "ReportFolderViewModel", ModuleKind.BusinessProcess => "BusinessProcessFolderViewModel",
            _ => throw new NotSupportedException("The native folder view is not mapped.")
        };
        var type = designer.GetType("Forguncy.Views.ObjectViews." + name, true)!;
        var constructor = type.GetConstructors(NativeV11Reflection.Flags).Single(candidate =>
        {
            var parameters = candidate.GetParameters();
            return parameters.Length >= 3 && parameters[0].ParameterType.FullName == "Forguncy.Views.ObjectViews.FolderViewModel" &&
                parameters[1].ParameterType.IsInstanceOfType(model) && parameters.Skip(2).All(parameter => parameter.ParameterType == typeof(bool));
        });
        var args = constructor.GetParameters().Select((_, index) => index == 0 ? parent : index == 1 ? model : (object)false).ToArray();
        return constructor.Invoke(args);
    }

    private void CheckNativeDeleteProtection(NativeV11Route route, object model)
    {
        // The view-model CanDelete may show a modal dialog; its underlying
        // checker returns the native protected-page veto without opening one.
        var designer = session.Context.DesignerAssembly;
        var nativeCheck = route.Kind switch
        {
            ModuleKind.Page or ModuleKind.UserControlPage => NativeV11Reflection.Static(
                designer.GetType("Forguncy.Model.Pages.PageOperationsParameterChecker", true)!, "CheckDeleteFolder", model),
            ModuleKind.Table => NativeV11Reflection.Static(designer.GetType(session.Profile.Version == "11.0.104.0" ? "mh" : "mb", true)!,
                "CheckDeleteFolder", model, true),
            _ => null
        };
        if (route.Kind is not (ModuleKind.Page or ModuleKind.UserControlPage or ModuleKind.Table)) return;
        if (nativeCheck is null) throw new InvalidOperationException("The native folder deletion checker returned no result.");
        if (NativeV11Reflection.Get(nativeCheck, "Result") is not true)
            throw new InvalidOperationException("The native page-folder deletion checker rejected this folder: " +
                (NativeV11Reflection.Get(nativeCheck, "ErrorMessage") as string ?? "protected or referenced folder"));
    }

    internal ModuleAddress ResultAddress(ModuleMutationRequest request)
    {
        if (NativeV11ImageFolderLifecycle.Supports(request.Address)) return NativeV11ImageFolderLifecycle.ResultAddress(request);
        var route = Route(request.Address);
        if (request.Operation != ModuleOperation.Update) return request.Address;
        var name = request.Document?.Metadata.GetProperty("Name").GetString() ?? throw new InvalidDataException("The folder rename requires a new Name.");
        Leaf(name);
        var parent = request.Address.FolderPath!;
        if (request.Document!.Metadata.TryGetProperty("ParentFolderPath", out var parentValue))
        {
            if (parentValue.ValueKind != JsonValueKind.String) throw new InvalidDataException("The destination parent must be a native relative folder path.");
            var relative = NativeV11Session.NormalizeFolder(parentValue.GetString());
            parent = route.Folder.TrimEnd('/') + (relative.Length == 0 ? "" : "/" + relative);
        }
        return request.Address with { Name = name, FolderPath = parent };
    }

    internal IReadOnlyList<string> SourcePaths(ModuleAddress address) { if (NativeV11ImageFolderLifecycle.Supports(address)) return [NativeV11ImageFolderLifecycle.Marker(address)]; _ = Route(address); return [Marker(address)]; }
    internal IReadOnlyList<string> SourcePaths(ModuleMutationRequest request) =>
        SourcePaths(request.Address).Concat(SourcePaths(ResultAddress(request))).Distinct(StringComparer.Ordinal).ToArray();
    internal IReadOnlyList<string> SourcePaths(ModuleKind kind) => List(kind).Select(document => SourcePaths(document.Address).Single()).ToArray();
    internal IReadOnlyDictionary<string, byte[]> CaptureSaveFiles(ModuleMutationRequest request)
    {
        if (NativeV11ImageFolderLifecycle.Supports(request.Address))
        {
            var address = ResultAddress(request);
            return NativeV11ImageFolderLifecycle.List(session).Any(item => item.Address == address)
                ? new Dictionary<string, byte[]> { [NativeV11ImageFolderLifecycle.Marker(address)] = [] } : new Dictionary<string, byte[]>();
        }
        var route = Route(request.Address);
        if (HasStaticLayout(route))
            foreach (var marker in SourcePaths(request))
            {
                var current = SourceState(session.LocalPath, marker);
                if (current.Exists != HasNativeMarker(route, marker) || !current.Empty)
                    throw new InvalidOperationException("The native static-layout folder cache differs from its empty-folder save registration.");
            }
        return Capture(SourcePaths(request));
    }
    internal IReadOnlyDictionary<string, byte[]> CaptureSaveFiles(ModuleAddress address) => NativeV11ImageFolderLifecycle.Supports(address)
        ? new Dictionary<string, byte[]> { [NativeV11ImageFolderLifecycle.Marker(address)] = [] } : Capture(SourcePaths(address));
    internal IReadOnlyDictionary<string, byte[]> CaptureSaveFiles(ModuleKind kind) => List(kind).SelectMany(document => CaptureSaveFiles(document.Address)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    private IReadOnlyDictionary<string, byte[]> Capture(IEnumerable<string> markers)
    {
        session.VerifyContext();
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var marker in markers)
        {
            var entry = Find(AddressFromMarker(marker));
            if (entry is null) continue;
            if (!HasNativeMarker(entry.Route, marker)) throw new InvalidOperationException("The folder is missing its native save-cache directory registration.");
            result.Add(marker, []);
        }
        return result;
    }

    internal void Validate(ModuleMutationRequest request)
    {
        if (NativeV11ImageFolderLifecycle.Supports(request.Address)) { NativeV11ImageFolderLifecycle.ValidateEmpty(session, request); return; }
        session.VerifyContext();
        if (!CanWrite(request.Address.Kind, request.Operation)) throw new NotSupportedException("This native folder operation is unavailable.");
        if (request.Scope != EffectScope.ProjectDefinition) throw new InvalidDataException("Folder editing changes project definitions only.");
        var route = Route(request.Address);
        var current = Find(request.Address);
        if (request.Operation == ModuleOperation.Create ? current is not null : current is null)
            throw new InvalidOperationException("The folder create/update/delete operation no longer matches the native tree.");
        var parent = Parent(route, request.Address.FolderPath!);
        if (request.Operation == ModuleOperation.Delete)
        {
            if (request.Document is not null) throw new InvalidDataException("A folder deletion cannot contain replacement metadata.");
            CheckNativeDeleteProtection(route, current!.Model);
        }
        else
        {
            var document = request.Document ?? throw new InvalidDataException("The folder definition is required.");
            if (document.Address != request.Address || document.Companions.Count != 0 || document.Metadata.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("The folder document address or companion list is invalid.");
            var allowed = new HashSet<string>(["OwnerKind", "Name", "ParentFolderPath", "Weight", "ChildCount", "Loaded", "NativeType", "NativeId"], StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.Metadata.EnumerateObject())
                if (!allowed.Contains(property.Name) || !seen.Add(property.Name)) throw new InvalidDataException("The folder definition contains unknown or ambiguous fields.");
            if (!document.Metadata.TryGetProperty("OwnerKind", out var owner) || owner.ValueKind != JsonValueKind.String || owner.GetString() != route.Kind.ToString())
                throw new InvalidDataException("The folder owner must match its exact native module root.");
            var destination = ResultAddress(request);
            var destinationParent = Parent(route, destination.FolderPath!);
            if (request.Operation == ModuleOperation.Create && (!document.Metadata.TryGetProperty("Name", out var incomingName) || incomingName.GetString() != request.Address.Name))
                throw new InvalidDataException("The new folder Name must match its address.");
            var model = current?.Model ?? NativeV11Reflection.Call(route.Colleague, "CreateFolder", request.Address.Name)!;
            var weight = Convert.ToInt32(NativeV11Reflection.Get(model, "Weight"));
            if (document.Metadata.TryGetProperty("Weight", out var suppliedWeight) && suppliedWeight.GetInt32() != weight)
                throw new NotSupportedException("Folder Weight is native user-local ordering metadata and cannot be changed as a persisted project definition.");
            if (document.Metadata.TryGetProperty("NativeType", out var type) && type.GetString() != model.GetType().FullName)
                throw new InvalidDataException("The folder native model type cannot be changed.");
            var nativeId = NativeV11Reflection.Get(model, "ID") ?? NativeV11Reflection.Get(model, "Id");
            if (document.Metadata.TryGetProperty("NativeId", out var id) && !Equals(id.ValueKind == JsonValueKind.Null ? null : id.GetString(), nativeId))
                throw new InvalidDataException("The native folder identity cannot be supplied or changed.");
            if (document.Metadata.TryGetProperty("ChildCount", out var count) && count.GetInt32() != (current is null ? 0 : Items(current.Model).Count) ||
                document.Metadata.TryGetProperty("Loaded", out var loaded) && loaded.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("Folder mutation supports fully loaded empty folders only.");
            if (destinationParent.Cast<object>().Any(item => !ReferenceEquals(item, current?.Model) && Name(item).Equals(destination.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("A native sibling with the destination name already exists.");
            if (request.Operation == ModuleOperation.Update && destination.FolderPath != request.Address.FolderPath && destination.Name != request.Address.Name)
                throw new NotSupportedException("Move and rename a native folder in separate operations.");
            if (request.Operation == ModuleOperation.Update && (destination.FolderPath == Marker(request.Address).TrimEnd('/') ||
                destination.FolderPath!.StartsWith(Marker(request.Address), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("A folder cannot be moved into its own subtree.");
            if (request.Operation == ModuleOperation.Update && request.Address.Name != destination.Name &&
                request.Address.Name.Equals(destination.Name, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Case-only native folder renames have not been source-verified.");
            if (request.Operation == ModuleOperation.Create || destination != request.Address)
            {
                var target = Marker(destination);
                if (HasNativeMarker(route, target) || SourceState(session.SourcePath, target).Exists ||
                    SourceState(session.LocalPath, target).Exists || SourceState(session.LastPath, target).Exists)
                    throw new InvalidOperationException("The destination folder is already registered or present in a native/source save location.");
            }
        }
        if (current is not null)
        {
            EnsureLoaded(current.Model);
            if (Items(current.Model).Count != 0 && IsGroupedNativeRename(request))
            {
                if (!approvedGroupedRenames.TryGetValue(request.OperationId, out var approved) ||
                    !approved.CoversTransition(current.Marker, Marker(ResultAddress(request))))
                    _ = CaptureGroupedRename(request);
                return;
            }
            if (Items(current.Model).Count != 0 && IsGroupedNativeDelete(request))
            {
                if (!approvedGroupedDeletes.ContainsKey(request.OperationId)) _ = CaptureGroupedDelete(request);
                return;
            }
            if (Items(current.Model).Count != 0) throw new InvalidOperationException("Only empty native folders can be renamed or deleted.");
            if (!HasNativeMarker(route, current.Marker)) throw new InvalidOperationException("The current native folder save registration is missing.");
            foreach (var root in new[] { session.SourcePath, session.LocalPath, session.LastPath })
                if (!SourceState(root, current.Marker).Empty) throw new InvalidOperationException("The native folder contains a source/cache child not covered by the empty-folder operation.");
            var references = FindReferences(request.Address);
            if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException(references.Reason ?? "Native folder references remain.");
        }
    }

    internal Action Apply(ModuleMutationRequest request)
    {
        if (NativeV11ImageFolderLifecycle.Supports(request.Address)) return NativeV11ImageFolderLifecycle.ApplyEmpty(session, request);
        Validate(request);
        var route = Route(request.Address);
        var existing = Find(request.Address);
        if (existing is not null && Items(existing.Model).Count != 0 && request.Operation == ModuleOperation.Delete)
        {
            if (!approvedGroupedDeletes.Remove(request.OperationId, out var deletion))
                throw new InvalidOperationException("The grouped deletion has no verified native checkout witness.");
            return ApplyGroupedDelete(request, deletion);
        }
        NativeV11FolderMoveJournal? grouped = null;
        if (existing is not null && Items(existing.Model).Count != 0)
        {
            if (!approvedGroupedRenames.Remove(request.OperationId, out grouped))
                throw new InvalidOperationException("The grouped folder mutation has no verified native checkout witness.");
        }
        var wasDirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        void Invalidate() => NativeV11Generation.Mark(session, FolderModuleType(request.Address).ToString()!);
        var oldParentModel = ParentModel(route, request.Address.FolderPath!);
        var destination = ResultAddress(request);
        var newParentModel = ParentModel(route, destination.FolderPath!);
        var change = ApplyCore(route, request.Operation, Items(oldParentModel), existing?.Model,
            request.Operation == ModuleOperation.Delete ? request.Address.Name : destination.Name, Invalidate, grouped,
            ReferenceEquals(oldParentModel, newParentModel) ? null : newParentModel, oldParentModel);
        try
        {
            if (request.Operation == ModuleOperation.Delete ? Find(request.Address) is not null : Find(ResultAddress(request)) is null)
                throw new InvalidOperationException("The native folder tree did not retain the requested mutation.");
            _ = CaptureSaveFiles(request);
            grouped?.VerifyMemory(session);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception primary)
        {
            try { change.Rollback(); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty); }
            catch (Exception rollback) { throw new AggregateException("Native folder verification and rollback both failed.", primary, rollback); }
            throw;
        }
        var restored = false;
        return () =>
        {
            if (restored) return;
            session.VerifyContext(); change.Rollback();
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty);
            restored = true;
        };
    }

    private Action ApplyGroupedDelete(ModuleMutationRequest request, GroupedDelete deletion)
    {
        var undo = new List<Action>();
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var restored = false;
        void Restore()
        {
            if (restored) return;
            var errors = new List<Exception>();
            for (var index = undo.Count - 1; index >= 0; index--)
                try { undo[index](); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("The grouped native folder deletion could not be fully rolled back.", errors);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
            restored = true;
        }
        try
        {
            if (deletion.StaticPlan is not null) undo.Add(NativeV11StaticFolderLifecycle.Delete(deletion.StaticPlan));
            foreach (var item in deletion.Inventory.Items.OrderByDescending(item => item.RelativePath.Count(character => character == '/')))
            {
                var child = request with { Address = item.Address, Document = null };
                if (deletion.StaticPlan is not null)
                    undo.Add(NativeV11DefinitionWriter.ApplyCore(item.Route, ModuleOperation.Delete, item.Parent, item.Model, null,
                        () => NativeV11Generation.MarkModules(session, item.Route)));
                else undo.Add(item.Route.IsPage ? NativeV11PageWriter.Apply(session, child) : NativeV11DefinitionWriter.Apply(session, child));
            }
            foreach (var address in deletion.Inventory.Folders.OrderByDescending(address => Marker(address).Count(character => character == '/')))
            {
                var entry = Find(address) ?? throw new InvalidOperationException("A reviewed descendant folder disappeared during grouped deletion.");
                if (Items(entry.Model).Count != 0) throw new InvalidOperationException("A reviewed descendant folder still contains native items.");
                var change = ApplyCore(entry.Route, ModuleOperation.Delete, entry.Parent, entry.Model, address.Name,
                    () => NativeV11Generation.Mark(session, FolderModuleType(address).ToString()!));
                undo.Add(change.Rollback);
            }
            deletion.Journal.VerifyMemory(session);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            return Restore;
        }
        catch (Exception primary)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new AggregateException("The grouped folder deletion failed and its rollback is incomplete.", primary, rollback); }
            throw;
        }
    }

    internal void RemoveReviewedEmptyTableFolder(ModuleAddress address)
    {
        var entry = Find(address) ?? throw new InvalidOperationException("A reviewed table folder disappeared.");
        if (entry.Route.Kind != ModuleKind.Table || Items(entry.Model).Count != 0)
            throw new InvalidOperationException("The reviewed table folder is not empty.");
        _ = ApplyCore(entry.Route, ModuleOperation.Delete, entry.Parent, entry.Model, address.Name,
            () => NativeV11Generation.Mark(session, FolderModuleType(address).ToString()!));
    }

    internal ReferenceResult FindReferences(ModuleAddress address, object? originalModel = null)
    {
        if (NativeV11ImageFolderLifecycle.Supports(address))
        {
            if (originalModel is not null) return NativeV11References.Find(session, address, originalModel);
            try
            {
                var images = NativeV11ImageFolderLifecycle.ExistingAddresses(session, address).Where(value => value.Kind == ModuleKind.Image).ToArray();
                var references = new List<ModuleReference>(); var resources = new NativeV11Resources(session);
                foreach (var image in images)
                {
                    var result = resources.FindReferences(image);
                    if (!result.Complete) return new(references, false, result.Reason);
                    references.AddRange(result.References);
                }
                return new(references.Distinct().ToArray(), true, "Native references checked for every image in the folder.");
            }
            catch { return new([], false, "The native image folder reference scan is incomplete."); }
        }
        session.VerifyContext();
        try
        {
            var model = originalModel ?? Find(address)?.Model ?? throw new KeyNotFoundException();
            if (originalModel is not null && Items(model).Count != 0 && Find(address) is null)
                return new([], false, "A deleted nonempty folder requires its frozen descendant models for reference rechecking.");
            var own = NativeV11References.Find(session, address, model);
            if (!own.Complete || Items(model).Count == 0) return own;
            var prefix = Marker(address);
            var route = Route(address);
            var kinds = route.Kind == ModuleKind.Table ? new[] { ModuleKind.Table, ModuleKind.View } : new[] { route.Kind };
            var items = kinds.SelectMany(session.Items).Where(item => item.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            var references = own.References.ToList();
            foreach (var item in items)
            {
                var child = NativeV11References.Find(session, item.Address, item.Model);
                if (!child.Complete) return new(references, false, "A descendant native reference scan is incomplete: " + item.RelativePath + "; " + child.Reason);
                references.AddRange(child.References);
            }
            return new(references.Distinct().ToArray(), true);
        }
        catch { return new([], false, "Native folder reference discovery is incomplete."); }
    }

    internal static Change ApplyCore(NativeV11Route route, ModuleOperation operation, IList parent, object? existing, string destinationName, Action invalidate,
        NativeV11FolderMoveJournal? grouped = null, object? destinationParentModel = null, object? originalParentModel = null)
    {
        Leaf(destinationName);
        // Table and several other native colleagues intentionally make their
        // per-folder InsertFolder a model-only operation. Their regular save
        // path refreshes the complete route through SaveToCache(true).
        if (route.WholeCache) EnsureRouteLoaded(route.Root);
        if (operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)) throw new InvalidDataException("Invalid folder operation.");
        if (operation == ModuleOperation.Create ? existing is not null : existing is null || !parent.Contains(existing))
            throw new InvalidOperationException("The native folder no longer matches the mutation.");
        if (existing is not null && Items(existing).Count != 0 && (grouped is null || operation != ModuleOperation.Update))
            throw new InvalidOperationException("Only empty native folders can be mutated without a reviewed loader-backed grouped folder rename.");
        var model = existing ?? NativeV11Reflection.Call(route.Colleague, "CreateFolder", destinationName)
            ?? throw new InvalidOperationException("The native folder factory returned no model.");
        var oldName = Name(model); var index = existing is null ? -1 : parent.IndexOf(existing);
        var oldKey = existing is null ? null : PathForModel(route, model);
        var moving = destinationParentModel is not null;
        if (moving && (grouped is null || originalParentModel is null || operation != ModuleOperation.Update || destinationName != oldName))
            throw new InvalidOperationException("A native cross-parent move requires its reviewed grouped witness and unchanged name.");
        var projectedKey = moving ? (ReferenceEquals(destinationParentModel, route.Root) ? route.Folder : PathForModel(route, destinationParentModel!)) + destinationName + "/" :
            oldKey is null ? null : oldKey[..(oldKey.TrimEnd('/').LastIndexOf('/') + 1)] + destinationName + "/";
        if (grouped is not null && (oldKey is null || projectedKey is null || !grouped.CoversTransition(oldKey, projectedKey)))
            throw new InvalidOperationException("The grouped folder source witness does not match the requested rename.");
        string? newKey = null;
        Action? undoLocal = null;
        NativeV11StaticFolderLifecycle.Snapshot? staticSnapshot = null;
        if (grouped is not null && HasStaticLayout(route))
        {
            var save = NativeV11Reflection.Get(route.Loader, "SaveLoadFile") ?? throw new MissingMemberException("Folder.SaveLoadFile");
            var local = NativeV11Reflection.Get(save, "LocalPath") as string ?? throw new MissingMemberException("Folder.LocalPath");
            staticSnapshot = NativeV11StaticFolderLifecycle.Capture(local, oldKey!);
        }
        var mutated = false; var restored = false;
        try
        {
            if (operation == ModuleOperation.Create)
            {
                mutated = true; parent.Add(model);
                NativeV11Reflection.Call(route.Colleague, "InsertFolder", model);
                RefreshCache();
                newKey = PathForModel(route, model);
                if (!HasNativeMarker(route, newKey)) throw new InvalidOperationException("The native folder creation did not register its directory save data.");
            }
            else if (operation == ModuleOperation.Update)
            {
                mutated = true;
                if (moving) { parent.RemoveAt(index); Items(destinationParentModel!).Add(model); }
                else NativeV11Reflection.Set(model, "Name", destinationName);
                newKey = PathForModel(route, model);
                if (moving) MoveNativeFolder(route, model, originalParentModel!);
                else NativeV11Reflection.Call(route.Colleague, "RenameFolder", model, oldName);
                RefreshCache();
                if (!HasNativeMarker(route, newKey) || newKey != oldKey && HasNativeMarker(route, oldKey!))
                    throw new InvalidOperationException("The native folder rename did not update its directory save registration.");
            }
            else
            {
                mutated = true;
                NativeV11Reflection.Call(route.Colleague, "DeleteFolder", model);
                parent.RemoveAt(index);
                RefreshCache();
                if (HasNativeMarker(route, oldKey!)) throw new InvalidOperationException("The native folder deletion left its directory save registration behind.");
            }
            if (staticSnapshot is not null)
            {
                var save = NativeV11Reflection.Get(route.Loader, "SaveLoadFile")!;
                var local = (string)NativeV11Reflection.Get(save, "LocalPath")!;
                undoLocal = NativeV11StaticFolderLifecycle.Move(route.Colleague.GetType().Assembly, local, staticSnapshot, newKey!);
            }
            else undoLocal = ApplyLocalDirectory(route, operation, oldKey, newKey);
            invalidate();
            return new(model, Restore, oldKey, newKey);
        }
        catch (Exception primary)
        {
            try { Restore(); } catch (Exception rollback) { throw new AggregateException("Native folder mutation and rollback failed; do not save the document.", primary, rollback); }
            throw;
        }

        void Restore()
        {
            if (restored) return;
            if (mutated)
            {
                undoLocal?.Invoke();
                if (operation == ModuleOperation.Create)
                {
                    if (parent.Contains(model)) { NativeV11Reflection.Call(route.Colleague, "DeleteFolder", model); parent.Remove(model); }
                }
                else if (operation == ModuleOperation.Update)
                {
                    var changedName = Name(model);
                    if (moving)
                    {
                        if (Items(destinationParentModel!).Contains(model)) Items(destinationParentModel!).Remove(model);
                        if (!parent.Contains(model)) parent.Insert(Math.Min(index, parent.Count), model);
                        MoveNativeFolder(route, model, destinationParentModel!);
                    }
                    else
                    {
                        NativeV11Reflection.Set(model, "Name", oldName);
                        NativeV11Reflection.Call(route.Colleague, "RenameFolder", model, changedName);
                    }
                    if (!HasNativeMarker(route, oldKey!)) NativeV11Reflection.Call(route.Colleague, "InsertFolder", model);
                    if (newKey is not null && newKey != oldKey && HasNativeMarker(route, newKey))
                        _ = NativeV11Reflection.Call(route.Loader, "DeleteFolder", newKey.Replace('/', '\\'));
                }
                else
                {
                    if (!parent.Contains(model)) parent.Insert(Math.Min(index, parent.Count), model);
                    NativeV11Reflection.Call(route.Colleague, "InsertFolder", model);
                }
                RefreshCache(); invalidate();
            }
            restored = true;
        }

        void RefreshCache()
        {
            if (route.WholeCache) NativeV11Reflection.Call(route.Colleague, "SaveToCache", true);
        }
    }

    private static void MoveNativeFolder(NativeV11Route route, object model, object oldParent)
    {
        var designer = route.Colleague.GetType().Assembly;
        _ = V11BuildProfile.Resolve(designer);
        var node = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.TreeNodeModelBase", true)!;
        var method = designer.GetType("Forguncy.SaveLoad.ModelColleagueBase", true)!.GetMethod("Move", NativeV11Reflection.Flags,
            null, [node, node], null) ?? throw new MissingMethodException("The exact native folder move method is unavailable.");
        try { method.Invoke(route.Colleague, [model, oldParent]); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private static bool HasStaticLayout(NativeV11Route route) => route.Kind is ModuleKind.Report or ModuleKind.BusinessProcess;

    // Report/process JSON loaders preserve their companion directories
    // (CleanFileAfterSave=false). Their native UI also creates/renames/deletes
    // Local directories, then the registered static saver mirrors Local into
    // Last. Reproduce only that empty-directory branch through FileUtilities;
    // never invoke a whole report/process manager or touch the project source.
    private static Action ApplyLocalDirectory(NativeV11Route route, ModuleOperation operation, string? oldMarker, string? newMarker)
    {
        if (!HasStaticLayout(route)) return () => { };
        var save = NativeV11Reflection.Get(route.Loader, "SaveLoadFile") ?? throw new MissingMemberException("Folder.SaveLoadFile");
        var local = NativeV11Reflection.Get(save, "LocalPath") as string ?? throw new MissingMemberException("Folder.LocalPath");
        var markers = new[] { oldMarker, newMarker }.OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var before = markers.ToDictionary(marker => marker, marker => SourceState(local, marker), StringComparer.Ordinal);
        if (before.Values.Any(state => !state.Empty)) throw new InvalidOperationException("Only empty native layout directories can be mutated.");
        if (operation != ModuleOperation.Delete && newMarker != oldMarker && before[newMarker!].Exists)
            throw new InvalidOperationException("The destination native layout directory already exists.");
        var expected = before.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (oldMarker is not null && oldMarker != newMarker) expected[oldMarker] = new(false, true);
        if (newMarker is not null) expected[newMarker] = new(true, true);
        var designer = route.Colleague.GetType().Assembly;
        _ = V11BuildProfile.Resolve(designer);
        var common = AssemblyLoadContext.GetLoadContext(designer)!.Assemblies.Single(assembly => assembly.GetName().Name == "CommonUtilities");
        var utilities = common.GetType("Forguncy.Common.FileUtilities", true)!;
        string Absolute(string marker) => V11Safety.Under(local, marker.TrimEnd('/'));
        void RequireEmpty(string marker)
        {
            if (!SourceState(local, marker).Empty) throw new InvalidOperationException("A native layout directory gained an unreviewed child.");
        }
        void Remove(string marker)
        {
            RequireEmpty(marker);
            if (SourceState(local, marker).Exists) NativeV11Reflection.Static(utilities, "DeleteFolder", Absolute(marker));
        }
        var restored = false;
        void Restore()
        {
            if (restored) return;
            foreach (var marker in markers)
            {
                var current = SourceState(local, marker);
                if (!current.Empty || current != before[marker] && current != expected[marker])
                    throw new InvalidOperationException("The native layout folder changed during rollback.");
            }
            if (operation == ModuleOperation.Update && oldMarker != newMarker && before[oldMarker!].Exists &&
                !SourceState(local, oldMarker!).Exists && SourceState(local, newMarker!).Exists)
                NativeV11Reflection.Static(utilities, "RenameFolder", Absolute(newMarker!), Path.GetFileName(Absolute(oldMarker!)));
            foreach (var marker in markers.Reverse())
                if (before[marker].Exists)
                {
                    if (!SourceState(local, marker).Exists) NativeV11Reflection.Static(utilities, "EnsureExistFolder", Absolute(marker));
                }
                else Remove(marker);
            if (markers.Any(marker => SourceState(local, marker) != before[marker]))
                throw new InvalidOperationException("The original native layout directory state was not restored.");
            restored = true;
        }
        try
        {
            if (operation == ModuleOperation.Delete) Remove(oldMarker!);
            else if (operation == ModuleOperation.Update && oldMarker != newMarker && before[oldMarker!].Exists)
                NativeV11Reflection.Static(utilities, "RenameFolder", Absolute(oldMarker!), Path.GetFileName(Absolute(newMarker!)));
            else NativeV11Reflection.Static(utilities, "EnsureExistFolder", Absolute(newMarker!));
            if (markers.Any(marker => SourceState(local, marker) != expected[marker]))
                throw new InvalidOperationException("The native layout folder mutation was not retained.");
            return Restore;
        }
        catch (Exception primary)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new AggregateException("Native layout directory mutation and rollback failed.", primary, rollback); }
            throw;
        }
    }

    internal static string PathForModel(NativeV11Route route, object model)
    {
        var key = (NativeV11Reflection.Call(route.Colleague, "GetKey", model) as string)?.Replace('\\', '/')
            ?? throw new InvalidDataException("The native folder has no save key.");
        ValidateMarker(key);
        if (!key.StartsWith(route.Folder, StringComparison.Ordinal)) throw new InvalidDataException("The native folder save key belongs to a different module root.");
        return key;
    }

    internal static bool HasNativeMarker(NativeV11Route route, string marker)
    {
        if (route.Loader is not IDictionary entries) throw new NotSupportedException("The native folder loader does not expose its directory registrations.");
        foreach (DictionaryEntry entry in entries)
            if (entry.Key is string key && key.Replace('\\', '/') == marker)
                return entry.Value?.GetType().FullName == "Forguncy.SaveLoad.FolderSaveData";
        return false;
    }

    internal readonly record struct DirectoryState(bool Exists, bool Empty);
    internal static DirectoryState SourceState(string project, string marker)
    {
        ValidateMarker(marker);
        if (Directory.Exists(project))
        {
            var path = V11Safety.Under(project, marker.TrimEnd('/'));
            if (File.Exists(path)) throw new InvalidDataException("A source file collides with the native folder path.");
            if (!Directory.Exists(path)) return new(false, true);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Native folder source paths cannot be reparse points.");
            return new(true, !Directory.EnumerateFileSystemEntries(path).Any());
        }
        if (!File.Exists(project) || !Path.GetExtension(project).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The native folder source is not an existing project directory or FGCC.");
        using var zip = ZipFile.OpenRead(project);
        var names = zip.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToArray();
        if (names.Any(name => name.Equals(marker.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("An archive file collides with the native folder path.");
        var related = names.Where(name => name.StartsWith(marker, StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var name in related)
            if (name.Split('/').Any(segment => segment is "." or "..")) throw new InvalidDataException("The native folder archive subtree contains unsafe paths.");
        if (related.Distinct(StringComparer.OrdinalIgnoreCase).Count() != related.Length) throw new InvalidDataException("The native folder archive subtree contains duplicate entries.");
        return new(related.Length > 0, related.All(name => name.Equals(marker, StringComparison.OrdinalIgnoreCase)));
    }

    internal static byte[]? ReadSourceDirectory(string project, string marker) => SourceState(project, marker).Exists ? [] : null;

    private NativeV11Route Route(ModuleAddress address)
    {
        NativeV11Session.ValidateAddress(address);
        if (address.Kind != ModuleKind.Folder || string.IsNullOrEmpty(address.FolderPath)) throw new InvalidDataException("A folder address requires its exact native module root in FolderPath.");
        Leaf(address.Name);
        var root = address.FolderPath.Split('/')[0];
        return session.Routes.SingleOrDefault(route => route.Folder.TrimEnd('/') == root)
            ?? throw new NotSupportedException("This native module root is not mapped for folder editing.");
    }

    private Entry? Find(ModuleAddress address)
    {
        var route = Route(address);
        var parent = Parent(route, address.FolderPath!);
        var matches = parent.Cast<object>().Select((model, index) => (Model: model, Index: index)).Where(candidate =>
            NativeV11Reflection.Get(candidate.Model, "Items") is IList && Name(candidate.Model) == address.Name).ToArray();
        if (matches.Length == 0) return null;
        if (matches.Length != 1) throw new InvalidDataException("The native folder address is ambiguous.");
        var match = matches[0]; EnsureLoaded(match.Model);
        var marker = PathForModel(route, match.Model);
        if (marker != Marker(address)) throw new InvalidDataException("The native folder path and address do not match.");
        return new(route, match.Model, parent, match.Index, address, marker);
    }

    private static IList Parent(NativeV11Route route, string folder)
        => Items(ParentModel(route, folder));

    private static object ParentModel(NativeV11Route route, string folder)
    {
        var parts = folder.Split('/');
        if (parts[0] != route.Folder.TrimEnd('/')) throw new InvalidDataException("The folder owner root changed.");
        var current = route.Root; EnsureLoaded(current);
        foreach (var part in parts.Skip(1))
        {
            Leaf(part);
            var matches = Items(current).Cast<object>().Where(child => NativeV11Reflection.Get(child, "Items") is IList && Name(child) == part).ToArray();
            if (matches.Length != 1) throw new KeyNotFoundException("The native parent folder does not exist.");
            current = matches[0]; EnsureLoaded(current);
        }
        return current;
    }

    private static ModuleDocument Document(Entry entry)
    {
        EnsureLoaded(entry.Model);
        var metadata = JsonSerializer.SerializeToElement(new
        {
            OwnerKind = entry.Route.Kind.ToString(), Name = Name(entry.Model), Weight = Convert.ToInt32(NativeV11Reflection.Get(entry.Model, "Weight")),
            ParentFolderPath = entry.Address.FolderPath![entry.Route.Folder.TrimEnd('/').Length..].TrimStart('/'),
            ChildCount = Items(entry.Model).Count, Loaded = NativeV11Reflection.Get(entry.Model, "Loaded") is true,
            NativeType = entry.Model.GetType().FullName, NativeId = NativeV11Reflection.Get(entry.Model, "ID") ?? NativeV11Reflection.Get(entry.Model, "Id")
        });
        var revision = NativeV11Session.Hash(Encoding.UTF8.GetBytes(metadata.GetRawText() + "\n" + entry.Marker + "\n" + HasNativeMarker(entry.Route, entry.Marker)));
        return new(entry.Address, metadata, [], revision);
    }

    private static IList Items(object node) => NativeV11Reflection.Get(node, "Items") as IList ?? throw new InvalidDataException("The native folder has no child collection.");
    private static string Name(object node) => NativeV11Reflection.Get(node, "Name") as string ?? throw new InvalidDataException("A native tree node has no name.");
    private static void EnsureLoaded(object node) { if (NativeV11Reflection.Get(node, "Loaded") is not true) throw new InvalidOperationException("The native folder tree must be fully loaded."); }
    private static void EnsureRouteLoaded(object folder)
    {
        EnsureLoaded(folder);
        foreach (var child in Items(folder).Cast<object>())
            if (NativeV11Reflection.Get(child, "Items") is IList) EnsureRouteLoaded(child);
    }
    private static string Marker(ModuleAddress address) { var value = address.FolderPath + "/" + address.Name + "/"; ValidateMarker(value); return value; }
    private static ModuleAddress AddressFromMarker(string marker)
    {
        ValidateMarker(marker); var path = marker.TrimEnd('/'); var split = path.LastIndexOf('/');
        return new(ModuleKind.Folder, path[(split + 1)..], path[..split]);
    }
    private static void ValidateMarker(string marker)
    {
        if (!marker.EndsWith('/') || marker.Contains('\\')) throw new InvalidDataException("A native directory key must be a canonical relative path ending '/'.");
        var parts = marker.TrimEnd('/').Split('/');
        if (parts.Length < 2 || !Roots.Contains(parts[0], StringComparer.Ordinal)) throw new InvalidDataException("The native directory key does not identify a mapped non-root folder.");
        foreach (var part in parts) Leaf(part);
    }
    private static void Leaf(string name)
    {
        V11Safety.Address(new(ModuleKind.Folder, name));
        var stem = name.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9')
            throw new InvalidDataException("The native folder name is a reserved Windows device name.");
    }
    private static readonly string[] Roots = ["Pages", "MasterPages", "UserControlPages", "Tables", "ServerCommands", "ShareCommandContainers", "SchedulerTasks", "Notifications", "Reports", "Process", "image"];
}
