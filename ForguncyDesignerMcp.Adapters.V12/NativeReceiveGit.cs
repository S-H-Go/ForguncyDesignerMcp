using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Network verification fetches into an isolated bare repository without checking out files.
internal sealed class NativeReceiveGit(CollaborationReceiveRequest request) : IDisposable
{
    internal string BarePath { get; private set; } = "";
    private bool network;
    private string? temporaryRoot;
    internal bool IsNetwork => network;
    internal string RepositoryName { get; private set; } = "";
    internal string ExpectedHead => request.ExpectedRemoteHead.ToLowerInvariant();
    internal void Preflight()
    {
        if (!Uri.TryCreate(request.RemoteUrl, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            (uri.Scheme != Uri.UriSchemeHttps && (!uri.IsFile || uri.IsUnc)) ||
            request.ExpectedRemoteHead.Length is not (40 or 64) || !request.ExpectedRemoteHead.All(Uri.IsHexDigit) ||
            !Regex.IsMatch(request.Branch, @"\A[A-Za-z0-9][A-Za-z0-9._/-]*\z", RegexOptions.CultureInvariant) ||
            request.Branch.Contains("..", StringComparison.Ordinal) || request.Branch.Contains("//", StringComparison.Ordinal) ||
            request.Branch.EndsWith('.') || request.Branch.EndsWith('/') || request.Branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            throw Error("ReceiveRemoteInvalid", "The credential-free remote, exact branch, or full commit identity is invalid.");
        network = uri.Scheme == Uri.UriSchemeHttps;
        RepositoryName = Path.GetFileNameWithoutExtension(network ? Uri.UnescapeDataString(uri.Segments.LastOrDefault() ?? "").TrimEnd('/') : uri.LocalPath.TrimEnd('\\', '/'));
        if (string.IsNullOrWhiteSpace(RepositoryName) || RepositoryName is "." or ".." || RepositoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw Error("ReceiveRemoteInvalid", "The remote repository name is invalid.");
        if (network)
        {
            RequireRemoteHead();
            temporaryRoot = Path.Combine(Path.GetTempPath(), "forguncy-receive-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            BarePath = Path.Combine(temporaryRoot, "review.git");
            Run(temporaryRoot, false, "init", "--bare", "--", BarePath);
            try { RunNetwork(temporaryRoot, "--git-dir=" + BarePath, "fetch", "--no-tags", "--", request.RemoteUrl, "refs/heads/" + request.Branch + ":refs/heads/" + request.Branch); }
            catch (BridgeException e) when (e.Code == "ReceiveRemoteAuthenticationOrTransportFailed")
            {
                BarePath = Path.Combine(temporaryRoot, "native-review.git");
                NativeCloneBare(request.RemoteUrl, request.Branch, BarePath);
            }
            if (Run(BarePath, true, "rev-parse", "--verify", "refs/heads/" + request.Branch + "^{commit}") != ExpectedHead)
                throw Error("ReceiveRemoteHeadChanged", "The fetched branch does not match the expected commit.");
        }
        else
        {
            if (!Path.IsPathFullyQualified(uri.LocalPath) || request.RemoteUrl != new Uri(Path.GetFullPath(uri.LocalPath)).AbsoluteUri)
                throw Error("ReceiveRemoteInvalid", "The local remote URI is not canonical.");
            BarePath = Path.GetFullPath(uri.LocalPath).TrimEnd('\\', '/');
            if (!Directory.Exists(BarePath)) throw Error("ReceiveRemoteInvalid", "The local remote repository does not exist.");
            NativeReceiveDestination.RequireUnlinkedExistingChain(BarePath);
            RequireNoLinks(BarePath);
            if (Run(BarePath, true, "rev-parse", "--is-bare-repository") != "true") throw Error("ReceiveRemoteInvalid", "Remote must be a normal bare repository.");
            RequireRemoteHead();
        }
        var tree = Run(BarePath, true, "ls-tree", "-r", "--full-tree", ExpectedHead);
        var entries = tree.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (entries.Length < 3 || entries.Any(x => !x.StartsWith("100644 blob ", StringComparison.Ordinal) && !x.StartsWith("100755 blob ", StringComparison.Ordinal)))
            throw Error("ReceiveRemoteTreeUnsupported", "The remote tree must contain regular files only.");
        foreach (var entry in entries.Where(x => x.EndsWith("\t.gitattributes", StringComparison.OrdinalIgnoreCase) || x.Contains("/.gitattributes", StringComparison.OrdinalIgnoreCase)))
        {
            var name = entry[(entry.IndexOf('\t') + 1)..];
            RequireSafeAttributes(Run(BarePath, true, "show", ExpectedHead + ":" + name));
        }
        var names = entries.Select(x => x[(x.IndexOf('\t')+1)..]).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains(".collaboration") || !names.Contains("DocumentInfo")) throw Error("ReceiveRemoteInvalid", "Remote native project markers are missing.");
        RequireDocumentVersion(Run(BarePath, true, "show", ExpectedHead + ":DocumentInfo"));
    }
    internal static void RequireDocumentVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("VersionString", out var value) || !Version.TryParse(value.GetString(), out var version))
                throw Error("ReceiveVersionUnsupported", "Native DocumentInfo has no valid version.");
            var installed = Version.Parse(V12AdapterFactory.Version);
            if (version < installed) throw Error("ReceiveRequiresConversion", "The project requires an interactive version upgrade.");
            if (version > installed) throw Error("ReceiveVersionUnsupported", "The project was created by a newer Designer.");
        }
        catch (JsonException) { throw Error("ReceiveVersionUnsupported", "Native DocumentInfo is invalid."); }
    }
    internal void RequireRemoteHead()
    {
        string actual;
        if (network)
        {
            try
            {
                var output = RunNetwork(Path.GetTempPath(), "ls-remote", "--exit-code", "--", request.RemoteUrl, "refs/heads/" + request.Branch);
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var parts = lines.Length == 1 ? lines[0].Split('\t') : [];
                actual = parts.Length == 2 && parts[1] == "refs/heads/" + request.Branch ? parts[0] : "";
            }
            catch (BridgeException e) when (e.Code == "ReceiveRemoteAuthenticationOrTransportFailed") { actual = NativeRemoteHead(request.RemoteUrl, request.Branch); }
        }
        else actual = Run(BarePath, true, "rev-parse", "--verify", "refs/heads/" + request.Branch + "^{commit}");
        if (!string.Equals(actual, ExpectedHead, StringComparison.OrdinalIgnoreCase))
            throw Error("ReceiveRemoteHeadChanged", "The actual remote branch does not match the expected commit.");
    }
    internal void VerifyClone(string working)
    {
        if (Run(working, false, "rev-parse", "HEAD") != ExpectedHead || Run(working, false, "branch", "--show-current") != request.Branch ||
            Run(working, false, "remote", "get-url", "origin") != request.RemoteUrl)
            throw Error("ReceiveCloneMismatch", "Native clone head, branch, or remote differs from the reviewed source.");
        foreach (var file in new[] { ".collaboration", "DocumentInfo" })
            if (!File.Exists(Path.Combine(working,file)) || Run(working, false, "hash-object", "--", file) != Run(working, false, "rev-parse", "HEAD:" + file))
                throw Error("ReceiveCloneMismatch", "Native clone marker differs from its committed source.");
        var status = Run(working, false, "status", "--porcelain=v1", "--untracked-files=all", "--ignored=matching");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "?? .local/BreakpointSettings", "?? .local/UserLocalSettings" };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in status.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!allowed.Contains(entry) || !seen.Add(entry)) throw Error("ReceiveCloneDirty", "Native clone contains unexpected changes.");
            var path = Path.Combine(working, entry[3..].Replace('/', Path.DirectorySeparatorChar));
            NativeReceiveDestination.RequireUnlinkedExistingChain(path);
            if (!File.Exists(path)) throw Error("ReceiveCloneDirty", "Native local settings are not regular files.");
        }
        RequireRemoteHead();
    }
    private static (object Provider, Type Repository) NativeProvider(string remote)
    {
        var assembly = System.Reflection.Assembly.Load("Forguncy.Collaboration");
        var type = assembly.GetType("Forguncy.Collaboration.Git.GitCollaboration", true)!;
        var provider = Activator.CreateInstance(type)!;
        type.GetProperty("RemoteAddress")!.SetValue(provider, remote);
        var repository = System.Reflection.Assembly.Load("LibGit2Sharp").GetType("LibGit2Sharp.Repository", true)!;
        return (provider, repository);
    }

    internal static string NativeRemoteHead(string remote, string branch)
    {
        try
        {
            var (provider, repository) = NativeProvider(remote);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var credentials = provider.GetType().GetMethod("CreateCredentialsProvider", flags)!.Invoke(provider, null);
            var handlerType = System.Reflection.Assembly.Load("LibGit2Sharp").GetType("LibGit2Sharp.Handlers.CredentialsHandler", true)!;
            var refs = (System.Collections.IEnumerable)repository.GetMethod("ListRemoteReferences", [typeof(string), handlerType])!.Invoke(null, [remote, credentials])!;
            var matches = refs.Cast<object>().Where(item => item.GetType().GetProperty("CanonicalName")?.GetValue(item) as string == "refs/heads/" + branch).ToArray();
            var id = matches.Length == 1 ? matches[0].GetType().GetProperty("TargetIdentifier")?.GetValue(matches[0]) as string : null;
            if (id is null || id.Length is not (40 or 64) || !id.All(Uri.IsHexDigit)) throw new InvalidDataException();
            return id;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        { throw Error("ReceiveRemoteAuthenticationOrTransportFailed", "Non-interactive native and system remote verification failed."); }
    }

    private static void NativeCloneBare(string remote, string branch, string destination)
    {
        try
        {
            var (provider, repository) = NativeProvider(remote);
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var options = provider.GetType().GetMethod("GetCloneOptions", flags)!.Invoke(provider, [null])!;
            var optionType = options.GetType();
            optionType.GetProperty("IsBare")!.SetValue(options, true);
            optionType.GetProperty("Checkout")!.SetValue(options, false);
            optionType.GetProperty("BranchName")!.SetValue(options, branch);
            repository.GetMethod("Clone", [typeof(string), typeof(string), optionType])!.Invoke(null, [remote, destination, options]);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        { throw Error("ReceiveRemoteAuthenticationOrTransportFailed", "Non-interactive native and system remote verification failed."); }
    }

    internal static void RequireDependencies(System.Text.Json.JsonElement declarations, Func<IReadOnlyList<PluginInstallationInfo>> installed)
    {
        if (declarations.ValueKind != System.Text.Json.JsonValueKind.Array || declarations.GetArrayLength() > 10000)
            throw new BridgeException("ReceiveDependenciesRequireReview", "The native plugin declaration is invalid.");
        if (declarations.GetArrayLength() == 0) return;
        var available = installed();
        var registry = new Dictionary<Guid, Version>();
        foreach (var item in available)
            if (!Guid.TryParse(item.PluginGuid, out var id) || id == Guid.Empty || !Version.TryParse(item.Version, out var version) || !registry.TryAdd(id, version))
                throw new BridgeException("NativePluginRegistryUnverified", "The installed plugin registry contains invalid or duplicate identities.");
        var seen = new HashSet<Guid>();
        foreach (var item in declarations.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object ||
                item.EnumerateObject().Count(p => p.NameEquals("guid")) != 1 || item.EnumerateObject().Count(p => p.NameEquals("version")) != 1 ||
                !item.TryGetProperty("guid", out var g) || g.ValueKind != System.Text.Json.JsonValueKind.String || !Guid.TryParse(g.GetString(), out var id) || id == Guid.Empty ||
                !item.TryGetProperty("version", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String || !Version.TryParse(v.GetString(), out var version) || !seen.Add(id))
                throw new BridgeException("ReceiveDependenciesRequireReview", "A native plugin dependency has an invalid or duplicate GUID/version.");
            if (!registry.TryGetValue(id, out var actual) || actual != version)
                throw new BridgeException("ReceiveDependencyUnavailable", "A declared native plugin or its exact version is not installed.");
        }
    }

    internal static void RequireSafeAttributes(string contents)
    {
        if (contents.Length > 1024 * 1024) throw Error("ReceiveRemoteTreeUnsupported", "Git attributes are too large.");
        foreach (var raw in contents.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts[0].StartsWith('[') || parts[0].StartsWith('!') || parts[0].Contains('\\') ||
                parts.Skip(1).Any(part => part is not ("text" or "-text" or "eol=crlf" or "eol=lf")))
                throw Error("ReceiveRemoteTreeUnsupported", "Git attributes contain an unsupported checkout directive.");
        }
    }

    public void Dispose()
    {
        if (temporaryRoot is not null && Directory.Exists(temporaryRoot))
        {
            RequireNoLinks(temporaryRoot);
            foreach (var path in Directory.EnumerateFiles(temporaryRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Directory.Delete(temporaryRoot, true);
        }
        temporaryRoot = null;
    }

    private static string RunNetwork(string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = directory };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_TERMINAL_PROMPT"] = "0"; info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var arg in new[] { "-c", "protocol.ext.allow=never", "-c", "core.hooksPath=NUL" }) info.ArgumentList.Add(arg);
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw Error("ReceiveGitUnavailable", "Git could not be started.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw Error("ReceiveGitTimeout", "Non-interactive remote verification timed out."); }
        Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0 || stdout.Result.Length > 16 * 1024 * 1024)
            throw Error("ReceiveRemoteAuthenticationOrTransportFailed", "Remote verification failed; check non-interactive credentials and network access.");
        return stdout.Result.TrimEnd('\r', '\n').Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void RequireNoLinks(string root)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw Error("ReceiveRemoteLinked", "Remote repository contains a linked path.");
            if ((attributes & FileAttributes.Directory) != 0) RequireNoLinks(entry);
        }
    }
    private static string Run(string directory, bool bare, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true, CreateNoWindow=true,
            StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8, WorkingDirectory=directory };
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["GIT_CONFIG_NOSYSTEM"]="1"; info.Environment["GIT_CONFIG_GLOBAL"]="NUL";
        info.Environment["GIT_TERMINAL_PROMPT"]="0"; info.Environment["GIT_OPTIONAL_LOCKS"]="0";
        info.ArgumentList.Add("--no-replace-objects");
        if (bare) info.ArgumentList.Add("--git-dir=" + directory);
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw Error("ReceiveGitUnavailable", "Git could not be started.");
        var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw Error("ReceiveGitTimeout", "Read-only Git verification timed out."); }
        Task.WhenAll(stdout,stderr).GetAwaiter().GetResult();
        if (process.ExitCode != 0 || stdout.Result.Length > 16 * 1024 * 1024) throw Error("ReceiveGitVerificationFailed", "Read-only Git verification failed.");
        return stdout.Result.TrimEnd('\r','\n').Replace("\r\n", "\n", StringComparison.Ordinal);
    }
    private static BridgeException Error(string code,string message) => new(code,message);
}

