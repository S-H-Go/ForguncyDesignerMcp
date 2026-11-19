using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using ForguncyDesignerMcp.Designer.Native;

namespace ForguncyDesignerMcp.Designer;

// One router per document. Old sessions keep their receipts and never rebind.
internal sealed class PlatformSessionBinding(NativeDesignerBackend backend, OperationCoordinator coordinator)
{
    private readonly SemaphoreSlim bindingGate = new(1, 1);
    private readonly ConcurrentDictionary<string, PlatformProjectRouter> sessions = new(StringComparer.Ordinal);

    internal async Task<PlatformProjectRouter> GetAsync(ProjectTarget target)
    {
        await bindingGate.WaitAsync();
        try
        {
            if (sessions.TryGetValue(target.SessionId, out var existing))
            {
                RequireSameTarget(existing.Target, target);
                return existing;
            }
            var adapter = await backend.CreatePlatformAdapterAsync(target);
            var router = coordinator.CreatePlatformRouter(adapter, target, backend.BindDevelopmentPreview(adapter));
            sessions[target.SessionId] = router;
            return router;
        }
        finally { bindingGate.Release(); }
    }

    internal PlatformCancellationResult Cancel(PlatformCancelRequest request)
    {
        if (!sessions.TryGetValue(request.Target.SessionId, out var router)) return new(false, "unknown", true);
        RequireSameTarget(router.Target, request.Target);
        return router.Cancel(request);
    }

    private static void RequireSameTarget(ProjectTarget a, ProjectTarget b)
    {
        if (a.InstanceId != b.InstanceId || a.SessionId != b.SessionId || a.DesignerVersion != b.DesignerVersion ||
            !string.Equals(Path.GetFullPath(a.ProjectPath), Path.GetFullPath(b.ProjectPath), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("TargetMismatch", "平台请求不能切换已绑定的实例或文档。");
    }
}
