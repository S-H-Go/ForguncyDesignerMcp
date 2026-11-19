using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ForguncyDesignerMcp.Core.Platform;
using Json.Path;

namespace ForguncyDesignerMcp.PlatformRuntime;

/// <summary>Read-only tools over the bound adapter's native module model.</summary>
internal sealed class DocumentTools(PlatformRuntimeContext context, IPlatformApiInvoker api)
{
    private const int MaxTreeNodes = 30_000;
    private const int MaxDocumentChars = 4_000_000;
    private const int MaxResponseChars = 1_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 96
    };

    public async Task<JsonElement> GetProjectStructureAsync(JsonElement args, CancellationToken token)
    {
        EnsureObject(args);
        RequireApi("list");
        var scope = Text(args, "scope") ?? "all";
        var includeTableDetails = Boolean(args, "includeTableDetails", true);
        var explicitScope = scope != "all";
        var kinds = explicitScope ? scope.Split(',').Select(value => ParseKind(value.Trim())).Distinct().ToArray()
            : Enum.GetValues<ModuleKind>();
        var maxItems = Integer(args, "maxItems", 10_000, 1, 30_000);
        var folderPath = Text(args, "folderPath");
        if (folderPath is not null && (!explicitScope || kinds.Length != 1 || kinds[0] == ModuleKind.DataConnection))
            throw new ArgumentException("folderPath requires scope to name exactly one tree resource type.");
        if (folderPath is not null) folderPath = NormalizeFolderPath(folderPath, GroupName(kinds[0]));
        var response = new Dictionary<string, object?> { ["target"] = context.Target };
        var unavailable = new List<object>();
        var total = 0;
        var truncated = false;

        foreach (var kind in kinds)
        {
            token.ThrowIfCancellationRequested();
            var capability = Capability(kind, ModuleOperation.List);
            if (capability is null || !capability.Available)
            {
                if (explicitScope) ThrowUnavailable(kind, ModuleOperation.List, capability);
                unavailable.Add(new { documentType = GroupName(kind), operation = "List",
                    available = false, validation = capability?.Validation ?? "Unimplemented",
                    reason = capability?.Reason ?? "The bound adapter did not advertise this operation." });
                continue;
            }

            var group = new StructureNode("folder", GroupName(kind));
            var connections = new List<object>();
            response[GroupName(kind)] = kind == ModuleKind.DataConnection ? connections : group;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var pagesRead = 0;
            do
            {
                if (++pagesRead > 1_000) throw new InvalidOperationException("module.list exceeded the pagination budget.");
                if (total >= maxItems) { truncated = true; break; }
                var pageResult = await InvokeAsync("list", Encode(new ModuleQuery(kind, folderPath, cursor,
                    Math.Min(100, maxItems - total))), token).ConfigureAwait(false);
                ModulePage page;
                try { page = pageResult.Deserialize<ModulePage>(JsonOptions)
                        ?? throw new InvalidOperationException("module.list returned null."); }
                catch (JsonException ex) { throw new InvalidOperationException("module.list returned an invalid ModulePage.", ex); }
                if (page.Items is null) throw new InvalidOperationException("module.list omitted items.");
                if (page.Items.Count > Math.Min(100, maxItems - total))
                    throw new InvalidOperationException("module.list exceeded its requested page size.");
                foreach (var item in page.Items)
                {
                    token.ThrowIfCancellationRequested();
                    if (item.Address is null || item.Address.Kind != kind || string.IsNullOrEmpty(item.Address.Name))
                        throw new InvalidOperationException("module.list returned an invalid or mismatched module address.");
                    if (kind == ModuleKind.DataConnection)
                        connections.Add(new { name = item.Address.Name, revision = item.Revision, description = item.Description });
                    else
                    {
                        var node = AddModule(group, item);
                        if (includeTableDetails && kind == ModuleKind.Table && Capability(kind, ModuleOperation.Read)?.Available == true &&
                            context.Apis.Any(entry => entry.Namespace == "module" && entry.Method == "read"))
                        {
                            var tableResult = await InvokeAsync("read", Encode(item.Address), token).ConfigureAwait(false);
                            var table = tableResult.Deserialize<ModuleDocument>(JsonOptions)
                                ?? throw new InvalidOperationException("module.read returned no table document.");
                            if (table.Address != item.Address) throw new InvalidOperationException("module.read returned the wrong table address.");
                            node.Comment = MetadataProperty(table.Metadata, "comment")?.GetString() ?? item.Description;
                            node.Columns = MetadataProperty(table.Metadata, "columns");
                        }
                    }
                    total++;
                }
                cursor = page.NextCursor;
                if (cursor is not null && !cursors.Add(cursor))
                    throw new InvalidOperationException("module.list returned a repeated pagination cursor.");
            } while (cursor is not null);
            if (truncated) break;
        }
        if (folderPath is not null)
        {
            var group = (StructureNode)response[GroupName(kinds[0])]!;
            response[GroupName(kinds[0])] = FolderSubtree(group, folderPath)
                ?? throw new KeyNotFoundException("The adapter returned no such folder subtree: " + folderPath);
        }
        response["unavailable"] = unavailable;
        response["totalModules"] = total;
        response["complete"] = !truncated && unavailable.Count == 0;
        response["truncated"] = truncated;
        var structure = Encode(response);
        if (structure.GetRawText().Length > MaxResponseChars)
            throw new ArgumentException("Project structure exceeds the response budget. Request a narrower scope or folderPath.");
        return structure;
    }

    public async Task<JsonElement> GetDocumentContentAsync(JsonElement args, CancellationToken token)
    {
        var document = await ReadDocumentAsync(args, token).ConfigureAwait(false);
        var pathText = Text(args, "jsonPath") ?? "$";
        var chunkMode = args.TryGetProperty("chunk", out _);
        if (chunkMode && args.TryGetProperty("depth", out _))
            throw new ArgumentException("chunk and depth are mutually exclusive.");
        if (!chunkMode && args.TryGetProperty("chunkSize", out _))
            throw new ArgumentException("chunkSize is only valid with chunk.");
        const int maxResults = 1_000;
        var depth = Integer(args, "depth", 5, 1, 10);
        var nodes = SelectNodes(document.Metadata, pathText, maxResults + 1, token);
        var more = nodes.Count > maxResults;
        if (more) nodes.RemoveAt(nodes.Count - 1);
        var clipped = false;
        var budget = 30_000;
        var matches = nodes.Select(node => new ContentMatch(node.Path,
            !chunkMode ? Clip(node.Value, depth, ref budget, ref clipped) : node.Value?.DeepClone())).ToList();
        var payload = Encode(new { documentName = document.Address.Name,
            documentType = GroupName(document.Address.Kind), folderPath = document.Address.FolderPath,
            revision = document.Revision, path = pathText, matched = matches.Count > 0,
            matches, content = matches.Count == 1 ? matches[0].Value : null,
            message = matches.Count == 0 ? "未匹配任何节点" : null,
            truncated = more || clipped, hasMore = more, mode = chunkMode ? "chunk" : "depth" });
        var json = payload.GetRawText();
        if (chunkMode)
        {
            var chunkSize = Integer(args, "chunkSize", 8_000, 1, 32_000);
            var chunk = Integer(args, "chunk", 1, 1, 4_000_001);
            var chunkIndex = chunk - 1;
            // Native-compatible chunking returns the selected raw JSON, not the enclosing tool response.
            json = matches.Count == 1 ? matches[0].Value?.ToJsonString() ?? "null" :
                new JsonArray(matches.Select(match => match.Value?.DeepClone()).ToArray()).ToJsonString();
            var chunks = Math.Max(1, (json.Length + chunkSize - 1) / chunkSize);
            if (chunkIndex >= chunks) throw new ArgumentException("chunk is outside the available chunks.");
            // Chunk boundaries are UTF-16 offsets. This avoids splitting a surrogate pair on the wire.
            var start = chunkIndex * chunkSize;
            var end = Math.Min(json.Length, start + chunkSize);
            if (start > 0 && char.IsLowSurrogate(json[start]) && char.IsHighSurrogate(json[start - 1])) start--;
            if (end < json.Length && char.IsLowSurrogate(json[end]) && char.IsHighSurrogate(json[end - 1])) end--;
            return Encode(new { documentName = document.Address.Name, documentType = GroupName(document.Address.Kind),
                revision = document.Revision, jsonPath = pathText, contentEncoding = "json-text-chunk", chunk,
                totalChunks = chunks, totalCharacters = json.Length, offset = start,
                content = json[start..end], hasMore = chunkIndex + 1 < chunks, truncated = more,
                matched = matches.Count > 0, message = matches.Count == 0 ? "未匹配任何节点" : null });
        }
        if (json.Length > MaxResponseChars)
            throw new ArgumentException("The selected content exceeds the response budget. Use chunk, a narrower jsonPath, or a smaller depth.");
        token.ThrowIfCancellationRequested();
        return payload;
    }

    public async Task<JsonElement> SearchDocumentContentAsync(JsonElement args, CancellationToken token)
    {
        var terms = SearchTerms(args);
        var document = await ReadDocumentAsync(args, token).ConfigureAwait(false);
        var limit = Integer(args, "maxResultsPerQuery", 20, 1, 50);
        var maxTotalChars = Math.Max(2_000, Integer(args, "maxTotalChars", 12_000, int.MinValue, 24_000));
        var caseSensitive = Boolean(args, "caseSensitive", false);
        var searchIn = Text(args, "searchIn") ?? "both";
        if (searchIn is not ("keys" or "values" or "both"))
            throw new ArgumentException("searchIn must be keys, values, or both.");
        var includePropertyNames = searchIn != "values";
        var includeValues = searchIn != "keys";
        var contextDepth = Integer(args, "contextDepth", 0, 0, 2);
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<object>();
        var perTerm = terms.ToDictionary(term => term, _ => 0, StringComparer.Ordinal);
        var stack = new Stack<SearchNode>();
        var scope = Text(args, "scopeJsonPath") ?? "$";
        var selected = SelectNodes(document.Metadata, scope, 1_001, token);
        if (selected.Count > 1_000) throw new ArgumentException("scopeJsonPath exceeds the 1000-root budget.");
        for (var index = selected.Count - 1; index >= 0; index--)
        {
            var node = selected[index];
            stack.Push(new SearchNode(Encode(node.Value), node.Path, null, []));
        }
        var visited = 0;
        var totalHits = 0;
        var characterBudget = maxTotalChars - 1_000;
        var visitedPaths = new HashSet<string>(StringComparer.Ordinal);
        while (stack.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!visitedPaths.Add(item.Path)) continue;
            if (++visited > MaxTreeNodes) throw new ArgumentException("Document exceeds the search node budget.");
            var valueText = item.Value.ValueKind == JsonValueKind.String ? item.Value.GetString()! :
                item.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? null : item.Value.GetRawText();
            foreach (var term in terms)
            {
                var nameMatch = includePropertyNames && item.PropertyName?.Contains(term, comparison) == true;
                var valueMatch = includeValues && valueText?.Contains(term, comparison) == true;
                if (!nameMatch && !valueMatch) continue;
                totalHits++;
                if (perTerm[term] < limit && results.Count < 100)
                {
                    var parent = item.Parents.Count == 0 ? item : item.Parents[^1];
                    var previewBudget = 100;
                    var previewClipped = false;
                    var match = new { query = term, path = item.Path,
                        matchType = nameMatch && valueMatch ? "property-and-value" : nameMatch ? "property" : "value",
                        propertyName = item.PropertyName, valuePreview = Preview(valueText ?? item.Value.GetRawText(), 512),
                        context = contextDepth == 0 ? null : new { path = parent.Path,
                            value = Clip(JsonNode.Parse(parent.Value.GetRawText()), contextDepth,
                                ref previewBudget, ref previewClipped, 160), truncated = previewClipped } };
                    var size = Encode(match).GetRawText().Length + 1;
                    if (size <= characterBudget)
                    {
                        results.Add(match);
                        perTerm[term]++;
                        characterBudget -= size;
                    }
                }
            }
            var parents = new List<SearchNode>(item.Parents) { item };
            if (parents.Count > 3) parents.RemoveAt(0);
            if (item.Value.ValueKind == JsonValueKind.Object)
            {
                var properties = item.Value.EnumerateObject().ToArray();
                for (var index = properties.Length - 1; index >= 0; index--)
                {
                    var property = properties[index];
                    stack.Push(new SearchNode(property.Value, PropertyPath(item.Path, property.Name), property.Name, parents));
                }
            }
            else if (item.Value.ValueKind == JsonValueKind.Array)
                for (var index = item.Value.GetArrayLength() - 1; index >= 0; index--)
                    stack.Push(new SearchNode(item.Value[index], item.Path + "[" + index + "]", null, parents));
        }
        var response = Encode(new { documentName = document.Address.Name,
            documentType = GroupName(document.Address.Kind), revision = document.Revision, scopeJsonPath = scope,
            queries = terms, results, totalMatches = totalHits, truncated = totalHits > results.Count, visitedNodes = visited });
        // The fixed 1000-character envelope reserve is normally ample, but long native names/revisions
        // can exceed it. Preserve earliest paths and trim later hits until the actual bound is satisfied.
        while (response.GetRawText().Length > maxTotalChars && results.Count > 0)
        {
            results.RemoveAt(results.Count - 1);
            response = Encode(new { documentName = document.Address.Name,
                documentType = GroupName(document.Address.Kind), revision = document.Revision, scopeJsonPath = scope,
                queries = terms, results, totalMatches = totalHits, truncated = true, visitedNodes = visited });
        }
        if (response.GetRawText().Length > maxTotalChars)
            throw new ArgumentException("The document identity and query envelope exceed maxTotalChars.");
        return response;
    }

    public async Task<JsonElement> FindReferencesAsync(JsonElement args, CancellationToken token)
    {
        EnsureObject(args);
        var address = Address(args);
        RequireAvailable(address.Kind, ModuleOperation.Read);
        return await InvokeAsync("findReferences", Encode(address), token).ConfigureAwait(false);
    }

    private async Task<ModuleDocument> ReadDocumentAsync(JsonElement args, CancellationToken token)
    {
        EnsureObject(args);
        var address = Address(args);
        RequireAvailable(address.Kind, ModuleOperation.Read);
        var result = await InvokeAsync("read", Encode(address), token).ConfigureAwait(false);
        ModuleDocument document;
        try { document = result.Deserialize<ModuleDocument>(JsonOptions)
                ?? throw new InvalidOperationException("module.read returned null."); }
        catch (JsonException ex) { throw new InvalidOperationException("module.read returned an invalid ModuleDocument.", ex); }
        if (document.Address != address || document.Metadata.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException("module.read returned the wrong address or omitted metadata.");
        if (document.Metadata.GetRawText().Length > MaxDocumentChars)
            throw new ArgumentException("Document exceeds the metadata size budget.");
        return document;
    }

    private async Task<JsonElement> InvokeAsync(string method, JsonElement args, CancellationToken token)
    {
        RequireApi(method);
        token.ThrowIfCancellationRequested();
        var result = await api.InvokeAsync("module", method, args, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private void RequireApi(string method)
    {
        if (!context.Apis.Any(item => item.Namespace == "module" && item.Method == method))
            throw new NotSupportedException($"APIUnavailable: module.{method} is not registered for designer {context.Target.DesignerVersion}.");
    }

    private ModuleCapability? Capability(ModuleKind kind, ModuleOperation operation) =>
        context.Capabilities.FirstOrDefault(item => item.Kind == kind && item.Operation == operation);

    private void RequireAvailable(ModuleKind kind, ModuleOperation operation)
    {
        var capability = Capability(kind, operation);
        if (capability is null || !capability.Available) ThrowUnavailable(kind, operation, capability);
    }

    private static void ThrowUnavailable(ModuleKind kind, ModuleOperation operation, ModuleCapability? capability) =>
        throw new NotSupportedException($"CapabilityUnavailable: {kind}.{operation}; validation={capability?.Validation ?? "Unimplemented"}; {capability?.Reason ?? "The adapter did not advertise this capability."}");

    private static ModuleAddress Address(JsonElement args)
    {
        var type = Text(args, "documentType") ?? Text(args, "kind") ?? throw new ArgumentException("documentType is required.");
        var name = Text(args, "documentName") ?? Text(args, "name") ?? throw new ArgumentException("documentName is required.");
        if (string.IsNullOrWhiteSpace(name) || name.Length > 2_048)
            throw new ArgumentException("documentName must contain 1 to 2048 characters and cannot be blank.");
        return new ModuleAddress(ParseKind(type), name, Text(args, "folderPath"));
    }

    private static ModuleKind ParseKind(string type)
    {
        if (Enum.TryParse<ModuleKind>(type, true, out var kind) && Enum.IsDefined(kind) && !int.TryParse(type, out _)) return kind;
        return type.ToLowerInvariant() switch
        {
            "pages" => ModuleKind.Page,
            "masterpages" => ModuleKind.MasterPage,
            "usercontrolpages" => ModuleKind.UserControlPage,
            "tables" => ModuleKind.Table,
            "views" => ModuleKind.View,
            "servercommands" => ModuleKind.ServerCommand,
            "sharedcommands" => ModuleKind.SharedCommand,
            "scheduledtasks" => ModuleKind.ScheduledTask,
            "notifications" => ModuleKind.Notification,
            "reports" => ModuleKind.Report,
            "businessprocesses" => ModuleKind.BusinessProcess,
            "images" => ModuleKind.Image,
            "resourcefiles" => ModuleKind.ResourceFile,
            "themes" => ModuleKind.Theme,
            "cellstyles" => ModuleKind.CellStyle,
            "listviewstyles" => ModuleKind.ListViewStyle,
            "dataconnections" => ModuleKind.DataConnection,
            "customlibraries" => ModuleKind.CustomLibrary,
            "globalvariables" => ModuleKind.GlobalVariable,
            "languageresources" => ModuleKind.LanguageResource,
            "plugins" => ModuleKind.Plugin,
            "users" => ModuleKind.User,
            "roles" => ModuleKind.Role,
            "organizations" => ModuleKind.Organization,
            "permissiongroups" => ModuleKind.PermissionGroup,
            "folders" => ModuleKind.Folder,
            "normalpage" => ModuleKind.Page,
            "master" => ModuleKind.MasterPage,
            "usercontrol" => ModuleKind.UserControlPage,
            "datatable" => ModuleKind.Table,
            "servercommandtemplate" => ModuleKind.ServerCommand,
            "sharedcommandtemplate" or "sharedclientcommand" => ModuleKind.SharedCommand,
            "schedule" or "schedulecommand" => ModuleKind.ScheduledTask,
            "workflow" => ModuleKind.BusinessProcess,
            "file" => ModuleKind.ResourceFile,
            "settings" or "application" => ModuleKind.ApplicationSettings,
            _ => throw new ArgumentException($"Unknown documentType '{type}'. Use a ModuleKind name advertised by the adapter.")
        };
    }

    private static List<SelectedNode> SelectNodes(JsonElement metadata, string text, int limit, CancellationToken token)
    {
        if (text.Length is 0 or > 2_048 || !text.StartsWith('$'))
            throw new ArgumentException("path must start with $ and contain at most 2048 characters.");
        // JsonPath.Net implements the language; these guards only bound the query's work.
        // The library has no per-query cancellation/regex deadline, so regex functions are deliberately excluded.
        if (Regex.IsMatch(text, @"\b(?:match|search)\s*\(", RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)))
            throw new ArgumentException("JSONPath regex functions are unavailable because this engine cannot bound their execution time.");
        if (text.Split("..", StringSplitOptions.None).Length > 2 || text.Count(c => c is '$' or '@') > 8)
            throw new ArgumentException("path exceeds the recursive-descent or filter-reference budget.");
        if (text.Contains('?') && (text.Contains("..", StringComparison.Ordinal) || text.Count(c => c == '$') > 1))
            throw new ArgumentException("Recursive or root-rescanning filter expressions exceed this runtime's query-work budget.");
        if (!JsonPath.TryParse(text, out var path)) throw new ArgumentException("Malformed JSONPath expression.");
        var segmentCount = path.Segments.Count();
        if (segmentCount > 64) throw new ArgumentException("path exceeds the 64-segment budget.");
        var root = JsonNode.Parse(metadata.GetRawText());
        var nodes = CountNodes(root, token);
        // Union selectors can repeatedly duplicate the same small subtree and make even a no-match
        // final selector exponential. Account for every union before executing the deferred query.
        long multiplicity = 1;
        foreach (var segment in path.Segments)
        {
            multiplicity *= Math.Max(1, segment.Selectors.Length);
            if (multiplicity > 500_000) break;
        }
        if (multiplicity * Math.Max(1, segmentCount) * nodes > 500_000)
            throw new ArgumentException("path and document exceed the combined query-work budget.");
        var selected = new List<SelectedNode>();
        foreach (var match in path.Evaluate(root).Matches ?? throw new ArgumentException("The JSONPath query could not be evaluated."))
        {
            token.ThrowIfCancellationRequested();
            selected.Add(new SelectedNode(match.Location?.ToString()
                ?? throw new InvalidOperationException("The JSONPath engine omitted a match location."), match.Value));
            if (selected.Count >= limit) break;
        }
        return selected;
    }

    private static int CountNodes(JsonNode? root, CancellationToken token)
    {
        var stack = new Stack<JsonNode?>();
        stack.Push(root);
        var count = 0;
        while (stack.TryPop(out var node))
        {
            token.ThrowIfCancellationRequested();
            if (++count > MaxTreeNodes) throw new ArgumentException("Document exceeds the query node budget.");
            if (node is JsonObject obj) foreach (var child in obj) stack.Push(child.Value);
            else if (node is JsonArray array) foreach (var child in array) stack.Push(child);
        }
        return count;
    }

    private static JsonNode? Clip(JsonNode? value, int depth, ref int budget, ref bool clipped, int maxStringChars = int.MaxValue)
    {
        if (--budget < 0) { clipped = true; return JsonValue.Create("[node budget exceeded]"); }
        if (value is JsonObject obj)
        {
            if (depth <= 0) { clipped = true; return JsonValue.Create("{...}"); }
            var copy = new JsonObject();
            foreach (var property in obj)
            {
                if (budget <= 0 || (maxStringChars != int.MaxValue && copy.Count >= 20)) { clipped = true; break; }
                copy[property.Key] = Clip(property.Value, depth - 1, ref budget, ref clipped, maxStringChars);
            }
            return copy;
        }
        if (value is JsonArray array)
        {
            if (depth <= 0) { clipped = true; return JsonValue.Create("[...]"); }
            var copy = new JsonArray();
            foreach (var child in array)
            {
                if (budget <= 0 || copy.Count >= 20) { clipped = true; break; }
                copy.Add(Clip(child, depth - 1, ref budget, ref clipped, maxStringChars));
            }
            return copy;
        }
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out var text) && text.Length > maxStringChars)
        {
            clipped = true;
            return JsonValue.Create(Preview(text, maxStringChars));
        }
        return value?.DeepClone();
    }

    private static string[] SearchTerms(JsonElement args)
    {
        EnsureObject(args);
        string[] terms;
        if (args.TryGetProperty("queries", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() is 0 or > 10)
                throw new ArgumentException("queries must be an array of 1 to 10 literal strings.");
            terms = list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()! : throw new ArgumentException("Every query must be a literal string.")).ToArray();
        }
        else terms = [Text(args, "query") ?? throw new ArgumentException("queries or query is required.")];
        if (terms.Any(term => string.IsNullOrWhiteSpace(term) || term.Length > 512))
            throw new ArgumentException("Each query must contain 1 to 512 characters.");
        return terms.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static StructureNode AddModule(StructureNode group, ModuleSummary item)
    {
        var current = group;
        var folder = item.Address.FolderPath;
        if (!string.IsNullOrEmpty(folder))
        {
            var pieces = folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var prefix = "";
            foreach (var piece in pieces)
            {
                prefix += (prefix.Length == 0 ? "" : "/") + piece;
                var existing = current.Children.FirstOrDefault(child => child.Type == "folder" && child.Name == piece);
                if (existing is null) { existing = new StructureNode("folder", piece) { FolderPath = prefix }; current.Children.Add(existing); }
                current = existing;
            }
        }
        var node = new StructureNode("item", item.Address.Name)
        { FolderPath = folder, Revision = item.Revision, Description = item.Description };
        current.Children.Add(node);
        return node;
    }

    private static StructureNode? FolderSubtree(StructureNode root, string path)
    {
        var current = root;
        foreach (var part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Children.FirstOrDefault(node => node.Type == "folder" && node.Name == part);
            if (current is null) return null;
        }
        return current;
    }

    private static string NormalizeFolderPath(string value, string group)
    {
        value = value.Replace('\\', '/');
        if (value.StartsWith('/'))
        {
            var prefix = "/" + group + "/";
            if (!value.StartsWith(prefix, StringComparison.Ordinal))
                throw new ArgumentException("The folder reference belongs to a different scope.");
            var afterGroup = value[prefix.Length..];
            var rootSeparator = afterGroup.IndexOf('/');
            value = rootSeparator < 0 ? "" : afterGroup[(rootSeparator + 1)..];
            if (value.EndsWith(".md", StringComparison.Ordinal)) value = value[..^3];
        }
        return value.Trim('/');
    }

    private static JsonElement? MetadataProperty(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in value.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value.Clone();
        return null;
    }

    private static string GroupName(ModuleKind kind) => kind switch
    {
        ModuleKind.Page => "pages", ModuleKind.MasterPage => "masterPages", ModuleKind.UserControlPage => "userControlPages",
        ModuleKind.Table => "tables", ModuleKind.View => "views", ModuleKind.ServerCommand => "serverCommands",
        ModuleKind.SharedCommand => "sharedCommands", ModuleKind.ScheduledTask => "scheduledTasks",
        ModuleKind.Notification => "notifications", ModuleKind.Report => "reports", ModuleKind.BusinessProcess => "businessProcesses",
        ModuleKind.Image => "images", ModuleKind.ResourceFile => "resourceFiles", ModuleKind.Theme => "themes",
        ModuleKind.CellStyle => "cellStyles", ModuleKind.ListViewStyle => "listViewStyles", ModuleKind.DataConnection => "dataConnections",
        ModuleKind.CustomLibrary => "customLibraries", ModuleKind.GlobalVariable => "globalVariables",
        ModuleKind.ApplicationSettings => "applicationSettings", ModuleKind.LanguageResource => "languageResources",
        ModuleKind.Plugin => "plugins", ModuleKind.User => "users", ModuleKind.Role => "roles",
        ModuleKind.Organization => "organizations", ModuleKind.PermissionGroup => "permissionGroups", ModuleKind.Folder => "folders",
        _ => throw new ArgumentException("Unknown module kind.")
    };

    private static JsonElement Encode(object? value) => JsonSerializer.SerializeToElement(value, JsonOptions);
    private static string PropertyPath(string path, string name) => path + "[" + JsonSerializer.Serialize(name) + "]";
    private static string ProjectName(string path) => path.TrimEnd('/', '\\').Split('/', '\\').LastOrDefault() ?? path;
    private static string Preview(string value, int max) => value.Length <= max ? value : value[..max] + "…";
    private static void EnsureObject(JsonElement args)
    { if (args.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object."); }
    private static string? Text(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new ArgumentException(name + " must be a string.");
        return value.GetString();
    }
    private static int Integer(JsonElement args, string name, int fallback, int min, int max)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw new ArgumentException($"{name} must be an integer from {min} to {max}.");
        return number;
    }
    private static bool Boolean(JsonElement args, string name, bool fallback)
    {
        if (!args.TryGetProperty(name, out var value)) return fallback;
        return value.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false,
            _ => throw new ArgumentException(name + " must be a boolean.") };
    }
    private sealed record SelectedNode(string Path, JsonNode? Value);
    private sealed record ContentMatch(string Path, JsonNode? Value);
    private sealed record SearchNode(JsonElement Value, string Path, string? PropertyName, List<SearchNode> Parents);
    private sealed class StructureNode(string type, string name)
    {
        public string Type { get; } = type;
        public string Name { get; } = name;
        public List<StructureNode> Children { get; } = [];
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? FolderPath { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Revision { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Description { get; init; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Comment { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Columns { get; set; }
    }
}
