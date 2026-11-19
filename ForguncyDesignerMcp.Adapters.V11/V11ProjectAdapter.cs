using System.Collections;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

/// <summary>V11 model adapter. Calls run only on the supplied UI scheduler and never edit source files.</summary>
public sealed partial class V11ProjectAdapter : IFixedProjectApiCatalog, IProjectCollaborationAdapter, IProjectNativeServiceApiAdapter
{
    private readonly AdapterHostContext context;
    private readonly NativeV11Session session;
    private readonly NativeV11Settings settings;
    private readonly Lazy<NativeV11Folders> folders;
    private readonly List<NativeV11AuxiliaryStore> auxiliaryStores = [];
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly Dictionary<string, (string Digest, ModuleMutationReceipt Receipt)> receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeV11PendingSave> pending = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NativeV11Collaboration.Plan> pendingOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<ModuleAddress, (NativeV11PendingSave Saved, NativeV11Collaboration.Plan Plan)> savedFirstProcesses = [];
    private readonly Dictionary<ModuleAddress, (NativeV11PendingSave Saved, NativeV11Collaboration.Plan Plan)> savedFolderMoves = [];
    private readonly Dictionary<ModuleAddress, (NativeV11PendingSave Saved, NativeV11Collaboration.Plan Plan)> savedFolderDeletes = [];
    private sealed record CancelledNewJournal(string DeleteOperationId, string? CreateOperationId,
        NativeV11Collaboration.Plan Plan, string[] SourcePaths);
    private readonly Dictionary<ModuleAddress, CancelledNewJournal> cancelledNew = [];
    private readonly Dictionary<ModuleAddress, string> savedCreations = [];
    private readonly Dictionary<string, DateTimeOffset> baselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Digest, ProjectSaveResult Result)> saveReceipts = new(StringComparer.Ordinal);
    private bool requiresReview;
    private string? pendingNativeRevision;
    public AdapterIdentity Identity => session.Profile.Identity;

    public V11ProjectAdapter(AdapterHostContext context)
    {
        this.context = context;
        session = new(context);
        settings = new(session);
        var resources = new Lazy<NativeV11Resources>(() => new(session, address => ReadDocument(address)));
        var localLibraries = new Lazy<NativeV11LocalLibraries>(() => new(session, settings));
        bool LocalAddress(ModuleAddress address) => NativeV11LocalLibraries.SupportsAddress(address);
        bool LocalOwner(ModuleAddress address) => LocalAddress(address) && localLibraries.Value.OwnsAddress(address);
        auxiliaryStores.Add(new([ModuleKind.Image, ModuleKind.ResourceFile],
            kind => resources.Value.List(kind), address => LocalOwner(address) ? localLibraries.Value.Read(address) : resources.Value.Read(address),
            (kind, operation) => NativeV11Resources.SupportsWrite(kind, operation) ||
                kind == ModuleKind.ResourceFile && operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete,
            request => { if (LocalAddress(request.Address)) localLibraries.Value.Validate(request); else resources.Value.Validate(request); },
            request => LocalAddress(request.Address) ? localLibraries.Value.Apply(request) : resources.Value.Apply(request),
            address => LocalOwner(address) ? localLibraries.Value.FindReferences(address) : resources.Value.FindReferences(address),
            address => LocalOwner(address) ? localLibraries.Value.SourcePaths(address) : resources.Value.SourcePaths(address),
            request => LocalAddress(request.Address) ? localLibraries.Value.SourcePaths(request) : resources.Value.SourcePaths(request.Address), request => request.Address,
            request => LocalAddress(request.Address) ? localLibraries.Value.CaptureSaveFiles(request) :
                request.Address.Kind == ModuleKind.ResourceFile ? resources.Value.CaptureFileMutationSaveFiles(request) : resources.Value.CaptureSaveFiles(request.Address),
            path => path == NativeV11LocalLibraries.DefinitionPath,
            request =>
            {
                if (LocalAddress(request.Address))
                {
                    if (!localLibraries.Value.VerifyDeleted(request))
                        throw new BridgeException("MemoryRevisionChanged", "已删除的本地 JS/CSS 文件或原生登记重新出现。");
                }
                else if (resources.Value.List(request.Address.Kind).Any(item => item.Address == request.Address))
                    throw new BridgeException("MemoryRevisionChanged", "已删除的原生资源重新出现在内存中。");
            },
            address => LocalAddress(address) ? LocalOwner(address) : resources.Value.List(address.Kind).Any(item => item.Address == address)));
        folders = new Lazy<NativeV11Folders>(() => new(session));
        auxiliaryStores.Add(new([ModuleKind.Folder],
            kind => folders.Value.List(kind), address => folders.Value.Read(address),
            (kind, operation) => kind == ModuleKind.Folder && operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete,
            request => folders.Value.Validate(request), request => folders.Value.Apply(request),
            address => folders.Value.FindReferences(address), address => folders.Value.SourcePaths(address),
            request => folders.Value.SourcePaths(request), request => folders.Value.ResultAddress(request),
            request => folders.Value.CaptureSaveFiles(request), _ => false));
        var plugins = new Lazy<NativeV11Plugins>(() => new(session));
        auxiliaryStores.Add(new([ModuleKind.Plugin],
            kind => plugins.Value.List(kind), address => plugins.Value.Read(address),
            (_, _) => false, request => plugins.Value.Validate(request), request => plugins.Value.Apply(request),
            address => plugins.Value.FindReferences(address), address => plugins.Value.SourcePaths(address),
            request => plugins.Value.SourcePaths(request.Address), request => request.Address,
            _ => throw new BridgeException("PluginLifecycleNotImplemented", "只读插件依赖适配器不生成保存内容。"), _ => true));
    }

    public Task<ProjectSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => CaptureSnapshotAsync(true, cancellationToken);
    public Task<ProjectSnapshot> InspectSnapshotAsync(CancellationToken cancellationToken = default) => CaptureSnapshotAsync(false, cancellationToken);

    private Task<ProjectSnapshot> CaptureSnapshotAsync(bool issueBaseline, CancellationToken cancellationToken) => OnUi(() =>
    {
        session.VerifyContext();
        var snapshot = Snapshot();
        if (issueBaseline) baselines[snapshot.Revision] = DateTimeOffset.UtcNow;
        return snapshot;
    }, cancellationToken);

    public Task<IReadOnlyList<ModuleCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => OnUi<IReadOnlyList<ModuleCapability>>(() =>
    {
        session.VerifyContext();
        return Enum.GetValues<ModuleKind>().SelectMany(kind => Enum.GetValues<ModuleOperation>().Select(operation =>
        {
            var readable = IsSetting(kind) || Auxiliary(kind) is not null || session.Routes.Any(route => route.Kind == (kind == ModuleKind.View ? ModuleKind.Table : kind));
            var available = operation is ModuleOperation.List or ModuleOperation.Read ? readable : Writable(kind, operation);
            var versionAbsent = kind == ModuleKind.PermissionGroup && session.Profile.Version == "11.0.3.0";
            return new ModuleCapability(kind, operation, available, versionAbsent ? "VersionUnavailable" : CapabilityValidation(kind, operation, available),
                available ? AvailableReason(kind, operation) : UnavailableReason(kind, operation),
                kind is ModuleKind.User or ModuleKind.Role or ModuleKind.Organization || kind == ModuleKind.PermissionGroup && !IsSetting(kind)
                    ? EffectScope.AccountService : kind == ModuleKind.Plugin && operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete
                        ? EffectScope.RuntimeDeployment : EffectScope.ProjectDefinition);
        })).ToArray();
    }, cancellationToken);

    public Task<IReadOnlyList<PlatformApiDescriptor>> GetApiCatalogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlatformApiDescriptor>>(Catalog);

    public Task<ModulePage> ListAsync(ProjectTarget target, ModuleQuery query, CancellationToken cancellationToken = default) => ReadOnUi(target,
        query.Kind is ModuleKind.Table or ModuleKind.View or ModuleKind.Folder or ModuleKind.Plugin, () =>
    {
        CheckTarget(target);
        if (query.Limit is < 1 or > 500) throw new BridgeException("InvalidLimit", "列表每次返回 1 至 500 项。");
        var revision = Snapshot().Revision;
        var offset = 0;
        if (query.Cursor is not null)
        {
            var cursor = JsonSerializer.Deserialize<ListCursor>(Convert.FromBase64String(query.Cursor), PlatformJson.Options)
                ?? throw new BridgeException("InvalidCursor", "游标无效。");
            if (cursor.Revision != revision || cursor.Kind != query.Kind || cursor.Folder != query.FolderPath || cursor.Offset < 0)
                throw new BridgeException("StaleCursor", "工程或列表参数已变化，请重新读取第一页。");
            offset = cursor.Offset;
        }
        var items = (IsSetting(query.Kind) ? settings.List(query.Kind).Select(item => ReadDocument(item.Address)) : Auxiliary(query.Kind) is { } store
                ? store.List(query.Kind).Select(item => ReadDocument(item.Address)) : session.Items(query.Kind).Select(item => ReadDocument(item.Address)))
            .Where(item => query.FolderPath is null || item.Address.FolderPath == query.FolderPath)
            .OrderBy(item => item.Address.FolderPath + "/" + item.Address.Name, StringComparer.Ordinal).ToArray();
        // Revision is derived from the actual native document, including companions, instead of just the name.
        var page = items.Skip(offset).Take(query.Limit).Select(item =>
        {
            return new ModuleSummary(item.Address, item.Revision);
        }).ToArray();
        var nextOffset = offset + page.Length;
        var next = nextOffset < items.Length ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ListCursor(revision, query.Kind, query.FolderPath, nextOffset), PlatformJson.Options)) : null;
        if (Snapshot().Revision != revision) throw new BridgeException("ProjectChangedDuringList", "列表读取期间工程发生变化，请重新读取。");
        return new ModulePage(page, next);
    }, cancellationToken);

    public Task<ModuleDocument> ReadAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default) =>
        ReadModuleAsync(target, address, true, cancellationToken);
    public Task<ModuleDocument> InspectModuleAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default) =>
        ReadModuleAsync(target, address, false, cancellationToken);

    private Task<ModuleDocument> ReadModuleAsync(ProjectTarget target, ModuleAddress address, bool issueBaseline, CancellationToken cancellationToken) => ReadOnUi(target,
        address.Kind is ModuleKind.Table or ModuleKind.View or ModuleKind.Plugin || address.Kind == ModuleKind.Folder &&
            (address.FolderPath == "Tables" || address.FolderPath?.StartsWith("Tables/", StringComparison.Ordinal) is true), () =>
    {
        CheckTarget(target); V11Safety.Address(address);
        return ReadDocument(address, issueBaseline: issueBaseline);
    }, cancellationToken);

    public Task<ReferenceResult> FindReferencesAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default) => ReadOnUi(target, true, () =>
    {
        CheckTarget(target); V11Safety.Address(address);
        return References(address);
    }, cancellationToken);

    public async Task<ModuleValidationResult> ValidateAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default)
    {
        try { return await ReadOnUi(request.Target, NeedsDefinitionLoading(request),
            () => Validate(request, SupportsAutomaticCheckout(request)), cancellationToken, CheckoutCandidate(request)).ConfigureAwait(false); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new(false, [new(error is BridgeException bridge ? bridge.Code : error.GetType().Name,
                SafeMessage(error), "phase=native-definition-load")]);
        }
    }

    private ModuleValidationResult Validate(ModuleMutationRequest request, bool planCheckout = false, bool ownedPlan = false)
    {
        var phase = "project-baseline";
        try
        {
            CheckWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser);
            phase = "request-definition";
            V11Safety.Address(request.Address);
            if (string.IsNullOrWhiteSpace(request.OperationId) || request.OperationId.Length > 128)
                throw new BridgeException("OperationIdRequired", "操作需要唯一编号（不超过 128 字符）。");
            if (request.Scope != EffectScope.ProjectDefinition)
                throw new BridgeException("EffectScopeUnavailable", "V11 定义维护不执行数据库、发布或账号服务操作。");
            if (request.ExpectedServiceBinding is not null)
                throw new BridgeException("ServiceBindingUnavailable", "此 V11 账号服务绑定尚未核实，不能接受带账号服务断言的定义操作。");
            if (!Writable(request.Address.Kind, request.Operation))
                throw new BridgeException("ModuleOperationUnavailable", "当前版本尚未实现此模块操作。");
            if (request.Operation is ModuleOperation.Create or ModuleOperation.Update)
            {
                if (request.Document is null || request.Document.Address != request.Address)
                    throw new BridgeException("DocumentAddressMismatch", "请求地址与文档地址不匹配。");
                if (request.Document.Metadata.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(request.Document.Metadata.GetRawText()) > 32 * 1024 * 1024)
                    throw new BridgeException("InvalidMetadata", "模块元数据必须是大小受限的原生 JSON 对象。");
                NativeV11DefinitionWriter.CheckTypes(request.Document.Metadata, context.DesignerAssembly.GetName().Version!);
            }
            if (request.Operation == ModuleOperation.Create)
            {
                if (request.ExpectedModuleRevision is not null || (IsSetting(request.Address.Kind) ? settings.List(request.Address.Kind).Any(item => item.Address == request.Address) : Auxiliary(request.Address.Kind) is { } existingStore
                    ? existingStore.ContainsAddress?.Invoke(request.Address) ?? existingStore.List(request.Address.Kind).Any(item => item.Address == request.Address) : session.Items(request.Address.Kind).Any(item => item.Address == request.Address)))
                    throw new BridgeException("ModuleAlreadyExists", "新增模块已存在或携带了已有模块修订。");
                if (!IsSetting(request.Address.Kind) && Auxiliary(request.Address.Kind) is null)
                    NativeV11CreationGuard.Verify(session, request.Address);
            }
            else
            {
                phase = "module-baseline";
                if (request.ExpectedModuleRevision is null || !baselines.TryGetValue(request.ExpectedModuleRevision, out var moduleIssued) ||
                    DateTimeOffset.UtcNow - moduleIssued > TimeSpan.FromMinutes(30))
                    throw new BridgeException("ModuleBaselineExpired", "模块基准尚未读取或超过 30 分钟，请重新读取。");
                var current = ReadDocument(request.Address, issueBaseline: false);
                if (string.IsNullOrEmpty(request.ExpectedModuleRevision) || current.Revision != request.ExpectedModuleRevision)
                    throw new BridgeException("ModuleRevisionChanged", "模块内存或配套文件已变化，请重新读取。");
            }
            phase = "ownership";
            if (planCheckout && context.CaptureSnapshot().InCollaborationMode)
            {
                if (request.Address.Kind == ModuleKind.BusinessProcess && request.Operation == ModuleOperation.Create && NativeV11ProcessInitialization.IsFirst(session))
                    _ = BuildFirstProcessPlan(request, NativeV11ProcessInitialization.CaptureCandidate(session, request));
                else if (IsImageFolderUpdate(request))
                    _ = BuildImageFolderPlan(request, PrepareImageFolderUpdate(request));
                else if (IsImageFolderGroupedDelete(request))
                    _ = BuildImageFolderDeletePlan(request, PrepareImageFolderDelete(request));
                else if (IsTableFolderGroupDelete(request))
                    _ = BuildTableFolderDeletePlan(request, NativeV11TableFolderDelete.Capture(session, request));
                else if (folders.Value.IsGroupedNativeRename(request))
                    _ = BuildGroupedFolderPlan(request, folders.Value.CaptureGroupedRename(request));
                else if (folders.Value.IsGroupedNativeDelete(request))
                    _ = BuildGroupedFolderDeletePlan(request, folders.Value.CaptureGroupedDelete(request));
                else
                {
                    var requested = NativeV11CollaborationOwners.ResolveMutation(session, request);
                    var future = requested.Any(owner => owner.State == "New") ? NativeV11CollaborationOwners.GetMutationFiles(session, request, requested.Where(owner => owner.State == "New").ToArray()) :
                        request.Address.Kind == ModuleKind.ResourceFile ? Auxiliary(request.Address.Kind)!.MutationSourcePaths(request) : null;
                    var owners = NativeV11CollaborationOwners.ExpandExistingOwners(session, requested, future,
                        allowSharedTableDatabase: request.Address.Kind is ModuleKind.Table or ModuleKind.View);
                    if (NativeV11TableRelationLifecycle.Supports(request))
                    {
                        var relation = NativeV11TableRelationLifecycle.Prepare(session, request);
                        if (!owners.Any(owner => owner.Address == relation.Primary.Address && owner.State == "Edit"))
                            throw new BridgeException("NativeCollaborationOwnerIncomplete", "关系的主表未包含在原生共享 owner 计划中。 ");
                    }
                    if (NativeV11StatisticColumnLifecycle.Supports(request))
                    {
                        var statistic = NativeV11StatisticColumnLifecycle.Prepare(session, request);
                        if (!owners.Any(owner => owner.Address == statistic.Detail.Address && owner.State == "Edit"))
                            throw new BridgeException("NativeCollaborationOwnerIncomplete", "统计字段的明细表未包含在原生共享 owner 计划中。 ");
                    }
                    _ = NativeV11Collaboration.CreatePlan(session, owners, request.ExpectedUser ?? throw new BridgeException("CurrentUserMismatch", "协同写入需要当前用户身份。"), mutation: future is not null ? request : null);
                }
            }
            else if (!ownedPlan) EnsureOwnership(request.Address, request.Operation);
            ReferenceResult? references = null;
            if (request.Operation == ModuleOperation.Delete)
            {
                phase = "incoming-references";
                references = IsTableFolderGroupDelete(request)
                    ? new ReferenceResult([], true, "Native descendant references were checked against the reviewed deletion group.") : References(request.Address);
                if (!references.Complete || references.References.Count > 0)
                    return new(false, [new("ModuleReferenced", references.Reason ?? "模块仍有引用，不能删除。")], references);
            }
            phase = "native-validation:" + request.Address.Kind;
            if (IsSetting(request.Address.Kind)) settings.Validate(request);
            else if (IsImageFolderUpdate(request)) _ = PrepareImageFolderUpdate(request);
            else if (IsImageFolderGroupedDelete(request)) _ = PrepareImageFolderDelete(request);
            else if (IsTableFolderGroupDelete(request)) _ = NativeV11TableFolderDelete.Capture(session, request);
            else if (Auxiliary(request.Address.Kind) is { } auxiliary) auxiliary.Validate(request);
            else if (NativeV11PageWriter.Supports(request.Address.Kind, request.Operation)) NativeV11PageWriter.Validate(session, request);
            else if (NativeV11Reports.Supports(request.Address.Kind, request.Operation)) NativeV11Reports.Validate(session, request);
            else if (NativeV11Processes.Supports(request.Address.Kind, request.Operation))
            {
                if (request.Operation == ModuleOperation.Create && NativeV11ProcessInitialization.IsFirst(session))
                    _ = NativeV11ProcessInitialization.CaptureCandidate(session, request);
                else NativeV11Processes.Validate(session, request);
            }
            else if (NativeV11BuiltInTableLifecycle.IsBuiltInCreate(request))
            {
                NativeV11BuiltInTableLifecycle.ValidateRequestShape(request);
                _ = NativeV11BuiltInTableLifecycle.PrepareCreate(session, request.Address,
                    request.Address.Kind == ModuleKind.View ? NativeV11BuiltInTableLifecycle.ViewSql(request) : null);
            }
            else if (NativeV11BuiltInTableLifecycle.IsBuiltInDelete(session, request))
                _ = NativeV11BuiltInTableLifecycle.PrepareDelete(session, request);
            else if (NativeV11BuiltInTableLifecycle.IsFormulaOperation(request))
                _ = NativeV11BuiltInTableLifecycle.PrepareFormula(session, request);
            else if (NativeV11BuiltInTableLifecycle.IsPhysicalColumnMutation(request))
                _ = NativeV11BuiltInTableLifecycle.PreparePhysicalColumnMutation(session, request);
            else if (NativeV11TableRelationLifecycle.Supports(request))
                _ = NativeV11TableRelationLifecycle.Prepare(session, request);
            else if (NativeV11StatisticColumnLifecycle.Supports(request))
                _ = NativeV11StatisticColumnLifecycle.Prepare(session, request);
            else if (NativeV11BuiltInTableLifecycle.IsColumnAdd(request))
                _ = NativeV11BuiltInTableLifecycle.PrepareColumnAdd(session, request);
            else if (NativeV11BuiltInTableLifecycle.IsViewEdit(request))
                _ = NativeV11BuiltInTableLifecycle.PrepareViewEdit(session, request);
            else if (NativeV11TableLifecycle.Supports(request.Address.Kind, request.Operation)) NativeV11TableLifecycle.Validate(session, request);
            else if (NativeV11TableWriter.Supports(request.Address.Kind, request.Operation)) NativeV11TableWriter.Validate(session, request);
            else NativeV11DefinitionWriter.Validate(session, request);
            return new(true, [], references);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, [new(exception is BridgeException bridge ? bridge.Code : exception.GetType().Name,
                SafeMessage(exception), "phase=" + phase)]);
        }
    }

    public async Task<ModuleMutationReceipt> ApplyAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                var digest = Wire.Hash(JsonSerializer.Serialize(request, PlatformJson.Options));
                if (string.IsNullOrWhiteSpace(request.OperationId))
                    return new ModuleMutationReceipt("", "Rejected", "OperationIdRequired", "写操作需要唯一编号。");
                if (receipts.TryGetValue(request.OperationId, out var previous))
                {
                    if (previous.Digest != digest) throw new BridgeException("OperationIdConflict", "操作编号已用于其他请求。");
                    return previous.Receipt;
                }
                CheckTarget(request.Target);
                if (NeedsDefinitionLoading(request) || SupportsAutomaticCheckout(request))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await NativeV11TableDefinitionLoading.EnsureAsync(session);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var automaticCheckout = SupportsAutomaticCheckout(request);
                var validation = Validate(request, automaticCheckout);
                if (!validation.Valid)
                {
                    var issue = validation.Issues[0];
                    return new(request.OperationId, "Rejected", issue.Code, issue.Message);
                }
                cancellationToken.ThrowIfCancellationRequested();
                var firstProcessCandidate = request.Address.Kind == ModuleKind.BusinessProcess && request.Operation == ModuleOperation.Create &&
                    NativeV11ProcessInitialization.IsFirst(session)
                    ? NativeV11ProcessInitialization.CaptureCandidate(session, request) : null;
                var auxiliary = Auxiliary(request.Address.Kind);
                var imageFolderUpdate = IsImageFolderUpdate(request) ? PrepareImageFolderUpdate(request) : null;
                var imageFolderDelete = IsImageFolderGroupedDelete(request) ? PrepareImageFolderDelete(request) : null;
                var tableFolderDelete = IsTableFolderGroupDelete(request) ? NativeV11TableFolderDelete.Capture(session, request) : null;
                var standaloneFolderRename = !automaticCheckout && folders.Value.IsGroupedNativeRename(request)
                    ? folders.Value.CaptureGroupedRename(request) : null;
                var builtInTableCreate = NativeV11BuiltInTableLifecycle.IsBuiltInCreate(request);
                var builtInPlan = builtInTableCreate ? NativeV11BuiltInTableLifecycle.PrepareCreate(session, request.Address,
                    request.Address.Kind == ModuleKind.View ? NativeV11BuiltInTableLifecycle.ViewSql(request) : null) : null;
                var builtInTableDelete = NativeV11BuiltInTableLifecycle.IsBuiltInDelete(session, request);
                var builtInDeletePlan = builtInTableDelete ? NativeV11BuiltInTableLifecycle.PrepareDelete(session, request) : null;
                var builtInFormula = NativeV11BuiltInTableLifecycle.IsFormulaOperation(request);
                var builtInFormulaPlan = builtInFormula ? NativeV11BuiltInTableLifecycle.PrepareFormula(session, request) : null;
                var builtInPhysicalColumn = NativeV11BuiltInTableLifecycle.IsPhysicalColumnMutation(request);
                var builtInPhysicalPlan = builtInPhysicalColumn ? NativeV11BuiltInTableLifecycle.PreparePhysicalColumnMutation(session, request) : null;
                var tableRelation = NativeV11TableRelationLifecycle.Supports(request);
                var tableRelationPlan = tableRelation ? NativeV11TableRelationLifecycle.Prepare(session, request) : null;
                var statisticColumn = NativeV11StatisticColumnLifecycle.Supports(request);
                var statisticPlan = statisticColumn ? NativeV11StatisticColumnLifecycle.Prepare(session, request) : null;
                var builtInColumnAdd = !builtInFormula && !builtInPhysicalColumn && !tableRelation && !statisticColumn && NativeV11BuiltInTableLifecycle.IsColumnAdd(request);
                var builtInColumnPlan = builtInColumnAdd ? NativeV11BuiltInTableLifecycle.PrepareColumnAdd(session, request) : null;
                var builtInViewEdit = NativeV11BuiltInTableLifecycle.IsViewEdit(request);
                var builtInViewPlan = builtInViewEdit ? NativeV11BuiltInTableLifecycle.PrepareViewEdit(session, request) : null;
                var affectedPaths = IsSetting(request.Address.Kind) ? settings.SourcePaths(request.Address.Kind) :
                    auxiliary is not null ? auxiliary.MutationSourcePaths(request) : NativeV11PendingSave.PathsForAddress(session, request.Address);
                if (imageFolderUpdate is not null) affectedPaths = imageFolderUpdate.Journal.Paths;
                if (imageFolderDelete is not null) affectedPaths = imageFolderDelete.Journal.Paths;
                if (tableFolderDelete is not null) affectedPaths = tableFolderDelete.Journal.Paths.Append(tableFolderDelete.Database).ToArray();
                if (standaloneFolderRename is not null) affectedPaths = standaloneFolderRename.Journal.Paths;
                if (builtInPlan is not null)
                    affectedPaths = affectedPaths.Append(builtInPlan.SourceBefore.DatabasePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (builtInDeletePlan is not null)
                    affectedPaths = affectedPaths.Append(builtInDeletePlan.SourceBefore.DatabasePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (builtInColumnPlan is not null)
                    affectedPaths = affectedPaths.Append(builtInColumnPlan.Baseline.SourceBefore.DatabasePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (builtInFormulaPlan is not null)
                    affectedPaths = affectedPaths.Append(builtInFormulaPlan.Baseline.SourceBefore.DatabasePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (builtInPhysicalPlan is not null)
                    affectedPaths = affectedPaths.Append(builtInPhysicalPlan.Baseline.SourceBefore.DatabasePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (tableRelationPlan is not null)
                    affectedPaths = affectedPaths.Append(tableRelationPlan.Baseline.SourceBefore.DatabasePath).Append(tableRelationPlan.Primary.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (tableRelationPlan?.PreviousPrimary is { } previousRelationPrimary)
                    affectedPaths = affectedPaths.Append(previousRelationPrimary.Item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (statisticPlan is not null)
                    affectedPaths = affectedPaths.Append(statisticPlan.Baseline.SourceBefore.DatabasePath).Append(statisticPlan.Detail.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (statisticPlan?.PreviousDetail is { } previousStatisticDetail)
                    affectedPaths = affectedPaths.Append(previousStatisticDetail.Item.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (builtInViewPlan is not null)
                    affectedPaths = affectedPaths.Append(builtInViewPlan.Baseline.SourceBefore.DatabasePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (firstProcessCandidate is not null)
                    affectedPaths = affectedPaths.Concat(firstProcessCandidate.ExistingSettings.Keys).Concat(firstProcessCandidate.TemplatePaths)
                        .Concat(firstProcessCandidate.NewDirectGroups.Values.SelectMany(paths => paths))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // Linked layouts/binary owners can stamp LocalPath during the
                // mutation. Waiting only before SaveAsync cannot repair a stamp
                // already preserved by the native copy into LastPath.
                await NativeV11ArchiveSaveBoundary.WaitAsync(session.SourcePath, affectedPaths);
                validation = Validate(request, automaticCheckout);
                if (!validation.Valid)
                {
                    var issue = validation.Issues[0];
                    return new(request.OperationId, "Rejected", issue.Code, issue.Message);
                }
                cancellationToken.ThrowIfCancellationRequested();
                NativeV11Collaboration.Plan? ownerPlan = null;
                NativeV11Folders.GroupedRename? groupedFolderRename = standaloneFolderRename;
                NativeV11Folders.GroupedDelete? groupedFolderDelete = null;
                if (automaticCheckout)
                {
                    IReadOnlyList<NativeV11CollaborationOwner> owners;
                    if (firstProcessCandidate is not null)
                    {
                        ownerPlan = BuildFirstProcessPlan(request, firstProcessCandidate);
                        owners = ownerPlan.Owners;
                    }
                    else if (imageFolderUpdate is not null)
                    {
                        imageFolderUpdate = PrepareImageFolderUpdate(request);
                        ownerPlan = BuildImageFolderPlan(request, imageFolderUpdate);
                        owners = ownerPlan.Owners;
                    }
                    else if (imageFolderDelete is not null)
                    {
                        imageFolderDelete = PrepareImageFolderDelete(request);
                        ownerPlan = BuildImageFolderDeletePlan(request, imageFolderDelete);
                        owners = ownerPlan.Owners;
                    }
                    else if (tableFolderDelete is not null)
                    {
                        tableFolderDelete = NativeV11TableFolderDelete.Capture(session, request);
                        ownerPlan = BuildTableFolderDeletePlan(request, tableFolderDelete);
                        owners = ownerPlan.Owners;
                    }
                    else if (folders.Value.IsGroupedNativeRename(request))
                    {
                        groupedFolderRename = folders.Value.CaptureGroupedRename(request);
                        ownerPlan = BuildGroupedFolderPlan(request, groupedFolderRename);
                        owners = ownerPlan.Owners;
                    }
                    else if (folders.Value.IsGroupedNativeDelete(request))
                    {
                        groupedFolderDelete = folders.Value.CaptureGroupedDelete(request);
                        ownerPlan = BuildGroupedFolderDeletePlan(request, groupedFolderDelete);
                        owners = ownerPlan.Owners;
                    }
                    else
                    {
                        var requested = NativeV11CollaborationOwners.ResolveMutation(session, request);
                        var future = requested.Any(owner => owner.State == "New") ? NativeV11CollaborationOwners.GetMutationFiles(session, request, requested.Where(owner => owner.State == "New").ToArray()) :
                            request.Address.Kind == ModuleKind.ResourceFile ? Auxiliary(request.Address.Kind)!.MutationSourcePaths(request) : null;
                        owners = NativeV11CollaborationOwners.ExpandExistingOwners(session, requested, future,
                            allowSharedTableDatabase: request.Address.Kind is ModuleKind.Table or ModuleKind.View);
                        if (tableRelationPlan is not null && !owners.Any(owner => owner.Address == tableRelationPlan.Primary.Address && owner.State == "Edit"))
                            throw new BridgeException("NativeCollaborationOwnerIncomplete", "关系的主表未包含在原生共享 owner 计划中。 ");
                        if (tableRelationPlan?.PreviousPrimary is { } previousRelation && !owners.Any(owner => owner.Address == previousRelation.Item.Address && owner.State == "Edit"))
                            throw new BridgeException("NativeCollaborationOwnerIncomplete", "关系的原主表未包含在原生共享 owner 计划中。 ");
                        if (statisticPlan is not null && !owners.Any(owner => owner.Address == statisticPlan.Detail.Address && owner.State == "Edit"))
                            throw new BridgeException("NativeCollaborationOwnerIncomplete", "统计字段的明细表未包含在原生共享 owner 计划中。 ");
                        if (statisticPlan?.PreviousDetail is { } previousOwner && !owners.Any(owner => owner.Address == previousOwner.Item.Address && owner.State == "Edit"))
                            throw new BridgeException("NativeCollaborationOwnerIncomplete", "统计字段的原明细表未包含在原生共享 owner 计划中。 ");
                        ownerPlan = NativeV11Collaboration.CreatePlan(session, owners, request.ExpectedUser!, mutation: future is not null ? request : null);
                    }
                    var before = Snapshot();
                    var probe = new GitProbe(session.SourcePath);
                    var git = await probe.CaptureAsync();
                    var remote = await probe.RemoteHeadAsync(git);
                    if (Snapshot() != before || string.IsNullOrEmpty(remote) || string.IsNullOrEmpty(git.RemoteUrl))
                        throw new BridgeException("CollaborationBaselineChanged", "签出前工程或远端基准未确认。");
                    var checkout = new CollaborationMutationRequest(request.OperationId, request.Target, request.ExpectedProjectRevision,
                        request.ExpectedUser!, owners.Select(owner => owner.Address).ToArray(), ExpectedRemoteUrl: git.RemoteUrl,
                        ExpectedBranch: git.Branch, ExpectedRemoteHead: remote);
                    void VerifyCheckoutContext()
                    {
                        CheckTarget(request.Target);
                        var current = context.CaptureSnapshot();
                        if (!current.InCollaborationMode || current.CurrentUser != request.ExpectedUser || current.Busy || current.HasUnsavedChanges)
                            throw new BridgeException("CollaborationBaselineChanged", "签出期间工程或用户状态已变化。");
                    }
                    var reuseSavedAsset = request.Address.Kind is ModuleKind.ResourceFile or ModuleKind.CustomLibrary && git.Head == remote &&
                        await ReuseAssetOwnersAsync(ownerPlan, git);
                    var reuseSavedOwner = SupportsSavedOwnerReuse(request.Address.Kind) && git.Head == remote &&
                        ReuseSavedOwners(ownerPlan, git);
                    if (!reuseSavedAsset && savedAssets is null && request.Address.Kind == ModuleKind.ResourceFile &&
                        request.Operation == ModuleOperation.Create && git.Head == remote)
                    {
                        var assetPaths = affectedPaths.Where(NativeV11AssetRemovalRegistration.IsAssetPath).Distinct(StringComparer.Ordinal).ToArray();
                        if (assetPaths.Length == 1)
                            reuseSavedAsset = await NativeV11AssetRegistryRecovery.VerifyAsync(ownerPlan, git, assetPaths[0]);
                    }
                    if (reuseSavedAsset || reuseSavedOwner) VerifyCheckoutContext();
                    var outcome = reuseSavedAsset || reuseSavedOwner
                        ? new NativeV11Collaboration.CheckoutResult(null, true, false, null, "AlreadyOwned")
                        : await NativeV11Collaboration.CheckoutExistingPlanAsync(ownerPlan, checkout, VerifyCheckoutContext, cancellationToken);
                    if (!outcome.LockStateVerified || outcome.ExecutionMayContinue)
                    {
                        if (outcome.ExecutionMayContinue) requiresReview = true;
                        var rejected = new ModuleMutationReceipt(request.OperationId, outcome.ExecutionMayContinue ? "verification_failed" : "Rejected", outcome.Code,
                            "原生签出未确认，尚未修改模块。", ExecutionMayContinue: outcome.ExecutionMayContinue);
                        receipts.Add(request.OperationId, (digest, rejected));
                        return rejected;
                    }
                    try
                    {
                        NativeV11Collaboration.VerifyOwned(ownerPlan, true);
                        if (groupedFolderRename is not null || groupedFolderDelete is not null || imageFolderUpdate is not null || imageFolderDelete is not null || tableFolderDelete is not null) NativeV11Collaboration.VerifyGroupedMoveRemovalFiles(ownerPlan);
                        var updated = Snapshot();
                        baselines[updated.Revision] = DateTimeOffset.UtcNow;
                        request = request with { ExpectedProjectRevision = updated.Revision };
                        if (tableFolderDelete is not null)
                        {
                            var postCheckout = NativeV11TableFolderDelete.Capture(session, request);
                            RequireSameTableFolderDelete(tableFolderDelete, postCheckout); tableFolderDelete = postCheckout;
                        }
                        if (groupedFolderRename is not null) folders.Value.ApproveGroupedRename(request, groupedFolderRename);
                        if (groupedFolderDelete is not null) folders.Value.ApproveGroupedDelete(request, groupedFolderDelete);
                        validation = Validate(request, ownedPlan: true);
                        if (validation.Valid && firstProcessCandidate is not null)
                        {
                            // Native checkout may update .collaboration. Its executor has
                            // already proved the business source unchanged; retain all
                            // candidate identities/groups and freeze the new full revision.
                            var postCheckout = NativeV11ProcessInitialization.CaptureCandidate(session, request);
                            NativeV11ProcessInitialization.RequireSameCandidate(session, request,
                                firstProcessCandidate with { SourceRevision = postCheckout.SourceRevision });
                            firstProcessCandidate = postCheckout;
                        }
                    }
                    catch (Exception error)
                    {
                        if (groupedFolderRename is not null) folders.Value.RevokeGroupedRename(request.OperationId);
                        if (groupedFolderDelete is not null) folders.Value.RevokeGroupedDelete(request.OperationId);
                        requiresReview = true;
                        var uncertain = new ModuleMutationReceipt(request.OperationId, "verification_failed", "CheckoutPostReadUnconfirmed",
                            "原生签出后的状态未能完整确认，模块修改未执行：" + SafeMessage(error));
                        receipts.Add(request.OperationId, (digest, uncertain));
                        return uncertain;
                    }
                    if (!validation.Valid)
                    {
                        if (groupedFolderRename is not null) folders.Value.RevokeGroupedRename(request.OperationId);
                        if (groupedFolderDelete is not null) folders.Value.RevokeGroupedDelete(request.OperationId);
                        var issue = validation.Issues[0];
                        var rejected = new ModuleMutationReceipt(request.OperationId, "Rejected", issue.Code, "签出已核实，但模块修改未执行：" + issue.Message);
                        receipts.Add(request.OperationId, (digest, rejected));
                        return rejected;
                    }
                    if (groupedFolderRename is not null) folders.Value.RevokeGroupedRename(request.OperationId);
                    if (groupedFolderDelete is not null) folders.Value.RevokeGroupedDelete(request.OperationId);
                }
                var assetPending = await CaptureAssetBeforeAsync(request, ownerPlan, affectedPaths);
                var sourceBefore = IsSetting(request.Address.Kind)
                    ? NativeV11PendingSave.ReadFiles(session.SourcePath, settings.SourcePaths(request.Address.Kind))
                    : auxiliary is not null ? NativeV11PendingSave.ReadFiles(session.SourcePath, auxiliary.MutationSourcePaths(request))
                        : NativeV11PendingSave.CaptureSource(session, request.Address);
                if (imageFolderUpdate is not null) sourceBefore = NativeV11PendingSave.ReadFiles(session.SourcePath, imageFolderUpdate.Journal.Paths);
                if (imageFolderDelete is not null) sourceBefore = NativeV11PendingSave.ReadFiles(session.SourcePath, imageFolderDelete.Journal.Paths);
                if (tableFolderDelete is not null) sourceBefore = NativeV11PendingSave.ReadFiles(session.SourcePath, tableFolderDelete.Journal.Paths);
                if (groupedFolderRename is not null)
                    sourceBefore = NativeV11PendingSave.ReadFiles(session.SourcePath,
                        sourceBefore.Keys.Concat(groupedFolderRename.Journal.Paths).Distinct(StringComparer.OrdinalIgnoreCase));
                if (groupedFolderDelete is not null)
                    sourceBefore = NativeV11PendingSave.ReadFiles(session.SourcePath,
                        sourceBefore.Keys.Concat(groupedFolderDelete.Journal.Paths).Distinct(StringComparer.OrdinalIgnoreCase));
                if (ownerPlan?.CancellationDirectFiles.Count > 0)
                    sourceBefore = NativeV11PendingSave.ReadFiles(session.SourcePath,
                        sourceBefore.Keys.Concat(ownerPlan.Files.Values.SelectMany(paths => paths)).Distinct(StringComparer.OrdinalIgnoreCase));
                var unchangedProcessDatabase = request.Address.Kind == ModuleKind.BusinessProcess && firstProcessCandidate is null && ownerPlan is not null &&
                    ownerPlan.Files.Values.SelectMany(paths => paths).Contains(NativeV11ProcessInitialization.Database, StringComparer.OrdinalIgnoreCase)
                    ? NativeV11UnchangedDatabaseProof.Capture(session, NativeV11ProcessInitialization.Database) :
                    groupedFolderRename is not null && request.Address.FolderPath?.Split('/')[0] == "Tables"
                    ? NativeV11UnchangedDatabaseProof.Capture(session, NativeV11CollaborationOwners.TableDatabasePath()) : null;
                if (firstProcessCandidate is not null)
                    sourceBefore = NativeV11PendingSave.ReadFiles(session.SourcePath, sourceBefore.Keys.Concat(firstProcessCandidate.TemplatePaths)
                        .Concat(firstProcessCandidate.NewDirectGroups.Values.SelectMany(paths => paths))
                        .Where(path => !path.Equals(NativeV11ProcessInitialization.Database, StringComparison.OrdinalIgnoreCase) &&
                            !path.Equals(NativeV11CollaborationOwners.TableDatabasePath(), StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
                var projectSourceRevision = NativeV11SourceRevision.Capture(session.SourcePath);
                var deletedModel = request.Operation == ModuleOperation.Delete && !IsSetting(request.Address.Kind) && auxiliary is null ? session.Find(request.Address).Model : null;
                NativeV11TableSharedSaveJournal? sharedTableJournal = null;
                Action? undo = null;
                NativeV11BuiltInTableLifecycle.CreatedTable? builtInCreated = null;
                NativeV11BuiltInTableLifecycle.DeletedTable? builtInDeleted = null;
                NativeV11TableFolderDelete.Completed? tableFolderDeleted = null;
                var builtInDispatched = false;
                var firstProcessDispatched = false;
                try
                {
                    if (builtInTableCreate) builtInPlan = NativeV11BuiltInTableLifecycle.PrepareCreate(session, request.Address,
                        request.Address.Kind == ModuleKind.View ? NativeV11BuiltInTableLifecycle.ViewSql(request) : null);
                    if (builtInTableDelete) builtInDeletePlan = NativeV11BuiltInTableLifecycle.PrepareDelete(session, request);
                    if (builtInColumnAdd) builtInColumnPlan = NativeV11BuiltInTableLifecycle.PrepareColumnAdd(session, request);
                    if (builtInFormula) builtInFormulaPlan = NativeV11BuiltInTableLifecycle.PrepareFormula(session, request);
                    if (builtInPhysicalColumn) builtInPhysicalPlan = NativeV11BuiltInTableLifecycle.PreparePhysicalColumnMutation(session, request);
                    if (tableRelation) tableRelationPlan = NativeV11TableRelationLifecycle.Prepare(session, request);
                    if (statisticColumn) statisticPlan = NativeV11StatisticColumnLifecycle.Prepare(session, request);
                    if (builtInViewEdit) builtInViewPlan = NativeV11BuiltInTableLifecycle.PrepareViewEdit(session, request);
                    if (ownerPlan is not null && request.Address.Kind is (ModuleKind.Table or ModuleKind.View))
                        sharedTableJournal = NativeV11TableSharedSaveJournal.Capture(session, ownerPlan, request.Address,
                            builtInPlan is not null || builtInDeletePlan is not null || builtInColumnPlan is not null || builtInViewPlan is not null || builtInPhysicalPlan is not null);
                    if (ownerPlan is not null && groupedFolderRename is not null && request.Address.FolderPath?.Split('/')[0] == "Tables" && ownerPlan.AllowSharedTableDatabase)
                        sharedTableJournal = NativeV11TableSharedSaveJournal.Capture(session, ownerPlan, request.Address, databaseChanges: false);
                    if (ownerPlan is not null && tableFolderDelete is not null)
                        sharedTableJournal = NativeV11TableSharedSaveJournal.Capture(session, ownerPlan, request.Address, databaseChanges: true);
                    if (ownerPlan is not null && firstProcessCandidate is not null)
                        sharedTableJournal = NativeV11TableSharedSaveJournal.Capture(session, ownerPlan, request.Address, databaseChanges: true);
                    string? firstProcessAppDatabase = null;
                    NativeV11SqlitePreservation? firstProcessPreservation = null;
                    using var firstProcessSqliteResolver = firstProcessCandidate is null ? null : NativeV11ProcessInitialization.SqliteResolver(context.DesignerAssembly);
                    if (firstProcessCandidate is not null)
                    {
                        var sourceGroup = NativeV11BuiltInTableLifecycle.CaptureSourceGroup(session, request.Address);
                        NativeV11BuiltInTableLifecycle.VerifyDatabaseBaseline(session, sourceGroup);
                        firstProcessAppDatabase = V11Safety.Under(session.LocalPath, sourceGroup.DatabasePath);
                        firstProcessPreservation = NativeV11SqlitePreservation.Capture(firstProcessAppDatabase);
                    }
                    using var appPreservation = firstProcessPreservation;
                    if (firstProcessCandidate is not null)
                    {
                        firstProcessDispatched = true;
                        undo = await ApplyFirstProcessInitializationAsync(request, firstProcessCandidate,
                            () =>
                            {
                                NativeV11ProcessInitialization.RequireSameCandidate(session, request, firstProcessCandidate);
                                if (ownerPlan is not null) NativeV11Collaboration.VerifyOwned(ownerPlan, true);
                            });
                        NativeV11ProcessInitialization.RequireLiveDatabaseSchema(context.DesignerAssembly,
                            V11Safety.Under(session.LocalPath, NativeV11ProcessInitialization.Database));
                        appPreservation!.Verify(firstProcessAppDatabase!);
                    }
                    else if (builtInPlan is not null)
                    {
                        builtInDispatched = true;
                        builtInCreated = await NativeV11BuiltInTableLifecycle.CreateAsync(builtInPlan);
                    }
                    else if (builtInDeletePlan is not null)
                    {
                        builtInDispatched = true;
                        builtInDeleted = await NativeV11BuiltInTableLifecycle.DeleteAsync(builtInDeletePlan);
                    }
                    else if (builtInColumnPlan is not null)
                    {
                        builtInDispatched = true;
                        builtInCreated = await NativeV11BuiltInTableLifecycle.AddColumnAsync(builtInColumnPlan);
                    }
                    else if (builtInFormulaPlan is not null)
                    {
                        builtInDispatched = true;
                        builtInCreated = await NativeV11BuiltInTableLifecycle.ApplyFormulaAsync(builtInFormulaPlan);
                    }
                    else if (builtInPhysicalPlan is not null)
                    {
                        builtInDispatched = true;
                        builtInCreated = await NativeV11BuiltInTableLifecycle.ApplyPhysicalColumnMutationAsync(builtInPhysicalPlan);
                    }
                    else if (tableRelationPlan is not null)
                    {
                        builtInDispatched = true;
                        builtInCreated = NativeV11TableRelationLifecycle.Apply(tableRelationPlan);
                    }
                    else if (statisticPlan is not null)
                    {
                        builtInDispatched = true;
                        builtInCreated = await NativeV11StatisticColumnLifecycle.ApplyAsync(statisticPlan);
                    }
                    else if (builtInViewPlan is not null)
                    {
                        builtInDispatched = true;
                        builtInCreated = await NativeV11BuiltInTableLifecycle.EditViewAsync(builtInViewPlan);
                    }
                    else if (imageFolderUpdate is not null)
                        undo = NativeV11ImageFolderLifecycle.ApplyUpdate(imageFolderUpdate);
                    else if (imageFolderDelete is not null)
                        undo = NativeV11ImageFolderLifecycle.ApplyDelete(imageFolderDelete);
                    else if (tableFolderDelete is not null)
                    {
                        builtInDispatched = true;
                        tableFolderDeleted = await NativeV11TableFolderDelete.ApplyAsync(tableFolderDelete);
                    }
                    else
                    {
                        if (groupedFolderRename is not null) folders.Value.ApproveGroupedRename(request, groupedFolderRename);
                        if (groupedFolderDelete is not null) folders.Value.ApproveGroupedDelete(request, groupedFolderDelete);
                        try { undo = IsSetting(request.Address.Kind) ? settings.Apply(request) : auxiliary is not null ? auxiliary.Apply(request) : NativeV11PageWriter.Supports(request.Address.Kind, request.Operation)
                        ? NativeV11PageWriter.Apply(session, request) : NativeV11Reports.Supports(request.Address.Kind, request.Operation)
                            ? NativeV11Reports.Apply(session, request) : NativeV11Processes.Supports(request.Address.Kind, request.Operation)
                                ? NativeV11Processes.Apply(session, request) : NativeV11TableLifecycle.Supports(request.Address.Kind, request.Operation)
                                    ? NativeV11TableLifecycle.Apply(session, request) : NativeV11TableWriter.Supports(request.Address.Kind, request.Operation)
                            ? NativeV11TableWriter.Apply(session, request) : NativeV11DefinitionWriter.Apply(session, request); }
                        finally
                        {
                            if (groupedFolderRename is not null) folders.Value.RevokeGroupedRename(request.OperationId);
                            if (groupedFolderDelete is not null) folders.Value.RevokeGroupedDelete(request.OperationId);
                        }
                    }
                    session.VerifyContext();
                    var resultAddress = imageFolderUpdate?.Destination ?? auxiliary?.ResultAddress(request) ?? request.Address;
                    if (ownerPlan is null) EnsureOwnership(resultAddress, request.Operation, afterMutation: true);
                    if (ownerPlan is not null) NativeV11Collaboration.VerifyOwned(ownerPlan, true, requireRegisteredModel: true);
                    var document = request.Operation == ModuleOperation.Delete ? null : ReadDocument(resultAddress, true);
                    var tracked = new NativeV11PendingSave(request.OperationId, resultAddress, request.Operation, document, sourceBefore, deletedModel,
                        IsSetting(request.Address.Kind) ? settings : null, imageFolderUpdate is null && imageFolderDelete is null && tableFolderDelete is null ? auxiliary : null,
                        imageFolderUpdate is not null || imageFolderDelete is not null || tableFolderDelete is not null || auxiliary is null ? null : request);
                    tracked.ProjectSourceRevision = projectSourceRevision;
                    tracked.BuiltInTable = builtInCreated;
                    tracked.DeletedBuiltInTable = builtInDeleted;
                    tracked.SharedTableJournal = sharedTableJournal;
                    tracked.UnchangedProcessDatabase = unchangedProcessDatabase;
                    tracked.FolderMoveJournal = imageFolderUpdate?.Journal ?? groupedFolderRename?.Journal;
                    tracked.FolderDeleteJournal = tableFolderDelete?.Journal ?? imageFolderDelete?.Journal ?? groupedFolderDelete?.Journal;
                    tracked.ImageFolderUpdate = imageFolderUpdate;
                    tracked.ImageFolderDelete = imageFolderDelete;
                    tracked.TableFolderDelete = tableFolderDeleted;
                    if (firstProcessCandidate is not null)
                        tracked.FirstProcessJournal = NativeV11FirstProcessSaveJournal.Capture(session, firstProcessCandidate);
                    if (document is not null && (request.Address.Kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask) && NativeV11CommandSaveSnapshot.ContainsCondition(document.Metadata))
                    {
                        var item = session.Find(resultAddress);
                        tracked.CommandSaveSnapshot = NativeV11CommandSaveSnapshot.Capture(context.DesignerAssembly, item.Route, item.Model, session.NativePath(resultAddress));
                    }
                    if (document is not null && tracked.CommandSaveSnapshot is null && builtInCreated is null && imageFolderUpdate is null && !IsSetting(request.Address.Kind) && auxiliary is null)
                        tracked.ExpectedSourceMetadata = NativeV11SourceMetadata.Capture(session, resultAddress);
                    sharedTableJournal?.VerifyMemory(session);
                    tracked.VerifySourceUnchanged(session);
                    if (assetPending is not null)
                        assetPending = assetPending with
                        {
                            Expected = NativeV11PendingSave.ReadFiles(session.LocalPath, [assetPending.Path])[assetPending.Path]
                        };
                    var nativeRevision = context.CaptureSnapshot().Revision;
                    pending.Add(request.OperationId, tracked);
                    if (ownerPlan is not null) pendingOwners.Add(request.OperationId, ownerPlan);
                    if (assetPending is not null)
                        pendingAssets.Add(request.OperationId, assetPending);
                    pendingNativeRevision = nativeRevision;
                    var loaded = new ModuleMutationReceipt(request.OperationId, "LoadedPendingSave", "NativeMemoryChanged",
                        "原生内存及保存缓存已更新；尚未确认源工程写回。" + (builtInCreated?.NativeRowversionReset is true ? "原生字段约束操作会重置目标表的并发令牌。" : ""), document, ChangedFiles: tracked.Paths);
                    receipts.Add(request.OperationId, (digest, loaded));
                }
                catch (Exception exception)
                {
                    if (firstProcessDispatched)
                    {
                        requiresReview = true;
                        var uncertain = new ModuleMutationReceipt(request.OperationId, "verification_failed", "NativeProcessInitializationUncertain",
                            "首次流程原生初始化可能已更改多个所有者；工程已冻结，请勿重试或保存。", ExecutionMayContinue: true);
                        receipts[request.OperationId] = (digest, uncertain);
                        return uncertain;
                    }
                    if (builtInDispatched)
                    {
                        requiresReview = true;
                        var uncertain = new ModuleMutationReceipt(request.OperationId, "verification_failed", "NativeBuiltInTableMutationUncertain",
                            "原生内置表操作可能已修改共享数据库；工程已冻结，请勿重试或保存。" + (builtInPhysicalPlan?.Constraint is not null ? "原生字段约束操作可能已重置目标表的并发令牌。" : ""), ExecutionMayContinue: true);
                        receipts[request.OperationId] = (digest, uncertain);
                        return uncertain;
                    }
                    if (pending.ContainsKey(request.OperationId)) requiresReview = true;
                    if (undo is not null)
                    {
                        try { undo(); }
                        catch (Exception rollback)
                        {
                            requiresReview = true;
                            if (ownerPlan is null) throw new AggregateException("原生操作失败且回退未确认。", exception, rollback);
                            var failed = new ModuleMutationReceipt(request.OperationId, "verification_failed", "MutationRollbackUnconfirmed",
                                "原生签出已执行，但模块修改回退未确认；请勿重试。", ExecutionMayContinue: false);
                            receipts.Add(request.OperationId, (digest, failed));
                            return failed;
                        }
                    }
                    if (exception is AggregateException) requiresReview = true;
                    if (ownerPlan is not null)
                    {
                        var failed = new ModuleMutationReceipt(request.OperationId, requiresReview ? "verification_failed" : "Rejected",
                            "MutationFailedAfterCheckout", "原生签出状态已保留；模块修改未完成：" + SafeMessage(exception));
                        receipts.Add(request.OperationId, (digest, failed));
                        return failed;
                    }
                    throw;
                }
                if (request.Save)
                {
                    try
                    {
                        var saved = await SavePendingAsync();
                        var prior = receipts[request.OperationId].Receipt;
                        var state = saved.NativeSaveAccepted ? saved.SourceVerified && !saved.ContainsUnsavedChanges ? "Saved" : "SaveUnconfirmed" : "SaveFailed";
                        var receipt = prior with { State = state, Code = state, SaveResult = saved,
                            Message = (state == "Saved" ? "原生保存完成，源工程及配套文件读回已匹配。" : "内存已更新；原生保存或源工程读回未确认，请查询原操作而不要重提。") +
                                (builtInPhysicalPlan?.Constraint is not null ? "原生字段约束操作会重置目标表的并发令牌。" : "") };
                        receipts[request.OperationId] = (digest, receipt);
                    }
                    catch (Exception exception)
                    {
                        var prior = receipts[request.OperationId].Receipt;
                        receipts[request.OperationId] = (digest, prior with { State = "SaveFailed", Code = "NativeSaveFailed",
                            Message = "内存已更新，保存尚未确认：" + SafeMessage(exception) + (builtInPhysicalPlan?.Constraint is not null ? "原生字段约束操作可能已重置目标表的并发令牌。" : "") });
                    }
                }
                return receipts[request.OperationId].Receipt;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<ProjectSaveResult> SaveAsync(ProjectSaveRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                if (string.IsNullOrWhiteSpace(request.OperationId)) throw new BridgeException("OperationIdRequired", "保存操作需要唯一编号。");
                var digest = Wire.Hash(JsonSerializer.Serialize(request, PlatformJson.Options));
                if (saveReceipts.TryGetValue(request.OperationId, out var prior))
                {
                    if (prior.Digest != digest) throw new BridgeException("OperationIdConflict", "保存操作编号已用于其他请求。");
                    return prior.Result;
                }
                CheckTarget(request.Target);
                if (request.ExpectedServiceBinding is not null)
                    throw new BridgeException("ServiceBindingUnavailable", "尚未核实 V11 账号服务的有效绑定和权威修订，不能确认或导出所断言的账号服务状态。");
                RequireReviewedState();
                if (Snapshot().Revision != request.ExpectedProjectRevision)
                    throw new BridgeException("ProjectRevisionChanged", "工程修订已变化，请重新读取保存基准。");
                cancellationToken.ThrowIfCancellationRequested();
                var result = await SavePendingAsync();
                saveReceipts.Add(request.OperationId, (digest, result));
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    private async Task<ProjectSaveResult> SavePendingAsync()
    {
        session.VerifyContext();
        RequireReviewedState();
        var editEpoch = context.CaptureSnapshot().Revision;
        if (pending.Count > 0 && pendingNativeRevision != editEpoch)
            throw new BridgeException("NativeStateChangedSinceMutation", "模块变更后设计器又发生了编辑；请先核对新增修改，未将其一并自动保存。");
        await NativeV11ArchiveSaveBoundary.WaitAsync(session.SourcePath, pending.Values.SelectMany(item => item.Paths));
        session.VerifyContext();
        if (context.CaptureSnapshot().Revision != editEpoch)
            throw new BridgeException("NativeStateChangedBeforeSave", "等待原生归档保存边界期间设计器内容发生变化，未保存。");
        foreach (var item in pending.Values)
        {
            item.VerifySourceUnchanged(session);
            item.VerifyMemory(session);
            if (pendingOwners.TryGetValue(item.OperationId, out var ownerPlan)) NativeV11Collaboration.VerifyOwned(ownerPlan, true, requireRegisteredModel: true);
            if (!pendingOwners.ContainsKey(item.OperationId)) EnsureOwnership(item.Address, item.Operation, afterMutation: true);
        }
        if (NativeV11Reflection.Call(session.Document, "CanSave") is not true)
            return new(false, NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true, false);
        var method = session.Document.GetType().GetMethod("SaveAsync", NativeV11Reflection.Flags, null, [typeof(bool)], null)!;
        // Cancellation is not translated into a claim that a running native save stopped.
        var accepted = await (Task<bool>)method.Invoke(session.Document, [false])!;
        session.VerifyContext();
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var verified = accepted && !dirty && pending.Count > 0;
        var items = pending.Values.ToArray();
        var archiveEligible = verified && !context.CaptureSnapshot().InCollaborationMode && File.Exists(session.SourcePath) &&
            Path.GetExtension(session.SourcePath).Equals(".fgcc", StringComparison.OrdinalIgnoreCase);
        // Keep the archive read lock through model/source verification, hash
        // capture, receipt publication and construction of the return value.
        FileStream? archive = null;
        try { if (archiveEligible) archive = new FileStream(session.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch { requiresReview = true; throw; }
        using var archiveLock = archive;
        string? savedSourceSha256 = null;
        bool VerifyBatch()
        {
            session.VerifyContext();
            if (NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is not false) return false;
            foreach (var item in items)
            {
                if (!item.SourceMatches(session)) return false;
                if (pendingOwners.TryGetValue(item.OperationId, out var owner) && owner.CancellationDirectFiles.Count != 0)
                    NativeV11Collaboration.VerifyCancelledAfterSave(owner);
            }
            session.VerifyContext();
            return NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is false;
        }
        try
        {
            if (verified) verified = VerifyBatch();
            if (verified && archive is not null)
            {
                savedSourceSha256 = CaptureVerifiedArchiveHash(archive, VerifyBatch);
                verified = savedSourceSha256 is not null;
            }
        }
        catch { requiresReview = true; throw; }
        if (!verified)
        {
            if (accepted && !dirty && items.Length > 0) requiresReview = true;
            return new(accepted, dirty, false);
        }
        try
        {
            foreach (var item in items)
                if (pendingAssets.TryGetValue(item.OperationId, out var asset) && pendingOwners.TryGetValue(item.OperationId, out var assetOwner))
                    await SaveAssetEvidenceAsync(assetOwner, asset);
                else if (item.Address.Kind == ModuleKind.CustomLibrary && pendingOwners.TryGetValue(item.OperationId, out var libraryOwner))
                    await SaveCustomLibraryEvidenceAsync(libraryOwner);
        }
        catch { requiresReview = true; throw; }
        try
        {
            foreach (var item in items)
                if (pendingOwners.TryGetValue(item.OperationId, out var owner))
                    await SaveOwnerEvidenceAsync(item, owner);
        }
        catch { requiresReview = true; throw; }
        foreach (var item in items)
        {
            if (item.FirstProcessJournal is not null && pendingOwners.TryGetValue(item.OperationId, out var firstProcessOwner))
                savedFirstProcesses[item.Address] = (item, firstProcessOwner);
            if (item.FolderMoveJournal is not null && pendingOwners.TryGetValue(item.OperationId, out var folderOwner))
                savedFolderMoves[item.Address] = (item, folderOwner);
            if (item.FolderDeleteJournal is not null && pendingOwners.TryGetValue(item.OperationId, out var deletedFolderOwner))
                savedFolderDeletes[item.Address] = (item, deletedFolderOwner);
            if (pendingOwners.TryGetValue(item.OperationId, out var completedOwner) && completedOwner.CancellationDirectFiles.Count != 0)
            {
                    savedCreations.TryGetValue(item.Address, out var creationId);
                    cancelledNew[item.Address] = new(item.OperationId, creationId, completedOwner, item.Paths);
                    savedCreations.Remove(item.Address);
            }
            if (item.Operation == ModuleOperation.Create && pendingOwners.ContainsKey(item.OperationId))
            {
                savedCreations[item.Address] = item.OperationId;
                cancelledNew.Remove(item.Address);
            }
            if (receipts.TryGetValue(item.OperationId, out var prior))
                receipts[item.OperationId] = (prior.Digest, prior.Receipt with { State = "Saved", Code = cancelledNew.ContainsKey(item.Address) ? "NewModuleCancelled" : "Saved",
                    Message = (cancelledNew.ContainsKey(item.Address) ? "新建模块的撤销已保存并核实源文件及原生登记；没有执行业务签入。" : "原生保存完成并通过源工程及配套文件读回。") +
                        (item.BuiltInTable?.NativeRowversionReset is true ? "原生字段约束操作会重置目标表的并发令牌。" : ""), SaveResult = new ProjectSaveResult(true, false, true) { SavedSourceSha256 = savedSourceSha256 } });
            pending.Remove(item.OperationId);
            pendingOwners.Remove(item.OperationId);
            pendingAssets.Remove(item.OperationId);
        }
        if (pending.Count == 0) pendingNativeRevision = null;
        else if (accepted && !dirty) requiresReview = true;
        return new ProjectSaveResult(accepted, dirty, verified) { SavedSourceSha256 = savedSourceSha256 };
    }

    internal static string HashArchive(Stream archive)
    {
        archive.Position = 0;
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(archive));
    }

    internal static string? CaptureVerifiedArchiveHash(Stream archive, Func<bool> verifyBatch)
    {
        var first = HashArchive(archive);
        if (!verifyBatch() || HashArchive(archive) != first) return null;
        return first;
    }

    private void CheckTarget(ProjectTarget target)
    {
        session.VerifyContext();
        V11Safety.SameTarget(target, context.CaptureSnapshot().Target);
    }

    private void RequireReviewedState()
    {
        var root = session.Route(ModuleKind.Table).Root;
        if (NativeV11DefinitionLoading.IsQuarantined(root) || NativeV11ExternalDefinitionLoading.IsQuarantined(root))
            requiresReview = true;
        if (requiresReview)
            throw new BridgeException("NativeStateRequiresReview", "原生变更、加载或回退未完整确认，请先核对并重新打开文档，禁止继续自动写入或保存。");
    }

    private void CheckWrite(ProjectTarget target, string revision, UserIdentity? expectedUser)
    {
        CheckTarget(target);
        var snapshot = Snapshot();
        RequireReviewedState();
        if (pending.Count > 0)
            throw new BridgeException("PendingMutationRequiresSave", "已有模块变更尚未确认源保存；请先查询并核对原操作，不能合入新的写入。");
        if (snapshot.Revision != revision || string.IsNullOrEmpty(revision))
            throw new BridgeException("ProjectRevisionChanged", "工程修订已变化，请重新读取。");
        if (!baselines.TryGetValue(revision, out var issued) || DateTimeOffset.UtcNow - issued > TimeSpan.FromMinutes(30))
            throw new BridgeException("ProjectBaselineExpired", "工程基准尚未读取或已超过 30 分钟，请重新获取工程状态。");
        if (snapshot.Busy) throw new BridgeException("DesignerBusy", "设计器当前忙碌。");
        if (snapshot.HasUnsavedChanges || NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true)
            throw new BridgeException("UnsavedChanges", "请先保存已有设计器修改，再提交新的模块变更。");
        if (snapshot.InCollaborationMode && (expectedUser is null || expectedUser != snapshot.CurrentUser))
            throw new BridgeException("CurrentUserMismatch", "协同写入需要匹配当前原生用户身份。");
    }

    private void EnsureOwnership(ModuleAddress address, ModuleOperation operation, bool afterMutation = false)
    {
        var snapshot = context.CaptureSnapshot();
        if (!snapshot.InCollaborationMode) return;
        if (Auxiliary(address.Kind) is not null)
            throw new BridgeException("AuxiliaryCollaborationUnverified", "此资源/目录的协同登记尚未通过验证，当前写入仅允许独立工程。");
        var type = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        var manager = NativeV11Reflection.Static(type, "get_Instance")!;
        var file = NativeV11Reflection.Get(manager, "ModuleFilePath") as string;
        if (file is null || !Path.GetFullPath(file).Equals(Path.Combine(session.SourcePath, ".collaboration"), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("CollaborationPathMismatch", "原生协同登记不属于当前工程。");
        if (operation == ModuleOperation.Create && !afterMutation && !IsSetting(address.Kind))
            throw new BridgeException("NewModuleCheckoutNotIntegrated", "V11 新模块的协同签出与保存登记尚待集成验证；当前仅允许独立工程新增。");
        if (NativeV11Reflection.Get(manager, "SaveLoadTrackedFiles") is not IDictionary tracked)
            throw new BridgeException("SaveRegistrationUnavailable", "原生保存登记不可用。");
        using var diskState = JsonDocument.Parse(File.ReadAllBytes(file));
        if (diskState.RootElement.ValueKind != JsonValueKind.Array || NativeV11Reflection.Get(manager, "Modules") is not IDictionary modules)
            throw new BridgeException("CollaborationStateUnavailable", "无法读取原生协同状态。");
        var paths = IsSetting(address.Kind) ? settings.SourcePaths(address.Kind) : new[] { session.NativePath(address) }.Concat(address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage
            ? [Path.ChangeExtension(session.NativePath(address), ".rd").Replace('\\', '/')]
            : address.Kind == ModuleKind.Report ? [Path.ChangeExtension(session.NativePath(address), ".rdlx").Replace('\\', '/')]
            : address.Kind == ModuleKind.BusinessProcess ? [Path.ChangeExtension(session.NativePath(address), ".bpmn").Replace('\\', '/')] : Array.Empty<string>());
        foreach (var path in paths)
        {
            var found = false;
            foreach (DictionaryEntry entry in tracked)
            {
                if (entry.Value is not IEnumerable<string> files || !files.Any(x => x.Replace('\\', '/').Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
                found = true;
                var moduleName = entry.Key as string ?? throw new BridgeException("InvalidSaveRegistration", "保存登记缺少原生模块名称。");
                var onDisk = diskState.RootElement.EnumerateArray().Where(node => node.TryGetProperty("ModuleName", out var name) && name.GetString() == moduleName).ToArray();
                if (onDisk.Length != 1 || modules[moduleName] is not object nativeModule ||
                    !onDisk[0].TryGetProperty("State", out var diskModuleState) || diskModuleState.GetInt32() is not (1 or 3) ||
                    diskModuleState.GetInt32() != Convert.ToInt32(NativeV11Reflection.Get(nativeModule, "State")) ||
                    !onDisk[0].TryGetProperty("LockedBy", out var locked) || locked.ValueKind != JsonValueKind.Object ||
                    !locked.TryGetProperty("UserName", out var userName) || userName.GetString() != snapshot.CurrentUser?.UserName ||
                    !locked.TryGetProperty("Email", out var email) || email.GetString() != snapshot.CurrentUser?.Email)
                    throw new BridgeException("CollaborationStateChanged", "磁盘协同状态与当前本人签出登记不一致，请刷新原生协同状态。");
                if (NativeV11Reflection.Call(manager, "IsModuleCheckoutByCurrentUser", entry.Key) is not true)
                    throw new BridgeException("ModuleNotCheckedOut", "模块保存登记缺少本人签出：" + path);
            }
            if (!found) throw new BridgeException("SaveRegistrationUnavailable", "模块缺少原生保存登记：" + path);
        }
    }

    private bool IsSetting(ModuleKind kind) => settings.Kinds.Contains(kind);
    private NativeV11AuxiliaryStore? Auxiliary(ModuleKind kind) => auxiliaryStores.SingleOrDefault(store => store.Kinds.Contains(kind));
    private ReferenceResult References(ModuleAddress address) => IsSetting(address.Kind) ? SettingsReferences(address) :
        Auxiliary(address.Kind) is { } store ? store.FindReferences(address) : NativeV11References.Find(session, address);
    private static string CapabilityValidation(ModuleKind kind, ModuleOperation operation, bool available)
    {
        if (!available) return "NotImplemented";
        if (kind == ModuleKind.PermissionGroup) return "NativeMementoVerified_SourceAndLivePending";
        if (kind == ModuleKind.Plugin) return "NativeDependencyReadImplemented_InitializedLiveRegistryPending";
        if (kind == ModuleKind.ResourceFile && operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)
            return "NativeLocalLibraryFileGroupComponentsVerified_LiveSettingsPending";
        if (kind is ModuleKind.Table or ModuleKind.View && operation is ModuleOperation.Create or ModuleOperation.Delete)
            return "NativeBindingAndBuiltInImplemented_LiveSavePending";
        if (kind == ModuleKind.Report) return "NativePairComponentsVerified_DefaultPermissionAndLivePending";
        if (kind == ModuleKind.BusinessProcess) return "NativePairImplemented_ModelGenerationDefaultPermissionAndLivePending";
        if (kind == ModuleKind.Notification && operation is ModuleOperation.Create or ModuleOperation.Delete) return "NativeLifecycleImplemented_DefaultPermissionAndLivePending";
        if ((kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or
            ModuleKind.Notification or ModuleKind.Report or ModuleKind.BusinessProcess or ModuleKind.ApplicationSettings or ModuleKind.GlobalVariable or ModuleKind.LanguageResource or
            ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle or ModuleKind.DataConnection or ModuleKind.CustomLibrary) ||
            (kind is ModuleKind.Table or ModuleKind.View) && (operation is ModuleOperation.List or ModuleOperation.Read or ModuleOperation.Update))
            return "NativeComponentsVerified_LiveDesignerPending";
        return "Implemented_ComponentVerificationPending";
    }
    private static string AvailableReason(ModuleKind kind, ModuleOperation operation) => (kind, operation) switch
    {
        (ModuleKind.Table, ModuleOperation.Update) => "支持说明、字段别名/说明、已有公式、原生权限绑定及只读外联定义重绑定；内建表支持受限物理、公式、统计字段及关系操作。布尔、整数、数值、文本和日期时间列可用 setColumnDefault（value 为对应类型或 null）和 setColumnRequired（value 为布尔）；日期时间采用不带时区的 ISO 格式、最多毫秒，文本默认值不能包含双引号或控制字符。原生约束重建会重置目标表的并发令牌，其他管理数据及业务数据受保全检查。内层 null 权限绑定和外联默认权限转换受限；协同时要求完整共享 owner。实时设计器、业务计算及运行时并发验收待完成。",
        (ModuleKind.View, ModuleOperation.Update) => "支持说明、字段别名/说明、已有公式、现有非空原生权限绑定及只读外联定义重绑定；根目录自定义内建视图另支持单条 SELECT 的 sqlBody 更新，须完整证明无传入引用，协同时要求完整共享 owner。实际保存和实时设计器验收待完成。",
        (ModuleKind.Table, ModuleOperation.Create) => "支持已登记连接的外联定义和根目录最小内建表创建；协同内建表要求完整共享 owner 签出及 SQLite 源组证明，实际保存和实时设计器验收待完成。",
        (ModuleKind.Table, ModuleOperation.Delete) => "支持普通外联定义移除和根目录内建表删除；内建删除影响 SQLite 物理表，协同时要求完整共享 owner 签出，须通过引用、关系和源组检查；实际保存与实时设计器验收待完成。",
        (ModuleKind.View, ModuleOperation.Create) => "支持已登记连接的外联视图绑定和根目录内建视图创建；内建视图须提供单条 SELECT 的 sqlBody，协同时要求完整共享 owner 签出及源组证明；实际保存和实时设计器验收待完成。",
        (ModuleKind.View, ModuleOperation.Delete) => "支持普通外联视图移除和根目录自定义内建视图删除；协同时要求完整共享 owner 签出，需通过引用及源组检查；实际保存和实时设计器验收待完成。",
        (ModuleKind.UserControlPage, ModuleOperation.Update) => "编辑页面内容并同步原生 RD；已有公开属性/命令/方法定义保持稳定；完整 ISpread 缓存与实时调用验收待完成。",
        (ModuleKind.PermissionGroup, _) => "仅精确104构建且工程已使用PermissionGroup模式；原生模型/memento已验证，两文件源保存和实时权限验收待完成，不自动切换模式。",
        (ModuleKind.Report, _) => "原生报表树、权限DTO、缓存、目录保存及FGCC双文件C/U/D组件通过；创建使用原生默认授权工厂，完整默认授权/SaveAsync/界面和重开仍需实时验收。",
        (ModuleKind.BusinessProcess, _) => "仅流程JSON/BPMN定义维护；真实模型、权限工厂和原生目录保存路径已接入，组件及完整模型代次/默认授权/SaveAsync/重开正在验收；不部署流程或调用流程引擎。",
        (ModuleKind.Notification, ModuleOperation.Create or ModuleOperation.Delete) => "原生通知模型、默认授权工厂和保存登记已接入；默认授权与实时验收正在验证；不发送或执行通知。",
        (ModuleKind.CustomLibrary, _) => "维护已存在本地资源或链接的定义，保留稳定地址；不下载或执行脚本；原生组件通过，实时设计器验收待完成。",
        (ModuleKind.ResourceFile, ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) => "UserJaveScript 本地注册库及原生静态目录 UserFile/UserMapFile/可用 UserTemplateFile 使用各自原生保存管线；普通资源删除需完整引用证明，Upload 独立侧目录未开放写入。",
        (ModuleKind.Plugin, _) => "读取已完成初始化的当前原生工程依赖；已保存声明单独标注；不会初始化插件注册表、载入程序集或执行安装。",
        _ => "已通过精确版本的原生模型、缓存或保存组件检查；完整实时适配器/设计器保存、界面与重开验收仍待统筹执行。"
    };
    private ProjectSnapshot Snapshot()
    {
        var before = context.CaptureSnapshot();
        var source = NativeV11SourceRevision.Capture(session.SourcePath);
        var after = context.CaptureSnapshot();
        if (before != after) throw new BridgeException("ContextChangedDuringRead", "读取工程基准期间原生状态已变化。");
        return after with { Revision = Wire.Hash(after.Revision + "\n" + source) };
    }
    private string UnavailableReason(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.PermissionGroup && session.Profile.Version == "11.0.3.0"
        ? "精确构建 11.0.3.0 没有工程 PermissionGroupConfig 与 PermissionGroup 模式；不会将较新构建的配置写入旧版。" : kind switch
    {
        ModuleKind.Table or ModuleKind.View => "表/视图原生 TableSet、工作表及权限登记尚未完成写入与保存验证；只提供定义读取，不连接数据库。",
        ModuleKind.Report => "报表 JSON/RDLX 创建及删除的配对原生生命周期尚在验证；不能以单独删除缓存文件代替。",
        ModuleKind.BusinessProcess => "流程 JSON/BPMN 配对原生模型/编辑器登记尚未完成写入验证；发布和运行实例操作不会执行。",
        ModuleKind.Notification => "通知原生权限服务与完整保存登记尚未验证；不会发送通知。",
        ModuleKind.Theme or ModuleKind.CellStyle or ModuleKind.ListViewStyle => "主题/样式条目的默认项保护、引用与完整主题保存登记正在适配。",
        ModuleKind.DataConnection => "连接定义集合的稳定 ID、引用与原生保存登记正在适配；不会连接或变更外部数据库。",
        ModuleKind.CustomLibrary => "自定义库条目和配套资源的原生登记正在适配；不会执行或下载脚本。",
        ModuleKind.Image or ModuleKind.ResourceFile => "二进制资源树身份、配套数据与原生保存/删除登记尚未完成。",
        ModuleKind.Plugin => "插件登记与安装包文件操作尚未适配；不会安装或卸载插件。",
        ModuleKind.User or ModuleKind.Role or ModuleKind.Organization or ModuleKind.PermissionGroup => "该管理能力涉及独立账号/权限服务或专用登记，本定义适配器尚未开放此副作用范围。",
        ModuleKind.Folder => "文件夹需要模块类别和目录登记的专用 CRUD，尚未开放模糊文件路径写入。",
        ModuleKind.ApplicationSettings when operation is ModuleOperation.Create or ModuleOperation.Delete => "应用设置为必需单例根配置，仅支持原生读取和经验证字段的更新。",
        _ => "该模块/操作尚未完成原生映射与保存验证。"
    };
    private ModuleDocument ReadDocument(ModuleAddress address, bool afterMutation = false, bool issueBaseline = true)
    {
        var store = Auxiliary(address.Kind);
        var document = IsSetting(address.Kind) ? settings.Read(address) : store is not null ? store.Read(address) : session.Read(session.Find(address), afterMutation);
        var source = IsSetting(address.Kind)
            ? NativeV11PendingSave.ReadFiles(session.SourcePath, settings.SourcePaths(address.Kind))
            : store is not null ? NativeV11PendingSave.ReadFiles(session.SourcePath, store.SourcePaths(address)) : NativeV11PendingSave.CaptureSource(session, address);
        var revision = Wire.Hash(document.Revision + string.Concat(source.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => "\n" + pair.Key + "\n" + (pair.Value is null ? "absent" : NativeV11Session.Hash(pair.Value)))));
        if (issueBaseline) baselines[revision] = DateTimeOffset.UtcNow;
        return document with { Revision = revision };
    }
    private bool Writable(ModuleKind kind, ModuleOperation operation) => settings.CanWrite(kind, operation) || Auxiliary(kind)?.CanWrite(kind, operation) == true || NativeV11PageWriter.Supports(kind, operation) || NativeV11Reports.Supports(kind, operation) || NativeV11Processes.Supports(kind, operation) || NativeV11TableLifecycle.Supports(kind, operation) || NativeV11TableWriter.Supports(kind, operation) || NativeV11DefinitionWriter.Supports(kind, operation);

    private ReferenceResult SettingsReferences(ModuleAddress address)
    {
        if (address.Kind is ModuleKind.CellStyle or ModuleKind.ListViewStyle or ModuleKind.DataConnection or ModuleKind.CustomLibrary or ModuleKind.PermissionGroup)
            return settings.FindReferences(address);
        if (address.Kind is not (ModuleKind.GlobalVariable or ModuleKind.LanguageResource))
            return new([], false, "此配置条目的完整原生引用枚举尚未适配。");
        return settings.FindEntryReferences(address);
    }
    private Task<T> OnUi<T>(Func<T> action, CancellationToken token) => context.Ui.RunAsync(() => Task.FromResult(action()), token);
    private bool NeedsDefinitionLoading(ModuleMutationRequest request) => request.Scope == EffectScope.ProjectDefinition &&
        Writable(request.Address.Kind, request.Operation) &&
        (request.Operation == ModuleOperation.Delete || request.Address.Kind is ModuleKind.Table or ModuleKind.View or ModuleKind.Folder);
    private static bool CheckoutCandidate(ModuleMutationRequest request) =>
        (request.Address.Kind is ModuleKind.Table or ModuleKind.View && request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) ||
        (request.Address.Kind == ModuleKind.Folder && request.Operation is ModuleOperation.Create or ModuleOperation.Delete) ||
        (request.Address.Kind == ModuleKind.Image && request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) ||
        (request.Address.Kind == ModuleKind.ResourceFile && request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) ||
        (NativeV11CollaborationOwners.SettingsOwnerAddress(request.Address.Kind) is not null && request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) ||
        (request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete && request.Address.Kind is ModuleKind.Page or ModuleKind.Report) ||
        (request.Operation is ModuleOperation.Create && request.Address.Kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.BusinessProcess) ||
        (request.Operation is ModuleOperation.Update or ModuleOperation.Delete && request.Address.Kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or
            ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.BusinessProcess);
    private bool SupportsAutomaticCheckout(ModuleMutationRequest request) => context.CaptureSnapshot().InCollaborationMode &&
        (CheckoutCandidate(request) || IsImageFolderUpdate(request) || folders.Value.IsGroupedNativeRename(request));
    private async Task<T> ReadOnUi<T>(ProjectTarget target, bool loadDefinitions, Func<T> action, CancellationToken token, bool loadForCollaboration = false)
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                CheckTarget(target);
                if (loadDefinitions || loadForCollaboration && context.CaptureSnapshot().InCollaborationMode)
                {
                    token.ThrowIfCancellationRequested();
                    await NativeV11TableDefinitionLoading.EnsureAsync(session);
                    token.ThrowIfCancellationRequested();
                }
                CheckTarget(target);
                return action();
            }, token).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
    private sealed record ListCursor(string Revision, ModuleKind Kind, string? Folder, int Offset);
    private static string SafeMessage(Exception error)
    {
        while (error is System.Reflection.TargetInvocationException { InnerException: not null }) error = error.InnerException!;
        // Native parse errors may embed supplied command parameters or credentials.
        return error is BridgeException ? error.Message : "原生操作未完成（" + error.GetType().Name + "）。";
    }

    public async Task<JsonElement> InvokeAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        if (NativeV11UserManagementRead(request))
            return await InvokeNativeAccountReadAsync(request, cancellationToken);
        if (NativeV11OrganizationRead.Handles(request))
            return await InvokeNativeOrganizationReadAsync(request, cancellationToken);
        if (NativeV11CommandApi.Handles(request.Namespace, request.Method))
            return await InvokeCommandApiAsync(request, cancellationToken);
        await OnUi(() => { CheckTarget(request.Target); return true; }, cancellationToken);
        object result = (request.Namespace, request.Method) switch
        {
            ("project", "getSnapshot") => await GetSnapshotAsync(cancellationToken),
            ("project", "getCapabilities") => await GetCapabilitiesAsync(cancellationToken),
            ("project", "getMutationStatus") => await OnUi(() =>
            {
                var id = request.Parameters.GetProperty("operationId").GetString() ?? "";
                return receipts.TryGetValue(id, out var receipt) ? receipt.Receipt :
                    throw new BridgeException("OperationNotFound", "当前文档会话中没有此操作回执。");
            }, cancellationToken),
            _ => throw new BridgeException("UnknownPlatformApi", "此 V11 适配器未注册该 API。")
        };
        return JsonSerializer.SerializeToElement(result, result.GetType(), PlatformJson.Options);
    }

    private static readonly PlatformApiDescriptor[] Catalog =
    [
        Api("project", "getSnapshot", false, "读取绑定实例与工程状态。"),
        Api("project", "getCapabilities", false, "逐模块读取真实能力和验收状态。"),
        new("project", "getMutationStatus", JsonSerializer.SerializeToElement(new { type = "object", properties = new { operationId = new { type = "string" } }, required = new[] { "operationId" } }), null, false,
            Documentation: "读取同一文档会话中原操作的最新原生加载/保存回执；不会重新执行写入。"),
        .. NativeV11CommandApi.Descriptors(),
        NativeV11CommandTypes.Descriptor(),
        .. NativeAccountDescriptors()
    ];
    internal static IReadOnlyList<PlatformApiDescriptor> ExtensionCatalog => Catalog;
    private static PlatformApiDescriptor Api(string ns, string method, bool mutation, string docs) =>
        new(ns, method, JsonSerializer.SerializeToElement(new { type = "object" }), null, mutation, Documentation: docs);
}

