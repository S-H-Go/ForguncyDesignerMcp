using System.Security.Cryptography;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

public sealed partial class PlatformProjectRouter
{
    private sealed class PluginEvidenceException(string code, JsonElement? evidence, bool mayContinue) : Exception
    {
        internal string Code { get; } = code;
        internal JsonElement? Evidence { get; } = evidence;
        internal bool MayContinue { get; } = mayContinue;
    }

    private Task<JsonElement> InvokePluginAsync(PlatformApiRequest request, CancellationToken token)
    {
        if (adapter is not IProjectPluginLifecycleAdapter native)
            throw new BridgeException("PluginLifecycleUnavailable", "This exact adapter has no native plugin lifecycle implementation.");
        if (request.Method is "list" or "read") return SerializedAsync(async () =>
        {
            var before = await SnapshotAsync(request.Target, token);
            var allowed = request.Method == "read" ? new[] { "pluginGuid" } : [];
            if (request.Parameters.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal)) ||
                request.Parameters.EnumerateObject().GroupBy(p => p.Name).Any(g => g.Count() != 1))
                throw new BridgeException("InvalidArguments", "Unexpected plugin query properties.");
            var rows = await native.ListInstalledPluginsAsync(request.Target, token);
            ValidatePluginRows(rows);
            if (await SnapshotAsync(request.Target, token) != before)
                throw new BridgeException("ContextChangedDuringRead", "The native project changed while reading plugin registrations.");
            if (request.Method == "list") return PlatformJson.Element(rows);
            if (!Guid.TryParse(RequiredString(request.Parameters, "pluginGuid"), out var id))
                throw new BridgeException("InvalidPluginIdentity", "A native plugin GUID is required.");
            var match = rows.Where(row => Guid.Parse(row.PluginGuid) == id).ToArray();
            if (match.Length != 1) throw new BridgeException("PluginNotFound", "The installed plugin GUID does not resolve to one native registration.");
            return PlatformJson.Element(match[0]);
        }, token);
        if (request.Method is not ("create" or "update" or "delete")) throw new BridgeException("UnknownApi", "Unknown plugin lifecycle method.");
        var mutation = PlatformJson.Read<PluginLifecycleRequest>(request.Parameters);
        CheckTarget(mutation.Target); CheckOperationId(request.OperationId, mutation.OperationId);
        ValidatePluginRequest(mutation);
        if (!string.Equals(mutation.Operation.ToString(), request.Method, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("OperationMismatch", "The plugin method and requested operation differ.");
        return Task.FromResult(Submit(request, mutation.OperationId, EffectScope.RuntimeDeployment, async started =>
        {
            var before = await CheckPluginContext(mutation);
            await native.ValidatePluginLifecycleAsync(mutation, default);
            if (await CheckPluginContext(mutation) != before) throw new BridgeException("BaselineChanged", "Plugin preflight changed the reviewed context.");
            var original = await native.ListInstalledPluginsAsync(mutation.Target, default);
            ValidatePluginRows(original);
            var id = Guid.Parse(mutation.PluginGuid);
            var installed = original.SingleOrDefault(row => Guid.Parse(row.PluginGuid) == id);
            if (mutation.Operation == ModuleOperation.Create ? installed is not null : installed?.Version != mutation.ExpectedInstalledVersion)
                throw new BridgeException("PluginVersionChanged", "The installed plugin version differs from the reviewed baseline.");
            await CheckPluginContext(mutation);
            started();
            PluginLifecycleReceipt receipt;
            try { receipt = await native.InvokePluginLifecycleAsync(mutation, default); }
            catch { throw new PluginEvidenceException("PluginOutcomeUnconfirmed", null, true); }
            var evidence = PlatformJson.Element(receipt);
            if (receipt is null || receipt.OperationId != mutation.OperationId)
                throw new PluginEvidenceException("PluginReceiptIdMismatch", evidence, receipt?.ExecutionMayContinue ?? true);
            if (receipt.NativeAccepted is null || receipt.ExecutionMayContinue || receipt.State == "verification_failed")
                throw new PluginEvidenceException("PluginOutcomeUnconfirmed", evidence, receipt.ExecutionMayContinue);
            IReadOnlyList<PluginInstallationInfo> afterRows;
            ProjectSnapshot after;
            try { afterRows = await native.ListInstalledPluginsAsync(mutation.Target, default); ValidatePluginRows(afterRows); after = await SnapshotAsync(mutation.Target, default); }
            catch { throw new PluginEvidenceException("PluginOutcomeUnconfirmed", evidence, false); }
            if (after.CurrentUser != before.CurrentUser || after.Busy || after.InCollaborationMode != before.InCollaborationMode)
                throw new PluginEvidenceException("PluginOutcomeUnconfirmed", evidence, false);
            var unrelatedBefore = original.Where(row => Guid.Parse(row.PluginGuid) != id).OrderBy(row => Guid.Parse(row.PluginGuid)).ToArray();
            var unrelatedAfter = afterRows.Where(row => Guid.Parse(row.PluginGuid) != id).OrderBy(row => Guid.Parse(row.PluginGuid)).ToArray();
            if (!unrelatedBefore.SequenceEqual(unrelatedAfter)) throw new PluginEvidenceException("PluginOutcomeUnconfirmed", evidence, false);
            if (receipt.NativeAccepted == false)
            {
                if (receipt.State != "failed" || !original.OrderBy(row => row.PluginGuid).SequenceEqual(afterRows.OrderBy(row => row.PluginGuid)) || after != before)
                    throw new PluginEvidenceException("PluginOutcomeUnconfirmed", evidence, false);
                return evidence;
            }
            var observed = afterRows.SingleOrDefault(row => Guid.Parse(row.PluginGuid) == id);
            var correctTarget = mutation.Operation == ModuleOperation.Delete ? observed is null && receipt.ObservedVersion is null :
                observed?.Version == mutation.PackageVersion && receipt.ObservedVersion == mutation.PackageVersion;
            var correctState = receipt.RestartRequired ? receipt.State == "restart_required" && !receipt.RuntimeActivationVerified :
                receipt.State == "succeeded" && receipt.RuntimeActivationVerified ||
                receipt.State == "native_accepted" && !receipt.RuntimeActivationVerified && !string.IsNullOrWhiteSpace(receipt.Limitation);
            if (!receipt.RegistryVerified || !correctTarget || !correctState || !Guid.TryParse(receipt.ObservedGuid, out var observedId) || observedId != id ||
                !receipt.RuntimeActivationVerified && string.IsNullOrWhiteSpace(receipt.Limitation))
                throw new PluginEvidenceException("PluginOutcomeUnconfirmed", evidence, false);
            return evidence;
        }));
    }

    private async Task<ProjectSnapshot> CheckPluginContext(PluginLifecycleRequest request)
    {
        var snapshot = await SnapshotAsync(request.Target, default);
        if (snapshot.Busy || snapshot.HasUnsavedChanges || snapshot.Revision != request.ExpectedProjectRevision || snapshot.CurrentUser != request.ExpectedUser)
            throw new BridgeException("PluginBaselineChanged", "Plugin installation requires the reviewed clean idle document and unchanged native user.");
        return snapshot;
    }
    private static void ValidatePluginRows(IReadOnlyList<PluginInstallationInfo> rows)
    {
        if (rows is null || rows.Count > 10000 || rows.Any(row => row is null || !Guid.TryParse(row.PluginGuid, out _) ||
            !Version.TryParse(row.Version, out _) || string.IsNullOrWhiteSpace(row.Name)) || rows.Select(row => Guid.Parse(row.PluginGuid)).Distinct().Count() != rows.Count)
            throw new BridgeException("PluginRegistryUnverified", "Native installed plugin identities and versions must be complete and unique.");
    }
    private static void ValidatePluginRequest(PluginLifecycleRequest request)
    {
        if (request.Operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) ||
            !Guid.TryParseExact(request.PluginGuid, "D", out _) || string.IsNullOrWhiteSpace(request.ExpectedProjectRevision))
            throw new BridgeException("InvalidPluginArguments", "Plugin mutations require an exact GUID, revision and Create/Update/Delete operation.");
        if (request.Operation == ModuleOperation.Create ? request.ExpectedInstalledVersion is not null : !Version.TryParse(request.ExpectedInstalledVersion, out _))
            throw new BridgeException("PluginVersionRequired", "Update and delete require the exact installed version; create requires absence.");
        if (request.Operation == ModuleOperation.Delete)
        {
            if (request.PackageBase64 is not null || request.PackageSha256 is not null || request.PackageVersion is not null)
                throw new BridgeException("UnexpectedPluginPackage", "Deletion has no incoming package.");
            return;
        }
        if (!Version.TryParse(request.PackageVersion, out _) || request.PackageBase64 is null || request.PackageBase64.Length > 90 * 1024 * 1024 ||
            request.PackageSha256 is not { Length: 64 } || !request.PackageSha256.All(Uri.IsHexDigit))
            throw new BridgeException("PluginPackageRequired", "A bounded complete package, version and SHA-256 are required.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(request.PackageBase64); }
        catch (FormatException) { throw new BridgeException("InvalidPluginPackage", "Plugin package must use valid base64."); }
        if (bytes.Length is < 1 or > 64 * 1024 * 1024 || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(request.PackageSha256, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("PluginPackageHashMismatch", "Plugin package bytes differ from the reviewed SHA-256.");
    }
}
