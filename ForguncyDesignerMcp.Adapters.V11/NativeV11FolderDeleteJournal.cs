using System.IO;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Frozen source, native model identities and reference checks for a grouped
// folder deletion. It never removes files or performs collaboration itself.
internal sealed class NativeV11FolderDeleteJournal
{
    private readonly string root;
    private readonly NativeV11Item[] items;
    private readonly (ModuleAddress Address, object Model)[] folders;
    private readonly string[] files;
    private readonly string[] directories;
    private readonly IReadOnlyDictionary<string, byte[]?> original;
    private readonly IReadOnlyDictionary<string, string> unrelated;
    private readonly IReadOnlySet<string> allowed;
    private readonly bool requireLocal;
    private (ModuleAddress Address, object Model)[] imageItems = [];
    internal IReadOnlyList<string> OldFiles => files;
    internal string[] Paths => directories.Select(path => path.TrimEnd('/') + "/").Concat(files).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private NativeV11FolderDeleteJournal(string root, NativeV11Item[] items, (ModuleAddress Address, object Model)[] folders,
        string[] files, string[] directories, IReadOnlyDictionary<string, byte[]?> original,
        IReadOnlyDictionary<string, string> unrelated, IReadOnlySet<string> allowed, bool requireLocal)
    { this.root = root; this.items = items; this.folders = folders; this.files = files; this.directories = directories;
      this.original = original; this.unrelated = unrelated; this.allowed = allowed; this.requireLocal = requireLocal; }

    internal static NativeV11FolderDeleteJournal Capture(NativeV11Session session, NativeV11FolderInventory inventory, string? changedDatabase = null)
    {
        var store = new NativeV11Folders(session);
        var root = inventory.Root; var files = inventory.Files.ToArray(); var dirs = inventory.Directories.ToArray();
        var requireLocal = store.OwnerRoute(inventory.Folders[0]).Kind is ModuleKind.Report or ModuleKind.BusinessProcess;
        var paths = dirs.Select(path => path.TrimEnd('/') + "/").Concat(files).ToArray();
        var allowed = NativeV11FirstProcessSaveJournal.AllowedPaths(paths); allowed.Add(".collaboration");
        if (changedDatabase is not null) allowed.Add(changedDatabase);
        var original = NativeV11PendingSave.ReadFiles(session.SourcePath, files);
        var journal = new NativeV11FolderDeleteJournal(root, inventory.Items.ToArray(),
            inventory.Folders.Select(address => (address, store.NativeModel(address))).ToArray(), files, dirs, original,
            NativeV11FirstProcessSaveJournal.SourceInventory(session.SourcePath, allowed), allowed, requireLocal);
        if (!journal.VerifyOriginal(session)) throw new InvalidDataException("The native folder deletion baseline changed.");
        return journal;
    }

    internal static NativeV11FolderDeleteJournal CaptureImages(NativeV11Session session, NativeV11FolderInventory inventory,
        IReadOnlyList<(ModuleAddress Address, object Model)> images)
    {
        var root = inventory.Root; var files = inventory.Files.ToArray(); var dirs = inventory.Directories.ToArray();
        var paths = dirs.Select(path => path.TrimEnd('/') + "/").Concat(files).ToArray();
        var allowed = NativeV11FirstProcessSaveJournal.AllowedPaths(paths); allowed.Add(".collaboration");
        var journal = new NativeV11FolderDeleteJournal(root, [],
            inventory.Folders.Select(address => (address, NativeV11ImageFolderLifecycle.Model(session, address))).ToArray(),
            files, dirs, NativeV11PendingSave.ReadFiles(session.SourcePath, files),
            NativeV11FirstProcessSaveJournal.SourceInventory(session.SourcePath, allowed), allowed, true) { imageItems = images.ToArray() };
        if (!journal.VerifyOriginal(session)) throw new InvalidDataException("The image folder deletion baseline changed.");
        var local = NativeV11PendingSave.ReadFiles(session.LocalPath, files);
        if (files.Any(path => local[path] is not byte[] actual || journal.original[path] is not byte[] saved || !actual.AsSpan().SequenceEqual(saved)))
            throw new InvalidDataException("The local image folder bytes differ from their saved source.");
        return journal;
    }

    internal bool VerifyOriginal(NativeV11Session session)
    {
        if (imageItems.Length != 0 || folders.Any(folder => NativeV11ImageFolderLifecycle.Supports(folder.Address)))
        {
            try
            {
                if (folders.Any(folder => !ReferenceEquals(NativeV11ImageFolderLifecycle.Model(session, folder.Address), folder.Model)) ||
                    imageItems.Any(image => !ReferenceEquals(NativeV11CollaborationOwners.FindImage(session, image.Address), image.Model))) return false;
            }
            catch { return false; }
        }
        if (!NativeV11FolderTreeEvidence.Matches(session.LocalPath, session.LastPath, session.SourcePath, root, directories, files, requireLocal)) return false;
        var other = NativeV11FirstProcessSaveJournal.SourceInventory(session.SourcePath, allowed);
        if (other.Count != unrelated.Count || unrelated.Any(pair => !other.TryGetValue(pair.Key, out var hash) || hash != pair.Value)) return false;
        var source = NativeV11PendingSave.ReadFiles(session.SourcePath, files);
        var staged = NativeV11PendingSave.ReadFiles(session.LastPath, files);
        return files.All(path => original[path] is byte[] bytes && source[path] is byte[] current && staged[path] is byte[] saved &&
            bytes.AsSpan().SequenceEqual(current) && bytes.AsSpan().SequenceEqual(saved));
    }

    internal void VerifyMemory(NativeV11Session session)
    {
        var current = session.Routes.SelectMany(route => route.Kind == ModuleKind.Table
            ? session.Items(ModuleKind.Table).Concat(session.Items(ModuleKind.View)) : session.Items(route.Kind)).ToArray();
        if (items.Any(item => current.Any(other => ReferenceEquals(other.Model, item.Model))))
            throw new InvalidOperationException("A deleted folder descendant remains in the native model.");
        var folderStore = new NativeV11Folders(session);
        var currentFolders = folderStore.List(ModuleKind.Folder).Select(document => document.Address).ToHashSet();
        if (folders.Any(folder => currentFolders.Contains(folder.Address))) throw new InvalidOperationException("A deleted native folder reappeared.");
        if (imageItems.Length != 0)
        {
            var liveImages = new NativeV11Resources(session).List(ModuleKind.Image).Select(document => document.Address).ToHashSet();
            foreach (var image in imageItems)
            {
                if (liveImages.Contains(image.Address)) throw new InvalidOperationException("A deleted native image reappeared.");
                var references = NativeV11References.Find(session, image.Address, image.Model);
                if (!references.Complete || references.References.Count != 0) throw new InvalidOperationException("A deleted image has references or incomplete reference evidence.");
            }
        }
        foreach (var item in items)
        {
            var references = NativeV11References.Find(session, item.Address, item.Model);
            if (!references.Complete || references.References.Count != 0)
                throw new InvalidOperationException("A deleted descendant has references or incomplete native reference evidence: " + item.RelativePath);
        }
    }

    internal bool VerifyDeleted(NativeV11Session session)
    {
        VerifyMemory(session);
        if (new[] { session.LocalPath, session.LastPath, session.SourcePath }.Any(path => NativeV11Folders.SourceState(path, root + "/").Exists)) return false;
        var other = NativeV11FirstProcessSaveJournal.SourceInventory(session.SourcePath, allowed);
        return other.Count == unrelated.Count && unrelated.All(pair => other.TryGetValue(pair.Key, out var hash) && hash == pair.Value);
    }
}
