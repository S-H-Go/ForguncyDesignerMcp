namespace ForguncyDesignerMcp.Core.Platform;

/// <summary>Canonicalizes a detached page through its native loader, including
/// redundant companion generation. Never edits the live document or saves.</summary>
public interface IProjectPageProjectionAdapter
{
    Task<ModuleDocument> ProjectPageDocumentAsync(ProjectTarget target, ModuleDocument document,
        CancellationToken cancellationToken = default);
}
