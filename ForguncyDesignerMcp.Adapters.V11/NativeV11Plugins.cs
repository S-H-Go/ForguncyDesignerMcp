using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Read-only project dependency metadata, not the installation registry. In
// particular, do not call lk/le.Instance, CelltypeProvider.Initialize or the
// nd/mx static constructor: those native lifecycles can load .NET/Java plugins.
internal sealed class NativeV11Plugins
{
    private readonly NativeV11Session? session;
    private readonly Contract contract;
    private readonly string source;
    private readonly object? componentItems;
    internal IReadOnlyList<ModuleKind> Kinds => [ModuleKind.Plugin];
    internal string ManifestName => contract.ManifestName;
    internal string ProjectPluginFolderName => contract.ProjectPluginFolderName;

    internal NativeV11Plugins(NativeV11Session session)
    {
        this.session = session;
        contract = new(session.Context.DesignerAssembly);
        source = session.SourcePath;
    }

    // An isolated real List<PluginItem> exercises the native save-info mapper.
    // It is explicitly labelled as a component projection, never as evidence
    // that a live document's current dependency enumeration is complete.
    internal NativeV11Plugins(Assembly designer, object nativeItems, string source)
    {
        contract = new(designer); this.source = Path.GetFullPath(source);
        if (nativeItems.GetType() != contract.PluginList) throw Error("NativePluginContractChanged", "Expected the exact native plugin-item list.");
        componentItems = nativeItems;
    }

    internal bool CanWrite(ModuleKind kind, ModuleOperation operation) => false;
    internal IReadOnlyList<string> SourcePaths(ModuleKind kind)
    { RequireKind(kind); return [ManifestName]; }
    internal IReadOnlyList<string> SourcePaths(ModuleAddress address)
    { ValidateAddress(address); return [ManifestName]; }

    internal IReadOnlyList<ModuleDocument> List(ModuleKind kind)
    {
        RequireKind(kind);
        return Capture().OrderBy(item => item.Address.Name, StringComparer.Ordinal).ToArray();
    }

    internal ModuleDocument Read(ModuleAddress address)
    {
        ValidateAddress(address);
        return Capture().SingleOrDefault(item => item.Address == address)
            ?? throw Error("PluginDependencyNotFound", "No current native project dependency has this stable plugin GUID.");
    }

    // Explicit saved declarations. A failed current-registry read never calls
    // this method as a fallback, and saved-only entries never enter List().
    internal JsonElement ReadSavedManifest()
    {
        session?.VerifyContext();
        var before = NativeV11SourceRevision.Capture(source);
        var result = Saved();
        session?.VerifyContext();
        if (before != NativeV11SourceRevision.Capture(source)) throw Incomplete("The saved plugin manifest source changed during the read.");
        return result;
    }

    internal ReferenceResult FindReferences(ModuleAddress address)
    {
        ValidateAddress(address);
        _ = Read(address);
        return new([], false, "Native dependency metadata does not provide a complete module-reference graph; plugin installation, replacement and removal are disabled.");
    }

    internal void Validate(ModuleMutationRequest request) => throw Error("PluginLifecycleNotImplemented",
        "Plugin installation, replacement and removal require a reviewed runtime lifecycle; this adapter only reads dependency metadata.");
    internal Action Apply(ModuleMutationRequest request) { Validate(request); throw new InvalidOperationException(); }

    private IReadOnlyList<ModuleDocument> Capture()
    {
        try { return CaptureCore(); }
        catch (BridgeException) { throw; }
        catch (Exception error)
        {
            while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException!;
            throw Incomplete("The native current dependency read could not be verified (" + error.GetType().Name + ").");
        }
    }

    private IReadOnlyList<ModuleDocument> CaptureCore()
    {
        var initial = session?.Context.CaptureSnapshot();
        session?.VerifyContext();
        var sourceRevision = NativeV11SourceRevision.Capture(source);
        object items;
        HashSet<string>? requiredGuids = null;
        RegistryState? before = null;
        if (componentItems is not null) items = componentItems;
        else
        {
            if (session is null) throw Incomplete("No native document is bound.");
            Stage("PluginDependencyDefinitionsIncomplete", () => { RequireDefinitionsLoaded(session); return true; });
            before = Stage("PluginDependencyRegistryIncomplete", () => ExistingRegistry(contract, session));
            requiredGuids = Stage("PluginDependencyMappingsIncomplete", () => RequireUsedTypeMappings(contract, before));
            items = Stage("PluginDependencyEnumerationIncomplete", () => NativeV11Reflection.Static(contract.Utility, "GetUsedPluginItems")
                ?? throw Incomplete("The native current dependency reader returned no metadata."));
        }
        var records = Project(contract, items);
        if (requiredGuids is not null && requiredGuids.Except(records.Select(Identity), StringComparer.Ordinal).Any())
            throw Incomplete("The native current dependency result omitted an actual used cell/command plugin GUID.");
        var saved = Saved();
        if (session is not null)
        {
            session.VerifyContext();
            Stage("PluginDependencyDefinitionsIncomplete", () => { RequireDefinitionsLoaded(session); return true; });
            var current = session.Context.CaptureSnapshot();
            if (current.Target != initial!.Target || current.Revision != initial.Revision || current.HasUnsavedChanges != initial.HasUnsavedChanges ||
                current.CurrentUser != initial.CurrentUser || current.InCollaborationMode != initial.InCollaborationMode ||
                !SameRegistry(before!, Stage("PluginDependencyRegistryIncomplete", () => ExistingRegistry(contract, session))))
                throw Incomplete("The native document or initialized plugin registries changed during dependency enumeration.");
        }
        if (sourceRevision != NativeV11SourceRevision.Capture(source)) throw Incomplete("The saved project changed during dependency enumeration.");
        var savedItems = saved.GetProperty("Items").EnumerateArray().ToDictionary(Identity, record => record.Clone(), StringComparer.Ordinal);
        var result = new List<ModuleDocument>();
        foreach (var record in records)
        {
            var guid = Identity(record);
            var metadata = JsonSerializer.SerializeToElement(new
            {
                Registry = componentItems is null ? "CurrentNativeProjectDependency" : "NativeMetadataComponentProjection",
                NativeSaveInfo = record,
                SavedDeclaration = new
                {
                    Registry = "SavedProjectDeclaration", ManifestPath = ManifestName,
                    ManifestSha256 = saved.GetProperty("ManifestSha256"),
                    Present = savedItems.ContainsKey(guid),
                    NativeSaveInfo = savedItems.TryGetValue(guid, out var declaration) ? (JsonElement?)declaration : null
                }
            });
            result.Add(new(new(ModuleKind.Plugin, guid), metadata, [], NativeV11Session.Hash(Encoding.UTF8.GetBytes(metadata.GetRawText()))));
        }
        return result;
    }

    private JsonElement Saved()
    {
        var bytes = NativeV11PendingSave.ReadFiles(source, [ManifestName])[ManifestName];
        JsonElement[] records = [];
        if (bytes is not null)
        {
            try
            {
                var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
                using var parsed = JsonDocument.Parse(text);
                records = ValidateRecords(contract, parsed.RootElement);
            }
            catch (BridgeException) { throw; }
            catch (Exception error) when (error is JsonException or DecoderFallbackException)
            { throw Error("InvalidPluginManifest", "The saved native plugin declaration is not strict UTF-8 JSON."); }
        }
        return JsonSerializer.SerializeToElement(new
        {
            Registry = "SavedProjectDeclaration", ManifestPath = ManifestName,
            ManifestSha256 = bytes is null ? null : NativeV11Session.Hash(bytes),
            ManifestPresent = bytes is not null, Items = records
        });
    }

    internal static JsonElement[] ProjectNativeItems(Assembly designer, object items) => Project(new(designer), items);
    internal static void RequireInitializedRegistry(Assembly designer) => _ = ExistingRegistry(new(designer));

    private static JsonElement[] Project(Contract contract, object items, bool requireIdentities = true)
    {
        if (items.GetType() != contract.PluginList) throw Error("NativePluginContractChanged", "The native plugin dependency reader returned an unexpected list type.");
        foreach (var item in ((IEnumerable)items).Cast<object?>())
            if (item is null || item.GetType() != contract.PluginList.GetGenericArguments().Single())
                throw Error("NativePluginContractChanged", "A native plugin metadata item has an unknown runtime type.");
        var native = NativeV11Reflection.Static(contract.Utility, "GetUsedPlugInItemsSaveInfo", items)
            ?? throw Error("NativePluginContractChanged", "The native plugin save-info mapper returned no metadata.");
        if (native.GetType() != contract.SaveList) throw Error("NativePluginContractChanged", "The native plugin save-info list type changed.");
        var records = ((IEnumerable)native).Cast<object>().Select(value =>
        {
            if (value.GetType() != contract.SaveInfo) throw Error("NativePluginContractChanged", "A native plugin save-info item type changed.");
            var fields = contract.Properties.ToDictionary(property => property.Name, property => property.GetValue(value), StringComparer.Ordinal);
            return JsonSerializer.SerializeToElement(fields);
        }).ToArray();
        return ValidateRecords(contract, JsonSerializer.SerializeToElement(records), requireIdentities);
    }

    private static JsonElement[] ValidateRecords(Contract contract, JsonElement records, bool requireIdentities = true)
    {
        if (records.ValueKind != JsonValueKind.Array) throw Error("InvalidPluginManifest", "The native plugin declaration must be an array.");
        var result = new List<JsonElement>(); var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object) throw Error("InvalidPluginManifest", "A native plugin declaration must be a metadata object.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in record.EnumerateObject())
            {
                var expected = contract.Properties.SingleOrDefault(candidate => candidate.Name == property.Name);
                if (!names.Add(property.Name) || expected is null ||
                    (expected.PropertyType == typeof(bool) ? property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) :
                        property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
                    throw Error("InvalidPluginManifest", "A plugin declaration contains unknown, duplicate or incorrectly typed native fields.");
            }
            if (requireIdentities && !identities.Add(Identity(record)))
                throw Error("PluginIdentityAmbiguous", "Plugin dependencies contain a duplicate stable GUID or conflicting versions.");
            result.Add(record.Clone());
        }
        return result.ToArray();
    }

    private static string Identity(JsonElement record)
    {
        if (!record.TryGetProperty("guid", out var value) || value.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(value.GetString(), out var guid) || guid == Guid.Empty)
            throw Error("PluginIdentityUnavailable", "A native plugin dependency has no nonempty stable GUID; no replacement identity is fabricated.");
        return guid.ToString("D");
    }

    private static void RequireDefinitionsLoaded(NativeV11Session session)
    {
        if (session.Routes.Count != 10) throw Incomplete("A required native module route is unavailable.");
        foreach (var route in session.Routes)
        {
            var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
            void Walk(object model)
            {
                if (!visited.Add(model)) throw Incomplete("A native definition tree contains a cycle or shared node: " + route.Folder);
                if (NativeV11Reflection.Get(model, "Items") is not IList children) return;
                if (NativeV11Reflection.Get(model, "Loaded") is not true)
                    throw Incomplete("A native definition root or folder is not loaded: " + route.Folder);
                foreach (var child in children.Cast<object>()) Walk(child);
            }
            Walk(route.Root);
            try { _ = session.Items(route.Kind); }
            catch (Exception error) { throw Incomplete("Native definition enumeration is incomplete: " + route.Folder + " (" + error.GetType().Name + ")."); }
        }
        var pages = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.WorkbookExtensions", true)!, "EnumAllPages", session.Workbook) as IEnumerable
            ?? throw Incomplete("The native page worksheet enumeration is unavailable.");
        foreach (var sheet in pages.Cast<object>())
            if (NativeV11Reflection.Get(sheet, "Loaded") is not true)
                throw Incomplete("A native page/template worksheet is not loaded.");
    }

    private sealed record RegistryState(object Manager, object Items, object Task, object CellTypes, object Commands,
        object TypeMap, object JavaMap, object? Connections, string Stamp);
    private static RegistryState ExistingRegistry(Contract contract, NativeV11Session? session = null)
    {
        var manager = contract.Manager.GetField(contract.Newer ? "bez" : "bcy", NativeV11Reflection.Flags)!.GetValue(null)
            ?? throw Incomplete("The native plugin manager is not initialized; metadata reads will not create it.");
        var task = Field(manager, contract.Newer ? "bfe" : "bdd") as Task;
        if (task is null || !task.IsCompletedSuccessfully) throw Incomplete("The native plugin manager has not completed its existing load task.");
        var items = Field(manager, contract.Newer ? "bfc" : "bdb")
            ?? throw Incomplete("The existing native plugin metadata list is unavailable.");
        if (items.GetType() != contract.PluginList) throw Incomplete("The existing native plugin metadata list has an unknown type.");
        // These native handlers are subscribed only after the corresponding
        // initialization path. In particular, reading an nd/mx static field
        // before this witness would itself execute its Java-loading .cctor.
        var handlers = (Field(manager, contract.Newer ? "bfa" : "bcz") as Delegate)?.GetInvocationList() ?? [];
        foreach (var owner in new[] { contract.Commands, contract.CellProvider })
            if (!handlers.Any(handler => handler.Method.DeclaringType == owner && handler.Method.Name == "Instance_PluginChanged" && handler.Method.IsStatic))
                throw Incomplete("Native command/cell-type plugin initialization is not proven complete; metadata reads will not initialize it.");
        var cellTypes = contract.CellProvider.GetField("_cellTypeDefinitions", NativeV11Reflection.Flags)!.GetValue(null)
            ?? throw Incomplete("The initialized native cell-type registry is unavailable.");
        var commands = contract.Commands.GetField(contract.Newer ? "clv" : "cih", NativeV11Reflection.Flags)!.GetValue(null) as IDictionary
            ?? throw Incomplete("The initialized native command registry is unavailable.");
        if (NativeV11Reflection.Get(manager, "NeedReloadAssembly") is not false)
            throw Incomplete("Native plugin replacement/removal is awaiting an assembly reload.");
        var reference = contract.Designer.GetType(contract.Newer ? "rg" : "rc", true)!;
        if (reference.GetField(contract.Newer ? "dyq" : "dui", NativeV11Reflection.Flags)!.GetValue(null) is not false)
            throw Incomplete("The native dependency enumerator is in a cached reference scope.");
        var model = AssemblyLoadContext.GetLoadContext(contract.Designer)!.Assemblies.Single(assembly => assembly.GetName().Name == "ServerDesignerCommon");
        var referenceBase = model.GetType("Forguncy.Common.NameReferenceHelperBase", true)!;
        if (NativeV11Reflection.Static(referenceBase, "get_IgnoreDisableCommands") is not false)
            throw Incomplete("The native dependency enumerator would omit disabled commands.");
        var editing = contract.Designer.GetType("Forguncy.ServerCommands.Views.ServerCommandEditingContext", true)!;
        if (editing.GetField("_commandEditingContext", NativeV11Reflection.Flags)!.GetValue(null) is not ICollection stack || stack.Count != 0)
            throw Incomplete("An active native server-command editor requires a separately verified dependency read.");
        // PluginMapCache's audited .cctor only creates its two dictionaries.
        // Missing Java mappings must not be interpreted as no Java use: the
        // vendor helper returns an empty result when this dictionary is empty.
        var map = model.GetType("ServerDesignerCommon.Metadata.PluginMapCache", true)!;
        var typeMap = map.GetField("<TypeToPluginItemCache>k__BackingField", NativeV11Reflection.Flags)!.GetValue(null) as IDictionary
            ?? throw Incomplete("The native type-to-plugin GUID map is unavailable.");
        var javaMap = map.GetField("<JavaPluginItemCache>k__BackingField", NativeV11Reflection.Flags)!.GetValue(null) as IDictionary
            ?? throw Incomplete("The native Java command-to-plugin GUID map is unavailable.");
        object? connections = null;
        var needsDatabaseRegistry = true;
        var connectionStamp = "Unknown";
        if (session is not null)
        {
            // The actual cz/cv getter lazily constructs its configuration.
            // Read its existing field instead so List cannot start a lifecycle.
            var nativeConfiguration = Field(session.NativeApplication, "cd")
                ?? throw Incomplete("The existing native data-connection configuration is unavailable.");
            var configurationType = contract.Designer.GetType(contract.Newer ? "ld" : "kx", true)!;
            if (nativeConfiguration.GetType() != configurationType) throw Incomplete("The native data-connection configuration type changed.");
            connections = NativeV11Reflection.Get(nativeConfiguration, "DataConnectionSet")
                ?? throw Incomplete("The existing native data-connection list is unavailable.");
            var connectionType = model.GetType("Forguncy.Model.Tables.DataConnectionInfo", true)!;
            needsDatabaseRegistry = NeedsDatabaseRegistryCore(connections, connectionType);
            var connectionTypes = new List<string>();
            foreach (var item in ((IEnumerable)connections).Cast<object>())
            {
                if (item.GetType() != connectionType || NativeV11Reflection.Get(item, "DataBaseType") is not string type)
                    throw Incomplete("An existing native data connection has no verified database type.");
                connectionTypes.Add(type);
            }
            connectionStamp = JsonSerializer.Serialize(connectionTypes);
        }
        IDictionary? databaseItems = null;
        if (needsDatabaseRegistry)
        {
            var db = AssemblyLoadContext.GetLoadContext(contract.Designer)!.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "ForguncyDataBaseService")
                ?? throw Incomplete("The native database-plugin registry component is not already loaded.");
            var expectedHash = contract.Newer ? "14AF70773DC4F08BED9F9789E9A3D571926816DF665C0859D3253A8CF4540E76" : "754788FEAED6D379F77311B7DF5F76064351A9B806CE0C0C2E37A41BECF690FB";
            if (db.GetName().Version != contract.Designer.GetName().Version || string.IsNullOrEmpty(db.Location) || NativeV11Session.Hash(File.ReadAllBytes(db.Location)) != expectedHash)
                throw Incomplete("The already-loaded native database-plugin registry component does not match this exact V11 build.");
            databaseItems = db.GetType("ForguncyDataAccess.DataBaseAssemblysCache", true)!.GetField("_allDataBasePluginItems", NativeV11Reflection.Flags)!.GetValue(null) as IDictionary
                ?? throw Incomplete("The native database-plugin metadata dictionary is unavailable.");
        }
        var stamp = string.Join("\n", Project(contract, items, false).Select(record => record.GetRawText())) + "\n" +
            DictionaryStamp(commands, false, contract) + "\n" +
            string.Join("\n", ((IEnumerable)cellTypes).Cast<object>().Select(RuntimeHelpers.GetHashCode)) + "\n" +
            connectionStamp + "\n" + (databaseItems is null ? "NoDataConnections" : DictionaryStamp(databaseItems, true, contract)) + "\n" + MapStamp(typeMap, false) + "\n" + MapStamp(javaMap, true);
        return new(manager, items, task, cellTypes, commands, typeMap, javaMap, connections, NativeV11Session.Hash(Encoding.UTF8.GetBytes(stamp)));
    }
    internal static bool NeedsDatabaseRegistryCore(object connections, Type connectionType)
    {
        if (connections.GetType() != typeof(List<>).MakeGenericType(connectionType) || connections is not ICollection list)
            throw Incomplete("The native data-connection collection has an unknown type.");
        return list.Count != 0;
    }
    private static HashSet<string> RequireUsedTypeMappings(Contract contract, RegistryState state)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new Dictionary<Type, bool>();
        var definitionType = contract.Designer.GetType("Forguncy.CellTypes.CellTypeDefinition", true)!;
        foreach (var definition in ((IEnumerable)state.CellTypes).Cast<object>())
        {
            if (definition.GetType() != definitionType || NativeV11Reflection.Get(definition, "Type") is not Type type ||
                NativeV11Reflection.Get(definition, "IsBuiltin") is not bool builtin)
                throw Incomplete("The native cell-type definition metadata has an unknown schema.");
            if (definitions.TryGetValue(type, out var previous) && previous != !builtin)
                throw Incomplete("The native cell-type registry has conflicting plugin classifications.");
            definitions[type] = !builtin;
        }
        var usedCells = NativeV11Reflection.Static(contract.Utility, "GetUsedCellTypes") as IEnumerable
            ?? throw Incomplete("The native used-cell-type enumeration is unavailable.");
        var locatedCell = NativeV11Reflection.Method(contract.Utility, "GetUsedCellTypes").ReturnType.GetGenericArguments().Single();
        foreach (var value in usedCells.Cast<object>())
        {
            if (value.GetType() != locatedCell || NativeV11Reflection.Get(value, "Object") is not object attached ||
                NativeV11Reflection.Get(attached, "CellType") is not object cell)
                throw Incomplete("An actual native used-cell-type location has an unknown schema.");
            var type = cell.GetType();
            if (!definitions.TryGetValue(type, out var plugin)) throw Incomplete("An actual used cell type is absent from the initialized native definition registry.");
            if (plugin) Resolve(((IDictionary)state.TypeMap)[type] as string, type);
        }
        var usedCommands = NativeV11Reflection.Static(contract.Utility, "GetUsedServerCommands") as IEnumerable
            ?? throw Incomplete("The native used-command enumeration is unavailable.");
        var locatedCommand = NativeV11Reflection.Method(contract.Utility, "GetUsedServerCommands").ReturnType.GetGenericArguments().Single();
        var model = AssemblyLoadContext.GetLoadContext(contract.Designer)!.Assemblies.Single(assembly => assembly.GetName().Name == "ServerDesignerCommon");
        var javaCommand = model.GetType("Forguncy.Model.JavaPluginCommand", true)!;
        foreach (var value in usedCommands.Cast<object>())
        {
            if (value.GetType() != locatedCommand || NativeV11Reflection.Get(value, "Object") is not object command)
                throw Incomplete("An actual native used-command location has an unknown schema.");
            var type = command.GetType();
            if (type == javaCommand)
            {
                if (NativeV11Reflection.Get(command, "Type") is not string javaType ||
                    !((IDictionary)state.JavaMap).Contains(javaType))
                    throw Incomplete("An actual Java plugin command has no native GUID mapping; an empty map is not proof of no dependency.");
                Resolve(((IDictionary)state.JavaMap)[javaType] as string, type);
            }
            else
            {
                if (CommandRequiresPluginGuidCore((IDictionary)state.Commands, type))
                    Resolve(((IDictionary)state.TypeMap)[type] as string, type);
            }
        }
        return required;

        void Resolve(string? nativeGuid, Type usedType)
        {
            if (!Guid.TryParse(nativeGuid, out var guid) || guid == Guid.Empty)
                throw Incomplete("An actual used plugin type has no stable native GUID mapping; " + SafeTypeIdentity(usedType));
            var identity = guid.ToString("D");
            var matches = ((IEnumerable)state.Items).Cast<object>().Count(item =>
                NativeV11Reflection.Get(item, "guid") is string installed && Guid.TryParse(installed, out var installedGuid) && installedGuid == guid);
            if (matches != 1) throw Incomplete("An actual used plugin GUID does not match exactly one existing native installed metadata record.");
            required.Add(identity);
        }
    }

    // Both pinned native GetPlugInCommandTypes predicates select Value == false;
    // true denotes a built-in definition, not a plugin definition.
    internal static bool CommandRequiresPluginGuidCore(IDictionary definitions, Type type)
    {
        if (!definitions.Contains(type) || definitions[type] is not bool builtin)
            throw Incomplete("An actual used command type is absent from the initialized native definition registry.");
        return !builtin;
    }

    internal static string SafeTypeIdentity(Type type)
    {
        static string Clean(string? value) => value is { Length: > 0 and <= 256 } &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '+' or '`') ? value : "<unavailable>";
        return "type=" + Clean(type.FullName) + "; assembly=" + Clean(type.Assembly.GetName().Name);
    }

    private static string MapStamp(IDictionary map, bool java)
    {
        var entries = new List<string>();
        foreach (DictionaryEntry entry in map)
        {
            if (entry.Value is not string guid || (java ? entry.Key is not string : entry.Key is not Type))
                throw Incomplete("A native plugin GUID mapping registry has an unknown key/value schema.");
            entries.Add(JsonSerializer.Serialize(new { Key = java ? (string)entry.Key : RuntimeHelpers.GetHashCode(entry.Key).ToString(System.Globalization.CultureInfo.InvariantCulture), Guid = guid }));
        }
        return string.Join("\n", entries.Order(StringComparer.Ordinal));
    }
    private static string DictionaryStamp(IDictionary dictionary, bool database, Contract contract)
    {
        var entries = new List<string>();
        // IDictionary.GetEnumerator returns DictionaryEntry even when the
        // concrete Dictionary<TKey,TValue>.IEnumerable returns KeyValuePair.
        foreach (DictionaryEntry entry in dictionary)
        {
            if (!database)
            {
                if (entry.Key is not Type type || entry.Value is not bool plugin)
                    throw Incomplete("The native command-definition registry has an unknown key/value schema.");
                entries.Add(RuntimeHelpers.GetHashCode(type) + ":" + (plugin ? "true" : "false"));
            }
            else
            {
                if (entry.Key is not string code || entry.Value is null || entry.Value.GetType() != contract.PluginList.GetGenericArguments().Single())
                    throw Incomplete("The native database-plugin registry has an unknown key/value schema.");
                entries.Add(JsonSerializer.Serialize(new
                {
                    Code = code, Identity = RuntimeHelpers.GetHashCode(entry.Value),
                    Guid = NativeV11Reflection.Get(entry.Value, "guid") as string,
                    Version = NativeV11Reflection.Get(entry.Value, "version") as string,
                    PluginType = NativeV11Reflection.Get(entry.Value, "pluginType") as string
                }));
            }
        }
        return string.Join("\n", entries.Order(StringComparer.Ordinal));
    }
    private static bool SameRegistry(RegistryState left, RegistryState right) =>
        ReferenceEquals(left.Manager, right.Manager) && ReferenceEquals(left.Items, right.Items) && ReferenceEquals(left.Task, right.Task) &&
        ReferenceEquals(left.CellTypes, right.CellTypes) && ReferenceEquals(left.Commands, right.Commands) &&
        ReferenceEquals(left.TypeMap, right.TypeMap) && ReferenceEquals(left.JavaMap, right.JavaMap) &&
        ReferenceEquals(left.Connections, right.Connections) && left.Stamp == right.Stamp;
    private static object? Field(object instance, string name) => instance.GetType().GetField(name, NativeV11Reflection.Flags)?.GetValue(instance);

    private sealed class Contract
    {
        internal readonly Assembly Designer;
        internal readonly bool Newer;
        internal readonly Type Utility, Manager, CellProvider, Commands, PluginList, SaveList, SaveInfo;
        internal readonly PropertyInfo[] Properties;
        internal readonly string ManifestName, ProjectPluginFolderName;
        internal Contract(Assembly designer)
        {
            Designer = designer; Newer = V11BuildProfile.Resolve(designer).Version == "11.0.104.0";
            Utility = designer.GetType(Newer ? "go" : "gj", true)!;
            Manager = designer.GetType(Newer ? "lk" : "le", true)!;
            CellProvider = designer.GetType("Forguncy.CellTypes.CelltypeProvider", true)!;
            Commands = designer.GetType(Newer ? "nd" : "mx", true)!;
            if (Utility.TypeInitializer is not null || Manager.TypeInitializer is not null)
                throw Error("NativePluginContractChanged", "A native plugin metadata type acquired an unaudited static initializer.");
            PluginList = NativeV11Reflection.Method(Utility, "GetUsedPluginItems").ReturnType;
            SaveList = Utility.GetMethod("GetUsedPlugInItemsSaveInfo", NativeV11Reflection.Flags, null, [PluginList], null)!.ReturnType;
            SaveInfo = SaveList.GetGenericArguments().Single();
            if (PluginList != typeof(List<>).MakeGenericType(PluginList.GetGenericArguments().Single()) ||
                PluginList.GetGenericArguments().Single().FullName != "CommonUtilities.PluginItem" || SaveInfo != designer.GetType("Forguncy.SaveLoad.PlugInItemSaveInfo", true) ||
                SaveList != typeof(List<>).MakeGenericType(SaveInfo))
                throw Error("NativePluginContractChanged", "The exact native plugin/save-info metadata types changed.");
            Properties = SaveInfo.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var fields = new[] { "assemblyName", "guid", "isPaidPlugin", "name", "pluginType", "relativePath", "version" };
            if (!Properties.Select(property => property.Name).SequenceEqual(fields, StringComparer.Ordinal) ||
                Properties.Any(property => property.GetMethod is null || property.SetMethod is null || property.PropertyType != (property.Name == "isPaidPlugin" ? typeof(bool) : typeof(string))))
                throw Error("NativePluginContractChanged", "The native plugin save-info schema changed.");
            ManifestName = (string)NativeV11Reflection.Static(Utility, Newer ? "get_aor" : "get_ang")!;
            ProjectPluginFolderName = (string)NativeV11Reflection.Static(Utility, Newer ? "get_aos" : "get_anh")!;
            foreach (var leaf in new[] { ManifestName, ProjectPluginFolderName }) V11Safety.Address(new(ModuleKind.Plugin, leaf));
        }
    }

    private static void RequireKind(ModuleKind kind)
    { if (kind != ModuleKind.Plugin) throw Error("InvalidPluginQuery", "This metadata adapter only addresses project plugin dependencies."); }
    private static void ValidateAddress(ModuleAddress address)
    {
        RequireKind(address.Kind);
        if (address.FolderPath is not null || !Guid.TryParseExact(address.Name, "D", out var guid) || guid == Guid.Empty || guid.ToString("D") != address.Name)
            throw Error("InvalidPluginAddress", "Plugin dependency addresses use the adapter's canonical stable GUID without an installation path.");
    }
    private static BridgeException Incomplete(string message) => Error("PluginDependencyReadIncomplete", message);
    private static BridgeException Error(string code, string message) => new(code, message);
    internal static T Stage<T>(string code, Func<T> action)
    {
        try { return action(); }
        catch (BridgeException error) when (error.Code == "PluginDependencyReadIncomplete")
        { throw Error(code, error.Message); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { throw Error(code, "The native plugin dependency stage failed (" + error.InnerException.GetType().Name + ")."); }
    }
}
