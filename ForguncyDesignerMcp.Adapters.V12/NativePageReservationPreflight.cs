using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// In this exact lifecycle, explicit MCP New checkout inserts a name before
// V12's native dispatcher checks the shared page/table name inventory. Deny
// the currently unsupported double path rather than leave a New reservation.
internal static class NativePageReservationPreflight
{
    internal static void Validate(Assembly designer, object document, ProjectSnapshot snapshot,
        string ns, string method, JsonElement input, NativeApiFileCapture? capture)
    {
        if (!snapshot.InCollaborationMode || ns != "page" || method != "createPage" ||
            Text(input, "type") is not (null or "normal") || !string.IsNullOrEmpty(Text(input, "folderPath")) ||
            input.EnumerateObject().Any(p => p.Name.Equals("isMobilePage", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True) ||
            Text(input, "name") is not { Length: > 0 } name) return;
        var manager = designer.GetType("Forguncy.Common.CollaborationManager", true)!.GetField("_instance", R.Flags)!.GetValue(null)
            ?? throw new BridgeException("CollaborationUnavailable", "Native collaboration must be initialized for the reviewed document.");
        if (R.Get(manager, "IsRemoteCollaboration") is not true) return;
        if (R.Get(manager, "InCollaborationMode") is not true || R.Get(manager, "Collaboration") is not { } collaboration ||
            R.Get(collaboration, "WorkingDirectory") is not string working ||
            !string.Equals(Path.GetFullPath(working), Path.GetFullPath(snapshot.Target.ProjectPath), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("CollaborationProjectMismatch", "The native name inventory belongs to another working directory.");
        var settings = R.Get(document, "ForguncySettings")!;
        var state = R.Static(designer.GetType("s", true)!, "get_Instance")!;
        if (!ReferenceEquals(R.Get(state, "et"), settings) || !ReferenceEquals(R.Get(state, "ew"), document))
            throw new BridgeException("DocumentChanged", "The collaboration name check is not bound to the reviewed document.");
        var pages = R.Get(settings, "Pages")!;
        var tables = R.Get(settings, "TableSet")!;
        var workbook = R.Get(document, "Workbook")!;
        var sheets = ((IEnumerable)R.Static(designer.GetType("Forguncy.Common.WorkbookExtensions", true)!,
            "EnumAllSheetNames", workbook)!).Cast<string>();
        bool realModel = R.Call(pages, "TryGetPage", name) is not null ||
            R.Call(R.Get(tables, "Tables")!, "TryGetTable", name) is not null ||
            sheets.Any(s => SameName(s, name)) ||
            ((IEnumerable)R.Get(tables, "RealTablesList")!).Cast<object>().Any(table => SameName((string?)R.Get(table, "Name"), name));

        var registry = (IDictionary)R.Get(manager, "Modules")!;
        var sharedTypes = ((IEnumerable)R.Get(manager, "SheetShareNameModuleTypes")!).Cast<object>().ToHashSet();
        var matches = registry.Values.Cast<object>().Where(module =>
        {
            var nativeName = (string)R.Get(module, "ModuleName")!;
            return sharedTypes.Contains(R.Get(module, "ModuleType")!) &&
                SameName(nativeName[(nativeName.LastIndexOf('/') + 1)..], name);
        }).ToArray();

        bool priorOwnNew = matches is [var existing] && R.Get(existing, "State")?.ToString() == "New" &&
            R.Get(existing, "LockedBy") is { } signature && snapshot.CurrentUser is { } actor &&
            (string?)R.Get(signature, "UserName") == actor.UserName && (string?)R.Get(signature, "Email") == actor.Email;

        RequireEligible(realModel, matches.Length, priorOwnNew, MatchesPlan(capture, name));
    }

    internal static bool MatchesPlan(NativeApiFileCapture? capture, string name) =>
        capture is { Operation: ModuleOperation.Create } && capture.Address == new ModuleAddress(ModuleKind.Page, name) &&
        capture.Plan is { BeforeGroups.Count: 0, ExpectedNewModules: [var created], NativeChanges: [var change] } &&
        created == ("PCPage/" + name, "PCPage") &&
        change.GetType().GetField("Item2")?.GetValue(change)?.ToString() == "New" &&
        change.GetType().GetField("Item1")?.GetValue(change) is { } token &&
        token.GetType().FullName == "Forguncy.Common.SimpleModule" &&
        (string?)R.Get(token, "ModuleName") == created.Name && R.Get(token, "ModuleType")?.ToString() == created.Type;

    internal static void RequireEligible(bool existingModel, int registeredMatches, bool ownedNew, bool exactNewPlan)
    {
        if (existingModel)
            throw new BridgeException("NativePageNameAlreadyExists", "A page or table already uses this name. No checkout or page creation was dispatched.");
        if (registeredMatches != 0)
            throw new BridgeException(ownedNew ? "NativePageNamePreviouslyReserved" : "NativePageNameRegistered",
                ownedNew
                    ? "A prior collaboration New registration already holds this name. Reconcile that original operation; it is not proof of this request's ownership. No checkout or page creation was dispatched."
                    : "An existing collaboration registration holds this name. No checkout or page creation was dispatched.");
        if (exactNewPlan)
            throw new BridgeException("NativePageCreationReservationConflict",
                "This V12 remote page-create path would register the name before native duplicate validation. No checkout or page creation was dispatched.");
    }

    private static bool SameName(string? x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    private static string? Text(JsonElement input, string key) => input.EnumerateObject()
        .Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
        .Select(p => p.Value.GetString()).FirstOrDefault();
}
