using System.IO;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Read-only proof of the exact saved subtree and its native owners. This does
// not authorize a mutation by itself: native collector states, source journal
// and post-save tombstones must also be proved by the caller.
internal sealed record NativeV11FolderInventory(
    string Root, IReadOnlyList<ModuleAddress> Folders, IReadOnlyList<NativeV11Item> Items,
    IReadOnlyList<NativeV11CollaborationOwner> Owners, IReadOnlyList<string> Directories,
    IReadOnlyList<string> Files)
{
    internal IReadOnlyList<NativeV11CollaborationOwner> SharedOwners { get; init; } = [];
    internal sealed record MutationPlan(IReadOnlyList<NativeV11CollaborationOwner> Owners,
        IReadOnlyDictionary<string, IReadOnlyList<string>> FutureGroups);

    // The native collector is authoritative for states. Map its old/new
    // folder identities and each descendant item only after an exact match.
    internal MutationPlan Project(ModuleMutationRequest request, ModuleAddress destination,
        IReadOnlyList<(object Simple, string State)> changes)
    {
        if (request.Operation is not (ModuleOperation.Update or ModuleOperation.Delete) || request.Address.Kind != ModuleKind.Folder)
            throw new InvalidDataException("A grouped native folder update or deletion is required.");
        string NativeName(object simple) => NativeV11Reflection.Get(simple, "ModuleName") as string
            ?? throw new InvalidDataException("A native folder owner has no identity.");
        string NativeType(object simple) => NativeV11Reflection.Get(simple, "ModuleType")?.ToString()
            ?? throw new InvalidDataException("A native folder owner has no type.");
        var old = Owners.ToDictionary(owner => NativeName(owner.SimpleModule), StringComparer.Ordinal);
        var shared = SharedOwners.ToDictionary(owner => NativeName(owner.SimpleModule), StringComparer.Ordinal);
        var oldRoot = Root;
        if (destination.Kind != ModuleKind.Folder || string.IsNullOrEmpty(destination.FolderPath))
            throw new InvalidDataException("A grouped folder destination must identify a native parent.");
        var newRoot = destination.FolderPath + "/" + destination.Name;
        if (request.Operation == ModuleOperation.Update && (newRoot.Equals(oldRoot, StringComparison.OrdinalIgnoreCase) || newRoot.StartsWith(oldRoot + "/", StringComparison.OrdinalIgnoreCase) ||
            !newRoot.Split('/')[0].Equals(oldRoot.Split('/')[0], StringComparison.Ordinal)))
            throw new InvalidDataException("A grouped folder destination must be distinct and outside its own subtree in the same native root.");
        var expected = new Dictionary<string, (ModuleAddress Address, string State, string Type)>(StringComparer.Ordinal);
        foreach (var owner in Owners)
        {
            var name = NativeName(owner.SimpleModule);
            var type = NativeType(owner.SimpleModule);
            if (owner.Address.Kind == ModuleKind.Folder)
            {
                if (name != oldRoot && !name.StartsWith(oldRoot + "/", StringComparison.Ordinal))
                    throw new InvalidDataException("A native child folder lies outside the requested subtree.");
                expected.Add(name, (owner.Address, "Remove", type));
                if (request.Operation == ModuleOperation.Update)
                {
                    var next = newRoot + name[oldRoot.Length..];
                    var split = next.LastIndexOf('/');
                    expected.Add(next, (new ModuleAddress(ModuleKind.Folder, next[(split + 1)..], next[..split]), "New", type));
                }
            }
            else expected.Add(name, (owner.Address, request.Operation == ModuleOperation.Update ? "Move" : "Remove", type));
        }
        var names = changes.Select(change => NativeName(change.Simple)).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || expected.Keys.Any(name => !names.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("The native folder collector did not return the exact grouped owner set.");
        var projected = new List<NativeV11CollaborationOwner>();
        var future = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var (simple, state) in changes)
        {
            var name = NativeName(simple);
            if (!expected.TryGetValue(name, out var target) && shared.TryGetValue(name, out var sharedOwner))
                target = (sharedOwner.Address, "Edit", NativeType(sharedOwner.SimpleModule));
            if (target.Address is null || state != target.State || NativeType(simple) != target.Type)
                throw new InvalidDataException("A native subtree owner or state differs from its reviewed model: " + name);
            if (state != "New" && (!old.TryGetValue(name, out var existing) && !shared.TryGetValue(name, out existing) ||
                !Equals(NativeV11Reflection.Get(existing.SimpleModule, "IconType"), NativeV11Reflection.Get(simple, "IconType")) ||
                !Equals(NativeV11Reflection.Get(existing.SimpleModule, "ExclusiveType"), NativeV11Reflection.Get(simple, "ExclusiveType"))))
                throw new InvalidDataException("A native subtree owner identity changed.");
            projected.Add(new(target.Address, simple, state));
            if (state == "New") future.Add(name, [name + "/"]);
        }
        foreach (var owner in SharedOwners.Where(owner => !names.Contains(NativeName(owner.SimpleModule), StringComparer.Ordinal)))
            projected.Add(owner with { State = "Edit" });
        return new(projected, future);
    }

    internal static NativeV11FolderInventory Capture(NativeV11Session session, ModuleAddress address, bool checkReferences,
        IReadOnlyList<(object Simple, string State)>? standaloneChanges = null)
    {
        session.VerifyContext();
        var folderStore = new NativeV11Folders(session);
        var route = folderStore.OwnerRoute(address);
        var root = folderStore.SourcePaths(address).Single().TrimEnd('/');
        var prefix = root + "/";
        var folders = folderStore.List(ModuleKind.Folder).Select(document => document.Address)
            .Where(candidate =>
            {
                var marker = folderStore.SourcePaths(candidate).Single().TrimEnd('/');
                return marker == root || marker.StartsWith(prefix, StringComparison.Ordinal);
            }).ToArray();
        if (folders.Count(candidate => candidate == address) != 1) throw new InvalidDataException("The native folder subtree root is absent or ambiguous.");
        var kinds = route.Kind == ModuleKind.Table ? new[] { ModuleKind.Table, ModuleKind.View } : new[] { route.Kind };
        var items = kinds.SelectMany(session.Items).Where(item => item.RelativePath.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (items.Select(item => item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new InvalidDataException("The native folder subtree contains colliding definition paths.");
        if (checkReferences)
            foreach (var item in items)
            {
                var references = NativeV11References.Find(session, item.Address, item.Model);
                if (!references.Complete || references.References.Any(reference => route.Kind != ModuleKind.Table || !items.Any(candidate => candidate.Address == reference.Source)))
                    throw new InvalidOperationException("A descendant definition has references or an incomplete native reference scan: " + item.RelativePath);
            }
        var collaboration = session.Context.CaptureSnapshot().InCollaborationMode;
        if (!collaboration && (route.Kind != ModuleKind.Table || standaloneChanges is null))
            throw new NotSupportedException("Standalone grouped inventory is implemented for native table folders only.");
        IReadOnlyList<NativeV11CollaborationOwner> owners;
        if (collaboration) owners = NativeV11CollaborationOwners.Resolve(session, folders.Concat(items.Select(item => item.Address)).ToArray());
        else
        {
            var projected = new List<NativeV11CollaborationOwner>();
            foreach (var folder in folders)
            {
                var name = folderStore.SourcePaths(folder).Single().TrimEnd('/');
                var simple = standaloneChanges!.Single(change => NativeV11Reflection.Get(change.Simple, "ModuleName") as string == name &&
                    NativeV11Reflection.Get(change.Simple, "ModuleType")?.ToString() == "TableFolder" && change.State == "Remove").Simple;
                projected.Add(new(folder, simple, "Edit"));
            }
            foreach (var item in items)
            {
                var classified = NativeV11CollaborationOwners.ClassifyModelCore(session.Context.DesignerAssembly, item.Model);
                var name = NativeV11Reflection.Get(classified, "ModuleName") as string;
                var simple = standaloneChanges!.Single(change => NativeV11Reflection.Get(change.Simple, "ModuleName") as string == name &&
                    NativeV11Reflection.Get(change.Simple, "ModuleType")?.ToString() == NativeV11Reflection.Get(classified, "ModuleType")?.ToString() && change.State == (checkReferences ? "Remove" : "Move")).Simple;
                projected.Add(new(item.Address, simple, "Edit"));
            }
            owners = projected;
        }
        IReadOnlyList<NativeV11CollaborationOwner> expanded;
        if (collaboration) expanded = NativeV11CollaborationOwners.ExpandExistingOwners(session, owners, allowSharedTableDatabase: true);
        else
        {
            var allOthers = session.Items(ModuleKind.Table).Concat(session.Items(ModuleKind.View)).Where(candidate => !items.Any(item => ReferenceEquals(item.Model, candidate.Model))).ToArray();
            var related = new List<NativeV11CollaborationOwner>();
            foreach (var change in standaloneChanges!.Where(value => value.State == "Edit"))
            {
                var matches = allOthers.Where(item =>
                {
                    var identity = NativeV11CollaborationOwners.ClassifyModelCore(session.Context.DesignerAssembly, item.Model);
                    return NativeV11Reflection.Get(identity, "ModuleName") as string == NativeV11Reflection.Get(change.Simple, "ModuleName") as string &&
                        Equals(NativeV11Reflection.Get(identity, "ModuleType"), NativeV11Reflection.Get(change.Simple, "ModuleType")) &&
                        Equals(NativeV11Reflection.Get(identity, "IconType"), NativeV11Reflection.Get(change.Simple, "IconType")) &&
                        Equals(NativeV11Reflection.Get(identity, "ExclusiveType"), NativeV11Reflection.Get(change.Simple, "ExclusiveType"));
                }).ToArray();
                if (matches.Length != 1) throw new InvalidOperationException("A native related owner has no unique loaded table model.");
                related.Add(new(matches[0].Address, change.Simple, "Edit"));
            }
            expanded = owners.Concat(related).ToArray();
        }
        var ownerNames = owners.Select(owner => (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!).ToHashSet(StringComparer.Ordinal);
        var sharedOwners = expanded.Where(owner => !ownerNames.Contains((string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!)).ToArray();
        var paths = (collaboration ? owners.SelectMany(owner => NativeV11CollaborationOwners.GetDirectFiles(session, owner,
                allowSharedTableDatabase: true)).Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && !path.EndsWith('/'))
            : items.Select(item => item.RelativePath)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
        var directories = folders.Select(candidate => folderStore.SourcePaths(candidate).Single().TrimEnd('/')).Order(StringComparer.Ordinal).ToArray();
        var requireLocal = route.Kind is ModuleKind.Report or ModuleKind.BusinessProcess;
        if (!NativeV11FolderTreeEvidence.Matches(session.LocalPath, session.LastPath, session.SourcePath, root, directories, paths, requireLocal))
            throw new InvalidOperationException("The saved folder subtree differs from its complete native owner file inventory.");
        var local = NativeV11PendingSave.ReadFiles(session.LocalPath, paths);
        var last = NativeV11PendingSave.ReadFiles(session.LastPath, paths);
        var source = NativeV11PendingSave.ReadFiles(session.SourcePath, paths);
        if (paths.Any(path => last[path] is not byte[] staged || source[path] is not byte[] saved ||
            !staged.AsSpan().SequenceEqual(saved) || local[path] is byte[] bytes && !bytes.AsSpan().SequenceEqual(saved) ||
            requireLocal && local[path] is null))
            throw new InvalidOperationException("A descendant source file differs from native LocalData or LastSaveFiles.");
        foreach (var item in items)
        {
            if (!source.TryGetValue(item.RelativePath, out var metadata) || metadata is null ||
                !V11Safety.Equivalent(NativeV11SourceMetadata.Capture(session, item.Address), V11Safety.Body(metadata)))
                throw new InvalidOperationException("A descendant model differs from its saved native metadata: " + item.RelativePath);
            var document = session.Read(item, true);
            foreach (var asset in document.Companions)
            {
                if (!source.TryGetValue(asset.RelativePath, out var saved) || saved is null)
                    throw new InvalidOperationException("A descendant native companion is absent from the saved owner group: " + asset.RelativePath);
                var expected = Convert.FromBase64String(asset.ContentBase64);
                var equal = asset.RelativePath.EndsWith(".rd", StringComparison.OrdinalIgnoreCase)
                    ? V11Safety.Equivalent(V11Safety.Body(expected), V11Safety.Body(saved))
                    : expected.AsSpan().SequenceEqual(saved);
                if (!equal) throw new InvalidOperationException("A descendant native companion differs from its saved source: " + asset.RelativePath);
            }
        }
        session.VerifyContext();
        return new(root, folders, items, owners, directories, paths) { SharedOwners = sharedOwners };
    }
}
