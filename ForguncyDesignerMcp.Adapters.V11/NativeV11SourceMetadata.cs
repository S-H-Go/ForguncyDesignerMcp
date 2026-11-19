using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ForguncyDesignerMcp.Adapters.V11;

// Source files use each native loader's serializer settings. The public model
// projection intentionally has a different shape for some typed collections;
// comparing those two JSON encodings directly would misreport a valid save.
internal static class NativeV11SourceMetadata
{
    internal static JsonElement Capture(NativeV11Session session, ForguncyDesignerMcp.Core.Platform.ModuleAddress address)
    {
        var item = session.Find(address);
        var data = item.Route.IsPage
            ? NativeV11Reflection.Static(session.Generator, "GetPageSaveData", session.Sheet(address.Name))
            : NativeV11Reflection.Call(item.Route.Mediator, "ConvertToSaveData", item.Model);
        return SerializeSaveData(session.Context.DesignerAssembly, item.Route, data
            ?? throw new InvalidOperationException("The native save-data projection is unavailable."));
    }

    internal static JsonElement Serialize(Assembly designer, NativeV11Route route, object model) =>
        SerializeSaveData(designer, route, NativeV11Reflection.Call(route.Mediator, "ConvertToSaveData", model)
            ?? throw new InvalidOperationException("The native save-data projection is unavailable."));

    internal static JsonElement SerializeSaveData(Assembly designer, NativeV11Route route, object data)
    {
        _ = V11BuildProfile.Resolve(designer);
        var settings = NativeV11Reflection.Call(route.Loader, "GetJsonSerializerSettings");
        var newtonsoft = Assembly.Load("Newtonsoft.Json");
        var settingsType = newtonsoft.GetType("Newtonsoft.Json.JsonSerializerSettings", true)!;
        if (settings is not null && !settingsType.IsInstanceOfType(settings))
            throw new InvalidOperationException("The native source serializer settings contract changed.");
        // A native null settings value means Newtonsoft's native defaults;
        // select the actual settings overload explicitly instead of guessing
        // an overload from a null reflection argument.
        var method = newtonsoft.GetType("Newtonsoft.Json.JsonConvert", true)!.GetMethod("SerializeObject",
            NativeV11Reflection.Flags, null, [typeof(object), settingsType], null)
            ?? throw new MissingMethodException("Newtonsoft.Json.JsonConvert.SerializeObject(object,JsonSerializerSettings)");
        var json = method.Invoke(null, [data, settings]) as string
            ?? throw new InvalidOperationException("The native source serializer returned no JSON.");
        return V11Safety.Body(Encoding.UTF8.GetBytes(json));
    }
}
