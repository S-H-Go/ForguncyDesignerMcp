using System.Collections.Concurrent;
using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// V11 lacks V12's AddUsersApi but has the same native RBAC host/context base,
// InvokeArgument attribute, InvokeableExtensions and Manager/User/AddUsers
// server action. This fixed, local request type uses that native lifecycle; no
// URL, CLR type, authentication header or endpoint comes from a caller.
internal sealed class NativeV11UserManagement
{
    private readonly NativeV11Session session;
    private static readonly ConcurrentDictionary<(Assembly Assembly, string Action), Lazy<NativeRoute>> Routes = new();
    internal const string Limitation = "Native service acceptance only; independent persistence and project export were not verified. Native account actions have no cross-client atomic revision precondition.";
    internal NativeV11UserManagement(NativeV11Session session) => this.session = session;
    internal static string AuthenticationMode(object? value)
    {
        if (value is null || value.GetType().FullName != "GrapeCity.Forguncy.Plugin.AuthenticationMode" || !value.GetType().IsEnum)
            throw new BridgeException("AccountAuthenticationUnverified", "The native authentication mode is unavailable.");
        var number = Convert.ToInt32(value);
        var name = value.ToString();
        return (number, name) switch
        {
            (1, "Windows") => "Windows", (3, "Forms") => "Forms", (4, "SecurityProvider") => "SecurityProvider",
            _ => throw new BridgeException("AccountAuthenticationUnverified", "The native authentication mode is unrecognized.")
        };
    }

    private string CurrentAuthenticationMode()
    {
        session.VerifyContext();
        var settings = NativeV11Reflection.Get(session.Document, "ForguncySettings");
        var application = settings is null ? null : NativeV11Reflection.Get(settings, "ApplicationSettings");
        return AuthenticationMode(application is null ? null : NativeV11Reflection.Get(application, "AuthenticationMode"));
    }

    internal static string ResolveNativeAction(string action, string mode)
    {
        if (mode is not ("Windows" or "Forms" or "SecurityProvider"))
            throw new BridgeException("AccountAuthenticationUnverified", "The native authentication mode is unrecognized.");
        if (mode != "Windows") return action;
        if (action is "setUsersPassword" or "setUsersValidity")
            throw new BridgeException("NativeWindowsUserActionsUnavailable", "This user action is unavailable in Windows authentication mode.");
        return WindowsEndpoints.ContainsKey(action) ? "windows." + action : action;
    }

    private static readonly IReadOnlyDictionary<string, string> WindowsEndpoints = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["createUser"] = "Manager/User/AddWindowsUsers", ["updateUser"] = "Manager/User/UpdateWindowsUserInfo",
        ["deleteUsers"] = "Manager/User/DeleteWindowsUsers", ["getUser"] = "Manager/User/GetWindowsUserInfo", ["listUsers"] = "Manager/User/GetWindowsUserInfos",
        ["addRolesToUsers"] = "Manager/Role/V2/AddWindowsUsersToRoles", ["addUsersToRoles"] = "Manager/Role/V2/AddWindowsUsersToRoles",
        ["removeRolesFromUsers"] = "Manager/Role/V2/RemoveWindowsUsersFromRoles", ["removeUsersFromRoles"] = "Manager/Role/V2/RemoveWindowsUsersFromRoles",
        ["setRolesForUsers"] = "Manager/Role/V2/SetRolesForWindowsUsers", ["setUsersForRoles"] = "Manager/Role/V2/SetWindowsUsersForRoles",
        ["addOrganizationsToUsers"] = "Manager/Organization/V2/AddWindowsUsersToOrganizations", ["addUsersToOrganizations"] = "Manager/Organization/V2/AddWindowsUsersToOrganizations",
        ["removeOrganizationsFromUsers"] = "Manager/Organization/V2/RemoveWindowsUsersFromOrganizations", ["removeUsersFromOrganizations"] = "Manager/Organization/V2/RemoveWindowsUsersFromOrganizations",
        ["setOrganizationsForUsers"] = "Manager/Organization/V2/SetOrganizationsForWindowsUsers", ["setUsersForOrganizations"] = "Manager/Organization/V2/SetWindowsUsersForOrganizations",
        ["addLeaders"] = "Manager/Organization/V2/AddOrganizationWindowsLeaders", ["removeLeaders"] = "Manager/Organization/V2/RemoveOrganizationWindowsLeaders",
        ["setLeaders"] = "Manager/Organization/V2/SetOrganizationWindowsLeaders", ["addMemberRoles"] = "Manager/Organization/V2/AddOrganizationWindowsMembersRoles",
        ["removeMemberRoles"] = "Manager/Organization/V2/RemoveOrganizationWindowsMembersRoles", ["setMemberRoles"] = "Manager/Organization/V2/SetOrganizationWindowsMembersRoles"
    };

    internal async Task<string?> GetProviderStorageModeAsync(CancellationToken token = default)
    {
        session.VerifyContext();
        var mode = CurrentAuthenticationMode();
        if (mode != "SecurityProvider") return null;
        var route = Routes.GetOrAdd((session.Context.DesignerAssembly, "providerInfo"), key => new(() => NativeRoute.Create(key.Assembly, key.Action))).Value;
        var native = Activator.CreateInstance(route.RequestType)!;
        token.ThrowIfCancellationRequested();
        var task = (Task)route.Invoke.Invoke(null, [native, null])!; await task; session.VerifyContext();
        if (task.GetType().GetProperty("Result")!.GetValue(task) is not V11NativeSecurityProviderInfo info || info.UserInformationStorageMode is not int storage || storage is < 0 or > 1)
            throw new BridgeException("AccountProviderModeUnverified", "The native security provider storage mode is unavailable.");
        return storage == 1 ? "InMemoryCache" : "InForguncyDatabase";
    }

    internal async Task RequireWriteSupportedAsync(CancellationToken token = default)
    {
        if (await GetProviderStorageModeAsync(token) == "InMemoryCache")
            throw new BridgeException("NativeServiceReadOnlyProvider", "The native security provider stores users in memory and does not support native account writes.");
    }
    internal void ValidateRequest(PlatformApiRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId))
            throw new BridgeException("NativeServiceApiNotAllowed", "The native account action is not registered.");
        session.VerifyContext();
        var mode = CurrentAuthenticationMode();
        _ = ResolveNativeAction(request.Method, mode);
        if (request.Namespace == "userManagement.user" && request.Method == "createUser") { if (mode == "Windows") _ = ParseWindowsUser(request.Parameters); else _ = ParseUser(request.Parameters); }
        else if (request.Namespace == "userManagement.user" && request.Method == "updateUser") _ = mode == "Windows" ? ParseWindowsUpdate(request.Parameters) : ParseUpdate(request.Parameters);
        else if (request.Namespace == "userManagement.user" && request.Method == "deleteUsers")
        {
            var names = ParseUserNames(request.Parameters);
            var current = session.Context.CaptureSnapshot().CurrentUser;
            if (current is not null && names.Contains(current.UserName, StringComparer.OrdinalIgnoreCase))
                throw new BridgeException("NativeServiceCurrentUserProtected", "The current collaboration user cannot be deleted by this action.");
        }
        else if ((request.Namespace, request.Method) is ("userManagement.role", "createRole") or ("userManagement.organization", "createOrganization"))
            _ = ParseCreateEntity(request.Namespace, request.Parameters);
        else if ((request.Namespace, request.Method) is ("userManagement.role", "updateRole") or ("userManagement.organization", "updateOrganization") or
            ("userManagement.role", "deleteRoles") or ("userManagement.organization", "deleteOrganizations"))
            _ = ParseEntityMutation(request.Namespace, request.Method, request.Parameters);
        else if (RelationActions.Contains(request.Method, StringComparer.Ordinal) && request.Namespace == RelationNamespace(request.Method))
            _ = ParseUserRelations(request.Method, request.Parameters);
        else if (OrgRoleActions.Contains((request.Namespace, request.Method))) _ = ParseOrgRoleRelations(request.Method, request.Parameters);
        else if (request.Namespace == "userManagement.user" && request.Method is "setUsersPassword" or "setUsersValidity")
            _ = ParseUserSecurity(request.Method, request.Parameters);
        else if (ExtraActions.TryGetValue((request.Namespace, request.Method), out var extra)) _ = ParseExtra(extra, request.Parameters);
        else throw new BridgeException("NativeServiceApiNotAllowed", "The native account action is not registered.");
    }
    internal static Type InspectNativeRequestType(Assembly designer, string action = "createUser") =>
        Routes.GetOrAdd((designer, action), key => new(() => NativeRoute.Create(key.Assembly, key.Action))).Value.RequestType;

    internal static PlatformApiDescriptor CreateUserDescriptor() => new("userManagement.user", "createUser",
        PlatformJson.Element(new
        {
            type = "object", additionalProperties = false,
            properties = new { userName = new { type = "string" }, password = new { type = "string" },
                email = new { type = "string" }, fullName = new { type = "string" }, extendedProperties = new { type = "object" } },
            required = new[] { "userName" }
        }), null, true, EffectScope.AccountService,
        "Creates one native user. Forms requires password and email; Windows accepts only userName and optional extendedProperties. Returns native acceptance only, not independently verified persistence or project export.");

    internal static PlatformApiDescriptor ReadUserDescriptor(bool list) => new("userManagement.user", list ? "listUsers" : "getUser",
        list ? PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { roleName = new { type = "string" }, keyword = new { type = "string" }, offset = new { type = "integer" }, limit = new { type = "integer" } } }) :
            PlatformJson.Element(new { type = "object", additionalProperties = false,
                properties = new { userName = new { type = "string" } }, required = new[] { "userName" } }),
        null, false, EffectScope.AccountService, "Reads the native V11 user service; this may start its Designer-owned service.");

    internal async Task<JsonElement> ReadUsersAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Namespace != "userManagement.user" || request.Method is not ("getUser" or "listUsers") || request.Parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("NativeServiceApiNotAllowed", "The native account read is not registered.");
        var list = request.Method == "listUsers"; var input = request.Parameters;
        var allowed = list ? new[] { "roleName", "keyword", "offset", "limit" } : new[] { "userName" };
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in input.EnumerateObject())
            if (!keys.Add(p.Name) || !allowed.Contains(p.Name, StringComparer.Ordinal)) throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
        string? ReadString(string name, bool required = false)
        {
            if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
                return required ? throw new BridgeException("InvalidAccountArguments", "A native user identity is required.") : null;
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > 1024 ||
                required && (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))) throw new BridgeException("InvalidAccountArguments", "An account read argument is invalid.");
            return text;
        }
        int ReadInt(string name, int fallback, int minimum, int maximum)
        {
            if (!input.TryGetProperty(name, out var value)) return fallback;
            return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n >= minimum && n <= maximum ? n :
                throw new BridgeException("InvalidAccountArguments", "Account read pagination is invalid.");
        }
        var userName = ReadString("userName", !list); var roleName = ReadString("roleName"); var keyword = ReadString("keyword");
        var offset = list ? ReadInt("offset", 0, 0, int.MaxValue) : 0;
        var limit = list ? ReadInt("limit", 100, 1, 500) : 1;
        session.VerifyContext();
        var readAction = ResolveNativeAction(request.Method, CurrentAuthenticationMode());
        var route = Routes.GetOrAdd((session.Context.DesignerAssembly, readAction), key => new(() => NativeRoute.Create(key.Assembly, key.Action))).Value;
        var native = Activator.CreateInstance(route.RequestType)!;
        route.RequestType.GetProperty(list ? "roleName" : "userName")!.SetValue(native, list ? roleName : userName);
        cancellationToken.ThrowIfCancellationRequested();
        var task = (Task)route.Invoke.Invoke(null, [native, null])!;
        await task; session.VerifyContext();
        var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
        if (route.ResponseType.GetProperty("Result")!.GetValue(response) is not true)
            throw new BridgeException("NativeServiceReadRejected", "The native account service did not accept the read.");
        object ConvertUser(object user)
        {
            object? Get(string name) => user.GetType().GetProperty(name)?.GetValue(user);
            if (Get("UserName") is not string name || string.IsNullOrWhiteSpace(name) || Get("IsEnabled") is not bool enabled)
                throw new BridgeException("NativeServiceReadUnverified", "The native account response shape changed.");
            var fullName = Get("FullName") as string; var email = Get("Email") as string;
            var extended = Get("ExtendedProperties") as IDictionary;
            var properties = new Dictionary<string, string>();
            if (extended is not null) foreach (DictionaryEntry pair in extended)
            {
                if (pair.Key is not string key || pair.Value is not string value || !properties.TryAdd(key, value))
                    throw new BridgeException("NativeServiceReadUnverified", "The native account property response changed.");
            }
            string[] Values(string name) => Get(name) is IEnumerable values ? values.Cast<object>().Select(value => value as string ??
                throw new BridgeException("NativeServiceReadUnverified", "The native account relation response changed.")).ToArray() : [];
            return new { userName = name, fullName, email, isEnabled = enabled, extendedProperties = properties,
                roles = Values("Roles"), organizationPaths = Values("OrganizationPaths") };
        }
        if (!list)
        {
            var nativeUser = route.ResponseType.GetProperty("UserInfo")!.GetValue(response)
                ?? throw new BridgeException("AccountProjectionNotFound", "The native user service did not return that identity.");
            if (!string.Equals(nativeUser.GetType().GetProperty("UserName")?.GetValue(nativeUser) as string, userName, StringComparison.Ordinal))
                throw new BridgeException("NativeServiceReadUnverified", "The native response identity differs from the request.");
            return PlatformJson.Element(ConvertUser(nativeUser));
        }
        var collection = route.ResponseType.GetProperty("Users")!.GetValue(response) as IEnumerable
            ?? throw new BridgeException("NativeServiceReadUnverified", "The native account inventory was unavailable.");
        var users = collection.Cast<object>().Take(500001).Select(ConvertUser).Select(value => PlatformJson.Element(value)).ToArray();
        if (users.Length > 500000 || users.Select(user => user.GetProperty("userName").GetString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != users.Length)
            throw new BridgeException("NativeServiceReadUnverified", "The native user inventory is too large or contains duplicate identities.");
        if (keyword is not null) users = users.Where(user => user.GetProperty("userName").GetString()!.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            user.TryGetProperty("fullName", out var fullName) && fullName.ValueKind == JsonValueKind.String && fullName.GetString()!.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToArray();
        return PlatformJson.Element(new { items = users.Skip(offset).Take(limit).ToArray(), total = users.Length });
    }

    internal static PlatformApiDescriptor DeleteUsersDescriptor() => new("userManagement.user", "deleteUsers",
        PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { userNames = new { type = "array", items = new { type = "string" } } }, required = new[] { "userNames" } }),
        null, true, EffectScope.AccountService,
        "Deletes specified form users through the native account service. Returns native acceptance only, without independently verified persistence or export.");

    internal static PlatformApiDescriptor UpdateUserDescriptor() => new("userManagement.user", "updateUser",
        PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { userName = new { type = "string" }, fullName = new { type = "string" }, email = new { type = "string" },
                isEnabled = new { type = "boolean" }, extendedProperties = new { type = "object" } },
            required = new[] { "userName" } }),
        null, true, EffectScope.AccountService,
        "Updates one native user. Forms requires all fields; Windows requires userName, email and extendedProperties and rejects fullName and isEnabled; native acceptance does not establish independently verified persistence or export.");

    internal static PlatformApiDescriptor CreateRoleDescriptor() => new("userManagement.role", "createRole",
        PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { roleName = new { type = "string" }, description = new { type = "string" }, parentFolderId = new { type = "integer" } },
            required = new[] { "roleName" } }), null, true, EffectScope.AccountService,
        "Creates one role; its ID is generated by the native service. Acceptance does not prove independent persistence or export.");

    internal static PlatformApiDescriptor CreateOrganizationDescriptor() => new("userManagement.organization", "createOrganization",
        PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { organizationName = new { type = "string" }, description = new { type = "string" }, levelId = new { type = "integer" }, parentOrganizationId = new { type = "integer" } },
            required = new[] { "organizationName" } }), null, true, EffectScope.AccountService,
        "Creates one organization; its ID is generated by the native service. Acceptance does not prove independent persistence or export.");

    internal static PlatformApiDescriptor EntityMutationDescriptor(bool role, bool delete)
    {
        var ns = role ? "userManagement.role" : "userManagement.organization";
        var method = role ? delete ? "deleteRoles" : "updateRole" : delete ? "deleteOrganizations" : "updateOrganization";
        var properties = new Dictionary<string, object>();
        string[] required;
        if (delete)
        {
            var key = role ? "roleIds" : "organizationIds";
            properties[key] = new { type = "array", items = new { type = "integer" } }; required = [key];
        }
        else
        {
            var id = role ? "roleId" : "organizationId"; var name = role ? "roleName" : "organizationName";
            properties[id] = new { type = "integer" }; properties[name] = new { type = "string" };
            properties["description"] = new { type = new[] { "string", "null" } };
            if (role) properties["parentFolderId"] = new { type = new[] { "integer", "null" } };
            else { properties["levelId"] = new { type = new[] { "integer", "null" } }; properties["parentOrganizationId"] = new { type = new[] { "integer", "null" } }; }
            required = properties.Keys.ToArray();
        }
        return new(ns, method, PlatformJson.Element(new { type = "object", additionalProperties = false, properties, required }),
            null, true, EffectScope.AccountService,
            delete ? "Deletes entities by native service IDs; acceptance does not prove independent persistence or export." :
                "Updates all named fields explicitly, including null values. Native acceptance does not prove independent persistence or export.");
    }

    private static readonly string[] RelationActions = ["addRolesToUsers", "removeRolesFromUsers", "setRolesForUsers",
        "addOrganizationsToUsers", "removeOrganizationsFromUsers", "setOrganizationsForUsers",
        "addUsersToRoles", "removeUsersFromRoles", "setUsersForRoles", "addUsersToOrganizations", "removeUsersFromOrganizations", "setUsersForOrganizations"];
    private static string RelationNamespace(string action) => action.StartsWith("addUsersTo", StringComparison.Ordinal) || action.StartsWith("removeUsersFrom", StringComparison.Ordinal) || action.StartsWith("setUsersFor", StringComparison.Ordinal)
        ? action.Contains("Roles", StringComparison.Ordinal) ? "userManagement.role" : "userManagement.organization" : "userManagement.user";
    internal static IReadOnlyList<PlatformApiDescriptor> UserRelationDescriptors() => RelationActions.Select(method =>
    {
        var key = method.Contains("Roles", StringComparison.Ordinal) ? "roleIds" : "organizationIds";
        var properties = new Dictionary<string, object> { ["userNames"] = new { type = "array", items = new { type = "string" } },
            [key] = new { type = "array", items = new { type = "integer" } } };
        return new PlatformApiDescriptor(RelationNamespace(method), method, PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties, required = new[] { "userNames", key } }), null, true, EffectScope.AccountService,
            "Changes user relations using native IDs. Set operations may clear the assigned side using an empty list. Native acceptance does not independently verify persistence or export.");
    }).ToArray();

    private static readonly (string Namespace, string Method)[] OrgRoleActions =
    [
        ("userManagement.role", "addOrganizationsToRoles"), ("userManagement.role", "removeOrganizationsFromRoles"),
        ("userManagement.role", "setOrganizationsForRoles"), ("userManagement.role", "setRolesForOrganizations"),
        ("userManagement.organization", "addRolesToOrganizations"), ("userManagement.organization", "removeRolesFromOrganizations"),
        ("userManagement.organization", "setRolesForOrganizations")
    ];
    internal static IReadOnlyList<PlatformApiDescriptor> OrgRoleRelationDescriptors() => OrgRoleActions.Select(action =>
        new PlatformApiDescriptor(action.Namespace, action.Method, PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { roleIds = new { type = "array", items = new { type = "integer" } }, organizationIds = new { type = "array", items = new { type = "integer" } },
                isEffectedForSubOrganizations = new { type = "boolean" } }, required = new[] { "roleIds", "organizationIds" } }), null, true,
            EffectScope.AccountService, "Changes native role and organization associations by service ID; native acceptance does not independently verify persistence or export.")).ToArray();

    internal static IReadOnlyList<PlatformApiDescriptor> UserSecurityDescriptors() =>
    [
        new("userManagement.user", "setUsersPassword", PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { userNames = new { type = "array", items = new { type = "string" } }, password = new { type = "string" } }, required = new[] { "userNames", "password" } }),
            null, true, EffectScope.AccountService, "Changes form-user passwords through the native service; native acceptance does not verify independent persistence or export."),
        new("userManagement.user", "setUsersValidity", PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties = new { userNames = new { type = "array", items = new { type = "string" } }, isValid = new { type = "boolean" } }, required = new[] { "userNames", "isValid" } }),
            null, true, EffectScope.AccountService, "Changes form-user enabled status through the native service; native acceptance does not verify independent persistence or export.")
    ];

    private sealed record ExtraField(string Name, Type Type, bool Required = true);
    private sealed record ExtraAction(string Endpoint, bool GeneratedId, ExtraField[] Fields);
    private static ExtraField Id(string name) => new(name, typeof(int));
    private static ExtraField Ids(string name) => new(name, typeof(List<int>));
    private static ExtraField Users() => new("userNames", typeof(List<string>));
    private static ExtraField Text(string name, bool required = true) => new(name, typeof(string), required);
    private static readonly Dictionary<(string Namespace, string Method), ExtraAction> ExtraActions = new()
    {
        [("userManagement.role", "createRoleFolder")] = new("Manager/Role/V2/AddRoleFolder", true, [Text("folderName"), new("parentFolderId", typeof(int?), false)]),
        [("userManagement.role", "updateRoleFolder")] = new("Manager/Role/V2/UpdateRoleFolder", false, [Id("folderId"), Text("folderName"), new("parentFolderId", typeof(int?))]),
        [("userManagement.role", "deleteRoleFolders")] = new("Manager/Role/V2/DeleteRoleFolders", false, [Ids("folderIds")]),
        [("userManagement.role", "moveRoleOrFolder")] = new("Manager/Role/V2/MoveRoleOrFolder", false, [Id("dragNodeId"), new("isDraggedRole", typeof(bool)), Id("dropNodeId"), new("isDroppedRole", typeof(bool)), new("moveType", typeof(int))]),
        [("userManagement.role", "addOwnedRoles")] = new("Manager/Role/V2/AddRoleOwnedPermissions", false, [Id("roleId"), Ids("ownedRoleIds"), new("type", typeof(int))]),
        [("userManagement.role", "removeOwnedRoles")] = new("Manager/Role/V2/RemoveRoleOwnedPermissions", false, [Id("roleId"), Ids("ownedRoleIds"), new("type", typeof(int))]),
        [("userManagement.role", "setOwnedRoles")] = new("Manager/Role/V2/SetRoleOwnedPermissions", false, [Id("roleId"), Ids("ownedRoleIds"), new("type", typeof(int))]),
        [("userManagement.organization", "moveOrganization")] = new("Manager/Organization/V2/MoveOrganization", false, [Id("organizationId"), Id("targetOrganizationId"), new("moveType", typeof(int))]),
        [("userManagement.organization", "addLeaders")] = new("Manager/Organization/V2/AddOrganizationLeaders", false, [Id("organizationId"), Users()]),
        [("userManagement.organization", "removeLeaders")] = new("Manager/Organization/V2/RemoveOrganizationLeaders", false, [Id("organizationId"), Users()]),
        [("userManagement.organization", "setLeaders")] = new("Manager/Organization/V2/SetOrganizationLeaders", false, [Id("organizationId"), Users()]),
        [("userManagement.organization", "addMemberRoles")] = new("Manager/Organization/V2/AddOrganizationMembersRoles", false, [Id("organizationId"), Users(), Ids("roleIds")]),
        [("userManagement.organization", "removeMemberRoles")] = new("Manager/Organization/V2/RemoveOrganizationMembersRoles", false, [Id("organizationId"), Users(), Ids("roleIds")]),
        [("userManagement.organization", "setMemberRoles")] = new("Manager/Organization/V2/SetOrganizationMembersRoles", false, [Id("organizationId"), Users(), Ids("roleIds")])
    };
    internal static bool HandlesExtra(PlatformApiRequest request) => ExtraActions.ContainsKey((request.Namespace, request.Method));
    internal static IReadOnlyList<PlatformApiDescriptor> ExtraDescriptors() => ExtraActions.Select(pair =>
    {
        var properties = pair.Value.Fields.ToDictionary(field => field.Name, field => field.Type == typeof(List<int>) ? (object)new { type = "array", items = new { type = "integer" } } :
            field.Type == typeof(List<string>) ? new { type = "array", items = new { type = "string" } } :
            field.Type == typeof(bool) ? new { type = "boolean" } : field.Type == typeof(string) ? new { type = "string" } :
            field.Type == typeof(int?) ? new { type = new[] { "integer", "null" } } : new { type = "integer" }, StringComparer.Ordinal);
        return new PlatformApiDescriptor(pair.Key.Namespace, pair.Key.Method, PlatformJson.Element(new { type = "object", additionalProperties = false,
            properties, required = pair.Value.Fields.Where(field => field.Required).Select(field => field.Name).ToArray() }), null, true, EffectScope.AccountService,
            "Runs the fixed native account action; native acceptance does not independently verify persistence or project export.");
    }).ToArray();

    internal async Task<NativeServiceApiReceipt> InvokeExtraAsync(PlatformApiRequest request, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || !ExtraActions.TryGetValue((request.Namespace, request.Method), out var action))
            throw new BridgeException("NativeServiceApiNotAllowed", "The native account action is not registered.");
        return await InvokeAsync(request, request.Method, ParseExtra(action, request.Parameters), token);
    }

    private static Dictionary<string, object> ParseExtra(ExtraAction action, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name != "guard" && !action.Fields.Any(field => field.Name == property.Name))
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
        var result = new Dictionary<string, object>();
        foreach (var field in action.Fields)
        {
            if (!parameters.TryGetProperty(field.Name, out var value))
            {
                if (field.Required) throw new BridgeException("InvalidAccountArguments", "A required native account field is missing.");
                continue;
            }
            if (value.ValueKind == JsonValueKind.Null && field.Type == typeof(int?)) continue;
            object parsed;
            if (field.Type == typeof(string))
            {
                if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > 190 || text.Any(char.IsControl))
                    throw new BridgeException("InvalidAccountArguments", "A native account name is invalid.");
                parsed = text;
            }
            else if (field.Type == typeof(bool))
                parsed = value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new BridgeException("InvalidAccountArguments", "A native boolean is invalid.");
            else if (field.Type == typeof(int) || field.Type == typeof(int?))
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) ||
                    (field.Name == "moveType" ? number is < -1 or > 1 : field.Name == "type" ? number is < 0 or > 1 : number <= 0))
                    throw new BridgeException("InvalidAccountArguments", "A native ID or enum value is invalid.");
                parsed = number;
            }
            else if (field.Type == typeof(List<string>))
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 500 || value.GetArrayLength() == 0 &&
                    (!action.Endpoint.Contains("/Set", StringComparison.Ordinal) || action.Endpoint.EndsWith("SetOrganizationMembersRoles", StringComparison.Ordinal)))
                    throw new BridgeException("InvalidAccountArguments", "The native user list is invalid.");
                var names = new List<string>(); var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var element in value.EnumerateArray())
                    if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } name || string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 190 || name.Any(char.IsControl) || !distinct.Add(name))
                        throw new BridgeException("InvalidAccountArguments", "The native user list contains an invalid or duplicate identity.");
                    else names.Add(name);
                parsed = names;
            }
            else
            {
                if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 500 || value.GetArrayLength() == 0 && !action.Endpoint.Contains("/Set", StringComparison.Ordinal))
                    throw new BridgeException("InvalidAccountArguments", "The native ID list is invalid.");
                var ids = new List<int>(); var distinct = new HashSet<int>();
                foreach (var element in value.EnumerateArray())
                    if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var id) || id <= 0 || !distinct.Add(id))
                        throw new BridgeException("InvalidAccountArguments", "The native ID list contains an invalid or duplicate identity.");
                    else ids.Add(id);
                parsed = ids;
            }
            result[char.ToUpperInvariant(field.Name[0]) + field.Name[1..]] = parsed;
        }
        return result;
    }

    internal async Task<NativeServiceApiReceipt> CreateUserAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Namespace != "userManagement.user" || request.Method != "createUser" || string.IsNullOrWhiteSpace(request.OperationId))
            throw new BridgeException("NativeServiceApiNotAllowed", "Only the fixed native createUser action is supported.");
        var mode = CurrentAuthenticationMode();
        object users = mode == "Windows" ? new List<V11NativeWindowsUserInfo> { ParseWindowsUser(request.Parameters) } : new List<V11NativeUserInfo> { ParseUser(request.Parameters) };
        return await InvokeAsync(request, "createUser", new Dictionary<string, object> { ["UserInfos"] = users }, cancellationToken);
    }

    internal async Task<NativeServiceApiReceipt> UpdateUserAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Namespace != "userManagement.user" || request.Method != "updateUser" || string.IsNullOrWhiteSpace(request.OperationId))
            throw new BridgeException("NativeServiceApiNotAllowed", "Only the fixed native updateUser action is supported.");
        return await InvokeAsync(request, "updateUser", CurrentAuthenticationMode() == "Windows" ? ParseWindowsUpdate(request.Parameters) : ParseUpdate(request.Parameters), cancellationToken);
    }

    internal async Task<NativeServiceApiReceipt> DeleteUsersAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Namespace != "userManagement.user" || request.Method != "deleteUsers" || string.IsNullOrWhiteSpace(request.OperationId))
            throw new BridgeException("NativeServiceApiNotAllowed", "Only the fixed native deleteUsers action is supported.");
        var names = ParseUserNames(request.Parameters);
        var current = session.Context.CaptureSnapshot().CurrentUser;
        if (current is not null && names.Contains(current.UserName, StringComparer.OrdinalIgnoreCase))
            throw new BridgeException("NativeServiceCurrentUserProtected", "The current collaboration user cannot be deleted by this action.");
        return await InvokeAsync(request, "deleteUsers", new Dictionary<string, object> { ["Users"] = names }, cancellationToken);
    }

    internal async Task<NativeServiceApiReceipt> CreateEntityAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || (request.Namespace, request.Method) is not
            (("userManagement.role", "createRole") or ("userManagement.organization", "createOrganization")))
            throw new BridgeException("NativeServiceApiNotAllowed", "The native account creation action is not registered.");
        return await InvokeAsync(request, request.Method, ParseCreateEntity(request.Namespace, request.Parameters), cancellationToken);
    }

    internal async Task<NativeServiceApiReceipt> MutateEntityAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || (request.Namespace, request.Method) is not
            (("userManagement.role", "updateRole") or ("userManagement.organization", "updateOrganization") or
             ("userManagement.role", "deleteRoles") or ("userManagement.organization", "deleteOrganizations")))
            throw new BridgeException("NativeServiceApiNotAllowed", "The native entity action is not registered.");
        return await InvokeAsync(request, request.Method, ParseEntityMutation(request.Namespace, request.Method, request.Parameters), cancellationToken);
    }

    internal async Task<NativeServiceApiReceipt> MutateUserRelationsAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || !RelationActions.Contains(request.Method, StringComparer.Ordinal) || request.Namespace != RelationNamespace(request.Method))
            throw new BridgeException("NativeServiceApiNotAllowed", "The native account relation action is not registered.");
        return await InvokeAsync(request, request.Method, ParseUserRelations(request.Method, request.Parameters), cancellationToken);
    }

    internal async Task<NativeServiceApiReceipt> MutateOrgRoleRelationsAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.OperationId) || !OrgRoleActions.Contains((request.Namespace, request.Method)))
            throw new BridgeException("NativeServiceApiNotAllowed", "The native role and organization relation action is not registered.");
        return await InvokeAsync(request, request.Method, ParseOrgRoleRelations(request.Method, request.Parameters), cancellationToken);
    }

    internal async Task<NativeServiceApiReceipt> MutateUserSecurityAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Namespace != "userManagement.user" || string.IsNullOrWhiteSpace(request.OperationId) || request.Method is not ("setUsersPassword" or "setUsersValidity"))
            throw new BridgeException("NativeServiceApiNotAllowed", "The native account security action is not registered.");
        return await InvokeAsync(request, request.Method, ParseUserSecurity(request.Method, request.Parameters), cancellationToken);
    }

    private async Task<NativeServiceApiReceipt> InvokeAsync(PlatformApiRequest request, string action, IReadOnlyDictionary<string, object> values, CancellationToken cancellationToken)
    {
        session.VerifyContext();
        var mode = CurrentAuthenticationMode();
        // Revalidate mode-dependent arguments immediately before choosing the native route.
        if (action == "createUser")
        {
            if (mode == "Windows") _ = ParseWindowsUser(request.Parameters); else _ = ParseUser(request.Parameters);
            var expected = mode == "Windows" ? typeof(List<V11NativeWindowsUserInfo>) : typeof(List<V11NativeUserInfo>);
            if (values["UserInfos"].GetType() != expected) throw new BridgeException("AccountAuthenticationUnverified", "Authentication mode changed during account preparation.");
        }
        if (action == "updateUser") _ = mode == "Windows" ? ParseWindowsUpdate(request.Parameters) : ParseUpdate(request.Parameters);
        var routeAction = ResolveNativeAction(action, mode);
        var route = Routes.GetOrAdd((session.Context.DesignerAssembly, routeAction), key => new(() => NativeRoute.Create(key.Assembly, key.Action))).Value;
        var nativeRequest = Activator.CreateInstance(route.RequestType)!;
        foreach (var pair in values) route.RequestType.GetProperty(pair.Key)!.SetValue(nativeRequest, pair.Value);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Native API has no cancellation contract: await the one dispatch.
            var task = (Task)route.Invoke.Invoke(null, [nativeRequest, null])!;
            await task;
            var response = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var accepted = (bool)route.ResponseType.GetProperty("Result")!.GetValue(response)!;
            int? id = null;
            if (accepted && action is "createRole" or "createOrganization" or "createRoleFolder")
            {
                var properties = route.ResponseType.GetProperty("Properties")!.GetValue(response)
                    ?? throw new BridgeException("NativeServiceResponseUnverified", "The service did not return its generated identity.");
                id = properties.GetType().GetProperty("Data")!.GetValue(properties) is int key && key > 0 ? key :
                    throw new BridgeException("NativeServiceResponseUnverified", "The service returned an invalid generated identity.");
            }
            session.VerifyContext();
            var nativeService = NativeV11Reflection.Get(session.NativeApplication, "UserService")
                ?? throw new BridgeException("AccountServiceUnavailable", "The native account service is unavailable.");
            var serviceContract = session.Context.DesignerAssembly.GetType("Forguncy.UserService.Abstraction.IUserService", true)!;
            serviceContract.GetMethod("ClearCache", Type.EmptyTypes)!.Invoke(nativeService, null);
            return new(request.OperationId!, accepted ? "native_accepted" : "failed", accepted ? "NativeServiceAccepted" : "NativeServiceRejected",
                accepted ? "The native user service accepted the action." : "The native user service rejected the action.",
                accepted, false, id is null ? PlatformJson.Element(new { success = accepted }) : PlatformJson.Element(new { success = accepted, id }), false, false, false, Limitation);
        }
        catch (Exception)
        {
            // Native errors can contain usernames/passwords. A call may have
            // committed even when its response, context check or cache clear fails.
            return new(request.OperationId!, "verification_failed", "NativeServiceOutcomeUnconfirmed",
                "The native user service outcome could not be confirmed; do not replay the action.", null, false, null, false, false, true, Limitation);
        }
    }

    internal static Dictionary<string, object> ParseCreateEntity(string ns, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        var role = ns == "userManagement.role";
        if (!role && ns != "userManagement.organization") throw new BridgeException("NativeServiceApiNotAllowed", "The account type is not registered.");
        var allowed = role ? new[] { "guard", "roleName", "description", "parentFolderId" } :
            new[] { "guard", "organizationName", "description", "levelId", "parentOrganizationId" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parameters.EnumerateObject())
            if (!seen.Add(p.Name) || !allowed.Contains(p.Name, StringComparer.Ordinal))
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
        var name = role ? "roleName" : "organizationName";
        if (!parameters.TryGetProperty(name, out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            nameValue.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Length > 190 || text.Any(char.IsControl))
            throw new BridgeException("InvalidAccountArguments", "A valid native entity name is required.");
        var result = new Dictionary<string, object> { [role ? "RoleName" : "OrganizationName"] = text };
        if (parameters.TryGetProperty("description", out var description) && description.ValueKind != JsonValueKind.Null)
        {
            if (description.ValueKind != JsonValueKind.String || description.GetString()!.Length > 2048)
                throw new BridgeException("InvalidAccountArguments", "The entity description is invalid.");
            result["Description"] = description.GetString()!;
        }
        foreach (var key in role ? new[] { "parentFolderId" } : new[] { "levelId", "parentOrganizationId" })
        {
            if (!parameters.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) continue;
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number <= 0)
                throw new BridgeException("InvalidAccountArguments", "Native parent and level IDs must be positive integers.");
            result[char.ToUpperInvariant(key[0]) + key[1..]] = number;
        }
        return result;
    }

    internal static Dictionary<string, object> ParseEntityMutation(string ns, string method, JsonElement parameters)
    {
        var role = ns == "userManagement.role";
        if (!role && ns != "userManagement.organization") throw new BridgeException("NativeServiceApiNotAllowed", "The account type is not registered.");
        var delete = method == (role ? "deleteRoles" : "deleteOrganizations");
        if (!delete && method != (role ? "updateRole" : "updateOrganization")) throw new BridgeException("NativeServiceApiNotAllowed", "The account action is not registered.");
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        if (delete)
        {
            var key = role ? "roleIds" : "organizationIds";
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in parameters.EnumerateObject())
                if (!keys.Add(property.Name) || property.Name != key && property.Name != "guard") throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
            if (!parameters.TryGetProperty(key, out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() is < 1 or > 500)
                throw new BridgeException("InvalidAccountArguments", "Provide between 1 and 500 native IDs.");
            var distinct = new HashSet<int>(); var values = new List<int>();
            foreach (var value in ids.EnumerateArray())
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id <= 0 || !distinct.Add(id))
                    throw new BridgeException("InvalidAccountArguments", "Native IDs must be unique positive integers.");
                else values.Add(id);
            return new() { [role ? "RoleIds" : "OrganizationIds"] = values };
        }
        var required = role ? new[] { "roleId", "roleName", "description", "parentFolderId" } :
            new[] { "organizationId", "organizationName", "description", "levelId", "parentOrganizationId" };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name != "guard" && !required.Contains(property.Name, StringComparer.Ordinal))
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
        if (required.Any(key => !parameters.TryGetProperty(key, out _)))
            throw new BridgeException("InvalidAccountArguments", "Every update field must be supplied explicitly, including nulls.");
        var idName = role ? "roleId" : "organizationId";
        var idValue = parameters.GetProperty(idName);
        if (idValue.ValueKind != JsonValueKind.Number || !idValue.TryGetInt32(out var idNumber) || idNumber <= 0)
            throw new BridgeException("InvalidAccountArguments", "A positive native entity ID is required.");
        var creationFields = new Dictionary<string, object?>();
        foreach (var key in required.Where(key => key != idName)) creationFields[key] = JsonSerializer.Deserialize<object>(parameters.GetProperty(key));
        var create = ParseCreateEntity(ns, JsonSerializer.SerializeToElement(creationFields));
        create[role ? "RoleId" : "OrganizationId"] = idNumber;
        return create;
    }

    internal static Dictionary<string, object> ParseUpdate(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!keys.Add(property.Name) || property.Name is not ("guard" or "userName" or "fullName" or "email" or "isEnabled" or "extendedProperties"))
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
        string Text(string name, bool allowEmpty = false) => parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            value.GetString() is { } text && text.Length <= 1024 && (allowEmpty || !string.IsNullOrWhiteSpace(text)) ? text :
            throw new BridgeException("InvalidAccountArguments", "Every update field must be supplied explicitly.");
        var username = Text("userName");
        if (username.Length > 190 || username.Any(char.IsControl)) throw new BridgeException("InvalidAccountArguments", "The account name is invalid.");
        if (!parameters.TryGetProperty("isEnabled", out var enabled) || enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new BridgeException("InvalidAccountArguments", "The user enabled state is required.");
        if (!parameters.TryGetProperty("extendedProperties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidAccountArguments", "All extended properties must be supplied explicitly.");
        var extended = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in properties.EnumerateObject())
            if (property.Name.Length is 0 or > 190 || property.Name.StartsWith('$') || property.Value.ValueKind != JsonValueKind.String ||
                !extended.TryAdd(property.Name, property.Value.GetString()!))
                throw new BridgeException("InvalidAccountArguments", "An extended account property is invalid or duplicated.");
        return new() { ["UserName"] = username, ["FullName"] = Text("fullName", true), ["Email"] = Text("email"),
            ["IsEnabled"] = enabled.GetBoolean(), ["ExtendedProperties"] = extended };
    }

    internal static List<string> ParseUserNames(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!keys.Add(property.Name) || property.Name is not ("guard" or "userNames"))
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
        if (!parameters.TryGetProperty("userNames", out var values) || values.ValueKind != JsonValueKind.Array || values.GetArrayLength() is < 1 or > 500)
            throw new BridgeException("InvalidAccountArguments", "Provide between 1 and 500 user identities.");
        var names = new List<string>(); var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in values.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } name ||
                string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Length > 190 || name.Any(char.IsControl) || !distinct.Add(name))
                throw new BridgeException("InvalidAccountArguments", "User identities must be unique, nonempty native names without surrounding spaces.");
            names.Add(name);
        }
        return names;
    }

    internal static Dictionary<string, object> ParseUserRelations(string action, JsonElement parameters)
    {
        if (!RelationActions.Contains(action, StringComparer.Ordinal) || parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidAccountArguments", "The native relation arguments are invalid.");
        var key = action.Contains("Roles", StringComparison.Ordinal) ? "roleIds" : "organizationIds";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name != "guard" && property.Name != "userNames" && property.Name != key)
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate relation arguments are not supported.");
        var setUsers = action.StartsWith("setUsersFor", StringComparison.Ordinal);
        var setIds = action is "setRolesForUsers" or "setOrganizationsForUsers";
        if (!parameters.TryGetProperty("userNames", out var namesElement) || namesElement.ValueKind != JsonValueKind.Array ||
            namesElement.GetArrayLength() > 500 || namesElement.GetArrayLength() == 0 && !setUsers ||
            !parameters.TryGetProperty(key, out var ids) || ids.ValueKind != JsonValueKind.Array ||
            ids.GetArrayLength() > 500 || ids.GetArrayLength() == 0 && !setIds)
            throw new BridgeException("InvalidAccountArguments", "Provide user names and bounded native relation IDs.");
        var names = new List<string>(); var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in namesElement.EnumerateArray())
            if (element.ValueKind != JsonValueKind.String || element.GetString() is not { } name || string.IsNullOrWhiteSpace(name) ||
                name != name.Trim() || name.Length > 190 || name.Any(char.IsControl) || !distinctNames.Add(name))
                throw new BridgeException("InvalidAccountArguments", "User identities must be unique nonempty native names.");
            else names.Add(name);
        var numbers = new List<int>(); var distinctIds = new HashSet<int>();
        foreach (var element in ids.EnumerateArray())
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var id) || id <= 0 || !distinctIds.Add(id))
                throw new BridgeException("InvalidAccountArguments", "Relation IDs must be unique positive integers.");
            else numbers.Add(id);
        return new() { ["UserNames"] = names, [key == "roleIds" ? "RoleIds" : "OrganizationIds"] = numbers };
    }

    internal static Dictionary<string, object> ParseOrgRoleRelations(string action, JsonElement parameters)
    {
        if (!OrgRoleActions.Any(item => item.Method == action) || parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidAccountArguments", "The native relation arguments are invalid.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name is not ("guard" or "roleIds" or "organizationIds" or "isEffectedForSubOrganizations"))
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate relation arguments are not supported.");
        List<int> Ids(string key, bool allowEmpty)
        {
            if (!parameters.TryGetProperty(key, out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 500 || ids.GetArrayLength() == 0 && !allowEmpty)
                throw new BridgeException("InvalidAccountArguments", "Provide bounded native relation IDs.");
            var values = new List<int>(); var distinct = new HashSet<int>();
            foreach (var value in ids.EnumerateArray())
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var id) || id <= 0 || !distinct.Add(id))
                    throw new BridgeException("InvalidAccountArguments", "Relation IDs must be unique positive integers.");
                else values.Add(id);
            return values;
        }
        var result = new Dictionary<string, object>
        {
            ["RoleIds"] = Ids("roleIds", action == "setRolesForOrganizations"),
            ["OrganizationIds"] = Ids("organizationIds", action == "setOrganizationsForRoles")
        };
        if (parameters.TryGetProperty("isEffectedForSubOrganizations", out var value) && value.ValueKind != JsonValueKind.Null)
        {
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new BridgeException("InvalidAccountArguments", "The suborganization option must be boolean.");
            result["IsEffectedForSubOrganizations"] = value.GetBoolean();
        }
        return result;
    }

    internal static Dictionary<string, object> ParseUserSecurity(string action, JsonElement parameters)
    {
        if (action is not ("setUsersPassword" or "setUsersValidity") || parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidAccountArguments", "The account security arguments are invalid.");
        var key = action == "setUsersPassword" ? "password" : "isValid";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name != "guard" && property.Name != "userNames" && property.Name != key)
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account security arguments are not supported.");
        if (!parameters.TryGetProperty("userNames", out var names) || !parameters.TryGetProperty(key, out var option))
            throw new BridgeException("InvalidAccountArguments", "Account names and the security setting are required.");
        var validated = ParseUserNames(PlatformJson.Element(new { userNames = names }));
        var result = new Dictionary<string, object> { ["UserNames"] = validated };
        if (action == "setUsersPassword")
        {
            if (option.ValueKind != JsonValueKind.String || option.GetString() is not { } password || string.IsNullOrWhiteSpace(password) || password.Length > 1024)
                throw new BridgeException("InvalidAccountArguments", "A valid password is required.");
            // V11 ResetUsersPasswordParam calls this property NewPassword.
            result["NewPassword"] = password;
        }
        else
        {
            if (option.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new BridgeException("InvalidAccountArguments", "The enabled state must be boolean.");
            result["IsValid"] = option.GetBoolean();
        }
        return result;
    }

    internal static V11NativeUserInfo ParseUser(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name is not ("guard" or "userName" or "password" or "email" or "fullName" or "extendedProperties"))
                throw new BridgeException("InvalidAccountArguments", "Unknown or duplicate account arguments are not supported.");
        string Required(string name) => parameters.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString()) && value.GetString()!.Length <= 1024 ? value.GetString()! :
            throw new BridgeException("InvalidAccountArguments", "A required account string is missing or invalid.");
        string? Optional(string name) => !parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null :
            value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 1024 ? value.GetString() :
            throw new BridgeException("InvalidAccountArguments", "An optional account string is invalid.");
        var extended = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters.TryGetProperty("extendedProperties", out var properties) && properties.ValueKind != JsonValueKind.Null)
        {
            if (properties.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Extended properties must be an object.");
            foreach (var property in properties.EnumerateObject())
                if (property.Name.Length is 0 or > 190 || property.Name.StartsWith('$') || property.Value.ValueKind != JsonValueKind.String ||
                    !extended.TryAdd(property.Name, property.Value.GetString()!))
                    throw new BridgeException("InvalidAccountArguments", "An extended account property is invalid or duplicated.");
        }
        var user = new V11NativeUserInfo { UserName = Required("userName"), Password = Required("password"), Email = Required("email"),
            FullName = Optional("fullName"), ExtendedProperties = extended };
        if (user.UserName.Length > 190 || user.UserName.Any(char.IsControl)) throw new BridgeException("InvalidAccountArguments", "The account name is invalid.");
        return user;
    }

    internal static V11NativeWindowsUserInfo ParseWindowsUser(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        foreach (var p in parameters.EnumerateObject())
            if (p.Name is not ("guard" or "userName" or "extendedProperties"))
                throw new BridgeException("InvalidAccountArguments", "This field is unavailable for Windows users.");
        var fields = new Dictionary<string, object?>();
        foreach (var p in parameters.EnumerateObject())
            if (!fields.TryAdd(p.Name, p.Value.Clone())) throw new BridgeException("InvalidAccountArguments", "Duplicate account arguments are not supported.");
        fields["password"] = "unused"; fields["email"] = "unused";
        var parsed = ParseUser(PlatformJson.Element(fields));
        return new V11NativeWindowsUserInfo { UserName = parsed.UserName, ExtendedProperties = parsed.ExtendedProperties };
    }

    internal static Dictionary<string, object> ParseWindowsUpdate(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountArguments", "Account arguments must be an object.");
        var fields = new Dictionary<string, object?>();
        foreach (var p in parameters.EnumerateObject())
        {
            if (p.Name is not ("guard" or "userName" or "email" or "extendedProperties") || !fields.TryAdd(p.Name, p.Value.Clone()))
                throw new BridgeException("InvalidAccountArguments", "Unknown, duplicate or unsupported Windows user field.");
        }
        fields["fullName"] = ""; fields["isEnabled"] = true;
        var parsed = ParseUpdate(PlatformJson.Element(fields));
        parsed.Remove("FullName"); parsed.Remove("IsEnabled");
        return parsed;
    }

    private sealed record NativeRoute(Type RequestType, Type ResponseType, MethodInfo Invoke)
    {
        internal static NativeRoute Create(Assembly designer, string action)
        {
            _ = V11BuildProfile.Resolve(designer);
            var windows = action.StartsWith("windows.", StringComparison.Ordinal);
            if (windows) action = action[8..];
            var extra = ExtraActions.FirstOrDefault(item => item.Key.Method == action).Value;
            var baseDefinition = designer.GetType("Forguncy.RbacPermission.Api.UserServiceApiAbstract`1", true)!;
            var common = Assembly.Load("CommonUtilities");
            var response = action switch
            {
                "getUser" => common.GetType("CommonUtilities.UserInfoResultData", true)!,
                "listUsers" => common.GetType("CommonUtilities.UserInfosResultData", true)!,
                "providerInfo" => typeof(V11NativeSecurityProviderInfo),
                _ => common.GetType("CommonUtilities.ManageApiResponseDto`1", true)!.MakeGenericType(
                    action is "createRole" or "createOrganization" || extra?.GeneratedId == true ? typeof(int) : typeof(object))
            };
            var baseType = baseDefinition.MakeGenericType(response);
            var invokeAssembly = Assembly.Load("Forguncy.Infrastructure.InvokeService");
            var enumType = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.InvokeMethod", true)!;
            var attribute = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.Attribute.InvokeArgumentAttribute", true)!;
            var ctor = attribute.GetConstructor([typeof(string), enumType, typeof(int)]) ?? throw new NotSupportedException("Native invoke attribute changed.");
            var nativeCtor = baseType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new NotSupportedException("Native account API constructor changed.");
            var (endpoint, fields) = action switch
            {
                "createUser" => ("Manager/User/AddUsers", new[] { ("UserInfos", typeof(List<V11NativeUserInfo>)) }),
                "deleteUsers" => ("Manager/User/DeleteUsers", new[] { ("Users", typeof(List<string>)) }),
                "updateUser" => ("Manager/User/UpdateUserInfo", new[] { ("UserName", typeof(string)), ("FullName", typeof(string)),
                    ("Email", typeof(string)), ("IsEnabled", typeof(bool)), ("ExtendedProperties", typeof(Dictionary<string, string>)) }),
                "createRole" => ("Manager/Role/V2/AddRole", new[] { ("RoleName", typeof(string)), ("Description", typeof(string)), ("ParentFolderId", typeof(int?)) }),
                "createOrganization" => ("Manager/Organization/V2/AddOrganization", new[] { ("OrganizationName", typeof(string)), ("Description", typeof(string)),
                    ("LevelId", typeof(int?)), ("ParentOrganizationId", typeof(int?)) }),
                "updateRole" => ("Manager/Role/V2/UpdateRole", new[] { ("RoleId", typeof(int)), ("RoleName", typeof(string)), ("Description", typeof(string)), ("ParentFolderId", typeof(int?)) }),
                "updateOrganization" => ("Manager/Organization/V2/UpdateOrganization", new[] { ("OrganizationId", typeof(int)), ("OrganizationName", typeof(string)), ("Description", typeof(string)),
                    ("LevelId", typeof(int?)), ("ParentOrganizationId", typeof(int?)) }),
                "deleteRoles" => ("Manager/Role/V2/DeleteRoles", new[] { ("RoleIds", typeof(List<int>)) }),
                "deleteOrganizations" => ("Manager/Organization/V2/DeleteOrganizations", new[] { ("OrganizationIds", typeof(List<int>)) }),
                "addRolesToUsers" => ("Manager/Role/V2/AddUsersToRoles", new[] { ("UserNames", typeof(List<string>)), ("RoleIds", typeof(List<int>)) }),
                "removeRolesFromUsers" => ("Manager/Role/V2/RemoveUsersFromRoles", new[] { ("UserNames", typeof(List<string>)), ("RoleIds", typeof(List<int>)) }),
                "setRolesForUsers" => ("Manager/Role/V2/SetRolesForUsers", new[] { ("UserNames", typeof(List<string>)), ("RoleIds", typeof(List<int>)) }),
                "addOrganizationsToUsers" => ("Manager/Organization/V2/AddUsersToOrganizations", new[] { ("UserNames", typeof(List<string>)), ("OrganizationIds", typeof(List<int>)) }),
                "removeOrganizationsFromUsers" => ("Manager/Organization/V2/RemoveUsersFromOrganizations", new[] { ("UserNames", typeof(List<string>)), ("OrganizationIds", typeof(List<int>)) }),
                "setOrganizationsForUsers" => ("Manager/Organization/V2/SetOrganizationsForUsers", new[] { ("UserNames", typeof(List<string>)), ("OrganizationIds", typeof(List<int>)) }),
                "addUsersToRoles" => ("Manager/Role/V2/AddUsersToRoles", new[] { ("UserNames", typeof(List<string>)), ("RoleIds", typeof(List<int>)) }),
                "removeUsersFromRoles" => ("Manager/Role/V2/RemoveUsersFromRoles", new[] { ("UserNames", typeof(List<string>)), ("RoleIds", typeof(List<int>)) }),
                "setUsersForRoles" => ("Manager/Role/V2/SetUsersForRoles", new[] { ("UserNames", typeof(List<string>)), ("RoleIds", typeof(List<int>)) }),
                "addUsersToOrganizations" => ("Manager/Organization/V2/AddUsersToOrganizations", new[] { ("UserNames", typeof(List<string>)), ("OrganizationIds", typeof(List<int>)) }),
                "removeUsersFromOrganizations" => ("Manager/Organization/V2/RemoveUsersFromOrganizations", new[] { ("UserNames", typeof(List<string>)), ("OrganizationIds", typeof(List<int>)) }),
                "setUsersForOrganizations" => ("Manager/Organization/V2/SetUsersForOrganizations", new[] { ("UserNames", typeof(List<string>)), ("OrganizationIds", typeof(List<int>)) }),
                "addOrganizationsToRoles" or "addRolesToOrganizations" => ("Manager/Role/V2/AddOrganizationsToRoles", new[] { ("RoleIds", typeof(List<int>)), ("OrganizationIds", typeof(List<int>)), ("IsEffectedForSubOrganizations", typeof(bool?)) }),
                "removeOrganizationsFromRoles" or "removeRolesFromOrganizations" => ("Manager/Role/V2/RemoveOrganizationsFromRoles", new[] { ("RoleIds", typeof(List<int>)), ("OrganizationIds", typeof(List<int>)), ("IsEffectedForSubOrganizations", typeof(bool?)) }),
                "setOrganizationsForRoles" => ("Manager/Role/V2/SetOrganizationsForRoles", new[] { ("RoleIds", typeof(List<int>)), ("OrganizationIds", typeof(List<int>)), ("IsEffectedForSubOrganizations", typeof(bool?)) }),
                "setRolesForOrganizations" => ("Manager/Role/V2/SetRolesForOrganizations", new[] { ("RoleIds", typeof(List<int>)), ("OrganizationIds", typeof(List<int>)), ("IsEffectedForSubOrganizations", typeof(bool?)) }),
                "setUsersPassword" => ("Manager/User/ResetUsersPassword", new[] { ("UserNames", typeof(List<string>)), ("NewPassword", typeof(string)) }),
                "setUsersValidity" => ("Manager/User/V2/SetUsersValidity", new[] { ("UserNames", typeof(List<string>)), ("IsValid", typeof(bool)) }),
                "getUser" => ("Manager/User/GetUserInfo", new[] { ("userName", typeof(string)) }),
                "listUsers" => ("Manager/User/GetUserInfos", new[] { ("roleName", typeof(string)) }),
                "providerInfo" => ("UserService/GetSimpleSecurityProviderInfo", Array.Empty<(string, Type)>()),
                _ => extra is not null ? (extra.Endpoint, extra.Fields.Select(field => (char.ToUpperInvariant(field.Name[0]) + field.Name[1..], field.Type)).ToArray()) :
                    throw new NotSupportedException("The native account action is not registered.")
            };
            if (windows)
            {
                endpoint = WindowsEndpoints.TryGetValue(action, out var nativeEndpoint) ? nativeEndpoint : throw new NotSupportedException("The Windows account action is not registered.");
                if (action == "createUser") fields = [("UserInfos", typeof(List<V11NativeWindowsUserInfo>))];
                if (action == "updateUser") fields = [("UserName", typeof(string)), ("Email", typeof(string)), ("ExtendedProperties", typeof(Dictionary<string, string>))];
            }
            var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("ForguncyDesignerMcp.V11.FixedUserApi." + Guid.NewGuid().ToString("N")), AssemblyBuilderAccess.Run);
            var builder = assembly.DefineDynamicModule("fixed").DefineType("FixedV11UserApi", TypeAttributes.Public | TypeAttributes.Sealed, baseType);
            builder.SetCustomAttribute(new CustomAttributeBuilder(ctor, [endpoint, Enum.ToObject(enumType, action is "getUser" or "listUsers" ? 0 : 1), 100000]));
            var constructor = builder.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var il = constructor.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, nativeCtor); il.Emit(OpCodes.Ret);
            foreach (var (propertyName, propertyType) in fields)
            {
                var field = builder.DefineField("_" + propertyName, propertyType, FieldAttributes.Private);
                var property = builder.DefineProperty(propertyName, PropertyAttributes.None, propertyType, null);
                var getter = builder.DefineMethod("get_" + propertyName, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, propertyType, Type.EmptyTypes);
                il = getter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, field); il.Emit(OpCodes.Ret); property.SetGetMethod(getter);
                var setter = builder.DefineMethod("set_" + propertyName, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, [propertyType]);
                il = setter.GetILGenerator(); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, field); il.Emit(OpCodes.Ret); property.SetSetMethod(setter);
            }
            var request = builder.CreateType()!;
            var invokeable = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.IInvokeable`1", true)!.MakeGenericType(response);
            if (!invokeable.IsAssignableFrom(request)) throw new NotSupportedException("Native account request interface changed.");
            var method = invokeAssembly.GetType("Forguncy.Infrastructure.InvokeService.InvokeableExtensions", true)!.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "InvokeAsync" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2)
                .Single(m => m.GetParameters()[0].ParameterType.IsGenericType && m.GetParameters()[0].ParameterType.GetGenericTypeDefinition().FullName == "Forguncy.Infrastructure.InvokeService.IInvokeable`1")
                .MakeGenericMethod(response);
            if (action != "providerInfo" && response.GetProperty("Result")?.PropertyType != typeof(bool)) throw new NotSupportedException("Native account response contract changed.");
            return new(request, response, method);
        }
    }
}

// Public because the pinned native runtime serializes this object from an
// emitted request assembly. It is never returned as a receipt or log value.
public sealed class V11NativeUserInfo
{
    public string UserName { get; init; } = "";
    public string Password { get; init; } = "";
    public string? FullName { get; init; }
    public string Email { get; init; } = "";
    public Dictionary<string, string> ExtendedProperties { get; init; } = [];
}
public sealed class V11NativeWindowsUserInfo
{
    public string UserName { get; init; } = "";
    public Dictionary<string, string> ExtendedProperties { get; init; } = [];
}
public sealed class V11NativeSecurityProviderInfo
{
    public int? UserInformationStorageMode { get; set; }
}
