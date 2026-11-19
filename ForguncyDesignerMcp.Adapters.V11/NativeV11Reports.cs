using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

// Definition-only report lifecycle. Supplied RDLX uses native SaveFile and the
// report tree/mediator/memento; no AR editor, report server or rendering starts.
internal static class NativeV11Reports
{
    internal static bool Supports(ModuleKind kind, ModuleOperation operation) => kind == ModuleKind.Report &&
        operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete;

    private sealed record Prepared(NativeV11Route Route, string Path, IList Parent, object? Previous,
        object? Incoming, string? Layout, object Manager);

    internal static void Validate(NativeV11Session session, ModuleMutationRequest request)
    {
        var phase = "context";
        try { Prepare(session, request, current => phase = current); }
        catch (BridgeException) { throw; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException!;
            throw new BridgeException("NativeReportValidationIncomplete", "Native report validation incomplete; phase=" + phase + "; error=" + error.GetType().Name + ".");
        }
    }

    internal static Action Apply(NativeV11Session session, ModuleMutationRequest request)
    {
        var prepared = Prepare(session, request);
        var dirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var undo = ApplyCore(session.Context.DesignerAssembly, prepared.Route, session.SaveFile,
            prepared.Path, request.Operation, prepared.Parent, prepared.Previous, prepared.Incoming, prepared.Layout,
            (model, state) => NativeV11Generation.MarkModel(session, model, state),
            NativeV11Reflection.Get(prepared.Manager, "ReportPathCache") as IDictionary);
        var restored = false;
        try
        {
            if (request.Operation == ModuleOperation.Delete)
            {
                if (session.Items(ModuleKind.Report).Any(item => item.Address == request.Address))
                    throw new InvalidOperationException("The deleted report remains in the native report tree.");
            }
            else
            {
                var actual = session.Read(session.Find(request.Address), true);
                if (!NativeV11DefinitionWriter.Equivalent(session.Serialize(prepared.Route, prepared.Incoming!), actual.Metadata.GetRawText()) ||
                    !Convert.FromBase64String(actual.Companions.Single().ContentBase64).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(prepared.Layout!)))
                    throw new InvalidOperationException("The native report metadata/layout pair differs after mutation.");
            }
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
        }
        catch (Exception primary)
        {
            try { undo(); NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty); }
            catch (Exception rollback) { throw new AggregateException("Native report mutation failed and rollback is not confirmed.", primary, rollback); }
            throw;
        }
        return () =>
        {
            if (restored) return;
            session.VerifyContext(); undo();
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", dirty);
            restored = true;
        };
    }

    private static Prepared Prepare(NativeV11Session session, ModuleMutationRequest request, Action<string>? phase = null)
    {
        session.VerifyContext();
        if (!Supports(request.Address.Kind, request.Operation) || request.Scope != EffectScope.ProjectDefinition)
            throw new BridgeException("ReportOperationUnavailable", "Report editing changes project definitions only.");
        V11Safety.Address(request.Address);
        phase?.Invoke("configured-owner");
        NativeV11StaticFiles.VerifyConfigOwner(session);
        var route = session.Route(ModuleKind.Report);
        phase?.Invoke("static-file-owner");
        VerifyStaticFileRegistration(session);
        var path = session.NativePath(request.Address);
        var layoutPath = Path.ChangeExtension(path, ".rdlx").Replace('\\', '/');
        phase?.Invoke("native-report-tree");
        var items = session.Items(ModuleKind.Report);
        var matches = items.Where(item => item.Address == request.Address).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("The native report address is ambiguous.");
        var previous = matches.SingleOrDefault();
        if (request.Operation == ModuleOperation.Create)
        {
            if (items.Any(item => item.Address.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                NativeV11PendingSave.ReadFiles(session.SourcePath, [path, layoutPath]).Values.Any(bytes => bytes is not null) ||
                new[] { session.LocalPath, session.LastPath }.Any(root => File.Exists(V11Safety.Under(root, path)) || File.Exists(V11Safety.Under(root, layoutPath))))
                throw new BridgeException("ReportAlreadyExists", "The native report name or its file group already exists.");
        }
        else if (previous is null) throw new BridgeException("ReportNotFound", "No unique loaded native report matches the requested address.");
        var parent = previous?.Parent ?? Parent(route, request.Address.FolderPath);
        phase?.Invoke("report-manager-owner");
        var manager = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.ReportsManager", true)!, "get_Instance")
            ?? throw new InvalidOperationException("The native report manager is not initialized.");
        if (NativeV11Reflection.Get(manager, "InARDesignerMode") is not false ||
            NativeV11Reflection.Get(manager, "IsReportDataSourceEditingMode") is not false ||
            manager.GetType().GetField("_checkingDirtyState", NativeV11Reflection.Flags)?.GetValue(manager) is true ||
            manager.GetType().GetField("_updateLocalRootPathTask", NativeV11Reflection.Flags)?.GetValue(manager) is Task { IsCompleted: false })
            throw new BridgeException("ReportEditorOpen", "Close the native report/data-source editor before editing its definition.");
        var reportRoot = NativeV11Reflection.Get(manager, "LocalReportsPath") as string;
        if (reportRoot is null || !Path.GetFullPath(reportRoot).TrimEnd('\\', '/').Equals(
            Path.GetFullPath(V11Safety.Under(session.LocalPath, "Reports")).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new BridgeException("ReportOwnerMismatch", "The native report manager belongs to another save session.");
        if (request.Operation == ModuleOperation.Delete)
        {
            if (request.Document is not null) throw new InvalidDataException("Report deletion cannot include replacement metadata or RDLX.");
            phase?.Invoke("existing-report-pair");
            _ = session.Read(previous!);
            return new(route, path, parent, previous!.Model, null, null, manager);
        }
        var document = request.Document ?? throw new InvalidDataException("A complete report JSON/RDLX pair is required.");
        if (document.Address != request.Address || document.Metadata.ValueKind != JsonValueKind.Object || document.Companions.Count != 1)
            throw new InvalidDataException("The report address or companion group is invalid.");
        NativeV11DefinitionWriter.CheckTypes(document.Metadata, session.Context.DesignerAssembly.GetName().Version!);
        var json = document.Metadata.GetRawText();
        if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new InvalidDataException("The report metadata is too large.");
        V11Safety.Companion(document.Companions[0], layoutPath);
        var bytes = Convert.FromBase64String(document.Companions[0].ContentBase64);
        var layout = NativeV11DefinitionWriter.DecodeXml(bytes, "Report");
        phase?.Invoke("native-report-loader");
        var model = NativeV11DefinitionWriter.Load(route, path, json);
        if (model.GetType().FullName != "ServerDesignerCommon.Model.ReportModel" ||
            NativeV11Reflection.Get(model, "Name") as string != request.Address.Name ||
            !NativeV11DefinitionWriter.Equivalent(json, session.Serialize(route, model)))
            throw new BridgeException("ReportIdentityMismatch", "The native report loader did not preserve the exact identity and metadata.");
        var reportType = NativeV11Reflection.Get(model, "ReportType") as Enum;
        if (reportType is null || !Enum.IsDefined(reportType.GetType(), reportType))
            throw new InvalidDataException("The report type is not defined by this exact V11 build.");
        if (previous is null)
        {
            foreach (var field in document.Metadata.EnumerateObject())
            {
                if (field.Name.Contains("Permission", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("ReportPermissionInitializationRequired", "New report permissions are initialized by the native permission factory.");
                if (field.Name is "AllowAccessRoleList" or "AllowViewRoleList" && field.Value.ValueKind != JsonValueKind.Null &&
                    (field.Value.ValueKind != JsonValueKind.Array || field.Value.GetArrayLength() != 0))
                    throw new BridgeException("ReportPermissionInitializationRequired", "Report creation cannot supply legacy role grants.");
            }
            phase?.Invoke("native-default-permission");
            InitializePermission(session.Context.DesignerAssembly, model);
        }
        else
        {
            var before = session.Read(previous).Metadata;
            foreach (var field in new[] { "Name", "ReportType", "ID", "Id", "GUID", "Guid", "PermissionData", "AllowAccessRoleList", "AllowViewRoleList", "FolderPath", "Path", "Weight", "DesignerSetting" })
            {
                var hasBefore = before.TryGetProperty(field, out var a);
                var hasAfter = document.Metadata.TryGetProperty(field, out var b);
                if (hasBefore != hasAfter || hasBefore && !V11Safety.Equivalent(a, b))
                    throw new BridgeException("ProtectedReportIdentity", "Report identity, type and permission grants must remain unchanged: " + field);
            }
            foreach (var field in new[] { "Weight", "DesignerSetting", "Loaded", "PermissionData", "AllowAccessRoleList", "AllowViewRoleList" })
                if (model.GetType().GetProperty(field, NativeV11Reflection.Flags) is { SetMethod: not null } property)
                    property.SetValue(model, NativeV11Reflection.Get(previous.Model, field));
        }
        // Classify through the real native factory before any file/tree mutation.
        phase?.Invoke("native-module-classifier");
        _ = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", model)
            ?? throw new InvalidOperationException("The native report module classifier is unavailable.");
        return new(route, path, parent, previous?.Model, model, layout, manager);
    }

    internal static void InitializePermission(Assembly designer, object model)
    {
        var profile = V11BuildProfile.Resolve(designer);
        NativeV11DefinitionWriter.VerifyPermissionComponents(designer);
        // These are the factories called by each exact native report-folder
        // CreateReportItem. true preserves native default role/group binding;
        // false would produce an empty binding and is not an equivalent shortcut.
        var type = designer.GetType(profile.Version == "11.0.104.0" ? "jq" : "jk", true)!;
        var factory = NativeV11Reflection.Static(type, "get_Instance")!;
        var permission = NativeV11Reflection.Call(factory, "CreateForDesigner", true)
            ?? throw new InvalidOperationException("The native report permission factory returned no permission data.");
        AssignPermission(designer, model, permission);
    }

    internal static void AssignPermission(Assembly designer, object model, object permission)
    {
        NativeV11DefinitionWriter.VerifyPermissionComponents(designer);
        var process = model.GetType().FullName == "Forguncy.Model.BusinessProcessModel";
        if ((!process && model.GetType().FullName != "ServerDesignerCommon.Model.ReportModel") ||
            !permission.GetType().GetInterfaces().Any(type => type.FullName == (process
                ? "Forguncy.RbacPermission.Core.Impl.BpmProcess.IBpmProcessPermissionData"
                : "Forguncy.RbacPermission.Core.Impl.ActiveReport.IActiveReportPermissionData")))
            throw new InvalidOperationException("The exact native linked-definition permission model is invalid.");
        NativeV11Reflection.Set(model, "PermissionData", permission);
        if (process) return;
        var resource = NativeV11Reflection.Get(permission, "PermissionResource")
            ?? throw new InvalidOperationException("The native report permission resource is unavailable.");
        var properties = resource.GetType().GetInterfaces().Select(type => type.GetProperty("ActiveReportType"))
            .Where(property => property is not null).Cast<PropertyInfo>().ToArray();
        var property = resource.GetType().GetProperty("ActiveReportType", NativeV11Reflection.Flags) ?? properties.Single();
        property.SetValue(resource, Enum.ToObject(property.PropertyType, Convert.ToInt32(NativeV11Reflection.Get(model, "ReportType"))));
    }

    private static void VerifyStaticFileRegistration(NativeV11Session session)
    {
        var registry = NativeV11StaticFiles.Resolve(session);
        if (!registry.Folders.Any(folder => folder.Replace('\\', '/').Trim('/') == "Reports"))
            throw new InvalidOperationException("The native static-file save registry does not own the report directory.");
    }

    internal static (List<string> Folders, List<string> Files) StaticFileRegistry(Assembly designer, object loader)
    {
        // Copies retain the exact native registry; fixtures never invent a set
        // of report files to stand in for the native directory mirror.
        return NativeV11StaticFiles.Registry(designer, loader);
    }

    internal static Action ApplyCore(Assembly designer, NativeV11Route route, object saveFile, string path, ModuleOperation operation,
        IList parent, object? previous, object? incoming, string? layout, Action<object, string> invalidate, IDictionary? pathCache = null)
    {
        _ = V11BuildProfile.Resolve(designer);
        if (route.Kind is not (ModuleKind.Report or ModuleKind.BusinessProcess) || !ReferenceEquals(NativeV11Reflection.Get(route.Loader, "SaveLoadFile"), saveFile))
            throw new InvalidOperationException("The report route and native save session do not match.");
        var local = NativeV11Reflection.Get(saveFile, "LocalPath") as string ?? throw new MissingMemberException("SaveLoadFile.LocalPath");
        var paths = new[] { path, Path.ChangeExtension(path, route.Kind == ModuleKind.Report ? ".rdlx" : ".bpmn").Replace('\\', '/') };
        var before = paths.ToDictionary(relative => relative, relative => ReadOptional(V11Safety.Under(local, relative)), StringComparer.Ordinal);
        if (operation == ModuleOperation.Create ? before.Values.Any(bytes => bytes is not null) : before.Values.Any(bytes => bytes is null))
            throw new InvalidOperationException("The native report file group does not match its tree operation.");
        // Native SaveFile restores UTF-8 text. Prove the entire original pair
        // can be restored byte-for-byte before the first mutation, including
        // deletion of an existing layout with a different original encoding.
        foreach (var original in before.Values.Where(bytes => bytes is not null).Cast<byte[]>())
            if (!Encoding.UTF8.GetBytes(new UTF8Encoding(false, true).GetString(original)).AsSpan().SequenceEqual(original))
                throw new InvalidDataException("The native linked-definition cache encoding cannot be restored exactly by SaveFile.");
        var expected = new Dictionary<string, byte[]?>(before, StringComparer.Ordinal);
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var cacheBefore = new Dictionary<object, object?>();
        foreach (var model in new[] { previous, incoming }.Where(model => model is not null).Cast<object>().Distinct())
            if (pathCache?.Contains(model) is true) cacheBefore.Add(model, pathCache[model]);
        Action? modelUndo = null;
        var restored = false;
        void Restore()
        {
            if (restored) return;
            var errors = new List<Exception>();
            try { modelUndo?.Invoke(); } catch (Exception error) { errors.Add(error); }
            foreach (var relative in paths.Reverse().Where(touched.Contains))
            {
                try
                {
                    var absolute = V11Safety.Under(local, relative);
                    var current = ReadOptional(absolute);
                    if (Same(current, before[relative])) continue;
                    if (!Same(current, expected[relative])) throw new InvalidOperationException("A changed native report cache file cannot be overwritten during rollback.");
                    if (before[relative] is byte[] original)
                        NativeV11Reflection.Call(saveFile, "SaveFile", absolute, new UTF8Encoding(false, true).GetString(original));
                    else DeleteCacheFile(absolute);
                    if (!Same(ReadOptional(absolute), before[relative])) throw new InvalidOperationException("The original native report bytes were not restored.");
                }
                catch (Exception error) { errors.Add(error); }
            }
            foreach (var pair in cacheBefore)
                try
                {
                    if (pathCache!.Contains(pair.Key) && !Equals(pathCache[pair.Key], pair.Value))
                        throw new InvalidOperationException("The native report path cache changed during rollback.");
                    pathCache[pair.Key] = pair.Value;
                }
                catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("Native report rollback is incomplete; do not save this document.", errors);
            restored = true;
        }
        try
        {
            foreach (var relative in paths)
            {
                var absolute = V11Safety.Under(local, relative);
                var text = relative == path ? incoming is null ? null : Serialize(designer, route, incoming) : layout;
                expected[relative] = text is null ? null : Encoding.UTF8.GetBytes(text);
                touched.Add(relative);
                if (text is null) DeleteCacheFile(absolute);
                else NativeV11Reflection.Call(saveFile, "SaveFile", absolute, text);
                if (!Same(ReadOptional(absolute), expected[relative])) throw new InvalidOperationException("The native report file write did not match the complete requested pair.");
            }
            modelUndo = NativeV11DefinitionWriter.ApplyCore(route, operation, parent, previous, incoming, () =>
            {
                var active = incoming is not null && parent.Contains(incoming) ? incoming : previous ?? incoming!;
                var state = parent.Contains(active) ? operation == ModuleOperation.Create && ReferenceEquals(active, incoming) ? "New" : "Edit" : "Remove";
                invalidate(active, state);
            });
            foreach (var model in new[] { previous, incoming }.Where(model => model is not null).Cast<object>()) pathCache?.Remove(model);
            NativeV11Reflection.Call(route.Colleague, "SaveToCache", true);
            return Restore;
        }
        catch (Exception primary)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new AggregateException("Native report editing failed and rollback is not confirmed.", primary, rollback); }
            throw;
        }
    }

    private static string Serialize(Assembly designer, NativeV11Route route, object model) => (string)NativeV11Reflection.Static(
        designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!, "SaveToStr", NativeV11Reflection.Call(route.Mediator, "ConvertToSaveData", model))!;
    private static byte[]? ReadOptional(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private static bool Same(byte[]? left, byte[]? right) => left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);
    private static void DeleteCacheFile(string path) => NativeV11Reflection.Static(Assembly.Load("CommonUtilities").GetType("Forguncy.Common.FileUtilities", true)!, "DeleteFile", path);
    private static IList Parent(NativeV11Route route, string? folder)
    {
        object current = route.Root;
        foreach (var segment in (folder ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NativeV11Reflection.Get(current, "Loaded") is not true) throw new InvalidOperationException("The native report folder is not loaded.");
            var matches = ((IList)NativeV11Reflection.Get(current, "Items")!).Cast<object>().Where(model =>
                NativeV11Reflection.Get(model, "Items") is IList && NativeV11Reflection.Get(model, "Name") as string == segment).ToArray();
            if (matches.Length != 1) throw new InvalidOperationException("Create the exact native parent folder before creating a report.");
            current = matches[0];
        }
        if (NativeV11Reflection.Get(current, "Loaded") is not true) throw new InvalidOperationException("The native report folder is not loaded.");
        return NativeV11Reflection.Get(current, "Items") as IList ?? throw new InvalidDataException("The native report parent has no item collection.");
    }
}
