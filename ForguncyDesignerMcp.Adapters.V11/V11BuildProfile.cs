using System.Reflection;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using ForguncyDesignerMcp.Core.Platform;

namespace ForguncyDesignerMcp.Adapters.V11;

/// <summary>Verified installed V11 component pairs. Unknown builds fail closed.</summary>
public sealed record V11BuildProfile(string Version, string DesignerSha256, string CommonSha256,
    string DocumentType, string DocumentProperty, string GenerationCacheType)
{
    private static readonly ConditionalWeakTable<Assembly, V11BuildProfile> Profiles = new();
    private static readonly V11BuildProfile[] Known =
    [
        new("11.0.104.0", "07E51B2AD8A70CE2AF337ED9A6071C81E87ACA421817ED1B8FB10A2F04CDA9BF",
            "BE23CB916F6C9B9BF7365B54D8BBB36263962E6BE28567C70629366DEC32E436", "ej", "dc", "ok"),
        new("11.0.3.0", "AD42655737D449128A0DC4AF1EEE53FF228C933E7D0FEECAEC9A1155DDD741CF",
            "F1477113B178D0424BC617C755DF4D6B0D7985D215C6C745FAAA92D85C072421", "ef", "cy", "of")
    ];

    public AdapterIdentity Identity => new(Version, DesignerSha256, "v11-native-" + Version);
    public string PageLoaderType => Version == "11.0.104.0" ? "cm" : "cl";
    public string RedundantProjection => Version == "11.0.104.0" ? "rx" : "ro";
    public string ModelSha256 => Version == "11.0.104.0"
        ? "56965CCB33A80CAAF22A995D7B006B9503FBED589A15A5DFF8A3DE4C7F07EDE8"
        : "A235E81DA6E9867E84634D6DA0578D5B0A0C27D8018E0F2D2FD7BF438B26E309";
    public string SpreadsheetSha256 => Version == "11.0.104.0"
        ? "D86617A5368022FAFFA9E1B122264632D5CB93BC7ED630CE4BE6F53FBE8EE5D3"
        : "A9F3344A54DCAB72A27D0483E5123E900B75EBD38DA4136D2422A573258B8D73";

    public static bool Supports(AdapterIdentity identity) => Known.Any(profile =>
        StringComparer.Ordinal.Equals(profile.Version, identity.DesignerVersion) &&
        StringComparer.OrdinalIgnoreCase.Equals(profile.DesignerSha256, identity.DesignerSha256));

    public static V11BuildProfile Resolve(Assembly designer) => Profiles.GetValue(designer, Validate);

    private static V11BuildProfile Validate(Assembly designer)
    {
        if (designer.GetName().Name != "Forguncy" || string.IsNullOrEmpty(designer.Location))
            throw new NotSupportedException("The V11 adapter requires an installed Forguncy designer assembly.");
        var version = designer.GetName().Version?.ToString();
        var hash = Hash(designer.Location);
        var profile = Known.SingleOrDefault(candidate => candidate.Version == version && candidate.DesignerSha256 == hash)
            ?? throw new NotSupportedException("Unsupported V11 designer version or component fingerprint.");
        var context = AssemblyLoadContext.GetLoadContext(designer)
            ?? throw new NotSupportedException("The V11 component load context is unavailable.");
        var common = context.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == "CommonUtilities");
        if (common is null)
        {
            var commonPath = Path.Combine(Path.GetDirectoryName(designer.Location)!, "CommonUtilities.dll");
            if (!File.Exists(commonPath)) throw new FileNotFoundException("The installed V11 CommonUtilities component is missing.", commonPath);
            common = context.LoadFromAssemblyPath(commonPath);
        }
        if (common.GetName().Version?.ToString() != version || string.IsNullOrEmpty(common.Location) || Hash(common.Location) != profile.CommonSha256)
            throw new NotSupportedException("The V11 designer and CommonUtilities components do not match the verified build.");
        ValidateDependency("ServerDesignerCommon", profile.ModelSha256, profile.Version);
        ValidateDependency("GrapeCity.WPF.SpreadSheet.Data", profile.SpreadsheetSha256, "7.40.20142.1");
        var document = designer.GetType(profile.DocumentType, true)!;
        foreach (var property in new[] { "CurrentFilePath", "Workbook", "ForguncyColleagueManager", "ContainsUnsavedChanges" })
            if (document.GetProperty(property, NativeV11Reflection.Flags) is null)
                throw new MissingMemberException(document.FullName, property);
        if (document.GetMethod("SaveAsync", NativeV11Reflection.Flags, null, [typeof(bool)], null)?.ReturnType != typeof(Task<bool>))
            throw new MissingMethodException(document.FullName, "SaveAsync(bool): Task<bool>");
        var generator = designer.GetType("Forguncy.SaveLoad.PageSaveDataGenerator", true)!;
        if (generator.GetMethod("SaveToStr", NativeV11Reflection.Flags, null, [typeof(object)], null)?.ReturnType != typeof(string))
            throw new MissingMethodException(generator.FullName, "SaveToStr(object): string");
        return profile;

        void ValidateDependency(string name, string expectedHash, string expectedVersion)
        {
            var dependency = context.Assemblies.SingleOrDefault(assembly => assembly.GetName().Name == name) ??
                context.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(designer.Location)!, name + ".dll"));
            if (dependency.GetName().Version?.ToString() != expectedVersion || string.IsNullOrEmpty(dependency.Location) || Hash(dependency.Location) != expectedHash)
                throw new NotSupportedException("A V11 native model/serializer dependency does not match the verified component set: " + name);
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
