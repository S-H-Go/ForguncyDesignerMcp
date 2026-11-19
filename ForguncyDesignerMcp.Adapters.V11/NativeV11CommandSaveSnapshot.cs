using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForguncyDesignerMcp.Adapters.V11;

// Freezes the command owner's native memento. PendingSave separately owns
// the source baseline, native save and exact staged/source readback.
internal sealed class NativeV11CommandSaveSnapshot
{
    private readonly Assembly designer;
    private readonly NativeV11Route route;
    private readonly object model;
    private readonly string key;
    private readonly JsonElement cache;

    private NativeV11CommandSaveSnapshot(Assembly designer, NativeV11Route route, object model, string key, JsonElement cache)
    { this.designer = designer; this.route = route; this.model = model; this.key = key; this.cache = cache; }

    internal static NativeV11CommandSaveSnapshot Capture(Assembly designer, NativeV11Route route, object model, string expectedPath)
    {
        if (route.Kind is not (ForguncyDesignerMcp.Core.Platform.ModuleKind.ServerCommand or ForguncyDesignerMcp.Core.Platform.ModuleKind.SharedCommand or ForguncyDesignerMcp.Core.Platform.ModuleKind.ScheduledTask))
            throw new InvalidDataException("Only command definitions have this native save-cache contract.");
        var key = NativeV11Reflection.Call(route.Colleague, "GetKey", model) as string ?? throw new InvalidDataException("Native command save key is missing.");
        if (!key.Replace('\\', '/').Equals(expectedPath.Replace('\\', '/'), StringComparison.Ordinal) || SaveItems(route)[key] is not object cached)
            throw new InvalidDataException("The native command save cache does not own the requested model key.");
        var snapshot = new NativeV11CommandSaveSnapshot(designer, route, model, key, NativeV11SourceMetadata.SerializeSaveData(designer, route, cached));
        snapshot.Verify(model);
        return snapshot;
    }

    internal void Verify(object currentModel)
    {
        if (!ReferenceEquals(model, currentModel) || SaveItems(route)[key] is not object current ||
            !V11Safety.Equivalent(cache, NativeV11SourceMetadata.SerializeSaveData(designer, route, current)) ||
            !EquivalentCommandProjection(cache.GetRawText(), NativeV11SourceMetadata.Serialize(designer, route, currentModel).GetRawText()))
            throw new InvalidDataException("The native command model or frozen save cache changed.");
    }

    internal bool MatchesSource(string source, string staged) =>
        V11Safety.Equivalent(cache, V11Safety.Body(System.Text.Encoding.UTF8.GetBytes(source))) &&
        V11Safety.Equivalent(cache, V11Safety.Body(System.Text.Encoding.UTF8.GetBytes(staged)));

    internal bool MatchesSource(byte[] source, byte[] staged) => V11Safety.Equivalent(cache, V11Safety.Body(source)) && V11Safety.Equivalent(cache, V11Safety.Body(staged));

    internal static bool ContainsCondition(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (value.TryGetProperty("$type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "Forguncy.Model.ConditionCommand, ServerDesignerCommon") return true;
            return value.EnumerateObject().Any(item => ContainsCondition(item.Value));
        }
        return value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(ContainsCondition);
    }

    internal static IDictionary SaveItems(NativeV11Route route)
    {
        var memento = NativeV11Reflection.Get(route.Loader, "Memento") ?? throw new InvalidDataException("The native command save memento is missing.");
        var data = NativeV11Reflection.Call(route.Loader, "GetSaveData", memento) ?? throw new InvalidDataException("The native command save data is missing.");
        return data.GetType().GetField("SaveDataItems", NativeV11Reflection.Flags)?.GetValue(data) as IDictionary
            ?? throw new InvalidDataException("The native command save item collection is missing.");
    }

    // Loader settings use TypeNameHandling.All, so typed collections are
    // represented as { "$type": ..., "$values": [...] }. Never use this
    // projection to compare a source/staged file against the frozen memento.
    internal static bool EquivalentCommandProjection(string left, string right)
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
                    type == "Forguncy.Model.ConditionCommand, ServerDesignerCommon")
                {
                    var raw = value["ConditionAndCommandPairList"];
                    var pairs = raw is JsonArray array ? array : (raw as JsonObject)?["$values"] as JsonArray;
                    if (pairs is null) valid = false;
                    else foreach (var pair in pairs)
                    {
                        if (pair is not JsonObject entry || entry["ID"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) ||
                            !Guid.TryParse(id, out _) || !ids.Add(id)) { valid = false; continue; }
                        entry["ID"] = "<native-condition-pair-clone-id>";
                    }
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
