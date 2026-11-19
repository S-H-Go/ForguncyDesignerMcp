using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Reads the pinned V11 Designer's own registered prototypes. Initial access
// can initialize its plugin manager and load existing plugin code.
internal static class NativeV11CommandTypes
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    internal static PlatformApiDescriptor Descriptor() => new("command", "listTypes",
        PlatformJson.Element(new { type = "object", properties = new { scope = new { type = "string", @enum = new[] { "server", "scheduledTask", "client", "pageLoad", "pageMethod", "shared" } } }, required = new[] { "scope" }, additionalProperties = false }),
        PlatformJson.Element(new { type = "array", items = new { type = "object" } }), true, Scope: EffectScope.RuntimeDeployment,
        Documentation: "Discovers V11 native registered command prototypes and design-time property metadata. First use may load built-in and existing plugin code. Native metadata is descriptive; V11 command-tree write support remains limited to its separately documented types.");

    internal static string ValidateParameters(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(p => p.Name is not ("scope" or "guard")) ||
            input.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1) ||
            !input.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.String ||
            scope.GetString() is not ("server" or "scheduledTask" or "client" or "pageLoad" or "pageMethod" or "shared"))
            throw new BridgeException("InvalidCommandScope", "A single native command scope is required: server, scheduledTask, client, pageLoad, pageMethod or shared.");
        return scope.GetString()!;
    }

    internal static JsonElement List(Assembly designer, string scope)
    {
        var registryName = designer.GetName().Version?.ToString() switch { "11.0.104.0" => "nd", "11.0.3.0" => "mx", _ => throw new BridgeException("UnsupportedDesignerBuild", "The V11 command registry is not recognized.") };
        var registry = designer.GetType(registryName, true)!;
        var commands = registry.GetProperty("Commands", Flags)?.GetValue(null) as IEnumerable ?? throw new BridgeException("NativeCommandRegistryUnavailable", "The native V11 command registry is unavailable.");
        var prototypes = commands.Cast<object>().ToArray();
        var scopeName = scope switch { "server" => "ServerSide", "scheduledTask" => "TaskSchedule", "client" => "Cell", "pageLoad" => "PageLoad", "pageMethod" => "UserControlPage", "shared" => "ShareCommand", _ => throw new BridgeException("InvalidCommandScope", "Unrecognized command scope.") };
        var baseType = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == "Forguncy.Commands").GetType("GrapeCity.Forguncy.Commands.Command", true)!;
        var scopeType = baseType.Assembly.GetType("GrapeCity.Forguncy.Commands.CommandScope", true)!;
        var flag = Convert.ToInt64(Enum.Parse(scopeType, scopeName));
        var getScope = baseType.GetMethod("GetCommandScope", Flags) ?? throw new MissingMethodException(baseType.FullName, "GetCommandScope");
        var category = registry.GetMethod("GetGroupName", Flags, null, [typeof(Type)], null) ?? throw new MissingMethodException(registryName, "GetGroupName");
        var serverSync = baseType.Assembly.GetType("GrapeCity.Forguncy.Commands.ICommandExecutableInServerSide", true)!;
        var serverAsync = baseType.Assembly.GetType("GrapeCity.Forguncy.Commands.ICommandExecutableInServerSideAsync", true)!;
        var chosen = prototypes.Where(p => baseType.IsInstanceOfType(p) && (Convert.ToInt64(getScope.Invoke(p, null)) & flag) != 0 &&
            (scope is not ("server" or "scheduledTask") || serverSync.IsInstanceOfType(p) || serverAsync.IsInstanceOfType(p)))
            .GroupBy(p => p.GetType()).Select(g => g.First()).ToArray();
        var names = chosen.GroupBy(p => ShortName(p.GetType()), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var rows = chosen.Select(p => {
            var type = p.GetType(); var shortName = ShortName(type);
            var properties = TypeDescriptor.GetProperties(type).Cast<PropertyDescriptor>()
                .Where(prop => prop.IsBrowsable && (!prop.IsReadOnly || typeof(IList).IsAssignableFrom(prop.PropertyType)) && prop.Attributes.Cast<Attribute>().All(a => a.GetType().FullName != "Newtonsoft.Json.JsonIgnoreAttribute"))
                .Select(prop => new { name = JsonNamingPolicy.CamelCase.ConvertName(prop.Name), nativeName = prop.Name, typeName = prop.PropertyType.FullName, displayName = prop.DisplayName, description = prop.Description,
                    category = prop.Category, enumValues = prop.PropertyType.IsEnum ? Enum.GetNames(prop.PropertyType) : null }).ToArray();
            return new { typeId = type.AssemblyQualifiedName, typeName = names[shortName] == 1 ? shortName : type.FullName,
                displayName = p.ToString(), category = category.Invoke(null, [type]) as string, description = TypeDescriptor.GetAttributes(type)[typeof(DescriptionAttribute)] is DescriptionAttribute d ? d.Description : "",
                properties, metadataSource = "V11 native registered prototype and design-time descriptors" };
        }).OrderBy(row => row.category, StringComparer.Ordinal).ThenBy(row => row.typeName, StringComparer.Ordinal).ToArray();
        return PlatformJson.Element(rows);
    }

    private static string ShortName(Type type) => type.Name.EndsWith("Command", StringComparison.Ordinal) && type.Name.Length > 7 ? type.Name[..^7] : type.Name;
}
