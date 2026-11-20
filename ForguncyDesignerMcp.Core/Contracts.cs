using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core;

public static class Wire
{
    public const int Version = 1;
    public const string PluginVersion = "0.5.27";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value, Json);
}

public sealed record UserIdentity(string UserName, string Email);
public sealed record ModuleInfo(string Name, int Type, int IconType, int State, UserIdentity? LockedBy,
    string? LockDateTime, string? ExclusiveType, string[] Files);
public sealed record GitState(string Head, string Branch, string? RemoteName, string? RemoteUrl,
    string? Upstream, string? UpstreamHead, bool IndexClean, string WorktreeHash,
    UserIdentity? Author = null, UserIdentity? Committer = null, Dictionary<string, string>? ChangedFiles = null);
public sealed record DesignerContext(string SessionId, string ProjectPath, string DesignerVersion,
    bool InCollaborationMode, bool HasUnsavedChanges, bool Busy, UserIdentity? CurrentUser,
    bool ModuleStateStale, string LockHash, string Revision, GitState? Git, ModuleInfo[] Modules);
public sealed record InstanceInfo(int ProtocolVersion, int ProcessId, long ProcessStartTicks, string PipeName,
    string DesignerVersion, string PluginVersion, string? SessionId, string? ProjectPath, DateTimeOffset Heartbeat)
{
    // A string key avoids JavaScript's 53-bit integer limit for process start ticks.
    public string InstanceId => ProcessId + "-" + ProcessStartTicks;
}
public sealed record Capability(string Name, bool Available, string Effect, string Validation, string? Reason = null);
public sealed record MutationRequest(string OperationId, string SessionId, string ProjectPath,
    string? ExpectedRevision, string[] Modules, string? Message, UserIdentity? ExpectedUser,
    string? ExpectedRemoteUrl, string? ExpectedBranch);
public sealed record OperationReceipt(string OperationId, string Operation, string State,
    DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, string? Code = null, string? Message = null,
    DesignerContext? Context = null, string[]? CommittedFiles = null, bool? RemoteSynchronized = null,
    ReportSaveResult? ReportResult = null);
public sealed record BridgeRequest(int ProtocolVersion, string Method, JsonElement Arguments);
public sealed record BridgeResponse(bool Ok, JsonElement? Result, string? Code = null, string? Message = null)
{
    public static BridgeResponse Success<T>(T value) => new(true, Wire.Element(value));
    public static BridgeResponse Failure(string code, string message) => new(false, null, code, message);
}

public sealed class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// Issued only after a verified checkout returned and before the requested
// business mutation was dispatched. The verified checkout can retain locks.
public sealed class CheckoutBaselineException : Exception
{
    public string Code { get; }
    public CheckoutBaselineException(bool sourceChanged, string? owner = null) : this(
        sourceChanged ? "CheckoutSourceChanged" : "CheckoutOwnerChanged",
        sourceChanged ? "Project source changed during checkout." : $"Native owner changed during checkout: {owner}.") { }

    public CheckoutBaselineException(Exception readError) : this("CheckoutBaselineReadFailed",
        $"Post-checkout read failed ({(readError is BridgeException b ? b.Code : readError.GetType().Name)}).", readError) { }

    private CheckoutBaselineException(string code, string message, Exception? cause = null)
        : base(message + " Checkout was verified. The requested mutation was not dispatched. Read a fresh baseline.", cause) => Code = code;
}

public interface IDesignerBackend
{
    Task<DesignerContext> CaptureAsync();
    Task RefreshAsync(string sessionId);
    Task<string[]> GetModuleFilesAsync(string[] moduleNames, string sessionId);
    Task<bool> CheckoutAsync(string[] moduleNames, string beginCommitId, DesignerContext expected);
    Task<bool> CheckinAsync(string[] moduleNames, string message, DesignerContext expected);
    Task<string?> GetRemoteHeadAsync(GitState git);
    Task<string[]> GetCommittedFilesAsync(string oldHead, string newHead);
}
