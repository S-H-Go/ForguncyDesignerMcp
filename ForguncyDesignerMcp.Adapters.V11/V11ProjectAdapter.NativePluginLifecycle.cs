using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter : IProjectPluginLifecycleAdapter
{
    private NativePluginLifecycleEngine? nativePluginLifecycle;
    private NativePluginLifecycleEngine PluginLifecycle => nativePluginLifecycle ??= new(context);
    private readonly HashSet<string> nativePluginStarted = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<PluginInstallationInfo>> ListInstalledPluginsAsync(ProjectTarget target, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(() =>
            {
                CheckTarget(target); var result = PluginLifecycle.ListInstalled(); CheckTarget(target);
                return Task.FromResult(result);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task ValidatePluginLifecycleAsync(PluginLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Ui.RunAsync(() =>
            {
                CheckPluginLifecycleContext(request); CheckPluginDependencies(request); PluginLifecycle.Validate(PluginChange(request)); CheckPluginLifecycleContext(request);
                return Task.FromResult(true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<PluginLifecycleReceipt> InvokePluginLifecycleAsync(PluginLifecycleRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                CheckPluginLifecycleContext(request);
                var change = PluginChange(request); CheckPluginDependencies(request); PluginLifecycle.Validate(change); CheckPluginLifecycleContext(request);
                cancellationToken.ThrowIfCancellationRequested();
                if (!nativePluginStarted.Add(request.OperationId)) throw new BridgeException("OperationAlreadyStarted", "Query the original plugin operation instead of replaying it.");
                var outcome = await PluginLifecycle.ExecuteAsync(change);
                if (outcome.ExecutionMayContinue || outcome.NativeAccepted is null) requiresReview = true;
                return new PluginLifecycleReceipt(request.OperationId, outcome.State, outcome.Code, outcome.Message, outcome.NativeAccepted,
                    outcome.RegistryVerified, outcome.RestartRequired, outcome.RuntimeActivationVerified, outcome.ExecutionMayContinue,
                    outcome.ObservedGuid, outcome.ObservedVersion, outcome.Limitation);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    private void CheckPluginLifecycleContext(PluginLifecycleRequest request)
    {
        if (!Guid.TryParseExact(request.OperationId, "D", out _)) throw new BridgeException("InvalidOperationId", "Plugin lifecycle requires the original UUID.");
        CheckTarget(request.Target); RequireReviewedState();
        if (pending.Count != 0) throw new BridgeException("PendingMutationRequiresSave", "Review pending project edits before a plugin lifecycle action.");
        var snapshot = Snapshot();
        if (snapshot.Busy || snapshot.HasUnsavedChanges || snapshot.CurrentUser != request.ExpectedUser || snapshot.Revision != request.ExpectedProjectRevision ||
            NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true)
            throw new BridgeException("PluginBaselineChanged", "Plugin lifecycle requires the reviewed clean, idle document and actor.");
        if (!baselines.TryGetValue(snapshot.Revision, out var issued) || DateTimeOffset.UtcNow - issued > TimeSpan.FromMinutes(30))
            throw new BridgeException("ProjectBaselineExpired", "Read a current project snapshot before changing plugins.");
    }

    private void CheckPluginDependencies(PluginLifecycleRequest request)
    {
        if (request.Operation != ModuleOperation.Delete) return;
        // This reader proves the existing registries and all relevant native
        // definition roots are loaded; it does not initialize plugin services.
        var dependencies = new NativeV11Plugins(session).List(ModuleKind.Plugin);
        if (dependencies.Any(item => string.Equals(item.Address.Name, request.PluginGuid, StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("PluginInUse", "The current native project dependency list includes this plugin.");
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
