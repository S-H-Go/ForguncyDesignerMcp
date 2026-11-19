using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter : IProjectPluginLifecycleAdapter
{
    private NativePluginLifecycleEngine? nativePluginLifecycle;
    private NativePluginLifecycleEngine PluginLifecycle => nativePluginLifecycle ??= new(context);
    private readonly HashSet<string> nativePluginStarted = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<PluginInstallationInfo>> ListInstalledPluginsAsync(ProjectTarget target, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            return await OnUi(() =>
            {
                guard.Value.CheckTarget(target); var result = PluginLifecycle.ListInstalled(); guard.Value.CheckTarget(target);
                return Task.FromResult(result);
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    public async Task ValidatePluginLifecycleAsync(PluginLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            await OnUi(() =>
            {
                CheckPluginLifecycleContext(request); PluginLifecycle.Validate(PluginChange(request)); CheckPluginLifecycleContext(request);
                return Task.FromResult(true);
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    public async Task<PluginLifecycleReceipt> InvokePluginLifecycleAsync(PluginLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            return await OnUi(async () =>
            {
                CheckPluginLifecycleContext(request);
                var change = PluginChange(request); PluginLifecycle.Validate(change); CheckPluginLifecycleContext(request);
                cancellationToken.ThrowIfCancellationRequested();
                if (!nativePluginStarted.Add(request.OperationId)) throw new BridgeException("OperationAlreadyStarted", "Query the original plugin operation instead of replaying it.");
                var outcome = await PluginLifecycle.ExecuteAsync(change);
                if (outcome.ExecutionMayContinue || outcome.NativeAccepted is null) uncertain = true;
                return new PluginLifecycleReceipt(request.OperationId, outcome.State, outcome.Code, outcome.Message, outcome.NativeAccepted,
                    outcome.RegistryVerified, outcome.RestartRequired, outcome.RuntimeActivationVerified, outcome.ExecutionMayContinue,
                    outcome.ObservedGuid, outcome.ObservedVersion, outcome.Limitation);
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    private void CheckPluginLifecycleContext(PluginLifecycleRequest request)
    {
        ValidateOperationId(request.OperationId);
        if (uncertain || pending.Count != 0 || apiPending || pendingAccountExport is not null || nativeServicePending)
            throw new BridgeException("PendingMutationRequiresReview", "Review pending project or service changes before a plugin lifecycle action.");
        var snapshot = guard.Value.RequireWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser);
        if (snapshot.CurrentUser != request.ExpectedUser) throw new BridgeException("UserChanged", "The native plugin action actor changed.");
    }

    private static NativePluginLifecycleEngine.Change PluginChange(PluginLifecycleRequest request)
    {
        byte[]? bytes = null;
        if (request.PackageBase64 is not null)
        {
            if (request.PackageBase64.Length > 90 * 1024 * 1024) throw new BridgeException("PluginPackageSizeInvalid", "The plugin package is too large.");
            try { bytes = Convert.FromBase64String(request.PackageBase64); }
            catch (FormatException) { throw new BridgeException("InvalidPluginPackage", "The plugin package is not valid base64."); }
        }
        return new(request.Operation, request.PluginGuid, request.ExpectedInstalledVersion, bytes, request.PackageVersion, request.PackageSha256);
    }
}
