using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

// Permission group definitions and role mappings belong to the current native
// PermissionGroupConfig root. Deletion is limited to an unreferenced leaf.
internal static class NativePermissionGroupGuard
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal sealed record Group(string Id, string? ParentId, bool IsDefault);
    internal sealed record Binding(string RoleId, string GroupId);

    internal static void RequireUnreferencedLeaf(string id, IReadOnlyList<Group> groups, IReadOnlyList<Binding> bindings, int resourceCount)
    {
        if (string.IsNullOrWhiteSpace(id) || resourceCount < 0) throw new BridgeException("PermissionGroupReferenceScanIncomplete", "Permission group reference evidence is incomplete.");
        var matches = groups.Where(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || groups.Any(g => string.IsNullOrWhiteSpace(g.Id)) || groups.Select(g => g.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != groups.Count ||
            bindings.Any(b => string.IsNullOrWhiteSpace(b.RoleId) || string.IsNullOrWhiteSpace(b.GroupId)))
            throw new BridgeException("PermissionGroupReferenceScanIncomplete", "Native permission identities are missing or ambiguous.");
        if (matches[0].IsDefault || groups.Any(g => string.Equals(g.ParentId, id, StringComparison.OrdinalIgnoreCase)) ||
            bindings.Any(b => string.Equals(b.GroupId, id, StringComparison.OrdinalIgnoreCase)) || resourceCount != 0)
            throw new BridgeException("PermissionGroupReferenced", "A default, parent, role-bound or resource-referenced permission group cannot be deleted by this guarded route.");
    }

    internal static object BoundConfig(Assembly designer, object settings)
    {
        var app = NativeReflection.Get(settings, "ApplicationSettings")!;
        if (Convert.ToInt32(NativeReflection.Get(app, "PermissionMode")) != 1) throw new BridgeException("PermissionGroupModeRequired", "Permission-group mode is required.");
        var config = NativeReflection.Get(settings, "PermissionGroupConfig") ?? throw new BridgeException("PermissionGroupContextUnavailable", "The native permission configuration is unavailable.");
        var provider = NativeReflection.Static(designer.GetType("Forguncy.RbacPermission.DesignerRolePermissionGroupProvider", true)!, "get_Instance")!;
        if (!ReferenceEquals(config, NativeReflection.Get(provider, "PermissionGroupConfig"))) throw new BridgeException("PermissionGroupContextUnavailable", "The native permission provider does not belong to the bound document.");
        return config;
    }

    internal static void ValidateDelete(Assembly designer, object settings, JsonElement parameters)
    {
        var id = Text(parameters, "id");
        if (parameters.EnumerateObject().Any(p => p.Name.Equals("recursive", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True) ||
            parameters.EnumerateObject().Any(p => p.Name.Equals("replacementDefaultPermissionGroupId", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)))
            throw new BridgeException("PermissionGroupDeleteScopeDenied", "Recursive deletion and default replacement require a separate complete effect plan.");
        var config = BoundConfig(designer, settings);
        var groups = Values(config, "PermissionGroups").Select(g => new Group(Value(g,"Id"), NativeReflection.Get(g,"ParentId") as string, NativeReflection.Get(g,"IsDefault") is true)).ToArray();
        var bindings = ReadBindings(config);
        var scanner = designer.GetType("abl", true)!.GetMethod("gsp", Flags, null, [typeof(HashSet<string>)], null);
        if (scanner is null || !scanner.IsStatic) throw new BridgeException("PermissionGroupReferenceScanIncomplete", "The native permission resource scanner is unavailable.");
        var references = scanner.Invoke(null, [new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }]) as ICollection
            ?? throw new BridgeException("PermissionGroupReferenceScanIncomplete", "The native permission resource scan did not complete.");
        RequireUnreferencedLeaf(id, groups, bindings, references.Count);
    }

    internal static Binding[] ReadBindings(object config) => Values(config,"RolePermissionGroups").Select(item => new Binding(Value(item,"RoleId"), Value(item,"PermissionGroupId"))).ToArray();
    internal static bool IsBindingMutation(string ns, string method) => ns == "app.permissionGroup" && method is "setRolePermissionGroupsForRole" or "setRolePermissionGroupsForRoles";

    internal static Dictionary<string, HashSet<string>> RequestedBindings(string method, JsonElement parameters)
    {
        var rows = method == "setRolePermissionGroupsForRole" ? new[] { parameters } :
            Property(parameters, "roles") is { ValueKind: JsonValueKind.Array } roleArray ? roleArray.EnumerateArray().Select(r => r.Clone()).ToArray() :
            throw new BridgeException("InvalidApiParameters", "A role binding list is required.");
        if (rows.Length is < 1 or > 1000) throw new BridgeException("InvalidApiParameters", "A bounded nonempty role binding list is required.");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidApiParameters", "Each role binding must be an object.");
            var role = Text(row, "roleId");
            var ids = Property(row, "permissionGroupIds");
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 10000) throw new BridgeException("InvalidApiParameters", "A bounded permission-group list is required.");
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ids.EnumerateArray())
                if (id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()) || !set.Add(id.GetString()!))
                    throw new BridgeException("InvalidApiParameters", "Permission-group IDs must be nonempty and unique.");
            if (!result.TryAdd(role, set)) throw new BridgeException("InvalidApiParameters", "Role IDs must be unique.");
        }
        return result;
    }

    internal static void VerifyBindings(IReadOnlyList<Binding> before, IReadOnlyList<Binding> after, Dictionary<string, HashSet<string>> requested)
    {
        static HashSet<string> Pairs(IEnumerable<Binding> items) => items.Select(b => b.RoleId.ToUpperInvariant() + "\0" + b.GroupId.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        if (before.Any(b=>string.IsNullOrWhiteSpace(b.RoleId)||string.IsNullOrWhiteSpace(b.GroupId)) || after.Any(b=>string.IsNullOrWhiteSpace(b.RoleId)||string.IsNullOrWhiteSpace(b.GroupId)) ||
            Pairs(before).Count != before.Count || Pairs(after).Count != after.Count)
            throw new BridgeException("PermissionGroupBindingReadbackFailed", "Native role bindings are incomplete or ambiguous.");
        var expected = Pairs(before.Where(b => !requested.ContainsKey(b.RoleId)));
        foreach (var (role, ids) in requested) foreach (var id in ids) expected.Add(role.ToUpperInvariant() + "\0" + id.ToUpperInvariant());
        if (!expected.SetEquals(Pairs(after))) throw new BridgeException("PermissionGroupBindingReadbackFailed", "The native role binding result differs from the requested mappings.");
    }

    private static JsonElement Property(JsonElement obj,string name)
    {
        var values = obj.EnumerateObject().Where(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Select(p=>p.Value).ToArray();
        return values.Length == 1 ? values[0] : default;
    }
    private static IEnumerable<object> Values(object obj, string name) => NativeReflection.Get(obj,name) is IEnumerable values ? values.Cast<object>().ToArray() :
        throw new BridgeException("PermissionGroupReferenceScanIncomplete", "A native permission collection is unavailable.");
    private static string Value(object obj,string name) => NativeReflection.Get(obj,name) as string ?? "";
    internal static string Text(JsonElement obj,string name) => obj.EnumerateObject().SingleOrDefault(p=>p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)).Value is { ValueKind:JsonValueKind.String } value && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! :
        throw new BridgeException("InvalidApiParameters", "A required permission identity is unavailable.");
}
