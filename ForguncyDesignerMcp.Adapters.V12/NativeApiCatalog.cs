using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>Explicit API namespace boundary and module routing for the verified V12 registry.</summary>
public static class NativeApiCatalog
{
    private static readonly HashSet<string> Namespaces = new(StringComparer.Ordinal)
    {
        "app", "app.customLibrary", "app.permissionGroup", "command", "image", "notification", "page",
        "page.backgroundImage", "page.cell", "page.listView", "page.repeater", "page.tabControl",
        "page.chart", "page.conditionFormat", "page.dataValidation", "page.definition", "page.picture",
        "scheduledTask", "serverCommand", "sharedCommand", "table", "table.view", "userManagement",
        "userManagement.organization", "userManagement.role", "userManagement.user"
    };
    private static readonly HashSet<string> Prohibited = new(StringComparer.Ordinal)
    {
        "app.generateProject", "app.saveProject", "serverCommand.testServerCommand",
        "table.queryData", "table.insertRows", "table.updateRows", "table.deleteRows", "table.deleteTableFolder", "table.duplicateTable", "table.generatePageFromTable",
        // These native aliases either omit reference guards, reset protected
        // defaults, recursively delete unguarded children, or clean role/resource
        // bindings. They must not bypass the typed adapter's stricter lifecycle.
        "scheduledTask.deleteFolder", "page.deleteCellStyle", "page.deletePageFolder"
    };
    private static readonly HashSet<string> UnguardedDeletes = new(StringComparer.Ordinal)
    {
        "scheduledTask.deleteFolder", "page.deleteCellStyle", "page.deletePageFolder"
    };

    public static bool IsAllowed(string ns, string method, string access) => Namespaces.Contains(ns) &&
        !Prohibited.Contains(ns + "." + method);

    public static string? ExclusionReason(string ns, string method, string access) => IsAllowed(ns, method, access) ? null :
        !Namespaces.Contains(ns) ? "Native namespace is not yet reviewed or adapted." :
        ns == "table" && method == "generatePageFromTable" ? "Table-driven generation creates multiple page modules; its generated native target groups have not been fully adapted. Use explicit page creation." :
        UnguardedDeletes.Contains(ns + "." + method) ? "This native deletion omits the required reference/default protection or cleans additional role/resource bindings. Use a separately guarded typed lifecycle." :
        "This API can execute data, generation, installation, native save without adapter verification, or unreviewed recursive/external database effects.";

    internal static bool IsRuntimeAction(string ns,string method)=>
        ns=="app" && method is "uploadFrontendLibrary" or "deleteFrontendLibrary" || ns=="command" && method=="listTypes";

    public static EffectScope Scope(string ns, string method) => IsRuntimeAction(ns,method)?EffectScope.RuntimeDeployment:
        ns.StartsWith("userManagement", StringComparison.Ordinal)?EffectScope.AccountService:EffectScope.ProjectDefinition;

    public static void ValidateParameters(string ns, string method, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidApiParameters", "Parameters must be a JSON object.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        JsonElement? commands = null;
        // Folder arguments are logical project paths, never filesystem paths.
        foreach (var property in parameters.EnumerateObject())
        {
            if (!seen.Add(property.Name)) throw new BridgeException("AmbiguousApiParameter", "Duplicate or differently-cased request properties are not allowed.");
            var parameterName = property.Name.ToLowerInvariant();
            if (parameterName == "commands") commands = property.Value;
            if (parameterName == "force" && property.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                throw new BridgeException("ReferenceBypassNotAllowed", "Project-definition CRUD must preserve native reference checks.");
            if ((ns == "table.view" || ns == "table") && parameterName is "deletedatabaseobject" or "renamedatabaseobject" or "modifydatabaseobject" &&
                property.Value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null))
                throw new BridgeException("ExternalDatabaseSchemaNotAllowed", "Database-object changes are outside project-definition CRUD.");
            if (property.Value.ValueKind != JsonValueKind.String) continue;
            if (parameterName is "folder" or "folderpath" or "targetfolder" or "targetfolderpath" or "sourcefolderpath" or "newfolderpath" or "parentfolderpath")
                RequireProjectFolder(property.Value.GetString()!, property.Name);
            if (ns == "image" && parameterName is "filename" or "name" || ns == "app.customLibrary" && parameterName is "name" or "newname")
                RequireLeafName(property.Value.GetString()!, property.Name);
            if (ns == "app.customLibrary" && parameterName is "url" or "newurl")
            {
                var value = property.Value.GetString()!;
                if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
                    throw new BridgeException("InvalidLibraryUrl", "Linked library definitions require an absolute HTTP or HTTPS URL.");
            }
        }
        if (ns is "serverCommand" or "sharedCommand" or "scheduledTask")
        {
            var creates = method is "createServerCommand" or "createSharedCommand" or "createScheduledTask";
            var updates = method is "updateServerCommand" or "updateSharedCommand" or "updateScheduledTask";
            if ((creates || updates && commands is not null) &&
                (commands is not { ValueKind: JsonValueKind.Array } nodes || nodes.GetArrayLength() == 0))
                throw new BridgeException("CommandNodesRequired", "Command definitions require a nonempty native-validated command-node array before mutation.");
        }
        // Notification parameter changes use the pinned native resolver to
        // capture the notification plus every rewritten caller before dispatch.
        // Column and relation mutations capture their actual native resolver
        // tuples, all shared database owners, and any rewritten reference
        // owners in NativeModuleFiles before dispatch.
        // No scheduler execution or notification send operation is registered.
        // Do not infer read-only access from method names: app.generateProject
        // and serverCommand.testServerCommand are native Read APIs with effects.
    }

    private static void RequireProjectFolder(string value, string parameter)
    {
        if (value.Length == 0) return;
        if (value.StartsWith('/') || value.StartsWith('\\') || value.Contains(':') || value.Contains('\\') ||
            value.IndexOfAny(['\0', '<', '>', '"', '|', '?', '*']) >= 0 ||
            value.Split('/').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ')))
            throw new BridgeException("InvalidProjectFolder", parameter + " must be a logical project folder separated by '/', without rooted paths or traversal.");
    }

    private static void RequireLeafName(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(['/', '\\', ':', '\0', '<', '>', '"', '|', '?', '*']) >= 0 || value.EndsWith('.') || value.EndsWith(' '))
            throw new BridgeException("InvalidResourceName", parameter + " must identify one project resource filename, without paths or alternate data streams.");
    }

    public static IReadOnlyDictionary<ModuleKind, NativeModuleApiRoute> Routes { get; } =
        new Dictionary<ModuleKind, NativeModuleApiRoute>
        {
            [ModuleKind.Page] = new("page", "listPages", "getPageInfo", "createPage", "updatePageSettings", "deletePage", "pageName", "pages"),
            [ModuleKind.MasterPage] = new("page", "listPages", "getPageInfo", "createPage", "updatePageSettings", "deletePage", "pageName", "pages"),
            [ModuleKind.UserControlPage] = new("page", "listPages", "getPageInfo", "createPage", "updatePageSettings", "deletePage", "pageName", "pages"),
            [ModuleKind.Table] = new("table", "listTables", "getTable", "createTable", null, "deleteTable", "tableName", "tables"),
            [ModuleKind.View] = new("table.view", "list", "get", "create", "update", "delete", "viewName", "views"),
            [ModuleKind.ServerCommand] = new("serverCommand", "listServerCommands", "getServerCommand", "createServerCommand", "updateServerCommand", "deleteServerCommand", "name", "serverCommands"),
            [ModuleKind.SharedCommand] = new("sharedCommand", "listSharedCommands", "getSharedCommand", "createSharedCommand", "updateSharedCommand", "deleteSharedCommand", "name", "sharedCommands"),
            [ModuleKind.ScheduledTask] = new("scheduledTask", "listScheduledTasks", "getScheduledTask", "createScheduledTask", "updateScheduledTask", "deleteScheduledTask", "name", "scheduledTasks"),
            [ModuleKind.Notification] = new("notification", "listNotifications", "getNotification", "createNotification", "updateNotification", "deleteNotification", "name", "notifications"),
            [ModuleKind.Image] = new("image", "listUploaded", null, "upload", "upload", "delete", "name", "images"),
            [ModuleKind.GlobalVariable] = new("app", "listGlobalVariables", null, "createGlobalVariable", "updateGlobalVariable", "deleteGlobalVariable", "name", "variables"),
            [ModuleKind.CustomLibrary] = new("app.customLibrary", "list", "getContent", "createLocal", "updateContent", "delete", "name", "items"),
            [ModuleKind.PermissionGroup] = new("app.permissionGroup", "listPermissionGroups", null, "createPermissionGroup", "updatePermissionGroup", "deletePermissionGroup", "id", "permissionGroups"),
            [ModuleKind.CellStyle] = new("page", "listCellStyles", null, "createCellStyle", null, "deleteCellStyle", "styleKey", "styles"),
            [ModuleKind.DataConnection] = new("table", "listConnections", null, null, null, null, "name", "connections"),
            [ModuleKind.User] = new("userManagement.user", "listUsers", "getUser", "createUser", "updateUser", "deleteUsers", "userName", "users"),
            [ModuleKind.Role] = new("userManagement.role", "listRoles", "getRole", "createRole", "updateRole", "deleteRoles", "roleId", "roles"),
            [ModuleKind.Organization] = new("userManagement.organization", "listOrganizations", "getOrganization", "createOrganization", "updateOrganization", "deleteOrganizations", "organizationId", "organizations")
        };

    // Identity address names are the immutable service key for R/U/D. Create
    // receives a human name and resolves its generated service ID from the native
    // result. These definitions only describe mapping; no account service runs
    // during registry/schema/component discovery.
    public static IReadOnlyDictionary<ModuleKind, NativeIdentityApiRoute> IdentityRoutes { get; } =
        new Dictionary<ModuleKind, NativeIdentityApiRoute>
        {
            [ModuleKind.User] = new("userName", "userName", "userNames", false, null, "userName"),
            [ModuleKind.Role] = new("roleName", "roleId", "roleIds", true, "id", "roleId"),
            [ModuleKind.Organization] = new("organizationName", "organizationId", "organizationIds", true, "id", "id")
        };
}

// A route means a real attributed native method exists, not that a generic
// read document can be round-tripped through Update. Metadata is native request
// arguments for C/U, and the native response for R. Capability validation is
// maintained by the adapter and its component/live acceptance matrix.
public sealed record NativeModuleApiRoute(string Namespace, string List, string? Read, string? Create,
    string? Update, string? Delete, string NameParameter, string? ListContainer = null);
public sealed record NativeIdentityApiRoute(string CreateNameParameter, string IdentityParameter,
    string DeleteArrayParameter, bool NumericIdentity, string? CreatedIdentityProperty, string ListIdentityProperty);
