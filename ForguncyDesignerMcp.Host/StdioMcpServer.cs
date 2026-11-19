using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Host;

public sealed class StdioMcpServer
{
    private readonly string registry;
    private readonly IntegratedNativeSession? native;
    private readonly PlatformMcpSession? platform;
    private readonly ConcurrentDictionary<string, RequestExecution> requests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim outputGate = new(1, 1);
    private bool initialized;
    private bool ready;

    public StdioMcpServer(string registry, string? projectPath = null)
    {
        this.registry = registry;
        native = projectPath is null ? null : new(registry, Path.GetFullPath(projectPath));
    }

    public StdioMcpServer(string registry, PlatformMcpSession platform)
    {
        this.registry = registry;
        this.platform = platform;
    }

    public async Task RunAsync(TextReader input, TextWriter output)
    {
        var running = new List<Task>();
        async Task WriteAsync(object value)
        {
            await outputGate.WaitAsync();
            try { await output.WriteLineAsync(JsonSerializer.Serialize(value, Wire.Json)); await output.FlushAsync(); }
            finally { outputGate.Release(); }
        }
        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (Encoding.UTF8.GetByteCount(line) > PipeFrames.MaximumBytes) { await WriteAsync(Error(null, -32600, "Request too large")); continue; }
            JsonElement root;
            try { using var parsed = JsonDocument.Parse(line); root = parsed.RootElement.Clone(); }
            catch (JsonException) { await WriteAsync(Error(null, -32700, "Parse error")); continue; }
            if (!ValidMessage(root)) { await WriteAsync(Error(null, -32600, "Invalid request")); continue; }
            var method = root.GetProperty("method").GetString()!;
            if (!root.TryGetProperty("id", out var id))
            {
                if (method == "notifications/initialized" && initialized) ready = true;
                else if (method == "notifications/cancelled" && root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("requestId", out var cancelled) && ValidId(cancelled) && requests.TryGetValue(IdKey(cancelled), out var pending)) await pending.CancelAsync();
                continue;
            }
            if (!ValidId(id)) { await WriteAsync(Error(null, -32600, "Invalid request id")); continue; }
            var key = IdKey(id);
            var execution = new RequestExecution();
            if (!requests.TryAdd(key, execution)) { execution.Dispose(); await WriteAsync(Error(id.Clone(), -32600, "Request id is already active")); continue; }
            async Task RunRequestAsync()
            {
                try
                {
                    var value = await HandleAsync(root, execution);
                    execution.Token.ThrowIfCancellationRequested();
                    await WriteAsync(value);
                }
                catch (Exception) when (execution.Token.IsCancellationRequested)
                {
                    await WriteAsync(Error(id.Clone(), -32800, "Request cancelled. Query original operation IDs; do not repeat a write whose outcome is unknown.", new { executionMayContinue = await execution.GetCancellationMayContinueAsync() }));
                }
                catch (BridgeException error) { await WriteAsync(Error(id.Clone(), -32000, error.Message, new { code = error.Code })); }
                catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
                { await WriteAsync(Error(id.Clone(), -32602, "Invalid parameters")); }
                catch (IOException) { await WriteAsync(Error(id.Clone(), -32000, "Designer connection failed; a native operation may still be running.", new { executionMayContinue = true })); }
                catch (Exception) { await WriteAsync(Error(id.Clone(), -32603, "Internal error")); }
                finally { requests.TryRemove(key, out _); execution.Dispose(); }
            }
            // Preserve lifecycle ordering; ready requests remain cancellable.
            if (method == "initialize" || !ready) await RunRequestAsync();
            else running.Add(RunRequestAsync());
            if (running.Count > 256) running.RemoveAll(t => t.IsCompleted);
        }
        await Task.WhenAll(running);
    }

    public async Task<string?> DispatchAsync(string message)
    {
        JsonElement root;
        try { using var doc=JsonDocument.Parse(message); root=doc.RootElement.Clone(); }
        catch(JsonException) { return JsonSerializer.Serialize(Error(null,-32700,"Parse error"),Wire.Json); }
        if(!ValidMessage(root)) return JsonSerializer.Serialize(Error(null,-32600,"Invalid request"),Wire.Json);
        var method=root.GetProperty("method").GetString()!;
        if(!root.TryGetProperty("id",out var id))
        {
            if(method=="notifications/initialized" && initialized) ready=true;
            else if(method=="notifications/cancelled" && root.TryGetProperty("params",out var p) && p.ValueKind==JsonValueKind.Object && p.TryGetProperty("requestId",out var cancelled) && ValidId(cancelled) && requests.TryGetValue(IdKey(cancelled),out var pending)) await pending.CancelAsync();
            return null;
        }
        if(!ValidId(id)) return JsonSerializer.Serialize(Error(null,-32600,"Invalid request id"),Wire.Json);
        var key=IdKey(id); using var execution=new RequestExecution();
        if(!requests.TryAdd(key,execution)) return JsonSerializer.Serialize(Error(id.Clone(),-32600,"Request id is already active"),Wire.Json);
        object result;
        try { result=await HandleAsync(root,execution); execution.Token.ThrowIfCancellationRequested(); }
        catch(Exception) when(execution.Token.IsCancellationRequested) { result=Error(id.Clone(),-32800,"Request cancelled. Query original operation IDs; do not repeat a write whose outcome is unknown.",new { executionMayContinue=await execution.GetCancellationMayContinueAsync() }); }
        catch(BridgeException error) { result=Error(id.Clone(),-32000,error.Message,new { code=error.Code }); }
        catch(Exception error) when(error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException) { result=Error(id.Clone(),-32602,"Invalid parameters"); }
        catch(IOException) { result=Error(id.Clone(),-32000,"Designer connection failed; a native operation may still be running.",new { executionMayContinue=true }); }
        catch(Exception) { result=Error(id.Clone(),-32603,"Internal error"); }
        finally { requests.TryRemove(key,out _); }
        return JsonSerializer.Serialize(result,Wire.Json);
    }

    private async Task<object> HandleAsync(JsonElement root, RequestExecution execution)
    {
        var id = root.GetProperty("id").Clone();
        var method = root.GetProperty("method").GetString()!;
        var parameters = root.TryGetProperty("params", out var p) ? p : Wire.Element(new { });
        switch (method)
        {
            case "initialize":
                if (initialized) return Error(id, -32600, "Already initialized");
                var requested = parameters.GetProperty("protocolVersion").GetString();
                if (string.IsNullOrWhiteSpace(requested)) return Error(id, -32602, "Missing protocolVersion");
                object initialization;
                if (platform is not null) initialization = await platform.InitializeAsync(parameters, execution.Token);
                else if (native is not null) initialization = await native.InitializeAsync(parameters, execution);
                else
                {
                    var supported = new[] { "2024-11-05", "2025-03-26", "2025-06-18" };
                    initialization = new { protocolVersion = supported.Contains(requested) ? requested : supported[^1], capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "ForguncyDesignerMcp", version = Wire.PluginVersion }, instructions = "先发现实例，再核对工程上下文。扩展写操作返回操作编号；查询至终态。超时后查询原编号，不要重复写入。" };
                }
                initialized = true;
                return Result(id, initialization);
            case "ping": return Result(id, new { });
            case "tools/list":
                if (!ready) return Error(id, -32000, "Initialize first");
                if (platform is not null)
                {
                    var catalog = JsonNode.Parse((await platform.ListToolsAsync(execution.Token)).GetRawText())!.AsObject();
                    foreach (var tool in ToolCatalog.All()) catalog["tools"]!.AsArray().Add(JsonNode.Parse(JsonSerializer.Serialize(tool, Wire.Json)));
                    return Result(id, Wire.Element(catalog));
                }
                return Result(id, native is null ? new { tools = ToolCatalog.All() } : await native.ListToolsAsync(parameters, execution));
            case "tools/call":
                if (!ready) return Error(id, -32000, "Initialize first");
                var name = parameters.GetProperty("name").GetString()!;
                if (platform is not null && await platform.IsToolAsync(name, execution.Token))
                    return Result(id, await platform.CallToolAsync(name, parameters.TryGetProperty("arguments", out var pa) ? pa : Wire.Element(new { }), execution));
                if (native is not null && await native.IsNativeToolAsync(name, execution)) return Result(id, await native.SendAsync("tools/call", parameters, execution));
                var arguments = parameters.TryGetProperty("arguments", out var a) ? a : Wire.Element(new { });
                var result = await CallExtensionAsync(name, arguments, execution);
                if (name == "get_designer_runtime_info" && result.Ok && result.Result is { } runtimeInfo)
                    return Result(id, new { content = new[] { new { type = "text", text = runtimeInfo.GetRawText() } }, structuredContent = runtimeInfo, isError = false });
                return Result(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(result.Ok ? (object?)result.Result : new { result.Code, result.Message }, Wire.Json) } }, isError = !result.Ok });
            default:
                if (!ready) return Error(id, -32000, "Initialize first");
                if (platform is not null && PlatformMcpSession.SupportsMethod(method))
                    return Result(id, await platform.HandleResourceAsync(method, parameters, execution.Token));
                if (native is not null && native.SupportsMethod(method)) return Result(id, await native.SendAsync(method, parameters, execution));
                return Error(id, -32601, "Method not found");
        }
    }

    private async Task<BridgeResponse> CallExtensionAsync(string name, JsonElement arguments, RequestExecution execution)
    {
        var platformInstance = platform is null ? null : await platform.GetBoundAsync(execution.Token);
        if (name == "designer.list_instances") return BridgeResponse.Success(platformInstance is not null ? new[] { platformInstance } : native is null ? InstanceRegistry.List(registry) : new[] { await native.GetBoundAsync(execution.Token) });
        if (!new[] { "get_designer_runtime_info", "designer.get_context", "designer.list_capabilities", "designer.get_operation", "collaboration.list_modules", "collaboration.refresh", "collaboration.checkout", "collaboration.checkin", "report.get", "report.update" }.Contains(name))
            return BridgeResponse.Failure("UnknownTool", "未实现的工具。");
        try
        {
            var instanceId = arguments.GetProperty("instanceId").GetString();
            InstanceInfo instance;
            if (platformInstance is not null)
            {
                instance = platformInstance;
                if (instanceId != instance.InstanceId) throw new BridgeException("PlatformInstanceMismatch", "Host is bound to another Designer instance.");
            }
            else if (native is not null)
            {
                instance = await native.GetBoundAsync(execution.Token);
                if (instanceId != instance.InstanceId) throw new BridgeException("IntegratedInstanceMismatch", "此 Host 已固定其他设计器实例。");
            }
            else instance = InstanceRegistry.List(registry, includeStale: name == "designer.get_operation").SingleOrDefault(x => x.InstanceId == instanceId)
                ?? throw new BridgeException("InstanceUnavailable", "实例已退出、重启或心跳过期，请重新发现设计器。");
            return await BridgeClient.CallAsync(instance, name == "get_designer_runtime_info" ? "designer.get_runtime_info" : name, arguments, execution.Token);
        }
        catch (BridgeException error) { return BridgeResponse.Failure(error.Code, error.Message); }
        catch (IOException) { return BridgeResponse.Failure("ConnectionFailed", "无法连接设计器。若已提交操作，请查询原 operationId；不要重复写入。"); }
    }

    private static bool ValidMessage(JsonElement root) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("jsonrpc", out var protocol) && protocol.ValueKind == JsonValueKind.String && protocol.GetString() == "2.0" && root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String;
    private static bool ValidId(JsonElement id) => id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
    private static string IdKey(JsonElement id) => id.ValueKind == JsonValueKind.String ? "s:" + id.GetString() : "n:" + id.GetRawText();
    private static object Result(object? id, object result) => new { jsonrpc = "2.0", id, result };
    private static object Error(object? id, int code, string message, object? data = null) => new { jsonrpc = "2.0", id, error = new { code, message, data } };
}
