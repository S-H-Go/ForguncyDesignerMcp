using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed class PageCommandTreePreflight(Assembly designer, string pageName)
{
    private readonly HashSet<string> validLocations = new(StringComparer.Ordinal);
    private object? parseContext;
    private object? validationContext;
    internal static bool AppliesTo(string ns, string method) => ns == "page" && method is "setCells" or "updateCells";

    // Only inspect command envelopes. Native generic conversion can instantiate plugin types
    // and run their property converters, so it cannot safely be used as a dry run.
    internal void Validate(JsonElement cells)
    {
        if (cells.ValueKind != JsonValueKind.Array) throw Invalid("cells", "must be an array");
        int index = 0;
        foreach (var cell in cells.EnumerateArray())
        {
            if (cell.ValueKind != JsonValueKind.Object) throw Invalid($"cells[{index}]", "must be a cell object");
            ValidateCommands(cell, $"cells[{index}]");
            index++;
        }
    }

    private void ValidateCommands(JsonElement owner, string path)
    {
        if (!owner.TryGetProperty("commands", out var commands) || commands.ValueKind == JsonValueKind.Null) return;
        if (commands.ValueKind != JsonValueKind.Array) throw Invalid(path + ".commands", "must be an array");
        int index = 0;
        foreach (var node in commands.EnumerateArray())
        {
            var nodePath = $"{path}.commands[{index++}]";
            if (node.ValueKind != JsonValueKind.Object) throw Invalid(nodePath, "must be a command object");
            if (!node.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
                throw Invalid(nodePath + ".type", "must name a command");
            switch (type.GetString())
            {
                case "Condition":
                    ValidateBranches(node, nodePath, condition: true);
                    break;
                case "CatchException":
                    ValidateBranches(node, nodePath, condition: false);
                    break;
                default:
                    // Native sublist commands put their child command list on the envelope.
                    // Plugin-specific properties and literal JSON values are opaque.
                    ValidateCommands(node, nodePath);
                    break;
            }
        }
    }

    private void ValidateBranches(JsonElement node, string path, bool condition)
    {
        if (!node.TryGetProperty("branches", out var branches) || branches.ValueKind != JsonValueKind.Array || branches.GetArrayLength() == 0)
        {
            if (condition) throw Invalid(path + ".branches", "requires a nonempty array");
            return;
        }
        int index = 0;
        foreach (var branch in branches.EnumerateArray())
        {
            var branchPath = $"{path}.branches[{index++}]";
            if (branch.ValueKind != JsonValueKind.Object) throw Invalid(branchPath, "must be an object");
            if (condition)
            {
                if (!branch.TryGetProperty("predicate", out var predicate) || predicate.ValueKind != JsonValueKind.Object)
                    throw Invalid(branchPath + ".predicate", "requires a predicate object");
                if (!predicate.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(type.GetString()))
                    throw Invalid(branchPath + ".predicate.type", "requires a predicate type");
                if (branch.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String &&
                    (key.GetString() == "else") != string.Equals(type.GetString()!.Trim(), "else", StringComparison.OrdinalIgnoreCase))
                    throw Invalid(branchPath + ".predicate.type", "must be 'else' exactly when the branch key is 'else'");
                if (string.Equals(type.GetString(), "logic", StringComparison.OrdinalIgnoreCase) &&
                    predicate.TryGetProperty("condition", out var expression))
                    ValidateLogicCondition(expression, branchPath + ".predicate.condition");
            }
            ValidateCommands(branch, branchPath);
        }
    }

    private void ValidateLogicCondition(JsonElement condition, string path)
    {
        if (condition.ValueKind != JsonValueKind.Object || !condition.TryGetProperty("type", out var kind) ||
            kind.ValueKind != JsonValueKind.String) return;
        switch (kind.GetString()!.ToLowerInvariant())
        {
            case "and":
            case "or":
                if (condition.TryGetProperty("conditions", out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var child in children.EnumerateArray())
                        ValidateLogicCondition(child, $"{path}.conditions[{index++}]");
                }
                break;
            case "compare":
                if (condition.TryGetProperty("left", out var left) && left.ValueKind == JsonValueKind.Object &&
                    left.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String &&
                    string.Equals(source.GetString(), "pageElement", StringComparison.OrdinalIgnoreCase) &&
                    left.TryGetProperty("location", out var location) && location.ValueKind == JsonValueKind.String)
                    ValidatePageElementLocation(location, path + ".left.location");
                break;
        }
    }

    private void ValidatePageElementLocation(JsonElement location, string path)
    {
        var text = location.GetString()!;
        if (validLocations.Contains(text)) return;
        // Use the scalar conversion from V12's condition reader and the same flags
        // as IfCondition.AIValidateAsync. No command body or plugin converter runs.
        parseContext ??= NativeReflection.Static(designer.GetType("aaw", true)!, "ForPage", pageName)!;
        validationContext ??= NativeReflection.Static(designer.GetType("acq", true)!, "ForPage", pageName)!;
        var scalar = Activator.CreateInstance(Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Linq.JValue", true)!, text);
        var parsed = NativeReflection.Static(designer.GetType("aba", true)!, "ghz", scalar, parseContext);
        if (parsed is null) throw Invalid(path, "requires a page element location");
        var message = (string?)NativeReflection.Call(validationContext, "GetCellLocationValidationMessage", parsed, true, false);
        if (!string.IsNullOrEmpty(message))
            throw Invalid(path, message + " For a record field expression, use a formula such as =Record.Field");
        validLocations.Add(text);
    }

    private static BridgeException Invalid(string path, string detail) =>
        new("InvalidCommandTree", $"Command-tree preflight: {path} {detail}. Correct the input and submit with a new operationId.");
}
