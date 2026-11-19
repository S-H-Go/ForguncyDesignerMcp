using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Images use the native ImageManager import/delete entry points, live tree,
// native static-file owner and generation cache. Other resource roots are read
// through their exact native Config registrations and remain explicitly read-only.
internal sealed partial class NativeV11Resources
{
    private readonly NativeV11Session session;
    private readonly Lazy<NativeV11ImageReplacement>? imageReplacement;
    private readonly object imageRoot, imageManager, imageColleague, imageProvider;
    private readonly string imageDirectory;
    private readonly Dictionary<string, string> fileRoots = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> unavailableFileRoots = new(StringComparer.Ordinal);
    private sealed record ImageEntry(ModuleAddress Address, object Model, IList Parent, int Index, string RelativePath);
    private sealed record CacheEntry(IDictionary Store, string Key, bool Existed, object? Value);

    internal NativeV11Resources(NativeV11Session session, Func<ModuleAddress, ModuleDocument>? readBoundDocument = null)
    {
        this.session = session;
        if (readBoundDocument is not null) imageReplacement = new(() => new(session, readBoundDocument));
        session.VerifyContext();
        NativeV11StaticFiles.VerifyConfigOwner(session);
        var designer = session.Context.DesignerAssembly;
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        imageRoot = NativeV11Reflection.Get(settings, "ImageFolder") ?? throw new MissingMemberException("Settings.ImageFolder");
        var colleagues = NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!;
        imageColleague = NativeV11Reflection.Get(colleagues, "ImageModel") ?? throw new MissingMemberException("Colleagues.ImageModel");
        if (!ReferenceEquals(NativeV11Reflection.Get(imageColleague, "Root"), imageRoot))
            throw new InvalidOperationException("The native image tree is not owned by the bound document.");
        imageManager = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.ImageManager", true)!, "get_Instance")!;
        imageDirectory = Path.GetFullPath(Path.Combine(session.LocalPath, "image"));
        if (!SamePath(NativeV11Reflection.Get(imageManager, "LocalImagesPath") as string, imageDirectory))
            throw new InvalidOperationException("The native image owner belongs to another document/cache directory.");
        imageProvider = Activator.CreateInstance(designer.GetType("Forguncy.Common.ImageReferFilesProvider", true)!)!;
        if (!ReferenceEquals(NativeV11Reflection.Get(imageProvider, "RootFolder"), imageRoot))
            throw new InvalidOperationException("The native image file registration belongs to another document.");
        var config = designer.GetType("Forguncy.Common.Config", true)!;
        foreach (var (root, property) in new[]
                 {
                     ("UserFile", "LocalUserFilePath"), ("UserJaveScript", "LocalUserPageJavaScriptPath"),
                     ("UserMapFile", "LocalUserMapFilePath"), ("Upload", "DesignerUploadFolderPath")
                 })
        {
            try
            {
                var value = NativeV11Reflection.Static(config, "get_" + property) as string
                    ?? throw new MissingMemberException("Config." + property);
                var ownerRoot = root == "Upload" ? NativeV11Reflection.Get(session.SaveFile, "DesignerPath") as string : session.LocalPath;
                if (ownerRoot is null || !SamePath(value, Path.Combine(ownerRoot, root)))
                    throw new InvalidOperationException("The native resource root is not owned by the bound document.");
                fileRoots.Add(root, Path.GetFullPath(value));
            }
            catch (Exception error) { unavailableFileRoots.Add(root, error.GetBaseException().Message); }
        }
        // Some exact V11 builds expose the template-file root; absence is not
        // treated as a broken registry on builds which do not provide it.
        if(config.GetMethod("get_LocalUserTemplateFilePath",NativeV11Reflection.Flags) is not null)
        {
            try
            {
                var value=NativeV11Reflection.Static(config,"get_LocalUserTemplateFilePath") as string ?? throw new MissingMemberException("Config.LocalUserTemplateFilePath");
                if(!SamePath(value,Path.Combine(session.LocalPath,"UserTemplateFile")))throw new InvalidOperationException("The native template-resource root belongs to another document.");
                fileRoots.Add("UserTemplateFile",Path.GetFullPath(value));
            }
            catch(Exception error){unavailableFileRoots.Add("UserTemplateFile",error.GetBaseException().Message);}
        }
    }

    internal IReadOnlyList<ModuleKind> Kinds { get; } = [ModuleKind.Image, ModuleKind.ResourceFile];
    internal IReadOnlyDictionary<string, string> UnavailableFileRoots => unavailableFileRoots;

    internal IReadOnlyList<ModuleDocument> List(ModuleKind kind)
    {
        session.VerifyContext();
        if (kind == ModuleKind.Image) return Images().Select(ReadImage).ToArray();
        if (kind != ModuleKind.ResourceFile) throw new NotSupportedException("This native resource kind is not mapped.");
        if (unavailableFileRoots.Count != 0)
            throw new BridgeException("IncompleteResourceRegistry", "The native file-resource registry is incomplete: " + string.Join(", ", unavailableFileRoots.Keys));
        var documents = new List<ModuleDocument>();
        foreach (var root in fileRoots)
        {
            if (!Directory.Exists(root.Value)) continue;
            void Walk(string relative)
            {
                var current = relative.Length == 0 ? root.Value : V11Safety.Under(root.Value, relative);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("A native resource root or folder is a reparse point.");
                foreach (var path in Directory.EnumerateFileSystemEntries(current).OrderBy(path => path, StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(path);
                    var child = relative.Length == 0 ? name : relative + "/" + name;
                    _ = V11Safety.Under(root.Value, child);
                    if (Directory.Exists(path)) Walk(child);
                    else documents.Add(Read(new(ModuleKind.ResourceFile, name, root.Key + (relative.Length == 0 ? "" : "/" + relative))));
                }
            }
            Walk("");
        }
        return documents;
    }

    internal ModuleDocument Read(ModuleAddress address)
    {
        session.VerifyContext();
        V11Safety.Address(address);
        if (address.Kind == ModuleKind.Image) return ReadImage(FindImage(address));
        if (address.Kind != ModuleKind.ResourceFile) throw new NotSupportedException("This native resource kind is not mapped.");
        var (root, relative, source) = FileAddress(address);
        var bytes = ReadBytes(V11Safety.Under(root, relative));
        return BinaryDocument(address, PlatformJson.Element(new { Name = address.Name, ResourceRoot = source.Split('/')[0] }), source, bytes);
    }

    internal static bool SupportsWrite(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.Image &&
        operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete;

    internal bool CanWrite(ModuleKind kind, ModuleOperation operation) => SupportsWrite(kind, operation);

    internal IReadOnlyList<string> SourcePaths(ModuleAddress address)
    {
        session.VerifyContext(); V11Safety.Address(address);
        if (address.Kind == ModuleKind.Image) return [ImagePath(address)];
        if (address.Kind == ModuleKind.ResourceFile) return [FileAddress(address).Source];
        throw new NotSupportedException("This native resource source group is not mapped.");
    }

    internal IReadOnlyDictionary<string, byte[]> CaptureSaveFiles(ModuleAddress address)
    {
        session.VerifyContext();
        if (address.Kind == ModuleKind.Image)
        {
            var item = Images().SingleOrDefault(item => item.Address == address);
            return item is null ? new Dictionary<string, byte[]>() : new Dictionary<string, byte[]>
            {
                [item.RelativePath] = ReadBytes(ImageFile(item))
            };
        }
        var document = Read(address);
        return document.Companions.ToDictionary(asset => asset.RelativePath, asset => Convert.FromBase64String(asset.ContentBase64), StringComparer.Ordinal);
    }

    internal ReferenceResult FindReferences(ModuleAddress address)
    {
        session.VerifyContext();
        if (address.Kind != ModuleKind.Image)
            return NativeV11FileReferences.Find(session,address,FileAddress(address).Source);
        // ReferenceFinder's exact image branch calls EnumLocalImageReference.
        // The shared mapper also requires all relevant native pages/trees loaded.
        return NativeV11References.Find(session, address, FindImage(address).Model);
    }

    internal void Validate(ModuleMutationRequest request)
    {
        session.VerifyContext(); V11Safety.Address(request.Address);
        if(request.Address.Kind==ModuleKind.ResourceFile){ValidateFileMutation(request);return;}
        if (request.Address.Kind == ModuleKind.Image && request.Operation == ModuleOperation.Update)
        {
            (imageReplacement?.Value ?? throw new InvalidOperationException("The authoritative image revision reader is unavailable.")).Validate(request);
            return;
        }
        if (!CanWrite(request.Address.Kind, request.Operation))
            throw new NotSupportedException(request.Address.Kind == ModuleKind.Image
                ? "The native image replacement entry opens a file dialog; noninteractive image update is not verified."
                : "No noninteractive native owner write pipeline is verified for this registered resource root.");
        var staticFiles = NativeV11StaticFiles.Resolve(session);
        if (!staticFiles.Folders.Any(folder => folder.Replace('\\', '/').Trim('/').Equals("image", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The native static-file save registry does not own the image directory.");
        var entries = Images();
        if (request.Operation == ModuleOperation.Create)
        {
            var document = request.Document ?? throw new InvalidDataException("Image creation requires metadata and one binary companion.");
            if (document.Address != request.Address || document.Metadata.ValueKind != JsonValueKind.Object || document.Companions.Count != 1)
                throw new InvalidDataException("The image document identity or companion group is invalid.");
            foreach (var property in document.Metadata.EnumerateObject())
                if (property.Name != "Name" || property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() != request.Address.Name)
                    throw new InvalidDataException("Image creation accepts only native Name metadata; default native flags are assigned by ImageManager.");
            if (!document.Metadata.TryGetProperty("Name", out _)) throw new InvalidDataException("Image metadata requires Name.");
            var path = ImagePath(request.Address);
            V11Safety.Companion(document.Companions[0], path);
            ValidateRaster(request.Address.Name, Convert.FromBase64String(document.Companions[0].ContentBase64));
            NativeV11CreationGuard.VerifySourceGroupAbsent(session.SourcePath, session.LocalPath, session.LastPath, [path]);
            if (entries.Any(entry => entry.Address.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                File.Exists(V11Safety.Under(session.LocalPath, path)))
                throw new InvalidOperationException("The image name or its native file already exists; import collisions are not resolved automatically.");
            var names = (IDictionary)NativeV11Reflection.Get(imageManager, "ImageNameToFullNameCache")!;
            if (names.Keys.Cast<string>().Any(name => name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("The native global image-name cache already contains this name.");
            var imports = (IDictionary)NativeV11Reflection.Get(imageManager, "ImportImageNameToFullNameCache")!;
            if (imports.Keys.Cast<string>().Any(name => name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                imports.Values.Cast<string>().Any(value => SamePath(value, V11Safety.Under(session.LocalPath, path))))
                throw new InvalidOperationException("The native image import cache already registers this identity.");
            _ = Parent(request.Address.FolderPath);
            _ = ParentViewModel(Parent(request.Address.FolderPath));
        }
        else
        {
            if (request.Document is not null) throw new InvalidDataException("Image deletion must not contain replacement metadata or bytes.");
            var item = FindImage(request.Address);
            if (NativeV11Reflection.Get(item.Model, "BuiltIn") is not false || NativeV11Reflection.Get(item.Model, "IsImportImage") is true)
                throw new NotSupportedException("Built-in images and native import aliases are protected.");
            VerifyRegistration(item);
            var imports = (IDictionary)NativeV11Reflection.Get(imageManager, "ImportImageNameToFullNameCache")!;
            if (imports.Values.Cast<string>().Any(path => SamePath(path, ImageFile(item))))
                throw new NotSupportedException("This image has native import aliases; their replacement/deletion lifecycle is not validated.");
            var references = FindReferences(request.Address);
            if (!references.Complete || references.References.Count != 0)
                throw new BridgeException("ResourceReferenced", "The image has native references or reference enumeration is incomplete; deletion stopped.");
            _ = ReadImage(item);
            _ = ParentViewModel(Parent(request.Address.FolderPath));
        }
    }

    internal object ImageCollaborationExecutor(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.Image || request.Operation is not (ModuleOperation.Create or ModuleOperation.Delete))
            throw new InvalidDataException("An image create/delete request is required.");
        Validate(request);
        var parent = ParentViewModel(Parent(request.Address.FolderPath));
        var designer = session.Context.DesignerAssembly;
        if (request.Operation == ModuleOperation.Create)
        {
            var executorType = designer.GetType("Forguncy.Views.ObjectViews.CreateItemOrFolderExecutor", true)!;
            var newer = designer.GetName().Version!.ToString() == "11.0.104.0";
            var constructor = executorType.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == (newer ? 6 : 5));
            var parameters = constructor.GetParameters();
            var moduleType = Enum.Parse(parameters[3].ParameterType, "Image");
            var args = newer ? new object?[] { null, parent, request.Address.Name, moduleType, Enum.ToObject(parameters[4].ParameterType, 41), null }
                : new object?[] { null, parent, request.Address.Name, moduleType, null };
            return constructor.Invoke(args);
        }
        var item = FindImage(request.Address);
        var viewType = designer.GetType("Forguncy.Views.ObjectViews.ImageItemViewModel", true)!;
        var view = viewType.GetConstructors(NativeV11Reflection.Flags).Single(value => value.GetParameters().Length == 2).Invoke([parent, item.Model]);
        var itemType = designer.GetType("Forguncy.Views.ObjectViews.ItemBaseViewModel", true)!;
        var selected = Array.CreateInstance(itemType, 1);
        selected.SetValue(view, 0);
        return NativeV11Reflection.Call(view, "DeleteItemsCollaborationExecutor", selected)
            ?? throw new InvalidOperationException("The native image deletion collector is unavailable.");
    }

    internal Action Apply(ModuleMutationRequest request)
    {
        if(request.Address.Kind==ModuleKind.ResourceFile)return ApplyFileMutation(request);
        if (request.Address.Kind == ModuleKind.Image && request.Operation == ModuleOperation.Update)
            return (imageReplacement?.Value ?? throw new InvalidOperationException("The authoritative image revision reader is unavailable.")).Apply(request);
        Validate(request);
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var parentModel = Parent(request.Address.FolderPath);
        var parentView = ParentViewModel(parentModel);
        var parent = (IList)NativeV11Reflection.Get(parentModel, "Items")!;
        var before = request.Operation == ModuleOperation.Delete ? FindImage(request.Address) : null;
        var bytes = before is null ? null : ReadBytes(ImageFile(before));
        var originalPath = V11Safety.Under(session.LocalPath, ImagePath(request.Address));
        var key = before is null ? null : NativeV11Reflection.Call(imageColleague, "GetKey", before.Model) as string;
        var cacheEntries = CaptureCaches(request.Address.Name, originalPath);
        object? created = null;
        bool completed = false;
        void Undo()
        {
            if (completed) return;
            session.VerifyContext();
            if (before is null)
            {
                if (created is not null && parent.Contains(created)) RemoveNative(created, parent);
                else if (created is null && File.Exists(V11Safety.Under(session.LocalPath, ImagePath(request.Address))))
                    throw new InvalidOperationException("The native import wrote bytes before publishing its tree node; rollback is not confirmed.");
            }
            else
            {
                // Native DeleteImage can fail before deleting bytes. Check the file
                // before changing a healthy tree or attempting a new native import.
                if (File.Exists(originalPath))
                {
                    if (!ReadBytes(originalPath).SequenceEqual(bytes!))
                        throw new InvalidOperationException("Native image rollback found different bytes and will not overwrite them.");
                }
                else
                {
                    if (parent.Contains(before.Model))
                    {
                        parent.Remove(before.Model);
                    }
                    var imported = Import(parentView, Path.GetDirectoryName(originalPath)!, before.Address.Name, bytes!);
                    parent.Remove(imported);
                }
                RestoreImageRegistrationCore(imageColleague, parent, before.Model, before.Index, key!);
                if (NativeV11Session.Hash(ReadBytes(originalPath)) != NativeV11Session.Hash(bytes!))
                    throw new InvalidOperationException("Native image rollback did not restore the original bytes.");
            }
            RestoreCaches(cacheEntries, originalPath);
            NativeV11Generation.Mark(session, "Image");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
            completed = true;
        }
        try
        {
            if (before is null)
            {
                var input = request.Document!.Companions[0];
                var full = V11Safety.Under(session.LocalPath, ImagePath(request.Address));
                created = Import(parentView, Path.GetDirectoryName(full)!, request.Address.Name, Convert.FromBase64String(input.ContentBase64), value => created = value);
                var current = FindImage(request.Address);
                VerifyRegistration(current);
                if (!ReferenceEquals(current.Model, created) || NativeV11Session.Hash(ReadBytes(ImageFile(current))) != input.Sha256)
                    throw new InvalidOperationException("The native image import identity or bytes differ from the request.");
            }
            else
            {
                // The owner entry deletes its bytes and derived image caches.
                // It is called only after the complete native reference guard.
                RemoveNative(before.Model, parent);
                if (File.Exists(V11Safety.Under(session.LocalPath, before.RelativePath)) || Images().Any(item => item.Address == request.Address))
                    throw new InvalidOperationException("The native image deletion left its file or tree registration present.");
            }
            NativeV11Generation.Mark(session, "Image");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            return Undo;
        }
        catch (Exception primary)
        {
            try { Undo(); }
            catch (Exception rollback) { throw new AggregateException("Native image mutation failed and rollback was not confirmed.", primary, rollback); }
            throw;
        }
    }

    // Images are saved by the native static-directory mirror. Their native
    // model colleague Insert/Delete/Edit methods intentionally have no save-data
    // dictionary. Restore the actual tree identity after verifying owner bytes.
    internal static void RestoreImageRegistrationCore(object colleague, IList parent, object model, int index, string key)
    {
        var name = NativeV11Reflection.Get(model, "Name") as string;
        if (parent.Cast<object>().Any(item => !ReferenceEquals(item, model) && NativeV11Reflection.Get(item, "Name") as string == name))
            throw new InvalidOperationException("Native image rollback found a different tree node at the original identity.");
        if (!parent.Contains(model)) parent.Insert(Math.Min(index, parent.Count), model);
        var restoredKey = NativeV11Reflection.Call(colleague, "GetKey", model) as string;
        if (!parent.Contains(model) || !string.Equals(restoredKey?.Replace('\\', '/'), key.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Native image rollback did not restore the original model/path identity.");
    }

    private IReadOnlyList<CacheEntry> CaptureCaches(string name, string path)
    {
        var names = (IDictionary)NativeV11Reflection.Get(imageManager, "ImageNameToFullNameCache")!;
        var imports = (IDictionary)NativeV11Reflection.Get(imageManager, "ImportImageNameToFullNameCache")!;
        var temp = (IDictionary)NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.ShareUtilities", true)!, "get_TempImagePathCache")!;
        return new[] { (names, name), (imports, name), (temp, path) }
            .Select(pair => new CacheEntry(pair.Item1, pair.Item2, pair.Item1.Contains(pair.Item2), pair.Item1[pair.Item2])).ToArray();
    }

    private static void RestoreCaches(IReadOnlyList<CacheEntry> entries, string ownedPath)
    {
        foreach (var entry in entries)
        {
            var exists = entry.Store.Contains(entry.Key);
            var current = exists ? entry.Store[entry.Key] : null;
            if (exists && !Equals(current, entry.Value) && !(current is string value && SamePath(value, ownedPath)))
                throw new InvalidOperationException("Native image rollback found a changed derived-cache registration.");
            if (entry.Existed)
            {
                if (!exists) entry.Store.Add(entry.Key, entry.Value);
                else if (!Equals(current, entry.Value)) entry.Store[entry.Key] = entry.Value;
            }
            else if (exists) entry.Store.Remove(entry.Key);
        }
    }

    private object Import(object parentView, string folder, string name, byte[] bytes, Action<object>? observe = null)
    {
        session.VerifyContext();
        if (session.Context.CaptureSnapshot().InCollaborationMode)
            throw new InvalidOperationException("Image import requires a verified standalone native document.");
        var stage = Path.Combine(Path.GetTempPath(), "ForguncyDesignerMcp-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var source = V11Safety.Under(stage, name);
        try
        {
            File.WriteAllBytes(source, bytes); // Private input staging only, never a source/cache path.
            var values = NativeV11Reflection.Call(imageManager, "NewImage", new[] { source }, parentView, folder, false, false) as IList
                ?? throw new InvalidOperationException("The native image import was rejected.");
            if (values.Count != 1 || values[0] is not object model) throw new InvalidOperationException("The native image import did not return exactly one image.");
            observe?.Invoke(model);
            if (NativeV11Reflection.Get(model, "Name") as string != name || !SamePath(NativeV11Reflection.Get(model, "FullName") as string, Path.Combine(folder, name)))
                throw new InvalidOperationException("The native import changed the requested image identity; collisions are not accepted.");
            return model;
        }
        finally
        {
            if (File.Exists(source)) File.Delete(source);
            if (Directory.Exists(stage)) Directory.Delete(stage, false);
        }
    }

    private void RemoveNative(object model, IList parent)
    {
        var full = NativeV11Reflection.Get(model, "FullName") as string ?? throw new InvalidDataException("The native image has no owner file.");
        var relative = Path.GetRelativePath(session.LocalPath, full).Replace('\\', '/');
        if (!relative.StartsWith("image/", StringComparison.Ordinal) || !SamePath(full, V11Safety.Under(session.LocalPath, relative)))
            throw new InvalidDataException("The native image deletion target escaped its registered owner root.");
        NativeV11Reflection.Call(imageManager, "DeleteImage", model);
        parent.Remove(model);
    }

    private IReadOnlyList<ImageEntry> Images()
    {
        session.VerifyContext();
        var result = new List<ImageEntry>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object folder, string prefix)
        {
            if (!seen.Add(folder) || NativeV11Reflection.Get(folder, "Loaded") is not true)
                throw new InvalidOperationException("The native image tree is cyclic or not fully loaded.");
            var items = NativeV11Reflection.Get(folder, "Items") as IList ?? throw new MissingMemberException("ImageFolder.Items");
            for (var index = 0; index < items.Count; index++)
            {
                var model = items[index]!;
                var name = NativeV11Reflection.Get(model, "Name") as string ?? throw new InvalidDataException("Native image node has no name.");
                V11Safety.Address(new(ModuleKind.Image, name));
                if (NativeV11Reflection.Get(model, "Items") is IList) Walk(model, prefix + name + "/");
                else
                {
                    if (model.GetType().FullName != "Forguncy.HelpForm.ImageItem" || !seen.Add(model))
                        throw new InvalidDataException("The native image tree contains an unknown or duplicated item.");
                    var address = new ModuleAddress(ModuleKind.Image, name, prefix.Length == 0 ? null : prefix.TrimEnd('/'));
                    result.Add(new(address, model, items, index, "image/" + prefix + name));
                }
            }
        }
        Walk(imageRoot, "");
        return result;
    }

    private ImageEntry FindImage(ModuleAddress address) => Images().SingleOrDefault(item => item.Address == address)
        ?? throw new KeyNotFoundException("No unique native image matches the address.");

    private object Parent(string? folder)
    {
        var current = imageRoot;
        foreach (var segment in NativeV11Session.NormalizeFolder(folder).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NativeV11Reflection.Get(current, "Loaded") is not true) throw new InvalidOperationException("The native image folder is not loaded.");
            current = ((IList)NativeV11Reflection.Get(current, "Items")!).Cast<object>().SingleOrDefault(item =>
                NativeV11Reflection.Get(item, "Name") as string == segment && NativeV11Reflection.Get(item, "Items") is IList)
                ?? throw new KeyNotFoundException("The requested native image folder does not exist.");
        }
        if (NativeV11Reflection.Get(current, "Loaded") is not true) throw new InvalidOperationException("The native image parent is not loaded.");
        return current;
    }

    private object ParentViewModel(object parent)
    {
        var singleton = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("o", true)!, "get_Instance")!;
        var property = singleton.GetType().GetProperties(NativeV11Reflection.Flags).Single(candidate => candidate.PropertyType.FullName == "Forguncy.Views.ObjectViews.ObjectExplorer");
        var explorer = property.GetValue(singleton) ?? throw new InvalidOperationException("The native image object explorer is not initialized.");
        var imageView = NativeV11Reflection.Get(explorer, "ImageObjectView")!;
        var root = NativeV11Reflection.Get(imageView, "Root") ?? throw new InvalidOperationException("The native image view root is unavailable.");
        object? Walk(object node)
        {
            if (ReferenceEquals(NativeV11Reflection.Get(node, "Model"), parent)) return node;
            if (NativeV11Reflection.Get(node, "Items") is not IEnumerable children) return null;
            foreach (var child in children.Cast<object>())
                if (child.GetType().FullName == "Forguncy.Views.ObjectViews.ImageFolderViewModel" && Walk(child) is object found) return found;
            return null;
        }
        return Walk(root) ?? throw new InvalidOperationException("The owning native image folder view is not initialized.");
    }

    private ModuleDocument ReadImage(ImageEntry item)
    {
        if (NativeV11Reflection.Get(item.Model, "BuiltIn") is true)
        {
            var metadata = ImageMetadata(item.Model);
            return new(item.Address, metadata, [], NativeV11Session.Hash(Encoding.UTF8.GetBytes(metadata.GetRawText())));
        }
        VerifyRegistration(item);
        return CaptureImageCore(item.Address, item.Model, session.LocalPath);
    }

    internal static ModuleDocument CaptureImageCore(ModuleAddress address, object model, string localRoot)
    {
        V11Safety.Address(address);
        if (address.Kind != ModuleKind.Image || model.GetType().FullName != "Forguncy.HelpForm.ImageItem" ||
            NativeV11Reflection.Get(model, "Name") as string != address.Name || NativeV11Reflection.Get(model, "BuiltIn") is not false)
            throw new InvalidDataException("The native image identity is invalid or protected.");
        var relative = ImagePath(address);
        var file = V11Safety.Under(localRoot, relative);
        if (!SamePath(NativeV11Reflection.Get(model, "FullName") as string, file))
            throw new InvalidDataException("The native image path is outside its owning resource root.");
        return BinaryDocument(address, ImageMetadata(model), relative, ReadBytes(file));
    }

    private string ImageFile(ImageEntry item)
    {
        var expected = V11Safety.Under(session.LocalPath, item.RelativePath);
        if (!SamePath(NativeV11Reflection.Get(item.Model, "FullName") as string, expected))
            throw new InvalidDataException("The native image file is not the tree node's registered project resource.");
        return expected;
    }

    private void VerifyRegistration(ImageEntry item)
    {
        _ = ImageFile(item);
        var key = NativeV11Reflection.Call(imageColleague, "GetKey", item.Model) as string;
        if (key?.Replace('\\', '/') != item.RelativePath)
            throw new InvalidDataException("The native image save key differs from its tree/resource path.");
        var files = (IEnumerable<string>)NativeV11Reflection.Call(imageProvider, "GetReferFiles", "Image/" + item.RelativePath["image/".Length..])!;
        if (!files.Select(path => path.Replace('\\', '/')).Contains(item.RelativePath, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("The native image reference-file registry does not own this file.");
    }

    private (string Root, string Relative, string Source) FileAddress(ModuleAddress address)
    {
        V11Safety.Address(address);
        var segments = (address.FolderPath ?? "").Split('/');
        if (unavailableFileRoots.ContainsKey(segments[0]))
            throw new BridgeException("IncompleteResourceRegistry", "The requested native resource root is unavailable: " + segments[0]);
        if (segments.Length == 0 || !fileRoots.TryGetValue(segments[0], out var root))
            throw new InvalidDataException("ResourceFile requires a registered native root in FolderPath.");
        var relative = string.Join('/', segments.Skip(1).Append(address.Name));
        _ = V11Safety.Under(root, relative);
        return (root, relative, segments[0] + "/" + relative);
    }

    private static string ImagePath(ModuleAddress address) => "image/" + (address.FolderPath is { Length: > 0 } folder ? folder + "/" : "") + address.Name;
    private static bool SamePath(string? left, string right) => !string.IsNullOrEmpty(left) && Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static byte[] ReadBytes(string file)
    {
        if (!File.Exists(file)) throw new FileNotFoundException("The native resource file is missing.");
        if (new FileInfo(file).Length > 32 * 1024 * 1024) throw new InvalidDataException("The resource exceeds the 32 MiB companion limit.");
        return File.ReadAllBytes(file);
    }
    private static JsonElement ImageMetadata(object model) => JsonSerializer.SerializeToElement(new
    {
        Name = (string)NativeV11Reflection.Get(model, "Name")!, BuiltIn = NativeV11Reflection.Get(model, "BuiltIn") is true,
        IsImportImage = NativeV11Reflection.Get(model, "IsImportImage") is true,
        Fill = NativeV11Reflection.Get(model, "Fill") as string,
        UserCellForeColor = NativeV11Reflection.Get(model, "UserCellForeColor") is true
    });
    private static ModuleDocument BinaryDocument(ModuleAddress address, JsonElement metadata, string path, byte[] bytes)
    {
        var hash = NativeV11Session.Hash(bytes);
        var asset = new CompanionAsset(path, Convert.ToBase64String(bytes), hash, Mime(path));
        return new(address, metadata, [asset], NativeV11Session.Hash(Encoding.UTF8.GetBytes(metadata.GetRawText() + "\n" + path + "\n" + hash)));
    }
    private static string Mime(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".bmp" => "image/bmp",
        ".ico" => "image/x-icon", ".svg" => "image/svg+xml", ".js" => "text/javascript", ".css" => "text/css",
        ".json" => "application/json", _ => "application/octet-stream"
    };
    internal static void ValidateRaster(string name, byte[] bytes)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico"))
            throw new NotSupportedException("Noninteractive native image import is currently verified for raster formats only; SVG import can open native dialogs.");
        try
        {
            using var input = new MemoryStream(bytes, false);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0 || !decoder.CodecInfo.FileExtensions.Split(',').Select(value => value.Trim()).Contains(extension, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException();
        }
        catch { throw new InvalidDataException("The image bytes do not match a supported raster format and filename."); }
    }
}
