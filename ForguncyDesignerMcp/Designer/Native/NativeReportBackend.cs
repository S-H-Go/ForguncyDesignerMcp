using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Designer.Native;

internal sealed partial class NativeDesignerBackend
{
    private (object Document, object Manager, object Reports, NativeReportStore Store) ReportEnvironment(string expectedSession)
    {
        NativeAccess.RequireV12Legacy(assembly);
        var (document, manager) = RequireSession(expectedSession);
        var colleagues = NativeAccess.Get(document, "ForguncyColleagueManager") ?? throw new MissingMemberException("ForguncyColleagueManager");
        var colleague = NativeAccess.Get(colleagues, "ReportModel") ?? throw new MissingMemberException("ReportModel");
        var reports = assembly.GetType("Forguncy.Common.ReportsManager", true)!.GetProperty("Instance", NativeAccess.Flags)!.GetValue(null)!;
        var store = new NativeReportStore(assembly, colleague, reports);
        var nativeReports = Path.GetFullPath((string)NativeAccess.Get(reports, "LocalReportsPath")!);
        if (!string.Equals(nativeReports, Path.Combine(store.LocalRoot, "Reports"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(store.SourcePath, currentPath, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(store.LocalRoot, currentPath, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ReportCacheMismatch", "原生报表工作目录不属于当前文档。");
        return (document, manager, reports, store);
    }

    private static bool IsReportEditorOpen(object reports) =>
        NativeAccess.Get(reports, "InARDesignerMode") is not false ||
        NativeAccess.Get(reports, "IsReportDataSourceEditingMode") is not false ||
        reports.GetType().GetField("_checkingDirtyState", NativeAccess.Flags)?.GetValue(reports) is true ||
        reports.GetType().GetField("_updateLocalRootPathTask", NativeAccess.Flags)?.GetValue(reports) is Task { IsCompleted: false };

    private string ReportModuleName(object model)
    {
        var helper = assembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        var method = helper.GetMethods(NativeAccess.Flags).Single(m => m.Name == "GetModuleName" && m.GetParameters().Length == 2);
        return (string)method.Invoke(null, [model, null])!;
    }

    private ReportSnapshot ReadReportSnapshot(string name, string expectedSession)
    {
        dispatcher.VerifyAccess();
        var env = ReportEnvironment(expectedSession);
        var item = env.Store.Find(name);
        var json = env.Store.Serialize(item.Model);
        var rdlx = env.Store.ReadRdlx(item);
        var source = NativeReportFiles.ReadSource(currentPath!, item.JsonFile, item.RdlxFile);
        return new(name, ReportModuleName(item.Model), json, rdlx,
            ReportDocuments.Revision(json, rdlx, source.JsonHash, source.RdlxHash),
            item.JsonFile, item.RdlxFile, source.JsonHash, source.RdlxHash, IsReportEditorOpen(env.Reports));
    }

    public async Task<ReportSnapshot> GetReportAsync(string reportName, string expectedSessionId) =>
        await dispatcher.InvokeAsync(() => ReadReportSnapshot(reportName, expectedSessionId));

    public async Task<ReportSaveResult> UpdateReportAsync(ReportUpdateRequest request, DesignerContext expected, ReportSnapshot baseline) =>
        await dispatcher.InvokeAsync(async () =>
        {
            RequireWriteContext(expected);
            var env = ReportEnvironment(request.SessionId);
            var fresh = ReadReportSnapshot(request.ReportName, request.SessionId);
            if (fresh.Revision != baseline.Revision || fresh.Revision != request.ExpectedReportRevision || fresh.EditorOpen)
                throw new BridgeException("ReportBaselineChanged", "UI 线程写入前报表或编辑器状态已改变。");
            ReportDocuments.ValidateUpdate(fresh, request.Json, request.Rdlx);
            var item = env.Store.Find(request.ReportName);
            EnsureReportTracking(env.Manager, expected, fresh);
            var prepared = env.Store.Prepare(item, request.Json);
            var before = NativeReportFiles.ReadSource(currentPath!, item.JsonFile, item.RdlxFile);
            NativeReportFiles.CheckSource(fresh, before);
            var evidence = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ForguncyDesignerMcp", "operations", request.OperationId);
            Directory.CreateDirectory(evidence);
            File.WriteAllBytes(Path.Combine(evidence, "before.json"), before.Json);
            File.WriteAllBytes(Path.Combine(evidence, "before.rdlx"), before.Rdlx);
            File.WriteAllText(Path.Combine(evidence, "before-memory.json"), fresh.Json);
            File.WriteAllText(Path.Combine(evidence, "before-memory.rdlx"), fresh.Rdlx);
            void Record(object status) => File.WriteAllText(Path.Combine(evidence, "verification.json"), JsonSerializer.Serialize(status, Wire.Json));
            Record(new { state = "prepared", request.OperationId, request.ProjectPath, fresh.JsonFile, fresh.RdlxFile, fresh.JsonFileHash, fresh.RdlxFileHash });
            try
            {
                RequireWriteContext(expected);
                NativeReportFiles.CheckSource(fresh, NativeReportFiles.ReadSource(currentPath!, item.JsonFile, item.RdlxFile));
                // Mark dirty before the first change, so a partial native failure remains visible.
                env.Document.GetType().GetProperty("ContainsUnsavedChanges", NativeAccess.Flags)!.SetValue(env.Document, true);
                env.Store.Replace(item, prepared, request.Rdlx);
                InvalidateReportGeneration(prepared);
                var memory = ReadReportSnapshot(request.ReportName, request.SessionId);
                var memoryOk = ReportDocuments.JsonEquivalent(request.Json, memory.Json) && ReportDocuments.RdlxEquivalent(request.Rdlx, memory.Rdlx);
                if (!memoryOk) throw new BridgeException("ReportMemoryMismatch", "原生报表内存回读不一致，尚未调用工程保存。");
                NativeReportFiles.CheckSource(fresh, NativeReportFiles.ReadSource(currentPath!, item.JsonFile, item.RdlxFile));
                var saved = await NativeReportStore.SaveDocumentAsync(env.Document);
                RequireSession(request.SessionId);
                var result = ReadReportSnapshot(request.ReportName, request.SessionId);
                var after = NativeReportFiles.ReadSource(currentPath!, item.JsonFile, item.RdlxFile);
                File.WriteAllBytes(Path.Combine(evidence, "after.json"), after.Json);
                File.WriteAllBytes(Path.Combine(evidence, "after.rdlx"), after.Rdlx);
                var sourceOk = ReportDocuments.JsonEquivalent(request.Json, after.JsonText) && ReportDocuments.RdlxEquivalent(request.Rdlx, after.RdlxText);
                memoryOk = ReportDocuments.JsonEquivalent(request.Json, result.Json) && ReportDocuments.RdlxEquivalent(request.Rdlx, result.Rdlx);
                saved = saved && NativeAccess.Get(env.Document, "ContainsUnsavedChanges") is false;
                Record(new { state = saved && sourceOk && memoryOk ? "saved-and-verified" : "save-unconfirmed", saved, sourceOk, memoryOk,
                    request.OperationId, request.ProjectPath, result.JsonFile, result.RdlxFile, result.JsonFileHash, result.RdlxFileHash });
                return new ReportSaveResult(saved, memoryOk, sourceOk, result, evidence);
            }
            catch (Exception error)
            {
                // Never restore source/LastSaveFiles over a native save or an external edit.
                Record(new { state = "verification-failed", request.OperationId, code = error is BridgeException b ? b.Code : "NativeReportError", message = error.Message });
                throw new BridgeException(error is BridgeException bridge ? bridge.Code : "NativeReportError", error.Message + "；备份与回读证据：" + evidence);
            }
        }).Task.Unwrap();

    private static void EnsureReportTracking(object manager, DesignerContext expected, ReportSnapshot report)
    {
        if (!expected.InCollaborationMode) return;
        var module = expected.Modules.SingleOrDefault(x => x.Name == report.ModuleName);
        if (module is null || module.State != 1 || module.LockedBy != expected.CurrentUser)
            throw new BridgeException("ReportLockRequired", "报表须已由当前用户原生签出。");
        if (NativeAccess.Get(manager, "SaveLoadTrackedFiles") is not IDictionary tracking ||
            !tracking.Contains(report.ModuleName) || tracking[report.ModuleName] is not IEnumerable files)
            throw new BridgeException("ReportSaveTrackingMissing", "报表缺少原生保存登记，请在设计器重新签出目标模块。");
        var paths = files.Cast<string>().Select(f => SessionGuard.RelativeFile(expected.ProjectPath, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!paths.SetEquals([report.JsonFile, report.RdlxFile]))
            throw new BridgeException("ReportSaveTrackingMismatch", "报表原生保存登记必须恰好包含同名 JSON 和 RDLX。");
    }

    private void InvalidateReportGeneration(object model)
    {
        var cacheType = assembly.GetType("pb", true)!;
        var cache = cacheType.GetProperty("Instance", NativeAccess.Flags)!.GetValue(null)!;
        var method = cacheType.GetMethod("DirtyModules", NativeAccess.Flags)!;
        var tuple = method.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var generic = tuple.GetGenericArguments();
        var module = generic[0].GetMethods(NativeAccess.Flags).Single(m => m.Name == "Create" && m.GetParameters().Length == 1 &&
            m.GetParameters()[0].ParameterType.IsInstanceOfType(model)).Invoke(null, [model]);
        var changes = Array.CreateInstance(tuple, 1);
        changes.SetValue(Activator.CreateInstance(tuple, module, Enum.Parse(generic[1], "Edit")), 0);
        method.Invoke(cache, [null, changes]);
    }
}
