using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeTableDefinitionEffects
{
    internal static bool IsReferenceUpdate(string ns, string method, JsonElement parameters)
    {
        if (ns != "table") return false;
        if (method is "deleteColumn" or "setRelation" or "deleteRelation" or "renameColumn") return true;
        if (method is not ("updateFormulaColumn" or "updateStatisticColumn") || parameters.ValueKind != JsonValueKind.Object) return false;
        string? Text(string key) => parameters.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value
            is {ValueKind:JsonValueKind.String} value ? value.GetString() : null;
        // The native DTO treats an omitted, null or blank replacement name as
        // preserving OldName. Only a supplied different name rewrites references.
        return Text("columnName") is { } renamed && !string.IsNullOrWhiteSpace(renamed) && renamed != Text("oldName");
    }

    public NativeTableDefinitionEffectPlan? TryResolveReferenceUpdate(string method, JsonElement parameters,
        object settings, Action requirePureReferenceContext)
    {
        try
        {
            var result = ResolveReferenceUpdate(method, parameters, settings, requirePureReferenceContext);
            UnavailableReason = null; return result;
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        { UnavailableReason = error is BridgeException bridge ? bridge.Message : "The native column/relation effect context is unavailable."; return null; }
    }

    private NativeTableDefinitionEffectPlan ResolveReferenceUpdate(string method, JsonElement parameters, object settings, Action pure)
    {
        if (!IsReferenceUpdate("table", method, parameters) || settings.GetType() != assembly.GetType("lt", true) || parameters.ValueKind != JsonValueKind.Object)
            throw Denied("A reviewed column/relation operation and the exact native settings are required.");
        var args = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in parameters.EnumerateObject())
            if (!args.TryAdd(property.Name, property.Value)) throw Denied("Ambiguous column/relation parameters are not allowed.");
        string Text(string key) => args.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw Denied("A nonempty native identity is required: " + key);
        var relation = method is "setRelation" or "deleteRelation";
        var name = Text(relation ? "foreignTable" : "tableName");
        var columnName = Text(relation ? "foreignColumn" : method is "renameColumn" or "updateFormulaColumn" or "updateStatisticColumn" ? "oldName" : "columnName");
        var singleton = NativeReflection.Static(assembly.GetType("s", true)!, "get_Instance")!;
        if (!ReferenceEquals(NativeReflection.Get(singleton, "et"), settings)) throw Denied("Settings are not the current native context.");
        RequireRealStatusService();
        var tableSet = NativeReflection.Get(settings, "TableSet")!;
        var root = NativeReflection.Get(tableSet, "Root")!; RequireLoaded(root);
        var tables = ((IEnumerable)NativeReflection.Get(tableSet, "RealTablesList")!).Cast<object>().ToArray();
        var tableType = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.Table", true)!;
        if (tables.Any(t => t.GetType() != tableType) || tables.Select(TableName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tables.Length)
            throw Denied("The complete native table collection must resolve uniquely.");
        object Table(string identity)
        {
            var found = tables.SingleOrDefault(t => TableName(t) == identity) ?? throw Denied("A requested table is missing.");
            if (NativeReflection.Get(found, "ExternalDatabaseInfo") is not null || NativeReflection.Get(found, "IsLinkedTableOrLinkedView") is true ||
                NativeReflection.Get(found, "IsCopyExternalBuiltInTable") is true || NativeReflection.Get(found, "IsView") is true)
                throw Denied("Column/relation changes require ordinary internal tables; linked, copied and view definitions are excluded.");
            return found;
        }
        object Column(object table, string identity) => NativeReflection.Call(NativeReflection.Get(table, "Columns")!, "TryGetColumn", identity)
            ?? throw Denied("The requested native column is missing.");
        var target = Table(name); var column = Column(target, columnName);
        if (relation) Column(Table(Text("primaryTable")), Text("primaryColumn"));
        if (method is "renameColumn" or "updateFormulaColumn" or "updateStatisticColumn")
        {
            var renamed = Text(method == "renameColumn" ? "newName" : "columnName");
            var columns = ((IEnumerable)NativeReflection.Get(target, "Columns")!).Cast<object>();
            if (renamed != columnName && columns.Any(c => string.Equals(NativeReflection.Get(c, "Name") as string, renamed, StringComparison.OrdinalIgnoreCase)))
                throw Denied("The renamed column would collide with an existing native column.");
        }
        // Reference enumeration may populate a derived cache only. It must not
        // load documents/plugins or use an incomplete worksheet projection.
        pure(); RequireCurrentRelationCache(tableSet, tables);
        var descriptors = ((IEnumerable)NativeReflection.Static(assembly.GetType("aam", true)!, "GetCurrentAssemblyDescriptors")!).Cast<object>();
        var descriptor = descriptors.Single(d => NativeReflection.Get(d, "Namespace") as string == "table" && NativeReflection.Get(d, "Method") as string == method);
        var nativeMethod = (MethodInfo)NativeReflection.Get(descriptor, "MethodInfo")!;
        var requestType = (Type)NativeReflection.Get(descriptor, "RequestType")!;
        var expectedResolver = method switch { "renameColumn" => "abe+v", "updateFormulaColumn" => "abe+w", "updateStatisticColumn" => "abe+x", _ => "abe+u" };
        var resolverType = nativeMethod.GetCustomAttributesData().Single(a => a.AttributeType.FullName == "abc").ConstructorArguments.Single().Value as Type;
        if (resolverType?.Assembly != assembly || resolverType.FullName != expectedResolver) throw Denied("The exact native effect resolver changed.");
        var request = Activator.CreateInstance(requestType)!;
        // Exact resolver reads only these scalar identities. Never deserialize
        // formulas/commands or accept client CLR type metadata while planning.
        foreach (var property in requestType.GetProperties(Flags).Where(p => p.PropertyType == typeof(string) && p.CanWrite))
            if (args.TryGetValue(property.Name, out var value) && value.ValueKind == JsonValueKind.String) property.SetValue(request, value.GetString());
        var tuples = ((IEnumerable)NativeReflection.Call(Activator.CreateInstance(resolverType)!, "Resolve", request)!).Cast<object>().ToArray();
        var simpleType = assembly.GetType("Forguncy.Common.SimpleModule", true)!;
        var known = tables.Where(t => NativeReflection.Get(t, "ExternalDatabaseInfo") is null)
            .Select(t => (Model: t, Module: NativeReflection.Static(simpleType, "Create", t)!)).ToArray();
        var seen = new HashSet<object>(); var effects = new List<NativeTableModuleEffect>();
        foreach (var tuple in tuples)
        {
            var module = tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
            var state = tuple.GetType().GetField("Item2")!.GetValue(tuple)!;
            if (state.ToString() != "Edit") throw Denied("Column/relation changes returned an unexpected native lifecycle state.");
            if (!seen.Add(module)) continue;
            var model = known.SingleOrDefault(t => t.Module.Equals(module)).Model;
            effects.Add(new(tuple, module, state, model, false));
        }
        if (known.Any(k => !effects.Any(e => ReferenceEquals(e.Model, k.Model)))) throw Denied("Native resolver omitted a shared database owner.");
        var incoming = method == "deleteColumn" ? ((IEnumerable)NativeReflection.Static(assembly.GetType("Forguncy.AI.Actions.TableSandboxAIActions", true)!,
            "FindColumnReferenceDescriptions", target, column)!).Cast<object>().Count() : 0;
        RequireCurrentRelationCache(tableSet, tables, true);
        if (!ReferenceEquals(NativeReflection.Get(singleton, "et"), settings) || !ReferenceEquals(NativeReflection.Get(tableSet, "Root"), root))
            throw Denied("The native context changed during column/relation planning.");
        return new("table", method, name, effects, false, "Every genuine native owner must resolve before dispatch; column deletion retains the native incoming-reference veto.")
        { RequiresPureReferenceContext = true, RequiresIncomingReferenceVeto = method == "deleteColumn", NativeIncomingReferenceCount = incoming, NativeResolverType = expectedResolver };
    }
}
