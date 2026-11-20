using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

public sealed partial class NativeModuleFiles
{
    internal static bool IsCommandDefinitionLifecycle(string ns,string method)=>
        ns=="serverCommand" && method is "createServerCommand" or "updateServerCommands" or "deleteServerCommand" ||
        ns=="sharedCommand" && method is "createSharedCommand" or "deleteSharedCommand" ||
        ns=="scheduledTask" && method=="createScheduledTask" ||
        ns=="notification" && method is "createNotification" or "updateNotification" or "deleteNotification";

    private NativeApiFileCapture? CaptureCommandDefinition(string ns,string method,JsonElement parameters)
    {
        var fields=UniqueFields(parameters);
        if(fields is null || Scalar(fields,"name") is not {Length:>0} name) return null;
        var kind=ns switch {"serverCommand"=>ModuleKind.ServerCommand,"sharedCommand"=>ModuleKind.SharedCommand,
            "scheduledTask"=>ModuleKind.ScheduledTask,"notification"=>ModuleKind.Notification,_=>throw new ArgumentException(nameof(ns))};
        var operation=method.StartsWith("create",StringComparison.Ordinal)?ModuleOperation.Create:
            method.StartsWith("delete",StringComparison.Ordinal)?ModuleOperation.Delete:ModuleOperation.Update;
        var address=new ModuleAddress(kind,name,Scalar(fields,"folderPath"));
        var colleague=Colleague(kind);
        if(colleague is null || R.Get(colleague,"Root") is not {} root) return null;
        NativeScheduledTaskLifecycle.RequireLoaded(root,new(ReferenceEqualityComparer.Instance));
        var before=Resolve(address);
        if(operation==ModuleOperation.Create && before is not null)
            throw new BridgeException("NativeDefinitionAlreadyExists","The native definition already exists.");
        if(operation==ModuleOperation.Create)
        {
            var baseFolder=NativePath(((string)R.Get(colleague,"BaseFolder")!).TrimEnd('/','\\'));
            var suffix=(string)R.Get(colleague,"FileSuffix")!;
            var relative=baseFolder.TrimEnd('/')+"/"+(string.IsNullOrEmpty(address.FolderPath)?"":address.FolderPath+"/")+name+suffix;
            var local=NativeSaveVerification.Under((string)R.Get(save,"LocalPath")!,relative);
            var staged=NativeSaveVerification.Under((string)R.Get(save,"LastPath")!,relative);
            var source=(string)R.Call(save,"GetDocumentPath")!;
            if(File.Exists(local) || Directory.Exists(local) || File.Exists(staged) || Directory.Exists(staged) ||
                NativeSaveVerification.ReadSource(source,relative) is not null || SourceDirectoryExists(source,relative))
                throw new BridgeException("NativeDefinitionFileCollision","Definition creation cannot overwrite an unregistered native file.");
        }
        if(operation!=ModuleOperation.Create && before is null)
            throw new BridgeException("NativeDefinitionNotFound","The requested native definition is absent.");
        var nodeUpdate = ns == "serverCommand" && method == "updateServerCommands";
        if (!nodeUpdate) RequirePureReferenceContext();
        if(operation==ModuleOperation.Delete || ns=="notification" && fields.TryGetValue("parameters",out var incoming) && incoming.ValueKind==JsonValueKind.Array)
            foreach(var definitionRoot in NativeResources.NativeDefinitionRoots(settings))
                NativeScheduledTaskLifecycle.RequireLoaded(definitionRoot,new(ReferenceEqualityComparer.Instance));
        var descriptor=((IEnumerable)R.Static(assembly.GetType("aam",true)!,"GetCurrentAssemblyDescriptors")!).Cast<object>()
            .Single(d=>R.Get(d,"Namespace") as string==ns && R.Get(d,"Method") as string==method);
        var requestType=(Type)R.Get(descriptor,"RequestType")!;
        var nativeMethod=(MethodInfo)R.Get(descriptor,"MethodInfo")!;
        var expectedResolver=method switch
        {
            "createServerCommand"=>"abe+ay","deleteServerCommand"=>"abe+az",
            "updateServerCommands"=>"abe+bd",
            "createSharedCommand"=>"abe+bh","deleteSharedCommand"=>"abe+bi",
            "createScheduledTask"=>"abe+bx","createNotification"=>"abe+bn",
            "updateNotification"=>"abe+bq","deleteNotification"=>"abe+bo",_=>throw new InvalidOperationException()
        };
        var resolver=nativeMethod.GetCustomAttributesData().Single(a=>a.AttributeType.FullName=="abc").ConstructorArguments.Single().Value as Type;
        if(resolver?.Assembly!=assembly || resolver.FullName!=expectedResolver)
            throw new BridgeException("NativeDefinitionContractChanged","The pinned native definition effect resolver changed.");
        var request=Activator.CreateInstance(requestType)!;
        // For a node update, pinned abe+bd.Resolve reads only Name from the DTO;
        // Updates and Permission are still validated/handled by the native dispatcher.
        R.Set(request,"Name",name);
        var folder=requestType.GetProperty("FolderPath",R.Flags);
        if(folder is not null) folder.SetValue(request,Scalar(fields,"folderPath"));
        if(ns=="notification" && fields.TryGetValue("parameters",out var supplied) && supplied.ValueKind is not JsonValueKind.Null)
        {
            if(supplied.ValueKind!=JsonValueKind.Array) throw new BridgeException("InvalidNotificationParameters","Notification parameters must be an array or null.");
            var property=requestType.GetProperty("Parameters",R.Flags)!;
            var parsed=(IList)Activator.CreateInstance(property.PropertyType)!;
            var itemType=property.PropertyType.GetGenericArguments().Single();
            foreach(var item in supplied.EnumerateArray())
            {
                var values=UniqueFields(item) ?? throw new BridgeException("InvalidNotificationParameters","Each notification parameter must be an unambiguous metadata object.");
                var dto=Activator.CreateInstance(itemType)!;
                foreach(var value in values)
                {
                    var target=itemType.GetProperties(R.Flags).SingleOrDefault(p=>p.CanWrite && p.Name.Equals(value.Key,StringComparison.OrdinalIgnoreCase));
                    if(target?.PropertyType!=typeof(string) || value.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                        throw new BridgeException("InvalidNotificationParameters","Notification parameter metadata contains an unknown or nonstring property.");
                    target.SetValue(dto,value.Value.ValueKind==JsonValueKind.Null?null:value.Value.GetString());
                }
                parsed.Add(dto);
            }
            // ParseParameters creates new native metadata items and never edits
            // the supplied existing list. Caller rewrites occur only on dispatch.
            var actions=nativeMethod.DeclaringType!;
            _=R.Static(actions,"ParseParameters",parsed,before is null?null:R.Get(before.NativeIdentity,"Params"));
            property.SetValue(request,parsed);
        }
        if(operation==ModuleOperation.Delete)
        {
            if(kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand)
                NativeCommandReferenceGuard.RequireUnreferenced(assembly,settings,this,before!);
            else
            {
                var preference=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase",true)!.GetProperty("IgnoreDisableCommands",R.Flags)!;
                var previous=preference.GetValue(null);
                try
                {
                    preference.SetValue(null,false);
                    if(((IEnumerable)R.Static(nativeMethod.DeclaringType!,"GetNotificationReferenceLocations",name)!).Cast<object>().Any())
                        throw new BridgeException("NotificationReferenced","The native notification has incoming references.");
                }
                finally { preference.SetValue(null,previous); }
            }
        }
        var previousPreference=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase",true)!.GetProperty("IgnoreDisableCommands",R.Flags)!;
        var previousValue=previousPreference.GetValue(null);
        object[] changes;
        try
        {
            previousPreference.SetValue(null,false);
            changes=((IEnumerable)R.Call(Activator.CreateInstance(resolver)!,"Resolve",request)!).Cast<object>().ToArray();
        }
        finally { previousPreference.SetValue(null,previousValue); }
        if (nodeUpdate)
        {
            // The reviewed V12 resolver names exactly one existing Edit owner.
            // Never accept a lifecycle or ambiguous plan for an in-place node update.
            if (changes is not [var edit] || edit.GetType().GetField("Item2")?.GetValue(edit)?.ToString() != "Edit")
                return null;
            var editPlan = CaptureNativeModulesCore(changes, pageReferences: false);
            if (editPlan?.BeforeGroups is not [var owner] ||
                !ReferenceEquals(owner.NativeIdentity, before!.NativeIdentity) ||
                editPlan.ExpectedNewModuleNames.Count != 0)
                return null;
            return new(ns, method, parameters.Clone(), address, operation, before, editPlan, [owner.NativeModuleName]);
        }
        var plan=CaptureNativeModules(changes);
        if(plan is null) return null;
        if(operation==ModuleOperation.Create)
        {
            if(plan.ExpectedNewModules.Count!=1)
                throw new BridgeException("NativeDefinitionFolderRequired","Create the native parent folder explicitly before creating its definition.");
            plan=new(plan.BeforeGroups,plan.ExpectedNewModules,new Dictionary<string,ModuleAddress>(StringComparer.Ordinal)
                {[plan.ExpectedNewModules.Single().Name]=address},plan.NativeChanges);
        }
        else if(!plan.BeforeGroups.Any(group=>ReferenceEquals(group.NativeIdentity,before!.NativeIdentity)) || plan.ExpectedNewModules.Count!=0) return null;
        return new(ns,method,parameters.Clone(),address,operation,before,plan);
    }
}
