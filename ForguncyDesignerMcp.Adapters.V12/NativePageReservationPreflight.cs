using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// In this exact lifecycle, explicit MCP New checkout inserts a name before
// V12's native dispatcher checks the shared page/table name inventory. Select
// the exact native single-dispatch route without weakening the name check.
internal static class NativePageReservationPreflight
{
    internal static bool Validate(Assembly designer, object document, ProjectSnapshot snapshot,
        string ns, string method, JsonElement input, NativeApiFileCapture? capture)
    {
        if (!snapshot.InCollaborationMode || ns != "page" || method != "createPage" ||
            Text(input, "type") is not (null or "normal") || !string.IsNullOrEmpty(Text(input, "folderPath")) ||
            input.EnumerateObject().Any(p => p.Name.Equals("isMobilePage", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.True) ||
            Text(input, "name") is not { Length: > 0 } name) return false;
        var manager = designer.GetType("Forguncy.Common.CollaborationManager", true)!.GetField("_instance", R.Flags)!.GetValue(null)
            ?? throw new BridgeException("CollaborationUnavailable", "Native collaboration must be initialized for the reviewed document.");
        if (R.Get(manager, "IsRemoteCollaboration") is not true) return false;
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

        var exact = MatchesPlan(capture, name);
        RequireEligible(realModel, matches.Length, priorOwnNew);
        if (!exact) return false;
        if (snapshot.HasUnsavedChanges || snapshot.Busy)
            throw new BridgeException("NativePageCreatePrestateUnverified", "Native single-dispatch page creation requires a clean, idle document.");
        // Ask the pinned native shared-name census for a complete answer.
        // This read does not reserve a name or run the checkout executor.
        RequireCompleteNameInventory(R.Call(manager, "EnumAllPageNames"), name);
        return true;
    }

    internal static void RequireCompleteNameInventory(object? result, string name)
    {
        if (result is not ValueTuple<List<string>, bool> census || !census.Item2 || census.Item1 is null)
            throw new BridgeException("NativePageNameInventoryUnverified", "The native shared page/table name inventory is incomplete.");
        if (census.Item1.Any(item => SameName(item, name)))
            throw new BridgeException("NativePageNameAlreadyExists", "The native shared page/table inventory already contains the name; no checkout was dispatched.");
    }

    internal static void RequireCreatedPage(ProjectSnapshot before, ProjectSnapshot after, string beforeSource, string afterSource,
        JsonElement result, ModuleAddress expected, string expectedNativeName, IReadOnlyList<NativeModuleFileGroup>? groups)
    {
        if (after.Target != before.Target || after.CurrentUser != before.CurrentUser || after.Busy ||
            !after.HasUnsavedChanges || beforeSource != afterSource ||
            result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("pageName", out var created) ||
            created.ValueKind != JsonValueKind.String || created.GetString() != expected.Name)
            throw new BridgeException("NativePageCreateReadbackUnverified", "Native creation did not preserve its reviewed target or pending-save evidence; do not replay.");
        if (groups is not [var group] || group.Address != expected || !group.Current() ||
            group.NativeModuleName != expectedNativeName)
            throw new BridgeException("NativePageCreateOwnerUnverified", "The exact native page source file group was not verified; do not replay.");
    }

    internal static bool MatchesPlan(NativeApiFileCapture? capture, string name) =>
        capture is { Operation: ModuleOperation.Create } && capture.Address == new ModuleAddress(ModuleKind.Page, name) &&
        capture.Plan is { BeforeGroups.Count: 0, ExpectedNewModules: [var created], NativeChanges: [var change] } &&
        created == ("PCPage/" + name, "PCPage") &&
        change.GetType().GetField("Item2")?.GetValue(change)?.ToString() == "New" &&
        change.GetType().GetField("Item1")?.GetValue(change) is { } token &&
        token.GetType().FullName == "Forguncy.Common.SimpleModule" &&
        (string?)R.Get(token, "ModuleName") == created.Name && R.Get(token, "ModuleType")?.ToString() == created.Type;

    internal static void RequireEligible(bool existingModel, int registeredMatches, bool ownedNew)
    {
        if (existingModel)
            throw new BridgeException("NativePageNameAlreadyExists", "A page or table already uses this name. No checkout or page creation was dispatched.");
        if (registeredMatches != 0)
            throw new BridgeException(ownedNew ? "NativePageNamePreviouslyReserved" : "NativePageNameRegistered",
                ownedNew
                    ? "A prior collaboration New registration already holds this name. Reconcile that original operation; it is not proof of this request's ownership. No checkout or page creation was dispatched."
                    : "An existing collaboration registration holds this name. No checkout or page creation was dispatched.");
    }

    private static bool SameName(string? x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
    private static string? Text(JsonElement input, string key) => input.EnumerateObject()
        .Where(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
        .Select(p => p.Value.GetString()).FirstOrDefault();
}
