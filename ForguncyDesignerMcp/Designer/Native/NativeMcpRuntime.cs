using System.Reflection;
using System.Text.Json;
using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Threading;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Designer.Native;

// Reuses the installed designer's complete MCP/JS/VFS implementation. This class
// neither creates the native MCP HTTP host (zo) nor copies its implementation.
internal sealed class NativeMcpRuntime
{
    private const string AssistantViewName = "Forguncy.AI.Controls.AIAssistantView";
    private readonly Assembly designer;
    private readonly Dispatcher dispatcher;
    private readonly Type viewType;
    private readonly Type handlerType;
    private readonly Assembly protocolCore;
    private readonly JsonSerializerOptions jsonOptions;
    private object? ownedView;
    private Window? ownedWindow;
    private object? verifiedView;
    private string? session;
    internal string? ExecutionUncertainSession { get; private set; }
    internal bool IsExecutionUncertain => ExecutionUncertainSession == session && session is not null;

    internal NativeMcpRuntime(Assembly designer, Dispatcher dispatcher)
    {
        this.designer = designer;
        this.dispatcher = dispatcher;
        viewType = designer.GetType(AssistantViewName, true)!;
        handlerType = designer.GetType("zr", true)!;
        protocolCore = Assembly.Load("ModelContextProtocol.Core");
        jsonOptions = NativeJsonOptions(protocolCore);
        ValidateContract();
    }

    internal void ResetForSession(string currentSession)
    {
        dispatcher.VerifyAccess();
        if (session == currentSession) return;
        session = currentSession;
        ExecutionUncertainSession = null;
    }

    internal static JsonSerializerOptions NativeJsonOptions(Assembly core) =>
        (JsonSerializerOptions)(core.GetType("ModelContextProtocol.McpJsonUtilities", true)!
            .GetProperty("DefaultOptions", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException("McpJsonUtilities.DefaultOptions"));

    private void ValidateContract()
    {
        if (viewType.GetConstructor(Type.EmptyTypes) is null ||
            viewType.GetProperty("Current", NativeAccess.Flags)?.PropertyType != viewType ||
            viewType.GetMethod("EnsureMCPReadyAsync", NativeAccess.Flags, null, [typeof(CancellationToken)], null)?.ReturnType != typeof(Task))
            throw new MissingMemberException("AIAssistantView native runtime contract changed.");
        foreach (var name in new[] { "ListToolsAsync", "CallToolAsync", "ListResourcesAsync", "ReadResourceAsync" })
        {
            var methods = handlerType.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == name).ToArray();
            if (methods.Length != 1 || methods[0].GetParameters().Length != 2 || methods[0].GetParameters()[1].ParameterType != typeof(CancellationToken) ||
                !methods[0].GetParameters()[0].ParameterType.IsGenericType ||
                methods[0].GetParameters()[0].ParameterType.GetGenericTypeDefinition().FullName != "ModelContextProtocol.Server.RequestContext`1" ||
                !methods[0].ReturnType.IsGenericType || methods[0].ReturnType.GetGenericTypeDefinition() != typeof(ValueTask<>))
                throw new MissingMethodException("Native MCP handler contract changed: " + name);
        }
    }

    internal static bool NativeHttpEnabled(Assembly designer)
    {
        var settings = designer.GetType("Forguncy.Properties.Settings", true)!
            .GetProperty("Default", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        return NativeAccess.Get(settings, "EnableDesignerMCPServer") is not false;
    }

    internal static void RequireNativeHttpDisabled(Assembly designer)
    {
        if (NativeHttpEnabled(designer))
            throw new BridgeException("NativeMcpHttpMustBeDisabled", "请先关闭设计器内置 MCP 服务；插件不会修改设置或启动内置 MCP HTTP 监听。");
    }

    internal async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        dispatcher.VerifyAccess();
        RequireNativeHttpDisabled(designer);
        var view = viewType.GetProperty("Current", NativeAccess.Flags)!.GetValue(null);
        if (view is null)
        {
            if (ownedView is null)
            {
                ownedView = Activator.CreateInstance(viewType) ?? throw new InvalidOperationException("Cannot create native AI runtime view.");
                // WebView2 requires a real presentation source. This window only
                // owns our new control; it never reparents or closes designer UI.
                ownedWindow = new Window
                {
                    Content = (UIElement)ownedView, Width = 1024, Height = 768,
                    Left = -32000, Top = -32000, Opacity = 0, ShowActivated = false,
                    ShowInTaskbar = false, WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize, Title = "Forguncy MCP plugin runtime",
                    Owner = Application.Current?.MainWindow
                };
                ownedWindow.Show();
            }
            view = ownedView;
            // The native Loaded handler establishes AIAssistantView.Current and
            // starts the coalesced InitWebViewAsync operation.
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!ReferenceEquals(viewType.GetProperty("Current", NativeAccess.Flags)!.GetValue(null), view))
                throw new BridgeException("NativeRuntimeNotAttached", "原生助手运行时尚未完成加载，未发送 MCP 请求。");
        }
        var task = (Task)Invoke(viewType.GetMethod("EnsureMCPReadyAsync", NativeAccess.Flags, null, [typeof(CancellationToken)], null)!, view, [cancellationToken])!;
        try { await task; }
        finally { RequireNativeHttpDisabled(designer); }
        if (!ReferenceEquals(viewType.GetProperty("Current", NativeAccess.Flags)!.GetValue(null), view))
            throw new BridgeException("NativeRuntimeChanged", "初始化期间原生助手运行时已改变，请重新读取当前工程。");
        if (!ReferenceEquals(verifiedView, view))
        {
            var registry = await RequestAsync("tools/list", JsonSerializer.SerializeToElement(new { }), cancellationToken);
            VerifyCompleteToolRegistry(registry);
            verifiedView = view;
        }
    }

    internal async Task<JsonElement> RequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        dispatcher.VerifyAccess();
        RequireNativeHttpDisabled(designer);
        if (method == "tools/call" && IsExecutionUncertain)
            throw new BridgeException("NativeExecutionUncertain", "之前的原生工具已超时且可能仍在执行；当前文档会话禁止继续写入，请先核对执行结果并重新打开目标文档。");
        if (method == "initialize")
        {
            // This is the plugin transport's handshake. Tool/resource registries
            // themselves always come from the live native handlers below.
            return JsonSerializer.SerializeToElement(new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { tools = new { listChanged = false }, resources = new { subscribe = false, listChanged = false } },
                serverInfo = new { name = "ForguncyDesignerMcp", version = Wire.PluginVersion },
                nativeRuntime = new { designerVersion = NativeAccess.SupportedVersion, officialMcpHttpEnabled = false }
            }, Wire.Json);
        }
        if (method == "resources/templates/list") return JsonSerializer.SerializeToElement(new { resourceTemplates = Array.Empty<object>() }, Wire.Json);
        var name = method switch
        {
            "tools/list" => "ListToolsAsync", "tools/call" => "CallToolAsync",
            "resources/list" => "ListResourcesAsync", "resources/read" => "ReadResourceAsync",
            _ => throw new BridgeException("UnsupportedNativeMcpMethod", "原生 MCP 未提供此请求：" + method)
        };
        var handler = handlerType.GetMethod(name, BindingFlags.Public | BindingFlags.Static)!;
        var context = CreateRequestContext(handler, parameters, jsonOptions);
        try
        {
            var valueTask = Invoke(handler, null, [context, cancellationToken])!;
            var task = (Task)Invoke(valueTask.GetType().GetMethod("AsTask", Type.EmptyTypes)!, valueTask, [])!;
            // Do not apply WaitAsync/cancel this await: the operation gate stays
            // held until the native handler actually terminates.
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            var json = JsonSerializer.SerializeToElement(result, result.GetType(), jsonOptions);
            if (method == "tools/list") VerifyCompleteToolRegistry(json);
            if (method == "tools/call" && IsNativeDeadlineResult(json, parameters))
            {
                ExecutionUncertainSession = session;
                throw new BridgeException("NativeExecutionUncertain", "原生工具已达到等待期限，但 JavaScript 可能仍在执行；本次会话已禁止继续写入，不能将此结果视为操作已停止。");
            }
            return json;
        }
        catch (TimeoutException) when (method == "tools/call")
        {
            ExecutionUncertainSession = session;
            throw new BridgeException("NativeExecutionUncertain", "原生工具等待超时，执行结果尚不明确；本次文档会话已禁止继续写入。");
        }
        finally { RequireNativeHttpDisabled(designer); }
    }

    internal static object CreateRequestContext(MethodInfo handler, JsonElement parameters, JsonSerializerOptions options)
    {
        var contextType = handler.GetParameters()[0].ParameterType;
        var constructor = contextType.GetConstructors().Single(c => c.GetParameters().Length == 1 &&
            c.GetParameters()[0].ParameterType.FullName == "ModelContextProtocol.Server.IMcpServer");
        var server = DispatchProxy.Create(constructor.GetParameters()[0].ParameterType, typeof(NativeMcpServerContextProxy));
        var context = constructor.Invoke([server]);
        var parameterType = contextType.GetGenericArguments()[0];
        var json = parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null ? "{}" : parameters.GetRawText();
        var request = JsonSerializer.Deserialize(json, parameterType, options) ?? throw new BridgeException("InvalidNativeMcpParams", "原生 MCP 参数不能为空。");
        contextType.GetProperty("Params")!.SetValue(context, request);
        return context;
    }

    internal static bool IsNativeDeadlineResult(JsonElement result, JsonElement parameters)
    {
        if (!result.TryGetProperty("isError", out var error) || error.ValueKind != JsonValueKind.True ||
            !parameters.TryGetProperty("name", out var nameValue) || nameValue.ValueKind != JsonValueKind.String ||
            !result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return false;
        var name = nameValue.GetString();
        var deadline = name == "execute_code" ? 60000 : 30000;
        var exact = $"Error: MCP tool '{name}' timed out after {deadline}ms";
        return content.EnumerateArray().Any(item => item.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String && text.GetString() == exact);
    }

    internal static void VerifyCompleteToolRegistry(JsonElement result)
    {
        // The native handler combines its four bootstrap tools with the live
        // JS/VFS registry. Bootstrap tools alone are not a complete runtime.
        string[] required = ["execute_code", "find_references", "get_assistant_def", "get_document_content", "get_project_structure",
            "list_files", "read_file", "search_document_content", "search_files", "search_knowledge_base"];
        if (!result.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
            throw new BridgeException("NativeToolRegistryIncomplete", "原生工具清单格式不完整，运行时尚未就绪。");
        var names = tools.EnumerateArray().Select(tool => tool.TryGetProperty("name", out var name) ? name.GetString() : null).ToHashSet(StringComparer.Ordinal);
        var missing = required.Where(name => !names.Contains(name)).ToArray();
        if (missing.Length != 0)
            throw new BridgeException("NativeToolRegistryIncomplete", "原生运行时返回的工具清单不完整，缺少：" + string.Join(", ", missing));
    }

    private static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    {
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}

// Adapter for this plugin's transport context, not a replacement tool backend.
// Native zr handlers only read RequestContext.Params. Its constructor requires
// IMcpServer.Services; all actual work still runs through the installed zr/zs.
public class NativeMcpServerContextProxy : DispatchProxy
{
    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type serviceType) => null; }
    private static readonly IServiceProvider Services = new EmptyServices();
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
    {
        "get_Services" => Services,
        _ => throw new NotSupportedException("The native MCP transport context does not implement " + targetMethod?.Name)
    };
}
