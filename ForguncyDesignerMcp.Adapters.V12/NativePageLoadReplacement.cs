using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// Plans a replacement without changing the live page. The caller owns UI scheduling,
// collaboration guards, dirty notification, save, and post-apply receipt handling.
internal static class NativePageLoadReplacement
{
    internal sealed record Plan(object Settings, string PageName, object Page, IList Live, object[] Before, object[] After,
        string Baseline, string Expected, Assembly Assembly, object Context);

    internal static async Task<Plan> PrepareAsync(Assembly assembly, object settings, JsonElement input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = Prepare(assembly, settings, input);
        var helper = assembly.GetType("Forguncy.AI.Actions.CommandTreeHelper", true)!;
        var commands = (IList)Activator.CreateInstance(plan.Live.GetType())!;
        foreach (var command in plan.After) commands.Add(command);
        // This is the page-load scope used by the pinned native action. Keep the
        // asynchronous validation asynchronous so the caller can await it on the UI scheduler.
        var scopeMethod = helper.GetMethods(R.Flags).Single(m => m.Name == "ValidateCommandScope" &&
            m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType == commands.GetType());
        var scope = Enum.ToObject(scopeMethod.GetParameters()[1].ParameterType, 4);
        try
        {
            R.Static(helper, "ValidateCommandScope", commands, scope);
            var nativeContext = R.Static(assembly.GetType("acq", true)!, "ForPage", plan.PageName)!;
            await ((Task)R.Static(helper, "ValidateCommandTreeAsync", commands, nativeContext, cancellationToken)!).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw Invalid("commands", "native validation rejected the tree: " + error.Message); }
        return plan;
    }

    private static Plan Prepare(Assembly assembly, object settings, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) throw Invalid("request", "an object is required");
        JsonElement Field(string name)
        {
            var matches = input.EnumerateObject().Where(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length > 1) throw Invalid(name, "duplicate request fields are not allowed");
            return matches.Length == 0 ? default : matches[0].Value;
        }
        var nameNode = Field("pageName");
        if (nameNode.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameNode.GetString()))
            throw Invalid("pageName", "a nonempty page name is required");
        var tree = Field("commands");
        if (tree.ValueKind != JsonValueKind.Array)
            throw Invalid("commands", "an array is required; [] explicitly clears the list");
        var name = nameNode.GetString()!;
        var page = R.Call(R.Get(settings, "Pages")!, "TryGetPage", name) ?? throw Invalid("pageName", "native page not found");
        var live = R.Get(page, "PageLoadedCommandList") as IList ?? throw Invalid("pageName", "page does not expose a command list");
        var context = R.Static(assembly.GetType("aaw", true)!, "ForPage", name)!;
        var helper = assembly.GetType("Forguncy.AI.Actions.CommandTreeHelper", true)!;
        var convert = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
        var jarray = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JArray", true)!;
        string Serialize(IList list) => (string)R.Static(convert, "SerializeObject", R.Static(helper, "SerializeCommands", list, context))!;
        var before = live.Cast<object>().ToArray();
        var baseline = Serialize(live);
        var original = JsonNode.Parse(baseline)!.AsArray();
        var requested = JsonNode.Parse(tree.GetRawText())!.AsArray();
        if (original.Count != before.Length) throw Invalid("commands", "native tree and model count differ");
        var originals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < original.Count; i++)
        {
            var id = original[i]?["id"]?.GetValue<string>();
            if (string.IsNullOrEmpty(id) || !originals.TryAdd(id, i)) throw Invalid("commands", "native top-level command identity is missing or duplicated");
        }
        var used = new HashSet<int>();
        var selected = new int?[requested.Count];
        for (var i = 0; i < requested.Count; i++)
        {
            if (requested[i] is not JsonObject node) throw Invalid($"commands[{i}]", "must be a command object");
            string? id = null;
            if (node.TryGetPropertyValue("id", out var suppliedId) &&
                (suppliedId is not JsonValue value || !value.TryGetValue<string>(out id) || string.IsNullOrWhiteSpace(id)))
                throw Invalid($"commands[{i}].id", "must be a nonempty native command ID when supplied");
            if (id is null) continue;
            if (!originals.TryGetValue(id, out var index)) continue; // Native parser must reproduce any explicitly supplied new ID.
            if (!JsonNode.DeepEquals(original[index], node))
                throw Invalid($"commands[{i}].id", "an existing command with changed content cannot be reconstructed safely");
            if (!used.Add(index)) throw Invalid($"commands[{i}].id", "the same native command cannot appear twice");
            selected[i] = index;
        }
        var after = new object[requested.Count];
        var usedBranches = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < requested.Count; i++)
        {
            if (selected[i] is int found) { after[i] = before[found]; continue; }
            var one = new JsonArray(requested[i]!.DeepClone());
            IList parsed;
            try { parsed = (IList)R.Static(helper, "ParseCommands", R.Static(jarray, "Parse", one.ToJsonString()), context)!; }
            catch (Exception error) { throw Invalid($"commands[{i}]", "native parser rejected the node: " + error.Message); }
            if (parsed.Count != 1 || parsed[0] is not { } created) throw Invalid($"commands[{i}]", "native parser did not produce exactly one command");
            // Conditions expose native branch objects with identity and hidden state. Reuse a
            // complete matching branch only when its source parent is not also reused.
            if (requested[i] is JsonObject wanted && wanted["type"]?.GetValue<string>() == "Condition" &&
                wanted["branches"] is JsonArray branches && R.Get(created, "ConditionAndCommandPairList") is IList createdBranches)
            {
                if (createdBranches.Count != branches.Count) throw Invalid($"commands[{i}].branches", "native branch count changed");
                for (var b = 0; b < branches.Count; b++)
                {
                    var matches = new List<object>();
                    for (var old = 0; old < before.Length; old++)
                    {
                        if (used.Contains(old) || original[old]?["type"]?.GetValue<string>() != "Condition" ||
                            original[old]?["branches"] is not JsonArray oldBranches ||
                            R.Get(before[old], "ConditionAndCommandPairList") is not IList oldNative || oldNative.Count != oldBranches.Count) continue;
                        for (var k = 0; k < oldBranches.Count; k++)
                            if (JsonNode.DeepEquals(oldBranches[k], branches[b]) && oldNative[k] is { } branch && !usedBranches.Contains(branch)) matches.Add(branch);
                    }
                    if (matches.Count > 1) throw Invalid($"commands[{i}].branches[{b}]", "matching native branch is ambiguous");
                    if (matches.Count == 1) { createdBranches[b] = matches[0]; usedBranches.Add(matches[0]); }
                }
            }
            var singleton = (IList)Activator.CreateInstance(live.GetType())!;
            singleton.Add(created);
            var roundtrip = JsonNode.Parse(Serialize(singleton))!.AsArray()[0];
            RequireExplicitFields(requested[i], roundtrip, $"commands[{i}]");
            after[i] = created;
        }
        var expectedList = (IList)Activator.CreateInstance(live.GetType())!;
        foreach (var command in after) expectedList.Add(command);
        var expected = Serialize(expectedList);
        return new(settings, name, page, live, before, after, baseline, expected, assembly, context);
    }

    internal static void Apply(Plan plan)
    {
        var helper = plan.Assembly.GetType("Forguncy.AI.Actions.CommandTreeHelper", true)!;
        var convert = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
        string Current() => (string)R.Static(convert, "SerializeObject", R.Static(helper, "SerializeCommands", plan.Live, plan.Context))!;
        if (!ReferenceEquals(R.Call(R.Get(plan.Settings, "Pages")!, "TryGetPage", plan.PageName), plan.Page) ||
            !ReferenceEquals(R.Get(plan.Page, "PageLoadedCommandList"), plan.Live) ||
            plan.Live.Count != plan.Before.Length || !plan.Before.Select((item, i) => ReferenceEquals(item, plan.Live[i])).All(x => x) ||
            !JsonNode.DeepEquals(JsonNode.Parse(plan.Baseline), JsonNode.Parse(Current())))
            throw new BridgeException("BaselineChanged", "Page load commands changed after replacement preflight.");
        try
        {
            plan.Live.Clear(); foreach (var command in plan.After) plan.Live.Add(command);
            if (!JsonNode.DeepEquals(JsonNode.Parse(plan.Expected), JsonNode.Parse(Current())))
                throw new BridgeException("NativeCommandReadbackUnverified", "The applied page-load tree differs from the preflight result.");
        }
        catch { plan.Live.Clear(); foreach (var command in plan.Before) plan.Live.Add(command); throw; }
    }

    private static void RequireExplicitFields(JsonNode? requested, JsonNode? actual, string path)
    {
        if (requested is JsonObject expectedObject)
        {
            if (actual is not JsonObject actualObject) throw Invalid(path, "native round-trip changed the object type");
            foreach (var property in expectedObject)
            {
                if (!actualObject.TryGetPropertyValue(property.Key, out var value)) throw Invalid(path + "." + property.Key, "native round-trip omitted the supplied field");
                RequireExplicitFields(property.Value, value, path + "." + property.Key);
            }
        }
        else if (requested is JsonArray expectedArray)
        {
            if (actual is not JsonArray actualArray || expectedArray.Count != actualArray.Count) throw Invalid(path, "native round-trip changed the array length");
            for (var i = 0; i < expectedArray.Count; i++) RequireExplicitFields(expectedArray[i], actualArray[i], $"{path}[{i}]");
        }
        else if (!JsonNode.DeepEquals(requested, actual)) throw Invalid(path, "native round-trip changed the supplied value");
    }
    private static BridgeException Invalid(string path, string reason) => new("CommandTreeRoundTripUnverified", $"{path}: {reason}.");
}
