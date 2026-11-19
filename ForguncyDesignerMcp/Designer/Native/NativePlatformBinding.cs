using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Designer.Native;

internal sealed partial class NativeDesignerBackend
{
    private string? platformInstanceId;
    private Func<string, bool>? platformExecutionUncertain;

    internal void SetPlatformSafetyProbe(Func<string, bool> probe) => platformExecutionUncertain = probe;

    internal void SetInstanceIdentity(string instanceId)
    {
        dispatcher.VerifyAccess();
        if (platformInstanceId is not null && platformInstanceId != instanceId)
            throw new BridgeException("InstanceIdentityChanged", "同一桥接进程不能更换实例标识。");
        platformInstanceId = instanceId;
    }

    internal ProjectSnapshot CapturePlatformSnapshot()
    {
        dispatcher.VerifyAccess();
        var state = ReadNativeState();
        var context = state.Context;
        if (platformInstanceId is null) throw new BridgeException("InstanceNotReady", "桥接实例尚未登记。");
        return new(new(platformInstanceId, context.SessionId, context.ProjectPath, DesignerVersion),
            context.HasUnsavedChanges, context.Busy || HasUncertainNativeExecution(context.SessionId), context.InCollaborationMode, context.CurrentUser,
            Wire.Hash(state.Signature + "|" + nativeEditEpoch));
    }

    // Fixed read-only diagnostics for installation and native-service verification.
    // This never loads an assembly, starts a service or changes a user setting.
    internal async Task<JsonElement> CapturePlatformStatusAsync(ProjectTarget expected) =>
        await dispatcher.InvokeAsync(() =>
        {
            var snapshot = CapturePlatformSnapshot();
            if (snapshot.Target != expected)
                throw new BridgeException("TargetMismatch", "诊断请求不属于当前确切工程会话。");
            var settingsType = assembly.GetType("Forguncy.Properties.Settings", true)!;
            var enabledProperty = settingsType.GetProperty("EnableDesignerMCPServer", NativeAccess.Flags);
            var portProperty = settingsType.GetProperty("DesignerMCPPort", NativeAccess.Flags);
            bool? nativeMcpEnabled = null;
            int? nativeMcpPort = null;
            if (enabledProperty is not null)
            {
                if (enabledProperty.PropertyType != typeof(bool) || portProperty?.PropertyType != typeof(int))
                    throw new BridgeException("NativeMcpSettingsChanged", "原生 MCP 设置属性不匹配已适配构建。");
                var settings = settingsType.GetProperty("Default", NativeAccess.Flags)!.GetValue(null);
                nativeMcpEnabled = (bool)enabledProperty.GetValue(settings)!;
                nativeMcpPort = (int)portProperty.GetValue(settings)!;
            }
            var ownNames = new[] { "ForguncyDesignerMcp", "ForguncyDesignerMcp.Core", "ForguncyDesignerMcp.Adapters.V11", "ForguncyDesignerMcp.Adapters.V12" };
            var loaded = AppDomain.CurrentDomain.GetAssemblies();
            var own = loaded.Where(item => ownNames.Contains(item.GetName().Name, StringComparer.Ordinal))
                .Select(item =>
                {
                    using var input = File.OpenRead(item.Location);
                    return new { name = item.GetName().Name, version = item.GetName().Version?.ToString(),
                        path = item.Location, sha256 = Convert.ToHexString(SHA256.HashData(input)) };
                }).OrderBy(item => item.name, StringComparer.Ordinal).ThenBy(item => item.path, StringComparer.Ordinal).ToArray();
            var loadedTestInstrumentation = loaded.Select(item => item.GetName().Name ?? "")
                .Where(name => name.StartsWith("ForguncyDesignerMcp.LiveAcceptance", StringComparison.Ordinal) ||
                    name is "PlatformConformance" or "PlatformConformance.NativeObserver")
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return PlatformJson.Element(new { schemaVersion = 1, target = snapshot.Target,
                pluginVersion = Wire.PluginVersion, designerSha256 = buildProfile.Sha256,
                nativeMcpFeaturePresent = enabledProperty is not null, nativeMcpEnabled, nativeMcpPort,
                ownAssemblies = own, loadedTestInstrumentation, observedAtUtc = DateTimeOffset.UtcNow });
        });

    internal async Task<IProjectAdapter> CreatePlatformAdapterAsync(ProjectTarget expected) =>
        await dispatcher.InvokeAsync(() =>
        {
            var actual = CapturePlatformSnapshot();
            if (actual.Target.InstanceId != expected.InstanceId || actual.Target.SessionId != expected.SessionId ||
                actual.Target.DesignerVersion != expected.DesignerVersion ||
                !string.Equals(Path.GetFullPath(actual.Target.ProjectPath), Path.GetFullPath(expected.ProjectPath), StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("TargetMismatch", "平台适配器的目标实例、会话、路径或构建不一致。");
            var (document, _) = RequireSession(expected.SessionId);
            var identity = new AdapterIdentity(buildProfile.Version, buildProfile.Sha256, buildProfile.Name);
            var simpleName = buildProfile.Version.StartsWith("11.", StringComparison.Ordinal)
                ? "ForguncyDesignerMcp.Adapters.V11" : "ForguncyDesignerMcp.Adapters.V12";
            var candidates = AppDomain.CurrentDomain.GetAssemblies().Where(a => a.GetName().Name == simpleName).ToArray();
            if (candidates.Length == 0)
            {
                var folder = Path.GetDirectoryName(typeof(NativeDesignerBackend).Assembly.Location)!;
                var file = Path.Combine(folder, simpleName + ".dll");
                if (!File.Exists(file)) throw new BridgeException("AdapterNotInstalled", "缺少匹配构建的平台适配器：" + simpleName);
                var loadContext = AssemblyLoadContext.GetLoadContext(typeof(IProjectAdapterFactory).Assembly) ?? AssemblyLoadContext.Default;
                candidates = [loadContext.LoadFromAssemblyPath(file)];
            }
            var factories = candidates.SelectMany(a => a.GetTypes())
                .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IProjectAdapterFactory).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
                .Select(t => (IProjectAdapterFactory)Activator.CreateInstance(t)!).Where(f => f.Supports(identity)).ToArray();
            if (factories.Length != 1) throw new BridgeException("AdapterNotUnique", "未找到唯一匹配精确组件指纹的平台适配器。");
            var designerBin = Path.GetDirectoryName(assembly.Location)!;
            var designerRoot = Path.GetDirectoryName(Path.GetDirectoryName(designerBin)!)!;
            return factories[0].Create(new(assembly, document, new WpfUiScheduler(dispatcher), CapturePlatformSnapshot, designerRoot)
            {
                CollaborationReceiveRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ForguncyDesignerMcp", "ReceivedProjects")
            });
        });
}
