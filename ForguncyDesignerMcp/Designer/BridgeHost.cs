using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using ForguncyDesignerMcp.Designer.Native;
using Application = System.Windows.Application;

namespace ForguncyDesignerMcp.Designer;

public static class BridgeHost
{
    private static int scheduled;
    private static BridgeServer? server;
    private static NativeDesignerBackend? backend;
    private static OperationCoordinator? coordinator;
    private static DispatcherTimer? heartbeat;
    private static InstanceInfo? instance;
    private static string? registryFile;
    private static PlatformSessionBinding? platform;
    private static EmbeddedMcpController? embedded;
    private static EmbeddedMcpUi? embeddedUi;

    public static void Schedule()
    {
        var application = Application.Current;
        if (application?.GetType().Assembly.GetName().Name != "Forguncy" || application.Dispatcher.HasShutdownStarted) return;
        if (Interlocked.Exchange(ref scheduled, 1) != 0) return;
        application.Dispatcher.BeginInvoke(new Action(() => Start(application)));
    }

    public static void ShowManagement()
    {
        var application = Application.Current;
        if (application?.GetType().Assembly.GetName().Name != "Forguncy" || application.Dispatcher.HasShutdownStarted || application.Dispatcher.HasShutdownFinished) return;
        Schedule();
        var designer = application.MainWindow;
        application.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (designer?.IsVisible != true || application.MainWindow != designer) return;
            if (embeddedUi is not null) embeddedUi.ShowAfterCellEdit(designer);
            else System.Windows.MessageBox.Show("MCP 管理入口尚未就绪，请检查插件日志后重试。", "Forguncy MCP");
        }));
    }

    private static void Start(Application application)
    {
        try
        {
            backend = new(application.Dispatcher);
            coordinator = new(backend);
            backend.SetPlatformSafetyProbe(coordinator.IsPlatformExecutionUncertain);
            using var process = Process.GetCurrentProcess();
            instance = new(Wire.Version, process.Id, process.StartTime.ToUniversalTime().Ticks,
                "ForguncyDesignerMcp-" + process.Id + "-" + Guid.NewGuid().ToString("N"),
                backend.DesignerVersion, Wire.PluginVersion, null, null, DateTimeOffset.UtcNow);
            backend.SetInstanceIdentity(instance.InstanceId);
            platform = new(backend, coordinator);
            server = new(instance.PipeName, HandleAsync); server.Start();
            heartbeat = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
            heartbeat.Tick += (_, _) => Publish(); heartbeat.Start(); Publish();
            application.Exit += (_, _) => Stop();
            try
            {
                embedded = new EmbeddedMcpController(); embedded.UpdateInstance(instance!);
                embeddedUi = new EmbeddedMcpUi(embedded, application.Dispatcher);
                if (embedded.Settings.AutoStart) _ = StartEmbeddedAsync(embedded);
            }
            catch (Exception error) { Log(error); }
        }
        catch (Exception error) { Interlocked.Exchange(ref scheduled, 0); Log(error); }
    }

    private static void Publish()
    {
        try
        {
            var context = backend!.ObserveDocument();
            instance = instance! with { SessionId = context.SessionId, ProjectPath = context.ProjectPath, Heartbeat = DateTimeOffset.UtcNow };
            embedded?.UpdateInstance(instance);
            InstanceRegistry.Write(InstanceRegistry.DefaultDirectory, instance);
            registryFile = Path.Combine(InstanceRegistry.DefaultDirectory, instance.ProcessId + "-" + instance.ProcessStartTicks + ".json");
        }
        catch (Exception error) { Log(error); }
    }

    private static async Task<BridgeResponse> HandleAsync(BridgeRequest request)
    {
        try { return await HandleCoreAsync(request); }
        catch (Exception error) { Log(error); throw; }
    }

    private static async Task<BridgeResponse> HandleCoreAsync(BridgeRequest request)
    {
        switch (request.Method)
        {
            case "platform.describe":
                var describe = PlatformJson.Read<PlatformDescribeRequest>(request.Arguments);
                var describeRouter = await platform!.GetAsync(describe.Target);
                return BridgeResponse.Success(PlatformJson.Element(await describeRouter.DescribeAsync(describe.Target)));
            case "platform.status":
                var statusRequest = PlatformJson.Read<PlatformDescribeRequest>(request.Arguments);
                return BridgeResponse.Success(await backend!.CapturePlatformStatusAsync(statusRequest.Target));
            case "platform.invoke":
                var apiRequest = PlatformJson.Read<PlatformApiRequest>(request.Arguments);
                var apiRouter = await platform!.GetAsync(apiRequest.Target);
                return BridgeResponse.Success(await apiRouter.InvokeAsync(apiRequest));
            case "platform.cancel":
                return BridgeResponse.Success(PlatformJson.Element(platform!.Cancel(PlatformJson.Read<PlatformCancelRequest>(request.Arguments))));
            case "native.request":
                var nativeRequest = request.Arguments.Deserialize<NativeMcpRequest>(Wire.Json)
                    ?? throw new BridgeException("InvalidArguments", "原生 MCP 请求为空。");
                return BridgeResponse.Success(await coordinator!.NativeRequestAsync(nativeRequest));
            case "native.cancel":
                return BridgeResponse.Success(coordinator!.CancelNativeRequest(request.Arguments.GetProperty("callId").GetString()!));
            case "designer.get_context": return BridgeResponse.Success(await backend!.CaptureAsync());
            case "designer.get_runtime_info":
                return BridgeResponse.Success(await backend!.CaptureRuntimeInfoAsync(PlatformJson.Read<ProjectTarget>(request.Arguments)));
            case "designer.list_capabilities":
                var legacyV12 = backend!.DesignerVersion == NativeAccess.SupportedVersion;
                return BridgeResponse.Success(new[] {
                    new Capability("collaboration.refresh", true, "重读本地协同状态并通知界面；不保存或同步远端", "native-contract-verified; live-designer-acceptance-pending"),
                    new Capability("collaboration.checkout", legacyV12, "通过原生协同签出并同步锁", legacyV12 ? "native-contract-verified; live-designer-acceptance-pending" : "use-version-platform-adapter"),
                    new Capability("collaboration.checkin", legacyV12, "通过原生签入指定的普通修改模块，并核对提交和锁", legacyV12 ? "native-contract-verified; live-designer-acceptance-pending" : "use-version-platform-adapter"),
                    new Capability("report.get", legacyV12, "读取现有报表 JSON/RDLX 内存文件组及源文件基线", legacyV12 ? "native-component-tested; live-designer-acceptance-pending" : "use-version-platform-adapter"),
                    new Capability("report.update", legacyV12, "成组更新现有报表并原生保存，核对内存及源文件；不签入", legacyV12 ? "native-component-tested; live-designer-acceptance-pending" : "use-version-platform-adapter", "要求已保存、报表编辑器关闭、报表树完整加载及本人原生签出登记；不创建、删除、移动或改报表类型"),
                    new Capability("platform", true, "自有运行时与当前精确版本适配器；具体CRUD能力以platform.describe为准", "integration-in-progress"),
                    new Capability("report.delete", false, "删除报表 JSON/RDLX 文件组", "not-implemented", "本版仅支持现有报表读取及更新"),
                    new Capability("page.native_controls", false, "原生控件编辑", "not-implemented", "后续阶段逐项适配，首版不开放"),
                    new Capability("resource.workflow", false, "流程编辑", "not-implemented", "后续阶段逐项适配") });
            case "report.get":
                return BridgeResponse.Success(await coordinator!.ReadReportAsync(
                    request.Arguments.GetProperty("reportName").GetString()!,
                    request.Arguments.GetProperty("sessionId").GetString()!,
                    request.Arguments.GetProperty("projectPath").GetString()!));
            case "report.update":
                var reportUpdate = request.Arguments.Deserialize<ReportUpdateRequest>(Wire.Json)
                    ?? throw new BridgeException("InvalidArguments", "报表请求为空。");
                return BridgeResponse.Success(coordinator!.SubmitReport(reportUpdate));
            case "collaboration.list_modules":
                var context = await backend!.CaptureAsync();
                var names = request.Arguments.TryGetProperty("modules", out var filter) && filter.ValueKind == JsonValueKind.Array
                    ? filter.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal) : null;
                return BridgeResponse.Success(context with { Modules = names is null ? context.Modules : context.Modules.Where(x => names.Contains(x.Name)).ToArray() });
            case "designer.get_operation":
                return BridgeResponse.Success(coordinator!.Get(request.Arguments.GetProperty("operationId").GetString()!));
            case "collaboration.refresh":
            case "collaboration.checkout":
            case "collaboration.checkin":
                var mutation = request.Arguments.Deserialize<MutationRequest>(Wire.Json)
                    ?? throw new BridgeException("InvalidArguments", "请求参数为空。");
                return BridgeResponse.Success(coordinator!.Submit(request.Method, mutation));
            default: return BridgeResponse.Failure("UnknownMethod", "未实现的设计器操作。");
        }
    }

    private static void Stop()
    {
        heartbeat?.Stop();
        embeddedUi?.Dispose(); embeddedUi = null;
        if (embedded is not null) { _ = StopEmbeddedAsync(embedded); embedded = null; }
        if (registryFile is not null) { try { File.Delete(registryFile); } catch (IOException) { } }
        if (server is not null) _ = server.DisposeAsync();
    }

    private static async Task StartEmbeddedAsync(EmbeddedMcpController controller)
    { try { await controller.StartAsync().ConfigureAwait(false); } catch (Exception error) { Log(error); } }
    private static async Task StopEmbeddedAsync(EmbeddedMcpController controller)
    { try { await controller.DisposeAsync().ConfigureAwait(false); } catch (Exception error) { Log(error); } }

    private static void Log(Exception error)
    {
        var cause = error.GetBaseException();
        var message = DateTimeOffset.Now.ToString("O") + " [ForguncyDesignerMcp] " +
            cause.GetType().FullName + ": " + cause.Message + Environment.NewLine + cause.StackTrace;
        Trace.WriteLine(message);
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForguncyDesignerMcp", "logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, Environment.ProcessId + ".log"), message + Environment.NewLine);
        }
        catch (Exception logError) when (logError is IOException or UnauthorizedAccessException) { }
    }
}
