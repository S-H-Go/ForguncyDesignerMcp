using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Host;

/// <summary>Independent runtime injection point. No native MCP, JS or VFS is used.</summary>
public sealed class PlatformMcpSession
{
    private readonly IPlatformToolRuntime runtime;
    private sealed record ToolCatalogSnapshot(PlatformRuntimeContext Context, JsonElement Result, HashSet<string> Names);
    private readonly SemaphoreSlim catalogGate = new(1, 1);
    private ToolCatalogSnapshot? cachedTools;
    private readonly IPlatformApiInvoker api;
    private readonly Func<CancellationToken, Task<PlatformRuntimeContext>> describe;
    private readonly Func<CancellationToken, Task<InstanceInfo?>> bound;
    private readonly Func<string, Task<PlatformCancellationResult>> cancel;
    private readonly HashSet<string> extensions = ToolCatalog.All().Select(x => Wire.Element(x).GetProperty("name").GetString()!).ToHashSet(StringComparer.Ordinal);

    public PlatformMcpSession(IPlatformToolRuntime runtime, PlatformProjectRouter router)
        : this(runtime, router, CreateContextProvider(router),
            _ => Task.FromResult<InstanceInfo?>(null), id => Task.FromResult(router.Cancel(new(router.Target, id)))) { }

    private static Func<CancellationToken, Task<PlatformRuntimeContext>> CreateContextProvider(PlatformProjectRouter router)
    {
        PlatformRuntimeContext? context = null;
        var gate = new SemaphoreSlim(1, 1);
        return async token =>
        {
            await gate.WaitAsync(token);
            try { return context ??= (await router.DescribeAsync(router.Target, token)).Runtime; }
            finally { gate.Release(); }
        };
    }

    public PlatformMcpSession(IPlatformToolRuntime runtime, IPlatformApiInvoker api,
        Func<CancellationToken, Task<PlatformRuntimeContext>> describe,
        Func<CancellationToken, Task<InstanceInfo?>>? getBoundInstance = null,
        Func<string, Task<PlatformCancellationResult>>? cancelOperation = null)
    {
        this.runtime = runtime;
        this.api = api;
        this.describe = describe;
        bound = getBoundInstance ?? (_ => Task.FromResult<InstanceInfo?>(null));
        cancel = cancelOperation ?? (_ => Task.FromResult(new PlatformCancellationResult(false, "unknown", true)));
    }

    internal Task<InstanceInfo?> GetBoundAsync(CancellationToken token) => bound(token);
    internal async Task<JsonElement> InitializeAsync(JsonElement parameters, CancellationToken token)
    {
        var requested = parameters.GetProperty("protocolVersion").GetString();
        var supported = new[] { "2024-11-05", "2025-03-26", "2025-06-18" };
        await ListToolsAsync(token); // Refuse collisions or missing runtime during initialization.
        return Wire.Element(new { protocolVersion = supported.Contains(requested) ? requested : supported[^1],
            capabilities = new { tools = new { listChanged = false }, resources = new { } },
            serverInfo = new { name = "ForguncyDesignerMcp.Platform", version = Wire.PluginVersion },
            instructions = "独立平台入口固定确切工程/实例/文档会话。写入返回原 operationId；使用 api.project.get_operation 查询至终态。不确定执行禁止自动重试。LoadedPendingSave 不是源文件保存成功。" });
    }

    internal async Task<JsonElement> ListToolsAsync(CancellationToken token) =>
        (await GetToolCatalogAsync(token)).Result;

    private async Task<ToolCatalogSnapshot> GetToolCatalogAsync(CancellationToken token)
    {
        await catalogGate.WaitAsync(token);
        try
        {
            var context = await describe(token);
            if (cachedTools is not null && ReferenceEquals(cachedTools.Context, context)) return cachedTools;
            var result = await runtime.ListToolsAsync(context, token);
            cachedTools = ValidateToolCatalog(context, result);
            return cachedTools;
        }
        finally { catalogGate.Release(); }
    }

    private ToolCatalogSnapshot ValidateToolCatalog(PlatformRuntimeContext context, JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            throw new BridgeException("PlatformCatalogInvalid", "Runtime tools/list must return an object with a tools array.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name)) throw new BridgeException("PlatformCatalogInvalid", "Runtime tool names must be unique and nonempty.");
            if (extensions.Contains(name)) throw new BridgeException("PlatformToolNameCollision", "Runtime shadows an extension: " + name);
        }
        return new(context, result.Clone(), names);
    }

    internal async Task<bool> IsToolAsync(string name, CancellationToken token) =>
        (await GetToolCatalogAsync(token)).Names.Contains(name);

    internal async Task<JsonElement> CallToolAsync(string name, JsonElement arguments, RequestExecution execution)
    {
        var context = await describe(execution.Token);
        return (await runtime.CallToolAsync(name, arguments, context,
            new TrackingInvoker(api, cancel, execution, context.Apis), execution.Token)).Clone();
    }

    internal async Task<JsonElement> HandleResourceAsync(string method, JsonElement parameters, CancellationToken token) =>
        (await runtime.HandleResourceAsync(method, parameters, await describe(token), token)).Clone();

    internal static bool SupportsMethod(string method) => method is "resources/list" or "resources/templates/list" or "resources/read";

    private sealed class TrackingInvoker(IPlatformApiInvoker inner, Func<string, Task<PlatformCancellationResult>> cancel,
        RequestExecution execution, IReadOnlyList<PlatformApiDescriptor> catalog) : IPlatformApiInvoker
    {
        public async Task<JsonElement> InvokeAsync(string @namespace, string method, JsonElement parameters, CancellationToken cancellationToken = default)
        {
            // Track IDs structurally, including API calls originating inside JS.
            var p = parameters;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("guard", out var guard)) p = guard;
            if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("operationId", out var id) && id.ValueKind == JsonValueKind.String &&
                catalog.Any(x => x.Namespace == @namespace && x.Method == method && x.MutatesProject))
            {
                var operationId = id.GetString()!;
                execution.RegisterPlatformCancellation(() => cancel(operationId));
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, execution.Token);
            linked.Token.ThrowIfCancellationRequested();
            return await inner.InvokeAsync(@namespace, method, parameters, linked.Token);
        }
    }
}
