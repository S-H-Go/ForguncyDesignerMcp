using System.Security.Cryptography;
using System.Text.Json;

namespace ForguncyDesignerMcp.Core.Platform;

// Per-router cursors bind exact final schemas, filters and limit. No process,
// filesystem or native mutation can be addressed through this discovery API.
internal sealed class PlatformCatalogQueries
{
    internal const int DefaultLimit = 25;
    internal const int MaximumLimit = 100;
    internal const int MaximumPageChars = 240_000;
    private readonly byte[] cursorKey = RandomNumberGenerator.GetBytes(32);
    private sealed record Cursor(int Version, string Revision, string? Namespace, string? Method, int Limit, int Offset);

    internal JsonElement List(IReadOnlyList<PlatformApiDescriptor> catalog, JsonElement parameters)
    {
        ValidateProperties(parameters, "namespace", "method");
        return PlatformJson.Element(Filter(catalog, OptionalString(parameters, "namespace"), OptionalString(parameters, "method")));
    }

    internal JsonElement Page(IReadOnlyList<PlatformApiDescriptor> catalog, JsonElement parameters)
    {
        ValidateProperties(parameters, "namespace", "method", "limit", "cursor");
        var ns = OptionalString(parameters, "namespace");
        var method = OptionalString(parameters, "method");
        var limit = DefaultLimit;
        if (parameters.TryGetProperty("limit", out var requestedLimit) &&
            (requestedLimit.ValueKind != JsonValueKind.Number || !requestedLimit.TryGetInt32(out limit)))
            throw new BridgeException("InvalidCatalogQuery", "Catalog page limit must be an integer.");
        if (limit is < 1 or > MaximumLimit) throw new BridgeException("InvalidCatalogQuery", "Catalog page limit must be between 1 and 100.");
        var ordered = catalog.OrderBy(x => x.Namespace, StringComparer.Ordinal).ThenBy(x => x.Method, StringComparer.Ordinal).ToArray();
        var revision = Wire.Hash(PlatformJson.Element(ordered).GetRawText());
        var filtered = Filter(ordered, ns, method).ToArray();
        var cursorText = OptionalString(parameters, "cursor", 16_384);
        var offset = 0;
        if (cursorText is not null)
        {
            var cursor = Decode(cursorText);
            if (cursor.Revision != revision) throw new BridgeException("CatalogChanged", "Final API schemas changed; restart catalog pagination.");
            if (cursor.Namespace != ns || cursor.Method != method || cursor.Limit != limit)
                throw new BridgeException("CatalogCursorMismatch", "Catalog cursor is bound to the exact namespace, method and limit.");
            if (cursor.Version != 1 || cursor.Offset < 0 || cursor.Offset >= filtered.Length)
                throw new BridgeException("InvalidCatalogCursor", "Catalog cursor offset or version is invalid.");
            offset = cursor.Offset;
        }

        var items = new List<PlatformApiDescriptor>();
        JsonElement Render(int end) => PlatformJson.Element(new
        {
            items, nextCursor = end < filtered.Length ? Encode(new(1, revision, ns, method, limit, end)) : null,
            catalogRevision = revision, totalCount = filtered.Length
        });
        var result = Render(offset);
        while (offset + items.Count < filtered.Length && items.Count < limit)
        {
            items.Add(filtered[offset + items.Count]);
            var candidate = Render(offset + items.Count);
            if (candidate.GetRawText().Length > MaximumPageChars)
            {
                items.RemoveAt(items.Count - 1);
                if (items.Count == 0)
                    throw new BridgeException("CatalogEntryTooLarge", "A complete descriptor exceeds the catalog page budget. Read that exact namespace/method with catalog.list; schemas are never truncated.");
                break;
            }
            result = candidate;
        }
        return result;
    }

    private static IEnumerable<PlatformApiDescriptor> Filter(IEnumerable<PlatformApiDescriptor> catalog, string? ns, string? method) =>
        catalog.Where(x => (ns is null || string.Equals(x.Namespace, ns, StringComparison.Ordinal)) &&
                           (method is null || string.Equals(x.Method, method, StringComparison.Ordinal)));

    private static string? OptionalString(JsonElement parameters, string name, int maximumLength = 512)
    {
        if (!parameters.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()) || value.GetString()!.Length > maximumLength)
            throw new BridgeException("InvalidCatalogQuery", name + " must be a nonempty string within its catalog schema limit.");
        return value.GetString();
    }

    private static void ValidateProperties(JsonElement parameters, params string[] allowed)
    {
        if (parameters.ValueKind != JsonValueKind.Object) throw new BridgeException("InvalidCatalogQuery", "Catalog parameters must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in parameters.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new BridgeException("InvalidCatalogQuery", "Unknown or duplicate catalog parameter: " + property.Name);
    }

    private string Encode(Cursor cursor)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(cursor, PlatformJson.Options);
        return Base64Url(payload) + "." + Base64Url(HMACSHA256.HashData(cursorKey, payload));
    }

    private Cursor Decode(string text)
    {
        try
        {
            var parts = text.Split('.');
            if (parts.Length != 2) throw new FormatException();
            var payload = FromBase64Url(parts[0]);
            var signature = FromBase64Url(parts[1]);
            if (!CryptographicOperations.FixedTimeEquals(signature, HMACSHA256.HashData(cursorKey, payload))) throw new FormatException();
            return JsonSerializer.Deserialize<Cursor>(payload, PlatformJson.Options) ?? throw new FormatException();
        }
        catch (Exception error) when (error is FormatException or JsonException)
        { throw new BridgeException("InvalidCatalogCursor", "Catalog cursor is invalid, altered, or belongs to another router/session."); }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
    }
}
