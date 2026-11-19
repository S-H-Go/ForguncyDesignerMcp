using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V11;

// Exact factories used by native mi/mc.AddNewPage. Only NormalPage has this ACL;
// MasterPage and UserControlPage must retain their own native type semantics.
internal static class NativeV11PagePermissions
{
    internal static void ValidateCreateInput(JsonElement pageInfo)
    {
        if (pageInfo.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Native page information is required.");
        foreach (var property in pageInfo.EnumerateObject())
        {
            if (property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("PagePermissionInitializationRequired", "New normal-page permissions are initialized by the native default permission factory.");
            if ((property.Name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) || property.Name.EndsWith("RoleList", StringComparison.OrdinalIgnoreCase)) && HasLegacyGrant(property.Value))
                throw new BridgeException("PagePermissionInitializationRequired", "New normal pages cannot supply legacy authorization grants.");
        }
    }

    internal static void InitializeDefault(Assembly designer, object page)
    {
        if (page.GetType().FullName != "Forguncy.Model.Pages.NormalPage")
            throw new InvalidDataException("Normal-page default permissions require the exact native NormalPage type.");
        NativeV11DefinitionWriter.VerifyPermissionComponents(designer);
        var newer = V11BuildProfile.Resolve(designer).Version == "11.0.104.0";
        var factory = NativeV11Reflection.Static(designer.GetType(newer ? "jv" : "jp", true)!, "get_Instance")!;
        var permission = NativeV11Reflection.Call(factory, "CreateForDesigner", true)
            ?? throw new InvalidOperationException("The native page permission factory returned no default permission data.");
        Assign(designer, page, permission);
    }

    internal static void Assign(Assembly designer, object page, object permission)
    {
        NativeV11DefinitionWriter.VerifyPermissionComponents(designer);
        if (page.GetType().FullName != "Forguncy.Model.Pages.NormalPage" ||
            !permission.GetType().GetInterfaces().Any(type => type.FullName == "Forguncy.RbacPermission.Core.Impl.Page.IPagePermissionData"))
            throw new InvalidDataException("The native page permission object or target type is invalid.");
        NativeV11Reflection.Set(page, "PermissionData", permission);
    }

    internal static void Preserve(JsonElement before, JsonElement after)
    {
        var protectedNames = before.EnumerateObject().Select(property => property.Name)
            .Union(after.EnumerateObject().Select(property => property.Name), StringComparer.Ordinal)
            .Where(name => name.Contains("Permission", StringComparison.OrdinalIgnoreCase) || name.Contains("Authoriz", StringComparison.OrdinalIgnoreCase) || name.EndsWith("RoleList", StringComparison.OrdinalIgnoreCase));
        foreach (var name in protectedNames)
        {
            var a = before.TryGetProperty(name, out var left); var b = after.TryGetProperty(name, out var right);
            if (a != b || a && !V11Safety.Equivalent(left, right))
                throw new BridgeException("ProtectedPageIdentity", "Normal-page permission and legacy authorization fields must remain unchanged: " + name);
        }
    }

    private static bool HasLegacyGrant(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined => false,
        JsonValueKind.Array => value.GetArrayLength() != 0,
        JsonValueKind.Object => value.EnumerateObject().Any(property => HasLegacyGrant(property.Value)),
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Number => !value.TryGetDecimal(out var number) || number != 0,
        _ => true
    };
}
