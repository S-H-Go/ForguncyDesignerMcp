using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal sealed partial class NativeRegisteredFolders
{
    // Use the same non-mutating native scanners as the single-command guard,
    // then map each real location back to its owning native module. The whole
    // selected set is compared, so references inside it do not veto deletion.
    private void RequireCommandReferencesContained(NativeModuleFiles files,IReadOnlyList<object> deleting)
    {
        if(deleting.Count==0) return;
        files.RequirePureReferenceContext();
        var settings=R.Get(R.Static(assembly.GetType("s",true)!,"get_Instance")!,"et")!;
        var rootProperty=owner switch {"ServerCommand"=>"ServerCommandsList","SharedCommand"=>"ShareCommandContainerList","ScheduledTask"=>"TaskSchedulerList",_=>throw new InvalidOperationException()};
        if(!ReferenceEquals(R.Get(settings,rootProperty),root)) throw new BridgeException("NativeFolderReferenceUnavailable","The native reference scanner is bound to a different project tree.");
        foreach(var definitionRoot in NativeResources.NativeDefinitionRoots(settings)) NativeScheduledTaskLifecycle.RequireLoaded(definitionRoot,new(ReferenceEqualityComparer.Instance));
        var simple=assembly.GetType("Forguncy.Common.SimpleModule",true)!;
        var owners=deleting.Select(model=>R.Static(simple,"Create",model)!).ToArray();
        var finder=assembly.GetType("Forguncy.Common.ReferenceFinder",true)!;
        var mapper=finder.GetMethod("EnumEditedModulesByReferences",R.Flags)!;
        void CheckLocation(object location)
        {
            var locationType=location.GetType();
            while(locationType.FullName!="GrapeCity.Forguncy.Plugin.LocationIndicator") locationType=locationType.BaseType ?? throw new BridgeException("NativeFolderReferenceUnavailable","Unknown native reference location.");
            var locations=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(locationType))!;
            foreach(var part in (IEnumerable)R.Call(location,"Enum")!) locations.Add(part);
            var mapped=(mapper.Invoke(null,[locations]) as IEnumerable)?.Cast<object>().ToArray() ?? throw new BridgeException("NativeFolderReferenceUnavailable","Native reference owner mapping failed.");
            if(mapped.Length==0 && owner=="SharedCommand")
            {
                // The pinned finder omits its shared-container indicator; the
                // native indicator itself carries the exact container identity.
                var containers=locations.Cast<object>().Select(segment=>segment.GetType().GetProperties(R.Flags).Where(p=>p.Name=="Object" && p.GetIndexParameters().Length==0)
                    .Select(p=>p.GetValue(segment)).FirstOrDefault(value=>value?.GetType().FullName=="Forguncy.Model.ShareCommandContainer"))
                    .Where(value=>value is not null).Distinct(ReferenceEqualityComparer.Instance).ToArray();
                bool InTree(object node,object value)
                {
                    if(ReferenceEquals(node,value)) return true;
                    return node.GetType().GetProperty("Items",R.Flags)?.GetValue(node) is IEnumerable children && children.Cast<object>().Any(child=>InTree(child,value));
                }
                var sharedRoot=R.Get(settings,"ShareCommandContainerList")!;
                if(containers.Length!=1 || !InTree(sharedRoot,containers[0]!)) throw new BridgeException("NativeFolderReferenceUnavailable","The shared-command reference owner is not a unique loaded native container.");
                mapped=[R.Static(simple,"Create",containers[0]!)!];
            }
            if(mapped.Length==0) throw new BridgeException("NativeFolderReferenceUnavailable","A native reference has no owning module.");
            if(mapped.Any(module=>!owners.Any(owner=>owner.Equals(module)))) throw new BridgeException("NativeFolderExternalReference","A module outside the deletion set references a selected definition.");
        }
        object Value(object located)
        {
            for(var type=located.GetType();type is not null;type=type.BaseType)
                if(type.GetProperty("Object",R.Flags | BindingFlags.DeclaredOnly) is { } property) return property.GetValue(located) ?? throw new BridgeException("NativeFolderReferenceUnavailable","A native reference has no value.");
            throw new BridgeException("NativeFolderReferenceUnavailable","A native reference value is unavailable.");
        }
        void Scan(object? result,string name,bool filter)
        {
            if(result is not IEnumerable values) throw new BridgeException("NativeFolderReferenceUnavailable","The native reference scan is unavailable.");
            foreach(var entry in values.Cast<object>())
                if(!filter || Value(entry) is string candidate && candidate==name)
                    CheckLocation(R.Get(entry,"Location") ?? throw new BridgeException("NativeFolderReferenceUnavailable","A native reference has no location."));
        }
        var preference=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase",true)!.GetProperty("IgnoreDisableCommands",R.Flags)!;
        var previous=preference.GetValue(null);
        try
        {
            preference.SetValue(null,false);
            foreach(var target in deleting)
            {
                var name=(string)R.Get(target,"Name")!;
                if(owner=="SharedCommand")
                {
                    Scan(R.Static(assembly.GetType("Forguncy.ShareCommandContainers.ShareCommandContainerReferenceHelper",true)!,"EnumShareCommandContainerReference",target),name,false);
                    continue;
                }
                var scanner=assembly.GetType("sf",true)!;
                var contract=AppDomain.CurrentDomain.GetAssemblies().Single(a=>a.GetType("GrapeCity.Forguncy.Plugin.IReferenceServerCommand") is not null);
                var reference=contract.GetType("GrapeCity.Forguncy.Plugin.IReferenceServerCommand",true)!;
                var get=reference.GetMethod("GetServerCommandLocation")!;
                var indicator=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.ProcessIndicator",true)!;
                foreach(var process in ((IEnumerable)R.Static(scanner,"EnumAllProcesses")!).Cast<object>())
                    if(reference.IsInstanceOfType(process)) Scan(get.Invoke(process,[Activator.CreateInstance(indicator,[process])!]),name,true);
                foreach(var located in ((IEnumerable)R.Static(scanner,"EnumCommands")!).Cast<object>())
                {
                    var item=Value(located); var location=R.Get(located,"Location")!;
                    if(reference.IsInstanceOfType(item)) Scan(get.Invoke(item,[location]),name,true);
                    Scan(R.Static(scanner,"EnumReferenceServerCommandObject",item,location,name),name,false);
                }
                foreach(var located in ((IEnumerable)R.Static(scanner,"EnumPropertiesOfAttachInfo")!).Cast<object>())
                    Scan(R.Static(scanner,"EnumReferenceServerCommandObject",Value(located),R.Get(located,"Location")!,name),name,false);
            }
        }
        finally { preference.SetValue(null,previous); }
    }
}
