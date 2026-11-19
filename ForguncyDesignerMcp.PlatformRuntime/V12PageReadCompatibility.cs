using System.Text.Json;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.PlatformRuntime;

/// <summary>Prepares an exact typed read only. Response conversion requires a version-specific page model projector.</summary>
public static class V12PageReadCompatibility
{
    private static readonly HashSet<string> Reads = new(StringComparer.Ordinal)
        { "getPageInfo", "getPageLayout", "getPageLoadCommands", "getPagePermission", "listCellNames" };

    public sealed record PreparedRead(string NativeMethod, ModuleAddress Address, bool RequiresReferences);

    /// <summary>Projects only fields supported by the captured V11 native page model; absent dimensions are an error.</summary>
    public static JsonElement ProjectPageInfo(PreparedRead request, ModuleDocument document)
    {
        if (request.NativeMethod != "getPageInfo" || request.RequiresReferences)
            throw new NotSupportedException("A verified response projector is not available for this method.");
        if (document.Address != request.Address)
            throw new InvalidOperationException("Typed page response address differs from the resolved request.");
        var metadata = document.Metadata;
        // V12 module.read already returns the native PageInfoDto, not a serialized worksheet.
        if (metadata.ValueKind == JsonValueKind.Object && metadata.TryGetProperty("name", out var nativeName))
        {
            if (nativeName.ValueKind != JsonValueKind.String || nativeName.GetString() != request.Address.Name ||
                !metadata.TryGetProperty("type", out var nativeType) || nativeType.ValueKind != JsonValueKind.String ||
                nativeType.GetString() != (request.Address.Kind switch { ModuleKind.Page => "normal", ModuleKind.MasterPage => "master", ModuleKind.UserControlPage => "userControl", _ => "" }))
                throw new InvalidOperationException("Native page information does not match the resolved address.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in metadata.EnumerateObject())
            {
                if(!seen.Add(property.Name)) throw new InvalidOperationException("Duplicate native page information field: "+property.Name);
                var valid=property.Name switch {
                    "name" or "type" or "folderPath" or "title" or "masterPageName" => property.Value.ValueKind==JsonValueKind.String,
                    "rowCount" or "columnCount" => property.Value.ValueKind==JsonValueKind.Number && property.Value.TryGetInt32(out var n) && n>=0,
                    "isMobilePage" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    _ => false };
                if(!valid) throw new InvalidOperationException("Native page information has an unsupported field or type: "+property.Name);
            }
            if(metadata.TryGetProperty("folderPath",out var folder) && folder.GetString() != (request.Address.FolderPath ?? ""))
                throw new InvalidOperationException("Native page folder differs from the resolved address.");
            return metadata.Clone();
        }
        if (metadata.ValueKind != JsonValueKind.Object || !metadata.TryGetProperty("Rows", out var rows) ||
            !metadata.TryGetProperty("Cols", out var cols) || rows.ValueKind != JsonValueKind.Object || cols.ValueKind != JsonValueKind.Object ||
            !rows.TryGetProperty("Count", out var rowCount) || !cols.TryGetProperty("Count", out var columnCount) ||
            !rowCount.TryGetInt32(out var r) || !columnCount.TryGetInt32(out var c) || r < 0 || c < 0)
            throw new InvalidOperationException("Page model does not provide verified row and column counts.");
        var type = request.Address.Kind switch { ModuleKind.Page => "normal", ModuleKind.MasterPage => "master", ModuleKind.UserControlPage => "userControl", _ => throw new NotSupportedException("Not a page kind.") };
        return PlatformJson.Element(new { name = request.Address.Name, type, rowCount = r, columnCount = c,
            folderPath = request.Address.FolderPath ?? "" });
    }

    public static PreparedRead Prepare(string method, JsonElement input, IReadOnlyList<ModuleAddress> resolvedExactMatches)
    {
        if (!Reads.Contains(method) && method != "findPageReferences")
            throw new NotSupportedException("No verified V12 page read mapping: " + method);
        if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Count() != 1 ||
            !input.TryGetProperty("pageName", out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()))
            throw new ArgumentException("Exactly one nonblank pageName is required; unsupported fields are not discarded.");
        var pageName = name.GetString()!;
        var matches = resolvedExactMatches.Where(a => a.Name == pageName && a.Kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("Page name must resolve to exactly one matching page across all folders and page kinds.");
        return new PreparedRead(method, matches[0], method == "findPageReferences");
    }
}
