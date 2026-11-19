using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class V12ProjectAdapter : IFixedProjectApiCatalog, IProjectCollaborationAdapter
{
    private readonly AdapterHostContext context;
    private readonly NativeApiRegistry registry;
    private readonly Lazy<NativeProjectGuard> guard;
    private readonly Lazy<NativeReports> reports;
    private readonly Lazy<NativeProcesses> processes;
    private readonly Lazy<NativeModules> modules;
    private readonly Lazy<NativeConnections> connections;
    private readonly Lazy<NativeResources> resources;
    private readonly Lazy<NativePlugins> plugins;
    private readonly Lazy<NativeDefinitionFolders> folders;
    private readonly Lazy<NativeRegisteredFolders> pageFolders;
    private readonly Lazy<NativeRegisteredFolders> tableFolders;
    private readonly Dictionary<string,Lazy<NativeRegisteredFolders>> additionalFolders;
    private readonly Lazy<NativeModuleFiles> targetFiles;
    private readonly Lazy<NativeAccounts> accounts;
    private readonly Lazy<RegistryModules> registryModules;
    private readonly Lazy<NativeCollaborationModules> collaborationModules;
    private readonly Lazy<NativeCollaboration> collaboration;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly Dictionary<string, (string Hash, ModuleMutationReceipt Receipt)> receipts = new(StringComparer.Ordinal);
    private readonly Dictionary<ModuleAddress, (string Revision, DateTimeOffset Created)> issued = [];
    private readonly List<(ModuleMutationRequest Request, IReadOnlyList<string> Files)> pending = [];
    private readonly Dictionary<string,ModuleAddress> pendingAddresses = new(StringComparer.Ordinal);
    private readonly HashSet<string> startedApiOperations = new(StringComparer.Ordinal);
    private bool apiPending;
    private bool apiFileGroupsKnown=true;
    private readonly Dictionary<object,NativeModuleFileGroup> apiTargetGroups = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string,NativeModuleFileGroup> typedTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> apiPendingFiles = new(StringComparer.OrdinalIgnoreCase);
    private string? originalSourceRevision;
    private string? ownDirtyRevision;
    private AccountServiceBinding? pendingAccountExport;
    private bool uncertain;
    private static readonly JsonSerializerOptions GuardJson = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(allowIntegerValues: false) } };

    public AdapterIdentity Identity { get; } = new(V12AdapterFactory.Version, V12AdapterFactory.DesignerSha256, V12AdapterFactory.Profile);
    private NativeRegisteredFolders RegisteredFolders(string? path)=>path?.Split('/')[0] switch
    { "Table"=>tableFolders.Value, "Page"=>pageFolders.Value, var owner when owner is not null && additionalFolders.TryGetValue(owner,out var route)=>route.Value,
      _=>throw new BridgeException("UnsupportedFolderOwner","The native folder owner is not available.") };

    private readonly Func<Task<bool>> saveDocument;
    internal V12ProjectAdapter(AdapterHostContext context) : this(context, null) { }
    internal V12ProjectAdapter(AdapterHostContext context, Func<Task<bool>>? saveDocument)
    {
        this.context = context;
        this.saveDocument = saveDocument ?? SaveNativeDocumentAsync;
        registry = new(context.DesignerAssembly,()=>NativeReflection.Get(context.NativeDocument,"ForguncySettings")!,RequireUnreferencedCommandSignature,ValidateNativeLibraryLifecycle);
        guard = new(() => new(context),LazyThreadSafetyMode.PublicationOnly);
        reports = new(() => new(context.DesignerAssembly, context.NativeDocument),LazyThreadSafetyMode.PublicationOnly);
        processes = new(() => new(context.DesignerAssembly, context.NativeDocument),LazyThreadSafetyMode.PublicationOnly);
        modules = new(() => new(context),LazyThreadSafetyMode.PublicationOnly);
        connections = new(() => new(context),LazyThreadSafetyMode.PublicationOnly);
        resources = new(() => new(context),LazyThreadSafetyMode.PublicationOnly);
        plugins = new(() => new(context),LazyThreadSafetyMode.PublicationOnly);
        folders = new(() => new(context.DesignerAssembly, context.NativeDocument),LazyThreadSafetyMode.PublicationOnly);
        targetFiles = new(()=>new(context.DesignerAssembly,context.NativeDocument,()=>plugins.Value),LazyThreadSafetyMode.PublicationOnly);
        pageFolders = new(()=>targetFiles.Value.RegisteredPageFolders(),LazyThreadSafetyMode.PublicationOnly);
        tableFolders = new(()=>targetFiles.Value.RegisteredTableFolders(),LazyThreadSafetyMode.PublicationOnly);
        additionalFolders = new[]{ModuleKind.MasterPage,ModuleKind.UserControlPage,ModuleKind.ServerCommand,ModuleKind.SharedCommand,ModuleKind.ScheduledTask,ModuleKind.Notification,ModuleKind.Image}
            .ToDictionary(kind=>kind.ToString(),kind=>new Lazy<NativeRegisteredFolders>(()=>targetFiles.Value.RegisteredFolders(kind),LazyThreadSafetyMode.PublicationOnly),StringComparer.Ordinal);
        accounts = new(()=>new(context with { CaptureSnapshot=()=>guard.Value.Capture() },registry),LazyThreadSafetyMode.PublicationOnly);
        registryModules = new(() => new(registry, context));
        collaborationModules=new(()=>new(context.DesignerAssembly,context.NativeDocument),LazyThreadSafetyMode.PublicationOnly);
        collaboration=new(()=>new(context with {CaptureSnapshot=()=>guard.Value.Capture()},collaborationModules.Value.ResolveNativeModule,
            collaborationModules.Value.MapNativeModule,new NativeCollaborationGit(guard.Value.Capture().Target.ProjectPath)),LazyThreadSafetyMode.PublicationOnly);
    }

    public Task<ProjectSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
        OnUi(() =>
        {
            var snapshot=guard.Value.Capture();
            AccountServiceBinding? binding=null;
            try { binding=accounts.Value.TryCaptureBinding(); }
            catch(Exception error) when(error is not OperationCanceledException) { }
            return Task.FromResult(snapshot with { AccountService=binding });
        }, cancellationToken);

    public Task<IReadOnlyList<PlatformApiDescriptor>> GetApiCatalogAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<PlatformApiDescriptor>>(registry.Catalog().Where(x=>!x.MutatesProject||x.Scope!=EffectScope.AccountService||IsNativeServiceAction(x)).ToArray());
    }

    public Task<IReadOnlyList<ModuleCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) => OnUi(async () =>
    {
        var state = guard.Value.Capture();
        var native = new Dictionary<(ModuleKind,ModuleOperation),ModuleCapability>();
        void BindCapabilities(Func<IReadOnlyList<ModuleCapability>> capture, params ModuleKind[] kinds)
        {
            try { foreach(var entry in capture()) native[(entry.Kind,entry.Operation)] = entry; }
            catch(Exception error) when(error is not OperationCanceledException)
            {
                while(error is System.Reflection.TargetInvocationException { InnerException: not null }) error=error.InnerException;
                foreach(var kind in kinds)
                    foreach(var operation in Enum.GetValues<ModuleOperation>())
                        native[(kind,operation)] = new(kind,operation,false,"native-binding-unavailable",error.GetType().Name+": "+error.Message,
                            IsAccount(kind) ? EffectScope.AccountService : EffectScope.ProjectDefinition);
            }
        }
        BindCapabilities(()=>modules.Value.Capabilities(),ModuleKind.Theme,ModuleKind.CellStyle,ModuleKind.ListViewStyle,ModuleKind.LanguageResource,ModuleKind.GlobalVariable,ModuleKind.ApplicationSettings);
        BindCapabilities(()=>connections.Value.Capabilities(),ModuleKind.DataConnection);
        BindCapabilities(()=>resources.Value.Capabilities(),ModuleKind.ResourceFile);
        BindCapabilities(()=>plugins.Value.Capabilities(),ModuleKind.Plugin);
        BindCapabilities(()=>accounts.Value.Capabilities(),ModuleKind.User,ModuleKind.Role,ModuleKind.Organization);
        var api = registryModules.Value.Capabilities().ToDictionary(x => (x.Kind, x.Operation));
        var list = new List<ModuleCapability>();
        foreach (var kind in Enum.GetValues<ModuleKind>())
            foreach (var operation in Enum.GetValues<ModuleOperation>())
            {
                if (kind == ModuleKind.Folder)
                    list.Add(new(kind,operation,true,
                        "implemented-awaiting-live-designer", "Page, MasterPage, UserControlPage, Table, ServerCommand, SharedCommand, ScheduledTask, Notification, Image, Report and BusinessProcess folders; FolderPath starts with owner kind. Nonempty rename, move and deletion use complete native child-owner, reference and source plans. Table deletion awaits the native internal-database action and requires affected application settings to be preowned; external and native system objects retain their own restrictions. Image deletion rejects external reference owners. Rename and move are separate operations; process deletion preserves the shared database."));
                else if (IsFileDefinition(kind))
                    list.Add(new(kind, operation, true, "implemented-awaiting-live-designer", operation is ModuleOperation.Create or ModuleOperation.Delete ?
                        kind==ModuleKind.Report ? "Report collaboration creation/deletion uses native owner registration and grouped source verification; current-user identity and checkout guards apply. Live Designer acceptance is pending." :
                        "BusinessProcess Create uses a verified initialized built-in database or the complete native first-initialization owner plan. First initialization imports native process views, pages and styles and may update the start page. Delete protects the shared database and all native owners, including the last owner. External databases and first-initialization destination collisions are unsupported. Live Designer acceptance is pending." : null));
                else if (native.TryGetValue((kind, operation), out var entry)) list.Add(entry);
                else if (api.TryGetValue((kind, operation), out var apiEntry)) list.Add(apiEntry.Scope==EffectScope.AccountService&&operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete ?
                    apiEntry with { Available=false,Validation="service-binding-and-authoritative-evidence-pending",Reason="Typed account mutation requires a verified effective provider/process binding and consistent semantic revision. Use the dedicated userManagement APIs for native service acceptance receipts." } : apiEntry);
                else list.Add(new(kind, operation, false, "not-implemented", "This module operation has no verified V12 adapter implementation yet."));
            }
        await Task.CompletedTask;
        return (IReadOnlyList<ModuleCapability>)list;
    }, cancellationToken);

    public Task<ModulePage> ListAsync(ProjectTarget target, ModuleQuery query, CancellationToken cancellationToken = default) => OnUi(async () =>
    {
        RequireRead(target);
        if (query.Kind == ModuleKind.Report) return reports.Value.List(query);
        if (query.Kind == ModuleKind.BusinessProcess) return processes.Value.List(query);
        if (query.Kind == ModuleKind.DataConnection) return connections.Value.List(query);
        if (query.Kind == ModuleKind.ResourceFile) return resources.Value.List(query);
        if (query.Kind == ModuleKind.Plugin) return plugins.Value.List(query);
        if (IsAccount(query.Kind)) return await accounts.Value.ListAsync(query,cancellationToken);
        if (query.Kind==ModuleKind.Folder && query.FolderPath is null)
        {
            if(query.Limit is < 1 or > 1000 || query.Cursor is not null && (!int.TryParse(query.Cursor,out var position) || position<0)) throw new BridgeException("InvalidFolderQuery","Invalid folder pagination.");
            IEnumerable<ModuleSummary> Collect(Func<ModuleQuery,ModulePage> list)
            {
                string? cursor=null;
                do { var page=list(query with {Cursor=cursor,Limit=1000}); foreach(var item in page.Items) yield return item; cursor=page.NextCursor; } while(cursor is not null);
            }
            var all=Collect(folders.Value.List).Concat(Collect(pageFolders.Value.List)).Concat(Collect(tableFolders.Value.List))
                .Concat(additionalFolders.Values.SelectMany(route=>Collect(route.Value.List))).OrderBy(item=>item.Address.FolderPath,StringComparer.Ordinal).ThenBy(item=>item.Address.Name,StringComparer.Ordinal).ToArray();
            var offset=query.Cursor is null?0:int.Parse(query.Cursor);
            return new(all.Skip(offset).Take(query.Limit).ToArray(),offset+query.Limit<all.Length?(offset+query.Limit).ToString():null);
        }
        if (NativeRegisteredFolders.Supports(query)) return RegisteredFolders(query.FolderPath).List(query);
        if (query.Kind == ModuleKind.Folder && NativeDefinitionFolders.Supports(query)) return folders.Value.List(query);
        if (modules.Value.Capabilities().Any(x => x.Kind == query.Kind && x.Operation == ModuleOperation.List && x.Available)) return modules.Value.List(query);
        return await registryModules.Value.ListAsync(query, cancellationToken);
    }, cancellationToken);

    public Task<ModuleDocument> ReadAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default) =>
        ReadModuleAsync(target, address, true, cancellationToken);
    public Task<ModuleDocument> InspectModuleAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default) =>
        ReadModuleAsync(target, address, false, cancellationToken);

    private Task<ModuleDocument> ReadModuleAsync(ProjectTarget target, ModuleAddress address, bool issueBaseline, CancellationToken cancellationToken) => OnUi(async () =>
    {
        var before = RequireRead(target);
        var result = await ReadCore(address, cancellationToken);
        if (guard.Value.Capture().Revision != before.Revision) throw new BridgeException("ReadBaselineChanged", "Project changed while reading the module.");
        if (issueBaseline) issued[address] = (result.Revision, DateTimeOffset.UtcNow);
        return result;
    }, cancellationToken);

    public Task<ReferenceResult> FindReferencesAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default) => OnUi(() =>
    {
        RequireRead(target);
        return Task.FromResult(address.Kind == ModuleKind.Report ? reports.Value.References(address) :
            address.Kind == ModuleKind.BusinessProcess ? processes.Value.References(address) :
            address.Kind == ModuleKind.DataConnection ? connections.Value.References(address) :
            address.Kind == ModuleKind.ResourceFile ? resources.Value.References(address) :
            address.Kind == ModuleKind.Plugin ? plugins.Value.References(address) :
            IsAccount(address.Kind) ? accounts.Value.References(address) :
            NativeRegisteredFolders.Supports(address) ? RegisteredFolders(address.FolderPath).References(address) :
            NativeDefinitionFolders.Supports(address) ? folders.Value.References(address) :
            modules.Value.Capabilities().Any(x => x.Kind == address.Kind && x.Available) ? modules.Value.References(address) :
            registryModules.Value.References(address));
    }, cancellationToken);

    public Task<ModuleValidationResult> ValidateAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default) =>
        OnUi(() => ValidateCore(request, cancellationToken), cancellationToken);

    public async Task<ModuleMutationReceipt> ApplyAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            return await OnUi(async () =>
            {
                ValidateOperationId(request.OperationId);
                var hash = Wire.Hash(JsonSerializer.Serialize(request, Wire.Json));
                if (receipts.TryGetValue(request.OperationId, out var old))
                {
                    if (old.Hash != hash) throw new BridgeException("OperationIdReused", "Operation ID is already bound to a different request.");
                    return old.Receipt;
                }
                var validation = await ValidateCore(request, cancellationToken);
                if (!validation.Valid) return new(request.OperationId, "Rejected", "ValidationFailed", string.Join("; ", validation.Issues.Select(x => x.Code + ": " + x.Message)));
                cancellationToken.ThrowIfCancellationRequested();
                if(IsAccount(request.Address.Kind)) return await ApplyAccountCore(request,hash,cancellationToken);
                await NativeArchiveSaveBoundary.WaitAsync(request.Target.ProjectPath,null,async () =>
                {
                    var checkedAgain = await ValidateCore(request,cancellationToken);
                    if(!checkedAgain.Valid) throw new BridgeException("MutationBaselineChangedDuringArchiveWait",string.Join("; ",checkedAgain.Issues.Select(x=>x.Code+": "+x.Message)));
                },cancellationToken);
                NativeApiFileCapture? lifecycleCapture=null;
                NativeResourceOwnerCapture? resourceCapture=null;
                NativeCollaboration.EditRegistration? registerGlobalOwner = null;
                NativeProcessLifecycle? processLifecycle=null;
                NativeFolderLifecyclePlan? folderLifecycle=null;
                NativeModuleFilePlan? firstProcessPlan=null;
                if(request.Address.Kind==ModuleKind.BusinessProcess && request.Operation==ModuleOperation.Create && !targetFiles.Value.HasAnyProcessDatabase())
                {
                    processLifecycle=new NativeProcessLifecycle(context.DesignerAssembly,NativeReflection.Get(context.NativeDocument,"ForguncySettings")!,processes.Value,targetFiles.Value,()=>guard.Value.Capture().InCollaborationMode);
                    firstProcessPlan=processLifecycle.CaptureFirstInitializationPlan(request);
                    processes.Value.VerifyFirstInitializationContext();
                    lifecycleCapture=new NativeApiFileCapture("process","first-initialization",default,request.Address,request.Operation,firstProcessPlan.BeforeGroups.FirstOrDefault(),firstProcessPlan);
                }
                NativeFolderLifecyclePlan CaptureDefinitionFolder()
                {
                    NativeReports? store=null;
                    if(request.Operation==ModuleOperation.Delete && folders.Value.RequiresGrouped(request))
                    {
                        if(request.Address.FolderPath!.Split('/')[0]=="BusinessProcess")
                        {
                            processLifecycle=new NativeProcessLifecycle(context.DesignerAssembly,NativeReflection.Get(context.NativeDocument,"ForguncySettings")!,processes.Value,targetFiles.Value,()=>guard.Value.Capture().InCollaborationMode);
                            store=processes.Value.Definitions;
                        }
                        else store=reports.Value;
                    }
                    return folders.Value.CaptureCollaborationLifecycle(request,targetFiles.Value,store,processLifecycle);
                }
                if(!guard.Value.Capture().InCollaborationMode && NativeDefinitionFolders.Supports(request.Address) && folders.Value.RequiresGrouped(request))
                    folderLifecycle=CaptureDefinitionFolder();
                if(!guard.Value.Capture().InCollaborationMode && NativeRegisteredFolders.Supports(request.Address))
                    folderLifecycle=RegisteredFolders(request.Address.FolderPath).Capture(request,targetFiles.Value);
                if(guard.Value.Capture().InCollaborationMode)
                {
                    if(NativeRegisteredFolders.Supports(request.Address))
                    {
                        folderLifecycle=RegisteredFolders(request.Address.FolderPath).Capture(request,targetFiles.Value);
                        if(folderLifecycle.RequiredOwnedModules is {Count:>0} preowned)
                        {
                            var state=guard.Value.Capture();
                            foreach(var name in preowned) guard.Value.RequireOwnedModule(state,name);
                        }
                        await CheckoutLifecyclePlanAsync(request.OperationId,request.Target,folderLifecycle.Plan,cancellationToken);
                    }
                    else if(NativeDefinitionFolders.Supports(request.Address))
                    {
                        folderLifecycle=CaptureDefinitionFolder();
                        await CheckoutLifecyclePlanAsync(request.OperationId,request.Target,folderLifecycle.Plan,cancellationToken);
                    }
                    else if(request.Address.Kind == ModuleKind.ResourceFile)
                    {
                        resourceCapture = resources.Value.CaptureCollaborationOwners(request, targetFiles.Value);
                        await CheckoutExistingPlanAsync(request.OperationId, request.Target, resourceCapture.Plan, cancellationToken);
                    }
                    else if (request.Address.Kind == ModuleKind.GlobalVariable)
                    {
                        var state = guard.Value.RequireWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser,
                            allowDirty: (pending.Count != 0 || apiPending) && request.ExpectedProjectRevision == ownDirtyRevision);
                        var group = targetFiles.Value.Resolve(NativeModuleFiles.ApplicationRoot)!;
                        registerGlobalOwner = await PrepareRootTrackingAsync(group, state, cancellationToken);
                    }
                    else if(request.Address.Kind is ModuleKind.PermissionGroup or ModuleKind.CustomLibrary)
                    {
                        var owner = targetFiles.Value.Resolve(request.Address) ?? throw new BridgeException("CollaborationEffectPlanUnavailable", "The permission-group root owner is unavailable.");
                        await CheckoutExistingPlanAsync(request.OperationId, request.Target, new NativeModuleFilePlan([owner], []), cancellationToken);
                    }
                    else if((UsesNativeModules(request.Address.Kind) || request.Address.Kind == ModuleKind.DataConnection) && NativeModuleFiles.Supports(request.Address.Kind))
                    {
                        var owner = targetFiles.Value.Resolve(request.Address) ?? throw new BridgeException("CollaborationEffectPlanUnavailable", "The settings entry needs its complete existing native owner.");
                        await CheckoutExistingPlanAsync(request.OperationId, request.Target, new NativeModuleFilePlan([owner], []), cancellationToken);
                    }
                    else if(request.Operation is ModuleOperation.Create or ModuleOperation.Delete)
                    {
                        if(request.Address.Kind==ModuleKind.Report)
                        {
                            var plan=reports.Value.CaptureReportLifecycle(request,targetFiles.Value);
                            lifecycleCapture=new NativeApiFileCapture("report","lifecycle",default,request.Address,request.Operation,plan.BeforeGroups.FirstOrDefault(),plan);
                        }
                        else if(request.Address.Kind==ModuleKind.BusinessProcess)
                        {
                            if(firstProcessPlan is null)
                            {
                                processLifecycle=new NativeProcessLifecycle(context.DesignerAssembly,NativeReflection.Get(context.NativeDocument,"ForguncySettings")!,processes.Value,targetFiles.Value,()=>guard.Value.Capture().InCollaborationMode);
                                var plan=processLifecycle.Capture(request);
                                lifecycleCapture=new NativeApiFileCapture("process","lifecycle",default,request.Address,request.Operation,plan.BeforeGroups.FirstOrDefault(),plan);
                            }
                        }
                        else lifecycleCapture=registryModules.Value.CaptureLifecycle(request);
                        RequirePreownedCapture(lifecycleCapture);
                        await CheckoutLifecyclePlanAsync(request.OperationId,request.Target,lifecycleCapture?.Plan,cancellationToken);
                        if(processLifecycle is not null && request.Operation==ModuleOperation.Create)
                        {
                            // Native checkout may have created the New lock. Do not
                            // mutate a model if the shared process DB drifted while
                            // acquiring it; retain uncertainty rather than replay.
                            try
                            {
                                if(firstProcessPlan is null) processLifecycle.RequireInitializedDatabase();
                                else processLifecycle.RequireSameFirstInitializationPlan(request,firstProcessPlan);
                            }
                            catch { uncertain=true; throw; }
                        }
                    }
                    else await CheckoutExistingPlanAsync(request.OperationId,request.Target,request.Operation==ModuleOperation.Update && request.Address.Kind is ModuleKind.Report or ModuleKind.GlobalVariable
                        ? targetFiles.Value.Resolve(request.Address) is { } owner ? new NativeModuleFilePlan([owner],[]) : null
                        : registryModules.Value.CaptureExistingUpdatePlan(request),cancellationToken);
                }
                var definitionBefore=IsFileDefinition(request.Address.Kind)&&request.Operation!=ModuleOperation.Create
                    ?targetFiles.Value.Resolve(request.Address):null;
                var retainProcessDatabase=request.Address.Kind==ModuleKind.BusinessProcess && request.Operation==ModuleOperation.Delete &&
                    processLifecycle is not null && definitionBefore is not null ? processLifecycle.CaptureRetainedDatabase(definitionBefore) : null;
                originalSourceRevision ??= NativeProjectGuard.SourceRevision(request.Target.ProjectPath, includeCollaboration: false);
                // Set dirty before any model/cache mutation; a partial failure must remain visible.
                if (IsFileDefinition(request.Address.Kind) || request.Address.Kind is ModuleKind.DataConnection or ModuleKind.ResourceFile or ModuleKind.Folder || UsesNativeModules(request.Address.Kind))
                    NativeReflection.Set(context.NativeDocument, "ContainsUnsavedChanges", true);
                var initial = new ModuleMutationReceipt(request.OperationId, "Running", "NativeMutationStarted", "Native mutation started; do not replay the operation.", ExecutionMayContinue: true);
                receipts.Add(request.OperationId, (hash, initial));
                IReadOnlyList<string> files;
                try
                {
                    registerGlobalOwner?.Register();
                    if(firstProcessPlan is not null)
                        files=await processes.Value.InitializeFirstProcessAsync(() => processLifecycle!.RequireSameFirstInitializationPlan(request,firstProcessPlan),()=>processes.Value.Apply(request));
                    else if (request.Address.Kind == ModuleKind.Report) files = reports.Value.Apply(request);
                    else if (request.Address.Kind == ModuleKind.BusinessProcess) files = processes.Value.Apply(request);
                    else if (request.Address.Kind == ModuleKind.DataConnection) files = connections.Value.Apply(request);
                    else if (request.Address.Kind == ModuleKind.ResourceFile) files = resources.Value.Apply(request);
                    else if (NativeRegisteredFolders.Supports(request.Address)) files = await RegisteredFolders(request.Address.FolderPath).ApplyAsync(request,folderLifecycle!);
                    else if (NativeDefinitionFolders.Supports(request.Address)) files = folders.Value.Apply(request,folderLifecycle);
                    else if (UsesNativeModules(request.Address.Kind)) files = modules.Value.Apply(request);
                    else files = await registryModules.Value.ApplyAsync(request, CancellationToken.None);
                    if(folderLifecycle is not null)
                        foreach(var group in NativeRegisteredFolders.Supports(request.Address) ? RegisteredFolders(request.Address.FolderPath).After(request,folderLifecycle,targetFiles.Value) : folders.Value.ResolveCollaborationLifecycleAfter(request,folderLifecycle,targetFiles.Value)) RecordFileGroup(group);
                    if(resourceCapture is not null)
                    {
                        foreach (var resourceOwner in resources.Value.CaptureOwnersAfter(resourceCapture, targetFiles.Value))
                        { RecordFileGroup(resourceOwner); pendingResourceOwnerNames.Add(resourceOwner.NativeModuleName); }
                    }
                    else if(lifecycleCapture is not null)
                    {
                        var finalGroups=targetFiles.Value.ResolveApiGroupsAfter(lifecycleCapture);
                        if(finalGroups is not { Count:>0 })
                            throw new BridgeException("NativeAffectedTargetsReadbackFailed","The native lifecycle mutation did not resolve every planned owner; do not replay it.");
                        if(firstProcessPlan is not null)
                        {
                            foreach(var group in finalGroups) RecordFileGroup(group);
                            files=files.Concat(finalGroups.SelectMany(group=>group.RequiredFiles.Concat(group.AbsentFiles)))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        }
                    }
                    if(IsFileDefinition(request.Address.Kind))
                    {
                        // Native definition U replaces the model object. Keep
                        // its old identity absent, then register the replacement
                        // so a preceding raw reference edit is not left stale.
                        if(definitionBefore is not null&&!definitionBefore.IsPresent()&&targetFiles.Value.ResolveDeletedIdentity(definitionBefore) is { } removed)
                        { var finalRemoved=retainProcessDatabase is null ? removed : retainProcessDatabase(removed); RecordFileGroup(finalRemoved); typedTargets[request.OperationId]=finalRemoved; }
                        if(request.Operation!=ModuleOperation.Delete&&targetFiles.Value.Resolve(request.Address) is { } replacement)
                        { RecordFileGroup(replacement); typedTargets[request.OperationId]=replacement; }
                    }
                    else if((UsesNativeModules(request.Address.Kind) || request.Address.Kind == ModuleKind.DataConnection)&&NativeModuleFiles.Supports(request.Address.Kind))
                    {
                        var ownerAddress = registerGlobalOwner is null ? request.Address : NativeModuleFiles.ApplicationRoot;
                        if (targetFiles.Value.Resolve(ownerAddress) is { } finalGroup)
                        {
                            RecordFileGroup(finalGroup); typedTargets[request.OperationId] = finalGroup;
                            if (registerGlobalOwner is not null) pendingRegisteredOwners.Add(finalGroup.NativeIdentity);
                        }
                    }
                    else if(request.Address.Kind is not (ModuleKind.DataConnection or ModuleKind.ResourceFile or ModuleKind.Folder or ModuleKind.Plugin)&&!UsesNativeModules(request.Address.Kind))
                        foreach(var finalGroup in registryModules.Value.GetTargetGroups(request)) RecordFileGroup(finalGroup);
                    // Native execution cannot be abandoned merely because the caller stopped waiting.
                    bool SamePending(ModuleMutationRequest previous) => previous.Address == request.Address || pendingAddresses.GetValueOrDefault(previous.OperationId) == request.Address;
                    var previousOperations = pending.Where(x => SamePending(x.Request)).Select(x => x.Request.OperationId).ToHashSet(StringComparer.Ordinal);
                    var previousFiles = pending.Where(x => previousOperations.Contains(x.Request.OperationId)).SelectMany(x => x.Files);
                    files = files.Concat(previousFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    foreach (var previousOperation in previousOperations) { pendingAddresses.Remove(previousOperation); typedTargets.Remove(previousOperation); }
                    pending.RemoveAll(x => previousOperations.Contains(x.Request.OperationId));
                    pending.Add((request, files));
                    var resultAddress = IsFileDefinition(request.Address.Kind) || request.Address.Kind is ModuleKind.DataConnection or ModuleKind.ResourceFile or ModuleKind.Folder || UsesNativeModules(request.Address.Kind) ? request.Address : registryModules.Value.ResultAddress(request);
                    var result = request.Operation == ModuleOperation.Delete ? null : NativeRegisteredFolders.Supports(request.Address) ? RegisteredFolders(request.Address.FolderPath).ReadMutationResult(request) : NativeDefinitionFolders.Supports(request.Address) ? folders.Value.ReadMutationResult(request) : await ReadCore(resultAddress, CancellationToken.None);
                    if (result is not null) resultAddress = result.Address;
                    pendingAddresses[request.OperationId] = resultAddress;
                    if (request.Operation != ModuleOperation.Delete && result is null) throw new BridgeException("NativeReadbackMissing", "Native mutation has no module readback.");
                    ownDirtyRevision = guard.Value.Capture().Revision;
                    var save = request.Save ? await SaveCore(request.ExpectedServiceBinding) : null;
                    var saved = save is { NativeSaveAccepted: true, ContainsUnsavedChanges: false, SourceVerified: true };
                    if (saved && request.Operation != ModuleOperation.Delete) result = await ReadCore(resultAddress, CancellationToken.None);
                    if (result is not null) issued[result.Address] = (result.Revision, DateTimeOffset.UtcNow);
                    var receipt = new ModuleMutationReceipt(request.OperationId, saved ? "Saved" : request.Save ? "verification_failed" : "LoadedPendingSave",
                        saved ? "SavedAndVerified" : request.Save ? "SaveUnconfirmed" : "NativeMemoryReadback",
                        saved ? "Native save accepted and source file group verified." : "Native model changed; source persistence is not confirmed.",
                        result, save, files, false);
                    receipts[request.OperationId] = (hash, receipt);
                    return receipt;
                }
                catch (Exception error)
                {
                    uncertain = true;
                    var receipt = new ModuleMutationReceipt(request.OperationId, "Uncertain", error is BridgeException b ? b.Code : "NativeMutationError",
                        error.Message, ExecutionMayContinue: true);
                    receipts[request.OperationId] = (hash, receipt);
                    return receipt;
                }
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    public async Task<ProjectSaveResult> SaveAsync(ProjectSaveRequest request, CancellationToken cancellationToken = default)
    {
        await writer.WaitAsync(cancellationToken);
        try
        {
            return await OnUi(async () =>
            {
                ValidateOperationId(request.OperationId);
                if (uncertain) throw new BridgeException("NativeExecutionUncertain", "A previous native operation is unconfirmed; reconcile or reopen the document before saving.");
                if(pendingAccountExport is not null&&request.ExpectedServiceBinding is null) throw new BridgeException("AccountServiceBindingRequired","Exporting committed service changes requires the reviewed post-operation account binding.");
                var state = guard.Value.Capture();
                guard.Value.RequireWrite(request.Target, request.ExpectedProjectRevision, null,
                    allowDirty: (pending.Count != 0 || apiPending||pendingAccountExport is not null) && state.Revision == ownDirtyRevision);
                return await SaveCore(request.ExpectedServiceBinding);
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    public Task<JsonElement> InvokeAsync(PlatformApiRequest request, CancellationToken cancellationToken = default) =>
        InvokeProjectApiAsync(request, static () => { }, cancellationToken);

    public async Task<JsonElement> InvokeProjectApiAsync(PlatformApiRequest request, Action startingNativeWrite, CancellationToken cancellationToken = default)
    {
        if (!registry.Contains(request.Namespace, request.Method)) throw new BridgeException("ApiNotRegistered", "API is not in the approved native C# registry.");
        if(registry.Mutates(request.Namespace,request.Method)&&registry.Entry(request.Namespace,request.Method).Scope==EffectScope.AccountService)
            throw new BridgeException("AccountServiceAliasNotSupported","Account-service writes require the typed single-entity evidence contract; raw aliases are disabled.");
        if (!registry.Mutates(request.Namespace, request.Method)) return await OnUi(async () =>
        {
            RequireRead(request.Target);
            return await registry.InvokeAsync(request.Namespace, request.Method, request.Parameters, cancellationToken);
        }, cancellationToken);
        await writer.WaitAsync(cancellationToken);
        try
        {
            return await OnUi(async () =>
            {
                if (uncertain) throw new BridgeException("NativeExecutionUncertain", "A previous native operation is unconfirmed; reconcile or reopen the document.");
                ValidateOperationId(request.OperationId);
                if (startedApiOperations.Contains(request.OperationId!)) throw new BridgeException("OperationAlreadyStarted", "Native API operation has already started; inspect its result instead of retrying.");
                if (request.Parameters.ValueKind != JsonValueKind.Object || !request.Parameters.TryGetProperty("guard", out var platform) || platform.ValueKind != JsonValueKind.Object)
                    throw new BridgeException("ProjectBaselineRequired", "Mutating APIs require the standard parameters.guard from a current project snapshot.");
                var mutation = platform.Deserialize<PlatformApiMutationGuard>(GuardJson) ?? throw new BridgeException("InvalidMutationGuard", "The API mutation guard is missing.");
                if (mutation.OperationId != request.OperationId) throw new BridgeException("OperationIdMismatch", "API request and guard operation IDs differ.");
                guard.Value.CheckTarget(mutation.Target);
                if (mutation.Scope != registry.Entry(request.Namespace, request.Method).Scope)
                    throw new BridgeException("EffectScopeMismatch", "Mutation guard scope must match the registered API's actual effect domain.");
                var writeState = guard.Value.RequireWrite(request.Target, mutation.ExpectedProjectRevision, mutation.ExpectedUser,
                    allowDirty: (pending.Count != 0 || apiPending) && mutation.ExpectedProjectRevision == ownDirtyRevision);
                RequirePageLoadModuleGuard(request, mutation, writeState);
                if (mutation.Address is not null)
                {
                    if (mutation.ExpectedModuleRevision is null || !issued.TryGetValue(mutation.Address, out var baseline) ||
                        baseline.Revision != mutation.ExpectedModuleRevision || DateTimeOffset.UtcNow - baseline.Created > TimeSpan.FromMinutes(30) ||
                        (await ReadCore(mutation.Address, cancellationToken)).Revision != mutation.ExpectedModuleRevision)
                        throw new BridgeException("ModuleRevisionChanged", "Optional API module guard must match a fresh module read in this adapter session.");
                }
                else if (mutation.ExpectedModuleRevision is not null) throw new BridgeException("ModuleAddressRequired", "A module revision guard requires its module address.");
                var parameters = JsonNode.Parse(request.Parameters.GetRawText())!.AsObject(); parameters.Remove("guard");
                if(mutation.Address is not null) RegistryModules.ValidateApiAddressGuard(request.Namespace,request.Method,JsonSerializer.SerializeToElement(parameters),mutation.Address);
                if(mutation.Scope==EffectScope.ProjectDefinition)
                    await NativeArchiveSaveBoundary.WaitAsync(request.Target.ProjectPath,null,() => RevalidateApiGuard(request,mutation,cancellationToken),cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var nativeParameters=JsonSerializer.SerializeToElement(parameters);
                // Reject deterministic payload errors before recording native
                // execution as started; no native mutation has occurred here.
                registry.ValidateRequestParameters(request.Namespace,request.Method,nativeParameters);
                var pageLoadEdit = await registry.PreparePageLoadAsync(request.Namespace, request.Method, nativeParameters, cancellationToken);
                if (pageLoadEdit is not null) await RevalidateApiGuard(request, mutation, cancellationToken);
                if(request.Namespace=="app.customLibrary" && NativeLibraryLifecycle.Supports(request.Method))
                    await NativeArchiveSaveBoundary.WaitAsync(request.Target.ProjectPath,null,async ()=>
                    {
                        guard.Value.RequireWrite(request.Target,mutation.ExpectedProjectRevision,mutation.ExpectedUser,
                            allowDirty:(pending.Count!=0||apiPending)&&mutation.ExpectedProjectRevision==ownDirtyRevision);
                        if(mutation.Address is not null && (await ReadCore(mutation.Address,cancellationToken)).Revision!=mutation.ExpectedModuleRevision)
                            throw new BridgeException("ModuleRevisionChanged","The library module changed while waiting for the native file timestamp boundary.");
                        registry.ValidateRequestParameters(request.Namespace,request.Method,nativeParameters);
                    },cancellationToken);
                var permissionBindings = NativePermissionGroupGuard.IsBindingMutation(request.Namespace, request.Method) ? NativePermissionGroupGuard.RequestedBindings(request.Method, nativeParameters) : null;
                var permissionConfig = permissionBindings is null ? null : NativePermissionGroupGuard.BoundConfig(context.DesignerAssembly, NativeReflection.Get(context.NativeDocument, "ForguncySettings")!);
                var permissionBefore = permissionConfig is null ? null : NativePermissionGroupGuard.ReadBindings(permissionConfig);
                var fileCapture=targetFiles.Value.CaptureApiBefore(request.Namespace,request.Method,nativeParameters);
                ValidateApiCapture(request.Namespace,request.Method,fileCapture);
                RequirePreownedCapture(fileCapture);
                var lifecyclePlan=fileCapture?.Operation is ModuleOperation.Create or ModuleOperation.Delete ||
                    fileCapture?.Plan?.NativeChanges?.Any(change=>change.GetType().GetField("Item2")?.GetValue(change)?.ToString() is "New" or "Remove")==true;
                var registerOwner = await PrepareApiOwnerAsync(fileCapture, writeState, cancellationToken);
                if(lifecyclePlan)
                {
                    startingNativeWrite();
                    await CheckoutLifecyclePlanAsync(request.OperationId!,request.Target,fileCapture?.Plan,cancellationToken);
                }
                else if (registerOwner is null)
                    await CheckoutExistingPlanAsync(request.OperationId!,request.Target,fileCapture?.Plan ??
                    (request.Namespace=="page" && request.Method=="updatePageSettings" && fileCapture?.Before is { } page ? new NativeModuleFilePlan([page],[]) : null),cancellationToken,startingNativeWrite);
                originalSourceRevision ??= NativeProjectGuard.SourceRevision(request.Target.ProjectPath, includeCollaboration: false);
                startingNativeWrite();
                startedApiOperations.Add(request.OperationId!);
                try
                {
                    registerOwner?.Register();
                    if (registerOwner is not null && fileCapture!.Address.Kind == ModuleKind.GlobalVariable)
                        modules.Value.RequireNativeTracking(fileCapture.Address);
                    var result = pageLoadEdit is null
                        ? await registry.InvokeAsync(request.Namespace, request.Method, nativeParameters, CancellationToken.None)
                        : await registry.ApplyPageLoadAsync(pageLoadEdit);
                    if (registerOwner is not null && fileCapture!.Address.Kind == ModuleKind.CustomLibrary)
                    {
                        NativeModules.RegisterDirty(context.DesignerAssembly, "CustomLibrary");
                        NativeReflection.Set(context.NativeDocument, "ContainsUnsavedChanges", true);
                    }
                    if (permissionBindings is not null)
                    {
                        if (!ReferenceEquals(permissionConfig, NativePermissionGroupGuard.BoundConfig(context.DesignerAssembly, NativeReflection.Get(context.NativeDocument, "ForguncySettings")!)))
                            throw new BridgeException("PermissionGroupBindingReadbackFailed", "The native permission configuration changed during binding.");
                        NativePermissionGroupGuard.VerifyBindings(permissionBefore!, NativePermissionGroupGuard.ReadBindings(permissionConfig!), permissionBindings);
                    }
                    var groups=fileCapture is null?null:targetFiles.Value.ResolveApiGroupsAfter(fileCapture);
                    if(guard.Value.Capture().InCollaborationMode && lifecyclePlan && groups is not { Count:>0 })
                        throw new BridgeException("NativeAffectedTargetsReadbackFailed","The native lifecycle mutation did not resolve every planned owner; do not replay it.");
                    if (registerOwner is not null)
                    {
                        if (groups is not [var updated]) throw new BridgeException("NativeOwnerReadbackFailed", "The updated root could not be read back.");
                        pendingRegisteredOwners.Add(updated.NativeIdentity);
                    }
                    apiPending = true;
                    apiFileGroupsKnown&=groups is {Count:>0};
                    if(groups is not null) foreach(var group in groups) RecordFileGroup(group);
                    ownDirtyRevision = guard.Value.Capture().Revision;
                    // Direct C# APIs preserve their native dirty semantics and raw result. They do not claim a source save.
                    return result;
                }
                catch { uncertain = true; throw; }
            }, cancellationToken);
        }
        finally { writer.Release(); }
    }

    private async Task<ModuleValidationResult> ValidateCore(ModuleMutationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            ValidateOperationId(request.OperationId);
            if (uncertain) throw new BridgeException("NativeExecutionUncertain", "A previous native operation is unconfirmed.");
            if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)) throw new BridgeException("InvalidMutation", "Mutation must be Create, Update or Delete.");
            var expectedScope = NativeApiCatalog.IdentityRoutes.ContainsKey(request.Address.Kind) ? EffectScope.AccountService : request.Address.Kind == ModuleKind.Plugin ? EffectScope.RuntimeDeployment : EffectScope.ProjectDefinition;
            if (request.Scope != expectedScope) throw new BridgeException("EffectScopeDenied", "Mutation scope differs from this module's native domain.");
            if(request.Scope==EffectScope.AccountService)
            {
                if(request.ExpectedServiceBinding is null) throw new BridgeException("AccountServiceBindingRequired","Typed account writes require a verified effective service binding and authoritative semantic revision.");
            }
            var observed = guard.Value.Capture();
            var state = guard.Value.RequireWrite(request.Target, request.ExpectedProjectRevision, request.ExpectedUser,
                allowDirty: (pending.Count != 0 || apiPending||pendingAccountExport is not null) && observed.Revision == ownDirtyRevision);
            if(IsAccount(request.Address.Kind))
            {
                if(request.Operation!=ModuleOperation.Create && (!issued.TryGetValue(request.Address,out var accountRead)||
                    accountRead.Revision!=request.ExpectedModuleRevision||DateTimeOffset.UtcNow-accountRead.Created>TimeSpan.FromMinutes(30)))
                    throw new BridgeException("ModuleBaselineRequired","Typed account update/delete requires a fresh authoritative entity read in this adapter session.");
                return await accounts.Value.ValidateAsync(request,cancellationToken);
            }
            if(request.Save&&pendingAccountExport is not null&&request.ExpectedServiceBinding is null)
                throw new BridgeException("AccountServiceBindingRequired","This native save would also export committed account changes; supply the reviewed post-operation binding or save the module in memory first.");
            if (request.Document is not null && request.Document.Address != request.Address) throw new BridgeException("ModuleAddressMismatch", "Document address differs from the mutation target.");
            if (request.Operation is ModuleOperation.Update or ModuleOperation.Delete)
            {
                if (!issued.TryGetValue(request.Address, out var baseline) || DateTimeOffset.UtcNow - baseline.Created > TimeSpan.FromMinutes(30) || baseline.Revision != request.ExpectedModuleRevision)
                    throw new BridgeException("ModuleBaselineRequired", "Read this module in the current adapter session within 30 minutes before updating or deleting it.");
                var current = await ReadCore(request.Address, cancellationToken);
                if (current.Revision != request.ExpectedModuleRevision) throw new BridgeException("ModuleRevisionChanged", "Native module or its source file group changed.");
            }
            if (IsFileDefinition(request.Address.Kind))
            {
                if (request.Operation != ModuleOperation.Create)
                {
                    var name = request.Address.Kind == ModuleKind.Report ? reports.Value.GetModuleName(request.Address) : processes.Value.GetModuleName(request.Address);
                    if(request.Operation==ModuleOperation.Delete || (request.Address.Kind==ModuleKind.Report && request.Operation==ModuleOperation.Update)) guard.Value.RequireAvailableModule(state,name);
                    else guard.Value.RequireOwnedModule(state,name);
                }
                return request.Address.Kind == ModuleKind.Report ? reports.Value.Validate(request) : processes.Value.Validate(request);
            }
            if (request.Address.Kind == ModuleKind.DataConnection)
            {
                guard.Value.RequireOwnedModule(state, connections.Value.GetModuleName(request.Address));
                return connections.Value.Validate(request);
            }
            if (request.Address.Kind == ModuleKind.ResourceFile)
            {
                if (state.InCollaborationMode && request.Operation == ModuleOperation.Update && resources.Value.IsRegisteredLocalLibrary(request.Address))
                    throw new BridgeException("RegisteredLibraryContentRequired", "Use app.customLibrary.updateContent for this registered CSS or JavaScript file.");
                guard.Value.RequireOwnedModule(state, resources.Value.GetModuleName(request.Address));
                if (state.InCollaborationMode) _ = resources.Value.CaptureCollaborationOwners(request, targetFiles.Value);
                return resources.Value.Validate(request);
            }
            if (request.Address.Kind == ModuleKind.Plugin) return plugins.Value.Validate(request);
            if (NativeRegisteredFolders.Supports(request.Address))
            {
                if (state.InCollaborationMode && request.Operation!=ModuleOperation.Create)
                    guard.Value.RequireAvailableModule(state,RegisteredFolders(request.Address.FolderPath).NativeName(request.Address));
                return RegisteredFolders(request.Address.FolderPath).Validate(request);
            }
            if (NativeDefinitionFolders.Supports(request.Address))
            {
                if (state.InCollaborationMode && request.Operation!=ModuleOperation.Create)
                    guard.Value.RequireAvailableModule(state,folders.Value.GetModuleName(request.Address));
                return folders.Value.Validate(request);
            }
            if (UsesNativeModules(request.Address.Kind))
            {
                if (state.InCollaborationMode && request.Address.Kind == ModuleKind.GlobalVariable)
                {
                    ValidateGlobalVariable(request.Address, request.Operation, exactName: true);
                    var group = targetFiles.Value.Resolve(NativeModuleFiles.ApplicationRoot)!;
                    var registration = await PrepareRootTrackingAsync(group, state, cancellationToken);
                    return modules.Value.Validate(request, registration);
                }
                guard.Value.RequireOwnedModule(state, modules.Value.GetModuleName(request.Address));
                return modules.Value.Validate(request);
            }
            return await registryModules.Value.ValidateAsync(request, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return new(false, [new(error is BridgeException b ? b.Code : "NativeValidationError", error.Message)]); }
    }

    private Task<bool> SaveNativeDocumentAsync()
    {
        var method = context.NativeDocument.GetType().GetMethod("SaveAsync", NativeReflection.Flags, null, [typeof(bool)], null);
        if (method?.ReturnType != typeof(Task<bool>)) throw new BridgeException("NativeSaveContractChanged", "Native SaveAsync(bool) signature differs from the verified contract.");
        return (Task<bool>)method.Invoke(context.NativeDocument, [false])!;
    }

    private async Task<ProjectSaveResult> SaveCore(AccountServiceBinding? reviewedService=null)
    {
        if(pendingAccountExport is not null&&reviewedService!=pendingAccountExport)
            throw new BridgeException("AccountServiceRevisionChanged","The requested account export does not match the approved post-operation service binding.");
        if(reviewedService is not null) accounts.Value.RequireCurrentBinding(reviewedService);
        if (originalSourceRevision is not null && originalSourceRevision != NativeProjectGuard.SourceRevision(context.CaptureSnapshot().Target.ProjectPath, includeCollaboration: false))
            throw new BridgeException("ProjectSourceChangedBeforeSave", "Project source changed after the native mutation baseline; save was not invoked.");
        var beforeWait=guard.Value.Capture();
        await NativeArchiveSaveBoundary.WaitAsync(beforeWait.Target.ProjectPath,null,() =>
        {
            var current=guard.Value.Capture();
            if(current.Revision!=beforeWait.Revision) throw new BridgeException("SaveBaselineChangedDuringArchiveWait","Native project, source or UI edits changed while waiting for the native archive save boundary.");
            if(reviewedService is not null) accounts.Value.RequireCurrentBinding(reviewedService);
            return Task.CompletedTask;
        });
        foreach (var rootIdentity in pendingRegisteredOwners)
        {
            var group = apiTargetGroups[rootIdentity];
            guard.Value.RequireOwnedModule(beforeWait, group.NativeModuleName);
            var register = await collaboration.Value.PrepareRegisteredEditFilesAsync(group, CancellationToken.None);
            if (!group.Current() || guard.Value.Capture().Revision != beforeWait.Revision)
                throw new BridgeException("BaselineChanged", "The reviewed owner group changed before native save.");
            register.Register();
        }
        var accepted = await saveDocument();
        guard.Value.CheckTarget(context.CaptureSnapshot().Target);
        var afterSave=guard.Value.Capture();
        var dirty=afterSave.HasUnsavedChanges || NativeReflection.Get(context.NativeDocument,"ContainsUnsavedChanges") is true;
        bool VerifyPending()
        {
            var valid=(pending.Count > 0 || apiPending||reviewedService is not null) && pending.All(x => IsFileDefinition(x.Request.Address.Kind) ? VerifyTypedTarget(x.Request) :
                x.Request.Address.Kind == ModuleKind.DataConnection ? connections.Value.VerifySource(x.Request) :
                x.Request.Address.Kind == ModuleKind.ResourceFile ? resources.Value.VerifySource(x.Request) :
                NativeRegisteredFolders.Supports(x.Request.Address) ? RegisteredFolders(x.Request.Address.FolderPath).VerifySource(x.Request) :
                NativeDefinitionFolders.Supports(x.Request.Address) ? folders.Value.VerifySource(x.Request) :
                UsesNativeModules(x.Request.Address.Kind) ? NativeModuleFiles.Supports(x.Request.Address.Kind) ? VerifyTypedTarget(x.Request) : modules.Value.VerifySource(x.Request) : VerifyRegistryTarget(x.Request));
            if(apiTargetGroups.Count>0) valid &= ReduceFileGroups(apiTargetGroups.Values).All(targetFiles.Value.Verify);
            if(apiPending) valid &= apiFileGroupsKnown&&apiTargetGroups.Count>0;
            if(reviewedService is not null) valid &= accounts.Value.VerifyProjectExport(reviewedService,beforeWait.Target.ProjectPath);
            return valid;
        }
        string? savedHash=null;
        bool verified;
        if(accepted && !dirty && !afterSave.InCollaborationMode && reviewedService is null &&
           Path.GetExtension(afterSave.Target.ProjectPath).Equals(".fgcc",StringComparison.OrdinalIgnoreCase))
        {
            bool ContextStable()
            {
                var current=guard.Value.Capture();
                return current.Target==afterSave.Target && current.Revision==afterSave.Revision && !current.HasUnsavedChanges && !current.Busy &&
                    NativeReflection.Get(context.NativeDocument,"ContainsUnsavedChanges") is false;
            }
            (verified,savedHash)=StableNativeArchiveSave.Verify(afterSave.Target.ProjectPath,ContextStable,VerifyPending);
        }
        else verified=VerifyPending();
        if(accepted && !dirty && verified && afterSave.InCollaborationMode)
            await CompleteSavedLibraryOwners(apiTargetGroups.Values);
        if(accepted && !dirty && verified && afterSave.InCollaborationMode)
            await RememberOwnedEditsAsync(apiTargetGroups.Values);
        if (accepted && !dirty && verified) { pendingRegisteredOwners.Clear(); pending.Clear(); pendingAddresses.Clear(); typedTargets.Clear(); apiPending = false; apiFileGroupsKnown=true; apiTargetGroups.Clear(); apiPendingFiles.Clear(); pendingResourceOwnerNames.Clear(); pendingAccountExport=null; originalSourceRevision = null; ownDirtyRevision = null; }
        else if (originalSourceRevision is not null && originalSourceRevision != NativeProjectGuard.SourceRevision(context.CaptureSnapshot().Target.ProjectPath, includeCollaboration: false)) uncertain = true;
        else ownDirtyRevision = guard.Value.Capture().Revision;
        return new ProjectSaveResult(accepted, dirty, verified) { SavedSourceSha256=savedHash };
    }

    private async Task RevalidateApiGuard(PlatformApiRequest request,PlatformApiMutationGuard mutation,CancellationToken cancellationToken)
    {
        guard.Value.CheckTarget(mutation.Target);
        guard.Value.RequireWrite(request.Target,mutation.ExpectedProjectRevision,mutation.ExpectedUser,
            allowDirty:(pending.Count!=0||apiPending)&&mutation.ExpectedProjectRevision==ownDirtyRevision);
        if(mutation.Address is not null && (mutation.ExpectedModuleRevision is null ||
            !issued.TryGetValue(mutation.Address,out var baseline) || baseline.Revision!=mutation.ExpectedModuleRevision ||
            DateTimeOffset.UtcNow-baseline.Created>TimeSpan.FromMinutes(30) ||
            (await ReadCore(mutation.Address,cancellationToken)).Revision!=mutation.ExpectedModuleRevision))
            throw new BridgeException("ModuleRevisionChanged","Native module baseline changed while waiting for its archive write boundary.");
    }

    private async Task<ModuleMutationReceipt> ApplyAccountCore(ModuleMutationRequest request,string hash,CancellationToken cancellationToken)
    {
        var original=NativeProjectGuard.SourceRevision(request.Target.ProjectPath,includeCollaboration:false);
        receipts.Add(request.OperationId,(hash,new(request.OperationId,"Running","AccountServiceMutationStarted","Typed native account mutation is running.",ExecutionMayContinue:true)));
        NativeAccountMutationOutcome outcome;
        try { outcome=await accounts.Value.ApplyAsync(request,cancellationToken); }
        catch(Exception error) when(error is not StackOverflowException and not OutOfMemoryException)
        {
            // NativeAccounts returns every post-dispatch uncertainty as an
            // outcome. Exceptions here are pre-dispatch failures, never a
            // reason to replay an already submitted service transaction.
            var rejected=new ModuleMutationReceipt(request.OperationId,"failed",error is BridgeException b?b.Code:error is OperationCanceledException?"CancelledBeforeDispatch":"AccountServiceUnavailable",
                "Typed account request did not enter the native mutation dispatcher.");
            receipts[request.OperationId]=(hash,rejected);
            return rejected;
        }
        var service=outcome.ServiceResult;
        if(outcome.OutcomeUncertain||!service.PersistenceVerified||!service.ReadbackVerified)
        {
            if(outcome.OutcomeUncertain) uncertain=true;
            var failed=new ModuleMutationReceipt(request.OperationId,service.NativeAccepted is false&&!outcome.OutcomeUncertain?"failed":"verification_failed",outcome.Code,
                service.NativeAccepted is false&&!outcome.OutcomeUncertain?"Native account request was rejected without a verified service change.":"Service outcome is unconfirmed; do not replay this account mutation.",
                outcome.Document,ExecutionMayContinue:outcome.OutcomeUncertain,ServiceResult:service);
            receipts[request.OperationId]=(hash,failed);
            return failed;
        }
        if(service.Binding!=request.ExpectedServiceBinding||service.ObservedRevision is null||service.EntityPresent!=(request.Operation!=ModuleOperation.Delete))
        {
            uncertain=true;
            var invalid=new ModuleMutationReceipt(request.OperationId,"verification_failed","ServiceOutcomeUnconfirmed","Typed service evidence did not match the approved binding or entity lifecycle.",ServiceResult:service);
            receipts[request.OperationId]=(hash,invalid);
            return invalid;
        }
        pendingAccountExport=new(service.Binding.Identity,service.ObservedRevision);
        originalSourceRevision??=original;
        ownDirtyRevision=guard.Value.Capture().Revision;
        if(outcome.Document is not null) issued[outcome.Document.Address]=(outcome.Document.Revision,DateTimeOffset.UtcNow);
        ProjectSaveResult? exported=null;
        var state="persisted_verified";
        var code="ServicePersistedAndVerified";
        var message="Native service persistence and independent fresh entity readback verified; no project export is claimed.";
        if(request.Save)
        {
            try
            {
                exported=await SaveCore(pendingAccountExport);
                if(exported is {NativeSaveAccepted:true,ContainsUnsavedChanges:false,SourceVerified:true})
                { code="ServicePersistedAndExported"; message="Native service persistence, fresh entity readback and separate native project export verified."; }
                else {state="verification_failed";code="ServicePersistedProjectExportFailed";message="Service persistence verified, but the requested native project export was not verified. Do not replay the service mutation.";}
            }
            catch(Exception error) when(error is not StackOverflowException and not OutOfMemoryException)
            {
                _=error;
                state="verification_failed";code="ServicePersistedProjectExportFailed";message="Service persistence verified, but project export failed or its reviewed binding changed. Do not replay the service mutation.";
            }
        }
        var receipt=new ModuleMutationReceipt(request.OperationId,state,code,message,outcome.Document,exported,ServiceResult:service);
        receipts[request.OperationId]=(hash,receipt);
        return receipt;
    }

    internal static NativeModuleFileGroup MergeFileGroups(NativeModuleFileGroup previous,NativeModuleFileGroup current)
    {
        var present=current.RequiredFiles.Concat(current.ReferencedFiles).ToHashSet(StringComparer.Ordinal);
        var absent=previous.AbsentFiles.Concat(current.AbsentFiles).Concat(previous.RequiredFiles.Except(current.RequiredFiles,StringComparer.Ordinal))
            .Where(path=>!present.Contains(path)).Distinct(StringComparer.Ordinal).ToArray();
        var requiredDirs = current.RequiredDirectories;
        var absentDirs = previous.AbsentDirectories.Concat(current.AbsentDirectories).Concat(previous.RequiredDirectories.Except(requiredDirs, StringComparer.Ordinal))
            .Where(directory => !requiredDirs.Any(path => path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal)))
            .Distinct(StringComparer.Ordinal).ToArray();
        return new(current.Address,current.NativeModuleName,current.RequiredFiles,absent,current.ReferencedFiles,current.ExpectedJson,current.Current,current.MatchesContent,current.NativeIdentity,current.IsPresent,requiredDirs,absentDirs,current.DirectoriesRequireLocal);
    }

    internal static IReadOnlyList<NativeModuleFileGroup> ReduceFileGroups(IEnumerable<NativeModuleFileGroup> groups)
    {
        var values=groups.ToArray();
        var finalRequired=values.SelectMany(x=>x.RequiredFiles.Concat(x.ReferencedFiles)).ToHashSet(StringComparer.Ordinal);
        var finalDirectories = values.SelectMany(group => group.RequiredDirectories).ToArray();
        return values.Select(x=>new NativeModuleFileGroup(x.Address,x.NativeModuleName,x.RequiredFiles,x.AbsentFiles.Where(path=>!finalRequired.Contains(path)).ToArray(),
            x.ReferencedFiles,x.ExpectedJson,x.Current,x.MatchesContent,x.NativeIdentity,x.IsPresent,x.RequiredDirectories,
            x.AbsentDirectories.Where(directory => !finalDirectories.Any(path => path == directory || path.StartsWith(directory + "/", StringComparison.Ordinal)) &&
                !finalRequired.Any(path => path.StartsWith(directory + "/", StringComparison.Ordinal))).ToArray(),x.DirectoriesRequireLocal)).ToArray();
    }

    private void RecordFileGroup(NativeModuleFileGroup group)
    {
        if(apiTargetGroups.TryGetValue(group.NativeIdentity,out var previous)) group=MergeFileGroups(previous,group);
        apiTargetGroups[group.NativeIdentity]=group;
        foreach(var file in group.Paths) apiPendingFiles.Add(file);
    }
    private bool VerifyRegistryTarget(ModuleMutationRequest request)
    {
        var captured=registryModules.Value.GetTargetGroups(request);
        return captured.Count>0&&captured.All(group=>targetFiles.Value.Verify(FindFinalTargetGroup(group,apiTargetGroups.Values)));
    }
    private bool VerifyTypedTarget(ModuleMutationRequest request) => typedTargets.TryGetValue(request.OperationId,out var group)&&
        targetFiles.Value.Verify(FindFinalTargetGroup(group,apiTargetGroups.Values));
    private void RequirePreownedCapture(NativeApiFileCapture? capture)
    {
        if(capture?.RequiredOwnedModules is not { Count:>0 } names) return;
        var state=guard.Value.Capture();
        foreach(var name in names) guard.Value.RequireOwnedModule(state,name);
    }
    internal static void ValidateApiCapture(string ns,string method,NativeApiFileCapture? capture)
    {
        if (ns == "app.customLibrary" && method == "updateContent" && capture?.Plan is null)
            throw new BridgeException("NativeLibraryOwnerUnavailable", "The library content edit requires its native owner file group.");
        if (ns == "page" && method is "setCells" or "updateCells" or "clearCells" or "updatePageLoadCommands" && capture?.Plan is null)
            throw new BridgeException("NativeAffectedTargetsUnverified", "Page writes require the complete native page owner and its source file group before dispatch.");
        if(NativeModuleFiles.IsCommandDefinitionLifecycle(ns,method) && capture?.Plan is null)
            throw new BridgeException("NativeAffectedTargetsUnverified","Command/task/notification lifecycle changes require the complete native owner and caller plan before dispatch.");
        if(ns=="scheduledTask" && method=="deleteScheduledTask" && (capture?.Plan is null || capture.Before is null))
            throw new BridgeException("NativeAffectedTargetsUnverified", "Scheduled-task deletion requires a complete reference guard and native owner plan before dispatch.");
        if(ns is "table" or "table.view"&&capture?.Plan is null)
            throw new BridgeException("NativeAffectedTargetsUnverified","This table-definition API requires a complete reviewed native owner plan before dispatch.");
        if(ns=="page.definition"&&method is "upsertProperty" or "upsertEvent" or "upsertMethod"&&capture?.Plan is null)
            throw new BridgeException("NativeAffectedTargetsUnverified","Definition upsert requires the complete native owner and caller plan before dispatch.");
        if(capture is null&&(method.StartsWith("rename",StringComparison.Ordinal)||ns=="page"&&method=="deletePage"))
            throw new BridgeException("NativeAffectedTargetsUnverified","Native rename/deletion requires complete affected-target capture before dispatch.");
    }
    internal static NativeModuleFileGroup FindFinalTargetGroup(NativeModuleFileGroup captured,IEnumerable<NativeModuleFileGroup> journal) =>
        ReduceFileGroups(journal).SingleOrDefault(group=>ReferenceEquals(group.NativeIdentity,captured.NativeIdentity))??captured;

    private ProjectSnapshot RequireRead(ProjectTarget target)
    {
        guard.Value.CheckTarget(target);
        var state = guard.Value.Capture();
        if (state.Busy) throw new BridgeException("DesignerBusy", "Native document is opening or importing.");
        return state;
    }
    private bool UsesNativeModules(ModuleKind kind) => modules.Value.Capabilities().Any(x => x.Kind == kind && x.Available);
    private static bool IsAccount(ModuleKind kind)=>NativeApiCatalog.IdentityRoutes.ContainsKey(kind);
    private static bool IsFileDefinition(ModuleKind kind) => kind is ModuleKind.Report or ModuleKind.BusinessProcess;
    private Task<ModuleDocument> ReadCore(ModuleAddress address, CancellationToken cancellationToken) => address.Kind == ModuleKind.Report ?
        Task.FromResult(reports.Value.Read(address)) : address.Kind == ModuleKind.BusinessProcess ? Task.FromResult(processes.Value.Read(address)) :
        address.Kind == ModuleKind.DataConnection ? Task.FromResult(connections.Value.Read(address)) : address.Kind == ModuleKind.ResourceFile ? Task.FromResult(resources.Value.Read(address)) : address.Kind == ModuleKind.Plugin ? Task.FromResult(plugins.Value.Read(address)) : IsAccount(address.Kind) ? accounts.Value.ReadAsync(address,cancellationToken) : NativeRegisteredFolders.Supports(address) ? Task.FromResult(RegisteredFolders(address.FolderPath).Read(address)) : NativeDefinitionFolders.Supports(address) ? Task.FromResult(folders.Value.Read(address)) : UsesNativeModules(address.Kind) ? Task.FromResult(modules.Value.Read(address)) :
        registryModules.Value.ReadAsync(address, cancellationToken);
    private Task<T> OnUi<T>(Func<Task<T>> action, CancellationToken cancellationToken) => context.Ui.RunAsync(action, cancellationToken);
    private static void ValidateOperationId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new BridgeException("InvalidOperationId", "Operation ID must use 1-128 ASCII letters, digits, hyphens or underscores.");
    }
}
