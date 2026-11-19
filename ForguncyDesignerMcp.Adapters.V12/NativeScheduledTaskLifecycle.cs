using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// 12.0.102.0 ReferenceFinder dispatches ServerCommands (including scheduler
// models) to ServerCommandReferenceHelper.EnumServerCommandReference(name).
// Its iterator attaches aliases to matching commands before yielding. This
// equivalent veto uses the same three native root enumerations and nested
// property scanner, but never invokes that mutating alias helper.
internal static class NativeScheduledTaskLifecycle
{
    internal static void RequireUnreferenced(Assembly assembly, object settings, NativeModuleFiles files, NativeModuleFileGroup target)
    {
        files.RequirePureReferenceContext();
        var state = R.Static(assembly.GetType("s", true)!, "get_Instance")!;
        if (!ReferenceEquals(R.Get(state, "et"), settings)) throw new BridgeException("ScheduledReferenceContextMismatch", "Native reference enumeration must use the bound project.");
        foreach (var root in NativeResources.NativeDefinitionRoots(settings)) RequireLoaded(root, new HashSet<object>(ReferenceEqualityComparer.Instance));
        if (target.NativeIdentity.GetType().FullName != "Forguncy.ServerCommands.Models.ServerCommands" ||
            target.Address.Kind is not (ModuleKind.ScheduledTask or ModuleKind.ServerCommand))
            throw new BridgeException("NativeCommandReferenceContextUnavailable", "A genuine native task/server-command owner is required for the incoming reference scan.");
        var scanner = assembly.GetType("sf", true)!;
        var contract = assembly.GetReferencedAssemblies().FirstOrDefault(x => x.Name == "GrapeCity.Forguncy.Plugin");
        var plugin = contract is null ? AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetType("GrapeCity.Forguncy.Plugin.IReferenceServerCommand") is not null) : Assembly.Load(contract);
        var referenceType = plugin.GetType("GrapeCity.Forguncy.Plugin.IReferenceServerCommand", true)!;
        var locationMethod = referenceType.GetMethod("GetServerCommandLocation") ?? throw new MissingMethodException(referenceType.FullName, "GetServerCommandLocation");
        var indicator = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Model.ProcessIndicator", true)!;
        var preference = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.Common.NameReferenceHelperBase", true)!.GetProperty("IgnoreDisableCommands", R.Flags)!;
        var original = preference.GetValue(null);
        try
        {
            preference.SetValue(null, false); // include disabled command definitions; restore even on exceptions.
            foreach (var process in Sequence(R.Static(scanner, "EnumAllProcesses")))
            {
                if (referenceType.IsInstanceOfType(process))
                {
                    var location = Activator.CreateInstance(indicator, [process])!;
                    RequireNoNames(locationMethod.Invoke(process, [location]), target.Address.Name);
                }
            }
            foreach (var located in Sequence(R.Static(scanner, "EnumCommands")))
            {
                var item = LocatedValue(located); var location = R.Get(located, "Location") ?? throw Incomplete();
                if (referenceType.IsInstanceOfType(item)) RequireNoNames(locationMethod.Invoke(item, [location]), target.Address.Name);
                RequireNoNested(scanner, item, location, target.Address.Name);
            }
            foreach (var located in Sequence(R.Static(scanner, "EnumPropertiesOfAttachInfo")))
            {
                var item = LocatedValue(located); var location = R.Get(located, "Location") ?? throw Incomplete();
                RequireNoNested(scanner, item, location, target.Address.Name);
            }
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { throw new BridgeException("ScheduledReferenceScanFailed", "The complete native scheduled-task incoming reference scan failed: " + error.InnerException.GetType().Name); }
        finally { preference.SetValue(null, original); }
    }
    internal static void RequireNoNames(object? value, string name)
    {
        foreach (var item in Sequence(value))
            if (LocatedValue(item) is not string candidate) throw Incomplete();
            else if (candidate == name) throw Referenced();
    }
    internal static void RequireEmptyNested(object? value)
    { if (Sequence(value).Any()) throw Referenced(); }
    private static void RequireNoNested(Type scanner, object item, object location, string name) =>
        RequireEmptyNested(R.Static(scanner, "EnumReferenceServerCommandObject", item, location, name));
    private static IEnumerable<object> Sequence(object? value) => value is IEnumerable items ? items.Cast<object>() : throw Incomplete();
    private static object LocatedValue(object located)
    {
        for (var type = located.GetType(); type is not null; type = type.BaseType)
            if (type.GetProperty("Object", R.Flags | BindingFlags.DeclaredOnly) is { } property)
                return property.GetValue(located) ?? throw Incomplete();
        throw Incomplete();
    }
    internal static void RequireLoaded(object model, HashSet<object> visited)
    {
        if (!visited.Add(model)) throw Incomplete();
        var loaded = model.GetType().GetProperty("Loaded", R.Flags);
        var children = model.GetType().GetProperty("Items", R.Flags)?.GetValue(model) as IEnumerable;
        if (children is not null && loaded is null || loaded is not null && loaded.GetValue(model) is not true) throw Incomplete();
        if (children is not null) foreach (var child in children) RequireLoaded(child ?? throw Incomplete(), visited);
    }
    private static BridgeException Referenced() => new("ScheduledTaskReferenced", "The native reference scan found an incoming task/command reference; deletion was not dispatched.");
    private static BridgeException Incomplete() => new("ScheduledReferenceScanIncomplete", "The complete native scheduled-task reference inventory is unavailable.");
}
