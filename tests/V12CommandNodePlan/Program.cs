using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Adapters.V12;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

internal static class Program
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the exact V12 SDK installation root.");
        var site = Path.Combine(Path.GetFullPath(args[0]), "WebSite");
        var dll = Path.Combine(site, "designerBin", "Forguncy.dll");
        if (AssemblyName.GetAssemblyName(dll).Version?.ToString() != V12AdapterFactory.Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll))) != V12AdapterFactory.DesignerSha256)
            throw new InvalidOperationException("This fixture requires the reviewed exact native Designer build.");
        AssemblyLoadContext.Default.Resolving += (context, name) => new[] { "designerBin", "bin", "arServerBin" }
            .Select(part => Path.Combine(site, part, name.Name + ".dll")).Where(File.Exists)
            .Select(context.LoadFromAssemblyPath).FirstOrDefault();
        var native = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
        VerifyListViewColumnPreflight(native);
        object New(string type, params object[] values) => Activator.CreateInstance(native.GetType(type, true)!, Members, null, values, null)!;
        var scratch = Path.Combine(Path.GetTempPath(), "mcp-command-node-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var settings = New("lt");
        var nativeState = native.GetType("s", true)!.GetProperty("Instance", Members)!.GetValue(null)!;
        Set(nativeState, "et", settings);
        VerifyLazyPageOwner(native, settings);
        var colleagues = New("hl");
        var mediator = New("dt");
        var save = New("hu", Path.Combine(scratch, "fixture.fgcc"));
        // No native save or collaboration operation is invoked by this fixture.
        var loader = New("dv", mediator, save, New("cp"));
        var colleague = New("du", mediator);
        var tree = mediator.GetType().GetMethod("CreateFolder", Members)!.Invoke(mediator, ["ServerCommands"])!;
        Set(tree, "Loaded", true);
        Set(colleague, "Root", tree);
        Set(mediator, "SaveDataColleague", loader);
        Set(colleagues, "ServerCommands", colleague);
        Set(settings, "ServerCommandsList", tree);
        var commandType = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.ServerCommands.Models.ServerCommands", true)!;
        var command = Activator.CreateInstance(commandType)!;
        Set(command, "Name", "Demo");
        foreach (var list in commandType.GetProperties(Members).Where(p =>
                     p.CanWrite && p.PropertyType.IsGenericType &&
                     p.PropertyType.GetGenericTypeDefinition() == typeof(List<>)))
            if (list.GetValue(command) is null) list.SetValue(command, Activator.CreateInstance(list.PropertyType));
        ((IList)Get(tree, "Items")!).Add(command);

        var files = new NativeModuleFiles(native, colleagues, settings, save);
        var address = new ModuleAddress(ModuleKind.ServerCommand, "Demo");
        var noCondition = files.Resolve(address)!;
        Check(noCondition.Current(), "plain command control is stable");
        var modelAssembly = Assembly.Load("ServerDesignerCommon");
        var condition = Activator.CreateInstance(modelAssembly.GetType("Forguncy.Model.ConditionCommand", true)!)!;
        var pairs = (IList)Get(condition, "ConditionAndCommandPairList")!;
        var pair = Activator.CreateInstance(pairs.GetType().GetGenericArguments().Single())!;
        pairs.Add(pair);
        var liveCommands = (IList)Get(command, "Commands")!;
        liveCommands.Add(condition);
        var liveId = (string)Get(pair, "ID")!;
        var one = files.Resolve(address)!;
        var two = files.Resolve(address)!;
        var oneJson = one.ExpectedJson.Single().Value;
        var twoJson = two.ExpectedJson.Single().Value;
        var firstProjection = System.Text.Json.Nodes.JsonNode.Parse(oneJson)!;
        var secondProjection = System.Text.Json.Nodes.JsonNode.Parse(twoJson)!;
        var firstPair = firstProjection["Commands"]!["$values"]![0]!["ConditionAndCommandPairList"]!["$values"]![0]!;
        var secondPair = secondProjection["Commands"]!["$values"]![0]!["ConditionAndCommandPairList"]!["$values"]![0]!;
        Check(!System.Text.Json.Nodes.JsonNode.DeepEquals(firstPair["ID"], secondPair["ID"]),
            "pinned native ServerCommand conversion generates fresh detached pair IDs");
        secondPair["ID"] = firstPair["ID"]!.DeepClone();
        Check(System.Text.Json.Nodes.JsonNode.DeepEquals(firstProjection, secondProjection),
            "the only difference between these synthetic projections is the cloned pair ID");
        Check(one.IsPresent() && ReferenceEquals(one.NativeIdentity, command) && (string)Get(pair, "ID")! == liveId,
            "the exact live model and pair ID remain unchanged by conversion");
        Check(one.RequiredFiles.SequenceEqual(two.RequiredFiles) && one.ReferencedFiles.SequenceEqual(two.ReferencedFiles),
            "native key and reference inventory did not change");
        Check(one.Current() && two.Current(), "unchanged live conditional command is stable");
        Check(ReferenceEquals(Get(command, "Commands"), liveCommands) &&
            ReferenceEquals(liveCommands[0], condition) && ReferenceEquals(pairs[0], pair) && (string)Get(pair, "ID")! == liveId,
            "comparison did not mutate the live command collection or branch ID");
        var originalDescription = Get(command, "Description");
        Set(command, "Description", "changed command metadata");
        Check(!one.Current(), "non-Commands fields stay in the strict server command comparison");
        commandType.GetProperty("Description", Members)!.SetValue(command, originalDescription);
        Check(one.Current(), "restored metadata restores comparison");
        string? Drift() => one.Current() ? null : one.NativeModuleName;
        var ownerRevision = V12ProjectAdapter.CheckoutExistingOwnersAsync("unchanged-source",
            () => Task.FromResult(true), () => "unchanged-source", Drift, () => "fresh-revision").GetAwaiter().GetResult();
        Check(ownerRevision == "fresh-revision", "accepted checkout witness reaches the real native owner-comparison gate");
        Set(pair, "ID", Guid.NewGuid().ToString("D"));
        Check(!one.Current(), "an actual live condition pair ID edit invalidates the owner witness");
        Set(pair, "ID", liveId);
        Check(one.Current(), "restored actual ID is stable");
        Set(pair, "Comments", "changed condition branch");
        Check(!one.Current(), "a real condition branch change invalidates the owner witness");
        try
        {
            V12ProjectAdapter.CheckoutExistingOwnersAsync("unchanged-source", () => Task.FromResult(true),
                () => "unchanged-source", Drift, () => "fresh-revision").GetAwaiter().GetResult();
            throw new InvalidOperationException("The stale owner witness was accepted.");
        }
        catch (CheckoutBaselineException error) when (error.Code == "CheckoutOwnerChanged") { }
        Set(pair, "Comments", "");
        var stable = files.Resolve(address)!;
        Set(command, "Name", "Renamed");
        Check(!stable.Current(), "a real native key and command signature change invalidates the owner witness");
        Set(command, "Name", "Demo");

        var arguments = JsonSerializer.SerializeToElement(new { name = "Demo", updates = Array.Empty<object>() });
        var capture = files.CaptureApiBefore("serverCommand", "updateServerCommands", arguments);
        V12ProjectAdapter.ValidateApiCapture("serverCommand", "updateServerCommands", capture);
        if (capture?.Plan?.BeforeGroups is not [var owner] || capture.Operation != ModuleOperation.Update ||
            capture.Address != new ModuleAddress(ModuleKind.ServerCommand, "Demo") ||
            !ReferenceEquals(owner.NativeIdentity, command) || capture.Plan.ExpectedNewModuleNames.Count != 0 ||
            capture.RequiredOwnedModules is not [var required] || required != owner.NativeModuleName ||
            owner.RequiredFiles.Count != 1 || !owner.RequiredFiles.Single().EndsWith("/Demo.json", StringComparison.Ordinal))
            throw new InvalidOperationException("Node update did not produce one exact required ownership/file plan from the real native resolver.");
        Expect("NativeDefinitionNotFound", () => files.CaptureApiBefore("serverCommand", "updateServerCommands",
            JsonSerializer.SerializeToElement(new { name = "Missing", updates = Array.Empty<object>() })));
        Expect("NativeAffectedTargetsUnverified", () => V12ProjectAdapter.ValidateApiCapture("serverCommand", "updateServerCommands", null));
        var second = Activator.CreateInstance(commandType)!;
        Set(second, "Name", "Demo");
        ((IList)Get(tree, "Items")!).Add(second);
        Expect("NativeFileTargetAmbiguous", () => files.CaptureApiBefore("serverCommand", "updateServerCommands", arguments));
        Console.WriteLine("PASS exact V12 node update owner, missing/ambiguous owner, mandatory effect plan, and strict live conditional owner witness.");
    }

    private static void Check(bool result, string message) { if (!result) throw new InvalidOperationException(message); }
    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Members)!.GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetProperty(name, Members)!.SetValue(obj, value);
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (BridgeException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static void VerifyListViewColumnPreflight(Assembly native)
    {
        const string formula = "=DemoPage!G5";
        const string target = "DemoList";
        using var data = JsonDocument.Parse("""
            [{"commands":[{"type":"RequestServer","props":{"parameters":{"Item":"After"}}},
              {"type":"Condition","branches":[{"predicate":{"type":"else"},"commands":[
                {"type":"UpdateListview","props":{"listviewName":"DemoList","needUpdateValues":true,
                 "updateValues":[{"column":"Customer","value":"A"},{"column":"=DemoPage!G5","value":"B"},
                                 {"column":{"formula":"=DemoPage!G5"},"value":"C"}]}}]}]}]}]
            """);
        var found = NativeListViewColumnPreflight.Find(data.RootElement).Single();
        Check(found == new NativeListViewColumnPreflight.Column(
            "cells[0].commands[1].branches[0].commands[0].props.updateValues[1].column", target, formula),
            "Identify only the reported string form at the exact nested path.");

        var serializer = Assembly.Load("Newtonsoft.Json");
        const string dsl = """
            [{"type":"UpdateListview","props":{"listviewName":"DemoList","needUpdateValues":true,
             "updateValues":[{"column":"=DemoPage!G5","value":"B"}]}}]
            """;
        var wire = NativeReflection.Static(serializer.GetType("Newtonsoft.Json.Linq.JArray", true)!, "Parse", dsl)!;
        var parse = NativeReflection.Static(native.GetType("aaw", true)!, "ForPage", "DemoPage")!;
        var tree = (IList)NativeReflection.Static(native.GetType("Forguncy.AI.Actions.CommandTreeHelper", true)!, "ParseCommands", wire, parse)!;
        Check(Get(((IList)Get(tree[0]!, "UpdateValues")!)[0]!, "Column") is string text && text == formula,
            "The exact installed V12 parser leaves the template formula string untyped.");

        var scope = NativeReflection.Static(native.GetType("acq", true)!, "ForPage", "DemoPage")!;
        var plugin = Assembly.Load("GrapeCity.Forguncy.Plugin");
        var shape = plugin.GetType("GrapeCity.Forguncy.Plugin.IListViewInfoDesigner", true)!;
        var list = (ColumnNamesProxy)DispatchProxy.Create(shape, typeof(ColumnNamesProxy));
        list.Names = ["Customer"];
        try
        {
            NativeListViewColumnPreflight.RequireNativeColumn(found, scope, list);
            throw new InvalidOperationException("Expected native list-view column rejection.");
        }
        catch (BridgeException error) when (error.Code == "InvalidCommandTree" && error.Message.Contains(found.Path, StringComparison.Ordinal)) { }
        NativeListViewColumnPreflight.RequireNativeColumn(found with { Value = "Customer" }, scope, list);
        list.Names.Add(formula);
        NativeListViewColumnPreflight.RequireNativeColumn(found, scope, list);
        Console.WriteLine("Native V12 list-view formula-string column preflight passed.");
    }

    public class ColumnNamesProxy : DispatchProxy
    {
        internal List<string> Names = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "GetAllColumnNames" ? Names : throw new NotSupportedException(targetMethod?.Name);
    }

    private static void VerifyLazyPageOwner(Assembly native, object settings)
    {
        var actions = native.GetType("Forguncy.AI.Actions.PageSandboxAIActions", true)!;
        var requestType = actions.GetNestedType("CreatePageRequest", Members)!;
        var request = JsonSerializer.Deserialize("""{"name":"FixturePageNew","type":"normal"}""", requestType,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var resolver = Activator.CreateInstance(native.GetType("abe+ae", true)!)!;
        var tuples = (IEnumerable)NativeReflection.Call(resolver, "Resolve", request)!;
        var tuple = tuples.Cast<object>().Single();
        var token = tuple.GetType().GetField("Item1")!.GetValue(tuple)!;
        Check(tuple.GetType().GetField("Item2")!.GetValue(tuple)!.ToString() == "New", "Native create page resolver produces its New token.");
        var root = Get(Get(settings, "Pages")!, "NormalPageRoot")!;
        var map = new NativeCollaborationModules(native, settings, Array.Empty<object>());
        Set(root, "Loaded", false);
        try
        {
            _ = map.MapNativeModule(token);
            throw new InvalidOperationException("The native page mapper accepted a lazy root without proof.");
        }
        catch (BridgeException error) when (error.Code == "NativeCollaborationModuleUnavailable" &&
            error.Message.Contains("complete native owner tree", StringComparison.Ordinal)) { }
        Check(Get(root, "Loaded") is false, "Read-only mapper must not force native page tree state.");
        Set(root, "Loaded", true);
        Check(map.MapNativeModule(token) == new ModuleAddress(ModuleKind.Page, "FixturePageNew"),
            "A complete empty native owner tree accepts the same resolver-generated New token.");
        Console.WriteLine("PASS exact V12 createPage native New-owner mapping distinguishes lazy and complete roots.");
    }

}
