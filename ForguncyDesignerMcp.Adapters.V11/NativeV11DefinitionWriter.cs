using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Definition editing only. This class never executes a command, starts a schedule,
// publishes a process, or modifies a database. Source persistence belongs to SaveAsync.
internal static class NativeV11DefinitionWriter
{
    private static readonly ConditionalWeakTable<Assembly, HashSet<Type>> CellModelTypes = new();
    private sealed record PermissionComponentStamp(bool Trusted);
    private static readonly ConditionalWeakTable<Assembly, PermissionComponentStamp> PermissionComponents = new();
    internal static bool Supports(ModuleKind kind, ModuleOperation operation) =>
        (operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) &&
        (kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification ||
         (kind is ModuleKind.Report or ModuleKind.BusinessProcess) && operation == ModuleOperation.Update);

    internal static void Validate(NativeV11Session session, ModuleMutationRequest request) => Prepare(session, request);

    internal static Action Apply(NativeV11Session session, ModuleMutationRequest request)
    {
        var prepared = Prepare(session, request);
        session.VerifyContext();
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var undo = new List<Action>();
        try
        {
            if (prepared.Layout is not null)
            {
                var layout = Path.ChangeExtension(prepared.Path, request.Address.Kind == ModuleKind.Report ? ".rdlx" : ".bpmn").Replace('\\', '/');
                var absolute = Under(session.LocalPath, layout);
                var original = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(absolute));
                undo.Add(() => NativeV11Reflection.Call(session.SaveFile, "SaveFile", absolute, original));
                NativeV11Reflection.Call(session.SaveFile, "SaveFile", absolute, prepared.Layout);
                if (request.Address.Kind == ModuleKind.BusinessProcess)
                {
                    NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.BpmnHelper", true)!,
                        "UpdateProcessBindSource", prepared.Incoming, absolute);
                    if (!Equivalent(prepared.NormalizedJson!, session.Serialize(prepared.Route, prepared.Incoming!)))
                        throw new InvalidDataException("The native BPMN binding does not match the supplied process metadata.");
                }
            }
            var modelUndo = ApplyCore(prepared.Route, request.Operation, prepared.Parent,
                prepared.Old, prepared.Incoming, () => Dirty(session, prepared.Route, prepared.Parent, prepared.Old, prepared.Incoming));
            undo.Add(modelUndo);
            if (prepared.Incoming is not null)
            {
                var current = session.Find(request.Address);
                if (!ReferenceEquals(current.Model, prepared.Incoming) ||
                    !Equivalent(prepared.NormalizedJson!, session.Serialize(current.Route, current.Model)))
                    throw new InvalidOperationException("V11 native definition readback differs after editing.");
            }
            else if (prepared.Parent.Contains(prepared.Old!))
                throw new InvalidOperationException("V11 native definition remains in the object tree after deletion.");
            InvalidateLinkedPath(session, request.Address.Kind, prepared.Old);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception primary)
        {
            Rollback(undo, primary);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
            throw;
        }
        var restored = false;
        return () =>
        {
            if (restored) return;
            session.VerifyContext();
            Rollback(undo, null);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
            restored = true;
        };
    }

    private sealed record Prepared(NativeV11Route Route, string Path, IList Parent, object? Old,
        object? Incoming, string? NormalizedJson, string? Layout);

    private static Prepared Prepare(NativeV11Session session, ModuleMutationRequest request)
    {
        session.VerifyContext();
        if (!Supports(request.Address.Kind, request.Operation) || request.Scope != EffectScope.ProjectDefinition)
            throw new NotSupportedException("This V11 definition operation has not been implemented and verified.");
        var route = session.Route(request.Address.Kind);
        if (route.IsPage) throw new InvalidOperationException("Pages use the native worksheet writer.");
        var path = session.NativePath(request.Address);
        var matches = session.Items(request.Address.Kind).Where(item =>
            string.Equals(item.RelativePath, path, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("The native module path is ambiguous.");
        var previous = matches.SingleOrDefault();
        if (request.Operation == ModuleOperation.Create && previous is not null)
            throw new InvalidOperationException("The native definition already exists.");
        if (request.Operation != ModuleOperation.Create && previous is null)
            throw new InvalidOperationException("The native definition does not exist or is not loaded.");
        var parent = previous?.Parent ?? ExistingParent(route, request.Address.FolderPath);
        EnsureLoaded(route.Root);
        if (request.Operation == ModuleOperation.Delete)
        {
            if (request.Document is not null)
                throw new InvalidDataException("A deletion must not include replacement metadata or assets.");
            if (request.Address.Kind == ModuleKind.Notification)
                VerifyNotificationModel(session.Context.DesignerAssembly, previous!.Model);
            return new(route, path, parent, previous!.Model, null, null, null);
        }

        var document = request.Document ?? throw new InvalidDataException("Replacement metadata is required.");
        if (document.Address != request.Address || document.Metadata.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The replacement address or JSON object is invalid.");
        if (request.Address.Kind == ModuleKind.Notification && previous is null)
            ValidateNotificationCreation(document.Metadata);
        var json = document.Metadata.GetRawText();
        if (request.Address.Kind == ModuleKind.ServerCommand && request.Operation == ModuleOperation.Create)
            json = NormalizeNewServerCommandTriggers(document.Metadata);
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new InvalidDataException("The definition exceeds the 16 MB metadata limit.");
        HashSet<Type>? pluginTypes = null;
        CheckTypes(document.Metadata, session.Context.DesignerAssembly.GetName().Version!,
            request.Address.Kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask
                ? type => (pluginTypes ??= NativeV11PluginCommandTypes.Resolve(session.Context.DesignerAssembly, route, document.Metadata)).Contains(type)
                : null);
        var incoming = Load(route, path, json);
        if (request.Address.Kind == ModuleKind.ServerCommand)
        {
            if (NativeV11Reflection.Get(incoming, "Triggers") is not IList ||
                NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.ServerCommands.ServerCommandsExtension", true)!, "IsTaskScheduler", incoming) is not false)
                throw new InvalidDataException("A server command requires a native trigger collection that does not classify it as a scheduled task.");
        }
        if (!string.Equals(NativeV11Reflection.Get(incoming, "Name") as string, request.Address.Name, StringComparison.Ordinal))
            throw new InvalidDataException("The native definition name differs from its address.");
        if (previous is not null)
        {
            if (incoming.GetType() != previous.Model.GetType())
                throw new InvalidDataException("The replacement changes the native definition type.");
            // A shared command's callers resolve its stable ID, not its displayed name.
            if (request.Address.Kind == ModuleKind.SharedCommand)
            {
                var original = NativeV11Reflection.Get(previous.Model, "ID") as string;
                if (string.IsNullOrWhiteSpace(original) ||
                    original != NativeV11Reflection.Get(incoming, "ID") as string)
                    throw new InvalidDataException("Updating a shared command must preserve its native ID.");
            }
            if (request.Address.Kind == ModuleKind.ScheduledTask &&
                !Equals(NativeV11Reflection.Get(previous.Model, "Enabled"), NativeV11Reflection.Get(incoming, "Enabled")))
                throw new InvalidDataException("Definition editing must preserve the schedule's enabled state.");
            foreach (var property in new[] { "Weight", "DesignerSetting", "Loaded", "PermissionData", "AllowAccessRoleList", "AllowViewRoleList", "AllowStartRoleList", "AllowEditRoleList" })
            {
                var target = incoming.GetType().GetProperty(property, NativeV11Reflection.Flags);
                if (target?.SetMethod is not null)
                    target.SetValue(incoming, NativeV11Reflection.Get(previous.Model, property));
            }
        }
        else if (request.Address.Kind == ModuleKind.SharedCommand)
        {
            var id = NativeV11Reflection.Get(incoming, "ID") as string;
            if (string.IsNullOrWhiteSpace(id) || session.Items(ModuleKind.SharedCommand).Any(item =>
                Equals(NativeV11Reflection.Get(item.Model, "ID"), id)))
                throw new InvalidDataException("A new shared command requires a new, nonempty native ID.");
        }
        var normalized = session.Serialize(route, incoming);
        if (!PreservesDefinition(request.Address.Kind, json, normalized))
            throw new InvalidDataException("V11 native roundtrip did not preserve all supplied metadata. Read a native template and submit its complete normalized JSON.");
        if (request.Address.Kind == ModuleKind.Notification)
        {
            VerifyNotificationModel(session.Context.DesignerAssembly, incoming);
            if (previous is null)
            {
                InitializeNotificationPermission(session.Context.DesignerAssembly, incoming);
                normalized = session.Serialize(route, incoming);
            }
        }

        string? layout = null;
        if (request.Address.Kind is ModuleKind.Report or ModuleKind.BusinessProcess)
        {
            var report = request.Address.Kind == ModuleKind.Report;
            RejectActiveLinkedEditor(session.Context.DesignerAssembly, report);
            if (report && NativeV11Reflection.Get(incoming, "ReportType") is Enum reportType &&
                !Enum.IsDefined(reportType.GetType(), reportType))
                throw new InvalidDataException("The report type is not defined by this native V11 build.");
            if (document.Companions.Count != 1)
                throw new InvalidDataException("Linked definitions require exactly one matching native layout asset.");
            var asset = document.Companions[0];
            var expected = Path.ChangeExtension(path, report ? ".rdlx" : ".bpmn").Replace('\\', '/');
            if (!string.Equals(asset.RelativePath, expected, StringComparison.Ordinal))
                throw new InvalidDataException("The layout path is not the native definition companion.");
            var bytes = Convert.FromBase64String(asset.ContentBase64);
            if (bytes.Length > 32 * 1024 * 1024 ||
                !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), asset.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The layout asset size or SHA-256 is invalid.");
            layout = DecodeXml(bytes, report ? "Report" : "definitions");
            // Updates can restore the previous companion through native SaveFile. Creation
            // and deletion remain closed until a native physical-companion removal path is verified.
            if (!File.Exists(Under(session.LocalPath, expected)))
                throw new InvalidDataException("The existing native layout companion is missing.");
        }
        else if (document.Companions.Count != 0)
            throw new InvalidDataException("Command definitions do not own arbitrary companion files.");
        return new(route, path, parent, previous?.Model, incoming, normalized, layout);
    }

    internal static string NormalizeNewServerCommandTriggers(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Server command metadata must be an object.");
        var properties = metadata.EnumerateObject().Where(property => property.NameEquals("Triggers")).ToArray();
        if (properties.Length > 1 || properties.Length == 1 && properties[0].Value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Server command Triggers must be an array when supplied.");
        if (properties.Length == 1) return metadata.GetRawText();
        var root = JsonNode.Parse(metadata.GetRawText())!.AsObject();
        root.Add("Triggers", new JsonArray());
        return root.ToJsonString();
    }

    internal static object Load(NativeV11Route route, string path, string json)
    {
        using var stream = new MemoryStream(new UTF8Encoding(false, true).GetBytes(json), false);
        var saved = NativeV11Reflection.Call(route.Loader, "GenerateObject", path.Replace('/', '\\'), stream)
            ?? throw new InvalidDataException("The V11 native loader returned no save-data model.");
        return NativeV11Reflection.Call(route.Mediator, "ConvertToModel", saved)
            ?? throw new InvalidDataException("The V11 native mediator returned no definition model.");
    }

    // The caller hashes the original request bytes before decoding. Native
    // SaveFile receives normalized XML text; receipts capture its actual saved
    // bytes. Existing UTF-8 bytes used for rollback are decoded separately and
    // deliberately retain their BOM character for byte-exact restoration.
    internal static string DecodeXml(byte[] bytes, string expectedRoot)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        using var reader = XmlReader.Create(new StringReader(text),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        if (XDocument.Load(reader).Root?.Name.LocalName != expectedRoot)
            throw new InvalidDataException("The layout XML root does not match its native definition type.");
        return text;
    }

    // The fixtures call the same production tree/save-memento transaction with real
    // native colleagues. Identity checks happen before native mutations and on rollback.
    internal static Action ApplyCore(NativeV11Route route, ModuleOperation operation, IList parent,
        object? previous, object? incoming, Action invalidate)
    {
        var originalIndex = previous is null ? -1 : parent.IndexOf(previous);
        if (operation is not (ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete) ||
            operation == ModuleOperation.Create && (previous is not null || incoming is null) ||
            operation != ModuleOperation.Create && (previous is null || originalIndex < 0) ||
            operation == ModuleOperation.Update && incoming is null ||
            operation == ModuleOperation.Delete && incoming is not null)
            throw new InvalidOperationException("The definition mutation no longer matches its native tree.");
        var treeChanged = false;
        var nativeAttempted = false;
        var restored = false;
        var stage = "tree";
        var rollbackStage = "tree";
        void Restore()
        {
            if (restored) return;
            if (operation == ModuleOperation.Create)
            {
                if (parent.Contains(incoming!))
                {
                    if (nativeAttempted)
                    {
                        rollbackStage = "native-delete";
                        NativeV11Reflection.Call(route.Colleague, "Delete", incoming);
                    }
                    rollbackStage = "tree-remove";
                    parent.Remove(incoming!);
                }
            }
            else if (operation == ModuleOperation.Update)
            {
                if (treeChanged)
                {
                    if (!ReferenceEquals(parent[originalIndex], incoming))
                        throw new InvalidOperationException("Cannot rollback over a changed native definition.");
                    parent[originalIndex] = previous;
                }
                if (nativeAttempted)
                    NativeV11Reflection.Call(route.Colleague, "Edit", previous, NativeV11Reflection.Get(previous!, "Name"));
            }
            else
            {
                if (treeChanged) parent.Insert(Math.Min(originalIndex, parent.Count), previous);
                if (nativeAttempted) NativeV11Reflection.Call(route.Colleague, "Insert", previous);
            }
            rollbackStage = "native-cache";
            if (route.WholeCache) NativeV11Reflection.Call(route.Colleague, "SaveToCache", true);
            rollbackStage = "generation-invalidation";
            invalidate();
            restored = true;
        }
        try
        {
            if (operation == ModuleOperation.Create)
            {
                parent.Add(incoming);
                treeChanged = true;
                nativeAttempted = true;
                stage = "native-insert";
                NativeV11Reflection.Call(route.Colleague, "Insert", incoming);
            }
            else if (operation == ModuleOperation.Update)
            {
                parent[originalIndex] = incoming;
                treeChanged = true;
                nativeAttempted = true;
                NativeV11Reflection.Call(route.Colleague, "Edit", incoming, NativeV11Reflection.Get(previous!, "Name"));
            }
            else
            {
                // Resolve the native key while the original object still belongs to its tree.
                _ = NativeV11Reflection.Call(route.Colleague, "GetKey", previous);
                nativeAttempted = true;
                NativeV11Reflection.Call(route.Colleague, "Delete", previous);
                parent.RemoveAt(originalIndex);
                treeChanged = true;
            }
            stage = "native-cache";
            if (route.WholeCache) NativeV11Reflection.Call(route.Colleague, "SaveToCache", true);
            stage = "generation-invalidation";
            invalidate();
            return Restore;
        }
        catch (Exception primary)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new AggregateException("The V11 definition mutation failed and native rollback was incomplete; do not save this document. " +
                "stage=" + stage + "; " + NativeV11DefinitionLoading.SafeFailure(primary) + "; rollbackStage=" + rollbackStage + "; " + NativeV11DefinitionLoading.SafeFailure(rollback)); }
            throw;
        }
    }

    private static IList ExistingParent(NativeV11Route route, string? folderPath)
    {
        object node = route.Root;
        foreach (var part in (folderPath ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") throw new InvalidDataException("Invalid native folder address.");
            EnsureLoaded(node);
            var matches = ((IList)(NativeV11Reflection.Get(node, "Items") ?? throw new InvalidDataException("Native folder has no child collection.")))
                .Cast<object>().Where(item => NativeV11Reflection.Get(item, "Name") as string == part &&
                    NativeV11Reflection.Get(item, "Items") is IList).ToArray();
            if (matches.Length != 1) throw new InvalidDataException("Create the target native folder separately before creating a definition.");
            node = matches[0];
        }
        EnsureLoaded(node);
        return (IList)(NativeV11Reflection.Get(node, "Items") ?? throw new InvalidDataException("Native folder has no child collection."));
    }

    private static void EnsureLoaded(object node)
    {
        if (NativeV11Reflection.Get(node, "Loaded") is false)
            throw new InvalidOperationException("The entire native module tree must be loaded before rebuilding its save cache.");
        if (NativeV11Reflection.Get(node, "Items") is IList items)
            foreach (var child in items.Cast<object>()) EnsureLoaded(child);
    }

    internal static bool Equivalent(string left, string right) => JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right));

    internal static bool PreservesDefinition(ModuleKind kind, string left, string right) => Equivalent(left, right) ||
        (kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask &&
         NativeV11CommandSaveSnapshot.EquivalentCommandProjection(left, right));

    internal static void CheckTypes(JsonElement value, Version version, Func<Type, bool>? trustedPluginType = null)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Ambiguous JSON property names are not allowed.");
                if (property.Name == "$type")
                {
                    if (property.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid native JSON type metadata.");
                    var name = property.Value.GetString()!;
                    // A type tag is metadata, not permission to instantiate arbitrary CLR objects.
                    var type = Type.GetType(name, assemblyName => AppDomain.CurrentDomain.GetAssemblies()
                        .SingleOrDefault(assembly => assembly.GetName().Name == assemblyName.Name), null, false)
                        ?? throw new InvalidDataException("The native JSON type is not loaded in the matched designer.");
                    if (!Allowed(type, version, trustedPluginType)) throw new InvalidDataException("The JSON type is outside the native definition model allowlist.");
                }
                CheckTypes(property.Value, version, trustedPluginType);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckTypes(item, version, trustedPluginType);
    }

    private static bool Allowed(Type type, Version version, Func<Type, bool>? trustedPluginType = null)
    {
        if (type.IsArray) return Allowed(type.GetElementType()!, version, trustedPluginType);
        if (type.IsPrimitive || type == typeof(string) || type == typeof(object) || type == typeof(decimal) ||
            type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan)) return true;
        if (type.IsGenericType)
        {
            var generic = type.GetGenericTypeDefinition();
            if (generic == typeof(List<>) || generic == typeof(Dictionary<,>) || generic == typeof(HashSet<>) ||
                generic == typeof(KeyValuePair<,>) || generic == typeof(ValueTuple<,>) || generic == typeof(Tuple<,>) ||
                generic == typeof(System.ComponentModel.BindingList<>)) return type.GetGenericArguments().All(item => Allowed(item, version, trustedPluginType));
        }
        var identity = type.Assembly.GetName();
        var name = type.FullName ?? "";
        return identity.Version == version && Convert.ToHexString(identity.GetPublicKeyToken() ?? []) == "967100671921C115" &&
            (type.IsEnum || IsCellType(type) || AllowedPermissionDto(type, version) ||
             name.StartsWith("Forguncy.Model.", StringComparison.Ordinal) || name.StartsWith("Forguncy.SaveLoad.", StringComparison.Ordinal) ||
             name.StartsWith("Forguncy.ServerCommands.Models.", StringComparison.Ordinal) || name.StartsWith("Forguncy.Notification.", StringComparison.Ordinal) || name.StartsWith("ServerDesignerCommon.", StringComparison.Ordinal) ||
             name.StartsWith("GrapeCity.Forguncy.Commands.", StringComparison.Ordinal) || name.StartsWith("Forguncy.Commands.", StringComparison.Ordinal) ||
             CellModelTypes.GetValue(type.Assembly, CellHelpers).Contains(type)) || trustedPluginType?.Invoke(type) is true;
    }

    private static bool AllowedPermissionDto(Type type, Version version)
    {
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters ||
            type.Assembly.GetName().Name != "Forguncy.RbacPermission.Core") return false;
        var group = type.Namespace;
        if (group is not ("Forguncy.RbacPermission.Core.Impl.Data" or "Forguncy.RbacPermission.Core.Impl.Page" or
            "Forguncy.RbacPermission.Core.Impl.ActiveReport" or "Forguncy.RbacPermission.Core.Impl.ServerNotification" or
            "Forguncy.RbacPermission.Core.Impl.BpmProcess")) return false;
        if (group == "Forguncy.RbacPermission.Core.Impl.Data")
        {
            var contract = type.Name switch
            {
                "CreateRecordBindingConfig" or "RowBindingConfig" or "FieldBindingConfig" => "Forguncy.RbacPermission.Core.Impl.Data.IDataBindingConfig",
                "CreateRecordFilter" or "RowFilter" or "FieldFilter" => "Forguncy.RbacPermission.Core.Impl.Data.IDataFilter",
                _ => null
            };
            if (contract is not null)
                return type.GetInterfaces().Any(item => item.FullName == contract) && TrustedPermissionComponent(type.Assembly, version);
            if (type.Name == "FollowMasterDetail")
                return TrustedPermissionComponent(type.Assembly, version) &&
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance).All(property => property.PropertyType == typeof(string)) &&
                    type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "RelatedColumnName", "SubColumnName", "SubTableName" }, StringComparer.Ordinal);
        }
        if (!type.Name.EndsWith("PermissionData", StringComparison.Ordinal) &&
            !type.Name.EndsWith("PermissionResource", StringComparison.Ordinal) &&
            !type.Name.EndsWith("PermissionBinding", StringComparison.Ordinal)) return false;
        var contracts = type.GetInterfaces().Where(contract => contract.FullName is
            "Forguncy.RbacPermission.Abstractions.Model.IPermissionData" or
            "Forguncy.RbacPermission.Abstractions.Model.IPermissionResource" or
            "Forguncy.RbacPermission.Abstractions.Model.IPermissionBinding").ToArray();
        return contracts.Length > 0 && TrustedPermissionComponent(type.Assembly, version) &&
            contracts.All(contract => TrustedPermissionComponent(contract.Assembly, version));
    }

    private static bool TrustedPermissionComponent(Assembly assembly, Version version)
    {
        if (assembly.GetName().Version != version) return false;
        return PermissionComponents.GetValue(assembly, value =>
        {
            var expected = (version.ToString(), value.GetName().Name) switch
            {
                ("11.0.104.0", "Forguncy.RbacPermission.Core") => "F26DDF36115C68E4CEBAC25E206EA894983B5EEE3BA7A4DE03EDEAB796194303",
                ("11.0.104.0", "Forguncy.RbacPermission.Abstractions") => "5CFEE859983AF97CF50027C1A74DB337D208BFA16B470C598BD44E225AFFED0C",
                ("11.0.3.0", "Forguncy.RbacPermission.Core") => "9C59704C9BD86288C6A408084C52522D9BA55BE65BFFBFEC946D8A4848C42266",
                ("11.0.3.0", "Forguncy.RbacPermission.Abstractions") => "B9681E5871EB6E6D01A9797E3C23AB4E979D9D6B4F1E9A41A606400B8D0CBAD6",
                _ => null
            };
            if (expected is null || string.IsNullOrEmpty(value.Location)) return new(false);
            using var stream = File.OpenRead(value.Location);
            return new(Convert.ToHexString(SHA256.HashData(stream)) == expected);
        }).Trusted;
    }

    internal static void VerifyPermissionComponents(Assembly designer)
    {
        var version = Version.Parse(V11BuildProfile.Resolve(designer).Version);
        foreach (var name in new[] { "Forguncy.RbacPermission.Core", "Forguncy.RbacPermission.Abstractions" })
        {
            var component = Assembly.Load(name);
            if (!TrustedPermissionComponent(component, version))
                throw new NotSupportedException("The native V11 permission component does not match the verified build: " + name);
        }
    }

    internal static void ValidateNotificationCreation(JsonElement metadata)
    {
        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("New notification permissions are initialized by the native permission factory.");
            if (property.Name.Equals("AllowAccessRoleList", StringComparison.OrdinalIgnoreCase) && !EmptyGrants(property.Value))
                throw new InvalidDataException("Notification creation cannot supply legacy role grants.");
        }
        static bool EmptyGrants(JsonElement value) => value.ValueKind == JsonValueKind.Null ||
            value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0 ||
            value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$values", out var entries) &&
            entries.ValueKind == JsonValueKind.Array && entries.GetArrayLength() == 0 &&
            value.EnumerateObject().All(property => property.Name is "$type" or "$values");
    }

    internal static void InitializeNotificationPermission(Assembly designer, object model)
    {
        var profile = V11BuildProfile.Resolve(designer);
        VerifyNotificationModel(designer, model);
        var factory = NativeV11Reflection.Static(designer.GetType(profile.Version == "11.0.104.0" ? "jx" : "jr", true)!, "get_Instance")!;
        // true is the exact NotificationsFolderViewModel creation path. It
        // creates the native default binding; no notification is published.
        var permission = NativeV11Reflection.Call(factory, "CreateForDesigner", true)
            ?? throw new InvalidOperationException("The native notification permission factory returned no permission data.");
        AssignNotificationPermission(designer, model, permission);
    }

    internal static void AssignNotificationPermission(Assembly designer, object model, object permission)
    {
        VerifyNotificationModel(designer, model);
        if (!permission.GetType().GetInterfaces().Any(contract =>
                contract.FullName == "Forguncy.RbacPermission.Core.Impl.ServerNotification.IServerNotificationPermissionData") ||
            !TrustedPermissionComponent(permission.GetType().Assembly, designer.GetName().Version!))
            throw new InvalidOperationException("The native notification permission data does not match this verified V11 build.");
        if (NativeV11Reflection.Get(permission, "PermissionResource") is null)
            throw new InvalidOperationException("The native notification permission resource is unavailable.");
        NativeV11Reflection.Set(model, "PermissionData", permission);
    }

    private static void VerifyNotificationModel(Assembly designer, object model)
    {
        VerifyPermissionComponents(designer);
        if (model.GetType().FullName != "Forguncy.Notification.NotificationModel" ||
            model.GetType().Assembly.GetName().Version != designer.GetName().Version ||
            model.GetType().GetProperty("PermissionData", NativeV11Reflection.Flags)?.PropertyType.FullName !=
                "Forguncy.RbacPermission.Core.Impl.ServerNotification.IServerNotificationPermissionData" ||
            model.GetType().GetProperty("AllowAccessRoleList", NativeV11Reflection.Flags) is null)
            throw new InvalidOperationException("The native notification identity or permission contract differs from the verified V11 build.");
        if (NativeV11Reflection.Get(model, "PermissionData") is object permission &&
            (!TrustedPermissionComponent(permission.GetType().Assembly, designer.GetName().Version!) ||
             !permission.GetType().GetInterfaces().Any(contract => contract.FullName ==
                 "Forguncy.RbacPermission.Core.Impl.ServerNotification.IServerNotificationPermissionData")))
            throw new InvalidOperationException("The existing notification permission data does not belong to the verified native V11 build.");
    }

    private static bool IsCellType(Type type)
    {
        for (var current = type; current is not null; current = current.BaseType)
            if (current.FullName == "GrapeCity.Forguncy.CellTypes.CellType") return true;
        return false;
    }

    // Chart, form, flex and free-layout helpers are reached through typed native
    // cell-model properties. Their presence in an arbitrary vendor DLL alone
    // does not grant permission to instantiate a designer/service object.
    private static HashSet<Type> CellHelpers(Assembly assembly)
    {
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException error) { types = error.Types.Where(item => item is not null).Cast<Type>().ToArray(); }
        var version = assembly.GetName().Version;
        var set = new HashSet<Type>();
        var pending = new Queue<Type>(types.Where(IsCellType));
        void Enqueue(Type candidate)
        {
            if (candidate.IsArray) { Enqueue(candidate.GetElementType()!); return; }
            if (candidate.IsGenericType)
                foreach (var argument in candidate.GetGenericArguments()) Enqueue(argument);
            if (candidate.Assembly.GetName().Version == version &&
                Convert.ToHexString(candidate.Assembly.GetName().GetPublicKeyToken() ?? []) == "967100671921C115" &&
                !candidate.IsPointer && !candidate.IsByRef && set.Add(candidate)) pending.Enqueue(candidate);
        }
        while (pending.TryDequeue(out var model))
        {
            foreach (var property in model.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (property.GetIndexParameters().Length == 0) Enqueue(property.PropertyType);
            foreach (var field in model.GetFields(BindingFlags.Public | BindingFlags.Instance)) Enqueue(field.FieldType);
        }
        return set;
    }

    internal static (object Model, string State) GenerationChange(IList parent, object? original, object? incoming)
    {
        var model = incoming is not null && parent.Contains(incoming) ? incoming : original ?? incoming
            ?? throw new InvalidOperationException("The native definition transaction has no generation target.");
        return (model, parent.Contains(model) ? "Edit" : "Remove");
    }

    internal static (object Model, string State) GenerationChange(NativeV11Route route, IList parent, object? original, object? incoming)
    {
        var change = GenerationChange(parent, original, incoming);
        if (route.Kind == ModuleKind.Notification && original is null && incoming is not null && parent.Contains(incoming))
            change.State = "New";
        return change;
    }

    private static void Dirty(NativeV11Session session, NativeV11Route route, IList parent, object? original, object? incoming)
    {
        var change = GenerationChange(route, parent, original, incoming);
        // Commands own per-name data caches. The notification model factory is
        // also verified; linked definitions retain the whole-module contract.
        if (route.Kind is ModuleKind.ServerCommand or ModuleKind.SharedCommand or ModuleKind.ScheduledTask or ModuleKind.Notification)
            NativeV11Generation.MarkModel(session, change.Model, change.State);
        NativeV11Generation.MarkModules(session, route, change.State);
    }

    private static void RejectActiveLinkedEditor(Assembly assembly, bool report)
    {
        var type = assembly.GetType(report ? "Forguncy.Common.ReportsManager" : "Forguncy.Common.BusinessProcessManager", true)!;
        var field = type.GetField("_instance", NativeV11Reflection.Flags)
            ?? throw new MissingFieldException(type.FullName, "_instance");
        if (field.GetValue(null) is not object manager) return;
        foreach (var name in report ? new[] { "InARDesignerMode", "IsReportDataSourceEditingMode" } : new[] { "InBPMDesignerMode" })
        {
            var property = type.GetProperty(name, NativeV11Reflection.Flags)
                ?? throw new MissingMemberException(type.FullName, name);
            if (property.GetValue(manager) is not false)
                throw new InvalidOperationException("Close the native report/process editor before replacing its metadata and layout.");
        }
    }

    private static void InvalidateLinkedPath(NativeV11Session session, ModuleKind kind, object? old)
    {
        if (old is null || kind is not (ModuleKind.Report or ModuleKind.BusinessProcess)) return;
        var type = session.Context.DesignerAssembly.GetType(kind == ModuleKind.Report ? "Forguncy.Common.ReportsManager" : "Forguncy.Common.BusinessProcessManager", true)!;
        var field = type.GetField("_instance", NativeV11Reflection.Flags)
            ?? throw new MissingFieldException(type.FullName, "_instance");
        if (field.GetValue(null) is not object manager) return;
        if (NativeV11Reflection.Get(manager, kind == ModuleKind.Report ? "ReportPathCache" : "ProcessPathCache") is not IDictionary cache)
            throw new InvalidOperationException("The native report path cache is unavailable.");
        cache.Remove(old);
    }

    private static string Under(string root, string relative)
    {
        if (Path.IsPathRooted(relative) || relative.Contains(':') || relative.Split('/', '\\').Any(part => part is "." or ".."))
            throw new InvalidDataException("Invalid native companion path.");
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The companion path leaves its native working root.");
        for (var parent = Path.GetDirectoryName(path); parent is not null && parent.Length >= fullRoot.Length; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Native companion paths must not traverse symbolic links.");
        return path;
    }

    private static void Rollback(IReadOnlyList<Action> undo, Exception? primary)
    {
        var errors = new List<Exception>();
        for (var index = undo.Count - 1; index >= 0; index--)
            try { undo[index](); } catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0)
            throw new AggregateException("V11 native rollback was incomplete; do not save this document.",
                primary is null ? errors : new[] { primary }.Concat(errors));
    }
}
