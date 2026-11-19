using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

public sealed partial class V11ProjectAdapter : IProjectModuleApiAdapter
{
    public bool SupportsModuleApi(string @namespace, string method) => NativeV11CommandApi.Handles(@namespace, method) &&
        (method.StartsWith("create", StringComparison.Ordinal) || method.StartsWith("update", StringComparison.Ordinal) || method.StartsWith("delete", StringComparison.Ordinal));

    private async Task<JsonElement> InvokeCommandApiAsync(PlatformApiRequest request, CancellationToken token)
    {
        if (request.Parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidCommandRequest", "Command parameters must be an object.");
        var kind = NativeV11CommandApi.Kind(request.Namespace);
        var method = request.Method;
        if (method.StartsWith("list", StringComparison.Ordinal))
        {
            var result = new List<ModuleSummary>(); string? cursor = null;
            do { var page = await ListAsync(request.Target, new ModuleQuery(kind, null, cursor, 500), token); result.AddRange(page.Items); cursor = page.NextCursor; } while (cursor is not null);
            var projected = new JsonArray();
            foreach (var item in result) projected.Add(NativeV11CommandApi.Project(await ReadAsync(request.Target, item.Address, token), true));
            return JsonSerializer.SerializeToElement(projected, PlatformJson.Options);
        }
        var name = NativeV11CommandApi.Text(request.Parameters, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("InvalidCommandRequest", "A command name is required.");
        if (method.StartsWith("get", StringComparison.Ordinal))
        {
            var matches = new List<ModuleSummary>(); string? cursor = null;
            do { var page = await ListAsync(request.Target, new ModuleQuery(kind, null, cursor, 500), token); matches.AddRange(page.Items.Where(item => item.Address.Name.Equals(name, StringComparison.OrdinalIgnoreCase))); cursor = page.NextCursor; } while (cursor is not null);
            if (matches.Count != 1) throw new BridgeException("CommandNotUnique", "The command name must resolve to exactly one native definition.");
            var address = matches[0].Address;
            var document = await ReadAsync(request.Target, address, token);
            var projection = NativeV11CommandApi.Project(document, false);
            await OnUi(() =>
            {
                CheckTarget(request.Target);
                if (ReadDocument(address).Revision != document.Revision) throw new BridgeException("ModuleBaselineChanged", "The native command changed during its projection.");
                NativeV11CommandApi.ProjectKnownCommands(projection, session.Find(address).Model);
                return true;
            }, token);
            return JsonSerializer.SerializeToElement(projection, PlatformJson.Options);
        }
        throw new BridgeException("GuardedModuleRouteRequired", "Command mutations must use the shared guarded module route.");
    }

    public async Task<ModuleMutationRequest> PrepareModuleApiAsync(PlatformApiRequest request, CancellationToken token = default)
    {
        if (!SupportsModuleApi(request.Namespace, request.Method) || request.Parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidCommandRequest", "A supported command mutation and object parameters are required.");
        var kind = NativeV11CommandApi.Kind(request.Namespace);
        var method = request.Method;
        var name = NativeV11CommandApi.Text(request.Parameters, "name");
        if (string.IsNullOrWhiteSpace(name)) throw new BridgeException("InvalidCommandRequest", "A command name is required.");
        if (!request.Parameters.TryGetProperty("guard", out var guardJson)) throw new BridgeException("MutationGuardRequired", "A command mutation guard is required.");
        var guard = PlatformJson.Read<PlatformApiMutationGuard>(guardJson);
        if (request.OperationId != guard.OperationId || guard.Target != request.Target || guard.Scope != EffectScope.ProjectDefinition || guard.ExpectedServiceBinding is not null)
            throw new BridgeException("InvalidCommandGuard", "The command guard differs from the original request.");
        var operation = method.StartsWith("create", StringComparison.Ordinal) ? ModuleOperation.Create :
            method.StartsWith("update", StringComparison.Ordinal) ? ModuleOperation.Update : ModuleOperation.Delete;
        ModuleAddress address;
        ModuleDocument? document = null;
        if (operation == ModuleOperation.Create)
        {
            if (guard.Address is not null || guard.ExpectedModuleRevision is not null) throw new BridgeException("InvalidCommandGuard", "New commands cannot carry an existing module guard.");
            address = new(kind, name, NativeV11CommandApi.Text(request.Parameters, "folderPath"));
            V11Safety.Address(address);
            var seed = NativeV11CommandApi.CreateSeed(kind, name, request.Parameters, guard.OperationId);
            var canonical = await OnUi(() =>
            {
                CheckTarget(request.Target);
                var route = session.Route(kind);
                var model = NativeV11DefinitionWriter.Load(route, session.NativePath(address), seed.ToJsonString());
                NativeV11CommandApi.ApplyCommandNodes(model, request.Parameters, context.DesignerAssembly);
                if (kind == ModuleKind.SharedCommand) NativeV11CommandApi.ApplySharedSignatures(model, request.Parameters, true);
                else { NativeV11CommandApi.ApplyServerParameters(model, request.Parameters, true); NativeV11CommandApi.ApplyServerReturns(model, request.Parameters, true); }
                return session.Serialize(route, model);
            }, token);
            using var parsed = JsonDocument.Parse(canonical);
            document = new(address, parsed.RootElement.Clone(), [], "native-create-template");
        }
        else
        {
            address = guard.Address ?? throw new BridgeException("InvalidCommandGuard", "An existing command address and revision are required.");
            if (address.Kind != kind || !address.Name.Equals(name, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(guard.ExpectedModuleRevision))
                throw new BridgeException("InvalidCommandGuard", "The command name and native module guard differ.");
            if (operation == ModuleOperation.Update)
            {
                var before = await InspectModuleAsync(request.Target, address, token);
                if (before.Revision != guard.ExpectedModuleRevision) throw new BridgeException("ModuleBaselineChanged", "The command changed after its guarded read.");
                var model = JsonNode.Parse(before.Metadata.GetRawText())!.AsObject();
                NativeV11CommandApi.Patch(model, request.Parameters, false, kind);
                var canonical = await OnUi(() =>
                {
                    CheckTarget(request.Target);
                    var route = session.Route(kind);
                    var native = NativeV11DefinitionWriter.Load(route, session.NativePath(address), model.ToJsonString());
                    NativeV11CommandApi.ApplyCommandNodes(native, request.Parameters, context.DesignerAssembly);
                    if (kind == ModuleKind.SharedCommand) NativeV11CommandApi.ApplySharedSignatures(native, request.Parameters, false);
                    else { NativeV11CommandApi.ApplyServerParameters(native, request.Parameters, false); NativeV11CommandApi.ApplyServerReturns(native, request.Parameters, false); }
                    return session.Serialize(route, native);
                }, token);
                using var parsed = JsonDocument.Parse(canonical);
                document = before with { Metadata = parsed.RootElement.Clone() };
            }
            else if (request.Parameters.EnumerateObject().Any(field => field.Name is not ("name" or "guard")))
                throw new BridgeException("InvalidCommandRequest", "Command deletion accepts only name and guard.");
        }
        return new ModuleMutationRequest(guard.OperationId, request.Target, operation, address,
            guard.ExpectedProjectRevision, guard.ExpectedModuleRevision, document, Save: true, ExpectedUser: guard.ExpectedUser);
    }
}
