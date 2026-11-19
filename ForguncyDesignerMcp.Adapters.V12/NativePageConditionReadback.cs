using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForguncyDesignerMcp.Adapters.V12;

internal static class NativePageConditionReadback
{
    private const string CommandType = "Forguncy.Model.ConditionCommand, ServerDesignerCommon";
    private const string GroupType = "ForguncyDataAccess.RelationSqlCondition, ForguncyDataAccess";

    // Called after the native reader has loaded the file and strict DTO comparison failed.
    // Check the signed file's JSON as well: a native reader may discard properties while folding.
    internal static bool MatchesFoldedGroups(string expected, string decoded, string source)
    {
        var baseline = JsonNode.Parse(expected);
        var restored = JsonNode.Parse(decoded);
        FoldBranchConditions(baseline);
        FoldBranchConditions(restored);
        if (!JsonNode.DeepEquals(baseline, restored)) return false;
        JsonNode? stored;
        try { stored = JsonNode.Parse(source, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException) { return false; }
        FoldBranchConditions(stored);
        return JsonNode.DeepEquals(baseline, stored);
    }

    private static void FoldBranchConditions(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var item in array) FoldBranchConditions(item);
        }
        else if (node is JsonObject obj)
        {
            if (IsType(obj, CommandType) && obj["ConditionAndCommandPairList"] is JsonArray branches)
            {
                foreach (var branch in branches.OfType<JsonObject>())
                {
                    if (branch["Condition"] is JsonObject condition)
                    {
                        var folded = Fold(condition);
                        if (!ReferenceEquals(folded, condition)) branch["Condition"] = folded;
                    }
                    FoldBranchConditions(branch["CommandList"]);
                }
                return;
            }
            foreach (var property in obj) FoldBranchConditions(property.Value);
        }
    }

    private static JsonObject Fold(JsonObject node)
    {
        if (!IsType(node, GroupType) || node["SubConditions"] is not JsonArray children) return node;
        for (int i = 0; i < children.Count; i++)
            if (children[i] is JsonObject child)
            {
                var folded = Fold(child);
                if (!ReferenceEquals(folded, child)) children[i] = folded;
            }
        // V12 encodes AND as an omitted RelationType or 0, OR as 1.
        // Removing a unary wrapper is valid only when it carries no other state.
        if (children.Count != 1 || children[0] is not JsonObject only || only["$type"] is not JsonValue ||
            node.Any(p => p.Key is not ("$type" or "SubConditions" or "RelationType"))) return node;
        if (node.TryGetPropertyValue("RelationType", out var relation) &&
            !(relation is JsonValue value && value.TryGetValue<int>(out int kind) && kind is 0 or 1)) return node;
        children.RemoveAt(0);
        return only;
    }

    private static bool IsType(JsonObject node, string name) =>
        node["$type"] is JsonValue value && value.TryGetValue<string>(out var type) && type == name;
}
