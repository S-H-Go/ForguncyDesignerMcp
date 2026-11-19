using System.Collections;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal static class NativeLibraryLifecycle
{
    internal static bool Supports(string method)=>method is "createLocal" or "updateContent" or "renameLocal" or "addLink" or "updateLink" or "delete" or "reorder";

    internal static void Validate(AdapterHostContext context,NativeModuleFiles files,NativeResources resources,string method,JsonElement parameters,
        IReadOnlySet<string>? pendingDeletedPaths=null)
    {
        var assembly=context.DesignerAssembly;
        var settings=R.Get(context.NativeDocument,"ForguncySettings")!;
        if(!ReferenceEquals(R.Get(R.Static(assembly.GetType("s",true)!,"get_Instance")!,"et"),settings))
            throw new BridgeException("NativeLibraryContextChanged","Library validation requires the currently bound native settings.");
        _=files.Resolve(new(ModuleKind.CustomLibrary,"Current")) ?? throw new BridgeException("NativeLibraryContextUnavailable","The native library root/file group is unavailable.");
        if (method == "updateContent")
        {
            var actionsType = assembly.GetType("Forguncy.AI.Actions.AppCustomLibrarySandboxAIActions", true)!;
            var request = Activator.CreateInstance(actionsType.GetNestedType("LocalRequest", R.Flags)!)!;
            string? Input(string key)
            {
                var field = parameters.EnumerateObject().FirstOrDefault(p => p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
                if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
                if (field.ValueKind != JsonValueKind.String) throw new BridgeException("InvalidLibraryParameter", key + " must be a string.");
                return field.GetString();
            }
            R.Set(request, "Type", Input("type"));
            R.Set(request, "Name", Input("name"));
            object current;
            try { current = R.Static(actionsType, "GetContentCore", request)!; }
            catch (ArgumentException) { throw new BridgeException("NativeLibraryNotFound", "type and name must identify a registered local CSS or JavaScript library."); }
            var expected = Input("expectedContentHash");
            if (!string.IsNullOrWhiteSpace(expected) && !string.Equals(expected, (string)R.Get(current, "ContentHash")!, StringComparison.OrdinalIgnoreCase))
                throw new BridgeException("LibraryContentChanged", "expectedContentHash differs from the current native library content hash.");
            R.Static(assembly.GetType("abg", true)!, "grb", Input("content"));
            return;
        }
        if(method is not ("createLocal" or "renameLocal" or "delete")) return;
        var actions=assembly.GetType("Forguncy.AI.Actions.AppCustomLibrarySandboxAIActions",true)!;
        var dto=Activator.CreateInstance(actions.GetNestedType("ItemRequest",R.Flags)!)!;
        foreach(var property in new[]{"Type","Kind","Name","Url"})
        {
            var input=parameters.EnumerateObject().FirstOrDefault(p=>p.Name.Equals(property,StringComparison.OrdinalIgnoreCase)).Value;
            if(input.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                throw new BridgeException("InvalidLibraryIdentity","Library identity fields must be strings.");
            if(input.ValueKind==JsonValueKind.String) R.Set(dto,property,input.GetString());
        }
        if(method is "createLocal" or "renameLocal") R.Set(dto,"Kind","local");
        var identity=((string Type,bool Link,string Key))R.Static(actions,"ResolveIdentity",dto)!;
        var list=(IList)R.Static(assembly.GetType("abg",true)!,"gqw",identity.Type)!;
        // The exact native lookup throws ArgumentException for an absent item;
        // absence is expected during creation and becomes our explicit delete
        // precondition error otherwise. ResolveIdentity already validated input.
        int index;
        try { index=(int)R.Static(assembly.GetType("abg",true)!,"FindIndex",list,identity.Link,identity.Key)!; }
        catch(ArgumentException) { index=-1; }
        if(method!="createLocal" && index<0) throw new BridgeException("NativeLibraryNotFound","The requested native library registration is absent.");
        if(method=="createLocal" && index>=0) throw new BridgeException("NativeLibraryAlreadyExists","The local library is already registered; update its content instead.");
        if(identity.Link) return; // Removing an external URL changes only this root configuration.
        var name=method=="createLocal"?identity.Key:(string)R.Get(list[index]!,"Name")!;
        var config=assembly.GetType("Forguncy.Common.Config",true)!;
        var localRoot=(string)R.Static(config,"get_LocalUserFilePath")!;
        if(method is "createLocal" or "renameLocal")
        {
            var nextName=name;
            if(method=="renameLocal")
            {
                var input=parameters.EnumerateObject().FirstOrDefault(p=>p.Name.Equals("newName",StringComparison.OrdinalIgnoreCase)).Value;
                if(input.ValueKind!=JsonValueKind.String) throw new BridgeException("InvalidLibraryIdentity","Library rename requires a string newName.");
                nextName=(string)R.Static(assembly.GetType("abg",true)!,"gqz",identity.Type,input.GetString())!;
            }
            var local=NativeSaveVerification.Under(localRoot,nextName);
            var relative="UserFile/"+nextName;
            if(File.Exists(local) || Directory.Exists(local)) throw new BridgeException("NativeLibraryFileCollision","Library creation cannot replace an existing unregistered cache file.");
            if(pendingDeletedPaths?.Contains(relative) is not true &&
                (NativeSaveVerification.ReadSource(context.CaptureSnapshot().Target.ProjectPath,relative) is not null ||
                 NativeModuleFiles.SourceDirectoryExists(context.CaptureSnapshot().Target.ProjectPath,relative)))
                throw new BridgeException("NativeLibraryFileCollision","Library creation cannot overwrite an existing source file.");
            if(method=="createLocal") return;
        }
        var root=R.Get(settings,"CustomLibraries")!;
        var registrations=new[]{"UserJSFileList","UserCSSFileList"}.SelectMany(field=>((IEnumerable)R.Get(root,field)!).Cast<object>());
        if(registrations.Count(row=>R.Get(row,"IsLink") is false && string.Equals(R.Get(row,"Name") as string,name,StringComparison.OrdinalIgnoreCase))!=1)
            throw new BridgeException("NativeLibraryFileShared","The file is ambiguously registered by more than one native library.");
        var references=resources.References(new(ModuleKind.ResourceFile,name,"UserFile"));
        if(!references.Complete) throw new BridgeException("NativeLibraryReferencesIncomplete",references.Reason??"The native library dependency inventory is incomplete.");
        // The native CustomLibrary provider enumerates its owned binaries. The
        // registration being removed is not an incoming usage reference; all
        // other native owners and actual command file references remain vetoes.
        if(references.References.Any(reference=>reference.Source.Kind!=ModuleKind.CustomLibrary))
            throw new BridgeException("NativeLibraryReferenced","The local library binary is still referenced by another native module.");
    }
}
