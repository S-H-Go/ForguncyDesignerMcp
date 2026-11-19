using System.Reflection;
using System.Security.Cryptography;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// Only discovers a pinned contract. This class does not invoke the receiver.
internal sealed record NativeReceiveContract(object Manager, Type OptionType, Type TaskType,
    MethodInfo Initialize, MethodInfo Reload, MethodInfo Open, string ProgressMessage)
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    internal static NativeReceiveContract Resolve(Assembly designer)
    {
        Verify(designer, "Forguncy", V12AdapterFactory.DesignerSha256, null);
        var directory = Path.GetDirectoryName(designer.Location)!;
        var collaboration = Assembly.Load("Forguncy.Collaboration");
        var wpf = Assembly.Load("WPFBase");
        var resource = Assembly.Load("ForguncyResource");
        Verify(collaboration, "Forguncy.Collaboration", "2A0AB78316A5BFD1C66332D8D50C7935B817C82CB5409DD91480017DA9791A1D", directory);
        Verify(wpf, "WPFBase", "8841C1F73CF00476F3993BD66A3C5AB9BD0BFAE966298E8D6604A151299B1747", directory);
        Verify(resource, "ForguncyResource", "7A607EE3ACD52E8CB3E207D29F413BB2656AB2C579E2D3DE8E06F2A834983EA8", directory);
        var option = collaboration.GetType("Forguncy.Collaboration.InitializeWorkingDirectoryOption", true)!;
        var task = wpf.GetType("WPFBase.Common.TaskInfo", true)!;
        if (option.GetConstructor([typeof(bool), typeof(string), typeof(string), typeof(string)]) is null ||
            task.GetConstructor([typeof(bool), typeof(bool), typeof(int)]) is null)
            throw Error("Native receiver constructor signature changed.");
        var managerType = designer.GetType("Forguncy.Common.CollaborationManager", true)!;
        var manager = managerType.GetProperty("Instance", Flags)?.GetValue(null) ?? throw Error("Native collaboration manager unavailable.");
        var initialize = managerType.GetMethod("TryInitializeAndUpdateWorkingDirectory", Flags, null, [option, task, typeof(string), typeof(bool)], null) ?? throw Error("Native initialize method unavailable.");
        var reload = managerType.GetMethod("ReloadModules", Flags, null, Type.EmptyTypes, null) ?? throw Error("Native reload method unavailable.");
        var open = managerType.GetMethod("OpenWithVersionCheck", Flags, null, [task], null) ?? throw Error("Native versioned open method unavailable.");
        var result = managerType.GetNestedType("OpenResult", Flags) ?? throw Error("Native open result unavailable.");
        if (!initialize.IsAssembly || initialize.ReturnType != typeof(bool) || !reload.IsPublic || reload.ReturnType != typeof(void) ||
            !open.IsPrivate || open.ReturnType != typeof(Task<>).MakeGenericType(result) || !result.IsEnum ||
            Convert.ToInt32(Enum.Parse(result, "OpenInCollaboration")) != 2)
            throw Error("Native receiver method or result contract changed.");
        var resourceType = resource.GetType("Forguncy.Properties.Resources", true)!;
        var progress = resourceType.GetProperty("DownloadDocumentsFromCollaborationServer", Flags)?.GetValue(null) as string;
        if (string.IsNullOrWhiteSpace(progress)) throw Error("Native receiver progress resource unavailable.");
        return new(manager, option, task, initialize, reload, open, progress);
    }

    private static void Verify(Assembly assembly, string name, string expectedHash, string? directory)
    {
        var identity = assembly.GetName();
        if (identity.Name != name || identity.Version?.ToString() != V12AdapterFactory.Version ||
            string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location) ||
            (directory is not null && !string.Equals(Path.GetDirectoryName(assembly.Location), directory, StringComparison.OrdinalIgnoreCase)))
            throw Error("Native receiver assembly identity or location changed.");
        using var file = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(file)) != expectedHash) throw Error("Native receiver assembly fingerprint changed.");
    }
    private static BridgeException Error(string message) => new("NativeReceiveContractChanged", message);
}
