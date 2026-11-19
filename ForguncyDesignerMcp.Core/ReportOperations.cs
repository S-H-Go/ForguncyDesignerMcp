using System.Text.Json;

namespace ForguncyDesignerMcp.Core;

public sealed partial class OperationCoordinator
{
    public async Task<ReportReadResult> ReadReportAsync(string reportName, string sessionId, string projectPath)
    {
        ReportDocuments.ValidateName(reportName);
        await gate.WaitAsync();
        try
        {
            var before = await backend.CaptureAsync();
            CheckReportContext(before, sessionId, projectPath);
            var report = await RequireReports().GetReportAsync(reportName, sessionId);
            var after = await backend.CaptureAsync();
            CheckReportContext(after, sessionId, projectPath);
            if (before.Revision != after.Revision)
                throw new BridgeException("ContextChangedDuringRead", "读取报表期间工程发生变化，请重新读取。");
            return new(after, report);
        }
        finally { gate.Release(); }
    }

    public OperationReceipt SubmitReport(ReportUpdateRequest request)
    {
        if (!Guid.TryParseExact(request.OperationId, "D", out _))
            throw new BridgeException("InvalidOperationId", "operationId 必须是 UUID；超时后查询原 ID。");
        ReportDocuments.ValidateName(request.ReportName);
        if (string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.ProjectPath) ||
            string.IsNullOrWhiteSpace(request.ExpectedRevision) || string.IsNullOrWhiteSpace(request.ExpectedReportRevision) || request.Json is null || request.Rdlx is null)
            throw new BridgeException("InvalidArguments", "缺少工程会话、路径、双重基线或报表文件组。");
        var fingerprint = Wire.Hash("report.update" + JsonSerializer.Serialize(request, Wire.Json));
        Entry entry;
        lock (registrationGate)
        {
            if (operations.TryGetValue(request.OperationId, out var existing))
            {
                if (existing.Fingerprint != fingerprint) throw new BridgeException("OperationIdConflict", "同一 operationId 已用于不同请求。");
                return Volatile.Read(ref existing.Receipt);
            }
            if (operations.Count >= 1024) throw new BridgeException("ReceiptLimit", "本实例已保留 1024 条操作回执，不能再接收新写操作。");
            RequireConfirmedPlatformSession(request.SessionId);
            entry = new(fingerprint, new(request.OperationId, "report.update", "queued", DateTimeOffset.UtcNow, null));
            operations[request.OperationId] = entry;
        }
        _ = RunReportAsync(entry, request);
        return Volatile.Read(ref entry.Receipt);
    }

    private IReportBackend RequireReports() => backend as IReportBackend
        ?? throw new BridgeException("ReportsUnavailable", "当前后端未实现报表写入。");

    private async Task RunReportAsync(Entry entry, ReportUpdateRequest request)
    {
        await gate.WaitAsync();
        var nativeStarted = false;
        try
        {
            Volatile.Write(ref entry.Receipt, entry.Receipt with { State = "running" });
            RequireConfirmedPlatformSession(request.SessionId);
            var before = await backend.CaptureAsync();
            CheckReportMutation(before, request);
            var reports = RequireReports();
            var baseline = await reports.GetReportAsync(request.ReportName, request.SessionId);
            CheckReportBaseline(before, request, baseline);
            ReportDocuments.ValidateUpdate(baseline, request.Json, request.Rdlx);
            // Recheck both memory and saved-file hashes after asynchronous inspection.
            var latest = await backend.CaptureAsync();
            CheckReportMutation(latest, request);
            var fresh = await reports.GetReportAsync(request.ReportName, request.SessionId);
            CheckReportBaseline(latest, request, fresh);
            nativeStarted = true;
            var result = await reports.UpdateReportAsync(request, latest, fresh);
            Volatile.Write(ref entry.Receipt, entry.Receipt with { ReportResult = result });
            var after = await backend.CaptureAsync();
            CheckReportContext(after, request.SessionId, request.ProjectPath);
            if (!result.Saved || !result.MemoryVerified || !result.SourceVerified || after.HasUnsavedChanges || after.ModuleStateStale)
                throw new BridgeException("ReportSaveUnconfirmed", "原生保存、内存回读及源文件组尚未全部核实；不要重发写操作。");
            if (result.Report.Name != baseline.Name || result.Report.ModuleName != baseline.ModuleName ||
                result.Report.JsonFile != baseline.JsonFile || result.Report.RdlxFile != baseline.RdlxFile ||
                !ReportDocuments.JsonEquivalent(request.Json, result.Report.Json) || !ReportDocuments.RdlxEquivalent(request.Rdlx, result.Report.Rdlx))
                throw new BridgeException("ReportReadbackMismatch", "保存后的报表内容或文件组与请求不一致。");
            VerifyReportScope(before, after, baseline);
            Finish(entry, "succeeded", "ReportSavedAndVerified", "报表文件组已原生保存，内存和源文件回读通过；未签入或推送。", after);
        }
        catch (Exception error)
        {
            Finish(entry, nativeStarted ? "verification_failed" : "failed",
                error is BridgeException bridge ? bridge.Code : "NativeReportError", error.Message);
        }
        finally { gate.Release(); }
    }

    private static void CheckReportContext(DesignerContext context, string sessionId, string projectPath)
    {
        if (context.SessionId != sessionId || !string.Equals(Path.GetFullPath(context.ProjectPath), Path.GetFullPath(projectPath), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("DocumentChanged", "工程会话或路径已变化，请重新读取。");
        if (context.Busy) throw new BridgeException("DesignerBusy", "设计器正在打开、导入或执行其他操作。");
    }

    private static void CheckReportMutation(DesignerContext context, ReportUpdateRequest request)
    {
        CheckReportContext(context, request.SessionId, request.ProjectPath);
        if (context.HasUnsavedChanges) throw new BridgeException("UnsavedChanges", "请先保存已有修改；报表写入仅保存本次文件组。");
        if (context.ModuleStateStale) throw new BridgeException("ModuleStateStale", "模块内存状态与磁盘不同，请先刷新。");
        if (context.Revision != request.ExpectedRevision) throw new BridgeException("BaselineChanged", "工程基线已变化，请重新读取。");
        if (context.InCollaborationMode && (context.CurrentUser is null || context.CurrentUser != request.ExpectedUser))
            throw new BridgeException("IdentityChanged", "请求身份与当前协同身份不同。");
    }

    private static void CheckReportBaseline(DesignerContext context, ReportUpdateRequest request, ReportSnapshot report)
    {
        if (report.Name != request.ReportName || report.Revision != request.ExpectedReportRevision)
            throw new BridgeException("ReportBaselineChanged", "报表内存或源文件已变化，请重新读取。");
        if (report.EditorOpen) throw new BridgeException("ReportEditorOpen", "请先保存并关闭报表编辑器，再读取新基线。");
        if (context.InCollaborationMode)
        {
            var module = context.Modules.SingleOrDefault(x => x.Name == report.ModuleName)
                ?? throw new BridgeException("UnknownModule", "报表未登记为现有协同模块。");
            if (module.State != 1 || module.LockedBy != context.CurrentUser)
                throw new BridgeException("ReportLockRequired", "请先以当前协同身份签出现有报表模块。");
        }
    }

    private static void VerifyReportScope(DesignerContext before, DesignerContext after, ReportSnapshot baseline)
    {
        if (before.InCollaborationMode != after.InCollaborationMode || before.CurrentUser != after.CurrentUser)
            throw new BridgeException("IdentityChanged", "保存期间工程模式或身份发生变化。");
        // A report update retains every module lock and performs no Git commit.
        if (JsonSerializer.Serialize(before.Modules, Wire.Json) != JsonSerializer.Serialize(after.Modules, Wire.Json))
            throw new BridgeException("OtherModulesChanged", "保存期间协同模块或锁发生变化，请审阅。");
        if (before.Git is not null)
        {
            if (after.Git is null || before.Git.Head != after.Git.Head || before.Git.Branch != after.Git.Branch ||
                before.Git.RemoteUrl != after.Git.RemoteUrl || before.Git.IndexClean != after.Git.IndexClean)
                throw new BridgeException("GitChangedDuringReportSave", "保存期间 Git 提交、分支、远端或暂存状态发生变化。");
            var allowed = new HashSet<string>([baseline.JsonFile.Replace('\\', '/'), baseline.RdlxFile.Replace('\\', '/')], StringComparer.OrdinalIgnoreCase);
            var oldOther = (before.Git.ChangedFiles ?? []).Where(x => !allowed.Contains(x.Key)).OrderBy(x => x.Key).ToArray();
            var newOther = (after.Git.ChangedFiles ?? []).Where(x => !allowed.Contains(x.Key)).OrderBy(x => x.Key).ToArray();
            if (!oldOther.SequenceEqual(newOther)) throw new BridgeException("OtherWorktreeFilesChanged", "保存期间范围外的工作区文件发生变化，请审阅。");
        }
    }
}
