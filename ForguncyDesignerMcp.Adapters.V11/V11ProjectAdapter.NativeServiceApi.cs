using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Native service actions have a distinct receipt and never enter the stronger
// module persistence/export path. All calls use this document's UI scheduler.
public sealed partial class V11ProjectAdapter
{
    private NativeV11UserManagement? nativeAccountClient;
    private NativeV11UserManagement NativeAccountClient => nativeAccountClient ??= new(session);
    private readonly HashSet<string> nativeAccountStarted = new(StringComparer.Ordinal);
    private bool nativeAccountUncertain;
    private string? nativeAccountDirtyRevision;

    private static IReadOnlyList<PlatformApiDescriptor> NativeAccountDescriptors() =>
    [
        NativeV11UserManagement.ReadUserDescriptor(true), NativeV11UserManagement.ReadUserDescriptor(false),
        NativeV11UserManagement.CreateUserDescriptor(), NativeV11UserManagement.UpdateUserDescriptor(), NativeV11UserManagement.DeleteUsersDescriptor(),
        NativeV11UserManagement.CreateRoleDescriptor(), NativeV11UserManagement.EntityMutationDescriptor(true, false), NativeV11UserManagement.EntityMutationDescriptor(true, true),
        NativeV11UserManagement.CreateOrganizationDescriptor(), NativeV11UserManagement.EntityMutationDescriptor(false, false), NativeV11UserManagement.EntityMutationDescriptor(false, true),
        .. NativeV11OrganizationRead.Descriptors(), .. NativeV11UserManagement.UserRelationDescriptors(), .. NativeV11UserManagement.OrgRoleRelationDescriptors(),
        .. NativeV11UserManagement.UserSecurityDescriptors(), .. NativeV11UserManagement.ExtraDescriptors()
    ];

    private static bool NativeV11UserManagementRead(PlatformApiRequest request) => request.Namespace == "userManagement.user" && request.Method is "getUser" or "listUsers";

    private async Task<JsonElement> InvokeNativeAccountReadAsync(PlatformApiRequest request, CancellationToken token)
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                CheckTarget(request.Target);
                var result = await NativeAccountClient.ReadUsersAsync(request, token);
                CheckTarget(request.Target);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    private async Task<JsonElement> InvokeNativeOrganizationReadAsync(PlatformApiRequest request, CancellationToken token)
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                CheckTarget(request.Target);
                var result = await new NativeV11OrganizationRead(session).ReadAsync(request, token);
                CheckTarget(request.Target);
                return result;
            }, token).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task ValidateNativeServiceApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await context.Ui.RunAsync(async () =>
            {
                var guard = ValidateNativeAccountGuard(request);
                CheckNativeAccountContext(request, guard);
                NativeAccountClient.ValidateRequest(request);
                await NativeAccountClient.RequireWriteSupportedAsync(cancellationToken);
                CheckNativeAccountContext(request, guard);
                return true;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    public async Task<NativeServiceApiReceipt> InvokeNativeServiceApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        await writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await context.Ui.RunAsync(async () =>
            {
                var guard = ValidateNativeAccountGuard(request);
                CheckNativeAccountContext(request, guard);
                NativeAccountClient.ValidateRequest(request);
                await NativeAccountClient.RequireWriteSupportedAsync(cancellationToken);
                CheckNativeAccountContext(request, guard);
                cancellationToken.ThrowIfCancellationRequested();
                if (!nativeAccountStarted.Add(request.OperationId!))
                    throw new BridgeException("OperationAlreadyStarted", "The native service action is already reserved; query its original operation ID.");
                var before = Snapshot();
                try
                {
                    var receipt = (request.Namespace, request.Method) switch
                    {
                        ("userManagement.user", "createUser") => await NativeAccountClient.CreateUserAsync(request, CancellationToken.None),
                        ("userManagement.user", "updateUser") => await NativeAccountClient.UpdateUserAsync(request, CancellationToken.None),
                        ("userManagement.user", "deleteUsers") => await NativeAccountClient.DeleteUsersAsync(request, CancellationToken.None),
                        ("userManagement.role", "createRole") or ("userManagement.organization", "createOrganization") => await NativeAccountClient.CreateEntityAsync(request, CancellationToken.None),
                        ("userManagement.role", "updateRole" or "deleteRoles") or ("userManagement.organization", "updateOrganization" or "deleteOrganizations") => await NativeAccountClient.MutateEntityAsync(request, CancellationToken.None),
                        ("userManagement.user", "addRolesToUsers" or "removeRolesFromUsers" or "setRolesForUsers" or "addOrganizationsToUsers" or "removeOrganizationsFromUsers" or "setOrganizationsForUsers") => await NativeAccountClient.MutateUserRelationsAsync(request, CancellationToken.None),
                        ("userManagement.role", "addUsersToRoles" or "removeUsersFromRoles" or "setUsersForRoles") or
                            ("userManagement.organization", "addUsersToOrganizations" or "removeUsersFromOrganizations" or "setUsersForOrganizations") => await NativeAccountClient.MutateUserRelationsAsync(request, CancellationToken.None),
                        ("userManagement.role", "addOrganizationsToRoles" or "removeOrganizationsFromRoles" or "setOrganizationsForRoles" or "setRolesForOrganizations") or
                            ("userManagement.organization", "addRolesToOrganizations" or "removeRolesFromOrganizations" or "setRolesForOrganizations") => await NativeAccountClient.MutateOrgRoleRelationsAsync(request, CancellationToken.None),
                        ("userManagement.user", "setUsersPassword" or "setUsersValidity") => await NativeAccountClient.MutateUserSecurityAsync(request, CancellationToken.None),
                        _ when NativeV11UserManagement.HandlesExtra(request) => await NativeAccountClient.InvokeExtraAsync(request, CancellationToken.None),
                        _ => throw new BridgeException("NativeServiceApiNotAllowed", "The native service action is not registered.")
                    };
                    var after = Snapshot();
                    if (after.Target != before.Target || after.CurrentUser != before.CurrentUser || after.Busy ||
                        receipt.NativeAccepted == false && (after.Revision != before.Revision || after.HasUnsavedChanges != before.HasUnsavedChanges) ||
                        receipt.NativeAccepted is null || receipt.ExecutionMayContinue)
                    {
                        nativeAccountUncertain = true;
                        return UncertainAccountReceipt(request.OperationId!);
                    }
                    if (receipt.NativeAccepted == true) nativeAccountDirtyRevision = after.HasUnsavedChanges ? after.Revision : null;
                    return receipt;
                }
                catch
                {
                    nativeAccountUncertain = true;
                    return UncertainAccountReceipt(request.OperationId!);
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }

    private PlatformApiMutationGuard ValidateNativeAccountGuard(PlatformApiRequest request)
    {
        if (!Guid.TryParseExact(request.OperationId, "D", out _) || request.Parameters.ValueKind != JsonValueKind.Object ||
            !request.Parameters.TryGetProperty("guard", out var json))
            throw new BridgeException("MutationGuardRequired", "Native account actions require the exact original operation ID and standard guard.");
        var guard = PlatformJson.Read<PlatformApiMutationGuard>(json);
        if (guard.Target != request.Target || guard.OperationId != request.OperationId || guard.Scope != EffectScope.AccountService ||
            guard.Address is not null || guard.ExpectedModuleRevision is not null || guard.ExpectedServiceBinding is not null ||
            !NativeAccountDescriptors().Any(item => item.MutatesProject && item.Namespace == request.Namespace && item.Method == request.Method))
            throw new BridgeException("NativeServiceGuardMismatch", "Native account action target, scope and original ID must match its registered guard.");
        return guard;
    }

    private void CheckNativeAccountContext(PlatformApiRequest request, PlatformApiMutationGuard guard)
    {
        CheckTarget(request.Target);
        RequireReviewedState();
        if (nativeAccountUncertain) throw new BridgeException("NativeExecutionUncertain", "A previous native account outcome needs review before further writes.");
        if (pending.Count != 0) throw new BridgeException("PendingMutationRequiresSave", "Review pending project edits before a native account action.");
        var snapshot = Snapshot();
        if (snapshot.Busy || snapshot.Revision != guard.ExpectedProjectRevision || snapshot.CurrentUser != guard.ExpectedUser ||
            string.IsNullOrWhiteSpace(guard.ExpectedProjectRevision))
            throw new BridgeException("ProjectRevisionChanged", "The native account action no longer matches the reviewed project and actor.");
        if (!baselines.TryGetValue(snapshot.Revision, out var issued) || DateTimeOffset.UtcNow - issued > TimeSpan.FromMinutes(30))
            throw new BridgeException("ProjectBaselineExpired", "Read the current project snapshot before the account action.");
        if ((snapshot.HasUnsavedChanges || NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true) &&
            nativeAccountDirtyRevision != snapshot.Revision)
            throw new BridgeException("UnsavedChanges", "Review existing Designer edits before the account action.");
    }

    private static NativeServiceApiReceipt UncertainAccountReceipt(string operationId) => new(operationId, "verification_failed", "NativeServiceOutcomeUnconfirmed",
        "The native account outcome could not be confirmed; retain its original ID and do not replay.", null, false, null, false, false, true, NativeV11UserManagement.Limitation);
}
