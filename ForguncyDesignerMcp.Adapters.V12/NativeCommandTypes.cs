using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// The pinned Designer subscribes this handler at the end of nw's initializer.
// Inspecting ma's already-existing singleton is noninitializing in this build:
// ma has no type initializer, and we never call its Instance getter here.
internal static class NativeCommandTypes
{
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static void ValidateParameters(JsonElement parameters)
    {
        var scope=parameters.EnumerateObject().FirstOrDefault(p=>p.Name.Equals("scope",StringComparison.OrdinalIgnoreCase)).Value;
        // Exact scalar alternatives in this pinned native ListTypes action.
        // Validate before its first prototype lookup, rather than quarantining
        // an invalid scope after a runtime initialization was marked started.
        if(scope.ValueKind!=JsonValueKind.String || scope.GetString() is not ("server" or "scheduledTask" or "client" or "pageLoad" or "pageMethod" or "shared"))
            throw new BridgeException("InvalidCommandScope","Command scope must be server, scheduledTask, client, pageLoad, pageMethod or shared.");
    }

    internal static bool HasInitializedRegistryEvidence(Assembly designer)
    {
        var managerType = designer.GetType("ma", true)!;
        var commandType = designer.GetType("nw", true)!;
        if (managerType.TypeInitializer is not null)
            throw new InvalidOperationException("The native plugin manager inspection contract has changed.");
        var singleton = managerType.GetField("bjt", Static) ?? throw new MissingFieldException("ma", "bjt");
        var changed = managerType.GetField("bju", Instance) ?? throw new MissingFieldException("ma", "bju");
        var handler = commandType.GetMethod("Instance_PluginChanged", Static) ?? throw new MissingMethodException("nw", "Instance_PluginChanged");
        var manager = singleton.GetValue(null);
        if (manager is null) return false;
        return (changed.GetValue(manager) as Delegate)?.GetInvocationList().Any(item => item.Method == handler) == true;
    }

    // Use the ordinary pinned native dispatcher, including its request validation
    // and serializer, so all native command descriptions and property metadata
    // are returned. A false readiness result means the native first invocation
    // can initialize the command and plugin registries; it is not a rejection.
    internal static Task<JsonElement> ListAsync(NativeApiRegistry registry, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ValidateParameters(parameters);
        return registry.InvokeAsync("command", "listTypes", parameters, cancellationToken);
    }
}

