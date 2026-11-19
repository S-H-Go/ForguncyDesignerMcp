namespace ForguncyDesignerMcp.Core;

// File hashes describe saved bytes. Revision also includes the in-memory pair;
// serialization formatting can differ without changing the report content.
public sealed record ReportSnapshot(string Name, string ModuleName, string Json, string Rdlx,
    string Revision, string JsonFile, string RdlxFile, string JsonFileHash, string RdlxFileHash, bool EditorOpen);
public sealed record ReportReadResult(DesignerContext Context, ReportSnapshot Report);
public sealed record ReportUpdateRequest(string OperationId, string SessionId, string ProjectPath,
    string ExpectedRevision, string ExpectedReportRevision, UserIdentity? ExpectedUser,
    string ReportName, string Json, string Rdlx);
public sealed record ReportSaveResult(bool Saved, bool MemoryVerified, bool SourceVerified, ReportSnapshot Report,
    string? EvidenceDirectory = null);

public interface IReportBackend
{
    Task<ReportSnapshot> GetReportAsync(string reportName, string sessionId);
    Task<ReportSaveResult> UpdateReportAsync(ReportUpdateRequest request, DesignerContext expected, ReportSnapshot baseline);
}
