using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// This narrow path edits the existing command object; it never reconstructs
// opaque siblings, plugin commands, nested branch IDs or return bindings.
internal static class NativePageLoadPatch
{
    internal sealed record Plan(object Page, object Command, string OldScript, string NewScript, string PageName, string ExpectedJson);
    internal static Plan Prepare(Assembly assembly, object settings, JsonElement input)
    {
        var name=input.EnumerateObject().Single(p=>p.Name.Equals("pageName",StringComparison.OrdinalIgnoreCase)).Value.GetString();
        var commands=input.EnumerateObject().Single(p=>p.Name.Equals("commands",StringComparison.OrdinalIgnoreCase)).Value;
        if(string.IsNullOrWhiteSpace(name) || commands.ValueKind!=JsonValueKind.Array) throw Error("commands", "page name and command array are required");
        var page=R.Call(R.Get(settings,"Pages")!,"TryGetPage",name) ?? throw Error("pageName","native page not found");
        if(page.GetType().FullName!="Forguncy.Model.Pages.NormalPage") throw Error("pageName","only normal pages have a verified native save colleague");
        var support=R.Static(assembly.GetType("Forguncy.AI.Actions.PageSandboxAIActions",true)!,"GetPageLoadCommandSupportOrThrow",name) ?? throw Error("pageName","native load-command owner not found");
        var list=R.Get(support,"PageLoadedCommandList") ?? throw Error("pageName","page has no native load command list");
        var state=R.Static(assembly.GetType("s",true)!,"get_Instance")!;
        var workbook=R.Get(state,"ev") ?? throw Error("pageName","native workbook unavailable");
        var sheet=R.Call(R.Get(workbook,"Sheets")!,"get_Item",name) ?? throw Error("pageName","native worksheet unavailable");
        var sheetPage=R.Get(R.Get(sheet,"Tag")!,"PageInfo") ?? throw Error("pageName","native worksheet page model unavailable");
        if(!ReferenceEquals(R.Get(sheetPage,"PageLoadedCommandList"),list)) throw Error("pageName","native page and worksheet load-command owners differ; source persistence cannot be proven");
        var helper=assembly.GetType("Forguncy.AI.Actions.CommandTreeHelper",true)!;
        var context=R.Static(assembly.GetType("aaw",true)!,"ForPage",name)!;
        var serialized=R.Static(helper,"SerializeCommands",list,context)!;
        var json=(string)R.Static(Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert",true)!,"SerializeObject",serialized)!;
        var original=JsonNode.Parse(json) ?? throw Error("commands","native serialization returned no tree");
        var requested=JsonNode.Parse(commands.GetRawText()) ?? throw Error("commands","invalid tree");
        var candidates=new List<(string Id,string Script,string Path)>();
        static string? StringValue(JsonNode? node)=>node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        void Visit(JsonNode? node,string path)
        {
            if(node is JsonObject obj)
            {
                if(StringValue(obj["type"])=="RunJavaScript" && StringValue(obj["id"]) is {Length:>0} id && obj["props"] is JsonObject props && props["javaScript"] is JsonValue v && v.TryGetValue<string>(out var script)) candidates.Add((id,script,path));
                if(obj["commands"] is JsonArray children) Visit(children,path+".commands");
                if(obj["branches"] is JsonArray branches) for(var i=0;i<branches.Count;i++) if(branches[i] is JsonObject branch && branch["commands"] is JsonArray nested) Visit(nested,path+".branches["+i+"].commands");
            }
            else if(node is JsonArray arr) for(var i=0;i<arr.Count;i++) Visit(arr[i],path+"["+i+"]");
        }
        Visit(requested,"commands");
        var matches=new List<Plan>();
        string? unsupportedPath=null;
        foreach(var candidate in candidates)
        {
            var found=R.Static(helper,"FindCommandByNodeId",list,candidate.Id);
            if(found is null) continue;
            var tuple=found.GetType(); var container=tuple.GetField("Item1")?.GetValue(found) as IList; var index=tuple.GetField("Item2")?.GetValue(found) as int?;
            if(container is null || index is null || index<0 || index>=container.Count || container[index.Value] is not { } command || command.GetType().FullName!="Forguncy.Model.RunJavaScriptCommand") continue;
            var old=R.Get(command,"JavaScript") as string ?? ""; if(old==candidate.Script) continue;
            var changed=original.DeepClone();
            var originals=new List<JsonObject>();
            void Locate(JsonNode? node) { if(node is JsonObject o) { if(StringValue(o["id"])==candidate.Id && StringValue(o["type"])=="RunJavaScript") originals.Add(o); if(o["commands"] is JsonArray children) Locate(children); if(o["branches"] is JsonArray branches) foreach(var branch in branches) if(branch is JsonObject b && b["commands"] is JsonArray nested) Locate(nested); } else if(node is JsonArray a) foreach(var x in a) Locate(x); }
            Locate(changed);
            if(originals.Count!=1 || originals[0]["props"] is not JsonObject originalProps || StringValue(originalProps["javaScript"])!=old) continue;
            originalProps["javaScript"]=candidate.Script;
            if(JsonNode.DeepEquals(changed,requested)) matches.Add(new(page,command,old,candidate.Script,name,requested.ToJsonString()));
            else unsupportedPath ??= FirstDifference(changed,requested,"commands");
        }
        if(matches.Count!=1) throw Error(unsupportedPath ?? FirstDifference(original,requested,"commands"),"only one existing RunJavaScript props.javaScript value may change; all other nodes and fields must match the native readback");
        return matches[0];
    }
    private static string FirstDifference(JsonNode? before,JsonNode? after,string path)
    {
        if(JsonNode.DeepEquals(before,after)) return path;
        if(before is JsonArray a && after is JsonArray b)
        {
            if(a.Count!=b.Count) return path+".length";
            for(var i=0;i<a.Count;i++) if(!JsonNode.DeepEquals(a[i],b[i])) return FirstDifference(a[i],b[i],path+"["+i+"]");
        }
        if(before is JsonObject x && after is JsonObject y)
        {
            foreach(var item in x) { if(!y.ContainsKey(item.Key)) return path+"."+item.Key; if(!JsonNode.DeepEquals(item.Value,y[item.Key])) return FirstDifference(item.Value,y[item.Key],path+"."+item.Key); }
            foreach(var item in y) if(!x.ContainsKey(item.Key)) return path+"."+item.Key;
        }
        return path;
    }
    private static BridgeException Error(string path,string detail)=>new("CommandTreeRoundTripUnverified",$"{path}: {detail}.");
}
