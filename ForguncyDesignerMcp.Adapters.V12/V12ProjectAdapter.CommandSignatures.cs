using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter
{
    private void RequireUnreferencedCommandSignature(string ns,string method,JsonElement parameters)
    {
        var name=parameters.EnumerateObject().FirstOrDefault(p=>p.Name.Equals("name",StringComparison.OrdinalIgnoreCase)).Value;
        if(name.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString())) throw new BridgeException("InvalidCommandSignatureRequest","The native command name is required.");
        var address=new ModuleAddress(ns=="serverCommand"?ModuleKind.ServerCommand:ModuleKind.SharedCommand,name.GetString()!);
        var owner=targetFiles.Value.Resolve(address) ?? throw new BridgeException("NativeCommandReferenceContextUnavailable","The original native command owner could not be resolved.");
        NativeCommandReferenceGuard.RequireUnreferenced(context.DesignerAssembly,NativeReflection.Get(context.NativeDocument,"ForguncySettings")!,targetFiles.Value,owner);
    }
}
