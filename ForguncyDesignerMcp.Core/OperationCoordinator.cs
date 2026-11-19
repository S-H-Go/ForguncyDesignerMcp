using System.Collections.Concurrent;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core;

public sealed partial class OperationCoordinator(IDesignerBackend backend)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ConcurrentDictionary<string, Entry> operations = new(StringComparer.Ordinal);
    private readonly object registrationGate = new();
    private sealed class Entry(string fingerprint, OperationReceipt receipt)
    {
        public string Fingerprint { get; } = fingerprint;
        public OperationReceipt Receipt = receipt;
    }

    public OperationReceipt Submit(string operation, MutationRequest request)
    {
        if (!Guid.TryParseExact(request.OperationId, "D", out _))
            throw new BridgeException("InvalidOperationId", "operationId 必须是 UUID；超时后用同一 ID 查询，不要重复操作。");
        if (request.Modules is null || string.IsNullOrWhiteSpace(request.SessionId) || string.IsNullOrWhiteSpace(request.ProjectPath))
            throw new BridgeException("InvalidArguments", "缺少工程会话、路径或模块列表。");
        if (operation is not ("collaboration.refresh" or "collaboration.checkout" or "collaboration.checkin"))
            throw new BridgeException("UnknownOperation", "未知的写操作。");
        var fingerprint = Wire.Hash(operation + JsonSerializer.Serialize(request, Wire.Json));
        Entry entry;
        lock (registrationGate)
        {
            if (operations.TryGetValue(request.OperationId, out var existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new BridgeException("OperationIdConflict", "同一 operationId 已用于不同请求。");
                return Volatile.Read(ref existing.Receipt);
            }
            // Receipts are retained for the process lifetime so retries cannot repeat a commit.
            RequireConfirmedPlatformSession(request.SessionId);
            if (operations.Count >= 1024)
                throw new BridgeException("ReceiptLimit", "本实例已保留 1024 条操作回执，不能再接收新写操作。");
            entry = new(fingerprint, new(request.OperationId, operation, "queued", DateTimeOffset.UtcNow, null));
            operations[request.OperationId] = entry;
        }
        _ = RunAsync(entry, operation, request);
        return Volatile.Read(ref entry.Receipt);
    }

    public OperationReceipt Get(string id) => operations.TryGetValue(id, out var entry)
        ? Volatile.Read(ref entry.Receipt)
        : throw new BridgeException("UnknownOperation", "未找到该回执；不能据此假定之前的操作未执行。");

    private async Task RunAsync(Entry entry, string operation, MutationRequest request)
    {
        await gate.WaitAsync();
        var nativeStarted = false;
        try
        {
            Volatile.Write(ref entry.Receipt, entry.Receipt with { State = "running" });
            RequireConfirmedPlatformSession(request.SessionId);
            var before = await backend.CaptureAsync();
            SessionGuard.CheckContext(before, request);
            if (operation == "collaboration.refresh")
            {
                nativeStarted = true;
                await backend.RefreshAsync(request.SessionId);
                var afterRefresh = await backend.CaptureAsync();
                SessionGuard.CheckContext(afterRefresh, request);
                if (afterRefresh.ModuleStateStale || JsonSerializer.Serialize(before.Git, Wire.Json) != JsonSerializer.Serialize(afterRefresh.Git, Wire.Json) || before.LockHash != afterRefresh.LockHash)
                    throw new BridgeException("RefreshVerificationFailed", "刷新期间源文件或 Git 状态发生变化，请重新核对。");
                Finish(entry, "succeeded", "Refreshed", "原生模块状态已重读；未执行保存或远端同步。", afterRefresh);
                return;
            }
            SessionGuard.CheckMutation(before, request, operation);
            var files = operation == "collaboration.checkin"
                ? await backend.GetModuleFilesAsync(request.Modules, request.SessionId) : [];
            if (operation == "collaboration.checkin" && files.Length == 0)
                throw new BridgeException("MissingModuleFiles", "未能通过原生模块映射确定待签入文件。");
            var allowed = files.Select(f => SessionGuard.RelativeFile(before.ProjectPath, f)).Append(".collaboration")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (before.Git!.RemoteUrl is not null && await backend.GetRemoteHeadAsync(before.Git) != before.Git.Head)
                throw new BridgeException("RemoteAdvanced", "远端已变化，尚未开始原生协同操作。");
            // Recheck after asynchronous Git/network inspection and immediately before dispatch.
            var latest = await backend.CaptureAsync();
            SessionGuard.CheckMutation(latest, request, operation);
            nativeStarted = true;
            var accepted = operation == "collaboration.checkin"
                ? await backend.CheckinAsync(request.Modules, request.Message!, latest)
                : await backend.CheckoutAsync(request.Modules, before.Git.Head, latest);
            var after = await backend.CaptureAsync();
            SessionGuard.CheckContext(after, request);
            if (!accepted)
            {
                Finish(entry, "failed", "NativeCancelledOrFailed", "原生操作返回取消或失败，请审阅当前状态；不会自动重试。", after);
                return;
            }
            VerifyModules(before, after, request.Modules, operation);
            if (after.HasUnsavedChanges || after.ModuleStateStale)
                throw new BridgeException("SaveVerificationFailed", "操作后保存状态或内存模块状态未核对通过。");
            if (after.Git is null || after.Git.RemoteUrl != before.Git.RemoteUrl || after.Git.Branch != before.Git.Branch)
                throw new BridgeException("DestinationChangedAfterOperation", "原生操作后分支或远端发生变化，尚未核实同步结果。");
            if (!after.Git.IndexClean || (after.Git.ChangedFiles ?? []).Keys.Any(allowed.Contains))
                throw new BridgeException("TargetFilesStillDirty", "操作后暂存区或目标文件仍有未提交变化，不能报告保存和签入完成。");
            var committed = await backend.GetCommittedFilesAsync(before.Git.Head, after.Git!.Head);
            var oldOtherFiles = (before.Git.ChangedFiles ?? []).Where(x => !allowed.Contains(x.Key)).OrderBy(x => x.Key).ToArray();
            var newOtherFiles = (after.Git.ChangedFiles ?? []).Where(x => !allowed.Contains(x.Key)).OrderBy(x => x.Key).ToArray();
            if (!oldOtherFiles.SequenceEqual(newOtherFiles))
                throw new BridgeException("OtherWorktreeFilesChanged", "原生操作期间范围外的工作区文件发生变化，请审阅；不会自动恢复覆盖。");
            if (after.Git.Author != before.CurrentUser || after.Git.Committer != before.CurrentUser)
                throw new BridgeException("CommitIdentityMismatch", "新提交的作者或提交者与设计器协同身份不一致。");
            var extra = committed.Where(f => !allowed.Contains(f)).ToArray();
            if (extra.Length != 0)
                throw new BridgeException("CommitScopeChanged", "原生提交包含请求范围外的文件：" + string.Join(", ", extra));
            if (after.Git.Head == before.Git.Head || !committed.Contains(".collaboration", StringComparer.OrdinalIgnoreCase))
                throw new BridgeException("CommitUnconfirmed", "未核实目标锁与内容已形成新的原生提交。");
            var remoteSynced = after.Git.RemoteUrl is null ? (bool?)null : await backend.GetRemoteHeadAsync(after.Git) == after.Git.Head;
            if (remoteSynced == false)
            {
                Finish(entry, "verification_failed", "RemoteUnconfirmed", "本地原生操作已返回，但远端同步未核实；不要重复提交。", after, committed, false);
                return;
            }
            Finish(entry, "succeeded", "Verified", "原生协同操作、目标锁及提交范围已核实。", after, committed, remoteSynced);
        }
        catch (Exception error)
        {
            var code = error is BridgeException b ? b.Code : "NativeError";
            Finish(entry, nativeStarted ? "verification_failed" : "failed", code, error.Message);
        }
        finally { gate.Release(); }
    }

    private static void VerifyModules(DesignerContext before, DesignerContext after, string[] names, string operation)
    {
        var targets = names.ToHashSet(StringComparer.Ordinal);
        var oldOther = before.Modules.Where(x => !targets.Contains(x.Name)).OrderBy(x => x.Name).ToArray();
        var newOther = after.Modules.Where(x => !targets.Contains(x.Name)).OrderBy(x => x.Name).ToArray();
        if (JsonSerializer.Serialize(oldOther, Wire.Json) != JsonSerializer.Serialize(newOther, Wire.Json))
            throw new BridgeException("OtherModulesChanged", "其他模块记录发生变化，不能报告本次签入范围已通过。");
        foreach (var name in names)
        {
            var module = after.Modules.SingleOrDefault(x => x.Name == name);
            var good = module is not null && (operation == "collaboration.checkin"
                ? module.State == 0 && module.LockedBy is null
                : module.State == 1 && module.LockedBy == before.CurrentUser);
            if (!good) throw new BridgeException("LockUnconfirmed", "目标模块锁状态未核实：" + name);
        }
    }

    private static void Finish(Entry entry, string state, string code, string message, DesignerContext? context = null,
        string[]? files = null, bool? remote = null) => Volatile.Write(ref entry.Receipt, entry.Receipt with
        { State = state, FinishedAt = DateTimeOffset.UtcNow, Code = code, Message = message,
            Context = context, CommittedFiles = files, RemoteSynchronized = remote });
}
