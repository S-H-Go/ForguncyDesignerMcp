using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForguncyDesignerMcp.Adapters.V11;

// Only for comparing a native page model's cloned DTO with its own exact
// native save-cache DTO. Source and staged DTOs must still match the cached
// DTO including the actual IDs; this is never a source-file equivalence rule.
internal static class NativeV11ConditionPairProjection
{
    internal static bool Equivalent(string left, string right)
    {
        try { var a = Project(left); var b = Project(right); return a is not null && b is not null && JsonNode.DeepEquals(a, b); }
        catch (JsonException) { return false; }
    }

    private static JsonNode? Project(string json)
    {
        var root = JsonNode.Parse(json); if (root is null) return null;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var valid = true;
        void Collect(JsonNode? node)
        {
            if (node is JsonObject value)
            {
                if (value["$type"] is JsonValue typed && typed.TryGetValue<string>(out var type) &&
                    type == "Forguncy.Model.ConditionCommand, ServerDesignerCommon" && value["ConditionAndCommandPairList"] is JsonArray pairs)
                    foreach (var pair in pairs)
                    {
                        if (pair is not JsonObject entry || entry["ID"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
                            !Guid.TryParse(id, out _) || !ids.Add(id)) { valid = false; continue; }
                        entry["ID"] = "<native-condition-pair-clone-id>";
                    }
                foreach (var property in value.ToArray()) Collect(property.Value);
            }
            else if (node is JsonArray values) foreach (var item in values) Collect(item);
        }
        Collect(root);
        void Check(JsonNode? node)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && ids.Any(id => text.Contains(id, StringComparison.OrdinalIgnoreCase))) valid = false;
            else if (node is JsonObject obj) foreach (var property in obj) Check(property.Value);
            else if (node is JsonArray array) foreach (var item in array) Check(item);
        }
        Check(root); return valid ? root : null;
    }
}
