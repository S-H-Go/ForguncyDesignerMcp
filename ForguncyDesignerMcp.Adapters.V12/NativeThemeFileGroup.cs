using System.Reflection;
using ForguncyDesignerMcp.Core;
using ForguncyDesignerMcp.Core.Platform;
using R = ForguncyDesignerMcp.Adapters.V12.NativeReflection;

namespace ForguncyDesignerMcp.Adapters.V12;

// Theme, cell styles and list styles are one existing native collaboration
// owner. Deleting a child retains and verifies the whole owner file group.
internal static class NativeThemeFileGroup
{
    internal static NativeModuleFileGroup Capture(Assembly assembly, object settings, object manager,
        string localRoot, string nativeName, Func<IEnumerable<string>> directReferences)
    {
        if (manager.GetType() != assembly.GetType("Forguncy.ThemesManager", true))
            throw new BridgeException("NativeThemeContextUnavailable", "The real native theme manager is required.");
        var json = Assembly.Load("CommonUtilities").GetType("CommonUtilities.JsonUtilities", true)!;
        string Serialize(object value) => (string)R.Static(json, "GetJsonString", value, true)!;
        var paths = new[] { "GetThemeConfigPath", "GetCellTypeStylePath", "GetListViewStylePath", "GetDefaultStylePath" }
            .Select(method => NativeSaveVerification.Relative(".", (string)R.Call(manager, method, "Theme")!)).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4)
            throw new BridgeException("NativeThemeFilesAmbiguous", "The native theme owner must have four distinct configuration files.");
        Dictionary<string,string> Current()
        {
            var theme = R.Get(manager, "CurrentTheme") ?? throw new BridgeException("NativeThemeContextUnavailable", "The current native theme is not loaded.");
            var defaults = Activator.CreateInstance(assembly.GetType("Forguncy.StyleDefaultNames", true)!)!;
            R.Set(defaults, "DefaultListViewStyleName", R.Get(settings, "DefaultListViewStyleTemplateName"));
            R.Set(defaults, "DefaultCellTypeStyleNames", R.Get(settings, "DefaultStyleKeys"));
            return new(StringComparer.Ordinal)
            {
                [paths[0]] = Serialize(theme),
                [paths[1]] = Serialize(R.Get(settings, "CustomCellTypeStyleTemplates")!),
                [paths[2]] = Serialize(R.Get(settings, "ListViewStyleTemplates")!),
                [paths[3]] = Serialize(defaults)
            };
        }
        string? FileHash(string relative)
        {
            var file = NativeSaveVerification.Under(localRoot, relative);
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > NativeFiles.MaximumAssetBytes)
                throw new BridgeException("NativeThemeResourceTooLarge", "A theme resource exceeds the verified asset limit.");
            return NativeFiles.Hash(File.ReadAllBytes(file));
        }
        var expected = Current();
        var references = directReferences().Select(path => NativeSaveVerification.Relative(".", path))
            .Except(paths, StringComparer.Ordinal).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var hashes = references.ToDictionary(path => path, FileHash, StringComparer.Ordinal);
        bool Unchanged()
        {
            var current = Current();
            return expected.All(pair => NativeModuleFiles.JsonMatches(System.Text.Encoding.UTF8.GetBytes(current[pair.Key]), pair.Value)) &&
                hashes.All(pair => FileHash(pair.Key) == pair.Value);
        }
        return new(new(ModuleKind.Theme, "Current"), nativeName, paths, hashes.Where(pair => pair.Value is null).Select(pair => pair.Key).ToArray(),
            references.Where(path => hashes[path] is not null).ToArray(), expected, Unchanged,
            (path, bytes) => expected.TryGetValue(path, out var value) ? NativeModuleFiles.JsonMatches(bytes, value) :
                hashes.TryGetValue(path, out var hash) && hash is not null && NativeFiles.Hash(bytes) == hash,
            manager, () => R.Get(manager, "CurrentTheme") is not null);
    }
}
