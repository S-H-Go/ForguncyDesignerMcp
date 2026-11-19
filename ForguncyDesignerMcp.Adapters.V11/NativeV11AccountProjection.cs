using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Designer's IUserService offers only projections, not the management API.
// This helper is deliberately not registered as full module List/Read: role
// names are not the management service's numeric RoleId, and the projection
// lacks the complete user/role relation set required for CUD preconditions.
internal sealed class NativeV11AccountProjection
{
    private readonly NativeV11Session session;
    internal NativeV11AccountProjection(NativeV11Session session) => this.session = session;

    internal async Task<IReadOnlyList<ModuleDocument>> ReadProjectionAsync(ModuleKind kind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        session.VerifyContext();
        var methodName = kind switch
        {
            ModuleKind.User => "GetUserNamesAsync",
            ModuleKind.Role => "GetRoleNamesAsync",
            ModuleKind.Organization => "GetOrganizationNodesAsync",
            _ => throw new BridgeException("AccountModuleRequired", "Only account projections are supported.")
        };
        var service = NativeV11Reflection.Get(session.NativeApplication, "UserService")
            ?? throw new BridgeException("AccountServiceUnavailable", "The native Designer user service is unavailable.");
        var contract = session.Context.DesignerAssembly.GetType("Forguncy.UserService.Abstraction.IUserService", true)!;
        if (!contract.IsInstanceOfType(service)) throw new BridgeException("AccountNativeContractChanged", "The native Designer user service contract changed.");
        var method = contract.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null)
            ?? throw new BridgeException("AccountNativeContractChanged", "The native Designer account projection is unavailable.");
        // Native invocation uses the Designer's existing InvokeableExtensions
        // lifecycle and may start its service. Cancellation before this point is
        // safe; after dispatch await the native task to its actual completion.
        var task = method.Invoke(service, null) as Task
            ?? throw new BridgeException("AccountNativeContractChanged", "The native account projection did not return a task.");
        await task;
        session.VerifyContext();
        var result = task.GetType().GetProperty("Result")?.GetValue(task) as IEnumerable
            ?? throw new BridgeException("AccountNativeContractChanged", "The native account projection returned an unexpected result.");
        return ConvertProjection(kind, result);
    }

    internal static IReadOnlyList<ModuleDocument> ConvertProjection(ModuleKind kind, IEnumerable result)
    {
        var documents = new List<ModuleDocument>();
        foreach (var item in result)
        {
            if (item is null || documents.Count >= 500000)
                throw new BridgeException("AccountNativeShapeChanged", "The native account projection is invalid or exceeds its size limit.");
            string identity;
            JsonElement metadata;
            if (kind is ModuleKind.User or ModuleKind.Role)
            {
                identity = item as string ?? throw new BridgeException("AccountNativeShapeChanged", "The native account name is invalid.");
                metadata = JsonSerializer.SerializeToElement(new { projection = "designer-name-only", name = identity, complete = false });
            }
            else if (kind == ModuleKind.Organization)
            {
                var type = item.GetType();
                object? Get(string name) => type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item)
                    ?? (name == "ParentID" ? null : throw new BridgeException("AccountNativeShapeChanged", "The native organization projection changed."));
                if (Get("ID") is not int id || id <= 0 || Get("Name") is not string name || Get("NodeLevelID") is not int level)
                    throw new BridgeException("AccountNativeShapeChanged", "The native organization projection changed.");
                var parent = Get("ParentID");
                if (parent is not null and not int) throw new BridgeException("AccountNativeShapeChanged", "The native organization parent changed.");
                identity = id.ToString(CultureInfo.InvariantCulture);
                metadata = JsonSerializer.SerializeToElement(new { projection = "designer-organization-node", id, parentId = parent, name, nodeLevelId = level, complete = false });
            }
            else throw new BridgeException("AccountModuleRequired", "Only account projections are supported.");
            if (string.IsNullOrWhiteSpace(identity) || identity.Any(char.IsControl))
                throw new BridgeException("AccountNativeShapeChanged", "The native account projection contains an invalid identity.");
            var address = new ModuleAddress(kind, identity);
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v11-unbound-designer-projection-v1\n" + kind + "\n" + metadata.GetRawText())));
            documents.Add(new(address, metadata, [], digest));
        }
        if (documents.Select(document => document.Address).Distinct().Count() != documents.Count)
            throw new BridgeException("AccountIdentityAmbiguous", "The native projection contains duplicate identities.");
        return documents.OrderBy(document => document.Address.Name, StringComparer.Ordinal).ToArray();
    }
}
