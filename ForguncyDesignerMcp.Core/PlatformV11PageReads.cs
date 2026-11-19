using System.Text.Json;
namespace ForguncyDesignerMcp.Core.Platform;

internal static class PlatformV11PageReads
{
    private static readonly ModuleKind[] Kinds=[ModuleKind.Page,ModuleKind.MasterPage,ModuleKind.UserControlPage];
    internal static bool SupportsVersion(string version) => version is "11.0.3.0" or "11.0.104.0";
    internal static bool HasPageCapabilities(IReadOnlyList<ModuleCapability> caps) =>
        Kinds.All(k=>caps.Any(c=>c.Kind==k && c.Operation==ModuleOperation.List && c.Available) && caps.Any(c=>c.Kind==k && c.Operation==ModuleOperation.Read && c.Available));
    internal static PlatformApiDescriptor Descriptor(string method)
    {
        if(method=="getCells")return new("page",method,PlatformJson.Element(new{type="object",properties=new{pageName=new{type="string"},range=new{type="string"},startRow=new{type="integer"},startCol=new{type="integer"},endRow=new{type="integer"},endCol=new{type="integer"},row=new{type="integer"},col=new{type="integer"},rowCount=new{type="integer"},colCount=new{type="integer"}},required=new[]{"pageName"},additionalProperties=false}),PlatformJson.Element(new{type="object",properties=new{pageName=new{type="string"},cells=new{type="array",items=new{type="object"}},nativeMetadata=new{type="object",properties=new{source=new{type="string"},scope=new{type="string"},completeCellProjection=new{type="boolean"},fields=new{type="object"}},required=new[]{"source","scope","completeCellProjection","fields"}}},required=new[]{"pageName","cells","nativeMetadata"}}),false,Documentation:"V11 compatibility read: values/formulas are projected; unmapped whole-page native cell/style fields are returned with provenance, not interpreted as V12 style.");
        var input=method=="listPages" ? PlatformJson.Element(new{type="object",properties=new{type=new{type="string"}},additionalProperties=false}) :
            PlatformJson.Element(new{type="object",properties=new{pageName=new{type="string",minLength=1}},required=new[]{"pageName"},additionalProperties=false});
        var output=method=="listPages" ? PlatformJson.Element(new{type="array",items=new{type="object",properties=new{name=new{type="string"},type=new{type="string"},folderPath=new{type="string"},isMobilePage=new{type="boolean"}},required=new[]{"name","type"}}}) :
            PlatformJson.Element(new{type="object",properties=new{name=new{type="string"},type=new{type="string"},rowCount=new{type="integer"},columnCount=new{type="integer"},isMobilePage=new{type="boolean"},folderPath=new{type="string"},title=new{type="string"},masterPageName=new{type="string"}},required=new[]{"name","type"}});
        return new("page",method,input,output,false,Documentation:"V11 compatibility implementation using typed module list/read; not a native API.");
    }
    internal static async Task<JsonElement> InvokeAsync(IProjectAdapter adapter,ProjectTarget target,string method,JsonElement input,CancellationToken token)
    {
        if(input.ValueKind!=JsonValueKind.Object)throw new ArgumentException("Expected an object.");
        var properties=input.EnumerateObject().ToArray();var key=method=="listPages"?"type":"pageName";
        if(method!="getCells" && (properties.Length>1 || properties.Any(p=>p.Name!=key)))throw new ArgumentException("Unknown or duplicate page parameter.");
        var kinds=Kinds;
        if(method=="listPages" && properties.Length==1)
        {
            if(properties[0].Value.ValueKind!=JsonValueKind.String)throw new ArgumentException("Page type must be a string.");
            kinds=properties[0].Value.GetString() switch {""=>Kinds,"normal"=>[ModuleKind.Page],"master"=>[ModuleKind.MasterPage],"userControl"=>[ModuleKind.UserControlPage],_=>throw new ArgumentException("Unknown page type.")};
        }
        if(method is "getPageInfo" or "getCells" && (!input.TryGetProperty("pageName",out var pageName) || pageName.ValueKind!=JsonValueKind.String || string.IsNullOrWhiteSpace(pageName.GetString())))throw new ArgumentException("pageName is required.");
        var before=await adapter.GetSnapshotAsync(token);
        if(before.Target!=target || before.Busy)throw new InvalidOperationException("Target changed or Designer busy.");
        var addresses=new List<ModuleAddress>();
        foreach(var kind in kinds)
        {
            string? cursor=null;var seen=new HashSet<string>(StringComparer.Ordinal);var count=0;
            do {
                if(++count>1000)throw new InvalidOperationException("Page listing exceeds pagination bound.");
                var page=await adapter.ListAsync(target,new(kind,null,cursor,100),token);
                foreach(var item in page.Items){if(item.Address.Kind!=kind || addresses.Contains(item.Address))throw new InvalidOperationException("Inconsistent page listing.");addresses.Add(item.Address);}
                cursor=page.NextCursor;if(cursor is not null && !seen.Add(cursor))throw new InvalidOperationException("Repeated page cursor.");
            }while(cursor is not null);
        }
        JsonElement result;
        if(method is "getPageInfo" or "getCells")
        {
            var matches=addresses.Where(a=>a.Name==input.GetProperty("pageName").GetString()).ToArray();
            if(matches.Length!=1)throw new InvalidOperationException("Page name must resolve to exactly one page across folders and kinds.");
            var doc=await adapter.ReadAsync(target,matches[0],token);
            result=method=="getCells"?PlatformV11CellReads.Project(matches[0],doc,input):ProjectInfo(matches[0],doc);
        }
        else result=PlatformJson.Element(addresses.Select(a=>new{name=a.Name,type=Type(a.Kind),folderPath=a.FolderPath??""}).ToArray());
        var after=await adapter.GetSnapshotAsync(token);
        if(after.Target!=target || after.Busy || before.Revision!=after.Revision)throw new InvalidOperationException("Project changed during page read.");
        return result;
    }
    internal static JsonElement ProjectInfo(ModuleAddress address,ModuleDocument doc)
    {
        if(doc.Address!=address)throw new InvalidOperationException("Page response address differs.");
        var m=doc.Metadata;
        if(m.ValueKind!=JsonValueKind.Object || !m.TryGetProperty("Rows",out var rows) || rows.ValueKind!=JsonValueKind.Object ||
           !m.TryGetProperty("Cols",out var cols) || cols.ValueKind!=JsonValueKind.Object ||
           !rows.TryGetProperty("Count",out var r) || !cols.TryGetProperty("Count",out var c) ||
           r.ValueKind!=JsonValueKind.Number || c.ValueKind!=JsonValueKind.Number || !r.TryGetInt32(out var rn) || !c.TryGetInt32(out var cn) || rn<0 || cn<0)
            throw new InvalidOperationException("Page model lacks valid row/column counts.");
        return PlatformJson.Element(new{name=address.Name,type=Type(address.Kind),rowCount=rn,columnCount=cn,folderPath=address.FolderPath??""});
    }
    private static string Type(ModuleKind kind)=>kind switch{ModuleKind.Page=>"normal",ModuleKind.MasterPage=>"master",ModuleKind.UserControlPage=>"userControl",_=>throw new ArgumentException("Not a page.")};
}
