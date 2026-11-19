using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Definition-only JSON/BPMN lifecycle. No process-manager save, engine,
// deployment or database API is called. Reference extraction uses the
// native BPMN parser against an already validated private temporary file.
internal static class NativeV11Processes
{
    private const string BpmnNamespace = "http://www.omg.org/spec/BPMN/20100524/MODEL";
    private sealed record Prepared(NativeV11Route Route, string Path, IList Parent, object? Previous,
        object? Incoming, string? Layout, object Manager);
    internal sealed record ValidatedBpmn(string Layout, string Key);

    internal static bool Supports(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.BusinessProcess &&
        operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete;

    internal static void Validate(NativeV11Session session, ModuleMutationRequest request) => Prepare(session, request);

    // Candidate validation is read-only; it does not authorize initialization.
    internal static void ValidateFirstCandidate(NativeV11Session session, ModuleMutationRequest request) => Prepare(session, request, allowFirstCandidate: true);

    internal static Action Apply(NativeV11Session session, ModuleMutationRequest request)
        => ApplyCore(session, request, allowFirstCandidate: false);

    // Used only from the pinned first-initialization action after its caller
    // has captured and revalidated the complete native owner/source plan.
    internal static Action ApplyFirstCandidate(NativeV11Session session, ModuleMutationRequest request)
        => ApplyCore(session, request, allowFirstCandidate: true);

    private static Action ApplyCore(NativeV11Session session, ModuleMutationRequest request, bool allowFirstCandidate)
    {
        var prepared = Prepare(session, request, allowFirstCandidate);
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var undo = NativeV11Reports.ApplyCore(session.Context.DesignerAssembly, prepared.Route, session.SaveFile,
            prepared.Path, request.Operation, prepared.Parent, prepared.Previous, prepared.Incoming, prepared.Layout,
            (model, state) => NativeV11Generation.MarkModel(session, model, state),
            NativeV11Reflection.Get(prepared.Manager, "ProcessPathCache") as IDictionary);
        try
        {
            if (request.Operation == ModuleOperation.Delete)
            {
                if (session.Items(ModuleKind.BusinessProcess).Any(item => item.Address == request.Address))
                    throw new InvalidOperationException("The deleted process remains in its native definition tree.");
            }
            else
            {
                var actual = session.Read(session.Find(request.Address), true);
                if (!NativeV11DefinitionWriter.Equivalent(session.Serialize(prepared.Route, prepared.Incoming!), actual.Metadata.GetRawText()) ||
                    actual.Companions.Count != 1 || !Convert.FromBase64String(actual.Companions[0].ContentBase64)
                        .AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(prepared.Layout!)))
                    throw new InvalidOperationException("The native process metadata/BPMN pair differs after mutation.");
            }
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception primary)
        {
            try { undo(); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty); }
            catch (Exception rollback) { throw new AggregateException("Native process mutation failed and rollback is not confirmed.", primary, rollback); }
            throw;
        }
        var restored = false;
        return () =>
        {
            if (restored) return;
            session.VerifyContext(); undo();
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
            restored = true;
        };
    }

    private static Prepared Prepare(NativeV11Session session, ModuleMutationRequest request, bool allowFirstCandidate = false)
    {
        session.VerifyContext();
        if (!Supports(request.Address.Kind, request.Operation) || request.Scope != EffectScope.ProjectDefinition)
            throw new BridgeException("ProcessOperationUnavailable", "Process editing changes project definitions only.");
        if (!allowFirstCandidate && request.Operation == ModuleOperation.Create && NativeV11ProcessInitialization.IsFirst(session))
            throw new BridgeException("NativeProcessInitializationUnavailable", "First-process creation requires the complete native initialization owner and source-save plan.");
        V11Safety.Address(request.Address);
        NativeV11StaticFiles.VerifyConfigOwner(session);
        var registry = NativeV11StaticFiles.Resolve(session);
        if (!registry.Folders.Any(folder => folder.Replace('\\', '/').Trim('/') == "Process"))
            throw new InvalidOperationException("The native static-file saver does not own the process directory.");
        var route = session.Route(ModuleKind.BusinessProcess);
        var path = session.NativePath(request.Address);
        var bpmnPath = Path.ChangeExtension(path, ".bpmn").Replace('\\', '/');
        var items = session.Items(ModuleKind.BusinessProcess);
        var matches = items.Where(item => item.Address == request.Address).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The native process address is ambiguous.");
        var previous = matches.SingleOrDefault();
        if (request.Operation == ModuleOperation.Create)
        {
            if (items.Any(item => item.Address.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                NativeV11PendingSave.ReadFiles(session.SourcePath, [path, bpmnPath]).Values.Any(bytes => bytes is not null) ||
                new[] { session.LocalPath, session.LastPath }.Any(root => File.Exists(V11Safety.Under(root, path)) || File.Exists(V11Safety.Under(root, bpmnPath))))
                throw new BridgeException("ProcessAlreadyExists", "The native process name or its complete file group already exists.");
        }
        else if (previous is null) throw new BridgeException("ProcessNotFound", "No unique loaded native process matches the address.");
        var parent = previous?.Parent ?? Parent(route, request.Address.FolderPath);
        var manager = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.BusinessProcessManager", true)!, "get_Instance")
            ?? throw new InvalidOperationException("The native process manager is unavailable.");
        if (NativeV11Reflection.Get(manager, "InBPMDesignerMode") is not false || NativeV11Reflection.Get(manager, "CurrentActiveBPM") is not null ||
            NativeV11Reflection.Get(manager, "DeployProcessServerTask") is Task { IsCompleted: false })
            throw new BridgeException("ProcessEditorOpen", "Close the active native process editor and finish any pending native deployment before editing definitions.");
        var processRoot = NativeV11Reflection.Get(manager, "LocalProcessPath") as string;
        if (processRoot is null || !Path.GetFullPath(processRoot).TrimEnd('\\', '/').Equals(
                Path.GetFullPath(V11Safety.Under(session.LocalPath, "Process")).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ProcessOwnerMismatch", "The native process manager belongs to another save session.");
        if (request.Operation == ModuleOperation.Delete)
        {
            if (request.Document is not null) throw new InvalidDataException("Process deletion cannot supply replacement JSON or BPMN.");
            _ = session.Read(previous!);
            return new(route, path, parent, previous!.Model, null, null, manager);
        }
        var document = request.Document ?? throw new InvalidDataException("A complete native process JSON/BPMN pair is required.");
        if (document.Address != request.Address || document.Metadata.ValueKind != JsonValueKind.Object || document.Companions.Count != 1)
            throw new InvalidDataException("The process identity or companion group is invalid.");
        NativeV11DefinitionWriter.CheckTypes(document.Metadata, session.Context.DesignerAssembly.GetName().Version!);
        var json = document.Metadata.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new InvalidDataException("The process metadata is too large.");
        V11Safety.Companion(document.Companions[0], bpmnPath);
        var incoming = NativeV11DefinitionWriter.Load(route, path, json);
        if (incoming.GetType().FullName != "Forguncy.Model.BusinessProcessModel" ||
            NativeV11Reflection.Get(incoming, "Name") as string != request.Address.Name ||
            !NativeV11DefinitionWriter.Equivalent(json, session.Serialize(route, incoming)))
            throw new BridgeException("ProcessIdentityMismatch", "The native process loader did not preserve its exact identity and metadata.");
        var layout = PrepareBpmnCore(session.Context.DesignerAssembly, route, incoming, json,
            Convert.FromBase64String(document.Companions[0].ContentBase64));
        if (previous is null)
        {
            foreach (var property in document.Metadata.EnumerateObject())
            {
                if (property.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("ProcessPermissionInitializationRequired", "Native default process grants are initialized by the native permission factory.");
                if (property.Name is "AllowStartRoleList" or "AllowEditRoleList" && property.Value.ValueKind != JsonValueKind.Null &&
                    (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() != 0))
                    throw new BridgeException("ProcessPermissionInitializationRequired", "Process creation cannot supply legacy role grants.");
            }
            InitializePermission(session.Context.DesignerAssembly, incoming);
        }
        else
        {
            var before = session.Read(previous);
            foreach (var field in new[] { "Name", "ID", "Id", "GUID", "Guid", "PermissionData", "AllowStartRoleList", "AllowEditRoleList", "FolderPath", "Path" })
            {
                var oldPresent = before.Metadata.TryGetProperty(field, out var a);
                var newPresent = document.Metadata.TryGetProperty(field, out var b);
                if (oldPresent != newPresent || oldPresent && !V11Safety.Equivalent(a, b))
                    throw new BridgeException("ProtectedProcessIdentity", "Process identity and permission grants must remain unchanged: " + field);
            }
            var previousLayout = before.Companions.SingleOrDefault(asset => asset.RelativePath == bpmnPath)
                ?? throw new InvalidDataException("The original process BPMN companion is unavailable.");
            if (ValidateBpmnCore(session.Context.DesignerAssembly, Convert.FromBase64String(previousLayout.ContentBase64)).Key !=
                ValidateBpmnCore(session.Context.DesignerAssembly, Encoding.UTF8.GetBytes(layout)).Key)
                throw new BridgeException("ProtectedProcessKey", "Updating a process must preserve its native BPMN process key.");
            foreach (var property in new[] { "Weight", "PermissionData", "AllowStartRoleList", "AllowEditRoleList" })
                if (incoming.GetType().GetProperty(property, NativeV11Reflection.Flags) is { SetMethod: not null } target)
                    target.SetValue(incoming, NativeV11Reflection.Get(previous.Model, property));
        }
        // This native classifier requires the actual bound document's process
        // database settings. Component fixtures deliberately do not fabricate it.
        _ = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", incoming)
            ?? throw new InvalidOperationException("The native process module classifier is unavailable.");
        return new(route, path, parent, previous?.Model, incoming, layout, manager);
    }

    internal static void InitializePermission(Assembly designer, object model)
    {
        NativeV11DefinitionWriter.VerifyPermissionComponents(designer);
        var newer = V11BuildProfile.Resolve(designer).Version == "11.0.104.0";
        var factory = NativeV11Reflection.Static(designer.GetType(newer ? "jr" : "jl", true)!, "get_Instance")!;
        var permission = NativeV11Reflection.Call(factory, "CreateForDesigner", true)
            ?? throw new InvalidOperationException("The native process permission factory returned no permission data.");
        NativeV11Reports.AssignPermission(designer, model, permission);
    }

    internal static string PrepareBpmnCore(Assembly designer, NativeV11Route route, object model, string canonicalJson, byte[] bytes)
    {
        if (route.Kind != ModuleKind.BusinessProcess || model.GetType().FullName != "Forguncy.Model.BusinessProcessModel")
            throw new InvalidDataException("BPMN binding requires the exact native process model and route.");
        if (!NativeV11DefinitionWriter.Equivalent(canonicalJson, Serialize(designer, route, model)))
            throw new InvalidDataException("Unknown or noncanonical process metadata must be rejected before native reference extraction.");
        var validated = ValidateBpmnCore(designer, bytes);
        var stage = Path.Combine(Path.GetTempPath(), "ForguncyDesignerMcp-bpmn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        var file = Path.Combine(stage, "definition.bpmn");
        try
        {
            File.WriteAllText(file, validated.Layout, new UTF8Encoding(false)); // Private parser input only.
            NativeV11Reflection.Static(designer.GetType("Forguncy.Common.BpmnHelper", true)!, "UpdateProcessBindSource", model, file);
            if (!File.ReadAllBytes(file).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(validated.Layout)) ||
                !NativeV11DefinitionWriter.Equivalent(canonicalJson, Serialize(designer, route, model)))
                throw new BridgeException("ProcessBindingMismatch", "The native BPMN references do not match the supplied normalized process metadata.");
            var nativeKey = NativeV11Reflection.Static(designer.GetType("Forguncy.Common.BpmnHelper", true)!, "GetBPMNXmlProcessKey", validated.Layout) as string;
            if (nativeKey != validated.Key) throw new InvalidDataException("The native BPMN parser did not retain the unique process key.");
            return validated.Layout;
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            if (Directory.Exists(stage)) Directory.Delete(stage, false);
        }
    }

    internal static ValidatedBpmn ValidateBpmnCore(Assembly designer, byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("BPMN exceeds the definition size limit.");
        var layout = NativeV11DefinitionWriter.DecodeXml(bytes, "definitions");
        using var reader = XmlReader.Create(new StringReader(layout), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16 * 1024 * 1024 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        XNamespace ns = BpmnNamespace;
        if (document.Root?.Name != ns + "definitions") throw new InvalidDataException("The BPMN root namespace is invalid.");
        var processes = document.Descendants(ns + "process").ToArray();
        if (processes.Length != 1 || processes[0].Parent != document.Root)
            throw new InvalidDataException("BPMN must contain one direct process definition.");
        var key = (string?)processes[0].Attribute("id");
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("The BPMN process key is missing.");
        _ = XmlConvert.VerifyNCName(key);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.Root.DescendantsAndSelf())
        {
            if (element.Attribute("id") is { } id)
            {
                if (string.IsNullOrWhiteSpace(id.Value) || !ids.Add(id.Value))
                    throw new InvalidDataException("BPMN contains an empty or duplicate identifier.");
                _ = XmlConvert.VerifyNCName(id.Value);
            }
            foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration)) InspectEmbedded(attribute.Value, 0);
            foreach (var text in element.Nodes().OfType<XText>()) InspectEmbedded(text.Value, 0);
        }
        var nodes = processes[0].Descendants().Where(element => element.Name.Namespace == ns && element.Name.LocalName != "sequenceFlow" && element.Attribute("id") is not null)
            .Select(element => (string)element.Attribute("id")!).ToHashSet(StringComparer.Ordinal);
        foreach (var flow in processes[0].Descendants(ns + "sequenceFlow"))
            if (flow.Attribute("id") is null || !nodes.Contains((string?)flow.Attribute("sourceRef") ?? "") || !nodes.Contains((string?)flow.Attribute("targetRef") ?? ""))
                throw new InvalidDataException("A BPMN sequence flow has a missing or unresolved endpoint.");
        return new(layout, key);

        void InspectEmbedded(string text, int depth)
        {
            if (depth > 12) throw new InvalidDataException("Embedded BPMN JSON nesting exceeds the definition limit.");
            var trimmed = text.Trim();
            if (trimmed.Length == 0) return;
            if (trimmed[0] is '{' or '[' or '"')
            {
                try
                {
                    using var json = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 128 });
                    NativeV11DefinitionWriter.CheckTypes(json.RootElement, designer.GetName().Version!);
                    void Walk(JsonElement value)
                    {
                        if (value.ValueKind == JsonValueKind.String) InspectEmbedded(value.GetString()!, depth + 1);
                        else if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) Walk(property.Value);
                        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Walk(item);
                    }
                    Walk(json.RootElement);
                    return;
                }
                catch (JsonException) { }
            }
            if (trimmed.Contains("$type", StringComparison.OrdinalIgnoreCase) || trimmed.Contains("\\u0024", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Embedded BPMN type metadata is not a registered native JSON model.");
        }
    }

    private static string Serialize(Assembly designer, NativeV11Route route, object model) => (string)NativeV11Reflection.Static(
        designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!, "SaveToStr", NativeV11Reflection.Call(route.Mediator, "ConvertToSaveData", model))!;

    private static IList Parent(NativeV11Route route, string? folder)
    {
        var current = route.Root;
        foreach (var part in NativeV11Session.NormalizeFolder(folder).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NativeV11Reflection.Get(current, "Loaded") is not true) throw new InvalidOperationException("The native process folder is not fully loaded.");
            current = ((IList)NativeV11Reflection.Get(current, "Items")!).Cast<object>().SingleOrDefault(item =>
                NativeV11Reflection.Get(item, "Items") is IList && NativeV11Reflection.Get(item, "Name") as string == part)
                ?? throw new InvalidOperationException("Create the native parent folder before creating a process.");
        }
        if (NativeV11Reflection.Get(current, "Loaded") is not true) throw new InvalidOperationException("The native process parent is not fully loaded.");
        return NativeV11Reflection.Get(current, "Items") as IList ?? throw new InvalidDataException("The native process parent has no item collection.");
    }
}
