using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Fixed in-process native stores only. No delegate or native object is accepted
// from MCP input. Definitions with binary files or directory markers need their
// own source comparison semantics instead of pretending every file is JSON.
internal sealed record NativeV11AuxiliaryStore(
    IReadOnlyList<ModuleKind> Kinds,
    Func<ModuleKind, IReadOnlyList<ModuleDocument>> List,
    Func<ModuleAddress, ModuleDocument> Read,
    Func<ModuleKind, ModuleOperation, bool> CanWrite,
    Action<ModuleMutationRequest> Validate,
    Func<ModuleMutationRequest, Action> Apply,
    Func<ModuleAddress, ReferenceResult> FindReferences,
    Func<ModuleAddress, IReadOnlyList<string>> SourcePaths,
    Func<ModuleMutationRequest, IReadOnlyList<string>> MutationSourcePaths,
    Func<ModuleMutationRequest, ModuleAddress> ResultAddress,
    Func<ModuleMutationRequest, IReadOnlyDictionary<string, byte[]>> CaptureSaveFiles,
    Func<string, bool> IsJsonSaveFile,
    Action<ModuleMutationRequest>? VerifyDeleted = null,
    Func<ModuleAddress, bool>? ContainsAddress = null);
