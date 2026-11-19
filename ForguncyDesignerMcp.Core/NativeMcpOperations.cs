using System.Collections.Concurrent;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core;

public sealed partial class OperationCoordinator
{
    private sealed class NativeCall
    {
        internal readonly CancellationTokenSource Waiting = new();
        internal readonly object Sync = new();
        internal string State = "queued";
    }
    private readonly ConcurrentDictionary<string, NativeCall> nativeCalls = new(StringComparer.Ordinal);
    private readonly object nativeRegistration = new();

    public async Task<JsonElement> NativeRequestAsync(NativeMcpRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Method) || request.Method.Length > 200 ||
            string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.ProjectPath) ||
            request.Parameters.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            throw new BridgeException("InvalidNativeRequest", "原生请求缺少方法、参数对象或精确工程会话。");
        if (backend is not INativeMcpBackend native)
            throw new BridgeException("NativeMcpUnavailable", "当前设计器扩展未提供独立原生处理器。");
        var callId = request.CallId ?? Guid.NewGuid().ToString("D");
        if (!Guid.TryParseExact(callId, "D", out _)) throw new BridgeException("InvalidNativeCallId", "原生调用编号必须是 UUID。");
        var call = new NativeCall();
        lock (nativeRegistration)
        {
            if (nativeCalls.Count >= 16384) throw new BridgeException("NativeCallLimit", "本设计器实例已保留 16384 个原生调用编号，请重新连接新的设计器实例。");
            if (!nativeCalls.TryAdd(callId, call))
                throw new BridgeException("NativeCallIdConflict", "原生调用编号已被使用；不能通过重发执行状态不明的写操作。");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, call.Waiting.Token);
        var acquired = false;
        try
        {
            await gate.WaitAsync(linked.Token);
            acquired = true;
            linked.Token.ThrowIfCancellationRequested();
            RequireConfirmedPlatformSession(request.SessionId);
            var before = await backend.CaptureAsync();
            CheckReportContext(before, request.SessionId, request.ProjectPath);
            lock (call.Sync)
            {
                linked.Token.ThrowIfCancellationRequested();
                Volatile.Write(ref call.State, "running");
            }
            // Cancellation after this point stops the caller's wait, never releases
            // the shared gate before the actual native operation has completed.
            var result = await native.RequestNativeAsync(request);
            var after = await backend.CaptureAsync();
            CheckReportContext(after, request.SessionId, request.ProjectPath);
            return result.Clone();
        }
        catch (OperationCanceledException) when (!acquired || Volatile.Read(ref call.State) == "queued")
        {
            throw new BridgeException("NativeCancelledBeforeStart", "原生请求已在开始执行前取消。");
        }
        finally
        {
            lock (call.Sync) Volatile.Write(ref call.State, "finished");
            if (acquired) gate.Release();
            // Retain the ID for the process lifetime, just as extension operation
            // IDs are retained, so a duplicate call never repeats a native write.
        }
    }

    public NativeCancellationResult CancelNativeRequest(string callId)
    {
        if (!nativeCalls.TryGetValue(callId, out var call)) return new(false, "unknown", true);
        lock (call.Sync)
        {
            var state = Volatile.Read(ref call.State);
            if (state == "queued")
            {
                call.Waiting.Cancel();
                return new(true, "cancellation-requested-before-start", false);
            }
            return new(true, state == "running" ? "running-may-continue" : "finished", state == "running");
        }
    }
}
