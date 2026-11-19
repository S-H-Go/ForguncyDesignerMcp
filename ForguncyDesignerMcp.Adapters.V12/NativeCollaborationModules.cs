using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed record NativeCollaborationModuleMapping(ModuleAddress Address, object NativeModule,
    string NativeModuleName, string NativeModuleType, bool OwnerOnly, bool ModelPresent, string Description);

/// <summary>Read-only native collaboration identity mapping. Never registers, checks out, loads trees or writes files.</summary>
public sealed class NativeCollaborationModules
{
    private readonly Assembly assembly;
    private readonly object settings;
    private readonly IEnumerable? registered;
    private readonly Type simpleType, moduleType, iconType, moduleClass, extensions;
    private readonly Dictionary<ModuleAddress, object> declared = new();
    private readonly Dictionary<ModuleAddress, object> observed = new();
    private static readonly (string Type, ModuleAddress Address)[] Roots =
    [
        ("ApplicationSetting", new(ModuleKind.ApplicationSettings, "Current")), ("Theme", new(ModuleKind.Theme, "Current")),
        ("CustomLibrary", new(ModuleKind.CustomLibrary, "Current")), ("ShareCommand", new(ModuleKind.SharedCommand, "Current")), ("DataContectionSet", new(ModuleKind.DataConnection, "Current")),
        ("Plugin", new(ModuleKind.Plugin, "Current")), ("PermissionGroupConfig", new(ModuleKind.PermissionGroup, "Current")),
        ("MapJsonFile", new(ModuleKind.ResourceFile, "Current", "MapJsonFile")), ("Upload", new(ModuleKind.ResourceFile, "Current", "Upload")),
        ("UserService", new(ModuleKind.ApplicationSettings, "UserService")), ("PublishConfiguration", new(ModuleKind.ApplicationSettings, "PublishConfiguration")),
        ("PublishSetting", new(ModuleKind.ApplicationSettings, "PublishSetting")), ("AIAssistantRules", new(ModuleKind.ApplicationSettings, "AIAssistantRules"))
    ];
    private static readonly (ModuleKind Owner, string Property, string? Nested, string FolderType)[] Trees =
    [
        (ModuleKind.Page, "Pages", "NormalPageRoot", "Folder"), (ModuleKind.MasterPage, "Pages", "MasterPageRoot", "MasterPageFolder"),
        (ModuleKind.UserControlPage, "Pages", "UserControlPageRoot", "UserControlPageFolder"), (ModuleKind.Table, "TableSet", "Root", "TableFolder"),
        (ModuleKind.Image, "ImageFolder", null, "ImageFolder"), (ModuleKind.ServerCommand, "ServerCommandsList", null, "ServerCommandFolder"),
        (ModuleKind.ScheduledTask, "TaskSchedulerList", null, "TaskSchedulerFolder"), (ModuleKind.Notification, "NotificationList", null, "NotificationFolder"),
        (ModuleKind.Report, "ActiveReportsList", null, "ReportFolder"), (ModuleKind.BusinessProcess, "BusinessProcessList", null, "ProcessFolder"),
        (ModuleKind.SharedCommand, "ShareCommandContainerList", null, "ShareCommandContainerFolder")
    ];

    public NativeCollaborationModules(Assembly assembly, object nativeDocument)
        : this(assembly, R.Get(nativeDocument, "ForguncySettings")!, null) { }

    public NativeCollaborationModules(Assembly assembly, object nativeSettings, IEnumerable? nativeRegisteredModules)
    {
        this.assembly = assembly; settings = nativeSettings; registered = nativeRegisteredModules;
        if (assembly.GetName().Version != new Version(12, 0, 102, 0) ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != "A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246" ||
            settings.GetType() != assembly.GetType("lt", true)) throw Denied("The exact verified native document/settings build is required.");
        simpleType = assembly.GetType("Forguncy.Common.SimpleModule", true)!; extensions = assembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        var collaboration = Assembly.Load("Forguncy.Collaboration"); moduleType = collaboration.GetType("Forguncy.Collaboration.ModuleType", true)!;
        iconType = collaboration.GetType("Forguncy.Collaboration.IconType", true)!; moduleClass = collaboration.GetType("Forguncy.Collaboration.Module", true)!;
    }

    public object ResolveNativeModule(ModuleAddress address)
    {
        RequireBound(); ValidateAddress(address);
        var singleton = Roots.SingleOrDefault(root => root.Address == address);
        if (singleton.Type is not null) return RegisteredOr(CreateRoot(singleton.Type));
        if (observed.TryGetValue(address, out var known) && RegisteredModules().Any(module => Equal(module, known))) return RegisteredOr(known);
        if (declared.TryGetValue(address, out var pending)) return RegisteredOr(pending);
        var owner = Owner(address); var matches = Tree(owner).Where(item => item.Address == address ||
            address.Kind != ModuleKind.Folder && address.FolderPath is null && item.Address.Kind == address.Kind && item.Address.Name == address.Name).ToArray();
        if (matches.Length != 1) throw Denied("The canonical module address does not identify exactly one loaded native owner.");
        return RegisteredOr(matches[0].NativeModule);
    }

    internal object ResolveCheckinModule(ModuleAddress address)
    {
        if (address.Kind == ModuleKind.Report)
        {
            RequireBound();
            ValidateAddress(address);
            var removed = RegisteredModules().Where(module => NativeRemovedReport.IsRemoval(module) &&
                NativeItemName(module) == address.Name).ToArray();
            if (removed.Length > 1) throw Denied("Multiple registered report removals match the selected report.");
            if (removed.Length == 1) return removed[0];
        }
        return ResolveNativeModule(address);
    }

    /// <summary>Logical settings entries share a single atomic native collaboration owner.</summary>
    public object ResolveOwnerModule(ModuleAddress address) => ResolveNativeModule(address.Kind switch
    {
        ModuleKind.CellStyle or ModuleKind.ListViewStyle => new(ModuleKind.Theme, "Current"),
        ModuleKind.GlobalVariable or ModuleKind.LanguageResource => new(ModuleKind.ApplicationSettings, "Current"),
        ModuleKind.DataConnection => new(ModuleKind.DataConnection, "Current"),
        ModuleKind.CustomLibrary => new(ModuleKind.CustomLibrary, "Current"),
        ModuleKind.PermissionGroup => new(ModuleKind.PermissionGroup, "Current"),
        ModuleKind.Plugin => new(ModuleKind.Plugin, "Current"),
        _ => address
    });

    /// <summary>Only a token obtained from a fixed reviewed native resolver may predeclare a not-yet-created owner.</summary>
    public void Predeclare(ModuleAddress address, object nativeModule)
    {
        RequireBound(); ValidateAddress(address); ValidateToken(nativeModule);
        if (Roots.Any(root => root.Address == address)) throw Denied("Aggregate owners are not new item declarations.");
        var type = TypeName(nativeModule);
        if (address.Kind == ModuleKind.Folder)
        {
            var spec = Trees.Single(tree => tree.Owner == Owner(address));
            if (spec.FolderType != type) throw Denied("The native new-folder type differs from the canonical owner.");
            var root = Root(spec); var logicalParent = FolderParent(address);
            var parent = (string)R.Get(root, "Name")! + (logicalParent.Length == 0 ? "" : "/" + logicalParent);
            if (!Equal(nativeModule, R.Static(simpleType, "CreateNewFolder", address.Name, R.Get(nativeModule, "ModuleType"), R.Get(nativeModule, "IconType"), parent)!)) throw Denied("The native new-folder token differs from its declared address.");
        }
        else if (LeafKind(type) != address.Kind || !Equal(nativeModule, R.Static(simpleType, "CreateNewItem", address.Name,
            R.Get(nativeModule, "ModuleType"), R.Get(nativeModule, "IconType"), R.Get(nativeModule, "ExclusiveType"))!)) throw Denied("The native new-item token differs from its declared address.");
        if (Tree(Owner(address)).Any(item => item.Address.Kind == address.Kind && item.Address.Name == address.Name &&
            (address.Kind != ModuleKind.Folder || item.Address.FolderPath == address.FolderPath))) throw Denied("An existing native owner cannot be predeclared as new.");
        if (declared.TryGetValue(address, out var previous) && !Equal(previous, nativeModule)) throw Denied("The new native owner declaration changed.");
        declared[address] = nativeModule;
    }

    public ModuleAddress MapNativeModule(object nativeModule) => Describe(nativeModule).Address;

    public NativeCollaborationModuleMapping Describe(object nativeModule)
    {
        RequireBound(); ValidateToken(nativeModule);
        var type = TypeName(nativeModule); var name = Name(nativeModule);
        var aggregate = Roots.SingleOrDefault(root => root.Type == type);
        if (aggregate.Type is not null)
        {
            if (!Equal(nativeModule, CreateRoot(type))) throw Denied("The native aggregate module name is not canonical.");
            return Record(aggregate.Address, true, true);
        }
        var owner = FolderOwner(type) ?? LeafKind(type) ?? throw Denied("This actual native module type has no reviewed canonical address: " + type);
        if (owner == ModuleKind.View) owner = ModuleKind.Table;
        var isRegistered = IsRegistered(nativeModule);
        // Registered identities are authoritative for locks even when their
        // definition trees are lazy. Use loaded models for folder paths when available.
        var tree = Tree(owner, requireComplete: !isRegistered);
        var candidates = tree.Where(item => Equal(item.NativeModule, nativeModule)).ToArray();
        if (candidates.Length > 1) throw Denied("Multiple loaded native models resolve to the same collaboration identity.");
        if (candidates.Length == 1) return Record(candidates[0].Address, false, true);
        var declaration = declared.SingleOrDefault(pair => Equal(pair.Value, nativeModule));
        if (declaration.Key is not null) return Record(declaration.Key, false, false);
        // In-process SimpleModules are also the native resolver's predeclared
        // New/rename tokens. No client JSON can construct this exact SDK type.
        // Reproduce the complete token with its native factory before retaining
        // it; never derive a kind from a module-name prefix.
        if (!isRegistered && nativeModule.GetType() != simpleType)
            throw Denied("The unresolved native owner is neither registered nor predeclared by a reviewed native resolver.");
        var actualModule = AsModule(nativeModule);
        var leaf = type is "TableSqlite" or "TableProcessView" ? NativeItemName(nativeModule) : (string?)R.Static(extensions, "GetDescription", actualModule, true);
        if (string.IsNullOrEmpty(leaf)) throw Denied("The native owner has no unambiguous native display identity.");
        if (FolderOwner(type) is { } folderOwner)
        {
            var spec = Trees.Single(tree => tree.Owner == folderOwner); var rootName = (string)R.Get(Root(spec), "Name")!;
            var ending = "/" + leaf;
            if (!name.EndsWith(ending, StringComparison.Ordinal)) throw Denied("The registered folder name differs from the native display name.");
            var parent = name[..^ending.Length];
            if (parent != rootName && !parent.StartsWith(rootName + "/", StringComparison.Ordinal)) throw Denied("The registered native folder is outside its actual owner root.");
            var relative = parent == rootName ? "" : parent[(rootName.Length + 1)..];
            var address = new ModuleAddress(ModuleKind.Folder, leaf, folderOwner + (relative.Length == 0 ? "" : "/" + relative)); ValidateAddress(address);
            if (!Equal(nativeModule, R.Static(simpleType, "CreateNewFolder", leaf, R.Get(nativeModule, "ModuleType"), R.Get(nativeModule, "IconType"), parent)!)) throw Denied("The registered native folder cannot be reproduced by its fixed native factory.");
            if (!isRegistered) declared[address] = nativeModule;
            return Record(address, false, false);
        }
        var kind = LeafKind(type)!.Value;
        if (!Equal(nativeModule, R.Static(simpleType, "CreateNewItem", leaf, R.Get(nativeModule, "ModuleType"), R.Get(nativeModule, "IconType"), R.Get(nativeModule, "ExclusiveType"))!)) throw Denied("The registered native item cannot be reproduced by its fixed native factory.");
        var leafAddress = new ModuleAddress(kind, leaf);
        if (!isRegistered)
        {
            if (tree.Any(item => item.Address.Kind == kind && item.Address.Name == leaf)) throw Denied("A current native owner already uses this logical name with another token.");
            declared[leafAddress] = nativeModule;
        }
        return Record(leafAddress, false, false);

        NativeCollaborationModuleMapping Record(ModuleAddress address, bool ownerOnly, bool present)
        {
            if (observed.TryGetValue(address, out var other) && !Equal(other, nativeModule)) throw Denied("Different native identities share the same canonical module address.");
            observed[address] = nativeModule;
            return new(address, nativeModule, name, type, ownerOnly, present, type == "TableSqlite" ? name : (string?)R.Static(extensions, "GetDescription", AsModule(nativeModule), false) ?? name);
        }
    }

    public IReadOnlyList<NativeCollaborationModuleMapping> EnumerateRegistered()
        => EnumerateRegistered(RegisteredModules());

    public IReadOnlyList<NativeCollaborationModuleMapping> EnumerateRegistered(IEnumerable nativeModules)
    {
        var result = new List<NativeCollaborationModuleMapping>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in nativeModules)
        {
            var module = ModuleValue(raw!);
            var mapping = Describe(module);
            if (!names.Add(mapping.NativeModuleName)) throw Denied("Native registered module names contain a duplicate or Windows case alias.");
            result.Add(mapping);
        }
        return result;
    }

    private IEnumerable Registry => registered ??
        (IEnumerable)R.Get(R.Static(assembly.GetType("Forguncy.Common.CollaborationManager", true)!, "get_Instance")!, "Modules")!;

    private IEnumerable<object> RegisteredModules() => Registry.Cast<object>().Select(ModuleValue);

    private bool IsRegistered(object nativeModule)
    {
        if (Registry is IDictionary modules)
            return modules[Name(nativeModule)] is { } module && Equal(module, nativeModule);
        return RegisteredModules().Any(module => Equal(module, nativeModule));
    }
    private static object ModuleValue(object item) => item is DictionaryEntry entry ? entry.Value! :
        item.GetType().IsGenericType && item.GetType().GetGenericTypeDefinition() == typeof(KeyValuePair<,>) ? R.Get(item, "Value")! : item;
    private object RegisteredOr(object module)
    {
        var matches = RegisteredModules().Where(item => Equal(item, module)).ToArray();
        if (matches.Length > 1) throw Denied("Duplicate registered native module identities.");
        return matches.Length == 1 ? matches[0] : module;
    }
    private object CreateRoot(string type) => R.Static(simpleType, "Create", Enum.Parse(moduleType, type), Enum.ToObject(iconType, 0))!;
    private string NativeItemName(object module)
    {
        var prefix = (string)R.Static(extensions, "GetModuleName", R.Get(module, "ModuleType"))! + "/";
        var name = Name(module);
        if (!name.StartsWith(prefix, StringComparison.Ordinal)) throw Denied("The native item token differs from its SDK enum naming rule.");
        var leaf = name[prefix.Length..]; ValidateAddress(new(ModuleKind.Table, leaf)); return leaf;
    }
    private object AsModule(object value) => value.GetType() == moduleClass ? value : Activator.CreateInstance(moduleClass,
        [Name(value), R.Get(value, "ModuleType"), R.Get(value, "IconType"), R.Get(value, "ExclusiveType")])!;
    private void ValidateToken(object value)
    { if (value.GetType() != simpleType && value.GetType() != moduleClass) throw Denied("Expected the exact native SimpleModule or registered Module type."); }
    private static string Name(object value) => (string)R.Get(value, "ModuleName")!;
    private static string TypeName(object value) => R.Get(value, "ModuleType")!.ToString()!;
    private static bool Equal(object left, object right) => Name(left) == Name(right) && TypeName(left) == TypeName(right);
    private void RequireBound()
    { if (!ReferenceEquals(R.Get(R.Static(assembly.GetType("s", true)!, "get_Instance")!, "et"), settings)) throw Denied("The native module mapper is bound to a different document."); }
    private object Root((ModuleKind Owner, string Property, string? Nested, string FolderType) tree)
    { var value = R.Get(settings, tree.Property)!; return tree.Nested is null ? value : R.Get(value, tree.Nested)!; }
    private IReadOnlyList<NativeCollaborationModuleMapping> Tree(ModuleKind owner, bool requireComplete = true)
    {
        var spec = Trees.SingleOrDefault(tree => tree.Owner == owner);
        if (spec.Property is null) throw Denied("This address is not a native tree owner.");
        var result = new List<NativeCollaborationModuleMapping>(); var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Visit(object folder, string path, string parentNativeName)
        {
            if (!visited.Add(folder)) throw Denied("A native definition node is repeated or cyclic.");
            if (R.Get(folder, "Loaded") is not true)
            {
                if (requireComplete) throw Denied("The complete native owner tree must already be loaded.");
                return;
            }
            foreach (var model in ((IEnumerable)R.Get(folder, "Items")!).Cast<object>())
            {
                if (!visited.Add(model)) throw Denied("A native definition node is repeated or cyclic.");
                var isFolder = model.GetType().GetProperty("Items", R.Flags) is not null;
                var nativeType = R.Static(extensions, "GetModuleType", model)!;
                var type = nativeType.ToString()!; var nativeName = (string)R.Static(extensions, "GetModuleName", model, isFolder ? parentNativeName : null)!;
                var native = Activator.CreateInstance(moduleClass, [nativeName, nativeType, R.Static(extensions, "GetIconType", model), R.Static(extensions, "GetExclusiveType", model)])!;
                var name = (string)R.Get(model, "Name")!;
                ModuleAddress address;
                if (isFolder)
                {
                    if (type != spec.FolderType) throw Denied("A native folder type differs from its actual owner tree.");
                    address = new(ModuleKind.Folder, name, owner + (path.Length == 0 ? "" : "/" + path));
                }
                else address = new(LeafKind(type) ?? throw Denied("Unknown native leaf type in a loaded owner tree: " + type), name, path.Length == 0 ? null : path);
                result.Add(new(address, native, nativeName, type, false, true, name));
                if (isFolder) { visited.Remove(model); Visit(model, path.Length == 0 ? name : path + "/" + name, nativeName); }
            }
        }
        var root = Root(spec); Visit(root, "", (string)R.Get(root, "Name")!);
        if (owner == ModuleKind.Page)
            foreach (var model in ((IEnumerable)R.Get(R.Get(settings, "Pages")!, "TemplatePages")!).Cast<object>())
            { var native = R.Static(simpleType, "Create", model)!; result.Add(new(new(ModuleKind.Page, (string)R.Get(model, "Name")!), native, Name(native), TypeName(native), false, true, (string)R.Get(model, "Name")!)); }
        return result;
    }
    private static ModuleKind? LeafKind(string type) => type switch
    {
        "PCPage" or "MobilePage" or "TemplatePage" => ModuleKind.Page, "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage,
        "UserControlPage" => ModuleKind.UserControlPage, "Table" or "TableSqlite" => ModuleKind.Table, "TableView" or "TableProcessView" => ModuleKind.View,
        "ServerCommand" or "ServerCommandPrivate" or "AppService" => ModuleKind.ServerCommand, "TaskScheduler" => ModuleKind.ScheduledTask,
        "PageReport" or "RdlReport" => ModuleKind.Report, "ProcessItem" => ModuleKind.BusinessProcess, "Notification" => ModuleKind.Notification,
        "ShareCommand" or "ShareCommandContainer" => ModuleKind.SharedCommand, "Image" => ModuleKind.Image, _ => null
    };
    private static ModuleKind? FolderOwner(string type) => Trees.Where(tree => tree.FolderType == type).Select(tree => (ModuleKind?)tree.Owner).SingleOrDefault();
    private static ModuleKind Owner(ModuleAddress address) => address.Kind == ModuleKind.Folder
        ? Enum.TryParse<ModuleKind>(address.FolderPath?.Split('/')[0], out var kind) && Trees.Any(tree => tree.Owner == kind) ? kind : throw Denied("A folder requires its exact native owner kind.")
        : address.Kind == ModuleKind.View ? ModuleKind.Table : address.Kind;
    private static string FolderParent(ModuleAddress address)
    { var value = address.FolderPath!; var split = value.IndexOf('/'); return split < 0 ? "" : value[(split + 1)..]; }
    private static void ValidateAddress(ModuleAddress address)
    {
        if (string.IsNullOrWhiteSpace(address.Name) || address.Name.IndexOfAny(['/', '\\', ':', '\0']) >= 0 || address.Name is "." or ".." || address.Name.Any(char.IsControl)) throw Denied("Module names must be unambiguous native logical names.");
        if (address.FolderPath is { } path && path.Split('/').Any(part => string.IsNullOrEmpty(part) || part is "." or ".." || part.IndexOfAny(['\\', ':', '\0']) >= 0 || part.Any(char.IsControl))) throw Denied("Module folders must be canonical logical native paths.");
    }
    private static BridgeException Denied(string reason) => new("NativeCollaborationModuleUnavailable", reason);
}
