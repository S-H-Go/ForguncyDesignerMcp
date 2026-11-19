using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// These two fixed GET routes mirror the installed V12 assistant's normalized
// native API and use this V11 process's own RBAC context and InvokeService.
internal sealed class NativeV11OrganizationRead(NativeV11Session session)
{
    private static readonly ConcurrentDictionary<(Assembly, string), Lazy<Route>> Routes = new();
    internal static Type InspectRequest(Assembly assembly, string action) => GetRoute(assembly, action).RequestType;
    internal static bool Handles(PlatformApiRequest request) => (request.Namespace, request.Method) is
        ("userManagement", "getContext") or ("userManagement.role", "getRole" or "listRoles") or ("userManagement.organization", "listOrganizations" or "getOrganization" or "listOrganizationLevels");
    internal static IReadOnlyList<PlatformApiDescriptor> Descriptors() =>
    [
        new("userManagement", "getContext", PlatformJson.Element(new { type = "object", additionalProperties = false, properties = new { } }), null, false, EffectScope.AccountService,
            "Reads native authentication mode, provider storage mode, user properties and organization levels; V11 organization property names are not yet enumerated."),
        new("userManagement.role", "getRole", IdSchema("roleId"), null, false, EffectScope.AccountService, "Read a role by native numeric ID."),
        new("userManagement.role", "listRoles", PlatformJson.Element(new { type = "object", additionalProperties = false, properties = new { } }), null, false, EffectScope.AccountService, "Read the native role tree and numeric role identities."),
        new("userManagement.organization", "getOrganization", IdSchema("organizationId"), null, false, EffectScope.AccountService, "Read an organization by native numeric ID."),
        new("userManagement.organization", "listOrganizationLevels", PlatformJson.Element(new { type = "object", additionalProperties = false, properties = new { } }), null, false, EffectScope.AccountService, "Read native organization levels and IDs."),
        new("userManagement.organization", "listOrganizations", PlatformJson.Element(new { type = "object", additionalProperties = false, properties = new { } }), null, false, EffectScope.AccountService, "Read native organization structure and IDs.")
    ];
    private static JsonElement IdSchema(string name) => PlatformJson.Element(new { type = "object", additionalProperties = false,
        properties = new Dictionary<string, object> { [name] = new { type = "integer", minimum = 1 } }, required = new[] { name } });

    internal async Task<JsonElement> ReadAsync(PlatformApiRequest request, CancellationToken token)
    {
        if (!Handles(request) || request.Parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("NativeServiceApiNotAllowed", "The native account read is not registered.");
        if (request.Method == "getContext") return await ReadContextAsync(request.Parameters, token);
        if (request.Method == "listRoles") return await ReadRoleListAsync(request.Parameters, token);
        if (request.Method == "listOrganizationLevels") return await ReadLevelsAsync(request.Parameters, token);
        var idKey = request.Method == "getRole" ? "roleId" : request.Method == "getOrganization" ? "organizationId" : null;
        var properties = request.Parameters.EnumerateObject().ToArray();
        if (properties.Length != (idKey is null ? 0 : 1) || idKey is not null && properties[0].Name != idKey)
            throw new BridgeException("InvalidAccountArguments", "The account read requires only its registered native ID.");
        var id = 0;
        if (idKey is not null && (properties[0].Value.ValueKind != JsonValueKind.Number || !properties[0].Value.TryGetInt32(out id) || id <= 0))
            throw new BridgeException("InvalidAccountArguments", "The native entity ID must be a positive integer.");
        session.VerifyContext();
        var role = request.Method == "getRole";
        var route = GetRoute(session.Context.DesignerAssembly, role ? "getRole" : "listOrganizations");
        var native = Activator.CreateInstance(route.RequestType)!;
        if (role) route.RequestType.GetProperty("id")!.SetValue(native, id);
        token.ThrowIfCancellationRequested();
        var task = (Task)route.Invoke.Invoke(null, [native, null])!;
        await task; session.VerifyContext();
        var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
        if (response.GetType().GetProperty("Result")!.GetValue(response) is not true)
            throw new BridgeException("NativeServiceReadRejected", "The native account service did not accept the read.");
        var data = response.GetType().GetProperty("Data")!.GetValue(response);
        if (role)
        {
            if (data is not V11NativeRoleRead nativeRole || nativeRole.ID != id || string.IsNullOrWhiteSpace(nativeRole.Name))
                throw new BridgeException("NativeServiceReadUnverified", "The native role response identity or shape changed.");
            return PlatformJson.Element(new { role = new { roleId = nativeRole.ID, name = nativeRole.Name, description = nativeRole.Description, folderId = nativeRole.FolderId } });
        }
        if (data is not List<V11NativeOrganizationRead> organizations || organizations.Count > 500000 ||
            organizations.Any(item => item.ID <= 0 || string.IsNullOrWhiteSpace(item.Name)) || organizations.Select(item => item.ID).Distinct().Count() != organizations.Count)
            throw new BridgeException("NativeServiceReadUnverified", "The native organization response identity or shape changed.");
        object Convert(V11NativeOrganizationRead item) => new { id = item.ID, parentId = item.ParentID, name = item.Name,
            nodeLevelId = item.NodeLevelID, nodeOrder = item.NodeOrder, description = item.Description, properties = item.Properties };
        if (idKey is not null)
        {
            var entity = organizations.SingleOrDefault(item => item.ID == id)
                ?? throw new BridgeException("AccountProjectionNotFound", "The native organization inventory did not contain that ID.");
            return PlatformJson.Element(new { node = Convert(entity) });
        }
        return PlatformJson.Element(new { organizations = organizations.Select(Convert).ToArray() });
    }

    private async Task<JsonElement> ReadContextAsync(JsonElement parameters, CancellationToken token)
    {
        if (parameters.EnumerateObject().Any()) throw new BridgeException("InvalidAccountArguments", "The native account context does not accept arguments.");
        session.VerifyContext();
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings")!;
        var application = NativeV11Reflection.Get(settings, "ApplicationSettings")!;
        var mode = NativeV11Reflection.Get(application, "AuthenticationMode")?.ToString()
            ?? throw new BridgeException("AccountAuthenticationUnverified", "The native authentication mode is unavailable.");
        var storage = await new NativeV11UserManagement(session).GetProviderStorageModeAsync(token);
        var service = NativeV11Reflection.Get(session.NativeApplication, "UserService")
            ?? throw new BridgeException("AccountServiceUnavailable", "The native Designer user service is unavailable.");
        var contract = session.Context.DesignerAssembly.GetType("Forguncy.UserService.Abstraction.IUserService", true)!;
        if (!contract.IsInstanceOfType(service)) throw new BridgeException("NativeServiceReadUnverified", "The native account service contract changed.");
        token.ThrowIfCancellationRequested();
        var task = (Task)contract.GetMethod("GetPropertyInfosAsync", Type.EmptyTypes)!.Invoke(service, null)!;
        await task; session.VerifyContext();
        var values = task.GetType().GetProperty("Result")!.GetValue(task) as IEnumerable
            ?? throw new BridgeException("NativeServiceReadUnverified", "The native user properties were unavailable.");
        var userProperties = new List<object>(); var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            if (item is null || userProperties.Count >= 500000 || item.GetType().GetProperty("PropertyName")?.GetValue(item) is not string name || !names.Add(name))
                throw new BridgeException("NativeServiceReadUnverified", "The native user property shape changed.");
            userProperties.Add(new { propertyName = name, propertyType = item.GetType().GetProperty("PropertyType")?.GetValue(item)?.ToString(),
                propertyDescription = item.GetType().GetProperty("PropertyDescription")?.GetValue(item) as string });
        }
        var levels = await ReadLevelsAsync(PlatformJson.Element(new { }), token);
        return PlatformJson.Element(new { authenticationMode = mode, isWindows = mode == "Windows", securityProviderStorageMode = storage,
            userProperties, organizationProperties = (object?)null, organizationLevels = levels.GetProperty("levels"),
            limitation = "V11 organization property names have not been enumerated; null means unknown." });
    }

    private async Task<JsonElement> ReadLevelsAsync(JsonElement parameters, CancellationToken token)
    {
        if (parameters.EnumerateObject().Any()) throw new BridgeException("InvalidAccountArguments", "The native organization level inventory does not accept arguments.");
        session.VerifyContext();
        var service = NativeV11Reflection.Get(session.NativeApplication, "UserService")
            ?? throw new BridgeException("AccountServiceUnavailable", "The native Designer user service is unavailable.");
        var contract = session.Context.DesignerAssembly.GetType("Forguncy.UserService.Abstraction.IUserService", true)!;
        if (!contract.IsInstanceOfType(service)) throw new BridgeException("NativeServiceReadUnverified", "The native account service contract changed.");
        var method = contract.GetMethod("GetOrganizationLevelsAsync", Type.EmptyTypes)!
            ?? throw new BridgeException("NativeServiceReadUnverified", "The native organization-level API is unavailable.");
        token.ThrowIfCancellationRequested();
        var task = (Task)method.Invoke(service, null)!; await task; session.VerifyContext();
        var rows = task.GetType().GetProperty("Result")!.GetValue(task) as IEnumerable
            ?? throw new BridgeException("NativeServiceReadUnverified", "The native organization levels were unavailable.");
        var result = new List<object>(); var ids = new HashSet<int>();
        foreach (var row in rows)
        {
            if (row is null || result.Count >= 500000 || row.GetType().GetProperty("ID")?.GetValue(row) is not int id || id <= 0 || !ids.Add(id) ||
                row.GetType().GetProperty("OrganizationLevelName")?.GetValue(row) is not string name)
                throw new BridgeException("NativeServiceReadUnverified", "The native organization level shape changed.");
            result.Add(new { id, name });
        }
        return PlatformJson.Element(new { levels = result });
    }

    private async Task<JsonElement> ReadRoleListAsync(JsonElement parameters, CancellationToken token)
    {
        if (parameters.EnumerateObject().Any()) throw new BridgeException("InvalidAccountArguments", "The native role inventory does not accept arguments.");
        session.VerifyContext();
        var designer = session.Context.DesignerAssembly;
        var apiType = designer.GetType("Forguncy.RbacPermission.Api.GetRbacRolesApi");
        if (apiType is null) return await ReadRoleTreeFallbackAsync(designer, token);
        var api = Activator.CreateInstance(apiType)!;
        apiType.GetProperty("IsHideInternalRole")!.SetValue(api, false);
        var invokeAssembly = Assembly.Load("Forguncy.Infrastructure.InvokeService");
        var invokeableDefinition = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.IInvokeable`1", true)!;
        var responseType = apiType.GetInterfaces().Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == invokeableDefinition).GenericTypeArguments[0];
        var invoke = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.InvokeableExtensions", true)!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "InvokeAsync" && method.IsGenericMethodDefinition && method.GetParameters().Length == 2)
            .Single(method => method.GetParameters()[0].ParameterType.IsGenericType && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == invokeableDefinition)
            .MakeGenericMethod(responseType);
        token.ThrowIfCancellationRequested();
        var task = (Task)invoke.Invoke(null, [api, null])!;
        await task; session.VerifyContext();
        var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
        if (responseType.GetProperty("Result")!.GetValue(response) is not true || responseType.GetProperty("Data")!.GetValue(response) is not IEnumerable rows)
            throw new BridgeException("NativeServiceReadRejected", "The native role inventory was unavailable.");
        var roles = new List<object>(); var tree = new List<object>(); var ids = new HashSet<int>();
        foreach (var row in rows)
        {
            if (row is null || tree.Count >= 500000) throw new BridgeException("NativeServiceReadUnverified", "The native role inventory is invalid or too large.");
            var type = row.GetType();
            if (type.GetProperty("Id")?.GetValue(row) is not string rawId || type.GetProperty("Name")?.GetValue(row) is not string name ||
                type.GetProperty("IsRole")?.GetValue(row) is not bool isRole || type.GetProperty("Order")?.GetValue(row) is not int order)
                throw new BridgeException("NativeServiceReadUnverified", "The native role tree shape changed.");
            var parent = type.GetProperty("ParentId")?.GetValue(row) as string;
            tree.Add(new { id = rawId, name, isRole, parentId = parent, order });
            if (isRole)
            {
                // V12 also omits nonnumeric role-tree IDs from the editable
                // numeric roles list while preserving the tree itself.
                if (!int.TryParse(rawId, out var id)) continue;
                if (!ids.Add(id)) throw new BridgeException("NativeServiceReadUnverified", "The native role tree contains duplicate role IDs.");
                roles.Add(new { roleId = id, name });
            }
        }
        return PlatformJson.Element(new { roles, tree });
    }

    // 11.0.3 lacks GetRbacRolesApi, but contains this native role-tree client.
    // Its own short cache remains in force and is disclosed in the response.
    private async Task<JsonElement> ReadRoleTreeFallbackAsync(Assembly designer, CancellationToken token)
    {
        var apiType = designer.GetType("Forguncy.RbacPermission.Api.GetRoleWithFolderApi", true)!;
        var api = Activator.CreateInstance(apiType)!;
        apiType.GetProperty("IsHideInternalRole")!.SetValue(api, false);
        var invokeAssembly = Assembly.Load("Forguncy.Infrastructure.InvokeService");
        var invokeableDefinition = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.IInvokeable`1", true)!;
        var responseType = apiType.GetInterfaces().Single(type => type.IsGenericType && type.GetGenericTypeDefinition() == invokeableDefinition).GenericTypeArguments[0];
        var invoke = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.InvokeableExtensions", true)!.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.Name == "InvokeAsync" && method.IsGenericMethodDefinition && method.GetParameters().Length == 2)
            .Single(method => method.GetParameters()[0].ParameterType.IsGenericType && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == invokeableDefinition)
            .MakeGenericMethod(responseType);
        token.ThrowIfCancellationRequested();
        var task = (Task)invoke.Invoke(null, [api, null])!;
        await task; session.VerifyContext();
        var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
        if (responseType.GetProperty("Result")!.GetValue(response) is not true || responseType.GetProperty("Data")!.GetValue(response) is not IEnumerable roots)
            throw new BridgeException("NativeServiceReadRejected", "The native role inventory was unavailable.");
        var roles = new List<object>(); var tree = new List<object>(); var ids = new HashSet<int>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance); var stack = new Stack<(object Item, int? Parent)>();
        foreach (var root in roots) if (root is not null) stack.Push((root, null));
        while (stack.TryPop(out var entry))
        {
            if (tree.Count >= 500000 || !visited.Add(entry.Item)) throw new BridgeException("NativeServiceReadUnverified", "The native role tree is invalid or too large.");
            var type = entry.Item.GetType();
            if (type.GetProperty("Id")?.GetValue(entry.Item) is not int id || type.GetProperty("Name")?.GetValue(entry.Item) is not string name ||
                type.GetProperty("IsRole")?.GetValue(entry.Item) is not bool isRole)
                throw new BridgeException("NativeServiceReadUnverified", "The native role tree shape changed.");
            tree.Add(new { id = id.ToString(System.Globalization.CultureInfo.InvariantCulture), name, isRole, parentId = entry.Parent?.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            if (isRole)
            {
                if (!ids.Add(id)) throw new BridgeException("NativeServiceReadUnverified", "The native role tree contains duplicate IDs.");
                roles.Add(new { roleId = id, name });
            }
            if (type.GetProperty("Children")?.GetValue(entry.Item) is IEnumerable children)
                foreach (var child in children) if (child is not null) stack.Push((child, id));
        }
        return PlatformJson.Element(new { roles, tree, limitation = "The V11 native role tree client may use its short-lived native cache." });
    }

    private static Route GetRoute(Assembly designer, string action) => Routes.GetOrAdd((designer, action), key => new(() => Route.Create(key.Item1, key.Item2))).Value;
    private sealed record Route(Type RequestType, MethodInfo Invoke)
    {
        internal static Route Create(Assembly designer, string action)
        {
            _ = V11BuildProfile.Resolve(designer);
            var role = action == "getRole";
            if (!role && action != "listOrganizations") throw new NotSupportedException("The native account read is not registered.");
            var dataType = role ? typeof(V11NativeRoleRead) : typeof(List<V11NativeOrganizationRead>);
            var baseType = designer.GetType("Forguncy.RbacPermission.Api.UserServiceNormalizedApiAbstract`1", true)!.MakeGenericType(dataType);
            var invokeAssembly = Assembly.Load("Forguncy.Infrastructure.InvokeService");
            var invokeMethodType = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.InvokeMethod", true)!;
            var attribute = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.Attribute.InvokeArgumentAttribute", true)!;
            var attributeCtor = attribute.GetConstructor([typeof(string), invokeMethodType, typeof(int)])!;
            var nativeCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)!;
            var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ForguncyDesignerMcp.V11.FixedAccountRead." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            var builder = dynamicAssembly.DefineDynamicModule("fixed").DefineType("FixedV11AccountRead", TypeAttributes.Public | TypeAttributes.Sealed, baseType);
            builder.SetCustomAttribute(new CustomAttributeBuilder(attributeCtor, [role ? "Role/GetRoleInfo" : "OrganizationStructure/GetOrgStructureList", Enum.ToObject(invokeMethodType, 0), 100000]));
            var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var il = constructor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, nativeCtor); il.Emit(OpCodes.Ret);
            if (role)
            {
                var field = builder.DefineField("_id", typeof(int), FieldAttributes.Private);
                var property = builder.DefineProperty("id", PropertyAttributes.None, typeof(int), null);
                var getter = builder.DefineMethod("get_id", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(int), Type.EmptyTypes);
                il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret); property.SetGetMethod(getter);
                var setter = builder.DefineMethod("set_id", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, [typeof(int)]);
                il = setter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret); property.SetSetMethod(setter);
            }
            var requestType = builder.CreateType()!;
            var invokeableDefinition = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.IInvokeable`1", true)!;
            var normalized = requestType.GetInterfaces().SingleOrDefault(type => type.IsGenericType && type.GetGenericTypeDefinition() == invokeableDefinition)
                ?? throw new NotSupportedException("The native normalized account request interface changed.");
            var responseType = normalized.GenericTypeArguments[0];
            if (!responseType.IsGenericType || responseType.GetGenericTypeDefinition().FullName != "Forguncy.Infrastructure.Abstraction.ResponseDto`1" ||
                responseType.GenericTypeArguments[0] != dataType) throw new NotSupportedException("The native normalized account response changed.");
            var invoke = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.InvokeableExtensions", true)!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(method => method.Name == "InvokeAsync" && method.IsGenericMethodDefinition && method.GetParameters().Length == 2)
                .Single(method => method.GetParameters()[0].ParameterType.IsGenericType && method.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == invokeableDefinition)
                .MakeGenericMethod(responseType);
            return new(requestType, invoke);
        }
    }
}

public sealed class V11NativeRoleRead
{
    public int? ID { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public int? FolderId { get; set; }
}
public sealed class V11NativeOrganizationRead
{
    public int ID { get; set; }
    public int ParentID { get; set; }
    public string? Name { get; set; }
    public int NodeLevelID { get; set; }
    public int? NodeOrder { get; set; }
    public string? Description { get; set; }
    public Dictionary<string, string>? Properties { get; set; }
}
