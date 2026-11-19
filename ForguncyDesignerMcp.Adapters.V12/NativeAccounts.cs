using System.Collections;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>
/// V12 account-service operations have their own persistence domain. Reading a
/// binding never starts a service; explicit account reads may use the native
/// alive checker. No connection string, password, token or database row appears
/// in receipt evidence. Project export remains the owning adapter's operation.
/// </summary>
public sealed class NativeAccounts
{
    private readonly AdapterHostContext context;
    private readonly NativeApiRegistry registry;
    private readonly NativeAccountProcessObservation observation;
    private readonly SemaphoreSlim mutations = new(1, 1);
    private bool quarantined;
    public string? UnavailableReason { get; private set; }
    public bool WritesQuarantined => quarantined;
    public const string ConcurrencyBoundary = "The adapter checks an authoritative pre-dispatch transaction snapshot. The native service exposes no cross-client atomic compare-and-swap; this process queue does not serialize other service clients.";
    private static readonly ModuleKind[] Kinds = [ModuleKind.User, ModuleKind.Role, ModuleKind.Organization];

    public NativeAccounts(AdapterHostContext context, NativeApiRegistry registry)
    {
        this.context = context; this.registry = registry; observation = new(context);
        var assembly = context.DesignerAssembly;
        if (assembly.GetName().Version?.ToString() != V12AdapterFactory.Version ||
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) != V12AdapterFactory.DesignerSha256)
            throw new BridgeException("UnsupportedDesignerBuild", "Account service evidence requires the verified V12 native build.");
        AccountAuthoritySnapshot.ValidateNativeModel(assembly);
    }

    public bool Supports(ModuleKind kind, ModuleOperation operation) => Kinds.Contains(kind);

    // Safe for an ordinary project snapshot: fs.ajs and OS reads only. A failed
    // observation does not call the native alive checker or any account API.
    public AccountServiceBinding? TryCaptureBinding()
    {
        try { var captured = Capture(); UnavailableReason = null; return captured.Binding; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            UnavailableReason = error is BridgeException b ? b.Message : "The native child generation, listener, actual SQLite handles or authoritative V12 transaction snapshot are unverified; account writes remain unavailable.";
            return null;
        }
    }

    public IReadOnlyList<ModuleCapability> Capabilities()
    {
        var bound = TryCaptureBinding() is not null;
        return Kinds.SelectMany(kind => Enum.GetValues<ModuleOperation>().Select(operation =>
        {
            var read = operation is ModuleOperation.List or ModuleOperation.Read;
            return new ModuleCapability(kind, operation, read || bound && !quarantined,
                read ? "native-service-read-implemented; explicit-read-may-start-native-service" : bound && !quarantined ?
                    "implemented-bound-sqlite-authority; isolated-native-cud-acceptance-pending" : "unavailable",
                read ? "Explicit reads use the native dispatcher; no account service is started for ordinary snapshots." : quarantined ?
                    "A previous dispatched outcome is unconfirmed; subsequent account writes are isolated." : !bound ? UnavailableReason :
                    "Single-entity native account C/U/D with consistent SQLite authority and exact supplied-field/relationship readback. " + ConcurrencyBoundary,
                EffectScope.AccountService);
        })).ToArray();
    }

    public async Task<AccountServiceBinding?> PrepareReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); RequireDocument();
        var application = NativeReflection.Get(NativeReflection.Get(context.NativeDocument, "ForguncySettings")!, "ApplicationSettings")!;
        var port = Convert.ToInt32(NativeReflection.Get(application, "UserServicePort"));
        var pending = (Task)NativeReflection.Static(context.DesignerAssembly.GetType("fs", true)!, "CheckAliveDesignerUserServiceAsync", port)!;
        // Native startup has no cancellation contract. Continue awaiting after
        // dispatch; cancellation cannot certify that it did not start a child.
        await pending; RequireDocument(); return TryCaptureBinding();
    }

    public async Task<ModulePage> ListAsync(ModuleQuery query, CancellationToken cancellationToken = default)
    {
        ValidateAddress(new(query.Kind, "list"), true);
        if (query.FolderPath is not null || query.Limit is < 1 or > 1000) throw new BridgeException("InvalidAccountQuery", "Account queries use native IDs without folder filters and a limit from 1 to 1000.");
        var offset = query.Cursor is null ? 0 : int.TryParse(query.Cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0 ? value : throw new BridgeException("InvalidCursor", "Invalid account cursor.");
        await PrepareReadAsync(cancellationToken);
        var route = NativeApiCatalog.Routes[query.Kind];
        var result = await registry.InvokeAsync(route.Namespace, route.List, query.Kind == ModuleKind.User ?
            JsonSerializer.SerializeToElement(new { offset, limit = query.Limit }) : JsonSerializer.SerializeToElement(new { }), cancellationToken);
        var rows = Rows(query.Kind, result).ToArray(); var bound = TryCapture();
        var selected = query.Kind == ModuleKind.User ? rows : rows.OrderBy(row => Identity(query.Kind, row), StringComparer.Ordinal).Skip(offset).Take(query.Limit).ToArray();
        if (bound is not null && selected.Any(row => !bound.Data.VerifyNativeListRow(query.Kind, row)))
            throw new BridgeException("AccountAuthorityReadMismatch", "The native account inventory and independently observed authority disagree.");
        var items = selected.Select(row =>
        {
            var address = new ModuleAddress(query.Kind, Identity(query.Kind, row));
            return new ModuleSummary(address, bound is not null ? EntityRevision(bound, address) : UnboundRevision(row), Description(query.Kind, row));
        }).ToArray();
        if (items.Select(item => item.Address).Distinct().Count() != items.Length) throw new BridgeException("AccountIdentityAmbiguous", "Native account list returned duplicate stable identities.");
        var total = query.Kind == ModuleKind.User ? RequiredInt(result, "total") : rows.Length;
        if (total < offset + items.Length || items.Length == 0 && total > offset) throw new BridgeException("AccountPagingUnverified", "Native account pagination is incomplete or inconsistent.");
        return new(items, offset + items.Length < total ? (offset + items.Length).ToString(CultureInfo.InvariantCulture) : null);
    }

    public async Task<ModuleDocument> ReadAsync(ModuleAddress address, CancellationToken cancellationToken = default)
    {
        ValidateAddress(address); await PrepareReadAsync(cancellationToken);
        var result = await NativeRead(address, cancellationToken); var captured = TryCapture();
        if (captured is not null && (!captured.Data.Exists(address) || !captured.Data.VerifyNativeRead(address, result))) throw new BridgeException("AccountAuthorityReadMismatch", "The native response and independently observed authority disagree about the account fields or identity.");
        return new(address, result, [], captured is null ? UnboundRevision(result) : EntityRevision(captured, address));
    }

    public ReferenceResult References(ModuleAddress address)
    {
        ValidateAddress(address);
        return new([], false, "Account relations belong to the service. Typed deletion validates the authoritative account relation tables; project references are not represented as a complete account reference graph.");
    }

    public Task<ModuleValidationResult> ValidateAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { Validate(request, Capture()); return Task.FromResult(new ModuleValidationResult(true, [])); }
        catch (Exception error) when (error is not OperationCanceledException)
        { return Task.FromResult(new ModuleValidationResult(false, [new(error is BridgeException b ? b.Code : "AccountAuthorityUnavailable", SanitizedError(error))])); }
    }

    // Root performs native project SaveAsync only after this independent service
    // outcome is verified. A failure here never represents a project rollback.
    public async Task<NativeAccountMutationOutcome> ApplyAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var before = Capture(); var parameters = Validate(request, before);
            var route = NativeApiCatalog.Routes[request.Address.Kind];
            var method = request.Operation switch { ModuleOperation.Create => route.Create!, ModuleOperation.Update => route.Update!, _ => route.Delete! };
            var entry = registry.Entry(route.Namespace, method);
            if (entry.ResponseType.FullName != "Forguncy.AI.Actions.MutationResponse") throw new BridgeException("AccountNativeContractChanged", "The typed native account mutation response changed.");
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck immediately before entering the actual permission/checkout
            // preserving dispatcher. This is conservative optimistic validation,
            // not an atomic transaction against other account-service clients.
            var immediate = Capture();
            if (immediate.Binding != before.Binding) throw new BridgeException("AccountServiceRevisionChanged", "The bound account service changed before dispatch.");
            JsonElement response = default; bool? accepted = null; var entity = request.Address;
            try
            {
                response = await registry.InvokeAsync(route.Namespace, method, parameters, CancellationToken.None);
                if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("success", out var nativeAccepted) || nativeAccepted.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new BridgeException("AccountNativeContractChanged", "The typed native mutation response was not verifiable.");
                accepted = nativeAccepted.GetBoolean();
                if (accepted is true && request.Operation == ModuleOperation.Create && request.Address.Kind != ModuleKind.User)
                    entity = new(request.Address.Kind, RequiredInt(response, "id").ToString(CultureInfo.InvariantCulture));
                var after = Capture();
                if (after.Binding.Identity != before.Binding.Identity) throw new BridgeException("AccountServiceGenerationChanged", "The service generation changed after dispatch.");
                if (accepted is false)
                {
                    if (after.Binding.Revision != before.Binding.Revision) throw new BridgeException("AccountRejectedOutcomeChanged", "A rejected native request nevertheless changed authority state.");
                    return new(new(before.Binding, entity, false, false, false, after.Data.Exists(entity), after.Binding.Revision), null, "NativeAccountRejected", false);
                }
                var present = after.Data.Exists(entity);
                var expectedPresence = request.Operation != ModuleOperation.Delete;
                if (present != expectedPresence || request.Operation != ModuleOperation.Update && after.Binding.Revision == before.Binding.Revision) throw new BridgeException("AccountPersistenceUnverified", "The independent service transaction did not establish the expected account change.");
                if (request.Operation == ModuleOperation.Delete)
                {
                    // Authority NotFound is explicit. No HTTP exception, timeout
                    // or 403 response is ever interpreted as an absent entity.
                    return new(new(before.Binding, entity, true, true, true, false, after.Binding.Revision), null, "ServicePersistedAndVerified", false);
                }
                if (!after.Data.VerifyMutation(request, entity, parameters, before.Data)) throw new BridgeException("AccountFieldsUnverified", "The independently persisted account fields or relations differ from the approved request.");
                var fresh = await NativeRead(entity, CancellationToken.None);
                if (!after.Data.VerifyNativeRead(entity, fresh, parameters)) throw new BridgeException("AccountFreshReadUnverified", "The fresh native response differs from independently persisted account fields.");
                var final = Capture();
                if (final.Binding != after.Binding) throw new BridgeException("AccountReadbackRevisionChanged", "The service changed during fresh account readback.");
                var document = new ModuleDocument(entity, fresh, [], EntityRevision(final, entity));
                return new(new(before.Binding, entity, true, true, true, true, final.Binding.Revision), document, "ServicePersistedAndVerified", false);
            }
            catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
            {
                _ = error; // Never expose native exception text carrying account fields.
                quarantined = true;
                // Native task completion is not evidence that a rejected/lost
                // response rolled back an already committed service transaction.
                var after = TryCapture(); bool? present = null;
                if (after?.Binding.Identity == before.Binding.Identity)
                {
                    if (request.Operation == ModuleOperation.Create && request.Address.Kind != ModuleKind.User && entity == request.Address)
                        entity = after.Data.ResolveCreated(request.Address, before.Data) ?? entity;
                    present = after.Data.TryExists(entity);
                }
                return new(new(before.Binding, entity, accepted, false, false, present,
                    after?.Binding.Identity == before.Binding.Identity ? after.Binding.Revision : null), null, "ServiceOutcomeUnconfirmed", true);
            }
        }
        finally { mutations.Release(); }
    }

    public AccountServiceBinding RequireCurrentBinding(AccountServiceBinding expected)
    {
        var current = Capture().Binding;
        if (current != expected) throw new BridgeException("AccountServiceRevisionChanged", "The account service generation or authority revision differs from the reviewed export binding.");
        return current;
    }

    public bool VerifyProjectExport(AccountServiceBinding reviewed, string sourceProject)
    {
        try
        {
            var target = context.CaptureSnapshot().Target.ProjectPath;
            if (!string.Equals(Path.GetFullPath(target).TrimEnd('\\', '/'), Path.GetFullPath(sourceProject).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return false;
            _ = RequireCurrentBinding(reviewed);
            var verified = AccountAuthoritySnapshot.VerifyExportedSource(sourceProject, reviewed.Revision);
            _ = RequireCurrentBinding(reviewed);
            return verified;
        }
        catch { return false; }
    }

    private JsonElement Validate(ModuleMutationRequest request, CapturedAccountAuthority captured)
    {
        ValidateAddress(request.Address, request.Operation == ModuleOperation.Create);
        if (quarantined) throw new BridgeException("AccountWritesQuarantined", "A previous account-service outcome is unconfirmed; subsequent account writes are isolated.");
        if (request.Scope != EffectScope.AccountService) throw new BridgeException("AccountServiceScopeRequired", "Account mutations require explicit AccountService scope.");
        if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)) throw new BridgeException("InvalidAccountMutation", "Account mutation must be Create, Update or Delete.");
        if (request.ExpectedServiceBinding is null) throw new BridgeException("AccountServiceBindingRequired", "An explicitly prepared account service binding is required.");
        if (request.ExpectedServiceBinding != captured.Binding) throw new BridgeException("AccountServiceRevisionChanged", "The account service generation or authority data changed.");
        var project = context.CaptureSnapshot();
        if (project.Target != request.Target || project.Revision != request.ExpectedProjectRevision) throw new BridgeException("ProjectRevisionChanged", "The account request no longer matches the bound project baseline.");
        // Standalone projects have no collaboration user. Capture() has already
        // verified that this native parent and child run under the same actual
        // Windows process-user SID; aao retains the native permission context.
        // Never initialize/read CollaborationManager to invent an actor here.
        if (project.CurrentUser != request.ExpectedUser) throw new BridgeException("ExpectedUserChanged", "ExpectedUser must equal the current native identity, including null for a standalone project with an OS-user-bound native permission context.");
        if (request.Document is not null && (request.Document.Address != request.Address || request.Document.Companions.Count != 0)) throw new BridgeException("InvalidAccountDocument", "Account documents must match their single target and cannot contain companion files.");
        if (request.Operation != ModuleOperation.Delete && request.Document is null) throw new BridgeException("ModuleDocumentRequired", "Account create/update needs native request arguments.");
        if (request.Operation == ModuleOperation.Delete && request.Document is not null) throw new BridgeException("AccountDeleteDocumentNotAllowed", "Account deletion accepts only its single stable target identity.");
        var exists = request.Operation == ModuleOperation.Create ? captured.Data.ExistsByName(request.Address) : captured.Data.Exists(request.Address);
        if (exists != (request.Operation != ModuleOperation.Create)) throw new BridgeException(exists ? "ModuleAlreadyExists" : "ModuleNotFound", "The account entity does not match the requested create/update/delete lifecycle.");
        if (request.Operation != ModuleOperation.Create && (request.ExpectedModuleRevision is null || request.ExpectedModuleRevision != EntityRevision(captured, request.Address)))
            throw new BridgeException("ModuleRevisionChanged", "An authoritative account entity revision is required and must still match.");
        if (request.Operation == ModuleOperation.Delete && captured.Data.HasRelations(request.Address)) throw new BridgeException("AccountRelationsPresent", "Typed single-entity deletion requires removal of account memberships, parent/child and role relations first.");
        var parameters = Parameters(request);
        // Unknown fields and multi-entity aliases are rejected, rather than
        // silently ignored by Newtonsoft/native deserialization.
        var route = NativeApiCatalog.Routes[request.Address.Kind];
        var method = request.Operation switch { ModuleOperation.Create => route.Create!, ModuleOperation.Update => route.Update!, _ => route.Delete! };
        var schema = registry.Catalog().Single(item => item.Namespace == route.Namespace && item.Method == method).InputSchema;
        ValidateKnownParameters(schema, parameters);
        captured.Data.ValidateMutation(request, parameters);
        return parameters;
    }
    internal static JsonElement Parameters(ModuleMutationRequest request)
    {
        var identity = NativeApiCatalog.IdentityRoutes[request.Address.Kind];
        if (request.Document is not null && request.Document.Metadata.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidAccountMetadata", "Account native request arguments must be a JSON object.");
        var incoming = request.Document?.Metadata ?? JsonSerializer.SerializeToElement(new { });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in incoming.EnumerateObject())
            if (!seen.Add(property.Name) || property.Name.StartsWith('$')) throw new BridgeException("InvalidAccountMetadata", "Account arguments contain duplicate or CLR metadata fields.");
        var result = JsonNode.Parse(incoming.GetRawText())!.AsObject();
        var primary = request.Operation == ModuleOperation.Create ? identity.CreateNameParameter : identity.IdentityParameter;
        if (result.TryGetPropertyValue(primary, out var supplied) && supplied?.ToString() != request.Address.Name) throw new BridgeException("NativeAddressMismatch", "Account request identity differs from its single target address.");
        if (request.Operation == ModuleOperation.Delete)
        {
            result.Remove(primary); result[identity.DeleteArrayParameter] = identity.NumericIdentity ? new JsonArray(JsonValue.Create(ParseId(request.Address.Name))) : new JsonArray(JsonValue.Create(request.Address.Name));
        }
        else result[primary] = identity.NumericIdentity && request.Operation != ModuleOperation.Create ? JsonValue.Create(ParseId(request.Address.Name)) : JsonValue.Create(request.Address.Name);
        return JsonSerializer.SerializeToElement(result);
    }
    private static void ValidateKnownParameters(JsonElement schema, JsonElement parameters)
    {
        if (!schema.TryGetProperty("properties", out var properties)) throw new BridgeException("AccountNativeSchemaUnavailable", "Native account request properties are unverified.");
        foreach (var property in parameters.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var definition)) throw new BridgeException("UnknownAccountParameter", "An account request contains an unknown native parameter.");
            if (property.Value.ValueKind != JsonValueKind.Null && definition.TryGetProperty("type", out var declared))
            {
                var compatible = declared.GetString() switch
                {
                    "string" => property.Value.ValueKind == JsonValueKind.String,
                    "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "integer" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out _),
                    "array" => property.Value.ValueKind == JsonValueKind.Array,
                    "object" => property.Value.ValueKind == JsonValueKind.Object,
                    _ => true
                };
                if (!compatible) throw new BridgeException("InvalidAccountParameterType", "An account argument has an incompatible native scalar or collection type.");
            }
        }
        if (schema.TryGetProperty("required", out var required))
            foreach (var key in required.EnumerateArray())
                if (!parameters.TryGetProperty(key.GetString()!, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) throw new BridgeException("AccountParameterRequired", "A required native account parameter is missing.");
    }
    private async Task<JsonElement> NativeRead(ModuleAddress address, CancellationToken cancellationToken)
    {
        RequireDocument(); var route = NativeApiCatalog.Routes[address.Kind];
        var args = new Dictionary<string, object?> { [NativeApiCatalog.IdentityRoutes[address.Kind].IdentityParameter] = address.Kind == ModuleKind.User ? address.Name : ParseId(address.Name) };
        var result = await registry.InvokeAsync(route.Namespace, route.Read!, JsonSerializer.SerializeToElement(args), cancellationToken);
        if (ReadIdentity(address.Kind, result) != address.Name) throw new BridgeException("AccountReadIdentityMismatch", "The native account response does not match the requested stable identity.");
        return result;
    }
    private CapturedAccountAuthority Capture()
    {
        RequireRequestBoundEffectiveProviderProof();
        RequireDocument(); var before = observation.Capture();
        var data = AccountAuthoritySnapshot.Read(before.DatabasePath);
        var after = observation.Capture();
        if (before.Identity != after.Identity || !string.Equals(before.DatabasePath, after.DatabasePath, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("AccountServiceGenerationChanged", "Account service ownership changed while the authority snapshot was read.");
        return new(new(before.Identity, data.Revision), data);
    }
    private static void RequireRequestBoundEffectiveProviderProof()
    {
        // OS disk handles establish file ownership, not the provider selected by
        // a particular account request. The peer-bound child EF observer has
        // not been integrated/reviewed. Neither a startup event, retained JSON
        // file, parent configuration nor a host-supplied bool can satisfy this
        // gate. Default production snapshots and writes therefore stay unbound.
        throw new BridgeException("AccountEffectiveProviderProofRequired", "A live peer-bound, request-correlated observation of the actual native account EF provider and open connection is required; OS SQLite handles and startup-only evidence do not establish an account-service binding.");
    }
    private CapturedAccountAuthority? TryCapture() { try { return Capture(); } catch { return null; } }
    private void RequireDocument()
    {
        var singleton = NativeReflection.Static(context.DesignerAssembly.GetType("s", true)!, "get_Instance")!;
        if (!ReferenceEquals(NativeReflection.Get(singleton, "ew"), context.NativeDocument)) throw new BridgeException("AccountDocumentChanged", "The account service belongs to another native document.");
    }
    internal static void ValidateAddress(ModuleAddress address, bool create = false)
    {
        if (!Kinds.Contains(address.Kind) || address.FolderPath is not null || string.IsNullOrWhiteSpace(address.Name) || address.Name.Any(char.IsControl)) throw new BridgeException("InvalidAccountAddress", "Account addresses require a native identity without project folder paths.");
        if (!create && address.Kind != ModuleKind.User) _ = ParseId(address.Name);
    }
    private static int ParseId(string value) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 && id.ToString(CultureInfo.InvariantCulture) == value ? id : throw new BridgeException("InvalidServiceIdentity", "Use the canonical positive numeric account service ID.");
    private static IEnumerable<JsonElement> Rows(ModuleKind kind, JsonElement result)
    {
        var name = kind switch { ModuleKind.User => "items", ModuleKind.Role => "roles", _ => "organizations" };
        if (!result.TryGetProperty(name, out var rows) || rows.ValueKind != JsonValueKind.Array) throw new BridgeException("AccountNativeShapeChanged", "Native account list shape changed.");
        return rows.EnumerateArray();
    }
    private static string Identity(ModuleKind kind, JsonElement row) => kind == ModuleKind.User ? Text(row, "userName") ?? throw new BridgeException("AccountNativeShapeChanged", "Native user identity is missing.") : RequiredInt(row, kind == ModuleKind.Role ? "roleId" : "id").ToString(CultureInfo.InvariantCulture);
    private static string ReadIdentity(ModuleKind kind, JsonElement result) => Identity(kind, kind switch { ModuleKind.Role => result.GetProperty("role"), ModuleKind.Organization => result.GetProperty("node"), _ => result });
    private static string? Description(ModuleKind kind, JsonElement row) => Text(row, kind == ModuleKind.User ? "fullName" : "name");
    internal static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.ValueKind == JsonValueKind.String ? found.GetString() : null;
    private static int RequiredInt(JsonElement value, string name) => value.TryGetProperty(name, out var found) && found.TryGetInt32(out var id) && id >= 0 ? id : throw new BridgeException("AccountNativeShapeChanged", "A native account identity or pagination count is missing.");
    private static string EntityRevision(CapturedAccountAuthority captured, ModuleAddress entity) => Wire.Hash("account-entity-v1\n" + captured.Binding.Identity + "\n" + captured.Binding.Revision + "\n" + entity.Kind + "\n" + entity.Name);
    private static string UnboundRevision(JsonElement response) => Wire.Hash("unverified-account-read-v1\n" + response.GetRawText());
    private static string SanitizedError(Exception error) => error is BridgeException bridge ? bridge.Message : "The native account authority could not be verified; sensitive details are omitted.";
    private sealed record CapturedAccountAuthority(AccountServiceBinding Binding, AccountAuthoritySnapshot Data);
}

public sealed record NativeAccountMutationOutcome(ServiceMutationResult ServiceResult, ModuleDocument? Document, string Code, bool OutcomeUncertain);

// A normal SQLite read transaction participates in its live WAL. The snapshot
// canonicalizes SQL schema and every typed row, sorting row hashes to preserve
// duplicates and remove storage/order dependence. No file hash/data_version is
// substituted for this semantic authority revision. Raw values never escape.
internal sealed class AccountAuthoritySnapshot
{
    private readonly Dictionary<string, List<Dictionary<string, object?>>> records;
    private readonly Dictionary<long, string> passwordHashes;
    internal string Revision { get; }
    internal int TableCount { get; }
    private AccountAuthoritySnapshot(string revision, Dictionary<string, List<Dictionary<string, object?>>> records, int tableCount, Dictionary<long, string> passwordHashes)
    { Revision = revision; this.records = records; TableCount = tableCount; this.passwordHashes = passwordHashes; }
    private static readonly Dictionary<string, string[]> Required = new(StringComparer.Ordinal)
    {
        ["UserProfile"] = ["UserId", "UserName", "FullName", "Email"],
        ["webpages_Membership"] = ["UserId", "IsEnabled", "Password"],
        ["webpages_Roles"] = ["RoleId", "RoleName", "Description", "Type"],
        ["webpages_UsersInRoles"] = ["UserId", "RoleId"],
        ["OrganizationNodeListTable"] = ["ID", "Name", "NodeLevelID", "ParentID", "Description"],
        ["OrganizationMemberListTable"] = ["ID", "OrganizationID", "UserName", "IsWindowsUser"],
        ["OrganizationLevelTable"] = ["ID", "OrganizationLevelName"],
        ["OrganizationMemberInRoles"] = ["MemberId", "RoleId"],
        ["organization_role"] = ["OrganizationNodeId", "RoleId", "IsEffectedForSubordinates"],
        ["role_inherited"] = ["RoleId", "InheritedRoleId"],
        ["role_folder"] = ["RoleId", "FolderId"],
        ["CustomizedPropertiesTable"] = ["UserId", "PropertyId", "PropertyValue", "UserType"],
        ["PropertyListTable"] = ["ID", "PropertyName"],
        ["OrganizationProperties"] = ["OrganizationId", "PropertyId", "PropertyValue"],
        ["OrganizationPropertyInfo"] = ["ID", "PropertyName"]
    };
    internal static IReadOnlyDictionary<string, string[]> SchemaForComponentTests => Required;
    internal static void ValidateNativeModel(Assembly designer)
    {
        var service = NativeAccountAssemblyLoader.LoadService(designer);
        if (Convert.ToInt32(Enum.Parse(service.GetType("Forguncy.UserService2.Provider.UserType", true)!, "Form"), CultureInfo.InvariantCulture) != 1)
            throw new BridgeException("AccountNativeContractChanged", "The native Forms-user extension-property discriminator changed.");
        foreach (var pair in new Dictionary<string, string> { ["FormUser"] = "UserProfile", ["FormUserMembership"] = "webpages_Membership", ["Role"] = "webpages_Roles", ["UserInRole"] = "webpages_UsersInRoles", ["OrganizationNode"] = "OrganizationNodeListTable", ["OrganizationMember"] = "OrganizationMemberListTable", ["OrganizationLevel"] = "OrganizationLevelTable", ["OrganizationMemberInRole"] = "OrganizationMemberInRoles", ["OrganizationRoleEntity"] = "organization_role", ["RoleInheritedEntity"] = "role_inherited", ["RoleInFolderEntity"] = "role_folder", ["CustomizedProperties"] = "CustomizedPropertiesTable", ["PropertyInfoForDB"] = "PropertyListTable", ["OrganizationProperties"] = "OrganizationProperties", ["OrganizationPropertyInfo"] = "OrganizationPropertyInfo" })
        {
            var type = service.GetType("Forguncy.UserService2.Models." + pair.Key, true)!;
            var table = type.GetCustomAttributesData().Single(attribute => attribute.AttributeType.FullName == "System.ComponentModel.DataAnnotations.Schema.TableAttribute").ConstructorArguments[0].Value as string;
            if (table != pair.Value || Required[pair.Value].Any(column => type.GetProperty(column) is null)) throw new BridgeException("AccountNativeContractChanged", "A native account table or required scalar property changed.");
        }
    }
    internal static DbConnection OpenSqlite(string path, bool readOnly)
    {
        var type = Assembly.Load("Microsoft.Data.Sqlite").GetType("Microsoft.Data.Sqlite.SqliteConnection", true)!;
        var builder = new DbConnectionStringBuilder { ["Data Source"] = Path.GetFullPath(path), ["Mode"] = readOnly ? "ReadOnly" : "ReadWriteCreate", ["Cache"] = "Private", ["Pooling"] = false, ["Default Timeout"] = 5 };
        var connection = (DbConnection)Activator.CreateInstance(type, builder.ConnectionString)!;
        try { connection.Open(); return connection; } catch { connection.Dispose(); throw; }
    }
    internal static bool VerifyExportedSource(string sourceProject, string expectedRevision)
    {
        var nativeConfig = Assembly.Load("CommonUtilities").GetType("CommonUtilities.SystemConfigDef", true)!;
        var field = nativeConfig.GetField("UserServiceDB_sqlite3_File", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var nativeName = field is { IsLiteral: true } ? field.GetRawConstantValue() as string : field?.GetValue(null) as string;
        if (nativeName != "UserServiceDB.sqlite3") throw new BridgeException("AccountExportNameUnverified", "The fixed native account export filename could not be verified.");
        var group = ReadExportGroup(sourceProject, nativeName);
        if (!group.TryGetValue(nativeName, out var database) || database.Length == 0) return false;
        var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
        var scratch = Path.GetFullPath(Path.Combine(tempRoot, "forguncy-mcp-account-export-" + Guid.NewGuid().ToString("N")));
        if (!scratch.StartsWith(tempRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("AccountExportScratchInvalid", "Account export scratch is outside its private temporary root.");
        Directory.CreateDirectory(scratch);
        try
        {
            // SHM is a transient lock/index cache, never authoritative data. A
            // fresh private SHM is constructed by SQLite from main+WAL instead
            // of importing another process's mmap/lock bytes.
            foreach (var name in new[] { nativeName, nativeName + "-wal" })
                if (group.TryGetValue(name, out var bytes) && bytes.Length != 0) File.WriteAllBytes(Path.Combine(scratch, name), bytes);
            var actual = Read(Path.Combine(scratch, nativeName));
            if (actual.Revision != expectedRevision) return false;
            var repeated = ReadExportGroup(sourceProject, nativeName);
            try { return group.Count == repeated.Count && group.All(pair => repeated.TryGetValue(pair.Key, out var bytes) && pair.Value.AsSpan().SequenceEqual(bytes)); }
            finally { foreach (var bytes in repeated.Values) CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            foreach (var bytes in group.Values) CryptographicOperations.ZeroMemory(bytes);
            // Only delete this method's fixed leaf files, inside its verified
            // newly created directory. Never recursively traverse a computed
            // path or publish an account DB as ordinary receipt evidence.
            foreach (var name in new[] { nativeName, nativeName + "-wal", nativeName + "-shm", nativeName + "-journal" })
                try { File.Delete(Path.Combine(scratch, name)); } catch { }
            try { Directory.Delete(scratch, false); } catch { }
        }
    }
    private static Dictionary<string, byte[]> ReadExportGroup(string sourceProject, string nativeName)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var names = new[] { nativeName, nativeName + "-wal", nativeName + "-shm" };
        if (Directory.Exists(sourceProject))
        {
            var root = NativeAccountProcessObservation.CanonicalExisting(sourceProject, true);
            var inputs = new Dictionary<string, FileStream>(StringComparer.Ordinal);
            try
            {
                // Open all existing files with write/delete sharing excluded
                // before reading any bytes. A live source DB fails closed.
                foreach (var name in names)
                {
                    var path = Path.Combine(root, name);
                    if (File.Exists(path)) inputs.Add(name, new FileStream(NativeAccountProcessObservation.CanonicalExisting(path, false), FileMode.Open, FileAccess.Read, FileShare.Read));
                }
                foreach (var pair in inputs) result.Add(pair.Key, Limited(pair.Value));
            }
            finally { foreach (var input in inputs.Values) input.Dispose(); }
        }
        else
        {
            if (!Path.GetExtension(sourceProject).Equals(".fgcc", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("AccountExportSourceUnavailable", "Account source is neither a native directory nor FGCC archive.");
            _ = NativeAccountProcessObservation.CanonicalExisting(sourceProject, false);
            using var input = new FileStream(sourceProject, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var archive = new ZipArchive(input, ZipArchiveMode.Read);
            foreach (var name in names)
            {
                var entries = archive.Entries.Where(entry => entry.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (entries.Length == 0) continue;
                if (entries.Length != 1 || entries[0].FullName != name) throw new BridgeException("AccountExportSourceAmbiguous", "Native account archive entries are duplicated or noncanonical.");
                using var entry = entries[0].Open(); result.Add(name, Limited(entry));
            }
        }
        return result;
    }
    private static byte[] Limited(Stream input)
    {
        const int limit = 64 * 1024 * 1024;
        if (input.CanSeek && input.Length > limit) throw new BridgeException("AccountExportTooLarge", "Account export exceeds the bounded verification limit.");
        using var output = new MemoryStream(); var buffer = new byte[81920]; int count;
        while ((count = input.Read(buffer)) != 0)
        {
            if (output.Length + count > limit) throw new BridgeException("AccountExportTooLarge", "Account export exceeds the bounded verification limit.");
            output.Write(buffer, 0, count);
        }
        CryptographicOperations.ZeroMemory(buffer); return output.ToArray();
    }
    // The exact native SimpleMembershipProvider uses this same framework
    // PasswordHasher<User>. This pure hash comparison makes no login/API call,
    // changes no failed-login counter and never emits the supplied secret/hash.
    internal static bool VerifyPassword(string hash, string password)
    {
        var user = Assembly.Load("GrapeCity.Forguncy.SecurityProvider").GetType("GrapeCity.Forguncy.SecurityProvider.User", true)!;
        var type = Assembly.Load("Microsoft.Extensions.Identity.Core").GetType("Microsoft.AspNetCore.Identity.PasswordHasher`1", true)!.MakeGenericType(user);
        var hasher = Activator.CreateInstance(type, new object?[] { null })!;
        var verified = type.GetMethod("VerifyHashedPassword")!.Invoke(hasher, [null, hash, password]);
        return Convert.ToInt32(verified, CultureInfo.InvariantCulture) is 1 or 2;
    }
    internal static AccountAuthoritySnapshot Read(string path)
    {
        _ = NativeAccountProcessObservation.CanonicalExisting(path, false);
        using var connection = OpenSqlite(path, true);
        Execute(connection, "PRAGMA query_only=ON"); Execute(connection, "BEGIN");
        try
        {
            var schema = Query(connection, "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name,tbl_name");
            var tables = schema.Where(row => Equals(row["type"], "table")).Select(row => (string)row["name"]!).ToArray();
            if (tables.Length > 1000 || Required.Keys.Any(name => !tables.Contains(name, StringComparer.Ordinal))) throw new BridgeException("AccountAuthoritySchemaUnavailable", "The observed SQLite file does not contain the complete verified V12 account schema.");
            var retained = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
            var passwordHashes = new Dictionary<long, string>();
            using var revision = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            Append(revision, "v12-account-authority-v1");
            foreach (var row in schema) Append(revision, RowDigest(row));
            var rowCount = 0;
            foreach (var table in tables.OrderBy(name => name, StringComparer.Ordinal))
            {
                var rows = Query(connection, "SELECT * FROM " + Quote(table));
                rowCount = checked(rowCount + rows.Count);
                if (rowCount > 500000) throw new BridgeException("AccountAuthoritySnapshotTooLarge", "Account authority exceeds the bounded snapshot limit.");
                Append(revision, table); Append(revision, rows.Count.ToString(CultureInfo.InvariantCulture));
                foreach (var digest in rows.Select(RowDigest).OrderBy(value => value, StringComparer.Ordinal)) Append(revision, digest);
                if (Required.TryGetValue(table, out var expected))
                {
                    var columns = Query(connection, "PRAGMA table_info(" + Quote(table) + ")").Select(row => (string)row["name"]!).ToArray();
                    if (expected.Any(column => !columns.Contains(column, StringComparer.Ordinal))) throw new BridgeException("AccountAuthoritySchemaUnavailable", "A required native authority column is missing.");
                    // Retain only required ordinary fields, numeric relation IDs
                    // and a presence marker for password persistence. Credentials
                    // are hashed into the private revision and then discarded.
                    retained[table] = rows.Select(row => expected.ToDictionary(column => column, column => column == "Password" ?
                        (object?)(row[column] is string secret && secret.Length != 0) : row[column], StringComparer.Ordinal)).ToList();
                    if (table == "webpages_Membership")
                        foreach (var row in rows)
                            if (row["Password"] is string hash) passwordHashes.Add(Convert.ToInt64(row["UserId"], CultureInfo.InvariantCulture), hash);
                }
                else if (table.StartsWith("role_", StringComparison.OrdinalIgnoreCase) || table.Contains("Role", StringComparison.OrdinalIgnoreCase))
                    retained[table] = rows.Select(row => row.Where(pair => pair.Key.EndsWith("Id", StringComparison.OrdinalIgnoreCase)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)).ToList();
            }
            Execute(connection, "COMMIT");
            return new(Convert.ToHexString(revision.GetHashAndReset()), retained, tables.Length, passwordHashes);
        }
        catch { try { Execute(connection, "ROLLBACK"); } catch { } throw; }
    }
    internal bool Exists(ModuleAddress address) => Find(address) is not null;
    internal bool? TryExists(ModuleAddress address)
    { try { NativeAccounts.ValidateAddress(address); return Exists(address); } catch { return null; } }
    internal bool ExistsByName(ModuleAddress address) => Table(address.Kind).Any(row => String(row, NameColumn(address.Kind)) == address.Name);
    internal ModuleAddress? ResolveCreated(ModuleAddress address, AccountAuthoritySnapshot before)
    {
        var candidates = Table(address.Kind).Where(row => String(row, NameColumn(address.Kind)) == address.Name &&
            !before.Table(address.Kind).Any(old => Equal(old[IdColumn(address.Kind)], row[IdColumn(address.Kind)]))).ToArray();
        return candidates.Length == 1 ? new(address.Kind, String(candidates[0], IdColumn(address.Kind))!) : null;
    }
    internal bool HasRelations(ModuleAddress address)
    {
        var row = Find(address) ?? throw new BridgeException("ModuleNotFound", "Account entity is absent.");
        if (address.Kind == ModuleKind.User)
            return records["webpages_UsersInRoles"].Any(relation => Equal(relation["UserId"], row["UserId"])) ||
                records["OrganizationMemberListTable"].Any(relation => String(relation, "UserName") == address.Name && !Bool(relation["IsWindowsUser"]));
        var id = row[IdColumn(address.Kind)];
        if (address.Kind == ModuleKind.Organization)
            return Table(ModuleKind.Organization).Any(relation => Equal(relation["ParentID"], id)) || records["OrganizationMemberListTable"].Any(relation => Equal(relation["OrganizationID"], id)) ||
                records.Where(pair => pair.Key != "OrganizationNodeListTable" && pair.Key != "OrganizationProperties").SelectMany(pair => pair.Value).Any(relation => relation.Any(field => field.Key.Contains("Organization", StringComparison.OrdinalIgnoreCase) && field.Key.EndsWith("Id", StringComparison.OrdinalIgnoreCase) && Equal(field.Value, id)));
        return records.Where(pair => pair.Key != "webpages_Roles" && pair.Key != "role_folder").SelectMany(pair => pair.Value)
            .Any(relation => relation.Any(field => field.Key.EndsWith("RoleId", StringComparison.OrdinalIgnoreCase) && Equal(field.Value, id)));
    }
    internal void ValidateMutation(ModuleMutationRequest request, JsonElement parameters)
    {
        if (request.Operation == ModuleOperation.Delete) return;
        foreach (var name in request.Address.Kind == ModuleKind.User ? new[] { "extendedProperties" } : request.Address.Kind == ModuleKind.Organization ? new[] { "properties" } : Array.Empty<string>())
            if (parameters.TryGetProperty(name, out var dictionary) && dictionary.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object)) throw new BridgeException("InvalidAccountProperties", "Account extension properties must be a native string dictionary.");
        if (request.Address.Kind == ModuleKind.Organization && parameters.TryGetProperty("levelId", out var level) && level.ValueKind != JsonValueKind.Null &&
            (level.ValueKind != JsonValueKind.Number || !level.TryGetInt32(out var levelId) || !records["OrganizationLevelTable"].Any(row => Equal(row["ID"], levelId))))
            throw new BridgeException("AccountOrganizationLevelUnverified", "The requested native organization level does not resolve in the authority snapshot.");
        if (request.Address.Kind == ModuleKind.User && parameters.TryGetProperty("extendedProperties", out var values) && values.ValueKind == JsonValueKind.Object)
            ValidatePropertyNames(values, "PropertyListTable");
        if (request.Address.Kind == ModuleKind.Organization && parameters.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            ValidatePropertyNames(props, "OrganizationPropertyInfo");
    }
    internal bool VerifyMutation(ModuleMutationRequest request, ModuleAddress entity, JsonElement parameters, AccountAuthoritySnapshot before)
    {
        var row = Find(entity); if (row is null) return false;
        var kind = entity.Kind;
        if (request.Operation == ModuleOperation.Create && String(row, NameColumn(kind)) != request.Address.Name) return false;
        foreach (var pair in kind == ModuleKind.User ? new[] { ("fullName", "FullName"), ("email", "Email") } : kind == ModuleKind.Role ? new[] { ("roleName", "RoleName"), ("description", "Description") } : new[] { ("organizationName", "Name"), ("description", "Description") })
            if (parameters.TryGetProperty(pair.Item1, out var expected) && expected.ValueKind != JsonValueKind.Null &&
                expected.GetString() != String(row, pair.Item2)) return false;
        if (kind == ModuleKind.User)
        {
            var membership = records["webpages_Membership"].SingleOrDefault(member => Equal(member["UserId"], row["UserId"]));
            if (membership is null || request.Operation == ModuleOperation.Create && membership["Password"] is not true) return false;
            if (request.Operation == ModuleOperation.Create && (!parameters.TryGetProperty("password", out var password) || password.ValueKind != JsonValueKind.String ||
                !passwordHashes.TryGetValue(Convert.ToInt64(row["UserId"], CultureInfo.InvariantCulture), out var hash) || !VerifyPassword(hash, password.GetString()!))) return false;
            if (parameters.TryGetProperty("keepExistingFullName", out var keep) && keep.ValueKind == JsonValueKind.False &&
                (!parameters.TryGetProperty("fullName", out var fullName) || fullName.ValueKind == JsonValueKind.Null) && row["FullName"] is not null) return false;
            if (parameters.TryGetProperty("isEnabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False && enabled.GetBoolean() != Bool(membership["IsEnabled"])) return false;
            if (!PropertiesMatch(parameters, "extendedProperties", row["UserId"], "CustomizedPropertiesTable", "UserId", "PropertyListTable")) return false;
        }
        if (kind == ModuleKind.Role && parameters.TryGetProperty("parentFolderId", out var folder) && folder.ValueKind != JsonValueKind.Null)
        {
            var relation = records["role_folder"].SingleOrDefault(item => Equal(item["RoleId"], row["RoleId"]));
            if (relation is null || !Equal(relation["FolderId"], folder.GetInt32())) return false;
        }
        if (kind == ModuleKind.Organization)
        {
            if (parameters.TryGetProperty("parentOrganizationId", out var parent) && parent.ValueKind != JsonValueKind.Null && !Equal(row["ParentID"], parent.GetInt32())) return false;
            if (parameters.TryGetProperty("levelId", out var level) && level.ValueKind != JsonValueKind.Null && !Equal(row["NodeLevelID"], level.GetInt32())) return false;
            if (!PropertiesMatch(parameters, "properties", row["ID"], "OrganizationProperties", "OrganizationId", "OrganizationPropertyInfo")) return false;
        }
        // C/U APIs above do not accept memberships/inheritance. Detect changes
        // to those relations rather than declaring existence sufficient proof.
        if (request.Operation == ModuleOperation.Update && (RelationDigest(entity) != before.RelationDigest(entity) || !UnspecifiedFieldsPreserved(entity, parameters, before))) return false;
        return true;
    }
    private bool UnspecifiedFieldsPreserved(ModuleAddress entity, JsonElement parameters, AccountAuthoritySnapshot before)
    {
        var current = Find(entity)!; var old = before.Find(entity)!; var kind = entity.Kind;
        var fields = kind == ModuleKind.User ? new[] { ("fullName", "FullName"), ("email", "Email") } : kind == ModuleKind.Role ?
            new[] { ("roleName", "RoleName"), ("description", "Description") } :
            new[] { ("organizationName", "Name"), ("description", "Description"), ("parentOrganizationId", "ParentID"), ("levelId", "NodeLevelID") };
        foreach (var (parameter, column) in fields)
        {
            var supplied = parameters.TryGetProperty(parameter, out var value) && value.ValueKind != JsonValueKind.Null;
            if (kind == ModuleKind.User && parameter == "fullName" && parameters.TryGetProperty("keepExistingFullName", out var keep) && keep.ValueKind == JsonValueKind.False) supplied = true;
            if (!supplied && !Equal(current[column], old[column])) return false;
        }
        if (kind == ModuleKind.Role && (!parameters.TryGetProperty("parentFolderId", out var folder) || folder.ValueKind == JsonValueKind.Null) &&
            RowSetDigest(records["role_folder"].Where(row => Equal(row["RoleId"], current["RoleId"]))) != RowSetDigest(before.records["role_folder"].Where(row => Equal(row["RoleId"], old["RoleId"])))) return false;
        if (kind == ModuleKind.User)
        {
            var membership = records["webpages_Membership"].Single(row => Equal(row["UserId"], current["UserId"]));
            var oldMembership = before.records["webpages_Membership"].Single(row => Equal(row["UserId"], old["UserId"]));
            if ((!parameters.TryGetProperty("isEnabled", out var enabled) || enabled.ValueKind == JsonValueKind.Null) && !Equal(membership["IsEnabled"], oldMembership["IsEnabled"])) return false;
            var id = Convert.ToInt64(current["UserId"], CultureInfo.InvariantCulture);
            if (!passwordHashes.TryGetValue(id, out var hash) || !before.passwordHashes.TryGetValue(id, out var oldHash) || hash != oldHash) return false;
        }
        if (kind is ModuleKind.User or ModuleKind.Organization)
        {
            var table = kind == ModuleKind.User ? "CustomizedPropertiesTable" : "OrganizationProperties";
            var key = kind == ModuleKind.User ? "UserId" : "OrganizationId";
            var definitions = kind == ModuleKind.User ? "PropertyListTable" : "OrganizationPropertyInfo";
            var parameter = kind == ModuleKind.User ? "extendedProperties" : "properties";
            var updated = parameters.TryGetProperty(parameter, out var properties) && properties.ValueKind == JsonValueKind.Object ?
                properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
            var untouched = records[definitions].Where(row => !updated.Contains(String(row, "PropertyName")!)).Select(row => row["ID"]).ToArray();
            bool Include(Dictionary<string, object?> row, object? id) => Equal(row[key], id) && untouched.Any(property => Equal(row["PropertyId"], property)) &&
                (kind != ModuleKind.User || Convert.ToInt32(row["UserType"], CultureInfo.InvariantCulture) == 1);
            if (RowSetDigest(records[table].Where(row => Include(row, current[IdColumn(kind)]))) != RowSetDigest(before.records[table].Where(row => Include(row, old[IdColumn(kind)])))) return false;
        }
        return true;
    }
    private static string RowSetDigest(IEnumerable<Dictionary<string, object?>> rows) => Wire.Hash(string.Join("\n", rows.Select(RowDigest).OrderBy(value => value, StringComparer.Ordinal)));
    internal bool VerifyNativeListRow(ModuleKind kind, JsonElement result)
    {
        var identity = kind == ModuleKind.User ? NativeAccounts.Text(result, "userName") :
            result.TryGetProperty(kind == ModuleKind.Role ? "roleId" : "id", out var id) && id.TryGetInt32(out var value) ? value.ToString(CultureInfo.InvariantCulture) : null;
        if (identity is null) return false;
        if (kind == ModuleKind.User) return VerifyNativeRead(new(kind, identity), result);
        var row = Find(new(kind, identity));
        return row is not null && NativeAccounts.Text(result, "name") == String(row, NameColumn(kind)) &&
            (kind != ModuleKind.Organization || result.TryGetProperty("parentId", out var parent) && Equal(row["ParentID"], parent.GetInt32()) &&
                result.TryGetProperty("levelId", out var level) && Equal(row["NodeLevelID"], level.GetInt32()));
    }
    internal bool VerifyNativeRead(ModuleAddress entity, JsonElement result, JsonElement? expectedParameters = null)
    {
        var row = Find(entity); if (row is null) return false;
        var kind = entity.Kind; var value = kind == ModuleKind.User ? result : result.GetProperty(kind == ModuleKind.Role ? "role" : "node");
        var nativeId = NativeAccounts.Text(value, kind == ModuleKind.User ? "userName" : "name");
        if (nativeId != String(row, NameColumn(kind))) return false;
        if (kind is ModuleKind.User or ModuleKind.Organization && expectedParameters is { ValueKind: JsonValueKind.Object } expected)
        {
            var parameter = kind == ModuleKind.User ? "extendedProperties" : "properties";
            if (expected.TryGetProperty(parameter, out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                if (!result.TryGetProperty(parameter, out var readProperties) || readProperties.ValueKind != JsonValueKind.Object) return false;
                foreach (var property in properties.EnumerateObject())
                    if (!readProperties.TryGetProperty(property.Name, out var actual) || property.Value.ValueKind != actual.ValueKind || property.Value.ToString() != actual.ToString()) return false;
            }
        }
        if (kind == ModuleKind.User)
            return NativeAccounts.Text(value, "fullName") == String(row, "FullName") && NativeAccounts.Text(value, "email") == String(row, "Email") &&
                value.TryGetProperty("isEnabled", out var enabled) && enabled.GetBoolean() == Bool(records["webpages_Membership"].Single(member => Equal(member["UserId"], row["UserId"]))["IsEnabled"]);
        if (kind == ModuleKind.Role)
        {
            var relation = records["role_folder"].SingleOrDefault(item => Equal(item["RoleId"], row["RoleId"]));
            var nativeFolder = result.TryGetProperty("parentFolderId", out var folder) && folder.ValueKind != JsonValueKind.Null ? (object)folder.GetInt32() : null;
            if (!Equal(relation is null ? null : relation["FolderId"], nativeFolder)) return false;
            return NativeAccounts.Text(value, "description") == String(row, "Description");
        }
        return NativeAccounts.Text(result, "description") == String(row, "Description") &&
            value.TryGetProperty("parentId", out var parent) && Equal(row["ParentID"], parent.GetInt32()) &&
            value.TryGetProperty("levelId", out var level) && Equal(row["NodeLevelID"], level.GetInt32());
    }
    private string RelationDigest(ModuleAddress entity)
    {
        _ = entity;
        var relations = records.Where(pair => pair.Key is not ("UserProfile" or "webpages_Membership" or "webpages_Roles" or "OrganizationNodeListTable" or "CustomizedPropertiesTable" or "OrganizationProperties" or "role_folder" or "PropertyListTable" or "OrganizationPropertyInfo"))
            .SelectMany(pair => pair.Value.Select(relation => pair.Key + ":" + RowDigest(relation)));
        return Wire.Hash(string.Join("\n", relations.OrderBy(value => value, StringComparer.Ordinal)));
    }
    private bool PropertiesMatch(JsonElement parameters, string parameter, object? id, string table, string key, string definitions)
    {
        if (!parameters.TryGetProperty(parameter, out var expected) || expected.ValueKind == JsonValueKind.Null) return true;
        var values = records[table].Where(row => Equal(row[key], id) && (table != "CustomizedPropertiesTable" || Convert.ToInt32(row["UserType"], CultureInfo.InvariantCulture) == 1)).ToArray();
        foreach (var property in expected.EnumerateObject())
        {
            var definition = records[definitions].Single(row => String(row, "PropertyName") == property.Name);
            var value = values.SingleOrDefault(row => Equal(row["PropertyId"], definition["ID"]));
            if ((property.Value.ValueKind == JsonValueKind.Null ? null : property.Value.GetString()) != (value is null ? null : String(value, "PropertyValue"))) return false;
        }
        return true;
    }
    private void ValidatePropertyNames(JsonElement values, string definitions)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in values.EnumerateObject())
            if (!seen.Add(property.Name) || property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) || records[definitions].Count(row => String(row, "PropertyName") == property.Name) != 1)
                throw new BridgeException("InvalidAccountProperties", "Account extension properties must have unique existing native property names and string/null values.");
    }
    private Dictionary<string, object?>? Find(ModuleAddress address)
    {
        var column = address.Kind == ModuleKind.User ? "UserName" : IdColumn(address.Kind);
        var matches = Table(address.Kind).Where(row => String(row, column) == address.Name).ToArray();
        if (matches.Length > 1) throw new BridgeException("AccountAuthorityAmbiguous", "Account authority contains duplicate stable identities.");
        return matches.SingleOrDefault();
    }
    private List<Dictionary<string, object?>> Table(ModuleKind kind) => records[kind switch { ModuleKind.User => "UserProfile", ModuleKind.Role => "webpages_Roles", ModuleKind.Organization => "OrganizationNodeListTable", _ => throw new BridgeException("InvalidAccountKind", "Unsupported account kind.") }];
    private static string IdColumn(ModuleKind kind) => kind == ModuleKind.User ? "UserId" : kind == ModuleKind.Role ? "RoleId" : "ID";
    private static string NameColumn(ModuleKind kind) => kind == ModuleKind.User ? "UserName" : kind == ModuleKind.Role ? "RoleName" : "Name";
    private static string? String(Dictionary<string, object?> row, string key) => row[key] is null ? null : Convert.ToString(row[key], CultureInfo.InvariantCulture);
    private static bool Equal(object? first, object? second) => first is null || second is null ? first is null && second is null : Convert.ToString(first, CultureInfo.InvariantCulture) == Convert.ToString(second, CultureInfo.InvariantCulture);
    private static bool Bool(object? value) => value is not null && Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    private static List<Dictionary<string, object?>> Query(DbConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 5;
        using var reader = command.ExecuteReader(); var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            if (rows.Count >= 500000) throw new BridgeException("AccountAuthoritySnapshotTooLarge", "Account authority exceeds the bounded snapshot limit.");
            var row = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var index = 0; index < reader.FieldCount; index++) row.Add(reader.GetName(index), reader.IsDBNull(index) ? null : reader.GetValue(index));
            rows.Add(row);
        }
        return rows;
    }
    private static void Execute(DbConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 5; _ = command.ExecuteNonQuery(); }
    private static string RowDigest(Dictionary<string, object?> row)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var pair in row.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Append(hash, pair.Key);
            switch (pair.Value)
            {
                case null: Append(hash, "null"); break;
                case byte[] bytes: Append(hash, "blob"); Append(hash, Convert.ToHexString(bytes)); break;
                case string text: Append(hash, "text"); Append(hash, text); break;
                case double number: Append(hash, "real"); Append(hash, number.ToString("R", CultureInfo.InvariantCulture)); break;
                case long number: Append(hash, "integer"); Append(hash, number.ToString(CultureInfo.InvariantCulture)); break;
                default: Append(hash, pair.Value.GetType().FullName!); Append(hash, Convert.ToString(pair.Value, CultureInfo.InvariantCulture)!); break;
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void Append(IncrementalHash hash, string value)
    { var bytes = Encoding.UTF8.GetBytes(value); hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes); }
}
