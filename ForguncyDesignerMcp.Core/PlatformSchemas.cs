using System.Text.Json;
using System.Text.Json.Nodes;

namespace ForguncyDesignerMcp.Core.Platform;

// Schemas describe the actual frozen DTO shapes. No adapter CLR types or native
// document handles are accepted through this wire boundary.
internal static class PlatformSchemas
{
    private static object Text(string? description = null) => new { type = "string", minLength = 1, description };
    private static object Nullable(object schema) => new { anyOf = new[] { schema, new { type = "null" } } };
    private static object Choice<T>() where T : struct, Enum => new { type = "string", @enum = Enum.GetNames<T>() };
    private static object Object(Dictionary<string, object> properties, params string[] required) =>
        new { type = "object", properties, required, additionalProperties = false };
    private static object Target() => Object(new() {
        ["instanceId"] = Text("Exact registry instance ID"), ["sessionId"] = Text("Exact open document session"),
        ["projectPath"] = Text("Absolute project path"), ["designerVersion"] = Text("Exact four-part build")
    }, "instanceId", "sessionId", "projectPath", "designerVersion");
    private static object Address() => Object(new() {
        ["kind"] = Choice<ModuleKind>(), ["name"] = Text("Exact native module name"), ["folderPath"] = Nullable(Text())
    }, "kind", "name");
    private static object Document() => Object(new() {
        ["address"] = Address(), ["metadata"] = new { }, ["revision"] = Text(),
        ["companions"] = new { type = "array", items = Object(new() {
            ["relativePath"] = Text("Only native model asset paths; never arbitrary OS paths"),
            ["contentBase64"] = new { type = "string", contentEncoding = "base64" },
            ["sha256"] = Text(), ["mediaType"] = Nullable(Text())
        }, "relativePath", "contentBase64", "sha256") }
    }, "address", "metadata", "companions", "revision");
    private static object Mutation(string? operation) => RequireAccountBinding(Object(new() {
        ["operationId"] = new { type = "string", format = "uuid" }, ["target"] = Target(),
        ["operation"] = operation is null ? Choice<ModuleOperation>() : new { type = "string", @enum = new[] { operation } },
        ["address"] = Address(), ["expectedProjectRevision"] = Text("Use platform project.snapshot revision, not legacy designer.get_context"),
        ["expectedModuleRevision"] = Nullable(Text("Required for update/delete")), ["document"] = Nullable(Document()),
        ["save"] = new { type = "boolean", @default = true }, ["scope"] = Choice<EffectScope>(),
        ["expectedServiceBinding"] = Nullable(PlatformApiSchemas.ServiceBinding),
        ["expectedUser"] = Nullable(Object(new() { ["userName"] = Text(), ["email"] = Text() }, "userName", "email"))
    }, "operationId", "target", "operation", "address", "expectedProjectRevision"));

    private static object RequireAccountBinding(object input)
    {
        var schema = JsonSerializer.SerializeToNode(input, PlatformJson.Options)!.AsObject();
        schema["allOf"] = JsonSerializer.SerializeToNode(new[] { new Dictionary<string, object>
        {
            ["if"] = new { properties = new { scope = new { @const = "AccountService" } }, required = new[] { "scope" } },
            ["then"] = new { required = new[] { "expectedServiceBinding" }, properties = new { expectedServiceBinding = PlatformApiSchemas.ServiceBinding } }
        } }, PlatformJson.Options);
        return schema;
    }

    internal static JsonElement Input(string ns, string method)
    {
        if (ns == "collaboration") return CollaborationSchemas.Input(method);
        if (ns == "catalog") return CatalogInput(method);
        if (ns == "project" && method == "validate_api") return PlatformJson.Element(Object(new() {
            ["namespace"] = Text(), ["method"] = Text(), ["parameters"] = new { type = "object", description = "Proposed native API parameters, including the real mutation guard." }
        }, "namespace", "method", "parameters"));
        return PlatformJson.Element(ns == "module" ? method switch
    {
        "list" => Object(new() { ["kind"] = Choice<ModuleKind>(), ["folderPath"] = Nullable(Text()),
            ["cursor"] = Nullable(Text()), ["limit"] = new { type = "integer", minimum = 1, maximum = 1000, @default = 100 } }, "kind"),
        "read" or "references" or "findReferences" => Address(),
        "create" => Mutation("Create"), "update" => Mutation("Update"), "delete" => Mutation("Delete"),
        "validate" => Mutation(null),
        "save" => Object(new() { ["operationId"] = new { type = "string", format = "uuid" }, ["target"] = Target(),
            ["expectedProjectRevision"] = Text(), ["expectedServiceBinding"] = Nullable(PlatformApiSchemas.ServiceBinding)
        }, "operationId", "target", "expectedProjectRevision"),
        _ => Object(new())
    } : method is "get_operation" or "cancel_operation"
        ? Object(new() { ["operationId"] = new { type = "string", format = "uuid" } }, "operationId") : Object(new()));
    }

    private static JsonElement CatalogInput(string method)
    {
        var properties = new Dictionary<string, object>
        {
            ["namespace"] = Nullable(new { type = "string", minLength = 1, maxLength = 512, description = "Exact case-sensitive namespace; nested namespaces are separate." }),
            ["method"] = Nullable(new { type = "string", minLength = 1, maxLength = 512, description = "Exact case-sensitive method name." })
        };
        if (method == "page")
        {
            properties["limit"] = new { type = "integer", minimum = 1, maximum = PlatformCatalogQueries.MaximumLimit, @default = PlatformCatalogQueries.DefaultLimit };
            properties["cursor"] = Nullable(new { type = "string", minLength = 1, maxLength = 16_384, description = "Opaque nextCursor from the same router, final catalog, exact filters and limit." });
        }
        return PlatformJson.Element(Object(properties));
    }

    internal static JsonElement CatalogOutput(string method)
    {
        var descriptor = Object(new()
        {
            ["namespace"] = new { type = "string" }, ["method"] = new { type = "string" },
            ["inputSchema"] = new { }, ["outputSchema"] = new { },
            ["mutatesProject"] = new { type = "boolean" }, ["scope"] = Choice<EffectScope>(),
            ["documentation"] = Nullable(new { type = "string" })
        }, "namespace", "method", "inputSchema", "outputSchema", "mutatesProject", "scope", "documentation");
        var items = new { type = "array", items = descriptor };
        return PlatformJson.Element(method == "list" ? (object)items : Object(new()
        {
            ["items"] = items, ["nextCursor"] = Nullable(new { type = "string" }),
            ["catalogRevision"] = new { type = "string" }, ["totalCount"] = new { type = "integer", minimum = 0 }
        }, "items", "nextCursor", "catalogRevision", "totalCount"));
    }
}
