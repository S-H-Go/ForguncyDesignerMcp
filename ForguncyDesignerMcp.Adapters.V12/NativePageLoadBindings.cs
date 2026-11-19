using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// The MCP command DTO omits ServerPropertiesId. Compare those bindings on the
// native commands and on a detached page serialized by the real save colleague.
internal static class NativePageLoadBindings
{
    internal static void RequirePersistable(Assembly designer, string name, object sheet, object colleague, IList proposed)
    {
        var helper = designer.GetType("Forguncy.AI.Actions.CommandTreeHelper", true)!;
        var context = R.Static(designer.GetType("aaw", true)!, "ForPage", name)!;
        Dictionary<string, object> Bindings(IList commands)
        {
            var nodes = R.Static(helper, "SerializeCommands", commands, context)!;
            var convert = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
            using var doc = JsonDocument.Parse((string)R.Static(convert, "SerializeObject", nodes)!);
            var bindings = new Dictionary<string, object>(StringComparer.Ordinal);
            void Visit(JsonElement children, string parent)
            {
                var index = 0;
                foreach (var node in children.EnumerateArray())
                {
                    var path = $"{parent}[{index++}]";
                    if (!node.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                        throw Unverified(path, "native command has no readable ID");
                    var found = R.Static(helper, "FindCommandByNodeId", commands, id.GetString());
                    if (found?.GetType().GetField("Item1")?.GetValue(found) is not IList group ||
                        found.GetType().GetField("Item2")?.GetValue(found) is not int position || position < 0 || position >= group.Count || group[position] is not { } command)
                        throw Unverified(path, "native ID did not identify a command");
                    if (command.GetType().GetProperty("ServerPropertiesId", R.Flags)?.GetValue(command) is IDictionary ids)
                        foreach (DictionaryEntry binding in ids)
                            bindings.Add($"{path}.ServerPropertiesId[{binding.Key}]", binding.Value!);
                    if (node.TryGetProperty("commands", out var nested) && nested.ValueKind == JsonValueKind.Array)
                        Visit(nested, path + ".commands");
                    if (node.TryGetProperty("branches", out var branches) && branches.ValueKind == JsonValueKind.Array)
                    {
                        var b = 0;
                        foreach (var branch in branches.EnumerateArray())
                        {
                            if (branch.TryGetProperty("commands", out var inside) && inside.ValueKind == JsonValueKind.Array)
                                Visit(inside, $"{path}.branches[{b}].commands");
                            b++;
                        }
                    }
                }
            }
            Visit(doc.RootElement, "commands");
            return bindings;
        }
        var expected = Bindings(proposed);
        if (expected.Count == 0) return;
        var generator = designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
        var saveData = R.Static(generator, "GetPageSaveData", sheet)!;
        var clone = R.Get(saveData, "PageInfo")!;
        var clonedList = (IList)R.Get(clone, "PageLoadedCommandList")!;
        var live = R.Get(R.Get(sheet, "Tag")!, "PageInfo")!;
        if (ReferenceEquals(clone, live) || ReferenceEquals(clonedList, R.Get(live, "PageLoadedCommandList")))
            throw Unverified(expected.Keys.First(), "the native page save projection was not detached");
        clonedList.Clear();
        foreach (var command in proposed) clonedList.Add(command);
        var loader = R.Get(R.Get(colleague, "Mediator")!, "SaveDataColleague")!;
        var utility = Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!;
        var json = (string)R.Static(utility, "GetJsonStringNoFormat", saveData, false, null, R.Call(loader, "GetJsonSerializerSettings"))!;
        var restored = R.Static(generator, "CreatePageSaveData", json)!;
        var observed = Bindings((IList)R.Get(R.Get(restored, "PageInfo")!, "PageLoadedCommandList")!);
        foreach (var (key, value) in expected)
            if (!observed.TryGetValue(key, out var actual) || !Equals(value, actual))
                throw Unverified(key, "binding changed in native save/readback");
    }

    private static BridgeException Unverified(string path, string reason) => new("CommandTreeRoundTripUnverified", path + ": " + reason);
}
