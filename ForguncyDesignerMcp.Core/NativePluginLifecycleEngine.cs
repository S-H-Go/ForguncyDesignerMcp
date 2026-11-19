using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace ForguncyDesignerMcp.Core.Platform;

// Executed only by a version adapter on its native UI scheduler and shared
// mutation queue. This engine invokes the installed Designer manager, not a
// manifest-only approximation. It never initializes an absent singleton.
public sealed class NativePluginLifecycleEngine
{
    public sealed record Change(ModuleOperation Operation, string PluginGuid, string? ExpectedInstalledVersion,
        byte[]? Package, string? PackageVersion, string? PackageSha256);
    public sealed record Outcome(string State, string Code, string Message, bool? NativeAccepted,
        bool RegistryVerified, bool RestartRequired, bool RuntimeActivationVerified, bool ExecutionMayContinue,
        string? ObservedGuid, string? ObservedVersion, string Limitation);
    private sealed record Profile(string Version, string Sha256, string CommonSha256, string ManagerType, string SingletonField, string UtilityType, string DecryptType);
    private static readonly Profile[] Profiles =
    [
        new("11.0.3.0", "AD42655737D449128A0DC4AF1EEE53FF228C933E7D0FEECAEC9A1155DDD741CF", "F1477113B178D0424BC617C755DF4D6B0D7985D215C6C745FAAA92D85C072421", "le", "bcy", "gj", "x"),
        new("11.0.104.0", "07E51B2AD8A70CE2AF337ED9A6071C81E87ACA421817ED1B8FB10A2F04CDA9BF", "BE23CB916F6C9B9BF7365B54D8BBB36263962E6BE28567C70629366DEC32E436", "lk", "bez", "go", "w"),
        new("12.0.102.0", "A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246", "C39DDC1D3C6FF47365FFCD6B2311487BDA3A7E3F9A0838252440464520481608", "ma", "bjt", "gz", "x")
    ];
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private const string OwnGuid = "8b34e735-b71d-48cb-b943-c6eb5a0f1432";
    private const string Limitation = "The native plugin registry and native project-dependency list were inspected; dependency enumeration may omit unloaded definitions. Runtime activation/unload and project export were not independently verified. A native restart requirement must be honored.";
    private readonly AdapterHostContext context;
    private readonly Profile profile;
    private readonly Type managerType;
    private readonly Type itemType;

    public NativePluginLifecycleEngine(AdapterHostContext context)
    {
        this.context = context;
        var designer = context.DesignerAssembly;
        var version = designer.GetName().Version?.ToString();
        if (designer.GetName().Name != "Forguncy" || string.IsNullOrEmpty(designer.Location)) throw Error("UnsupportedDesignerBuild", "A verified native Designer build is required.");
        using var source = File.OpenRead(designer.Location);
        var hash = Convert.ToHexString(SHA256.HashData(source));
        profile = Profiles.SingleOrDefault(candidate => candidate.Version == version && candidate.Sha256 == hash)
            ?? throw Error("UnsupportedDesignerBuild", "The native plugin lifecycle build is not recognized.");
        managerType = designer.GetType(profile.ManagerType, true)!;
        itemType = managerType.GetMethod("CreatePluginItem", Flags, null, [typeof(string)], null)?.ReturnType
            ?? throw Error("NativePluginContractChanged", "The native plugin manager contract changed.");
        var common = itemType.Assembly;
        if (common.GetName().Name != "CommonUtilities" || common.GetName().Version?.ToString() != profile.Version || string.IsNullOrEmpty(common.Location))
            throw Error("UnsupportedDesignerBuild", "The native plugin component identity does not match the Designer.");
        using (var commonFile = File.OpenRead(common.Location))
            if (Convert.ToHexString(SHA256.HashData(commonFile)) != profile.CommonSha256)
                throw Error("UnsupportedDesignerBuild", "The native plugin component fingerprint does not match the Designer.");
        if (itemType.FullName != "CommonUtilities.PluginItem" || managerType.GetField(profile.SingletonField, Flags) is null ||
            managerType.GetMethod("AddNewPluginItem", Flags, null, [typeof(string), itemType], null)?.ReturnType != typeof(void) ||
            managerType.GetMethod("ReplacePluginItem", Flags, null, [typeof(string), itemType, itemType], null)?.ReturnType != typeof(void) ||
            managerType.GetMethod("DeleteItem", Flags, null, [itemType, typeof(bool)], null)?.ReturnType != typeof(void) ||
            managerType.GetProperty("NeedReloadAssembly", Flags)?.PropertyType != typeof(bool) ||
            managerType.GetProperty("PluginItems", Flags)?.PropertyType != typeof(List<>).MakeGenericType(itemType) ||
            new[] { "guid", "version", "dependenceVersion", "Name" }.Any(name => itemType.GetProperty(name, Flags)?.PropertyType != typeof(string)))
            throw Error("NativePluginContractChanged", "The native plugin lifecycle signature changed.");
        var java = designer.GetType("Forguncy.Common.JavaAdapter.Plugin.JavaPluginManager", true)!;
        var checker = designer.GetType("Forguncy.Model.PluginVersionChecker", true)!;
        var utility = designer.GetType(profile.UtilityType, true)!;
        if (managerType.TypeInitializer is not null || utility.TypeInitializer is not null ||
            java.GetMethod("CheckIsJavaPluginAndTryStartAdapterAsync", Flags, null, [typeof(string)], null)?.ReturnType != typeof(Task<bool>) ||
            checker.GetMethod("CheckPluginDependenceVersion", Flags, null, [itemType], null)?.ReturnType.FullName != "CommonUtilities.PluginItemStatus" ||
            utility.GetMethod("GetUsedPluginItems", Flags, null, Type.EmptyTypes, null)?.ReturnType != typeof(List<>).MakeGenericType(itemType))
            throw Error("NativePluginContractChanged", "The native plugin version, dependency or Java preparation contract changed.");
        var loadField = profile.Version switch { "11.0.104.0" => "bfe", "11.0.3.0" => "bdd", _ => null };
        if (loadField is not null && (managerType.GetField(loadField, Flags) is not { } field || !typeof(Task).IsAssignableFrom(field.FieldType)))
            throw Error("NativePluginContractChanged", "The native plugin manager load-task contract changed.");
    }

    // Constructor/profile validation and this method do not instantiate a
    // native manager, start services, stage files or change plugin state.
    public void Validate(Change change)
    {
        if (change.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) ||
            !Guid.TryParseExact(change.PluginGuid, "D", out var id) || !change.PluginGuid.Equals(id.ToString("D"), StringComparison.OrdinalIgnoreCase) || change.PluginGuid.Equals(OwnGuid, StringComparison.OrdinalIgnoreCase))
            throw Error("PluginIdentityInvalid", "A fixed plugin lifecycle operation and non-MCP plugin GUID are required.");
        var manager = ExistingManager(); var current = Find(manager, change.PluginGuid);
        if (change.Operation == ModuleOperation.Create)
        {
            if (current is not null || change.ExpectedInstalledVersion is not null) throw Error("PluginAlreadyInstalled", "Plugin creation requires an absent installed identity.");
        }
        else if (current is null || string.IsNullOrWhiteSpace(change.ExpectedInstalledVersion) || Text(current, "version") != change.ExpectedInstalledVersion)
            throw Error("PluginVersionChanged", "The installed plugin identity or reviewed version changed.");
        if (change.Operation == ModuleOperation.Delete)
        {
            if (change.Package is not null || change.PackageVersion is not null || change.PackageSha256 is not null)
                throw Error("PluginPackageNotAllowed", "Plugin removal does not accept a replacement package.");
            if (UsedPlugin(change.PluginGuid)) throw Error("PluginInUse", "The native project dependency list includes this plugin.");
        }
        else
        {
            if (change.Package is null || change.PackageVersion is null || change.PackageSha256 is null)
                throw Error("PluginPackageRequired", "The reviewed package, version and hash are required.");
            var (_, package) = PrepareArchive(change);
            if (Version.Parse(package.DependenceVersion) > Version.Parse(profile.Version))
                throw Error("PluginDesignerVersionUnsupported", "The package requires a newer native Designer build.");
        }
        if (managerType.GetProperty("NeedReloadAssembly", Flags)?.GetValue(manager) is not false)
            throw Error("PluginRestartPending", "A prior native plugin operation is awaiting Designer restart.");
    }

    public IReadOnlyList<PluginInstallationInfo> ListInstalled()
    {
        var manager = ExistingManager();
        if (managerType.GetProperty("PluginItems", Flags)?.GetValue(manager) is not IEnumerable items)
            throw Error("NativePluginRegistryUnverified", "The native plugin registry is unavailable.");
        var result = new List<PluginInstallationInfo>(); var identities = new HashSet<Guid>();
        foreach (var item in items.Cast<object>())
        {
            if (result.Count >= 10000 || !Guid.TryParse(Text(item, "guid"), out var guid) || !identities.Add(guid) ||
                Text(item, "version") is not { } version || !Version.TryParse(version, out _) ||
                Text(item, "Name") is not { } name || string.IsNullOrWhiteSpace(name))
                throw Error("NativePluginRegistryUnverified", "The native plugin registry contains an invalid or duplicate identity.");
            // The native item status alone does not prove all runtime registries
            // have loaded or unloaded a plugin; leave both observations unknown.
            result.Add(new(guid.ToString("D"), version, name));
        }
        return result.OrderBy(item => item.PluginGuid, StringComparer.Ordinal).ToArray();
    }

    public async Task<Outcome> ExecuteAsync(Change change)
    {
        Validate(change);
        var manager = ExistingManager(); var old = Find(manager, change.PluginGuid);
        var dispatched = false;
        try
        {
            if (change.Operation == ModuleOperation.Delete)
            {
                // Repeat native dependency check immediately before actual removal.
                if (UsedPlugin(change.PluginGuid)) throw Error("PluginInUse", "The native project dependency list includes this plugin.");
                dispatched = true;
                managerType.GetMethod("DeleteItem", Flags, null, [itemType, typeof(bool)], null)!.Invoke(manager, [old!, true]);
            }
            else
            {
                var (archive, inspected) = PrepareArchive(change);
                using var staged = NativePluginLifecyclePackage.Stage(archive, inspected);
                var candidate = managerType.GetMethod("CreatePluginItem", Flags, null, [typeof(string)], null)!.Invoke(manager, [staged.ConfigPath])
                    ?? throw Error("PluginPackageConfigInvalid", "The native plugin parser returned no item.");
                if (!string.Equals(Text(candidate, "guid"), change.PluginGuid, StringComparison.OrdinalIgnoreCase) || Text(candidate, "version") != change.PackageVersion ||
                    Text(candidate, "dependenceVersion") != inspected.DependenceVersion)
                    throw Error("PluginPackageIdentityMismatch", "The native parser and reviewed plugin identity differ.");
                var checker = context.DesignerAssembly.GetType("Forguncy.Model.PluginVersionChecker", true)!;
                var status = checker.GetMethod("CheckPluginDependenceVersion", Flags, null, [itemType], null)!.Invoke(null, [candidate])?.ToString();
                if (status == "VersionHigherThanDesigner" || status is null) throw Error("PluginDesignerVersionUnsupported", "The native version checker rejected this plugin.");
                Validate(change);
                // This is the same native Java preparation used by the
                // Designer's installation helper; it can start its adapter.
                var java = context.DesignerAssembly.GetType("Forguncy.Common.JavaAdapter.Plugin.JavaPluginManager", true)!;
                var checkJava = java.GetMethod("CheckIsJavaPluginAndTryStartAdapterAsync", Flags, null, [typeof(string)], null)!;
                dispatched = true;
                if (!await (Task<bool>)checkJava.Invoke(null, [staged.Root])!) return Unknown(change.PluginGuid);
                Validate(change);
                old = Find(manager, change.PluginGuid);
                if (change.Operation == ModuleOperation.Create)
                    managerType.GetMethod("AddNewPluginItem", Flags, null, [typeof(string), itemType], null)!.Invoke(manager, [staged.Root, candidate]);
                else managerType.GetMethod("ReplacePluginItem", Flags, null, [typeof(string), itemType, itemType], null)!.Invoke(manager, [staged.Root, candidate, old!]);
            }
            var after = Find(manager, change.PluginGuid);
            var verified = change.Operation == ModuleOperation.Delete ? after is null : after is not null && Text(after, "version") == change.PackageVersion;
            if (!verified) return Unknown(change.PluginGuid);
            var restart = managerType.GetProperty("NeedReloadAssembly", Flags)?.GetValue(manager) is true;
            return new(restart ? "restart_required" : "native_accepted", "NativePluginAccepted", "The native plugin manager accepted the action.",
                true, true, restart, false, false, change.PluginGuid, after is null ? null : Text(after, "version"), Limitation);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (dispatched) return Unknown(change.PluginGuid);
            return new("failed", error is BridgeException bridge ? bridge.Code : "NativePluginPreparationFailed", "Native plugin preparation failed before lifecycle dispatch.",
                false, false, false, false, false, change.PluginGuid, null, Limitation);
        }
    }

    private object ExistingManager()
    {
        var manager = managerType.GetField(profile.SingletonField, Flags)!.GetValue(null)
            ?? throw Error("NativePluginRegistryNotInitialized", "The existing native plugin manager is not initialized.");
        var loadField = profile.Version switch { "11.0.104.0" => "bfe", "11.0.3.0" => "bdd", _ => null };
        if (loadField is not null && managerType.GetField(loadField, Flags)?.GetValue(manager) is not Task { IsCompletedSuccessfully: true })
            throw Error("NativePluginRegistryNotReady", "The existing native plugin manager has not completed its plugin load.");
        return manager;
    }
    private (byte[] Archive, NativePluginLifecyclePackage.Inspection Inspection) PrepareArchive(Change change)
    {
        var input = change.Package!;
        if (input.Length is 0 or > 64 * 1024 * 1024 || change.PackageSha256 is not { Length: 64 } ||
            !Convert.ToHexString(SHA256.HashData(input)).Equals(change.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw Error("PluginPackageHashMismatch", "The original plugin package differs from its reviewed SHA-256.");
        try { return (input, NativePluginLifecyclePackage.Inspect(input, change.PluginGuid, change.PackageVersion!, change.PackageSha256)); }
        catch (InvalidDataException)
        {
            // Designer's native install helper tries the same version-pinned
            // CommonUtilities decryptor after plain ZIP extraction fails.
            var decryptType = itemType.Assembly.GetType(profile.DecryptType, true)!;
            var decrypt = decryptType.GetMethod("b", Flags, null, [typeof(byte[])], null);
            if (decrypt?.ReturnType != typeof(byte[])) throw Error("NativePluginContractChanged", "The native package decoder contract changed.");
            byte[] archive;
            try { archive = (byte[])decrypt.Invoke(Activator.CreateInstance(decryptType), [input])!; }
            catch { throw Error("PluginPackageInvalid", "The package is neither a valid ZIP nor a supported native encrypted package."); }
            if (archive.Length is 0 or > 64 * 1024 * 1024) throw Error("PluginPackageSizeInvalid", "The decoded plugin package exceeds its size limit.");
            var decodedHash = Convert.ToHexString(SHA256.HashData(archive));
            return (archive, NativePluginLifecyclePackage.Inspect(archive, change.PluginGuid, change.PackageVersion!, decodedHash));
        }
    }
    private object? Find(object manager, string guid)
    {
        if (managerType.GetProperty("PluginItems", Flags)?.GetValue(manager) is not IEnumerable items)
            throw Error("NativePluginRegistryUnverified", "The native plugin registry is unavailable.");
        var matches = items.Cast<object>().Where(item => string.Equals(Text(item, "guid"), guid, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (matches.Length > 1) throw Error("NativePluginIdentityAmbiguous", "The native plugin registry contains duplicate identities.");
        return matches.SingleOrDefault();
    }
    private bool UsedPlugin(string guid)
    {
        var type = context.DesignerAssembly.GetType(profile.UtilityType, true)!;
        var method = type.GetMethod("GetUsedPluginItems", Flags, null, Type.EmptyTypes, null)!;
        if (method.Invoke(null, null) is not IEnumerable items) throw Error("NativePluginReferencesUnverified", "The native dependency list is unavailable.");
        return items.Cast<object>().Any(item => string.Equals(Text(item, "guid"), guid, StringComparison.OrdinalIgnoreCase));
    }
    private static string? Text(object value, string property) => value.GetType().GetProperty(property, Flags)?.GetValue(value) as string;
    private static Outcome Unknown(string guid) => new("verification_failed", "NativePluginOutcomeUnconfirmed",
        "The native plugin outcome could not be confirmed; retain the original ID and do not replay.", null, false, false, false, true, guid, null, Limitation);
    private static BridgeException Error(string code, string message) => new(code, message);
}
