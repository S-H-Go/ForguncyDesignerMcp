using System.IO;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Designer.Native;

internal sealed partial class NativeDesignerBackend
{
    private NativeMcpRuntime? nativeMcpRuntime;

    internal bool HasUncertainNativeExecution(string expectedSessionId) =>
        nativeMcpRuntime?.ExecutionUncertainSession == expectedSessionId;

    public async Task<JsonElement> RequestNativeAsync(NativeMcpRequest request) =>
        await dispatcher.InvokeAsync(async () =>
        {
            void VerifyTarget()
            {
                var (document, _) = RequireSession(request.SessionId);
                if (!string.Equals(Path.GetFullPath(request.ProjectPath), Path.GetFullPath(currentPath!), StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("ProjectMismatch", "原生 MCP 请求的工程路径与当前文档不一致。");
                if (NativeAccess.Get(document, "Opening") is true || NativeAccess.Get(document, "IsImporting") is true)
                    throw new BridgeException("DesignerBusy", "设计器正在打开或导入工程，未执行原生 MCP 请求。");
            }
            VerifyTarget();
            NativeAccess.RequireV12Legacy(assembly);
            if (request.Method == "tools/call" && platformExecutionUncertain?.Invoke(request.SessionId) is true)
                throw new BridgeException("PlatformExecutionUncertain", "平台操作结果尚不明确；当前会话不能通过旧入口继续执行原生工具。");
            nativeMcpRuntime ??= new NativeMcpRuntime(assembly, dispatcher);
            nativeMcpRuntime.ResetForSession(request.SessionId);
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await nativeMcpRuntime.EnsureReadyAsync(deadline.Token);
            VerifyTarget();
            var result = await nativeMcpRuntime.RequestAsync(request.Method, request.Parameters, deadline.Token);
            VerifyTarget();
            return result;
        }).Task.Unwrap();
}
