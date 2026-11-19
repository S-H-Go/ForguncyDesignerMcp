using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace ForguncyDesignerMcp.Adapters.V11;

// Whole-module invalidation is distinct from page/model invalidation. In the
// exact 104 build SimpleModule.Create(ModuleType, IconType) requires IconType;
// 11.0.3.0 exposes only Create(ModuleType) and does not define that IconType.
internal static class NativeV11Generation
{
    private sealed record Contract(Type CacheType, Type ModuleType, Type StateType, Type TupleType,
        MethodInfo Create, object? Icon, MethodInfo Dirty, MethodInfo SaveDirty, MethodInfo MetadataDirty);
    private static readonly ConditionalWeakTable<Assembly, Contract> Contracts = new();

    internal static void Mark(NativeV11Session session, string moduleName, string state = "Edit")
    {
        session.VerifyContext();
        var contract = Resolve(session.Context.DesignerAssembly);
        var cache = NativeV11Reflection.Static(contract.CacheType, "get_Instance")!;
        MarkCore(session.Context.DesignerAssembly, cache, Enum.Parse(contract.ModuleType, moduleName), state);
    }

    internal static void MarkModules(NativeV11Session session, NativeV11Route route, string state = "Edit")
    {
        session.VerifyContext();
        var contract = Resolve(session.Context.DesignerAssembly);
        var cache = NativeV11Reflection.Static(contract.CacheType, "get_Instance")!;
        if (NativeV11Reflection.Get(route.Colleague, "ModuleTypes") is not IEnumerable modules)
            throw new MissingMemberException("The native colleague exposes no module types.");
        foreach (var module in modules.Cast<object>())
            MarkCore(session.Context.DesignerAssembly, cache, module, state);
    }

    internal static void MarkModel(NativeV11Session session, object model, string state = "Edit")
    {
        session.VerifyContext();
        var contract = Resolve(session.Context.DesignerAssembly);
        MarkModelCore(session.Context.DesignerAssembly, NativeV11Reflection.Static(contract.CacheType, "get_Instance")!, model, state);
    }

    // Named server/shared-command caches are indexed by the model's module
    // name. Invalidating only the whole ModuleType does not address that key.
    internal static void MarkModelCore(Assembly designer, object cache, object model, string state = "Edit")
    {
        var contract = Resolve(designer);
        if (cache.GetType() != contract.CacheType) throw new InvalidOperationException("The native generation cache does not match this V11 build.");
        var root = designer.GetType("Forguncy.Model.TreeNodeModelBase", false) ??
            model.GetType().Assembly.GetType("Forguncy.Model.TreeNodeModelBase", true)!;
        if (!root.IsInstanceOfType(model) || model.GetType().Assembly.GetName().Version != designer.GetName().Version)
            throw new InvalidOperationException("The native generation model does not match this V11 build.");
        var simple = contract.TupleType.GetGenericArguments()[0];
        var factory = simple.GetMethod("Create", NativeV11Reflection.Flags, null, [root], null)
            ?? throw new MissingMethodException(simple.FullName, "Create(TreeNodeModelBase)");
        var module = factory.Invoke(null, [model]) ?? throw new InvalidOperationException("The native model factory returned no module.");
        var changeState = Enum.Parse(contract.StateType, state);
        if (!Enum.IsDefined(contract.StateType, changeState)) throw new InvalidOperationException("The native generation state is undefined.");
        var changes = Array.CreateInstance(contract.TupleType, 1);
        changes.SetValue(Activator.CreateInstance(contract.TupleType, module, changeState), 0);
        contract.Dirty.Invoke(cache, [null, changes]);
    }

    internal static void MarkCore(Assembly designer, object cache, object moduleType, string state = "Edit")
    {
        var contract = Resolve(designer);
        if (cache.GetType() != contract.CacheType || moduleType.GetType() != contract.ModuleType ||
            !Enum.IsDefined(contract.ModuleType, moduleType))
            throw new InvalidOperationException("The native generation cache or module enum does not match the exact V11 build.");
        var changeState = Enum.Parse(contract.StateType, state);
        if (!Enum.IsDefined(contract.StateType, changeState))
            throw new InvalidOperationException("The native generation state is undefined.");
        var module = contract.Create.Invoke(null, contract.Icon is null ? [moduleType] : [moduleType, contract.Icon])
            ?? throw new InvalidOperationException("The native module factory returned no module.");
        var changes = Array.CreateInstance(contract.TupleType, 1);
        changes.SetValue(Activator.CreateInstance(contract.TupleType, module, changeState), 0);
        contract.Dirty.Invoke(cache, [null, changes]);
        if (state != "Remove" && (contract.SaveDirty.Invoke(cache, [moduleType]) is not true ||
            contract.MetadataDirty.Invoke(cache, [moduleType]) is not true))
            throw new InvalidOperationException("The native whole-module save or metadata cache was not marked dirty.");
    }

    private static Contract Resolve(Assembly designer) => Contracts.GetValue(designer, assembly =>
    {
        var profile = V11BuildProfile.Resolve(assembly);
        var cache = assembly.GetType(profile.GenerationCacheType, true)!;
        var dirty = cache.GetMethod("DirtyModules", NativeV11Reflection.Flags)
            ?? throw new MissingMethodException(cache.FullName, "DirtyModules");
        var parameters = dirty.GetParameters();
        if (parameters.Length != 2 || !parameters[1].ParameterType.IsGenericType)
            throw new NotSupportedException("The native DirtyModules signature differs from the V11 profile.");
        var tuple = parameters[1].ParameterType.GetGenericArguments().Single();
        var tupleArguments = tuple.GetGenericArguments();
        if (tupleArguments.Length != 2 || tupleArguments[0].FullName != "Forguncy.Common.SimpleModule" || !tupleArguments[1].IsEnum)
            throw new NotSupportedException("The native DirtyModules change tuple differs from the V11 profile.");
        var moduleType = cache.GetMethods(NativeV11Reflection.Flags).Where(method => method.Name == "IsSaveDataDirty" &&
                method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType.IsEnum)
            .Single().GetParameters()[0].ParameterType;
        if (moduleType.FullName != "Forguncy.Collaboration.ModuleType")
            throw new NotSupportedException("The native generation module enum differs from the V11 profile.");
        VerifyCollaboration(moduleType.Assembly, profile.Version);
        var simple = tupleArguments[0];
        MethodInfo create;
        object? icon = null;
        if (profile.Version == "11.0.104.0")
        {
            create = simple.GetMethods(NativeV11Reflection.Flags).Single(method => method.Name == "Create" && method.IsStatic &&
                method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == moduleType &&
                method.GetParameters()[1].ParameterType.IsEnum);
            var iconType = create.GetParameters()[1].ParameterType;
            if (iconType.FullName != "Forguncy.Collaboration.IconType" ||
                !Enum.GetNames(iconType).Contains("None", StringComparer.Ordinal))
                throw new NotSupportedException("The native V11 icon enum has no verified None member.");
            VerifyCollaboration(iconType.Assembly, profile.Version);
            icon = Enum.Parse(iconType, "None");
            if (Convert.ToInt32(icon) != 0) throw new NotSupportedException("The native V11 IconType.None value differs from the verified build.");
        }
        else
        {
            create = simple.GetMethod("Create", NativeV11Reflection.Flags, null, [moduleType], null)
                ?? throw new MissingMethodException(simple.FullName, "Create(ModuleType)");
        }
        if (!create.IsStatic || create.ReturnType != simple)
            throw new NotSupportedException("The native whole-module factory signature differs from the V11 profile.");
        var saveDirty = cache.GetMethod("IsSaveDataDirty", NativeV11Reflection.Flags, null, [moduleType], null)
            ?? throw new MissingMethodException(cache.FullName, "IsSaveDataDirty(ModuleType)");
        var metadataDirty = cache.GetMethod("IsMetadateDirty", NativeV11Reflection.Flags, null, [moduleType], null)
            ?? throw new MissingMethodException(cache.FullName, "IsMetadateDirty(ModuleType)");
        if (saveDirty.ReturnType != typeof(bool) || metadataDirty.ReturnType != typeof(bool))
            throw new NotSupportedException("The native generation dirty checks differ from the V11 profile.");
        return new(cache, moduleType, tupleArguments[1], tuple, create, icon, dirty, saveDirty, metadataDirty);
    });

    private static void VerifyCollaboration(Assembly assembly, string version)
    {
        var expected = version == "11.0.104.0"
            ? "228DD755C5607955D06F22751A04FD97ECB30095B54F23BBF468E078762B38F5"
            : "FAC1A08973E9B81B5C4D32B44C661B129D7FE8D50FE84A9072B6465022E611F8";
        if (assembly.GetName().Name != "Forguncy.Collaboration" || assembly.GetName().Version?.ToString() != version ||
            string.IsNullOrEmpty(assembly.Location))
            throw new NotSupportedException("The native V11 collaboration component identity differs from the verified build.");
        using var stream = File.OpenRead(assembly.Location);
        if (Convert.ToHexString(SHA256.HashData(stream)) != expected)
            throw new NotSupportedException("The native V11 collaboration component fingerprint differs from the verified build.");
    }
}
