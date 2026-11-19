namespace ForguncyDesignerMcp.Core;

// Independent wire DTO; never includes account identities or environment values.
public sealed record RuntimeFileEvidence(string Path, string Sha256);
public sealed record DesignerRuntimeInfo(int ProcessId, string ProcessStartTicks, RuntimeFileEvidence Executable,
    string DesignerVersion, RuntimeFileEvidence DesignerAssembly, RuntimeFileEvidence PluginAssembly,
    RuntimeFileEvidence CoreAssembly, string SessionId, string ProjectPath,
    bool DotnetStartupHooksPresent, bool AcceptanceConfigPresent,
    bool NativeMcpFeaturePresent, bool? NativeMcpEnabled, int? NativeMcpPort);

public static class DesignerRuntimeInfoSchema
{
    public static object Output()
    {
        var file = new { type = "object", properties = new { path = new { type = "string" }, sha256 = new { type = "string" } }, required = new[] { "path", "sha256" }, additionalProperties = false };
        return new { type = "object", properties = new Dictionary<string, object> {
            ["processId"] = new { type = "integer" }, ["processStartTicks"] = new { type = "string" }, ["executable"] = file,
            ["designerVersion"] = new { type = "string" }, ["designerAssembly"] = file, ["pluginAssembly"] = file, ["coreAssembly"] = file,
            ["sessionId"] = new { type = "string" }, ["projectPath"] = new { type = "string" },
            ["dotnetStartupHooksPresent"] = new { type = "boolean" }, ["acceptanceConfigPresent"] = new { type = "boolean" },
            ["nativeMcpFeaturePresent"] = new { type = "boolean" }, ["nativeMcpEnabled"] = new { type = new[] { "boolean", "null" } },
            ["nativeMcpPort"] = new { type = new[] { "integer", "null" } }
        }, required = new[] { "processId", "processStartTicks", "executable", "designerVersion", "designerAssembly", "pluginAssembly", "coreAssembly", "sessionId", "projectPath", "dotnetStartupHooksPresent", "acceptanceConfigPresent", "nativeMcpFeaturePresent", "nativeMcpEnabled", "nativeMcpPort" }, additionalProperties = false };
    }
}
