using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Host;

internal sealed class RequestExecution : IDisposable
{
    private readonly object sync = new();
    private readonly CancellationTokenSource cancellation = new();
    private (InstanceInfo Instance, string CallId)? nativeCall;
    private bool disposed;
    private TaskCompletionSource? cancellationFinished;
    private readonly List<Func<Task<PlatformCancellationResult>>> platformCancellations = [];
    internal bool CancellationMayContinue { get; private set; } = true;
    internal async Task<bool> GetCancellationMayContinueAsync()
    {
        Task? finished;
        lock (sync) finished = cancellationFinished?.Task;
        if (finished is not null) await finished;
        return CancellationMayContinue;
    }
    internal void RegisterPlatformCancellation(Func<Task<PlatformCancellationResult>> action)
    {
        lock (sync) { cancellation.Token.ThrowIfCancellationRequested(); platformCancellations.Add(action); }
    }
    internal CancellationToken Token => cancellation.Token;
    internal void NativeStarted(InstanceInfo instance, string callId) { lock (sync) nativeCall = (instance, callId); }
    internal void NativeFinished(string callId) { lock (sync) if (nativeCall?.CallId == callId) nativeCall = null; }
    internal async Task CancelAsync()
    {
        (InstanceInfo Instance, string CallId)? active;
        Func<Task<PlatformCancellationResult>>[] platform;
        TaskCompletionSource finished;
        lock (sync)
        {
            if (disposed || cancellationFinished is not null) return;
            finished = cancellationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellation.Cancel(); active = nativeCall; platform = platformCancellations.ToArray();
        }
        try
        {
            if (platform.Length > 0)
            {
                var mayContinue = false;
                foreach (var cancel in platform)
                {
                    try { mayContinue |= (await cancel()).ExecutionMayContinue; }
                    catch (Exception) { mayContinue = true; }
                }
                CancellationMayContinue = mayContinue;
            }
            if (active is null) return;
            try { await BridgeClient.CallAsync(active.Value.Instance, "native.cancel", new { callId = active.Value.CallId }, requestTimeout: TimeSpan.FromSeconds(3)); }
            catch (Exception error) when (error is BridgeException or IOException) { }
        }
        finally { finished.TrySetResult(); }
    }
    public void Dispose() { lock (sync) { disposed = true; cancellation.Dispose(); } }
}
