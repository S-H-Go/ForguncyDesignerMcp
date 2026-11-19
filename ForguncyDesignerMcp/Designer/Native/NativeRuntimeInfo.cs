using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Designer.Native;

internal sealed partial class NativeDesignerBackend
{
    internal async Task<DesignerRuntimeInfo> CaptureRuntimeInfoAsync(ProjectTarget expected) => await dispatcher.InvokeAsync(() =>
    {
        try
        {
            dispatcher.VerifyAccess();
            var before = ObserveDocument();
            if (platformInstanceId != expected.InstanceId || before.SessionId != expected.SessionId ||
                before.ProjectPath is null || !string.Equals(Path.GetFullPath(before.ProjectPath), Path.GetFullPath(expected.ProjectPath), StringComparison.OrdinalIgnoreCase) || DesignerVersion != expected.DesignerVersion)
                throw new BridgeException("TargetMismatch", "Runtime diagnostics require the exact current instance, document session, path and build.");
            using var process = Process.GetCurrentProcess();
            if (platformInstanceId != process.Id + "-" + process.StartTime.ToUniversalTime().Ticks)
                throw new BridgeException("RuntimeInfoUnavailable", "Process identity does not match the registered instance.");
            RuntimeFileEvidence FileInfo(string? path)
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new BridgeException("RuntimeInfoUnavailable", "A loaded executable or assembly has no verifiable path.");
                using var input = File.OpenRead(path);
                return new(Path.GetFullPath(path), Convert.ToHexString(SHA256.HashData(input)));
            }
            var settingsType = assembly.GetType("Forguncy.Properties.Settings", true)!;
            var flag = settingsType.GetProperty("EnableDesignerMCPServer", NativeAccess.Flags);
            var port = settingsType.GetProperty("DesignerMCPPort", NativeAccess.Flags);
            bool? enabled = null; int? configuredPort = null;
            if (DesignerVersion.StartsWith("12.", StringComparison.Ordinal))
            {
                if (flag?.PropertyType != typeof(bool) || port?.PropertyType != typeof(int)) throw new BridgeException("RuntimeInfoUnavailable", "Native MCP settings do not match this exact build.");
                var settings = settingsType.GetProperty("Default", NativeAccess.Flags)?.GetValue(null) ?? throw new BridgeException("RuntimeInfoUnavailable", "Native settings are unavailable.");
                enabled = (bool)flag.GetValue(settings)!; configuredPort = (int)port.GetValue(settings)!;
            }
            else if (flag is not null || port is not null) throw new BridgeException("RuntimeInfoUnavailable", "Unexpected native MCP feature in the V11 profile.");
            var designerFile = FileInfo(assembly.Location);
            if (!string.Equals(designerFile.Sha256, buildProfile.Sha256, StringComparison.OrdinalIgnoreCase)) throw new BridgeException("RuntimeInfoUnavailable", "Native Designer file changed since profile binding.");
            var result = new DesignerRuntimeInfo(process.Id, process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FileInfo(process.MainModule?.FileName), DesignerVersion, designerFile, FileInfo(typeof(NativeDesignerBackend).Assembly.Location),
                FileInfo(typeof(Wire).Assembly.Location), before.SessionId!, Path.GetFullPath(before.ProjectPath),
                Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS") is not null, Environment.GetEnvironmentVariable("FDMCP_ACCEPTANCE_CONFIG") is not null,
                flag is not null, enabled, configuredPort);
            var after = ObserveDocument();
            if (after != before) throw new BridgeException("TargetMismatch", "Document changed during runtime diagnostics.");
            return result;
        }
        catch (BridgeException) { throw; }
        catch (Exception) { throw new BridgeException("RuntimeInfoUnavailable", "Current runtime evidence could not be verified."); }
    });
}
