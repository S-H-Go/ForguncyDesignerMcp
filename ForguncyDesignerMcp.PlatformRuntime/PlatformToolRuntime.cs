using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.PlatformRuntime;

/// <summary>Own MCP tools, JS interpreter and generated VFS; independent of the vendor MCP implementation.</summary>
public sealed class PlatformToolRuntime : IPlatformToolRuntime
{
    private readonly RuntimeLimits limits;
    private readonly SemaphoreSlim executionSlots;
    public PlatformToolRuntime(RuntimeLimits? limits = null)
    {
        this.limits = limits ?? new RuntimeLimits();
        this.limits.Validate();
        executionSlots = new SemaphoreSlim(this.limits.MaxConcurrentExecutions);
    }

    public Task<JsonElement> ListToolsAsync(PlatformRuntimeContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context = PlatformContractSchemas.Enrich(context);
        var tools = new List<object>
        {
            Tool("execute_code", "Run JavaScript with top-level await. Only catalog APIs and logging/assert primitives exist. A dispatched write may continue after cancellation; do not retry uncertain writes.",
                Schema(new() { ["code"] = new { type = "string", minLength = 1, maxLength = limits.MaxCodeChars }, ["title"] = Text(), ["readOnly"] = Boolean() }, "code"), false),
            Tool("get_project_structure", "List native module directories and names. Set includeTableDetails=false to skip reading each table's columns and comment. Defaults to true.",
                Schema(new() { ["scope"] = Text(), ["folderPath"] = Text(), ["includeTableDetails"] = Boolean() })),
            Tool("get_document_content", "Read real module JSON; RFC 9535 JSONPath, depth projection or 1-based chunk mode. No script/eval JSONPath extensions.",
                Schema(new() { ["documentType"] = Text(), ["documentName"] = Text(), ["folderPath"] = Text(), ["jsonPath"] = Text(),
                    ["depth"] = Number(1, 10), ["chunk"] = Number(1, int.MaxValue), ["chunkSize"] = Number(1, 32000) }, "documentType", "documentName")),
            Tool("search_document_content", "Search literal property names and values in real module JSON; exact absolute JSONPaths with bounded context.",
                Schema(new() { ["documentType"] = Text(), ["documentName"] = Text(), ["folderPath"] = Text(), ["queries"] = Strings(10),
                    ["caseSensitive"] = Boolean(), ["contextDepth"] = Number(0, 2), ["maxResultsPerQuery"] = Number(1, 50),
                    ["maxTotalChars"] = Number(2000, 24000), ["scopeJsonPath"] = Text(), ["searchIn"] = Text("keys", "values", "both") }, "documentType", "documentName", "queries")),
            Tool("find_references", "Read the adapter's actual reference analysis, preserving complete=false and its reason.",
                Schema(new() { ["documentType"] = Text(), ["documentName"] = Text(), ["folderPath"] = Text() }, "documentType", "documentName")),
            Tool("list_files", "List generated runtime/API/version documentation in the virtual filesystem.",
                Schema(new() { ["path"] = Text(), ["level"] = Text("name", "summary", "full") })),
            Tool("read_file", "Read generated documentation by path and optional 1-based line range; never reads OS files.",
                Schema(new() { ["path"] = Text(), ["level"] = Text("summary", "overview", "full"), ["startLine"] = Number(1, int.MaxValue), ["lineCount"] = Number(1, 2000) }, "path")),
            Tool("search_files", "Literal line search in generated documentation; paths may be directories or glob patterns.",
                Schema(new() { ["paths"] = Strings(50), ["queries"] = Strings(10), ["caseSensitive"] = Boolean(), ["contextLines"] = Number(0, 20),
                    ["maxFiles"] = Number(1, 500), ["maxResults"] = Number(1, 100), ["maxTotalChars"] = Number(2000, 24000) }, "paths", "queries")),
            Tool("get_assistant_def", "Read this independent runtime's own assistant guidance and exact-version capability evidence.", Schema(new())),
            Tool("search_knowledge_base", "检索活字格官方在线帮助。按绑定的设计器版本返回对应手册中的正文片段和链接。仅发送查询关键词和公开文档请求。",
                Schema(new() { ["keyword"] = new { type = "string", minLength = 1, maxLength = 300 } }, "keyword"), openWorld: true),
            Tool("get_platform_capabilities", "Read exact bound target, catalog and per-operation native validation status. Availability is not live acceptance.", Schema(new())),
            Tool("invoke_api", "Invoke one registered platform API. The Host enforces exact target, permissions, revision baselines and save receipts.",
                Schema(new() { ["namespace"] = Text(), ["method"] = Text(), ["parameters"] = new { type = "object" } }, "namespace", "method", "parameters"), false)
        };
        foreach (var descriptor in context.Apis.Where(a => a.Namespace == "module"))
            tools.Add(Tool("module_" + descriptor.Method, descriptor.Documentation ?? $"Registered api.module.{descriptor.Method}; inspect capability evidence before use.",
                descriptor.InputSchema, !HasSideEffect(descriptor)));
        foreach (var descriptor in context.Apis.Where(a => a.Namespace == "collaboration"))
            tools.Add(Tool("collaboration_" + descriptor.Method, descriptor.Documentation ?? "Explicit registered collaboration action; inspect original operation receipt.", descriptor.InputSchema, !HasSideEffect(descriptor)));
        foreach (var descriptor in context.Apis.Where(a => a.Namespace is "page" or "report"))
            tools.Add(Tool(descriptor.Namespace + "_" + descriptor.Method, descriptor.Documentation ?? "Registered native API; arguments and result retain the adapter contract.", descriptor.InputSchema, !HasSideEffect(descriptor)));
        return Task.FromResult(RuntimeJson.Serialize(new { tools }));
    }

    public async Task<JsonElement> CallToolAsync(string name, JsonElement arguments, PlatformRuntimeContext context,
        IPlatformApiInvoker api, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mutationDispatched = false;
        string? operationId = null;
        try
        {
            context = PlatformContractSchemas.Enrich(context);
            if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be an object.");
            if (arguments.GetRawText().Length > limits.MaxJsonChars) throw new ArgumentException("Tool arguments exceed MaxJsonChars.");
            var docs = new RuntimeDocumentation(context);
            var document = new DocumentTools(context, api);
            JsonElement result;
            switch (name)
            {
                case "execute_code":
                    await executionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try { result = await new JavaScriptExecutor(limits).ExecuteAsync(RuntimeJson.RequiredString(arguments, "code"), context, api, cancellationToken, arguments.TryGetProperty("readOnly",out var ro) && ro.ValueKind==JsonValueKind.True).ConfigureAwait(false); }
                    finally { executionSlots.Release(); }
                    var status = result.GetProperty("status").GetString();
                    return RuntimeJson.ToolResult(result, status != "Completed");
                case "get_project_structure": result = await document.GetProjectStructureAsync(arguments, cancellationToken).ConfigureAwait(false); break;
                case "get_document_content": result = await document.GetDocumentContentAsync(arguments, cancellationToken).ConfigureAwait(false); break;
                case "search_document_content": result = await document.SearchDocumentContentAsync(arguments, cancellationToken).ConfigureAwait(false); break;
                case "find_references": result = await document.FindReferencesAsync(arguments, cancellationToken).ConfigureAwait(false); break;
                case "list_files": result = docs.ListFiles(arguments); break;
                case "read_file": result = docs.ReadFile(arguments); break;
                case "search_files": result = docs.SearchFiles(arguments); break;
                case "search_knowledge_base": result = await HelpKnowledgeBase.SearchAsync(context.Target.DesignerVersion,
                    RuntimeJson.RequiredString(arguments, "keyword"), cancellationToken).ConfigureAwait(false); break;
                case "get_assistant_def": result = docs.AssistantDefinition(); break;
                case "get_platform_capabilities": result = RuntimeJson.Serialize(context); break;
                case "invoke_api":
                    var ns = RuntimeJson.RequiredString(arguments, "namespace");
                    var method = RuntimeJson.RequiredString(arguments, "method");
                    var descriptor = RuntimeJson.RequireApi(context, ns, method);
                    if (!arguments.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
                        throw new ArgumentException("parameters must be an object.");
                    operationId = ReadOperationId(parameters);
                    mutationDispatched = HasSideEffect(descriptor);
                    result = await api.InvokeAsync(ns, method, parameters, cancellationToken).ConfigureAwait(false); break;
                default:
                    if(name.StartsWith("page_",StringComparison.Ordinal) || name.StartsWith("report_",StringComparison.Ordinal))
                    {
                        var separator=name.IndexOf('_'); var nativeNs=name[..separator]; var nativeMethod=name[(separator+1)..];
                        var nativeDescriptor=RuntimeJson.RequireApi(context,nativeNs,nativeMethod);
                        operationId=ReadOperationId(arguments); mutationDispatched=HasSideEffect(nativeDescriptor);
                        result=await api.InvokeAsync(nativeNs,nativeMethod,arguments,cancellationToken).ConfigureAwait(false); break;
                    }
                    if(name.StartsWith("collaboration_",StringComparison.Ordinal))
                    {
                        var action=name[14..];
                        var registered=RuntimeJson.RequireApi(context,"collaboration",action);
                        operationId=ReadOperationId(arguments);mutationDispatched=HasSideEffect(registered);
                        result=await api.InvokeAsync("collaboration",action,arguments,cancellationToken).ConfigureAwait(false);break;
                    }
                    if (!name.StartsWith("module_", StringComparison.Ordinal)) throw new KeyNotFoundException("Unknown platform tool: " + name);
                    var moduleMethod = name[7..];
                    var moduleDescriptor = RuntimeJson.RequireApi(context, "module", moduleMethod);
                    operationId = ReadOperationId(arguments);
                    mutationDispatched = HasSideEffect(moduleDescriptor);
                    result = await api.InvokeAsync("module", moduleMethod, arguments, cancellationToken).ConfigureAwait(false); break;
            }
            // Preserve native media and structured MCP results without flattening or re-serializing content blocks.
            if ((name == "invoke_api" || name.StartsWith("module_", StringComparison.Ordinal) || name.StartsWith("collaboration_", StringComparison.Ordinal) || name.StartsWith("page_", StringComparison.Ordinal) || name.StartsWith("report_", StringComparison.Ordinal)) &&
                IsMcpEnvelope(result))
                return result.Clone();
            return RuntimeJson.ToolResult(result);
        }
        catch (OperationCanceledException e) when (mutationDispatched)
        {
            return RuntimeJson.ToolResult(RuntimeJson.Serialize(new { status = "Cancelled", operationId,
                error = new { code = e.GetType().Name, message = e.Message }, executionMayContinue = true }), true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            return RuntimeJson.ToolResult(RuntimeJson.Serialize(new { operationId,
                error = new { code = e.GetType().Name, message = e.Message }, executionMayContinue = mutationDispatched }), true);
        }
    }

    public Task<JsonElement> HandleResourceAsync(string method, JsonElement parameters, PlatformRuntimeContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new RuntimeDocumentation(PlatformContractSchemas.Enrich(context)).HandleResource(method, parameters));
    }

    private static object Tool(string name, string description, object schema, bool readOnly = true, bool openWorld = false) => new
    {
        name, description, inputSchema = schema,
        annotations = new { readOnlyHint = readOnly, destructiveHint = !readOnly, idempotentHint = readOnly, openWorldHint = openWorld }
    };
    private static bool IsMcpEnvelope(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return false;
        if (value.EnumerateObject().Any(p => p.Name is not ("content" or "structuredContent" or "isError" or "_meta"))) return false;
        return content.EnumerateArray().All(block => block.ValueKind == JsonValueKind.Object &&
            block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String &&
            type.GetString() is "text" or "image" or "audio" or "resource" or "resource_link");
    }
    internal static bool HasSideEffect(PlatformApiDescriptor descriptor) =>
        // Scope identifies the access domain; account reads remain reads.
        descriptor.MutatesProject;
    internal static string? ReadOperationId(JsonElement parameters)
    {
        if (parameters.TryGetProperty("operationId", out _)) return RuntimeJson.String(parameters, "operationId");
        return parameters.TryGetProperty("guard", out var guard) && guard.ValueKind == JsonValueKind.Object
            ? RuntimeJson.String(guard, "operationId") : null;
    }
    private static object Text(params string[] values) => values.Length == 0 ? new { type = "string" } : (object)new { type = "string", @enum = values };
    private static object Number(int min, int max) => new { type = "integer", minimum = min, maximum = max };
    private static object Boolean() => new { type = "boolean" };
    private static object Strings(int max) => new { type = "array", items = new { type = "string" }, minItems = 1, maxItems = max };
    private static object Schema(Dictionary<string, object> properties, params string[] required) => new
    { type = "object", properties, required, additionalProperties = false };
}
