namespace ForguncyDesignerMcp.Core.Platform;

/// <summary>
/// Partial APIs that prepare a detached module edit, then use the shared
/// validation, native apply, save verification and original-ID receipt path.
/// Preparation must never mutate the live document or dispatch a native write.
/// </summary>
public interface IProjectModuleApiAdapter
{
    bool SupportsModuleApi(string @namespace, string method);
    Task<ModuleMutationRequest> PrepareModuleApiAsync(PlatformApiRequest request,
        CancellationToken cancellationToken = default);
}
