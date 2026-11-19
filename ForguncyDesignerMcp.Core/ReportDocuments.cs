using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

namespace ForguncyDesignerMcp.Core;

public static class ReportDocuments
{
    // Smaller than the bridge frame, leaving room for escaped JSON and context.
    public const int MaximumPairBytes = 256 * 1024;
    private static readonly string[] ProtectedProperties =
        ["Name", "ID", "Id", "GUID", "Guid", "ReportType", "PermissionData", "Permission", "Permissions", "FolderPath", "Path", "$type"];

    public static string Revision(string json, string rdlx, string jsonFileHash, string rdlxFileHash) =>
        Wire.Hash(System.Text.Json.JsonSerializer.Serialize(new { json, rdlx, jsonFileHash, rdlxFileHash }, Wire.Json));

    public static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 240 || name != name.Trim() ||
            name.IndexOfAny(['/', '\\', ':', '\0', '<', '>', '"', '|', '?', '*']) >= 0 || name is "." or "..")
            throw new BridgeException("InvalidReportName", "请使用现有报表的确切名称，不能传文件路径。");
    }

    public static void ValidateUpdate(ReportSnapshot baseline, string json, string rdlx)
    {
        if (json is null || rdlx is null || Encoding.UTF8.GetByteCount(json) + Encoding.UTF8.GetByteCount(rdlx) > MaximumPairBytes)
            throw new BridgeException("ReportTooLarge", "报表 JSON 和 RDLX 合计不能超过 256 KiB。");
        using var original = ParseMetadata(baseline.Json);
        using var candidate = ParseMetadata(json);
        // Native GenerateObject uses TypeNameHandling.Auto. Retain only exact CLR
        // discriminators already present in this report; a new report type needs
        // a separately adapted contract, not a namespace or assembly wildcard.
        var knownTypes = new HashSet<string>(StringComparer.Ordinal);
        VisitClrTypes(original.RootElement, value => knownTypes.Add(value));
        VisitClrTypes(candidate.RootElement, value => {
            if (!knownTypes.Contains(value))
                throw new BridgeException("UnsupportedReportClrType", "不能引入原报表未使用的 .NET 类型；新增类型暂不在本版支持范围。");
        });
        foreach (var name in ProtectedProperties)
        {
            var oldExists = original.RootElement.TryGetProperty(name, out var oldValue);
            var newExists = candidate.RootElement.TryGetProperty(name, out var newValue);
            if (oldExists != newExists || (oldExists && !JsonEquivalent(oldValue.GetRawText(), newValue.GetRawText())))
                throw new BridgeException("ProtectedReportMetadata", "不能通过报表写入改变身份、类型、权限或目录字段：" + name);
        }
        var originalLayout = ParseRdlx(baseline.Rdlx);
        var candidateLayout = ParseRdlx(rdlx);
        var originalFixedPage = originalLayout.Descendants().Any(x => x.Name.LocalName == "FixedPage");
        var candidateFixedPage = candidateLayout.Descendants().Any(x => x.Name.LocalName == "FixedPage");
        var reportType = candidate.RootElement.TryGetProperty("ReportType", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number : candidate.RootElement.TryGetProperty("ReportType", out _) ? -1 : 0;
        if (originalLayout.Root!.Name.NamespaceName != candidateLayout.Root!.Name.NamespaceName ||
            originalFixedPage != candidateFixedPage || reportType is not (0 or 1) || candidateFixedPage != (reportType == 0))
            throw new BridgeException("ReportLayoutTypeMismatch", "RDLX 命名空间及 Page/RDL 布局必须与原报表和 ReportType 一致。");
    }

    public static bool JsonEquivalent(string left, string right) => JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));
    public static bool RdlxEquivalent(string left, string right) =>
        XNode.DeepEquals(CanonicalXml(ParseRdlx(left).Root!), CanonicalXml(ParseRdlx(right).Root!));

    private static XElement CanonicalXml(XElement element)
    {
        var result = new XElement(element.Name, element.Attributes().Where(x => !x.IsNamespaceDeclaration)
            .OrderBy(x => x.Name.ToString(), StringComparer.Ordinal).Select(x => new XAttribute(x.Name, x.Value)));
        if (!element.HasElements)
        {
            if (element.Value.Length != 0) result.Add(new XText(element.Value));
            return result;
        }
        var preserveSpace = element.AncestorsAndSelf().Select(x => (string?)x.Attribute(XNamespace.Xml + "space")).FirstOrDefault(x => x is not null) == "preserve";
        foreach (var node in element.Nodes())
            if (node is XElement child) result.Add(CanonicalXml(child));
            else if (node is XText text && (preserveSpace || !string.IsNullOrWhiteSpace(text.Value))) result.Add(new XText(text.Value));
        return result;
    }

    public static JsonDocument ParseMetadata(string json)
    {
        try
        {
            var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 96 });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                document.Dispose();
                throw new BridgeException("InvalidReportJson", "报表元数据必须是 JSON 对象。");
            }
            try { RejectDuplicateProperties(document.RootElement); }
            catch { document.Dispose(); throw; }
            return document;
        }
        catch (JsonException error) { throw new BridgeException("InvalidReportJson", "报表 JSON 无效：" + error.Message); }
    }

    public static XDocument ParseRdlx(string rdlx)
    {
        try
        {
            using var input = new StringReader(rdlx);
            using var reader = XmlReader.Create(input, new XmlReaderSettings {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaximumPairBytes, MaxCharactersFromEntities = 0
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            if (!string.IsNullOrWhiteSpace(document.Declaration?.Encoding) &&
                !string.Equals(document.Declaration.Encoding, "utf-8", StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("InvalidRdlxEncoding", "原生保存使用 UTF-8；RDLX 声明的编码必须为 UTF-8 或省略。");
            if (document.Root?.Name.LocalName != "Report" || string.IsNullOrWhiteSpace(document.Root.Name.NamespaceName))
                throw new BridgeException("InvalidRdlx", "RDLX 必须含带命名空间的 Report 根节点。");
            return document;
        }
        catch (XmlException error) { throw new BridgeException("InvalidRdlx", "RDLX XML 无效或含被禁止的 DTD：" + error.Message); }
    }

    private static void RejectDuplicateProperties(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            // The native JSON mapper resolves CLR members case-insensitively.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new BridgeException("InvalidReportJson", "报表 JSON 含重复字段：" + property.Name);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var value in node.EnumerateArray()) RejectDuplicateProperties(value);
    }

    private static void VisitClrTypes(JsonElement node, Action<string> visit)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in node.EnumerateObject())
            {
                if (string.Equals(property.Name, "$type", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Name != "$type" || property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString()))
                        throw new BridgeException("InvalidReportTypeMetadata", "JSON 类型标记必须使用精确的 $type 键及非空字符串值。");
                    visit(property.Value.GetString()!);
                }
                VisitClrTypes(property.Value, visit);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) VisitClrTypes(item, visit);
    }
}
