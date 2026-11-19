using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Host;

// The adapter/router lives in Designer. Host processes never own the native gate.
public sealed class PlatformPipeSession : IPlatformApiInvoker
{
    private readonly string registry;
    private readonly string projectPath;
    private readonly string? fixedInstanceId;
    private readonly string? fixedSessionId;
    private readonly InstanceInfo? expectedInstance;
    private readonly SemaphoreSlim binding = new(1, 1);
    private InstanceInfo? instance;
    private ProjectTarget? target;
    private volatile PlatformDescription? cachedDescription;
    private bool invalidated;

    public PlatformPipeSession(string registry, string projectPath, string? instanceId = null, string? sessionId = null)
    {
        if (!Path.IsPathFullyQualified(projectPath)) throw new ArgumentException("An absolute project path is required.", nameof(projectPath));
        this.registry = registry;
        this.projectPath = Path.GetFullPath(projectPath);
        if (instanceId is not null && string.IsNullOrWhiteSpace(instanceId) || sessionId is not null && (string.IsNullOrWhiteSpace(sessionId) || instanceId is null))
            throw new ArgumentException("A fixed session requires a nonempty fixed instance ID.");
        fixedInstanceId = instanceId; fixedSessionId = sessionId;
    }

    public PlatformMcpSession CreateMcpSession(IPlatformToolRuntime runtime) =>
        new(runtime, this, async token => (cachedDescription ?? await DescribeCoreAsync(false, token)).Runtime,
            async token => { await DescribeCoreAsync(false, token); return instance; }, CancelAsync);

    public PlatformPipeSession(string registry, InstanceInfo expected)
        : this(registry, expected.ProjectPath ?? throw new ArgumentException("A saved project is required.", nameof(expected)), expected.InstanceId, expected.SessionId)
    {
        if (string.IsNullOrWhiteSpace(expected.SessionId) || string.IsNullOrWhiteSpace(expected.PipeName) || string.IsNullOrWhiteSpace(expected.DesignerVersion))
            throw new ArgumentException("A complete Designer session snapshot is required.", nameof(expected));
        expectedInstance = expected;
    }

    public Task<PlatformDescription> DescribeAsync(CancellationToken token = default) => DescribeCoreAsync(true, token);

    private async Task<PlatformDescription> DescribeCoreAsync(bool refresh, CancellationToken token)
    {
        await binding.WaitAsync(token);
        try
        {
            if (invalidated) throw new BridgeException("PlatformSessionInvalidated", "Bound Designer/session changed. Restart this Host; it will never rebind.");
            var fresh = InstanceRegistry.List(registry);
            InstanceInfo current;
            if (instance is null)
            {
                var matches = fresh.Where(x => SamePath(x.ProjectPath, projectPath) && (fixedInstanceId is null || x.InstanceId == fixedInstanceId)).ToArray();
                if (matches.Length != 1) throw new BridgeException(matches.Length == 0 ? "PlatformProjectUnavailable" : "PlatformProjectAmbiguous", "Exactly one fresh instance must open the exact project path.");
                current = matches[0];
                if (fixedSessionId is not null && current.SessionId != fixedSessionId)
                    throw Invalidate("The requested Designer document session changed before binding.");
                if (expectedInstance is not null && (current.DesignerVersion != expectedInstance.DesignerVersion || current.PipeName != expectedInstance.PipeName))
                    throw Invalidate("The requested Designer build or pipe changed before binding.");
            }
            else
            {
                current = fresh.SingleOrDefault(x => x.InstanceId == instance.InstanceId)
                    ?? throw Invalidate("Designer exited, restarted or heartbeat expired.");
                if (!SamePath(current.ProjectPath, projectPath) || current.SessionId != target!.SessionId || current.DesignerVersion != target.DesignerVersion || current.PipeName != instance.PipeName)
                    throw Invalidate("Designer switched/reopened the document or changed build/pipe.");
            }
            var candidate = target ?? new(current.InstanceId, current.SessionId ?? "", projectPath, current.DesignerVersion);
            if (string.IsNullOrWhiteSpace(candidate.SessionId)) throw new BridgeException("PlatformSessionUnavailable", "Registry does not identify an active document session.");
            // The capability catalog is immutable for a bound exact-build adapter.
            // Do not enqueue discovery before receipt queries/cancellation: a
            // running native write must not prevent clients observing its ID.
            if (!refresh && cachedDescription is not null) return cachedDescription;
            var result = Unwrap(await BridgeClient.CallAsync(current, "platform.describe", new PlatformDescribeRequest(candidate), token, TimeSpan.FromMinutes(5)));
            var description = PlatformJson.Read<PlatformDescription>(result);
            if (!SameTarget(description.Snapshot.Target, candidate) || !SameTarget(description.Runtime.Target, candidate))
                throw Invalidate("Native platform target differs from registry or bound target.");
            instance = current;
            target = candidate;
            cachedDescription = description;
            return description;
        }
        finally { binding.Release(); }
    }

    public async Task<JsonElement> InvokeAsync(string @namespace, string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        if (target is not null && @namespace == "project" && method is "get_operation" or "cancel_operation")
            return Unwrap(await BridgeClient.CallAsync(instance!, "platform.invoke",
                new PlatformApiRequest(target, @namespace, method, parameters), cancellationToken, TimeSpan.FromSeconds(10)));
        var description = await DescribeCoreAsync(false, cancellationToken);
        return Unwrap(await BridgeClient.CallAsync(instance!, "platform.invoke",
            new PlatformApiRequest(description.Runtime.Target, @namespace, method, parameters), cancellationToken, TimeSpan.FromMinutes(5)));
    }

    private async Task<PlatformCancellationResult> CancelAsync(string operationId)
    {
        // Use the original endpoint/target, even when the active context changed;
        // cancellation must never select a different Designer document.
        if (instance is null || target is null) return new(false, "unknown", true);
        return PlatformJson.Read<PlatformCancellationResult>(Unwrap(await BridgeClient.CallAsync(instance,
            "platform.cancel", new PlatformCancelRequest(target, operationId), requestTimeout: TimeSpan.FromSeconds(3))));
    }

    private BridgeException Invalidate(string reason) { invalidated = true; return new("PlatformSessionInvalidated", reason); }
    private static JsonElement Unwrap(BridgeResponse response) => response.Ok && response.Result is { } result ? result.Clone()
        : throw new BridgeException(response.Code ?? "PlatformRequestFailed", response.Message ?? "No platform result.");
    private static bool SameTarget(ProjectTarget a, ProjectTarget b) => a.InstanceId == b.InstanceId && a.SessionId == b.SessionId && a.DesignerVersion == b.DesignerVersion && SamePath(a.ProjectPath, b.ProjectPath);
    private static bool SamePath(string? a, string b) => !string.IsNullOrWhiteSpace(a) && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}
