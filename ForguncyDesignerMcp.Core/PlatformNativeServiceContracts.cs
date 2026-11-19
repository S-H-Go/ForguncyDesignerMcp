using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

// A native response is separate from the stronger typed account persistence
// and project export receipts. Never include credentials or secrets in it.
public sealed record NativeServiceApiReceipt(string OperationId, string State, string Code, string Message,
    bool? NativeAccepted, bool NativeReadbackVerified, JsonElement? NativeResponse,
    bool PersistenceVerified, bool SourceExportVerified, bool ExecutionMayContinue, string? Limitation);

public interface IProjectNativeServiceApiAdapter
{
    // Nonmutating DTO validation before the Host marks native work as started.
    // Implementations repeat their target/baseline checks before actual dispatch.
    Task ValidateNativeServiceApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    Task<NativeServiceApiReceipt> InvokeNativeServiceApiAsync(PlatformApiRequest request, CancellationToken cancellationToken = default);
}
