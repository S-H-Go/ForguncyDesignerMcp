using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// The pinned V12 parser leaves a nested UpdateListview column string as a string,
// even if it looks like a formula. Check that exact native string predicate before
// native write bookkeeping. Never parse arbitrary command or plugin properties.
internal static class NativeListViewColumnPreflight
{
    internal readonly record struct Column(string Path, string ListName, string Value);

    internal static void Validate(Assembly designer, string ns, string method, JsonElement parameters)
    {
        if (ns != "page" || method != "updateCells" ||
            !parameters.TryGetProperty("pageName", out var name) || name.ValueKind != JsonValueKind.String ||
            !parameters.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array) return;
        var columns = Find(cells);
        if (columns.Count == 0) return;
        var page = name.GetString()!;
        var context = R.Static(designer.GetType("acq", true)!, "ForPage", page)!;
        var plugin = Assembly.Load("GrapeCity.Forguncy.Plugin");
        var contextType = plugin.GetType("GrapeCity.Forguncy.Plugin.IAIValidateContext", true)!;
        var inspectorType = plugin.GetType("GrapeCity.Forguncy.Plugin.IAIListViewInspector", true)!;
        var inspector = contextType.GetProperty("ListViewInspector")!.GetValue(context);
        var lookup = inspectorType.GetMethod("TryGetListViewInfo")!;
        if (inspector is null) return;
        var lists = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (!lists.TryGetValue(column.ListName, out var list))
            {
                object?[] args = [page, column.ListName, null];
                list = lookup.Invoke(inspector, args) is true ? args[2] : null;
                lists.Add(column.ListName, list);
            }
            // Unresolved native list owners stay on the existing validation route.
            if (list is not null) RequireNativeColumn(column, context, list);
        }
    }

    internal static void RequireNativeColumn(Column column, object context, object list)
    {
        var command = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.UpdateListviewCommand", true)!;
        var check = command.GetMethod("IsValidListViewColumnForAI", R.Flags)!;
        var receiver = check.IsStatic ? null : Activator.CreateInstance(command);
        if (check.Invoke(receiver, [column.Value, context, list]) is false)
            throw new BridgeException("InvalidCommandTree",
                column.Path + ": this V12 page update reads a formula-shaped column as a name, and that name is not a column of the target native list view. No cell update was dispatched.");
    }

    internal static IReadOnlyList<Column> Find(JsonElement cells)
    {
        var result = new List<Column>();
        void Visit(JsonElement owner, string path)
        {
            if (!owner.TryGetProperty("commands", out var commands) || commands.ValueKind != JsonValueKind.Array) return;
            int i = 0;
            foreach (var node in commands.EnumerateArray())
            {
                var here = $"{path}.commands[{i++}]";
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var kind) ||
                    kind.ValueKind != JsonValueKind.String) continue;
                if (kind.GetString() is "Condition" or "CatchException")
                {
                    if (!node.TryGetProperty("branches", out var branches) || branches.ValueKind != JsonValueKind.Array) continue;
                    int b = 0;
                    foreach (var branch in branches.EnumerateArray())
                    {
                        var branchPath = $"{here}.branches[{b++}]";
                        if (branch.ValueKind == JsonValueKind.Object) Visit(branch, branchPath);
                    }
                    continue;
                }
                if (kind.GetString() == "UpdateListview" &&
                    node.TryGetProperty("props", out var props) && props.ValueKind == JsonValueKind.Object &&
                    props.TryGetProperty("listviewName", out var list) && list.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(list.GetString()) &&
                    props.TryGetProperty("needUpdateValues", out var enabled) && enabled.ValueKind == JsonValueKind.True &&
                    props.TryGetProperty("updateValues", out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    int j = 0;
                    foreach (var value in values.EnumerateArray())
                    {
                        var columnPath = $"{here}.props.updateValues[{j++}].column";
                        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("column", out var col) &&
                            col.ValueKind == JsonValueKind.String && col.GetString() is { } raw && raw.StartsWith('='))
                            result.Add(new(columnPath, list.GetString()!, raw));
                    }
                }
                Visit(node, here);
            }
        }
        int n = 0;
        foreach (var cell in cells.EnumerateArray())
        {
            var path = $"cells[{n++}]";
            if (cell.ValueKind == JsonValueKind.Object) Visit(cell, path);
        }
        return result;
    }
}
