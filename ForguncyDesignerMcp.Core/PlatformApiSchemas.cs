using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForguncyDesignerMcp.Core.Platform;

public static class PlatformApiSchemas
{
    private static object Shape(Dictionary<string, object> properties, params string[] required) =>
        new { type = "object", properties, required, additionalProperties = false };
    private static object Nullable(object schema) => new { anyOf = new object[] { schema, new { type = "null" } } };
    private static object Text() => new { type = "string" };
    private static object Flag() => new { type = "boolean" };

    public static JsonElement ModuleMutationReceipt()
    {
        var address = PlatformSchemas.Input("module", "read");
        var asset = Shape(new()
        {
            ["relativePath"] = Text(), ["contentBase64"] = new { type = "string", contentEncoding = "base64" },
            ["sha256"] = Text(), ["mediaType"] = Nullable(Text())
        }, "relativePath", "contentBase64", "sha256", "mediaType");
        var document = Shape(new()
        {
            ["address"] = address, ["metadata"] = new { }, ["companions"] = new { type = "array", items = asset }, ["revision"] = Text()
        }, "address", "metadata", "companions", "revision");
        var saveResult = Shape(new()
        {
            ["nativeSaveAccepted"] = Flag(), ["containsUnsavedChanges"] = Flag(), ["sourceVerified"] = Flag(), ["evidenceDirectory"] = Nullable(Text()),
            ["savedSourceSha256"] = Nullable(new { type = "string", pattern = "^[A-Fa-f0-9]{64}$" }),
            ["verificationFailure"] = Nullable(Shape(new()
            {
                ["stage"] = Text(), ["fileRole"] = Text(), ["code"] = Text()
            }, "stage", "fileRole", "code"))
        }, "nativeSaveAccepted", "containsUnsavedChanges", "sourceVerified", "evidenceDirectory", "savedSourceSha256");
        var serviceResult = Shape(new()
        {
            ["binding"] = ServiceBinding, ["entity"] = address, ["nativeAccepted"] = Nullable(Flag()),
            ["persistenceVerified"] = Flag(), ["readbackVerified"] = Flag(), ["entityPresent"] = Nullable(Flag()),
            ["observedRevision"] = Nullable(Text()), ["evidenceDirectory"] = Nullable(Text())
        }, "binding", "entity", "nativeAccepted", "persistenceVerified", "readbackVerified", "entityPresent", "observedRevision", "evidenceDirectory");
        return PlatformJson.Element(Shape(new()
        {
            ["operationId"] = Text(), ["state"] = Text(), ["code"] = Text(), ["message"] = Text(),
            ["document"] = Nullable(document), ["saveResult"] = Nullable(saveResult),
            ["changedFiles"] = Nullable(new { type = "array", items = Text() }), ["executionMayContinue"] = Flag(),
            ["serviceResult"] = Nullable(serviceResult)
        }, "operationId", "state", "code", "message", "document", "saveResult", "changedFiles", "executionMayContinue", "serviceResult"));
    }

    public static JsonElement ServiceBinding => PlatformJson.Element(new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            identity = new { type = "string", minLength = 1, pattern = "\\S", description = "Opaque verified effective account-service generation/configuration fingerprint; never credentials." },
            revision = new { type = "string", minLength = 1, pattern = "\\S", description = "Authoritative consistent semantic data revision from the same service binding." }
        },
        required = new[] { "identity", "revision" }
    });

    public static JsonElement MutationGuard => PlatformJson.Element(new
    {
        type = "object", additionalProperties = false,
        properties = new Dictionary<string, object>
        {
            ["operationId"] = new { type = "string", format = "uuid" },
            ["target"] = new { type = "object", additionalProperties = false, properties = new {
                instanceId = new { type = "string" }, sessionId = new { type = "string" },
                projectPath = new { type = "string" }, designerVersion = new { type = "string" } },
                required = new[] { "instanceId", "sessionId", "projectPath", "designerVersion" } },
            ["expectedProjectRevision"] = new { type = "string", minLength = 1 },
            ["expectedUser"] = new { anyOf = new object[] { new { type = "object", properties = new {
                userName = new { type = "string" }, email = new { type = "string" } }, required = new[] { "userName", "email" } }, new { type = "null" } } },
            ["address"] = new { anyOf = new object[] { PlatformSchemas.Input("module", "read"), new { type = "null" } } },
            ["expectedModuleRevision"] = new { type = new[] { "string", "null" } },
            ["expectedServiceBinding"] = new { anyOf = new object[] { ServiceBinding, new { type = "null" } } },
            ["scope"] = new { type = "string", @enum = Enum.GetNames<EffectScope>(), @default = "ProjectDefinition" }
        },
        required = new[] { "operationId", "target", "expectedProjectRevision" }
    });

    // Idempotent: adapters may expose the same canonical guard in their own
    // catalog and the router can safely ensure it again for actual tool schemas.
    public static JsonElement AddMutationGuard(JsonElement nativeSchema)
    {
        if (nativeSchema.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidApiSchema", "Native API input schema must be an object.");
        var schema = JsonNode.Parse(nativeSchema.GetRawText())!.AsObject();
        if (schema["type"] is { } type && type.GetValue<string>() != "object")
            throw new BridgeException("InvalidApiSchema", "Guarded native APIs require an object parameter schema.");
        schema["type"] = "object";
        if (schema["properties"] is null) schema["properties"] = new JsonObject();
        schema["properties"]!.AsObject()["guard"] = JsonNode.Parse(MutationGuard.GetRawText());
        if (schema["required"] is null) schema["required"] = new JsonArray();
        var required = schema["required"]!.AsArray();
        if (!required.Any(x => x?.GetValue<string>() == "guard")) required.Add("guard");
        return PlatformJson.Element(schema);
    }

    public static JsonElement OperationStatus(JsonElement? nativeResultSchema = null) => PlatformJson.Element(new
    {
        type = "object",
        properties = new Dictionary<string, object>
        {
            ["operationId"] = new { type = "string", format = "uuid" }, ["state"] = new { type = "string" },
            ["code"] = new { type = "string" }, ["message"] = new { type = "string" },
            ["scope"] = new { type = "string", @enum = Enum.GetNames<EffectScope>() },
            ["executionMayContinue"] = new { type = "boolean" },
            ["result"] = new { anyOf = new object[] { nativeResultSchema is { } schema ? schema : new { }, new { type = "null" } } }
        },
        required = new[] { "operationId", "state", "code", "message", "scope", "executionMayContinue" },
        description = "Queued/running/terminal Host receipt. Query project.get_operation with the original ID. Terminal result retains the native payload."
    });
}
