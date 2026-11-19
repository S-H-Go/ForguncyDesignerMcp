using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
using ForguncyDesignerMcp.Core;

namespace ForguncyDesignerMcp.Designer.Native;

internal static class NativeAccess
{
    internal const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    internal const string SupportedVersion = "12.0.102.0";
    internal const string SupportedHash = "A6047895A74DC5C608D500376534C27BD32EBBBC2C2D423D5C4B2FFDAC885246";

    internal static Assembly FindDesigner()
    {
        var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "Forguncy")
            ?? throw new BridgeException("NotDesignerProcess", "当前进程不是活字格设计器。");
        _ = DesignerBuildProfile.Resolve(assembly);
        ValidateCommonContract(assembly);
        return assembly;
    }

    internal static void RequireV12Legacy(Assembly assembly)
    {
        if (DesignerBuildProfile.Resolve(assembly).Version != SupportedVersion)
            throw new BridgeException("LegacyToolUnavailable", "此旧版扩展工具仅支持已验证的 V12 构建；请使用当前版本的平台接口。");
        ValidateContract(assembly);
    }

    private static void ValidateCommonContract(Assembly assembly)
    {
        var main = assembly.GetType("Forguncy.MainWindow", true)!;
        var document = main.GetProperty("SaveLoadManager", Flags)?.PropertyType ?? throw new MissingMemberException("SaveLoadManager");
        foreach (var name in new[] { "CurrentFilePath", "ContainsUnsavedChanges", "Opening", "IsImporting", "Workbook", "ForguncyColleagueManager" })
            if (document.GetProperty(name, Flags) is null) throw new MissingMemberException(document.FullName, name);
        var manager = assembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        foreach (var name in new[] { "Instance", "InCollaborationMode", "CurrentUser", "Modules", "ModuleFilePath" })
            if (manager.GetProperty(name, Flags) is null) throw new MissingMemberException(manager.FullName, name);
    }

    internal static void ValidateContract(Assembly assembly)
    {
        var manager = assembly.GetType("Forguncy.Common.CollaborationManager", true)!;
        foreach (var name in new[] { "Instance", "InCollaborationMode", "CurrentUser", "Modules", "ModuleFilePath" })
            if (manager.GetProperty(name, Flags) is null) throw new MissingMemberException(manager.FullName, name);
        if (manager.GetMethod("ReloadModules", Flags, null, Type.EmptyTypes, null)?.ReturnType != typeof(void))
            throw new MissingMethodException("ReloadModules 的签名已变化。");
        var checkin = manager.GetMethod("CheckInAsync", Flags);
        if (checkin?.ReturnType != typeof(Task<bool>) || checkin.GetParameters().Length != 3)
            throw new MissingMethodException("CheckInAsync 的签名已变化。");
        var main = assembly.GetType("Forguncy.MainWindow", true)!;
        var document = main.GetProperty("SaveLoadManager", Flags)?.PropertyType ?? throw new MissingMemberException("SaveLoadManager");
        foreach (var name in new[] { "CurrentFilePath", "ContainsUnsavedChanges", "Opening", "IsImporting", "Workbook" })
            if (document.GetProperty(name, Flags) is null) throw new MissingMemberException(document.FullName, name);
        var executor = assembly.GetType("Forguncy.Common.CollaborationExecutor", true)!;
        if (executor.GetMethod("Checkout", Flags)?.GetParameters().Length != 4)
            throw new MissingMethodException("Checkout 的签名已变化。");
        if (document.GetMethod("SaveAsync", Flags, null, [typeof(bool)], null)?.ReturnType != typeof(Task<bool>))
            throw new MissingMethodException("SaveAsync(bool) 的签名已变化。");
        var saveFile = assembly.GetType("Forguncy.SaveLoad.SaveLoadFileBase", true)!;
        if (saveFile.GetMethod("SaveFile", Flags, null, [typeof(string), typeof(string)], null)?.ReturnType != typeof(void) ||
            saveFile.GetMethod("GetDocumentPath", Flags, null, Type.EmptyTypes, null)?.ReturnType != typeof(string))
            throw new MissingMethodException("报表配套文件原生保存契约已变化。");
        var generator = assembly.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
        if (generator.GetMethod("SaveToStr", Flags, null, [typeof(object)], null)?.ReturnType != typeof(string))
            throw new MissingMethodException("报表原生序列化契约已变化。");
        var reportManager = assembly.GetType("Forguncy.Common.ReportsManager", true)!;
        foreach (var name in new[] { "Instance", "LocalReportsPath", "ReportPathCache", "InARDesignerMode", "IsReportDataSourceEditingMode" })
            if (reportManager.GetProperty(name, Flags) is null) throw new MissingMemberException(reportManager.FullName, name);
        var generationCache = assembly.GetType("pb", true)!;
        if (generationCache.GetMethod("DirtyModules", Flags)?.GetParameters().Length != 2)
            throw new MissingMethodException("报表生成缓存失效契约已变化。");
    }

    internal static object? Get(object instance, string name) => instance.GetType().GetProperty(name, Flags)?.GetValue(instance);
    internal static object Manager(Assembly assembly) => assembly.GetType("Forguncy.Common.CollaborationManager", true)!.GetProperty("Instance", Flags)!.GetValue(null)!;
    internal static object? Call(object instance, string name, params object?[] arguments)
    {
        var methods = instance.GetType().GetMethods(Flags).Where(m => m.Name == name && m.GetParameters().Length == arguments.Length).ToArray();
        if (methods.Length != 1) throw new MissingMethodException(instance.GetType().FullName, name);
        try { return methods[0].Invoke(instance, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }
}

internal sealed record DesignerBuildProfile(string Version, string Sha256, string Name, bool HasNativeAssistant)
{
    internal static DesignerBuildProfile Resolve(Assembly assembly)
    {
        var version = assembly.GetName().Version?.ToString();
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)));
        return (version, hash) switch
        {
            ("12.0.102.0", NativeAccess.SupportedHash) => new(version, hash, "v12-102", true),
            ("11.0.104.0", "07E51B2AD8A70CE2AF337ED9A6071C81E87ACA421817ED1B8FB10A2F04CDA9BF") => new(version, hash, "v11-104", false),
            ("11.0.3.0", "AD42655737D449128A0DC4AF1EEE53FF228C933E7D0FEECAEC9A1155DDD741CF") => new(version, hash, "v11-3", false),
            _ => throw new BridgeException("UnsupportedDesignerBuild", "设计器版本或组件指纹尚未适配，未启动写入桥接。")
        };
    }
}
