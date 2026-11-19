using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter : IProjectRuntimeApiAdapter
{
    private readonly HashSet<string> startedRuntimeCommands = new(StringComparer.Ordinal);

    private (ProjectSnapshot Snapshot, string Scope) ValidateCommandTypes(PlatformApiRequest request)
    {
        if (request.Namespace != "command" || request.Method != "listTypes" || !Guid.TryParseExact(request.OperationId, "D", out _) ||
            request.Parameters.ValueKind != JsonValueKind.Object || !request.Parameters.TryGetProperty("guard", out var value))
            throw new BridgeException("MutationGuardRequired", "Command discovery requires the original operation ID and runtime guard.");
        var scope = NativeV11CommandTypes.ValidateParameters(request.Parameters);
        var guard = PlatformJson.Read<PlatformApiMutationGuard>(value);
        if (guard.Target != request.Target || guard.OperationId != request.OperationId || guard.Scope != EffectScope.RuntimeDeployment ||
            guard.Address is not null || guard.ExpectedModuleRevision is not null || guard.ExpectedServiceBinding is not null)
            throw new BridgeException("RuntimeApiGuardMismatch", "Command discovery requires the exact runtime target and operation ID.");
        CheckTarget(request.Target); RequireReviewedState();
        if (pending.Count != 0 || nativeAccountUncertain) throw new BridgeException("RuntimeApiPendingWork", "Complete pending project or account work before command discovery.");
        var snapshot = Snapshot();
        if (snapshot.Busy || snapshot.HasUnsavedChanges || snapshot.Revision != guard.ExpectedProjectRevision || snapshot.CurrentUser != guard.ExpectedUser ||
            NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true)
            throw new BridgeException("RuntimeApiGuardMismatch", "Command discovery requires the reviewed clean, idle project and actor.");
        if (!baselines.TryGetValue(snapshot.Revision, out var issued) || DateTimeOffset.UtcNow - issued > TimeSpan.FromMinutes(30))
            throw new BridgeException("ProjectBaselineExpired", "Read the current project snapshot before command discovery.");
        return (snapshot, scope);
    }

    public async Task ValidateRuntimeApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await context.Ui.RunAsync(() => { _ = ValidateCommandTypes(request); return Task.FromResult(true); }, cancellationToken).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    public async Task<RuntimeApiReceipt> InvokeRuntimeApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(() =>
            {
                var (before, scope) = ValidateCommandTypes(request);
                cancellationToken.ThrowIfCancellationRequested();
                if (!startedRuntimeCommands.Add(request.OperationId!)) throw new BridgeException("OperationAlreadyStarted", "Query the original command discovery operation.");
                try
                {
                    var result = NativeV11CommandTypes.List(context.DesignerAssembly, scope);
                    var after = Snapshot();
                    if (after != before || NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true)
                        throw new BridgeException("CommandMetadataReadbackFailed", "The project context changed during native command discovery.");
                    return Task.FromResult(new RuntimeApiReceipt(request.OperationId!, "native_accepted", "NativeCommandMetadataRead", "The native V11 registry returned command type metadata.",
                        true, true, false, false, true, false, false, false, result,
                        "Native command and existing plugin registries may have initialized. Metadata discovery does not execute commands, install plugins, save the project or imply support for writing every discovered type."));
                }
                catch
                {
                    requiresReview = true;
                    return Task.FromResult(new RuntimeApiReceipt(request.OperationId!, "verification_failed", "NativeCommandMetadataUnconfirmed", "Native command discovery was dispatched but its outcome could not be confirmed.",
                        null, false, false, false, false, false, false, false, null, "Retain the original operation ID; existing plugin code may have loaded."));
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
}
