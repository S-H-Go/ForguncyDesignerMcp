using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

// Cross-version wire contracts. Only the integration chat changes this file.
public enum ModuleKind
{
    Page, MasterPage, UserControlPage, Table, View, ServerCommand, SharedCommand,
    ScheduledTask, Notification, Report, BusinessProcess, Image, ResourceFile,
    Theme, CellStyle, ListViewStyle, DataConnection, CustomLibrary, GlobalVariable,
    ApplicationSettings, LanguageResource, Plugin, User, Role, Organization,
    PermissionGroup, Folder
}

public enum ModuleOperation { List, Read, Create, Update, Delete }
public enum EffectScope { ProjectDefinition, ExternalDatabaseSchema, RuntimeDeployment, AccountService, DevelopmentPreview }

public sealed record ProjectTarget(string InstanceId, string SessionId, string ProjectPath, string DesignerVersion);
public sealed record AdapterIdentity(string DesignerVersion, string DesignerSha256, string Profile);
public sealed record ProjectSnapshot(ProjectTarget Target, bool HasUnsavedChanges, bool Busy,
    bool InCollaborationMode, UserIdentity? CurrentUser, string Revision,
    AccountServiceBinding? AccountService = null);

// Opaque tokens identify a verified effective service generation/configuration
// and a consistent semantic data snapshot. Reading a project snapshot never
// starts an account service merely to populate this optional binding.
public sealed record AccountServiceBinding(string Identity, string Revision);
public sealed record ModuleAddress(ModuleKind Kind, string Name, string? FolderPath = null);
public sealed record ModuleQuery(ModuleKind Kind, string? FolderPath = null, string? Cursor = null, int Limit = 100);
public sealed record ModuleSummary(ModuleAddress Address, string Revision, string? Description = null);
public sealed record ModulePage(IReadOnlyList<ModuleSummary> Items, string? NextCursor = null);

// Relative paths come from the adapter's native model. Callers cannot address arbitrary files.
public sealed record CompanionAsset(string RelativePath, string ContentBase64, string Sha256, string? MediaType = null);
public sealed record ModuleDocument(ModuleAddress Address, JsonElement Metadata,
    IReadOnlyList<CompanionAsset> Companions, string Revision);
public sealed record ModuleCapability(ModuleKind Kind, ModuleOperation Operation, bool Available,
    string Validation, string? Reason = null, EffectScope Scope = EffectScope.ProjectDefinition);
public sealed record ModuleReference(ModuleAddress Source, ModuleAddress Target, string Location);
public sealed record ReferenceResult(IReadOnlyList<ModuleReference> References, bool Complete, string? Reason = null);
public sealed record ValidationIssue(string Code, string Message, string? Path = null);
public sealed record ModuleValidationResult(bool Valid, IReadOnlyList<ValidationIssue> Issues,
    ReferenceResult? References = null);

public sealed record ModuleMutationRequest(string OperationId, ProjectTarget Target, ModuleOperation Operation,
    ModuleAddress Address, string ExpectedProjectRevision, string? ExpectedModuleRevision,
    ModuleDocument? Document, bool Save = true, UserIdentity? ExpectedUser = null,
    EffectScope Scope = EffectScope.ProjectDefinition,
    AccountServiceBinding? ExpectedServiceBinding = null);
public sealed record ProjectSaveRequest(string OperationId, ProjectTarget Target, string ExpectedProjectRevision,
    AccountServiceBinding? ExpectedServiceBinding = null);
public sealed record ProjectSaveResult(bool NativeSaveAccepted, bool ContainsUnsavedChanges,
    bool SourceVerified, string? EvidenceDirectory = null)
{
    // Standalone archives only: captured by the adapter after native save and
    // source/model verification. Null means no historical archive hash proof.
    // Keep the positional constructor unchanged for existing adapter binaries.
    public string? SavedSourceSha256 { get; init; }
}
public sealed record ModuleMutationReceipt(string OperationId, string State, string Code, string Message,
    ModuleDocument? Document = null, ProjectSaveResult? SaveResult = null,
    IReadOnlyList<string>? ChangedFiles = null, bool ExecutionMayContinue = false,
    ServiceMutationResult? ServiceResult = null);

// Binding is the verified pre-dispatch binding. ObservedRevision is the fresh
// post-action revision under that same service Identity. Stable Entity keys
// remain available even for deletion or a missing native response body.
public sealed record ServiceMutationResult(AccountServiceBinding Binding, ModuleAddress Entity,
    bool? NativeAccepted, bool PersistenceVerified, bool ReadbackVerified, bool? EntityPresent,
    string? ObservedRevision = null, string? EvidenceDirectory = null);

public sealed record PlatformApiDescriptor(string Namespace, string Method, JsonElement InputSchema,
    JsonElement? OutputSchema, bool MutatesProject, EffectScope Scope = EffectScope.ProjectDefinition,
    string? Documentation = null);
public sealed record PlatformApiRequest(ProjectTarget Target, string Namespace, string Method,
    JsonElement Parameters, string? OperationId = null);

// Guard for a typed, registered partial API action. Generic module CRUD keeps
// its complete ModuleMutationRequest; native aliases do not duplicate a module.
public sealed record PlatformApiMutationGuard(string OperationId, ProjectTarget Target,
    string ExpectedProjectRevision, UserIdentity? ExpectedUser = null,
    ModuleAddress? Address = null, string? ExpectedModuleRevision = null,
    EffectScope Scope = EffectScope.ProjectDefinition,
    AccountServiceBinding? ExpectedServiceBinding = null);

/// <summary>The adapter's registered API signatures and dispatch contracts are fixed for its lifetime.
/// Per-document module capabilities and their synthetic aliases are evaluated separately.</summary>
public interface IFixedProjectApiCatalog : IProjectAdapter { }

public interface IProjectAdapter
{
    AdapterIdentity Identity { get; }
    Task<ProjectSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);
    // Internal guard reads must not issue or renew a client baseline. Stateless adapters reuse their ordinary reads.
    Task<ProjectSnapshot> InspectSnapshotAsync(CancellationToken cancellationToken = default) => GetSnapshotAsync(cancellationToken);
    Task<ModuleDocument> InspectModuleAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default) =>
        ReadAsync(target, address, cancellationToken);
    Task<IReadOnlyList<ModuleCapability>> GetCapabilitiesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlatformApiDescriptor>> GetApiCatalogAsync(CancellationToken cancellationToken = default);
    Task<ModulePage> ListAsync(ProjectTarget target, ModuleQuery query, CancellationToken cancellationToken = default);
    Task<ModuleDocument> ReadAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default);
    Task<ReferenceResult> FindReferencesAsync(ProjectTarget target, ModuleAddress address, CancellationToken cancellationToken = default);
    Task<ModuleValidationResult> ValidateAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default);
    Task<ModuleMutationReceipt> ApplyAsync(ModuleMutationRequest request, CancellationToken cancellationToken = default);
    Task<ProjectSaveResult> SaveAsync(ProjectSaveRequest request, CancellationToken cancellationToken = default);
    Task<JsonElement> InvokeAsync(PlatformApiRequest request, CancellationToken cancellationToken = default);
}

// In-process construction only; this context is never accepted from MCP JSON.
public interface IUiScheduler
{
    Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken = default);
}

public sealed record AdapterHostContext(System.Reflection.Assembly DesignerAssembly,
    object NativeDocument, IUiScheduler Ui, Func<ProjectSnapshot> CaptureSnapshot, string DesignerRoot)
{
    // Server-owned policy, never populated from MCP request JSON. A receiver
    // without this policy stays unavailable. CaptureSnapshot observes the Host's
    // actual current document, including a native receive's new session.
    public string? CollaborationReceiveRoot { get; init; }
}

public interface IProjectAdapterFactory
{
    bool Supports(AdapterIdentity identity);
    IProjectAdapter Create(AdapterHostContext context);
}

// Implemented by the host router. The runtime exposes only this API bridge to JS.
public interface IPlatformApiInvoker
{
    Task<JsonElement> InvokeAsync(string @namespace, string method, JsonElement parameters,
        CancellationToken cancellationToken = default);
}

public sealed record PlatformRuntimeContext(ProjectTarget Target, IReadOnlyList<PlatformApiDescriptor> Apis,
    IReadOnlyList<ModuleCapability> Capabilities);

// MCP request params and results retain their original JSON structure, including media.
public interface IPlatformToolRuntime
{
    Task<JsonElement> ListToolsAsync(PlatformRuntimeContext context, CancellationToken cancellationToken = default);
    Task<JsonElement> CallToolAsync(string name, JsonElement arguments, PlatformRuntimeContext context,
        IPlatformApiInvoker api, CancellationToken cancellationToken = default);
    Task<JsonElement> HandleResourceAsync(string method, JsonElement parameters,
        PlatformRuntimeContext context, CancellationToken cancellationToken = default);
}
