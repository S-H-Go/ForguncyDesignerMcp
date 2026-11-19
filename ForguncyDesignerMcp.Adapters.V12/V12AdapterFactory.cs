using System.Security.Cryptography;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed class V12AdapterFactory : IProjectAdapterFactory
{
    public const string Version = "12.0.102.0";
    public const string DesignerSha256 = "A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246";
    public const string Profile = "v12-12.0.102.0-csharp";
    public bool Supports(AdapterIdentity identity) => identity.DesignerVersion == Version &&
        string.Equals(identity.DesignerSha256, DesignerSha256, StringComparison.OrdinalIgnoreCase);

    public IProjectAdapter Create(AdapterHostContext context)
    {
        var assembly = context.DesignerAssembly;
        if (assembly.GetName().Name != "Forguncy" || assembly.GetName().Version?.ToString() != Version ||
            !File.Exists(assembly.Location) || Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild", "V12 adapter requires the verified 12.0.102.0 designer component.");
        if (context.NativeDocument.GetType().Assembly != assembly)
            throw new BridgeException("NativeDocumentMismatch", "The supplied document is not owned by the verified designer assembly.");
        return new V12ProjectAdapter(context);
    }
}
