using System.Text.Json.Nodes;

namespace ForguncyDesignerMcp.Adapters.V12;

// Native ConditionAndCommandPair.Clone constructs a new GUID instead of
// copying ID. Compare only that exact typed clone field semantically, and
// reject any occurrence of its ID elsewhere in the same native projection.
internal static class NativeConditionPairProjection
{
    internal static bool Equivalent(string left,string right)
    {
        try
        {
            var a=Project(left); var b=Project(right);
            return a is not null && b is not null && JsonNode.DeepEquals(a,b);
        }
        catch(System.Text.Json.JsonException) { return false; }
    }
    // Only used between two save DTOs. The caller checks actual live IDs
    // separately and compares decoded source IDs strictly to the native cache.
    internal static bool SamePageSaveData(string left, string right)
    {
        var a = Project(left, pageSaveData: true);
        var b = Project(right, pageSaveData: true);
        return a is not null && b is not null && JsonNode.DeepEquals(a, b);
    }

    private static JsonNode? Project(string json, bool pageSaveData = false)
    {
        var root=JsonNode.Parse(json); if(root is null) return null;
        var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase); var valid=true;
        void Collect(JsonNode? node)
        {
            if(node is JsonObject value)
            {
                if(value["$type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var type) &&
                    type=="Forguncy.Model.ConditionCommand, ServerDesignerCommon" && value["ConditionAndCommandPairList"] is JsonArray pairs)
                    foreach(var pair in pairs)
                    {
                        if(pair is not JsonObject entry) {valid=false; continue;}
                        bool HasText(string key)=>entry[key] is not null && (entry[key] is not JsonValue textValue || !textValue.TryGetValue<string>(out var text) || !string.IsNullOrEmpty(text));
                        if(!pageSaveData && (HasText("JSText") || HasText("FunctionNameForJS") || entry["CommandList"] is not JsonArray commands || commands.Count!=1 ||
                            commands[0] is not JsonObject command || command["$type"] is not JsonValue commandType ||
                            !commandType.TryGetValue<string>(out var commandName) || commandName!="Forguncy.Model.NavigateCommand, ServerDesignerCommon")) continue;
                        if(entry["ID"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || !Guid.TryParse(id,out _) || !ids.Add(id))
                        { valid=false; continue; }
                        entry["ID"]="<native-condition-pair-clone-id>";
                    }
                foreach(var property in value.ToArray()) Collect(property.Value);
            }
            else if(node is JsonArray values) foreach(var element in values) Collect(element);
        }
        Collect(root);
        void Check(JsonNode? node)
        {
            if(node is JsonValue value && value.TryGetValue<string>(out var text) && ids.Any(id=>text.Contains(id,StringComparison.OrdinalIgnoreCase))) valid=false;
            else if(node is JsonObject obj) foreach(var property in obj) Check(property.Value);
            else if(node is JsonArray array) foreach(var item in array) Check(item);
        }
        Check(root); return valid?root:null;
    }
}
