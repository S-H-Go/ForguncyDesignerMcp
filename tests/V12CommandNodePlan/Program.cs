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
        object New(string type, params object[] values) => Activator.CreateInstance(native.GetType(type, true)!, Members, null, values, null)!;
        var scratch = Path.Combine(Path.GetTempPath(), "mcp-command-node-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var settings = New("lt");
        var nativeState = native.GetType("s", true)!.GetProperty("Instance", Members)!.GetValue(null)!;
        Set(nativeState, "et", settings);
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
        Console.WriteLine("PASS exact V12 node update owner, missing/ambiguous owner, and mandatory effect plan.");
    }

    private static object? Get(object obj, string name) => obj.GetType().GetProperty(name, Members)!.GetValue(obj);
    private static void Set(object obj, string name, object value) => obj.GetType().GetProperty(name, Members)!.SetValue(obj, value);
    private static void Expect(string code, Action action)
    {
        try { action(); }
        catch (BridgeException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
}
