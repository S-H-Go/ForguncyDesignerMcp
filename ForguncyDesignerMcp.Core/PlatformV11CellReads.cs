using System.Text.Json;
using System.Text.RegularExpressions;
namespace ForguncyDesignerMcp.Core.Platform;

internal static class PlatformV11CellReads
{
    internal static JsonElement Project(ModuleAddress address,ModuleDocument document,JsonElement input)
    {
        if(document.Address!=address)throw new InvalidOperationException("Page address mismatch.");
        var metadata=document.Metadata;
        var info=PlatformV11PageReads.ProjectInfo(address,document);
        if(metadata.EnumerateObject().GroupBy(p=>p.Name,StringComparer.Ordinal).Any(g=>g.Count()!=1))throw new InvalidOperationException("Duplicate native worksheet field.");
        var range=ParseRange(input,info.GetProperty("rowCount").GetInt32(),info.GetProperty("columnCount").GetInt32());
        // Preserve unmapped native worksheet fields once per response; they are not
        // silently re-labelled as V12 cell styles or assigned guessed coordinates.
        var nativeFields=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach(var property in metadata.EnumerateObject())
        {
            if(property.Name is "Rows" or "Cols" or "PrintInfo" or "Values" or "Formulas")continue;
            if(!nativeFields.TryAdd(property.Name,property.Value.Clone()))throw new InvalidOperationException("Duplicate native worksheet field.");
        }
        if(nativeFields.Values.Sum(v=>(long)v.GetRawText().Length)>1_000_000)throw new InvalidOperationException("Unmapped native worksheet metadata exceeds the read budget; request a typed module read.");
        JsonElement values=default,formulas=default;
        if(metadata.TryGetProperty("Values",out values) && values.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Invalid native Values.");
        if(metadata.TryGetProperty("Formulas",out formulas) && formulas.ValueKind!=JsonValueKind.Object)throw new InvalidOperationException("Invalid native Formulas.");
        var cells=new List<Dictionary<string,object?>>();
        for(var row=range.StartRow;row<=range.EndRow;row++)for(var col=range.StartCol;col<=range.EndCol;col++)
        {
            var key=$"{row},{col}";var item=new Dictionary<string,object?>{{"row",row},{"col",col}};
            if(values.ValueKind==JsonValueKind.Object && values.TryGetProperty(key,out var value))item["value"]=value.Clone();
            if(formulas.ValueKind==JsonValueKind.Object && formulas.TryGetProperty(key,out var formula))
            {
                if(formula.ValueKind!=JsonValueKind.String)throw new NotSupportedException("Unsupported native formula representation.");
                var text=formula.GetString()!;item["formula"]=text.StartsWith('=')?text:"="+text;
            }
            cells.Add(item);
        }
        return PlatformJson.Element(new{pageName=address.Name,cells,nativeMetadata=new{source="V11.ModuleDocument.metadata",scope="whole-page-unmapped-fields",completeCellProjection=false,fields=nativeFields}});
    }
    internal static bool IsSheetStyleOnly(JsonElement style)=>style.ValueKind==JsonValueKind.Object &&
        style.EnumerateObject().All(p=>p.Name is "Types" or "SheetStyle") &&
        style.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==style.EnumerateObject().Count();
    internal static (int StartRow,int StartCol,int EndRow,int EndCol) ParseRange(JsonElement input,int rows,int cols)
    {
        var keys=new[]{"range","startRow","startCol","endRow","endCol","row","col","rowCount","colCount"};
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var p in input.EnumerateObject())if(!seen.Add(p.Name)||p.Name!="pageName" && !keys.Contains(p.Name,StringComparer.Ordinal))throw new ArgumentException("Unknown or duplicate getCells parameter.");
        int Number(string n,int? fallback=null){if(!input.TryGetProperty(n,out var v)){if(fallback.HasValue)return fallback.Value;throw new ArgumentException("Missing "+n);}if(v.ValueKind!=JsonValueKind.Number || !v.TryGetInt32(out var x))throw new ArgumentException("Invalid "+n);return x;}
        var hasRange=input.TryGetProperty("range",out var range);var hasBounds=keys.Skip(1).Take(4).Any(seen.Contains);var hasSize=keys.Skip(5).Any(seen.Contains);
        if((hasRange?1:0)+(hasBounds?1:0)+(hasSize?1:0)!=1)throw new ArgumentException("Specify exactly one cell range form.");
        int r,c,er,ec;
        if(hasRange)
        {
            if(range.ValueKind!=JsonValueKind.String)throw new ArgumentException("Invalid A1 range.");
            var match=Regex.Match(range.GetString()!,"^([A-Za-z]+)([1-9][0-9]*)(?::([A-Za-z]+)([1-9][0-9]*))?$",RegexOptions.CultureInvariant);
            if(!match.Success)throw new ArgumentException("Invalid A1 range.");
            int Column(string text){var v=0;checked{foreach(char ch in text.ToUpperInvariant()){v=v*26+ch-'A'+1;}}return v-1;}
            r=checked(int.Parse(match.Groups[2].Value)-1);c=Column(match.Groups[1].Value);
            er=match.Groups[3].Success?checked(int.Parse(match.Groups[4].Value)-1):r;ec=match.Groups[3].Success?Column(match.Groups[3].Value):c;
        }
        else if(hasBounds){r=Number("startRow");c=Number("startCol");er=Number("endRow");ec=Number("endCol");}
        else {r=Number("row");c=Number("col");var height=Number("rowCount",1);var width=Number("colCount",1);if(height<1||width<1)throw new ArgumentException("Cell range dimensions must be positive.");er=checked(r+height-1);ec=checked(c+width-1);}
        if(r<0||c<0||er<r||ec<c||er>=rows||ec>=cols||((long)er-r+1)*((long)ec-c+1)>10000)throw new ArgumentException("Cell range is outside page or exceeds 10000 cells.");
        return (r,c,er,ec);
    }
}
