namespace ForguncyDesignerMcp.Core.Platform;

public sealed record PluginLifecycleRequest(string OperationId, ProjectTarget Target, string ExpectedProjectRevision,
    UserIdentity? ExpectedUser, ModuleOperation Operation, string PluginGuid, string? ExpectedInstalledVersion = null,
    string? PackageBase64 = null, string? PackageSha256 = null, string? PackageVersion = null);

public sealed record PluginLifecycleReceipt(string OperationId, string State, string Code, string Message,
    bool? NativeAccepted, bool RegistryVerified, bool RestartRequired, bool RuntimeActivationVerified,
    bool ExecutionMayContinue, string? ObservedGuid, string? ObservedVersion, string? Limitation);

public sealed record PluginInstallationInfo(string PluginGuid, string Version, string Name,
    bool? IsLoaded = null, bool? IsUsedByCurrentProject = null);

public interface IProjectPluginLifecycleAdapter
{
    Task<IReadOnlyList<PluginInstallationInfo>> ListInstalledPluginsAsync(ProjectTarget target, CancellationToken cancellationToken = default);
    // Pure package/identity/version/dependency validation; no registration or native write.
    Task ValidatePluginLifecycleAsync(PluginLifecycleRequest request, CancellationToken cancellationToken = default);
    Task<PluginLifecycleReceipt> InvokePluginLifecycleAsync(PluginLifecycleRequest request, CancellationToken cancellationToken = default);
}
