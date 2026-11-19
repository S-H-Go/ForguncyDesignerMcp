using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    // This is the last independently verified service state which still needs
    // project export. A newly echoed external revision cannot replace it.
    private AccountServiceBinding? pendingServiceExport;

    private static bool IsAccountModule(ModuleKind kind) => kind is ModuleKind.User or ModuleKind.Role or ModuleKind.Organization;
    private static bool ValidBinding(AccountServiceBinding? binding) => binding is not null &&
        !string.IsNullOrWhiteSpace(binding.Identity) && !string.IsNullOrWhiteSpace(binding.Revision);
    private static void ValidateRequiredServiceBinding(AccountServiceBinding? binding)
    {
        if (!ValidBinding(binding)) throw new BridgeException("ServiceBindingRequired", "A verified account service identity and authoritative data revision are required.");
    }
    private static void ValidateOptionalServiceBinding(AccountServiceBinding? binding)
    {
        if (binding is not null) ValidateRequiredServiceBinding(binding);
    }

    private static void RequireCurrentServiceBinding(ProjectSnapshot snapshot, AccountServiceBinding? expected)
    {
        ValidateRequiredServiceBinding(expected);
        if (!ValidBinding(snapshot.AccountService)) throw new BridgeException("ServiceBindingUnavailable", "The effective account service has not been verified; no service is started by this check.");
        if (snapshot.AccountService != expected) throw new BridgeException("ServiceBindingChanged", "Account service generation or authoritative data revision differs from the reviewed binding.");
    }

    private void CheckAccountMutationBinding(ProjectSnapshot snapshot, AccountServiceBinding? expected)
    {
        RequireCurrentServiceBinding(snapshot, expected);
        if (pendingServiceExport is not null && expected != pendingServiceExport)
            throw new BridgeException("ServiceExportBaselineChanged", "An unexported service outcome was verified at another revision. Reconcile it before new service mutations.");
    }

    private void CheckExportServiceBinding(ProjectSnapshot snapshot, AccountServiceBinding? expected)
    {
        if (expected is null && pendingServiceExport is null) return;
        RequireCurrentServiceBinding(snapshot, expected);
        if (pendingServiceExport is not null && expected != pendingServiceExport)
            throw new BridgeException("ServiceExportBaselineChanged", "Project export must retain the service generation and reviewed after revision of the committed account changes.");
    }

    private bool VerifyExportBindingAfter(ProjectSnapshot snapshot, AccountServiceBinding? expected) =>
        expected is null && pendingServiceExport is null ||
        ValidBinding(expected) && snapshot.AccountService == expected && (pendingServiceExport is null || pendingServiceExport == expected);

    private async Task<JsonElement> FinishServiceMutationAsync(ModuleMutationRequest request, ModuleMutationReceipt receipt)
    {
        ProjectSnapshot? after = null;
        try { after = await SnapshotAsync(request.Target, default); }
        catch (Exception)
        {
            // Keep the original typed service evidence even when a document
            // switch or failed snapshot prevents host verification after CUD.
        }
        var service = receipt.ServiceResult;
        if (service is { NativeAccepted: false, PersistenceVerified: false } &&
            service.Binding == request.ExpectedServiceBinding && after?.AccountService == request.ExpectedServiceBinding &&
            !receipt.ExecutionMayContinue)
            return PlatformJson.Element(receipt with { State = "failed", Message = "Native account operation was explicitly rejected; no committed service change was verified." });

        var expectedPresence = request.Operation != ModuleOperation.Delete;
        var entityMatches = service is not null && service.Entity is not null &&
            service.Entity.Kind == request.Address.Kind && !string.IsNullOrWhiteSpace(service.Entity.Name) &&
            (request.Operation == ModuleOperation.Create || service.Entity == request.Address) &&
            (receipt.Document is null || receipt.Document.Address == service.Entity);
        var verified = service is { NativeAccepted: true, PersistenceVerified: true, ReadbackVerified: true } &&
            service.EntityPresent == expectedPresence && entityMatches &&
            service.Binding == request.ExpectedServiceBinding && !string.IsNullOrWhiteSpace(service.ObservedRevision) &&
            after?.AccountService is { } actual && actual.Identity == service.Binding.Identity && actual.Revision == service.ObservedRevision;
        if (!verified)
        {
            // Unknown outcome can remain after the Task ended. It is distinct
            // from ExecutionMayContinue and still quarantines new native writes.
            executionUncertain = true;
            return PlatformJson.Element(receipt with { State = "verification_failed", Code = "ServiceOutcomeUnconfirmed",
                Message = "Account service acceptance, persistence, entity read-back or binding is unconfirmed. Keep the original operationId; do not replay the service action." });
        }

        pendingServiceExport = new(service!.Binding.Identity, service.ObservedRevision!);
        ownedDirtyRevision = after!.HasUnsavedChanges ? after.Revision : null;
        if (!request.Save)
            return PlatformJson.Element(receipt with { State = "persisted_verified", Code = "ServicePersistedAndVerified",
                Message = "Account service transaction and authoritative read-back are verified. Project export was not requested." });

        var exported = receipt.SaveResult is { NativeSaveAccepted: true, ContainsUnsavedChanges: false, SourceVerified: true } && !after.HasUnsavedChanges;
        if (!exported)
            return PlatformJson.Element(receipt with { State = "verification_failed", Code = "ServicePersistedProjectExportFailed",
                Message = "Account service persistence remains verified, but requested project export is unconfirmed. Retry only a separately guarded project save, never the account CUD." });

        pendingServiceExport = null;
        ownedDirtyRevision = null;
        return PlatformJson.Element(receipt with { State = "persisted_verified", Code = "ServicePersistedAndExported",
            Message = "Account service persistence/read-back and native project export/source read-back are verified separately." });
    }
}
