using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal sealed record NativeV11CollaborationOwner(ModuleAddress Address, object SimpleModule, string State);
internal sealed record NativeV11CollaborationFileOwner(object NativeModule, IReadOnlyList<string> Files);
internal sealed record NativeV11FirstProcessOwner(string Name, string Type, string State, object SimpleModule);

// Read-only owner resolution. This class never calls CanExecute, ToModule,
// Checkout, SaveModules, InitModules or a native lock/commit operation.
// ToModule synthesizes a new detached Module on a miss; its nonnull result is
// not evidence of registration. Existing owners use the populated Modules map.
internal static class NativeV11CollaborationOwners
{
    private sealed record Contract(Type Simple, Type Module, ConstructorInfo FromModule,
        MethodInfo CreateModel, MethodInfo DirectFiles, FieldInfo Modules, FieldInfo Tracked);
    private static readonly ConditionalWeakTable<Assembly, Contract> Contracts = new();

    internal static IReadOnlyList<NativeV11CollaborationOwner> Resolve(NativeV11Session session,
        IReadOnlyList<ModuleAddress> addresses)
    {
        session.VerifyContext();
        var contract = GetContract(session.Context.DesignerAssembly);
        var manager = NativeV11Collaboration.RequireBoundManager(session);
        var modules = ModuleMap(contract, manager);
        var result = new List<NativeV11CollaborationOwner>();
        foreach (var address in addresses)
        {
            NativeV11Session.ValidateAddress(address);
            var identity = Identity(session, contract, address);
            var module = ExistingModule(contract, modules, identity.Name, identity.Type);
            result.Add(FromExistingCore(session.Context.DesignerAssembly, address, module, identity.Classification));
        }
        session.VerifyContext();
        if (!ReferenceEquals(manager, NativeV11Collaboration.RequireBoundManager(session)))
            throw Incomplete("The native collaboration manager changed while resolving owners.");
        return result;
    }

    // Narrow mutation lifecycle: real page/report classifiers, the native New
    // collector, and the actual native Delete collector. No lock or mutation
    // is performed while collecting this plan.
    internal static IReadOnlyList<NativeV11CollaborationOwner> ResolveMutation(NativeV11Session session, ModuleMutationRequest request)
    {
        if (NativeV11ImageFolderLifecycle.Supports(request.Address) && request.Operation is ModuleOperation.Create or ModuleOperation.Delete && request.Scope == EffectScope.ProjectDefinition)
        {
            var imageRelated = NativeV11ImageFolderLifecycle.CollectEmpty(session, request);
            var expectedName = NativeV11ImageFolderLifecycle.Marker(request.Address).TrimEnd('/');
            var expectedState = request.Operation == ModuleOperation.Create ? "New" : "Remove";
            if (imageRelated.Count != 1 || imageRelated[0].State != expectedState || Name(imageRelated[0].Simple) != expectedName || TypeName(imageRelated[0].Simple) != "ImageFolder")
                throw Incomplete("The native image folder collector disagrees with the requested owner or includes other references.");
            if (request.Operation == ModuleOperation.Delete && !SameIdentity(Resolve(session, [request.Address]).Single().SimpleModule, imageRelated[0].Simple))
                throw Incomplete("The native image folder deletion identity changed.");
            return [new(request.Address, imageRelated[0].Simple, expectedState)];
        }
        if (request.Address.Kind == ModuleKind.Folder && request.Operation is ModuleOperation.Create or ModuleOperation.Delete && request.Scope == EffectScope.ProjectDefinition)
        {
            var folders = new NativeV11Folders(session);
            folders.Validate(request);
            var route = folders.OwnerRoute(request.Address);
            var rootName = route.Folder.TrimEnd('/');
            var parentPath = request.Address.FolderPath ?? throw Incomplete("The native folder parent is missing.");
            if (parentPath != rootName && !parentPath.StartsWith(rootName + "/", StringComparison.Ordinal))
                throw Incomplete("The folder parent does not belong to the reviewed native route.");
            object model = route.Root;
            object view = NativeV11Folders.CreateFolderView(session.Context.DesignerAssembly, route.Kind, null, model);
            foreach (var part in parentPath[rootName.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (NativeV11Reflection.Get(model, "Items") is not IEnumerable children) throw Incomplete("The native folder parent is not loaded.");
                var matches = children.Cast<object>().Where(child => NativeV11Reflection.Get(child, "Name") as string == part && NativeV11Reflection.Get(child, "Items") is IList).ToArray();
                if (matches.Length != 1) throw Incomplete("The native folder parent path is missing or ambiguous.");
                model = matches[0];
                view = NativeV11Folders.CreateFolderView(session.Context.DesignerAssembly, route.Kind, view, model);
            }
            if (request.Operation == ModuleOperation.Delete)
            {
                var original = folders.NativeModel(request.Address);
                var target = Resolve(session, [request.Address]).Single();
                var folderView = NativeV11Folders.CreateFolderView(session.Context.DesignerAssembly, route.Kind, view, original);
                var itemType = session.Context.DesignerAssembly.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!;
                var selected = Array.CreateInstance(itemType, 1);
                selected.SetValue(folderView, 0);
                var folderDeleteExecutor = NativeV11Reflection.Call(folderView, "DeleteItemsCollaborationExecutor", selected)
                    ?? throw Incomplete("The native folder delete collector is unavailable.");
                var collected = RelatedCore(folderDeleteExecutor);
                var targetRelated = collected.Where(value => SameIdentity(value.Simple, target.SimpleModule)).ToArray();
                if (targetRelated.Length != 1 || targetRelated[0].State != "Remove")
                    throw Incomplete("The native folder delete collector disagrees with its target owner.");
                var known = EnumerateKnownLogicalOwners(session).ToDictionary(value => Name(value.SimpleModule), StringComparer.Ordinal);
                var folderResult = new List<NativeV11CollaborationOwner> { new(request.Address, targetRelated[0].Simple, "Remove") };
                foreach (var relatedFolderOwner in collected.Where(value => !SameIdentity(value.Simple, target.SimpleModule)))
                {
                    if (relatedFolderOwner.State != "Edit" || !known.TryGetValue(Name(relatedFolderOwner.Simple), out var folderExistingOwner) || !SameIdentity(relatedFolderOwner.Simple, folderExistingOwner.SimpleModule))
                        throw Incomplete("The native folder deletion includes an unrecognized related owner.");
                    folderResult.Add(folderExistingOwner);
                }
                return folderResult;
            }
            var incoming = NativeV11Reflection.Call(route.Colleague, "CreateFolder", request.Address.Name)!;
            var classification = ClassifyModelCore(session.Context.DesignerAssembly, incoming);
            var moduleType = folders.FolderModuleType(request.Address);
            var executorType = session.Context.DesignerAssembly.GetType("Forguncy.Views.ObjectViews.CreateItemOrFolderExecutor", true)!;
            var newer = session.Context.DesignerAssembly.GetName().Version!.ToString() == "11.0.104.0";
            var folderConstructor = executorType.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == (newer ? 6 : 5));
            var arguments = newer ? new object?[] { null, view, request.Address.Name, moduleType, NativeV11Reflection.Get(classification, "IconType"), NativeV11Reflection.Get(classification, "ExclusiveType") }
                : new object?[] { null, view, request.Address.Name, moduleType, NativeV11Reflection.Get(classification, "ExclusiveType") };
            var folderRelated = RelatedCore(folderConstructor.Invoke(arguments));
            var qualified = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.ModuleExtensions", true)!, "GetModuleName", incoming, parentPath) as string;
            if (folderRelated.Count != 1 || folderRelated[0].State != "New" || Name(folderRelated[0].Simple) != qualified ||
                !Equals(NativeV11Reflection.Get(folderRelated[0].Simple, "ModuleType"), moduleType))
                throw Incomplete("The native folder create collector disagrees with its qualified model identity.");
            var folderContract = GetContract(session.Context.DesignerAssembly);
            var modules = ModuleMap(folderContract, NativeV11Collaboration.RequireBoundManager(session));
            if (modules.Keys.Cast<object>().Any(key => key is not string name || name.Equals(qualified, StringComparison.OrdinalIgnoreCase)))
                throw Incomplete("The future native folder owner already exists or is ambiguous.");
            return [new(request.Address, folderRelated[0].Simple, "New")];
        }
        if (request.Address.Kind == ModuleKind.Image && request.Operation == ModuleOperation.Update && request.Scope == EffectScope.ProjectDefinition)
            return Resolve(session, [request.Address]);
        if (request.Address.Kind == ModuleKind.Image && request.Operation is ModuleOperation.Create or ModuleOperation.Delete && request.Scope == EffectScope.ProjectDefinition)
        {
            var resource = new NativeV11Resources(session);
            var collected = RelatedCore(resource.ImageCollaborationExecutor(request));
            if (request.Operation == ModuleOperation.Create)
            {
                var created = collected.Where(value => value.State == "New" && TypeName(value.Simple) == "Image").ToArray();
                if (collected.Count != 1 || created.Length != 1 || Name(created[0].Simple) != "Image/" + request.Address.Name)
                    throw Incomplete("The actual native image import collector disagrees with the requested image owner.");
                var modules = ModuleMap(GetContract(session.Context.DesignerAssembly), NativeV11Collaboration.RequireBoundManager(session));
                if (modules.Keys.Cast<object>().Any(key => key is not string name || name.Equals(Name(created[0].Simple), StringComparison.OrdinalIgnoreCase)))
                    throw Incomplete("The future native image owner already exists or is ambiguous.");
                return [new(request.Address, created[0].Simple, "New")];
            }
            var target = Resolve(session, [request.Address]).Single();
            var targetRelated = collected.Where(value => SameIdentity(value.Simple, target.SimpleModule)).ToArray();
            if (targetRelated.Length != 1 || targetRelated[0].State != "Remove")
                throw Incomplete("The native image delete collector disagrees with its target owner.");
            var known = EnumerateKnownLogicalOwners(session).ToDictionary(value => Name(value.SimpleModule), StringComparer.Ordinal);
            var imageResult = new List<NativeV11CollaborationOwner> { new(request.Address, targetRelated[0].Simple, "Remove") };
            foreach (var relatedImageOwner in collected.Where(value => !SameIdentity(value.Simple, target.SimpleModule)))
            {
                if (relatedImageOwner.State != "Edit" || !known.TryGetValue(Name(relatedImageOwner.Simple), out var imageExistingOwner) ||
                    !SameIdentity(relatedImageOwner.Simple, imageExistingOwner.SimpleModule))
                    throw Incomplete("The native image deletion includes an unrecognized related owner.");
                imageResult.Add(imageExistingOwner);
            }
            return imageResult;
        }
        if (request.Address.Kind == ModuleKind.ResourceFile && request.Scope == EffectScope.ProjectDefinition &&
            request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete && NativeV11LocalLibraries.SupportsAddress(request.Address))
            return Resolve(session, [new(ModuleKind.CustomLibrary, "CustomLibraries")]);
        if (request.Address.Kind == ModuleKind.ResourceFile && request.Scope == EffectScope.ProjectDefinition &&
            request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)
        {
            var resource = new NativeV11Resources(session);
            resource.Validate(request);
            var files = resource.SourcePaths(request.Address);
            if (files.Count != 1) throw Incomplete("An ordinary resource must have exactly one native source path.");
            var inventory = CaptureNativeFileGroups(session);
            var known = EnumerateKnownLogicalOwners(session).ToDictionary(owner => Name(owner.SimpleModule), StringComparer.Ordinal);
            var owners = new List<NativeV11CollaborationOwner>();
            foreach (var (name, group) in inventory)
            {
                if (!group.Files.Any(path => FileGroupsIntersect(path, files[0]))) continue;
                if (!known.TryGetValue(name, out var owner) || !SameIdentity(owner.SimpleModule, group.NativeModule))
                    throw Incomplete("A resource file intersects an unmapped native collaboration owner.");
                if (NativeV11Reflection.Get(group.NativeModule, "State")?.ToString() == "Remove")
                    throw Incomplete("A resource owner has a pending native deletion.");
                owners.Add(owner);
            }
            if (owners.Count == 0) throw Incomplete("The resource file has no registered native owner; its checkout and save scope cannot be established.");
            return owners;
        }
        if (request.Address.Kind is ModuleKind.Table or ModuleKind.View && request.Operation == ModuleOperation.Create &&
            request.Scope == EffectScope.ProjectDefinition && NativeV11BuiltInTableLifecycle.IsBuiltInCreate(request))
        {
            NativeV11BuiltInTableLifecycle.ValidateRequestShape(request);
            var designer = session.Context.DesignerAssembly;
            var tableCreateType = designer.GetType("Forguncy.Common.CreateTableExecutor", true)!;
            var newer = designer.GetName().Version!.ToString() == "11.0.104.0";
            var tableCreateConstructor = tableCreateType.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == (newer ? 5 : 4) &&
                value.GetParameters()[1].ParameterType == typeof(string));
            var parameters = tableCreateConstructor.GetParameters();
            var isView = request.Address.Kind == ModuleKind.View;
            var moduleType = Enum.Parse(parameters[2].ParameterType, isView ? "TableView" : "Table");
            var arguments = newer ? new object?[] { null, request.Address.Name, moduleType, Enum.ToObject(parameters[3].ParameterType, isView ? 26 : 24), true }
                : new object?[] { null, request.Address.Name, moduleType, true };
            var collected = RelatedCore(tableCreateConstructor.Invoke(arguments));
            var created = collected.Where(value => value.State == "New" && TypeName(value.Simple) == (isView ? "TableView" : "Table")).ToArray();
            if (created.Length != 1 || Name(created[0].Simple) != (isView ? "TableView/" : "Table/") + request.Address.Name)
                throw Incomplete("The native built-in table collector disagrees with the requested New owner.");
            var known = EnumerateKnownLogicalOwners(session).ToDictionary(value => Name(value.SimpleModule), StringComparer.Ordinal);
            var tableResult = new List<NativeV11CollaborationOwner> { new(request.Address, created[0].Simple, "New") };
            foreach (var extra in collected.Where(value => !ReferenceEquals(value.Simple, created[0].Simple)))
            {
                if (extra.State != "Edit" || !known.TryGetValue(Name(extra.Simple), out var tableExisting) || !SameIdentity(extra.Simple, tableExisting.SimpleModule))
                    throw Incomplete("The native built-in table collector includes an unrecognized related owner.");
                if (!tableResult.Any(value => Name(value.SimpleModule) == Name(tableExisting.SimpleModule))) tableResult.Add(tableExisting);
            }
            return tableResult;
        }
        if (request.Address.Kind is ModuleKind.Table or ModuleKind.View && request.Scope == EffectScope.ProjectDefinition &&
            request.Operation is ModuleOperation.Update or ModuleOperation.Delete)
        {
            var shared = ResolveTableSharedOwners(session, request.Address).ToList();
            if (request.Operation == ModuleOperation.Update) return shared;
            var target = shared.Single(owner => owner.Address == request.Address);
            var tableModel = session.Find(request.Address).Model;
            var tableReferences = NativeV11References.Find(session, request.Address, tableModel);
            if (!tableReferences.Complete || tableReferences.References.Count != 0)
                throw Incomplete("Table deletion requires a complete native inbound-reference check.");
            var tableRelated = RelatedCore(CreateTreeItemDeleteCollector(session, session.Find(request.Address)));
            var removed = tableRelated.Where(value => SameIdentity(value.Simple, target.SimpleModule)).ToArray();
            if (removed.Length != 1 || removed[0].State != "Remove") throw Incomplete("The native table delete collector disagrees with the target.");
            var known = EnumerateKnownLogicalOwners(session).ToDictionary(value => Name(value.SimpleModule), StringComparer.Ordinal);
            var result = shared.Where(owner => owner.Address != request.Address).ToList();
            result.Add(new(request.Address, removed[0].Simple, "Remove"));
            foreach (var extra in tableRelated.Where(value => !SameIdentity(value.Simple, target.SimpleModule)))
            {
                if (extra.State != "Edit" || !known.TryGetValue(Name(extra.Simple), out var owner) || !SameIdentity(extra.Simple, owner.SimpleModule))
                    throw Incomplete("The native table delete collector includes an unrecognized related owner.");
                if (!result.Any(value => Name(value.SimpleModule) == Name(owner.SimpleModule))) result.Add(owner);
            }
            return result;
        }
        if (SettingsOwnerAddress(request.Address.Kind) is ModuleAddress settingsOwner)
        {
            if (request.Scope != EffectScope.ProjectDefinition || request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
                throw Incomplete("The requested settings mutation lifecycle is not available.");
            new NativeV11Settings(session).Validate(request);
            return Resolve(session, [settingsOwner]);
        }
        if (request.Address.Kind is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.Report or
                ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.BusinessProcess) ||
            request.Scope != EffectScope.ProjectDefinition || request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
            throw Incomplete("The requested mutation owner lifecycle is not available.");
        if (request.Address.Kind == ModuleKind.Report) NativeV11Reports.Validate(session, request);
        else if (request.Address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage) NativeV11PageWriter.Validate(session, request);
        else if (request.Address.Kind == ModuleKind.BusinessProcess) NativeV11Processes.Validate(session, request);
        else NativeV11DefinitionWriter.Validate(session, request);
        var contract = GetContract(session.Context.DesignerAssembly);
        var manager = NativeV11Collaboration.RequireBoundManager(session);
        if (request.Operation == ModuleOperation.Create)
        {
            if (request.Address.Kind is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.Report or ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.BusinessProcess))
                throw Incomplete("New registration for this native definition requires a separate future file-group proof.");
            NativeV11CreationGuard.Verify(session, request.Address);
            var route = session.Route(request.Address.Kind);
            var path = session.NativePath(request.Address);
            var json = request.Address.Kind == ModuleKind.ServerCommand
                ? NativeV11DefinitionWriter.NormalizeNewServerCommandTriggers(request.Document!.Metadata)
                : request.Document!.Metadata.GetRawText();
            var data = route.IsPage ? NativeV11PageWriter.LoadForAddress(route, path, json) : NativeV11DefinitionWriter.Load(route, path, json);
            var model = route.IsPage ? NativeV11Reflection.Get(data, "PageInfo")! : data;
            var classification = ClassifyModelCore(session.Context.DesignerAssembly, model);
            var processOwners = request.Address.Kind == ModuleKind.BusinessProcess ? NewProcessOwners(session, request.Address, classification) : null;
            var owner = processOwners is null ? NewOwnerCore(session.Context.DesignerAssembly, request.Address, classification) : processOwners.Single(value => value.State == "New");
            foreach (DictionaryEntry entry in ModuleMap(contract, manager))
                if (entry.Key is not string key || key.Equals(Name(owner.SimpleModule), StringComparison.OrdinalIgnoreCase))
                    throw Incomplete("The new native owner name is already registered or ambiguous.");
            return processOwners ?? [owner];
        }
        var existing = Resolve(session, [request.Address]).Single();
        if (request.Operation == ModuleOperation.Update) return [existing];
        var item = session.Find(request.Address);
        var references = NativeV11References.Find(session, request.Address, item.Model);
        if (!references.Complete || references.References.Count != 0)
            throw Incomplete("Deletion requires complete native inbound-reference rejection before its owner plan is collected.");
        var executor = request.Address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.Report
            ? CreateDeleteCollector(session.Context.DesignerAssembly, request.Address.Kind, item.Model)
            : CreateTreeItemDeleteCollector(session, item);
        var related = RelatedCore(executor);
        if (related.Count != 1 || related[0].State != "Remove" || !SameIdentity(existing.SimpleModule, related[0].Simple))
            throw Incomplete("The native delete collector found an additional owner or disagrees with the reviewed target.");
        return [new(request.Address, related[0].Simple, "Remove")];
    }

    internal static object CreateDeleteCollector(Assembly designer, ModuleKind kind, object model)
    {
        var name = kind switch
        {
            ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage => "Forguncy.Common.DeletePageExecutor",
            ModuleKind.Report => "Forguncy.Common.DeleteReportExecutor",
            _ => throw Incomplete("The concrete native delete collector for this definition has not been verified.")
        };
        var type = designer.GetType(name, true)!;
        if (type.IsAbstract || !designer.GetType("Forguncy.Common.TreeNodeDeleteExecutor", true)!.IsAssignableFrom(type))
            throw Incomplete("The native delete collector is not the expected concrete executor.");
        var constructor = type.GetConstructors(NativeV11Reflection.Flags).Single(ctor => ctor.GetParameters().Length == 2 &&
            ctor.GetParameters()[1].ParameterType.IsInstanceOfType(model));
        return constructor.Invoke([null, model]);
    }

    private static object CreateTreeItemDeleteCollector(NativeV11Session session, NativeV11Item item)
    {
        var designer = session.Context.DesignerAssembly;
        var route = item.Route;
        var kind = item.Address.Kind is ModuleKind.Table or ModuleKind.View ? ModuleKind.Table : item.Address.Kind;
        var model = route.Root;
        object view = NativeV11Folders.CreateFolderView(designer, kind, null, model);
        foreach (var segment in NativeV11Session.NormalizeFolder(item.Address.FolderPath).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NativeV11Reflection.Get(model, "Items") is not IList children) throw Incomplete("The native item parent is not a folder.");
            var matches = children.Cast<object>().Where(child => NativeV11Reflection.Get(child, "Name") as string == segment &&
                NativeV11Reflection.Get(child, "Items") is IList).ToArray();
            if (matches.Length != 1) throw Incomplete("The native item parent folder is missing or ambiguous.");
            model = matches[0];
            view = NativeV11Folders.CreateFolderView(designer, kind, view, model);
        }
        return CreateTreeItemDeleteCollectorCore(designer, kind, view, item.Model);
    }

    internal static object CreateTreeItemDeleteCollectorCore(Assembly designer, ModuleKind kind, object parent, object model)
    {
        var typeName = kind switch
        {
            ModuleKind.Table => "TableItemViewModel", ModuleKind.ServerCommand => "ServerCommandItemViewModel",
            ModuleKind.SharedCommand => "ShareCommandContainerItemViewModel", ModuleKind.ScheduledTask => "TaskSchedulerItemViewModel",
            ModuleKind.Notification => "NotificationsItemViewModel", ModuleKind.BusinessProcess => "BusinessProcessItemViewModel",
            _ => throw Incomplete("The native item deletion view is not mapped.")
        };
        var viewType = designer.GetType("Forguncy.Views.ObjectViews." + typeName, true)!;
        var constructors = viewType.GetConstructors(NativeV11Reflection.Flags).Where(ctor =>
        {
            var parameters = ctor.GetParameters();
            return parameters.Length >= 2 && parameters[0].ParameterType.IsInstanceOfType(parent) &&
                parameters[1].ParameterType.IsInstanceOfType(model) && parameters.Skip(2).All(parameter => parameter.ParameterType == typeof(bool));
        }).ToArray();
        if (constructors.Length != 1) throw Incomplete("The native item deletion view constructor is unavailable or ambiguous.");
        var args = constructors[0].GetParameters().Select((_, index) => index == 0 ? parent : index == 1 ? model : (object)false).ToArray();
        var itemView = constructors[0].Invoke(args);
        var itemType = designer.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!;
        var selected = Array.CreateInstance(itemType, 1);
        selected.SetValue(itemView, 0);
        return NativeV11Reflection.Call(itemView, "DeleteItemsCollaborationExecutor", selected)
            ?? throw Incomplete("The native item deletion collector is unavailable.");
    }

    internal static IReadOnlyList<string> GetMutationFiles(NativeV11Session session, ModuleMutationRequest request,
        IReadOnlyList<NativeV11CollaborationOwner> owners)
    {
        if (owners.Count != 1 || owners[0].Address != request.Address) throw Incomplete("The mutation owner projection is invalid.");
        if (request.Operation != ModuleOperation.Create)
            return GetDirectFiles(session, owners[0] with { State = "Edit" }); // Actual pre-mutation module still exists.
        if (owners[0].State != "New") throw Incomplete("A creation must retain native New state.");
        _ = ResolveMutation(session, request); // Recheck absence and exact native classification.
        if (request.Address.Kind is ModuleKind.Folder or ModuleKind.Image) return PrimaryPaths(session, request.Address);
        if (request.Address.Kind is ModuleKind.Table or ModuleKind.View)
            return PrimaryPaths(session, request.Address).Append(TableDatabasePath()).ToArray();
        if (request.Address.Kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask)
        {
            var route = session.Route(request.Address.Kind);
            var json = request.Address.Kind == ModuleKind.ServerCommand
                ? NativeV11DefinitionWriter.NormalizeNewServerCommandTriggers(request.Document!.Metadata)
                : request.Document!.Metadata.GetRawText();
            var model = NativeV11DefinitionWriter.Load(route, session.NativePath(request.Address), json);
            var projection = new System.Text.Json.Nodes.JsonObject();
            NativeV11CommandApi.ProjectKnownCommands(projection, model);
            if (!projection.ContainsKey("commands"))
                throw Incomplete("New command ownership requires a complete proven asset-free native command tree.");
        }
        else if (request.Address.Kind == ModuleKind.BusinessProcess)
            return ProcessFutureFiles(session, owners[0]);
        else if (request.Address.Kind is not (ModuleKind.Report or ModuleKind.Notification))
        {
            var route = session.Route(request.Address.Kind);
            var data = NativeV11PageWriter.LoadForAddress(route, session.NativePath(request.Address), request.Document!.Metadata.GetRawText());
            RequireAssetFreeNewPageCore(data, request.Document.Metadata);
        }
        // ReportReferFilesProvider has exactly its JSON/RDLX pair; the blank
        // page guard proves both native command/cell-designer asset inputs
        // absent. Future files cannot be queried via a not-yet-registered tree.
        return PrimaryPaths(session, request.Address);
    }

    private static IReadOnlyList<NativeV11CollaborationOwner> NewProcessOwners(NativeV11Session session, ModuleAddress address, object classification)
    {
        // The initialized built-in path uses the actual native collector with
        // NeedCreateTable=false. Never run first-time database initialization.
        if (NativeV11Reflection.Get(classification, "ExclusiveType") is null)
            throw Incomplete("Only an already-initialized built-in process database is currently supported for process creation.");
        var type = session.Context.DesignerAssembly.GetType("Forguncy.Common.CreateProcessItemExecutor", true)!;
        var constructor = type.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == 4);
        var related = RelatedCore(constructor.Invoke([null, address.Name, false, false]));
        var created = related.Where(value => value.State == "New").ToArray();
        if (created.Length != 1 || !SameIdentity(created[0].Simple, classification))
            throw Incomplete("The native process creation collector disagrees with the incoming process identity.");
        var known = EnumerateKnownLogicalOwners(session).ToDictionary(value => Name(value.SimpleModule), StringComparer.Ordinal);
        var result = new List<NativeV11CollaborationOwner> { new(address, created[0].Simple, "New") };
        foreach (var item in related.Where(value => value.State != "New"))
        {
            if (item.State != "Edit" || !known.TryGetValue(Name(item.Simple), out var existing) ||
                !SameIdentity(item.Simple, existing.SimpleModule) || existing.Address.Kind != ModuleKind.BusinessProcess)
                throw Incomplete("Process initialization or an unrecognized related native owner requires a separate lifecycle.");
            result.Add(existing);
        }
        return result;
    }

    // Discovery only: first initialization may create tables, pages and other
    // owners. Capture every tuple from the real native collector, without
    // running its action, acquiring locks or initializing a database.
    internal static IReadOnlyList<NativeV11FirstProcessOwner> CaptureFirstProcessOwners(NativeV11Session session, ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.BusinessProcess || request.Operation != ModuleOperation.Create || request.Scope != EffectScope.ProjectDefinition)
            throw Incomplete("First-process owner discovery requires a process creation request.");
        session.VerifyContext();
        if (session.Items(ModuleKind.BusinessProcess).Count != 0)
            throw Incomplete("First-process owner discovery requires an empty native process tree.");
        var manager = NativeV11Collaboration.RequireBoundManager(session);
        var type = session.Context.DesignerAssembly.GetType("Forguncy.Common.CreateProcessItemExecutor", true)!;
        var constructor = type.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == 4);
        var related = RelatedCore(constructor.Invoke([null, request.Address.Name, true, false]));
        var owners = related.Select(value => new NativeV11FirstProcessOwner(Name(value.Simple), TypeName(value.Simple), value.State, value.Simple)).ToArray();
        if (owners.Length == 0 || owners.Any(owner => owner.State is not ("New" or "Edit")) ||
            owners.Select(owner => owner.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != owners.Length ||
            owners.Count(owner => owner.Type == "ProcessItem" && owner.State == "New" && owner.Name == "ProcessItem/" + request.Address.Name) != 1)
            throw Incomplete("The native first-process initialization owner set is incomplete or ambiguous.");
        session.VerifyContext();
        if (!ReferenceEquals(manager, NativeV11Collaboration.RequireBoundManager(session)))
            throw Incomplete("The native collaboration session changed while discovering initialization owners.");
        return owners;
    }

    private static IReadOnlyList<string> ProcessFutureFiles(NativeV11Session session, NativeV11CollaborationOwner owner)
    {
        var groups = CaptureNativeFileGroups(session);
        var processGroups = groups.Values.Where(group => TypeName(group.NativeModule) == "ProcessItem" &&
            Equals(NativeV11Reflection.Get(group.NativeModule, "ExclusiveType"), NativeV11Reflection.Get(owner.SimpleModule, "ExclusiveType"))).ToArray();
        var databases = processGroups.SelectMany(group => group.Files).Where(path =>
            Path.GetFileName(path).Equals("ProcessForguncyDB.sqlite3", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (processGroups.Length == 0 || databases.Length != 1 || !databases[0].Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase))
            throw Incomplete("An already-initialized shared native process database and existing process owner are required.");
        var database = databases[0];
        (string Hash, long Length, bool Valid)? baseline = null;
        foreach (var root in new[] { session.SourcePath, session.LocalPath, session.LastPath })
        {
            var actual = NativeV11UnchangedDatabaseProof.ReadFingerprint(root, database);
            if (!actual.Valid || baseline is not null && actual != baseline.Value)
                throw Incomplete("The initialized process database differs between source and native saved caches.");
            baseline = actual;
        }
        return PrimaryPaths(session, owner.Address).Append(database).ToArray();
    }

    internal static void RequireAssetFreeNewPageCore(object data, JsonElement metadata)
    {
        if (data.GetType().FullName != "Forguncy.SaveLoad.PageSaveData" || metadata.ValueKind != JsonValueKind.Object ||
            !metadata.TryGetProperty("PageInfo", out var pageInfo) || pageInfo.ValueKind != JsonValueKind.Object)
            throw Incomplete("The future page must be a real canonical native page DTO.");
        foreach (var property in new[] { "AttachInfos", "Pictures", "BackgroundPictures", "Charts" })
        {
            var value = NativeV11Reflection.Get(data, property);
            if (value is not null && (value is not ICollection collection || collection.Count != 0))
                throw Incomplete("New collaboration pages currently require no attached cells, drawing objects or asset providers.");
        }
        var allowed = new HashSet<string>(["$type", "Name", "PermissionData", "RowBreakLines", "RepeatRangeSettings"], StringComparer.Ordinal);
        if (pageInfo.EnumerateObject().Any(property => !allowed.Contains(property.Name)))
            throw Incomplete("New collaboration pages currently require the canonical blank PageInfo shape; page commands, public interfaces and scripts need a future-group proof.");
    }

    internal static NativeV11CollaborationOwner NewOwnerCore(Assembly designer, ModuleAddress address, object classification)
    {
        var contract = GetContract(designer);
        if (classification.GetType() != contract.Simple) throw Incomplete("The new native classifier identity is invalid.");
        var moduleType = NativeV11Reflection.Get(classification, "ModuleType")!;
        var attributes = moduleType.GetType().Assembly.GetType("Forguncy.Collaboration.ModuleTypeAttributeExtensions", true)!;
        if (NativeV11Reflection.Static(attributes, "IsFolderItemModule", moduleType) is not true)
            throw Incomplete("The native new-item collector accepts named leaf modules only.");
        var executorType = designer.GetType("Forguncy.Views.ObjectViews.CreateItemOrFolderExecutor", true)!;
        var newer = designer.GetName().Version!.ToString() == "11.0.104.0";
        var arguments = newer
            ? new object?[] { null, null, address.Name, moduleType, NativeV11Reflection.Get(classification, "IconType"), NativeV11Reflection.Get(classification, "ExclusiveType") }
            : new object?[] { null, null, address.Name, moduleType, NativeV11Reflection.Get(classification, "ExclusiveType") };
        var constructor = executorType.GetConstructors(NativeV11Reflection.Flags).Single(ctor => ctor.GetParameters().Length == arguments.Length);
        var related = RelatedCore(constructor.Invoke(arguments));
        if (related.Count != 1 || related[0].State != "New" || !SameIdentity(classification, related[0].Simple))
            throw Incomplete("The actual native new-item collector disagrees with the detached model classification.");
        return new(address, related[0].Simple, "New");
    }

    private sealed record Related(object Simple, string State);
    private static IReadOnlyList<Related> RelatedCore(object executor)
    {
        if (NativeV11Reflection.Call(executor, "GetRelatedModules") is not IEnumerable values)
            throw Incomplete("The native related-owner collector returned no enumerable.");
        var result = new List<Related>();
        foreach (var value in values)
        {
            if (value is null) throw Incomplete("A native related-owner tuple is null.");
            var simple = value.GetType().GetField("Item1")?.GetValue(value);
            var state = value.GetType().GetField("Item2")?.GetValue(value)?.ToString();
            if (simple is null || state is not ("Edit" or "New" or "Remove" or "Move"))
                throw Incomplete("A native related-owner tuple has an unknown identity/state.");
            result.Add(new(simple, state));
        }
        return result;
    }

    // Pending deletion is recovered from the real native record and its saved
    // ToRemoveFiles, never by treating a missing live model as success.
    internal static IReadOnlyList<NativeV11CollaborationOwner> ResolveForCheckin(NativeV11Session session, IReadOnlyList<ModuleAddress> addresses)
    {
        var contract = GetContract(session.Context.DesignerAssembly);
        var modules = ModuleMap(contract, NativeV11Collaboration.RequireBoundManager(session));
        var result = new List<NativeV11CollaborationOwner>();
        foreach (var address in addresses)
        {
            var removed = new List<object>();
            foreach (DictionaryEntry entry in modules)
            {
                var module = entry.Value ?? throw Incomplete("A native record is null.");
                ValidateModule(contract, entry.Key, module);
                if (NativeV11Reflection.Get(module, "State")?.ToString() == "Remove" && DeletedAddress(session, module) == address)
                    removed.Add(module);
            }
            if (removed.Count > 1) throw Incomplete("Multiple native deletion records match one logical address.");
            if (removed.Count == 0) { result.AddRange(Resolve(session, [address])); continue; }
            var paths = PrimaryPaths(session, address);
            if (NativeV11PendingSave.ReadFiles(session.SourcePath, paths).Values.Any(bytes => bytes is not null))
                throw Incomplete("The pending native deletion has not been source-verified.");
            result.Add(new(address, contract.FromModule.Invoke([removed[0]]), "Remove"));
        }
        session.VerifyContext();
        return result;
    }

    internal static IReadOnlyList<NativeV11CollaborationOwner> PendingDeletions(NativeV11Session session)
    {
        var contract = GetContract(session.Context.DesignerAssembly);
        var modules = ModuleMap(contract, NativeV11Collaboration.RequireBoundManager(session));
        var result = new List<NativeV11CollaborationOwner>();
        foreach (DictionaryEntry entry in modules)
        {
            var module = entry.Value ?? throw Incomplete("A native record is null.");
            ValidateModule(contract, entry.Key, module);
            if (NativeV11Reflection.Get(module, "State")?.ToString() != "Remove") continue;
            var address = DeletedAddress(session, module);
            if (address is not null) result.Add(new(address, contract.FromModule.Invoke([module]), "Remove"));
        }
        return result;
    }

    private static ModuleAddress? DeletedAddress(NativeV11Session session, object module)
    {
        if (TypeName(module) == "ImageFolder")
        {
            var imageFolderName = Name(module).Replace('\\', '/'); var imageFolderSlash = imageFolderName.LastIndexOf('/');
            if (!imageFolderName.StartsWith("image/", StringComparison.Ordinal) || imageFolderSlash < "image".Length || imageFolderSlash == imageFolderName.Length - 1)
                throw Incomplete("The pending image folder identity is invalid.");
            var imageFolderAddress = new ModuleAddress(ModuleKind.Folder, imageFolderName[(imageFolderSlash + 1)..], imageFolderName[..imageFolderSlash]);
            if (NativeV11ImageFolderLifecycle.Marker(imageFolderAddress).TrimEnd('/') != imageFolderName) throw Incomplete("The pending image folder name is not canonical.");
            if (NativeV11Reflection.Get(module, "ToRemoveFiles") is not IEnumerable files ||
                !files.Cast<object>().Any(value => value is string path && NormalizeFile(session.SourcePath, path, true).TrimEnd('/') == imageFolderName))
                throw Incomplete("The pending image folder has no exact native removal marker.");
            return imageFolderAddress;
        }
        if (TypeName(module) == "Image")
        {
            if (NativeV11Reflection.Get(module, "ToRemoveFiles") is not IEnumerable imageFiles)
                throw Incomplete("The pending image deletion has no native source group.");
            var imageName = Name(module).Split('/').Last();
            var imagePaths = imageFiles.Cast<object>().Select(value => value is string path ? NormalizeFile(session.SourcePath, path) :
                throw Incomplete("The pending image deletion contains an invalid path."))
                .Where(path => path.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == imageName).ToArray();
            if (imagePaths.Length != 1) throw Incomplete("The pending image deletion has no unique original image file.");
            var imageRelative = imagePaths[0]["image/".Length..];
            var imageSlash = imageRelative.LastIndexOf('/');
            var imageAddress = new ModuleAddress(ModuleKind.Image, imageName, imageSlash < 0 ? null : imageRelative[..imageSlash]);
            NativeV11Session.ValidateAddress(imageAddress);
            if (PrimaryPaths(session, imageAddress).Single() != imagePaths[0])
                throw Incomplete("The pending image deletion path differs from its native resource identity.");
            return imageAddress;
        }
        var nativeType = NativeV11Reflection.Get(module, "ModuleType") ?? throw Incomplete("A native deletion type is missing.");
        var attributes = nativeType.GetType().Assembly.GetType("Forguncy.Collaboration.ModuleTypeAttributeExtensions", true)!;
        if (NativeV11Reflection.Static(attributes, "IsFolderModule", nativeType) is true)
        {
            var folderName = Name(module).Replace('\\', '/');
            var folderMatches = new List<ModuleAddress>();
            foreach (var folderRoute in session.Routes)
            {
                if (!folderName.StartsWith(folderRoute.Folder, StringComparison.Ordinal)) continue;
                var folderSlash = folderName.LastIndexOf('/');
                if (folderSlash < folderRoute.Folder.Length - 1 || folderSlash == folderName.Length - 1) continue;
                var candidate = new ModuleAddress(ModuleKind.Folder, folderName[(folderSlash + 1)..], folderName[..folderSlash]);
                NativeV11Session.ValidateAddress(candidate);
                if (Equals(new NativeV11Folders(session).FolderModuleType(candidate), nativeType)) folderMatches.Add(candidate);
            }
            if (folderMatches.Count != 1 || NativeV11Reflection.Get(module, "ToRemoveFiles") is not IEnumerable folderFiles ||
                !folderFiles.Cast<object>().Any(value => value is string path && NormalizeFile(session.SourcePath, path, true).TrimEnd('/') == folderName))
                throw Incomplete("The pending folder deletion cannot be recovered from its exact native owner and original directory marker.");
            return folderMatches[0];
        }
        var kind = TypeName(module) switch
        {
            "PCPage" or "MobilePage" => ModuleKind.Page,
            "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage,
            "UserControlPage" => ModuleKind.UserControlPage,
            "PageReport" or "RdlReport" => ModuleKind.Report,
            "ServerCommand" or "ServerCommandPrivate" or "AppService" => ModuleKind.ServerCommand,
            "ShareCommandContainer" => ModuleKind.SharedCommand,
            "TaskScheduler" => ModuleKind.ScheduledTask,
            "Notification" => ModuleKind.Notification,
            "ProcessItem" => ModuleKind.BusinessProcess,
            "Table" => ModuleKind.Table,
            "TableView" or "TableProcessView" => ModuleKind.View,
            _ => (ModuleKind?)null
        };
        if (kind is null) return null;
        if (NativeV11Reflection.Get(module, "ToRemoveFiles") is not IEnumerable nativePaths)
            throw Incomplete("A pending native deletion has no tracked original file group.");
        var route = session.Route(kind.Value);
        var paths = nativePaths.Cast<object>().Select(value => value is string path ? NormalizeFile(session.SourcePath, path) :
            throw Incomplete("A native removal path is invalid.")).Where(path => path.StartsWith(route.Folder, StringComparison.OrdinalIgnoreCase) &&
                path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray();
        var name = Name(module).Split('/').Last();
        NativeV11Session.ValidateAddress(new(kind.Value, name));
        var matches = paths.Where(path => Path.GetFileNameWithoutExtension(path) == name).ToArray();
        if (matches.Length != 1) throw Incomplete("The exact original definition path cannot be recovered from the native deletion record.");
        var relative = matches[0][route.Folder.Length..];
        var slash = relative.LastIndexOf('/');
        var address = new ModuleAddress(kind.Value, name, slash < 0 ? null : relative[..slash]);
        if (session.NativePath(address) != matches[0]) throw Incomplete("The recovered deletion path differs from its native route.");
        return address;
    }

    // All native records remain represented, including owners without a wire
    // ModuleKind. Their provider is used only to prove file intersections; it
    // does not pretend that they are logical account/settings documents.
    internal static IReadOnlyDictionary<string, NativeV11CollaborationFileOwner> CaptureNativeFileGroups(NativeV11Session session)
    {
        session.VerifyContext();
        var contract = GetContract(session.Context.DesignerAssembly);
        var manager = NativeV11Collaboration.RequireBoundManager(session);
        var root = NativeV11Collaboration.RequireWorkingDirectory(session);
        RequireCompleteProviderContext(session);
        var modules = ModuleMap(contract, manager);
        var result = new Dictionary<string, NativeV11CollaborationFileOwner>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in modules)
        {
            var module = entry.Value ?? throw Incomplete("A native owner record is null.");
            ValidateModule(contract, entry.Key, module);
            var type = NativeV11Reflection.Get(module, "ModuleType") ?? throw Incomplete("A native module type is missing.");
            if (!Enum.IsDefined(type.GetType(), type)) throw Incomplete("An unrecognized native owner type is present.");
            var removed = NativeV11Reflection.Get(module, "State")?.ToString() == "Remove";
            var files = removed ? NativeV11Reflection.Get(module, "ToRemoveFiles") as IEnumerable : contract.DirectFiles.Invoke(null, [module]) as IEnumerable;
            if (files is null) throw Incomplete("A native owner has no authoritative file group.");
            var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            void Add(object? item)
            {
                if (item is not string raw) throw Incomplete("A native provider returned a non-string path.");
                var path = NormalizeFile(root, raw, true);
                if (paths.TryGetValue(path, out var previous) && previous != path)
                    throw Incomplete("A native file group contains a case-insensitive collision.");
                paths[path] = path;
            }
            foreach (var file in files) Add(file);
            if (contract.Tracked.GetValue(manager) is not IDictionary tracked) throw Incomplete("Native tracked files are unavailable.");
            foreach (DictionaryEntry trackedEntry in tracked)
                if (trackedEntry.Key is string key && key.Equals(Name(module), StringComparison.OrdinalIgnoreCase))
                {
                    if (key != Name(module) || trackedEntry.Value is not IEnumerable trackedFiles)
                        throw Incomplete("Native tracked owner identity is ambiguous.");
                    foreach (var file in trackedFiles) Add(file);
                }
            var attributes = type.GetType().Assembly.GetType("Forguncy.Collaboration.ModuleTypeAttributeExtensions", true)!;
            if (NativeV11Reflection.Static(attributes, "IsFolderItemModule", type) is true && paths.Count == 0)
                throw Incomplete("An existing native item has an empty provider group; its model may be missing or unloaded.");
            result.Add(Name(module), new(module, paths.Values.OrderBy(path => path, StringComparer.Ordinal).ToArray()));
        }
        session.VerifyContext();
        if (!ReferenceEquals(manager, NativeV11Collaboration.RequireBoundManager(session))) throw Incomplete("Native provider context changed.");
        return result;
    }

    // Transitive intersections protect shared command-uploaded/template files.
    // No unknown intersecting native owner may disappear from this closure.
    internal static IReadOnlyList<NativeV11CollaborationOwner> ExpandExistingOwners(NativeV11Session session,
        IReadOnlyList<NativeV11CollaborationOwner> requested, IReadOnlyList<string>? futureFiles = null, bool allowSharedTableDatabase = false)
    {
        var inventory = CaptureNativeFileGroups(session);
        var known = EnumerateKnownLogicalOwners(session).GroupBy(owner => Name(owner.SimpleModule), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var result = requested.ToList();
        var names = requested.Select(owner => Name(owner.SimpleModule)).ToHashSet(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (futureFiles is not null) foreach (var path in futureFiles) paths.Add(path);
        foreach (var owner in requested)
        {
            if (owner.State == "New")
            {
                if (futureFiles is null || inventory.ContainsKey(Name(owner.SimpleModule)))
                    throw Incomplete("A new owner requires a complete future file group and an unused native identity.");
                foreach (var path in futureFiles) paths.Add(path);
                continue;
            }
            if (!inventory.TryGetValue(Name(owner.SimpleModule), out var native) || !SameIdentity(owner.SimpleModule, native.NativeModule))
                throw Incomplete("A requested native owner is absent from the complete file inventory.");
            foreach (var path in native.Files) paths.Add(path);
        }
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (name, native) in inventory)
            {
                if (names.Contains(name) || !native.Files.Any(path => paths.Any(other => FileGroupsIntersect(path, other)))) continue;
                if (!known.TryGetValue(name, out var owner))
                    throw Incomplete("An intersecting native file owner has no proven logical projection: " + TypeName(native.NativeModule) + ".");
                if (NativeV11Reflection.Get(native.NativeModule, "State")?.ToString() == "Remove")
                    throw Incomplete("A shared file intersects a pending native deletion.");
                _ = GetDirectFiles(session, owner, allowSharedTableDatabase: allowSharedTableDatabase);
                names.Add(name); result.Add(owner);
                foreach (var path in native.Files) paths.Add(path);
                changed = true;
            }
        }
        return result;
    }

    // Read-only owner closure for callers implementing a table/view native
    // lifecycle. Shared SQLite and every intersecting native owner stay in the
    // returned set; this helper does not dispatch checkout or change the DB.
    internal static IReadOnlyList<NativeV11CollaborationOwner> ResolveTableSharedOwners(NativeV11Session session, ModuleAddress address)
    {
        if (address.Kind is not (ModuleKind.Table or ModuleKind.View)) throw Incomplete("A native table or view owner is required.");
        var target = Resolve(session, [address]).Single();
        var model = session.Find(address).Model;
        var executorType = session.Context.DesignerAssembly.GetType("Forguncy.Common.TableCheckOutExecutor", true)!;
        var constructor = executorType.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == 3 &&
            value.GetParameters()[1].ParameterType.IsInstanceOfType(model));
        var state = Enum.Parse(constructor.GetParameters()[2].ParameterType, "Edit");
        var related = RelatedCore(constructor.Invoke([null, model, state]));
        var known = EnumerateKnownLogicalOwners(session).ToDictionary(owner => Name(owner.SimpleModule), StringComparer.Ordinal);
        var requested = new List<NativeV11CollaborationOwner>();
        foreach (var item in related)
        {
            if (item.State != "Edit" || !known.TryGetValue(Name(item.Simple), out var owner) || !SameIdentity(item.Simple, owner.SimpleModule))
                throw Incomplete("The native table checkout collector includes an unrecognized related owner.");
            if (!requested.Any(value => Name(value.SimpleModule) == Name(owner.SimpleModule))) requested.Add(owner);
        }
        if (!requested.Any(owner => SameIdentity(owner.SimpleModule, target.SimpleModule)))
            throw Incomplete("The native table checkout collector omitted its requested owner.");
        var owners = ExpandExistingOwners(session, requested, allowSharedTableDatabase: true);
        foreach (var owner in owners) _ = GetDirectFiles(session, owner with { State = "Edit" }, allowSharedTableDatabase: true);
        return owners;
    }

    internal static string TableDatabasePath()
    {
        var common = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "CommonUtilities");
        var config = common.GetType("CommonUtilities.SystemConfigDef", true)!;
        var value = config.GetField("ForguncyDB_sqlite3_File", NativeV11Reflection.Flags)?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(value) || value != Path.GetFileName(value) || value.Contains(':'))
            throw Incomplete("The configured native shared table database path is invalid.");
        return value;
    }

    private static bool FileGroupsIntersect(string left, string right)
    {
        if (left.Equals(right, StringComparison.OrdinalIgnoreCase)) return true;
        return left.EndsWith('/') && right.StartsWith(left, StringComparison.OrdinalIgnoreCase) ||
               right.EndsWith('/') && left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireCompleteProviderContext(NativeV11Session session)
    {
        foreach (var route in session.Routes)
            foreach (var kind in route.Kind == ModuleKind.Table ? new[] { ModuleKind.Table, ModuleKind.View } : new[] { route.Kind })
                foreach (var item in session.Items(kind))
                    if (route.IsPage)
                    {
                        var sheet = session.Sheet(item.Address.Name);
                        if (sheet is null || NativeV11Reflection.Get(sheet, "Loaded") is not true ||
                            NativeV11Reflection.Get(sheet, "Tag") is not object tag || !ReferenceEquals(NativeV11Reflection.Get(tag, "PageInfo"), item.Model))
                            throw Incomplete("Every native page must have its fully loaded real worksheet before complete provider enumeration.");
                    }
        _ = new NativeV11Folders(session).List(ModuleKind.Folder);
        _ = Images(session);
    }

    // Status.Modules is a projection of known logical addresses, not the full
    // native owner inventory. The plan layer retains the complete Modules map
    // for scope guards; an unknown owner affected by a mutation must be refused.
    internal static IReadOnlyList<NativeV11CollaborationOwner> EnumerateStatusOwners(NativeV11Session session)
        => EnumerateKnownLogicalOwners(session);

    internal static IReadOnlyList<NativeV11CollaborationOwner> EnumerateKnownLogicalOwners(NativeV11Session session)
    {
        session.VerifyContext();
        var contract = GetContract(session.Context.DesignerAssembly);
        var manager = NativeV11Collaboration.RequireBoundManager(session);
        var modules = ModuleMap(contract, manager);
        var candidates = new Dictionary<string, ModuleAddress>(StringComparer.Ordinal);
        void Add(ModuleAddress address)
        {
            var identity = Identity(session, contract, address);
            if (!candidates.TryAdd(identity.Name, address))
                throw Incomplete("Multiple native model nodes share one collaboration owner name.");
        }
        foreach (var route in session.Routes)
        {
            foreach (var item in session.Items(route.Kind)) Add(item.Address);
            if (route.Kind == ModuleKind.Table)
                foreach (var item in session.Items(ModuleKind.View)) Add(item.Address);
        }
        foreach (var folder in new NativeV11Folders(session).List(ModuleKind.Folder)) Add(folder.Address);
        foreach (var folder in ImageFolders(session)) Add(folder.Address);
        foreach (var address in ImageAddresses(session)) Add(address);
        foreach (var (nativeType, address) in SingletonAddresses)
            if (modules.Values.Cast<object>().Any(module => TypeName(module) == nativeType)) Add(address);
        var result = new List<NativeV11CollaborationOwner>();
        foreach (DictionaryEntry entry in modules)
        {
            var module = entry.Value ?? throw Incomplete("A native module registration is null.");
            ValidateModule(contract, entry.Key, module);
            var name = Name(module);
            if (!candidates.TryGetValue(name, out var address)) continue;
            result.Add(new(address, contract.FromModule.Invoke([module]), "Edit"));
        }
        session.VerifyContext();
        return result.OrderBy(owner => Name(owner.SimpleModule), StringComparer.Ordinal).ToArray();
    }

    // Complete relative native owner group, including command-uploaded assets.
    // The native wrapper supplies the exact installed working-directory prefix;
    // no adapter-generated JSON/pair list replaces the native provider result.
    // The exact shared built-in process database is retained for process owners;
    // other database groups remain unavailable before checkout dispatch.
    internal static IReadOnlyList<string> GetDirectFiles(NativeV11Session session, NativeV11CollaborationOwner owner, bool includeTracked = true,
        bool allowSharedTableDatabase = false)
    {
        session.VerifyContext();
        var contract = GetContract(session.Context.DesignerAssembly);
        if (owner.State != "Edit" || owner.SimpleModule.GetType() != contract.Simple)
            throw Incomplete("Only existing native Edit owners have a verified read-only file projection.");
        var resolved = Resolve(session, [owner.Address]).Single();
        if (!SameIdentity(resolved.SimpleModule, owner.SimpleModule))
            throw Incomplete("The reviewed native owner identity changed.");
        var manager = NativeV11Collaboration.RequireBoundManager(session);
        var root = NativeV11Collaboration.RequireWorkingDirectory(session);
        var modules = ModuleMap(contract, manager);
        var module = ExistingModule(contract, modules, Name(owner.SimpleModule), TypeName(owner.SimpleModule));
        if (NativeV11Reflection.Get(module, "State")?.ToString() == "Remove")
            throw Incomplete("Removed owners require their native ToRemoveFiles lifecycle, which is not enabled here.");
        if (contract.DirectFiles.Invoke(null, [module]) is not IEnumerable nativeFiles)
            throw Incomplete("The native file provider returned no enumerable group.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exact = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var primary = PrimaryPaths(session, owner.Address);
        void AddPath(object? value)
        {
            if (value is not string raw) throw Incomplete("A native tracked-file path is not a string.");
            var allowDatabase = owner.Address.Kind == ModuleKind.BusinessProcess ||
                (allowSharedTableDatabase && owner.Address.Kind is (ModuleKind.Table or ModuleKind.View));
            var path = NormalizeFile(root, raw, allowDatabase);
            if (owner.Address.Kind == ModuleKind.Folder && primary.Count == 1 &&
                path.TrimEnd('/').Equals(primary[0].TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) path = primary[0];
            if (owner.Address.Kind == ModuleKind.BusinessProcess && IsDatabasePath(path) &&
                !path.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase))
                throw Incomplete("Only the native shared process database can be included in a process file group.");
            if (owner.Address.Kind is ModuleKind.Table or ModuleKind.View && IsDatabasePath(path))
            {
                var configured = TableDatabasePath();
                if (!path.Equals(configured, StringComparison.OrdinalIgnoreCase) &&
                    !(TypeName(owner.SimpleModule) == "TableProcessView" && path.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase)))
                    throw Incomplete("Only the exact native shared table database can be included in a table/view file group.");
            }
            if (exact.TryGetValue(path, out var prior) && prior != path)
                throw Incomplete("The native owner file group contains a case-insensitive path collision.");
            if (!includeTracked && exact.ContainsKey(path))
                throw Incomplete("The native direct provider returned a duplicate file path.");
            exact[path] = path;
            paths.Add(path);
        }
        foreach (var path in nativeFiles) AddPath(path);
        if (contract.Tracked.GetValue(manager) is not IDictionary tracked)
            throw Incomplete("The native tracked-file registry is unavailable.");
        foreach (DictionaryEntry entry in tracked)
        {
            if (entry.Key is not string key) throw Incomplete("A native tracked-file owner key is invalid.");
            if (!key.Equals(Name(owner.SimpleModule), StringComparison.OrdinalIgnoreCase)) continue;
            if (key != Name(owner.SimpleModule) || entry.Value is not IEnumerable files)
                throw Incomplete("The native tracked-file owner registration is ambiguous.");
            if (includeTracked) foreach (var path in files) AddPath(path);
        }
        if (paths.Count == 0) throw Incomplete("The native owner has an empty file group; completeness cannot be asserted.");
        if (primary.Any(path => !paths.Contains(path)))
            throw Incomplete("The native owner file group omits an authoritative definition or companion path.");
        session.VerifyContext();
        _ = NativeV11Collaboration.RequireBoundManager(session);
        return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();
    }

    // The native checkout itself asks its direct provider about a detached
    // not-yet-registered New module before insertion. Freeze that exact output
    // separately from the future model's complete file group.
    internal static IReadOnlyList<string> GetUnregisteredDirectFiles(NativeV11Session session, NativeV11CollaborationOwner owner)
    {
        if (owner.State != "New") throw Incomplete("A detached provider projection requires an exact native New owner.");
        session.VerifyContext();
        var contract = GetContract(session.Context.DesignerAssembly);
        var manager = NativeV11Collaboration.RequireBoundManager(session);
        var modules = ModuleMap(contract, manager);
        var name = Name(owner.SimpleModule);
        if (modules.Contains(name)) throw Incomplete("The future native owner is already registered.");
        var count = modules.Count;
        var module = NativeV11Reflection.Call(owner.SimpleModule, "ToModule", modules)
            ?? throw Incomplete("The native New owner projection is unavailable.");
        if (module.GetType() != contract.Module || !SameIdentity(owner.SimpleModule, module) || modules.Contains(name) || modules.Count != count)
            throw Incomplete("The detached native New projection changed the live registration.");
        var rawPaths = contract.DirectFiles.Invoke(null, [module]);
        // Both pinned checkout IL bodies explicitly coalesce a null native
        // provider result to an empty List<string> before tracking.
        var paths = rawPaths is null ? Array.Empty<string>() : rawPaths as IEnumerable
            ?? throw Incomplete("The native New provider returned an invalid file enumeration.");
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in paths)
        {
            if (item is not string raw) throw Incomplete("The native New provider returned a non-string path.");
            var normalized = NormalizeFile(session.SourcePath, raw, owner.Address.Kind is ModuleKind.BusinessProcess or ModuleKind.Table or ModuleKind.View);
            if (owner.Address.Kind == ModuleKind.BusinessProcess && IsDatabasePath(normalized) &&
                !normalized.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase))
                throw Incomplete("The detached process provider returned an unexpected database path.");
            if (owner.Address.Kind is ModuleKind.Table or ModuleKind.View && IsDatabasePath(normalized) &&
                !normalized.Equals(TableDatabasePath(), StringComparison.OrdinalIgnoreCase))
                throw Incomplete("The detached table provider returned an unexpected database path.");
            if (!result.TryAdd(normalized, normalized))
                throw Incomplete("The native New provider returned duplicate or ambiguous paths.");
        }
        session.VerifyContext();
        if (!ReferenceEquals(manager, NativeV11Collaboration.RequireBoundManager(session)) || modules.Contains(name) || modules.Count != count)
            throw Incomplete("The native New provider changed the active registration or manager.");
        return result.Values.Order(StringComparer.Ordinal).ToArray();
    }

    private sealed record ModelIdentity(string Name, string Type, object? Classification);
    private static ModelIdentity Identity(NativeV11Session session, Contract contract, ModuleAddress address)
    {
        if (address == MapResourceOwner) return new("MapJsonFile", "MapJsonFile", null);
        if (SingletonType(address.Kind) is string singleton)
        {
            if (address.Kind == ModuleKind.Plugin) _ = new NativeV11Plugins(session).Read(address);
            else _ = new NativeV11Settings(session).Read(address);
            return new(singleton, singleton, null);
        }
        if (address.Kind == ModuleKind.ResourceFile)
            throw Incomplete("Resource-file ownership must include every intersecting native module; that closure is not yet proven.");
        if (address.Kind == ModuleKind.Folder)
        {
            if (IsImageFolderAddress(address))
            {
                var folder = ImageFolders(session).SingleOrDefault(node => node.Address == address)
                    ?? throw Incomplete("The requested native image folder is missing or ambiguous.");
                var classification = ClassifyModelCore(session.Context.DesignerAssembly, folder.Model);
                var imageFolderName = address.FolderPath + "/" + address.Name;
                if (TypeName(classification) != "ImageFolder")
                    throw Incomplete("The native image folder classifier returned an unexpected type.");
                return new(imageFolderName, "ImageFolder", null);
            }
            var folders = new NativeV11Folders(session);
            var model = folders.NativeModel(address);
            var moduleType = folders.FolderModuleType(address).ToString()!;
            var extension = session.Context.DesignerAssembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
            var name = NativeV11Reflection.Static(extension, "GetModuleName", model, address.FolderPath) as string;
            if (string.IsNullOrEmpty(name)) throw Incomplete("The native folder owner name is unavailable.");
            return new(name, moduleType, null);
        }
        object node;
        if (address.Kind == ModuleKind.Image)
            node = FindImage(session, address);
        else node = session.Find(address).Model;
        var simple = ClassifyModelCore(session.Context.DesignerAssembly, node);
        return new(Name(simple), TypeName(simple), simple);
    }

    internal static object ClassifyModelCore(Assembly designer, object model)
    {
        var contract = GetContract(designer);
        if (!contract.CreateModel.GetParameters()[0].ParameterType.IsInstanceOfType(model) ||
            model.GetType().Assembly.GetName().Version != designer.GetName().Version)
            throw Incomplete("The model does not belong to the fingerprinted native tree contract.");
        return contract.CreateModel.Invoke(null, [model]) ?? throw Incomplete("The native model classifier returned no owner.");
    }

    internal static NativeV11CollaborationOwner FromExistingCore(Assembly designer, ModuleAddress address,
        object nativeModule, object? classification)
    {
        var contract = GetContract(designer);
        NativeV11Session.ValidateAddress(address);
        if (nativeModule.GetType() != contract.Module || classification is not null &&
            (classification.GetType() != contract.Simple || !SameIdentity(classification, nativeModule)))
            throw Incomplete("The model and native collaboration registration disagree.");
        return new(address, contract.FromModule.Invoke([nativeModule]), "Edit");
    }

    private static IReadOnlyList<string> PrimaryPaths(NativeV11Session session, ModuleAddress address)
    {
        if (address == MapResourceOwner) return [];
        if (SingletonType(address.Kind) is not null)
            return address.Kind == ModuleKind.Plugin ? new NativeV11Plugins(session).SourcePaths(address) : new NativeV11Settings(session).SourcePaths(address.Kind);
        if (address.Kind == ModuleKind.Folder) return IsImageFolderAddress(address)
            ? [address.FolderPath + "/" + address.Name + "/"] : new NativeV11Folders(session).SourcePaths(address);
        if (address.Kind == ModuleKind.Image)
            return ["image/" + (address.FolderPath is { Length: > 0 } folder ? folder + "/" : "") + address.Name];
        var primary = session.NativePath(address);
        return address.Kind switch
        {
            ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage => [primary, Path.ChangeExtension(primary, ".rd").Replace('\\', '/')],
            ModuleKind.Report => [primary, Path.ChangeExtension(primary, ".rdlx").Replace('\\', '/')],
            ModuleKind.BusinessProcess => [primary, Path.ChangeExtension(primary, ".bpmn").Replace('\\', '/')],
            _ => [primary]
        };
    }

    private static string? SingletonType(ModuleKind kind) => kind switch
    {
        ModuleKind.ApplicationSettings or ModuleKind.GlobalVariable or ModuleKind.LanguageResource => "ApplicationSetting",
        ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle => "Theme",
        ModuleKind.CustomLibrary => "CustomLibrary",
        ModuleKind.DataConnection => "DataContectionSet",
        ModuleKind.PermissionGroup => "PermissionGroupConfig",
        ModuleKind.Plugin => "Plugin",
        _ => null
    };
    internal static ModuleAddress? SettingsOwnerAddress(ModuleKind kind) => kind switch
    {
        ModuleKind.ApplicationSettings or ModuleKind.GlobalVariable or ModuleKind.LanguageResource => new(ModuleKind.ApplicationSettings, "ApplicationSettings"),
        ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle => new(ModuleKind.Theme, "Theme"),
        ModuleKind.CustomLibrary => new(ModuleKind.CustomLibrary, "CustomLibraries"),
        ModuleKind.DataConnection => new(ModuleKind.DataConnection, "DataConnectionSet"),
        ModuleKind.PermissionGroup => new(ModuleKind.PermissionGroup, "PermissionGroupConfig"),
        _ => null
    };
    private static readonly ModuleAddress MapResourceOwner = new(ModuleKind.ResourceFile, "Current", "MapJsonFile");
    private static readonly (string Type, ModuleAddress Address)[] SingletonAddresses =
    [
        ("ApplicationSetting", new(ModuleKind.ApplicationSettings, "ApplicationSettings")),
        ("Theme", new(ModuleKind.Theme, "Theme")),
        ("CustomLibrary", new(ModuleKind.CustomLibrary, "CustomLibraries")),
        ("DataContectionSet", new(ModuleKind.DataConnection, "DataConnectionSet")),
        ("PermissionGroupConfig", new(ModuleKind.PermissionGroup, "PermissionGroupConfig")),
        ("MapJsonFile", MapResourceOwner)
    ];

    private static IReadOnlyList<ModuleAddress> ImageAddresses(NativeV11Session session) => Images(session).Select(pair => pair.Address).ToArray();
    internal static object FindImage(NativeV11Session session, ModuleAddress address) =>
        Images(session).SingleOrDefault(pair => pair.Address == address)?.Model ?? throw Incomplete("The requested native image owner is missing.");
    internal sealed record ImageNode(ModuleAddress Address, object Model);
    private static bool IsImageFolderAddress(ModuleAddress address) => address.Kind == ModuleKind.Folder &&
        (address.FolderPath == "image" || address.FolderPath?.StartsWith("image/", StringComparison.Ordinal) == true);
    private static IReadOnlyList<ImageNode> ImageFolders(NativeV11Session session)
    {
        var manager = NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!;
        var colleague = NativeV11Reflection.Get(manager, "ImageModel") ?? throw Incomplete("The native image colleague is unavailable.");
        var root = NativeV11Reflection.Get(colleague, "Root") ?? throw Incomplete("The native image root is unavailable.");
        return ImageFoldersCore(root);
    }
    internal static IReadOnlyList<ImageNode> ImageFoldersCore(object root)
    {
        var result = new List<ImageNode>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object folder, string parent)
        {
            if (!seen.Add(folder) || NativeV11Reflection.Get(folder, "Loaded") is not true || NativeV11Reflection.Get(folder, "Items") is not IList children)
                throw Incomplete("The native image folder tree is incomplete or cyclic.");
            foreach (var child in children.Cast<object>().Where(child => NativeV11Reflection.Get(child, "Items") is IList))
            {
                if (child.GetType().FullName != "Forguncy.HelpForm.ImageFolder") throw Incomplete("The native image tree contains an unknown folder type.");
                var name = NativeV11Reflection.Get(child, "Name") as string ?? throw Incomplete("A native image folder has no name.");
                var address = new ModuleAddress(ModuleKind.Folder, name, parent);
                NativeV11Session.ValidateAddress(address);
                result.Add(new(address, child));
                Walk(child, parent + "/" + name);
            }
        }
        Walk(root, "image");
        return result;
    }
    private static IReadOnlyList<ImageNode> Images(NativeV11Session session)
    {
        var manager = NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!;
        var colleague = NativeV11Reflection.Get(manager, "ImageModel") ?? throw Incomplete("The native image colleague is unavailable.");
        var root = NativeV11Reflection.Get(colleague, "Root") ?? throw Incomplete("The native image root is unavailable.");
        var result = new List<ImageNode>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object folder, string prefix)
        {
            if (!seen.Add(folder) || NativeV11Reflection.Get(folder, "Loaded") is not true || NativeV11Reflection.Get(folder, "Items") is not IList children)
                throw Incomplete("The native image tree is incomplete or cyclic.");
            foreach (var child in children.Cast<object>())
            {
                var name = NativeV11Reflection.Get(child, "Name") as string ?? throw Incomplete("A native image node has no name.");
                NativeV11Session.ValidateAddress(new(ModuleKind.Image, name));
                if (NativeV11Reflection.Get(child, "Items") is IList) Walk(child, prefix + name + "/");
                else if (child.GetType().FullName == "Forguncy.HelpForm.ImageItem" && seen.Add(child))
                    result.Add(new(new(ModuleKind.Image, name, prefix.Length == 0 ? null : prefix.TrimEnd('/')), child));
                else throw Incomplete("The native image tree contains an unknown node.");
            }
        }
        Walk(root, "");
        return result;
    }

    private static string NormalizeFile(string root, string raw, bool allowDatabasePath = false)
    {
        if (string.IsNullOrWhiteSpace(raw) || Path.IsPathRooted(raw) || raw.Contains(':'))
            throw Incomplete("A native owner file lies outside its relative project namespace.");
        var path = raw.Replace('\\', '/').TrimEnd('/');
        if (path.Split('/').Any(part => part.Length == 0 || part is "." or ".."))
            throw Incomplete("A native owner file has an unsafe relative path.");
        var first = path.Split('/')[0];
        if (new[] { ".git", ".collaboration", ".local", "LocalData", "LastSaveFiles" }.Contains(first, StringComparer.OrdinalIgnoreCase))
            throw Incomplete("A native owner file crosses a repository-control or cache boundary.");
        var leaf = Path.GetFileName(path);
        if (!allowDatabasePath && (leaf.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
            leaf.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || leaf.EndsWith("-shm", StringComparison.OrdinalIgnoreCase)))
            throw Incomplete("The native owner group includes a database file; database checkout is not enabled.");
        var full = NativeV11Session.Under(root, path);
        return Directory.Exists(full) || raw.EndsWith('/') || raw.EndsWith('\\') ? path + "/" : path;
    }
    private static bool IsDatabasePath(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
    }

    private static IDictionary ModuleMap(Contract contract, object manager) => contract.Modules.GetValue(manager) as IDictionary
        ?? throw Incomplete("The native module registry is unavailable.");
    private static object ExistingModule(Contract contract, IDictionary modules, string name, string type)
    {
        var selected = new List<DictionaryEntry>();
        foreach (DictionaryEntry entry in modules)
            if (entry.Key is string key && key.Equals(name, StringComparison.OrdinalIgnoreCase)) selected.Add(entry);
        var matches = selected.ToArray();
        if (matches.Length != 1 || matches[0].Key is not string exact || exact != name || matches[0].Value is not object module)
            throw Incomplete("The existing native module registration is missing or ambiguous.");
        ValidateModule(contract, exact, module);
        if (TypeName(module) != type) throw Incomplete("The native module type does not match its loaded model.");
        return module;
    }
    private static void ValidateModule(Contract contract, object key, object module)
    {
        if (module.GetType() != contract.Module || key is not string text || text != Name(module))
            throw Incomplete("The native module registry has an invalid key or component identity.");
    }
    private static bool SameIdentity(object left, object right) => Name(left) == Name(right) && TypeName(left) == TypeName(right) &&
        Equals(NativeV11Reflection.Get(left, "IconType"), NativeV11Reflection.Get(right, "IconType")) &&
        Equals(NativeV11Reflection.Get(left, "ExclusiveType"), NativeV11Reflection.Get(right, "ExclusiveType"));
    private static string Name(object module) => NativeV11Reflection.Get(module, "ModuleName") as string
        ?? throw Incomplete("A native owner has no module name.");
    private static string TypeName(object module) => NativeV11Reflection.Get(module, "ModuleType")?.ToString()
        ?? throw Incomplete("A native owner has no module type.");

    private static Contract GetContract(Assembly designer) => Contracts.GetValue(designer, assembly =>
    {
        var profile = V11BuildProfile.Resolve(assembly);
        var simple = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var manager = assembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        var from = simple.GetConstructors(NativeV11Reflection.Flags).Single(ctor => ctor.GetParameters().Length == 1 && ctor.GetParameters()[0].ParameterType.FullName == "Forguncy.Collaboration.Module");
        var module = from.GetParameters()[0].ParameterType;
        var expected = profile.Version == "11.0.104.0" ? "228DD755C5607955D06F22751A04FD97ECB30095B54F23BBF468E078762B38F5" : "FAC1A08973E9B81B5C4D32B44C661B129D7FE8D50FE84A9072B6465022E611F8";
        if (module.Assembly.GetName().Name != "Forguncy.Collaboration" || module.Assembly.GetName().Version?.ToString() != profile.Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.Assembly.Location))) != expected)
            throw Incomplete("The native collaboration component fingerprint changed.");
        var create = simple.GetMethods(NativeV11Reflection.Flags).Single(method => method.Name == "Create" && method.IsStatic &&
            method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.FullName == "Forguncy.Model.TreeNodeModelBase");
        var direct = assembly.GetType("Forguncy.Common.ModuleExtensions", true)!.GetMethod("GetReferFilesFromWorkingDirectory", NativeV11Reflection.Flags, null, [module], null)
            ?? throw Incomplete("The exact native working-directory file provider is unavailable.");
        var modules = manager.GetField("<Modules>k__BackingField", NativeV11Reflection.Flags) ?? throw Incomplete("The native module registry field is unavailable.");
        var tracked = manager.GetField("<SaveLoadTrackedFiles>k__BackingField", NativeV11Reflection.Flags) ?? throw Incomplete("The native tracked-file registry field is unavailable.");
        return new(simple, module, from, create, direct, modules, tracked);
    });

    private static BridgeException Incomplete(string message) => new("NativeCollaborationOwnerIncomplete", message);
}
