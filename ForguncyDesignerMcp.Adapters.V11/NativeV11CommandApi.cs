using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal static class NativeV11CommandApi
{
    internal static bool Handles(string ns, string method) => ns switch
    {
        "serverCommand" => method is "listServerCommands" or "getServerCommand" or "createServerCommand" or "updateServerCommand" or "deleteServerCommand",
        "sharedCommand" => method is "listSharedCommands" or "getSharedCommand" or "createSharedCommand" or "updateSharedCommand" or "deleteSharedCommand",
        _ => false
    };
    internal static ModuleKind Kind(string ns) => ns == "serverCommand" ? ModuleKind.ServerCommand : ModuleKind.SharedCommand;
    internal static string? Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
        ? property.ValueKind == JsonValueKind.String ? property.GetString() : throw new BridgeException("InvalidCommandRequest", name + " must be a string.") : null;

    internal static JsonObject CreateSeed(ModuleKind kind, string name, JsonElement parameters, string operationId)
    {
        var model = new JsonObject { ["Name"] = name };
        if (kind == ModuleKind.ServerCommand) { model["Enabled"] = false; model["Triggers"] = new JsonArray(); model["Commands"] = new JsonArray(); }
        else
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes("V11 shared command\0" + operationId));
            model["ID"] = new Guid(hash.AsSpan(0, 16)).ToString("D");
        }
        Patch(model, parameters, true, kind);
        return model;
    }

    internal static JsonObject Project(ModuleDocument document, bool summary)
    {
        var metadata = document.Metadata;
        var result = new JsonObject { ["name"] = document.Address.Name, ["folderPath"] = document.Address.FolderPath ?? "" };
        if (metadata.TryGetProperty("Description", out var description) && description.ValueKind == JsonValueKind.String)
            result["description"] = description.GetString();
        if (document.Address.Kind == ModuleKind.ServerCommand && metadata.TryGetProperty("Enabled", out var enabled) &&
            enabled.ValueKind is JsonValueKind.True or JsonValueKind.False) result["enabled"] = enabled.GetBoolean();
        if (metadata.TryGetProperty("Commands", out var commands) && commands.ValueKind == JsonValueKind.Array)
        {
            if (summary) result["commandCount"] = commands.GetArrayLength();
            else if (commands.GetArrayLength() == 0) result["commands"] = new JsonArray();
        }
        if (document.Address.Kind == ModuleKind.SharedCommand)
            foreach (var (native, output) in new[] { ("Params", "parameters"), ("Returns", "returnProperties") })
                if (metadata.TryGetProperty(native, out var values) && values.ValueKind == JsonValueKind.Array)
                {
                    var projected = new JsonArray();
                    foreach (var value in values.EnumerateArray())
                    {
                        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String)
                            throw new BridgeException("NativeCommandProjectionUnavailable", "The native shared signature cannot be projected safely.");
                        var item = new JsonObject { ["name"] = name.GetString() };
                        if (value.TryGetProperty("Remark", out var remark) && remark.ValueKind == JsonValueKind.String) item["remark"] = remark.GetString();
                        projected.Add(item);
                    }
                    result[summary ? (native == "Params" ? "parameterNames" : "returnPropertyNames") : output] = summary
                        ? new JsonArray(projected.Select(item => (JsonNode?)JsonValue.Create(item!["name"]!.GetValue<string>())).ToArray()) : projected;
                }
        if (document.Address.Kind == ModuleKind.ServerCommand && metadata.TryGetProperty("ReturnProperties", out var serverReturns) && serverReturns.ValueKind == JsonValueKind.Array)
        {
            var projected = new JsonArray();
            foreach (var value in serverReturns.EnumerateArray())
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("Name", out var name) || name.ValueKind != JsonValueKind.String)
                    throw new BridgeException("NativeCommandProjectionUnavailable", "The native server return signature cannot be projected safely.");
                var item = new JsonObject { ["name"] = name.GetString() };
                if (value.TryGetProperty("Remark", out var remark) && remark.ValueKind == JsonValueKind.String) item["remark"] = remark.GetString();
                projected.Add(item);
            }
            result[summary ? "returnPropertyNames" : "returnProperties"] = summary
                ? new JsonArray(projected.Select(item => (JsonNode?)JsonValue.Create(item!["name"]!.GetValue<string>())).ToArray()) : projected;
        }
        if (!summary)
        {
            // Complex V11 command/signature JSON is not a V12 command-builder
            // DTO. Preserve it explicitly as native metadata, not fabricated DTOs.
            result["nativeMetadata"] = JsonNode.Parse(metadata.GetRawText());
            result["revision"] = document.Revision;
        }
        return result;
    }

    internal static void ProjectKnownCommands(JsonObject result, object model)
    {
        if (NativeV11Reflection.Get(model, "Commands") is not System.Collections.IList commands) return;
        var projected = new JsonArray();
        foreach (var command in commands.Cast<object>())
        {
            if (NativeV11Reflection.Get(command, "Disabled") is true ||
                NativeV11Reflection.Get(command, "Comments") is string comments && comments.Length != 0) return;
            var props = new JsonObject(); string type;
            if (command.GetType().FullName == "Forguncy.Model.ReturnCommand")
            {
                type = "Return";
                if (NativeV11Reflection.Get(command, "ErrorCode") is not int code) return;
                props["errorCode"] = code;
                if (NativeV11Reflection.Get(command, "Message") is string message) props["message"] = message;
            }
            else if (command.GetType().FullName == "Forguncy.Model.Commands.SetParameterCommand")
            {
                type = "SetParameter";
                if (NativeV11Reflection.Get(command, "IsTableValue") is not false ||
                    NativeV11Reflection.Get(command, "ParameterName") is not string name) return;
                if (NativeV11Reflection.Get(command, "FormulaValues") is System.Collections.ICollection formulas && formulas.Count != 0) return;
                props["parameterName"] = name;
                var value = NativeV11Reflection.Get(command, "ParameterValue");
                if (value is not (null or string or bool or byte or short or int or long or float or double or decimal)) return;
                props["parameterValue"] = JsonSerializer.SerializeToNode(value);
            }
            else return; // Raw nativeMetadata remains available; never fabricate a partial tree.
            projected.Add(new JsonObject { ["type"] = type, ["props"] = props });
        }
        result["commands"] = projected;
    }

    internal static void Patch(JsonObject model, JsonElement parameters, bool create, ModuleKind kind)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in parameters.EnumerateObject())
        {
            if (!seen.Add(field.Name)) throw new BridgeException("InvalidCommandRequest", "Duplicate command request fields are not accepted.");
            if (field.Name is "guard" or "name" || create && field.Name == "folderPath") continue;
            if (field.Value.ValueKind == JsonValueKind.Null) continue;
            if (field.Name == "description" && field.Value.ValueKind == JsonValueKind.String)
            { model["Description"] = field.Value.GetString(); continue; }
            if (field.Name == "enabled" && kind == ModuleKind.ServerCommand && field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            { model["Enabled"] = field.Value.GetBoolean(); continue; }
            if (field.Name == "commands" && field.Value.ValueKind == JsonValueKind.Array)
            { ValidateCommandNodes(field.Value); continue; }
            if (kind == ModuleKind.SharedCommand && field.Name is "parameters" or "returnProperties" && field.Value.ValueKind == JsonValueKind.Array)
            { ValidateSimpleSignature(field.Value); continue; }
            if (kind == ModuleKind.ServerCommand && field.Name == "returnProperties" && field.Value.ValueKind == JsonValueKind.Array)
            { ValidateSimpleSignature(field.Value); continue; }
            if (kind == ModuleKind.ServerCommand && field.Name == "parameters" && field.Value.ValueKind == JsonValueKind.Array)
            { ValidateServerParameters(field.Value); continue; }
            if (create && field.Name is "parameters" or "returnProperties" && field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() == 0) continue;
            throw new BridgeException("NativeCommandFieldUnavailable", "This command field or command-tree/signature edit is not yet verified in V11: " + field.Name);
        }
    }

    internal static void ValidateServerParameters(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 256)
            throw new BridgeException("NativeCommandFieldUnavailable", "Server parameters require a bounded array.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(field => field.Name is not ("name" or "remark" or "type")) ||
                value.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count() ||
                !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) || !names.Add(name.GetString()!) ||
                value.TryGetProperty("remark", out var remark) && remark.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                value.TryGetProperty("type", out var type) && (type.ValueKind != JsonValueKind.String || type.GetString() != "normal"))
                throw new BridgeException("NativeCommandFieldUnavailable", "Only unique normal server parameters with optional remarks are currently supported.");
        }
    }

    internal static void ApplyServerParameters(object model, JsonElement parameters, bool create)
    {
        if (!parameters.TryGetProperty("parameters", out var values) || values.ValueKind == JsonValueKind.Null) return;
        ValidateServerParameters(values);
        if (model.GetType().FullName != "Forguncy.ServerCommands.Models.ServerCommands") throw new InvalidOperationException("The native server-command model differs from the reviewed type.");
        var triggers = NativeV11Reflection.Get(model, "Triggers") as System.Collections.IList ?? throw new InvalidOperationException("The native server trigger collection is unavailable.");
        var nativeAssembly = model.GetType().Assembly;
        var invokeType = nativeAssembly.GetType("Forguncy.ServerCommands.Models.InvokeTrigger", true)!;
        var matches = triggers.Cast<object>().Where(invokeType.IsInstanceOfType).ToArray();
        if (matches.Length > 1) throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Server parameters require exactly one native invoke trigger.");
        if (!create && matches.Length == 0)
        {
            if (values.GetArrayLength() == 0) return;
            throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Adding a server invocation trigger requires a separate interface migration.");
        }
        if (create && matches.Length != 0) throw new InvalidOperationException("A new server command unexpectedly contains an invocation trigger.");
        if (create && values.GetArrayLength() == 0) return;
        var trigger = create ? Activator.CreateInstance(invokeType)! : matches[0];
        var property = trigger.GetType().GetProperty("Parameters", NativeV11Reflection.Flags) ?? throw new MissingMemberException("Native invocation parameters are unavailable.");
        if (property.SetMethod is null || !property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(List<>))
            throw new InvalidOperationException("The native invocation parameter collection differs from the reviewed type.");
        var itemType = property.PropertyType.GetGenericArguments()[0];
        if (itemType.FullName != "Forguncy.ServerCommands.Models.PostRequestParameter" || itemType.Assembly != nativeAssembly)
            throw new InvalidOperationException("The native invocation parameter item differs from the reviewed type.");
        var existing = property.GetValue(trigger) as System.Collections.IList;
        if (!create && (existing is null || existing.Count != values.GetArrayLength()))
            throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Changing server parameter names requires complete caller migration.");
        var result = (System.Collections.IList)Activator.CreateInstance(property.PropertyType)!;
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var name = value.GetProperty("name").GetString()!;
            var item = create ? Activator.CreateInstance(itemType)! : existing![index]!;
            if (!create && (!string.Equals(NativeV11Reflection.Get(item, "Name") as string, name, StringComparison.Ordinal) ||
                NativeV11Reflection.Get(item, "Type")?.ToString() != "Normal"))
                throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Changing server parameter names, type or order requires complete caller migration.");
            if (create) NativeV11Reflection.Set(item, "Name", name);
            if (value.TryGetProperty("remark", out var remark)) NativeV11Reflection.Set(item, "Remark", remark.ValueKind == JsonValueKind.Null ? null : remark.GetString());
            result.Add(item); index++;
        }
        property.SetValue(trigger, result);
        if (create) triggers.Add(trigger);
    }

    internal static void ApplyServerReturns(object model, JsonElement parameters, bool create)
    {
        if (!parameters.TryGetProperty("returnProperties", out var values) || values.ValueKind == JsonValueKind.Null) return;
        ValidateSimpleSignature(values);
        if (model.GetType().FullName != "Forguncy.ServerCommands.Models.ServerCommands") throw new InvalidOperationException("The native server-command model differs from the reviewed type.");
        var property = model.GetType().GetProperty("ReturnProperties", NativeV11Reflection.Flags) ?? throw new MissingMemberException("Native server return properties are unavailable.");
        if (property.SetMethod is null || !property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(List<>))
            throw new InvalidOperationException("The native server return collection differs from the reviewed type.");
        var itemType = property.PropertyType.GetGenericArguments()[0];
        if (itemType.FullName != "Forguncy.ServerCommands.Models.ReturnProperty" || itemType.Assembly != model.GetType().Assembly)
            throw new InvalidOperationException("The native server return item differs from the reviewed type.");
        var existing = property.GetValue(model) as System.Collections.IList;
        if (!create && (existing is null || existing.Count != values.GetArrayLength()))
            throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Changing server return names requires complete caller migration.");
        var result = (System.Collections.IList)Activator.CreateInstance(property.PropertyType)!;
        var index = 0;
        foreach (var value in values.EnumerateArray())
        {
            var name = value.GetProperty("name").GetString()!;
            var item = create ? Activator.CreateInstance(itemType)! : existing![index]!;
            if (!create && !string.Equals(NativeV11Reflection.Get(item, "Name") as string, name, StringComparison.Ordinal))
                throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Changing server return names or order requires complete caller migration.");
            if (create) NativeV11Reflection.Set(item, "Name", name);
            if (value.TryGetProperty("remark", out var remark)) NativeV11Reflection.Set(item, "Remark", remark.ValueKind == JsonValueKind.Null ? null : remark.GetString());
            result.Add(item); index++;
        }
        property.SetValue(model, result);
    }

    internal static void ValidateSimpleSignature(JsonElement values)
    {
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() > 256)
            throw new BridgeException("NativeCommandFieldUnavailable", "Shared command signatures require a bounded array.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(field => field.Name is not ("name" or "remark")) ||
                value.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != value.EnumerateObject().Count() ||
                !value.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(name.GetString()) ||
                !names.Add(name.GetString()!) || value.TryGetProperty("remark", out var remark) && remark.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                throw new BridgeException("NativeCommandFieldUnavailable", "Shared command signature items require unique names and optional remarks.");
        }
    }

    internal static void ApplySharedSignatures(object model, JsonElement parameters, bool create)
    {
        if (model.GetType().FullName != "Forguncy.Model.ShareCommandContainer") throw new InvalidOperationException("The native shared-command model differs from the reviewed type.");
        foreach (var (input, native) in new[] { ("parameters", "Params"), ("returnProperties", "Returns") })
        {
            if (!parameters.TryGetProperty(input, out var values) || values.ValueKind == JsonValueKind.Null) continue;
            ValidateSimpleSignature(values);
            var property = model.GetType().GetProperty(native, NativeV11Reflection.Flags) ?? throw new MissingMemberException("Native shared signature property unavailable.");
            if (property.SetMethod is null || !property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(List<>))
                throw new InvalidOperationException("The native shared signature collection differs from the reviewed type.");
            var elementType = property.PropertyType.GetGenericArguments()[0];
            var expectedType = native == "Params" ? "Forguncy.Model.ShareCommandParam" : "Forguncy.Model.ShareCommandReturnProperty";
            if (elementType.FullName != expectedType || elementType.Assembly != model.GetType().Assembly)
                throw new InvalidOperationException("The native shared signature item type differs from the reviewed type.");
            var existing = property.GetValue(model) as System.Collections.IList;
            if (!create && (existing is null || existing.Count != values.GetArrayLength()))
                throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Changing a shared signature requires complete caller migration and is not available.");
            var result = (System.Collections.IList)Activator.CreateInstance(property.PropertyType)!;
            var index = 0;
            foreach (var value in values.EnumerateArray())
            {
                var name = value.GetProperty("name").GetString()!;
                var item = create ? Activator.CreateInstance(elementType)! : existing![index]!;
                if (!create && !string.Equals(NativeV11Reflection.Get(item, "Name") as string, name, StringComparison.Ordinal))
                    throw new BridgeException("NativeCommandSignatureChangeUnavailable", "Changing shared signature names or order requires complete caller migration.");
                if (create) NativeV11Reflection.Set(item, "Name", name);
                if (value.TryGetProperty("remark", out var remark)) NativeV11Reflection.Set(item, "Remark", remark.ValueKind == JsonValueKind.Null ? null : remark.GetString());
                result.Add(item); index++;
            }
            property.SetValue(model, result);
        }
    }

    internal static void ValidateCommandNodes(JsonElement commands)
    {
        if (commands.ValueKind != JsonValueKind.Array || commands.GetArrayLength() > 256)
            throw new BridgeException("NativeCommandFieldUnavailable", "Command nodes must be a bounded array.");
        foreach (var item in commands.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || item.EnumerateObject().Any(field => field.Name is not ("type" or "props")) ||
                item.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != item.EnumerateObject().Count() ||
                !item.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not ("Return" or "SetParameter") ||
                !item.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object ||
                props.EnumerateObject().Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != props.EnumerateObject().Count() ||
                type.GetString() == "Return" && (props.EnumerateObject().Any(field => field.Name is not ("errorCode" or "message")) ||
                    props.TryGetProperty("errorCode", out var code) && (code.ValueKind != JsonValueKind.Number || !code.TryGetInt32(out _)) ||
                    props.TryGetProperty("message", out var message) && message.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) ||
                type.GetString() == "SetParameter" && (props.EnumerateObject().Any(field => field.Name is not ("parameterName" or "parameterValue")) ||
                    !props.TryGetProperty("parameterName", out var parameterName) || parameterName.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(parameterName.GetString()) ||
                    !props.TryGetProperty("parameterValue", out var parameterValue) || parameterValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null)))
                throw new BridgeException("NativeCommandFieldUnavailable", "Only verified Return and scalar SetParameter command nodes are currently supported.");
        }
    }

    internal static void ApplyCommandNodes(object model, JsonElement parameters, System.Reflection.Assembly designer)
    {
        if (!parameters.TryGetProperty("commands", out var commands) || commands.ValueKind == JsonValueKind.Null) return;
        ValidateCommandNodes(commands);
        var property = model.GetType().GetProperty("Commands", NativeV11Reflection.Flags) ?? throw new MissingMemberException("Native command collection is unavailable.");
        if (property.SetMethod is null || !property.PropertyType.IsGenericType || property.PropertyType.GetGenericTypeDefinition() != typeof(List<>))
            throw new InvalidOperationException("The native command collection contract changed.");
        var list = (System.Collections.IList)Activator.CreateInstance(property.PropertyType)!;
        var assembly = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(designer)!.Assemblies.Single(value => value.GetName().Name == "ServerDesignerCommon");
        foreach (var node in commands.EnumerateArray())
        {
            var commandKind = node.GetProperty("type").GetString();
            var type = assembly.GetType(commandKind == "Return" ? "Forguncy.Model.ReturnCommand" : "Forguncy.Model.Commands.SetParameterCommand", true)!;
            var command = Activator.CreateInstance(type)!;
            if (NativeV11Reflection.Call(command, "GetCommandScope") is not Enum scope ||
                !Enum.TryParse(scope.GetType(), model.GetType().FullName == "Forguncy.Model.ShareCommandContainer" ? "ShareCommand" : "ServerSide", out var required) ||
                !scope.HasFlag((Enum)required!))
                throw new BridgeException("NativeCommandFieldUnavailable", "The native command is not supported in this command scope.");
            var props = node.GetProperty("props");
            if (commandKind == "Return")
            {
                if (props.TryGetProperty("errorCode", out var code)) NativeV11Reflection.Set(command, "ErrorCode", code.GetInt32());
                if (props.TryGetProperty("message", out var message)) NativeV11Reflection.Set(command, "Message", message.ValueKind == JsonValueKind.Null ? null : message.GetString());
            }
            else
            {
                NativeV11Reflection.Set(command, "ParameterName", props.GetProperty("parameterName").GetString());
                var value = props.GetProperty("parameterValue");
                object? scalar = value.ValueKind switch { JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetDecimal(),
                    JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
                NativeV11Reflection.Set(command, "ParameterValue", scalar);
            }
            list.Add(command);
        }
        property.SetValue(model, list);
    }

    internal static PlatformApiDescriptor[] Descriptors() => new[]
    {
        ("serverCommand", "ServerCommand"), ("sharedCommand", "SharedCommand")
    }.SelectMany(pair => new[]
    {
        Descriptor(pair.Item1, "list" + pair.Item2 + "s", false), Descriptor(pair.Item1, "get" + pair.Item2, false),
        Descriptor(pair.Item1, "create" + pair.Item2, true), Descriptor(pair.Item1, "update" + pair.Item2, true),
        Descriptor(pair.Item1, "delete" + pair.Item2, true)
    }).ToArray();

    private static PlatformApiDescriptor Descriptor(string ns, string method, bool mutation)
    {
        var properties = new JsonObject { ["name"] = new JsonObject { ["type"] = "string" },
            ["folderPath"] = new JsonObject { ["type"] = "string" }, ["description"] = new JsonObject { ["type"] = "string" } };
        if (ns == "serverCommand") properties["enabled"] = new JsonObject { ["type"] = "boolean" };
        if (ns == "serverCommand" && (method.StartsWith("create", StringComparison.Ordinal) || method.StartsWith("update", StringComparison.Ordinal)))
            properties["parameters"] = new JsonObject { ["type"] = "array", ["description"] = "Normal parameter names and remarks only; updates preserve names, types and order." };
        if (ns == "serverCommand" && (method.StartsWith("create", StringComparison.Ordinal) || method.StartsWith("update", StringComparison.Ordinal)))
            properties["returnProperties"] = new JsonObject { ["type"] = "array", ["description"] = "Items support name and remark; updates preserve names and order." };
        if (ns == "sharedCommand" && (method.StartsWith("create", StringComparison.Ordinal) || method.StartsWith("update", StringComparison.Ordinal)))
            foreach (var field in new[] { "parameters", "returnProperties" })
                properties[field] = new JsonObject { ["type"] = "array", ["description"] = "Items support name and remark; updates preserve names and order." };
        if (method.StartsWith("create", StringComparison.Ordinal) || method.StartsWith("update", StringComparison.Ordinal))
            properties["commands"] = new JsonObject { ["type"] = "array", ["description"] = "Native Return and scalar SetParameter nodes are supported in this V11 slice." };
        var schema = new JsonObject { ["type"] = "object", ["properties"] = properties,
            ["required"] = method.StartsWith("list", StringComparison.Ordinal) ? new JsonArray() : new JsonArray("name") };
        return new(ns, method, JsonSerializer.SerializeToElement(schema), null, mutation,
            Documentation: "V11 native definition aliases: name/folder, description, server enabled, Return/scalar SetParameter command trees, and shared signature names/remarks; other command types or shared signature changes reject before mutation. No command execution.");
    }
}
