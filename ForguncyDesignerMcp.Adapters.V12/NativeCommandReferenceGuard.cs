using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R=ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal static class NativeCommandReferenceGuard
{
    internal static void RequireUnreferenced(Assembly assembly,object settings,NativeModuleFiles files,NativeModuleFileGroup target)
    {
        if(target.Address.Kind==ModuleKind.ServerCommand)
        {
            try { NativeScheduledTaskLifecycle.RequireUnreferenced(assembly,settings,files,target); }
            catch(BridgeException error) when(error.Code=="ScheduledTaskReferenced")
            { throw new BridgeException("CommandSignatureReferenced","The command has incoming callers; its existing signature must be retained until those callers are adapted."); }
            return;
        }
        if(target.Address.Kind!=ModuleKind.SharedCommand || target.NativeIdentity.GetType().FullName!="Forguncy.Model.ShareCommandContainer")
            throw new BridgeException("NativeCommandReferenceContextUnavailable","A genuine server/shared command owner is required.");
        files.RequirePureReferenceContext();
        if(!ReferenceEquals(R.Get(R.Static(assembly.GetType("s",true)!,"get_Instance")!,"et"),settings))
            throw new BridgeException("NativeCommandReferenceContextUnavailable","The shared-command reference context changed.");
        foreach(var root in NativeResources.NativeDefinitionRoots(settings)) NativeScheduledTaskLifecycle.RequireLoaded(root,new(ReferenceEqualityComparer.Instance));
        var preference=Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase",true)!.GetProperty("IgnoreDisableCommands",R.Flags)!;
        var previous=preference.GetValue(null);
        try
        {
            preference.SetValue(null,false);
            var values=R.Static(assembly.GetType("Forguncy.ShareCommandContainers.ShareCommandContainerReferenceHelper",true)!,"EnumShareCommandContainerReference",target.NativeIdentity) as IEnumerable
                ?? throw new BridgeException("NativeCommandReferenceContextUnavailable","The native shared-command reference inventory is unavailable.");
            if(values.Cast<object>().Any()) throw new BridgeException("CommandSignatureReferenced","The shared command has incoming callers; its existing signature must be retained until those callers are adapted.");
        }
        finally { preference.SetValue(null,previous); }
    }
}
