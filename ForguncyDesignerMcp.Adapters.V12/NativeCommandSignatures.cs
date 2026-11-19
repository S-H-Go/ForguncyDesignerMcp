using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

/// <summary>Allows native command metadata edits only when their persisted contract is unchanged apart from remarks.</summary>
public static class NativeCommandSignatures
{
    private const string ServerActions = "Forguncy.AI.Actions.ServerCommandSandboxAIActions";
    private const string SharedActions = "Forguncy.AI.Actions.SharedCommandSandboxAIActions";

    public static bool Supports(string ns, string method) =>
        ns == "serverCommand" && method is "updateServerCommand" or "updateServerCommandParameterSettings" ||
        ns == "sharedCommand" && method == "updateSharedCommand";

    public static void Validate(string ns, string method, JsonElement parameters, object nativeSettings)
        => ValidateCore(ns,method,parameters,nativeSettings,true);

    // Used only after the bound adapter proves there are no incoming callers.
    // The native parser still validates a detached copy; hidden persisted test
    // data on retained parameters must not disappear as a side effect.
    internal static void ValidateUnreferencedDefinition(string ns,string method,JsonElement parameters,object nativeSettings)
        => ValidateCore(ns,method,parameters,nativeSettings,false);

    private static void ValidateCore(string ns,string method,JsonElement parameters,object nativeSettings,bool preserveContract)
    {
        if (!Supports(ns, method))
            throw new BridgeException("SignatureValidationUnsupported", "This API has no adapted command-signature validation.");
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new BridgeException("InvalidCommandSignatureRequest", "Command metadata parameters must be an object.");
        var supplied = Properties(parameters);
        var hasParameters = supplied.TryGetValue("parameters", out var incomingParameters) && incomingParameters.ValueKind != JsonValueKind.Null;
        var hasReturns = supplied.TryGetValue("returnProperties", out var incomingReturns) && incomingReturns.ValueKind != JsonValueKind.Null;
        var settingsUpdate = method == "updateServerCommandParameterSettings";
        if (!settingsUpdate && !hasParameters && !hasReturns) return;
        if (!supplied.TryGetValue("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()))
            throw new BridgeException("InvalidCommandSignatureRequest", "The native command name is required for signature validation.");
        var assembly = nativeSettings.GetType().Assembly;
        if (nativeSettings.GetType().FullName != "lt" || assembly.GetName().Version != new Version(12, 0, 102, 0))
            throw new BridgeException("NativeCommandSignatureContractChanged", "Command signatures require the bound 12.0.102.0 settings model.");
        try
        {
            var shared = ns == "sharedCommand";
            var model = Find(nativeSettings, shared, name.GetString()!);
            var actions = assembly.GetType(shared ? SharedActions : ServerActions, true)!;
            if (hasParameters || settingsUpdate)
            {
                var current = shared ? R.Get(model, "Params")! : ServerParameters(model);
                EnsurePlainMetadata(current, shared);
                var detached = CloneList(current);
                object candidate;
                if (settingsUpdate)
                    candidate = ApplySettingsUpdates(actions, supplied, detached);
                else
                {
                    var dto = DeserializeList(incomingParameters, actions.GetNestedType(shared ? "SharedCommandParameterDto" : "ServerCommandParameterDto", R.Flags)!);
                    candidate = R.Static(actions, shared ? "ParseParameters" : "ParsePostRequestParameters", dto, detached)!;
                }
                if(preserveContract) RequireSame(assembly, current, candidate, shared ? "ShareCommandParamSaveData" : "PostRequestParameterSaveData");
                else { EnsurePlainMetadata(candidate,shared); if(!shared) RequireRetainedTestData(current,candidate); }
            }
            if (hasReturns)
            {
                var current = R.Get(model, shared ? "Returns" : "ReturnProperties")!;
                EnsurePlainMetadata(current, shared);
                var dto = DeserializeList(incomingReturns, actions.GetNestedType(shared ? "SharedCommandReturnPropertyDto" : "ServerCommandReturnPropertyDto", R.Flags)!);
                var candidate = shared ? R.Static(actions, "ParseReturnProperties", dto, CloneList(current))! : R.Static(actions, "ParseReturnProperties", dto)!;
                if(preserveContract) RequireSame(assembly, current, candidate, shared ? "ShareCommandReturnPropertySaveData" : "ReturnPropertySaveData");
                else EnsurePlainMetadata(candidate,shared);
            }
        }
        catch (BridgeException) { throw; }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException or OperationCanceledException))
        {
            throw new BridgeException("InvalidCommandSignatureRequest", "Native command metadata could not be validated on a detached copy.");
        }
    }

    private static object Find(object settings, bool shared, string name)
    {
        var root = R.Get(settings, shared ? "ShareCommandContainerList" : "ServerCommandsList")!;
        var expected = shared ? "Forguncy.Model.ShareCommandContainer" : "Forguncy.ServerCommands.Models.ServerCommands";
        var matches = ((IEnumerable)R.Call(root, "EnumNonFolderItems")!).Cast<object>()
            .Where(model => model.GetType().FullName == expected && string.Equals(R.Get(model, "Name") as string, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new BridgeException("NativeCommandSignatureTargetNotUnique", "The command name must resolve to one local native definition.");
        return matches[0];
    }

    private static object ServerParameters(object model)
    {
        var invoke = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.ServerCommands.Models.InvokeTrigger", true)!;
        var triggers = (R.Get(model, "Triggers") as IEnumerable)?.Cast<object>().Where(invoke.IsInstanceOfType).ToArray() ?? [];
        if (triggers.Length != 1)
            throw new BridgeException("NativeCommandSignatureTriggerNotUnique", "Parameter edits require one native invoke trigger.");
        return R.Get(triggers[0], "Parameters")!;
    }

    private static object CloneList(object current)
    {
        var result = (IList)Activator.CreateInstance(current.GetType())!;
        foreach (var item in (IEnumerable)current) result.Add(R.Call(item, "Clone"));
        return result;
    }

    private static object ApplySettingsUpdates(Type actions, Dictionary<string, JsonElement> supplied, object detached)
    {
        if (!supplied.TryGetValue("updates", out var updates) || updates.ValueKind != JsonValueKind.Array || updates.GetArrayLength() == 0)
            throw new BridgeException("InvalidCommandSignatureRequest", "Parameter settings updates must contain at least one metadata edit.");
        var dtoType = actions.GetNestedType("ServerCommandParameterSettingsUpdateDto", R.Flags)!;
        var parsed = ((IEnumerable)DeserializeList(updates, dtoType)).Cast<object>().ToArray();
        var paths = new List<string[]>();
        foreach (var update in parsed)
        {
            var path = (string[])R.Static(actions, "SplitParameterPath", R.Get(update, "ParameterPath"))!;
            if (paths.Any(old => Prefix(old, path) || Prefix(path, old)))
                throw new BridgeException("InvalidCommandSignatureRequest", "Overlapping parameter metadata paths are not allowed.");
            paths.Add(path);
            object siblings = detached;
            object? target = null;
            for (var index = 0; index < path.Length; index++)
            {
                var matches = ((IEnumerable)siblings).Cast<object>().Where(item => string.Equals(R.Get(item, "Name") as string, path[index], StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1)
                    throw new BridgeException("NativeCommandSignatureTargetNotUnique", "The parameter path must resolve uniquely in the current native contract.");
                target = matches[0];
                if (index + 1 != path.Length) siblings = R.Get(target, "ArrayParamItems")!;
            }
            R.Static(actions, "ApplyParameterSettingsUpdate", target, update, siblings, path.Length == 1);
        }
        return detached;
    }

    private static bool Prefix(string[] left, string[] right) => left.Length <= right.Length &&
        left.Select((value, index) => string.Equals(value, right[index], StringComparison.OrdinalIgnoreCase)).All(value => value);

    // ParameterItem.Clone uses native CommonClone, and DataValidationInfo.Clone
    // can clone ValidateCommand. Reject command-bearing or foreign object
    // metadata before cloning; command prototypes and plugin managers are never needed.
    private static void EnsurePlainMetadata(object list, bool shared)
    {
        if (!list.GetType().IsGenericType || list.GetType().GetGenericTypeDefinition() != typeof(List<>)) UnsupportedMetadata();
        foreach (var item in (IEnumerable)list)
        {
            var type = item.GetType().FullName;
            if (shared)
            {
                if (type is not ("Forguncy.Model.ShareCommandParam" or "Forguncy.Model.ShareCommandReturnProperty")) UnsupportedMetadata();
                continue;
            }
            if (type == "Forguncy.ServerCommands.Models.ReturnProperty") continue;
            if (type is not ("Forguncy.ServerCommands.Models.PostRequestParameter" or "Forguncy.ServerCommands.Models.ParameterItem")) UnsupportedMetadata();
            if (!Scalar(R.Get(item, "TestData"))) UnsupportedMetadata();
            if (R.Get(item, "DataValidationInfo") is { } validation)
            {
                if (validation.GetType().FullName != "Forguncy.Model.DataValidationInfo" ||
                    R.Get(validation, "ValidateCommand") is IEnumerable commands && commands.Cast<object>().Any() ||
                    !ValidationValue(R.Get(validation, "FirstParamValue")) || !ValidationValue(R.Get(validation, "SecondParamValue"))) UnsupportedMetadata();
            }
            if (R.Get(item, "ArrayParamItems") is { } children) EnsurePlainMetadata(children, false);
        }
    }

    private static bool Scalar(object? value) => value is null or string or bool or char or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or DateTime or DateTimeOffset or TimeSpan or Guid;
    private static void RequireRetainedTestData(object before,object after)
    {
        var candidates=((IEnumerable)after).Cast<object>().ToArray();
        foreach(var item in ((IEnumerable)before).Cast<object>())
        {
            var retained=candidates.SingleOrDefault(candidate=>R.Get(candidate,"Name") as string==R.Get(item,"Name") as string);
            if(retained is null) continue; // Explicit removal/replacement of that named parameter.
            if(!Equals(R.Get(item,"TestData"),R.Get(retained,"TestData")))
                throw new BridgeException("CommandParameterTestDataWouldBeLost","The native full-list parser would change persisted TestData on a retained parameter; use the parameter-settings API to preserve it.");
            var children=R.Get(item,"ArrayParamItems"); var next=R.Get(retained,"ArrayParamItems");
            if(children is not null && next is not null) RequireRetainedTestData(children,next);
        }
    }
    private static bool ValidationValue(object? value) => Scalar(value);
    private static void UnsupportedMetadata() => throw new BridgeException("NativeCommandSignatureMetadataUnsupported", "Command-bearing, formula-object or foreign parameter metadata cannot be detached by the read-only signature validator.");

    private static object DeserializeList(JsonElement value, Type itemType)
    {
        var type = typeof(List<>).MakeGenericType(itemType);
        ValidateDto(value, type, 0);
        var json = Assembly.Load("Newtonsoft.Json").GetType("Newtonsoft.Json.JsonConvert", true)!;
        // This fixed-type overload has no TypeNameHandling or client-selected CLR type.
        return R.Static(json, "DeserializeObject", value.GetRawText(), type)!;
    }

    private static void ValidateDto(JsonElement value, Type type, int depth)
    {
        if (depth > 32) throw new BridgeException("InvalidCommandSignatureRequest", "Command metadata nesting exceeds the validation limit.");
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (type.Namespace == "Forguncy.AI.Actions" && type.IsNested)
                throw new BridgeException("InvalidCommandSignatureRequest", "Native command metadata items cannot be null.");
            return;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
        {
            if (value.ValueKind != JsonValueKind.Array) throw new BridgeException("InvalidCommandSignatureRequest", "Command metadata lists must be arrays.");
            foreach (var child in value.EnumerateArray()) ValidateDto(child, type.GetGenericArguments()[0], depth + 1);
        }
        else if (type.Namespace == "Forguncy.AI.Actions" && type.IsNested)
        {
            if (value.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidCommandSignatureRequest", "Native command metadata items must be objects.");
            var properties = type.GetProperties(R.Flags).Where(property => property.SetMethod is not null).ToDictionary(property => property.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var supplied in Properties(value))
            {
                if (!properties.TryGetValue(supplied.Key, out var property))
                    throw new BridgeException("InvalidCommandSignatureRequest", "A supplied field is not part of the fixed native command metadata DTO.");
                ValidateDto(supplied.Value, property.PropertyType, depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in Properties(value))
            {
                if (property.Key.StartsWith('$')) throw new BridgeException("InvalidCommandSignatureRequest", "CLR type metadata is not allowed in command metadata values.");
                ValidateDto(property.Value, typeof(object), depth + 1);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var child in value.EnumerateArray()) ValidateDto(child, typeof(object), depth + 1);
    }

    private static Dictionary<string, JsonElement> Properties(JsonElement value)
    {
        var result = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!result.TryAdd(property.Name, property.Value))
                throw new BridgeException("InvalidCommandSignatureRequest", "Duplicate or differently-cased command metadata fields are not allowed.");
        return result;
    }

    private static void RequireSame(Assembly assembly, object current, object candidate, string saveName)
    {
        var before = Project(assembly, current, saveName);
        var after = Project(assembly, candidate, saveName);
        RemoveRemarks(before); RemoveRemarks(after);
        if (!JsonNode.DeepEquals(before, after))
            throw new BridgeException("CommandSignatureChangeNotSupported", "Only remarks may change in this command metadata operation; parameter identities, names, structure, validation and saved test data must be preserved.");
    }

    private static JsonNode Project(Assembly assembly, object model, string saveName)
    {
        var saveType = Assembly.Load("ServerDesignerCommon").GetType("Forguncy.SaveLoad." + saveName, true)!;
        var mapper = assembly.GetType("hk", true)!.GetProperty("Mapper", R.Flags)!.GetValue(null)!;
        var map = mapper.GetType().GetMethods(R.Flags).Single(method => method.Name == "Map" && !method.ContainsGenericParameters && method.GetParameters().Length == 3 && method.GetParameters()[1].ParameterType == typeof(Type));
        var saved = map.Invoke(mapper, [model, model.GetType(), typeof(List<>).MakeGenericType(saveType)])!;
        var settings = R.Static(assembly.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!, "GetLoadJsonSerializerSettings");
        var json = (string)R.Static(Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!, "GetJsonStringNoFormat", saved, false, null, settings)!;
        return JsonNode.Parse(json)!;
    }

    private static void RemoveRemarks(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            obj.Remove("Remark");
            foreach (var child in obj.ToArray()) if (child.Value is not null) RemoveRemarks(child.Value);
        }
        else if (node is JsonArray array)
            foreach (var child in array) if (child is not null) RemoveRemarks(child);
    }
}
