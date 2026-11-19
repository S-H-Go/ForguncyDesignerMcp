using System.Reflection;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

internal static class NativeReceiveFormat
{

    internal static void RequireNativeVersion(Assembly designer, string working)
    {
        var type = designer.GetType("gf", true)!;
        var method = type.GetMethod("GetOpeningFileVersion", NativeReflection.Flags, null, [typeof(string)], null);
        if (method is null || !method.IsStatic || method.ReturnType != typeof(Version))
            throw new BridgeException("NativeReceiveContractChanged", "The pinned native format probe changed.");
        var projectVersion = method.Invoke(null, [working]) as Version ?? throw new BridgeException("ReceiveVersionUnsupported", "Native project version is unavailable.");
        var installed = Version.Parse(V12AdapterFactory.Version);
        if (projectVersion < installed) throw new BridgeException("ReceiveRequiresConversion", "Native project requires an interactive upgrade.");
        if (projectVersion > installed) throw new BridgeException("ReceiveVersionUnsupported", "Native project is newer than this Designer.");
    }
}

