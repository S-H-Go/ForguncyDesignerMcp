using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Module metadata for registry-backed writes is the documented native request
// object (a patch for U); R preserves the native response. The API catalog carries
// the exact request schema. Page cell/layout APIs remain independently available.
internal sealed class RegistryModules(NativeApiRegistry registry, AdapterHostContext context)
{
    private readonly Dictionary<string, ModuleAddress> createdAddresses = new(StringComparer.Ordinal);
    private readonly Lazy<NativeModuleFiles> targetFiles = new(()=>new(context.DesignerAssembly,context.NativeDocument),LazyThreadSafetyMode.PublicationOnly);
    private NativeLinkedTableCreation LinkedTables => new(context,targetFiles.Value);
    private readonly Dictionary<string,NativeModuleFileGroup> savedTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<string,IReadOnlyList<NativeModuleFileGroup>> savedAffectedTargets = new(StringComparer.Ordinal);
    private readonly Dictionary<object,NativeModuleFileGroup> latestTargets = new(ReferenceEqualityComparer.Instance);
    private static readonly HashSet<string> TablePatchMethods = new(StringComparer.Ordinal)
    { "updateTableComment", "updateTablePermission", "addColumn", "updateColumnMetadata", "addFormulaColumn", "updateFormulaColumn", "addStatisticColumn", "updateStatisticColumn", "updateAutoNumbering", "deleteColumn", "renameColumn", "setRelation", "deleteRelation" };
    internal IReadOnlyList<ModuleCapability> Capabilities() => NativeApiCatalog.Routes.SelectMany(pair =>
        Enum.GetValues<ModuleOperation>().Select(operation =>
        {
            var method = Method(pair.Value, operation);
            var available = method is not null && registry.Contains(pair.Value.Namespace, method);
            var reason = (string?)null;
            if(pair.Key is ModuleKind.Table or ModuleKind.View && operation==ModuleOperation.Create)
                reason="Native built-in creation request, or metadata { binding: { connectionId, sourceTableName, sourceSchema? } } to link an existing remote object at the root. Link import reads remote schema and does not request CREATE/DROP; physical external schema flags are unavailable.";
            if (pair.Key == ModuleKind.Table && operation == ModuleOperation.Update)
            { available = true; reason = "Metadata.operations is an ordered array of native patches: "+string.Join(", ",TablePatchMethods.Order(StringComparer.Ordinal))+". Reference-changing operations capture every native affected owner; column deletion vetoes incoming references. Read-only external bindings allow project comments/permissions; remote column/schema changes are excluded."; }
            if(pair.Key is ModuleKind.ServerCommand or ModuleKind.SharedCommand && operation==ModuleOperation.Update)
                reason="Native request patch. Parameter/return structure changes require a complete native scan proving no incoming callers; referenced commands retain their compatible signature. Native parsing validates detached metadata and protects retained test data; command-bearing or foreign metadata is rejected before dispatch.";
            if(pair.Key==ModuleKind.Notification&&operation==ModuleOperation.Update)
                reason="Native request patch. Parameter changes validate detached native metadata and capture the definition plus every native caller owner before checkout and dispatch. Notifications are not sent.";
            if (pair.Key == ModuleKind.ScheduledTask && operation == ModuleOperation.Delete)
            { reason = "Task definition deletion preserves the native enabled-state semantics and requires a complete incoming-reference veto and exact source-file tombstone. No task execution is requested; live Designer save remains pending."; }
            if(pair.Key is ModuleKind.Table or ModuleKind.View && operation==ModuleOperation.Delete)
                reason="Native definition deletion with complete incoming-reference veto, affected owner capture and source tombstones. Read-only external bindings require AllowModifySchema=false. System/history views and implicit companion deletion are excluded; application side-effect owners require pre-existing current-user checkout.";
            if(pair.Key is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage && operation==ModuleOperation.Delete)
                reason="Native deletion captures every resolved owner, including child templates and affected application/publish settings. Incomplete or impure reference context is rejected before dispatch.";
            if (method is null && reason is null) reason = "No complete native request route has been adapted for this module operation.";
            var scope = pair.Value.Namespace.StartsWith("userManagement", StringComparison.Ordinal) ? EffectScope.AccountService : EffectScope.ProjectDefinition;
            return new ModuleCapability(pair.Key, operation, available, available ? "implemented-awaiting-live-designer" : "not-implemented", reason, scope);
        })).ToArray();

    internal async Task<ModulePage> ListAsync(ModuleQuery query, CancellationToken cancellationToken)
    {
        var route = Route(query.Kind, ModuleOperation.List);
        if (query.Limit is < 1 or > 1000) throw new BridgeException("InvalidPageSize", "Module page size must be between 1 and 1000.");
        if (NativeApiCatalog.IdentityRoutes.ContainsKey(query.Kind) && query.FolderPath is not null) throw new BridgeException("AccountFolderFilterUnavailable", "Account hierarchy is represented by native immutable parent IDs, not project folder paths.");
        var nativeOffset = query.Cursor is null ? 0 : int.TryParse(query.Cursor, out var parsedOffset) && parsedOffset >= 0 ? parsedOffset : throw new BridgeException("InvalidCursor", "Invalid module cursor.");
        var listParameters = query.Kind == ModuleKind.User ? JsonSerializer.SerializeToElement(new { offset = nativeOffset, limit = query.Limit }) : JsonSerializer.SerializeToElement(new { });
        var result = await registry.InvokeAsync(route.Namespace, route.List, listParameters, cancellationToken);
        var items = Rows(result, route).Where(x => MatchesKind(query.Kind, x) && IsSupportedRow(query.Kind, x))
            .Select(x => (Row: x, Address: Address(query.Kind, x, route)))
            .Where(x => query.FolderPath is null || x.Address.FolderPath == query.FolderPath)
            .OrderBy(x => x.Address.FolderPath, StringComparer.Ordinal).ThenBy(x => x.Address.Name, StringComparer.Ordinal).ToArray();
        if (items.Select(x => x.Address).Distinct().Count() != items.Length) throw new BridgeException("DuplicateNativeModule", "Native list contains ambiguous module addresses.");
        var offset = query.Kind == ModuleKind.User ? 0 : nativeOffset;
        var snapshot = context.CaptureSnapshot();
        if (query.Kind == ModuleKind.User)
        {
            if (!result.TryGetProperty("total", out var total) || !total.TryGetInt32(out var totalCount)) throw new BridgeException("NativeUserPagingUnavailable", "Native user list must expose total for exact pagination.");
            return new(items.Select(x => new ModuleSummary(x.Address, Wire.Hash(snapshot.Revision + "\n" + x.Row.GetRawText()))).ToArray(),
                nativeOffset + items.Length < totalCount ? (nativeOffset + items.Length).ToString() : null);
        }
        return new(items.Skip(offset).Take(query.Limit).Select(x => new ModuleSummary(x.Address, Wire.Hash(snapshot.Revision + "\n" + x.Row.GetRawText()))).ToArray(),
            offset + query.Limit < items.Length ? (offset + query.Limit).ToString() : null);
    }

    internal async Task<ModuleDocument> ReadAsync(ModuleAddress address, CancellationToken cancellationToken)
    {
        ValidateAddress(address);
        var route = Route(address.Kind, ModuleOperation.Read);
        JsonElement result;
        var readMetadata = await ReadIdentityMetadata(address, route, cancellationToken);
        if (route.Read is not null)
            result = await registry.InvokeAsync(route.Namespace, route.Read, Parameters(route, address, readMetadata, ModuleOperation.Read), cancellationToken);
        else
        {
            var list = await registry.InvokeAsync(route.Namespace, route.List, JsonSerializer.SerializeToElement(new { }), cancellationToken);
            var matches = Rows(list, route).Where(x => MatchesKind(address.Kind, x) && Address(address.Kind, x, route) == address).ToArray();
            if (matches.Length != 1) throw new BridgeException("NativeModuleNotUnique", "Native module address does not resolve to one entry.");
            result = matches[0].Clone();
        }
        ValidateReadIdentity(address, result);
        if (address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage && !MatchesKind(address.Kind, result))
            throw new BridgeException("NativePageKindMismatch", "The native page exists under a different page kind.");
        var snapshot = context.CaptureSnapshot();
        IReadOnlyList<CompanionAsset> companions = [];
        var binaryHash = "";
        if (address.Kind == ModuleKind.Image)
        {
            var verifier = new NativeSaveVerification(context);
            var imageManager = NativeReflection.Static(context.DesignerAssembly.GetType("Forguncy.Common.ImageManager", true)!, "get_Instance")!;
            var nativeRoot = (string)NativeReflection.Get(imageManager,"LocalImagesPath")!;
            var local = (string)NativeReflection.Get(verifier.SaveFile, "LocalPath")!;
            if (!string.Equals(Path.GetFullPath(nativeRoot).TrimEnd('\\','/'), Path.GetFullPath(Path.Combine(local,"image")).TrimEnd('\\','/'), StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("ImageCacheMismatch", "Native image directory does not belong to this bound document cache.");
            if(NativeReflection.Get(imageManager,"ImageNameToFullNameCache") is not System.Collections.IDictionary names || names[address.Name] is not string nativeFile)
                throw new BridgeException("ImageRegistrationMissing","Native image-name registry does not resolve the requested uploaded image.");
            var relative=Path.GetRelativePath(nativeRoot,nativeFile).Replace('\\','/');
            var path=NativeSaveVerification.Under(nativeRoot,relative);
            if(Path.GetFileName(path)!=address.Name) throw new BridgeException("ImageRegistrationMismatch","Native image name and registered file disagree.");
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length > NativeFiles.MaximumAssetBytes) throw new BridgeException("ImageTooLarge", "Image exceeds the adapter binary limit.");
            binaryHash = NativeFiles.Hash(bytes);
            companions = [new("image/" + relative, Convert.ToBase64String(bytes), binaryHash)];
        }
        return new(address, result, companions, Wire.Hash(snapshot.Revision + "\n" + result.GetRawText() + "\n" + binaryHash));
    }

    internal ReferenceResult References(ModuleAddress address) => new([], false,
        "Native per-module reference APIs are not yet normalized. Supported deletion routes still run their own native reference guards.");

    internal Task<ModuleValidationResult> ValidateAsync(ModuleMutationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ValidateAddress(request.Address);
            if(NativeLinkedTableCreation.Handles(request))
            { _=LinkedTables.Prepare(request); return Task.FromResult(new ModuleValidationResult(true,[])); }
            var route = Route(request.Address.Kind, request.Operation);
            if(request.Address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage && request.Operation==ModuleOperation.Delete && targetFiles.Value.CaptureMutation(request) is null)
                throw new BridgeException("NativeAffectedTargetsUnverified","Page deletion requires a complete native affected-target capture before dispatch.");
            var expectedScope = NativeApiCatalog.IdentityRoutes.ContainsKey(request.Address.Kind) ? EffectScope.AccountService : EffectScope.ProjectDefinition;
            if (request.Scope != expectedScope) throw new BridgeException("EffectScopeDenied", "Mutation scope differs from this module's actual native effect domain.");
            if (request.Operation != ModuleOperation.Delete && request.Document is null) throw new BridgeException("ModuleDocumentRequired", "Create/update needs a native request document.");
            if (request.Document?.Companions.Count > 0 && request.Address.Kind != ModuleKind.Image) throw new BridgeException("CompanionRouteUnavailable", "This native request route does not support client supplied companion paths.");
            if (request.Address.Kind == ModuleKind.Table && request.Operation == ModuleOperation.Update)
            {
                foreach (var call in TableCalls(request))
                {
                    registry.ValidateRequestParameters("table", call.Method, call.Parameters);
                    RequireTablePlan("table",call.Method,call.Parameters);
                }
                return Task.FromResult(new ModuleValidationResult(true, []));
            }
            var parameters = Parameters(route, request.Address, WriteMetadata(request), request.Operation);
            if (request.Address.Kind == ModuleKind.PermissionGroup && request.Operation == ModuleOperation.Delete)
                NativePermissionGroupGuard.ValidateDelete(context.DesignerAssembly, NativeReflection.Get(context.NativeDocument, "ForguncySettings")!, parameters);
            var method = Method(route, request.Operation)!;
            if(request.Address.Kind == ModuleKind.ScheduledTask && request.Operation == ModuleOperation.Delete)
                _ = RequireScheduledTaskPlan(parameters);
            registry.ValidateRequestParameters(route.Namespace, method, parameters);
            if(NativeModuleFiles.IsCommandDefinitionLifecycle(route.Namespace,method))
                _=RequireCommandDefinitionPlan(route.Namespace,method,parameters);
            if(request.Address.Kind is ModuleKind.Table or ModuleKind.View && request.Operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)
                RequireTablePlan(route.Namespace,method,parameters);
            return Task.FromResult(new ModuleValidationResult(true, []));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        { return Task.FromResult(new ModuleValidationResult(false, [new(error is BridgeException b ? b.Code : "NativeRequestValidation", error.Message)])); }
    }

    internal async Task<IReadOnlyList<string>> ApplyAsync(ModuleMutationRequest request, CancellationToken cancellationToken)
    {
        if(NativeLinkedTableCreation.Handles(request))
        {
            var linkedGroups=await LinkedTables.ApplyAsync(request,cancellationToken);
            RecordTargets(request,linkedGroups,null);
            return linkedGroups.SelectMany(g=>g.Paths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        var route = Route(request.Address.Kind, request.Operation);
        var parameters = request.Address.Kind == ModuleKind.Table && request.Operation == ModuleOperation.Update ? default : Parameters(route, request.Address, WriteMetadata(request), request.Operation);
        if (request.Address.Kind == ModuleKind.PermissionGroup && request.Operation == ModuleOperation.Delete)
            NativePermissionGroupGuard.ValidateDelete(context.DesignerAssembly, NativeReflection.Get(context.NativeDocument, "ForguncySettings")!, parameters);
        var before=request.Operation==ModuleOperation.Create?null:targetFiles.Value.Resolve(request.Address);
        var capture=request.Address.Kind == ModuleKind.ScheduledTask && request.Operation == ModuleOperation.Delete
            ? RequireScheduledTaskPlan(parameters) : request.Address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage && request.Operation==ModuleOperation.Delete
            ? targetFiles.Value.CaptureMutation(request) : request.Address.Kind is ModuleKind.Table or ModuleKind.View && request.Operation is ModuleOperation.Create or ModuleOperation.Delete || request.Address.Kind==ModuleKind.View&&request.Operation==ModuleOperation.Update
                ? RequireTablePlan(route.Namespace,Method(route,request.Operation)!,parameters) : null;
        if(NativeModuleFiles.IsCommandDefinitionLifecycle(route.Namespace,Method(route,request.Operation)!))
            capture=RequireCommandDefinitionPlan(route.Namespace,Method(route,request.Operation)!,parameters);
        if(request.Address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage && request.Operation==ModuleOperation.Delete && capture is null)
            throw new BridgeException("NativeAffectedTargetsUnverified","Page deletion requires a complete native affected-target capture before dispatch.");
        JsonElement result;
        if (request.Address.Kind == ModuleKind.Table && request.Operation == ModuleOperation.Update)
        {
            foreach (var call in TableCalls(request))
            {
                var callCapture=RequireTablePlan("table",call.Method,call.Parameters);
                var callResult=await registry.InvokeAsync("table",call.Method,call.Parameters,cancellationToken);
                RequireAccepted(callResult);
                var affected=targetFiles.Value.ResolveApiGroupsAfter(callCapture)??throw new BridgeException("NativeAffectedTargetsReadbackFailed","Native table mutation completed but its complete affected owners could not be read back; do not replay it.");
                RecordTargets(request,affected,before);
            }
            result = JsonSerializer.SerializeToElement(new { });
        }
        else result = await registry.InvokeAsync(route.Namespace, Method(route, request.Operation)!, parameters, cancellationToken);
        RequireAccepted(result);
        if (request.Operation == ModuleOperation.Create) RecordCreatedAddress(request, result);
        if (request.Operation == ModuleOperation.Delete)
        {
            var offset=0;
            while(true)
            {
                var parametersForList=request.Address.Kind==ModuleKind.User ? JsonSerializer.SerializeToElement(new {keyword=request.Address.Name,offset,limit=1000}) : JsonSerializer.SerializeToElement(new { });
                var list = await registry.InvokeAsync(route.Namespace, route.List, parametersForList, CancellationToken.None);
                var rows=Rows(list,route).ToArray();
                if (rows.Any(x => MatchesKind(request.Address.Kind, x) && Address(request.Address.Kind, x, route) == request.Address))
                    throw new BridgeException("NativeDeleteReadbackFailed", "The deleted module remains in the native definition registry.");
                if(request.Address.Kind!=ModuleKind.User) break;
                if(!list.TryGetProperty("total",out var total)||!total.TryGetInt32(out var count)) throw new BridgeException("NativeUserPagingUnavailable","Service deletion readback requires native total count.");
                offset+=rows.Length;
                if(offset>=count) break;
                if(rows.Length==0||offset>1000000) throw new BridgeException("NativeUserPagingIncomplete","Service deletion readback did not complete the native user search.");
            }
        }
        // Native permission/checkout/dirty handling occurs inside aao. Do not call
        // MethodInfo or edit .collaboration to imitate that transaction.
        if(!(request.Address.Kind==ModuleKind.Table&&request.Operation==ModuleOperation.Update))
        {
            var groups=capture is null ? targetFiles.Value.ResolveAfter(request,before) is { } single ? new[]{single} : null : targetFiles.Value.ResolveApiGroupsAfter(capture);
            if(capture is not null&&groups is null) throw new BridgeException("NativeAffectedTargetsReadbackFailed","Native mutation completed but its complete affected owners could not be read back; do not replay it.");
            if(groups is not null) RecordTargets(request,groups,before);
        }
        return GetTargetGroups(request).SelectMany(group=>group.Paths).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    internal ModuleAddress ResultAddress(ModuleMutationRequest request) => createdAddresses.GetValueOrDefault(request.OperationId, request.Address);
    internal NativeApiFileCapture? CaptureLifecycle(ModuleMutationRequest request)
    {
        if(NativeLinkedTableCreation.Handles(request)) return LinkedTables.Capture(request);
        if(request.Address.Kind is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.Table or ModuleKind.View or ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification) ||
            request.Operation is not (ModuleOperation.Create or ModuleOperation.Delete)) return null;
        var route=Route(request.Address.Kind,request.Operation);
        var parameters=Parameters(route,request.Address,WriteMetadata(request),request.Operation);
        return targetFiles.Value.CaptureApiBefore(route.Namespace,Method(route,request.Operation)!,parameters);
    }
    internal NativeModuleFilePlan? CaptureExistingUpdatePlan(ModuleMutationRequest request)
    {
        if(request.Operation!=ModuleOperation.Update) return null;
        if(request.Address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage)
        {
            var route=Route(request.Address.Kind,request.Operation);
            var capture=targetFiles.Value.CaptureApiBefore(route.Namespace,route.Update!,Parameters(route,request.Address,WriteMetadata(request),request.Operation));
            return capture is { Operation: ModuleOperation.Update, Before: not null } ? new NativeModuleFilePlan([capture.Before],[]) : null;
        }
        if(request.Address.Kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification)
        {
            var route=Route(request.Address.Kind,request.Operation);
            var capture=targetFiles.Value.CaptureApiBefore(route.Namespace,route.Update!,Parameters(route,request.Address,WriteMetadata(request),request.Operation));
            return capture?.Plan is {ExpectedNewModules.Count:0} plan?plan:
                capture is {Operation:ModuleOperation.Update,Before:not null}?new NativeModuleFilePlan([capture.Before],[]):null;
        }
        var calls=request.Address.Kind==ModuleKind.Table ? TableCalls(request).ToArray() : request.Address.Kind==ModuleKind.View ?
            new[]{("update",Parameters(Route(request.Address.Kind,request.Operation),request.Address,WriteMetadata(request),request.Operation))} : [];
        if(calls.Length==0) return null;
        var groups=new Dictionary<object,NativeModuleFileGroup>(ReferenceEqualityComparer.Instance);
        foreach(var call in calls)
        {
            var plan=targetFiles.Value.CaptureApiBefore(request.Address.Kind==ModuleKind.View?"table.view":"table",call.Item1,call.Item2)?.Plan;
            if(plan is null||plan.ExpectedNewModuleNames.Count!=0) return null;
            foreach(var group in plan.BeforeGroups) groups[group.NativeIdentity]=group;
        }
        return groups.Count==0?null:new NativeModuleFilePlan(groups.Values.ToArray(),[]);
    }
    private NativeApiFileCapture RequireScheduledTaskPlan(JsonElement parameters) =>
        targetFiles.Value.CaptureApiBefore("scheduledTask", "deleteScheduledTask", parameters) is { Plan: not null, Before: not null } capture ? capture :
        throw new BridgeException("NativeAffectedTargetsUnverified", "Scheduled-task deletion requires a complete reference guard and native owner plan.");

    private NativeApiFileCapture RequireCommandDefinitionPlan(string ns,string method,JsonElement parameters) =>
        targetFiles.Value.CaptureApiBefore(ns,method,parameters) is {Plan:not null} capture?capture:
        throw new BridgeException("NativeAffectedTargetsUnverified","Command, task and notification lifecycle changes require their complete native owner plan before dispatch.");

    private NativeApiFileCapture RequireTablePlan(string ns,string method,JsonElement parameters) => targetFiles.Value.CaptureApiBefore(ns,method,parameters) is {Plan:not null} capture ? capture :
        throw new BridgeException("NativeAffectedTargetsUnverified","Table creation/update/deletion requires the complete native shared-database and related owner plan before dispatch.");
    private static void RequireAccepted(JsonElement result)
    {
        if(result.ValueKind==JsonValueKind.Object&&result.TryGetProperty("success",out var success)&&success.ValueKind==JsonValueKind.False)
            throw new BridgeException("NativeOperationRejected",Text(result,"message")??"Native operation returned success=false.");
    }
    private void RecordTargets(ModuleMutationRequest request,IEnumerable<NativeModuleFileGroup> groups,NativeModuleFileGroup? before)
    {
        var recorded=savedAffectedTargets.TryGetValue(request.OperationId,out var prior)?prior.ToDictionary(group=>group.NativeIdentity,ReferenceEqualityComparer.Instance):new Dictionary<object,NativeModuleFileGroup>(ReferenceEqualityComparer.Instance);
        foreach(var captured in groups)
        {
            var group=latestTargets.TryGetValue(captured.NativeIdentity,out var previous)?V12ProjectAdapter.MergeFileGroups(previous,captured):captured;
            latestTargets[group.NativeIdentity]=group;
            recorded[group.NativeIdentity]=group;
        }
        savedAffectedTargets[request.OperationId]=recorded.Values.ToArray();
        var primary=recorded.Values.SingleOrDefault(group=>before is not null&&ReferenceEquals(group.NativeIdentity,before.NativeIdentity))??
            recorded.Values.SingleOrDefault(group=>group.Address.Kind==request.Address.Kind&&group.Address.Name==request.Address.Name);
        if(primary is not null) savedTargets[request.OperationId]=primary;
    }
    internal NativeModuleFileGroup? GetTargetGroup(ModuleMutationRequest request) => savedTargets.TryGetValue(request.OperationId,out var group)?latestTargets.GetValueOrDefault(group.NativeIdentity,group):null;
    internal IReadOnlyList<NativeModuleFileGroup> GetTargetGroups(ModuleMutationRequest request) => savedAffectedTargets.TryGetValue(request.OperationId,out var groups)?groups.Select(group=>latestTargets.GetValueOrDefault(group.NativeIdentity,group)).ToArray():[];
    internal bool VerifySource(ModuleMutationRequest request,NativeModuleFileGroup? finalGroup=null) => (finalGroup??GetTargetGroup(request)) is { } group&&targetFiles.Value.Verify(group);

    internal static void ValidateApiAddressGuard(string ns,string method,JsonElement parameters,ModuleAddress address)
    {
        string? primary=null;
        if (address == NativeModuleFiles.ApplicationRoot && NativeModuleFiles.IsGlobalVariableEdit(ns,method)) return;
        if(address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage && (ns=="page" || ns.StartsWith("page.",StringComparison.Ordinal)))
            primary=ns=="page"&&method=="createPage"?"name":"pageName";
        else if(NativeApiCatalog.Routes.TryGetValue(address.Kind,out var route)&&route.Namespace==ns)
        {
            primary=method==route.Create?"name":route.NameParameter;
            if (address.Kind == ModuleKind.CustomLibrary && method == "updateContent" &&
                (!parameters.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
                 !string.Equals(type.GetString(), address.FolderPath, StringComparison.OrdinalIgnoreCase)))
                throw new BridgeException("ApiModuleGuardMismatch", "The library type differs from the guarded module address.");
            if(address.Kind==ModuleKind.Image&&method=="upload") primary="fileName";
            if(address.Kind==ModuleKind.Table&&method is "setRelation" or "deleteRelation") primary="foreignTable";
            if(NativeApiCatalog.IdentityRoutes.TryGetValue(address.Kind,out var identity))
            {
                if(method==route.Delete)
                {
                    if(!parameters.TryGetProperty(identity.DeleteArrayParameter,out var ids)||ids.ValueKind!=JsonValueKind.Array||ids.GetArrayLength()!=1||ids[0].ToString()!=address.Name)
                        throw new BridgeException("ApiModuleGuardMismatch","An account API module guard must match exactly its guarded immutable identity.");
                    return;
                }
                primary=method==route.Create?identity.CreateNameParameter:identity.IdentityParameter;
            }
        }
        if(primary is null || !parameters.TryGetProperty(primary,out var value) || value.ToString()!=address.Name)
            throw new BridgeException("ApiModuleGuardMismatch","Native API target parameters do not match the optional guarded module address.");
    }

    private NativeModuleApiRoute Route(ModuleKind kind, ModuleOperation operation)
    {
        if (!NativeApiCatalog.Routes.TryGetValue(kind, out var route) ||
            !Capabilities().Any(x => x.Kind == kind && x.Operation == operation && x.Available))
            throw new BridgeException("ModuleOperationNotImplemented", $"The V12 adapter has no enabled {operation} implementation for {kind}.");
        return route;
    }
    private static string? Method(NativeModuleApiRoute route, ModuleOperation operation) => operation switch
    { ModuleOperation.List => route.List, ModuleOperation.Read => route.Read ?? route.List, ModuleOperation.Create => route.Create, ModuleOperation.Update => route.Update, ModuleOperation.Delete => route.Delete, _ => null };

    internal static JsonElement Parameters(NativeModuleApiRoute route, ModuleAddress address, JsonElement? metadata, ModuleOperation operation)
    {
        var parameters = metadata is { ValueKind: JsonValueKind.Object } json ? JsonNode.Parse(json.GetRawText())!.AsObject() : new JsonObject();
        if (metadata is not null && metadata.Value.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidModuleMetadata", "Native API request metadata must be an object.");
        var primary = operation == ModuleOperation.Create ? "name" : route.NameParameter;
        NativeApiCatalog.IdentityRoutes.TryGetValue(address.Kind, out var identity);
        if (identity is not null) primary = operation == ModuleOperation.Create ? identity.CreateNameParameter : identity.IdentityParameter;
        if (address.Kind == ModuleKind.Image && operation is ModuleOperation.Create or ModuleOperation.Update) primary = "fileName";
        if (parameters.TryGetPropertyValue(primary, out var name) && name?.ToString() != address.Name)
            throw new BridgeException("NativeAddressMismatch", "Native request name differs from the target module address.");
        if (identity is { NumericIdentity: true } && operation != ModuleOperation.Create)
        {
            if (!int.TryParse(address.Name, out var nativeId) || nativeId <= 0) throw new BridgeException("InvalidServiceIdentity", "Native account identity must be a positive integer.");
            parameters[primary] = nativeId;
        }
        else parameters[primary] = address.Name;
        if (identity is not null && operation == ModuleOperation.Delete)
        {
            parameters.Remove(primary);
            parameters[identity.DeleteArrayParameter] = identity.NumericIdentity ? new JsonArray(JsonValue.Create(int.Parse(address.Name))) : new JsonArray(JsonValue.Create(address.Name));
        }
        if (parameters.TryGetPropertyValue("newName", out var renamed) && renamed?.GetValue<string>() != address.Name)
            throw new BridgeException("NativeRenameRequiresAddress", "Rename is not part of an in-place module patch.");
        if (address.Kind == ModuleKind.CustomLibrary)
        {
            var type = address.FolderPath;
            if (type is not ("javascript" or "css")) throw new BridgeException("LibraryTypeRequired", "Local library address FolderPath must be javascript or css.");
            if (parameters.TryGetPropertyValue("type", out var specifiedType) && specifiedType?.ToString() != type) throw new BridgeException("LibraryTypeMismatch", "Library type differs from its address.");
            parameters["type"] = type;
            if (operation == ModuleOperation.Delete) parameters["kind"] = "local";
        }
        else if (address.Kind == ModuleKind.Image)
        {
            if (operation is ModuleOperation.Create or ModuleOperation.Update)
            {
                if (parameters.TryGetPropertyValue("folder", out var folder) && folder?.ToString() != (address.FolderPath ?? "")) throw new BridgeException("NativeFolderMismatch", "Image folder differs from its native address.");
                parameters["folder"] = address.FolderPath ?? "";
            }
        }
        else if (operation == ModuleOperation.Create && identity is null)
        {
            if (parameters.TryGetPropertyValue("folderPath", out var folder) && folder?.GetValue<string>() != (address.FolderPath ?? ""))
                throw new BridgeException("NativeFolderMismatch", "Native request folder differs from the module address.");
            if (address.FolderPath is not null) parameters["folderPath"] = address.FolderPath;
        }
        if (address.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage && operation == ModuleOperation.Create)
        {
            var type = address.Kind switch { ModuleKind.Page => "Normal", ModuleKind.MasterPage => "Master", _ => "UserControl" };
            if (parameters.TryGetPropertyValue("type", out var pageType) && !string.Equals(pageType?.GetValue<string>(), type, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("PageKindMismatch", "Native page type differs from the module kind.");
            parameters["type"] = type;
        }
        if (address.Kind == ModuleKind.View && operation == ModuleOperation.Delete) parameters["deleteDatabaseObject"] = false;
        if (address.Kind == ModuleKind.Image && operation is ModuleOperation.Create or ModuleOperation.Update) parameters["overwrite"] = operation == ModuleOperation.Update;
        return JsonSerializer.SerializeToElement(parameters);
    }

    private Task<JsonElement?> ReadIdentityMetadata(ModuleAddress address, NativeModuleApiRoute route, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<JsonElement?>(address.Kind == ModuleKind.CustomLibrary ? JsonSerializer.SerializeToElement(new { type = address.FolderPath }) : null);
    }

    private static JsonElement? WriteMetadata(ModuleMutationRequest request)
    {
        if (request.Address.Kind != ModuleKind.Image || request.Document?.Companions.Count is not > 0) return request.Document?.Metadata;
        if (request.Document.Companions.Count != 1) throw new BridgeException("InvalidImageAsset", "One image binary is required.");
        var asset = request.Document.Companions[0];
        var bytes = Convert.FromBase64String(asset.ContentBase64);
        var expectedPath="image/"+(request.Address.FolderPath is { Length: >0 } imageFolder ? imageFolder+"/" : "")+request.Address.Name;
        if (asset.RelativePath != expectedPath || bytes.Length > NativeFiles.MaximumAssetBytes || NativeFiles.Hash(bytes) != asset.Sha256)
            throw new BridgeException("InvalidImageAsset", "Image binary address, size or hash is invalid.");
        var metadata = JsonNode.Parse(request.Document.Metadata.GetRawText())!.AsObject();
        metadata["base64"] = asset.ContentBase64;
        return JsonSerializer.SerializeToElement(metadata);
    }

    private IEnumerable<(string Method, JsonElement Parameters)> TableCalls(ModuleMutationRequest request)
    {
        var metadata = request.Document?.Metadata ?? default;
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > 100)
            throw new BridgeException("TablePatchesRequired", "Table update metadata.operations must contain 1-100 approved table-definition patches.");
        foreach (var operation in operations.EnumerateArray())
        {
            var method = Text(operation,"method") ?? throw new BridgeException("TablePatchMethodRequired", "Table patch needs its registered method.");
            if (!TablePatchMethods.Contains(method) || !registry.Contains("table",method)) throw new BridgeException("TablePatchNotAllowed", "Table patch method is not an approved native definition operation: " + method);
            if (!operation.TryGetProperty("parameters",out var input) || input.ValueKind != JsonValueKind.Object) throw new BridgeException("TablePatchParametersRequired", "Table patch needs its native parameters object.");
            var parameters = JsonNode.Parse(input.GetRawText())!.AsObject();
            // Relation requests guard their foreign table; the native impact
            // plan also includes the primary table and shared database owners.
            var targetField = method is "setRelation" or "deleteRelation" ? "foreignTable" : "tableName";
            if (parameters.TryGetPropertyValue(targetField,out var targetName) && targetName?.ToString() != request.Address.Name) throw new BridgeException("TablePatchTargetMismatch", "A table patch must target the guarded table definition.");
            parameters[targetField] = request.Address.Name;
            yield return (method,JsonSerializer.SerializeToElement(parameters));
        }
    }

    private void RecordCreatedAddress(ModuleMutationRequest request, JsonElement result)
    {
        if(request.Address.Kind==ModuleKind.CustomLibrary)
        {
            if(!result.TryGetProperty("item",out var item) || Text(item,"name") is not {Length:>0} name || Text(item,"type") is not {Length:>0} type)
                throw new BridgeException("CreatedLibraryIdentityUnavailable","Native library creation must return its actual normalized filename and type.");
            createdAddresses[request.OperationId]=new(ModuleKind.CustomLibrary,name,type);
            return;
        }
        if (request.Address.Kind == ModuleKind.PermissionGroup)
        {
            if (!result.TryGetProperty("affectedPermissionGroupIds",out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() != 1 || ids[0].ValueKind != JsonValueKind.String)
                throw new BridgeException("CreatedPermissionIdentityUnavailable", "Native permission-group creation must return exactly one generated identity.");
            createdAddresses[request.OperationId] = request.Address with { Name = ids[0].GetString()! };
        }
        else if (NativeApiCatalog.IdentityRoutes.TryGetValue(request.Address.Kind,out var route) && route.CreatedIdentityProperty is { } property)
        {
            var id = Scalar(result,property) ?? throw new BridgeException("CreatedServiceIdentityUnavailable", "Native account creation did not return its immutable identity.");
            createdAddresses[request.OperationId] = request.Address with { Name = id };
        }
    }

    private static bool IsSupportedRow(ModuleKind kind, JsonElement row) => kind != ModuleKind.CustomLibrary || Text(row,"kind") == "local";

    private static IEnumerable<JsonElement> Rows(JsonElement value, NativeModuleApiRoute route)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Select(x => x.Clone()).ToArray();
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (route.ListContainer is not null && value.TryGetProperty(route.ListContainer, out var container) && container.ValueKind == JsonValueKind.Array)
                return container.EnumerateArray().Select(x => x.Clone()).ToArray();
            if (value.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array) return items.EnumerateArray().Select(x => x.Clone()).ToArray();
        }
        throw new BridgeException("NativeListShapeUnverified", "Native list output does not match its validated array/container shape.");
    }
    private static ModuleAddress Address(ModuleKind kind, JsonElement item, NativeModuleApiRoute route)
    {
        if (item.ValueKind == JsonValueKind.String) return new(kind, item.GetString()!);
        var identityProperty = NativeApiCatalog.IdentityRoutes.TryGetValue(kind,out var identity) ? identity.ListIdentityProperty :
            kind is ModuleKind.PermissionGroup or ModuleKind.DataConnection ? "id" : route.NameParameter;
        var name = Scalar(item, identityProperty) ?? Text(item, "name") ?? Scalar(item, "id") ??
            throw new BridgeException("NativeListAddressUnavailable", "Native list item has no registered module identity field.");
        var folderName = kind == ModuleKind.CustomLibrary ? "type" : kind == ModuleKind.Image ? "folder" : "folderPath";
        return new(kind, name, Text(item, folderName) is { Length: > 0 } folder ? folder : null);
    }
    private static bool MatchesKind(ModuleKind kind, JsonElement item)
    {
        if (kind is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage)) return true;
        var type = Text(item, "type") ?? Text(item, "pageType");
        if (type is null) throw new BridgeException("NativePageKindUnavailable", "Native page list must identify its exact page kind.");
        return kind switch { ModuleKind.Page => type.Equals("Normal", StringComparison.OrdinalIgnoreCase), ModuleKind.MasterPage => type.Equals("Master", StringComparison.OrdinalIgnoreCase), _ => type.Equals("UserControl", StringComparison.OrdinalIgnoreCase) };
    }
    private static void ValidateReadIdentity(ModuleAddress address, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        if (address.Kind == ModuleKind.PermissionGroup)
        {
            var id = Scalar(value, "id");
            if (!string.Equals(id, address.Name, StringComparison.Ordinal))
                throw new BridgeException("NativeReadAddressMismatch", "The native permission group has a different immutable identity.");
            return;
        }
        if (address.Kind == ModuleKind.Role && value.TryGetProperty("role", out var role)) value = role;
        if (address.Kind == ModuleKind.Organization && value.TryGetProperty("node", out var node)) value = node;
        if (NativeApiCatalog.IdentityRoutes.TryGetValue(address.Kind, out var identity))
        {
            var serviceKey = Scalar(value, identity.ListIdentityProperty) ?? Scalar(value, identity.IdentityParameter);
            if (serviceKey is not null && serviceKey != address.Name) throw new BridgeException("NativeReadAddressMismatch", "Native service read returned a different immutable identity.");
            return;
        }
        var name = Text(value, "name") ?? Text(value, "pageName") ?? Text(value, "tableName") ?? Text(value, "viewName");
        if (name is not null && name != address.Name) throw new BridgeException("NativeReadAddressMismatch", "Native read returned a different module name.");
    }
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
    private static string? Scalar(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name,out var x) && x.ValueKind is JsonValueKind.String or JsonValueKind.Number ? x.ToString() : null;
    private static void ValidateAddress(ModuleAddress address)
    {
        if (string.IsNullOrWhiteSpace(address.Name) || address.Name.Contains('\0') || address.Name.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new BridgeException("InvalidModuleName", "Module name must not contain path components.");
        if (address.FolderPath is not null && (address.FolderPath.Contains(':') || address.FolderPath.Replace('\\','/').Split('/').Any(x => x is "" or "." or "..")))
            throw new BridgeException("InvalidModuleFolder", "Module folder must be a canonical relative folder.");
        if (NativeApiCatalog.IdentityRoutes.ContainsKey(address.Kind) && address.FolderPath is not null)
            throw new BridgeException("AccountFolderUnavailable", "Account module addresses use immutable service IDs without project folder paths.");
    }
}
