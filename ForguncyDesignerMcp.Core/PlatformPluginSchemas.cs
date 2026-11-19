using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

public static class PluginLifecycleSchemas
{
    public static JsonElement Input(string method)
    {
        var text = new { type = "string" }; var nullable = new { type = new[] { "string", "null" } };
        var properties = new Dictionary<string,object>(); var required = new List<string>();
        if (method == "read") { properties["pluginGuid"] = new { type = "string", format = "uuid" }; required.Add("pluginGuid"); }
        else if (method != "list")
        {
            var module = PlatformSchemas.Input("module", "create").GetProperty("properties");
            properties["operationId"] = new { type = "string", format = "uuid" };
            properties["target"] = module.GetProperty("target").Clone();
            properties["expectedProjectRevision"] = text; properties["expectedUser"] = module.GetProperty("expectedUser").Clone();
            properties["operation"] = new { type = "string", @enum = new[] { char.ToUpperInvariant(method[0]) + method[1..] } };
            properties["pluginGuid"] = new { type = "string", format = "uuid" };
            properties["expectedInstalledVersion"] = nullable; properties["packageBase64"] = nullable;
            properties["packageSha256"] = nullable; properties["packageVersion"] = nullable;
            required.AddRange(["operationId", "target", "expectedProjectRevision", "expectedUser", "operation", "pluginGuid"]);
            if (method != "create") required.Add("expectedInstalledVersion");
            if (method != "delete") required.AddRange(["packageBase64", "packageSha256", "packageVersion"]);
        }
        return PlatformJson.Element(new { type = "object", additionalProperties = false, properties, required });
    }
    public static JsonElement Output(string method)
    {
        var text = new { type = "string" }; var flag = new { type = "boolean" }; var nullable = new { type = new[] { "string", "null" } };
        if (method is "read" or "list")
        {
            var item = new { type = "object", additionalProperties = false, properties = new {
                pluginGuid = new { type = "string", format = "uuid" }, version = text, name = text,
                isLoaded = new { type = new[] { "boolean", "null" } }, isUsedByCurrentProject = new { type = new[] { "boolean", "null" } } },
                required = new[] { "pluginGuid", "version", "name", "isLoaded", "isUsedByCurrentProject" } };
            return method == "read" ? PlatformJson.Element(item) : PlatformJson.Element(new { type = "array", items = item });
        }
        var receipt = PlatformJson.Element(new { type = "object", additionalProperties = false, properties = new {
            operationId = new { type = "string", format = "uuid" }, state = text, code = text, message = text,
            nativeAccepted = new { type = new[] { "boolean", "null" } }, registryVerified = flag, restartRequired = flag,
            runtimeActivationVerified = flag, executionMayContinue = flag, observedGuid = nullable, observedVersion = nullable, limitation = nullable },
            required = new[] { "operationId", "state", "code", "message", "nativeAccepted", "registryVerified", "restartRequired",
                "runtimeActivationVerified", "executionMayContinue", "observedGuid", "observedVersion", "limitation" } });
        return PlatformApiSchemas.OperationStatus(receipt);
    }
}
