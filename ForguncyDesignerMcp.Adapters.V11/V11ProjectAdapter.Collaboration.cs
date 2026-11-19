using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter : IProjectCollaborationCancellationAdapter
{
    public async Task<CollaborationMutationReceipt?> InspectCancelledNewAsync(CollaborationMutationRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                if (request.Modules.Count != 1 || !cancelledNew.TryGetValue(request.Modules[0], out var journal)) return null;
                void Verify()
                {
                    CheckWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser);
                    if (!Snapshot().InCollaborationMode) throw new BridgeException("CollaborationNotApplicable", "当前工程不在协同模式。");
                    NativeV11Collaboration.VerifyCancelledAfterSave(journal.Plan);
                    if (NativeV11PendingSave.ReadFiles(session.SourcePath, journal.SourcePaths).Values.Any(value => value is not null))
                        throw new BridgeException("SourceRevisionChanged", "已撤销新建的源文件重新出现，不能确认撤销状态。");
                }
                Verify();
                var before = Snapshot();
                var native = NativeV11Collaboration.ReadStatus(session);
                var probe = new GitProbe(session.SourcePath);
                var git = await probe.CaptureAsync();
                var remote = await probe.RemoteHeadAsync(git);
                Verify();
                var after = Snapshot();
                if (before != after || native.Target != after.Target || native.CurrentUser != after.CurrentUser ||
                    native.Modules.Any(module => module.Address == request.Modules[0]) || !git.IndexClean ||
                    git.RemoteUrl != request.ExpectedRemoteUrl || git.Branch != request.ExpectedBranch ||
                    remote is null || remote != request.ExpectedRemoteHead || git.Head != remote)
                    throw new BridgeException("CancellationEvidenceUnverified", "撤销日志、工程状态或远端 Git 基线未能完整核实。");
                var status = native with { Revision = after.Revision, Git = git with { UpstreamHead = remote } };
                return new CollaborationMutationReceipt(request.OperationId, "cancelled", "NewModuleCancelled",
                    "该新建模块已撤销并保存；未派发签入。删除操作：" + journal.DeleteOperationId,
                    status, false, true, true, null, true);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<CollaborationStatus> GetCollaborationStatusAsync(CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                session.VerifyContext();
                var before = Snapshot();
                if (!before.InCollaborationMode) return new(before.Target, false, before.CurrentUser, before.Revision, []);
                var native = NativeV11Collaboration.ReadStatus(session);
                var probe = new GitProbe(session.SourcePath);
                var git = await probe.CaptureAsync();
                var remote = await probe.RemoteHeadAsync(git);
                var after = Snapshot();
                if (before != after || native.Target != after.Target || native.CurrentUser != after.CurrentUser)
                    throw new BridgeException("CollaborationBaselineChanged", "读取协同状态期间工程或用户发生变化。");
                return native with { Revision = after.Revision, Git = git with { UpstreamHead = remote } };
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<CollaborationMutationReceipt> CheckoutAsync(CollaborationMutationRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                CheckWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser);
                if (!context.CaptureSnapshot().InCollaborationMode) throw new BridgeException("CollaborationNotApplicable", "独立工程不能签出。");
                var owners = NativeV11CollaborationOwners.Resolve(session, request.Modules);
                var plan = NativeV11Collaboration.CreatePlan(session, owners, request.ExpectedUser);
                void VerifyContext()
                {
                    CheckTarget(request.Target);
                    var current = context.CaptureSnapshot();
                    if (!current.InCollaborationMode || current.CurrentUser != request.ExpectedUser || current.Busy || current.HasUnsavedChanges)
                        throw new BridgeException("CollaborationBaselineChanged", "协同工程或用户状态已变化。");
                }
                var result = await NativeV11Collaboration.CheckoutExistingPlanAsync(plan, request, VerifyContext, cancellationToken);
                if (result.ExecutionMayContinue) requiresReview = true;
                var status = result.Status is null ? null : NativeV11Collaboration.ReadStatus(session) with
                {
                    Revision = Snapshot().Revision,
                    Git = result.Repository?.Git ?? result.Status.Git
                };
                return new CollaborationMutationReceipt(request.OperationId,
                    result.LockStateVerified && !result.ExecutionMayContinue ? "succeeded" :
                        result.NativeAccepted is null && !result.ExecutionMayContinue ? "failed" : "verification_failed",
                    result.Code, result.Code == "AlreadyOwned" ? "原生本人锁及远端基线已核实；未重复签出。" : "原生签出未确认，详情见状态码。",
                    status, result.NativeAccepted, result.LockStateVerified, ExecutionMayContinue: result.ExecutionMayContinue);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<CollaborationMutationReceipt> CheckinAsync(CollaborationMutationRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                CheckWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser);
                if (!context.CaptureSnapshot().InCollaborationMode || string.IsNullOrWhiteSpace(request.Comment) ||
                    request.Modules.Count == 0 || request.Modules.Any(address => address.Kind is not (ModuleKind.Page or ModuleKind.Report or
                        ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.BusinessProcess or ModuleKind.Image or ModuleKind.Folder or ModuleKind.Table or ModuleKind.View) &&
                        NativeV11CollaborationOwners.SettingsOwnerAddress(address.Kind) != address &&
                        address != new ModuleAddress(ModuleKind.ResourceFile, "Current", "MapJsonFile")))
                    throw new BridgeException("NativeCheckinScopeUnavailable", "当前签入仅接受已验证文件组的页面、报表、命令、计划任务、通知、流程、图片或设置根及非空说明。");
                if (request.Modules.Any(cancelledNew.ContainsKey))
                {
                    if (request.Modules.Count != 1 || !cancelledNew.TryGetValue(request.Modules[0], out var cancellation))
                        throw new BridgeException("NativeCheckinScopeUnavailable", "已撤销新建的模块不能与其他模块合并签入。");
                    NativeV11Collaboration.VerifyCancelledAfterSave(cancellation.Plan);
                    if (NativeV11PendingSave.ReadFiles(session.SourcePath, cancellation.SourcePaths).Values.Any(value => value is not null))
                        throw new BridgeException("SourceRevisionChanged", "已撤销新建的源文件重新出现，不能确认撤销状态。");
                    var status = NativeV11Collaboration.ReadStatus(session) with { Revision = Snapshot().Revision };
                    return new CollaborationMutationReceipt(request.OperationId, "failed", "NewModuleCancelled",
                        "该新建模块已撤销并保存，无可签入的业务版本；未派发签入。删除操作：" + cancellation.DeleteOperationId +
                        (cancellation.CreateOperationId is null ? "；创建操作不在当前会话记录中。" : "；创建操作：" + cancellation.CreateOperationId),
                        status, false, false, true);
                }
                var firstMatches = savedFirstProcesses.Where(pair => request.Modules.Contains(pair.Key)).ToArray();
                if (savedFirstProcesses.Any(pair => pair.Value.Plan.Owners.Any(owner => request.Modules.Contains(owner.Address)) && !request.Modules.Contains(pair.Key)))
                    throw new BridgeException("NativeCheckinScopeUnavailable", "首次流程生成的原生模块必须与该流程及完整文件组一并签入。");
                if (firstMatches.Length > 1) throw new BridgeException("NativeCheckinScopeUnavailable", "首次流程签入只能使用一份已保存的原生证明。");
                var first = firstMatches.Length == 1 ? firstMatches[0].Value : ((NativeV11PendingSave Saved, NativeV11Collaboration.Plan Plan)?)null;
                var folderMatches = savedFolderMoves.Where(pair => pair.Value.Plan.Owners.Any(owner =>
                    request.Modules.Contains(owner.State == "Move" ? pair.Value.Saved.FolderMoveJournal!.CurrentAddress(owner.Address) : owner.Address))).ToArray();
                var deleteMatches = savedFolderDeletes.Where(pair => pair.Value.Plan.Owners.Any(owner => request.Modules.Contains(owner.Address))).ToArray();
                if (folderMatches.Length > 1 || deleteMatches.Length > 1 ||
                    (first is not null ? 1 : 0) + (folderMatches.Length != 0 ? 1 : 0) + (deleteMatches.Length != 0 ? 1 : 0) > 1)
                    throw new BridgeException("NativeCheckinScopeUnavailable", "分组目录签入必须独立使用一份完整保存证明。");
                var folder = folderMatches.Length == 1 ? folderMatches[0].Value : ((NativeV11PendingSave Saved, NativeV11Collaboration.Plan Plan)?)null;
                var deletedFolder = deleteMatches.Length == 1 ? deleteMatches[0].Value : ((NativeV11PendingSave Saved, NativeV11Collaboration.Plan Plan)?)null;
                var retained = first ?? folder ?? deletedFolder;
                var owners = retained is not null ? retained.Value.Plan.Owners : NativeV11CollaborationOwners.ExpandExistingOwners(session, NativeV11CollaborationOwners.ResolveForCheckin(session, request.Modules),
                    allowSharedTableDatabase: request.Modules.Any(address => address.Kind is ModuleKind.Table or ModuleKind.View));
                if (owners.Count != request.Modules.Count || owners.Any(owner => !request.Modules.Contains(folder is not null && owner.State == "Move"
                    ? folder.Value.Saved.FolderMoveJournal!.CurrentAddress(owner.Address) : owner.Address)))
                    throw new BridgeException("NativeCheckinScopeUnavailable", "共享文件涉及其他原生模块，请明确选择完整范围。");
                var plan = retained is not null ? retained.Value.Plan : NativeV11Collaboration.CreatePlan(session, owners, request.ExpectedUser, allowPendingDeletion: true);
                NativeV11Collaboration.VerifyOwned(plan, true, requireRegisteredModel: retained is not null);
                var ownerNames = owners.Select(owner => (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!).ToHashSet(StringComparer.Ordinal);
                var assetProof = savedAssets is not null && savedAssets.Owners.Any(ownerNames.Contains) ? savedAssets : null;
                if (assetProof is not null && (retained is not null || assetProof.Owners.Any(name => !ownerNames.Contains(name)) ||
                    assetProof.SourceRevision != NativeV11SourceRevision.Capture(session.SourcePath)))
                    throw new BridgeException("AssetCheckinScopeUnverified", "资源保存证明、原生所有者或工程源已变化，未派发签入。 ");
                if (first is not null && (!first.Value.Saved.SourceMatches(session) || first.Value.Saved.FirstProcessJournal is null))
                    throw new BridgeException("SourceRevisionChanged", "首次流程的保存证明已变化，未派发签入。");
                if (folder is not null && (!folder.Value.Saved.SourceMatches(session) || folder.Value.Saved.FolderMoveJournal is null))
                    throw new BridgeException("SourceRevisionChanged", "分组目录的保存证明已变化，未派发签入。");
                if (deletedFolder is not null && (!deletedFolder.Value.Saved.SourceMatches(session) || deletedFolder.Value.Saved.FolderDeleteJournal is null))
                    throw new BridgeException("SourceRevisionChanged", "分组目录删除的保存证明已变化，未派发签入。");
                var expected = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
                var expectedDatabases = new Dictionary<string, (string Hash, long Length, bool Valid)>(StringComparer.OrdinalIgnoreCase);
                var files = plan.Files.Values.SelectMany(value => value).Concat(first?.Saved.FirstProcessJournal?.TemplatePaths ?? Array.Empty<string>())
                    .Concat(folder?.Saved.FolderMoveJournal?.Paths ?? Array.Empty<string>())
                    .Concat(deletedFolder?.Saved.FolderDeleteJournal?.Paths ?? Array.Empty<string>())
                    .Concat(assetProof?.Paths.Keys ?? Enumerable.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                string? tableDatabase = null;
                if (owners.Any(owner => owner.Address.Kind is ModuleKind.Table or ModuleKind.View))
                    tableDatabase = NativeV11CollaborationOwners.TableDatabasePath();
                if (retained is not null)
                {
                    foreach (var pair in NativeV11PendingSave.ReadFiles(session.SourcePath, files.Where(path =>
                        !path.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase) &&
                        !path.Equals(tableDatabase, StringComparison.OrdinalIgnoreCase)))) expected.Add(pair.Key, pair.Value);
                }
                foreach (var owner in retained is null ? owners : Array.Empty<NativeV11CollaborationOwner>())
                {
                    if (owner.State == "Remove")
                    {
                        var name = (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!;
                        foreach (var path in plan.Files[name])
                            if (!(owner.Address.Kind == ModuleKind.BusinessProcess && path.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase)) &&
                                !(NativeV11Reflection.Get(owner.SimpleModule, "ModuleType")?.ToString() == "TableProcessView" && path.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase)) &&
                                !(owner.Address.Kind is ModuleKind.Table or ModuleKind.View && path.Equals(tableDatabase, StringComparison.OrdinalIgnoreCase)))
                                expected.Add(path, null);
                        continue;
                    }
                    if (NativeV11CollaborationOwners.SettingsOwnerAddress(owner.Address.Kind) == owner.Address)
                    {
                        foreach (var file in settings.CaptureSaveFiles(owner.Address.Kind)) expected.Add(file.Key, file.Value);
                        if (owner.Address.Kind == ModuleKind.CustomLibrary && files.Any(path => path.StartsWith("UserJaveScript/", StringComparison.OrdinalIgnoreCase)))
                        {
                            var libraries = new NativeV11LocalLibraries(session, settings);
                            foreach (var library in libraries.List(ModuleKind.ResourceFile))
                                foreach (var asset in library.Companions.Where(asset => files.Contains(asset.RelativePath, StringComparer.OrdinalIgnoreCase)))
                                    expected.Add(asset.RelativePath, Convert.FromBase64String(asset.ContentBase64));
                        }
                        continue;
                    }
                    if (owner.Address.Kind == ModuleKind.Image)
                    {
                        var image = ReadDocument(owner.Address);
                        foreach (var asset in image.Companions) expected.Add(asset.RelativePath, Convert.FromBase64String(asset.ContentBase64));
                        continue;
                    }
                    if (owner.Address == new ModuleAddress(ModuleKind.ResourceFile, "Current", "MapJsonFile"))
                    {
                        foreach (var path in plan.Files[(string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!])
                        {
                            var source = NativeV11PendingSave.ReadFiles(session.SourcePath, [path])[path];
                            var local = NativeV11PendingSave.ReadFiles(session.LocalPath, [path])[path];
                            var staged = NativeV11PendingSave.ReadFiles(session.LastPath, [path])[path];
                            if (!SameBytes(source, local) || !SameBytes(source, staged))
                                throw new BridgeException("AssetCheckinScopeUnverified", "资源原生文件组的源、缓存与暂存不一致。 ");
                            expected.Add(path, source);
                        }
                        continue;
                    }
                    if (owner.Address.Kind == ModuleKind.Folder)
                    {
                        var marker = new NativeV11Folders(session).SourcePaths(owner.Address).Single();
                        if (NativeV11Folders.SourceState(session.SourcePath, marker).Empty)
                            throw new BridgeException("EmptyFolderNotRepresentableInGit", "空目录不能作为 Git 树单独保存；请先纳入受控的目录内容再签入。");
                        foreach (var file in new NativeV11Folders(session).CaptureSaveFiles(owner.Address)) expected.Add(file.Key, file.Value);
                        continue;
                    }
                    var document = session.Read(session.Find(owner.Address));
                    var main = session.NativePath(owner.Address);
                    expected.Add(main, System.Text.Encoding.UTF8.GetBytes(NativeV11SourceMetadata.Capture(session, owner.Address).GetRawText()));
                    foreach (var asset in document.Companions) expected.Add(asset.RelativePath, Convert.FromBase64String(asset.ContentBase64));
                }
                const string processDatabase = NativeV11ProcessInitialization.Database;
                if (tableDatabase is not null && owners.Any(owner => owner.Address.Kind is ModuleKind.Table or ModuleKind.View) &&
                    files.Contains(tableDatabase, StringComparer.OrdinalIgnoreCase))
                {
                    if (new[] { session.SourcePath, session.LocalPath, session.LastPath }.Any(root =>
                        !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(NativeV11Session.Under(root, tableDatabase))))
                        throw new BridgeException("NativeCheckinScopeUnavailable", "原生表数据库存在活动日志，不能确认完整保存组。");
                    var fingerprint = NativeV11UnchangedDatabaseProof.ReadFingerprint(session.LocalPath, tableDatabase);
                    if (!fingerprint.Valid) throw new BridgeException("NativeCheckinScopeUnavailable", "原生表数据库无效，不能确认完整文件组。");
                    expectedDatabases.Add(tableDatabase, fingerprint);
                }
                if (owners.Any(owner => owner.Address == new ModuleAddress(ModuleKind.CustomLibrary, "CustomLibraries")))
                    foreach (var path in files.Where(path => path.StartsWith("UserJaveScript/", StringComparison.OrdinalIgnoreCase) && !expected.ContainsKey(path)))
                    {
                        var sourceFile = NativeV11PendingSave.ReadFiles(session.SourcePath, [path])[path];
                        var localFile = NativeV11PendingSave.ReadFiles(session.LocalPath, [path])[path];
                        if (path.EndsWith('/') && sourceFile is { Length: 0 } && localFile is { Length: 0 })
                            expected.Add(path, []);
                        else if (sourceFile is null && localFile is null)
                            expected.Add(path, null);
                    }
                if (owners.Any(owner => owner.Address.Kind == ModuleKind.BusinessProcess || NativeV11Reflection.Get(owner.SimpleModule, "ModuleType")?.ToString() == "TableProcessView") &&
                    files.Contains(processDatabase, StringComparer.OrdinalIgnoreCase))
                {
                    if (new[] { session.SourcePath, session.LocalPath, session.LastPath }.Any(root =>
                        !NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(NativeV11Session.Under(root, processDatabase))))
                        throw new BridgeException("NativeCheckinScopeUnavailable", "原生流程数据库存在活动日志，不能确认完整保存组。");
                    var fingerprint = NativeV11UnchangedDatabaseProof.ReadFingerprint(session.LocalPath, processDatabase);
                    if (!fingerprint.Valid) throw new BridgeException("NativeCheckinScopeUnavailable", "共享流程数据库无效，不能确认完整文件组。");
                    expectedDatabases.Add(processDatabase, fingerprint);
                }
                if (assetProof is not null)
                    foreach (var (path, evidence) in assetProof.Paths)
                    {
                        if (expected.TryGetValue(path, out var existing) && !SameBytes(existing, evidence.Content))
                            throw new BridgeException("AssetCheckinScopeUnverified", "资源保存证明与原生所有者文件组不一致。 ");
                        expected[path] = evidence.Content;
                    }
                if (files.Any(path => !expected.ContainsKey(path) && !expectedDatabases.ContainsKey(path)) ||
                    expected.Keys.Concat(expectedDatabases.Keys).Any(path => !files.Contains(path, StringComparer.OrdinalIgnoreCase)))
                    throw new BridgeException("NativeCheckinScopeUnavailable", "原生模块文件组包含尚未建立保存证明的其他资源。");
                void VerifyFirstProcess()
                {
                    if (first is null) return;
                    if (!first.Value.Saved.SourceMatches(session))
                        throw new BridgeException("SourceRevisionChanged", "首次流程的已保存模型或源文件发生变化。");
                    var direct = owners.SelectMany(owner => NativeV11CollaborationOwners.GetDirectFiles(session,
                        owner with { State = "Edit" }, includeTracked: false, allowSharedTableDatabase: true))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (first.Value.Saved.FirstProcessJournal!.TemplatePaths.Any(path => !direct.Contains(path)))
                        throw new BridgeException("NativeFirstProcessCheckinIncomplete", "原生签入文件组未覆盖全部首次流程模板，未派发签入。");
                }
                void VerifyFolderMove()
                {
                    if (folder is null) return;
                    if (!folder.Value.Saved.SourceMatches(session))
                        throw new BridgeException("SourceRevisionChanged", "分组目录的已保存模型或源文件发生变化。");
                    NativeV11Collaboration.VerifyGroupedMoveRemovalFiles(plan);
                    var journal = folder.Value.Saved.FolderMoveJournal!;
                    var direct = owners.Where(owner => owner.State == "Move")
                        .SelectMany(owner => NativeV11CollaborationOwners.GetDirectFiles(session,
                            owner with { Address = journal.CurrentAddress(owner.Address), State = "Edit" },
                            includeTracked: false, allowSharedTableDatabase: true))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (journal.NewFiles.Any(path => !direct.Contains(path)))
                        throw new BridgeException("NativeFolderCheckinIncomplete", "原生移动 owner 文件组未覆盖全部新位置文件，未派发签入。");
                }
                void VerifyFolderDelete()
                {
                    if (deletedFolder is null) return;
                    if (!deletedFolder.Value.Saved.SourceMatches(session))
                        throw new BridgeException("SourceRevisionChanged", "分组目录删除的模型、引用或源文件已变化。");
                    NativeV11Collaboration.VerifyGroupedMoveRemovalFiles(plan);
                }
                void VerifyAssets()
                {
                    if (assetProof is null) return;
                    foreach (var (path, evidence) in assetProof.Paths)
                    {
                        if (evidence.Owners.Any(owner => !ownerNames.Contains(owner)) ||
                            !SameBytes(evidence.Content, NativeV11PendingSave.ReadFiles(session.SourcePath, [path])[path]) ||
                            !SameBytes(evidence.Content, NativeV11PendingSave.ReadFiles(session.LastPath, [path])[path]) ||
                            !SameBytes(evidence.Content, NativeV11PendingSave.ReadFiles(session.LocalPath, [path])[path]))
                            throw new BridgeException("AssetCheckinScopeUnverified", "资源保存内容或所有者发生变化。 ");
                        var registrations = NativeV11Reflection.Get(plan.Modules[evidence.RegistrationOwner]!, "ToRemoveFiles") as System.Collections.IEnumerable;
                        var registered = registrations is null ? Array.Empty<string>() : registrations.Cast<object>().OfType<string>().Select(value => value.Replace('\\', '/')).ToArray();
                        if (registered.Any(value => value.Equals(path, StringComparison.OrdinalIgnoreCase) && value != path) ||
                            registered.Contains(path, StringComparer.Ordinal) != (evidence.Content is null && evidence.TrackedAtHead))
                            throw new BridgeException("AssetCheckinScopeUnverified", "原生资源删除登记与已保存文件不一致。 ");
                    }
                }
                void VerifyDatabases()
                {
                    foreach (var (path, fingerprint) in expectedDatabases)
                        foreach (var root in new[] { session.SourcePath, session.LocalPath, session.LastPath })
                        {
                            var database = NativeV11Session.Under(root, path);
                            if (!NativeV11BuiltInTableLifecycle.NoPendingSqliteJournal(database))
                                throw new BridgeException("NativeCheckinScopeUnavailable", "共享数据库存在未合并的日志。");
                            if (NativeV11UnchangedDatabaseProof.ReadFingerprint(root, path) != fingerprint)
                                throw new BridgeException("SourceRevisionChanged", "共享数据库与原生暂存或保存源不一致。");
                        }
                }
                if (NativeV11Reflection.Call(session.Document, "CanSave") is not true)
                    throw new BridgeException("NativeSaveUnavailable", "原生保存不可用，未派发签入。");
                var sourceBeforeSave = NativeV11SourceRevision.Capture(session.SourcePath);
                var save = session.Document.GetType().GetMethod("SaveAsync", NativeV11Reflection.Flags, null, [typeof(bool)], null)!;
                if (!await (Task<bool>)save.Invoke(session.Document, [false])! || NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is not false)
                    throw new BridgeException("NativeSaveUnconfirmed", "签入前原生保存未确认。");
                CheckTarget(request.Target);
                if (NativeV11SourceRevision.Capture(session.SourcePath) != sourceBeforeSave)
                    throw new BridgeException("ProjectSourceChanged", "签入前保存期间工程源发生变化，未派发签入。");
                var proof = NativeV11CollaborationSourceProof.Capture(session.SourcePath, expected, path => path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".rd", StringComparison.OrdinalIgnoreCase));
                VerifyDatabases();
                VerifyFirstProcess();
                VerifyFolderMove();
                VerifyFolderDelete();
                VerifyAssets();
                var baseline = await NativeV11CollaborationRepository.CaptureAsync(session.SourcePath, request, files.Append(".collaboration").ToArray());
                if (assetProof is not null && !SameAssetGit(assetProof.Git, baseline.Git))
                    throw new BridgeException("AssetCheckinScopeUnverified", "资源保存后的 Git 状态或提交身份已变化，未派发签入。 ");
                await NativeV11CollaborationRepository.VerifyBeforeAsync(session.SourcePath, baseline, request);
                NativeV11Collaboration.VerifyOwned(plan, true, requireRegisteredModel: retained is not null);
                proof.VerifyUnchanged(session.SourcePath);
                VerifyDatabases();
                VerifyFirstProcess();
                VerifyFolderMove();
                VerifyFolderDelete();
                VerifyAssets();
                var priorStatus = NativeV11Collaboration.ReadStatus(session);
                cancellationToken.ThrowIfCancellationRequested();
                var manager = NativeV11Collaboration.RequireBoundManager(session);
                var method = manager.GetType().GetMethod("CheckInAsync", NativeV11Reflection.Flags) ?? throw new MissingMethodException("CheckInAsync");
                var taskInfo = Activator.CreateInstance(method.GetParameters()[2].ParameterType, [false, false, 100])!;
                bool? accepted = null;
                try
                {
                    accepted = await (Task<bool>)method.Invoke(manager, [request.Comment, plan.Files.Keys.ToArray(), taskInfo])!;
                    CheckTarget(request.Target);
                    proof.VerifyUnchanged(session.SourcePath);
                    VerifyDatabases();
                    if (folder is not null && !folder.Value.Saved.FolderMoveJournal!.VerifyMoved(session.LocalPath, session.LastPath, session.SourcePath))
                        throw new InvalidOperationException("The reviewed folder subtree or unrelated source changed during native checkin.");
                    if (deletedFolder is not null && !deletedFolder.Value.Saved.FolderDeleteJournal!.VerifyDeleted(session))
                        throw new InvalidOperationException("The reviewed folder deletion or unrelated source changed during native checkin.");
                    var outcome = await NativeV11CollaborationRepository.VerifyAfterCheckinAsync(session.SourcePath, baseline, request.ExpectedUser);
                    if (first is not null && first.Value.Saved.FirstProcessJournal!.TemplatePaths.Any(path =>
                        !outcome.CommittedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("The native checkin did not commit every reviewed first-process template file.");
                    if (folder is not null && folder.Value.Saved.FolderMoveJournal!.OldFiles.Concat(folder.Value.Saved.FolderMoveJournal.NewFiles)
                        .Any(path => !outcome.CommittedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("The native checkin did not commit every original and relocated folder file.");
                    if (deletedFolder is not null && deletedFolder.Value.Saved.FolderDeleteJournal!.OldFiles
                        .Any(path => !outcome.CommittedFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("The native checkin did not commit every reviewed folder file deletion.");
                    if (assetProof is not null && assetProof.Paths.Any(pair => pair.Value.Content is null && pair.Value.TrackedAtHead &&
                        !outcome.CommittedFiles.Contains(pair.Key, StringComparer.Ordinal)))
                        throw new InvalidOperationException("The native checkin did not commit every reviewed asset deletion.");
                    foreach (var owner in owners.Where(value => value.Address.Kind == ModuleKind.Folder))
                    {
                        var name = (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!;
                        var marker = new NativeV11Folders(session).SourcePaths(owner.Address).Single();
                        if (plan.Before.TryGetValue(name, out var beforeOwner) && beforeOwner.State == 3 &&
                            !outcome.CommittedFiles.Any(path => path.StartsWith(marker, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("The new folder has no committed Git path; its remote materialization is unconfirmed.");
                    }
                    CheckTarget(request.Target);
                    proof.VerifyUnchanged(session.SourcePath);
                    VerifyDatabases();
                    if (folder is not null && !folder.Value.Saved.FolderMoveJournal!.VerifyMoved(session.LocalPath, session.LastPath, session.SourcePath))
                        throw new InvalidOperationException("The reviewed folder source changed during the final remote check.");
                    if (deletedFolder is not null && !deletedFolder.Value.Saved.FolderDeleteJournal!.VerifyDeleted(session))
                        throw new InvalidOperationException("The reviewed folder deletion changed during the final remote check.");
                    var status = NativeV11Collaboration.ReadStatus(session) with { Revision = Snapshot().Revision, Git = outcome.Git };
                    var released = owners.All(owner => owner.State == "Remove"
                        ? !status.Modules.Any(state => state.Address == owner.Address || state.NativeModuleName == (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")!)
                        : status.Modules.Count(state => state.Address == (folder is not null && owner.State == "Move"
                            ? folder.Value.Saved.FolderMoveJournal!.CurrentAddress(owner.Address) : owner.Address) &&
                            state.NativeModuleName == (string)NativeV11Reflection.Get(owner.SimpleModule, "ModuleName")! &&
                            !state.OwnedByCurrentUser && state.LockedBy is null) == 1);
                    var selectedNames = plan.Files.Keys.ToHashSet(StringComparer.Ordinal);
                    var unrelated = priorStatus.Modules.Where(state => !selectedNames.Contains(state.NativeModuleName)).ToArray();
                    var nowUnrelated = status.Modules.Where(state => !selectedNames.Contains(state.NativeModuleName)).ToArray();
                    if (status.CurrentUser != request.ExpectedUser || unrelated.Length != nowUnrelated.Length ||
                        unrelated.Any(old => nowUnrelated.Count(current => current.Address == old.Address && current.NativeModuleName == old.NativeModuleName &&
                            current.LockedBy == old.LockedBy && current.OwnedByCurrentUser == old.OwnedByCurrentUser) != 1))
                        throw new InvalidOperationException("The native user or unrelated module locks changed during checkin.");
                    if (accepted != true || !released || outcome.RemoteSynchronized != true) throw new InvalidOperationException("Native checkin outcome is incomplete.");
                    if (first is not null) savedFirstProcesses.Remove(firstMatches[0].Key);
                    if (folder is not null) savedFolderMoves.Remove(folderMatches[0].Key);
                    if (deletedFolder is not null) savedFolderDeletes.Remove(deleteMatches[0].Key);
                    if (assetProof is not null) savedAssets = null;
                    savedOwners = null;
                    return new CollaborationMutationReceipt(request.OperationId, "succeeded", "CheckedIn", "原生签入及源、提交、锁和远端回读已核实。", status, true, true, true, outcome.Git.Head, true);
                }
                catch (Exception error)
                {
                    requiresReview = true;
                    GitState? observed = null;
                    bool? remoteSynchronized = null;
                    try
                    {
                        var probe = new GitProbe(session.SourcePath);
                        observed = await probe.CaptureAsync();
                        var remote = await probe.RemoteHeadAsync(observed);
                        if (remote is not null) remoteSynchronized = remote == observed.Head;
                    }
                    catch { }
                    return new CollaborationMutationReceipt(request.OperationId, "verification_failed", "NativeCheckinOutcomeUnconfirmed",
                        "原生签入已派发，但结果未完整核实；不要重试。" + SafeMessage(error), NativeAccepted: accepted,
                        CommitId: observed?.Head != baseline.Git.Head ? observed?.Head : null, RemoteSynchronized: remoteSynchronized);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
}
