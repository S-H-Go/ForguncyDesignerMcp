using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using ForguncyDesignerMcp.PlatformRuntime;

foreach (var version in new[] { "11.0.3.0", "11.0.104.0", "12.0.102.0" })
{
    var target = new ProjectTarget("test-instance", "test-document", Path.GetFullPath("fixture.fgcc"), version);
    var adapter = new PreviewFixtureAdapter(target);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
    var count = 0;
    var router = new PlatformProjectRouter(adapter, target, startDevelopmentPreview: (_, mark) =>
    {
        Interlocked.Increment(ref count);
        mark();
        started.TrySetResult();
        return completion.Task;
    });
    var description = await router.DescribeAsync(target);
    var descriptor = description.Runtime.Apis.Single(a => a.Namespace == "preview" && a.Method == "start");
    Require(descriptor.MutatesProject && descriptor.Scope == EffectScope.DevelopmentPreview, "effect catalog");
    var runtime = new PlatformToolRuntime();
    var id = Guid.NewGuid().ToString("D");
    var request = new DevelopmentPreviewRequest(id, target, "rev-1");
    var arguments = PlatformJson.Element(new { @namespace = "preview", method = "start", parameters = request });
    var initial = await runtime.CallToolAsync("invoke_api", arguments, description.Runtime, router);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
    Require(initial.GetProperty("structuredContent").GetProperty("operationId").GetString() == id, "MCP returns matching receipt");
    Require(router.GetOperation(id).State == "running", "starts asynchronously");
    _ = await router.InvokeAsync("preview", "start", PlatformJson.Element(request));
    completion.SetResult("http://127.0.0.1:31587/Forguncy");
    var receipt = await WaitAsync(router, id);
    Require(receipt.State == "succeeded" && receipt.Result!.Value.GetProperty("url").GetString() ==
        "http://127.0.0.1:31587/Forguncy" && count == 1, "wait and deduplicate native result");

    adapter.State = adapter.State with { HasUnsavedChanges = true };
    await RejectAsync("PreviewRequiresCleanDocument", "rev-1");
    adapter.State = adapter.State with { HasUnsavedChanges = false, Busy = true };
    await RejectAsync("DesignerBusy", "rev-1");
    adapter.State = adapter.State with { Busy = false, Revision = "rev-2" };
    await RejectAsync("ProjectRevisionMismatch", "rev-1");
    Require(count == 1, "failed preconditions do not dispatch");
    var wrongTarget = request with { OperationId = Guid.NewGuid().ToString("D"), Target = target with { SessionId = "other-document" } };
    try
    {
        _ = await router.InvokeAsync("preview", "start", PlatformJson.Element(wrongTarget));
        throw new Exception("A different document target was accepted.");
    }
    catch (BridgeException e) when (e.Code == "TargetMismatch") { }
    Require(count == 1, "wrong document never dispatches");

    var operationLookup = await runtime.CallToolAsync("invoke_api", PlatformJson.Element(new
    {
        @namespace = "project", method = "get_operation", parameters = new { operationId = id }
    }), description.Runtime, router);
    Require(operationLookup.GetProperty("structuredContent").GetProperty("result").GetProperty("url").GetString() ==
        "http://127.0.0.1:31587/Forguncy", "retrieve the actual URL through the original MCP operation");

    var failed = await FinishResultAsync("https://example.invalid/");
    Require(failed.State == "verification_failed" && failed.Code == "PreviewUrlUnconfirmed" &&
        failed.ExecutionMayContinue && failed.Result is null, "external URL is never returned");
    failed = await FinishResultAsync("http://localhost:31587/Forguncy?token=fixture");
    Require(failed.State == "verification_failed" && failed.Code == "PreviewUrlUnconfirmed" &&
        failed.Result is null, "token-bearing URL is never returned");
    failed = await FinishResultAsync(null);
    Require(failed.State == "failed" && failed.Code == "NativePreviewRejected" &&
        !failed.ExecutionMayContinue, "native false is not success");

    var unsupported = new PlatformProjectRouter(adapter, target);
    Require(!(await unsupported.DescribeAsync(target)).Runtime.Apis.Any(a => a.Namespace == "preview"),
        "unregistered build does not advertise preview");
    Console.WriteLine($"{version}: catalog, MCP receipt, URL, deduplication and pre-dispatch guards passed (simulated native host).");

    async Task RejectAsync(string code, string revision)
    {
        var failureId = Guid.NewGuid().ToString("D");
        _ = await router.InvokeAsync("preview", "start", PlatformJson.Element(new DevelopmentPreviewRequest(failureId, target, revision)));
        var result = await WaitAsync(router, failureId);
        Require(result.State == "failed" && result.Code == code && !result.ExecutionMayContinue, code);
    }

    async Task<PlatformOperationStatus> FinishResultAsync(string? url)
    {
        var isolated = new PlatformProjectRouter(adapter, target, startDevelopmentPreview: (_, mark) =>
        {
            mark();
            return Task.FromResult(url);
        });
        var r = new DevelopmentPreviewRequest(Guid.NewGuid().ToString("D"), target, "rev-2");
        _ = await isolated.InvokeAsync("preview", "start", PlatformJson.Element(r));
        return await WaitAsync(isolated, r.OperationId);
    }
}

static void Require(bool condition, string label)
{
    if (!condition) throw new Exception("Failed: " + label);
}

static async Task<PlatformOperationStatus> WaitAsync(PlatformProjectRouter router, string id)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
    while (true)
    {
        var receipt = router.GetOperation(id);
        if (receipt.State is not ("queued" or "running")) return receipt;
        await Task.Delay(10, timeout.Token);
    }
}

sealed class PreviewFixtureAdapter(ProjectTarget target) : IProjectAdapter
{
    public ProjectSnapshot State { get; set; } = new(target, false, false, false, null, "rev-1");
    public AdapterIdentity Identity => new(State.Target.DesignerVersion, "fixture", "fixture");
    public Task<ProjectSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);
    public Task<IReadOnlyList<ModuleCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ModuleCapability>>([]);
    public Task<IReadOnlyList<PlatformApiDescriptor>> GetApiCatalogAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<PlatformApiDescriptor>>([]);
    public Task<ModulePage> ListAsync(ProjectTarget actual, ModuleQuery query, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ModuleDocument> ReadAsync(ProjectTarget actual, ModuleAddress address, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ReferenceResult> FindReferencesAsync(ProjectTarget actual, ModuleAddress address, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ModuleValidationResult> ValidateAsync(ModuleMutationRequest request, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ModuleMutationReceipt> ApplyAsync(ModuleMutationRequest request, CancellationToken token = default) => throw new NotSupportedException();
    public Task<ProjectSaveResult> SaveAsync(ProjectSaveRequest request, CancellationToken token = default) => throw new NotSupportedException();
    public Task<JsonElement> InvokeAsync(PlatformApiRequest request, CancellationToken token = default) => throw new NotSupportedException();
}
