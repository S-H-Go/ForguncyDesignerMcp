using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>
/// Read-only native shared-SQLite definition impact plan. No EnsureCanExecute,
/// checkout, SQL, connection, generation, save or dirty operation is called.
/// A plan identifies affected native models; consumers still need fresh native
/// file-group tokens after mutation, including a newly created target token.
/// </summary>
public sealed partial class NativeTableDefinitionEffects
{
    private const string NativeVersion = "12.0.102.0";
    private const string NativeDesignerSha256 = "A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246";
    private readonly Assembly assembly;
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly HashSet<string> SafeUpdates = new(StringComparer.Ordinal)
    { "updateTableComment", "updateTablePermission", "addColumn", "updateColumnMetadata", "addFormulaColumn", "updateFormulaColumn", "addStatisticColumn", "updateStatisticColumn", "updateAutoNumbering" };
    public string? UnavailableReason { get; private set; }

    public NativeTableDefinitionEffects(Assembly assembly)
    {
        this.assembly = assembly;
        if (assembly.GetName().Version?.ToString() != NativeVersion ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != NativeDesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild", "Table definition effects require the exact verified native V12 build.");
    }

    public NativeTableDefinitionEffectPlan? TryResolve(string ns, string method, JsonElement parameters, object nativeSettings)
    {
        try { var plan = Resolve(ns, method, parameters, nativeSettings); UnavailableReason = null; return plan; }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { UnavailableReason = error is BridgeException bridge ? bridge.Message : "The native table effect context or required service is unavailable."; return null; }
    }

    /// <summary>
    /// Captures the exact native deletion resolver, including incoming owners.
    /// The host must pass its genuine pure-reference-context guard; it runs
    /// before either native reference enumeration. This is an impact plan, not
    /// permission to delete: the host must veto any incoming references, resolve
    /// every returned owner, and retain the normal native permission gate.
    /// </summary>
    public NativeTableDefinitionEffectPlan? TryResolveDeletion(string ns, string method, JsonElement parameters,
        object nativeSettings, Action requirePureReferenceContext)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(requirePureReferenceContext);
            if (!(ns == "table" && method == "deleteTable" || ns == "table.view" && method == "delete"))
                throw Denied("Only the two exact native table/view deletion routes use this reference-aware plan.");
            var plan = Resolve(ns, method, parameters, nativeSettings, requirePureReferenceContext);
            UnavailableReason = null; return plan;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { UnavailableReason = error is BridgeException bridge ? bridge.Message : "The native deletion effect context or required service is unavailable."; return null; }
    }

    private NativeTableDefinitionEffectPlan Resolve(string ns, string method, JsonElement parameters, object settings,
        Action? requirePureReferenceContext = null)
    {
        if (parameters.ValueKind != JsonValueKind.Object || settings.GetType() != assembly.GetType("lt", true)) throw Denied("A real native settings model and object parameters are required.");
        var create = ns == "table" && method == "createTable" || ns == "table.view" && method == "create";
        var update = ns == "table" && SafeUpdates.Contains(method) || ns == "table.view" && method == "update";
        var delete = requirePureReferenceContext is not null && (ns == "table" && method == "deleteTable" || ns == "table.view" && method == "delete");
        // Delete uses ReferenceFinder and may change incoming owners. Its shared
        // database expansion alone is not a complete deletion effect plan.
        if (!create && !update && !delete) throw Denied("This operation has no reviewed complete table-definition effect plan; rename, deletion and relation changes require their separate reference-aware plan.");
        var args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!args.TryAdd(property.Name, property.Value)) throw Denied("Ambiguous native table request properties are not allowed.");
        if (delete && args.Keys.Any(key => !string.Equals(key, ns == "table" ? "tableName" : "viewName", StringComparison.OrdinalIgnoreCase) &&
            !(ns == "table.view" && string.Equals(key, "deleteDatabaseObject", StringComparison.OrdinalIgnoreCase))))
            throw Denied("Deletion impact capture accepts only the exact native identity and optional false physical-delete flag.");
        string? Text(string key) => args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        foreach (var key in new[] { "connectionId", "folderPath", "newName" })
            if (args.TryGetValue(key, out var value) && value.ValueKind != JsonValueKind.Null && (value.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(value.GetString())))
                throw Denied("External connections, new folders and renaming require a separately reviewed effect plan.");
        foreach (var key in new[] { "deleteDatabaseObject", "renameDatabaseObject", "modifyDatabaseObject" })
            if (args.TryGetValue(key, out var value) && value.ValueKind is not (JsonValueKind.False or JsonValueKind.Null)) throw Denied("Physical external database effects are outside this definition plan.");
        var name = Text(create ? "name" : ns == "table.view" ? "viewName" : "tableName");
        if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl)) throw Denied("A native table/view identity is required.");
        if (method is "updateFormulaColumn" or "updateStatisticColumn")
        {
            var original = Text("oldName");
            if (string.IsNullOrWhiteSpace(original) || args.TryGetValue("columnName", out var candidate) && candidate.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                throw Denied("Formula/statistic update requires an exact existing column identity and a string/null optional column name.");
            if (Text("columnName") is { } renamed && !string.IsNullOrWhiteSpace(renamed) && !string.Equals(renamed, original, StringComparison.Ordinal))
                throw Denied("Column renaming requires its complete cross-owner reference plan.");
        }
        var singleton = NativeReflection.Static(assembly.GetType("s", true)!, "get_Instance")!;
        if (!ReferenceEquals(NativeReflection.Get(singleton, "et"), settings)) throw Denied("The supplied settings are not the current native table context.");
        RequireRealStatusService();
        var tableSet = NativeReflection.Get(settings, "TableSet") ?? throw Denied("Native TableSet is unavailable.");
        var modelsAssembly = Assembly.Load("ServerDesignerCommon");
        if (tableSet.GetType() != modelsAssembly.GetType("Forguncy.Model.Tables.TableSet", true)) throw Denied("Unexpected native TableSet type.");
        var root = NativeReflection.Get(tableSet, "Root") ?? throw Denied("Native table root is unavailable.");
        RequireLoaded(root);
        var tables = ((IEnumerable)NativeReflection.Get(tableSet, "RealTablesList")!).Cast<object>().ToArray();
        if (tables.Any(table => table.GetType() != modelsAssembly.GetType("Forguncy.Model.Tables.Table", true)) || tables.Select(TableName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tables.Length)
            throw Denied("The native table collection is ambiguous or contains an unknown model.");
        var target = tables.SingleOrDefault(table => string.Equals(TableName(table), name, StringComparison.Ordinal));
        if (create ? target is not null || tables.Any(table => string.Equals(TableName(table), name, StringComparison.OrdinalIgnoreCase)) : target is null)
            throw Denied("The table lifecycle target does not resolve uniquely in the current native tree.");
        var linkedTarget = target is not null && NativeLinkedTablePolicy.Allows(ns,method,target);
        if (target is not null && (NativeReflection.Get(target, "ExternalDatabaseInfo") is not null || NativeReflection.Get(target, "IsLinkedTableOrLinkedView") is true || NativeReflection.Get(target, "IsCopyExternalBuiltInTable") is true) && !linkedTarget)
            throw Denied("Linked/external-copy table targets are not part of this internal definition plan.");
        if (target is not null && (NativeReflection.Get(target, "IsView") is true) != (ns == "table.view")) throw Denied("The native table/view kind differs from this fixed API route.");
        if (delete && ns == "table.view" && !linkedTarget && NativeReflection.Get(target!, "IsBuiltInViewCustom") is not true)
            throw Denied("Only a genuine internal custom view is supported by the exact native view-deletion route; generated process/history views are excluded.");
        if (delete) return ResolveDeletion(ns, method, name, settings, singleton, tableSet, root, tables, target!, requirePureReferenceContext!);
        var executorType = assembly.GetType(create ? "Forguncy.Common.CreateTableExecutor" : "Forguncy.Common.TableCheckOutExecutor", true)!;
        object executor;
        if (create)
        {
            var ctor = executorType.GetConstructors(Flags).Single(candidate => candidate.GetParameters().Length == 5);
            var types = ctor.GetParameters();
            // Exact native n/p descriptors construct these values (Table=7,
            // TableView=23; icons=24/26), with isBuiltIn=true for no connectionId.
            executor = ctor.Invoke([null, name, Enum.ToObject(types[2].ParameterType, ns == "table" ? 7 : 23), Enum.ToObject(types[3].ParameterType, ns == "table" ? 24 : 26), true]);
        }
        else
        {
            var ctor = executorType.GetConstructors(Flags).Single(candidate => candidate.GetParameters().Length == 3 && candidate.GetParameters()[1].ParameterType.FullName == "Forguncy.Model.Tables.Table");
            executor = ctor.Invoke([null, target, Enum.Parse(ctor.GetParameters()[2].ParameterType, "Edit")]);
        }
        var native = ((IEnumerable)NativeReflection.Call(executor, "GetRelatedModules")!).Cast<object>().ToArray();
        var models = linkedTarget ? [target!] : tables.Where(table => NativeReflection.Get(table, "ExternalDatabaseInfo") is null).ToArray();
        var modules = tables.Select(model => (Model: model, Module: NativeReflection.Static(assembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model)!)).ToArray();
        var effects = new List<NativeTableModuleEffect>();
        foreach (var tuple in native)
        {
            var module = tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
            var state = tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
            var matches = modules.Where(pair => pair.Module.Equals(module)).ToArray();
            var placeholder = create && state.ToString() == "New";
            if (matches.Length != 1 && !placeholder || matches.Length > 1) throw Denied("An affected native module cannot be mapped uniquely to a current internal table.");
            effects.Add(new(tuple, module, state, matches.Length == 1 ? matches[0].Model : null, placeholder));
        }
        if (models.Any(model => !effects.Any(effect => ReferenceEquals(effect.Model, model))) || create && effects.Count(effect => effect.RequiresCreatedTargetToken) != 1)
            throw Denied("Native executor did not return the complete current shared-database table set and required create placeholder.");
        if (!ReferenceEquals(NativeReflection.Get(singleton, "et"), settings) || !ReferenceEquals(NativeReflection.Get(tableSet, "Root"), root)) throw Denied("The native table context changed while the effect plan was captured.");
        return new(ns, method, name, effects, create,
            create ? "Create placeholder is not a saved model/file group. Resolve the new actual target token after successful native registration before verifying or superseding pending groups." : null);
    }

    private NativeTableDefinitionEffectPlan ResolveDeletion(string ns, string method, string name, object settings,
        object singleton, object tableSet, object root, object[] tables, object target, Action requirePureReferenceContext)
    {
        // Unlike table.view.delete, table.deleteTable has an explicit native
        // reference veto before EnsureSubmitSpread/SQL. Do not call that API's
        // FindTableReferencesCore here: it changes sf's suspension/cache state.
        // ul.FindTableReference(target,false) is its underlying fresh scanner.
        requirePureReferenceContext();
        RequireCurrentRelationCache(tableSet, tables);
        var resolverName = ns == "table" ? "abe+s" : "abe+r";
        var dtoType = assembly.GetType(ns == "table" ? "Forguncy.AI.Actions.DeleteTableRequest" :
            "Forguncy.AI.Actions.TableViewSandboxAIActions+DeleteViewRequest", true)!;
        var request = Activator.CreateInstance(dtoType)!;
        dtoType.GetProperty(ns == "table" ? "TableName" : "ViewName", Flags)!.SetValue(request, name);
        if (ns == "table.view") dtoType.GetProperty("DeleteDatabaseObject", Flags)!.SetValue(request, (bool?)false);
        var resolver = Activator.CreateInstance(assembly.GetType(resolverName, true)!, true)!;
        var native = ((IEnumerable)NativeReflection.Call(resolver, "Resolve", request)!).Cast<object>().ToList();
        native=NormalizeDeletionOwners(native).ToList();
        var references = ((IEnumerable)NativeReflection.Static(assembly.GetType("ul", true)!, "FindTableReference", target, false)!).Cast<object>().ToArray();
        // DeleteTableAsync can also remove a workflow-history view. Such a
        // second deletion has its own references and SQL lifecycle; do not
        // relabel its Edit tuple as a complete, reviewed Remove plan.
        if (ns == "table" && NativeReflection.Static(assembly.GetType("my", true)!, "GetBuiltInWorkflowHistoryView", name) is not null)
            throw Denied("A native workflow-history view would also be deleted; its separate lifecycle is not covered by this ordinary table deletion plan.");
        var simpleType = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var known = tables.Select(model => (Model: model, Module: NativeReflection.Static(simpleType, "Create", model)!)).ToArray();
        var targetModule = known.Single(pair => ReferenceEquals(pair.Model, target)).Module;
        var sample = native.FirstOrDefault() ?? throw Denied("Native deletion resolver returned no affected target.");
        var tupleType = sample.GetType(); var stateType = tupleType.GetField("Item2")!.FieldType;
        var app = NativeReflection.Get(settings, "ApplicationSettings") ?? throw Denied("Native application settings are unavailable.");
        // ApplicationSettings has an AuditingList GetTableInfo implementation,
        // but this build's sf.ela table scanner does not enumerate that owner;
        // StatusCacheTableName is absent from that implementation as well.
        // DeleteTableAsync writes both, so capture the missing genuine owner.
        object? appModule = null;
        var auditing = NativeReflection.Get(app, "AuditingList") as IDictionary;
        if (ns == "table" && (auditing?.Contains(name) is true ||
            string.Equals(NativeReflection.Get(app, "StatusCacheTableName") as string, name, StringComparison.Ordinal)))
        {
            var create = simpleType.GetMethods(Flags).Single(candidate => candidate.Name == "Create" && candidate.GetParameters().Length == 2 && candidate.GetParameters()[0].ParameterType.IsEnum);
            appModule = create.Invoke(null, [Enum.Parse(create.GetParameters()[0].ParameterType, "ApplicationSetting"), Enum.Parse(create.GetParameters()[1].ParameterType, "ApplicationSetting")])!;
            if (!native.Any(tuple => appModule.Equals(tuple.GetType().GetField("Item1")!.GetValue(tuple))))
                native.Add(Activator.CreateInstance(tupleType, [appModule, Enum.Parse(stateType, "Edit")])!);
        }
        var effects = new List<NativeTableModuleEffect>();
        foreach (var tuple in native)
        {
            var module = tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
            var state = tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
            var matches = known.Where(pair => pair.Module.Equals(module)).ToArray();
            if (matches.Length > 1 || state.ToString() != (targetModule.Equals(module) ? "Remove" : "Edit"))
                throw Denied("Native deletion returned an ambiguous owner or an unexpected lifecycle state.");
            // Non-table owners are deliberately retained. The file-group
            // consumer must map all of them against actual native trees; null
            // is an unresolved owner, never a New placeholder or permission to
            // omit its group.
            var model = matches.Length == 1 ? matches[0].Model : appModule?.Equals(module) is true ? app : null;
            effects.Add(new(tuple, module, state, model, false));
        }
        if (effects.Count(effect => effect.Module.Equals(targetModule) && effect.State.ToString() == "Remove") != 1 ||
            NativeReflection.Get(target, "ExternalDatabaseInfo") is null &&
            tables.Where(table => NativeReflection.Get(table, "ExternalDatabaseInfo") is null).Any(model => !effects.Any(effect => ReferenceEquals(effect.Model, model))))
            throw Denied("Native deletion did not include its exact target and complete shared built-in database owners.");
        RequireCurrentRelationCache(tableSet, tables, true);
        if (!ReferenceEquals(NativeReflection.Get(singleton, "et"), settings) || !ReferenceEquals(NativeReflection.Get(tableSet, "Root"), root))
            throw Denied("The native table context changed while deletion effects were captured.");
        return new(ns, method, name, effects, false,
            "Impact capture does not authorize deletion. Incoming references must be vetoed, all native owners must resolve, and native permission/execution gates remain required. Workflow-history companion deletion is excluded.")
        { RequiresPureReferenceContext = true, RequiresIncomingReferenceVeto = true, NativeIncomingReferenceCount = references.Length, NativeResolverType = resolverName };
    }

    internal static IReadOnlyList<object> NormalizeDeletionOwners(IEnumerable<object> native)
    {
        var unique = new List<object>();
        foreach (var tuple in native)
        {
            var module=tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
            var state=tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
            var ownerName=NativeReflection.Get(module,"ModuleName") as string;
            var type=NativeReflection.Get(module,"ModuleType");
            var prior=unique.Where(item=>
            {
                var other=item.GetType().GetField("Item1")!.GetValue(item)!;
                return Equals(NativeReflection.Get(other,"ModuleName"),ownerName);
            }).ToArray();
            if(prior.Length==0) { unique.Add(tuple); continue; }
            var priorModule=prior[0].GetType().GetField("Item1")!.GetValue(prior[0])!;
            var priorState=prior[0].GetType().GetField("Item2")!.GetValue(prior[0])!;
            if(prior.Length!=1 || !Equals(NativeReflection.Get(priorModule,"ModuleType"),type) ||
                !Equals(NativeReflection.Get(priorModule,"ExclusiveType"),NativeReflection.Get(module,"ExclusiveType")) || !module.Equals(priorModule) || !state.Equals(priorState))
                throw Denied("Native deletion returned conflicting identities or states for an affected owner.");
        }
        return unique;
    }

    private static void RequireCurrentRelationCache(object tableSet, object[] tables, bool afterScan = false)
    {
        // sf.ela calls TableSet.Relations even with no relation references. Its
        // getter may refresh only the derived aggregation _relations. It does
        // not modify individual tables, relations, saved DTOs or dirty state.
        // Caching disabled always rebuilds fresh; a cold cache does too. Never
        // toggle/prewarm/restore the Designer's cache configuration here. An
        // existing enabled cache must match every actual relation by identity
        // before the native scanner can use it (and again after enumeration).
        var enabled = tableSet.GetType().GetField("_enableRelationCache", Flags)?.GetValue(tableSet) as bool?;
        var value = tableSet.GetType().GetField("_relations", Flags)?.GetValue(tableSet);
        if (enabled is null) throw Denied("The verified native relation-cache context is unavailable.");
        if (!afterScan && (enabled is not true || value is null)) return;
        if (value is not IEnumerable cached) throw Denied("Unknown native relation aggregation type.");
        var actual = tables.SelectMany(table => (NativeReflection.Get(table, "Relations") as IEnumerable)?.Cast<object>() ?? []).ToArray();
        var snapshot = cached.Cast<object>().ToArray();
        if (actual.Length != snapshot.Length || actual.Where((item, index) => !ReferenceEquals(item, snapshot[index])).Any())
            throw Denied("The existing native relation cache does not match the complete loaded table models.");
    }

    private void RequireRealStatusService()
    {
        var config = Assembly.Load("CommonUtilities").GetType("CommonUtilities.SystemConfigDef", true)!;
        var provider = config.GetProperty("ServerProvider", Flags)?.GetValue(null) as IServiceProvider;
        var contract = Assembly.Load("ServerDesignerCommon").GetType("ServerDesignerCommon.Services.IStatusInfoTableService", true)!;
        var service = provider?.GetService(contract);
        if (service?.GetType() != assembly.GetType("Forguncy.Common.DesignerStatusInfoTableService", true)) throw Denied("The genuine native IStatusInfoTableService is unavailable; no replacement or fabricated service is installed by this helper.");
    }
    private static string TableName(object table) => (string)NativeReflection.Get(table, "Name")!;
    private static void RequireLoaded(object node)
    {
        // Table leaves have no Loaded property in V12. The lazy-load boundary
        // belongs to native TableFolder; never fabricate a leaf loaded flag.
        if (node.GetType().GetProperty("Items", Flags)?.GetValue(node) is IEnumerable items)
        {
            if (node.GetType().GetProperty("Loaded", Flags)?.GetValue(node) is not true) throw Denied("The complete native table folder tree must already be loaded; the planner will not load it.");
            foreach (var child in items.Cast<object>()) RequireLoaded(child);
        }
        else if (node.GetType().FullName != "Forguncy.Model.Tables.Table") throw Denied("Unknown object in the native table tree.");
    }
    private static BridgeException Denied(string reason) => new("NativeTableEffectsUnavailable", reason);
}

public sealed record NativeTableModuleEffect(object NativeTuple, object Module, object State, object? Model, bool RequiresCreatedTargetToken)
{
    public bool RequiresNativeOwnerResolution => Model is null && !RequiresCreatedTargetToken;
}
public sealed record NativeTableDefinitionEffectPlan(string Namespace, string Method, string TargetName,
    IReadOnlyList<NativeTableModuleEffect> Effects, bool RequiresCreatedTargetToken, string? Limitation)
{
    public bool RequiresPureReferenceContext { get; init; }
    public bool RequiresIncomingReferenceVeto { get; init; }
    public int NativeIncomingReferenceCount { get; init; }
    public string? NativeResolverType { get; init; }
    public void RequireNoIncomingReferences()
    {
        if (!RequiresIncomingReferenceVeto || NativeIncomingReferenceCount != 0)
            throw new BridgeException("ModuleReferenced", "The native table/view definition still has incoming references; deletion is blocked before native dispatch.");
    }
}
