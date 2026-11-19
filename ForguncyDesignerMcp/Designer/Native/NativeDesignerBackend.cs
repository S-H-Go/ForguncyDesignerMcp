using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Application = System.Windows.Application;
using System.Windows.Threading;
using ForguncyDesignerMcp.Adapters.V12;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Designer.Native;

internal sealed partial class NativeDesignerBackend : IDesignerBackend, IReportBackend, INativeMcpBackend
{
    private readonly Assembly assembly;
    private readonly Dispatcher dispatcher;
    private readonly DesignerBuildProfile buildProfile;
    internal string DesignerVersion => buildProfile.Version;
    private object? currentDocument;
    private object? currentWorkbook;
    private object? currentReportSaveFile;
    private long nativeEditEpoch;
    private readonly List<(object Source, EventInfo Event, Delegate Handler)> documentEvents = new();
    private string? currentPath;
    private string sessionId = Guid.NewGuid().ToString("D");

    internal NativeDesignerBackend(Dispatcher dispatcher)
    {
        this.dispatcher = dispatcher;
        assembly = NativeAccess.FindDesigner();
        buildProfile = DesignerBuildProfile.Resolve(assembly);
    }

    internal (string? SessionId, string? ProjectPath) ObserveDocument()
    {
        dispatcher.VerifyAccess();
        var window = Application.Current?.MainWindow;
        var document = window?.GetType().FullName == "Forguncy.MainWindow" ? NativeAccess.Get(window, "SaveLoadManager") : null;
        var path = document is null ? null : NativeAccess.Get(document, "CurrentFilePath") as string;
        var workbook = document is null ? null : NativeAccess.Get(document, "Workbook");
        var colleagues = document is null ? null : NativeAccess.Get(document, "ForguncyColleagueManager");
        var report = colleagues is null ? null : NativeAccess.Get(colleagues, "ReportModel");
        var mediator = report is null ? null : NativeAccess.Get(report, "Mediator");
        var loader = mediator is null ? null : NativeAccess.Get(mediator, "SaveDataColleague");
        var reportSaveFile = loader is null ? null : NativeAccess.Get(loader, "SaveLoadFile");
        if (!ReferenceEquals(document, currentDocument) || !ReferenceEquals(workbook, currentWorkbook) ||
            !ReferenceEquals(reportSaveFile, currentReportSaveFile) || currentPath != path)
        {
            currentDocument = document; currentWorkbook = workbook; currentReportSaveFile = reportSaveFile;
            currentPath = path; sessionId = Guid.NewGuid().ToString("D");
            TrackDocumentChanges(document, workbook);
        }
        return (document is null ? null : sessionId, path);
    }

    private void TrackDocumentChanges(object? document, object? workbook)
    {
        foreach (var subscription in documentEvents)
            try { subscription.Event.RemoveEventHandler(subscription.Source, subscription.Handler); }
            catch (Exception) { /* Old documents may already be disposed. */ }
        documentEvents.Clear();
        nativeEditEpoch = 0;
        void Subscribe(object? source, string name)
        {
            if (source is null) return;
            var notification = source.GetType().GetEvent(name, NativeAccess.Flags);
            if (notification?.EventHandlerType is null) return;
            var method = GetType().GetMethod(nameof(OnNativeDocumentChanged), BindingFlags.NonPublic | BindingFlags.Instance)!;
            var handler = Delegate.CreateDelegate(notification.EventHandlerType, this, method, false);
            if (handler is null) return;
            notification.AddEventHandler(source, handler);
            documentEvents.Add((source, notification, handler));
        }
        Subscribe(document, "ContainsUnsavedChangedSet");
        Subscribe(workbook is null ? null : NativeAccess.Get(workbook, "UndoManager"), "Changed");
    }

    private void OnNativeDocumentChanged(object? sender, EventArgs args) => System.Threading.Interlocked.Increment(ref nativeEditEpoch);

    private (object Document, object Manager) RequireDocument()
    {
        ObserveDocument();
        if (currentDocument is null || string.IsNullOrWhiteSpace(currentPath))
            throw new BridgeException("NoDocument", "请先打开并保存目标工程。");
        return (currentDocument, NativeAccess.Manager(assembly));
    }

    private (object Document, object Manager) RequireSession(string expectedSessionId)
    {
        var result = RequireDocument();
        if (sessionId != expectedSessionId) throw new BridgeException("DocumentChanged", "执行前工程会话已改变。");
        return result;
    }

    public async Task<DesignerContext> CaptureAsync()
    {
        var before = await dispatcher.InvokeAsync(ReadNativeState);
        GitState? git = before.Context.InCollaborationMode ? await new GitProbe(before.Context.ProjectPath).CaptureAsync() : null;
        var after = await dispatcher.InvokeAsync(ReadNativeState);
        if (before.Document != after.Document || before.Signature != after.Signature)
            throw new BridgeException("ContextChangedDuringRead", "读取期间工程发生变化，请重新读取。");
        var context = after.Context with { Git = git };
        return context with { Revision = Wire.Hash(JsonSerializer.Serialize(new { context.SessionId, context.ProjectPath,
            context.HasUnsavedChanges, context.Busy, context.LockHash, after.Signature, Git = git }, Wire.Json)) };
    }

    private sealed record NativeState(object Document, DesignerContext Context, string Signature);
    private NativeState ReadNativeState()
    {
        dispatcher.VerifyAccess();
        var (document, manager) = RequireDocument();
        var path = Path.GetFullPath(currentPath!);
        var enabled = NativeAccess.Get(manager, "InCollaborationMode") is true;
        var modules = enabled ? GetModules(manager) : [];
        // CurrentUser is the active collaboration provider's native Signature.
        // Standalone snapshots have no project collaboration identity.
        var identity = enabled ? ReadUser(NativeAccess.Get(manager, "CurrentUser")) : null;
        var busy = NativeAccess.Get(document, "Opening") is true || NativeAccess.Get(document, "IsImporting") is true;
        var unsaved = NativeAccess.Get(document, "ContainsUnsavedChanges") is true;
        string lockHash = "", diskSignature = "";
        if (enabled)
        {
            var file = NativeAccess.Get(manager, "ModuleFilePath") as string;
            if (file is null || !string.Equals(Path.GetFullPath(file), Path.Combine(path, ".collaboration"), StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ModuleFileMismatch", "设计器的协同状态文件不属于当前工程。");
            var bytes = File.ReadAllBytes(file); lockHash = Convert.ToHexString(SHA256.HashData(bytes));
            using var parsed = JsonDocument.Parse(File.ReadAllText(file));
            diskSignature = ModuleSignature(parsed.RootElement.EnumerateArray().Select(ReadDiskModule));
        }
        var nativeSignature = ModuleSignature(modules);
        var context = new DesignerContext(sessionId, path, DesignerVersion, enabled, unsaved, busy,
            identity, enabled && nativeSignature != diskSignature, lockHash, "", null, modules);
        return new(document, context, Wire.Hash(JsonSerializer.Serialize(new { context.SessionId, context.ProjectPath,
            context.HasUnsavedChanges, context.Busy, context.CurrentUser, nativeSignature, lockHash }, Wire.Json)));
    }

    private static ModuleInfo[] GetModules(object manager)
    {
        if (NativeAccess.Get(manager, "Modules") is not IDictionary source)
            throw new BridgeException("ModulesUnavailable", "原生模块集合尚未就绪。");
        return source.Values.Cast<object>().Select(module => new ModuleInfo(
            (string)NativeAccess.Get(module, "ModuleName")!, Convert.ToInt32(NativeAccess.Get(module, "ModuleType")),
            Convert.ToInt32(NativeAccess.Get(module, "IconType")), Convert.ToInt32(NativeAccess.Get(module, "State")),
            ReadUser(NativeAccess.Get(module, "LockedBy")), NormalizeDate(NativeAccess.Get(module, "LockDateTime")),
            NativeAccess.Get(module, "ExclusiveType") is { } exclusive ? Convert.ToInt32(exclusive).ToString() : null,
            NativeAccess.Get(module, "ToRemoveFiles") is IEnumerable files ? files.Cast<string>().Select(f => f.Replace('\\', '/')).Order().ToArray() : []))
            .OrderBy(x => x.Name, StringComparer.Ordinal).ToArray();
    }

    // ReloadModules fills editor-only defaults for unlocked records. Compare lock semantics,
    // while the raw file hash still guards every byte before a mutation.
    private static string ModuleSignature(IEnumerable<ModuleInfo> modules) => Wire.Hash(JsonSerializer.Serialize(
        modules.Where(x => x.State != 0 || x.LockedBy is not null).OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => new { x.Name, x.Type, x.State, x.LockedBy, x.LockDateTime }), Wire.Json));
    private static UserIdentity? ReadUser(object? native) => native is null ? null : new(
        NativeAccess.Get(native, "UserName") as string ?? "", NativeAccess.Get(native, "Email") as string ?? "");
    private static string? NormalizeDate(object? value) => value switch
    {
        DateTime time => new DateTimeOffset(time).ToUniversalTime().ToString("O"),
        DateTimeOffset time => time.ToUniversalTime().ToString("O"),
        string text when DateTimeOffset.TryParse(text, out var time) => time.ToUniversalTime().ToString("O"),
        _ => null
    };
    private static ModuleInfo ReadDiskModule(JsonElement item)
    {
        string? Text(string name) => item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
        int Number(string name) => item.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.Number ? x.GetInt32() : 0;
        UserIdentity? owner = item.TryGetProperty("LockedBy", out var user) && user.ValueKind == JsonValueKind.Object
            ? new(user.GetProperty("UserName").GetString()!, user.GetProperty("Email").GetString()!) : null;
        return new(Text("ModuleName") ?? throw new BridgeException("InvalidModuleFile", "模块登记缺少名称。"),
            Number("ModuleType"), Number("IconType"), Number("State"), owner, NormalizeDate(Text("LockDateTime")),
            item.TryGetProperty("ExclusiveType", out var exclusive) && exclusive.ValueKind == JsonValueKind.Number ? exclusive.GetInt32().ToString() : null,
            item.TryGetProperty("ToRemoveFiles", out var files) && files.ValueKind == JsonValueKind.Array
                ? files.EnumerateArray().Select(x => x.GetString()!.Replace('\\', '/')).Order().ToArray() : []);
    }

    public async Task RefreshAsync(string expectedSessionId) => await dispatcher.InvokeAsync(() =>
    {
        var (_, manager) = RequireSession(expectedSessionId); NativeAccess.Call(manager, "ReloadModules");
    });

    public async Task<string[]> GetModuleFilesAsync(string[] moduleNames, string expectedSessionId) => await dispatcher.InvokeAsync(() =>
    {
        var (_, manager) = RequireSession(expectedSessionId);
        var source = (IDictionary)NativeAccess.Get(manager, "Modules")!;
        var helper = assembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        var method = helper.GetMethod("GetReferFilesFromWorkingDirectory", NativeAccess.Flags)!;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in moduleNames)
        {
            var module = source[name] ?? throw new BridgeException("UnknownModule", name);
            if (method.Invoke(null, new[] { module }) is IEnumerable nativeFiles)
                foreach (string file in nativeFiles) files.Add(SessionGuard.RelativeFile(currentPath!, file));
            if (NativeAccess.Get(module, "ToRemoveFiles") is IEnumerable removed)
                foreach (string file in removed) files.Add(SessionGuard.RelativeFile(currentPath!, file));
        }
        return files.Order(StringComparer.Ordinal).ToArray();
    });

    private object RequireWriteContext(DesignerContext expected)
    {
        if (HasUncertainNativeExecution(expected.SessionId) || platformExecutionUncertain?.Invoke(expected.SessionId) is true)
            throw new BridgeException("NativeExecutionUncertain", "原生工具之前已超时且可能仍在执行；请先核对结果并重新打开目标文档，当前会话不继续写入。");
        var state = ReadNativeState();
        var context = state.Context;
        if (context.SessionId != expected.SessionId || context.ProjectPath != expected.ProjectPath || context.LockHash != expected.LockHash
            || context.CurrentUser != expected.CurrentUser || context.HasUnsavedChanges || context.Busy || context.ModuleStateStale)
            throw new BridgeException("ContextChangedBeforeNativeCall", "UI 线程执行前工程或协同状态已变化，未调用原生写操作。");
        return RequireSession(expected.SessionId).Manager;
    }

    public async Task<bool> CheckoutAsync(string[] moduleNames, string beginCommitId, DesignerContext expected) => await dispatcher.InvokeAsync(() =>
    {
        NativeAccess.RequireV12Legacy(assembly);
        var manager = RequireWriteContext(expected);
        var source = (IDictionary)NativeAccess.Get(manager, "Modules")!;
        var executorType = assembly.GetType("Forguncy.Common.CollaborationExecutor", true)!;
        var simpleType = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var method = executorType.GetMethod("Checkout", NativeAccess.Flags)!;
        var tupleType = method.GetParameters()[0].ParameterType.GetGenericArguments()[0];
        var stateType = tupleType.GetGenericArguments()[1];
        var items = Array.CreateInstance(tupleType, moduleNames.Length);
        for (var i = 0; i < moduleNames.Length; i++)
        {
            var simple = Activator.CreateInstance(simpleType, new[] { source[moduleNames[i]] })!;
            items.SetValue(Activator.CreateInstance(tupleType, simple, Enum.ToObject(stateType, 1)), i);
        }
        var executor = NativeCheckout.CreateExecutor(assembly, items);
        return NativeAccess.Call(executor, "Checkout", items, beginCommitId, CreateTaskInfo(), false) is true;
    });

    public async Task<bool> CheckinAsync(string[] moduleNames, string message, DesignerContext expected) => await dispatcher.InvokeAsync(async () =>
    {
        NativeAccess.RequireV12Legacy(assembly);
        var manager = RequireWriteContext(expected);
        return await (Task<bool>)NativeAccess.Call(manager, "CheckInAsync", message, moduleNames, CreateTaskInfo())!;
    }).Task.Unwrap();

    private object CreateTaskInfo()
    {
        var parameter = assembly.GetType("Forguncy.Common.CollaborationManager", true)!.GetMethod("CheckInAsync", NativeAccess.Flags)!.GetParameters()[2];
        return Activator.CreateInstance(parameter.ParameterType, false, true, 100)!;
    }
    public async Task<string?> GetRemoteHeadAsync(GitState git) => await new GitProbe((await CaptureAsync()).ProjectPath).RemoteHeadAsync(git);
    public async Task<string[]> GetCommittedFilesAsync(string oldHead, string newHead) => await new GitProbe((await CaptureAsync()).ProjectPath).CommittedFilesAsync(oldHead, newHead);
}
