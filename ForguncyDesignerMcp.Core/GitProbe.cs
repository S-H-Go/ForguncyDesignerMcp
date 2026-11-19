using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace ForguncyDesignerMcp.Core;

// Read-only Git inspection. Only the designer's native operations perform writes.
public sealed class GitProbe(string projectPath)
{
    public async Task<GitState> CaptureAsync()
    {
        var head = (await RunAsync("rev-parse", "HEAD")).Trim();
        var branch = (await RunAsync("symbolic-ref", "--quiet", "--short", "HEAD")).Trim();
        var upstream = await OptionalAsync("rev-parse", "--abbrev-ref", "@{upstream}");
        var upstreamHead = upstream is null ? null : await OptionalAsync("rev-parse", "@{upstream}");
        var remote = await OptionalAsync("config", "--get", "branch." + branch + ".remote");
        if (remote == ".") remote = null;
        var url = remote is null ? null : await OptionalAsync("remote", "get-url", "--push", remote);
        // Do not expose embedded credentials through MCP responses.
        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.UserInfo)
            && (uri.Scheme is "https" or "http" || uri.UserInfo.Contains(':')))
            throw new BridgeException("CredentialInRemoteUrl", "Git 远端 URL 含嵌入式凭据，请改用凭据管理器后重试。");
        var index = await RunAsync("diff", "--cached", "--name-only", "-z");
        var diff = await RunAsync("diff", "--no-ext-diff", "--no-textconv", "--binary", "HEAD", "--");
        var identity = (await RunAsync("show", "-s", "--format=%an%x00%ae%x00%cn%x00%ce", "HEAD")).TrimEnd('\r', '\n').Split('\0');
        var changedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tracked = await RunAsync("diff", "--no-ext-diff", "--no-renames", "--name-only", "-z", "HEAD", "--");
        var untracked = await RunAsync("ls-files", "--others", "--exclude-standard", "--exclude=.local/", "-z");
        var paths = (tracked + untracked).Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var relative = SessionGuard.RelativeFile(projectPath, path);
            var full = Path.Combine(projectPath, relative);
            if (File.Exists(full))
            {
                await using var stream = File.OpenRead(full);
                changedFiles[relative] = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            }
            else changedFiles[relative] = "deleted";
        }
        return new(head, branch, remote, url, upstream, upstreamHead, index.Length == 0, Wire.Hash(diff),
            new(identity[0], identity[1]), new(identity[2], identity[3]), changedFiles);
    }

    public async Task<string?> RemoteHeadAsync(GitState state)
    {
        if (state.RemoteName is null || state.RemoteUrl is null) return null;
        // Verify the exact upstream ref, not an inferred branch with the same name.
        var remoteRef = await OptionalAsync("config", "--get", "branch." + state.Branch + ".merge");
        if (remoteRef is null || !remoteRef.StartsWith("refs/heads/", StringComparison.Ordinal))
            throw new BridgeException("MissingRemoteRef", "当前分支没有明确的远端分支配置。");
        var output = await RunAsync("ls-remote", "--exit-code", state.RemoteName, remoteRef);
        return output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    }

    public async Task<string[]> CommittedFilesAsync(string oldHead, string newHead)
    {
        var output = await RunAsync("diff", "--no-ext-diff", "--no-renames", "--name-only", "-z", oldHead, newHead, "--");
        return output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private async Task<string?> OptionalAsync(params string[] args)
    {
        var result = await ExecuteAsync(args);
        return result.Code == 0 ? result.Output.Trim() : null;
    }

    private async Task<string> RunAsync(params string[] args)
    {
        var result = await ExecuteAsync(args);
        if (result.Code != 0) throw new BridgeException("GitReadFailed", "Git 状态核验失败（" + args[0] + "，退出码 " + result.Code + "）。");
        return result.Output;
    }

    private async Task<(int Code, string Output)> ExecuteAsync(string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = projectPath, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.ArgumentList.Add("-c"); info.ArgumentList.Add("core.quotepath=false");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new BridgeException("GitUnavailable", "无法启动 Git。");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new BridgeException("GitReadTimeout", "Git 只读核验超时，未开始原生协同操作。"); }
        var output = await stdout; await stderr;
        if (output.Length > 16 * 1024 * 1024) throw new BridgeException("DiffTooLarge", "工作区差异过大，请缩小待处理范围后重试。");
        return (process.ExitCode, output);
    }
}
