namespace ForguncyDesignerMcp.Core.Platform;

public static class NativeServiceApiSchemas
{
    public static System.Text.Json.JsonElement Receipt() => PlatformJson.Element(new
    {
        type = "object", additionalProperties = false,
        properties = new
        {
            operationId = new { type = "string", format = "uuid" }, state = new { type = "string" },
            code = new { type = "string" }, message = new { type = "string" },
            nativeAccepted = new { type = new[] { "boolean", "null" } },
            nativeReadbackVerified = new { type = "boolean" },
            nativeResponse = new { type = new[] { "object", "null" } },
            persistenceVerified = new { type = "boolean" }, sourceExportVerified = new { type = "boolean" },
            executionMayContinue = new { type = "boolean" }, limitation = new { type = new[] { "string", "null" } }
        },
        required = new[] { "operationId", "state", "code", "message", "nativeAccepted", "nativeReadbackVerified", "nativeResponse",
            "persistenceVerified", "sourceExportVerified", "executionMayContinue", "limitation" },
        description = "Native user-service response. native_accepted records native acceptance only; it is not independently verified database persistence or a Saved project export. Unknown evidence retains the original operation ID without replay."
    });
}
