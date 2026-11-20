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
        VerifyInternalTableOwnerPlan(native, settings);
        VerifyFirstTableSqliteProof(site);
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
        VerifyPageReservationConflict(native, token, tuple);
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

    private static void VerifyPageReservationConflict(Assembly native, object token, object tuple)
    {
        const string name = "FixturePageNew";
        var pairs = new (string, string)[] { ((string)Get(token, "ModuleName")!, Get(token, "ModuleType")!.ToString()!) };
        var plan = new NativeModuleFilePlan([], pairs, nativeChanges: [tuple]);
        var capture = new NativeApiFileCapture("page", "createPage", JsonSerializer.SerializeToElement(new { name }),
            new(ModuleKind.Page, name), ModuleOperation.Create, null, plan);
        Check(NativePageReservationPreflight.MatchesPlan(capture, name), "Native CreatePage resolver exact New tuple is within the guard.");
        Check(!NativePageReservationPreflight.MatchesPlan(capture, "DifferentPage"), "Do not block unrelated plans.");

        // The real pinned native validator accepts a fresh name, then refuses
        // that same name once it appears in the authoritative shared-name list.
        var validate = native.GetType("ace", true)!;
        Func<(List<string>, bool)> empty = () => ([], true);
        Func<(List<string>, bool)> reserved = () => ([name.ToUpperInvariant()], true);
        NativeReflection.Static(validate, "EnsureSheetNameNotExists", name, "page", empty);
        try
        {
            NativeReflection.Static(validate, "EnsureSheetNameNotExists", name, "page", reserved);
            throw new InvalidOperationException("Native name validator accepted the New reservation.");
        }
        catch (ArgumentException) { }

        NativePageReservationPreflight.RequireEligible(false, 0, false);
        NativePageReservationPreflight.RequireCompleteNameInventory((new List<string> { "DifferentPage" }, true), name);
        MustRefuse("NativePageNameInventoryUnverified", () =>
            NativePageReservationPreflight.RequireCompleteNameInventory((new List<string>(), false), name));
        MustRefuse("NativePageNameAlreadyExists", () =>
            NativePageReservationPreflight.RequireCompleteNameInventory((new List<string> { name.ToUpperInvariant() }, true), name));

        var scratch = Path.Combine(Path.GetTempPath(), "mcp-page-collision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(scratch, "Pages"));
        var alias = Path.Combine(scratch, "Pages", name.ToUpperInvariant() + ".RD");
        try
        {
            NativeModuleFiles.RequireDesktopPageFilesAbsent(name, [scratch]);
            File.WriteAllText(alias, "synthetic companion");
            MustRefuse("NativePageCreateSourceCollision", () => NativeModuleFiles.RequireDesktopPageFilesAbsent(name, [scratch]));
        }
        finally
        {
            File.Delete(alias);
            Directory.Delete(Path.Combine(scratch, "Pages"));
            Directory.Delete(scratch);
        }
        var target = new ProjectTarget("fixture", "document", Path.GetFullPath("fixture"), "12.0.102.0");
        var user = new UserIdentity("fixture", "fixture@example.invalid");
        var snapshot = new ProjectSnapshot(target, false, false, true, user, "before");
        var dirty = snapshot with { HasUnsavedChanges = true, Revision = "after" };
        var group = new NativeModuleFileGroup(capture.Address, pairs[0].Item1, ["fixture.json", "fixture.rd"], [], [],
            new Dictionary<string, string>(), () => true);
        var result = PlatformJson.Element(new { pageName = name });
        NativePageReservationPreflight.RequireCreatedPage(snapshot, dirty, "source", "source", result, capture.Address, pairs[0].Item1, [group]);
        MustRefuse("NativePageCreateReadbackUnverified", () =>
            NativePageReservationPreflight.RequireCreatedPage(snapshot, snapshot, "source", "source", result, capture.Address, pairs[0].Item1, [group]));
        MustRefuse("NativePageCreateReadbackUnverified", () =>
            NativePageReservationPreflight.RequireCreatedPage(snapshot, dirty, "source", "changed", result, capture.Address, pairs[0].Item1, [group]));
        MustRefuse("NativePageCreateOwnerUnverified", () =>
            NativePageReservationPreflight.RequireCreatedPage(snapshot, dirty, "source", "source", result, capture.Address, pairs[0].Item1, []));
        Require("NativePageNamePreviouslyReserved", false, 1, true);
        Require("NativePageNameRegistered", false, 1, false);
        Require("NativePageNameAlreadyExists", true, 1, true);

        Console.WriteLine("PASS exact V12 native page name validation after reservation and narrow remote-path guard.");

        static void MustRefuse(string code, Action action)
        {
            try { action(); throw new InvalidOperationException("Expected refusal " + code); }
            catch (BridgeException error) when (error.Code == code) { }
        }

        static void Require(string code, bool existing, int registrations, bool ownNew)
        {
            try
            {
                NativePageReservationPreflight.RequireEligible(existing, registrations, ownNew);
                throw new InvalidOperationException("Expected settled guard " + code);
            }
            catch (BridgeException error) when (error.Code == code) { }
        }
    }

    private static void VerifyInternalTableOwnerPlan(Assembly native, object settings)
    {
        var sys = Assembly.Load("CommonUtilities").GetType("CommonUtilities.SystemConfigDef", true)!;
        var provider = sys.GetProperty("ServerProvider", Members)!;
        var before = provider.GetValue(null);
        var service = Activator.CreateInstance(native.GetType("Forguncy.Common.DesignerStatusInfoTableService", true)!, true)!;
        var root = Get(Get(settings, "TableSet")!, "Root")!;
        var observer = new NativeTableDefinitionEffects(native);
        var request = PlatformJson.Element(new { name = "FixtureInternalTable", columns = new[] { new { columnName = "Label", type = "Text" } } });
        try
        {
            provider.SetValue(null, new NativeTableFixtureProvider(service));
            Set(root, "Loaded", false);
            Check(observer.TryResolve("table", "createTable", request, settings) is null &&
                observer.UnavailableReason!.Contains("complete native table folder tree", StringComparison.Ordinal),
                "Unloaded real SDK root refuses before native table checkout or SQLite effects.");
            Check(Get(root, "Loaded") is false, "Read-only table planner did not change the detached root.");
            Set(root, "Loaded", true); // Only the disposable native fixture, never a live project.
            var plan = observer.TryResolve("table", "createTable", request, settings);
            Check(plan is { Effects: [var created] } && created.RequiresCreatedTargetToken &&
                created.State.ToString() == "New" && (string?)Get(created.Module, "ModuleName") == "Table/FixtureInternalTable",
                "Genuine V12 executor resolves a sole New token from a proven complete empty native root.");
            var external = PlatformJson.Element(new { name = "FixtureExternal", connectionId = "external" });
            Check(observer.TryResolve("table", "createTable", external, settings) is null, "Do not add external-database creation to this internal route.");
        }
        finally { provider.SetValue(null, before); }
        Console.WriteLine("PASS exact V12 internal table-owner planning denies lazy/external roots and resolves a complete empty native fixture.");
    }

    private sealed class NativeTableFixtureProvider(object nativeService) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(nativeService) ? nativeService : null;
    }
    private static void VerifyFirstTableSqliteProof(string site)
    {
        static JsonElement Request(string? external = null, string type = "Text") =>
            PlatformJson.Element(new { name = "FixtureTable", connectionId = external, columns = new[] { new { columnName = "Label", type } } });
        Check(NativeFirstTableCreate.TryRequest(Request(), out var name, out var column) && name == "FixtureTable" && column == "Label",
            "Only exact standalone-text column scope is admitted for first-table creation.");
        Check(!NativeFirstTableCreate.TryRequest(Request("external"), out _, out _) &&
            !NativeFirstTableCreate.TryRequest(Request(type: "Integer"), out _, out _),
            "External or different SQL shape cannot use the narrow first-table route.");

        var provider = Assembly.Load("SQLitePCLRaw.provider.e_sqlite3");
        var nativePath = Path.Combine(site, "designerBin", "runtimes", "win-x64", "native", "e_sqlite3.dll");
        System.Runtime.InteropServices.NativeLibrary.SetDllImportResolver(provider,
            (library, _, _) => library == "e_sqlite3"
                ? System.Runtime.InteropServices.NativeLibrary.Load(nativePath) : IntPtr.Zero);
        var batteries = Assembly.Load("SQLitePCLRaw.batteries_v2").GetType("SQLitePCL.Batteries_V2", true)!;
        NativeReflection.Static(batteries, "Init");
        var path = Path.Combine(Path.GetTempPath(), "mcp-first-table-schema-" + Guid.NewGuid().ToString("N") + ".sqlite3");
        try
        {
            using (var database = AccountAuthoritySnapshot.OpenSqlite(path, false))
            {
                using var command = database.CreateCommand();
                command.CommandText = "PRAGMA user_version=1";
                command.ExecuteNonQuery();
                Check(NativeProcessAppDatabase.InspectReadOnlyTableSchema(path, null).Objects.Count == 0, "Exact provider confirms the SQLite schema starts empty.");
                command.CommandText = "CREATE TABLE FixtureTable (ID INTEGER PRIMARY KEY AUTOINCREMENT, Label TEXT)";
                command.ExecuteNonQuery();
                var schema = NativeProcessAppDatabase.InspectReadOnlyTableSchema(path, "FixtureTable");
                Check(NativeFirstTableCreate.MatchesSchema("FixtureTable", "Label", ["ID", "Label"], schema), "Physical SQLite and complete model columns agree.");
                Check(!NativeFirstTableCreate.MatchesSchema("FixtureTable", "Missing", ["ID", "Label"], schema) &&
                    !NativeFirstTableCreate.MatchesSchema("FixtureTable", "Label", ["Label"], schema), "Missing physical/model column evidence fails.");
                command.CommandText = "INSERT INTO FixtureTable(Label) VALUES ('synthetic')";
                command.ExecuteNonQuery();
                Check(!NativeFirstTableCreate.MatchesSchema("FixtureTable", "Label", ["ID", "Label"],
                    NativeProcessAppDatabase.InspectReadOnlyTableSchema(path, "FixtureTable")), "Unexpected rows fail.");
                command.CommandText = "DELETE FROM FixtureTable; CREATE TABLE OtherTable (ID INTEGER)";
                command.ExecuteNonQuery();
                Check(!NativeFirstTableCreate.MatchesSchema("FixtureTable", "Label", ["ID", "Label"],
                    NativeProcessAppDatabase.InspectReadOnlyTableSchema(path, "FixtureTable")), "Unrelated database object fails.");
            }
        }
        finally { File.Delete(path); }
        Console.WriteLine("PASS exact V12 SQLite provider first-table request and physical schema guards.");
    }

}
