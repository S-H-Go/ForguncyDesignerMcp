using System.Collections;
using System.Text;
using System.Text.Json;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal static class NativeV11PageWriter
{
    internal static ModuleDocument ProjectDetached(NativeV11Session session,ModuleDocument document)
    {
        session.VerifyContext();
        if(document.Address.Kind is not (ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage))throw new NotSupportedException("Page projection requires a page address.");
        NativeV11DefinitionWriter.CheckTypes(document.Metadata,session.Context.DesignerAssembly.GetName().Version!);
        var route=session.Route(document.Address.Kind);var path=session.NativePath(document.Address);
        var data=LoadForAddress(route,path,document.Metadata.GetRawText());
        var info=NativeV11Reflection.Get(data,"PageInfo") ?? throw new BridgeException("PageInfoRequired","PageInfo is required.");
        var type="Forguncy.Model.Pages."+(document.Address.Kind switch{ModuleKind.Page=>"NormalPage",ModuleKind.MasterPage=>"MasterPage",_=>"UserControlPage"});
        if(info.GetType().FullName!=type || NativeV11Reflection.Get(info,"Name") as string!=document.Address.Name)throw new BridgeException("PageIdentityMismatch","Native page identity differs from its address.");
        var canonical=V11Safety.Body(Encoding.UTF8.GetBytes(session.SerializeSaveData(data)));
        if(!V11Safety.Equivalent(document.Metadata,canonical))throw new BridgeException("NativeRoundtripMismatch","Native page canonicalization would discard or change metadata.");
        var redundant=NativeV11Reflection.Call(route.Loader,session.Profile.RedundantProjection,data) ?? throw new InvalidOperationException("Native redundant projection is unavailable.");
        var bytes=Encoding.UTF8.GetBytes(session.SerializeSaveData(redundant));
        var asset=new CompanionAsset(Path.ChangeExtension(path,".rd").Replace('\\','/'),Convert.ToBase64String(bytes),NativeV11Session.Hash(bytes),"application/json");
        session.VerifyContext();
        return document with {Metadata=canonical,Companions=[asset]};
    }
    internal static bool Supports(ModuleKind kind, ModuleOperation operation) =>
        kind is ModuleKind.Page or ModuleKind.MasterPage or ModuleKind.UserControlPage &&
        operation is ModuleOperation.Create or ModuleOperation.Update or ModuleOperation.Delete;

    internal static void Validate(NativeV11Session session, ModuleMutationRequest request)
    {
        if (!Supports(request.Address.Kind, request.Operation)) throw new NotSupportedException("页面操作尚未实现。");
        var route = session.Route(request.Address.Kind);
        if (request.Operation == ModuleOperation.Delete)
        {
            var item = session.Find(request.Address);
            if (request.Address.Kind == ModuleKind.UserControlPage) ValidateUserControlDeletion(session, item.Model);
            var result = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Model.Pages.PageOperationsParameterChecker", true)!,
                "CheckDeletePage", request.Address.Name, false)!;
            if (NativeV11Reflection.Get(result, "Result") is not true)
                throw new BridgeException("NativePageDeleteRejected", NativeV11Reflection.Get(result, "ErrorMessage") as string ?? "原生页面删除检查未通过。");
            if (((IEnumerable)NativeV11Reflection.Get(session.Workbook, "Sheets")!).Cast<object>().Count() <= 1)
                throw new BridgeException("LastPageProtected", "不能删除工作簿最后一个页面。");
            _ = session.Read(item);
            return;
        }
        var incoming = request.Document ?? throw new BridgeException("DocumentRequired", "页面创建或更新需要完整文档。");
        NativeV11DefinitionWriter.CheckTypes(incoming.Metadata, session.Context.DesignerAssembly.GetName().Version!);
        var path = session.NativePath(request.Address);
        var data = LoadForAddress(route, path, incoming.Metadata.GetRawText());
        var info = NativeV11Reflection.Get(data, "PageInfo") ?? throw new BridgeException("PageInfoRequired", "页面缺少 PageInfo。");
        var expectedType = "Forguncy.Model.Pages." + (request.Address.Kind switch
        {
            ModuleKind.Page => "NormalPage", ModuleKind.MasterPage => "MasterPage", _ => "UserControlPage"
        });
        if (info.GetType().FullName != expectedType || NativeV11Reflection.Get(info, "Name") as string != request.Address.Name)
            throw new BridgeException("PageIdentityMismatch", "页面的原生类型或名称与地址不一致。");
        if (!V11Safety.Equivalent(incoming.Metadata, V11Safety.Body(Encoding.UTF8.GetBytes(session.SerializeSaveData(data)))))
            throw new BridgeException("NativeRoundtripMismatch", "页面 JSON 存在原生模型无法保留的字段或值；请读取规范化的原生页面再编辑。");
        if (incoming.Companions.Count != 1) throw new BridgeException("PageCompanionRequired", "页面需要唯一的同名 .rd 配套文档。");
        V11Safety.Companion(incoming.Companions[0], Path.ChangeExtension(path, ".rd").Replace('\\', '/'));
        var redundant = NativeV11Reflection.Call(route.Loader, session.Profile.RedundantProjection, data)!;
        var expectedRd = V11Safety.Body(Encoding.UTF8.GetBytes(session.SerializeSaveData(redundant)));
        if (!V11Safety.Equivalent(expectedRd, V11Safety.Body(Convert.FromBase64String(incoming.Companions[0].ContentBase64))))
            throw new BridgeException("PageCompanionMismatch", "页面的公式、名称、尺寸等与原生 .rd 投影不一致。");
        if (request.Operation == ModuleOperation.Update)
        {
            var old = session.Read(session.Find(request.Address));
            PreserveIdentity(old.Metadata.GetProperty("PageInfo"), incoming.Metadata.GetProperty("PageInfo"));
            if (request.Address.Kind == ModuleKind.Page)
                NativeV11PagePermissions.Preserve(old.Metadata.GetProperty("PageInfo"), incoming.Metadata.GetProperty("PageInfo"));
            if (request.Address.Kind == ModuleKind.UserControlPage)
                PreserveIdentity(old.Metadata.GetProperty("PageInfo"), incoming.Metadata.GetProperty("PageInfo"),
                    ["PropertyDefinitions", "CommandDefinitions", "MethodDefinitions"]);
        }
        else
        {
            if (session.Routes.Where(x => x.IsPage).SelectMany(x => session.Items(x.Kind)).Any(x => x.Address.Name.Equals(request.Address.Name, StringComparison.OrdinalIgnoreCase)) ||
                session.Sheet(request.Address.Name) is not null)
                throw new BridgeException("PageAlreadyExists", "页面或工作表名称已存在。");
            if (request.Address.Kind == ModuleKind.Page)
            {
                NativeV11PagePermissions.ValidateCreateInput(incoming.Metadata.GetProperty("PageInfo"));
                NativeV11PagePermissions.InitializeDefault(session.Context.DesignerAssembly, info);
            }
        }
        // A page is inserted only into an existing native folder. Folder CRUD has a separate contract.
        _ = Parent(route, request.Address.FolderPath);
    }

    internal static Action Apply(NativeV11Session session, ModuleMutationRequest request)
    {
        Validate(session, request);
        var route = session.Route(request.Address.Kind);
        var existing = request.Operation == ModuleOperation.Create ? null : session.Find(request.Address);
        var original = existing is null ? null : session.Read(existing);
        var sheets = NativeV11Reflection.Get(session.Workbook, "Sheets")!;
        var originalSheet = session.Sheet(request.Address.Name);
        var originalSheetIndex = originalSheet is null ? -1 : ((IList)sheets).IndexOf(originalSheet);
        var originalActive = NativeV11Reflection.Get(session.Workbook, "ActiveSheet");
        var wasDirty = NativeV11Reflection.Get(session.Document, "ContainsUnsavedChanges") is true;
        var undo = new List<Action>();
        void Rollback()
        {
            var errors = new List<Exception>();
            for (var i = undo.Count - 1; i >= 0; i--) try { undo[i](); } catch (Exception error) { errors.Add(error); }
            if (errors.Count != 0) throw new AggregateException("页面回退不完整；请勿保存，需人工核对当前文档。", errors);
            RebuildReferences(session);
            if (existing is not null) NativeV11Reflection.Call(route.Colleague, "Edit", existing.Model, request.Address.Name);
            if (original is not null && session.Read(session.Find(request.Address)).Revision != original.Revision)
                throw new InvalidOperationException("页面回退后原生内存与 .rd 读回不一致。");
            if (originalActive is not null && ((IList)sheets).Contains(originalActive))
                NativeV11Reflection.Set(session.Workbook, "ActiveSheetIndex", ((IList)sheets).IndexOf(originalActive));
            if (request.Address.Kind == ModuleKind.UserControlPage) RefreshUserControlCache(session);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", wasDirty);
        }
        try
        {
            if (request.Operation == ModuleOperation.Delete)
            {
                var item = existing!;
                var key = NativeV11Reflection.Call(route.Colleague, "GetKey", item.Model) as string
                    ?? throw new InvalidOperationException("页面原生保存登记缺失。");
                var rdKey = NativeV11Reflection.Call(route.Loader, "GetRDFileName", key)!;
                undo.Add(() => NativeV11Reflection.Call(route.Colleague, "Insert", item.Model));
                NativeV11Reflection.Call(route.Colleague, "Delete", item.Model);
                // V11's page colleague removes both entries. Never treat a second
                // redundant Delete returning false as failure of the first one.
                if (NativeV11Reflection.Call(route.Loader, "ContainsKey", key) is true ||
                    NativeV11Reflection.Call(route.Loader, "ContainsKey", rdKey) is true)
                    throw new InvalidOperationException("原生页面或 .rd 删除登记失败。");
                undo.Add(() => item.Parent.Insert(Math.Min(item.Index, item.Parent.Count), item.Model));
                item.Parent.RemoveAt(item.Index);
                undo.Add(() => ((IList)sheets).Insert(Math.Min(originalSheetIndex, ((IList)sheets).Count), originalSheet));
                ((IList)sheets).Remove(originalSheet);
                RebuildReferences(session);
                Mark(session, item.Model, "Remove");
                if (session.Sheet(request.Address.Name) is not null ||
                    NativeV11Reflection.Call(NativeV11Reflection.Get(session.Document, "Pages")!, "TryGetPage", request.Address.Name) is not null)
                    throw new InvalidOperationException("删除后页面仍在原生索引中。");
            }
            else
            {
                var data = LoadForAddress(route, session.NativePath(request.Address), request.Document!.Metadata.GetRawText());
                if (request.Operation == ModuleOperation.Create && request.Address.Kind == ModuleKind.Page)
                {
                    NativeV11PagePermissions.ValidateCreateInput(request.Document.Metadata.GetProperty("PageInfo"));
                    NativeV11PagePermissions.InitializeDefault(session.Context.DesignerAssembly, NativeV11Reflection.Get(data, "PageInfo")!);
                }
                var expectedMetadata = V11Safety.Body(Encoding.UTF8.GetBytes(session.SerializeSaveData(data)));
                var expectedRedundant = NativeV11Reflection.Call(route.Loader, session.Profile.RedundantProjection, data)!;
                var expectedCompanion = V11Safety.Body(Encoding.UTF8.GetBytes(session.SerializeSaveData(expectedRedundant)));
                var sheet = originalSheet;
                if (sheet is null)
                {
                    var worksheetType = sheets.GetType().GetProperty("Item", [typeof(string)])!.PropertyType;
                    sheet = Activator.CreateInstance(worksheetType, [request.Address.Name])!;
                    NativeV11Reflection.Call(sheets, "Add", sheet);
                    var created = sheet;
                    undo.Add(() => NativeV11Reflection.Call(sheets, "Remove", created));
                }
                else
                {
                    var restore = sheet;
                    undo.Add(() => ReplaceContents(session, restore, NativeV11Reflection.Static(session.Generator,
                        "CreatePageSaveData", original!.Metadata.GetRawText())!));
                }
                ReplaceContents(session, sheet, data);
                var model = NativeV11Reflection.Get(NativeV11Reflection.Get(sheet, "Tag")!, "PageInfo")!;
                var parent = existing?.Parent ?? Parent(route, request.Address.FolderPath);
                if (existing is not null)
                {
                    undo.Add(() => { parent[existing.Index] = existing.Model; NativeV11Reflection.Call(route.Colleague, "Edit", existing.Model, request.Address.Name); });
                    parent[existing.Index] = model;
                    NativeV11Reflection.Call(route.Colleague, "Edit", model, request.Address.Name);
                }
                else
                {
                    parent.Add(model);
                    undo.Add(() => { NativeV11Reflection.Call(route.Colleague, "Delete", model); parent.Remove(model); });
                    NativeV11Reflection.Call(route.Colleague, "Insert", model);
                }
                RebuildReferences(session);
                Mark(session, model, "Edit");
                var actual = session.Read(session.Find(request.Address));
                if (!V11Safety.Equivalent(expectedMetadata, actual.Metadata) ||
                    !V11Safety.Equivalent(expectedCompanion,
                        V11Safety.Body(Convert.FromBase64String(actual.Companions[0].ContentBase64))))
                    throw new InvalidOperationException("页面加载后原生内容或 .rd 读回与输入不一致。");
            }
            if (originalActive is not null && ((IList)sheets).Contains(originalActive))
                NativeV11Reflection.Set(session.Workbook, "ActiveSheetIndex", ((IList)sheets).IndexOf(originalActive));
            else if (request.Operation == ModuleOperation.Delete && ((IList)sheets).Count > 0)
                NativeV11Reflection.Set(session.Workbook, "ActiveSheetIndex", Math.Min(Math.Max(originalSheetIndex, 0), ((IList)sheets).Count - 1));
            if (request.Address.Kind == ModuleKind.UserControlPage) RefreshUserControlCache(session);
            NativeV11Reflection.Set(session.Document, "ContainsUnsavedChanges", true);
            return Rollback;
        }
        catch (Exception failure)
        {
            try { Rollback(); }
            catch (Exception rollback) { throw new AggregateException("页面写入失败且回退未确认。", failure, rollback); }
            throw;
        }
    }

    internal static object PrepareAndNormalize(Type generator, string json) => NativeV11Reflection.Static(generator, "CreatePageSaveData", json)!;

    // Native page JSON intentionally omits PageInfo.Name. Its exact loader binds
    // the model identity from the native file key; plain CreatePageSaveData does
    // not perform that step. Use the same binding for validation and application.
    internal static object LoadForAddress(NativeV11Route route, string path, string json)
    {
        if (!route.IsPage) throw new InvalidDataException("A page identity requires its native page loader.");
        using var stream = new MemoryStream(new UTF8Encoding(false, true).GetBytes(json), false);
        return NativeV11Reflection.Call(route.Loader, "GenerateObject", path.Replace('/', '\\'), stream)
            ?? throw new InvalidDataException("The native page loader returned no page data.");
    }

    internal static void ReplaceContents(NativeV11Session session, object sheet, object data)
    {
        var formula = NativeV11Reflection.Get(session.Document, "FormulaReferManager")!;
        NativeV11Reflection.Call(formula, "BeginImport");
        try { ReplaceContentsCore(session.Generator, sheet, data); }
        finally { NativeV11Reflection.Call(formula, "EndInit"); }
    }

    internal static void ReplaceContentsCore(Type generator, object sheet, object data)
    {
        var name = NativeV11Reflection.Get(sheet, "Name") as string ?? throw new InvalidDataException("页面名称缺失。");
        var blank = Activator.CreateInstance(sheet.GetType(), [name])!;
        try
        {
            using var xml = new MemoryStream();
            NativeV11Reflection.Call(blank, "SaveXml", xml);
            xml.Position = 0;
            NativeV11Reflection.Call(sheet, "OpenXml", xml);
            NativeV11Reflection.Set(sheet, "Name", name);
            NativeV11Reflection.Static(generator, "ToWorksheet", sheet, data);
            RebuildListViews(generator, sheet, data);
        }
        finally { if (blank is IDisposable disposable) disposable.Dispose(); }
    }

    private static IList Parent(NativeV11Route route, string? path)
    {
        var folder = route.Root;
        foreach (var segment in (path ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (NativeV11Reflection.Get(folder, "Loaded") is false) throw new InvalidOperationException("页面目录尚未加载。");
            folder = ((IList)NativeV11Reflection.Get(folder, "Items")!).Cast<object>().SingleOrDefault(x =>
                NativeV11Reflection.Get(x, "Name") as string == segment && NativeV11Reflection.Get(x, "Items") is IList)
                ?? throw new InvalidOperationException("页面目录不存在；请先建立目录。");
        }
        if (NativeV11Reflection.Get(folder, "Loaded") is false) throw new InvalidOperationException("页面目录尚未加载。");
        return NativeV11Reflection.Get(folder, "Items") as IList ?? throw new InvalidOperationException("页面目录无原生集合。");
    }

    internal static void Mark(NativeV11Session session, object model, string state)
    {
        var cacheType = session.Context.DesignerAssembly.GetType(session.Profile.GenerationCacheType, true)!;
        var cache = NativeV11Reflection.Static(cacheType, "get_Instance")!;
        MarkCore(cache, model, state);
    }

    internal static void MarkCore(object cache, object model, string state)
    {
        var method = cache.GetType().GetMethod("DirtyModules", NativeV11Reflection.Flags)!;
        var tuple = method.GetParameters()[1].ParameterType.GetGenericArguments()[0];
        var types = tuple.GetGenericArguments();
        var module = NativeV11Reflection.Static(types[0], "Create", model)!;
        var list = Array.CreateInstance(tuple, 1);
        list.SetValue(Activator.CreateInstance(tuple, module, Enum.Parse(types[1], state)), 0);
        method.Invoke(cache, [null, list]);
        if (state != "Remove" && (NativeV11Reflection.Call(cache, "IsSaveDataDirty", model) is not true ||
            NativeV11Reflection.Call(cache, "IsMetadateDirty", model, false) is not true))
            throw new InvalidOperationException("原生页面保存或生成脏标记未登记。");
    }

    private static void RebuildReferences(NativeV11Session session)
    {
        var pages = NativeV11Reflection.Get(session.Document, "Pages")!;
        foreach (var key in new[] { "NormalPageListCache", "MasterPageListCache", "UserControlPageListCache" })
        {
            var list = NativeV11Reflection.Get(pages, key)!;
            NativeV11Reflection.Call(list, "BuildCache");
            foreach (var page in (IEnumerable)list)
                if (!ReferenceEquals(NativeV11Reflection.Call(pages, "TryGetPage", NativeV11Reflection.Get(page!, "Name")), page))
                    throw new InvalidOperationException("原生页面名称索引不一致。");
        }
    }

    private static void PreserveIdentity(JsonElement before, JsonElement after, string[]? keys = null)
    {
        foreach (var key in keys ?? ["$type", "Name", "ID", "GUID", "PermissionData", "AllowAccessRoleList"])
        {
            var a = before.TryGetProperty(key, out var x); var b = after.TryGetProperty(key, out var y);
            if (a != b || a && !V11Safety.Equivalent(x, y))
                throw new BridgeException("ProtectedPageIdentity", "页面更新不能改变原生身份、类型或权限字段：" + key);
        }
    }

    private static void ValidateUserControlDeletion(NativeV11Session session, object model)
    {
        var name = NativeV11Reflection.Get(model, "Name") as string ?? throw new InvalidDataException("用户控件名称缺失。");
        var helper = session.Context.DesignerAssembly.GetType("Forguncy.Common.UserControlPageReferenceHelper", true)!;
        void Reject(string method, params object?[] arguments)
        {
            if (NativeV11Reflection.Static(helper, method, arguments) is not IEnumerable references)
                throw new InvalidOperationException("用户控件原生引用枚举不可用。");
            if (references.Cast<object>().Any())
                throw new BridgeException("UserControlReferenced", "用户控件或公开接口仍有原生引用，不能删除。");
        }
        Reject("EnumUserControlPageReferences", name);
        foreach (var (property, method) in new[]
        {
            ("PropertyDefinitions", "EnumUserControlPagePropertyReferences"),
            ("CommandDefinitions", "EnumUserControlPageCommandReferences"),
            ("MethodDefinitions", "EnumUserControlPageMethodReferences")
        })
            foreach (var definition in (IEnumerable)(NativeV11Reflection.Get(model, property) ?? throw new MissingMemberException(property)))
                Reject(method, name, NativeV11Reflection.Get(definition!, "Name"));
    }

    private static void RefreshUserControlCache(NativeV11Session session)
    {
        var designer = session.Context.DesignerAssembly.GetType("Forguncy.UserControlPageCellTypeDesigner", true)!;
        NativeV11Reflection.Static(designer, "ClearCache");
        var cache = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType(session.Profile.GenerationCacheType, true)!, "get_Instance")!;
        NativeV11Reflection.Call(cache, "DirtyAllPages");
    }

    private static void RebuildListViews(Type generator, object sheet, object data)
    {
        if (NativeV11Reflection.Get(data, "AttachInfos") is not IDictionary attachments) return;
        var rows = (int)NativeV11Reflection.Get(sheet, "RowCount")!;
        var columns = (int)NativeV11Reflection.Get(sheet, "ColumnCount")!;
        var cells = NativeV11Reflection.Get(sheet, "Cells")!;
        var indexer = cells.GetType().GetProperty("Item", [typeof(int), typeof(int)])!;
        foreach (var attachment in attachments.Values)
        {
            if (attachment is null || NativeV11Reflection.Get(attachment, "ListViewInfo") is not object saved) continue;
            var row = (int)NativeV11Reflection.Get(saved, "RowIndex")!; var column = (int)NativeV11Reflection.Get(saved, "ColumnIndex")!;
            var height = (int)NativeV11Reflection.Get(saved, "RowCount")!; var width = (int)NativeV11Reflection.Get(saved, "ColumnCount")!;
            if (row < 0 || column < 0 || height <= 0 || width <= 0 || (long)row + height > rows || (long)column + width > columns)
                throw new InvalidDataException("列表区域超出页面范围。");
            var anchor = NativeV11Reflection.Call(sheet, "GetTag", row, column)!;
            var info = NativeV11Reflection.Get(anchor, "ListViewInfo") ?? throw new InvalidDataException("原生列表锚点未恢复。");
            var reset = NativeV11Reflection.Method(generator.Assembly.GetType("Forguncy.Commands.SetAsListViewAction", true)!,
                "ResetListviewInfoTagInCell", info, indexer.GetValue(cells, [row, column]));
            for (var r = row; r < row + height; r++) for (var c = column; c < column + width; c++)
                reset.Invoke(null, [info, indexer.GetValue(cells, [r, c])]);
        }
    }
}
