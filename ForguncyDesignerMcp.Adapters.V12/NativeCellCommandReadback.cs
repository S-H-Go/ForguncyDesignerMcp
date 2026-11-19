using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal static class NativeCellCommandReadback
{
    internal static JsonElement RestoreFieldFormulas(Assembly designer, JsonElement result, CancellationToken token)
    {
        if (!result.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array) return result;
        var name = result.GetProperty("pageName").GetString()!;
        object? sheet = null;
        JsonNode? output = null;
        int index = -1;
        foreach (var cell in cells.EnumerateArray())
        {
            index++;
            if (!cell.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array ||
                !cell.TryGetProperty("row", out var row) || !cell.TryGetProperty("col", out var col)) continue;
            var json = commands.GetRawText();
            if (!json.Contains("\"location\"", StringComparison.Ordinal) || !json.Contains('.')) continue;
            var tree = JsonNode.Parse(json)!.AsArray();
            Dictionary<string, object?>? nativeConditions = null;
            bool changed = false;
            foreach (var command in Conditions(tree))
            {
                if (command["branches"] is not JsonArray branches || command["id"] is null) continue;
                for (int b = 0; b < branches.Count; b++)
                {
                    if (branches[b]?["predicate"]?["condition"] is not JsonObject condition) continue;
                    foreach (var (left, indices) in FieldLocations(condition, []))
                    {
                        sheet ??= R.Static(designer.GetType("Forguncy.AI.Actions.PageSandboxAIActions", true)!, "GetWorksheetOrThrow", name)!;
                        nativeConditions ??= IndexConditions(designer, sheet, row.GetInt32(), col.GetInt32(), token);
                        if (!nativeConditions.TryGetValue((string)command["id"]!, out var model) || model is null) continue;
                        var pairs = (IList)R.Get(model, "ConditionAndCommandPairList")!;
                        if (b >= pairs.Count) continue;
                        var actual = R.Get(pairs[b]!, "Condition");
                        if (FindStoredFormula(actual, indices, (string)left["location"]!) is { } formula)
                        {
                            left["location"] = formula;
                            changed = true;
                        }
                    }
                }
            }
            if (!changed) continue;
            output ??= JsonNode.Parse(result.GetRawText());
            output!["cells"]![index]!["commands"] = tree;
        }
        return output is null ? result : JsonSerializer.SerializeToElement(output);
    }

    private static IEnumerable<JsonObject> Conditions(JsonArray commands)
    {
        foreach (var command in commands.OfType<JsonObject>())
        {
            if ((string?)command["type"] == "Condition") yield return command;
            if (command["commands"] is JsonArray body)
                foreach (var nested in Conditions(body)) yield return nested;
            if (command["branches"] is JsonArray branches)
                foreach (var branch in branches.OfType<JsonObject>())
                    if (branch["commands"] is JsonArray children)
                        foreach (var nested in Conditions(children)) yield return nested;
        }
    }

    private static IEnumerable<(JsonObject Left, int[] Indices)> FieldLocations(JsonObject condition, int[] indices)
    {
        if ((string?)condition["type"] == "compare" && condition["left"] is JsonObject left &&
            (string?)left["source"] == "pageElement" && left["location"] is JsonValue value &&
            value.TryGetValue<string>(out var text) && !text.StartsWith('=') && text.Contains('.'))
            yield return (left, indices);
        if (condition["conditions"] is JsonArray children)
            for (int i = 0; i < children.Count; i++)
                if (children[i] is JsonObject child)
                    foreach (var match in FieldLocations(child, [..indices, i])) yield return match;
    }

    private static string? FindStoredFormula(object? actual, int[] indices, string bare)
    {
        foreach (var index in indices)
        {
            if (actual?.GetType().GetProperty("SubConditions")?.GetValue(actual) is not IList children || index >= children.Count) return null;
            actual = children[index];
        }
        if (actual?.GetType().FullName != "Forguncy.Model.IfCondition" ||
            R.Get(actual, "Param") is not { } param || param.GetType().FullName != "Forguncy.Model.IfConditionPageElementParam" ||
            R.Get(param, "ParamElementCellLocations") is not IList { Count: 1 } locations) return null;
        var native = locations[0]!;
        var formula = (string?)R.Get(native, "SerializeProperty");
        // Use the formula held by this exact native operand, never infer one from a dot alone.
        if ((string?)R.Get(native, "Name") == bare && formula?.StartsWith('=') == true &&
            (formula == "=" + bare || formula.EndsWith("!" + bare, StringComparison.Ordinal))) return formula;
        return null;
    }

    private static Dictionary<string, object?> IndexConditions(Assembly designer, object sheet, int row, int col, CancellationToken token)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var tag = R.Call(sheet, "GetTag", row, col);
        if (tag is null || R.Get(tag, "CellType") is not { } cellType) return result;
        var contract = cellType.GetType().GetInterface("GrapeCity.Forguncy.CellTypes.ICommandCellType");
        if (contract?.GetProperty("CommandList")?.GetValue(cellType) is not IList roots) return result;
        var pending = new Stack<object>(roots.Cast<object>());
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var helper = designer.GetType("Forguncy.AI.Actions.CommandTreeHelper", true)!;
        var nested = designer.GetType("acx", true)!;
        while (pending.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(node)) continue;
            if (node.GetType().FullName == "Forguncy.Model.ConditionCommand")
            {
                var id = (string)R.Static(helper, "GetCommandNodeId", node)!;
                if (!result.TryAdd(id, node)) result[id] = null;
            }
            foreach (var child in (IEnumerable)R.Static(nested, "GetContainedSubCommands", node, token)!)
                pending.Push(child);
        }
        return result;
    }
}
