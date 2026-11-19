using System.Reflection;
using System.IO;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Read-only contract discovery. This does not initialize the manager, clone,
// replace the current document, create a target directory or clean failures.
internal static class NativeV11ReceiverContract
{
    internal static string RequireFreshDestination(string approvedRoot, string destination, string originalSource)
    {
        if (!Path.IsPathFullyQualified(approvedRoot) || !Path.IsPathFullyQualified(destination) || !Path.IsPathFullyQualified(originalSource))
            throw new InvalidDataException("Native receive requires absolute policy, destination and source paths.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(approvedRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalSource));
        var leaf = Path.GetFileName(target);
        var receiverLeaf = leaf.StartsWith("receiver-", StringComparison.Ordinal) ? leaf[9..] : leaf;
        if (!Directory.Exists(root) || !string.Equals(Path.GetDirectoryName(target), root, StringComparison.OrdinalIgnoreCase) ||
            !(Guid.TryParseExact(receiverLeaf, "D", out var receiverId) || Guid.TryParseExact(receiverLeaf, "N", out receiverId)) || receiverId == Guid.Empty ||
            Path.Exists(target) || source.Equals(target, StringComparison.OrdinalIgnoreCase) ||
            source.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Native receive requires an unused direct child of the approved root outside the original source.");
        for (var path = root; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The approved receiver root cannot traverse a filesystem link.");
        return target;
    }

    internal static void RequireDependencies(System.Text.Json.JsonElement declarations, Func<IReadOnlyList<PluginInstallationInfo>> installed)
    {
        if (declarations.ValueKind != System.Text.Json.JsonValueKind.Array || declarations.GetArrayLength() > 10000)
            throw new BridgeException("ReceiveDependenciesRequireReview", "The native plugin declaration is invalid.");
        if (declarations.GetArrayLength() == 0) return;
        var available = installed();
        var registry = new Dictionary<Guid, Version>();
        foreach (var item in available)
            if (!Guid.TryParse(item.PluginGuid, out var id) || id == Guid.Empty || !Version.TryParse(item.Version, out var version) || !registry.TryAdd(id, version))
                throw new BridgeException("NativePluginRegistryUnverified", "The installed plugin registry contains invalid or duplicate identities.");
        var seen = new HashSet<Guid>();
        foreach (var item in declarations.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object ||
                item.EnumerateObject().Count(p => p.NameEquals("guid")) != 1 || item.EnumerateObject().Count(p => p.NameEquals("version")) != 1 ||
                !item.TryGetProperty("guid", out var g) || g.ValueKind != System.Text.Json.JsonValueKind.String || !Guid.TryParse(g.GetString(), out var id) || id == Guid.Empty ||
                !item.TryGetProperty("version", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String || !Version.TryParse(v.GetString(), out var version) || !seen.Add(id))
                throw new BridgeException("ReceiveDependenciesRequireReview", "A native plugin dependency has an invalid or duplicate GUID/version.");
            if (!registry.TryGetValue(id, out var actual) || actual != version)
                throw new BridgeException("ReceiveDependencyUnavailable", "A declared native plugin or its exact version is not installed.");
        }
    }

    internal sealed record Profile(Type Manager, Type Option, Type TaskInfo, MethodInfo Initialize,
        MethodInfo Reload, MethodInfo Open, Type OpenResult, ConstructorInfo OptionConstructor,
        ConstructorInfo TaskConstructor);

    internal static Profile Inspect(Assembly designer)
    {
        _ = V11BuildProfile.Resolve(designer);
        NativeV11Collaboration.VerifyComponentFingerprints(designer);
        var collaboration = Assembly.Load("Forguncy.Collaboration");
        var wpf = Assembly.Load("WPFBase");
        if (collaboration.GetName().Version != designer.GetName().Version || wpf.GetName().Version != designer.GetName().Version)
            throw new NotSupportedException("Native receiver assemblies do not match the pinned Designer build.");
        var manager = designer.GetType("Forguncy.Common.CollaborationManager", true)!;
        var option = collaboration.GetType("Forguncy.Collaboration.InitializeWorkingDirectoryOption", true)!;
        var task = wpf.GetType("WPFBase.Common.TaskInfo", true)!;
        var result = manager.GetNestedType("OpenResult", NativeV11Reflection.Flags) ?? throw new MissingMemberException("Native receiver result unavailable.");
        var initialize = manager.GetMethod("TryInitializeAndUpdateWorkingDirectory", NativeV11Reflection.Flags, null, [option, task, typeof(string), typeof(bool)], null)
            ?? throw new MissingMethodException("Native receiver initialization unavailable.");
        var reload = manager.GetMethod("ReloadModules", NativeV11Reflection.Flags, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException("Native receiver registry reload unavailable.");
        var open = manager.GetMethod("OpenWithVersionCheck", NativeV11Reflection.Flags, null, [task], null)
            ?? throw new MissingMethodException("Native receiver versioned open unavailable.");
        var optionConstructor = option.GetConstructor(NativeV11Reflection.Flags, null, [typeof(bool), typeof(string), typeof(string), typeof(string)], null)
            ?? throw new MissingMethodException("Native explicit receiver parent option unavailable.");
        var taskConstructor = task.GetConstructor(NativeV11Reflection.Flags, null, [typeof(bool), typeof(bool), typeof(int)], null)
            ?? throw new MissingMethodException("Native receiver progress object unavailable.");
        if (!initialize.IsAssembly || initialize.ReturnType != typeof(bool) || !reload.IsPublic || reload.ReturnType != typeof(void) ||
            !open.IsPrivate || open.ReturnType != typeof(Task<>).MakeGenericType(result) || !result.IsEnum ||
            Convert.ToInt32(Enum.Parse(result, "OpenInCollaboration")) != 2)
            throw new NotSupportedException("Native receiver lifecycle contract differs from the pinned builds.");
        return new(manager, option, task, initialize, reload, open, result, optionConstructor, taskConstructor);
    }
}
