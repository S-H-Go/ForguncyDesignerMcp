using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Designer.Native;

// A scoped adapter for the version-checked report colleague, not a generic file/CLR API.
internal sealed class NativeReportStore
{
    private readonly Assembly assembly;
    private readonly object colleague;
    private readonly object mediator;
    private readonly object loader;
    private readonly object saveFile;
    private readonly object root;
    private readonly object? reportsManager;
    internal string LocalRoot { get; }
    internal string SourcePath { get; }
    internal sealed record Item(object Model, IList Parent, int Index, string JsonFile, string RdlxFile);

    internal NativeReportStore(Assembly assembly, object colleague, object? reportsManager = null)
    {
        this.assembly = assembly;
        this.colleague = colleague;
        this.reportsManager = reportsManager;
        mediator = NativeAccess.Get(colleague, "Mediator") ?? throw new MissingMemberException("Report.Mediator");
        loader = NativeAccess.Get(mediator, "SaveDataColleague") ?? throw new MissingMemberException("Report.SaveDataColleague");
        saveFile = NativeAccess.Get(loader, "SaveLoadFile") ?? throw new MissingMemberException("Report.SaveLoadFile");
        LocalRoot = Path.GetFullPath((string)(NativeAccess.Get(saveFile, "LocalPath") ?? throw new MissingMemberException("Report.LocalPath")));
        SourcePath = Path.GetFullPath((string)NativeAccess.Call(saveFile, "GetDocumentPath")!);
        root = NativeAccess.Get(colleague, "Root") ?? throw new MissingMemberException("Report.Root");
    }

    internal Item Find(string name)
    {
        ReportDocuments.ValidateName(name);
        var found = new List<(object Model, IList Parent, int Index)>();
        void Visit(object node)
        {
            if (NativeAccess.Get(node, "Loaded") is false)
                throw new BridgeException("ReportTreeNotLoaded", "报表树尚未完整加载，请先展开报表目录并重新读取。");
            if (NativeAccess.Get(node, "Items") is not IList items) return;
            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index]!;
                if (NativeAccess.Get(item, "Items") is IList) Visit(item);
                else
                {
                    if (NativeAccess.Get(item, "Loaded") is false)
                        throw new BridgeException("ReportTreeNotLoaded", "报表树尚未完整加载，请先加载报表并重新读取。");
                    if (item.GetType().FullName == "ServerDesignerCommon.Model.ReportModel" && NativeAccess.Get(item, "Name") as string == name)
                        found.Add((item, items, index));
                }
            }
        }
        Visit(root);
        if (found.Count != 1) throw new BridgeException("ReportNotUnique", "未找到唯一的现有报表：" + name);
        var match = found[0];
        var key = (NativeAccess.Call(colleague, "GetKey", match.Model) as string)?.Replace('\\', '/')
            ?? throw new BridgeException("ReportPathUnavailable", "原生报表未提供保存路径。");
        if (!key.EndsWith(".json", StringComparison.Ordinal) || Path.GetFileNameWithoutExtension(key) != name)
            throw new BridgeException("InvalidReportPath", "原生报表元数据名称与保存路径不一致。");
        NativeReportFiles.Under(LocalRoot, key);
        var rdlx = Path.ChangeExtension(key, ".rdlx");
        NativeReportFiles.Under(LocalRoot, rdlx);
        return new(match.Model, match.Parent, match.Index, key, rdlx);
    }

    internal string Serialize(object model)
    {
        var data = NativeAccess.Call(mediator, "ConvertToSaveData", model)!;
        var generator = assembly.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
        var method = generator.GetMethod("SaveToStr", NativeAccess.Flags, null, [typeof(object)], null)
            ?? throw new MissingMethodException("PageSaveDataGenerator.SaveToStr(object)");
        return (string)method.Invoke(null, [data])!;
    }

    internal string ReadRdlx(Item item) => NativeReportFiles.Decode(File.ReadAllBytes(NativeReportFiles.Under(LocalRoot, item.RdlxFile)));

    internal object Prepare(Item item, string json)
    {
        using var input = new MemoryStream(new UTF8Encoding(false, true).GetBytes(json), false);
        var data = NativeAccess.Call(loader, "GenerateObject", item.JsonFile.Replace('/', '\\'), input)!;
        var model = NativeAccess.Call(mediator, "ConvertToModel", data)!;
        if (model.GetType() != item.Model.GetType() || !Equals(NativeAccess.Get(model, "Name"), NativeAccess.Get(item.Model, "Name")))
            throw new BridgeException("InvalidReportModel", "原生报表类型或名称与现有模型不同。");
        if (!ReportDocuments.JsonEquivalent(json, Serialize(model)))
            throw new BridgeException("ReportNativeRoundtripMismatch", "报表 JSON 包含原生模型无法完整保留的字段或值，未修改工程。");
        // These native tree/editor fields do not belong to ReportSaveData.
        foreach (var name in new[] { "Weight", "DesignerSetting", "Loaded" })
        {
            var property = model.GetType().GetProperty(name, NativeAccess.Flags);
            if (property?.CanWrite is true) property.SetValue(model, NativeAccess.Get(item.Model, name));
        }
        return model;
    }

    internal void Replace(Item item, object model, string rdlx)
    {
        if (!ReferenceEquals(item.Parent[item.Index], item.Model))
            throw new BridgeException("ReportModelChanged", "报表树在写入前已变化。");
        var target = NativeReportFiles.Under(LocalRoot, item.RdlxFile);
        // The native SaveLoadFile owns the working cache. Source and LastPath are never targets.
        NativeAccess.Call(saveFile, "SaveFile", target, rdlx);
        item.Parent[item.Index] = model;
        NativeAccess.Call(colleague, "Edit", model, NativeAccess.Get(item.Model, "Name"));
        NativeAccess.Call(colleague, "SaveToCache", true);
        if (reportsManager is not null && NativeAccess.Get(reportsManager, "ReportPathCache") is IDictionary cache)
            cache.Remove(item.Model);
    }

    internal static async Task<bool> SaveDocumentAsync(object document)
    {
        var method = document.GetType().GetMethod("SaveAsync", NativeAccess.Flags, null, [typeof(bool)], null);
        if (method?.ReturnType != typeof(Task<bool>)) throw new MissingMethodException("SaveAsync(bool) 的签名已变化。");
        try { return await (Task<bool>)method.Invoke(document, [false])!; }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}
