using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Host;

// An in-process native-handler bridge. There is deliberately no HTTP client,
// port discovery or fallback to the built-in MCP listener in this Host.
internal sealed class IntegratedNativeSession(string registry, string projectPath)
{
    private readonly SemaphoreSlim bindingGate = new(1, 1);
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> nativeNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> extensionNames = ToolCatalog.All().Select(x => Wire.Element(x).GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);
    private InstanceInfo? bound;
    private string? sessionId;
    private bool invalidated;
    private bool catalogComplete;
    private JsonElement capabilities;

    internal async Task<InstanceInfo> GetBoundAsync(CancellationToken token)
    {
        await bindingGate.WaitAsync(token);
        try
        {
            if (invalidated) throw new BridgeException("IntegratedSessionInvalidated", "固定工程的实例或文档会话已变化，请重新启动此 Host。");
            var instances = InstanceRegistry.List(registry);
            if (bound is null)
            {
                var matches = instances.Where(x => SamePath(x.ProjectPath, projectPath)).ToArray();
                if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "IntegratedProjectUnavailable" : "IntegratedProjectAmbiguous",
                    "必须恰好一个新鲜设计器实例打开指定的确切工程；不会自动选择其他窗口。");
                var context = await ContextAsync(matches[0], token);
                if (!SamePath(context.ProjectPath, projectPath) || string.IsNullOrWhiteSpace(context.SessionId))
                    throw new BridgeException("IntegratedProjectMismatch", "实例登记与实际设计器工程不一致。");
                bound = matches[0]; sessionId = context.SessionId;
                return bound;
            }
            var current = instances.SingleOrDefault(x => x.InstanceId == bound.InstanceId);
            if (current is null || !SamePath(current.ProjectPath, projectPath))
            {
                invalidated = true;
                throw new BridgeException("IntegratedSessionInvalidated", "固定设计器已退出、重启、切换工程或心跳过期；不会重新选择实例。");
            }
            var latest = await ContextAsync(current, token);
            if (latest.SessionId != sessionId || !SamePath(latest.ProjectPath, projectPath))
            {
                invalidated = true;
                throw new BridgeException("IntegratedSessionInvalidated", "固定文档已重开或切换，请重新启动此 Host。");
            }
            bound = current;
            return current;
        }
        finally { bindingGate.Release(); }
    }

    private static async Task<DesignerContext> ContextAsync(InstanceInfo instance, CancellationToken token)
    {
        var response = await BridgeClient.CallAsync(instance, "designer.get_context", new { instanceId = instance.InstanceId }, token);
        return Unwrap(response).Deserialize<DesignerContext>(Wire.Json) ?? throw new BridgeException("IntegratedContextUnavailable", "无法读取真实设计器上下文。");
    }

    internal async Task<JsonElement> InitializeAsync(JsonElement parameters, RequestExecution execution)
    {
        var result = await SendAsync("initialize", parameters, execution);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.String ||
            !result.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object || !result.TryGetProperty("serverInfo", out _))
            throw new BridgeException("NativeInitializationInvalid", "独立原生处理器未返回完整的初始化元数据。");
        capabilities = caps.Clone();
        var output = JsonNode.Parse(result.GetRawText())!.AsObject();
        var advertised = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } };
        // Only announce methods and notifications actually implemented by this
        // transport. Native media/results remain unmodified on the call path.
        if (HasCapability("resources")) advertised["resources"] = new JsonObject();
        if (HasCapability("prompts")) advertised["prompts"] = new JsonObject();
        if (HasCapability("completions")) advertised["completions"] = new JsonObject();
        output["capabilities"] = advertised;
        var instructions = result.TryGetProperty("instructions", out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() : "";
        output["instructions"] = instructions + "\n本入口由 ForguncyDesignerMcp 独立承载，固定当前工程实例和文档会话。扩展写操作使用原编号查询回执；取消等待不保证中止已开始的原生操作。";
        // Force handler readiness/catalog verification during initialization; a
        // reduced four-tool fallback must be rejected by the native backend.
        await EnsureCatalogAsync(execution);
        return Wire.Element(output);
    }

    internal bool SupportsMethod(string method) => method switch {
        "resources/list" or "resources/templates/list" or "resources/read" => HasCapability("resources"),
        "prompts/list" or "prompts/get" => HasCapability("prompts"),
        "completion/complete" => HasCapability("completions"),
        _ => false
    };

    internal async Task<JsonElement> ListToolsAsync(JsonElement parameters, RequestExecution execution)
    {
        if (!HasCapability("tools")) return Wire.Element(new { tools = ToolCatalog.All() });
        var result = await SendAsync("tools/list", parameters, execution);
        RememberTools(result);
        var output = JsonNode.Parse(result.GetRawText())!.AsObject();
        if (!parameters.TryGetProperty("cursor", out _))
        {
            var tools = output["tools"]!.AsArray();
            foreach (var tool in ToolCatalog.All()) tools.Add(JsonNode.Parse(JsonSerializer.Serialize(tool, Wire.Json)));
        }
        return Wire.Element(output);
    }

    internal async Task<bool> IsNativeToolAsync(string name, RequestExecution execution)
    {
        if (extensionNames.Contains(name)) return false;
        await EnsureCatalogAsync(execution);
        return nativeNames.ContainsKey(name);
    }

    private async Task EnsureCatalogAsync(RequestExecution execution)
    {
        if (catalogComplete) return;
        await catalogGate.WaitAsync(execution.Token);
        try
        {
            if (catalogComplete) return;
            if (!HasCapability("tools")) { catalogComplete = true; return; }
            string? cursor = null;
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            for (var page = 0; page < 100; page++)
            {
                var parameters = cursor is null ? Wire.Element(new { }) : Wire.Element(new { cursor });
                var result = await SendAsync("tools/list", parameters, execution);
                RememberTools(result);
                cursor = result.TryGetProperty("nextCursor", out var next) && next.ValueKind == JsonValueKind.String ? next.GetString() : null;
                if (cursor is null) { catalogComplete = true; return; }
                if (!cursors.Add(cursor)) throw new BridgeException("NativeCatalogInvalid", "原生工具目录分页游标重复。");
            }
            throw new BridgeException("NativeCatalogTooLarge", "原生工具目录超过 100 页。");
        }
        finally { catalogGate.Release(); }
    }

    private void RememberTools(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            throw new BridgeException("NativeCatalogInvalid", "原生工具目录格式无效。");
        var pageNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object || !tool.TryGetProperty("name", out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new BridgeException("NativeCatalogInvalid", "原生工具缺少准确名称。");
            var name = value.GetString()!;
            if (extensionNames.Contains(name)) throw new BridgeException("NativeToolNameCollision", "原生与扩展工具重名，未覆盖任何处理器：" + name);
            if (!pageNames.Add(name)) throw new BridgeException("NativeCatalogInvalid", "同一原生目录页含重名工具。");
            nativeNames[name] = 0;
            if (nativeNames.Count > 2048) throw new BridgeException("NativeCatalogTooLarge", "原生工具目录超过 2048 项。");
        }
    }

    internal async Task<JsonElement> SendAsync(string method, JsonElement parameters, RequestExecution execution)
    {
        var instance = await GetBoundAsync(execution.Token);
        var callId = Guid.NewGuid().ToString("D");
        execution.NativeStarted(instance, callId);
        try
        {
            execution.Token.ThrowIfCancellationRequested();
            var request = new NativeMcpRequest(method, parameters, sessionId!, projectPath, callId);
            return Unwrap(await BridgeClient.CallAsync(instance, "native.request", request, execution.Token, TimeSpan.FromMinutes(5)));
        }
        finally { execution.NativeFinished(callId); }
    }

    private bool HasCapability(string name) => capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty(name, out _);
    private static JsonElement Unwrap(BridgeResponse response) => response.Ok && response.Result is { } result ? result.Clone()
        : throw new BridgeException(response.Code ?? "NativeRequestFailed", response.Message ?? "原生处理器未返回结果。");
    private static bool SamePath(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }
}
