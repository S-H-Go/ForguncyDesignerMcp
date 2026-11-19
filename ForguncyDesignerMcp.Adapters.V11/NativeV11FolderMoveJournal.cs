using System.IO;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Frozen source witness for a reviewed folder rename/move. A verified source
// journal alone never authorizes mutation or native collaboration checkout.
internal sealed class NativeV11FolderMoveJournal
{
    private readonly string oldRoot;
    private readonly string newRoot;
    private readonly string[] directories;
    private readonly string[] files;
    private readonly IReadOnlyDictionary<string, byte[]?> originals;
    private readonly bool requireLocal;
    private (ModuleAddress Address, object Model)[]? folderModels;
    private (ModuleAddress OldAddress, ModuleAddress Address, object Model, string OriginalPath)[]? itemModels;
    private IReadOnlyDictionary<string, string>? unrelatedSource;
    private IReadOnlySet<string>? allowedPaths;
    private IReadOnlyDictionary<ModuleAddress, ModuleAddress>? imageAddresses;
    internal bool CoversTransition(string from, string to) => folderModels is not null && itemModels is not null &&
        oldRoot == from.TrimEnd('/') && newRoot == to.TrimEnd('/');
    internal string[] Paths => directories.SelectMany(path => new[] { path + "/", newRoot + path[oldRoot.Length..] + "/" })
        .Concat(files.SelectMany(path => new[] { path, newRoot + path[oldRoot.Length..] })).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal IReadOnlyList<string> OldFiles => files;
    internal IReadOnlyList<string> NewFiles => files.Select(path => newRoot + path[oldRoot.Length..]).ToArray();
    internal IReadOnlyList<string> NewDirectories => directories.Select(path => newRoot + path[oldRoot.Length..] + "/").ToArray();
    internal bool AllDirectoriesRepresentedInGit => NewDirectories.All(directory => NewFiles.Any(path => path.StartsWith(directory, StringComparison.Ordinal)));
    internal ModuleAddress CurrentAddress(ModuleAddress address) => imageAddresses is not null && imageAddresses.TryGetValue(address, out var image)
        ? image : itemModels?.SingleOrDefault(item => item.OldAddress == address).Address ?? address;
    internal void BindImageAddresses(IEnumerable<ModuleAddress> addresses)
    {
        imageAddresses = addresses.Where(address => address.Kind == ModuleKind.Image).ToDictionary(address => address, address =>
        {
            var path = "image/" + (string.IsNullOrEmpty(address.FolderPath) ? "" : address.FolderPath + "/") + address.Name;
            if (!path.StartsWith(oldRoot + "/", StringComparison.Ordinal)) throw new InvalidDataException("An image owner is outside its reviewed folder.");
            var moved = newRoot + path[oldRoot.Length..]; var slash = moved.LastIndexOf('/');
            return new ModuleAddress(ModuleKind.Image, address.Name, moved["image/".Length..slash]);
        });
    }

    private NativeV11FolderMoveJournal(string oldRoot, string newRoot, string[] directories, string[] files,
        IReadOnlyDictionary<string, byte[]?> originals, bool requireLocal)
    { this.oldRoot = oldRoot; this.newRoot = newRoot; this.directories = directories; this.files = files; this.originals = originals; this.requireLocal = requireLocal; }

    internal static NativeV11FolderMoveJournal Capture(string local, string last, string source, string oldRoot, string newRoot,
        IReadOnlyCollection<string> directories, IReadOnlyCollection<string> files, bool requireLocal)
    {
        oldRoot = oldRoot.TrimEnd('/'); newRoot = newRoot.TrimEnd('/');
        if (oldRoot.Equals(newRoot, StringComparison.OrdinalIgnoreCase) || newRoot.StartsWith(oldRoot + "/", StringComparison.OrdinalIgnoreCase) ||
            oldRoot.Split('/')[0] != newRoot.Split('/')[0]) throw new InvalidDataException("A folder move target must be distinct and outside its own subtree.");
        _ = V11Safety.Under(local, oldRoot); _ = V11Safety.Under(local, newRoot);
        var oldDirs = directories.Select(value => value.TrimEnd('/')).ToArray();
        var oldFiles = files.ToArray();
        if (!NativeV11FolderTreeEvidence.Matches(local, last, source, oldRoot, oldDirs, oldFiles, requireLocal))
            throw new InvalidDataException("The original native folder subtree does not match its reviewed inventory.");
        if (new[] { local, last, source }.Any(basePath => NativeV11Folders.SourceState(basePath, newRoot + "/").Exists))
            throw new InvalidDataException("The destination folder already exists in a native save location.");
        var sourceBytes = NativeV11PendingSave.ReadFiles(source, oldFiles);
        var lastBytes = NativeV11PendingSave.ReadFiles(last, oldFiles);
        if (oldFiles.Any(path => sourceBytes[path] is not byte[] data || lastBytes[path] is not byte[] stage || !stage.AsSpan().SequenceEqual(data)))
            throw new InvalidDataException("A native subtree file differs between staging and source.");
        var journal = new NativeV11FolderMoveJournal(oldRoot, newRoot, oldDirs, oldFiles, sourceBytes, requireLocal);
        var allowed = NativeV11FirstProcessSaveJournal.AllowedPaths(journal.Paths);
        // Native checkout commits the lock registry before the mutation. Its
        // exact state is verified by the collaboration plan independently.
        allowed.Add(".collaboration");
        journal.allowedPaths = allowed;
        journal.unrelatedSource = NativeV11FirstProcessSaveJournal.SourceInventory(source, journal.allowedPaths);
        return journal;
    }

    internal bool VerifyMoved(string local, string last, string source)
    {
        string Moved(string path) => newRoot + path[oldRoot.Length..];
        var newDirs = directories.Select(Moved).ToArray(); var newFiles = files.Select(Moved).ToArray();
        if (!NativeV11FolderTreeEvidence.Matches(local, last, source, newRoot, newDirs, newFiles, requireLocal) ||
            new[] { local, last, source }.Any(basePath => NativeV11Folders.SourceState(basePath, oldRoot + "/").Exists)) return false;
        if (allowedPaths is null || unrelatedSource is null) return false;
        var other = NativeV11FirstProcessSaveJournal.SourceInventory(source, allowedPaths);
        if (other.Count != unrelatedSource.Count || unrelatedSource.Any(pair => !other.TryGetValue(pair.Key, out var hash) || hash != pair.Value)) return false;
        var sourceBytes = NativeV11PendingSave.ReadFiles(source, newFiles);
        var lastBytes = NativeV11PendingSave.ReadFiles(last, newFiles);
        foreach (var old in files)
        {
            var path = Moved(old); var original = originals[old];
            if (original is null || sourceBytes[path] is not byte[] current || lastBytes[path] is not byte[] staged ||
                !staged.AsSpan().SequenceEqual(current)) return false;
            var equivalent = old.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || old.EndsWith(".rd", StringComparison.OrdinalIgnoreCase)
                ? V11Safety.Equivalent(V11Safety.Body(original), V11Safety.Body(current)) : original.AsSpan().SequenceEqual(current);
            if (!equivalent) return false;
        }
        return true;
    }

    internal bool VerifyOriginal(string local, string last, string source)
    {
        if (allowedPaths is null || unrelatedSource is null ||
            !NativeV11FolderTreeEvidence.Matches(local, last, source, oldRoot, directories, files, requireLocal) ||
            new[] { local, last, source }.Any(basePath => NativeV11Folders.SourceState(basePath, newRoot + "/").Exists)) return false;
        var other = NativeV11FirstProcessSaveJournal.SourceInventory(source, allowedPaths);
        if (other.Count != unrelatedSource.Count || unrelatedSource.Any(pair => !other.TryGetValue(pair.Key, out var hash) || hash != pair.Value)) return false;
        var current = NativeV11PendingSave.ReadFiles(source, files);
        var staged = NativeV11PendingSave.ReadFiles(last, files);
        return files.All(path => originals[path] is byte[] original && current[path] is byte[] actual && staged[path] is byte[] saved &&
            original.AsSpan().SequenceEqual(actual) && original.AsSpan().SequenceEqual(saved));
    }

    internal static NativeV11FolderMoveJournal Capture(NativeV11Session session, NativeV11FolderInventory inventory, ModuleAddress destination)
    {
        var newRoot = destination.FolderPath + "/" + destination.Name;
        var route = new NativeV11Folders(session).OwnerRoute(destination);
        var journal = Capture(session.LocalPath, session.LastPath, session.SourcePath, inventory.Root, newRoot,
            inventory.Directories, inventory.Files, route.Kind is ModuleKind.Report or ModuleKind.BusinessProcess);
        var folders = new NativeV11Folders(session);
        journal.folderModels = inventory.Folders.Select(address =>
        {
            var oldPath = folders.SourcePaths(address).Single().TrimEnd('/');
            var newPath = newRoot + oldPath[inventory.Root.Length..];
            var index = newPath.LastIndexOf('/');
            return (new ModuleAddress(ModuleKind.Folder, newPath[(index + 1)..], newPath[..index]), folders.NativeModel(address));
        }).ToArray();
        journal.itemModels = inventory.Items.Select(item =>
        {
            var newPath = newRoot + item.RelativePath[inventory.Root.Length..];
            if (!newPath.StartsWith(item.Route.Folder, StringComparison.Ordinal) || !newPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A moved native definition has an invalid destination path.");
            var relative = newPath[item.Route.Folder.Length..^5]; var slash = relative.LastIndexOf('/');
            return (item.Address, new ModuleAddress(item.Address.Kind, item.Address.Name, slash < 0 ? null : relative[..slash]), item.Model, item.RelativePath);
        }).ToArray();
        return journal;
    }

    internal void VerifyMemory(NativeV11Session session)
    {
        if (folderModels is null || itemModels is null) throw new InvalidOperationException("The grouped folder journal is not bound to native models.");
        var folders = new NativeV11Folders(session);
        foreach (var (address, model) in folderModels)
            if (!ReferenceEquals(folders.NativeModel(address), model)) throw new InvalidOperationException("A moved native folder identity changed.");
        foreach (var (_, address, model, originalPath) in itemModels)
        {
            var item = session.Find(address);
            if (!ReferenceEquals(item.Model, model) || originals[originalPath] is not byte[] original ||
                !V11Safety.Equivalent(NativeV11SourceMetadata.Capture(session, address), V11Safety.Body(original)))
                throw new InvalidOperationException("A moved native definition or its saved metadata changed.");
            foreach (var asset in session.Read(item, true).Companions)
            {
                if (!asset.RelativePath.StartsWith(newRoot + "/", StringComparison.Ordinal)) continue;
                var oldPath = oldRoot + asset.RelativePath[newRoot.Length..];
                if (!originals.TryGetValue(oldPath, out var before) || before is null) throw new InvalidOperationException("A moved native companion was not reviewed.");
                var current = Convert.FromBase64String(asset.ContentBase64);
                var equal = oldPath.EndsWith(".rd", StringComparison.OrdinalIgnoreCase)
                    ? V11Safety.Equivalent(V11Safety.Body(before), V11Safety.Body(current)) : before.AsSpan().SequenceEqual(current);
                if (!equal) throw new InvalidOperationException("A moved native companion changed.");
            }
        }
        session.VerifyContext();
    }
}
