using System.Text;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Host;
using ForguncyDesignerMcp.PlatformRuntime;

Console.InputEncoding = new UTF8Encoding(false);
Console.OutputEncoding = new UTF8Encoding(false);
var registry = InstanceRegistry.DefaultDirectory;
string? projectPath = null;
string? backend = null;
try
{
    for (var index = 0; index < args.Length; index++)
    {
        if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])) throw new ArgumentException();
        switch (args[index - 1])
        {
            case "--registry-dir": registry = Path.GetFullPath(args[index]); break;
            case "--project-path": projectPath = Path.GetFullPath(args[index]); break;
            case "--backend": backend = args[index]; break;
            default: throw new ArgumentException();
        }
    }
    backend ??= projectPath is null ? "extensions" : "platform";
    if (backend is not ("extensions" or "platform" or "native") || (backend is "platform" or "native" && projectPath is null)) throw new ArgumentException();
}
catch (ArgumentException)
{
    await Console.Error.WriteLineAsync("Usage: ForguncyDesignerMcp.Host [--registry-dir <directory>] [--project-path <exact project path>] [--backend platform|native|extensions]");
    return 2;
}
var server = backend == "platform"
    ? new StdioMcpServer(registry, new PlatformPipeSession(registry, projectPath!).CreateMcpSession(new PlatformToolRuntime()))
    : new StdioMcpServer(registry, backend == "native" ? projectPath : null);
await server.RunAsync(Console.In, Console.Out);
return 0;
