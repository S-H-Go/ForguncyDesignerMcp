using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter : IProjectCollaborationReceiver
{
    public Task<ProjectSnapshot> GetCurrentSnapshotAfterReceiveAsync(CancellationToken cancellationToken = default) =>
        OnUi(async () => { await WaitCurrentReceiveDocumentAsync(); return context.CaptureSnapshot(); }, cancellationToken);

    public async Task<CollaborationReceiveReceipt> ReceiveAsync(CollaborationReceiveRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try { return await OnUi(() => ReceiveCoreAsync(request, cancellationToken), cancellationToken); }
        finally { writer.Release(); }
    }

    private async Task<CollaborationReceiveReceipt> ReceiveCoreAsync(CollaborationReceiveRequest request, CancellationToken token)
    {
        var dispatched = false;
        try
        {
            ValidateOperationId(request.OperationId);
            if (uncertain || pending.Count != 0 || apiPending) throw new BridgeException("NativeExecutionUncertain", "The current adapter has pending or uncertain changes.");
            var before = guard.Value.RequireWrite(request.Target, request.ExpectedProjectRevision, null);
            var initialHostSnapshot = context.CaptureSnapshot();
            var initialWorkbook = NativeReflection.Get(context.NativeDocument, "Workbook");
            if (before.InCollaborationMode || before.Busy || before.HasUnsavedChanges)
                throw new BridgeException("ReceiveRequiresCleanStandalone", "The source must be a clean standalone document.");
            var trustedRoot = context.CollaborationReceiveRoot ?? throw new BridgeException("ReceiveRootUnavailable", "The server has no configured receive root.");
            using var pathLease = new NativeReceivePathLease();
            var native = NativeReceiveContract.Resolve(context.DesignerAssembly);
            var actor = RequireReceiveIdentity(native.Manager, request.ExpectedUser);
            using var remote = new NativeReceiveGit(request); remote.Preflight();
            pathLease.EnsureServerRoot(trustedRoot);
            if (!remote.IsNetwork) pathLease.AddChain(remote.BarePath);
            pathLease.AddChain(Path.GetDirectoryName(Path.GetFullPath(before.Target.ProjectPath))!);
            var destination = NativeReceiveDestination.Validate(trustedRoot, request.DestinationParent,
                before.Target.ProjectPath, remote.BarePath);
            using var source = new FileStream(before.Target.ProjectPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sourceHash = HashOpenFile(source);
            if (guard.Value.Capture() != before) throw new BridgeException("ReceiveBaselineChanged", "The source document changed during receive preflight.");
            remote.RequireRemoteHead(); token.ThrowIfCancellationRequested();
            NativeReceiveDestination.Validate(trustedRoot, destination, before.Target.ProjectPath, remote.BarePath);
            pathLease.CreateExclusive(destination);
            var option = Activator.CreateInstance(native.OptionType, [true, request.RemoteUrl, request.Branch, destination])!;
            var taskInfo = Activator.CreateInstance(native.TaskType, [false, false, 100])!;
            if (guard.Value.Capture() != before || HashOpenFile(source) != sourceHash) throw new BridgeException("ReceiveBaselineChanged", "The source changed before native dispatch.");
            remote.RequireRemoteHead(); RequireReceiveIdentity(native.Manager, actor);
            var collaborationAssembly = native.OptionType.Assembly;
            var providerType = collaborationAssembly.GetType("Forguncy.Collaboration.CollaborationService", true)!;
            object Provider() => providerType.GetProperty("Collaboration", NativeReflection.Flags)?.GetValue(null) ??
                throw new BridgeException("NativeReceiveProviderMissing", "Native collaboration provider is unavailable.");
            dispatched = true;
            if (remote.IsNetwork)
            {
                // The manager wrapper displays modal error dialogs; call the same pinned
                // native Git provider operations directly for unattended HTTPS receive.
                var providerBefore = Provider();
                var gitType = collaborationAssembly.GetType("Forguncy.Collaboration.Git.GitCollaboration", true)!;
                if (providerBefore.GetType() != gitType) throw new BridgeException("NativeReceiveProviderMissing", "The native Git provider is unavailable.");
                var initialize = gitType.GetMethod("InitializeWorkingDirectory", NativeReflection.Flags, null, [native.OptionType], null)!;
                var update = gitType.GetMethod("Update", NativeReflection.Flags, null, Type.EmptyTypes, null)!;
                try
                {
                    initialize.Invoke(providerBefore, [option]);
                    if (update.Invoke(providerBefore, null) is not true) throw new BridgeException("NativeReceiveInitializeFailed", "Native receive update returned false.");
                }
                catch (TargetInvocationException e) when (e.InnerException?.GetType().Name == "Collaboration401Exception")
                { throw new BridgeException("ReceiveRemoteAuthenticationOrTransportFailed", "Native non-interactive remote authentication failed."); }
            }
            else if (native.Initialize.Invoke(native.Manager, [option, taskInfo, native.ProgressMessage, true]) is not true)
                throw new BridgeException("NativeReceiveInitializeFailed", "Native receive initialization returned false.");
            var provider = Provider();
            var working = NativeReflection.Get(provider, "WorkingDirectory") as string ?? throw new BridgeException("NativeReceivePathMissing", "Native working directory unavailable.");
            var canonicalWorking = Path.GetFullPath(working).TrimEnd('\\','/');
            var leaf = Path.GetFileName(canonicalWorking); var repository = remote.RepositoryName;
            if (!string.Equals(Path.GetDirectoryName(canonicalWorking), destination, StringComparison.OrdinalIgnoreCase) ||
                (leaf != repository && leaf != repository + "-" + request.Branch) || !Directory.Exists(canonicalWorking))
                throw new BridgeException("NativeReceivePathMismatch", "Native clone is outside its unique requested parent.");
            NativeReceiveDestination.RequireUnlinkedExistingChain(canonicalWorking);
            RequireReceiveProvider(provider, canonicalWorking, request);
            remote.VerifyClone(canonicalWorking);
            void RequireOriginalDocumentUnchanged()
            {
                var current = context.CaptureSnapshot();
                if (current != initialHostSnapshot || !ReferenceEquals(initialWorkbook, NativeReflection.Get(context.NativeDocument, "Workbook")) ||
                    NativeReflection.Get(context.NativeDocument, "ContainsUnsavedChanges") is not false ||
                    NativeReflection.Get(context.NativeDocument, "Opening") is not false || NativeReflection.Get(context.NativeDocument, "IsImporting") is not false ||
                    !SamePath(NativeReflection.Get(context.NativeDocument, "CurrentFilePath") as string, before.Target.ProjectPath) || HashOpenFile(source) != sourceHash ||
                    !string.Equals(NativeProjectGuard.SourceRevision(before.Target.ProjectPath), sourceHash, StringComparison.Ordinal))
                    throw new BridgeException("ReceiveBaselineChanged", "The original document or source changed before native open.");
            }
            RequireOriginalDocumentUnchanged();
            NativeReceiveFormat.RequireNativeVersion(context.DesignerAssembly, canonicalWorking);
            var utility = context.DesignerAssembly.GetType("gz", true)!;
            var manifestName = NativeReflection.Static(utility, "get_ata") as string ?? throw new BridgeException("ReceiveUnsupportedFormat", "The native plugin declaration name is unavailable.");
            if (Path.GetFileName(manifestName) != manifestName || string.IsNullOrWhiteSpace(manifestName)) throw new BridgeException("ReceiveUnsupportedFormat", "The native plugin declaration name is invalid.");
            var manifest = Path.Combine(canonicalWorking, manifestName);
            if (File.Exists(manifest))
            {
                if ((File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0 || new FileInfo(manifest).Length > 16 * 1024 * 1024)
                    throw new BridgeException("ReceiveUnsupportedFormat", "The native plugin declaration is invalid.");
                using var pluginData = JsonDocument.Parse(File.ReadAllBytes(manifest));
                NativeReceiveGit.RequireDependencies(pluginData.RootElement, () => PluginLifecycle.ListInstalled());
            }
            native.Reload.Invoke(native.Manager, null);
            RequireReceiveRegistry(native.Manager, canonicalWorking);
            RequireOriginalDocumentUnchanged();
            var openTask = native.Open.Invoke(native.Manager, [taskInfo]) as Task ?? throw new BridgeException("NativeReceiveOpenMissing", "Native open task unavailable.");
            // Once dispatched, await the original task to completion even if the caller cancels.
            await openTask;
            if (Convert.ToInt32(openTask.GetType().GetProperty("Result")!.GetValue(openTask)) != 2)
                throw new BridgeException("NativeReceiveOpenRejected", "Native version-checked open did not return collaboration success.");
            var document = await WaitCurrentReceiveDocumentAsync();
            var workbook = NativeReflection.Get(document, "Workbook") ?? throw new BridgeException("NativeReceiveDocumentMissing", "Opened workbook is unavailable.");
            var sheets = (NativeReflection.Get(workbook, "Sheets") as IEnumerable)?.Cast<object>().ToArray() ?? [];
            var active = NativeReflection.Get(workbook, "ActiveSheet");
            if (sheets.Length == 0 || active is null || !sheets.Any(x => ReferenceEquals(x, active)) ||
                NativeReflection.Get(document, "Opening") is not false || NativeReflection.Get(document, "IsImporting") is not false ||
                NativeReflection.Get(document, "ContainsUnsavedChanges") is not false || NativeReflection.Get(native.Manager, "InCollaborationMode") is not true ||
                !SamePath(NativeReflection.Get(document, "CurrentFilePath") as string, canonicalWorking))
                throw new BridgeException("NativeReceiveOpenUnverified", "Native open did not produce a completed, clean collaboration workbook.");
            RequireReceiveProvider(Provider(), canonicalWorking, request);
            RequireReceiveRegistry(native.Manager, canonicalWorking);
            remote.VerifyClone(canonicalWorking);
            if (HashOpenFile(source) != sourceHash || !File.Exists(before.Target.ProjectPath) ||
                !string.Equals(NativeProjectGuard.SourceRevision(before.Target.ProjectPath), sourceHash, StringComparison.Ordinal))
                throw new BridgeException("ReceiveSourceChanged", "The original standalone source changed during native receive.");
            var after = context.CaptureSnapshot();
            RequireReceiveIdentity(native.Manager, actor);
            if (after.Target.InstanceId != before.Target.InstanceId || after.Target.DesignerVersion != before.Target.DesignerVersion ||
                string.IsNullOrWhiteSpace(after.Target.SessionId) || after.Target.SessionId == before.Target.SessionId ||
                !SamePath(after.Target.ProjectPath, canonicalWorking) || !after.InCollaborationMode || after.Busy || after.HasUnsavedChanges || after.CurrentUser != actor)
                throw new BridgeException("NativeReceiveSessionUnverified", "Host did not observe the actual new clean collaboration session.");
            return new(request.OperationId, "succeeded", "NativeReceiveVerified", "Native collaboration receiver opened and verified the new session.",
                after, request.RemoteUrl, request.Branch, remote.ExpectedHead, true, true, true, true, false, true);
        }
        catch (Exception error) when (error is not OperationCanceledException || dispatched)
        {
            if (dispatched) uncertain = true;
            var actual = error is TargetInvocationException { InnerException: not null } invocation ? invocation.InnerException : error;
            var code = actual is BridgeException bridge ? bridge.Code : "NativeReceiveFailed";
            return new(request.OperationId, dispatched ? "verification_failed" : "failed", code,
                dispatched ? "Native receive was dispatched; its outcome must be reconciled before further writes." : actual!.Message,
                NativeDispatched: dispatched);
        }
    }

    private async Task<object> WaitCurrentReceiveDocumentAsync()
    {
        var applicationType = Type.GetType("System.Windows.Application, PresentationFramework", true)!;
        var application = applicationType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        var window = application is null ? null : NativeReflection.Get(application, "MainWindow");
        var document = window is null ? null : NativeReflection.Get(window, "SaveLoadManager");
        if (document is null || document.GetType().Assembly != context.DesignerAssembly)
            throw new BridgeException("NativeReceiveDocumentMissing", "The current native document cannot be independently identified.");
        var wait = document.GetType().GetMethod("WaitOpenCompletedAsync", NativeReflection.Flags, null, Type.EmptyTypes, null)?.Invoke(document, null) as Task
            ?? throw new BridgeException("NativeReceiveDocumentMissing", "Native open completion task is unavailable.");
        await wait;
        foreach (var name in new[] { "WaitFillRemainTask", "WaitFillRemainModulesTask" })
            if (NativeReflection.Get(document, name) is Task fill) await fill;
        if (!ReferenceEquals(window is null ? null : NativeReflection.Get(window, "SaveLoadManager"), document))
            throw new BridgeException("NativeReceiveDocumentChanged", "The native document changed while awaiting completion.");
        return document;
    }
    private static string HashOpenFile(FileStream stream)
    {
        stream.Position = 0;
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    private static bool SamePath(string? left, string right) => !string.IsNullOrWhiteSpace(left) &&
        string.Equals(Path.GetFullPath(left).TrimEnd('\\','/'), Path.GetFullPath(right).TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase);
    internal static UserIdentity RequireReceiveIdentity(object manager, UserIdentity expected)
    {
        var user = NativeCollaboration.ReadCurrentIdentity(manager);
        if (user is null || user != expected)
            throw new BridgeException("ReceiveIdentityMismatch", "A complete native collaboration identity must match the reviewed receiver identity.");
        return user;
    }
    private static void RequireReceiveProvider(object provider, string working, CollaborationReceiveRequest request)
    {
        if (!SamePath(NativeReflection.Get(provider, "WorkingDirectory") as string, working) ||
            NativeReflection.Get(provider, "RemoteAddress") as string != request.RemoteUrl || NativeReflection.Get(provider, "BranchName") as string != request.Branch)
            throw new BridgeException("NativeReceiveProviderMismatch", "Native provider path, remote, or branch differs from the request.");
    }
    private static void RequireReceiveRegistry(object manager, string working)
    {
        var path = Path.Combine(working, ".collaboration");
        if (!SamePath(NativeReflection.Get(manager, "ModuleFilePath") as string, path) ||
            NativeReflection.Get(manager, "Modules") is not IDictionary { Count: > 0 } memory || !File.Exists(path))
            throw new BridgeException("NativeReceiveRegistryMismatch", "Native collaboration registry was not loaded from the new clone.");
        var moduleType = Assembly.Load("Forguncy.Collaboration").GetType("Forguncy.Collaboration.Module", true)!;
        var list = typeof(List<>).MakeGenericType(moduleType);
        var utility = Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!;
        var read = utility.GetMethods(NativeReflection.Flags).Single(method => method.Name == "ReadObjectFromFile" && method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string));
        var source = ((IEnumerable)read.MakeGenericMethod(list).Invoke(null, [path, null])!).Cast<object>().ToArray();
        string Name(object module) => (string)NativeReflection.Get(module, "ModuleName")!;
        string Key(object module)
        {
            var state = NativeReflection.Get(module, "State")!.ToString();
            var owner = state == "Unlocked" ? null : NativeReflection.Get(module, "LockedBy");
            return JsonSerializer.Serialize(new { Name = Name(module), Type = NativeReflection.Get(module, "ModuleType")!.ToString(), State = state,
                User = owner is null ? null : NativeReflection.Get(owner, "UserName"), Email = owner is null ? null : NativeReflection.Get(owner, "Email"),
                Date = state == "Unlocked" ? null : (NativeReflection.Get(module, "LockDateTime") as DateTime?)?.Ticks });
        }
        if (source.Select(Name).Distinct(StringComparer.Ordinal).Count() != source.Length)
            throw new BridgeException("NativeReceiveRegistryMismatch", "Persisted native registry has duplicate module names.");
        var cached = memory.Values.Cast<object>().ToArray();
        foreach (var module in cached)
        {
            var match = source.SingleOrDefault(item => Name(item) == Name(module));
            if (match is null && NativeReflection.Get(module, "State")?.ToString() == "Unlocked") continue;
            if (match is null || Key(match) != Key(module)) throw new BridgeException("NativeReceiveRegistryMismatch", "Native memory and persisted registry disagree.");
        }
        if (source.Any(module => NativeReflection.Get(module, "State")?.ToString() != "Unlocked" && !cached.Any(item => Name(item) == Name(module))))
            throw new BridgeException("NativeReceiveRegistryMismatch", "An active persisted registration is absent from native memory.");
    }
}












