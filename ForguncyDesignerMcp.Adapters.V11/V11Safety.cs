using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal static class V11Safety
{
    private static readonly HashSet<string> DeviceNames = new(
        new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Concat(
            "123456789\u00B9\u00B2\u00B3".SelectMany(digit => new[] { "COM" + digit, "LPT" + digit })), StringComparer.OrdinalIgnoreCase);

    private static bool IsDeviceName(string name) => DeviceNames.Contains(name.Split('.')[0].TrimEnd(' '));

    internal static void Address(ModuleAddress address)
    {
        if (!Enum.IsDefined(address.Kind)) throw new BridgeException("InvalidModuleKind", "模块类型无效。");
        Segment(address.Name);
        if (!string.IsNullOrEmpty(address.FolderPath))
        {
            if (address.FolderPath.Contains('\\') || address.FolderPath.StartsWith('/') || address.FolderPath.EndsWith('/'))
                throw new BridgeException("InvalidModuleFolder", "目录必须为规范的相对模块目录。");
            foreach (var part in address.FolderPath.Split('/')) Segment(part);
        }
    }

    private static void Segment(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value != value.Trim() ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\') ||
            value.EndsWith('.') || value.Length > 120 || IsDeviceName(value))
            throw new BridgeException("InvalidModuleName", "模块名称或目录包含无效字符。");
    }

    internal static string Under(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(x => x is "." or ".." or "" || IsDeviceName(x)))
            throw new BridgeException("InvalidNativePath", "原生模块路径无效。");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("InvalidNativePath", "原生模块路径超出工程目录。");
        for (var current = new FileInfo(target) as FileSystemInfo; current is not null; current = current is FileInfo f ? f.Directory : ((DirectoryInfo)current).Parent)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new BridgeException("ReparsePointRejected", "模块路径包含重解析点。");
            if (current.FullName.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)) break;
        }
        return target;
    }

    internal static JsonElement Body(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith(new byte[] { 239, 187, 191 })) span = span[3..];
        var reader = new Utf8JsonReader(span);
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.Clone();
    }

    internal static bool Equivalent(JsonElement left, JsonElement right) =>
        JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));

    internal static void Companion(CompanionAsset asset, string expected)
    {
        if (asset.RelativePath != expected) throw new BridgeException("CompanionPathMismatch", "配套文件路径与原生模块不匹配。");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(asset.ContentBase64); }
        catch (FormatException) { throw new BridgeException("InvalidCompanion", "配套文件不是有效 Base64。"); }
        if (bytes.Length > 32 * 1024 * 1024 || NativeV11Session.Hash(bytes) != asset.Sha256)
            throw new BridgeException("InvalidCompanion", "配套文件大小或 SHA-256 校验不通过。");
    }

    internal static void SameTarget(ProjectTarget expected, ProjectTarget actual)
    {
        if (expected.InstanceId != actual.InstanceId || expected.SessionId != actual.SessionId ||
            expected.DesignerVersion != actual.DesignerVersion ||
            !Path.GetFullPath(expected.ProjectPath).Equals(Path.GetFullPath(actual.ProjectPath), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ProjectTargetChanged", "工程、设计器实例或文档会话已变化。");
    }
}
