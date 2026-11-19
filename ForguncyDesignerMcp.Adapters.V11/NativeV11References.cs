using System.Collections;
using System.Reflection;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

internal static class NativeV11References
{
    internal static ReferenceResult Find(NativeV11Session session, ModuleAddress address, object? deletedModel = null)
    {
        var phase = "context";
        try
        {
        session.VerifyContext();
        phase = "target";
        var targetModel = deletedModel ?? session.Find(address).Model;
        var all = new List<NativeV11Item>();
        foreach (var route in session.Routes)
        {
            phase = "list:" + route.Kind + ":" + route.Folder;
            all.AddRange(session.Items(route.Kind));
            if (route.Kind == ModuleKind.Table) all.AddRange(session.Items(ModuleKind.View));
        }
        // Native enumeration walks formula/command objects. Unloaded sheets would hide references.
        foreach (var page in all.Where(item => item.Route.IsPage))
            if (session.Sheet(page.Address.Name) is not object sheet || NativeV11Reflection.Get(sheet, "Loaded") is not true)
                return new([], false, "Native reference scan incomplete; phase=page-content; kind=" + page.Address.Kind + "; path=" + page.RelativePath + "; loaded=false.");
        var semanticTable = address.Kind is ModuleKind.Table or ModuleKind.View &&
            targetModel.GetType().FullName == "Forguncy.Model.Tables.Table" && NativeV11Reflection.Get(targetModel, "ExternalDatabaseInfo") is null;
        IEnumerable native;
        if (semanticTable)
        {
            phase = "semantic-table-references";
            native = NativeV11SemanticTableReferences.NativeOwners(session.Context.DesignerAssembly, session.Profile.Version == "11.0.104.0", targetModel);
        }
        else
        {
        phase = "ReferenceFinder.constructor";
        var finderType = session.Context.DesignerAssembly.GetType("Forguncy.Common.ReferenceFinder", true)!;
        var finder = Activator.CreateInstance(finderType, NativeV11Reflection.Flags, null, [targetModel], null)
            ?? throw new MissingMethodException(finderType.FullName, ".ctor(TreeNodeModelBase)");
        var method = finderType.GetMethod("GetReferences", NativeV11Reflection.Flags, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(finderType.FullName, "GetReferences()");
        phase = "ReferenceFinder.GetReferences";
        if (method.Invoke(finder, null) is not IEnumerable found)
            throw new InvalidOperationException("原生引用枚举未返回结果。");
        native = found;
        }
        // GetReferences includes the target's own SimpleModule. Compare that
        // native identity before tree mapping, including save-time rechecks
        // after deletion when the target is intentionally absent from the tree.
        phase = "SimpleModule.Create(target)";
        var own = NativeV11Reflection.Static(session.Context.DesignerAssembly.GetType("Forguncy.Common.SimpleModule", true)!, "Create", targetModel)
            ?? throw new InvalidOperationException("原生目标模块身份未确认。");
        var references = new List<ModuleReference>();
        bool complete = true;
        phase = "ReferenceFinder.mapping";
        foreach (var reference in native)
        {
            if (reference is null) { complete = false; continue; }
            if (own.Equals(reference)) continue;
            var name = NativeV11Reflection.Get(reference, "ModuleName") as string ?? NativeV11Reflection.Get(reference, "Name") as string;
            var type = NativeV11Reflection.Get(reference, "ModuleType") ?? NativeV11Reflection.Get(reference, "Type");
            var kind = Kind(type?.ToString());
            if (name is null || kind is null) { complete = false; continue; }
            // SimpleModule names may contain a native module prefix. Prefer the live tree's own address.
            var matches = all.Where(item => item.Address.Kind == kind &&
                (item.Address.Name == name || name.EndsWith("/" + item.Address.Name, StringComparison.Ordinal))).ToArray();
            if (matches.Length == 0 && kind is ModuleKind.ApplicationSettings or ModuleKind.Theme or ModuleKind.CustomLibrary or ModuleKind.DataConnection)
            {
                var root = kind switch { ModuleKind.ApplicationSettings => "ApplicationSettings", ModuleKind.Theme => "Theme", ModuleKind.CustomLibrary => "CustomLibraries", _ => "DataConnectionSet" };
                references.Add(new(new(kind.Value, root), address, "原生 ReferenceFinder.GetReferences"));
                continue;
            }
            if (matches.Length != 1) { complete = false; continue; }
            if (matches[0].Address == address) continue;
            references.Add(new(matches[0].Address, address, "原生 ReferenceFinder.GetReferences"));
        }
        if (semanticTable)
        {
            phase = "sqlite-view-references";
            NativeV11BuiltInTableLifecycle.VerifyPrivateNativeProvider(session);
            var database = V11Safety.Under(session.LocalPath, NativeV11CollaborationOwners.TableDatabasePath());
            NativeV11BuiltInTableLifecycle.VerifyPrivateDatabasePath(session.LocalPath, database);
            foreach (var name in NativeV11SemanticTableReferences.SqliteViewOwners(session.Context.DesignerAssembly, database, address.Name))
            {
                var matches = all.Where(item => item.Address.Kind == ModuleKind.View && string.Equals(item.Address.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length != 1) { complete = false; continue; }
                references.Add(new(matches[0].Address, address, "SQLite view dependency"));
            }
        }
        return new(references.Distinct().ToArray(), complete,
            complete ? "原生模型引用枚举；动态拼接的脚本引用仍需调用方核对。" : "原生引用中存在尚未映射的模块，删除已停止。");
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            while (error is TargetInvocationException { InnerException: not null }) error = error.InnerException!;
            return new([], false, "Native reference scan incomplete; phase=" + phase + "; targetKind=" + address.Kind + "; error=" + error.GetType().Name + ".");
        }
    }

    private static ModuleKind? Kind(string? type) => type switch
    {
        "Page" or "NormalPage" or "PCPage" or "MobilePage" => ModuleKind.Page,
        "MasterPage" or "MasterPCPage" or "MasterMobilePage" => ModuleKind.MasterPage,
        "UserControlPage" or "UserControl" => ModuleKind.UserControlPage,
        "Table" or "TableSqlite" => ModuleKind.Table,
        "TableView" or "View" or "TableProcessView" => ModuleKind.View,
        "ServerCommand" or "ServerCommands" or "ServerCommandPrivate" => ModuleKind.ServerCommand,
        "ShareCommand" or "SharedCommand" or "ShareCommandContainer" => ModuleKind.SharedCommand,
        "TaskScheduler" or "SchedulerTask" or "ScheduledTask" => ModuleKind.ScheduledTask,
        "Notification" or "ServerNotification" => ModuleKind.Notification,
        "Report" or "PageReport" or "RdlReport" => ModuleKind.Report,
        "Process" or "BusinessProcess" or "ProcessItem" => ModuleKind.BusinessProcess,
        "ApplicationSetting" => ModuleKind.ApplicationSettings,
        "Theme" => ModuleKind.Theme,
        "CustomLibrary" => ModuleKind.CustomLibrary,
        "DataContectionSet" => ModuleKind.DataConnection,
        _ => null
    };
}
