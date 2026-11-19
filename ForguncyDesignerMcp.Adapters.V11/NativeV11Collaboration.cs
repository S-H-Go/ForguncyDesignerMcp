using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Native collaboration only. Read/plan never call CanExecute/CanEditExecute:
// those methods can display UI and acquire locks. The mutation entry invokes
// the real executor and never writes .collaboration or makes a Git commit.
internal static class NativeV11Collaboration
{
    private const string Code = "NativeCollaborationRequiresReview";
    private sealed record Contract(Type Manager, Type Module, Type Signature, Type Simple, Type State,
        Type Tuple, Type Multi, FieldInfo Instance, MethodInfo Checkout);
    private static readonly ConditionalWeakTable<Assembly, Contract> Contracts = new();

    internal sealed class Plan
    {
        internal NativeV11Session Session { get; }
        internal IReadOnlyList<NativeV11CollaborationOwner> Owners { get; }
        internal UserIdentity ExpectedUser { get; }
        internal object Manager { get; }
        internal object Collaboration { get; }
        internal object Signature { get; }
        internal IDictionary Modules { get; }
        internal string RawRevision { get; }
        internal string BusinessRevision { get; }
        internal string SimpleRevision { get; }
        internal SortedDictionary<string, ModuleImage> Before { get; }
        internal string TrackedRevision { get; }
        internal IReadOnlyDictionary<string, string[]> TrackedBefore { get; }
        internal IReadOnlyDictionary<string, string[]> CancellationDirectFiles { get; }
        internal IReadOnlyDictionary<string, string[]> DirectAtCheckout { get; }
        internal IReadOnlyDictionary<string, string[]> Files { get; }
        internal bool AllowPendingDeletion { get; }
        internal bool AllowSharedTableDatabase { get; }
        internal IReadOnlyList<string> FutureAncillaryFiles { get; }
        internal IReadOnlyList<string> SharedEffectFiles { get; }
        internal Plan(NativeV11Session session, IReadOnlyList<NativeV11CollaborationOwner> owners, Bound bound,
            SortedDictionary<string, ModuleImage> before, IReadOnlyDictionary<string, string[]> files, bool allowPendingDeletion,
            IReadOnlyDictionary<string, string[]> cancellationDirectFiles, IReadOnlyDictionary<string, string[]> directAtCheckout,
            bool allowSharedTableDatabase, IReadOnlyList<string> futureAncillaryFiles, IReadOnlyList<string> sharedEffectFiles)
        {
            Session = session; Owners = owners; ExpectedUser = bound.User; Manager = bound.Manager;
            Collaboration = bound.Collaboration; Signature = bound.Signature; Modules = bound.Modules;
            RawRevision = session.Context.CaptureSnapshot().Revision;
            BusinessRevision = CaptureBusinessRevision(session.SourcePath); Before = before; Files = files;
            SimpleRevision = SimpleRevisionOf(session.Context.DesignerAssembly, owners);
            TrackedRevision = CaptureTracked(session, bound.Manager);
            TrackedBefore = CaptureTrackedFiles(session, bound.Manager);
            CancellationDirectFiles = cancellationDirectFiles;
            DirectAtCheckout = directAtCheckout;
            AllowPendingDeletion = allowPendingDeletion;
            AllowSharedTableDatabase = allowSharedTableDatabase;
            FutureAncillaryFiles = futureAncillaryFiles;
            SharedEffectFiles = sharedEffectFiles;
        }
    }

    internal sealed record CheckoutResult(bool? NativeAccepted, bool LockStateVerified, bool ExecutionMayContinue,
        CollaborationStatus? Status, string Code, NativeV11CollaborationRepository.Outcome? Repository = null);
    internal sealed record Bound(object Manager, object Collaboration, object Signature, UserIdentity User, IDictionary Modules);
    internal sealed record ModuleImage(object Model, int State, UserIdentity? User, object? Exclusive, string Json);

    // The adapter wraps this native-only revision with its opaque issued
    // Snapshot().Revision and fresh Git state. This raw revision is not a new
    // write baseline supplied to callers by the helper itself.
    internal static CollaborationStatus ReadStatus(NativeV11Session session, IReadOnlyList<NativeV11CollaborationOwner>? projection = null)
    {
        session.VerifyContext();
        var snapshot = session.Context.CaptureSnapshot();
        if (!snapshot.InCollaborationMode)
            return new(snapshot.Target, false, snapshot.CurrentUser, snapshot.Revision, []); // No singleton/service access.
        var bound = Bind(session);
        if (projection is null)
        {
            var candidates = NativeV11CollaborationOwners.EnumerateStatusOwners(session)
                .Concat(NativeV11CollaborationOwners.PendingDeletions(session));
            projection = candidates.GroupBy(owner => owner.Address).Select(group =>
            {
                if (group.Select(owner => Name(owner.SimpleModule)).Distinct(StringComparer.Ordinal).Count() != 1)
                    throw Review("A logical status address maps to different native owners.");
                return group.OrderByDescending(owner => owner.State == "Remove").First();
            }).ToArray();
        }
        CaptureModules(session, bound, true);
        var states = ReadOwnersCore(session.Context.DesignerAssembly, bound.Modules, bound.Signature, projection);
        foreach (var state in states)
        {
            if (state.NativeState is null) continue; // A proven not-yet-registered New owner.
            if (NativeV11Reflection.Call(bound.Manager, "IsModuleCheckoutByCurrentUser", state.NativeModuleName) is not bool native || native != state.OwnedByCurrentUser)
                throw Review("The native manager disagrees with its module/signature ownership data.");
        }
        return new(snapshot.Target, true, bound.User, session.Context.CaptureSnapshot().Revision, states);
    }

    internal static object RequireBoundManager(NativeV11Session session) => Bind(session).Manager;
    internal static string RequireWorkingDirectory(NativeV11Session session) { _ = Bind(session); return session.SourcePath; }

    internal static Plan CreatePlan(NativeV11Session session, IReadOnlyList<NativeV11CollaborationOwner> owners, UserIdentity expectedUser,
        bool allowPendingDeletion = false, ModuleMutationRequest? mutation = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? reviewedFutureGroups = null,
        IReadOnlyList<string>? reviewedAncillaryFiles = null, IReadOnlyList<string>? reviewedSharedFiles = null)
    {
        var bound = Bind(session);
        var snapshot = session.Context.CaptureSnapshot();
        if (snapshot.HasUnsavedChanges || snapshot.Busy || NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is not false)
            throw Review("The native document must be clean and idle before collaboration checkout is planned.");
        if (bound.User != expectedUser || snapshot.CurrentUser != expectedUser) throw Review("The requested identity differs from the actual native collaboration signature.");
        if (owners.Count is < 1 or > 256 || owners.Select(owner => owner.Address).Distinct().Count() != owners.Count)
            throw Review("A bounded, unique and complete logical owner projection is required.");
        var savedOwners = owners.ToArray();
        if ((reviewedAncillaryFiles is not null || reviewedSharedFiles is not null) && reviewedFutureGroups is null)
            throw Review("Ancillary future files require an explicit reviewed multi-owner creation plan.");
        var ancillaryFiles = (reviewedAncillaryFiles ?? Array.Empty<string>())
            .Select(path => Relative(session.SourcePath, path)).Order(StringComparer.Ordinal).ToArray();
        if (ancillaryFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ancillaryFiles.Length)
            throw Review("The reviewed ancillary future files contain colliding paths.");
        var sharedFiles = (reviewedSharedFiles ?? Array.Empty<string>())
            .Select(path => Relative(session.SourcePath, path)).Order(StringComparer.Ordinal).ToArray();
        if (sharedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != sharedFiles.Length ||
            sharedFiles.Any(path => path.EndsWith('/') || ancillaryFiles.Contains(path, StringComparer.OrdinalIgnoreCase)))
            throw Review("The reviewed shared effects contain an ambiguous path or overlap a new ancillary file.");
        var groupedFolderMutation = mutation is { Operation: ModuleOperation.Update or ModuleOperation.Delete, Address.Kind: ModuleKind.Folder } && reviewedFutureGroups is not null;
        if (reviewedFutureGroups is not null && (mutation?.Operation != ModuleOperation.Create && !groupedFolderMutation ||
            reviewedFutureGroups.Count != savedOwners.Count(owner => owner.State == "New") ||
            savedOwners.Where(owner => owner.State == "New").Any(owner => !reviewedFutureGroups.ContainsKey(Name(owner.SimpleModule)))))
            throw Review("A multi-owner creation must provide one reviewed future file group for every exact New owner.");
        if (reviewedFutureGroups is not null)
        {
            var suppliedNames = savedOwners.Select(owner => Name(owner.SimpleModule)).ToHashSet(StringComparer.Ordinal);
            var futurePaths = reviewedFutureGroups.Values.SelectMany(value => value).Concat(ancillaryFiles).Concat(sharedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (NativeV11CollaborationOwners.ExpandExistingOwners(session, savedOwners, futurePaths, allowSharedTableDatabase: true)
                .Any(owner => !suppliedNames.Contains(Name(owner.SimpleModule))))
                throw Review("The multi-owner creation plan omits an intersecting native file owner.");
        }
        var allowSharedTableDatabase = savedOwners.Any(owner => owner.Address.Kind is ModuleKind.Table or ModuleKind.View);
        if (allowSharedTableDatabase)
        {
            var suppliedNames = savedOwners.Select(owner => Name(owner.SimpleModule)).ToHashSet(StringComparer.Ordinal);
            foreach (var owner in savedOwners.Where(owner => owner.Address.Kind is ModuleKind.Table or ModuleKind.View && owner.State != "New"))
            {
                var requiredOwners = session.Items(owner.Address.Kind).Any(item => item.Address == owner.Address)
                    ? NativeV11CollaborationOwners.ResolveTableSharedOwners(session, owner.Address)
                    : NativeV11CollaborationOwners.ExpandExistingOwners(session, [owner], allowSharedTableDatabase: true);
                if (requiredOwners.Any(required => !suppliedNames.Contains(Name(required.SimpleModule))))
                    throw Review("The requested plan omits a native owner sharing the table database or other intersecting files.");
            }
        }
        ValidateOwnersCore(session.Context.DesignerAssembly, bound.Modules, bound.Signature, savedOwners, allowPendingDeletion);
        var before = CaptureModules(session, bound, true);
        var cancellationDirectFiles = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var owner in savedOwners)
        {
            var name = Name(owner.SimpleModule);
            if (before.TryGetValue(name, out var prior))
            {
                if (prior.State == 3 && owner.State == "Remove")
                    cancellationDirectFiles.Add(name, NativeV11CollaborationOwners.GetDirectFiles(session, owner with { State = "Edit" }, includeTracked: false).ToArray());
            }
        }
        if (cancellationDirectFiles.Count != 0 && (cancellationDirectFiles.Count != 1 || savedOwners.Length != 1 ||
            savedOwners[0].Address.Kind is not (ModuleKind.Page or ModuleKind.Report or ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification or ModuleKind.Folder or ModuleKind.Image)))
            throw Review("New-module cancellation currently requires exactly one unshared supported owner.");
        var files = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var directAtCheckout = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var owner in savedOwners)
        {
            var name = Name(owner.SimpleModule);
            if (owner.State == "New")
            {
                if (mutation is null || mutation.Operation != ModuleOperation.Create && !groupedFolderMutation || bound.Modules.Contains(name))
                    throw Review("A new owner requires its verified creation request and an unused identity.");
                var future = reviewedFutureGroups is null ? NativeV11CollaborationOwners.GetMutationFiles(session, mutation, [owner]) : reviewedFutureGroups[name];
                if (future.Count == 0 || future.Distinct(StringComparer.OrdinalIgnoreCase).Count() != future.Count)
                    throw Review("A future native owner group is empty or ambiguous.");
                files[name] = future.Select(path => Relative(session.SourcePath, path)).Order(StringComparer.Ordinal).ToArray();
                if (files[name].Distinct(StringComparer.OrdinalIgnoreCase).Count() != files[name].Length)
                    throw Review("A future native owner group contains colliding normalized paths.");
                directAtCheckout[name] = NativeV11CollaborationOwners.GetUnregisteredDirectFiles(session, owner).ToArray();
                continue;
            }
            var nativeModule = bound.Modules[name];
            var removed = NativeV11Reflection.Get(nativeModule!, "State")?.ToString() == "Remove";
            if (!removed)
                directAtCheckout[name] = NativeV11CollaborationOwners.GetDirectFiles(session, owner with { State = "Edit" }, includeTracked: false, allowSharedTableDatabase: allowSharedTableDatabase).ToArray();
            var groupSource = removed && allowPendingDeletion ? NativeV11CollaborationOwners.CaptureNativeFileGroups(session)[name].Files :
                NativeV11CollaborationOwners.GetDirectFiles(session, owner with { State = "Edit" }, allowSharedTableDatabase: allowSharedTableDatabase);
            var group = groupSource.Select(path => Relative(session.SourcePath, path))
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            if (group.Length == 0) throw Review("An existing native owner has no proven complete file group.");
            if (files.TryGetValue(name, out var existing) && !existing.SequenceEqual(group, StringComparer.Ordinal))
                throw Review("Logical aliases disagree about their complete native owner file group.");
            files[name] = group;
        }
        bool CoveredSharedEffect(string path) => savedOwners.Any(owner =>
            files[Name(owner.SimpleModule)].Contains(path, StringComparer.OrdinalIgnoreCase) &&
            (owner.State != "New" || owner.Address.Kind is (ModuleKind.Table or ModuleKind.View) &&
                path.Equals(NativeV11CollaborationOwners.TableDatabasePath(), StringComparison.OrdinalIgnoreCase) &&
                (NativeV11Reflection.Get(owner.SimpleModule, "ExclusiveType") is object exclusive && Convert.ToInt32(exclusive) == 0 ||
                 mutation?.Address.Kind == ModuleKind.BusinessProcess &&
                 NativeV11Reflection.Get(owner.SimpleModule, "ModuleType")?.ToString() == "TableProcessView")));
        if (sharedFiles.Any(path => !CoveredSharedEffect(path) || !File.Exists(NativeV11Session.Under(session.SourcePath, path))))
            throw Review("A reviewed shared effect lacks an existing native owner or saved source file.");
        var plan = new Plan(session, savedOwners, bound, before, files, allowPendingDeletion, cancellationDirectFiles, directAtCheckout, allowSharedTableDatabase, ancillaryFiles, sharedFiles);
        ValidateOwners(plan);
        return plan;
    }

    // Readonly plan verification. No native permission/checkout UI, Git IO,
    // global signature initialization, lock setter or source writer is invoked.
    internal static void ValidateOwners(Plan plan)
    {
        var session = plan.Session; var bound = Bind(session);
        if (!ReferenceEquals(bound.Manager, plan.Manager) || !ReferenceEquals(bound.Collaboration, plan.Collaboration) ||
            !ReferenceEquals(bound.Signature, plan.Signature) || !ReferenceEquals(bound.Modules, plan.Modules) || bound.User != plan.ExpectedUser ||
            session.Context.CaptureSnapshot().Revision != plan.RawRevision || CaptureBusinessRevision(session.SourcePath) != plan.BusinessRevision ||
            SimpleRevisionOf(session.Context.DesignerAssembly, plan.Owners) != plan.SimpleRevision || CaptureTracked(session, bound.Manager) != plan.TrackedRevision)
            throw Review("The native session, signature, owner set, source or tracked-file registration changed after checkout planning.");
        var current = CaptureModules(session, bound, true);
        if (!SameModules(plan.Before, current, true)) throw Review("The native module records changed after readonly checkout planning.");
        ValidateOwnersCore(session.Context.DesignerAssembly, bound.Modules, bound.Signature, plan.Owners, plan.AllowPendingDeletion);
    }

    internal static CollaborationStatus GetStatusForOwners(Plan plan) => ReadStatus(plan.Session, plan.Owners);

    // A held lock is insufficient if native checkout requires a state change.
    // In particular New -> Remove must run the real cancellation executor.
    internal static bool CanReuseEditStates(Plan plan)
    {
        var state = Resolve(plan.Session.Context.DesignerAssembly).State;
        var edit = Enum.Parse(state, "Edit");
        return plan.Owners.Count != 0 && plan.Owners.All(owner => owner.State == "Edit" &&
            plan.Before.TryGetValue(Name(owner.SimpleModule), out var original) && original.State > 0 &&
            NativeV11Reflection.Call(original.Model, "StateNeedUpdate", edit) is false);
    }

    internal static void VerifyGroupedMoveRemovalFiles(Plan plan)
    {
        var bound = Bind(plan.Session);
        if (!ReferenceEquals(bound.Manager, plan.Manager) || !ReferenceEquals(bound.Collaboration, plan.Collaboration) || bound.User != plan.ExpectedUser)
            throw Review("The grouped folder owner plan belongs to another native collaboration session.");
        foreach (var owner in plan.Owners.Where(owner => owner.State is "Move" or "Remove"))
        {
            var name = Name(owner.SimpleModule);
            if (bound.Modules[name] is not object native || NativeV11Reflection.Get(native, "ToRemoveFiles") is not IEnumerable values)
                throw Review("A moved or removed native owner lacks its original removal file registration.");
            var actual = values.Cast<object>().Select(value => value is string path ? Relative(plan.Session.SourcePath, path)
                : throw Review("A native removal path is invalid.")).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
            if (!plan.Files.TryGetValue(name, out var expected) || !actual.SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal))
                throw Review("A moved or removed native owner does not retain its complete original file group.");
        }
    }

    // Verify an observation of the native New -> Remove branch, not a
    // substitute implementation of the executor. The frozen image is captured
    // before dispatch and the current images must already be read back from
    // the native registry. Other owners may not change in this narrow case.
    internal static void VerifyCancelledNewCore(string name, UserIdentity user,
        IReadOnlyDictionary<string, ModuleImage> before, IReadOnlyDictionary<string, ModuleImage> after,
        IReadOnlyDictionary<string, string[]> trackedBefore, IReadOnlyDictionary<string, string[]> trackedAfter,
        IReadOnlyList<string> nativeDirectFiles)
    {
        if (!before.TryGetValue(name, out var original) || original.State != 3 || original.User != user ||
            after.ContainsKey(name) || after.Count != before.Count - 1 ||
            before.Any(pair => pair.Key != name && (!after.TryGetValue(pair.Key, out var current) || pair.Value.Json != current.Json)))
            throw Review("The native cancellation did not remove exactly the current user's New registration.");
        if (nativeDirectFiles.Count == 0 || nativeDirectFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != nativeDirectFiles.Count ||
            !trackedAfter.TryGetValue(name, out var actual) || !actual.Order(StringComparer.Ordinal).SequenceEqual(nativeDirectFiles.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            trackedBefore.Any(pair => pair.Key != name && (!trackedAfter.TryGetValue(pair.Key, out var paths) || !pair.Value.SequenceEqual(paths, StringComparer.Ordinal))) ||
            trackedAfter.Keys.Any(key => key != name && !trackedBefore.ContainsKey(key)))
            throw Review("The native cancellation tracked-file registration differs from its frozen native provider output or changed an unrelated owner.");
    }

    internal static void VerifyCancelledAfterSave(Plan plan)
    {
        var bound = Bind(plan.Session);
        if (!ReferenceEquals(bound.Manager, plan.Manager) || !ReferenceEquals(bound.Collaboration, plan.Collaboration) ||
            !ReferenceEquals(bound.Signature, plan.Signature) || bound.User != plan.ExpectedUser || plan.CancellationDirectFiles.Count != 1)
            throw Review("The saved cancellation no longer belongs to the frozen native collaboration session.");
        var name = plan.CancellationDirectFiles.Single().Key;
        var current = CaptureModules(plan.Session, bound, true);
        if (!plan.Before.TryGetValue(name, out var prior) || prior.State != 3 || prior.User != plan.ExpectedUser || current.ContainsKey(name) ||
            current.Count != plan.Before.Count - 1 || plan.Before.Any(pair => pair.Key != name &&
                (!current.TryGetValue(pair.Key, out var value) || value.Json != pair.Value.Json)))
            throw Review("The saved cancellation registry or another native module changed after checkout.");
    }

    // Pending/save retains this plan even when its table/page/entity has been
    // deleted. Ownership is verified against the original native identity;
    // absence is an error, never an unlocked/deleted-success inference.
    internal static void VerifyOwned(Plan plan, bool afterMutation, bool requireRegisteredModel = false)
    {
        if (!afterMutation) ValidateOwners(plan);
        var bound = Bind(plan.Session);
        if (!ReferenceEquals(bound.Manager, plan.Manager) || !ReferenceEquals(bound.Collaboration, plan.Collaboration) ||
            !ReferenceEquals(bound.Signature, plan.Signature) || bound.User != plan.ExpectedUser ||
            SimpleRevisionOf(plan.Session.Context.DesignerAssembly, plan.Owners) != plan.SimpleRevision)
            throw Review("The frozen collaboration owner plan belongs to a different native session or identity.");
        var current = CaptureModules(plan.Session, bound, true);
        if (plan.CancellationDirectFiles.Count != 0)
        {
            if (plan.CancellationDirectFiles.Count != 1 || plan.Owners.Count != 1)
                throw Review("A new-module cancellation requires exactly one independently verified native owner.");
            var cancellation = plan.CancellationDirectFiles.Single();
            VerifyCancelledNewCore(cancellation.Key, plan.ExpectedUser, plan.Before, current, plan.TrackedBefore,
                CaptureTrackedFiles(plan.Session, bound.Manager), cancellation.Value);
            return;
        }
        var targets = plan.Owners.Select(owner => Name(owner.SimpleModule)).ToHashSet(StringComparer.Ordinal);
        var expectedNew = plan.Owners.Count(owner => owner.State == "New" && !plan.Before.ContainsKey(Name(owner.SimpleModule)));
        if (current.Count != plan.Before.Count + expectedNew || plan.Before.Any(pair => !current.TryGetValue(pair.Key, out var value) ||
            !targets.Contains(pair.Key) && pair.Value.Json != value.Json))
            throw Review("A mutation changed collaboration registrations outside its frozen native owner set.");
        var states = ReadOwnersCore(plan.Session.Context.DesignerAssembly, bound.Modules, bound.Signature, plan.Owners);
        if (states.Count != plan.Owners.Count || states.Any(state => !state.OwnedByCurrentUser || state.LockedBy != plan.ExpectedUser || state.NativeState is not (> 0)))
            throw Review("Every retained native owner must still have a verified current-user lock before pending changes can be saved.");
        foreach (var state in states)
            if (NativeV11Reflection.Call(bound.Manager, "IsModuleCheckoutByCurrentUser", state.NativeModuleName) is not true)
                throw Review("The native manager does not confirm ownership of the frozen mutation owner.");
        if (requireRegisteredModel)
            foreach (var owner in plan.Owners.Where(owner => owner.State == "New"))
            {
                var actual = NativeV11CollaborationOwners.GetDirectFiles(plan.Session, owner with { State = "Edit" }, allowSharedTableDatabase: plan.AllowSharedTableDatabase);
                if (!actual.Order(StringComparer.Ordinal).SequenceEqual(plan.Files[Name(owner.SimpleModule)].Order(StringComparer.Ordinal), StringComparer.Ordinal))
                    throw Review("The created native model did not register its complete reviewed file group.");
            }
    }

    internal static async Task<CheckoutResult> CheckoutExistingPlanAsync(Plan plan, CollaborationMutationRequest request,
        Action verifyContext, CancellationToken cancellationToken = default)
    {
        var session = plan.Session;
        if (request.Target != session.Context.CaptureSnapshot().Target || request.ExpectedUser != plan.ExpectedUser ||
            !request.Modules.OrderBy(AddressKey, StringComparer.Ordinal).SequenceEqual(plan.Owners.Select(owner => owner.Address).OrderBy(AddressKey, StringComparer.Ordinal)))
            throw Review("The checkout request differs from its frozen target, identity or logical owner projection.");
        cancellationToken.ThrowIfCancellationRequested();
        verifyContext(); ValidateOwners(plan);
        var repository = await NativeV11CollaborationRepository.CaptureAsync(session.SourcePath, request, [".collaboration"]);
        if (repository.Git.ChangedFiles is null || repository.Git.ChangedFiles.Keys.Any(path => path.Replace('\\', '/').Equals(".collaboration", StringComparison.OrdinalIgnoreCase)))
            throw Review("An uncommitted collaboration registry cannot establish a synchronized existing lock or a safe new checkout baseline.");
        await NativeV11CollaborationRepository.VerifyRegistryAtHeadAsync(session.SourcePath, repository.Git);
        await NativeV11CollaborationRepository.VerifyBeforeAsync(session.SourcePath, repository, request);
        if (plan.SharedEffectFiles.Any(path => repository.Git.ChangedFiles!.Keys.Any(changed => changed.Equals(path, StringComparison.OrdinalIgnoreCase))))
            throw Review("A reviewed shared effect has uncommitted Git changes before native checkout.");
        var sharedFilesAtHead = plan.SharedEffectFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.CancellationDirectFiles.Count != 0)
            await NativeV11CollaborationRepository.VerifyNewFilesAbsentAtHeadAsync(session.SourcePath, repository,
                plan.Files.Values.SelectMany(paths => paths));
        var existingFiles = plan.Owners.Where(owner => owner.State != "New")
            .SelectMany(owner => plan.Files[Name(owner.SimpleModule)]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        existingFiles.UnionWith(plan.SharedEffectFiles);
        if (plan.Owners.Any(owner => owner.State == "New" && owner.Address.Kind is ModuleKind.Table or ModuleKind.View))
        {
            var database = NativeV11CollaborationOwners.TableDatabasePath();
            if (File.Exists(NativeV11Session.Under(session.SourcePath, database)))
            {
                if (repository.Git.ChangedFiles!.Keys.Any(path => path.Equals(database, StringComparison.OrdinalIgnoreCase)))
                    throw Review("The existing shared table database has uncommitted changes before native checkout.");
                existingFiles.Add(database);
                sharedFilesAtHead.Add(database);
            }
        }
        if (sharedFilesAtHead.Count != 0)
            await NativeV11CollaborationRepository.VerifySharedFilesAtHeadAsync(session.SourcePath, repository, sharedFilesAtHead);
        var newFiles = plan.Owners.Where(owner => owner.State == "New")
            .SelectMany(owner => plan.Files[Name(owner.SimpleModule)])
            .Where(path => !existingFiles.Contains(path)).Concat(plan.FutureAncillaryFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (newFiles.Length != 0)
            await NativeV11CollaborationRepository.VerifyNewFilesAbsentAtHeadAsync(session.SourcePath, repository, newFiles);
        // Deliberately retain the owning UI synchronization context across Git
        // awaits. After native dispatch cancellation does not interrupt/replay.
        cancellationToken.ThrowIfCancellationRequested();
        verifyContext(); ValidateOwners(plan);
        var contract = Resolve(session.Context.DesignerAssembly);
        var needed = plan.Owners.GroupBy(owner => Name(owner.SimpleModule), StringComparer.Ordinal).Select(group => group.First())
            .Where(owner => !plan.Before.ContainsKey(Name(owner.SimpleModule)) || plan.Before[Name(owner.SimpleModule)].State == 0 ||
                NativeV11Reflection.Call(plan.Before[Name(owner.SimpleModule)].Model, "StateNeedUpdate", Enum.Parse(contract.State, owner.State)) is true)
            .ToArray();
        if (needed.Length == 0)
        {
            var synchronized = new NativeV11CollaborationRepository.Outcome(repository.Git, repository.RemoteHead, [], true, null);
            return new(null, true, false, GetStatusForOwners(plan) with { Git = repository.Git }, "AlreadyOwned", synchronized);
        }
        if (needed.Any(owner => !plan.DirectAtCheckout.ContainsKey(Name(owner.SimpleModule))))
            return new(null, false, false, GetStatusForOwners(plan) with { Git = repository.Git }, "NativeNewTrackedFilesUnverified");
        VerifyCheckoutDependencies(session.Context.DesignerAssembly);
        var checkoutIdentity = NativeCheckoutSystemIdentity(session.Context.DesignerAssembly, plan.Collaboration);
        if (checkoutIdentity == plan.ExpectedUser)
            throw Review("The native system checkout signature must be distinct from the current lock owner.");
        var tuples = Array.CreateInstance(contract.Tuple, needed.Length);
        for (var index = 0; index < needed.Length; index++)
            tuples.SetValue(Activator.CreateInstance(contract.Tuple, [needed[index].SimpleModule, Enum.Parse(contract.State, needed[index].State)]), index);
        var constructor = contract.Multi.GetConstructors(NativeV11Reflection.Flags).Single(candidate => candidate.GetParameters().Length == 2);
        var executor = constructor.Invoke([null, tuples]);
        var taskType = contract.Checkout.GetParameters()[2].ParameterType;
        var taskInfo = Activator.CreateInstance(taskType, [false, false, 100])!;
        bool? accepted = null;
        try
        {
            // Checkout contains native repository update, lock/SaveModules and
            // commit/push. Its own retry/error paths may have partial effects.
            accepted = (bool)contract.Checkout.Invoke(executor, [tuples, repository.Git.Head, taskInfo])!;
            verifyContext();
            var bound = Bind(session);
            if (!ReferenceEquals(bound.Manager, plan.Manager) || !ReferenceEquals(bound.Collaboration, plan.Collaboration) || bound.User != plan.ExpectedUser ||
                SimpleRevisionOf(session.Context.DesignerAssembly, plan.Owners) != plan.SimpleRevision ||
                CaptureBusinessRevision(session.SourcePath) != plan.BusinessRevision)
                throw Review("Checkout changed unrelated source, the native session or save registration.");
            if (plan.CancellationDirectFiles.Count != 0)
            {
                VerifyOwned(plan, true);
                var cancellationOutcome = await NativeV11CollaborationRepository.VerifyAfterCheckoutAsync(session.SourcePath, repository, checkoutIdentity);
                verifyContext();
                VerifyOwned(plan, true);
                var cancellationStatus = ReadStatus(session, []) with { Git = cancellationOutcome.Git };
                return accepted is true && cancellationOutcome.RemoteSynchronized is true
                    ? new(true, true, false, cancellationStatus, "NewRegistrationCancelled", cancellationOutcome)
                    : new(accepted, false, true, cancellationStatus, "CheckoutVerificationIncomplete", cancellationOutcome);
            }
            var expectedTracked = new Dictionary<string, string[]>(plan.TrackedBefore, StringComparer.Ordinal);
            foreach (var owner in needed)
            {
                var name = Name(owner.SimpleModule);
                if (!plan.DirectAtCheckout.TryGetValue(name, out var direct))
                    throw Review("Native checkout of a new owner requires a separate tracked-file proof.");
                expectedTracked[name] = direct;
            }
            RequireTrackedMatchCore(expectedTracked, CaptureTrackedFiles(session, bound.Manager));
            var current = CaptureModules(session, bound, true);
            var targets = plan.Owners.Select(owner => Name(owner.SimpleModule)).ToHashSet(StringComparer.Ordinal);
            var expectedNew = plan.Owners.Count(owner => owner.State == "New" && !plan.Before.ContainsKey(Name(owner.SimpleModule)));
            if (current.Count != plan.Before.Count + expectedNew || plan.Before.Any(pair => !current.TryGetValue(pair.Key, out var now) ||
                !targets.Contains(pair.Key) && now.Json != pair.Value.Json))
                throw Review("Checkout changed an unrelated native module or its registration.");
            var status = GetStatusForOwners(plan);
            var locks = status.Modules.Count == plan.Owners.Count && status.Modules.All(state => state.OwnedByCurrentUser && state.LockedBy == plan.ExpectedUser && state.NativeState is > 0);
            foreach (var owner in plan.Owners)
            {
                var name = Name(owner.SimpleModule);
                var image = current[name];
                if (!plan.Before.ContainsKey(name))
                {
                    if (owner.State != "New" || image.State != Convert.ToInt32(Enum.Parse(contract.State, "New")))
                        throw Review("The native new-module registration was not retained.");
                    continue;
                }
                var before = JsonNode.Parse(plan.Before[name].Json)!.AsObject(); var after = JsonNode.Parse(image.Json)!.AsObject();
                foreach (var field in new[] { "State", "LockedBy", "LockDateTime", "ToRemoveFiles" }) { before.Remove(field); after.Remove(field); }
                if (!JsonNode.DeepEquals(before, after)) throw Review("Checkout changed native owner metadata beyond its lock and file registration.");
                if (NativeV11Reflection.Get(image.Model, "ToRemoveFiles") is IEnumerable paths)
                {
                    var actual = paths.Cast<string>().Select(path => Relative(session.SourcePath, path)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
                    if (!actual.SequenceEqual(plan.Files[name], StringComparer.Ordinal)) throw Review("Native checkout registered an unexpected or incomplete owner file group.");
                }
                else if (plan.Before[name].State == 0) throw Review("The newly acquired native lock has no verified source-file registration.");
            }
            var outcome = await NativeV11CollaborationRepository.VerifyAfterCheckoutAsync(session.SourcePath, repository, checkoutIdentity);
            verifyContext();
            // Recheck native ownership after the asynchronous remote proof.
            status = GetStatusForOwners(plan) with { Git = outcome.Git };
            locks = locks && status.Modules.All(state => state.OwnedByCurrentUser && state.LockedBy == plan.ExpectedUser);
            return accepted is true && locks && outcome.RemoteSynchronized is true
                ? new(true, true, false, status, "CheckedOut", outcome)
                : new(accepted, locks, true, status, "CheckoutVerificationIncomplete", outcome);
        }
        catch (Exception)
        {
            // Do not restore lock files, unlock modules, retry the invocation or
            // imply that native false/exception means no commit was made.
            return new(accepted, false, true, null, "NativeCheckoutOutcomeRequiresReview");
        }
    }

    internal static IReadOnlyList<CollaborationModuleState> ReadOwnersCore(Assembly designer, IDictionary modules, object signature,
        IReadOnlyList<NativeV11CollaborationOwner> owners)
    {
        var contract = Resolve(designer); var user = User(signature, contract);
        var result = new List<CollaborationModuleState>();
        var seen = new HashSet<ModuleAddress>();
        foreach (var owner in owners)
        {
            RequireSimple(owner, contract);
            if (!seen.Add(owner.Address)) throw Review("The logical collaboration projection contains a duplicate address.");
            var name = Name(owner.SimpleModule);
            if (owner.State == "New" && !modules.Contains(name))
            {
                result.Add(new(owner.Address, name, null, null, false));
                continue;
            }
            if (!modules.Contains(name) || modules[name] is not object module || module.GetType() != contract.Module ||
                !Equals(NativeV11Reflection.Get(module, "ModuleType"), NativeV11Reflection.Get(owner.SimpleModule, "ModuleType")) ||
                !Equals(NativeV11Reflection.Get(module, "IconType"), NativeV11Reflection.Get(owner.SimpleModule, "IconType")) ||
                !Equals(NativeV11Reflection.Get(module, "ExclusiveType"), NativeV11Reflection.Get(owner.SimpleModule, "ExclusiveType")))
                throw Review("A requested existing logical owner has no matching actual native module record.");
            var state = Convert.ToInt32(NativeV11Reflection.Get(module, "State"));
            if (state is < 0 or > 4) throw Review("An unknown native collaboration module state is present.");
            if (owner.State == "New" && state != Convert.ToInt32(Enum.Parse(contract.State, "New")))
                throw Review("The new native owner identity collides with an existing module.");
            var locked = NativeV11Reflection.Get(module, "LockedBy");
            var lockedUser = locked is null ? null : User(locked, contract);
            if (state != 0 && (locked is null || NativeV11Reflection.Get(module, "LockDateTime") is null) || state == 0 && locked is not null)
                throw Review("The native module lock has an incomplete or inconsistent signature/time.");
            var owned = state != 0 && locked is not null && signature.Equals(locked);
            if (owned != (state != 0 && lockedUser == user)) throw Review("Native signature equality disagrees with the exact requested user identity.");
            var pendingDeletion = Equals(NativeV11Reflection.Get(module, "State"), Enum.Parse(contract.State, "Remove"));
            result.Add(new(owner.Address, name, state, lockedUser, owned, pendingDeletion));
        }
        return result;
    }

    internal static void ValidateOwnersCore(Assembly designer, IDictionary modules, object signature, IReadOnlyList<NativeV11CollaborationOwner> owners, bool allowPendingDeletion = false)
    {
        var contract = Resolve(designer);
        if (owners.Any(owner => owner.State == "New" && modules.Contains(Name(owner.SimpleModule))))
            throw Review("A new native owner must be absent before checkout planning.");
        var states = ReadOwnersCore(designer, modules, signature, owners);
        if (!allowPendingDeletion && states.Any(state => state.PendingDeletion)) throw Review("A pending native deletion cannot be revived by checkout planning.");
        if (states.Any(state => state.NativeState is > 0 && !state.OwnedByCurrentUser)) throw Review("A requested native owner is locked by another user.");
        foreach (var owner in owners)
        {
            var exclusive = NativeV11Reflection.Get(owner.SimpleModule, "ExclusiveType");
            if (exclusive is null) continue;
            foreach (var value in modules.Values.Cast<object>())
            {
                if (value.GetType() != contract.Module) throw Review("The native module dictionary contains an unknown module type.");
                if (Equals(NativeV11Reflection.Get(value, "ExclusiveType"), exclusive) && Convert.ToInt32(NativeV11Reflection.Get(value, "State")) != 0 &&
                    NativeV11Reflection.Get(value, "LockedBy") is object locked && !signature.Equals(locked))
                    throw Review("Another user owns a native module in the same exclusive resource group.");
            }
        }
    }

    private static Bound Bind(NativeV11Session session)
    {
        session.VerifyContext(); var snapshot = session.Context.CaptureSnapshot();
        if (!snapshot.InCollaborationMode || !Directory.Exists(session.SourcePath)) throw Review("Native checkout applies only to a bound collaboration directory.");
        var contract = Resolve(session.Context.DesignerAssembly);
        var manager = contract.Instance.GetValue(null);
        if (manager is null || manager.GetType() != contract.Manager || NativeV11Reflection.Get(manager, "InCollaborationMode") is not true)
            throw Review("The actual native collaboration manager is not already initialized for this document.");
        var collaboration = NativeV11Reflection.Get(manager, "Collaboration")!;
        if (collaboration.GetType() != contract.Module.Assembly.GetType("Forguncy.Collaboration.Git.GitCollaboration", true) ||
            !SamePath(NativeV11Reflection.Get(collaboration, "WorkingDirectory") as string, session.SourcePath) ||
            !SamePath(NativeV11Reflection.Get(manager, "ModuleFilePath") as string, Path.Combine(session.SourcePath, ".collaboration")))
            throw Review("The native collaboration repository or module registry belongs to a different document.");
        // GitCollaboration.Signature lazily reads/configures a signature when
        // this field is null. Status/planning require an existing witness.
        var signatureField = collaboration.GetType().GetField("_signature", NativeV11Reflection.Flags | BindingFlags.DeclaredOnly);
        if (signatureField?.FieldType != contract.Signature || signatureField.GetValue(collaboration) is not object signature ||
            !ReferenceEquals(NativeV11Reflection.Get(manager, "CurrentUser"), signature))
            throw Review("The bound native repository has no already initialized collaboration signature.");
        var user = User(signature, contract);
        if (snapshot.CurrentUser != user || NativeV11Reflection.Get(manager, "Modules") is not IDictionary modules)
            throw Review("The native collaboration signature or module collection changed.");
        return new(manager, collaboration, signature, user, modules);
    }

    private static SortedDictionary<string, ModuleImage> CaptureModules(NativeV11Session session, Bound bound, bool verifyDisk)
    {
        var contract = Resolve(session.Context.DesignerAssembly);
        var result = new SortedDictionary<string, ModuleImage>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in bound.Modules)
        {
            if (entry.Key is not string name || entry.Value is not object module || module.GetType() != contract.Module || Name(module) != name)
                throw Review("The actual native module registry has an invalid key or identity.");
            var array = Array.CreateInstance(contract.Module, 1); array.SetValue(module, 0);
            var content = NativeV11Reflection.Call(bound.Manager, "GetModulesContent", array) as string ?? throw Review("The native module serializer returned no content.");
            using var parsed = JsonDocument.Parse(content);
            if (parsed.RootElement.ValueKind != JsonValueKind.Array || parsed.RootElement.GetArrayLength() != 1) throw Review("The native module serializer contract changed.");
            var state = Convert.ToInt32(NativeV11Reflection.Get(module, "State")); var locked = NativeV11Reflection.Get(module, "LockedBy");
            result.Add(name, new(module, state, locked is null ? null : User(locked, contract), NativeV11Reflection.Get(module, "ExclusiveType"), parsed.RootElement[0].GetRawText()));
        }
        if (verifyDisk)
        {
            var path = Path.Combine(session.SourcePath, ".collaboration");
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16 * 1024 * 1024)
                throw Review("The persisted native module registry is missing, linked or exceeds its read bound.");
            using var parsed = JsonDocument.Parse(File.ReadAllBytes(path));
            if (parsed.RootElement.ValueKind != JsonValueKind.Array) throw Review("The persisted native module registry is not an array.");
            var disk = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var item in parsed.RootElement.EnumerateArray())
                if (!item.TryGetProperty("ModuleName", out var name) || name.ValueKind != JsonValueKind.String || !disk.TryAdd(name.GetString()!, item))
                    throw Review("The persisted module registry has missing or duplicate identities.");
            if (disk.Count != result.Count || result.Any(pair => !disk.TryGetValue(pair.Key, out var item) || !V11Safety.Equivalent(Element(pair.Value.Json), item)))
                throw Review("The actual native module registry differs from its persisted source records.");
        }
        return result;
    }
    private static string CaptureTracked(NativeV11Session session, object manager)
    {
        return Digest(CaptureTrackedFiles(session, manager).Select(pair => pair.Key + "\0" + string.Join("\0", pair.Value)));
    }
    internal static void RequireTrackedMatchCore(IReadOnlyDictionary<string, string[]> expected, IReadOnlyDictionary<string, string[]> actual)
    {
        if (expected.Count != actual.Count || expected.Any(pair => !actual.TryGetValue(pair.Key, out var files) ||
            !pair.Value.Order(StringComparer.Ordinal).SequenceEqual(files.Order(StringComparer.Ordinal), StringComparer.Ordinal)))
            throw Review("Native checkout changed an unrelated tracked-file group or did not retain the exact native direct files.");
    }
    private static IReadOnlyDictionary<string, string[]> CaptureTrackedFiles(NativeV11Session session, object manager)
    {
        if (NativeV11Reflection.Get(manager, "SaveLoadTrackedFiles") is not IDictionary tracked) throw Review("The native tracked-file registration is unavailable.");
        var records = new SortedDictionary<string, string[]>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in tracked)
        {
            if (entry.Key is not string name || entry.Value is not IEnumerable paths) throw Review("The native tracked-file registration contains an invalid owner or file group.");
            records.Add(name, paths.Cast<string>().Select(path => Relative(session.SourcePath, path)).Order(StringComparer.Ordinal).ToArray());
        }
        return records;
    }
    private static bool SameModules(SortedDictionary<string, ModuleImage> left, SortedDictionary<string, ModuleImage> right, bool identities) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value.Json == value.Json && (!identities || ReferenceEquals(pair.Value.Model, value.Model)));
    private static void RequireSimple(NativeV11CollaborationOwner owner, Contract contract)
    {
        if (owner.SimpleModule.GetType() != contract.Simple || owner.State is not ("Edit" or "New" or "Remove" or "Move") ||
            string.IsNullOrWhiteSpace(Name(owner.SimpleModule)) || NativeV11Reflection.Get(owner.SimpleModule, "ModuleType")?.GetType() != contract.Module.Assembly.GetType("Forguncy.Collaboration.ModuleType", true))
            throw Review("Only exact existing native owner modules with a reviewed requested state can be checked out; new registration is a separate gate.");
    }
    private static UserIdentity User(object signature, Contract contract)
    {
        if (signature.GetType() != contract.Signature || NativeV11Reflection.Get(signature, "UserName") is not string name || string.IsNullOrWhiteSpace(name) ||
            NativeV11Reflection.Get(signature, "Email") is not string email || string.IsNullOrWhiteSpace(email)) throw Review("An exact complete native collaboration signature is required.");
        return new(name, email);
    }
    private static string Name(object module) => NativeV11Reflection.Get(module, "ModuleName") as string ?? throw Review("The native owner name is missing.");
    private static string SimpleRevisionOf(Assembly designer, IEnumerable<NativeV11CollaborationOwner> owners) => Digest(owners.Select(owner => AddressKey(owner.Address) + "\0" + owner.State + "\0" +
        NativeV11Reflection.Static(designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!, "SaveToStr", owner.SimpleModule)));
    private static string AddressKey(ModuleAddress address) => address.Kind + "/" + address.FolderPath + "/" + address.Name;
    private static string Relative(string root, string path)
    {
        var relative = Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
        relative = relative.Replace('\\', '/');
        var directory = relative.EndsWith('/');
        var node = directory ? relative[..^1] : relative;
        _ = V11Safety.Under(root, node);
        if (new[] { ".collaboration", ".git", ".local", "LocalData", "LastSaveFiles" }.Contains(node.Split('/')[0], StringComparer.OrdinalIgnoreCase))
            throw Review("A native module file group crosses a repository control or cache boundary.");
        return relative;
    }
    private static string CaptureBusinessRevision(string root)
    {
        var records = new List<string>();
        void Visit(string directory, string prefix)
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw Review("Checkout proof cannot traverse source links.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(path);
                if (prefix.Length == 0 && name is ".git" or ".local" or "LocalData" or "LastSaveFiles" or ".collaboration") continue;
                var key = prefix + name; var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Review("Checkout proof cannot traverse source links.");
                if ((attributes & FileAttributes.Directory) != 0) { records.Add("D\0" + key); Visit(path, key + "/"); }
                else { using var stream = File.OpenRead(path); records.Add("F\0" + key + "\0" + Convert.ToHexString(SHA256.HashData(stream))); }
            }
        }
        Visit(root, ""); return Digest(records);
    }
    private static Contract Resolve(Assembly designer) => Contracts.GetValue(designer, assembly =>
    {
        var profile = V11BuildProfile.Resolve(assembly); var newer = profile.Version == "11.0.104.0";
        var collaboration = Assembly.Load("Forguncy.Collaboration");
        VerifyAssembly(collaboration, profile.Version, newer ? "228DD755C5607955D06F22751A04FD97ECB30095B54F23BBF468E078762B38F5" : "FAC1A08973E9B81B5C4D32B44C661B129D7FE8D50FE84A9072B6465022E611F8");
        var manager = assembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        if (manager.TypeInitializer is not null) throw Review("Readonly collaboration inspection cannot run an unreviewed manager type initializer.");
        var instance = manager.GetField("_instance", NativeV11Reflection.Flags | BindingFlags.DeclaredOnly);
        if (instance is null || !instance.IsStatic || instance.FieldType != manager) throw Review("The exact existing native collaboration-manager field is unavailable.");
        var simple = assembly.GetType("Forguncy.Common.SimpleModule", true)!; var state = collaboration.GetType("Forguncy.Collaboration.ModuleState", true)!;
        foreach (var (name, value) in new[] { ("Unlocked", 0), ("Edit", 1), ("Remove", 2), ("New", 3), ("Move", 4) })
            if (Convert.ToInt32(Enum.Parse(state, name)) != value) throw Review("The native module-state enum differs from the reviewed V11 build.");
        var tuple = typeof(ValueTuple<,>).MakeGenericType(simple, state);
        var executor = assembly.GetType("Forguncy.Common.CollaborationExecutor", true)!;
        var checkout = executor.GetMethods(NativeV11Reflection.Flags | BindingFlags.DeclaredOnly).Single(method => method.Name == "Checkout");
        if (checkout.ReturnType != typeof(bool) || checkout.GetParameters().Length != 3 || checkout.GetParameters()[0].ParameterType != typeof(IEnumerable<>).MakeGenericType(tuple) ||
            checkout.GetParameters()[1].ParameterType != typeof(string) || checkout.GetParameters()[2].ParameterType.FullName != "WPFBase.Common.TaskInfo")
            throw Review("The actual native checkout signature differs from the reviewed V11 build.");
        return new(manager, collaboration.GetType("Forguncy.Collaboration.Module", true)!, collaboration.GetType("Forguncy.Collaboration.Signature", true)!,
            simple, state, tuple, assembly.GetType("Forguncy.Common.MultiExecutor", true)!, instance, checkout);
    });
    internal static void VerifyCheckoutDependencies(Assembly designer)
    {
        var (git, native, expectedDirectory) = VerifiedComponentFiles(designer);
        // The pinned GlobalSettings cctor only initializes managed caches and
        // computes the assembly-relative native path. Its getter does not load
        // git2, lock the override or call NativeMethods' native initializer.
        var configured = NativeV11Reflection.Static(git.GetType("LibGit2Sharp.GlobalSettings", true)!, "get_NativeLibraryPath") as string;
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 || configured is not null && !SamePath(configured, expectedDirectory))
            throw Review("The native Git resolver is redirected or does not match the reviewed x64 installation.");
        var loaded = false;
        using var process = Process.GetCurrentProcess();
        foreach (ProcessModule module in process.Modules)
        {
            if (!string.Equals(module.ModuleName, "git2-106a5f2.dll", StringComparison.OrdinalIgnoreCase)) continue;
            if (!SamePath(module.FileName, native)) throw Review("A different native Git library with the same import name is already loaded.");
            loaded = true;
        }
        if (configured is null && !loaded)
            throw Review("The native Git resolver has no proven installed override or already loaded native-library witness.");
    }
    internal static void VerifyComponentFingerprints(Assembly designer) => _ = VerifiedComponentFiles(designer);
    private static (Assembly Git, string Native, string Directory) VerifiedComponentFiles(Assembly designer)
    {
        var profile = V11BuildProfile.Resolve(designer); _ = Resolve(designer);
        VerifyAssembly(Assembly.Load("WPFBase"), profile.Version, profile.Version == "11.0.104.0" ? "E5425A1E1D20B7FF0431BB91286B514A4845365EA53CC875C85B28C611F90440" : "E9A835E01A159211030E8DB1F71E3267C2CF6175A807BFE0839991FC89EB289F");
        var git = Assembly.Load("LibGit2Sharp");
        VerifyAssembly(git, "0.26.0.0", "DDA07C9CDFAF1F1C90852BB45DD9FCA94381DC1A5AC190F3975DB4B8BA0052AA");
        var expectedDirectory = Path.Combine(Path.GetDirectoryName(git.Location)!, "runtimes", "win-x64", "native");
        var native = V11Safety.Under(Path.GetDirectoryName(git.Location)!, "runtimes/win-x64/native/git2-106a5f2.dll");
        using (var stream = File.OpenRead(native))
            if (Convert.ToHexString(SHA256.HashData(stream)) != "9DAA0BCB0CEE0AEEEDB7C55432740C124A6C667F2C488B09F757E46F956804A4")
                throw Review("The native Git library fingerprint differs from the reviewed V11 installation.");
        return (git, native, expectedDirectory);
    }
    internal static bool NativeCheckoutUsesUserCommit(Assembly designer)
    {
        var contract = Resolve(designer); var assembly = contract.Module.Assembly;
        var type = assembly.GetType("Forguncy.Collaboration.CommitMessage", true)!;
        var messageType = assembly.GetType("Forguncy.Collaboration.MessageType", true)!;
        var message = Activator.CreateInstance(type, ["Native checkout protocol metadata check", Enum.Parse(messageType, "CheckOutModule")])!;
        return NativeV11Reflection.Call(message, "IsUserCommit") is true;
    }
    internal static UserIdentity NativeCheckoutSystemIdentity(Assembly designer, object collaboration)
    {
        var contract = Resolve(designer); var assembly = contract.Module.Assembly;
        if (collaboration.GetType() != assembly.GetType("Forguncy.Collaboration.Git.GitCollaboration", true) || NativeCheckoutUsesUserCommit(designer))
            throw Review("The exact native system checkout signature protocol is unavailable.");
        var messageType = assembly.GetType("Forguncy.Collaboration.MessageType", true)!;
        var message = Activator.CreateInstance(assembly.GetType("Forguncy.Collaboration.CommitMessage", true)!,
            ["Native checkout protocol metadata check", Enum.Parse(messageType, "CheckOutModule")])!;
        var signature = NativeV11Reflection.Call(collaboration, "GetSignature", message) ?? throw Review("The native checkout signature is unavailable.");
        var name = NativeV11Reflection.Get(signature, "Name") as string;
        var email = NativeV11Reflection.Get(signature, "Email") as string;
        if (name is not ("HZG System" or "Forguncy System") || email != name)
            throw Review("The native checkout signature is not an exact reviewed system signature.");
        return new(name, email);
    }
    private static void VerifyAssembly(Assembly assembly, string version, string hash)
    {
        if (assembly.GetName().Version?.ToString() != version || string.IsNullOrEmpty(assembly.Location)) throw Review("A native collaboration dependency has an unverified identity.");
        using var stream = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(stream)) != hash) throw Review("A native collaboration dependency fingerprint differs from the reviewed installation.");
    }
    private static string Digest(IEnumerable<string> values) => NativeV11Session.Hash(Encoding.UTF8.GetBytes(string.Join("\n", values.Order(StringComparer.Ordinal))));
    private static bool SamePath(string? left, string right) => left is not null && Path.GetFullPath(left).TrimEnd('\\', '/').Equals(Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static JsonElement Element(string text) { using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone(); }
    private static BridgeException Review(string message) => new(Code, message);
}
