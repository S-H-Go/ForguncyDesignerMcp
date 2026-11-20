using System.Collections.Concurrent;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

/// <summary>One instance per document, shared by every transport/client.</summary>
public sealed partial class PlatformProjectRouter : IPlatformApiInvoker
{
    private readonly IProjectAdapter adapter;
    private readonly SemaphoreSlim gate;
    private readonly ConcurrentDictionary<string, Entry> operations = new(StringComparer.Ordinal);
    private readonly object registration = new();
    private readonly PlatformCatalogQueries catalogQueries = new();
    private IReadOnlyList<PlatformApiDescriptor>? fixedSignatures;
    private readonly Action<string, string>? reserveId;
    private readonly Action<PlatformOperationStatus>? publishStatus;
    private readonly Func<bool>? isSessionExecutionUncertain;
    private volatile bool invalidated;
    private volatile bool executionUncertain;
    private string? ownedDirtyRevision;
    private bool HasUncertainExecution => executionUncertain || isSessionExecutionUncertain?.Invoke() == true;
    public ProjectTarget Target { get; }

    private sealed class Entry(string fingerprint, PlatformOperationStatus status)
    {
        internal readonly object Sync = new();
        internal readonly CancellationTokenSource Waiting = new();
        internal readonly string Fingerprint = fingerprint;
        internal PlatformOperationStatus Status = status;
    }

    private sealed class ReceiptCorrelationException(string expectedId, ModuleMutationReceipt reported) : Exception("Adapter changed the original operationId.")
    {
        internal string ExpectedId { get; } = expectedId;
        internal ModuleMutationReceipt Reported { get; } = reported;
    }

    public PlatformProjectRouter(IProjectAdapter adapter, ProjectTarget target, SemaphoreSlim? sharedGate = null,
        Action<string, string>? reserveOperationId = null, Action<PlatformOperationStatus>? publishOperationStatus = null,
        Func<bool>? isSessionExecutionUncertain = null, StartNativeDevelopmentPreview? startDevelopmentPreview = null)
    {
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        ValidateTarget(target);
        Target = target;
        gate = sharedGate ?? new(1, 1);
        reserveId = reserveOperationId;
        publishStatus = publishOperationStatus;
        this.isSessionExecutionUncertain = isSessionExecutionUncertain;
        this.startDevelopmentPreview = startDevelopmentPreview;
        if (adapter.Identity.DesignerVersion != target.DesignerVersion)
            throw new BridgeException("AdapterVersionMismatch", "Adapter and target must identify the exact same Designer build.");
    }

    public Task<JsonElement> InvokeAsync(string @namespace, string method, JsonElement parameters,
        CancellationToken cancellationToken = default) =>
        InvokeAsync(new PlatformApiRequest(Target, @namespace, method, parameters), cancellationToken);

    public async Task<PlatformDescription> DescribeAsync(ProjectTarget target, CancellationToken token = default) =>
        await SerializedAsync(async () =>
        {
            var snapshot = await SnapshotAsync(target, token);
            var capabilities = await adapter.GetCapabilitiesAsync(token);
            var apis = await CatalogAsync(token, capabilities);
            var after = await SnapshotAsync(target, token);
            if (after.Revision != snapshot.Revision) throw new BridgeException("ContextChangedDuringRead", "Project changed while discovering platform capabilities.");
            return new PlatformDescription(snapshot, new(Target, apis, capabilities));
        }, token);

    public async Task<JsonElement> InvokeAsync(PlatformApiRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckTarget(request.Target, request.Namespace == "project" && request.Method is "get_operation" or "cancel_operation");
        if (request.Parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidArguments", "API parameters must be an object.");
        if (request.Namespace == "project" && request.Method == "get_operation")
            return PlatformJson.Element(GetOperation(RequiredString(request.Parameters, "operationId")));
        if (request.Namespace == "project" && request.Method == "cancel_operation")
            return PlatformJson.Element(Cancel(new(request.Target, RequiredString(request.Parameters, "operationId"))));
        // The snapshot itself is the complete read. Capture it after acquiring the
        // document gate so a queued reader sees the latest revision and target.
        if (request.Namespace == "project" && request.Method == "snapshot")
            return await SerializedAsync(async () =>
                PlatformJson.Element(await SnapshotAsync(request.Target, cancellationToken)), cancellationToken);
        if (request.Namespace == "preview" && request.Method == "start") return SubmitDevelopmentPreview(request);
        if (request.Namespace == "plugin") return await InvokePluginAsync(request, cancellationToken);
        if (request.Namespace == "collaboration") return await InvokeCollaborationAsync(request, cancellationToken);
        if (request.Namespace == "module" && request.Method is "create" or "update" or "delete")
        {
            var mutation = PlatformJson.Read<ModuleMutationRequest>(request.Parameters);
            var operation = Enum.Parse<ModuleOperation>(request.Method, true);
            if (mutation.Operation != operation) throw new BridgeException("OperationMismatch", "Method and mutation operation differ.");
            CheckTarget(mutation.Target);
            CheckOperationId(request.OperationId, mutation.OperationId);
            ValidateMutationShape(mutation);
            return Submit(request, mutation.OperationId, mutation.Scope, started => ApplyAsync(mutation, started));
        }
        if (request.Namespace == "module" && request.Method == "save")
        {
            var save = PlatformJson.Read<ProjectSaveRequest>(request.Parameters);
            CheckTarget(save.Target);
            CheckOperationId(request.OperationId, save.OperationId);
            ValidateOptionalServiceBinding(save.ExpectedServiceBinding);
            if (string.IsNullOrWhiteSpace(save.ExpectedProjectRevision)) throw new BridgeException("BaselineRequired", "Project revision is required.");
            return Submit(request, save.OperationId, EffectScope.ProjectDefinition, started => SaveAsync(save, started));
        }
        // Registration is explicit. Source text is never inspected to classify writes.
        return await SerializedAsync(async () =>
        {
            var before = await SnapshotAsync(request.Target, cancellationToken, issueBaseline: false);
            var catalog = await CatalogAsync(cancellationToken);
            var descriptor = catalog.SingleOrDefault(x => x.Namespace == request.Namespace && x.Method == request.Method)
                ?? throw new BridgeException("UnknownApi", "The API namespace/method is not registered for this adapter.");
            if (descriptor.MutatesProject)
            {
                var guard = ReadApiGuard(request, descriptor);
                if (descriptor.Scope == EffectScope.AccountService)
                {
                    if (adapter is not IProjectNativeServiceApiAdapter native)
                        throw new BridgeException("AccountServiceMutationUnavailable", "This adapter does not provide the separate native service response contract.");
                    if (guard.ExpectedServiceBinding is not null || guard.Address is not null || guard.ExpectedModuleRevision is not null)
                        throw new BridgeException("NativeServiceGuardNotSupported", "Service binding and module revision guards require the stronger typed account mutation path.");
                    return Submit(request, guard.OperationId, descriptor.Scope,
                        started => InvokeNativeServiceAsync(native, request, guard, started));
                }
                if(descriptor.Scope==EffectScope.RuntimeDeployment)
                {
                    if(adapter is not IProjectRuntimeApiAdapter runtime)
                        throw new BridgeException("RuntimeApiUnavailable","This adapter has no separate mixed runtime lifecycle implementation.");
                    return Submit(request,guard.OperationId,descriptor.Scope,started=>InvokeRuntimeApiAsync(runtime,request,guard,started));
                }
                if (await PreparedApiAsync(descriptor, cancellationToken) is { } prepared)
                    return Submit(request, guard.OperationId, descriptor.Scope,
                        started => InvokeModuleApiAsync(prepared, request, guard, started));
                var memoryOnly = false;
                return Submit(request, guard.OperationId, descriptor.Scope, async started =>
                {
                    await CheckNativeApiAsync(request, guard, default);
                    var guardedRequest = request with { OperationId = guard.OperationId };
                    JsonElement result;
                    if (adapter is IProjectApiDispatchAdapter dispatcher)
                        result = await dispatcher.InvokeProjectApiAsync(guardedRequest, started, default);
                    else
                    {
                        started();
                        result = await adapter.InvokeAsync(guardedRequest, default);
                    }
                    var after = await SnapshotAsync(request.Target, default);
                    memoryOnly = after.HasUnsavedChanges;
                    ownedDirtyRevision = memoryOnly ? after.Revision : null;
                    return result;
                }, receiptExpected: false, pendingSave: () => memoryOnly);
            }
            var result = await ReadAsync(request, cancellationToken, catalog);
            var after = await SnapshotAsync(request.Target, cancellationToken, issueBaseline: false);
            if (after.Revision != before.Revision) throw new BridgeException("ContextChangedDuringRead", "Project changed while reading native model.");
            return result;
        }, cancellationToken);
    }

    public PlatformOperationStatus GetOperation(string id) => operations.TryGetValue(id, out var entry)
        ? ReadStatus(entry) : throw new BridgeException("UnknownOperation", "No receipt is known. This does not prove an earlier write did not execute.");

    public PlatformCancellationResult Cancel(PlatformCancelRequest request)
    {
        CheckTarget(request.Target, allowInvalidated: true);
        if (!operations.TryGetValue(request.OperationId, out var entry)) return new(false, "unknown", true);
        lock (entry.Sync)
        {
            if (entry.Status.State == "queued")
            {
                entry.Waiting.Cancel();
                entry.Status = entry.Status with { State = "cancelled", Code = "CancelledBeforeStart", Message = "Cancelled before native execution.", ExecutionMayContinue = false };
                publishStatus?.Invoke(entry.Status);
            }
            return new(true, entry.Status.State, entry.Status.ExecutionMayContinue);
        }
    }

    private JsonElement Submit(PlatformApiRequest request, string id, EffectScope scope, Func<Action, Task<JsonElement>> action, bool receiptExpected = true, Func<bool>? pendingSave = null)
    {
        if (!Guid.TryParseExact(id, "D", out _)) throw new BridgeException("InvalidOperationId", "operationId must be a UUID in D format.");
        var fingerprint = Wire.Hash(JsonSerializer.Serialize(request with { OperationId = id }, PlatformJson.Options));
        Entry entry;
        lock (registration)
        {
            if (operations.TryGetValue(id, out var existing))
            {
                if (existing.Fingerprint != fingerprint) throw new BridgeException("OperationIdConflict", "The operationId is already bound to different parameters.");
                return PlatformJson.Element(ReadStatus(existing));
            }
            if (HasUncertainExecution) throw new BridgeException("NativeExecutionUncertain", "A prior native execution is unconfirmed. Query its original receipt; new writes are quarantined for this document session.");
            if (operations.Count >= 4096) throw new BridgeException("ReceiptLimit", "Receipt retention limit reached. Existing IDs remain reserved.");
            reserveId?.Invoke(id, fingerprint);
            entry = new(fingerprint, new(id, "queued", "Queued", "Use the original operationId to query completion.", scope, false));
            operations[id] = entry;
            publishStatus?.Invoke(entry.Status);
        }
        _ = Task.Run(() => RunMutationAsync(entry, action, receiptExpected, pendingSave));
        return PlatformJson.Element(ReadStatus(entry));
    }

    private async Task RunMutationAsync(Entry entry, Func<Action, Task<JsonElement>> action, bool receiptExpected, Func<bool>? pendingSave)
    {
        var acquired = false;
        var started = false;
        try
        {
            await gate.WaitAsync(entry.Waiting.Token);
            acquired = true;
            lock (entry.Sync)
            {
                entry.Waiting.Token.ThrowIfCancellationRequested();
                if (HasUncertainExecution) throw new BridgeException("NativeExecutionUncertain", "An earlier operation left native execution unconfirmed; this queued operation was not dispatched.");
                entry.Status = entry.Status with { State = "running", Code = "Running", ExecutionMayContinue = true };
                publishStatus?.Invoke(entry.Status);
            }
            // Preconditions run inside action. Once dispatched, cancellation never
            // releases the shared queue while native work may still be executing.
            var result = await action(() => started = true);
            var state = "succeeded";
            var code = "Completed";
            var message = "Adapter completed; inspect its verification evidence.";
            if (!receiptExpected && pendingSave?.Invoke() == true)
            {
                state = "loaded_pending_save";
                code = "LoadedPendingSave";
                message = "Native API completed in memory. Source persistence requires explicit module.save verification.";
            }
            if (receiptExpected && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("state", out var value))
            {
                state = value.GetString() ?? "verification_failed";
                if (result.TryGetProperty("code", out var c)) code = c.GetString() ?? code;
                if (result.TryGetProperty("message", out var m)) message = m.GetString() ?? message;
            }
            var mayContinue = receiptExpected && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("executionMayContinue", out var may) && may.ValueKind == JsonValueKind.True;
            if (mayContinue) executionUncertain = true;
            if (state == "verification_failed" && code == "SaveUnconfirmed") executionUncertain = true;
            lock (entry.Sync) entry.Status = entry.Status with { State = state, Code = code, Message = message,
                ExecutionMayContinue = mayContinue, Result = result.Clone() };
        }
        catch (OperationCanceledException) when (!started)
        {
            lock (entry.Sync) entry.Status = entry.Status with { State = "cancelled", Code = "CancelledBeforeStart", Message = "Cancelled before native execution.", ExecutionMayContinue = false };
        }
        catch (ReceiptCorrelationException mismatch)
        {
            // The returned evidence is potentially committed but cannot be
            // attributed to the requested ID. Keep it verbatim as evidence,
            // while the original ledger status remains a quarantined failure.
            executionUncertain = true;
            lock (entry.Sync) entry.Status = entry.Status with
            {
                State = "verification_failed", Code = "ReceiptIdMismatch",
                Message = JsonSerializer.Serialize(new
                {
                    message = "Adapter receipt correlation differs from the original request. Returned evidence is retained without promoting it to verified request success; do not replay the mutation.",
                    expectedOperationId = mismatch.ExpectedId, reportedOperationId = mismatch.Reported.OperationId
                }, PlatformJson.Options),
                ExecutionMayContinue = mismatch.Reported.ExecutionMayContinue,
                Result = PlatformJson.Element(mismatch.Reported)
            };
        }
        catch (CollaborationCorrelationException mismatch)
        {
            executionUncertain = true;
            lock (entry.Sync) entry.Status = entry.Status with { State = "verification_failed", Code = mismatch.Code,
                Message = mismatch.Message, ExecutionMayContinue = mismatch.Receipt.ExecutionMayContinue, Result = PlatformJson.Element(mismatch.Receipt) };
        }
        catch (ReceiveEvidenceException error)
        {
            executionUncertain = true;
            lock (entry.Sync) entry.Status = entry.Status with { State = "verification_failed", Code = error.Code,
                Message = error.Message, ExecutionMayContinue = error.MayContinue, Result = error.Evidence };
        }
        catch (PluginEvidenceException error)
        {
            executionUncertain = true;
            lock (entry.Sync) entry.Status = entry.Status with { State = "verification_failed", Code = error.Code,
                Message = "The native plugin registry outcome is unconfirmed; retain this operation ID and do not replay.",
                ExecutionMayContinue = error.MayContinue, Result = error.Evidence };
        }
        catch (NativeServiceUncertainException error)
        {
            executionUncertain = true;
            lock (entry.Sync) entry.Status = entry.Status with { State = "verification_failed", Code = error.Code,
                Message = "The native service outcome is unconfirmed; retain this operation ID and do not replay.",
                ExecutionMayContinue = error.MayContinue, Result = error.Evidence };
        }
        catch(RuntimeApiEvidenceException error)
        {
            executionUncertain=true;
            lock(entry.Sync) entry.Status=entry.Status with{State="verification_failed",Code=error.Code,
                Message="The mixed runtime/project outcome is unconfirmed; retain this operation ID and do not replay.",
                ExecutionMayContinue=error.MayContinue,Result=error.Evidence};
        }
        catch (CheckoutBaselineException error)
        {
            lock (entry.Sync) entry.Status = entry.Status with { State = "failed", Code = error.Code,
                Message = error.Message, ExecutionMayContinue = false };
        }
        catch (Exception error)
        {
            if (started) executionUncertain = true;
            lock (entry.Sync) entry.Status = entry.Status with { State = started ? "verification_failed" : "failed",
                Code = error is BridgeException b ? b.Code : "AdapterError", Message = error.Message, ExecutionMayContinue = started };
        }
        finally
        {
            lock (entry.Sync) publishStatus?.Invoke(entry.Status);
            if (acquired) gate.Release();
        }
    }

    private async Task<JsonElement> ApplyAsync(ModuleMutationRequest request, Action started)
    {
        await CheckMutationAsync(request, default);
        var validation = await adapter.ValidateAsync(request, default);
        if (!validation.Valid) return PlatformJson.Element(new ModuleMutationReceipt(request.OperationId, "failed", "ValidationFailed",
            JsonSerializer.Serialize(new { message = "Native validation rejected the mutation.", issues = validation.Issues }, PlatformJson.Options)));
        if (await HasExternalDeleteReferencesAsync(request, validation, default))
            return PlatformJson.Element(new ModuleMutationReceipt(request.OperationId, "failed", "ModuleReferenced", "Other modules still reference the delete target."));
        // Async validation/reference inspection must not open a stale-baseline gap.
        await CheckMutationAsync(request, default);
        started();
        var receipt = await adapter.ApplyAsync(request, default);
        if (receipt.OperationId != request.OperationId) throw new ReceiptCorrelationException(request.OperationId, receipt);
        if (request.Scope == EffectScope.AccountService) return await FinishServiceMutationAsync(request, receipt);
        var after = await SnapshotAsync(request.Target, default);
        receipt = receipt with { State = receipt.State switch
        {
            "Saved" or "Succeeded" or "succeeded" => "succeeded",
            "LoadedPendingSave" or "loaded_pending_save" => "loaded_pending_save",
            "Rejected" or "Failed" or "failed" => "failed",
            "Uncertain" or "VerificationFailed" or "verification_failed" => "verification_failed",
            _ => "verification_failed"
        } };
        if (receipt.State == "succeeded" && request.Save &&
            (receipt.SaveResult is not { NativeSaveAccepted: true, ContainsUnsavedChanges: false, SourceVerified: true } || after.HasUnsavedChanges))
            receipt = receipt with { State = "verification_failed", Code = "SaveUnconfirmed", Message = "Native save and source read-back are not both verified." };
        if (receipt.State == "succeeded" && !request.Save)
            receipt = receipt with { State = "loaded_pending_save", Code = "LoadedPendingSave", Message = "Mutation applied to memory; persistence has not been requested." };
        if (request.Save && receipt.State == "succeeded" && !VerifyExportBindingAfter(after, request.ExpectedServiceBinding))
            receipt = receipt with { State = "verification_failed", Code = "ProjectExportServiceBindingChanged", Message = "Project export completed without confirming the reviewed account service generation/revision." };
        if (receipt.State is "succeeded" or "loaded_pending_save") ownedDirtyRevision = after.HasUnsavedChanges ? after.Revision : null;
        if (request.Save && receipt.State == "succeeded") pendingServiceExport = null;
        return PlatformJson.Element(receipt);
    }

    private async Task<JsonElement> SaveAsync(ProjectSaveRequest request, Action started)
    {
        var snapshot = await SnapshotAsync(request.Target, default);
        if (snapshot.Busy) throw new BridgeException("DesignerBusy", "Designer is busy.");
        if (snapshot.Revision != request.ExpectedProjectRevision) throw new BridgeException("BaselineChanged", "Project revision changed before save.");
        CheckExportServiceBinding(snapshot, request.ExpectedServiceBinding);
        ProjectSaveResult result;
        if (adapter is IProjectSaveDispatchAdapter dispatch)
            result = await dispatch.SaveWithDispatchAsync(request, started, default);
        else
        {
            started(); // Legacy adapters have no narrower authoritative boundary.
            result = await adapter.SaveAsync(request, default);
        }
        var after = await SnapshotAsync(request.Target, default);
        var verified = result is { NativeSaveAccepted: true, ContainsUnsavedChanges: false, SourceVerified: true } &&
            !after.HasUnsavedChanges && VerifyExportBindingAfter(after, request.ExpectedServiceBinding);
        if (verified) { ownedDirtyRevision = null; pendingServiceExport = null; }
        return PlatformJson.Element(new ModuleMutationReceipt(request.OperationId,
            verified ? "succeeded" : "verification_failed",
            verified ? "Saved" : "SaveUnconfirmed",
            verified ? "Native save and source verification both passed." :
                "Inspect saveResult on the original operation ID. Verification did not establish persistence; do not replay the save.", SaveResult: result));
    }

    private async Task CheckMutationAsync(ModuleMutationRequest request, CancellationToken token)
    {
        var snapshot = await SnapshotAsync(request.Target, token, issueBaseline: false);
        if (snapshot.Busy) throw new BridgeException("DesignerBusy", "Designer is busy.");
        if (snapshot.HasUnsavedChanges && snapshot.Revision != ownedDirtyRevision) throw new BridgeException("UnsavedChanges", "Existing unreviewed edits must be saved before module mutation.");
        if (snapshot.Revision != request.ExpectedProjectRevision) throw new BridgeException("BaselineChanged", "Project revision changed.");
        if (snapshot.InCollaborationMode && (snapshot.CurrentUser is null || snapshot.CurrentUser != request.ExpectedUser))
            throw new BridgeException("IdentityChanged", "Collaboration identity differs from the reviewed identity.");
        if (request.Scope == EffectScope.AccountService) CheckAccountMutationBinding(snapshot, request.ExpectedServiceBinding);
        else if (request.Save || request.ExpectedServiceBinding is not null) CheckExportServiceBinding(snapshot, request.ExpectedServiceBinding);
        await RequireCapabilityAsync(request.Address.Kind, request.Operation, request.Scope, token);
        if (request.Operation is ModuleOperation.Update or ModuleOperation.Delete)
        {
            var document = await adapter.InspectModuleAsync(request.Target, request.Address, token);
            if (document.Revision != request.ExpectedModuleRevision) throw new BridgeException("ModuleBaselineChanged", "Module memory/source revision changed.");
        }
    }

    private async Task<bool> HasExternalDeleteReferencesAsync(ModuleMutationRequest request, ModuleValidationResult validation, CancellationToken token)
    {
        if (request.Operation != ModuleOperation.Delete) return false;
        var references = validation.References ?? await adapter.FindReferencesAsync(request.Target, request.Address, token);
        return references.References.Any(x => x.Source != request.Address);
    }

    private async Task CheckApiMutationAsync(PlatformApiMutationGuard request, CancellationToken token, bool readModule = true)
    {
        var snapshot = await SnapshotAsync(request.Target, token, issueBaseline: false);
        if (snapshot.Busy) throw new BridgeException("DesignerBusy", "Designer is busy.");
        if (snapshot.Revision != request.ExpectedProjectRevision) throw new BridgeException("BaselineChanged", "Project revision changed before native API dispatch.");
        if (snapshot.HasUnsavedChanges && snapshot.Revision != ownedDirtyRevision)
            throw new BridgeException("UnsavedChanges", "Only this router's unchanged, reviewed pending edits may be continued.");
        if (snapshot.InCollaborationMode && (snapshot.CurrentUser is null || snapshot.CurrentUser != request.ExpectedUser))
            throw new BridgeException("IdentityChanged", "Collaboration identity differs from the reviewed identity.");
        if (request.ExpectedServiceBinding is not null) CheckExportServiceBinding(snapshot, request.ExpectedServiceBinding);
        if (readModule && request.Address is { } address)
        {
            await RequireCapabilityAsync(address.Kind, ModuleOperation.Read, null, token);
            var document = await adapter.InspectModuleAsync(request.Target, address, token);
            if (document.Revision != request.ExpectedModuleRevision) throw new BridgeException("ModuleBaselineChanged", "Native API module revision changed.");
        }
    }

    private async Task<JsonElement> ReadAsync(PlatformApiRequest request, CancellationToken token, IReadOnlyList<PlatformApiDescriptor> catalog)
    {
        var p = request.Parameters;
        if (request.Namespace == "project" && request.Method == "validate_api")
            return await PreviewApiAsync(request, catalog, token);
        if (request.Namespace == "project" && request.Method == "capabilities") return PlatformJson.Element(await adapter.GetCapabilitiesAsync(token));
        if (request.Namespace == "catalog" && request.Method == "list") return catalogQueries.List(catalog, p);
        if (request.Namespace == "catalog" && request.Method == "page") return catalogQueries.Page(catalog, p);
        if (request.Namespace == "page" && request.Method is "listPages" or "getPageInfo" or "getCells" &&
            catalog.Any(x=>x.Namespace=="page" && x.Method==request.Method && x.Documentation==PlatformV11PageReads.Descriptor(request.Method).Documentation) &&
            !(await adapter.GetApiCatalogAsync(token)).Any(x=>x.Namespace=="page" && x.Method==request.Method))
            return await PlatformV11PageReads.InvokeAsync(adapter,request.Target,request.Method,p,token);
        if (request.Namespace != "module") return (await adapter.InvokeAsync(request, token)).Clone();
        if (request.Method == "list")
        {
            var query = PlatformJson.Read<ModuleQuery>(p);
            if (!Enum.IsDefined(query.Kind) || query.Limit is < 1 or > 1000) throw new BridgeException("InvalidQuery", "Module kind/limit is invalid.");
            await RequireCapabilityAsync(query.Kind, ModuleOperation.List, null, token);
            return PlatformJson.Element(await adapter.ListAsync(request.Target, query, token));
        }
        if (request.Method == "validate")
        {
            var mutation = PlatformJson.Read<ModuleMutationRequest>(p);
            CheckTarget(mutation.Target);
            ValidateMutationShape(mutation);
            await CheckMutationAsync(mutation, token);
            return PlatformJson.Element(await adapter.ValidateAsync(mutation, token));
        }
        if (request.Method is not ("read" or "references" or "findReferences"))
            return (await adapter.InvokeAsync(request, token)).Clone();
        var address = PlatformJson.Read<ModuleAddress>(p);
        ValidateAddress(address);
        await RequireCapabilityAsync(address.Kind, ModuleOperation.Read, null, token);
        return request.Method switch
        {
            "read" => PlatformJson.Element(await adapter.ReadAsync(request.Target, address, token)),
            "references" or "findReferences" => PlatformJson.Element(await adapter.FindReferencesAsync(request.Target, address, token)),
            _ => throw new BridgeException("UnknownApi", "Unknown module method.")
        };
    }

    private async Task RequireCapabilityAsync(ModuleKind kind, ModuleOperation operation, EffectScope? scope, CancellationToken token)
    {
        var capability = (await adapter.GetCapabilitiesAsync(token)).SingleOrDefault(x => x.Kind == kind && x.Operation == operation);
        if (capability is null || !capability.Available) throw new BridgeException("OperationUnavailable", capability?.Reason ?? "Module operation is not implemented by this adapter.");
        if (scope is not null && scope != capability.Scope) throw new BridgeException("EffectScopeMismatch", "The request must explicitly name the registered side-effect scope.");
    }

    private async Task<IReadOnlyList<PlatformApiDescriptor>> CatalogAsync(CancellationToken token,
        IReadOnlyList<ModuleCapability>? capabilities = null)
    {
        // Called under the document gate. Publish only fully validated registrations.
        var signatures = fixedSignatures;
        if (signatures is null)
        {
            signatures = BuildSignatures(await adapter.GetApiCatalogAsync(token));
            if (adapter is IFixedProjectApiCatalog) fixedSignatures = signatures;
        }
        if (!PlatformV11PageReads.SupportsVersion(Target.DesignerVersion)) return signatures;
        capabilities ??= await adapter.GetCapabilitiesAsync(token);
        if (!PlatformV11PageReads.HasPageCapabilities(capabilities)) return signatures;
        var all = signatures.ToList();
        foreach (var method in new[] { "listPages", "getPageInfo", "getCells" })
            if (!all.Any(x => x.Namespace == "page" && x.Method == method)) all.Add(PlatformV11PageReads.Descriptor(method));
        if (adapter is IProjectPageProjectionAdapter && adapter is IProjectPageBaselineGuardAdapter &&
            capabilities.Any(x => x.Kind == ModuleKind.Page && x.Operation == ModuleOperation.Update && x.Available) &&
            !all.Any(x => x.Namespace == "page" && x.Method == "updateCells"))
            all.Add(PlatformV11CellWrites.Descriptor());
        return all;
    }

    private IReadOnlyList<PlatformApiDescriptor> BuildSignatures(IReadOnlyList<PlatformApiDescriptor> registered)
    {
        var extensions = registered.Select(x => x.MutatesProject
            ? x with { InputSchema = PlatformApiSchemas.AddMutationGuard(x.InputSchema),
                OutputSchema = PlatformApiSchemas.OperationStatus(x.Scope == EffectScope.AccountService && adapter is IProjectNativeServiceApiAdapter
                    ? NativeServiceApiSchemas.Receipt()
                    : x.Scope==EffectScope.RuntimeDeployment && adapter is IProjectRuntimeApiAdapter ? RuntimeApiSchemas.Receipt()
                    : adapter is IProjectModuleApiAdapter prepared && prepared.SupportsModuleApi(x.Namespace, x.Method)
                        ? PlatformApiSchemas.ModuleMutationReceipt() : x.OutputSchema),
                Documentation = x.Documentation + (x.Scope==EffectScope.RuntimeDeployment
                    ? " Parameters.guard and RuntimeDeployment scope are required. Project source save, native registration and runtime synchronization are reported separately; query the original operationId without replay."
                    : x.Scope == EffectScope.AccountService
                    ? adapter is IProjectNativeServiceApiAdapter
                        ? " Parameters.guard and AccountService scope are required. Returns native_accepted only when the native service accepted the action; independent database persistence and project export are not implied. Query the original operationId; never replay an unknown outcome."
                        : " Currently unavailable pending service persistence/read-back evidence; no account write is dispatched. Scope remains AccountService."
                    : adapter is IProjectModuleApiAdapter preparedApi && preparedApi.SupportsModuleApi(x.Namespace, x.Method)
                        ? " Parameters.guard is required. Uses the shared native module validation, mutation and save receipt; query the original operationId and inspect saveResult."
                        : " Parameters.guard is required. Native APIs edit memory; query the original operationId, then explicitly module.save with the latest project revision.") }
            : x);
        var builtins = startDevelopmentPreview is null ? BuiltinCatalog() : BuiltinCatalog().Append(DevelopmentPreviewDescriptor());
        var all = builtins.Concat(extensions).ToArray();
        if (all.GroupBy(x => (x.Namespace, x.Method)).Any(x => x.Count() != 1))
            throw new BridgeException("ApiNameCollision", "Adapter API catalog shadows another registered API.");
        return Array.AsReadOnly(all);
    }

    public static IReadOnlyList<PlatformApiDescriptor> BuiltinCatalog()
    {
        return new[] { ("plugin", "list", false), ("plugin", "read", false), ("plugin", "create", true), ("plugin", "update", true), ("plugin", "delete", true), ("collaboration", "status", false), ("collaboration", "checkout", true), ("collaboration", "checkin", true), ("collaboration", "receive", true), ("module", "list", false), ("module", "read", false), ("module", "create", true),
            ("module", "update", true), ("module", "delete", true), ("module", "validate", false),
            ("module", "references", false), ("module", "save", true), ("project", "snapshot", false),
            ("module", "findReferences", false), ("project", "capabilities", false), ("project", "get_operation", false),
            ("project", "cancel_operation", false), ("project", "validate_api", false), ("catalog", "list", false), ("catalog", "page", false) }
            .Select(x => new PlatformApiDescriptor(x.Item1, x.Item2, x.Item1 == "plugin" ? PluginLifecycleSchemas.Input(x.Item2) : PlatformSchemas.Input(x.Item1, x.Item2),
                x.Item1 == "plugin" ? PluginLifecycleSchemas.Output(x.Item2) : x.Item1 == "collaboration" ? CollaborationSchemas.Output(x.Item2) : x.Item1 == "catalog" ? PlatformSchemas.CatalogOutput(x.Item2) : x.Item3 ? PlatformApiSchemas.OperationStatus(PlatformApiSchemas.ModuleMutationReceipt()) : null, x.Item3,
                Scope: x.Item1 == "plugin" ? EffectScope.RuntimeDeployment : EffectScope.ProjectDefinition,
                Documentation: x.Item1 == "project" && x.Item2 == "validate_api"
                    ? "Read-only preflight of a registered project-definition adapter API. Supply namespace, method and the proposed parameters including guard. Returns valid/issues or a validation error. Does not reserve an ID, check out, write or save. A write revalidates the current state."
                    : x.Item1 == "catalog"
                    ? x.Item2 == "list" ? "Returns an array of complete final descriptors; optional namespace/method are exact case-sensitive filters. Empty parameters preserve full-array compatibility. Use catalog.page for bounded discovery in JavaScript."
                        : "Returns complete descriptors in stable namespace/method order with items/nextCursor/catalogRevision/totalCount. Exact filters and limit are bound to an opaque cursor; final schema changes invalidate it. Pages are bounded to 240000 JSON characters for default JavaScript budgets; individual schemas are never truncated."
                    : "Typed PlatformContracts parameters; mutations return an operation receipt. Read project.get_operation with the original ID.")).ToArray();
    }

    private async Task<ProjectSnapshot> SnapshotAsync(ProjectTarget target, CancellationToken token, bool issueBaseline = true)
    {
        CheckTarget(target);
        var snapshot = issueBaseline ? await adapter.GetSnapshotAsync(token) : await adapter.InspectSnapshotAsync(token);
        if (!SameTarget(snapshot.Target, Target))
        {
            invalidated = true;
            throw new BridgeException("SessionInvalidated", "Native document was reopened, switched, or replaced. This router will never rebind.");
        }
        return snapshot;
    }

    private void CheckTarget(ProjectTarget target, bool allowInvalidated = false)
    {
        if (invalidated && !allowInvalidated) throw new BridgeException("SessionInvalidated", "The bound document session is no longer valid.");
        ValidateTarget(target);
        if (!SameTarget(target, Target)) throw new BridgeException("TargetMismatch", "Exact instance, document session, project path and build must match.");
    }
    private static bool SameTarget(ProjectTarget a, ProjectTarget b) => a.InstanceId == b.InstanceId && a.SessionId == b.SessionId &&
        a.DesignerVersion == b.DesignerVersion && string.Equals(Path.GetFullPath(a.ProjectPath), Path.GetFullPath(b.ProjectPath), StringComparison.OrdinalIgnoreCase);
    private static void ValidateTarget(ProjectTarget target)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.InstanceId) || string.IsNullOrWhiteSpace(target.SessionId) ||
            string.IsNullOrWhiteSpace(target.DesignerVersion) || string.IsNullOrWhiteSpace(target.ProjectPath) || !Path.IsPathFullyQualified(target.ProjectPath))
            throw new BridgeException("InvalidTarget", "An exact instance/session/build and absolute project path are required.");
    }
    private static void ValidateAddress(ModuleAddress address)
    {
        if (address is null || !Enum.IsDefined(address.Kind) || string.IsNullOrWhiteSpace(address.Name))
            throw new BridgeException("InvalidModule", "An exact, known module kind and name are required.");
    }
    private static void ValidateMutationShape(ModuleMutationRequest request)
    {
        ValidateAddress(request.Address);
        if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete))
            throw new BridgeException("InvalidMutation", "Only create, update and delete are mutations.");
        if (request.Address.Kind == ModuleKind.ApplicationSettings && request.Operation is ModuleOperation.Create or ModuleOperation.Delete)
            throw new BridgeException("SingletonOperationNotSupported", "Application settings are a required singleton; update the root or edit collection entries.");
        if (!Enum.IsDefined(request.Scope)) throw new BridgeException("InvalidEffectScope", "Unknown side-effect scope.");
        if (request.Scope == EffectScope.AccountService)
        {
            if (!IsAccountModule(request.Address.Kind)) throw new BridgeException("AccountServiceMutationUnavailable", "Only typed single-entity User/Role/Organization operations accept service evidence.");
            ValidateRequiredServiceBinding(request.ExpectedServiceBinding);
        }
        else ValidateOptionalServiceBinding(request.ExpectedServiceBinding);
        if (string.IsNullOrWhiteSpace(request.ExpectedProjectRevision) ||
            (request.Operation != ModuleOperation.Create && string.IsNullOrWhiteSpace(request.ExpectedModuleRevision)))
            throw new BridgeException("BaselineRequired", "Project and existing-module revisions are required.");
        if (request.Operation != ModuleOperation.Delete && (request.Document is null || request.Document.Address != request.Address))
            throw new BridgeException("DocumentRequired", "Create/update require a document with the same module address.");
        if (request.Document is { } document)
        {
            if (document.Companions is null) throw new BridgeException("InvalidCompanions", "Companions must be an array, not null.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in document.Companions)
            {
                if (asset is null) throw new BridgeException("InvalidCompanions", "Companion array entries cannot be null.");
                var path = asset.RelativePath?.Replace('\\', '/');
                if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':') ||
                    path.Split('/').Any(x => x is ".." or "." or "") || !paths.Add(path))
                    throw new BridgeException("InvalidCompanionPath", "Companions require unique native relative asset paths without traversal.");
                byte[] bytes;
                try { bytes = Convert.FromBase64String(asset.ContentBase64); }
                catch (Exception error) when (error is FormatException or ArgumentNullException)
                { throw new BridgeException("InvalidCompanionBase64", "Companion data must be valid Base64."); }
                if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("CompanionHashMismatch", "Companion content does not match its SHA-256.");
            }
        }
    }
    private static void ValidateApiGuard(PlatformApiMutationGuard guard)
    {
        if (string.IsNullOrWhiteSpace(guard.ExpectedProjectRevision)) throw new BridgeException("BaselineRequired", "Native API guard requires the current final project revision.");
        if (!Enum.IsDefined(guard.Scope)) throw new BridgeException("InvalidEffectScope", "Unknown native API guard scope.");
        ValidateOptionalServiceBinding(guard.ExpectedServiceBinding);
        if (guard.Address is { } address)
        {
            ValidateAddress(address);
            if (string.IsNullOrWhiteSpace(guard.ExpectedModuleRevision)) throw new BridgeException("BaselineRequired", "Address and expectedModuleRevision must be supplied together.");
        }
        else if (guard.ExpectedModuleRevision is not null) throw new BridgeException("InvalidGuard", "Module revision requires a module address.");
    }
    private static void CheckOperationId(string? outer, string inner)
    {
        if (outer is not null && outer != inner) throw new BridgeException("OperationIdMismatch", "Outer and inner operationId differ.");
    }
    private static string RequiredString(JsonElement value, string name) => value.GetProperty(name).GetString()
        ?? throw new BridgeException("InvalidArguments", name + " is required.");
    private static PlatformOperationStatus ReadStatus(Entry entry) { lock (entry.Sync) return entry.Status; }
    private async Task<T> SerializedAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { token.ThrowIfCancellationRequested(); return await action(); }
        finally { gate.Release(); }
    }
}
