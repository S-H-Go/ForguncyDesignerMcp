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

sealed class Adapter(ProjectTarget target) : IProjectAdapter
{
    public UserIdentity User { get; } = new("test-user", "test-user@example.invalid");
    public ModuleDocument Document { get; } = new(new(ModuleKind.ServerCommand, "DemoCommand"),
        PlatformJson.Element(new { commands = new[] { new { type = "Condition", branch = "Then" } } }), [], "module-1");
    public int CommandWrites { get; private set; }
    public bool ThrowUnclassified { get; set; }
    public string ProjectRevision { get; set; } = "project-original";
    private bool refused;
    public AdapterIdentity Identity => new(target.DesignerVersion, "fixture", "fixture");
    public Task<ProjectSnapshot> GetSnapshotAsync(CancellationToken token = default) =>
        Task.FromResult(new ProjectSnapshot(target, false, false, true, User, ProjectRevision));
    public Task<IReadOnlyList<ModuleCapability>> GetCapabilitiesAsync(CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<ModuleCapability>>([new(ModuleKind.ServerCommand, ModuleOperation.Update, true, "fixture")]);
    public Task<IReadOnlyList<PlatformApiDescriptor>> GetApiCatalogAsync(CancellationToken token = default) =>
        Task.FromResult<IReadOnlyList<PlatformApiDescriptor>>([]);
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
    public Task<JsonElement> InvokeAsync(PlatformApiRequest request, CancellationToken token = default) => throw new NotSupportedException();
}
