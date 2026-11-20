using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>
/// Fixed-build native collaboration boundary. Module selection comes from the
/// adapter's complete native effect plan, never client reflection names. This
/// class does not implement locks, Git writes, native document save, or retries.
/// Native Checkout/CheckInAsync own their normal registration/authorization.
/// </summary>
public sealed class NativeCollaboration
{
    internal sealed record EditRegistration(string Owner, IReadOnlySet<string> Files, Action Register);
    private const string Version = "12.0.102.0";
    private const string Fingerprint = "A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246";
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private readonly AdapterHostContext context;
    private readonly Func<ModuleAddress, object?> resolveNativeModule;
    private readonly Func<object, ModuleAddress?> mapNativeModule;
    private readonly NativeCollaborationGit? git;
    private readonly ProjectTarget boundTarget;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly Dictionary<string, (string Hash, CollaborationMutationReceipt Receipt)> receipts = new(StringComparer.Ordinal);
    private bool uncertain;
    private NativeCollaborationGit Git => git ?? throw Error("GitRepositoryUnavailable", "A real bound collaboration Git observer is required before native repository operations.");

    public NativeCollaboration(AdapterHostContext context, Func<ModuleAddress, object?> resolveNativeModule,
        Func<object, ModuleAddress?> mapNativeModule, NativeCollaborationGit? git = null)
    {
        this.context = context; this.resolveNativeModule = resolveNativeModule; this.mapNativeModule = mapNativeModule; this.git = git;
        if (context.DesignerAssembly.GetName().Version?.ToString() != Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(context.DesignerAssembly.Location))) != Fingerprint)
            throw Error("UnsupportedDesignerBuild", "Native collaboration requires the exact verified V12 build.");
        boundTarget = context.CaptureSnapshot().Target;
        ValidateNativeContract();
    }

    public Task<CollaborationStatus> CaptureStatusAsync(CancellationToken cancellationToken = default) =>
        context.Ui.RunAsync(async () =>
        {
            var before = CaptureNativeStatus();
            if (!before.InCollaborationMode) return before;
            var state = await Git.CaptureAsync(cancellationToken);
            var after = CaptureNativeStatus(state);
            if (before.Revision != after.Revision || before.CurrentUser != after.CurrentUser || ModuleHash(before.Modules) != ModuleHash(after.Modules))
                throw Error("CollaborationContextChanged", "Native document or module locks changed during the read-only Git observation.");
            return after;
        }, cancellationToken);

    /// <summary>Call only on the native UI scheduler; never initializes collaboration for a standalone document.</summary>
    public CollaborationStatus CaptureNativeStatus(GitState? gitState = null) => CaptureNativeStatus(RequireSnapshot(), gitState);

    private CollaborationStatus CaptureNativeStatus(ProjectSnapshot snapshot, GitState? gitState = null)
    {
        if (!snapshot.InCollaborationMode) return new(snapshot.Target, false, snapshot.CurrentUser, snapshot.Revision, [], null);
        var manager = RequireManager(snapshot);
        if (gitState is not null) RequireDestination(manager, gitState);
        var current = ReadCurrentIdentity(manager) ?? throw Error("CollaborationIdentityUnavailable", "Native collaboration signature is unavailable.");
        if (current != snapshot.CurrentUser) throw Error("IdentityChanged", "The snapshot and actual native collaboration signature differ.");
        RequireRegistryCurrent(manager);
        var modules = Registry(manager).Values.Cast<object>().Select(module => State(module, current)).OrderBy(item => item.NativeModuleName, StringComparer.Ordinal).ToArray();
        return new(snapshot.Target, true, current, snapshot.Revision, modules, gitState);
    }

    /// <summary>
    /// Explicit checkout resolves requested existing modules as Edit. Automatic
    /// write preflight supplies the complete actual native tuples, including
    /// New/Remove registration. Both retain the original operation ID.
    /// </summary>
    public Task<CollaborationMutationReceipt> CheckoutAsync(CollaborationMutationRequest request,
        IEnumerable? nativeChanges = null, CancellationToken cancellationToken = default, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancellationFiles = null, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancellationDirectories = null, Action? startingNativeWrite = null) =>
        ExecuteAsync("checkout", request, nativeChanges, null, cancellationToken, cancellationFiles, cancellationDirectories, null, startingNativeWrite);

    public Task<CollaborationMutationReceipt> CheckinAsync(CollaborationMutationRequest request,
        Func<CancellationToken, Task<bool>> saveAndVerifySource, CancellationToken cancellationToken = default,
        IReadOnlyList<string>? reviewedAdditionalFiles = null, Action<NativeCollaborationGitBaseline>? verifySavedProjection = null) =>
        ExecuteAsync("checkin", request, null, saveAndVerifySource, cancellationToken, null, null, reviewedAdditionalFiles, verifySavedProjection: verifySavedProjection);

    // Called by the owning adapter on its single writer and UI dispatcher.
    // A placeholder exists only in the *observer* input; never in native state.
    internal sealed record NativePageCreateProof(CollaborationStatus Before, NativeCollaborationGitBaseline Git,
        CollaborationMutationRequest Request, object[] Changes, string LockFingerprint);

    internal async Task<NativePageCreateProof> PrepareNativePageCreateAsync(string operationId, ProjectSnapshot reviewed,
        NativeApiFileCapture capture, CancellationToken token)
    {
        if (uncertain) throw Error("NativeExecutionUncertain", "A previous collaboration outcome is unverified.");
        if (!NativePageReservationPreflight.MatchesPlan(capture, capture.Address.Name) || reviewed.HasUnsavedChanges || reviewed.Busy)
            throw Error("NativePageCreatePrestateUnverified", "Remote page creation requires the exact sole New plan and a clean document.");
        var user = reviewed.CurrentUser ?? throw Error("CollaborationIdentityUnavailable", "Native collaboration identity is unavailable.");
        var nativeName = capture.Plan!.ExpectedNewModules.Single().Name;
        var request = new CollaborationMutationRequest(operationId, reviewed.Target, reviewed.Revision, user, [capture.Address]);
        var snapshot = RequireRequest(request, "checkout");
        var manager = RequireManager(snapshot);
        var before = CaptureNativeStatus(snapshot);
        var fingerprint = LockFingerprint(manager);
        if (before.Modules.Any(x => x.Address == capture.Address || x.NativeModuleName.Equals(nativeName, StringComparison.OrdinalIgnoreCase)))
            throw Error("NativePageNameRegistered", "The planned page already has a native registration.");
        var placeholder = new CollaborationModuleState(capture.Address, nativeName, null, null, false);
        var baseline = await Git.PrepareCheckoutAsync(request, user, [..before.Modules, placeholder], token);
        if (!baseline.Git.IndexClean || baseline.Git.ChangedFiles is not { } files || files.Keys.Any(path => !path.StartsWith(".local/", StringComparison.Ordinal)))
            throw Error("NativePageCreatePrestateUnverified", "Remote page creation requires a clean index and no changes outside unchanged local diagnostic files.");
        RequireDestination(manager, baseline.Git);
        await Git.RequireCurrentAsync(baseline, token);
        RequireStable(before, fingerprint);
        return new(before, baseline, request, capture.Plan.NativeChanges!.ToArray(), fingerprint);
    }

    internal void ConfirmNativePageCreatePrestate(NativePageCreateProof plan) => RequireStable(plan.Before, plan.LockFingerprint);

    internal async Task<string> ObserveFailedNativePageCreateAsync(NativePageCreateProof plan, Exception error)
    {
        uncertain = true;
        var receipt = await VerifyAfterAsync(plan.Request, "checkout", plan.Before, plan.Git, null, false, plan.Changes, error);
        return receipt.Code + ": " + receipt.Message;
    }
    internal async Task VerifyNativePageCreateAsync(NativePageCreateProof plan)
    {
        var receipt = await VerifyAfterAsync(plan.Request, "checkout", plan.Before, plan.Git, true, false, plan.Changes, null);
        if (receipt.State != "succeeded" || !receipt.LockStateVerified || receipt.ExecutionMayContinue)
        {
            uncertain = true;
            throw Error("NativePageCreateCollaborationUnverified", receipt.Code + ": " + receipt.Message);
        }
    }

    private async Task<CollaborationMutationReceipt> ExecuteAsync(string operation, CollaborationMutationRequest request,
        IEnumerable? nativeChanges, Func<CancellationToken, Task<bool>>? saveAndVerifySource, CancellationToken token, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancellationFiles, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancellationDirectories, IReadOnlyList<string>? reviewedAdditionalFiles, Action? startingNativeWrite = null, Action<NativeCollaborationGitBaseline>? verifySavedProjection = null)
    {
        // Materialize only the host's already-resolved native effect list. No
        // execution occurs during enumeration; method/member names stay fixed.
        var supplied = nativeChanges?.Cast<object>().ToArray();
        var hash = Wire.Hash(operation + "\n" + JsonSerializer.Serialize(request) + "\n" +
            string.Join("\n", supplied?.Select(ChangeKey).Order(StringComparer.Ordinal).ToArray() ?? Array.Empty<string>()) + "\n" +
            JsonSerializer.Serialize(cancellationFiles?.OrderBy(x=>x.Key,StringComparer.Ordinal)) + "\n" + JsonSerializer.Serialize(cancellationDirectories?.OrderBy(x=>x.Key,StringComparer.Ordinal)) + "\n" + JsonSerializer.Serialize(reviewedAdditionalFiles));
        await writer.WaitAsync(token);
        try
        {
            if (receipts.TryGetValue(request.OperationId, out var old))
            {
                if (old.Hash != hash) throw Error("OperationIdConflict", "The original collaboration operation ID cannot be reused with different arguments.");
                return old.Receipt;
            }
            if (uncertain) throw Error("NativeExecutionUncertain", "A previous collaboration outcome is unverified; no further native write is allowed in this component session.");
            var receipt = await context.Ui.RunAsync(() => ExecuteCoreAsync(operation, request, supplied, saveAndVerifySource, token, cancellationFiles, cancellationDirectories, reviewedAdditionalFiles, startingNativeWrite, verifySavedProjection), token);
            receipts.Add(request.OperationId, (hash, receipt));
            if (receipt.State == "verification_failed" || receipt.ExecutionMayContinue) uncertain = true;
            return receipt;
        }
        finally { writer.Release(); }
    }

    private async Task<CollaborationMutationReceipt> ExecuteCoreAsync(string operation, CollaborationMutationRequest request,
        object[]? supplied, Func<CancellationToken, Task<bool>>? saveAndVerifySource, CancellationToken token, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancellationFiles, IReadOnlyDictionary<string,IReadOnlyList<string>>? cancellationDirectories, IReadOnlyList<string>? reviewedAdditionalFiles, Action? startingNativeWrite, Action<NativeCollaborationGitBaseline>? verifySavedProjection)
    {
        bool dispatched = false; bool? accepted = null; bool sourceSaved = false;
        NativeCollaborationGitBaseline? baseline = null;
        string[]? checkinNativeFiles = null;
        CollaborationStatus? before = null;
        try
        {
            var snapshot = RequireRequest(request, operation);
            before = CaptureNativeStatus(snapshot);
            var actor = before.CurrentUser!;
            var manager = RequireManager(snapshot);
            var lockFingerprint = LockFingerprint(manager);
            var ownershipFingerprint = LockOwnershipFingerprint(manager);
            var changes = operation == "checkout" ? ResolveChanges(request, supplied, manager, actor) : [];
            var selected = operation == "checkout" ? SelectedStates(before, changes) : SelectExisting(request, before, manager);
            RequireOwnedOrAvailable(selected, operation == "checkin");
            if (operation == "checkin")
            {
                ArgumentNullException.ThrowIfNull(saveAndVerifySource);
                // Saving belongs to the adapter's existing guarded SaveCore and
                // native source projection verification, not a second writer.
                if (!await saveAndVerifySource(token))
                    return Receipt(request, "rejected", "CollaborationSaveUnverified", "Native source save/read-back did not succeed; check-in was not dispatched.", CaptureNativeStatus(), false);
                sourceSaved = true;
                var afterSave = CaptureNativeStatus();
                if (afterSave.CurrentUser != actor || !SavePreservesModules(before.Modules,afterSave.Modules,selected) || LockOwnershipFingerprint(manager) != ownershipFingerprint)
                    throw Error("CollaborationContextChanged", "Identity or native lock registration changed while saving; no check-in was dispatched.");
                before = afterSave;
                lockFingerprint = LockFingerprint(manager);
                selected = SelectExisting(request, before, manager);
                checkinNativeFiles=NativeFiles(manager,selected).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var allowed=checkinNativeFiles.Concat((reviewedAdditionalFiles ?? []).Select(Relative)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                // A deleted report no longer has a tree address. Its selected folder was
                // checked against the native removal pair during save verification.
                var registeredSelection = request with { Modules = selected.Select(item => item.Address).ToArray() };
                baseline = await Git.PrepareCheckinAsync(registeredSelection, actor, allowed, before.Modules, token);
            }
            else
            {
                // Git proof compares unrelated locks too. Keep the whole
                // genuine registry, adding only explicit native New tokens
                // which are not yet registered; do not pass a selected subset.
                var all = before.Modules.Concat(selected.Where(item => !before.Modules.Any(existing => existing.NativeModuleName == item.NativeModuleName))).ToArray();
                var cancelling=changes.Where(change=>Change(change).State==2 && before.Modules.Any(item=>item.NativeModuleName==Name(Change(change).Module) && item.NativeState==3)).Select(change=>Name(Change(change).Module)).ToArray();
                if(cancelling.Any(name=>before.Modules.Single(item=>item.NativeModuleName==name).Address.Kind is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage or ModuleKind.Report or ModuleKind.Folder)))
                    throw Error("NativeNewRemovalPlanUnavailable","Cancellation is limited to reviewed page, report and folder owners.");
                if(cancelling.Any(name=>cancellationFiles is null || !cancellationFiles.ContainsKey(name)))
                    throw Error("NativeNewRemovalRequiresPlan","Cancelling a native New owner requires its complete original source file group.");
                var reviewed=cancelling.ToDictionary(name=>name,name=>cancellationFiles![name],StringComparer.Ordinal);
                var directories=cancelling.Where(name=>before.Modules.Single(item=>item.NativeModuleName==name).Address.Kind==ModuleKind.Folder)
                    .ToDictionary(name=>name,name=>cancellationDirectories is not null && cancellationDirectories.TryGetValue(name,out var paths) ? paths : Array.Empty<string>(),StringComparer.Ordinal);
                baseline = await Git.PrepareCheckoutAsync(request, actor, all, token, reviewed, directories);
            }
            RequireDestination(manager, baseline.Git);
            await Git.RequireCurrentAsync(baseline, token);
            if (operation == "checkin")
            {
                if (!NativeFiles(manager,selected).Order(StringComparer.Ordinal).SequenceEqual(checkinNativeFiles!.Order(StringComparer.Ordinal),StringComparer.Ordinal))
                    throw Error("CollaborationSourceProjectionChanged", "Native check-in file registration changed after the Git baseline was captured.");
                verifySavedProjection?.Invoke(baseline);
            }
            RequireStable(before, lockFingerprint);
            token.ThrowIfCancellationRequested();
            if (operation == "checkout" && changes.All(change => CanReuse(change, Registry(manager), before.CurrentUser!))) accepted = true;
            else
            {
                var taskInfo = CreateTaskInfo();
                if (operation == "checkout")
                {
                    RequireNativeCheckoutCommitIdentity(baseline.CommitIdentity);
                    var nativeChanges = NativeArray(changes);
                    var executor = NativeCheckout.CreateExecutor(context.DesignerAssembly, nativeChanges);
                    startingNativeWrite?.Invoke();
                    dispatched = true;
                    accepted = NativeReflection.Call(executor, "Checkout", nativeChanges, baseline.Git.Head, taskInfo, true) is true;
                }
                else
                {
                    // Do not cancel/abandon or replay a started native task.
                    // Native CheckInAsync can commit locally before push fails.
                    var names = selected.Select(item => item.NativeModuleName).ToArray();
                    dispatched = true;
                    accepted = await (Task<bool>)NativeReflection.Call(manager, "CheckInAsync", request.Comment, names, taskInfo)!;
                }
            }
            return await VerifyAfterAsync(request, operation, before, baseline, accepted, sourceSaved, changes, null);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            if (dispatched && before is not null && baseline is not null)
                return await VerifyAfterAsync(request, operation, before, baseline, accepted, sourceSaved, [], error);
            var code = error is BridgeException bridge ? bridge.Code : error is OperationCanceledException ? "CancelledBeforeNativeDispatch" : "NativeCollaborationPreflightFailed";
            return Receipt(request, "rejected", code, error is BridgeException ? error.Message : "Collaboration preflight did not authorize a native write.", TryStatus(), false, sourceVerified: sourceSaved);
        }
    }

    private async Task<CollaborationMutationReceipt> VerifyAfterAsync(CollaborationMutationRequest request, string operation,
        CollaborationStatus before, NativeCollaborationGitBaseline baseline, bool? accepted, bool sourceSaved,
        IReadOnlyList<object> changes, Exception? nativeError)
    {
        CollaborationStatus? after = null; NativeCollaborationGitVerification? proof = null;
        try
        {
            // A stale native registry or switched document must not prevent
            // independent observation of a local commit made before an error.
            after = TryStatus();
            proof = await Git.VerifyAfterNativeAsync(baseline, after?.Modules ?? [], CancellationToken.None);
            if (after is null)
                return Receipt(request, "verification_failed", "NativeCollaborationOutcomeUnverified", "Native post-state is unavailable; independently observed Git evidence is retained without replaying the request.",
                    null, accepted, false, false, proof.CommitId, proof.RemoteSynchronized);
            after = after with { Git = proof.Git };
            var selectedNames = baseline.SelectedModuleNames.ToHashSet(StringComparer.Ordinal);
            var unrelatedBefore = before.Modules.Where(item => !selectedNames.Contains(item.NativeModuleName)).ToArray();
            var unrelatedAfter = after.Modules.Where(item => !selectedNames.Contains(item.NativeModuleName)).ToArray();
            var locks = ModuleHash(unrelatedBefore) == ModuleHash(unrelatedAfter) &&
                after.CurrentUser == baseline.User &&
                (operation == "checkout" ? CheckoutStatesVerified(changes, before, after) : CheckinStatesVerified(before, after, selectedNames)) &&
                VerifyPersistedRegistry(after, selectedNames);
            var good = nativeError is null && accepted is true && locks && proof.LockStateVerified &&
                proof.SourceVerified && proof.LocalCommitVerified && proof.RemoteSynchronized is true && (operation == "checkout" || sourceSaved);
            if (good) return Receipt(request, "succeeded", operation == "checkout" ? "NativeCheckoutVerified" : "NativeCheckinVerified",
                operation == "checkout" ? "The exact native module registration and current-user locks were read back." : "Selected native modules were saved, committed, synchronized and released with independent read-back.",
                after, true, true, operation == "checkin" && sourceSaved && proof.SourceVerified, proof.CommitId, proof.RemoteSynchronized);
            var gitFailure = proof.Code != "NativeCollaborationVerified";
            return Receipt(request, "verification_failed", gitFailure ? proof.Code : "NativeCollaborationOutcomeUnverified",
                gitFailure ? proof.Message : "Native execution ended, but its lock, source, commit or remote evidence is incomplete. The original operation is retained and must not be replayed.",
                after, accepted, locks, sourceSaved && proof.SourceVerified, proof.CommitId, proof.RemoteSynchronized);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return Receipt(request, "verification_failed", "NativeCollaborationOutcomeUnverified",
                "Native execution was attempted and independent post-state verification could not complete; the original request must not be replayed.",
                after, accepted, false, false, proof?.CommitId, proof?.RemoteSynchronized);
        }
    }

    internal static UserIdentity NativeCheckoutCommitIdentity()
    {
        var assembly=Assembly.Load("Forguncy.Collaboration");
        RequireIdentityAssembly(assembly,"Forguncy.Collaboration","2A0AB78316A5BFD1C66332D8D50C7935B817C82CB5409DD91480017DA9791A1D");
        var kind=assembly.GetType("Forguncy.Collaboration.MessageType",true)!;
        var checkout=Enum.Parse(kind,"CheckOutModule");
        if(Convert.ToInt32(checkout)!=1) throw Error("NativeCheckoutIdentityUnavailable","The native checkout message type changed.");
        var message=Activator.CreateInstance(assembly.GetType("Forguncy.Collaboration.CommitMessage",true)!,["identity preflight",checkout])!;
        if(NativeReflection.Call(message,"IsUserCommit") is not false)
            throw Error("NativeCheckoutIdentityUnavailable","The native checkout message is no longer classified as a system commit.");
        var git=Activator.CreateInstance(assembly.GetType("Forguncy.Collaboration.Git.GitCollaboration",true)!)!;
        var signature=NativeReflection.Call(git,"GetSignature",message)!;
        var identity=new UserIdentity((string)NativeReflection.Get(signature,"Name")!,(string)NativeReflection.Get(signature,"Email")!);
        var resourceAssembly=Assembly.Load("ForguncyResource");
        RequireIdentityAssembly(resourceAssembly,"ForguncyResource","7A607EE3ACD52E8CB3E207D29F413BB2656AB2C579E2D3DE8E06F2A834983EA8");
        var resource=resourceAssembly.GetType("Forguncy.Properties.Resources",true)!;
        var expected=(string?)NativeReflection.Static(resource,"get_Collaboration_SystemCommitter_Name");
        if(expected is not ("HZG System" or "Forguncy System") || identity.UserName!=expected || identity.Email!=expected)
            throw Error("NativeCheckoutIdentityUnavailable","The exact native checkout signature does not match its system resource.");
        return identity;
    }
    private static void RequireIdentityAssembly(Assembly assembly,string name,string fingerprint)
    {
        var expected=name+", Version=12.0.102.0, Culture=neutral, PublicKeyToken=967100671921c115";
        if(assembly.FullName!=expected || string.IsNullOrEmpty(assembly.Location) ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))!=fingerprint)
            throw Error("NativeCheckoutIdentityUnavailable","The native checkout identity component differs from the pinned V12 assembly.");
    }
    internal static void RequireNativeCheckoutCommitIdentity(UserIdentity expected)
    {
        if(NativeCheckoutCommitIdentity()!=expected)
            throw Error("NativeCheckoutCommitIdentityChanged","The pinned native checkout signature changed before dispatch.");
    }

    private object[] ResolveChanges(CollaborationMutationRequest request, object[]? supplied, object manager, UserIdentity actor)
    {
        var checkout = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationExecutor", true)!.GetMethod("Checkout", Flags)!;
        var tuple = checkout.GetParameters()[0].ParameterType.GetGenericArguments()[0];
        var types = tuple.GetGenericArguments();
        var registeredType = types[1].Assembly.GetType("Forguncy.Collaboration.Module", throwOnError: true)!;
        var changes = supplied ?? request.Modules.Select(address =>
        {
            var native = resolveNativeModule(address) ?? throw Error("CollaborationModuleUnavailable", "A selected native module cannot be resolved from the native registration.");
            var simple = native.GetType() == registeredType ? Activator.CreateInstance(types[0], [native])! : native;
            if (simple.GetType() != types[0])
                throw Error("CollaborationModuleUnavailable", "A selected native token does not match the checkout module contract.");
            return Activator.CreateInstance(tuple, [simple, Enum.Parse(types[1], "Edit")])!;
        }).ToArray();
        if (changes.Length == 0 || changes.Any(item => item.GetType() != tuple)) throw Error("CollaborationEffectPlanInvalid", "A complete actual native module-state tuple plan is required.");
        var names = new HashSet<string>(StringComparer.Ordinal); var addresses = new HashSet<ModuleAddress>();
        var modules = Registry(manager);
        foreach (var change in changes)
        {
            var (simple, state) = Change(change); var name = Name(simple);
            var address = mapNativeModule(simple) ?? throw Error("CollaborationModuleUnavailable", "An affected native owner has no exact module address mapping.");
            if (!names.Add(name) || !addresses.Add(address) || state is not (1 or 2 or 3 or 4)) throw Error("CollaborationEffectPlanInvalid", "Affected modules must be unique native Edit, Remove, New or Move entries.");
            if (modules[name] is null && state != 3) throw Error("ModuleRegistrationMissing", "An existing affected native module is not registered.");
            if (modules[name] is { } removed && NativeReflection.Get(removed, "State")?.ToString() == "Remove" && state != 2)
                throw Error("ModulePendingDeletion", "A native registration pending deletion cannot be checked out again as an editable or new module.");
            var exclusive = NativeReflection.Get(simple, "ExclusiveType");
            if (exclusive is not null && modules.Values.Cast<object>().Any(other => Equals(NativeReflection.Get(other, "ExclusiveType"), exclusive) &&
                Convert.ToInt32(NativeReflection.Get(other, "State")) != 0 && Identity(NativeReflection.Get(other, "LockedBy")) != actor))
                throw Error("ModuleLockedByOther", "A native module in the same exclusive ownership group is locked by another identity.");
        }
        if (!addresses.SetEquals(request.Modules)) throw Error("CollaborationAffectedSetMismatch", "Requested module addresses must equal the complete native affected set.");
        return changes;
    }

    private IReadOnlyList<CollaborationModuleState> SelectedStates(CollaborationStatus status, IEnumerable<object> changes) => changes.Select(change =>
    {
        var (simple, _) = Change(change); var matches = status.Modules.Where(item => item.NativeModuleName == Name(simple)).ToArray();
        return matches.Length == 1 ? matches[0] : matches.Length == 0 ? new CollaborationModuleState(mapNativeModule(simple)!, Name(simple), null, null, false) :
            throw Error("CollaborationModuleAmbiguous", "Native module registration is ambiguous.");
    }).ToArray();
    private IReadOnlyList<CollaborationModuleState> SelectExisting(CollaborationMutationRequest request, CollaborationStatus status, object manager)
    {
        var selected = new List<CollaborationModuleState>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var address in request.Modules)
        {
            var matches = status.Modules.Where(item => item.Address == address ||
                item.PendingDeletion && address.Kind == ModuleKind.Report && item.Address == (address with { FolderPath = null })).ToArray();
            if (matches.Length != 1 || !names.Add(matches[0].NativeModuleName))
                throw Error("CollaborationModuleUnavailable", "Check-in requires unique registered native modules.");
            var item = matches[0];
            if (item.PendingDeletion && address.Kind == ModuleKind.Report)
                NativeRemovedReport.ReadPair(boundTarget.ProjectPath, address, Registry(manager)[item.NativeModuleName]!);
            selected.Add(item);
        }
        return selected;
    }
    private static void RequireOwnedOrAvailable(IEnumerable<CollaborationModuleState> selected, bool requireOwned)
    {
        foreach (var item in selected)
        {
            if (item.LockedBy is not null && !item.OwnedByCurrentUser) throw Error("ModuleLockedByOther", "An affected native module is locked by another identity.");
            if (item.NativeState is > 0 && !item.OwnedByCurrentUser || requireOwned && !item.OwnedByCurrentUser)
                throw Error("ModuleLockRequired", "All selected native registrations must be owned by the current identity.");
        }
    }
    private static bool CanReuse(object change, IDictionary registry, UserIdentity current)
    {
        var (simple, desired) = Change(change); if (registry[Name(simple)] is not { } module || Identity(NativeReflection.Get(module, "LockedBy")) != current) return false;
        var actual = Convert.ToInt32(NativeReflection.Get(module, "State"));
        return desired == 1 ? actual is 1 or 3 or 4 : desired == 4 ? actual is 3 or 4 : actual == desired;
    }
    private static bool CheckoutStatesVerified(IReadOnlyList<object> changes, CollaborationStatus before, CollaborationStatus after)
    {
        if (changes.Count == 0) return false;
        foreach (var change in changes)
        {
            var (simple, desired) = Change(change); var name = Name(simple);
            var prior=before.Modules.SingleOrDefault(item=>item.NativeModuleName==name);
            var actual = after.Modules.SingleOrDefault(item => item.NativeModuleName == name);
            if(desired==2 && prior is { NativeState:3, OwnedByCurrentUser:true }) { if(actual is not null) return false; continue; }
            if (actual is null || !actual.OwnedByCurrentUser || actual.LockedBy != after.CurrentUser) return false;
            if (desired == 1 ? actual.NativeState is not (1 or 3 or 4) :
                desired == 4 && prior is { NativeState:3, OwnedByCurrentUser:true } ? actual.NativeState != 3 : actual.NativeState != desired) return false;
        }
        return true;
    }
    private static bool CheckinStatesVerified(CollaborationStatus before, CollaborationStatus after, HashSet<string> selectedNames)
    {
        foreach (var name in selectedNames)
        {
            var prior = before.Modules.Single(item => item.NativeModuleName == name);
            var current = after.Modules.Where(item => item.NativeModuleName == name).ToArray();
            if (prior.PendingDeletion ? current.Length != 0 : current.Length != 1 || current[0].NativeState != 0 || current[0].LockedBy is not null || current[0].OwnedByCurrentUser || current[0].PendingDeletion)
                return false;
        }
        return true;
    }

    internal async Task<EditRegistration> PrepareRegisteredEditFilesAsync(NativeModuleFileGroup group, CancellationToken token, bool beforeSave = false)
    {
        var status = await CaptureStatusAsync(token);
        var owner = status.Modules.SingleOrDefault(item => item.NativeModuleName == group.NativeModuleName);
        if (owner?.Address != group.Address || !owner.OwnedByCurrentUser || owner.NativeState != 1)
            throw Error("ModuleLockRequired", "The registered native root must be checked out for edit by the current user.");
        string ownerKind = group.Address.Kind switch
        {
            ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage => "Page",
            ModuleKind.ApplicationSettings => "Application",
            ModuleKind.CustomLibrary => "Library",
            _ => throw Error("NativeOwnerUnavailable", "This module has no registered edit-file adapter.")
        };
        if (status.Git is not { ChangedFiles: not null } git || git.Head != git.UpstreamHead ||
            !await NativeOwnedRegistryProof.VerifyAsync(boundTarget.ProjectPath, git, [], token))
            throw Error(ownerKind + "LockUnconfirmed",
                "The registered native lock must match the synchronized collaboration registry.");
        var manager = RequireManager(RequireSnapshot());
        var module = Registry(manager)[group.NativeModuleName]!;
        var helper = context.DesignerAssembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        string[] Inventory(string method) => ((IEnumerable<string>)NativeReflection.Static(helper, method, module)!).ToArray();
        var nativePaths = Inventory("GetReferFilesFromWorkingDirectory");
        var files = nativePaths.Select(Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = Inventory("GetReferFilesFromLastSavedFilesFolder").Select(Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var application = group.Address == NativeModuleFiles.ApplicationRoot;
        var stage = beforeSave ? "beforeNativeSave" : "beforeMutation";
        if (!files.SetEquals(staged) || !group.RequiredFiles.All(files.Contains))
        {
            var hint = "";
            if (application)
            {
                var missing = group.RequiredFiles.FirstOrDefault(path => !files.Contains(path) || !staged.Contains(path));
                var mismatch = missing ?? files.Concat(staged).FirstOrDefault(path => files.Contains(path) != staged.Contains(path));
                hint = $" Stage={stage}; fileRole={NativeSaveVerification.ApplicationFileRole(mismatch)}; reason={(missing is null ? "InventoryMismatch" : "RequiredFileMissing")}.";
            }
            throw Error("Native" + ownerKind + "TrackingUnavailable",
                "The working and staged native inventories must agree and cover the requested root file group." + hint);
        }
        if (application)
        {
            var companions = files.Except(group.RequiredFiles, StringComparer.OrdinalIgnoreCase).ToArray();
            if (companions.Length != 0 &&
                new NativeSaveVerification(context).FirstStagedSourceMismatch(companions) is { } mismatch)
                throw Error("ApplicationOwnerFileChanged",
                    $"Stage={stage}; fileRole={NativeSaveVerification.ApplicationFileRole(mismatch.RelativeFile)}; reason={mismatch.Code}.");
        }
        var tracking = (IDictionary)NativeReflection.Get(manager, "SaveLoadTrackedFiles")!;
        if (tracking.Contains(group.NativeModuleName))
        {
            var prior = ((IEnumerable<string>)tracking[group.NativeModuleName]!).Select(Relative).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (prior.Any(path => !files.Contains(path)))
                throw Error("Native" + ownerKind + "TrackingChanged",
                    "The native owner already tracks pending removals outside this edit.");
            if (group.Address.Kind != ModuleKind.CustomLibrary && !files.SetEquals(prior))
                return new(group.NativeModuleName, files, () => tracking[group.NativeModuleName] = nativePaths);
            return new(group.NativeModuleName, files, () => { });
        }
        // ReloadModules can restore a synchronized lock without rebuilding this transient
        // dictionary. Reuse the same provider and entry shape as OpenWithVersionCheck.
        return new(group.NativeModuleName, files, () => tracking.Add(group.NativeModuleName, nativePaths));
    }

    private IReadOnlyCollection<string> NativeFiles(object manager, IReadOnlyList<CollaborationModuleState> selected)
    {
        var modules = Registry(manager); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".collaboration" };
        var helper = context.DesignerAssembly.GetType("Forguncy.Common.ModuleExtensions", true)!;
        foreach (var item in selected)
        {
            var module = modules[item.NativeModuleName] ?? throw Error("ModuleRegistrationMissing", "A selected native module disappeared.");
            if (item.PendingDeletion && item.Address.Kind == ModuleKind.Report)
            {
                paths.UnionWith(NativeRemovedReport.ReadPair(boundTarget.ProjectPath, item.Address, module));
                continue;
            }
            foreach (var value in new[] { NativeReflection.Static(helper, "GetReferFilesFromWorkingDirectory", module), NativeReflection.Get(module, "ToRemoveFiles") })
                if (value is IEnumerable files) foreach (var path in files.Cast<string>()) paths.Add(Relative(path));
        }
        return paths.ToArray();
    }
    private bool VerifyPersistedRegistry(CollaborationStatus status, HashSet<string> selected)
    {
        var manager = RequireManager(RequireSnapshot()); var path = (string)NativeReflection.Get(manager, "ModuleFilePath")!;
        if (Relative(path) != ".collaboration" || !File.Exists(path)) return false;
        var native = ReadPersistedModules(path).Select(module => State(module, status.CurrentUser!)).ToArray();
        if (native.Select(item => item.NativeModuleName).Distinct(StringComparer.Ordinal).Count() != native.Length) return false;
        return selected.All(name => ModuleHash(native.Where(item => item.NativeModuleName == name)) == ModuleHash(status.Modules.Where(item => item.NativeModuleName == name)));
    }

    internal static object[] ReadPersistedModules(string path)
    {
        // Same genuine serializer/type as native ReloadModules, without its
        // registry replacement or RaiseModuleStateChanged side effects.
        var moduleType = Assembly.Load("Forguncy.Collaboration").GetType("Forguncy.Collaboration.Module", true)!;
        var list = typeof(List<>).MakeGenericType(moduleType);
        var utility = Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!;
        var read = utility.GetMethods(Flags).Single(method => method.Name == "ReadObjectFromFile" && method.IsGenericMethodDefinition &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(string));
        return ((IEnumerable)read.MakeGenericMethod(list).Invoke(null, [path, null])!).Cast<object>().ToArray();
    }

    private void RequireRegistryCurrent(object manager)
    {
        var path = (string)NativeReflection.Get(manager, "ModuleFilePath")!;
        if (Relative(path) != ".collaboration" || !File.Exists(path)) throw Error("CollaborationRegistryUnavailable", "The bound native collaboration registration source is missing.");
        var source = ReadPersistedModules(path);
        if (source.Select(Name).Distinct(StringComparer.Ordinal).Count() != source.Length)
            throw Error("CollaborationRegistryAmbiguous", "The native registration source contains duplicate owners.");
        var memory = Registry(manager).Values.Cast<object>().ToArray();
        foreach (var module in memory)
        {
            var match = source.SingleOrDefault(item => Name(item) == Name(module));
            // ReloadModules may add default unlocked owners absent from an old
            // native registry. They are not locks; active registration cannot
            // be synthesized or omitted from this comparison.
            if (match is null && NativeReflection.Get(module, "State")?.ToString() == "Unlocked") continue;
            if (match is null || NativeStateKey(match) != NativeStateKey(module))
                throw Error("NativeModuleStateStale", "Cached native module locks differ from the actual .collaboration source; refresh through the native product before writing.");
        }
        if (source.Any(module => NativeReflection.Get(module, "State")?.ToString() != "Unlocked" && !memory.Any(item => Name(item) == Name(module))))
            throw Error("NativeModuleStateStale", "Active source locks are absent from the native registration cache.");
    }

    private static string NativeStateKey(object module)
    {
        var state = NativeReflection.Get(module, "State")!;
        var active = state.ToString() != "Unlocked";
        var date = NativeReflection.Get(module, "LockDateTime") as DateTime?;
        return JsonSerializer.Serialize(new { Name = Name(module), Type = NativeReflection.Get(module, "ModuleType")!.ToString(), State = state.ToString(),
            Owner = active ? Identity(NativeReflection.Get(module, "LockedBy")) : null, Date = active ? date?.Ticks : null,
            ReportRemoval = ReportRemovalPaths(module) });
    }

    private static string[]? ReportRemovalPaths(object module) => NativeRemovedReport.IsRemoval(module)
        ? (NativeReflection.Get(module, "ToRemoveFiles") as IEnumerable<string>)?.Order(StringComparer.Ordinal).ToArray() : null;

    private ProjectSnapshot RequireSnapshot()
    {
        var snapshot = context.CaptureSnapshot();
        if (snapshot.Target != boundTarget) throw Error("ProjectTargetMismatch", "The bound collaboration document/session changed.");
        return snapshot;
    }
    private object RequireManager(ProjectSnapshot snapshot)
    {
        if (!snapshot.InCollaborationMode) throw Error("CollaborationNotApplicable", "Standalone documents do not checkout, register collaboration locks or checkin.");
        var manager = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager", true)!.GetField("_instance", Flags)!.GetValue(null)
            ?? throw Error("CollaborationUnavailable", "The native collaboration manager must already be initialized by the current document.");
        if (NativeReflection.Get(manager, "InCollaborationMode") is not true) throw Error("CollaborationContextChanged", "The actual native document is not in collaboration mode.");
        var collaboration = NativeReflection.Get(manager, "Collaboration") ?? throw Error("CollaborationUnavailable", "The actual native collaboration provider is unavailable.");
        var working = (string)NativeReflection.Get(collaboration, "WorkingDirectory")!;
        if (!string.Equals(Path.GetFullPath(working), Path.GetFullPath(boundTarget.ProjectPath), StringComparison.OrdinalIgnoreCase))
            throw Error("CollaborationProjectMismatch", "The native collaboration provider belongs to another working directory.");
        return manager;
    }
    private ProjectSnapshot RequireRequest(CollaborationMutationRequest request, string operation)
    {
        var snapshot = RequireSnapshot();
        if (request.Target != boundTarget || request.ExpectedProjectRevision != snapshot.Revision) throw Error("CollaborationBaselineChanged", "Exact target and fresh project revision are required.");
        if (snapshot.Busy) throw Error("DesignerBusy", "The native document is busy.");
        if (!snapshot.InCollaborationMode) throw Error("CollaborationNotApplicable", "Standalone documents cannot checkout or checkin.");
        if (snapshot.CurrentUser is null || snapshot.CurrentUser != request.ExpectedUser) throw Error("IdentityChanged", "The current collaboration identity differs from the reviewed identity.");
        if (string.IsNullOrWhiteSpace(request.OperationId) || request.Modules is null || request.Modules.Count == 0 || request.Modules.Distinct().Count() != request.Modules.Count)
            throw Error("InvalidArguments", "Original operation ID and a unique nonempty affected-module selection are required.");
        if (operation == "checkin" && string.IsNullOrWhiteSpace(request.Comment)) throw Error("CommentRequired", "Native check-in requires a comment.");
        return snapshot;
    }
    private void RequireStable(CollaborationStatus before, string lockFingerprint)
    {
        var fresh = CaptureNativeStatus();
        if (fresh.Revision != before.Revision || fresh.CurrentUser != before.CurrentUser || ModuleHash(fresh.Modules) != ModuleHash(before.Modules) ||
            LockFingerprint(RequireManager(RequireSnapshot())) != lockFingerprint || RequireSnapshot().Busy)
            throw Error("CollaborationContextChanged", "Native identity, locks, project revision or busy state changed before dispatch.");
    }
    private CollaborationModuleState State(object module, UserIdentity current)
    {
        var address = mapNativeModule(module) ?? throw Error("CollaborationModuleUnavailable", "A native collaboration owner has no exact model address mapping.");
        var nativeState = NativeReflection.Get(module, "State")!; var state = Convert.ToInt32(nativeState); var owner = Identity(NativeReflection.Get(module, "LockedBy"));
        return new(address, Name(module), state, owner, state != 0 && owner == current, PendingDeletion: nativeState.ToString() == "Remove");
    }
    private static IDictionary Registry(object manager) => NativeReflection.Get(manager, "Modules") as IDictionary ?? throw Error("CollaborationRegistryUnavailable", "Native module registration is unavailable.");
    internal static bool SavePreservesModules(IReadOnlyList<CollaborationModuleState> before,IReadOnlyList<CollaborationModuleState> after,IReadOnlyList<CollaborationModuleState> selected)
    {
        if(before.Count!=after.Count) return false;
        var selectedNames=selected.Select(x=>x.NativeModuleName).ToHashSet(StringComparer.Ordinal);
        foreach(var old in before)
        {
            var matches=after.Where(x=>x.NativeModuleName==old.NativeModuleName).ToArray();
            if(matches.Length!=1) return false;
            var current=matches[0];
            if(!selectedNames.Contains(old.NativeModuleName)) { if(old!=current) return false; continue; }
            if(!old.OwnedByCurrentUser || !current.OwnedByCurrentUser || old.Address!=current.Address || old.LockedBy!=current.LockedBy ||
               (old.PendingDeletion && !current.PendingDeletion) || old.NativeState is not (1 or 2 or 3 or 4) || current.NativeState is not (1 or 2 or 3 or 4)) return false;
        }
        return selectedNames.Count==selected.Count && selectedNames.All(name=>before.Any(x=>x.NativeModuleName==name));
    }
    private static string LockOwnershipFingerprint(object manager) => Wire.Hash(string.Join("\n",Registry(manager).Values.Cast<object>().Select(module=>
    {
        var state=NativeReflection.Get(module,"State")!;
        var active=state.ToString()!="Unlocked";
        var date=NativeReflection.Get(module,"LockDateTime") as DateTime?;
        return JsonSerializer.Serialize(new { Name=Name(module), Type=NativeReflection.Get(module,"ModuleType")!.ToString(), Active=active,
            Owner=active?Identity(NativeReflection.Get(module,"LockedBy")):null, Date=active?date?.Ticks:null,
            ReportRemoval=ReportRemovalPaths(module) });
    }).Order(StringComparer.Ordinal)));
    private static string LockFingerprint(object manager) => Wire.Hash(string.Join("\n", Registry(manager).Values.Cast<object>().Select(NativeStateKey).Order(StringComparer.Ordinal)));
    private static void RequireDestination(object manager, GitState state)
    {
        var provider = NativeReflection.Get(manager, "Collaboration")!;
        var nativeGit=Assembly.Load("Forguncy.Collaboration").GetType("Forguncy.Collaboration.Git.GitCollaboration",true)!;
        if(provider.GetType()!=nativeGit) throw Error("NativeCollaborationDestinationChanged","The active native collaboration provider is not the reviewed V12 Git implementation.");
        if (NativeReflection.Get(provider, "IsRemote") is not true || state.RemoteUrl is null ||
            !string.Equals(NativeReflection.Get(provider, "RemoteAddress") as string, state.RemoteUrl, StringComparison.Ordinal) ||
            !string.Equals(NativeReflection.Get(provider, "BranchName") as string, state.Branch, StringComparison.Ordinal))
            throw Error("NativeCollaborationDestinationChanged", "The actual native collaboration provider and reviewed Git remote/branch do not match.");
    }
    internal static UserIdentity? ReadCurrentIdentity(object manager)
    {
        var user = Identity(NativeReflection.Get(manager, "CurrentUser"));
        return user is not null && !string.IsNullOrWhiteSpace(user.UserName) && !string.IsNullOrWhiteSpace(user.Email) ? user : null;
    }

    private static UserIdentity? Identity(object? signature) => signature is null ? null : new((string)NativeReflection.Get(signature, "UserName")!, (string)NativeReflection.Get(signature, "Email")!);
    private static string Name(object module) => (string)NativeReflection.Get(module, "ModuleName")!;
    private static (object Module, int State) Change(object tuple) => (tuple.GetType().GetField("Item1")!.GetValue(tuple)!, Convert.ToInt32(tuple.GetType().GetField("Item2")!.GetValue(tuple)));
    private static string ChangeKey(object tuple) { var (simple, state) = Change(tuple); return Name(simple) + ":" + state; }
    private static string ModuleHash(IEnumerable<CollaborationModuleState> modules) => Wire.Hash(JsonSerializer.Serialize(modules.OrderBy(item => item.NativeModuleName, StringComparer.Ordinal)));
    private Array NativeArray(IReadOnlyList<object> changes)
    { var array = Array.CreateInstance(changes[0].GetType(), changes.Count); for (var i = 0; i < changes.Count; i++) array.SetValue(changes[i], i); return array; }
    private object CreateTaskInfo()
    { var type = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager", true)!.GetMethod("CheckInAsync", Flags)!.GetParameters()[2].ParameterType; return Activator.CreateInstance(type, [false, true, 100])!; }
    private string Relative(string path)
    {
        var root = Path.GetFullPath(boundTarget.ProjectPath); var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
        var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative is ".." || relative.StartsWith("../", StringComparison.Ordinal) || relative.IndexOf(':') >= 0)
            throw Error("CollaborationPathEscape", "Native module files must remain inside the bound collaboration working directory.");
        return relative;
    }
    private CollaborationStatus? TryStatus() { try { return CaptureNativeStatus(); } catch { return null; } }
    private static CollaborationMutationReceipt Receipt(CollaborationMutationRequest request, string state, string code, string message,
        CollaborationStatus? status, bool? accepted, bool locks = false, bool sourceVerified = false, string? commit = null, bool? remote = null) =>
        new(request.OperationId, state, code, message, status, accepted, locks, sourceVerified, commit, remote, ExecutionMayContinue: false);
    private void ValidateNativeContract()
    {
        var manager = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        var checkin = manager.GetMethod("CheckInAsync", Flags)!;
        if (checkin.ReturnType != typeof(Task<bool>) || checkin.GetParameters().Length != 3 || manager.GetField("_instance", Flags) is null)
            throw Error("NativeCollaborationContractChanged", "The reviewed native collaboration manager contract differs.");
        var checkout = context.DesignerAssembly.GetType("Forguncy.Common.CollaborationExecutor", true)!.GetMethod("Checkout", Flags)!;
        if (checkout.ReturnType != typeof(bool) || checkout.GetParameters().Length != 4 || checkout.GetParameters()[3].ParameterType != typeof(bool))
            throw Error("NativeCollaborationContractChanged", "The reviewed native checkout executor contract differs.");
    }
    private static BridgeException Error(string code, string message) => new(code, message);
}
