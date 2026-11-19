using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Core;

public sealed partial class OperationCoordinator
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> uncertainPlatformSessions = new(StringComparer.Ordinal);
    private volatile bool uncertainReceive;
    private volatile bool uncertainNativeService;
    private volatile bool uncertainPluginLifecycle;
    private volatile bool uncertainRuntimeApi;
    public bool IsPlatformExecutionUncertain(string sessionId) => uncertainReceive || uncertainNativeService || uncertainPluginLifecycle || uncertainRuntimeApi || uncertainPlatformSessions.ContainsKey(sessionId);
    private void RequireConfirmedPlatformSession(string sessionId)
    {
        if(IsPlatformExecutionUncertain(sessionId))
            throw new BridgeException("NativeExecutionUncertain","A shared native operation is unconfirmed. Retain its original operation ID and reconcile before another native write.");
    }

    // Construct once per native document, never once per client/request. The very
    // same semaphore serializes legacy reports/collaboration/native and platform.
    public PlatformProjectRouter CreatePlatformRouter(IProjectAdapter adapter, ProjectTarget target) =>
        new(adapter, target, gate, ReservePlatformOperation, status => PublishPlatformOperation(status, target.SessionId),
            () => IsPlatformExecutionUncertain(target.SessionId));

    private void ReservePlatformOperation(string id, string fingerprint)
    {
        lock (registrationGate)
        {
            if (operations.ContainsKey(id)) throw new BridgeException("OperationIdConflict", "Operation ID is already reserved by another platform, report or collaboration request.");
            if (operations.Count >= 1024) throw new BridgeException("ReceiptLimit", "Shared operation receipt retention limit reached.");
            operations[id] = new Entry("platform:" + fingerprint, new(id, "platform", "queued", DateTimeOffset.UtcNow, null));
        }
    }

    private void PublishPlatformOperation(PlatformOperationStatus status, string sessionId)
    {
        if (status.State is not ("queued" or "running") && status.Code is "ReceiveOutcomeUnconfirmed" or "ReceiveReceiptIdMismatch") uncertainReceive = true;
        if (status.State is not ("queued" or "running") && status.Code is "PluginOutcomeUnconfirmed" or "PluginReceiptIdMismatch") uncertainPluginLifecycle = true;
        if(status.State is not ("queued" or "running") && status.Scope==EffectScope.RuntimeDeployment &&
            status.Code is "RuntimeApiOutcomeUnconfirmed" or "RuntimeApiReceiptIdMismatch") uncertainRuntimeApi=true;
        // A native-equivalent service response does not establish an effective
        // backend identity. Switching documents cannot prove that the same
        // unresolved service will no longer be used by another session.
        if (status.State is not ("queued" or "running") && status.Scope == EffectScope.AccountService &&
            status.Code is "NativeServiceOutcomeUnconfirmed" or "NativeServiceReceiptIdMismatch") uncertainNativeService = true;
        if (status.State is not ("queued" or "running") &&
            (status.ExecutionMayContinue || status.Code is "ReceiptIdMismatch" or "SaveUnconfirmed" or "CollaborationUnconfirmed" or "CollaborationPostReadFailed" ||
             status.Scope == EffectScope.AccountService && status.Code is "ServiceOutcomeUnconfirmed" or "NativeServiceOutcomeUnconfirmed" or "NativeServiceReceiptIdMismatch"))
            uncertainPlatformSessions.TryAdd(sessionId, 0);
        if (!operations.TryGetValue(status.OperationId, out var entry)) return;
        var previous = Volatile.Read(ref entry.Receipt);
        Volatile.Write(ref entry.Receipt, previous with { State = status.State, Code = status.Code, Message = status.Message,
            FinishedAt = status.State is "queued" or "running" ? null : DateTimeOffset.UtcNow });
    }
}
