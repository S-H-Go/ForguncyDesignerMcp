using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.Loader;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text.Json;

internal static class Program
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    private static readonly string[] Product = ["ForguncyDesignerMcp", "ForguncyDesignerMcp.Core", "ForguncyDesignerMcp.Adapters.V11", "ForguncyDesignerMcp.Adapters.V12", "ForguncyDesignerMcp.Host", "ForguncyDesignerMcp.PlatformRuntime"];
    private static readonly string[] RuntimeDependencies = ["Jint", "Acornima", "JsonPath.Net", "Json.More"];
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 3 || args[0] != "--verify") throw new ArgumentException("Usage: --verify SDK_ROOT BUNDLE_DIRECTORY");
            var root = Path.GetFullPath(args[1]);
            var site = Path.Combine(root, "WebSite");
            var dirs = new[] { Path.Combine(site, "designerBin"), Path.Combine(site, "bin"), Path.Combine(site, "arServerBin") };
            if (!File.Exists(Path.Combine(dirs[0], "Forguncy.dll"))) throw new DirectoryNotFoundException("Native Designer assembly is missing.");
            using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.Combine(dirs[0], "Forguncy.runtimeconfig.json")));
            if (runtimeConfig.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString() != "net8.0" || Environment.Version.Major != 8)
                throw new InvalidOperationException("The probe runtime does not match the installed native Designer runtime.");
            var bundle = Path.GetFullPath(args[2]);
            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                if (Product.Contains(name.Name, StringComparer.Ordinal))
                {
                    var pluginFile = Path.Combine(bundle, name.Name + ".dll");
                    return File.Exists(pluginFile) ? context.LoadFromAssemblyPath(pluginFile) : null;
                }
                foreach (var dir in dirs)
                {
                    var path = Path.Combine(dir, name.Name + ".dll");
                    if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
                }
                return null;
            };
            var paths = Product.ToDictionary(name => name, name => Path.Combine(bundle, name + ".dll"), StringComparer.Ordinal);
            var initialHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in paths)
            {
                if (!File.Exists(pair.Value)) throw new FileNotFoundException("A required candidate plugin assembly is missing.", pair.Value);
                initialHashes[pair.Key] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Value)));
                Console.WriteLine("CANDIDATE " + pair.Key + " " + initialHashes[pair.Key]);
            }
            var initialDependencies = RuntimeDependencies.ToDictionary(name => name,
                name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(bundle, name + ".dll")))), StringComparer.Ordinal);
            // The real Designer plugin load context shares the already-loaded
            // public SDK assemblies from Default. Reproduce that without
            // loading Forguncy.dll or starting a Designer process.
            foreach (var name in new[] { "GrapeCity.Forguncy.Plugin", "GrapeCity.Forguncy.CellTypes", "GrapeCity.Forguncy.CellTypes.Design", "Forguncy.Commands" }) _ = Assembly.Load(name);
            var nativeDependencies = new List<object>();
            foreach (var name in RuntimeDependencies)
            {
                var nativePath = dirs.Select(dir => Path.Combine(dir, name + ".dll")).FirstOrDefault(File.Exists);
                if (nativePath is null) continue;
                var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name);
                var dependency = existing ?? AssemblyLoadContext.Default.LoadFromAssemblyPath(nativePath);
                if (!string.Equals(Path.GetFullPath(dependency.Location), Path.GetFullPath(nativePath), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Default context already contains an unexpected native dependency: " + name);
                nativeDependencies.Add(new { name, path = nativePath, version = dependency.GetName().Version?.ToString(), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))) });
                Console.WriteLine("NATIVE_PRELOADED " + dependency.FullName + " " + nativePath);
            }
            var nativeCommon = Assembly.Load("CommonUtilities");
            var loaderType = nativeCommon.GetType("CommonUtilities.ForguncyPluginLoadContext", true)!;
            var loader = Activator.CreateInstance(loaderType, "isolated-plugin-compatibility", bundle) as AssemblyLoadContext
                ?? throw new InvalidOperationException("The native plugin load context could not be constructed.");
            var loadAssembly = loaderType.GetMethod("LoadAssembly", All, null, [typeof(string), typeof(string)], null)
                ?? throw new MissingMethodException(loaderType.FullName, "LoadAssembly");
            var loadedByName = new Dictionary<string, Assembly>(StringComparer.Ordinal);
            // Match the six reviewed product assembly entries in the plugin manifest.
            foreach (var name in Product)
            {
                var loadedAssembly = loadAssembly.Invoke(loader, [bundle, name + ".dll"]) as Assembly ?? throw new InvalidOperationException("Native plugin loader returned no assembly: " + name);
                if (!ReferenceEquals(AssemblyLoadContext.GetLoadContext(loadedAssembly), loader) ||
                    !string.Equals(Path.GetFullPath(loadedAssembly.Location), Path.GetFullPath(paths[name]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("A candidate did not load from its reviewed path into the native plugin context.");
                loadedByName.Add(name, loadedAssembly);
            }
            var plugin = loadedByName[Product[0]];
            var sdkReferences = plugin.GetReferencedAssemblies().Where(reference => reference.Name is string name &&
                (name.StartsWith("GrapeCity.Forguncy.", StringComparison.Ordinal) || name.StartsWith("Forguncy.Commands", StringComparison.Ordinal)))
                .Select(reference => new { name = reference.Name, version = reference.Version?.ToString() }).ToArray();
            if (sdkReferences.Length == 0 || sdkReferences.Any(reference => reference.version != "11.0.3.0"))
                throw new InvalidOperationException("The candidate bridge was not compiled consistently against the minimum V11.0.3.0 SDK.");
            var cell = plugin.GetType("ForguncyDesignerMcp.ForguncyDesignerMcpCellType", true)!;
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Forguncy")) throw new InvalidOperationException("Designer loaded before the inert CellType constructor.");
            var instance = Activator.CreateInstance(cell) ?? throw new InvalidOperationException("CellType construction returned null.");
            if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Forguncy")) throw new InvalidOperationException("CellType construction loaded Designer and could schedule a bridge.");
            Console.WriteLine("PASS CellType constructor without Designer or bridge scheduling: " + instance.GetType().FullName);
            var attribute = cell.GetCustomAttributesData().Single(a => a.AttributeType.FullName == "System.ComponentModel.DesignerAttribute");
            var designerName = (string)attribute.ConstructorArguments[0].Value!;
            var designerType = plugin.GetType(designerName.Split(',')[0].Trim(), true)!;
            var designerObject = Activator.CreateInstance(designerType) ?? throw new InvalidOperationException("CellType designer construction failed.");
            Console.WriteLine("PASS CellType Designer metadata constructor: " + designerObject.GetType().FullName);
            var loaded = Product.Select(name => loadedByName[name]).ToArray();
            Console.WriteLine("PASS native ForguncyPluginLoadContext.LoadAssembly loaded all six candidate DLLs");
            foreach (var assembly in loaded)
            {
                var types = assembly.GetTypes();
                foreach (var type in types)
                {
                    _ = type.BaseType; _ = type.GetInterfaces();
                    foreach (var method in type.GetMethods(All | BindingFlags.DeclaredOnly)) { _ = method.ReturnType; foreach (var parameter in method.GetParameters()) _ = parameter.ParameterType; }
                    foreach (var property in type.GetProperties(All | BindingFlags.DeclaredOnly)) { _ = property.PropertyType; _ = property.GetAccessors(true); }
                    foreach (var constructor in type.GetConstructors(All | BindingFlags.DeclaredOnly)) foreach (var parameter in constructor.GetParameters()) _ = parameter.ParameterType;
                    foreach (var field in type.GetFields(All | BindingFlags.DeclaredOnly)) _ = field.FieldType;
                }
                Console.WriteLine("PASS reflected types and signatures " + assembly.GetName().Name + " " + types.Length);
                var abi = AbiCheck.ResolveReferencedMembers(assembly);
                if (abi.Errors.Length != 0)
                {
                    foreach (var error in abi.Errors.Take(30)) Console.Error.WriteLine("ABI_UNRESOLVED " + error);
                    throw new InvalidOperationException($"{assembly.GetName().Name}: {abi.Errors.Length} IL member references could not be resolved.");
                }
                Console.WriteLine("PASS resolved IL member references " + assembly.GetName().Name + " " + abi.Count);
            }
            var native = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(dirs[0], "Forguncy.dll"));
            var nativeVersion = native.GetName().Version?.ToString() ?? throw new InvalidOperationException("Native build version is absent.");
            var runtimeDependencies = VerifyRuntime(loadedByName["ForguncyDesignerMcp.Core"], loadedByName["ForguncyDesignerMcp.PlatformRuntime"], loader, bundle, nativeVersion);
            VerifyEmbeddedHttp(loadedByName["ForguncyDesignerMcp.Host"]);
            if (runtimeDependencies.Any(pair => initialDependencies[pair.Key] != pair.Value))
                throw new InvalidOperationException("A runtime dependency changed before or during native loading.");
            CheckNativeVersion(native, Assembly.Load("CommonUtilities").GetType("CommonUtilities.PluginItem", true)!);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(native.Location)));
            var core = loaded.Single(a => a.GetName().Name == Product[1]);
            var identityType = core.GetType("ForguncyDesignerMcp.Core.Platform.AdapterIdentity", true)!;
            var identity = Activator.CreateInstance(identityType, nativeVersion, hash, "compatibility-probe")!;
            var contract = core.GetType("ForguncyDesignerMcp.Core.Platform.IProjectAdapterFactory", true)!;
            var matches = loaded.Where(a => a.GetName().Name is "ForguncyDesignerMcp.Adapters.V11" or "ForguncyDesignerMcp.Adapters.V12")
                .SelectMany(a => a.GetTypes()).Where(t => !t.IsAbstract && contract.IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
                .Select(t => Activator.CreateInstance(t)!).Where(factory => (bool)contract.GetMethod("Supports")!.Invoke(factory, [identity])!).ToArray();
            var expected = nativeVersion.StartsWith("11.", StringComparison.Ordinal) ? Product[2] : Product[3];
            if (matches.Length != 1 || matches[0].GetType().Assembly.GetName().Name != expected) throw new InvalidOperationException("Native build does not select exactly its pinned adapter factory.");
            Console.WriteLine("PASS pinned adapter selection " + nativeVersion + " " + matches[0].GetType().FullName);
            var sdk = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name is string name &&
                (name.StartsWith("GrapeCity.Forguncy.", StringComparison.Ordinal) || name.StartsWith("Forguncy.Commands", StringComparison.Ordinal)))
                .Select(a => new { name = a.GetName().Name, version = a.GetName().Version?.ToString(), path = a.Location,
                    sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(a.Location))) }).OrderBy(a => a.name, StringComparer.Ordinal).ToArray();
            if (sdk.Length == 0 || sdk.Any(item => !dirs.Any(dir => Path.GetFullPath(item.path).StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("A loaded native SDK assembly does not belong to the selected installation.");
            foreach (var item in sdk) Console.WriteLine($"SDK {item.name} {item.version} {item.sha256} {item.path}");
            foreach (var pair in paths)
                if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Value))) != initialHashes[pair.Key])
                    throw new InvalidOperationException("A candidate DLL changed during the native plugin load probe.");
            foreach (var pair in initialDependencies)
                if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(bundle, pair.Key + ".dll")))) != pair.Value)
                    throw new InvalidOperationException("A runtime dependency changed during the native plugin load probe.");
            if (System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle != IntPtr.Zero || System.Windows.Application.Current is { } app && app.Windows.Count != 0)
                throw new InvalidOperationException("The isolated plugin load probe unexpectedly created a window.");
            Console.WriteLine(JsonSerializer.Serialize(new { designerVersion = nativeVersion, designerSha256 = hash, dependencyAccepted = true, typesLoaded = true,
                cellTypeCreated = true, designerCreated = true, adapterResolved = true, nativePluginLoader = true, abiMembersResolved = true,
                runtimeVersion = Environment.Version.ToString(), referencedSdk = sdkReferences, sdkAssemblies = sdk,
                ownFiles = paths.Select(pair => new { path = pair.Key + ".dll", sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(pair.Value))) }).ToArray(),
                embeddedRuntimeExecuted = true,
                embeddedHttpStarted = true,
                nativePreloadedDependencies = nativeDependencies,
                dependencyFiles = runtimeDependencies.Select(pair => new { path = pair.Key + ".dll", sha256 = pair.Value }).ToArray() }));
            return 0;
        }
        catch (Exception e)
        {
            while (e is TargetInvocationException { InnerException: not null }) e = e.InnerException;
            Console.Error.WriteLine(e); return 1;
        }
    }

    private static Dictionary<string, string> VerifyRuntime(Assembly core, Assembly runtimeAssembly, AssemblyLoadContext loader, string bundle, string nativeVersion)
    {
        var target = Activator.CreateInstance(core.GetType("ForguncyDesignerMcp.Core.Platform.ProjectTarget", true)!, "fixture", "isolated", "no-project", nativeVersion)!;
        var descriptor = core.GetType("ForguncyDesignerMcp.Core.Platform.PlatformApiDescriptor", true)!;
        var capability = core.GetType("ForguncyDesignerMcp.Core.Platform.ModuleCapability", true)!;
        var context = Activator.CreateInstance(core.GetType("ForguncyDesignerMcp.Core.Platform.PlatformRuntimeContext", true)!, target,
            Array.CreateInstance(descriptor, 0), Array.CreateInstance(capability, 0))!;
        var contract = core.GetType("ForguncyDesignerMcp.Core.Platform.IPlatformApiInvoker", true)!;
        var invoker = DispatchProxy.Create(contract, typeof(RejectingInvoker));
        var runtimeType = runtimeAssembly.GetType("ForguncyDesignerMcp.PlatformRuntime.PlatformToolRuntime", true)!;
        var runtime = Activator.CreateInstance(runtimeType, [null])!;
        if (Environment.GetEnvironmentVariable("FORGUNCY_TEST_ONLINE_HELP") == "1")
            VerifyOnlineHelp(runtime, context, invoker, nativeVersion);
        var result = (Task)runtimeType.GetMethod("CallToolAsync")!.Invoke(runtime,
            ["execute_code", JsonSerializer.SerializeToElement(new { code = "return 1+2;" }), context, invoker, CancellationToken.None])!;
        result.GetAwaiter().GetResult();
        var receipt = (JsonElement)result.GetType().GetProperty("Result")!.GetValue(result)!;
        var structured = receipt.GetProperty("structuredContent");
        if (structured.GetProperty("status").GetString() != "Completed" || structured.GetProperty("value").GetInt32() != 3)
            throw new InvalidOperationException("Native plugin context did not execute the isolated JS arithmetic probe: " + receipt.GetRawText());
        var pathAssembly = loader.LoadFromAssemblyName(new AssemblyName("JsonPath.Net"));
        var pathType = pathAssembly.GetType("Json.Path.JsonPath", true)!;
        var parse = pathType.GetMethods(BindingFlags.Public | BindingFlags.Static).Single(m => m.Name == "Parse" && m.GetParameters().Length == 2);
        var path = parse.Invoke(null, ["$.value", null])!;
        var evaluate = pathType.GetMethods(BindingFlags.Public | BindingFlags.Instance).Single(m => m.Name == "Evaluate" && m.GetParameters().Length == 2);
        var evaluation = evaluate.Invoke(path, [System.Text.Json.Nodes.JsonNode.Parse("{\"value\":3}"), null])!;
        if (evaluation.GetType().GetProperty("Matches")?.GetValue(evaluation) is not System.Collections.IEnumerable matches || !matches.Cast<object>().Any())
            throw new InvalidOperationException("JSONPath evaluation returned no match.");
        _ = loader.LoadFromAssemblyName(new AssemblyName("Json.More"));
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in RuntimeDependencies)
        {
            var dependency = loader.Assemblies.SingleOrDefault(a => a.GetName().Name == name)
                ?? throw new InvalidOperationException("Native plugin context did not load required runtime dependency: " + name);
            if (!string.Equals(Path.GetFullPath(dependency.Location), Path.GetFullPath(Path.Combine(bundle, name + ".dll")), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Runtime dependency was loaded from an unexpected path: " + name);
            hashes[name] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dependency.Location)));
            Console.WriteLine("PASS native plugin dependency " + name + " " + hashes[name]);
        }
        Console.WriteLine("PASS native plugin runtime execute_code and JSONPath evaluation using isolated fake API");
        return hashes;
    }

    private static void VerifyOnlineHelp(object runtime, object context, object invoker, string version)
    {
        var edition = version switch { "11.0.3.0" => "v11.0", "11.0.104.0" => "v11.1", "12.0.102.0" => "v12.1", _ => throw new NotSupportedException(version) };
        var root = new Uri("https://www.grapecity.com.cn/solutions/huozige/help/docs/" + edition + "/");
        var catalog = (Task<JsonElement>)runtime.GetType().GetMethod("ListToolsAsync")!.Invoke(runtime, [context, CancellationToken.None])!;
        var tool = catalog.GetAwaiter().GetResult().GetProperty("tools").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "search_knowledge_base");
        if (!tool.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean() ||
            tool.GetProperty("inputSchema").GetProperty("required")[0].GetString() != "keyword") throw new Exception("Incorrect knowledge tool schema");
        JsonElement Call(string keyword) => ((Task<JsonElement>)runtime.GetType().GetMethod("CallToolAsync")!.Invoke(runtime,
            ["search_knowledge_base", JsonSerializer.SerializeToElement(new { keyword }), context, invoker, CancellationToken.None])!).GetAwaiter().GetResult();
        if (!Call(" ").GetProperty("isError").GetBoolean()) throw new Exception("Empty knowledge search accepted");
        var pin = runtime.GetType().Assembly.GetType("ForguncyDesignerMcp.PlatformRuntime.HelpKnowledgeBase", true)!
            .GetMethod("VersionedPage", BindingFlags.NonPublic | BindingFlags.Static)!;
        var different = edition == "v12.1" ? "v11.1" : "v12.1";
        foreach (var wrong in new[] { $"https://www.grapecity.com.cn/solutions/huozige/help/docs/{different}/overview", "https://example.org/overview" })
            if (pin.Invoke(null, [root, wrong]) is not null) throw new Exception("Cross-edition or external result admitted");
        var keyword = edition == "v12.1" ? "OPC UA" : "创建数据表";
        var receipt = Call(keyword);
        var result = receipt.GetProperty("structuredContent");
        if (receipt.GetProperty("isError").GetBoolean() || result.GetProperty("documentationVersion").GetString() != edition ||
            !result.GetProperty("knowledgeBaseAvailable").GetBoolean() || !result.GetProperty("versionIndexAvailable").GetBoolean())
            throw new Exception("Live help query failed: " + receipt.GetRawText());
        var articles = result.GetProperty("results").EnumerateArray().ToArray();
        if (articles.Length == 0 || !articles.Any(a => (a.GetProperty("title").GetString() + a.GetProperty("snippet").GetString()).Contains(keyword, StringComparison.OrdinalIgnoreCase)) ||
            articles.Any(a => !a.GetProperty("url").GetString()!.StartsWith(root.AbsoluteUri, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(a.GetProperty("snippet").GetString()))) throw new Exception("Missing version-qualified article for " + keyword + ": " + result.GetRawText());
        Console.WriteLine($"PASS live official knowledge search {version} {edition} {articles.Length} {articles[0].GetProperty("url").GetString()}");
    }

    private static void VerifyEmbeddedHttp(Assembly host)
    {
        var serverType = host.GetType("ForguncyDesignerMcp.Host.EmbeddedMcpHttpServer", true)!;
        var optionsType = host.GetType("ForguncyDesignerMcp.Host.EmbeddedMcpHttpOptions", true)!;
        var constructor = serverType.GetConstructors().Single();
        var factoryType = constructor.GetParameters()[0].ParameterType;
        var factorySignature = factoryType.GetMethod("Invoke")!;
        var parameter = Expression.Parameter(typeof(CancellationToken), "cancellationToken");
        var failure = Expression.New(typeof(InvalidOperationException).GetConstructor([typeof(string)])!, Expression.Constant("Isolated HTTP probe must not open a business session."));
        var factory = Expression.Lambda(factoryType, Expression.Throw(failure, factorySignature.ReturnType), parameter).Compile();
        var server = constructor.Invoke([factory, Activator.CreateInstance(optionsType)!]);
        try
        {
            ((Task)serverType.GetMethod("StartAsync")!.Invoke(server, [0, new string('x', 40), CancellationToken.None])!).GetAwaiter().GetResult();
            var address = (Uri?)serverType.GetProperty("Address")!.GetValue(server) ?? throw new InvalidOperationException("Embedded HTTP server did not publish its address.");
            if (!address.IsLoopback || address.Port == 0) throw new InvalidOperationException("Embedded HTTP server did not bind loopback.");
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var response = client.GetAsync(address).GetAwaiter().GetResult();
            if ((int)response.StatusCode != 401) throw new InvalidOperationException("Embedded HTTP listener did not reject the unauthenticated probe.");
            Console.WriteLine("PASS native plugin context started and reached the loopback HTTP server");
        }
        finally { ((Task)serverType.GetMethod("StopAsync")!.Invoke(server, [CancellationToken.None])!).GetAwaiter().GetResult(); }
    }


    private static void CheckNativeVersion(Assembly designer, Type itemType)
    {
        var getVersion = designer.GetType("Forguncy.Common.Utilities", true)!.GetMethod("GetDesignerVersion", All)!;
        var actual = (Version)getVersion.Invoke(null, null)!;
        if (actual != designer.GetName().Version) throw new InvalidOperationException("Native version checker is not bound to the selected installed Designer build.");
        var checker = designer.GetType("Forguncy.Model.PluginVersionChecker", true)!.GetMethod("CheckPluginDependenceVersion", All, null, [itemType], null)!;
        var item = Activator.CreateInstance(itemType)!;
        var dependency = itemType.GetProperty("dependenceVersion", All)!;
        dependency.SetValue(item, "11.0.3.0"); var supported = checker.Invoke(null, [item])!;
        dependency.SetValue(item, "99.0.0.0"); var tooNew = checker.Invoke(null, [item])!;
        dependency.SetValue(item, null); var noDependency = checker.Invoke(null, [item])!;
        Console.WriteLine($"NATIVE_VERSION {actual} dependency=11.0.3.0 status={supported} ({Convert.ToInt32(supported)}) future={tooNew} ({Convert.ToInt32(tooNew)}) absent={noDependency} ({Convert.ToInt32(noDependency)})");
        if (supported.ToString() != "Loaded" || tooNew.ToString() != "VersionHigherThanDesigner")
            throw new InvalidOperationException("The real native minimum-version checker did not accept the requested dependency or reject a future version.");
        Console.WriteLine("PASS native plugin minimum dependency version and future-version negative control");
    }
}

public class RejectingInvoker : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => throw new InvalidOperationException("The isolated arithmetic probe must not call the API.");
}
