using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.PlatformRuntime;

/// <summary>Request-local, generated documentation. Never reads the host or project filesystem.</summary>
internal sealed class RuntimeDocumentation
{
    private const string ResourceScheme = "forguncy-platform";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly SortedDictionary<string, VirtualDocument> files = new(StringComparer.Ordinal);
    private readonly PlatformRuntimeContext context;

    public RuntimeDocumentation(PlatformRuntimeContext context)
    {
        this.context = context ?? throw new ArgumentNullException(nameof(context));
        Generate();
    }

    public JsonElement ListFiles(JsonElement args)
    {
        var path = NormalizePath(String(args, "path", "/"));
        var level = Choice(args, "level", "name", "name", "summary", "full");
        var prefix = path == "/" ? "/" : path.TrimEnd('/') + "/";
        if (files.ContainsKey(path)) throw new ArgumentException("path must identify a VFS directory.");
        var entries = new SortedDictionary<string, object>(StringComparer.Ordinal);
        foreach (var (filePath, file) in files)
        {
            if (!filePath.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var remainder = filePath[prefix.Length..];
            var slash = remainder.IndexOf('/');
            if (slash >= 0)
            {
                var name = remainder[..slash];
                entries.TryAdd(name, new { name, path = prefix + name, type = "directory" });
            }
            else
            {
                var item = new Dictionary<string, object?> { ["name"] = remainder, ["path"] = filePath, ["type"] = "file" };
                if (level != "name") { item["description"] = file.Description; item["mimeType"] = file.MimeType; item["lineCount"] = Lines(file.Content).Length; }
                if (level == "full") item["overview"] = file.Overview;
                entries[remainder] = item;
            }
        }
        if (path != "/" && entries.Count == 0) throw new KeyNotFoundException("VFS directory does not exist: " + path);
        return Element(new { path, level, entries = entries.Values });
    }

    public JsonElement ReadFile(JsonElement args)
    {
        var path = NormalizePath(RequiredString(args, "path"));
        var level = Choice(args, "level", "full", "summary", "overview", "full");
        var file = Find(path);
        if (level != "full") return Element(new { path, level, content = level == "summary" ? file.Description : file.Overview, mimeType = file.MimeType });
        var lines = Lines(file.Content);
        var start = Number(args, "startLine", 1, 1, int.MaxValue);
        var count = Number(args, "lineCount", Has(args, "startLine") ? 200 : 2000, 1, 2000);
        var selected = lines.Skip(Math.Min(start - 1, lines.Length)).Take(count).ToArray();
        var content = string.Join("\n", selected);
        const int maxChars = 262144;
        var truncatedByChars = content.Length > maxChars;
        if (truncatedByChars) content = content[..maxChars];
        return Element(new { path, level, mimeType = file.MimeType, startLine = start,
            lineCount = truncatedByChars ? Lines(content).Length : selected.Length, totalLines = lines.Length, content,
            hasMore = (long)start - 1 + selected.Length < lines.Length || truncatedByChars, truncatedByChars });
    }

    public JsonElement SearchFiles(JsonElement args)
    {
        var caseSensitive = Boolean(args, "caseSensitive", false);
        var comparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
        var queries = StringArray(args, "queries", 10).Where(q => !string.IsNullOrWhiteSpace(q)).Distinct(comparer).ToArray();
        if (queries.Length == 0) throw new ArgumentException("queries must contain a nonblank literal keyword.");
        if (queries.Any(q => q.Length > 512)) throw new ArgumentException("Each query is limited to 512 characters.");
        var paths = StringArray(args, "paths", 50);
        var maxFiles = Number(args, "maxFiles", 300, 1, 500);
        var maxResults = Number(args, "maxResults", 20, 1, 100);
        var contextLines = Number(args, "contextLines", 3, 0, 20);
        var maxChars = Math.Clamp(Number(args, "maxTotalChars", 12000, 1, 24000), 2000, 24000);
        var selected = new SortedSet<string>(StringComparer.Ordinal);
        var errors = new List<object>();
        foreach (var input in paths)
        {
            string path;
            try { path = NormalizePath(input); }
            catch (ArgumentException error) { errors.Add(new { path = Preview(input), error = error.Message }); continue; }
            var matches = MatchPaths(path).ToArray();
            if (matches.Length == 0) errors.Add(new { path, error = "No VFS file or directory matched this path." });
            foreach (var match in matches) selected.Add(match);
        }
        var output = new List<object>();
        var stats = new List<object>();
        var totals = queries.ToDictionary(q => q, _ => 0, comparer);
        var truncatedByChars = false;
        var truncatedByResults = false;
        var usedChars = 0;
        foreach (var path in selected.Take(maxFiles))
        {
            var lines = Lines(files[path].Content);
            var count = 0;
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var query in queries)
                {
                    var column = lines[i].IndexOf(query, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
                    if (column < 0) continue;
                    count++;
                    totals[query]++;
                    if (output.Count >= maxResults) { truncatedByResults = true; continue; }
                    var first = Math.Max(0, i - contextLines);
                    var last = Math.Min(lines.Length - 1, i + contextLines);
                    var hit = new { path, query, line = i + 1, column = column + 1, text = Preview(lines[i]),
                        context = Enumerable.Range(first, last - first + 1).Select(n => new { line = n + 1, text = Preview(lines[n]) }).ToArray() };
                    var chars = JsonSerializer.Serialize(hit, JsonOptions).Length;
                    if (usedChars + chars > maxChars - 1200) { truncatedByChars = true; continue; }
                    output.Add(hit);
                    usedChars += chars;
                }
            }
            if (count > 0) stats.Add(new { path, matches = count });
        }
        // Statistics and errors are bounded along with hits so a wide catalog cannot defeat the output limit.
        var result = new Dictionary<string, object?>
        {
            ["matches"] = output, ["fileStats"] = stats, ["totalMatches"] = totals,
            ["errors"] = errors, ["scannedFiles"] = Math.Min(selected.Count, maxFiles),
            ["truncatedByFiles"] = selected.Count > maxFiles, ["truncatedByResults"] = truncatedByResults,
            ["truncatedByChars"] = truncatedByChars
        };
        while (JsonSerializer.Serialize(result, JsonOptions).Length > maxChars && (stats.Count > 0 || output.Count > 0 || errors.Count > 0 || totals.Count > 0))
        {
            if (stats.Count > 0) stats.RemoveAt(stats.Count - 1);
            else if (output.Count > 0) output.RemoveAt(output.Count - 1);
            else if (errors.Count > 0) errors.RemoveAt(errors.Count - 1);
            else totals.Remove(totals.Keys.Last());
            result["truncatedByChars"] = true;
        }
        return Element(result);
    }

    public JsonElement AssistantDefinition() => Element(new
    {
        name = "Forguncy independent platform assistant",
        version = "1",
        systemPrompt = "Use the registered api namespace methods and generated VFS documentation for the bound Forguncy project. " +
            "Check the exact designer version, capabilities, validation status, effect scope and current revisions before mutations. " +
            "An available operation or schema is not evidence of native or live-designer acceptance. Preserve original JSON values and errors. " +
            "A timed-out or canceled mutation may still complete; inspect its operation receipt before retrying. " +
            "Project definition CRUD must not implicitly execute business commands, send notifications, change external database schemas or deploy workflows. " +
            "Treat native save acceptance, source verification, re-open and business-flow checks as distinct evidence.",
        projectTarget = context.Target,
        documentationRoot = "/index.md",
        apiCatalog = "/apis/catalog.json",
        types = "/apis/index.d.ts",
        capabilities = "/versions/capabilities.json",
        validationNotice = "Capability availability and validation strings are supplied by the bound adapter and are reproduced without promotion. Missing operations are not advertised as implemented."
    });

    public JsonElement HandleResource(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "resources/list":
                return Element(new { resources = files.Select(p => new { uri = ToUri(p.Key), name = p.Key,
                    description = p.Value.Description, mimeType = p.Value.MimeType }).ToArray() });
            case "resources/templates/list":
                return Element(new { resourceTemplates = Array.Empty<object>() });
            case "resources/read":
                var rawUri = RequiredString(parameters, "uri");
                if (!rawUri.StartsWith(ResourceScheme + ":///", StringComparison.OrdinalIgnoreCase) ||
                    !Uri.TryCreate(rawUri, UriKind.Absolute, out var uri) || uri.Scheme != ResourceScheme ||
                    uri.Host.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                    throw new ArgumentException("uri must be an absolute forguncy-platform:/// VFS URI without a host, query or fragment.");
                // Reject traversal before System.Uri can normalize dot-segments away.
                var rawPath = rawUri[(rawUri.IndexOf("://", StringComparison.Ordinal) + 3)..];
                var path = NormalizePath(Uri.UnescapeDataString(rawPath));
                var file = Find(path);
                return Element(new { contents = new[] { new { uri = ToUri(path), mimeType = file.MimeType, text = file.Content } } });
            default:
                throw new ArgumentException("Unsupported resource method: " + method);
        }
    }

    private void Generate()
    {
        Add("/index.md", "Independent cross-version platform documentation", "Start here for runtime, API and exact-version capabilities.", """
            # Forguncy independent platform runtime

            This VFS is generated by the plugin from its public contracts and the bound adapter catalog. It is not the designer's native assistant VFS and contains no copied vendor JavaScript.

            - `/runtime.md`: JavaScript API boundary, asynchronous calls and cancellation.
            - `/apis/index.d.ts`: generated namespace API declarations.
            - `/apis/catalog.json`: exact registered descriptors and schemas.
            - `/apis/<namespace>/index.md`: namespace methods and effect scopes.
            - `/versions/index.md`: current target and complete operation capability matrix.
            - `/versions/capabilities.json`: unmodified adapter capability records.
            - `search_knowledge_base({keyword})`: search online Forguncy help. Snippets and links are taken from the manual for this designer build.

            Unlisted APIs are unavailable. Read the capability validation column before treating any operation as verified.
            """);
        Add("/runtime.md", "JavaScript execution and host boundary", "Registered JSON APIs, Promise calls and explicit cancellation semantics.", """
            # JavaScript runtime

            `execute_code` accepts JavaScript with top-level `await`. Each execution uses a fresh engine. Call only registered APIs as `await api.<namespace>.<method>(parameters)`; namespace and method spelling come from `/apis/catalog.json`. Dots in a catalog namespace create nested properties: namespace `design.pages` and method `read` is `api.design.pages.read(parameters)`, equivalently `api['design']['pages']['read'](parameters)`. A flat lookup such as `api['design.pages']` does not address that namespace.

            `console.log/info/warn/error/debug(...values)` and `log(...values)` append JSON log entries; `log` is an alias for `console.log`. `assert(condition, message)` throws when the condition is false. Use `return value` for the execution result's `value`; returning `undefined`, or omitting a return, produces JSON null. Results also include status, logs, API calls, API errors and uncertainty information. Inspect these fields before treating the execution as successful.

            All API parameters and returned values cross the bridge as JSON. No CLR objects, native document objects, file handles, arbitrary network, process or filesystem APIs are exposed. The runtime is an in-process interpreter with cooperative limits; it is not an operating-system security boundary.

            A full native API catalog can exceed the default 2,000,000-character per-API JSON bridge budget. Capture the complete catalog outside JavaScript with `invoke_api` when needed. Inside JavaScript, use exact `namespace`/`method` filters only when the registered catalog input schema advertises them; the filtered result remains an Array. For small lookups, read `/apis/<namespace>/<method>.input.schema.json` or the corresponding namespace docs. A native mutation script prepared from a reviewed descriptor needs no catalog query. Do not widen all API budgets to retrieve one descriptor.

            `Promise.all` may initiate concurrent API calls; the host retains its own write queue and session, revision, identity, effect and capability checks. Defining a command, notification or workflow does not execute it. Host exceptions reject their promises. Error-shaped JSON and native receipts are returned unchanged and must be inspected explicitly. Keep the native error details when reporting failures.

            The runtime drains already-dispatched API calls before successful completion even when the script did not await them. Explicitly await intended work so its result and errors can be handled. A user-created promise that never settles keeps the execution pending until cancellation or its deadline. API-call and pending-call quotas still apply; Promise concurrency does not raise those quotas.

            A request has finite source, execution, memory, recursion, call-count and output limits. Cancellation stops further script work and is passed to outstanding host calls. Host calls that ignore cancellation may still complete after the runtime stops waiting, particularly after mutation dispatch. Never interpret timeout as rollback or retry an uncertain mutation automatically; use its operation ID and host receipt.

            Read-before-write: confirm the bound target and operation availability; read native definitions and current revisions; apply the precise native change; inspect validation and save receipts. Native save acceptance, verified source writeback, reopen, runtime formula checks and business acceptance are separate outcomes.

            For typed AccountService operations, `persisted_verified` preserves verified service persistence/readback in `result.serviceResult`; it is not a memory-pending-save label. Project export has its own `saveResult`. `ServicePersistedProjectExportFailed` preserves an already verified service commit while reporting export failure: retain the original operation ID and retry only a separately guarded export/save, never the service CUD. Account availability and a verified service binding remain required; schema existence is not native acceptance.

            Mixed RuntimeDeployment APIs report native acceptance, registry readback, projectSaveAccepted/projectSourceVerified/projectDirtyFalse and runtimeFileSynchronized separately. A verified project source does not establish runtime activation. When requiresRegenerate is true, retain the native_accepted result and perform regeneration as a separately authorized action. A verification_failed outcome remains bound to its original operation ID across document rebinding; do not replay deployment under a new ID.
            """);
        Add("/apis/catalog.json", "Registered API descriptors", "The bound adapter catalog, including schemas and side effects.", Serialize(context.Apis), "application/json");
        Add("/versions/target.json", "Bound designer target", "Exact instance, session, project and designer version supplied by the host.", Serialize(context.Target), "application/json");
        Add("/versions/capabilities.json", "Adapter operation capabilities", "Availability, effect scope, validation state and reason copied from the adapter.", Serialize(context.Capabilities), "application/json");

        var version = new StringBuilder("# Bound version and capabilities\n\n");
        version.Append("Designer version: `").Append(Inline(context.Target.DesignerVersion)).Append("`\n\n")
            .Append("Instance: `").Append(Inline(context.Target.InstanceId)).Append("`; session: `").Append(Inline(context.Target.SessionId)).Append("`.\n\n")
            .Append("Availability and validation are independent. Values below are adapter claims reproduced exactly; absent claims are marked not advertised. This document does not establish live-designer acceptance or native CRUD success.\n\n")
            .Append("| Module | Operation | Available | Validation | Effect scope | Reason |\n| --- | --- | --- | --- | --- | --- |\n");
        foreach (var kind in Enum.GetValues<ModuleKind>())
        foreach (var operation in Enum.GetValues<ModuleOperation>())
        {
            var claims = context.Capabilities.Where(c => c.Kind == kind && c.Operation == operation).ToArray();
            if (claims.Length == 0) version.Append($"| {kind} | {operation} | false | not advertised | — | No adapter claim |\n");
            foreach (var capability in claims)
                version.Append($"| {kind} | {operation} | {capability.Available.ToString().ToLowerInvariant()} | {Cell(capability.Validation)} | {capability.Scope} | {Cell(capability.Reason ?? "")} |\n");
        }
        Add("/versions/index.md", "Exact-version operation matrix", "All module/operation pairs with honest adapter validation state.", version.ToString());

        foreach (var group in context.Apis.GroupBy(a => a.Namespace, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var markdown = new StringBuilder("# API namespace ").Append(group.Key).Append("\n\n");
            foreach (var api in group.OrderBy(a => a.Method, StringComparer.Ordinal))
            {
                var call = "api" + string.Concat(api.Namespace.Split('.').Append(api.Method).Select(p => "[" + JsonSerializer.Serialize(p) + "]"));
                markdown.Append("## ").Append(api.Method).Append("\n\n`await ").Append(call).Append("(parameters)`\n\n")
                    .Append("Mutates project: `").Append(api.MutatesProject.ToString().ToLowerInvariant()).Append("`; effect scope: `").Append(api.Scope).Append("`.\n\n")
                    .Append(api.Documentation ?? "No method-specific documentation was supplied by the adapter.").Append("\n\n")
                    .Append("Input JSON schema:\n\n```json\n").Append(Serialize(api.InputSchema)).Append("\n```\n\n");
                if (api.OutputSchema is { } output) markdown.Append("Output JSON schema:\n\n```json\n").Append(Serialize(output)).Append("\n```\n\n");
                var basePath = "/apis/" + Segment(api.Namespace) + "/" + Segment(api.Method);
                Add(basePath + ".input.schema.json", api.Namespace + "." + api.Method + " input schema", "Registered input schema.", Serialize(api.InputSchema), "application/json");
                if (api.OutputSchema is { } outputSchema)
                    Add(basePath + ".output.schema.json", api.Namespace + "." + api.Method + " output schema", "Registered output schema.", Serialize(outputSchema), "application/json");
            }
            Add("/apis/" + Segment(group.Key) + "/index.md", group.Key + " API namespace", "Registered methods, schemas and effect scopes.", markdown.ToString());
        }
        Add("/apis/index.d.ts", "Generated JavaScript API declarations", "JSON-schema-derived namespace APIs for the currently bound adapter.", GenerateDeclarations(context.Apis), "text/typescript");
        Add("/assistant.json", "Independent assistant definition", "The plugin's own assistant instructions; not the vendor system prompt.", Serialize(AssistantDefinition()), "application/json");
    }

    private static string GenerateDeclarations(IReadOnlyList<PlatformApiDescriptor> apis)
    {
        var root = new NamespaceNode();
        foreach (var api in apis)
        {
            var current = root;
            foreach (var part in api.Namespace.Split('.'))
            {
                if (current.Methods.ContainsKey(part)) throw new ArgumentException("Conflicting API namespace and method: " + api.Namespace);
                if (!current.Children.TryGetValue(part, out var next)) current.Children[part] = next = new NamespaceNode();
                current = next;
            }
            if (current.Children.ContainsKey(api.Method) || !current.Methods.TryAdd(api.Method, api))
                throw new ArgumentException("Conflicting API namespace and method: " + api.Namespace + "." + api.Method);
        }
        var declaration = new StringBuilder("// Generated from this bound adapter's registered JSON schemas.\n// Availability is not evidence of native or live-designer validation.\n// Only same-root JSON Schema references are resolved; recursive expansion is bounded.\n// Conditional if/then validation remains authoritative in the JSON schema and Host.\ndeclare const api: {\n");
        AppendDeclarations(declaration, root, 1);
        return declaration.Append("};\n").ToString();
    }

    private static void AppendDeclarations(StringBuilder text, NamespaceNode node, int level)
    {
        var indent = new string(' ', level * 2);
        foreach (var (name, child) in node.Children)
        {
            text.Append(indent).Append("readonly ").Append(JsonSerializer.Serialize(name)).Append(": {\n");
            AppendDeclarations(text, child, level + 1);
            text.Append(indent).Append("};\n");
        }
        foreach (var (name, api) in node.Methods)
            text.Append(indent).Append("readonly ").Append(JsonSerializer.Serialize(name)).Append(": (parameters: ")
                .Append(TypeScript(api.InputSchema, 0)).Append(") => Promise<")
                .Append(api.OutputSchema is { } schema ? TypeScript(schema, 0) : "unknown").Append(">;\n");
    }

    private static string TypeScript(JsonElement schema, int depth, JsonElement? schemaRoot = null)
    {
        if (depth > 16) return "unknown";
        if (schema.ValueKind == JsonValueKind.False) return "never";
        if (schema.ValueKind != JsonValueKind.Object) return "unknown";
        var root = schemaRoot ?? schema;
        var constraints = new List<string>();
        if (schema.TryGetProperty("$ref", out var reference))
            constraints.Add(reference.ValueKind == JsonValueKind.String && TryResolveLocalReference(root, reference.GetString()!, out var resolved)
                ? TypeScript(resolved, depth + 1, root) : "unknown");
        // Combinators constrain their sibling object shape; they do not replace it.
        // In particular an if/then-only allOf branch is neutral here, while the
        // owning JSON schema and Host still enforce that conditional requirement.
        constraints.Add(TypeScriptBase(schema, depth, root));
        foreach (var union in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(union, out var choices) && choices.ValueKind == JsonValueKind.Array)
                constraints.Add(TypeScriptUnion(choices.EnumerateArray().Select(s => TypeScript(s, depth + 1, root))));
        if (schema.TryGetProperty("allOf", out var intersection) && intersection.ValueKind == JsonValueKind.Array)
            constraints.AddRange(intersection.EnumerateArray().Select(s => TypeScript(s, depth + 1, root)));
        var parts = constraints.Where(t => t != "unknown").Distinct(StringComparer.Ordinal).ToArray();
        return parts.Contains("never", StringComparer.Ordinal) ? "never" : parts.Length switch
        { 0 => "unknown", 1 => parts[0], _ => "(" + string.Join(" & ", parts.Select(t => "(" + t + ")")) + ")" };
    }

    private static string TypeScriptUnion(IEnumerable<string> alternatives)
    {
        var parts = alternatives.Distinct(StringComparer.Ordinal).ToArray();
        if (parts.Contains("unknown", StringComparer.Ordinal)) return "unknown";
        parts = parts.Where(t => t != "never").ToArray();
        return parts.Length switch { 0 => "never", 1 => parts[0], _ => "(" + string.Join(" | ", parts) + ")" };
    }

    private static string TypeScriptBase(JsonElement schema, int depth, JsonElement root)
    {
        if (schema.TryGetProperty("enum", out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > 0)
            return string.Join(" | ", values.EnumerateArray().Select(v => v.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null ? v.GetRawText() : "unknown"));
        if (schema.TryGetProperty("const", out var constant)) return constant.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null ? constant.GetRawText() : "unknown";
        if (!schema.TryGetProperty("type", out var type))
            return schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                ? ObjectTypeScript(schema, depth, root) : "unknown";
        if (type.ValueKind == JsonValueKind.Array)
            return "(" + string.Join(" | ", type.EnumerateArray().Select(t => TypeScriptType(t.ValueKind == JsonValueKind.String ? t.GetString() : null, schema, depth, root))) + ")";
        return TypeScriptType(type.ValueKind == JsonValueKind.String ? type.GetString() : null, schema, depth, root);
    }

    private static bool TryResolveLocalReference(JsonElement root, string reference, out JsonElement resolved)
    {
        resolved = root;
        if (reference == "#") return true;
        if (!reference.StartsWith("#/", StringComparison.Ordinal)) return false; // No filesystem or network resolution.
        string pointer;
        try { pointer = Uri.UnescapeDataString(reference[2..]); }
        catch (UriFormatException) { return false; }
        foreach (var rawSegment in pointer.Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (resolved.ValueKind == JsonValueKind.Object && resolved.TryGetProperty(segment, out var property)) resolved = property;
            else if (resolved.ValueKind == JsonValueKind.Array && int.TryParse(segment, out var index) && index >= 0 && index < resolved.GetArrayLength()) resolved = resolved[index];
            else return false;
        }
        return true;
    }

    private static string TypeScriptType(string? type, JsonElement schema, int depth, JsonElement root) => type switch
    {
        "string" => "string", "number" or "integer" => "number", "boolean" => "boolean", "null" => "null",
        "array" => "Array<" + (schema.TryGetProperty("items", out var items) ? TypeScript(items, depth + 1, root) : "unknown") + ">",
        "object" => ObjectTypeScript(schema, depth, root), _ => "unknown"
    };

    private static string ObjectTypeScript(JsonElement schema, int depth, JsonElement root)
    {
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
            return schema.TryGetProperty("additionalProperties", out var values) && values.ValueKind == JsonValueKind.Object
                ? "Record<string, " + TypeScript(values, depth + 1, root) + ">" : "Record<string, unknown>";
        var required = schema.TryGetProperty("required", out var requiredNode) && requiredNode.ValueKind == JsonValueKind.Array
            ? requiredNode.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        return "{ " + string.Join(" ", properties.EnumerateObject().Select(p => JsonSerializer.Serialize(p.Name) +
            (required.Contains(p.Name) ? "" : "?") + ": " + TypeScript(p.Value, depth + 1, root) + ";")) + " }";
    }

    private sealed class NamespaceNode
    {
        public SortedDictionary<string, NamespaceNode> Children { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, PlatformApiDescriptor> Methods { get; } = new(StringComparer.Ordinal);
    }

    private IEnumerable<string> MatchPaths(string path)
    {
        if (path.Contains('*') || path.Contains('?'))
        {
            var pattern = "^" + Regex.Escape(path).Replace(@"\*\*", "\u0001").Replace(@"\*", "[^/]*").Replace(@"\?", "[^/]").Replace("\u0001", ".*") + "$";
            var matcher = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            return files.Keys.Where(p => matcher.IsMatch(p));
        }
        if (files.ContainsKey(path)) return new[] { path };
        var prefix = path == "/" ? "/" : path.TrimEnd('/') + "/";
        return files.Keys.Where(p => p.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("VFS path cannot be blank.");
        if (path.Length > 2048 || path.Contains('\\') || path.Contains(':') || path.Any(char.IsControl)) throw new ArgumentException("Invalid VFS path.");
        if (!path.StartsWith('/')) path = "/" + path;
        if (path.Split('/').Any(p => p is "." or "..")) throw new ArgumentException("VFS path traversal is not allowed.");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private void Add(string path, string title, string description, string content, string mimeType = "text/markdown") =>
        files.Add(path, new VirtualDocument(title, description, description + "\n\n" + FirstLines(content, 12), content.Replace("\r\n", "\n"), mimeType));
    private VirtualDocument Find(string path) => files.TryGetValue(path, out var file) ? file : throw new KeyNotFoundException("VFS file does not exist: " + path);
    private static string ToUri(string path) => ResourceScheme + "://" + string.Join("/", path.Split('/').Select(Uri.EscapeDataString));
    private static string Segment(string input) => Uri.EscapeDataString(input).Replace(".", "%2E", StringComparison.Ordinal);
    private static string Inline(string text) => text.Replace("`", "'", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private static string Cell(string text) => Inline(text).Replace("|", "\\|", StringComparison.Ordinal);
    private static string Preview(string text) => text.Length <= 500 ? text : text[..500] + "…";
    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    private static string FirstLines(string text, int count) => string.Join("\n", Lines(text).Take(count));
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static bool Has(JsonElement args, string property) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(property, out _);
    private static string RequiredString(JsonElement args, string property) => Has(args, property) ? String(args, property, "") : throw new ArgumentException(property + " is required.");
    private static string String(JsonElement args, string property, string fallback)
    {
        if (!Has(args, property)) return fallback;
        var value = args.GetProperty(property);
        return value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new ArgumentException(property + " must be a string.");
    }
    private static string Choice(JsonElement args, string property, string fallback, params string[] allowed)
    {
        var value = String(args, property, fallback);
        return allowed.Contains(value, StringComparer.Ordinal) ? value : throw new ArgumentException(property + " must be one of: " + string.Join(", ", allowed));
    }
    private static int Number(JsonElement args, string property, int fallback, int min, int max)
    {
        if (!Has(args, property)) return fallback;
        if (args.GetProperty(property).ValueKind == JsonValueKind.Number && args.GetProperty(property).TryGetInt32(out var value) && value >= min && value <= max) return value;
        throw new ArgumentException($"{property} must be an integer from {min} through {max}.");
    }
    private static bool Boolean(JsonElement args, string property, bool fallback)
    {
        if (!Has(args, property)) return fallback;
        return args.GetProperty(property).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => throw new ArgumentException(property + " must be boolean.") };
    }
    private static string[] StringArray(JsonElement args, string property, int max)
    {
        if (!Has(args, property) || args.GetProperty(property).ValueKind != JsonValueKind.Array) throw new ArgumentException(property + " must be an array.");
        var array = args.GetProperty(property);
        if (array.GetArrayLength() == 0 || array.GetArrayLength() > max) throw new ArgumentException($"{property} must have 1 through {max} items.");
        return array.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new ArgumentException(property + " must contain strings.")).ToArray();
    }
    private sealed record VirtualDocument(string Title, string Description, string Overview, string Content, string MimeType);
}
