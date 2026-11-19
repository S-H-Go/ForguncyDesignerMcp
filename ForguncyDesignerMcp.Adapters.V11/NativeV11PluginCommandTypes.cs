using System.Collections;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Adapters.V11;

// A native initialized command registration is the authority for plugin roots.
// Merely being loaded, sharing an assembly/namespace, or having a type name in
// caller JSON is never sufficient to become an allowed command type.
internal static class NativeV11PluginCommandTypes
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

    internal static HashSet<Type> Resolve(Assembly designer, NativeV11Route route, JsonElement metadata)
    {
        var newer = designer.GetName().Version?.ToString() switch { "11.0.104.0" => true, "11.0.3.0" => false, _ => throw new BridgeException("UnsupportedDesignerBuild", "Unrecognized V11 command registry.") };
        var managerType = designer.GetType(newer ? "lk" : "le", true)!;
        var commandsType = designer.GetType(newer ? "nd" : "mx", true)!;
        if (managerType.TypeInitializer is not null) throw new BridgeException("PluginRegistryUnverified", "The native plugin manager initialization contract changed.");
        var manager = managerType.GetField(newer ? "bez" : "bcy", Flags)!.GetValue(null)
            ?? throw new BridgeException("CommandRegistryInitializationRequired", "Run command.listTypes before submitting a plugin command definition.");
        var task = managerType.GetField(newer ? "bfe" : "bdd", Flags)!.GetValue(manager) as Task;
        var handlers = (managerType.GetField(newer ? "bfa" : "bcz", Flags)!.GetValue(manager) as Delegate)?.GetInvocationList() ?? [];
        if (task is null || !task.IsCompletedSuccessfully || !handlers.Any(h => h.Method.DeclaringType == commandsType && h.Method.Name == "Instance_PluginChanged" && h.Method.IsStatic) ||
            NativeV11Reflection.Get(manager, "NeedReloadAssembly") is not false)
            throw new BridgeException("CommandRegistryInitializationRequired", "The native command and plugin registries must finish initialization before submitting plugin commands.");
        var definitions = commandsType.GetField(newer ? "clv" : "cih", Flags)!.GetValue(null) as IDictionary
            ?? throw new BridgeException("PluginRegistryUnverified", "The existing native command registration is unavailable.");
        var prototypes = commandsType.GetField(newer ? "clw" : "cii", Flags)!.GetValue(null) as IEnumerable
            ?? throw new BridgeException("PluginRegistryUnverified", "The existing native command prototypes are unavailable.");
        var prototypeTypes = prototypes.Cast<object>().Select(item => item.GetType()).ToHashSet();
        var items = managerType.GetField(newer ? "bfc" : "bdb", Flags)!.GetValue(manager) as IEnumerable
            ?? throw new BridgeException("PluginRegistryUnverified", "The existing native plugin registrations are unavailable.");
        var installed = items.Cast<object>().Select(item => NativeV11Reflection.Get(item, "guid") as string)
            .Where(guid => Guid.TryParse(guid, out _)).Select(guid => Guid.Parse(guid!)).ToArray();
        var model = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "ServerDesignerCommon");
        var map = model.GetType("ServerDesignerCommon.Metadata.PluginMapCache", true)!
            .GetField("<TypeToPluginItemCache>k__BackingField", Flags)!.GetValue(null) as IDictionary
            ?? throw new BridgeException("PluginRegistryUnverified", "The native type-to-plugin identity map is unavailable.");
        var requested = RequestedTypes(metadata).ToHashSet();
        var roots = RegisteredRoots(requested, definitions, map, installed).Where(prototypeTypes.Contains).ToList();
        if (roots.Count == 0) throw new BridgeException("PluginCommandTypeUnverified", "No requested plugin command resolves to a unique registered native prototype and installed plugin identity.");
        var rootGuids = roots.ToDictionary(type => type, type => (string)map[type]!);
        bool Stable() => ReferenceEquals(managerType.GetField(newer ? "bez" : "bcy", Flags)!.GetValue(null), manager) &&
            NativeV11Reflection.Get(manager, "NeedReloadAssembly") is false && roots.All(type => definitions[type] is false && map.Contains(type) &&
                map[type] is string current && string.Equals(current, rootGuids[type], StringComparison.OrdinalIgnoreCase) &&
                prototypes.Cast<object>().Count(item => item.GetType() == type) == 1 && items.Cast<object>().Count(item =>
                    NativeV11Reflection.Get(item, "guid") is string actual && Guid.TryParse(actual, out var guid) && guid == Guid.Parse(rootGuids[type])) == 1);
        if (!Stable())
            throw new BridgeException("PluginRegistryUnverified", "The native plugin command registration changed during type verification.");
        var allowed = SerializerReachable(route, roots);
        if (!Stable()) throw new BridgeException("PluginRegistryUnverified", "The native plugin command registration changed during serializer inspection.");
        return allowed;
    }

    internal static HashSet<Type> SerializerReachable(NativeV11Route route, IEnumerable<Type> roots)
    {
        var settings = NativeV11Reflection.Call(route.Loader, "GetJsonSerializerSettings")
            ?? throw new BridgeException("PluginSerializerUnverified", "The native command serializer settings are unavailable.");
        var resolver = NativeV11Reflection.Get(settings, "ContractResolver") ?? Activator.CreateInstance(Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.Serialization.DefaultContractResolver", true)!)!;
        IEnumerable<Type> NativeMembers(Type type)
        {
            var contract = NativeV11Reflection.Call(resolver, "ResolveContract", type) ?? throw new BridgeException("PluginSerializerUnverified", "The native serializer contract is unavailable.");
            if (NativeV11Reflection.Get(contract, "Converter") is not null) yield break;
            if (NativeV11Reflection.Get(contract, "Properties") is not IEnumerable properties) yield break;
            foreach (var property in properties.Cast<object>())
                if (NativeV11Reflection.Get(property, "Ignored") is false && NativeV11Reflection.Get(property, "Readable") is true &&
                    (NativeV11Reflection.Get(property, "Writable") is true || NativeV11Reflection.Get(property, "PropertyType") is Type collection && typeof(IList).IsAssignableFrom(collection)) &&
                    NativeV11Reflection.Get(property, "Converter") is null && NativeV11Reflection.Get(property, "PropertyType") is Type propertyType)
                    yield return propertyType;
        }
        return Reachable(roots, NativeMembers);
    }

    internal static List<Type> RegisteredRoots(IEnumerable<Type> requested, IDictionary definitions, IDictionary map, IReadOnlyList<Guid> installed)
    {
        var roots = new List<Type>();
        foreach (var type in requested)
        {
            if (!definitions.Contains(type) || definitions[type] is not false || !map.Contains(type) || map[type] is not string text ||
                !Guid.TryParse(text, out var id) || id == Guid.Empty || installed.Count(value => value == id) != 1) continue;
            roots.Add(type);
        }
        return roots;
    }

    internal static HashSet<Type> Reachable(IEnumerable<Type> roots, Func<Type, IEnumerable<Type>>? members = null)
    {
        var allowed = new HashSet<Type>();
        foreach (var root in roots)
        {
            var queue = new Queue<Type>(); queue.Enqueue(root);
            while (queue.Count != 0)
            {
                var type = queue.Dequeue();
                if (type.IsArray) { queue.Enqueue(type.GetElementType()!); continue; }
                if (type.IsGenericType)
                {
                    var generic = type.GetGenericTypeDefinition();
                    if (generic == typeof(List<>) || generic == typeof(Dictionary<,>) || generic == typeof(HashSet<>) || generic == typeof(Nullable<>))
                        foreach (var argument in type.GetGenericArguments()) queue.Enqueue(argument);
                    continue;
                }
                if (type.Assembly != root.Assembly || type.IsAbstract || type.IsInterface || type.ContainsGenericParameters || typeof(Delegate).IsAssignableFrom(type) ||
                    !(type.IsClass || type.IsValueType) || !allowed.Add(type)) continue;
                if (allowed.Count > 512) throw new BridgeException("PluginCommandTypeGraphTooLarge", "The native plugin command property graph exceeds its inspection limit.");
                if (members is not null) { foreach (var member in members(type)) queue.Enqueue(member); continue; }
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                    if (property.GetIndexParameters().Length == 0 && property.GetMethod?.IsPublic == true &&
                        (property.SetMethod?.IsPublic == true || typeof(IList).IsAssignableFrom(property.PropertyType)) &&
                        !property.CustomAttributes.Any(a => a.AttributeType.FullName == "Newtonsoft.Json.JsonIgnoreAttribute")) queue.Enqueue(property.PropertyType);
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
                    if (!field.IsInitOnly && !field.CustomAttributes.Any(a => a.AttributeType.FullName == "Newtonsoft.Json.JsonIgnoreAttribute")) queue.Enqueue(field.FieldType);
            }
        }
        return allowed;
    }

    private static IEnumerable<Type> RequestedTypes(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "$type" && property.Value.ValueKind == JsonValueKind.String)
                {
                    var type = Type.GetType(property.Value.GetString()!, name => AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == name.Name), null, false);
                    if (type is not null) yield return type;
                }
                foreach (var type in RequestedTypes(property.Value)) yield return type;
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) foreach (var type in RequestedTypes(item)) yield return type;
    }
}
