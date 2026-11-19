using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

internal static class NativeRemovedReport
{
    internal static bool IsRemoval(object module) =>
        R.Get(module, "ModuleType")?.ToString() is "PageReport" or "RdlReport" &&
        R.Get(module, "State")?.ToString() == "Remove";

    internal static string[] ReadPair(string project, ModuleAddress address, object module)
    {
        if (address.Kind != ModuleKind.Report || !IsRemoval(module) ||
            !Equals(R.Get(module, "ModuleName"), $"{R.Get(module, "ModuleType")}/{address.Name}") ||
            R.Get(module, "ToRemoveFiles") is not IEnumerable<string> files)
            throw Invalid();
        var paths = files.Select(path => NativeSaveVerification.Relative(project, path)).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length != 2 || !paths[0].StartsWith("Reports/", StringComparison.Ordinal) ||
            Path.GetFileName(paths[0]) != address.Name + ".json" ||
            paths[1] != Path.ChangeExtension(paths[0], ".rdlx") ||
            address.FolderPath is not null && paths[0] != $"Reports/{address.FolderPath}/{address.Name}.json")
            throw Invalid();
        return paths;
    }

    private static BridgeException Invalid() => new("RemovedReportFileGroupInvalid",
        "The native Remove registration must contain exactly this report's JSON and RDLX in the same project folder.");
}
