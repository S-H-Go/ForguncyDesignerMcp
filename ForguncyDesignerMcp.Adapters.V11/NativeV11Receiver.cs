using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Called on the actual Designer UI scheduler by a host that owns the trusted
// destination policy and can independently bind/read its newly opened session.
// No Hook, fabricated session, direct registry write or failure cleanup.
internal static class NativeV11Receiver
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateDirectoryW(string path, IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    internal static IDisposable LeasePolicyRoot(string root, bool createMissing = true)
    {
        if (!Path.IsPathFullyQualified(root)) throw new InvalidDataException("The server receiver root is not absolute.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var volume = Path.GetPathRoot(full) ?? throw new InvalidDataException("The server receiver root has no volume.");
        var segments = Path.GetRelativePath(volume, full).Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var handles = new List<SafeFileHandle>();
        try
        {
            var current = volume;
            foreach (var segment in segments)
            {
                if (segment is "." or "..") throw new InvalidDataException("The receiver root is not canonical.");
                current = Path.Combine(current, segment);
                if (!Directory.Exists(current) && (!createMissing || !CreateDirectoryW(current, IntPtr.Zero) && !Directory.Exists(current)))
                    throw new IOException("The server receiver root could not be created.");
                // FILE_LIST_DIRECTORY is required: an attributes-only handle
                // can be excluded from Windows sharing checks and permit rename.
                var handle = CreateFileW(current, 0x81, 0x3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("The server receiver root could not be safely held."); }
                handles.Add(handle);
                if ((File.GetAttributes(current) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                    throw new InvalidDataException("The server receiver root contains a link or non-directory.");
            }
            return new DirectoryLease(handles);
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }
    private sealed class DirectoryLease(List<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose() { foreach (var handle in handles.AsEnumerable().Reverse()) handle.Dispose(); }
    }

    internal static async Task<CollaborationReceiveReceipt> ExecuteAsync(AdapterHostContext context,
        CollaborationReceiveRequest request, string approvedRoot, Action verifyOldBaseline,
        Func<Task<ProjectSnapshot>> captureActualNewSnapshot, Func<IReadOnlyList<PluginInstallationInfo>> installedPlugins, CancellationToken cancellationToken)
    {
        var dispatched = false;
        var sourceUnchanged = false;
        string? working = null;
        string phase = "preflight";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            verifyOldBaseline();
            if (!Guid.TryParse(request.OperationId, out var operationId) || operationId == Guid.Empty ||
                !Uri.TryCreate(request.RemoteUrl, UriKind.Absolute, out var remoteUri) || remoteUri.Scheme is not ("file" or "https") || remoteUri.IsUnc || !string.IsNullOrEmpty(remoteUri.Query) || !string.IsNullOrEmpty(remoteUri.Fragment) ||
                !string.IsNullOrEmpty(remoteUri.UserInfo) || string.IsNullOrWhiteSpace(request.Branch) ||
                request.Branch.Any(char.IsWhiteSpace) || request.Branch.Contains("..", StringComparison.Ordinal) ||
                request.ExpectedRemoteHead.Length is not (40 or 64) || !request.ExpectedRemoteHead.All(Uri.IsHexDigit))
                throw new InvalidDataException("The receiver operation, remote, branch or expected commit is invalid.");
            if (!File.Exists(request.Target.ProjectPath) || !Path.GetExtension(request.Target.ProjectPath).Equals(".fgcc", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The original saved standalone archive is unavailable.");
            using var rootLease = LeasePolicyRoot(approvedRoot);
            var destination = NativeV11ReceiverContract.RequireFreshDestination(approvedRoot, request.DestinationParent, request.Target.ProjectPath);
            var contract = NativeV11ReceiverContract.Inspect(context.DesignerAssembly);
            var manager = contract.Manager.GetProperty("Instance", NativeV11Reflection.Flags)!.GetValue(null)
                ?? throw new InvalidOperationException("The native collaboration manager is unavailable.");
            var actor = RequireNativeActor(manager, request.ExpectedUser);
            using var sourceParentLease = LeasePolicyRoot(Path.GetDirectoryName(Path.GetFullPath(request.Target.ProjectPath))!, false);
            using var original = new FileStream(request.Target.ProjectPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            string OriginalHash() { original.Position = 0; return Convert.ToHexString(SHA256.HashData(original)); }
            var originalHash = OriginalHash();
            var before = context.CaptureSnapshot();
            if (before.Target != request.Target || before.HasUnsavedChanges || before.Busy || before.InCollaborationMode)
                throw new InvalidOperationException("The original native session is not the reviewed clean standalone document.");
            var oldWorkbook = NativeV11Reflection.Get(context.NativeDocument, "Workbook") ??
                throw new InvalidOperationException("The original native workbook is unavailable.");
            void VerifyStillOriginal()
            {
                var window = System.Windows.Application.Current?.MainWindow ?? throw new InvalidOperationException("The original Designer window is unavailable.");
                var current = context.CaptureSnapshot();
                if (!ReferenceEquals(NativeV11Reflection.Get(window, "SaveLoadManager"), context.NativeDocument) ||
                    !ReferenceEquals(NativeV11Reflection.Get(context.NativeDocument, "Workbook"), oldWorkbook) ||
                    NativeV11Reflection.Get(context.NativeDocument, "ContainsUnsavedChanges") is not false ||
                    NativeV11Reflection.Get(context.NativeDocument, "Opening") is not false ||
                    NativeV11Reflection.Get(context.NativeDocument, "IsImporting") is not false ||
                    current.Target != before.Target || current.HasUnsavedChanges || current.Busy || OriginalHash() != originalHash)
                    throw new InvalidOperationException("The original native document, workbook, edit state or source changed before receiver open.");
            }
            using var remoteLease = LeaseFileRemote(request.RemoteUrl, destination);
            var remote = await ReadRemoteHeadAsync(request.RemoteUrl, request.Branch, cancellationToken);
            if (!string.Equals(remote, request.ExpectedRemoteHead, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The remote branch differs from the reviewed commit.");
            verifyOldBaseline();
            if (OriginalHash() != originalHash || context.CaptureSnapshot() != before)
                throw new InvalidOperationException("The original document changed before native receive.");
            RequireNativeActor(manager, actor);
            _ = NativeV11ReceiverContract.RequireFreshDestination(approvedRoot, destination, request.Target.ProjectPath);
            if (!CreateDirectoryW(destination, IntPtr.Zero)) throw new IOException("The receiver destination could not be reserved exclusively.");
            using var destinationLease = LeasePolicyRoot(destination);
            if (Directory.EnumerateFileSystemEntries(destination).Any())
                throw new IOException("The newly reserved receiver destination is not empty.");
            var option = contract.OptionConstructor.Invoke([true, request.RemoteUrl, request.Branch, destination]);
            var task = contract.TaskConstructor.Invoke([false, false, 100]);
            phase = "native-clone"; dispatched = true;
            var providerType = contract.Option.Assembly.GetType("Forguncy.Collaboration.CollaborationService", true)!;
            object Provider() => providerType.GetProperty("Collaboration", NativeV11Reflection.Flags)!.GetValue(null)
                ?? throw new InvalidOperationException("The native collaboration provider is unavailable.");
            var provider = Provider();
            var gitProviderType = contract.Option.Assembly.GetType("Forguncy.Collaboration.Git.GitCollaboration", true)!;
            if (provider.GetType() != gitProviderType)
                throw new InvalidOperationException("The actual native collaboration provider is not the pinned Git provider.");
            var initializeProvider = gitProviderType.GetMethod("InitializeWorkingDirectory", NativeV11Reflection.Flags, null, [contract.Option], null)
                ?? throw new MissingMethodException("The native provider initialization entry is unavailable.");
            var updateProvider = gitProviderType.GetMethod("Update", NativeV11Reflection.Flags, null, Type.EmptyTypes, null)
                ?? throw new MissingMethodException("The native provider update entry is unavailable.");
            if (initializeProvider.ReturnType != typeof(void) || updateProvider.ReturnType != typeof(bool))
                throw new InvalidOperationException("The native provider clone signatures differ from the pinned builds.");
            // Manager.TryInitializeAndUpdateWorkingDirectory and its Update
            // wrapper display modal error/conflict dialogs. The local-file
            // branch calls the same native provider operations directly so a
            // failed clone is reported as a receipt, never as a UI prompt.
            try
            {
                initializeProvider.Invoke(provider, [option]);
                if (updateProvider.Invoke(provider, null) is not true)
                    throw new InvalidOperationException("Native receiver update returned false.");
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException?.GetType().Name == "Collaboration401Exception")
            { throw new BridgeException("ReceiveRemoteAuthenticationOrTransportFailed", "Native non-interactive remote authentication failed."); }
            working = NativeV11Reflection.Get(provider, "WorkingDirectory") as string ?? throw new InvalidOperationException("Native receiver working directory unavailable.");
            var fullWorking = Path.TrimEndingDirectorySeparator(Path.GetFullPath(working));
            if (!string.Equals(Path.GetDirectoryName(fullWorking), destination, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(fullWorking) ||
                (File.GetAttributes(fullWorking) & FileAttributes.ReparsePoint) != 0 ||
                NativeV11Reflection.Get(provider, "RemoteAddress") as string != request.RemoteUrl || NativeV11Reflection.Get(provider, "BranchName") as string != request.Branch)
                throw new InvalidOperationException("Native receiver destination or provider binding differs from the request.");
            var probe = new GitProbe(fullWorking);
            var git = await probe.CaptureAsync();
            if (!git.IndexClean || !string.Equals(git.Head, request.ExpectedRemoteHead, StringComparison.OrdinalIgnoreCase) || git.Branch != request.Branch || git.RemoteUrl != request.RemoteUrl ||
                !string.Equals(await ReadRemoteHeadAsync(request.RemoteUrl, request.Branch, cancellationToken), request.ExpectedRemoteHead, StringComparison.OrdinalIgnoreCase) || git.ChangedFiles is null ||
                git.ChangedFiles.Keys.Any(path => path.Equals(".collaboration", StringComparison.OrdinalIgnoreCase) || path.Equals("DocumentInfo", StringComparison.OrdinalIgnoreCase)) ||
                !File.Exists(Path.Combine(fullWorking, ".collaboration")) || !File.Exists(Path.Combine(fullWorking, "DocumentInfo")) ||
                (File.GetAttributes(Path.Combine(fullWorking, ".collaboration")) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(Path.Combine(fullWorking, "DocumentInfo")) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The native clone or its remote/registry baseline is not verified.");
            await NativeV11CollaborationRepository.VerifyRegistryAtHeadAsync(fullWorking, git);
            if (await ReadGitAsync(fullWorking, "hash-object", "--", "DocumentInfo") !=
                await ReadGitAsync(fullWorking, "rev-parse", "HEAD:DocumentInfo"))
                throw new InvalidOperationException("The native document marker differs from the reviewed committed source.");
            phase = "native-format-preflight";
            RequireExactNativeFormat(context.DesignerAssembly, fullWorking, installedPlugins);
            var build = V11BuildProfile.Resolve(context.DesignerAssembly);
            var pluginUtility = context.DesignerAssembly.GetType(build.Version == "11.0.104.0" ? "go" : "gj", true)!;
            var pluginName = NativeV11Reflection.Static(pluginUtility, build.Version == "11.0.104.0" ? "get_aor" : "get_ang") as string;
            if (string.IsNullOrWhiteSpace(pluginName) || Path.GetFileName(pluginName) != pluginName)
                throw new BridgeException("ReceiveUnsupportedFormat", "The native plugin declaration name is invalid.");
            if (File.Exists(Path.Combine(fullWorking, pluginName)) &&
                await ReadGitAsync(fullWorking, "hash-object", "--", pluginName) != await ReadGitAsync(fullWorking, "rev-parse", "HEAD:" + pluginName))
                throw new BridgeException("ReceiveCloneMismatch", "The plugin declaration differs from its committed source.");
            VerifyStillOriginal();
            phase = "native-registry";
            contract.Reload.Invoke(manager, null);
            if (NativeV11Reflection.Get(manager, "Modules") is not IDictionary modules || modules.Count == 0 ||
                !string.Equals(Path.GetFullPath((string)NativeV11Reflection.Get(manager, "ModuleFilePath")!), Path.Combine(fullWorking, ".collaboration"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The native collaboration registry was not loaded from the receiver.");
            phase = "native-open";
            VerifyStillOriginal();
            var opening = contract.Open.Invoke(manager, [task]) as Task ?? throw new InvalidOperationException("The native versioned open did not return a task.");
            await opening;
            if (Convert.ToInt32(NativeV11Reflection.Get(opening, "Result")) != 2)
                throw new InvalidOperationException("The native versioned open did not return OpenInCollaboration.");
            var snapshot = await captureActualNewSnapshot();
            var currentProvider = Provider();
            RequireNativeActor(manager, actor);
            if (snapshot.Target.InstanceId != request.Target.InstanceId || snapshot.Target.DesignerVersion != request.Target.DesignerVersion ||
                snapshot.Target.SessionId == request.Target.SessionId || string.IsNullOrWhiteSpace(snapshot.Target.SessionId) ||
                !string.Equals(Path.GetFullPath(snapshot.Target.ProjectPath), fullWorking, StringComparison.OrdinalIgnoreCase) ||
                !snapshot.InCollaborationMode || snapshot.HasUnsavedChanges || snapshot.Busy || snapshot.CurrentUser != actor ||
                string.IsNullOrWhiteSpace(snapshot.Revision) || NativeV11Reflection.Get(manager, "InCollaborationMode") is not true ||
                NativeV11Reflection.Get(currentProvider, "RemoteAddress") as string != request.RemoteUrl ||
                NativeV11Reflection.Get(currentProvider, "BranchName") as string != request.Branch ||
                !string.Equals(Path.GetFullPath((string)NativeV11Reflection.Get(currentProvider, "WorkingDirectory")!), fullWorking, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The host did not independently bind the actual clean native collaboration session.");
            sourceUnchanged = OriginalHash() == originalHash;
            var finalGit = await probe.CaptureAsync();
            if (!sourceUnchanged || !string.Equals(finalGit.Head, request.ExpectedRemoteHead, StringComparison.OrdinalIgnoreCase) || finalGit.Branch != request.Branch ||
                finalGit.RemoteUrl != request.RemoteUrl || !finalGit.IndexClean || finalGit.ChangedFiles is null ||
                finalGit.ChangedFiles.Keys.Any(path => path.Equals(".collaboration", StringComparison.OrdinalIgnoreCase) || path.Equals("DocumentInfo", StringComparison.OrdinalIgnoreCase)) ||
                !string.Equals(await ReadRemoteHeadAsync(request.RemoteUrl, request.Branch, cancellationToken), request.ExpectedRemoteHead, StringComparison.OrdinalIgnoreCase) ||
                await captureActualNewSnapshot() != snapshot || OriginalHash() != originalHash)
                throw new InvalidOperationException("The original archive or receiver remote changed during native open.");
            return new(request.OperationId, "succeeded", "Received", "Native receiver and new host session were verified.", snapshot,
                request.RemoteUrl, request.Branch, finalGit.Head, true, true, true, true, false, true);
        }
        catch (Exception error) when (error is not OperationCanceledException || dispatched)
        {
            // Do not expose native exception text or delete a partially cloned directory.
            return new(request.OperationId, dispatched ? "verification_failed" : "failed", error is BridgeException bridge ? bridge.Code : "NativeReceiveUnconfirmed",
                "Native receive did not complete; phase=" + phase + "; exception=" + error.GetBaseException().GetType().Name,
                RemoteUrl: Uri.TryCreate(request.RemoteUrl, UriKind.Absolute, out var safeRemote) && string.IsNullOrEmpty(safeRemote.UserInfo) && string.IsNullOrEmpty(safeRemote.Query) && string.IsNullOrEmpty(safeRemote.Fragment) ? request.RemoteUrl : null, Branch: request.Branch, OriginalSourceUnchanged: sourceUnchanged,
                ExecutionMayContinue: dispatched, NativeDispatched: dispatched);
        }
    }

    internal static void RequireExactNativeFormat(System.Reflection.Assembly designer, string workingDirectory, Func<IReadOnlyList<PluginInstallationInfo>>? installedPlugins = null)
    {
        var path = Path.Combine(workingDirectory, "DocumentInfo");
        if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new BridgeException("ReceiveUnsupportedFormat", "The native document version marker is missing or invalid.");
        using var parsed = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(path));
        if (parsed.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object ||
            parsed.RootElement.EnumerateObject().Count(property => property.NameEquals("VersionString")) != 1 ||
            !parsed.RootElement.TryGetProperty("VersionString", out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String ||
            !Version.TryParse(value.GetString(), out var declared))
            throw new BridgeException("ReceiveUnsupportedFormat", "The native document version marker is malformed.");
        var version = designer.GetName().Version!;
        if (declared != version) throw new BridgeException("ReceiveRequiresConversion", "The native document requires another Designer version or conversion; no open was dispatched.");
        var profile = V11BuildProfile.Resolve(designer);
        var loader = designer.GetType(profile.Version == "11.0.104.0" ? "fw" : "fs", true)!;
        var nativeVersion = NativeV11Reflection.Static(loader, "GetOpeningFileVersion", workingDirectory) as Version;
        var designerVersion = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.Utilities", true)!, "GetDesignerVersion") as Version;
        if (nativeVersion != version || designerVersion != version)
            throw new BridgeException("ReceiveRequiresConversion", "The native version probe did not confirm an exact matching format; no open was dispatched.");
        var utility = designer.GetType(profile.Version == "11.0.104.0" ? "go" : "gj", true)!;
        var manifestName = NativeV11Reflection.Static(utility, profile.Version == "11.0.104.0" ? "get_aor" : "get_ang") as string
            ?? throw new BridgeException("ReceiveUnsupportedFormat", "The native plugin declaration name is unavailable.");
        var manifest = V11Safety.Under(workingDirectory, manifestName);
        if (File.Exists(manifest))
        {
            if (new FileInfo(manifest).Length > 16 * 1024 * 1024 || (File.GetAttributes(manifest) & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("ReceiveUnsupportedFormat", "The native plugin declaration is invalid.");
            using var pluginData = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(manifest));
            NativeV11ReceiverContract.RequireDependencies(pluginData.RootElement, installedPlugins ?? (() => throw new BridgeException("ReceiveDependenciesRequireReview", "The installed plugin registry is unavailable.")));
        }
    }

    private static IDisposable? LeaseFileRemote(string remote, string destination)
    {
        if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("The receiver remote URI is invalid.");
        if (!uri.IsFile) return null;
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(uri.LocalPath));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        if (!Directory.Exists(path) || target.Equals(path, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The receiver destination overlaps or cannot verify the file remote.");
        return LeasePolicyRoot(path, false);
    }

    private static UserIdentity RequireNativeActor(object manager, UserIdentity expected) =>
        RequireNativeActorSignature(NativeV11Reflection.Get(manager, "CurrentUser"), expected);

    internal static UserIdentity RequireNativeActorSignature(object? user, UserIdentity expected)
    {
        if (user?.GetType().FullName != "Forguncy.Collaboration.Signature" ||
            NativeV11Reflection.Get(user, "UserName") is not string name || string.IsNullOrWhiteSpace(name) ||
            NativeV11Reflection.Get(user, "Email") is not string email || string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("A complete native collaboration identity is required.");
        var actor = new UserIdentity(name, email);
        if (actor != expected)
            throw new InvalidOperationException("The native collaboration user differs from the reviewed receiver actor.");
        return actor;
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
        { throw new BridgeException("ReceiveRemoteAuthenticationOrTransportFailed", "Non-interactive native and system remote verification failed."); }
    }

    internal static async Task<string> ReadRemoteHeadAsync(string remote, string branch, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GCM_INTERACTIVE"] = "never";
        foreach (var argument in new[] { "ls-remote", "--exit-code", "--", remote, "refs/heads/" + branch }) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Git remote inspection is unavailable.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries); await error;
        if (process.ExitCode != 0)
        {
            if (Uri.TryCreate(remote, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps) return NativeRemoteHead(remote, branch);
            throw new IOException("Git remote branch was not uniquely resolved.");
        }
        if (lines.Length != 1) throw new IOException("Git remote branch was not uniquely resolved.");
        var fields = lines[0].Split('\t');
        if (fields.Length != 2 || fields[1] != "refs/heads/" + branch || fields[0].Length is not (40 or 64) || !fields[0].All(Uri.IsHexDigit))
            throw new IOException("Git remote returned an invalid branch proof.");
        return fields[0];
    }

    private static async Task<string> ReadGitAsync(string workingDirectory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = workingDirectory, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["GIT_TERMINAL_PROMPT"] = "0"; start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Git source inspection is unavailable.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token); var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var value = (await output).Trim(); await error;
        if (process.ExitCode != 0 || value.Length is not (40 or 64) || !value.All(Uri.IsHexDigit))
            throw new IOException("Git source marker proof is unavailable.");
        return value;
    }
}
