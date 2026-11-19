using System.IO;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter : IProjectCollaborationReceiver
{
    public async Task<CollaborationReceiveReceipt> ReceiveAsync(CollaborationReceiveRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(context.CollaborationReceiveRoot))
                    return new CollaborationReceiveReceipt(request.OperationId, "failed", "ReceivePolicyUnavailable",
                        "The server-owned receiver root is not configured or unavailable.", NativeDispatched: false);
                CheckWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser);
                void VerifyBaseline() => CheckWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser);
                return await NativeV11Receiver.ExecuteAsync(context, request, context.CollaborationReceiveRoot,
                    VerifyBaseline, CaptureCurrentReceiveSnapshotCoreAsync, () => PluginLifecycle.ListInstalled(), cancellationToken);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<ProjectSnapshot> GetCurrentSnapshotAfterReceiveAsync(CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await context.Ui.RunAsync(CaptureCurrentReceiveSnapshotCoreAsync, cancellationToken).ConfigureAwait(false); }
        finally { writeGate.Release(); }
    }

    private async Task<ProjectSnapshot> CaptureCurrentReceiveSnapshotCoreAsync()
    {
        var window = System.Windows.Application.Current?.MainWindow ?? throw new InvalidOperationException("The actual Designer main window is unavailable.");
        var document = NativeV11Reflection.Get(window, "SaveLoadManager") ?? throw new InvalidOperationException("The actual current native document is unavailable.");
        if (document.GetType() != context.DesignerAssembly.GetType(session.Profile.DocumentType, true))
            throw new InvalidOperationException("The current native document does not match the pinned build.");
        var wait = NativeV11Reflection.Call(document, "WaitOpenCompletedAsync") as Task ?? throw new InvalidOperationException("The native open completion task is unavailable.");
        await wait;
        foreach (var name in new[] { "WaitFillRemainTask", "WaitFillRemainModulesTask" })
            if (NativeV11Reflection.Get(document, name) is Task pendingTask) await pendingTask;
        if (!ReferenceEquals(NativeV11Reflection.Get(window, "SaveLoadManager"), document))
            throw new InvalidOperationException("The actual native document changed during receive completion.");
        var snapshot = context.CaptureSnapshot();
        var actualSession = new NativeV11Session(context with { NativeDocument = document });
        var status = NativeV11Collaboration.ReadStatus(actualSession);
        var after = context.CaptureSnapshot();
        if (snapshot != after || status.Target != snapshot.Target || status.CurrentUser != snapshot.CurrentUser || !status.InCollaborationMode)
            throw new InvalidOperationException("The live native receiver registry or host snapshot changed during readback.");
        return after;
    }
}
