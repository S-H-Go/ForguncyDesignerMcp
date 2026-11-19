using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Settings definitions remain in the live native model. The owning native
// colleague stages their save data; this class never writes a project/cache file.
internal sealed class NativeV11Settings
{
    private readonly NativeV11Session session;
    private readonly object settings;
    private readonly object application;
    private readonly object applicationColleague;
    private readonly NativeV11SettingsCodec applicationCodec;
    private readonly Dictionary<ModuleKind, (object Colleague, NativeV11SettingsCodec Codec)> roots = new();
    private static readonly HashSet<string> RestartOnly = new(StringComparer.Ordinal)
    {
        "WebSitePort", "UserServicePort", "BpmServerSchemaId", "ProcessDataConnectionSet", "AuthenticationMode", "JavaAdapterDebugPort"
    };

    internal NativeV11Settings(NativeV11Session session)
    {
        this.session = session;
        settings = NativeV11Reflection.Get(session.Document, "ForguncySettings") ?? throw new MissingMemberException("Document.ForguncySettings");
        application = NativeV11Reflection.Get(settings, "ApplicationSettings") ?? throw new MissingMemberException("Settings.ApplicationSettings");
        var manager = NativeV11Reflection.Get(session.Document, "ForguncyColleagueManager")!;
        applicationColleague = NativeV11Reflection.Get(manager, "ApplicationSettings") ?? throw new MissingMemberException("Colleagues.ApplicationSettings");
        applicationCodec = new(session.Context.DesignerAssembly, applicationColleague);
        roots.Add(ModuleKind.ApplicationSettings, (applicationColleague, applicationCodec));
        foreach (var (kind, property) in new[] { (ModuleKind.CustomLibrary, "CustomLibraries"), (ModuleKind.DataConnection, "DataConnection"), (ModuleKind.Theme, "Theme"), (ModuleKind.PermissionGroup, "PermissionGroupConfig") })
        {
            if (NativeV11Reflection.Get(manager, property) is not object colleague) continue;
            roots.Add(kind, (colleague, new(session.Context.DesignerAssembly, colleague)));
        }
        Kinds = roots.Keys.Concat(new[] { ModuleKind.GlobalVariable, ModuleKind.LanguageResource, ModuleKind.CellStyle, ModuleKind.ListViewStyle }).ToArray();
    }

    internal IReadOnlyList<ModuleKind> Kinds { get; }

    internal IReadOnlyList<ModuleDocument> List(ModuleKind kind)
    {
        session.VerifyContext();
        if (kind == ModuleKind.GlobalVariable)
            return Globals.Cast<object>().Select(value => Document(new(kind, Name(value)), applicationCodec.SerializeValue(value))).ToArray();
        if (kind == ModuleKind.LanguageResource)
            return Resources.Cast<DictionaryEntry>().Select(entry => Document(new(kind, (string)entry.Key), applicationCodec.SerializeValue(entry.Value!))).ToArray();
        if (kind == ModuleKind.CellStyle)
        {
            var styles = NativeV11Reflection.Get(settings, "CustomCellTypeStyleTemplates") as IDictionary ?? throw new MissingMemberException("Settings.CustomCellTypeStyleTemplates");
            var result = new List<ModuleDocument>();
            foreach (DictionaryEntry group in styles)
                foreach (var style in (IEnumerable)group.Value!)
                    result.Add(Document(new(kind, StyleKey(style), (string)group.Key), applicationCodec.SerializeValue(style)));
            return result;
        }
        if (kind == ModuleKind.ListViewStyle)
        {
            var styles = NativeV11Reflection.Get(settings, "ListViewStyleTemplates") as IEnumerable ?? throw new MissingMemberException("Settings.ListViewStyleTemplates");
            return styles.Cast<object>().Select(style => Document(new(kind, Name(style)), applicationCodec.SerializeValue(style))).ToArray();
        }
        if (kind == ModuleKind.ApplicationSettings) return [Document(new(kind, "ApplicationSettings"), applicationCodec.Capture(application))];
        if (kind == ModuleKind.DataConnection)
        {
            var codec = roots[kind].Codec;
            return Connections.Cast<object>().Select(connection => Document(new(kind, ConnectionId(connection)),
                codec.CaptureValue(connection, codec.DataType.GetGenericArguments()[0]))).ToArray();
        }
        if (kind == ModuleKind.PermissionGroup)
        {
            var permissionConfig = RootModel(kind);
            return NativeEntries(permissionConfig, "PermissionGroups").Select(group =>
                Document(new(kind, PermissionId(group)), roots[kind].Codec.SerializeValue(group))).ToArray();
        }
        if (kind == ModuleKind.CustomLibrary)
        {
            var librariesModel = NativeV11Reflection.Get(settings, "CustomLibraries")!;
            var result = new List<ModuleDocument>();
            foreach (var (folder, property) in LibraryLists)
                foreach (var library in (IEnumerable)NativeV11Reflection.Get(librariesModel, property)!)
                    result.Add(Document(new(kind, LibraryKey(library), folder), applicationCodec.SerializeValue(library)));
            return result;
        }
        if (!roots.TryGetValue(kind, out var root)) throw new NotSupportedException("This V11 settings module is not mapped.");
        // Native GetSaveModel reads the current model, rather than an old save-data memento.
        var model = NativeV11Reflection.Call(root.Colleague, "GetSaveModel") ?? throw new InvalidOperationException("The native settings model is unavailable.");
        return [Document(new(kind, RootName(kind)), root.Codec.Capture(model))];
    }

    internal ModuleDocument Read(ModuleAddress address)
    {
        NativeV11Session.ValidateAddress(address);
        if (address.FolderPath is null && address.Kind is ModuleKind.DataConnection or ModuleKind.CustomLibrary or ModuleKind.PermissionGroup && address.Name == RootName(address.Kind))
        {
            var root = roots[address.Kind];
            return Document(address, root.Codec.Capture(NativeV11Reflection.Call(root.Colleague, "GetSaveModel")!));
        }
        return List(address.Kind).SingleOrDefault(item => item.Address == address)
            ?? throw new KeyNotFoundException("No unique V11 settings entry matches the address.");
    }

    internal bool CanWrite(ModuleKind kind, ModuleOperation operation) =>
        kind == ModuleKind.PermissionGroup ? roots.ContainsKey(kind) && operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete :
        kind is ModuleKind.ApplicationSettings or ModuleKind.Theme ? operation == ModuleOperation.Update :
        kind is ModuleKind.GlobalVariable or ModuleKind.LanguageResource or ModuleKind.CellStyle or ModuleKind.ListViewStyle or ModuleKind.DataConnection or ModuleKind.CustomLibrary &&
        operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete;

    internal IReadOnlyList<string> SourcePaths(ModuleKind kind) => kind switch
    {
        ModuleKind.ApplicationSettings or ModuleKind.GlobalVariable or ModuleKind.LanguageResource => ["ApplicationSettings.json"],
        ModuleKind.CustomLibrary => ["CustomLibraries.json"],
        ModuleKind.DataConnection => ["DataConnectionSet.json"],
        ModuleKind.PermissionGroup when roots.ContainsKey(kind) => ["RbacPermissionGroup/PermissionGroups", "RbacPermissionGroup/RolePermissionGroup"],
        ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle => ["Theme/ThemeConfig", "Theme/CellTypeStyles", "Theme/ListViewStyles", "Theme/StyleDefaultNames"],
        _ => throw new NotSupportedException("No V11 settings source mapping exists.")
    };

    internal IReadOnlyDictionary<string, byte[]> CaptureSaveFiles(ModuleKind kind)
    {
        session.VerifyContext();
        if (kind is ModuleKind.ApplicationSettings or ModuleKind.GlobalVariable or ModuleKind.LanguageResource)
            return new Dictionary<string, byte[]> { ["ApplicationSettings.json"] = Encoding.UTF8.GetBytes(applicationCodec.Capture(application)) };
        if (kind is ModuleKind.CustomLibrary or ModuleKind.DataConnection)
        {
            var root = roots[kind];
            return new Dictionary<string, byte[]> { [SourcePaths(kind)[0]] = Encoding.UTF8.GetBytes(root.Codec.Capture(NativeV11Reflection.Call(root.Colleague, "GetSaveModel")!)) };
        }
        if (kind == ModuleKind.PermissionGroup && roots.TryGetValue(kind, out var permissions))
        {
            var config = RootModel(kind);
            return new Dictionary<string, byte[]>
            {
                [SourcePaths(kind)[0]] = Encoding.UTF8.GetBytes(permissions.Codec.SerializeValue(NativeV11Reflection.Get(config, "PermissionGroups")!)),
                [SourcePaths(kind)[1]] = Encoding.UTF8.GetBytes(permissions.Codec.SerializeValue(NativeV11Reflection.Get(config, "RolePermissionGroups")!))
            };
        }
        if (kind is ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle)
        {
            var info = NativeV11Reflection.Call(roots[ModuleKind.Theme].Colleague, "GetSaveModel")!;
            var common = AssemblyLoadContext.GetLoadContext(session.Context.DesignerAssembly)!.Assemblies.Single(assembly => assembly.GetName().Name == "CommonUtilities");
            var utilities = common.GetType("CommonUtilities.JsonUtilities", true)!;
            var fields = new[] { "ThemeItem", "CustomCellTypeStyleTemplates", "ListViewStyleTemplates", "StyleDefaultNames" };
            return fields.Select((field, index) => new KeyValuePair<string, byte[]>(SourcePaths(kind)[index],
                Encoding.UTF8.GetBytes((string)NativeV11Reflection.Static(utilities, "GetJsonString", NativeV11Reflection.Get(info, field)!, true)!)))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        }
        throw new NotSupportedException("No V11 settings save-file mapping exists.");
    }

    internal void Validate(ModuleMutationRequest request)
    {
        session.VerifyContext();
        NativeV11Session.ValidateAddress(request.Address);
        if (!CanWrite(request.Address.Kind, request.Operation)) throw new NotSupportedException("This V11 settings operation has no verified native write path.");
        if (Additional(request.Address.Kind)) { _ = PrepareAdditional(request); return; }
        if (!string.IsNullOrEmpty(request.Address.FolderPath)) throw new InvalidDataException("This V11 settings entry cannot have a module folder.");
        var existing = List(request.Address.Kind).SingleOrDefault(item => item.Address == request.Address);
        if (request.Operation == ModuleOperation.Create ? existing is not null : existing is null)
            throw new InvalidOperationException("The V11 settings create/update/delete baseline does not match the current collection.");
        if (request.Operation == ModuleOperation.Delete)
        {
            EnsureEntryUnreferenced(request.Address);
            return;
        }
        var document = request.Document ?? throw new InvalidDataException("The V11 settings definition is missing.");
        if (document.Address != request.Address || document.Companions.Count != 0)
            throw new InvalidDataException("A V11 settings definition has an unexpected address or companion file.");
        RejectDuplicateProperties(document.Metadata);
        if (request.Address.Kind == ModuleKind.ApplicationSettings)
        {
            if (request.Address.Name != "ApplicationSettings") throw new InvalidDataException("The V11 application settings root has a fixed name.");
            var parsed = applicationCodec.Parse(document.Metadata);
            var before = JsonNode.Parse(applicationCodec.Capture(application))!;
            var after = JsonNode.Parse(applicationCodec.SerializeValue(parsed))!;
            foreach (var property in RestartOnly)
                if (!JsonNode.DeepEquals(before[property], after[property]))
                    throw new NotSupportedException("The V11 setting requires reopening the project: " + property);
            _ = ChangedApplicationProperties(parsed);
        }
        else
        {
            var type = EntryType(request.Address.Kind);
            var parsed = applicationCodec.ParseValue(document.Metadata, type);
            if (request.Address.Kind == ModuleKind.GlobalVariable && Name(parsed) != request.Address.Name)
                throw new InvalidDataException("The global-variable name must match its module address.");
        }
    }

    internal Action Apply(ModuleMutationRequest request)
    {
        Validate(request);
        if (Additional(request.Address.Kind)) return ApplyAdditional(request);
        var undo = new List<Action>();
        var restored = false;
        var wasDirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var previousApplication = applicationCodec.Capture(application);
        try
        {
            if (request.Address.Kind == ModuleKind.ApplicationSettings)
            {
                var data = applicationCodec.Parse(request.Document!.Metadata);
                var model = applicationCodec.MapToModel(data);
                foreach (var property in ChangedApplicationProperties(data))
                {
                    var oldValue = property.GetValue(application);
                    var newValue = property.GetValue(model);
                    undo.Add(() => property.SetValue(application, oldValue));
                    property.SetValue(application, newValue);
                }
            }
            else if (request.Address.Kind == ModuleKind.GlobalVariable)
            {
                var list = Globals;
                var old = list.Cast<object>().SingleOrDefault(value => Name(value) == request.Address.Name);
                var index = old is null ? list.Count : list.IndexOf(old);
                if (request.Operation == ModuleOperation.Create)
                {
                    var created = applicationCodec.ParseValue(request.Document!.Metadata, EntryType(request.Address.Kind));
                    undo.Add(() => list.Remove(created));
                    list.Add(created);
                }
                else if (request.Operation == ModuleOperation.Update)
                {
                    var incoming = applicationCodec.ParseValue(request.Document!.Metadata, EntryType(request.Address.Kind));
                    undo.Add(() => list[index] = old);
                    list[index] = incoming;
                }
                else
                {
                    undo.Add(() => list.Insert(index, old));
                    list.RemoveAt(index);
                }
            }
            else
            {
                var dictionary = Resources;
                var key = request.Address.Name;
                var existed = dictionary.Contains(key);
                var old = existed ? dictionary[key] : null;
                undo.Add(() => { if (existed) dictionary[key] = old; else dictionary.Remove(key); });
                if (request.Operation == ModuleOperation.Delete) dictionary.Remove(key);
                else dictionary[key] = applicationCodec.ParseValue(request.Document!.Metadata, EntryType(request.Address.Kind));
            }
            StageApplication();
            if (request.Operation == ModuleOperation.Delete)
            {
                if (List(request.Address.Kind).Any(item => item.Address == request.Address))
                    throw new InvalidOperationException("The V11 settings entry was not removed from native memory.");
            }
            else if (!V11Safety.Equivalent(Read(request.Address).Metadata, request.Document!.Metadata))
                throw new InvalidOperationException("The V11 settings native memory readback differs from the requested definition.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception primary)
        {
            try { Rollback(); }
            catch (Exception rollback) { throw new AggregateException("Native application settings mutation and rollback both failed.", primary, rollback); }
            throw;
        }
        return Rollback;

        void Rollback()
        {
            if (restored) return;
            for (var index = undo.Count - 1; index >= 0; index--) undo[index]();
            StageApplication();
            if (!V11Safety.Equivalent(Parse(applicationCodec.Capture(application)), Parse(previousApplication)))
                throw new InvalidOperationException("The V11 settings rollback did not restore native memory.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty);
            restored = true;
        }
    }

    private sealed record AdditionalChange(ModuleKind Owner, object Before, object Incoming);
    private static bool Additional(ModuleKind kind) => kind is ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle or ModuleKind.DataConnection or ModuleKind.CustomLibrary or ModuleKind.PermissionGroup;
    private static readonly (string Folder, string Property)[] LibraryLists = [("javascript", "UserJSFileList"), ("css", "UserCSSFileList")];
    private IList Connections => NativeV11Reflection.Get(settings, "DataConnectionSet") as IList ?? throw new MissingMemberException("Settings.DataConnectionSet");
    private static string ConnectionId(object value) => NativeV11Reflection.Get(value, "ID") as string ?? throw new InvalidDataException("The native connection ID is missing.");
    private static string StyleKey(object value) => NativeV11Reflection.Get(value, "Key") as string ?? throw new InvalidDataException("The native cell-style key is missing.");
    private static string PermissionId(object value) => NativeV11Reflection.Get(value, "Id") as string ?? throw new InvalidDataException("The native permission-group ID is missing.");
    private static IEnumerable<object> NativeEntries(object model, string property) =>
        NativeV11Reflection.Get(model, property) is IEnumerable entries ? entries.Cast<object>() : Array.Empty<object>();
    internal static IList EnsureNativeList(object model, string property)
    {
        if (NativeV11Reflection.Get(model, property) is IList entries) return entries;
        var member = model.GetType().GetProperty(property, NativeV11Reflection.Flags) ?? throw new MissingMemberException(model.GetType().Name, property);
        var created = (IList)(Activator.CreateInstance(member.PropertyType) ?? throw new InvalidDataException("The native settings list cannot be initialized."));
        member.SetValue(model, created);
        return created;
    }
    internal static string LibraryKey(object value)
    {
        var name = Name(value);
        return name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\')
            ? NativeV11Session.Hash(Encoding.UTF8.GetBytes(name)) : name;
    }

    private object RootModel(ModuleKind owner) => owner switch
    {
        ModuleKind.DataConnection => Connections,
        ModuleKind.CustomLibrary => NativeV11Reflection.Get(settings, "CustomLibraries")!,
        ModuleKind.Theme => NativeV11Reflection.Call(roots[ModuleKind.Theme].Colleague, "GetSaveModel")!,
        ModuleKind.PermissionGroup => NativeV11Reflection.Get(settings, "PermissionGroupConfig")!,
        _ => throw new NotSupportedException()
    };

    private object CloneRoot(ModuleKind owner, object model)
    {
        var codec = roots[owner].Codec;
        var metadata = Parse(codec.Capture(model));
        return owner is ModuleKind.Theme or ModuleKind.PermissionGroup ? codec.ParseNativeValue(metadata, codec.ModelType) : codec.MapToModel(codec.Parse(metadata));
    }

    private AdditionalChange PrepareAdditional(ModuleMutationRequest request)
    {
        var kind = request.Address.Kind;
        var owner = kind is ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle ? ModuleKind.Theme : kind;
        var codec = roots[owner].Codec;
        var before = CloneRoot(owner, RootModel(owner));
        var incoming = CloneRoot(owner, RootModel(owner));
        if (kind == ModuleKind.PermissionGroup && !Equals(NativeV11Reflection.Get(application, "PermissionMode")?.ToString(), "PermissionGroup"))
            throw new NotSupportedException("Native permission-group definitions can be edited only when the current project already uses PermissionGroup mode; the adapter does not switch modes.");
        if (request.Operation != ModuleOperation.Delete)
        {
            if (request.Document is null || request.Document.Address != request.Address || request.Document.Companions.Count != 0)
                throw new InvalidDataException("The V11 settings entry address or companion list is invalid.");
            RejectDuplicateProperties(request.Document.Metadata);
        }
        else if (request.Document is not null) throw new InvalidDataException("A V11 settings deletion must not contain replacement metadata.");
        if (kind == ModuleKind.Theme)
        {
            if (request.Address.Name != "Theme" || request.Address.FolderPath is not null || request.Operation != ModuleOperation.Update)
                throw new InvalidDataException("The V11 current-theme root supports update only.");
            incoming = codec.ParseNativeValue(request.Document!.Metadata, codec.ModelType);
            var a = JsonNode.Parse(codec.Capture(before))!;
            var b = JsonNode.Parse(codec.Capture(incoming))!;
            foreach (var property in new[] { "RootPath", "CustomCellTypeStyleTemplates", "ListViewStyleTemplates", "StyleDefaultNames" })
                if (!JsonNode.DeepEquals(a[property], b[property])) throw new NotSupportedException("Use the dedicated native V11 style entry API, and preserve the current theme root path/defaults.");
            foreach (var property in new[] { "Name", "BasePathName", "Category", "Images", "Thumbnail" })
                if (!JsonNode.DeepEquals(a["ThemeItem"]?[property], b["ThemeItem"]?[property]))
                    throw new NotSupportedException("A V11 current-theme update must preserve theme identity and existing image resources.");
            return new(owner, before, incoming);
        }
        if (kind == ModuleKind.CustomLibrary && request.Address.FolderPath is null && request.Address.Name == "CustomLibraries")
        {
            if (request.Operation != ModuleOperation.Update) throw new NotSupportedException("The custom-library root cannot be created or deleted.");
            incoming = codec.MapToModel(codec.Parse(request.Document!.Metadata));
            ValidateLibraries(incoming);
            foreach (var (folder, property) in LibraryLists)
            {
                var retained = ((IEnumerable)NativeV11Reflection.Get(incoming, property)!).Cast<object>().Select(Name).ToHashSet(StringComparer.Ordinal);
                foreach (var removed in ((IEnumerable)NativeV11Reflection.Get(before, property)!).Cast<object>().Where(value => !retained.Contains(Name(value))))
                    EnsureNoStaticReferences(new(ModuleKind.CustomLibrary, LibraryKey(removed), folder), Name(removed));
            }
            return new(owner, before, incoming);
        }
        if (kind == ModuleKind.PermissionGroup && request.Address.FolderPath is null && request.Address.Name == "PermissionGroupConfig")
        {
            if (request.Operation != ModuleOperation.Update) throw new NotSupportedException("The native permission-group root cannot be created or deleted.");
            incoming = codec.ParseNativeValue(request.Document!.Metadata, codec.ModelType);
            ValidatePermissionGroups(before, incoming);
            return new(owner, before, incoming);
        }
        if (kind == ModuleKind.DataConnection && request.Address.FolderPath is null && request.Address.Name == "DataConnectionSet")
            throw new NotSupportedException("The connection root is read-only; create/update/delete its ID-addressed definition entries.");

        IList entries;
        Func<object, string> key;
        Type entryModel;
        Type entryData;
        if (kind == ModuleKind.DataConnection)
        {
            if (request.Address.FolderPath is not null) throw new InvalidDataException("A V11 connection ID has no folder.");
            entries = (IList)incoming; key = ConnectionId;
            entryModel = codec.ModelType.GetGenericArguments()[0]; entryData = codec.DataType.GetGenericArguments()[0];
        }
        else if (kind == ModuleKind.PermissionGroup)
        {
            if (request.Address.FolderPath is not null) throw new InvalidDataException("A native permission-group ID has no folder.");
            entries = EnsureNativeList(incoming, "PermissionGroups"); key = PermissionId;
            _ = EnsureNativeList(incoming, "RolePermissionGroups");
            entryModel = entries.GetType().GetGenericArguments()[0]; entryData = entryModel;
        }
        else if (kind == ModuleKind.CustomLibrary)
        {
            var property = LibraryLists.SingleOrDefault(item => item.Folder == request.Address.FolderPath).Property;
            if (property is null) throw new InvalidDataException("A V11 custom-library entry folder must be javascript or css.");
            entries = (IList)NativeV11Reflection.Get(incoming, property)!; key = LibraryKey;
            entryModel = entries.GetType().GetGenericArguments()[0]; entryData = entryModel;
        }
        else if (kind == ModuleKind.CellStyle)
        {
            if (string.IsNullOrWhiteSpace(request.Address.FolderPath) || request.Address.FolderPath.Contains('/'))
                throw new InvalidDataException("A V11 cell style requires its exact native style-group key as its folder.");
            var groups = (IDictionary)NativeV11Reflection.Get(incoming, "CustomCellTypeStyleTemplates")!;
            if (!groups.Contains(request.Address.FolderPath))
            {
                if (request.Operation != ModuleOperation.Create) throw new KeyNotFoundException("The native V11 style group does not exist.");
                groups.Add(request.Address.FolderPath, Activator.CreateInstance(groups.GetType().GetGenericArguments()[1]));
            }
            entries = (IList)groups[request.Address.FolderPath]!; key = StyleKey;
            entryModel = entries.GetType().GetGenericArguments()[0]; entryData = entryModel;
        }
        else
        {
            if (request.Address.FolderPath is not null) throw new InvalidDataException("A V11 list-view style has no folder.");
            entries = (IList)NativeV11Reflection.Get(incoming, "ListViewStyleTemplates")!; key = Name;
            entryModel = entries.GetType().GetGenericArguments()[0]; entryData = entryModel;
        }
        var previous = entries.Cast<object>().Where(value => key(value) == request.Address.Name).ToArray();
        if (previous.Length > 1) throw new InvalidDataException("The native V11 settings entry identity is ambiguous.");
        var old = previous.SingleOrDefault();
        if (request.Operation == ModuleOperation.Create ? old is not null : old is null)
            throw new InvalidOperationException("The native V11 settings entry does not match its create/update/delete operation.");
        if (kind is ModuleKind.CellStyle or ModuleKind.ListViewStyle)
        {
            if (old is not null) ProtectStyle(kind, old, before, request.Address, request.Operation);
        }
        if (kind == ModuleKind.PermissionGroup && old is not null && NativeV11Reflection.Get(old, "IsDefault") is true)
            throw new InvalidOperationException("Native default permission groups are protected.");
        object? replacement = null;
        if (request.Operation != ModuleOperation.Delete)
        {
            replacement = kind is ModuleKind.CellStyle or ModuleKind.ListViewStyle
                ? codec.ParseNativeValue(request.Document!.Metadata, entryData) : codec.ParseValue(request.Document!.Metadata, entryData);
            if (kind == ModuleKind.DataConnection) replacement = codec.MapValue(replacement, entryModel);
            if (key(replacement) != request.Address.Name) throw new InvalidDataException("V11 settings updates preserve the native entry identity; use root/entry creation and deletion for a rename.");
            if (kind is ModuleKind.CellStyle or ModuleKind.ListViewStyle) ProtectStyle(kind, replacement, incoming, request.Address, ModuleOperation.Create);
        }
        if (request.Operation == ModuleOperation.Delete)
        {
            if (kind == ModuleKind.DataConnection) EnsureConnectionUnreferenced(request.Address.Name);
            else EnsureNoStaticReferences(request.Address, kind == ModuleKind.CustomLibrary ? Name(old!) : request.Address.Name);
        }
        ChangeCollection(entries, request.Operation, old, replacement);
        if (kind == ModuleKind.CustomLibrary) ValidateLibraries(incoming);
        if (kind == ModuleKind.PermissionGroup) ValidatePermissionGroups(before, incoming);
        return new(owner, before, incoming);
    }

    internal static Action ChangeCollection(IList entries, ModuleOperation operation, object? old, object? replacement)
    {
        var original = entries.Cast<object>().ToArray();
        if (operation == ModuleOperation.Create) entries.Add(replacement ?? throw new InvalidDataException("A native settings entry is missing."));
        else
        {
            var index = old is null ? -1 : entries.IndexOf(old);
            if (index < 0) throw new InvalidOperationException("The native settings entry changed before editing.");
            if (operation == ModuleOperation.Update) entries[index] = replacement ?? throw new InvalidDataException("A native settings entry is missing.");
            else if (operation == ModuleOperation.Delete) entries.RemoveAt(index);
            else throw new InvalidDataException("Invalid native settings collection operation.");
        }
        var restored = false;
        return () => { if (restored) return; entries.Clear(); foreach (var entry in original) entries.Add(entry); restored = true; };
    }

    private Action ApplyAdditional(ModuleMutationRequest request)
    {
        var prepared = PrepareAdditional(request);
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var restored = false;
        try
        {
            LoadAdditionalRoot(prepared.Owner, prepared.Incoming);
            StageAdditional(prepared.Owner);
            if (request.Operation != ModuleOperation.Delete && !V11Safety.Equivalent(Read(request.Address).Metadata, request.Document!.Metadata))
                throw new InvalidOperationException("The V11 settings native model readback differs after editing.");
            if (request.Operation == ModuleOperation.Delete && List(request.Address.Kind).Any(document => document.Address == request.Address))
                throw new InvalidOperationException("The V11 settings entry remains after deletion.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception primary)
        {
            try { Rollback(); }
            catch (Exception rollback) { throw new AggregateException("Native settings mutation and rollback both failed.", primary, rollback); }
            throw;
        }
        return Rollback;
        void Rollback()
        {
            if (restored) return;
            LoadAdditionalRoot(prepared.Owner, prepared.Before);
            StageAdditional(prepared.Owner);
            if (!V11Safety.Equivalent(Parse(roots[prepared.Owner].Codec.Capture(prepared.Before)), Parse(roots[prepared.Owner].Codec.Capture(RootModel(prepared.Owner)))))
                throw new InvalidOperationException("The V11 settings native root rollback was not confirmed.");
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
            restored = true;
        }
    }

    private void LoadAdditionalRoot(ModuleKind owner, object incoming)
    {
        if (owner == ModuleKind.DataConnection)
        {
            Connections.Clear(); foreach (var value in (IEnumerable)incoming) Connections.Add(value); return;
        }
        if (owner == ModuleKind.CustomLibrary)
        {
            NativeV11Reflection.Call(NativeV11Reflection.Get(settings, "CustomLibraries")!, "Reset", incoming); return;
        }
        if (owner == ModuleKind.PermissionGroup)
        {
            NativeV11Reflection.Call(NativeV11Reflection.Get(settings, "PermissionGroupConfig")!, "Reset", incoming); return;
        }
        // The native theme loader merges style collections. Clear their live
        // contents first, retaining the native settings objects and all defaults.
        ((IDictionary)NativeV11Reflection.Get(settings, "CustomCellTypeStyleTemplates")!).Clear();
        ((IList)NativeV11Reflection.Get(settings, "ListViewStyleTemplates")!).Clear();
        ((IDictionary)NativeV11Reflection.Get(settings, "DefaultStyleKeys")!).Clear();
        var manager = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.ThemesManager", true)!, "get_Instance")!;
        NativeV11Reflection.Call(manager, "LoadByThemeInfo", incoming);
    }

    private void StageAdditional(ModuleKind owner)
    {
        NativeV11Reflection.Call(roots[owner].Colleague, "Save", RootModel(owner));
        MarkDirty(owner switch { ModuleKind.Theme => "Theme", ModuleKind.DataConnection => "DataContectionSet", ModuleKind.CustomLibrary => "CustomLibrary", ModuleKind.PermissionGroup => "PermissionGroupConfig", _ => throw new NotSupportedException() });
    }

    private static void ProtectStyle(ModuleKind kind, object style, object theme, ModuleAddress address, ModuleOperation operation)
    {
        var category = NativeV11Reflection.Get(style, kind == ModuleKind.CellStyle ? "Category" : "Group") as string;
        if (category != "_RS_Custom" || address.Name.StartsWith("_RS_", StringComparison.Ordinal))
            throw new InvalidOperationException("Only project custom styles can be changed; native system styles are protected.");
        if (operation != ModuleOperation.Delete) return;
        var defaults = NativeV11Reflection.Get(theme, "StyleDefaultNames")!;
        if (kind == ModuleKind.ListViewStyle && Equals(NativeV11Reflection.Get(defaults, "DefaultListViewStyleName"), address.Name) ||
            kind == ModuleKind.CellStyle && NativeV11Reflection.Get(defaults, "DefaultCellTypeStyleNames") is IDictionary names && Equals(names[address.FolderPath!], address.Name))
            throw new InvalidOperationException("The selected default V11 style cannot be deleted.");
    }

    private void ValidateLibraries(object model)
    {
        foreach (var (_, property) in LibraryLists)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in (IEnumerable)NativeV11Reflection.Get(model, property)!)
            {
                var name = Name(entry);
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name)) throw new InvalidDataException("Custom-library names cannot be blank or duplicated.");
                if (NativeV11Reflection.Get(entry, "IsLink") is true)
                {
                    if (Uri.TryCreate(name, UriKind.Absolute, out var uri) && uri.Scheme is not ("http" or "https"))
                        throw new InvalidDataException("A custom-library absolute link must use HTTP or HTTPS.");
                    if (!Uri.IsWellFormedUriString(name, UriKind.RelativeOrAbsolute)) throw new InvalidDataException("The custom-library link is not a valid URI.");
                }
                else
                {
                    var file = NativeV11Session.Under(Path.Combine(session.LocalPath, "UserJaveScript"), name);
                    if (!File.Exists(file)) throw new FileNotFoundException("Import the local custom-library resource before registering its definition.");
                }
            }
        }
    }

    private void ValidatePermissionGroups(object before, object incoming)
    {
        var previous = NativeEntries(before, "PermissionGroups").ToArray();
        var groups = NativeEntries(incoming, "PermissionGroups").ToArray();
        var ids = groups.Select(PermissionId).ToArray();
        if (ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new InvalidDataException("Native permission-group IDs cannot be blank or duplicated.");
        var byId = groups.ToDictionary(PermissionId, StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var id = PermissionId(group); NativeV11Session.ValidateAddress(new(ModuleKind.PermissionGroup, id));
            var name = Name(group);
            var parent = NativeV11Reflection.Get(group, "ParentId") as string;
            if (string.IsNullOrWhiteSpace(name) || !names.Add((parent ?? "") + "\0" + name))
                throw new InvalidDataException("Native permission-group names cannot be blank or duplicated within a parent.");
            var kind = NativeV11Reflection.Get(group, "Type")!;
            if (kind is not Enum nativeType || !Enum.IsDefined(nativeType.GetType(), nativeType))
                throw new InvalidDataException("The native permission-group type is invalid.");
            var visited = new HashSet<string>(StringComparer.Ordinal) { id };
            while (!string.IsNullOrEmpty(parent))
            {
                if (!visited.Add(parent) || !byId.TryGetValue(parent, out var ancestor))
                    throw new InvalidDataException("Native permission-group parent references must exist and cannot form a cycle.");
                parent = NativeV11Reflection.Get(ancestor, "ParentId") as string;
            }
            if (NativeV11Reflection.Get(group, "IsDefault") is true && !previous.Any(value => PermissionId(value) == id))
                throw new InvalidOperationException("The adapter cannot create a native default permission group.");
        }
        foreach (var protectedGroup in previous.Where(value => NativeV11Reflection.Get(value, "IsDefault") is true))
            if (!byId.TryGetValue(PermissionId(protectedGroup), out var retained) ||
                !V11Safety.Equivalent(Parse(roots[ModuleKind.PermissionGroup].Codec.SerializeValue(protectedGroup)), Parse(roots[ModuleKind.PermissionGroup].Codec.SerializeValue(retained))))
                throw new InvalidOperationException("Native default permission groups must remain unchanged.");
        var bindings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var binding in NativeEntries(incoming, "RolePermissionGroups"))
        {
            var role = NativeV11Reflection.Get(binding, "RoleId") as string;
            var group = NativeV11Reflection.Get(binding, "PermissionGroupId") as string;
            if (string.IsNullOrWhiteSpace(role) || string.IsNullOrWhiteSpace(group) || !byId.ContainsKey(group) || !bindings.Add(role + "\0" + group))
                throw new InvalidDataException("Native permission-group role bindings must have unique nonempty IDs and reference an existing group.");
        }
        foreach (var removed in previous.Where(value => !byId.ContainsKey(PermissionId(value))))
            EnsureNoStaticReferences(new(ModuleKind.PermissionGroup, PermissionId(removed)), PermissionId(removed));
    }

    private void EnsureConnectionUnreferenced(string id)
    {
        foreach (var route in session.Routes)
        {
            _ = session.Items(route.Kind);
            if (route.Kind == ModuleKind.Table) _ = session.Items(ModuleKind.View);
        }
        var helper = session.Context.DesignerAssembly.GetType(session.Profile.Version == "11.0.104.0" ? "rg" : "rc", true)!;
        foreach (var located in (IEnumerable)NativeV11Reflection.Static(helper, "EnumReferenceDataConnection")!)
        {
            var value = NativeV11Reflection.Get(located!, "Object") ?? throw new InvalidDataException("The native connection reference could not be resolved.");
            var contract = value.GetType().GetInterfaces().Single(type => type.Name == "IReferenceDataConnection");
            var ids = (IEnumerable)contract.GetMethod("EnumConnectionId")!.Invoke(value, null)!;
            if (ids.Cast<object>().Any(reference => Equals(reference, id))) throw new InvalidOperationException("The native V11 connection definition is still referenced.");
        }
    }

    internal ReferenceResult FindReferences(ModuleAddress address)
    {
        if (address.Kind is ModuleKind.GlobalVariable or ModuleKind.LanguageResource) return FindEntryReferences(address);
        session.VerifyContext();
        try
        {
            if (address.Kind == ModuleKind.DataConnection) { EnsureConnectionUnreferenced(address.Name); return new([], true); }
            var identifier = address.Kind == ModuleKind.CustomLibrary ? Read(address).Metadata.GetProperty("Name").GetString()! : address.Name;
            return StaticReferences(address, identifier);
        }
        catch { return new([], false, "Native V11 settings references could not be proved absent; values are omitted."); }
    }

    private void EnsureNoStaticReferences(ModuleAddress address, string identifier)
    {
        var references = StaticReferences(address, identifier);
        if (!references.Complete || references.References.Count > 0) throw new InvalidOperationException("The native V11 settings entry remains referenced, or the native tree is incomplete.");
    }

    private ReferenceResult StaticReferences(ModuleAddress address, string identifier)
    {
        var references = new List<ModuleReference>();
        foreach (var route in session.Routes)
            foreach (var kind in route.Kind == ModuleKind.Table ? new[] { ModuleKind.Table, ModuleKind.View } : new[] { route.Kind })
                foreach (var item in session.Items(kind))
                {
                    var definition = session.Read(item);
                    if (ContainsName(definition.Metadata, identifier) || definition.Companions.Any(asset => Encoding.UTF8.GetString(Convert.FromBase64String(asset.ContentBase64)).Contains(identifier, StringComparison.Ordinal)))
                        references.Add(new(item.Address, address, "native definition or companion contains the settings identifier"));
                }
        foreach (var kind in new[] { ModuleKind.ApplicationSettings, ModuleKind.Theme, ModuleKind.CustomLibrary, ModuleKind.DataConnection, ModuleKind.PermissionGroup }.Where(roots.ContainsKey))
        {
            var nativeRoot = kind == ModuleKind.ApplicationSettings ? application : RootModel(kind);
            var json = JsonNode.Parse(roots[kind].Codec.Capture(nativeRoot))!;
            if (kind == ModuleKind.Theme && address.Kind == ModuleKind.CellStyle && json["CustomCellTypeStyleTemplates"]?[address.FolderPath!] is JsonArray cellStyles)
                RemoveOwnEntry(cellStyles, "Key", address.Name);
            if (kind == ModuleKind.Theme && address.Kind == ModuleKind.ListViewStyle && json["ListViewStyleTemplates"] is JsonArray listStyles)
                RemoveOwnEntry(listStyles, "Name", address.Name);
            if (kind == ModuleKind.CustomLibrary && address.Kind == ModuleKind.CustomLibrary)
            {
                var property = LibraryLists.Single(item => item.Folder == address.FolderPath).Property;
                if (json[property] is JsonArray libraries) RemoveOwnEntry(libraries, "Name", identifier);
            }
            if (kind == ModuleKind.PermissionGroup && address.Kind == ModuleKind.PermissionGroup)
            {
                if (json["PermissionGroups"] is JsonArray groups) RemoveOwnEntry(groups, "id", address.Name);
            }
            if (ContainsName(Parse(json.ToJsonString()), identifier))
                references.Add(new(new(kind, kind == ModuleKind.ApplicationSettings ? "ApplicationSettings" : RootName(kind)), address,
                    "native global settings contains the settings identifier"));
        }
        var custom = RootModel(ModuleKind.CustomLibrary);
        foreach (var (folder, property) in LibraryLists)
            foreach (var library in (IEnumerable)NativeV11Reflection.Get(custom, property)!)
            {
                if (NativeV11Reflection.Get(library, "IsLink") is true) continue;
                var source = new ModuleAddress(ModuleKind.CustomLibrary, LibraryKey(library), folder);
                if (source == address) continue;
                var file = NativeV11Session.Under(Path.Combine(session.LocalPath, "UserJaveScript"), Name(library));
                if (!File.Exists(file)) throw new FileNotFoundException("A native registered script/CSS resource is missing; reference discovery is incomplete.");
                if (File.ReadAllText(file).Contains(identifier, StringComparison.Ordinal))
                    references.Add(new(source, address, "native registered local script/CSS contains the settings identifier"));
            }
        return new(references, true, "Conservative native-definition reference scan; dynamically constructed script references require caller review.");

        static void RemoveOwnEntry(JsonArray entries, string property, string value)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
                if (entries[index]?[property]?.GetValue<string>() == value) entries.RemoveAt(index);
        }
    }

    private IReadOnlyList<PropertyInfo> ChangedApplicationProperties(object data)
    {
        var before = JsonNode.Parse(applicationCodec.Capture(application))!.AsObject();
        var after = JsonNode.Parse(applicationCodec.SerializeValue(data))!.AsObject();
        var changed = before.Select(property => property.Key).Union(after.Select(property => property.Key), StringComparer.Ordinal)
            .Where(name => !JsonNode.DeepEquals(before[name], after[name])).ToArray();
        var result = new List<PropertyInfo>();
        foreach (var name in changed)
        {
            if (name is "GlobalProperties" or "MultipleLanguageSettings" or "ShareCommandContainers")
                throw new NotSupportedException("Use the dedicated native V11 entry API to change this settings collection: " + name);
            var property = application.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public);
            if (property?.SetMethod?.IsPublic is not true)
                throw new NotSupportedException("The V11 application-setting field requires a dedicated native editor: " + name);
            result.Add(property);
        }
        return result;
    }

    private void StageApplication()
    {
        // Save(T) is the real model-colleague -> mediator -> SaveToCache pipeline.
        NativeV11Reflection.Call(applicationColleague, "Save", application);
        MarkDirty("ApplicationSetting");
        if (session.Context.DesignerAssembly.GetType("Forguncy.Common.MultipleLanguageHelper", false) is { } helper)
            NativeV11Reflection.Static(helper, "RefreshWhenLanguageResourceChanged");
    }

    private void MarkDirty(string moduleName)
    {
        NativeV11Generation.Mark(session, moduleName);
        var cache = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType(session.Profile.GenerationCacheType, true)!, "get_Instance")!;
        NativeV11Reflection.Call(cache, "DirtyAllPages");
    }

    private void EnsureEntryUnreferenced(ModuleAddress address)
    {
        var result = FindEntryReferences(address);
        if (!result.Complete) throw new ForguncyDesignerMcp.Core.BridgeException("SettingsReferenceScanIncomplete", result.Reason ?? "The native settings reference scan is incomplete.");
        if (result.References.Count != 0) throw new ForguncyDesignerMcp.Core.BridgeException("SettingsEntryReferenced", result.Reason ?? "The native settings entry remains referenced.");
    }

    // Reports only model paths/phases and exception classes. Native exception
    // messages and application-setting values may contain credentials and are
    // deliberately excluded. Unloaded/inaccessible roots remain incomplete.
    internal ReferenceResult FindEntryReferences(ModuleAddress address)
    {
        try { session.VerifyContext(); }
        catch (Exception error) { return new([], false, "Native entry reference scan failed at context (" + ErrorClass(error) + ")."); }
        var references = new List<ModuleReference>();
        if (address.Kind == ModuleKind.LanguageResource)
        {
            try
            {
                var type = session.Context.DesignerAssembly.GetType("Forguncy.Common.ReferenceHelper.MultipleLanguageResourceReferenceHelper", true)!;
                var used = (IEnumerable)NativeV11Reflection.Static(type, "EnumUsedResourceKey")!;
                if (used.Cast<object>().Any(key => Equals(key, address.Name)))
                    references.Add(new(new(ModuleKind.ApplicationSettings, "ApplicationSettings"), address, "native language resource key enumeration"));
                return new(references, true, references.Count == 0 ? "Native language resource references are absent." : "The native language resource remains referenced.");
            }
            catch (Exception error) { return new(references, false, "Native entry reference scan failed at language-key enumeration (" + ErrorClass(error) + ")."); }
        }
        if (address.Kind != ModuleKind.GlobalVariable) return new([], false, "This native entry-reference scanner accepts GlobalVariable and LanguageResource only.");
        // Global names can occur in native formulas and script expressions. Scan
        // every fully loaded native module conservatively; an incomplete tree is
        // an error, and any occurrence blocks removal.
        foreach (var route in session.Routes)
            foreach (var kind in route.Kind == ModuleKind.Table ? new[] { ModuleKind.Table, ModuleKind.View } : new[] { route.Kind })
            {
                IReadOnlyList<NativeV11Item> items;
                try { items = session.Items(kind); }
                catch (Exception error)
                {
                    return new(references, false, "Native global reference scan incomplete at list " + route.Folder + " kind=" + kind +
                        " root=" + RootState(route.Root) + " (" + ErrorClass(error) + ").");
                }
                foreach (var item in items)
                    try
                    {
                        var definition = session.Read(item);
                        if (ContainsName(definition.Metadata, address.Name) ||
                            definition.Companions.Any(asset => Encoding.UTF8.GetString(Convert.FromBase64String(asset.ContentBase64)).Contains(address.Name, StringComparison.Ordinal)))
                            references.Add(new(item.Address, address, "native definition or companion at " + item.RelativePath));
                    }
                    catch (Exception error)
                    { return new(references, false, "Native global reference scan incomplete at read " + item.RelativePath + " kind=" + kind + " (" + ErrorClass(error) + ")."); }
            }
        try
        {
            if (ApplicationReferencesGlobal(applicationCodec, application, address.Name))
                references.Add(new(new(ModuleKind.ApplicationSettings, "ApplicationSettings"), address, "native application settings excluding the unique target declaration"));
        }
        catch (Exception error) { return new(references, false, "Native global reference scan incomplete at application model clone/capture (" + ErrorClass(error) + ")."); }
        return new(references, true, references.Count == 0 ? "The complete native global-variable reference scan found no references." : "The global-variable name remains in native definitions or application settings.");

        static string ErrorClass(Exception error)
        {
            while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException;
            return error.GetType().Name;
        }
        static string RootState(object root)
        {
            try
            {
                var loaded = NativeV11Reflection.Get(root, "Loaded") is bool value ? value ? "loaded" : "unloaded" : "loaded-undefined";
                var count = NativeV11Reflection.Get(root, "Items") is IList entries ? entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "unknown";
                return loaded + ",children=" + count;
            }
            catch { return "unavailable"; }
        }
    }

    internal static bool ApplicationReferencesGlobal(NativeV11SettingsCodec codec, object applicationModel, string name)
    {
        // Exclude the declaration through the native model/mapper, rather than
        // assuming a JSON array versus a native $values representation. Only
        // the detached clone is changed; all other application fields remain.
        var clone = codec.MapToModel(codec.Parse(Parse(codec.Capture(applicationModel))));
        var originals = NativeV11Reflection.Get(applicationModel, "GlobalProperties") as IList ?? throw new MissingMemberException("ApplicationSettings.GlobalProperties");
        var entries = NativeV11Reflection.Get(clone, "GlobalProperties") as IList ?? throw new MissingMemberException("ClonedApplicationSettings.GlobalProperties");
        if (ReferenceEquals(originals, entries)) throw new InvalidOperationException("The native application mapper did not detach its global collection.");
        var matches = entries.Cast<object>().Where(entry => Name(entry) == name).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The native target global declaration is missing or ambiguous.");
        entries.Remove(matches[0]);
        return ContainsName(Parse(codec.Capture(clone)), name);
    }

    private IList Globals => NativeV11Reflection.Get(application, "GlobalProperties") as IList
        ?? throw new MissingMemberException("ApplicationSettings.GlobalProperties");
    private IDictionary Resources => NativeV11Reflection.Get(NativeV11Reflection.Get(application, "MultipleLanguageSettings")
        ?? throw new MissingMemberException("ApplicationSettings.MultipleLanguageSettings"), "PublicResource") as IDictionary
        ?? throw new MissingMemberException("MultipleLanguageSettings.PublicResource");
    private Type EntryType(ModuleKind kind) => kind == ModuleKind.GlobalVariable ? Globals.GetType().GetGenericArguments()[0] : Resources.GetType().GetGenericArguments()[1];
    private static string Name(object value) => NativeV11Reflection.Get(value, "Name") as string
        ?? throw new InvalidDataException("The native V11 settings entry has no name.");
    private static string RootName(ModuleKind kind) => kind switch { ModuleKind.CustomLibrary => "CustomLibraries", ModuleKind.DataConnection => "DataConnectionSet", ModuleKind.Theme => "Theme", ModuleKind.PermissionGroup => "PermissionGroupConfig", _ => throw new NotSupportedException() };
    private static JsonElement Parse(string text) { using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone(); }
    private static ModuleDocument Document(ModuleAddress address, string json)
    {
        var metadata = Parse(json);
        return new(address, metadata, [], NativeV11Session.Hash(Encoding.UTF8.GetBytes(metadata.GetRawText())));
    }
    private static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("The V11 settings JSON contains a duplicate property.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) RejectDuplicateProperties(child);
    }
    private static bool ContainsName(JsonElement node, string name) => node.ValueKind switch
    {
        JsonValueKind.String => node.GetString()!.Contains(name, StringComparison.Ordinal),
        JsonValueKind.Object => node.EnumerateObject().Any(property => property.Name.Equals(name, StringComparison.Ordinal) || ContainsName(property.Value, name)),
        JsonValueKind.Array => node.EnumerateArray().Any(child => ContainsName(child, name)),
        _ => false
    };
}

// This codec also supports isolated component tests without constructing a live
// document or using a fake project adapter.
internal sealed class NativeV11SettingsCodec
{
    private readonly Assembly designer;
    private readonly object mapper;
    private readonly object loader;
    private readonly Type modelType;
    private readonly Type dataType;
    private readonly Type generator;
    internal Type ModelType => modelType;
    internal Type DataType => dataType;

    internal NativeV11SettingsCodec(Assembly designer, object colleague)
    {
        this.designer = designer;
        var profile = V11BuildProfile.Resolve(designer);
        var generic = colleague.GetType();
        while (!generic.IsGenericType && generic.BaseType is not null) generic = generic.BaseType;
        if (!generic.IsGenericType || generic.GetGenericArguments().Length != 2)
            throw new NotSupportedException("The V11 settings model-colleague contract changed.");
        modelType = generic.GetGenericArguments()[0];
        dataType = generic.GetGenericArguments()[1];
        object? mediator = NativeV11Reflection.Get(colleague, "ApplicationSettingsMediator") ?? NativeV11Reflection.Get(colleague, "Mediator");
        if (mediator is null)
            for (var type = colleague.GetType(); type is not null && mediator is null; type = type.BaseType)
                foreach (var field in type.GetFields(NativeV11Reflection.Flags | BindingFlags.DeclaredOnly).Where(field => !field.IsStatic))
                {
                    var value = field.GetValue(colleague);
                    if (value is not null && value.GetType().GetProperty("SaveDataColleague", NativeV11Reflection.Flags) is not null) { mediator = value; break; }
                }
        loader = mediator is null ? throw new MissingMemberException("Settings.Mediator") :
            NativeV11Reflection.Get(mediator, "SaveDataColleague") ?? throw new MissingMemberException("Settings.SaveDataColleague");
        if (NativeV11Reflection.Method(loader.GetType(), "ConvertToT", new MemoryStream()).ReturnType != dataType)
            throw new NotSupportedException("The V11 settings save-data loader type changed.");
        mapper = NativeV11Reflection.Static(designer.GetType(profile.Version == "11.0.104.0" ? "gz" : "gu", true)!, "get_Mapper")!;
        generator = designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
    }

    internal string Capture(object model)
    {
        try { return SerializeValue(Map(model, dataType)); }
        catch { throw new InvalidDataException("Native V11 settings serialization failed; setting values are omitted."); }
    }
    internal object MapToModel(object data) => MapValue(data, modelType);
    internal object MapValue(object data, Type target)
    {
        try { return Map(data, target); }
        catch { throw new InvalidDataException("Native V11 settings model mapping failed; setting values are omitted."); }
    }
    internal string CaptureValue(object value, Type saveType)
    {
        try { return SerializeValue(Map(value, saveType)); }
        catch { throw new InvalidDataException("Native V11 settings entry serialization failed; setting values are omitted."); }
    }
    internal string SerializeValue(object value)
    {
        var method = generator.GetMethod("SaveToStr", NativeV11Reflection.Flags, null, [typeof(object)], null)!;
        return (string)method.Invoke(null, [value])!;
    }

    internal object Parse(JsonElement json)
    {
        try
        {
            NativeV11DefinitionWriter.CheckTypes(json, designer.GetName().Version!);
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json.GetRawText()), false);
            var data = NativeV11Reflection.Call(loader, "ConvertToT", stream) ?? throw new InvalidDataException();
            if (!V11Safety.Equivalent(json, Element(SerializeValue(data)))) throw new InvalidDataException();
            return data;
        }
        catch { throw new InvalidDataException("Native V11 settings format or schema is invalid; setting values are omitted."); }
    }

    internal object ParseValue(JsonElement json, Type type)
    {
        // Language/global entries have concrete native contracts; disable $type
        // selection instead of exposing the polymorphic loader to entry callers.
        try
        {
            if (ContainsType(json)) throw new InvalidDataException();
            var context = AssemblyLoadContext.GetLoadContext(designer)!;
            var newtonsoft = context.Assemblies.Single(assembly => assembly.GetName().Name == "Newtonsoft.Json");
            var settingsType = newtonsoft.GetType("Newtonsoft.Json.JsonSerializerSettings", true)!;
            var options = Activator.CreateInstance(settingsType)!;
            var missing = settingsType.GetProperty("MissingMemberHandling")!;
            missing.SetValue(options, Enum.Parse(missing.PropertyType, "Error"));
            var method = newtonsoft.GetType("Newtonsoft.Json.JsonConvert", true)!.GetMethod("DeserializeObject", [typeof(string), typeof(Type), settingsType])!;
            var result = method.Invoke(null, [json.GetRawText(), type, options]) ?? throw new InvalidDataException();
            if (!V11Safety.Equivalent(json, Element(SerializeValue(result)))) throw new InvalidDataException();
            return result;
        }
        catch { throw new InvalidDataException("Native V11 settings entry format or schema is invalid; setting values are omitted."); }
    }

    internal object ParseNativeValue(JsonElement json, Type type)
    {
        try
        {
            if (ContainsType(json)) throw new InvalidDataException();
            var context = AssemblyLoadContext.GetLoadContext(designer)!;
            var newtonsoft = context.Assemblies.Single(assembly => assembly.GetName().Name == "Newtonsoft.Json");
            var settingsType = newtonsoft.GetType("Newtonsoft.Json.JsonSerializerSettings", true)!;
            var options = Activator.CreateInstance(settingsType)!;
            var nativeOptions = NativeV11Reflection.Static(generator, "GetLoadJsonSerializerSettings")!;
            foreach (var property in settingsType.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(property => property.CanRead && property.CanWrite &&
                property.GetIndexParameters().Length == 0 && property.Name is not ("Binder" or "ReferenceResolver")))
                property.SetValue(options, property.GetValue(nativeOptions));
            var missing = settingsType.GetProperty("MissingMemberHandling")!;
            missing.SetValue(options, Enum.Parse(missing.PropertyType, "Error"));
            var method = newtonsoft.GetType("Newtonsoft.Json.JsonConvert", true)!.GetMethod("DeserializeObject", [typeof(string), typeof(Type), settingsType])!;
            var result = method.Invoke(null, [json.GetRawText(), type, options]) ?? throw new InvalidDataException();
            if (!V11Safety.Equivalent(json, Element(SerializeValue(result)))) throw new InvalidDataException();
            return result;
        }
        catch { throw new InvalidDataException("Native V11 theme/style format or schema is invalid; setting values are omitted."); }
    }

    private object Map(object value, Type target) => NativeV11Reflection.Call(mapper, "Map", value, value.GetType(), target)
        ?? throw new InvalidDataException("The native V11 settings mapper returned no value.");
    private static JsonElement Element(string text) { using var document = JsonDocument.Parse(text); return document.RootElement.Clone(); }
    private static bool ContainsType(JsonElement node) => node.ValueKind == JsonValueKind.Object ? node.EnumerateObject().Any(property => property.Name == "$type" || ContainsType(property.Value)) :
        node.ValueKind == JsonValueKind.Array && node.EnumerateArray().Any(ContainsType);
}
