using System.Text.Json;
using System.Text.Json.Nodes;
namespace ForguncyDesignerMcp.Core.Platform;

/// <summary>Prepares a typed module update; never writes a file or calls an adapter.</summary>
internal static class PlatformV11CellWrites
{
    internal static PlatformApiDescriptor Descriptor()
    {
        var input=PlatformJson.Element(new{type="object",properties=new{pageName=new{type="string"},cells=new{type="array",minItems=1,maxItems=1000,items=new{type="object",properties=new{row=new{type="integer",minimum=0},col=new{type="integer",minimum=0},value=new{},formula=new{type=new[]{"string","null"}}},required=new[]{"row","col"},additionalProperties=false}}},required=new[]{"pageName","cells"},additionalProperties=false});
        return new("page","updateCells",PlatformApiSchemas.AddMutationGuard(input),PlatformApiSchemas.OperationStatus(PlatformApiSchemas.ModuleMutationReceipt()),true,Documentation:"V11 compatibility: batch plain values/formulas with native detached page projection and typed validated save. Other cell changes are unavailable.");
    }
    internal sealed record CellEdit(int Row,int Col,JsonElement? Value,JsonElement? Formula);
    internal static IReadOnlyList<CellEdit> ReadInput(JsonElement input,PlatformApiMutationGuard guard)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in input.EnumerateObject())
            if (field.Name is not ("pageName" or "cells" or "guard") || !fields.Add(field.Name))
                throw new BridgeException("InvalidCellRequest", field.Name + " is unsupported or duplicated.");
        if (!input.TryGetProperty("pageName", out var name) || name.ValueKind != JsonValueKind.String || guard.Address is null || name.GetString() != guard.Address.Name)
            throw new BridgeException("InvalidCellRequest", "pageName must match guard.address.name.");
        if (!input.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array || cells.GetArrayLength() is < 1 or > 1000)
            throw new BridgeException("InvalidCellRequest", "cells must be an array of 1..1000 entries.");
        var result = new List<CellEdit>();
        var seen = new HashSet<(int, int)>();
        foreach (var cell in cells.EnumerateArray())
        {
            var path = $"cells[{result.Count}]";
            if (cell.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidCellRequest", path + " must be a cell object.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in cell.EnumerateObject())
                if (p.Name is not ("row" or "col" or "value" or "formula") || !keys.Add(p.Name))
                    throw new BridgeException("InvalidCellRequest", $"{path}.{p.Name} is unsupported or duplicated.");
            int Coordinate(string key)
            {
                if (!cell.TryGetProperty(key, out var number) || number.ValueKind != JsonValueKind.Number ||
                    !number.TryGetInt32(out var coordinate) || coordinate < 0)
                    throw new BridgeException("InvalidCellRequest", $"{path}.{key} must be a nonnegative integer.");
                return coordinate;
            }
            var row = Coordinate("row");
            var col = Coordinate("col");
            var hasValue = cell.TryGetProperty("value", out var value);
            var hasFormula = cell.TryGetProperty("formula", out var formula);
            if (!hasValue && !hasFormula) throw new BridgeException("InvalidCellRequest", path + " requires value or formula.");
            if (hasFormula && formula.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new BridgeException("InvalidCellRequest", path + ".formula must be a string or null.");
            if (hasValue && value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new BridgeException("InvalidCellRequest", path + ".value must be a scalar or null.");
            if (!seen.Add((row, col))) throw new BridgeException("InvalidCellRequest", path + " repeats an earlier cell address.");
            result.Add(new(row, col, hasValue ? value.Clone() : null, hasFormula ? formula.Clone() : null));
        }
        return result;
    }
    internal static ModuleMutationRequest PrepareValueUpdate(PlatformApiMutationGuard guard,ModuleDocument document,IReadOnlyList<CellEdit> cells)
    {
        if(guard.Address is null || guard.Address!=document.Address || guard.Address.Kind!=ModuleKind.Page ||
           string.IsNullOrWhiteSpace(guard.OperationId) || string.IsNullOrWhiteSpace(guard.ExpectedProjectRevision) ||
           string.IsNullOrWhiteSpace(guard.ExpectedModuleRevision) || guard.ExpectedModuleRevision!=document.Revision || guard.Scope!=EffectScope.ProjectDefinition)
            throw new InvalidOperationException("Exact page and project/module baselines are required.");
        var info=PlatformV11PageReads.ProjectInfo(document.Address,document);
        var root=JsonNode.Parse(document.Metadata.GetRawText()) as JsonObject ?? throw new InvalidOperationException("Invalid page model.");
        // Controls or formula cells may have additional invariants; do not rewrite them as plain values.
        foreach(var property in root)
            if(property.Key is not ("Rows" or "Cols" or "PageInfo" or "PrintInfo" or "Values" or "DateValues" or "TimeValues" or "Formulas" or "ArrayFormulas" or "AttachInfos" or "Spans" or "Pictures" or "BackgroundPictures" or "Charts" or "CustomNames" or "CellTypes" or "CellTypeDatas" or "StyleDatas"))
                throw new NotSupportedException("Unknown native worksheet field: "+property.Key);
        var protectedRanges=new List<(int Row,int Col,int Rows,int Cols)>();
        if(root["Spans"] is { } spanNode)
        {
            if(spanNode is not JsonArray spans)throw new NotSupportedException("Unsupported native Spans representation.");
            foreach(var entry in spans)protectedRanges.Add(ParseNativeRange(entry?.GetValue<string>()));
        }
        if(root["ArrayFormulas"] is { } arrayNode)
        {
            if(arrayNode is not JsonObject arrays)throw new NotSupportedException("Unsupported native ArrayFormulas representation.");
            foreach(var entry in arrays)protectedRanges.Add(ParseNativeRange(entry.Key));
        }
        if(root["Values"] is not null and not JsonObject)throw new InvalidOperationException("Invalid Values map.");
        if(root["Formulas"] is not null and not JsonObject)throw new InvalidOperationException("Invalid Formulas map.");
        for (int index = 0; index < cells.Count; index++)
        {
            var (row, col, value, formula) = cells[index];
            if(value is { } scalar && scalar.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null))throw new NotSupportedException("Only scalar plain-cell values are supported.");
            if (row < 0 || row >= info.GetProperty("rowCount").GetInt32()) throw new BridgeException("InvalidCellRequest", $"cells[{index}].row is outside the page bounds.");
            if (col < 0 || col >= info.GetProperty("columnCount").GetInt32()) throw new BridgeException("InvalidCellRequest", $"cells[{index}].col is outside the page bounds.");
            if(protectedRanges.Any(r=>row>=r.Row && (long)row<(long)r.Row+r.Rows && col>=r.Col && (long)col<(long)r.Col+r.Cols))throw new NotSupportedException($"cells[{index}] intersects a merged or array-formula range.");
            var key=$"{row},{col}";
            foreach(var name in new[]{"DateValues","TimeValues","AttachInfos","CellTypes","CellTypeDatas"})
                if(root[name] is not null && (root[name] is not JsonObject map || map.ContainsKey(key)))throw new NotSupportedException($"cells[{index}] has native {name} metadata requiring an adapter-specific update.");
            if(value is { } v){if(v.ValueKind==JsonValueKind.Null)(root["Values"] as JsonObject)?.Remove(key);else {root["Values"] ??=new JsonObject();((JsonObject)root["Values"]!)[key]=JsonNode.Parse(v.GetRawText());}}
            if(formula is { } f){if(f.ValueKind==JsonValueKind.Null)(root["Formulas"] as JsonObject)?.Remove(key);else {root["Formulas"] ??=new JsonObject();((JsonObject)root["Formulas"]!)[key]=f.GetString();}}
        }
        var changed=document with {Metadata=PlatformJson.Element(root)};
        return new ModuleMutationRequest(guard.OperationId,guard.Target,ModuleOperation.Update,guard.Address,guard.ExpectedProjectRevision,
            guard.ExpectedModuleRevision,changed,true,guard.ExpectedUser,guard.Scope,guard.ExpectedServiceBinding);
    }
    private static (int Row,int Col,int Rows,int Cols) ParseNativeRange(string? text)
    {
        var parts=text?.Split(',');
        if(parts is not {Length:4} || parts.Any(p=>p.Length==0 || p.Any(ch=>ch<'0' || ch>'9')) ||
           !int.TryParse(parts[0],out var row) || !int.TryParse(parts[1],out var col) ||
           !int.TryParse(parts[2],out var rows) || !int.TryParse(parts[3],out var cols) || rows<1 || cols<1 ||
           (long)row+rows>int.MaxValue || (long)col+cols>int.MaxValue)
            throw new NotSupportedException("Unknown native merged/array range format.");
        return(row,col,rows,cols);
    }
}
