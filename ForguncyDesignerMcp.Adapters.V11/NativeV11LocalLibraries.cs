using System.Collections;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// This subset owns only local global JS/CSS resources and their native owner
// registration. It does not use AddUserFile/SaveFile (file
// dialogs), create default scripts, download links or evaluate any source text.
// Resource documents retain the existing NativeV11Resources.Read wire shape.
internal sealed class NativeV11LocalLibraries
{
    internal const string Root = "UserJaveScript";
    internal const string DefinitionPath = "CustomLibraries.json";
    private readonly NativeV11Session session;
    private readonly NativeV11Settings settings;
    private readonly object nativeModel, colleague, loader;
    private readonly NativeV11SettingsCodec codec;
    private readonly string directory;

    internal NativeV11LocalLibraries(NativeV11Session session, NativeV11Settings settings)
    {
        this.session = session; this.settings = settings;
        session.VerifyContext(); NativeV11StaticFiles.VerifyConfigOwner(session);
        var nativeSettings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        nativeModel = NativeV11Reflection.Get(nativeSettings, "CustomLibraries") ?? throw Invalid("The native custom-library owner is unavailable.");
        var manager = NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!;
        colleague = NativeV11Reflection.Get(manager, "CustomLibraries") ?? throw Invalid("The native custom-library colleague is unavailable.");
        var mediator = NativeV11Reflection.Get(colleague, "Mediator")!;
        loader = NativeV11Reflection.Get(mediator, "SaveDataColleague") ?? throw Invalid("The native custom-library saver is unavailable.");
        codec = new(session.Context.DesignerAssembly, colleague);
        directory = V11Safety.Under(session.LocalPath, Root);
        VerifyOwner();
    }

    internal IReadOnlyList<ModuleKind> Kinds => [ModuleKind.ResourceFile];
    internal static bool SupportsAddress(ModuleAddress address) => address.Kind == ModuleKind.ResourceFile && address.FolderPath == Root &&
        Path.GetExtension(address.Name).ToLowerInvariant() is ".js" or ".css";
    internal bool CanWrite(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.ResourceFile &&
        operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete;

    internal bool OwnsAddress(ModuleAddress address)
    {
        if (!SupportsAddress(address)) return false;
        VerifyOwner();
        var entry = Match(address.Name);
        return entry is not null && NativeV11Reflection.Get(entry.Model, "IsLink") is false;
    }

    internal bool VerifyDeleted(ModuleMutationRequest request)
    {
        VerifyOwner(); ValidateAddress(request.Address);
        if (request.Operation != ModuleOperation.Delete) throw Invalid("Deletion verification requires the reviewed delete operation.");
        var file = V11Safety.Under(directory, request.Address.Name);
        return Match(request.Address.Name) is null && !File.Exists(file) && !Directory.Exists(file);
    }

    internal IReadOnlyList<ModuleDocument> List(ModuleKind kind)
    {
        if (kind != ModuleKind.ResourceFile) throw Invalid("This writer only addresses registered local JS/CSS resources.");
        VerifyOwner();
        return Entries().Where(entry => NativeV11Reflection.Get(entry.Model, "IsLink") is false)
            .Select(entry => Read(new(ModuleKind.ResourceFile, entry.Name, Root))).OrderBy(document => document.Address.Name, StringComparer.Ordinal).ToArray();
    }

    internal ModuleDocument Read(ModuleAddress address)
    {
        VerifyOwner(); ValidateAddress(address);
        var entry = Match(address.Name);
        if (entry is null || NativeV11Reflection.Get(entry.Model, "IsLink") is not false) throw Invalid("The resource has no unique local native custom-library registration.");
        if (entry.Folder != Language(address.Name)) throw Invalid("The registered library kind disagrees with its file extension.");
        var document = ReadCore(address, directory);
        var definition = settings.CaptureSaveFiles(ModuleKind.CustomLibrary)[DefinitionPath];
        return document with { Revision = NativeV11Session.Hash(Encoding.UTF8.GetBytes(document.Revision + "\n" + NativeV11Session.Hash(definition))) };
    }

    internal IReadOnlyList<string> SourcePaths(ModuleAddress address)
    { ValidateAddress(address); return [DefinitionPath, Root + "/" + address.Name]; }
    internal IReadOnlyList<string> SourcePaths(ModuleMutationRequest request) => SourcePaths(request.Address);
    internal bool IsJsonSaveFile(string path) => path == DefinitionPath;
    internal IReadOnlyDictionary<string, byte[]> CaptureSaveFiles(ModuleAddress address)
    {
        VerifyOwner(); ValidateAddress(address);
        var result = settings.CaptureSaveFiles(ModuleKind.CustomLibrary).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (result.Count != 1 || !result.ContainsKey(DefinitionPath)) throw Invalid("The native custom-library save group changed.");
        var registered = Match(address.Name);
        var path = V11Safety.Under(directory, address.Name);
        if (registered is null)
        {
            if (File.Exists(path) || Directory.Exists(path)) throw Invalid("An unregistered resource still occupies the native library file path.");
        }
        else
        {
            var document = Read(address);
            result.Add(document.Companions[0].RelativePath, Convert.FromBase64String(document.Companions[0].ContentBase64));
        }
        return result;
    }
    internal IReadOnlyDictionary<string, byte[]> CaptureSaveFiles(ModuleMutationRequest request) => CaptureSaveFiles(request.Address);

    internal ReferenceResult FindReferences(ModuleAddress address)
    {
        VerifyOwner(); ValidateAddress(address);
        var entry = Match(address.Name);
        if (entry is null || NativeV11Reflection.Get(entry.Model, "IsLink") is not false)
            return new([], false, "The resource has no unique local CustomLibrary owner.");
        // Preserve the actual CustomLibrary target in returned references. The
        // scan includes live definitions, companions, global settings and other
        // registered scripts. Its existing dynamic-reference limitation remains.
        return settings.FindReferences(new(ModuleKind.CustomLibrary, NativeV11Settings.LibraryKey(entry.Model), entry.Folder));
    }

    internal void Validate(ModuleMutationRequest request)
    {
        VerifyOwner(); ValidateRequest(request);
        var existing = Match(request.Address.Name);
        var path = V11Safety.Under(directory, request.Address.Name);
        if (request.Operation == ModuleOperation.Create)
        {
            if (existing is not null || File.Exists(path) || Directory.Exists(path)) throw Invalid("The native library name, registration or file already exists.");
            VerifyCreateAvailable(codec, session.SourcePath, session.LocalPath, session.LastPath, request.Address.Name);
        }
        else
        {
            _ = Read(request.Address);
            if (existing!.Folder != Language(request.Address.Name)) throw Invalid("The native library kind changed.");
        }
        if (request.Operation == ModuleOperation.Delete)
        {
            var references = FindReferences(request.Address);
            if (!references.Complete || references.References.Count != 0)
                throw new BridgeException("LocalLibraryReferenced", "The actual native CustomLibrary is referenced, or its conservative reference scan is incomplete.");
        }
    }

    internal Action Apply(ModuleMutationRequest request)
    {
        Validate(request);
        var beforeSource = NativeV11SourceRevision.Capture(session.SourcePath);
        var beforeFiles = settings.CaptureSaveFiles(ModuleKind.CustomLibrary);
        var customAddress = new ModuleAddress(ModuleKind.CustomLibrary, request.Address.Name, Language(request.Address.Name));
        var customDocument = request.Operation == ModuleOperation.Delete ? null : new ModuleDocument(customAddress,
            JsonSerializer.SerializeToElement(new { Name = request.Address.Name, IsLink = false }), [], "");
        var customRequest = request with { Address = customAddress, Document = customDocument, ExpectedModuleRevision = null };
        var bytes = request.Document is null ? null : Convert.FromBase64String(request.Document.Companions[0].ContentBase64);
        void Verify()
        {
            VerifyOwner();
            if (NativeV11SourceRevision.Capture(session.SourcePath) != beforeSource)
                throw new BridgeException("ProjectSourceChanged", "The saved project changed during the native local-library transaction.");
        }
        var undo = ApplyFileCore(session.Context.DesignerAssembly, ResolveNativeFile(session.Context.DesignerAssembly), directory,
            request.Operation, request.Address.Name, bytes, () => settings.Apply(customRequest), Verify);
        try
        {
            Verify();
            if (request.Operation == ModuleOperation.Delete)
            {
                if (Match(request.Address.Name) is not null || File.Exists(V11Safety.Under(directory, request.Address.Name)))
                    throw Invalid("The native library definition/file deletion did not complete.");
            }
            else if (Read(request.Address).Companions[0].Sha256 != request.Document!.Companions[0].Sha256)
                throw Invalid("The native library file readback differs from the reviewed resource bytes.");
            _ = CaptureSaveFiles(request.Address);
        }
        catch (Exception primary)
        {
            try { undo(); }
            catch (Exception rollback) { throw new AggregateException("Native local-library verification and rollback both failed.", primary, rollback); }
            throw;
        }
        var restored = false;
        return () =>
        {
            if (restored) return;
            Verify(); undo();
            var current = settings.CaptureSaveFiles(ModuleKind.CustomLibrary);
            if (current.Count != beforeFiles.Count || beforeFiles.Any(pair => !current.TryGetValue(pair.Key, out var value) || !V11Safety.Equivalent(V11Safety.Body(value), V11Safety.Body(pair.Value))))
                throw Invalid("The native custom-library save group rollback was not confirmed.");
            restored = true;
        };
    }

    private void VerifyOwner()
    {
        session.VerifyContext(); NativeV11StaticFiles.VerifyConfigOwner(session);
        var designer = session.Context.DesignerAssembly; var newer = session.Profile.Version == "11.0.104.0";
        var liveSettings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var manager = NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!;
        var mediator = NativeV11Reflection.Get(colleague, "Mediator");
        var configured = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.Config", true)!, "get_LocalUserPageJavaScriptPath") as string;
        if (nativeModel.GetType() != designer.GetType("Forguncy.Model.CustomLibraries", true) ||
            colleague.GetType() != designer.GetType(newer ? "bw" : "bv", true) || loader.GetType() != designer.GetType(newer ? "bx" : "bw", true) ||
            !ReferenceEquals(NativeV11Reflection.Get(liveSettings, "CustomLibraries"), nativeModel) ||
            !ReferenceEquals(NativeV11Reflection.Get(manager, "CustomLibraries"), colleague) ||
            mediator is null || !ReferenceEquals(NativeV11Reflection.Get(mediator, "SaveDataColleague"), loader) ||
            !ReferenceEquals(NativeV11Reflection.Call(colleague, "GetSaveModel"), nativeModel) ||
            !ReferenceEquals(NativeV11Reflection.Get(loader, "SaveLoadFile"), session.SaveFile) ||
            configured is null || !Path.GetFullPath(configured).TrimEnd('\\', '/').Equals(directory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw Invalid("The native custom-library model, saver or physical owner does not belong to the bound document.");
        RequireStaticOwner(NativeV11StaticFiles.Resolve(session));
        _ = Entries();
    }

    internal static void RequireStaticOwner(NativeV11StaticFiles.Binding binding)
    {
        if (!binding.Folders.Any(folder => folder.Replace('\\', '/').Trim('/') == Root))
            throw Invalid("The native static-file registry does not own the local JS/CSS directory.");
    }

    internal static void VerifyCreateAvailable(NativeV11SettingsCodec codec, string source, string local, string last, string name)
    {
        ValidateAddress(new(ModuleKind.ResourceFile, name, Root));
        NativeV11CreationGuard.VerifySourceGroupAbsent(source, local, last, [Root + "/" + name]);
        foreach (var savedRoot in new[] { source, local, last })
        {
            if (Directory.Exists(savedRoot) && Directory.Exists(V11Safety.Under(savedRoot, DefinitionPath)))
                throw Invalid("A directory occupies the native custom-library definition filename.");
            var bytes = NativeV11PendingSave.ReadFiles(savedRoot, [DefinitionPath])[DefinitionPath];
            if (bytes is null) continue;
            var saved = codec.MapToModel(codec.Parse(V11Safety.Body(bytes)));
            foreach (var property in new[] { "UserJSFileList", "UserCSSFileList" })
            {
                if (NativeV11Reflection.Get(saved, property) is not IEnumerable entries)
                    throw Invalid("A saved native custom-library collection is unavailable.");
                foreach (var entry in entries.Cast<object>())
                    if (NativeV11Reflection.Get(entry, "Name") is not string entryName)
                        throw Invalid("A saved native custom-library identity is unavailable.");
                    else if (entryName.Equals(name, StringComparison.OrdinalIgnoreCase))
                        throw new BridgeException("ModulePathOccupied", "A saved source/local/last custom-library declaration still owns this filename; creation cannot overwrite it.");
            }
        }
    }

    private sealed record Entry(string Folder, string Name, object Model);
    private IReadOnlyList<Entry> Entries()
    {
        var result = new List<Entry>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modelType = AssemblyLoadContext.GetLoadContext(session.Context.DesignerAssembly)!.Assemblies.Single(assembly => assembly.GetName().Name == "ServerDesignerCommon").GetType("Forguncy.Model.UserJSOrCssFile", true)!;
        foreach (var (folder, property) in new[] { ("javascript", "UserJSFileList"), ("css", "UserCSSFileList") })
        {
            if (NativeV11Reflection.Get(nativeModel, property) is not IList list) throw Invalid("The native library list is unavailable.");
            foreach (var model in list.Cast<object>())
            {
                if (model.GetType() != modelType || NativeV11Reflection.Get(model, "Name") is not string name || NativeV11Reflection.Get(model, "IsLink") is not bool link)
                    throw Invalid("The native library list contains an unknown model.");
                if (!link)
                {
                    ValidateAddress(new(ModuleKind.ResourceFile, name, Root));
                    if (!names.Add(name) || folder != Language(name)) throw Invalid("The native local library identity is ambiguous or in the wrong JS/CSS collection.");
                }
                result.Add(new(folder, name, model));
            }
        }
        return result;
    }
    private Entry? Match(string name)
    {
        var matches = Entries().Where(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1 || matches.Length == 1 && matches[0].Name != name) throw Invalid("The native library name has duplicate or case-aliased registrations.");
        return matches.SingleOrDefault();
    }

    internal static ModuleDocument ReadCore(ModuleAddress address, string directory)
    {
        ValidateAddress(address);
        var bytes = ReadBytes(V11Safety.Under(directory, address.Name));
        var metadata = PlatformJson.Element(new { Name = address.Name, ResourceRoot = Root });
        var path = Root + "/" + address.Name; var hash = NativeV11Session.Hash(bytes);
        return new(address, metadata, [new(path, Convert.ToBase64String(bytes), hash, Language(address.Name) == "javascript" ? "text/javascript" : "text/css")],
            NativeV11Session.Hash(Encoding.UTF8.GetBytes(metadata.GetRawText() + "\n" + path + "\n" + hash)));
    }

    internal static void ValidateRequest(ModuleMutationRequest request)
    {
        ValidateAddress(request.Address);
        if (request.Scope != EffectScope.ProjectDefinition || request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
            throw Invalid("Local library resources use project-definition Create/Update/Delete only.");
        if (request.Operation == ModuleOperation.Delete)
        {
            if (request.Document is not null) throw Invalid("Local-library deletion must not contain replacement bytes or metadata.");
            return;
        }
        var document = request.Document;
        if (document is null || document.Address != request.Address || document.Metadata.ValueKind != JsonValueKind.Object || document.Companions.Count != 1)
            throw Invalid("The local JS/CSS resource document or companion group is invalid.");
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.Metadata.EnumerateObject())
            if (!properties.Add(property.Name) || property.Value.ValueKind != JsonValueKind.String ||
                (property.Name == "name" ? property.Value.GetString() != request.Address.Name : property.Name != "resourceRoot" || property.Value.GetString() != Root))
                throw Invalid("Local JS/CSS metadata must preserve the existing resource Name/ResourceRoot read shape.");
        if (properties.Count != 2) throw Invalid("Local JS/CSS metadata requires exactly Name and ResourceRoot.");
        var asset = document.Companions[0];
        V11Safety.Companion(asset, Root + "/" + request.Address.Name);
        var media = Language(request.Address.Name) == "javascript" ? "text/javascript" : "text/css";
        if (asset.MediaType is not null && asset.MediaType != media) throw Invalid("The JS/CSS media type does not match the fixed extension.");
        ValidateText(Convert.FromBase64String(asset.ContentBase64));
    }

    // Native file transaction shared by production and isolated component
    // tests. stageDefinition binds the existing Settings.Apply pipeline in
    // production; fixtures bind actual native collection + colleague.Save(T).
    // It must return its real native rollback action, never a success stub.
    internal static Action ApplyFileCore(Assembly designer, object file, string directory, ModuleOperation operation, string name,
        byte[]? incoming, Func<Action> stageDefinition, Action verifyContext)
    {
        _ = V11BuildProfile.Resolve(designer);
        RequireNativeFile(designer, file);
        var utilities = NativeUtilities(designer);
        ValidateAddress(new(ModuleKind.ResourceFile, name, Root));
        if (operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)) throw Invalid("Invalid local-library file operation.");
        if (operation != ModuleOperation.Delete) ValidateText(incoming ?? throw Invalid("Local-library file bytes are required."));
        else if (incoming is not null) throw Invalid("Local-library deletion does not accept incoming bytes.");
        var path = V11Safety.Under(directory, name);
        var directoryExisted = Directory.Exists(directory);
        if (Directory.Exists(path)) throw Invalid("A directory occupies the fixed local-library file path.");
        var before = File.Exists(path) ? ReadBytes(path) : null;
        if (operation == ModuleOperation.Create ? before is not null : before is null) throw Invalid("The native library file state does not match the requested operation.");
        var attributes = before is null ? (FileAttributes?)null : File.GetAttributes(path);
        var creation = before is null ? (DateTime?)null : File.GetCreationTimeUtc(path);
        var modified = before is null ? (DateTime?)null : File.GetLastWriteTimeUtc(path);
        Action? undoDefinition = null; var attempted = false; var restored = false;
        try
        {
            verifyContext();
            if (operation == ModuleOperation.Delete)
            {
                undoDefinition = stageDefinition();
                verifyContext(); RequireBytes(path, before);
                attempted = true; MakeWritable(); NativeV11Reflection.Call(file, "Delete", path);
                RequireBytes(path, null);
            }
            else
            {
                NativeV11Reflection.Static(utilities, "EnsureExistFolder", directory);
                RequireBytes(path, before); attempted = true;
                if (before is not null) MakeWritable();
                CopyBytes(incoming!, operation == ModuleOperation.Update);
                RequireBytes(path, incoming);
                verifyContext(); undoDefinition = stageDefinition();
            }
            verifyContext();
            RequireBytes(path, operation == ModuleOperation.Delete ? null : incoming);
        }
        catch (Exception primary)
        {
            try { Undo(); }
            catch (Exception rollback) { throw new AggregateException("Native local-library mutation and rollback both failed.", primary, rollback); }
            throw;
        }
        return Undo;

        void MakeWritable() => NativeV11Reflection.Static(utilities, "MakeFileNotReadOnly", path);
        void CopyBytes(byte[] bytes, bool overwrite)
        {
            var staging = Path.Combine(Path.GetTempPath(), "ForguncyDesignerMcp-local-library-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var input = V11Safety.Under(staging, name);
            try
            {
                File.WriteAllBytes(input, bytes); // private input only; never a project/cache/source direct write
                NativeV11Reflection.Call(file, "Copy", input, path, overwrite);
                NativeV11Reflection.Static(utilities, "MakeFileNotReadOnly", path);
            }
            finally
            {
                NativeV11Reflection.Call(file, "Delete", input);
                if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
            }
        }
        void Undo()
        {
            if (restored) return;
            verifyContext();
            if (attempted)
            {
                var current = File.Exists(path) ? ReadBytes(path) : null;
                var expected = operation == ModuleOperation.Delete ? null : incoming;
                if (!Equal(current, before) && !Equal(current, expected))
                    throw Invalid("The local-library file changed independently; native rollback cannot overwrite an unknown state.");
                if (before is null)
                {
                    if (current is not null) { MakeWritable(); NativeV11Reflection.Call(file, "Delete", path); }
                }
                else
                {
                    if (!Equal(current, before)) { if (current is not null) MakeWritable(); CopyBytes(before, true); }
                    NativeV11Reflection.Call(file, "SetCreationTimeUtc", path, creation!.Value);
                    NativeV11Reflection.Call(file, "SetLastWriteTimeUtc", path, modified!.Value);
                    NativeV11Reflection.Call(file, "SetAttributes", path, attributes!.Value);
                }
            }
            undoDefinition?.Invoke();
            RequireBytes(path, before);
            if (!directoryExisted && Directory.Exists(directory))
            {
                if (Directory.EnumerateFileSystemEntries(directory).Any())
                    throw Invalid("The newly created library directory gained independent files; its rollback is not confirmed.");
                NativeV11Reflection.Static(utilities, "DeleteFolder", directory);
                if (Directory.Exists(directory) || File.Exists(directory)) throw Invalid("The native empty library directory rollback did not remove its directory.");
            }
            restored = true;
        }
    }

    internal static object ResolveNativeFile(Assembly designer)
    {
        _ = V11BuildProfile.Resolve(designer);
        var common = AssemblyLoadContext.GetLoadContext(designer)!.Assemblies.Single(assembly => assembly.GetName().Name == "CommonUtilities");
        var file = NativeV11Reflection.Static(common.GetType("Forguncy.Common.FileContext", true)!, "get_Instance")!;
        RequireNativeFile(designer, file); return file;
    }
    private static void RequireNativeFile(Assembly designer, object file)
    {
        var common = AssemblyLoadContext.GetLoadContext(designer)!.Assemblies.Single(assembly => assembly.GetName().Name == "CommonUtilities");
        if (file.GetType() != common.GetType("Forguncy.Common.FileContext+FileImp", true))
            throw Invalid("The native IFile backend is not the audited installed physical-file implementation.");
        foreach (var (name, signature) in new (string, Type[])[]
                 {
                     ("Copy", [typeof(string), typeof(string), typeof(bool)]), ("Delete", [typeof(string)]),
                     ("SetAttributes", [typeof(string), typeof(FileAttributes)]),
                     ("SetCreationTimeUtc", [typeof(string), typeof(DateTime)]), ("SetLastWriteTimeUtc", [typeof(string), typeof(DateTime)])
                 })
        {
            var method = file.GetType().GetMethod(name, NativeV11Reflection.Flags, null, signature, null);
            if (method is null || method.DeclaringType != file.GetType() || method.IsStatic || method.ReturnType != typeof(void))
                throw Invalid("The audited native physical IFile method signature changed.");
        }
    }
    private static Type NativeUtilities(Assembly designer)
    {
        var common = AssemblyLoadContext.GetLoadContext(designer)!.Assemblies.Single(assembly => assembly.GetName().Name == "CommonUtilities");
        var utilities = common.GetType("Forguncy.Common.FileUtilities", true)!;
        foreach (var name in new[] { "EnsureExistFolder", "MakeFileNotReadOnly", "DeleteFolder" })
        {
            var method = utilities.GetMethod(name, NativeV11Reflection.Flags, null, [typeof(string)], null);
            if (method is null || method.DeclaringType != utilities || !method.IsStatic || method.ReturnType != typeof(void))
                throw Invalid("The audited native file-utility method signature changed.");
        }
        return utilities;
    }
    internal static void ValidateText(byte[] bytes)
    {
        if (bytes.Length > 32 * 1024 * 1024) throw Invalid("A local JS/CSS resource exceeds 32 MiB.");
        try { _ = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw Invalid("Local JS/CSS resource bytes must be strict UTF-8; bytes are never normalized or executed."); }
    }
    private static byte[] ReadBytes(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024) throw Invalid("The native local-library file is missing or exceeds 32 MiB.");
        return File.ReadAllBytes(path);
    }
    private static void RequireBytes(string path, byte[]? expected)
    {
        var actual = File.Exists(path) ? ReadBytes(path) : null;
        if (Directory.Exists(path) || !Equal(actual, expected)) throw Invalid("The native local-library file state changed or its byte readback differs.");
    }
    private static bool Equal(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static string Language(string name) => Path.GetExtension(name).Equals(".js", StringComparison.OrdinalIgnoreCase) ? "javascript" : "css";
    private static void ValidateAddress(ModuleAddress address)
    {
        V11Safety.Address(address);
        if (!SupportsAddress(address)) throw Invalid("This subset only addresses fixed leaf .js/.css names directly in the native UserJaveScript root; links, rename and arbitrary paths are excluded.");
    }
    private static BridgeException Invalid(string reason) => new("NativeLocalLibraryRequiresReview", reason);
}
