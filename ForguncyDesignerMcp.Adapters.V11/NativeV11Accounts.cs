using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// This is a deferred account-service adapter, deliberately separate from project
// model settings. Construction, capability queries and project snapshots do not
// resolve a native singleton, create a client/controller or start UserService.
// The current AdapterHostContext has no verified effective-service observation,
// consistent authoritative snapshot or native cross-client precondition boundary.
// Until that backend exists every account read/write remains unavailable.
//
// A future full native host must preserve administrator/permission filters and
// controller/request lifecycle; calling a reflected controller directly is not a
// substitute. It must verify actual endpoint/listener PID/start generation,
// parent/pipe and effective provider/database/tenant/auth configuration, then a
// consistent semantic revision including users and all role/org relations. A
// SQLite main-file hash, cache epoch, PRAGMA data_version, own process queue or
// ResilientTransaction by itself does not establish that precondition boundary.
// Service commit/fresh readback and project export need separate receipts. After
// dispatch an unknown outcome must quarantine writes and must not be replayed.
internal sealed class NativeV11Accounts
{
    private readonly Func<ModuleKind, CancellationToken, Task<IReadOnlyList<ModuleDocument>>>? projection;
    internal NativeV11Accounts() { }
    internal NativeV11Accounts(NativeV11Session session) => projection = new NativeV11AccountProjection(session).ReadProjectionAsync;
    internal NativeV11Accounts(Func<ModuleKind, CancellationToken, Task<IReadOnlyList<ModuleDocument>>> projection) => this.projection = projection;
    internal const string UnavailableCode = "AccountServiceBindingUnavailable";
    internal const string UnavailableReason = "V11 account-service operations require a verified effective service binding, a consistent authoritative semantic revision and a native permission/transaction context; this host does not provide them.";
    internal static IReadOnlyList<ModuleKind> Kinds { get; } = Array.AsReadOnly(new[] { ModuleKind.User, ModuleKind.Role, ModuleKind.Organization });
    internal AccountServiceBinding? Binding => null;

    internal sealed record Gap(string Code, string Detail);
    internal sealed record Preparation(bool Available, AccountServiceBinding? Binding, bool ServiceStartAttempted, IReadOnlyList<Gap> Gaps);
    internal sealed record NativeSignature(ModuleKind Kind, ModuleOperation Operation, string ControllerType,
        string Method, string? ParameterType, string ResultType, string KeyMapping, bool GeneratesNativeKey);
    internal sealed record NativeProperty(string Owner, string Name, string Type, bool Read, bool Write);
    internal sealed record ComponentEvidence(string File, string Version, string Sha256);
    internal sealed record ComponentInspection(string Version, IReadOnlyList<ComponentEvidence> Components,
        IReadOnlyList<NativeSignature> Signatures, IReadOnlyList<NativeProperty> Properties,
        bool ServiceOperationsVerified, IReadOnlyList<Gap> Gaps);

    internal static IReadOnlyList<Gap> Gaps { get; } = Array.AsReadOnly(new[]
    {
        new Gap("EffectiveBindingUnavailable", "No observation binds the actual endpoint/listener and service PID/start generation to its native parent/pipe and effective provider, database, tenant and authentication configuration."),
        new Gap("AuthoritativeRevisionUnavailable", "The designer IUserService exposes cached names and designer projections; no verified consistent authoritative semantic snapshot of entities and relations is available."),
        new Gap("NativePreconditionBoundaryUnavailable", "Native transaction helpers are present, but an atomic reviewed-revision check against concurrent service clients has not been established."),
        new Gap("NativeRequestContextUnavailable", "No authenticated native request lifecycle with preserved permission checks is attached to this host."),
        new Gap("ServiceReadbackAndExportUnverified", "Native generated keys, fresh expected-fields/relations readback, authoritative deletion and independent project export have not been exercised in a private service fixture.")
    });

    internal bool CanWrite(ModuleKind kind, ModuleOperation operation) => false;
    internal IReadOnlyList<ModuleCapability> Capabilities() => Kinds.SelectMany(kind => Enum.GetValues<ModuleOperation>()
        .Select(operation => new ModuleCapability(kind, operation, false, "native component metadata only; service integration deferred", UnavailableReason, EffectScope.AccountService))).ToArray();

    // This explicit preparation entry point reports the missing backend. It does
    // not silently turn a request into native lifecycle/service initialization.
    internal Preparation Prepare() => new(false, null, false, Gaps);
    internal IReadOnlyList<ModuleDocument> List(ModuleKind kind) => throw Unavailable();
    internal ModuleDocument Read(ModuleAddress address) => throw Unavailable();

    // Explicit reads use the existing native Designer service client. The
    // response is a deliberately incomplete projection, never a service
    // authority or an account-mutation baseline. Roles remain unavailable:
    // Designer exposes names but the native management identity is RoleId.
    internal async Task<ModulePage> ListAsync(ModuleQuery query, CancellationToken cancellationToken = default)
    {
        if (projection is null) throw Unavailable();
        if (query.Kind is not (ModuleKind.User or ModuleKind.Organization)) throw Unavailable();
        if (query.FolderPath is not null || query.Limit is < 1 or > 500)
            throw new BridgeException("InvalidAccountQuery", "Account projections do not support folder filters; limit must be between 1 and 500.");
        var rows = await projection(query.Kind, cancellationToken);
        var revision = ProjectionRevision(rows);
        var offset = 0;
        if (query.Cursor is not null)
        {
            ProjectionCursor cursor;
            try { cursor = JsonSerializer.Deserialize<ProjectionCursor>(Convert.FromBase64String(query.Cursor)) ?? throw new FormatException(); }
            catch (Exception e) when (e is FormatException or JsonException) { throw new BridgeException("InvalidCursor", "The account projection cursor is invalid."); }
            if (cursor.Kind != query.Kind || cursor.Revision != revision || cursor.Offset < 0 || cursor.Offset > rows.Count)
                throw new BridgeException("StaleCursor", "The native account projection changed; restart the listing.");
            offset = cursor.Offset;
        }
        var selected = rows.Skip(offset).Take(query.Limit).Select(row => new ModuleSummary(row.Address, row.Revision,
            "Incomplete Designer account projection; not a service-authority revision.")).ToArray();
        var next = offset + selected.Length;
        var nextCursor = next < rows.Count ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new ProjectionCursor(query.Kind, revision, next))) : null;
        return new(selected, nextCursor);
    }

    internal async Task<ModuleDocument> ReadAsync(ModuleAddress address, CancellationToken cancellationToken = default)
    {
        if (projection is null || address.Kind is not (ModuleKind.User or ModuleKind.Organization)) throw Unavailable();
        if (address.FolderPath is not null || string.IsNullOrWhiteSpace(address.Name) || address.Name.Any(char.IsControl))
            throw new BridgeException("InvalidAccountAddress", "Account projections require a native identity without a folder.");
        var rows = await projection(address.Kind, cancellationToken);
        return rows.SingleOrDefault(row => row.Address == address)
            ?? throw new BridgeException("AccountProjectionNotFound", "The identity was absent from the current Designer projection; this does not establish authoritative service deletion.");
    }

    private sealed record ProjectionCursor(ModuleKind Kind, string Revision, int Offset);
    private static string ProjectionRevision(IReadOnlyList<ModuleDocument> rows) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        "v11-unbound-projection-list-v1\n" + string.Join("\n", rows.Select(row => row.Revision)))));

    internal ModuleValidationResult Validate(ModuleMutationRequest request)
    {
        ValidationIssue issue;
        if (!Kinds.Contains(request.Address.Kind)) issue = new("AccountModuleRequired", "The deferred account adapter accepts User, Role and Organization only.");
        else if (request.Scope != EffectScope.AccountService) issue = new("EffectScopeMismatch", "Account mutations require AccountService scope.");
        else if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete)) issue = new("AccountMutationRequired", "A typed account mutation must specify Create, Update or Delete.");
        else if (request.ExpectedServiceBinding is not { } binding || string.IsNullOrWhiteSpace(binding.Identity) || string.IsNullOrWhiteSpace(binding.Revision))
            issue = new("AccountServiceBindingRequired", "Account mutations require the previously reviewed service Identity and authoritative Revision.");
        else issue = new(UnavailableCode, UnavailableReason);
        return new(false, [issue]);
    }

    internal ModuleMutationReceipt Apply(ModuleMutationRequest request)
    {
        var issue = Validate(request).Issues[0];
        // No dispatch occurred. No ServiceResult or project SaveResult is
        // fabricated, and request metadata/credentials are never echoed.
        return new(request.OperationId, "failed", issue.Code, issue.Message);
    }

    private static BridgeException Unavailable() => new(UnavailableCode, UnavailableReason);

    private const string UserController = "Forguncy.UserService2.Controllers.ServerManagerApiController.UserApiController";
    private const string RoleController = "Forguncy.UserService2.Controllers.ApiManagerController.V2.RoleApiV2Controller";
    private const string OrganizationController = "Forguncy.UserService2.Controllers.ApiManagerController.V2.OrganizationApiV2Controller";
    private const string Request = "Forguncy.UserService2.RequestModels.";
    private const string V2 = "Forguncy.UserService2.Models.UserManagementV2.";
    private const string UserKey = "V1 request key is UserName; authoritative readback must retain native UserId and verify the same user and its relations. Names are not adapter-generated IDs.";
    private const string RoleKey = "RoleId is the native integer key. Add returns a server-generated key; preserve it and verify expected fields and relations.";
    private const string OrganizationKey = "OrganizationId is the native integer key. Add returns a server-generated key; preserve it and verify parent, level and membership relations.";
    internal static IReadOnlyList<NativeSignature> NativeSignatures { get; } = Array.AsReadOnly(new[]
    {
        new NativeSignature(ModuleKind.User, ModuleOperation.Create, UserController, "AddUsersAsync", Request + "RequestUserInfosParam", "CommonUtilities.ResultDataBase", UserKey, true),
        new NativeSignature(ModuleKind.User, ModuleOperation.Update, UserController, "UpdateUserInfoAsync", Request + "UpdateUserInfoParam", "CommonUtilities.ResultDataBase", UserKey, false),
        new NativeSignature(ModuleKind.User, ModuleOperation.Delete, UserController, "DeleteUsersAsync", Request + "UsersParam", "CommonUtilities.ResultDataBase", UserKey, false),
        new NativeSignature(ModuleKind.User, ModuleOperation.List, UserController, "GetUserInfosAsync", Request + "GetUserInfosParam", "CommonUtilities.UserInfosResultData", UserKey, false),
        new NativeSignature(ModuleKind.User, ModuleOperation.Read, UserController, "GetUserInfoAsync", Request + "GetUserInfoParam", "CommonUtilities.UserInfoResultData", UserKey, false),
        new NativeSignature(ModuleKind.Role, ModuleOperation.Create, RoleController, "AddRoleAsync", V2 + "AddRoleParam", "CommonUtilities.ResultDataBase", RoleKey, true),
        new NativeSignature(ModuleKind.Role, ModuleOperation.Update, RoleController, "UpdateRoleAsync", V2 + "UpdateRoleParam", "CommonUtilities.ResultDataBase", RoleKey, false),
        new NativeSignature(ModuleKind.Role, ModuleOperation.Delete, RoleController, "DeleteRolesAsync", V2 + "DeleteRolesParam", "CommonUtilities.ResultDataBase", RoleKey, false),
        new NativeSignature(ModuleKind.Role, ModuleOperation.List, "Forguncy.UserService2.Controllers.ServerManagerApiController.RoleApiController", "GetRolesAsync", null, "CommonUtilities.RolesResultData", RoleKey, false),
        new NativeSignature(ModuleKind.Role, ModuleOperation.Read, "Forguncy.UserService2.Controllers.ServerManagerApiController.RoleApiController", "GetRolesAsync", null, "CommonUtilities.RolesResultData", RoleKey, false),
        new NativeSignature(ModuleKind.Organization, ModuleOperation.Create, OrganizationController, "AddOrganizationAsync", V2 + "AddOrganizationParam", "CommonUtilities.ResultDataBase", OrganizationKey, true),
        new NativeSignature(ModuleKind.Organization, ModuleOperation.Update, OrganizationController, "UpdateOrganizationAsync", V2 + "UpdateOrganizationParam", "CommonUtilities.ResultDataBase", OrganizationKey, false),
        new NativeSignature(ModuleKind.Organization, ModuleOperation.Delete, OrganizationController, "DeleteOrganizationsAsync", V2 + "DeleteOrganizationsParam", "CommonUtilities.ResultDataBase", OrganizationKey, false),
        new NativeSignature(ModuleKind.Organization, ModuleOperation.List, "Forguncy.UserService2.Controllers.ServerManagerApiController.OrganizationApiController", "GetOrganizationInfosAsync", Request + "GetOrganizationInfosParam", "CommonUtilities.OrganizationInfosResultData", OrganizationKey, false),
        new NativeSignature(ModuleKind.Organization, ModuleOperation.Read, "Forguncy.UserService2.Controllers.ServerManagerApiController.OrganizationApiController", "GetOrganizationInfosAsync", Request + "GetOrganizationInfosParam", "CommonUtilities.OrganizationInfosResultData", OrganizationKey, false)
    });

    // Opens installed PE metadata, not Assembly.Load/UserService constructors or
    // static getters. This can be used by the isolated component fixture without
    // ASP.NET initialization, an endpoint, an account read, or native credentials.
    // Metadata evidence does NOT make any operation available.
    internal static ComponentInspection InspectComponents(Assembly designer)
    {
        var profile = V11BuildProfile.Resolve(designer);
        var native = Path.GetDirectoryName(designer.Location)!;
        var bin = Path.GetFullPath(Path.Combine(native, "..", "bin"));
        var serverHash = profile.Version == "11.0.104.0"
            ? "3D314F3107E733C89B856BAAF7A5874C708ECEAA9B5907E8A1AFB4D8DA9B50B0"
            : "7CBB730CB3D1CFA392D9D3D3AF1B8FE110D9F860CE2983567D5A5CEC72350DD8";
        var requestHash = profile.Version == "11.0.104.0"
            ? "D260620009902FEBF82E6BFF55294691667E183D926935477A711906EAA5BE16"
            : "5E0244E3FB475462394BC9F5A25CEC0007E8895588CEFFCE70A6AA09C6D94961";
        using var service = new NativeMetadata(Path.Combine(bin, "Forguncy.UserService2.dll"), profile.Version, serverHash);
        using var requests = new NativeMetadata(Path.Combine(bin, "ForguncyServerCommon.dll"), profile.Version, requestHash);
        using var common = new NativeMetadata(Path.Combine(native, "CommonUtilities.dll"), profile.Version, profile.CommonSha256);
        var images = new[] { service, requests, common };
        foreach (var signature in NativeSignatures)
            service.RequireMethod(signature.ControllerType, signature.Method,
                signature.ParameterType is null ? [] : [signature.ParameterType], "System.Threading.Tasks.Task`1<" + signature.ResultType + ">");
        service.RequireMethod("Forguncy.UserService2.Services.UserManagement.V2.IUserManagementV2Service", "AddRoleAsync", [V2 + "IRoleInfoParam"], "System.Threading.Tasks.Task`1<System.Int32>");
        service.RequireMethod("Forguncy.UserService2.Services.UserManagement.V2.IUserManagementV2Service", "AddOrganizationAsync", [V2 + "IOrganizationInfoParam"], "System.Threading.Tasks.Task`1<System.Int32>");
        var properties = new List<NativeProperty>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void AddProperties(string name)
        {
            if (!visited.Add(name)) return;
            var image = images.SingleOrDefault(candidate => candidate.Contains(name))
                ?? throw new NotSupportedException("A pinned V11 account request/model type is missing.");
            properties.AddRange(image.Properties(name));
            var parent = image.Parent(name);
            if (parent is not null && images.Any(candidate => candidate.Contains(parent))) AddProperties(parent);
        }
        foreach (var name in NativeSignatures.Select(signature => signature.ParameterType).OfType<string>().Distinct(StringComparer.Ordinal)) AddProperties(name);
        foreach (var name in new[] { "CommonUtilities.RequestUserInfo", "CommonUtilities.UserDetailInfo", "CommonUtilities.RoleInfo" }) AddProperties(name);
        return new(profile.Version, images.Select(image => image.Evidence).ToArray(), NativeSignatures,
            properties.OrderBy(property => property.Owner, StringComparer.Ordinal).ThenBy(property => property.Name, StringComparer.Ordinal).ToArray(), false, Gaps);
    }

    private sealed class NativeMetadata : IDisposable
    {
        private readonly FileStream stream;
        private readonly PEReader pe;
        private readonly MetadataReader reader;
        private readonly Dictionary<string, TypeDefinitionHandle> types;
        internal ComponentEvidence Evidence { get; }
        internal NativeMetadata(string path, string version, string hash)
        {
            stream = File.OpenRead(path);
            try
            {
                if (Convert.ToHexString(SHA256.HashData(stream)) != hash) throw new NotSupportedException("The installed V11 account component fingerprint does not match its exact profile.");
                stream.Position = 0;
                pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                reader = pe.GetMetadataReader();
                var definition = reader.GetAssemblyDefinition();
                if (definition.Version.ToString() != version || reader.GetString(definition.Name) != Path.GetFileNameWithoutExtension(path))
                    throw new NotSupportedException("The installed V11 account component identity does not match its exact profile.");
                types = reader.TypeDefinitions.ToDictionary(handle => SignatureNames.Definition(reader, handle), StringComparer.Ordinal);
                Evidence = new(Path.GetFileName(path), version, hash);
            }
            catch { stream.Dispose(); throw; }
        }
        internal bool Contains(string name) => types.ContainsKey(name);
        internal string? Parent(string name)
        {
            var parent = reader.GetTypeDefinition(types[name]).BaseType;
            return parent.IsNil ? null : SignatureNames.Entity(reader, parent);
        }
        internal IReadOnlyList<NativeProperty> Properties(string name) => reader.GetTypeDefinition(types[name]).GetProperties().Select(handle =>
        {
            var property = reader.GetPropertyDefinition(handle);
            var accessors = property.GetAccessors();
            bool Public(MethodDefinitionHandle method) => !method.IsNil && (reader.GetMethodDefinition(method).Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public;
            return new NativeProperty(name, reader.GetString(property.Name), property.DecodeSignature(SignatureNames.Instance, (object?)null).ReturnType, Public(accessors.Getter), Public(accessors.Setter));
        }).ToArray();
        internal void RequireMethod(string owner, string name, string[] parameters, string result)
        {
            if (!types.TryGetValue(owner, out var type)) throw new NotSupportedException("A pinned V11 account controller/service type is missing.");
            var matches = reader.GetTypeDefinition(type).GetMethods().Select(reader.GetMethodDefinition).Where(method => reader.GetString(method.Name) == name)
                .Select(method => method.DecodeSignature(SignatureNames.Instance, (object?)null))
                .Count(signature => signature.ReturnType == result && signature.ParameterTypes.SequenceEqual(parameters, StringComparer.Ordinal));
            if (matches != 1) throw new NotSupportedException("A pinned V11 native account method signature is unavailable or ambiguous.");
        }
        public void Dispose() { pe.Dispose(); stream.Dispose(); }
    }

    private sealed class SignatureNames : ISignatureTypeProvider<string, object?>
    {
        internal static readonly SignatureNames Instance = new();
        internal static string Definition(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var type = reader.GetTypeDefinition(handle);
            return !type.GetDeclaringType().IsNil ? Definition(reader, type.GetDeclaringType()) + "+" + reader.GetString(type.Name)
                : Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }
        private static string Reference(MetadataReader reader, TypeReferenceHandle handle)
        {
            var type = reader.GetTypeReference(handle);
            return type.ResolutionScope.Kind == HandleKind.TypeReference ? Reference(reader, (TypeReferenceHandle)type.ResolutionScope) + "+" + reader.GetString(type.Name)
                : Join(reader.GetString(type.Namespace), reader.GetString(type.Name));
        }
        private static string Join(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
        internal static string Entity(MetadataReader reader, EntityHandle handle) => handle.Kind switch
        {
            HandleKind.TypeDefinition => Definition(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => Reference(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(Instance, (object?)null),
            _ => throw new BadImageFormatException("Unsupported native account metadata type handle.")
        };
        public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
        public string GetByReferenceType(string elementType) => elementType + "&";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "methodptr";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
        public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
        public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
        public string GetPinnedType(string elementType) => elementType;
        public string GetPointerType(string elementType) => elementType + "*";
        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Boolean => "System.Boolean", PrimitiveTypeCode.Byte => "System.Byte", PrimitiveTypeCode.Char => "System.Char",
            PrimitiveTypeCode.Double => "System.Double", PrimitiveTypeCode.Int16 => "System.Int16", PrimitiveTypeCode.Int32 => "System.Int32",
            PrimitiveTypeCode.Int64 => "System.Int64", PrimitiveTypeCode.IntPtr => "System.IntPtr", PrimitiveTypeCode.Object => "System.Object",
            PrimitiveTypeCode.SByte => "System.SByte", PrimitiveTypeCode.Single => "System.Single", PrimitiveTypeCode.String => "System.String",
            PrimitiveTypeCode.TypedReference => "System.TypedReference", PrimitiveTypeCode.UInt16 => "System.UInt16", PrimitiveTypeCode.UInt32 => "System.UInt32",
            PrimitiveTypeCode.UInt64 => "System.UInt64", PrimitiveTypeCode.UIntPtr => "System.UIntPtr", PrimitiveTypeCode.Void => "System.Void",
            _ => throw new BadImageFormatException("Unsupported native account metadata primitive.")
        };
        public string GetSZArrayType(string elementType) => elementType + "[]";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => Definition(reader, handle);
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => Reference(reader, handle);
        public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
