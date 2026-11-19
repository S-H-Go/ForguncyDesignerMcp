using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace ForguncyDesignerMcp.Designer;

internal sealed record EmbeddedMcpSettings(bool AutoStart, int PreferredPort, string BearerToken)
{
    internal const int DefaultPort = 39291;
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ForguncyDesignerMcp", "embedded-mcp.json");
    internal static EmbeddedMcpSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            var created = new EmbeddedMcpSettings(true, DefaultPort, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
            if (Write(path, created, false)) return created;
        }
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var settings = JsonSerializer.Deserialize<EmbeddedMcpSettings>(input) ?? throw new InvalidDataException("MCP settings are invalid.");
        Validate(settings); return settings;
    }
    internal static void Save(string path, EmbeddedMcpSettings settings)
    {
        _ = Write(path, settings, true);
    }
    private static bool Write(string path, EmbeddedMcpSettings settings, bool overwrite)
    {
        Validate(settings);
        var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, settings); output.Flush(true); }
            try { File.Move(temporary, full, overwrite); return true; }
            catch (IOException) when (!overwrite && File.Exists(full)) { return false; }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(EmbeddedMcpSettings settings)
    {
        if (settings.PreferredPort is < 1024 or > 65519 || settings.BearerToken is null || settings.BearerToken.Length != 64 ||
            settings.BearerToken.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException("MCP settings are invalid.");
    }
}
