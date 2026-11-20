using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

var target = new ProjectTarget("fixture-instance", "fixture-document", Path.GetFullPath("fixture.fgcc"), "12.0.102.0");
var fixture = new Adapter(target);
var coordinator = new OperationCoordinator(null!); // The platform route has no legacy backend calls.
var router = coordinator.CreatePlatformRouter(fixture, target);
var first = Request();
var result = await Run(first);
Require(result.State == "failed" && result.Code == "CheckoutOwnerChanged" && !result.ExecutionMayContinue,
    "verified checkout followed by a definite pre-command refusal has a terminal failure receipt");
Require(fixture.CommandWrites == 0 && !coordinator.IsPlatformExecutionUncertain(target.SessionId),
    "settled checkout is not uncertainty for the command or session");
fixture.ProjectRevision = "project-fresh";
var second = await Run(Request());
Require(second.State == "loaded_pending_save" && fixture.CommandWrites == 1 &&
    router.GetOperation(first.OperationId).Code == "CheckoutOwnerChanged", "fresh request dispatches without overwriting original receipt");

fixture.ThrowUnclassified = true;
var third = await Run(Request());
Require(third.State == "verification_failed" && third.ExecutionMayContinue &&
    coordinator.IsPlatformExecutionUncertain(target.SessionId), "same code without settled typed evidence quarantines");
try
{
    _ = await Run(Request());
    throw new Exception("Uncertain session admitted a new write.");
}
catch (BridgeException ex) when (ex.Code == "NativeExecutionUncertain") { }
Console.WriteLine("V12 module.update settled and uncertain checkout receipt routing passed (simulated adapter).");

await VerifyPagePreflightBoundary();
await VerifySaveBoundary(2);
await VerifySaveBoundary(3);

async Task VerifyPagePreflightBoundary()
{
    var pageTarget = target with { SessionId = "cell-preflight-fixture" };
    var adapter = new Adapter(pageTarget) { SaveMode = 1 };
    var pages = new OperationCoordinator(null!);
    var pageRouter = pages.CreatePlatformRouter(adapter, pageTarget);
    async Task<PlatformOperationStatus> Await(string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var status = pageRouter.GetOperation(id);
            if (status.State is not ("queued" or "running")) return status;
            await Task.Delay(10, timeout.Token);
        }
    }
    async Task<PlatformOperationStatus> Update(bool invalid, string? ownerFailure = null)
    {
        _ = await pageRouter.InvokeAsync("project", "snapshot", PlatformJson.Element(new { }));
        var id = Guid.NewGuid().ToString("D");
        var guard = new PlatformApiMutationGuard(id, pageTarget, adapter.ProjectRevision, adapter.User);
        _ = await pageRouter.InvokeAsync("page", "updateCells", PlatformJson.Element(new { guard, invalid, ownerFailure }));
        return await Await(id);
    }
    var prior = await Update(false);
    Require(prior.State == "loaded_pending_save" && adapter.PendingPageEdits == 1, "previous native edit is in the pending journal");
    var rejected = await Update(true);
    Require(rejected is { State: "failed", Code: "InvalidCommandTree", ExecutionMayContinue: false } &&
        adapter.PendingPageEdits == 1 && !pages.IsPlatformExecutionUncertain(pageTarget.SessionId) &&
        pageRouter.GetOperation(prior.OperationId).State == "loaded_pending_save",
        "settled page preflight rejection preserves the previous pending receipt and write eligibility");
    var ownerRefused = await Update(false, "before");
    Require(ownerRefused is { State: "failed", Code: "NativeCollaborationModuleUnavailable", ExecutionMayContinue: false } &&
        adapter.PendingPageEdits == 1 && !pages.IsPlatformExecutionUncertain(pageTarget.SessionId) &&
        pageRouter.GetOperation(prior.OperationId).State == "loaded_pending_save",
        "read-only lifecycle owner mapping denial does not quarantine the prior journal");
    var reserved = await Update(false, "reservation");
    Require(reserved is { State: "failed", Code: "NativePageCreationReservationConflict", ExecutionMayContinue: false } &&
        adapter.PendingPageEdits == 1 && !pages.IsPlatformExecutionUncertain(pageTarget.SessionId),
        "unsupported fresh page reservation stops before the dispatch callback and preserves the earlier journal");
    var save = new ProjectSaveRequest(Guid.NewGuid().ToString("D"), pageTarget, adapter.ProjectRevision);
    _ = await pageRouter.InvokeAsync("project", "snapshot", PlatformJson.Element(new { }));
    _ = await pageRouter.InvokeAsync("module", "save", PlatformJson.Element(save));
    Require((await Await(save.OperationId)).State == "succeeded" && adapter.SavedPageEdits == 1,
        "an explicit save can still persist the prior edit after the page rejection");
    var ownerAmbiguous = await Update(false, "after");
    Require(ownerAmbiguous is { State: "verification_failed", Code: "NativeCollaborationModuleUnavailable", ExecutionMayContinue: true } &&
        pages.IsPlatformExecutionUncertain(pageTarget.SessionId),
        "same lifecycle owner error after possible native checkout remains quarantined");
    Console.WriteLine("PASS lifecycle owner mapping before-dispatch and post-dispatch uncertainty (simulated adapter).");
    Console.WriteLine("PASS settled native page API denial preserves prior pending-save eligibility (simulated adapter).");
}

async Task VerifySaveBoundary(int uncertainMode)
{
    var saveTarget = target with { SessionId = "save-fixture" };
    var saveFixture = new Adapter(saveTarget);
    var saveCoordinator = new OperationCoordinator(null!);
    var saveRouter = saveCoordinator.CreatePlatformRouter(saveFixture, saveTarget);
    ProjectSaveRequest NewSave() => new(Guid.NewGuid().ToString("D"), saveTarget, saveFixture.ProjectRevision);
    async Task<PlatformOperationStatus> SubmitSave(ProjectSaveRequest request)
    {
        _ = await saveRouter.InvokeAsync("project", "snapshot", PlatformJson.Element(new { }));
        _ = await saveRouter.InvokeAsync("module", "save", PlatformJson.Element(request));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var receipt = saveRouter.GetOperation(request.OperationId);
            if (receipt.State is not ("queued" or "running") &&
                (saveFixture.SaveMode < 2 || saveCoordinator.IsPlatformExecutionUncertain(saveTarget.SessionId))) return receipt;
            await Task.Delay(10, timeout.Token);
        }
    }

    var original = NewSave();
    var refused = await SubmitSave(original);
    Require(refused.State == "failed" && !refused.ExecutionMayContinue &&
        saveFixture.NativeSaves == 0 && !saveCoordinator.IsPlatformExecutionUncertain(saveTarget.SessionId),
        "pre-save read-only refusal does not quarantine");
    saveFixture.SaveMode = 1;
    var successful = await SubmitSave(NewSave());
    Require(successful.State == "succeeded" && saveFixture.NativeSaves == 1 &&
        saveRouter.GetOperation(original.OperationId).Code == "ApplicationOwnerFileChanged",
        "fresh explicit save remains eligible without replaying the original ID");
    saveFixture.SaveMode = uncertainMode;
    var unconfirmed = await SubmitSave(NewSave());
    if (uncertainMode == 2) Require(unconfirmed is { State: "verification_failed", Code: "SaveUnconfirmed", ExecutionMayContinue: false } &&
            unconfirmed.Result?.GetProperty("saveResult").GetProperty("verificationFailure").GetProperty("fileRole").GetString() == "ApplicationSettings.json" &&
            saveCoordinator.IsPlatformExecutionUncertain(saveTarget.SessionId),
        "native call can terminate with unconfirmed persistence, retaining evidence and isolating later writes");
    else Require(unconfirmed is { State: "verification_failed", Code: "ApplicationOwnerFileChanged", ExecutionMayContinue: true } &&
                saveCoordinator.IsPlatformExecutionUncertain(saveTarget.SessionId),
        "the same code after registration/dispatch is not classified as a safe refusal");
    try
    {
        _ = await SubmitSave(NewSave());
        throw new InvalidOperationException("Unconfirmed save admitted a fresh write.");
    }
    catch (BridgeException ex) when (ex.Code == "NativeExecutionUncertain") { }
    Console.WriteLine("PASS explicit save boundary and terminal unverified persistence receipts (simulated adapter).");
}

ModuleMutationRequest Request() => new(Guid.NewGuid().ToString("D"), target, ModuleOperation.Update,
    fixture.Document.Address, fixture.ProjectRevision, fixture.Document.Revision, fixture.Document, Save: false, ExpectedUser: fixture.User);
async Task<PlatformOperationStatus> Run(ModuleMutationRequest request)
{
    _ = await router.InvokeAsync("project", "snapshot", PlatformJson.Element(new { }));
    _ = await router.InvokeAsync("module", "update", PlatformJson.Element(request));
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        var status = router.GetOperation(request.OperationId);
        if (status.State is not ("queued" or "running") && (!fixture.ThrowUnclassified || coordinator.IsPlatformExecutionUncertain(target.SessionId))) return status;
        await Task.Delay(10, timeout.Token);
    }
}
static void Require(bool value, string label) { if (!value) throw new Exception(label); }

sealed class Adapter(ProjectTarget target) : IProjectAdapter, IProjectSaveDispatchAdapter, IProjectApiDispatchAdapter
{
    public UserIdentity User { get; } = new("test-user", "test-user@example.invalid");
    public ModuleDocument Document { get; } = new(new(ModuleKind.ServerCommand, "DemoCommand"),
        PlatformJson.Element(new { commands = new[] { new { type = "Condition", branch = "Then" } } }), [], "module-1");
    public int CommandWrites { get; private set; }
    public int PendingPageEdits { get; private set; }
    public int SavedPageEdits { get; private set; }
    public int SaveMode { get; set; }
    public int NativeSaves { get; private set; }
    public Task<ProjectSaveResult> SaveWithDispatchAsync(ProjectSaveRequest request, Action startingNativeWrite, CancellationToken token = default)
    {
        if (SaveMode == 0) throw new BridgeException("ApplicationOwnerFileChanged", "Stage=beforeNativeSave; fileRole=favicon.ico.");
        startingNativeWrite();
        NativeSaves++;
        if (SaveMode == 1) { SavedPageEdits += PendingPageEdits; PendingPageEdits = 0; }
        if (SaveMode == 3) throw new BridgeException("ApplicationOwnerFileChanged", "Simulated exception after first possible native effect.");
        return Task.FromResult(SaveMode == 1 ? new ProjectSaveResult(true, false, true) :
            new ProjectSaveResult(true, false, false) { VerificationFailure = new("afterNativeSave", "ApplicationSettings.json", "SavedProjectionMismatch") });
    }
    public bool ThrowUnclassified { get; set; }
    public string ProjectRevision { get; set; } = "project-original";
    private bool refused;
    public AdapterIdentity Identity => new(target.DesignerVersion, "fixture", "fixture");
    public Task<ProjectSnapshot> GetSnapshotAsync(CancellationToken token = default) =>
        Task.FromResult(new ProjectSnapshot(target, PendingPageEdits > 0, false, true, User, ProjectRevision));
    public Task<IReadOnlyList<ModuleCapability>> GetCapabilitiesAsync(CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<ModuleCapability>>([new(ModuleKind.ServerCommand, ModuleOperation.Update, true, "fixture")]);
    public Task<IReadOnlyList<PlatformApiDescriptor>> GetApiCatalogAsync(CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<PlatformApiDescriptor>>([new("page", "updateCells", PlatformJson.Element(new { }), null, true)]);
    public Task<ModulePage> ListAsync(ProjectTarget actual, ModuleQuery query, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ModuleDocument> ReadAsync(ProjectTarget actual, ModuleAddress address, CancellationToken token = default) => Task.FromResult(Document);
    public Task<ReferenceResult> FindReferencesAsync(ProjectTarget actual, ModuleAddress address, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ModuleValidationResult> ValidateAsync(ModuleMutationRequest request, CancellationToken token = default) =>
        Task.FromResult(new ModuleValidationResult(true, []));
    public Task<ModuleMutationReceipt> ApplyAsync(ModuleMutationRequest request, CancellationToken token = default)
    {
        if (ThrowUnclassified) throw new BridgeException("CheckoutOwnerChanged", "Unclassified native fault.");
        if (!refused)
        {
            refused = true;
            throw new CheckoutBaselineException(sourceChanged: false, "ServerCommands/DemoCommand");
        }
        CommandWrites++;
        return Task.FromResult(new ModuleMutationReceipt(request.OperationId, "LoadedPendingSave", "NativeMemoryReadback", "fixture"));
    }
    public Task<ProjectSaveResult> SaveAsync(ProjectSaveRequest request, CancellationToken token = default) => throw new NotSupportedException();
    public Task ValidateProjectApiAsync(PlatformApiRequest request, CancellationToken token = default)
    {
        if (request.Parameters.GetProperty("invalid").GetBoolean())
            throw new BridgeException("InvalidCommandTree", "Exact simulated native column denial before dispatch.");
        return Task.CompletedTask;
    }

    public Task<JsonElement> InvokeProjectApiAsync(PlatformApiRequest request, Action startingNativeWrite, CancellationToken token = default)
    {
        var ownerFailure = request.Parameters.GetProperty("ownerFailure");
        if (ownerFailure.ValueKind == JsonValueKind.String && ownerFailure.GetString() == "reservation")
            throw new BridgeException("NativePageCreationReservationConflict", "Simulated safe refusal of the unsupported New reservation.");
        if (ownerFailure.ValueKind == JsonValueKind.String && ownerFailure.GetString() == "before")
            throw new BridgeException("NativeCollaborationModuleUnavailable", "Simulated read-only new owner mapping denial.");
        startingNativeWrite();
        if (ownerFailure.ValueKind == JsonValueKind.String && ownerFailure.GetString() == "after")
            throw new BridgeException("NativeCollaborationModuleUnavailable", "Simulated subsequent owner error after possible checkout.");
        PendingPageEdits++;
        ProjectRevision = "project-dirty";
        return Task.FromResult(PlatformJson.Element(new { }));
    }

    public Task<JsonElement> InvokeAsync(PlatformApiRequest request, CancellationToken token = default) => throw new NotSupportedException();
}
