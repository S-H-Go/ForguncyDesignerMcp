using ForguncyDesignerMcp.Core;
using System.IO;
using ForguncyDesignerMcp.Host;
using ForguncyDesignerMcp.PlatformRuntime;

namespace ForguncyDesignerMcp;

internal static class EmbeddedMcpSessionFactory
{
    internal static Task<StdioMcpServer> CreateAsync(InstanceInfo snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(snapshot.SessionId) || string.IsNullOrWhiteSpace(snapshot.ProjectPath) || !Path.IsPathFullyQualified(snapshot.ProjectPath))
            throw new BridgeException("PlatformSessionUnavailable", "当前设计器没有已保存且已注册的工程会话。");
        cancellationToken.ThrowIfCancellationRequested();
        var registry = InstanceRegistry.DefaultDirectory;
        var pipe = new PlatformPipeSession(registry, snapshot);
        return Task.FromResult(new StdioMcpServer(registry, pipe.CreateMcpSession(new PlatformToolRuntime())));
    }
}
