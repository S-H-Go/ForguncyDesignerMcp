using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V12;

// Links an existing remote table/view through the native Designer importer.
// This never routes connectionId into CreateTableAsync/CreateViewInDBAsync.
internal sealed class NativeLinkedTableCreation(AdapterHostContext context, NativeModuleFiles files)
{
    internal sealed record Input(string ConnectionId,string SourceTableName,string? SourceSchema);
    internal sealed record Prepared(Input Input,object Model,object Collection,object Executor,NativeModuleFilePlan Plan,string DefinitionPath);
    internal static bool Handles(ModuleMutationRequest request) => request.Operation==ModuleOperation.Create &&
        request.Address.Kind is ModuleKind.Table or ModuleKind.View && request.Document?.Metadata is {ValueKind:JsonValueKind.Object} metadata && metadata.TryGetProperty("binding",out _);

    internal static Input ReadInput(ModuleMutationRequest request)
    {
        if(!Handles(request) || request.Scope!=EffectScope.ProjectDefinition || request.Address.FolderPath is not null ||
            request.Document!.Address!=request.Address || request.Document.Companions.Count!=0)
            throw new BridgeException("InvalidLinkedTableRequest","Link creation requires a root Table/View address, ProjectDefinition scope, binding metadata and no companions.");
        var metadata=request.Document.Metadata;
        if(metadata.EnumerateObject().Count()!=1 || !metadata.TryGetProperty("binding",out var binding) || binding.ValueKind!=JsonValueKind.Object)
            throw new BridgeException("InvalidLinkedTableRequest","Metadata must contain only binding: { connectionId, sourceTableName, sourceSchema? }.");
        var values=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach(var property in binding.EnumerateObject())
            if(property.Name is not ("connectionId" or "sourceTableName" or "sourceSchema") || !values.TryAdd(property.Name,property.Value))
                throw new BridgeException("InvalidLinkedTableRequest","Unknown or duplicate binding property.");
        string? Text(string key,bool required)
        {
            if(!values.TryGetValue(key,out var value) || value.ValueKind==JsonValueKind.Null)
            { if(required) throw new BridgeException("InvalidLinkedTableRequest","Missing binding identity: "+key); return null; }
            if(value.ValueKind!=JsonValueKind.String || value.GetString() is not { } text || string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.Length>256)
                throw new BridgeException("InvalidLinkedTableRequest","Binding identities must be nonempty strings of at most 256 characters.");
            return text;
        }
        return new(Text("connectionId",true)!,Text("sourceTableName",true)!,Text("sourceSchema",false));
    }

    internal static object CreateDetached(Assembly designer,ModuleAddress address,Input input)
    {
        object? view=null;
        if(address.Kind==ModuleKind.View)
        {
            view=Activator.CreateInstance(Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.Tables.ViewInfo",true)!)!;
            NativeReflection.Set(view,"ViewName",input.SourceTableName);
        }
        var model=NativeReflection.Static(designer.GetType("my",true)!,"CreateTableForAddLinkedTable",address.Name,input.SourceTableName,input.SourceSchema,input.ConnectionId,view,Array.Empty<string>())!;
        var info=NativeReflection.Get(model,"ExternalDatabaseInfo")!;
        // The native factory's default is a read-only link. Never silently
        // enable the external schema modification/drop-table prompt.
        if(NativeReflection.Get(info,"AllowModifySchema") is not false || NativeReflection.Get(model,"IsLinkedTableOrLinkedView") is not true ||
            (NativeReflection.Get(model,"IsView") is true)!=(address.Kind==ModuleKind.View))
            throw new BridgeException("NativeLinkedTableContractChanged","The native link factory no longer creates the expected read-only binding kind.");
        return model;
    }

    internal Prepared Prepare(ModuleMutationRequest request)
    {
        var input=ReadInput(request); var designer=context.DesignerAssembly;
        if(context.CaptureSnapshot().Target!=request.Target)
            throw new BridgeException("DocumentChanged","The native link target is no longer the original document/session.");
        var settings=NativeReflection.Get(context.NativeDocument,"ForguncySettings")!;
        var connections=((IEnumerable)NativeReflection.Get(settings,"DataConnectionSet")!).Cast<object>();
        if(connections.Count(c=>NativeReflection.Get(c,"ID") as string==input.ConnectionId)!=1)
            throw new BridgeException("NativeConnectionNotUnique","The binding connectionId must identify one existing native connection definition.");
        var set=NativeReflection.Get(settings,"TableSet")!; var root=NativeReflection.Get(set,"Root")!;
        if(NativeReflection.Get(root,"Loaded") is not true || ((IEnumerable)NativeReflection.Get(set,"RealTablesList")!).Cast<object>().Any(t=>
            string.Equals(NativeReflection.Get(t,"Name") as string,request.Address.Name,StringComparison.OrdinalIgnoreCase)))
            throw new BridgeException("NativeLinkedTableTargetUnavailable","The native table root must be loaded and the proposed logical name absent.");
        var model=CreateDetached(designer,request.Address,input);
        var (executor,plan)=CaptureOwners(designer,request.Address,files);
        var colleagues=NativeReflection.Get(context.NativeDocument,"ForguncyColleagueManager")!;
        var colleague=NativeReflection.Get(colleagues,"TableModel")!;
        var relative=FutureDefinitionPath(colleague,request.Address);
        var save=NativeReflection.Get(NativeReflection.Get(NativeReflection.Get(colleague,"Mediator")!,"SaveDataColleague")!,"SaveLoadFile")!;
        foreach(var cache in new[]{"LocalPath","LastPath"})
            if(File.Exists(NativeSaveVerification.Under((string)NativeReflection.Get(save,cache)!,relative)) || Directory.Exists(NativeSaveVerification.Under((string)NativeReflection.Get(save,cache)!,relative)))
                throw new BridgeException("NativeLinkedTableSourceCollision","The new native definition path is already present in a cache.");
        if(NativeSaveVerification.ReadSource(context.CaptureSnapshot().Target.ProjectPath,relative) is not null ||
            NativeModuleFiles.SourceDirectoryExists(context.CaptureSnapshot().Target.ProjectPath,relative))
            throw new BridgeException("NativeLinkedTableSourceCollision","The proposed binding would overwrite an existing source definition.");
        return new(input,model,NativeReflection.Get(root,"Items")!,executor,plan,relative);
    }

    internal static string FutureDefinitionPath(object nativeColleague,ModuleAddress address)
    {
        // GetKey is a registry lookup and correctly returns null for detached
        // models. Root creation instead uses the actual native folder/suffix;
        // the registered native key must match this path after import.
        var folder=(string)NativeReflection.Get(nativeColleague,"BaseFolder")!;
        var suffix=(string)NativeReflection.Get(nativeColleague,"FileSuffix")!;
        if(suffix!=".json") throw new BridgeException("NativeLinkedTableContractChanged","The native table definition suffix changed.");
        return (folder.TrimEnd('\\','/')+"/"+address.Name+suffix).Replace('\\','/');
    }

    internal static (object Executor,NativeModuleFilePlan Plan) CaptureOwners(Assembly designer,ModuleAddress address,NativeModuleFiles files)
    {
        var executorType=designer.GetType("Forguncy.Common.CreateTableExecutor",true)!;
        var ctor=executorType.GetConstructors(NativeReflection.Flags).Single(c=>c.GetParameters().Length==5);
        var p=ctor.GetParameters(); var view=address.Kind==ModuleKind.View;
        var executor=ctor.Invoke([null,address.Name,Enum.ToObject(p[2].ParameterType,view?23:7),Enum.ToObject(p[3].ParameterType,view?26:24),false]);
        var changes=(IEnumerable)NativeReflection.Call(executor,"GetRelatedModules")!;
        var plan=files.CaptureNativeModulesCore(changes,false) ?? throw new BridgeException("NativeAffectedTargetsUnverified","Native linked-table creation owners could not be resolved.");
        if(plan.ExpectedNewModules.Count!=1 || plan.BeforeGroups.Any(g=>g.Address.Kind is ModuleKind.Table or ModuleKind.View))
            throw new BridgeException("NativeLinkedTableContractChanged","Read-only link creation unexpectedly affects existing table definitions.");
        plan=new(plan.BeforeGroups,plan.ExpectedNewModules,plan.ExpectedNewModules.ToDictionary(t=>t.Name,_=>address,StringComparer.Ordinal),plan.NativeChanges);
        return (executor,plan);
    }

    internal NativeApiFileCapture Capture(ModuleMutationRequest request)
    {
        var prepared=Prepare(request);
        return new("table","linkExisting",request.Document!.Metadata,request.Address,ModuleOperation.Create,null,prepared.Plan);
    }

    internal async Task<IReadOnlyList<NativeModuleFileGroup>> ApplyAsync(ModuleMutationRequest request,CancellationToken token)
    {
        var prepared=Prepare(request); token.ThrowIfCancellationRequested();
        if(NativeReflection.Call(prepared.Executor,"CanExecute",false) is not true)
            throw new BridgeException("NativeLinkedTablePermissionDenied","The native creation executor did not authorize this link.");
        // Same import used by the native linked-table UI. It reads the remote
        // schema and refreshes the native model; no CREATE/DROP SQL is requested.
        var task=(Task)NativeReflection.Static(context.DesignerAssembly.GetType("mz",true)!,"AddLinkedTableAsync",prepared.Model,prepared.Collection,-1,false)!;
        await task; // Native work has no cancellation contract; retain the original task.
        var currentRoot=NativeReflection.Get(NativeReflection.Get(NativeReflection.Get(context.NativeDocument,"ForguncySettings")!,"TableSet")!,"Root")!;
        if(context.CaptureSnapshot().Target!=request.Target || !ReferenceEquals(prepared.Collection,NativeReflection.Get(currentRoot,"Items")))
            throw new BridgeException("DocumentChanged","Native link import finished after its document/table collection changed; retain the original operation and do not replay.");
        var groups=files.ResolveNativeModulesAfter(prepared.Plan) ?? throw new BridgeException("NativeAffectedTargetsReadbackFailed","The linked native definition could not be resolved after import; do not replay.");
        var target=groups.Single(g=>ReferenceEquals(g.NativeIdentity,prepared.Model));
        var info=NativeReflection.Get(prepared.Model,"ExternalDatabaseInfo")!;
        if(target.Address!=request.Address || !target.RequiredFiles.Contains(prepared.DefinitionPath,StringComparer.Ordinal) || NativeReflection.Get(info,"ConnectionId") as string!=prepared.Input.ConnectionId ||
            NativeReflection.Get(info,"SourceTableName") as string!=prepared.Input.SourceTableName || NativeReflection.Get(info,"AllowModifySchema") is not false ||
            prepared.Input.SourceSchema is not null && NativeReflection.Get(info,"SourceTableSchema") as string!=prepared.Input.SourceSchema)
            throw new BridgeException("NativeLinkedTableReadbackFailed","The imported native binding differs from the original guarded request.");
        var tuples=prepared.Plan.NativeChanges!; var typed=Array.CreateInstance(tuples[0].GetType(),tuples.Count);
        for(var i=0;i<tuples.Count;i++) typed.SetValue(tuples[i],i);
        NativeReflection.Call(NativeReflection.Static(context.DesignerAssembly.GetType("pb",true)!,"get_Instance")!,"DirtyModules",null,typed);
        NativeReflection.Set(context.NativeDocument,"ContainsUnsavedChanges",true);
        return groups;
    }
}
