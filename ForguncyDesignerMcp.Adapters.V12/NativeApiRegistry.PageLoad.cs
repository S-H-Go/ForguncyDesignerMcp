using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeApiRegistry
{
    internal sealed record PageLoadEdit(object Settings, object Page, string PageName, string ExpectedTree, Action Apply);

    internal async Task<PageLoadEdit?> PreparePageLoadAsync(string ns, string method, JsonElement parameters, CancellationToken token)
    {
        if (ns != "page" || method != "updatePageLoadCommands") return null;
        var commands = parameters.EnumerateObject().Single(x => x.Name.Equals("commands", StringComparison.OrdinalIgnoreCase)).Value;
        if (commands.GetArrayLength() == 0 && boundSettings is null) return null;
        var settings = boundSettings?.Invoke() ?? throw new BridgeException("NativeCommandContextUnavailable", "Page load commands require the bound page.");
        var name = parameters.EnumerateObject().Single(x => x.Name.Equals("pageName", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        var model = R.Call(R.Get(settings, "Pages")!, "TryGetPage", name);
        if (model?.GetType().FullName != "Forguncy.Model.Pages.NormalPage")
            throw new BridgeException("NativePageOwnerUnverified", "The page-load owner must be a bound normal page: " + name);
        var native = R.Static(designer.GetType("Forguncy.AI.Actions.PageSandboxAIActions", true)!, "GetPageLoadCommandSupportOrThrow", name)!;
        var state = R.Static(designer.GetType("s", true)!, "get_Instance")!;
        var sheet = R.Call(R.Get(R.Get(state, "ev")!, "Sheets")!, "get_Item", name);
        var tag = sheet is null ? null : R.Get(sheet, "Tag");
        var sheetPage = tag is null ? null : R.Get(tag, "PageInfo");
        var commandsOnPage = R.Get(model, "PageLoadedCommandList");
        if (commandsOnPage is null || sheetPage is null || !ReferenceEquals(commandsOnPage, R.Get(native, "PageLoadedCommandList")) ||
            !ReferenceEquals(commandsOnPage, R.Get(sheetPage, "PageLoadedCommandList")))
            throw new BridgeException("NativePageOwnerUnverified", "The native command owner and the worksheet to be saved differ: " + name);
        var colleague = R.Get(R.Get(state, "fm")!, "PageModel")!;
        NativePageLoadPatch.Plan script;
        try { script = NativePageLoadPatch.Prepare(designer, settings, parameters); }
        catch (BridgeException error) when (error.Code == "CommandTreeRoundTripUnverified")
        {
            var replacement = await NativePageLoadReplacement.PrepareAsync(designer, settings, parameters, token);
            var proposed = (IList)Activator.CreateInstance(replacement.Live.GetType())!;
            foreach (var item in replacement.After) proposed.Add(item);
            NativePageLoadBindings.RequirePersistable(designer, name, sheet!, colleague, proposed);
            return new(settings, replacement.Page, replacement.PageName, replacement.Expected, () => NativePageLoadReplacement.Apply(replacement));
        }
        // Validate the changed command on a detached instance. Existing branch
        // objects, opaque plugin properties and IDs remain on the original tree.
        var clone = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(script.Command, null)!;
        R.Set(clone, "JavaScript", script.NewScript);
        var list = (IList)Activator.CreateInstance(R.Get(script.Page, "PageLoadedCommandList")!.GetType())!;
        list.Add(clone);
        var context = R.Static(designer.GetType("acq", true)!, "ForPage", script.PageName)!;
        var helper = designer.GetType("Forguncy.AI.Actions.CommandTreeHelper", true)!;
        try { await (Task)R.Static(helper, "ValidateCommandTreeAsync", list, context, token)!; }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        { throw new BridgeException("InvalidCommandTree", "commands: native JavaScript validation failed: " + error.Message); }
        NativePageLoadBindings.RequirePersistable(designer, name, sheet!, colleague, (IList)commandsOnPage);
        return new(settings, script.Page, script.PageName, script.ExpectedJson, () =>
        {
            var current = NativePageLoadPatch.Prepare(designer, settings, parameters);
            if (!ReferenceEquals(current.Command, script.Command) || !ReferenceEquals(current.Page, script.Page) || current.OldScript != script.OldScript)
                throw new BridgeException("PageLoadBaselineChanged", "The page or command changed during validation.");
            R.Set(script.Command, "JavaScript", script.NewScript);
        });
    }

    internal async Task<JsonElement> ApplyPageLoadAsync(PageLoadEdit edit)
    {
        var state = R.Static(designer.GetType("s", true)!, "get_Instance")!;
        var doc = R.Get(state, "ew") ?? throw new BridgeException("DocumentChanged", "The bound native document is unavailable.");
        if (!ReferenceEquals(R.Get(doc, "ForguncySettings"), edit.Settings) ||
            !ReferenceEquals(R.Call(R.Get(edit.Settings, "Pages")!, "TryGetPage", edit.PageName), edit.Page))
            throw new BridgeException("DocumentChanged", "The page or document changed after validation.");
        var colleague = R.Get(R.Get(state, "fm")!, "PageModel")!;
        edit.Apply();
        R.Call(colleague, "Edit", edit.Page, edit.PageName);
        R.Set(doc, "ContainsUnsavedChanges", true);
        var read = await InvokeAsync("page", "getPageLoadCommands", JsonSerializer.SerializeToElement(new { pageName = edit.PageName }), CancellationToken.None);
        if (!read.TryGetProperty("commands", out var current) || !JsonNode.DeepEquals(JsonNode.Parse(current.GetRawText()), JsonNode.Parse(edit.ExpectedTree)))
            throw new BridgeException("NativeCommandReadbackUnverified", "The native page-load command tree differs from the reviewed replacement.");
        var response = Activator.CreateInstance(Entry("page", "updatePageLoadCommands").ResponseType)!;
        R.Set(response, "Message", $"Page load commands updated on page '{edit.PageName}'");
        using var document = JsonDocument.Parse((string)Invoke(serialize, null, [response, serializerSettings])!);
        return document.RootElement.Clone();
    }
}
