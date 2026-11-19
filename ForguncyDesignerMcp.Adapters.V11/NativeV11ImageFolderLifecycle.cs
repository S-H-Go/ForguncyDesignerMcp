using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Native image folders are not JSON model routes. Callers must first freeze
// source/Last/Local tree, references and all real native collaboration owners;
// this component performs the actual image-manager move and guarded rollback.
internal static class NativeV11ImageFolderLifecycle
{
    // Main/Collab can pass its independently resolved, registered native owners
    // to this entry point; no synthetic JSON route or detached owner is used.
    internal sealed record UpdatePlan(ModuleAddress OldAddress, ModuleAddress Destination,
        NativeV11FolderInventory.MutationPlan Owners, NativeV11FolderMoveJournal Journal, MovePlan Move);
    internal sealed record DeletePlan(NativeV11Session Session, ModuleMutationRequest Request,
        NativeV11FolderInventory.MutationPlan Owners, NativeV11FolderDeleteJournal Journal,
        IReadOnlyList<ModuleAddress> Folders, IReadOnlyList<ModuleAddress> Images);

    internal static bool Supports(ModuleAddress address) => address.Kind == ModuleKind.Folder &&
        address.FolderPath is string path && (path == "image" || path.StartsWith("image/", StringComparison.Ordinal));

    internal static object Model(NativeV11Session session, ModuleAddress address)
    {
        if (!Supports(address)) throw new InvalidDataException("Not an image folder address.");
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        var colleague = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!, "ImageModel")!;
        if (!ReferenceEquals(root, NativeV11Reflection.Get(colleague, "Root"))) throw new InvalidOperationException("The image tree is not bound to its native colleague.");
        object node = root;
        foreach (var part in (address.FolderPath + "/" + address.Name)["image/".Length..].Split('/'))
        {
            V11Safety.Address(new(ModuleKind.Folder, part));
            if (NativeV11Reflection.Get(node, "Loaded") is not true || NativeV11Reflection.Get(node, "Items") is not IList children)
                throw new InvalidOperationException("The native image tree is not fully loaded.");
            node = children.Cast<object>().Single(child => child.GetType() == root.GetType() && NativeV11Reflection.Get(child, "Name") as string == part);
        }
        if (NativeV11Reflection.Get(node, "Loaded") is not true) throw new InvalidOperationException("The native image folder is not fully loaded.");
        return node;
    }

    internal static IReadOnlyList<ModuleDocument> List(NativeV11Session session)
    {
        session.VerifyContext();
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        var colleague = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!, "ImageModel")!;
        if (!ReferenceEquals(root, NativeV11Reflection.Get(colleague, "Root"))) throw new InvalidOperationException("The image tree is not bound to its native colleague.");
        var result = new List<ModuleDocument>(); var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object folder, string path)
        {
            if (!seen.Add(folder) || NativeV11Reflection.Get(folder, "Loaded") is not true || NativeV11Reflection.Get(folder, "Items") is not IList children)
                throw new InvalidOperationException("The native image tree is incomplete or cyclic.");
            foreach (var child in children.Cast<object>().Where(child => child.GetType() == root.GetType()))
            {
                var name = NativeV11Reflection.Get(child, "Name") as string ?? throw new InvalidDataException("An image folder has no name.");
                V11Safety.Address(new(ModuleKind.Folder, name));
                var address = new ModuleAddress(ModuleKind.Folder, name, path);
                result.Add(Document(session, address, child)); Walk(child, path + "/" + name);
            }
        }
        Walk(root, "image"); session.VerifyContext(); return result;
    }

    internal static ModuleDocument Read(NativeV11Session session, ModuleAddress address) => Document(session, address, Model(session, address));
    internal static ModuleAddress ResultAddress(ModuleMutationRequest request)
    {
        if (request.Operation != ModuleOperation.Update) return request.Address;
        var metadata = request.Document?.Metadata ?? throw new InvalidDataException("Image folder metadata is required.");
        var name = metadata.GetProperty("Name").GetString() ?? throw new InvalidDataException("Image folder name is required.");
        V11Safety.Address(new(ModuleKind.Folder, name));
        var parent = request.Address.FolderPath!;
        if (metadata.TryGetProperty("ParentFolderPath", out var value))
        {
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException("The destination parent is invalid.");
            var relative = NativeV11Session.NormalizeFolder(value.GetString());
            parent = "image" + (relative.Length == 0 ? "" : "/" + relative);
        }
        return new(ModuleKind.Folder, name, parent);
    }
    internal static string Marker(ModuleAddress address)
    {
        if (!Supports(address)) throw new InvalidDataException("Not an image folder address.");
        foreach (var segment in (address.FolderPath + "/" + address.Name).Split('/')) V11Safety.Address(new(ModuleKind.Folder, segment));
        return address.FolderPath + "/" + address.Name + "/";
    }
    internal static void ValidateEmpty(NativeV11Session session, ModuleMutationRequest request)
    {
        session.VerifyContext(); NativeV11StaticFiles.VerifyConfigOwner(session);
        if (!Supports(request.Address) || request.Scope != EffectScope.ProjectDefinition || request.Operation is not (ModuleOperation.Create or ModuleOperation.Delete))
            throw new InvalidDataException("An image folder create/delete request is required.");
        var marker = Marker(request.Address);
        var items = List(session);
        var exists = items.Any(item => item.Address == request.Address);
        if (exists != (request.Operation == ModuleOperation.Delete)) throw new InvalidOperationException("The image folder existence changed.");
        if (request.Operation == ModuleOperation.Create)
        {
            var document = request.Document ?? throw new InvalidDataException("Image folder metadata is required.");
            if (document.Address != request.Address || document.Companions.Count != 0 || document.Metadata.ValueKind != JsonValueKind.Object ||
                document.Metadata.EnumerateObject().Any(p => p.Name is not ("OwnerKind" or "Name")) ||
                document.Metadata.GetProperty("OwnerKind").GetString() != "Image" || document.Metadata.GetProperty("Name").GetString() != request.Address.Name)
                throw new InvalidDataException("Image folder creation accepts only Image owner and exact name.");
            if (request.Address.FolderPath != "image")
            {
                var path = request.Address.FolderPath!; var slash = path.LastIndexOf('/');
                _ = Model(session, new(ModuleKind.Folder, path[(slash + 1)..], path[..slash]));
            }
            if (items.Any(item => item.Address.FolderPath == request.Address.FolderPath && item.Address.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("An image sibling already has this name.");
            if (new[] { session.LocalPath, session.LastPath, session.SourcePath }.Any(root => NativeV11Folders.SourceState(root, marker).Exists))
                throw new InvalidOperationException("The image folder destination already exists.");
        }
        else
        {
            if (request.Document is not null) throw new InvalidDataException("Image folder deletion does not accept metadata.");
            var model = Model(session, request.Address);
            if (((IList)NativeV11Reflection.Get(model, "Items")!).Count != 0) throw new NotSupportedException("A nonempty image folder needs a complete descendant deletion plan.");
            foreach (var root in new[] { session.LocalPath, session.LastPath, session.SourcePath })
            {
                var state = NativeV11Folders.SourceState(root, marker);
                if (!state.Exists || !state.Empty) throw new InvalidOperationException("The image folder is not an exact saved empty directory.");
            }
            var references = NativeV11References.Find(session, request.Address, model);
            if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException("The native image folder reference scan is incomplete or has references.");
        }
        var manager = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.ImageManager", true)!, "get_Instance")!;
        var managerPath = NativeV11Reflection.Get(manager, "LocalImagesPath") as string;
        if (managerPath is null || !Path.GetFullPath(managerPath).TrimEnd('\\', '/').Equals(Path.GetFullPath(V11Safety.Under(session.LocalPath, "image")).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The native image manager belongs to a different document.");
    }

    internal static Action ApplyEmpty(NativeV11Session session, ModuleMutationRequest request)
    {
        ValidateEmpty(session, request);
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        object parent = root;
        if (request.Address.FolderPath != "image")
        {
            var path = request.Address.FolderPath!; var slash = path.LastIndexOf('/');
            parent = Model(session, new(ModuleKind.Folder, path[(slash + 1)..], path[..slash]));
        }
        var children = (IList)NativeV11Reflection.Get(parent, "Items")!;
        var model = request.Operation == ModuleOperation.Delete ? Model(session, request.Address) : Activator.CreateInstance(root.GetType())!;
        if (request.Operation == ModuleOperation.Create) { NativeV11Reflection.Set(model, "Name", request.Address.Name); NativeV11Reflection.Set(model, "Loaded", true); }
        var index = request.Operation == ModuleOperation.Delete ? children.IndexOf(model) : children.Count;
        var manager = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.ImageManager", true)!, "get_Instance")!;
        var pathLocal = V11Safety.Under(session.LocalPath, Marker(request.Address).TrimEnd('/'));
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true; var restored = false;
        void Undo()
        {
            if (restored) return;
            session.VerifyContext();
            if (request.Operation == ModuleOperation.Create)
            {
                if (Directory.Exists(pathLocal) && Directory.EnumerateFileSystemEntries(pathLocal).Any()) throw new InvalidOperationException("The new image folder gained content before rollback.");
                if (children.Contains(model)) { if (Directory.Exists(pathLocal)) NativeV11Reflection.Call(manager, "DeleteFolder", model); children.Remove(model); }
                else if (Directory.Exists(pathLocal)) throw new InvalidOperationException("The new image folder lost its model before rollback.");
            }
            else
            {
                if (Directory.Exists(pathLocal) && Directory.EnumerateFileSystemEntries(pathLocal).Any()) throw new InvalidOperationException("The deleted image folder path gained content before rollback.");
                if (!children.Contains(model)) children.Insert(Math.Min(index, children.Count), model);
                if (!Directory.Exists(pathLocal)) NativeV11Reflection.Call(manager, "NewFolder", model);
            }
            NativeV11Generation.Mark(session, "ImageFolder"); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty); restored = true;
        }
        try
        {
            if (request.Operation == ModuleOperation.Create) { children.Add(model); NativeV11Reflection.Call(manager, "NewFolder", model); }
            else { NativeV11Reflection.Call(manager, "DeleteFolder", model); children.Remove(model); }
            if (Directory.Exists(pathLocal) != (request.Operation == ModuleOperation.Create) || children.Contains(model) != (request.Operation == ModuleOperation.Create))
                throw new InvalidOperationException("Native image folder creation/deletion was incomplete.");
            NativeV11Generation.Mark(session, "ImageFolder"); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true); return Undo;
        }
        catch (Exception primary) { try { Undo(); } catch (Exception rollback) { throw new AggregateException("Image folder mutation and rollback failed.", primary, rollback); } throw; }
    }

    internal static IReadOnlyList<(object Simple, string State)> CollectEmpty(NativeV11Session session, ModuleMutationRequest request)
    {
        ValidateEmpty(session, request);
        var designer = session.Context.DesignerAssembly;
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        object node = root; object view = ImageView(designer, null, root);
        foreach (var part in request.Address.FolderPath!["image".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = ((IList)NativeV11Reflection.Get(node, "Items")!).Cast<object>().Single(child => child.GetType() == root.GetType() && NativeV11Reflection.Get(child, "Name") as string == part);
            view = ImageView(designer, view, node);
        }
        object executor;
        if (request.Operation == ModuleOperation.Create)
        {
            var model = Activator.CreateInstance(root.GetType())!; NativeV11Reflection.Set(model, "Name", request.Address.Name);
            var classification = NativeV11CollaborationOwners.ClassifyModelCore(designer, model);
            var executorType = designer.GetType("Forguncy.Views.ObjectViews.CreateItemOrFolderExecutor", true)!;
            var newer = V11BuildProfile.Resolve(designer).Version == "11.0.104.0";
            var ctor = executorType.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == (newer ? 6 : 5));
            var moduleType = NativeV11Reflection.Get(classification, "ModuleType");
            var arguments = newer ? new object?[] { null, view, request.Address.Name, moduleType, NativeV11Reflection.Get(classification, "IconType"), NativeV11Reflection.Get(classification, "ExclusiveType") }
                : new object?[] { null, view, request.Address.Name, moduleType, NativeV11Reflection.Get(classification, "ExclusiveType") };
            executor = ctor.Invoke(arguments);
        }
        else
        {
            var target = ImageView(designer, view, Model(session, request.Address));
            var selected = Array.CreateInstance(designer.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!, 1); selected.SetValue(target, 0);
            executor = NativeV11Reflection.Call(target, "DeleteItemsCollaborationExecutor", selected)!;
        }
        return ((IEnumerable)NativeV11Reflection.Call(executor, "GetRelatedModules")!).Cast<object>().Select(value =>
            (value.GetType().GetField("Item1")!.GetValue(value)!, value.GetType().GetField("Item2")!.GetValue(value)!.ToString()!)).ToArray();
    }

    internal static DeletePlan PrepareDelete(NativeV11Session session, ModuleMutationRequest request,
        IReadOnlyList<NativeV11CollaborationOwner>? existingOwners)
    {
        if (!Supports(request.Address) || request.Operation != ModuleOperation.Delete || request.Scope != EffectScope.ProjectDefinition || request.Document is not null)
            throw new InvalidDataException("An image folder deletion request is required.");
        var addresses = ExistingAddresses(session, request.Address);
        var folders = addresses.Where(value => value.Kind == ModuleKind.Folder).ToArray();
        var images = addresses.Where(value => value.Kind == ModuleKind.Image).ToArray();
        if (addresses.Count <= 1) throw new InvalidOperationException("Grouped deletion requires a nonempty image subtree.");
        var resources = new NativeV11Resources(session);
        var imageModels = images.Select(address => (Address: address, Model: NativeV11CollaborationOwners.FindImage(session, address))).ToArray();
        foreach (var image in images) resources.Validate(request with { Address = image });
        var designer = session.Context.DesignerAssembly;
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        object node = root; object view = ImageView(designer, null, node);
        foreach (var part in request.Address.FolderPath!["image".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            node = ((IList)NativeV11Reflection.Get(node, "Items")!).Cast<object>().Single(child => child.GetType() == root.GetType() && NativeV11Reflection.Get(child, "Name") as string == part);
            view = ImageView(designer, view, node);
        }
        var target = ImageView(designer, view, Model(session, request.Address));
        var selected = Array.CreateInstance(designer.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!, 1); selected.SetValue(target, 0);
        var executor = NativeV11Reflection.Call(target, "DeleteItemsCollaborationExecutor", selected)!;
        var changes = ((IEnumerable)NativeV11Reflection.Call(executor, "GetRelatedModules")!).Cast<object>().Select(value =>
            (value.GetType().GetField("Item1")!.GetValue(value)!, value.GetType().GetField("Item2")!.GetValue(value)!.ToString()!)).ToArray();
        if (existingOwners is null)
        {
            if (session.Context.CaptureSnapshot().InCollaborationMode) throw new InvalidOperationException("A collaboration deletion requires registered owners.");
            existingOwners = addresses.Select(address =>
            {
                var name = address.Kind == ModuleKind.Folder ? Marker(address).TrimEnd('/') : "Image/" + address.Name;
                var type = address.Kind == ModuleKind.Folder ? "ImageFolder" : "Image";
                var simple = changes.Single(value => NativeV11Reflection.Get(value.Item1, "ModuleName") as string == name &&
                    NativeV11Reflection.Get(value.Item1, "ModuleType")?.ToString() == type && value.Item2 == "Remove").Item1;
                return new NativeV11CollaborationOwner(address, simple, "Edit");
            }).ToArray();
        }
        if (existingOwners.Count != addresses.Count || !existingOwners.Select(owner => owner.Address).ToHashSet().SetEquals(addresses))
            throw new InvalidOperationException("The image subtree owner inventory is incomplete.");
        var dirs = folders.Select(address => Marker(address).TrimEnd('/')).ToArray();
        var files = images.Select(address => resources.SourcePaths(address).Single()).ToArray();
        var inventory = new NativeV11FolderInventory(Marker(request.Address).TrimEnd('/'), folders, [], existingOwners, dirs, files);
        var owners = inventory.Project(request, request.Address, changes);
        var journal = NativeV11FolderDeleteJournal.CaptureImages(session, inventory, imageModels);
        return new(session, request, owners, journal, folders, images);
    }

    internal static Action ApplyDelete(DeletePlan plan)
    {
        var session = plan.Session;
        if (!plan.Journal.VerifyOriginal(session)) throw new InvalidOperationException("The image folder source changed before deletion.");
        var resource = new NativeV11Resources(session);
        foreach (var image in plan.Images) resource.Validate(plan.Request with { Address = image });
        var undo = new List<Action>(); var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true; var restored = false;
        void Restore()
        {
            if (restored) return;
            var errors = new List<Exception>();
            for (var i = undo.Count - 1; i >= 0; i--) try { undo[i](); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Image folder deletion rollback is incomplete.", errors);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty); restored = true;
        }
        try
        {
            foreach (var image in plan.Images) undo.Add(resource.Apply(plan.Request with { Address = image }));
            foreach (var address in plan.Folders.OrderByDescending(value => Marker(value).Count(c => c == '/')))
                undo.Add(RemoveReviewedEmpty(session, address));
            plan.Journal.VerifyMemory(session); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true); return Restore;
        }
        catch (Exception primary) { try { Restore(); } catch (Exception rollback) { throw new AggregateException("Image folder deletion and rollback failed.", primary, rollback); } throw; }
    }

    private static Action RemoveReviewedEmpty(NativeV11Session session, ModuleAddress address)
    {
        var model = Model(session, address); var path = address.FolderPath!;
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        var slash = path.LastIndexOf('/');
        var parent = path == "image" ? root : Model(session, new(ModuleKind.Folder, path[(slash + 1)..], path[..slash]));
        var children = (IList)NativeV11Reflection.Get(parent, "Items")!; var index = children.IndexOf(model);
        var local = V11Safety.Under(session.LocalPath, Marker(address).TrimEnd('/'));
        if (((IList)NativeV11Reflection.Get(model, "Items")!).Count != 0 || !Directory.Exists(local) || Directory.EnumerateFileSystemEntries(local).Any())
            throw new InvalidOperationException("A reviewed image folder is not empty at deletion.");
        var manager = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.ImageManager", true)!, "get_Instance")!;
        var restored = false;
        void Restore()
        {
            if (restored) return;
            if (Directory.Exists(local) && Directory.EnumerateFileSystemEntries(local).Any() ||
                children.Cast<object>().Any(child => !ReferenceEquals(child, model) && NativeV11Reflection.Get(child, "Name") as string == address.Name))
                throw new InvalidOperationException("The deleted folder was replaced before rollback.");
            if (!children.Contains(model)) children.Insert(Math.Min(index, children.Count), model);
            if (!Directory.Exists(local)) NativeV11Reflection.Call(manager, "NewFolder", model);
            if (!Directory.Exists(local)) throw new InvalidOperationException("The image folder rollback was incomplete.");
            NativeV11Generation.Mark(session, "ImageFolder"); restored = true;
        }
        try
        {
            NativeV11Reflection.Call(manager, "DeleteFolder", model); children.Remove(model);
            if (Directory.Exists(local) || children.Contains(model)) throw new InvalidOperationException("The reviewed image folder deletion was incomplete.");
            NativeV11Generation.Mark(session, "ImageFolder"); return Restore;
        }
        catch (Exception primary) { try { Restore(); } catch (Exception rollback) { throw new AggregateException("Image folder delete and rollback failed.", primary, rollback); } throw; }
    }
    private static ModuleDocument Document(NativeV11Session session, ModuleAddress address, object model)
    {
        var metadata = JsonSerializer.SerializeToElement(new { OwnerKind = "Image", Name = address.Name,
            Weight = Convert.ToInt32(NativeV11Reflection.Get(model, "Weight")),
            ParentFolderPath = address.FolderPath!["image".Length..].TrimStart('/'),
            ChildCount = ((IList)NativeV11Reflection.Get(model, "Items")!).Count, Loaded = true,
            NativeType = model.GetType().FullName, NativeId = NativeV11Reflection.Get(model, "ID") ?? NativeV11Reflection.Get(model, "Id") });
        return new(address, metadata, [], NativeV11Session.Hash(Encoding.UTF8.GetBytes(metadata.GetRawText() + "\n" + address.FolderPath + "/" + address.Name)));
    }

    // Gives Collab the exact addresses it must resolve against its registered
    // native module map before PrepareUpdate is allowed to build the plan.
    internal static IReadOnlyList<ModuleAddress> ExistingAddresses(NativeV11Session session, ModuleAddress address)
    {
        session.VerifyContext();
        if (!Supports(address)) throw new InvalidDataException("Not an image folder address.");
        var type = session.Context.DesignerAssembly.GetType("Forguncy.HelpForm.ImageFolder", true)!;
        object node = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        foreach (var part in (address.FolderPath + "/" + address.Name)["image/".Length..].Split('/'))
        {
            V11Safety.Address(new(ModuleKind.Folder, part));
            if (NativeV11Reflection.Get(node, "Loaded") is not true || NativeV11Reflection.Get(node, "Items") is not IList children)
                throw new InvalidOperationException("The native image folder tree is not loaded.");
            node = children.Cast<object>().Single(child => child.GetType() == type && NativeV11Reflection.Get(child, "Name") as string == part);
        }
        var result = new List<ModuleAddress>(); var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object folder, string path)
        {
            if (!seen.Add(folder) || NativeV11Reflection.Get(folder, "Loaded") is not true || NativeV11Reflection.Get(folder, "Items") is not IList children)
                throw new InvalidOperationException("The native image subtree is incomplete or cyclic.");
            var slash = path.LastIndexOf('/'); result.Add(new(ModuleKind.Folder, path[(slash + 1)..], path[..slash]));
            foreach (var child in children.Cast<object>())
            {
                var name = NativeV11Reflection.Get(child, "Name") as string ?? throw new InvalidDataException("An image node has no name.");
                V11Safety.Address(new(ModuleKind.Image, name));
                if (child.GetType() == type) Visit(child, path + "/" + name);
                else if (child.GetType().FullName == "Forguncy.HelpForm.ImageItem" && seen.Add(child))
                    result.Add(new(ModuleKind.Image, name, path["image/".Length..]));
                else throw new InvalidDataException("The native image subtree contains an unknown or duplicate node.");
            }
        }
        Visit(node, address.FolderPath + "/" + address.Name);
        session.VerifyContext(); return result;
    }

    internal static UpdatePlan PrepareUpdate(NativeV11Session session, ModuleMutationRequest request,
        IReadOnlyList<NativeV11CollaborationOwner>? existingOwners)
    {
        session.VerifyContext();
        if (request.Address.Kind != ModuleKind.Folder || request.Operation != ModuleOperation.Update ||
            request.Scope != EffectScope.ProjectDefinition || request.Document is null || request.Document.Address != request.Address ||
            request.Document.Companions.Count != 0 || request.Document.Metadata.ValueKind != JsonValueKind.Object ||
            request.Address.FolderPath is not string parentPath || !(parentPath == "image" || parentPath.StartsWith("image/", StringComparison.Ordinal)))
            throw new InvalidDataException("An image folder update requires the exact image-root project definition address.");
        var metadata = request.Document.Metadata;
        var allowed = new HashSet<string>(["OwnerKind", "Name", "ParentFolderPath", "ChildCount", "Loaded", "NativeType", "NativeId", "Weight"], StringComparer.Ordinal);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadata.EnumerateObject())
            if (!allowed.Contains(property.Name) || !keys.Add(property.Name)) throw new InvalidDataException("Unknown or ambiguous image folder metadata.");
        if (!metadata.TryGetProperty("OwnerKind", out var owner) || owner.GetString() != "Image" ||
            !metadata.TryGetProperty("Name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Image folder owner and destination name are required.");
        var name = nameValue.GetString()!; V11Safety.Address(new(ModuleKind.Folder, name));
        var targetParent = parentPath;
        if (metadata.TryGetProperty("ParentFolderPath", out var parent))
        {
            if (parent.ValueKind != JsonValueKind.String) throw new InvalidDataException("The destination parent must be a relative image folder path.");
            var relative = NativeV11Session.NormalizeFolder(parent.GetString());
            targetParent = "image" + (relative.Length == 0 ? "" : "/" + relative);
        }
        var destination = new ModuleAddress(ModuleKind.Folder, name, targetParent);
        var designer = session.Context.DesignerAssembly;
        var folderType = designer.GetType("Forguncy.HelpForm.ImageFolder", true)!;
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        object Parent(string path)
        {
            object node = root;
            foreach (var part in path["image".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (NativeV11Reflection.Get(node, "Loaded") is not true || NativeV11Reflection.Get(node, "Items") is not IList children)
                    throw new InvalidOperationException("The native image tree is not loaded.");
                node = children.Cast<object>().Single(child => child.GetType() == folderType && NativeV11Reflection.Get(child, "Name") as string == part);
            }
            return node;
        }
        var oldParent = Parent(parentPath); var newParent = Parent(targetParent);
        var folder = ((IList)NativeV11Reflection.Get(oldParent, "Items")!).Cast<object>().Single(child =>
            child.GetType() == folderType && NativeV11Reflection.Get(child, "Name") as string == request.Address.Name);
        if (metadata.TryGetProperty("ChildCount", out var count) && count.GetInt32() != ((IList)NativeV11Reflection.Get(folder, "Items")!).Count ||
            metadata.TryGetProperty("Loaded", out var loaded) && loaded.ValueKind != JsonValueKind.True ||
            metadata.TryGetProperty("NativeType", out var type) && type.GetString() != folder.GetType().FullName ||
            metadata.TryGetProperty("NativeId", out var id) && !Equals(id.ValueKind == JsonValueKind.Null ? null : id.GetString(), NativeV11Reflection.Get(folder, "ID") ?? NativeV11Reflection.Get(folder, "Id")) ||
            metadata.TryGetProperty("Weight", out var weight) && weight.GetInt32() != Convert.ToInt32(NativeV11Reflection.Get(folder, "Weight")))
            throw new InvalidDataException("The supplied native image folder state changed.");
        var oldRoot = parentPath + "/" + request.Address.Name; var newRoot = targetParent + "/" + name;
        var directories = new List<string>(); var files = new List<string>(); var addresses = new List<ModuleAddress>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object node, string path)
        {
            if (!seen.Add(node) || NativeV11Reflection.Get(node, "Loaded") is not true || NativeV11Reflection.Get(node, "Items") is not IList children)
                throw new InvalidOperationException("The native image subtree is incomplete or cyclic.");
            directories.Add(path);
            var split = path.LastIndexOf('/'); addresses.Add(new(ModuleKind.Folder, path[(split + 1)..], path[..split]));
            foreach (var child in children.Cast<object>())
            {
                var childName = NativeV11Reflection.Get(child, "Name") as string ?? throw new InvalidDataException("Image tree node has no name.");
                V11Safety.Address(new(ModuleKind.Image, childName));
                if (child.GetType() == folderType) Walk(child, path + "/" + childName);
                else if (child.GetType().FullName == "Forguncy.HelpForm.ImageItem" && seen.Add(child))
                {
                    files.Add(path + "/" + childName);
                    var imageParent = path == "image" ? null : path["image/".Length..];
                    addresses.Add(new(ModuleKind.Image, childName, imageParent));
                }
                else throw new InvalidDataException("The native image subtree contains an unknown or duplicate node.");
            }
        }
        Walk(folder, oldRoot);
        object View(string path)
        {
            object node = root; object view = ImageView(designer, null, node);
            foreach (var part in path["image".Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                node = ((IList)NativeV11Reflection.Get(node, "Items")!).Cast<object>().Single(child => child.GetType() == folderType && NativeV11Reflection.Get(child, "Name") as string == part);
                view = ImageView(designer, view, node);
            }
            return view;
        }
        var targetView = ImageView(designer, View(parentPath), folder);
        object executor;
        if (targetParent == parentPath) executor = NativeV11Reflection.Call(targetView, "RenameCollaborationExecutor", name)!;
        else
        {
            var selected = Array.CreateInstance(designer.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!, 1); selected.SetValue(targetView, 0);
            executor = Activator.CreateInstance(designer.GetType("Forguncy.Views.ObjectViews.MoveItemOrFolderExecutor", true)!, [null, selected, View(targetParent)])!;
        }
        var changes = ((IEnumerable)NativeV11Reflection.Call(executor, "GetRelatedModules")!).Cast<object>().Select(value =>
            (value.GetType().GetField("Item1")!.GetValue(value)!, value.GetType().GetField("Item2")!.GetValue(value)!.ToString()!)).ToArray();
        if (existingOwners is null)
        {
            if (session.Context.CaptureSnapshot().InCollaborationMode) throw new InvalidOperationException("A collaboration image folder requires registered owners.");
            existingOwners = addresses.Select(address =>
            {
                var expectedName = address.Kind == ModuleKind.Folder ? Marker(address).TrimEnd('/') : "Image/" + address.Name;
                var expectedType = address.Kind == ModuleKind.Folder ? "ImageFolder" : "Image";
                var simple = changes.Single(value => NativeV11Reflection.Get(value.Item1, "ModuleName") as string == expectedName &&
                    NativeV11Reflection.Get(value.Item1, "ModuleType")?.ToString() == expectedType && value.Item2 == (address.Kind == ModuleKind.Folder ? "Remove" : "Move")).Item1;
                return new NativeV11CollaborationOwner(address, simple, "Edit");
            }).ToArray();
        }
        if (existingOwners.Count != addresses.Count || !existingOwners.Select(value => value.Address).ToHashSet().SetEquals(addresses))
            throw new InvalidOperationException("The owner inventory does not exactly cover the native image subtree.");
        var inventory = new NativeV11FolderInventory(oldRoot, addresses.Where(a => a.Kind == ModuleKind.Folder).ToArray(), [], existingOwners, directories, files);
        var owners = inventory.Project(request, destination, changes);
        var journal = NativeV11FolderMoveJournal.Capture(session.LocalPath, session.LastPath, session.SourcePath, oldRoot, newRoot, directories, files, true);
        journal.BindImageAddresses(addresses);
        if (session.Context.CaptureSnapshot().InCollaborationMode && !journal.AllDirectoriesRepresentedInGit)
            throw new InvalidOperationException("The image subtree contains an empty directory that Git cannot preserve.");
        var move = PrepareMove(session, folder, oldParent, newParent, name, directories, files);
        return new(request.Address, destination, owners, journal, move);
    }

    internal static Action ApplyUpdate(UpdatePlan plan)
    {
        var session = plan.Move.Session;
        if (!plan.Journal.VerifyOriginal(session.LocalPath, session.LastPath, session.SourcePath))
            throw new InvalidOperationException("The reviewed image folder source changed before mutation.");
        return Move(plan.Move);
    }

    internal static void VerifyMemory(UpdatePlan plan)
    {
        var move = plan.Move; var session = move.Session; session.VerifyContext();
        var parent = (IList)NativeV11Reflection.Get(move.NewParent, "Items")!;
        if (!parent.Contains(move.Folder) || NativeV11Reflection.Get(move.Folder, "Name") as string != move.NewName ||
            !ReferenceEquals(move.OldParent, move.NewParent) && ((IList)NativeV11Reflection.Get(move.OldParent, "Items")!).Contains(move.Folder) ||
            Directory.Exists(move.OldPath) || !Directory.Exists(move.NewPath))
            throw new InvalidOperationException("The moved image folder tree or path changed before save.");
        var oldRoot = Path.GetRelativePath(session.LocalPath, move.OldPath).Replace('\\', '/');
        var newRoot = Path.GetRelativePath(session.LocalPath, move.NewPath).Replace('\\', '/');
        string Moved(string path) => newRoot + path[oldRoot.Length..];
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var folderType = session.Context.DesignerAssembly.GetType("Forguncy.HelpForm.ImageFolder", true)!;
        void Walk(object folder, string path)
        {
            if (!visited.Add(folder) || NativeV11Reflection.Get(folder, "Loaded") is not true || NativeV11Reflection.Get(folder, "Items") is not IList children || !actualDirectories.Add(path))
                throw new InvalidOperationException("The moved native image tree is incomplete or ambiguous.");
            foreach (var child in children.Cast<object>())
            {
                var name = NativeV11Reflection.Get(child, "Name") as string ?? throw new InvalidDataException("A moved image node has no name.");
                var childPath = path + "/" + name;
                if (child.GetType() == folderType) Walk(child, childPath);
                else if (child.GetType().FullName == "Forguncy.HelpForm.ImageItem" && visited.Add(child) && actualFiles.Add(childPath) &&
                    move.Images.Any(image => ReferenceEquals(image.Model, child) && Moved(Path.GetRelativePath(session.LocalPath, image.OriginalPath).Replace('\\', '/')) == childPath)) { }
                else throw new InvalidOperationException("A moved image model no longer belongs to the reviewed tree.");
            }
        }
        Walk(move.Folder, newRoot);
        if (!actualDirectories.SetEquals(move.Directories.Select(Moved)) || !actualFiles.SetEquals(move.Files.Keys.Select(Moved)))
            throw new InvalidOperationException("The moved native image tree differs from the reviewed inventory.");
        if (!NativeV11FolderTreeEvidence.Matches(session.LocalPath, session.LocalPath, session.LocalPath, newRoot,
            move.Directories.Select(Moved).ToArray(), move.Files.Keys.Select(Moved).ToArray(), true))
            throw new InvalidOperationException("The moved image folder files changed before save.");
        var cache = NativeV11ImageReplacement.CacheSnapshot.Capture(NativeV11Reflection.Get(move.Manager, "ImageNameToFullNameCache")!);
        if (cache.Entries.Count != move.Cache.Entries.Count) throw new InvalidOperationException("The native image cache changed before save.");
        foreach (var entry in move.Cache.Entries)
        {
            var image = move.Images.SingleOrDefault(value => Equals(value.Name, entry.Key));
            var expected = image is null ? entry.Value : Path.Combine(move.NewPath, Path.GetRelativePath(move.OldPath, image.OriginalPath));
            if (!cache.Entries.TryGetValue(entry.Key, out var actual) || !Equals(actual, expected))
                throw new InvalidOperationException("A native image cache entry changed before save.");
        }
        foreach (var image in move.Images)
        {
            var expected = Path.Combine(move.NewPath, Path.GetRelativePath(move.OldPath, image.OriginalPath));
            var original = Path.GetRelativePath(session.LocalPath, image.OriginalPath).Replace('\\', '/');
            if (!string.Equals(NativeV11Reflection.Get(image.Model, "FullName") as string, expected, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(expected) || move.Files[original] is not byte[] bytes || !File.ReadAllBytes(expected).AsSpan().SequenceEqual(bytes))
                throw new InvalidOperationException("A native image identity or bytes changed before save.");
        }
        session.VerifyContext();
    }

    private static object ImageView(Assembly designer, object? parent, object model)
    {
        var type = designer.GetType("Forguncy.Views.ObjectViews.ImageFolderViewModel", true)!;
        return type.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == 3).Invoke([parent, model, false]);
    }

    internal sealed record ImageState(object Model, string Name, string OriginalPath);
    internal sealed record MovePlan(NativeV11Session Session, object Manager, object Folder, object OldParent,
        object NewParent, string OldName, string NewName, string OldPath, string NewPath,
        IReadOnlyList<ImageState> Images, NativeV11ImageReplacement.CacheSnapshot Cache,
        IReadOnlyList<string> Directories, IReadOnlyDictionary<string, byte[]?> Files);

    internal static MovePlan PrepareMove(NativeV11Session session, object folder, object oldParent, object newParent, string newName,
        IReadOnlyList<string> directories, IReadOnlyList<string> files)
    {
        session.VerifyContext(); NativeV11StaticFiles.VerifyConfigOwner(session);
        var designer = session.Context.DesignerAssembly; _ = V11BuildProfile.Resolve(designer);
        var nativeFolder = designer.GetType("Forguncy.HelpForm.ImageFolder", true)!;
        if (folder.GetType() != nativeFolder || oldParent.GetType() != nativeFolder || newParent.GetType() != nativeFolder)
            throw new InvalidDataException("The native image folder identity is not valid.");
        V11Safety.Address(new(ModuleKind.Folder, newName));
        var root = NativeV11Reflection.Get(NativeV11Reflection.Get(session.Document, "ForguncySettings")!, "ImageFolder")!;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void CheckTree(object node)
        {
            if (!seen.Add(node) || node.GetType() != nativeFolder || NativeV11Reflection.Get(node, "Loaded") is not true)
                throw new InvalidOperationException("The loaded native image folder tree is incomplete or ambiguous.");
            foreach (var child in ((IEnumerable)NativeV11Reflection.Get(node, "Items")!).Cast<object>())
                if (child.GetType() == nativeFolder) CheckTree(child);
        }
        CheckTree(root);
        if (!seen.Contains(folder) || !seen.Contains(oldParent) || !seen.Contains(newParent))
            throw new InvalidOperationException("The image folders are not owned by the bound document tree.");
        var oldItems = NativeV11Reflection.Get(oldParent, "Items") as IList ?? throw new InvalidDataException("The original image parent is unavailable.");
        var newItems = NativeV11Reflection.Get(newParent, "Items") as IList ?? throw new InvalidDataException("The destination image parent is unavailable.");
        if (!oldItems.Contains(folder) || newItems.Cast<object>().Any(item => !ReferenceEquals(item, folder) &&
                string.Equals(NativeV11Reflection.Get(item, "Name") as string, newName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The native image folder parent or destination changed.");
        var oldName = NativeV11Reflection.Get(folder, "Name") as string ?? throw new InvalidDataException("The native image folder name is missing.");
        var moving = !ReferenceEquals(oldParent, newParent);
        if (moving ? newName != oldName : oldName.Equals(newName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Rename or move an image folder, never both or case-only.");
        var manager = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.ImageManager", true)!, "get_Instance")!;
        var localRoot = Path.GetFullPath(V11Safety.Under(session.LocalPath, "image"));
        var nativeRoot = NativeV11Reflection.Get(manager, "LocalImagesPath") as string;
        if (nativeRoot is null || !Path.GetFullPath(nativeRoot).TrimEnd('\\', '/').Equals(localRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The image manager belongs to another project.");
        var extensions = designer.GetType("Forguncy.Common.ImageExtensions", true)!;
        string PathOf(object node) => Path.GetFullPath((string)NativeV11Reflection.Static(extensions, "GetImageFolderPath", node, localRoot)!);
        var from = PathOf(folder); var target = Path.Combine(PathOf(newParent), newName);
        if (!from.StartsWith(localRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            !target.StartsWith(localRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Directory.Exists(target) || File.Exists(target))
            throw new InvalidOperationException("The image folder destination is invalid or occupied.");
        var relative = Path.GetRelativePath(session.LocalPath, from).Replace('\\','/');
        var next = Path.GetRelativePath(session.LocalPath, target).Replace('\\','/');
        _ = V11Safety.Under(session.LocalPath, relative); _ = V11Safety.Under(session.LocalPath, next);
        if (!NativeV11FolderTreeEvidence.Matches(session.LocalPath, session.LastPath, session.SourcePath, relative, directories, files, true) ||
            new[] { session.LocalPath, session.LastPath, session.SourcePath }.Any(path => NativeV11Folders.SourceState(path, next + "/").Exists))
            throw new InvalidOperationException("The exact image folder source tree or destination changed.");
        var source = NativeV11PendingSave.ReadFiles(session.SourcePath, files);
        var staged = NativeV11PendingSave.ReadFiles(session.LastPath, files);
        var local = NativeV11PendingSave.ReadFiles(session.LocalPath, files);
        if (files.Any(path => source[path] is not byte[] a || staged[path] is not byte[] b || local[path] is not byte[] c ||
                !a.AsSpan().SequenceEqual(b) || !a.AsSpan().SequenceEqual(c)))
            throw new InvalidOperationException("An image folder file differs across native save locations.");
        var images = new List<ImageState>();
        void Walk(object node)
        {
            if (NativeV11Reflection.Get(node, "Loaded") is not true) throw new InvalidOperationException("Every native image folder must be loaded.");
            foreach (var child in ((IEnumerable)NativeV11Reflection.Get(node, "Items")!).Cast<object>())
            {
                if (child.GetType() == nativeFolder) { if (ReferenceEquals(child,newParent)) throw new InvalidOperationException("Cannot move a folder into its own subtree."); Walk(child); continue; }
                if (child.GetType().FullName != "Forguncy.HelpForm.ImageItem" || NativeV11Reflection.Get(child, "BuiltIn") is not false ||
                    NativeV11Reflection.Get(child, "IsImportImage") is true) throw new InvalidOperationException("Protected or unknown image nodes cannot be moved.");
                var name = (string)NativeV11Reflection.Get(child, "Name")!; var full = (string)NativeV11Reflection.Get(child, "FullName")!;
                var imageRelative = Path.GetRelativePath(session.LocalPath, full).Replace('\\','/');
                if (!files.Contains(imageRelative, StringComparer.Ordinal) || !Path.GetFullPath(full).StartsWith(from + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A native image is outside its reviewed source group.");
                images.Add(new(child,name,full));
            }
        }
        if (ReferenceEquals(folder,newParent)) throw new InvalidOperationException("Cannot move an image folder into itself.");
        Walk(folder);
        if (images.Count != files.Count || images.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Count)
            throw new InvalidOperationException("The native image tree does not exactly cover the reviewed file group.");
        var cache = NativeV11ImageReplacement.CacheSnapshot.Capture(NativeV11Reflection.Get(manager, "ImageNameToFullNameCache")!);
        foreach (var image in images)
            if (!cache.Entries.TryGetValue(image.Name, out var path) || !string.Equals(path as string, image.OriginalPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The native image path cache is incomplete or stale.");
        session.VerifyContext();
        return new(session,manager,folder,oldParent,newParent,oldName,newName,from,target,images,cache,directories.ToArray(),source);
    }
    // The folder adapter must supply a reviewed native collaboration plan and
    // source journal before calling this method. Save/check-in remain with it.
    internal static Action Move(MovePlan plan)
    {
        var session = plan.Session;
        var current = PrepareMove(session, plan.Folder, plan.OldParent, plan.NewParent, plan.NewName,
            plan.Directories, plan.Files.Keys.ToArray());
        if (!ReferenceEquals(current.Manager, plan.Manager) || current.OldName != plan.OldName || current.OldPath != plan.OldPath || current.NewPath != plan.NewPath ||
            current.Images.Count != plan.Images.Count || current.Images.Zip(plan.Images).Any(pair =>
                !ReferenceEquals(pair.First.Model, pair.Second.Model) || pair.First.Name != pair.Second.Name || pair.First.OriginalPath != pair.Second.OriginalPath) ||
            plan.Files.Any(pair => !current.Files.TryGetValue(pair.Key, out var bytes) || pair.Value is null || bytes is null || !pair.Value.AsSpan().SequenceEqual(bytes)))
            throw new InvalidOperationException("The frozen image folder plan changed before mutation.");
        plan.Cache.RequireUnchanged();
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var oldItems = (IList)NativeV11Reflection.Get(plan.OldParent, "Items")!;
        var newItems = (IList)NativeV11Reflection.Get(plan.NewParent, "Items")!;
        var oldIndex = oldItems.IndexOf(plan.Folder); var moving = !ReferenceEquals(plan.OldParent, plan.NewParent);
        var changed = false; var restored = false;
        NativeV11ImageReplacement.CacheSnapshot? afterCache = null;
        void VerifyMoved()
        {
            if (Directory.Exists(plan.OldPath) || !Directory.Exists(plan.NewPath)) throw new InvalidOperationException("The native image folder path move is incomplete.");
            var oldRoot = Path.GetRelativePath(session.LocalPath,plan.OldPath).Replace('\\','/');
            var newRoot = Path.GetRelativePath(session.LocalPath,plan.NewPath).Replace('\\','/');
            string Moved(string path) => newRoot + path[oldRoot.Length..];
            if (!NativeV11FolderTreeEvidence.Matches(session.LocalPath,session.LocalPath,session.LocalPath,newRoot,
                    plan.Directories.Select(Moved).ToArray(),plan.Files.Keys.Select(Moved).ToArray(),true))
                throw new InvalidOperationException("The moved image subtree gained or lost an unreviewed entry.");
            foreach (var image in plan.Images)
            {
                var expected = Path.Combine(plan.NewPath, Path.GetRelativePath(plan.OldPath,image.OriginalPath));
                if (!string.Equals(NativeV11Reflection.Get(image.Model, "FullName") as string, expected, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(expected) || !File.ReadAllBytes(expected).AsSpan().SequenceEqual(plan.Files[Path.GetRelativePath(session.LocalPath,image.OriginalPath).Replace('\\','/')]!))
                    throw new InvalidOperationException("A native moved image or its bytes did not match the reviewed plan.");
            }
            var actual = NativeV11ImageReplacement.CacheSnapshot.Capture(NativeV11Reflection.Get(plan.Manager,"ImageNameToFullNameCache")!);
            foreach (var entry in plan.Cache.Entries.Where(pair => !plan.Images.Any(image => Equals(image.Name,pair.Key))))
                if (!actual.Entries.TryGetValue(entry.Key,out var value) || !Equals(value,entry.Value))
                    throw new InvalidOperationException("The native image move changed an unrelated cache entry.");
            foreach (var image in plan.Images)
            {
                var expected = Path.Combine(plan.NewPath,Path.GetRelativePath(plan.OldPath,image.OriginalPath));
                if (!actual.Entries.TryGetValue(image.Name,out var value) || !string.Equals(value as string,expected,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The native image move did not update its manager cache.");
            }
            if (actual.Entries.Count != plan.Cache.Entries.Count) throw new InvalidOperationException("The native image cache size changed unexpectedly.");
            afterCache = actual;
        }
        void Restore()
        {
            if (restored) return;
            session.VerifyContext();
            if (!changed || afterCache is null) throw new InvalidOperationException("The image move state was not fully verified; do not save.");
            afterCache.RequireUnchanged();
            if (Directory.Exists(plan.OldPath) || !Directory.Exists(plan.NewPath)) throw new InvalidOperationException("The image move paths changed before rollback.");
            VerifyMoved();
            if (moving)
            {
                if (!newItems.Contains(plan.Folder) || oldItems.Contains(plan.Folder)) throw new InvalidOperationException("The native image folder parent changed before rollback.");
                NativeV11Reflection.Call(plan.Manager,"MoveFolder",plan.Folder,plan.OldParent);
                newItems.Remove(plan.Folder); oldItems.Insert(Math.Min(oldIndex,oldItems.Count),plan.Folder);
            }
            else
            {
                if (NativeV11Reflection.Get(plan.Folder,"Name") as string != plan.NewName) throw new InvalidOperationException("The native image folder name changed before rollback.");
                NativeV11Reflection.Call(plan.Manager,"RenameFolder",plan.Folder,plan.OldName);
                NativeV11Reflection.Set(plan.Folder,"Name",plan.OldName);
            }
            plan.Cache.RequireUnchanged();
            foreach (var image in plan.Images)
                if (!string.Equals(NativeV11Reflection.Get(image.Model,"FullName") as string,image.OriginalPath,StringComparison.OrdinalIgnoreCase) || !File.Exists(image.OriginalPath))
                    throw new InvalidOperationException("The native image folder rollback was incomplete.");
            NativeV11Generation.Mark(session,"Image"); NativeV11Generation.Mark(session,"ImageFolder");
            NativeV11Reflection.Set(session.Document,"ContainsUnsavedChanges",dirty); restored=true;
        }
        try
        {
            if (moving)
            {
                NativeV11Reflection.Call(plan.Manager,"MoveFolder",plan.Folder,plan.NewParent);
                oldItems.Remove(plan.Folder); newItems.Add(plan.Folder);
            }
            else
            {
                NativeV11Reflection.Call(plan.Manager,"RenameFolder",plan.Folder,plan.NewName);
                NativeV11Reflection.Set(plan.Folder,"Name",plan.NewName);
            }
            changed=true; VerifyMoved();
            NativeV11Generation.Mark(session,"Image"); NativeV11Generation.Mark(session,"ImageFolder");
            NativeV11Reflection.Set(session.Document,"ContainsUnsavedChanges",true);
            return Restore;
        }
        catch (Exception primary)
        {
            if (changed && afterCache is not null)
            {
                try { Restore(); } catch (Exception rollback) { throw new AggregateException("Native image folder move and rollback failed; do not save.",primary,rollback); }
            }
            else if (Directory.Exists(plan.NewPath) || !Directory.Exists(plan.OldPath))
                throw new AggregateException("Native image folder move may be partial; do not save.",primary);
            throw;
        }
    }

}
