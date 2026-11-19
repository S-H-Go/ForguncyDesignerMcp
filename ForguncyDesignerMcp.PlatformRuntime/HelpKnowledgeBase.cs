using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ForguncyDesignerMcp.PlatformRuntime;

internal static class HelpKnowledgeBase
{
    private const string HelpRoot = "https://www.grapecity.com.cn/solutions/huozige/help/docs/";
    private static readonly Uri Manual = new(HelpRoot);
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
    private sealed record Candidate(Uri Page, string Title);
    private sealed record Article(string Title, string Url, string Snippet);

    internal static async Task<JsonElement> SearchAsync(string version, string keyword, CancellationToken token)
    {
        keyword = keyword.Trim();
        if (keyword.Length is 0 or > 300) throw new ArgumentException("keyword 必须是 1 至 300 个字符。");
        var edition = version switch
        {
            "11.0.3.0" => "v11.0", "11.0.104.0" => "v11.1", "12.0.102.0" => "v12.1",
            _ => throw new NotSupportedException($"未配置设计器 {version} 对应的帮助手册。")
        };
        var root = new Uri(Manual, edition + "/");
        var sources = await Task.WhenAll(
            TrySource(() => KnowledgeCandidates(root, keyword, token), token),
            TrySource(() => IndexCandidates(root, keyword, token), token)).ConfigureAwait(false);
        if (sources.All(source => source is null)) throw new HttpRequestException("官方知识库和版本手册索引均暂时不可用。");
        var candidates = (sources[0] ?? []).Concat(sources[1] ?? []).DistinctBy(hit => hit.Page).ToArray();
        var reads = await Task.WhenAll(candidates.Select(hit => TrySource(() => ReadArticle(hit, keyword, token), token))).ConfigureAwait(false);
        var articles = reads.SelectMany(read => read ?? []).Take(5).ToArray();
        if (articles.Length == 0 && (reads.Any(read => read is null) || sources[1] is null))
            throw new HttpRequestException($"当前无法核实 {edition} 手册的正文，请稍后重试。");
        return RuntimeJson.Serialize(new { designerVersion = version, documentationVersion = edition, keyword, manual = root.AbsoluteUri,
            knowledgeBaseAvailable = sources[0] is not null, versionIndexAvailable = sources[1] is not null,
            partial = sources.Any(source => source is null) || reads.Any(read => read is null), results = articles });
    }

    private static async Task<T[]?> TrySource<T>(Func<Task<T[]>> read, CancellationToken token)
    {
        try { return await read().ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidDataException) { return null; }
    }

    private static async Task<Candidate[]> KnowledgeCandidates(Uri root, string keyword, CancellationToken token)
    {
        using var response = await Http.PostAsJsonAsync("https://ai_agent.grapecity.com.cn/search/",
            new { keyword, mode = "search", product = "forguncy" }, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        return Items(json.RootElement).Select(hit => hit.TryGetProperty("payload", out var p) &&
            Field(p, "source_type") == "official" && Field(p, "channel") == "doc"
            ? CandidateFor(root, Field(p, "url"), Field(p, "title")) : null)
            .OfType<Candidate>().DistinctBy(hit => hit.Page).Take(3).ToArray();
    }

    private static async Task<Candidate[]> IndexCandidates(Uri root, string keyword, CancellationToken token)
    {
        var html = await Http.GetStringAsync(new Uri(root, "overview"), token).ConfigureAwait(false);
        var config = Regex.Match(html, @"window\.__GCDOCSEARCHCONFIGURATION__\s*=\s*\{(?<fields>[^}]{1,2500})").Groups["fields"].Value;
        string Setting(string name, string pattern)
        {
            var match = Regex.Match(config, name + "\\s*:\\s*\"(" + pattern + ")\"");
            return match.Success ? match.Groups[1].Value : throw new InvalidDataException("官方手册搜索配置不可用。");
        }
        var app = Setting("appId", "[A-Za-z0-9]+");
        var key = Setting("apiKey", "[A-Za-z0-9=+/]+");
        var index = Setting("indexName", "huozige-document-site-zh(?:-v[0-9]+)?");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{app}-dsn.algolia.net/1/indexes/{index}/query");
        request.Headers.Add("X-Algolia-Application-Id", app);
        request.Headers.Add("X-Algolia-API-Key", key);
        request.Content = JsonContent.Create(new { query = keyword, hitsPerPage = 24, attributesToRetrieve = new[] { "url", "hierarchy" },
            attributesToHighlight = Array.Empty<string>() });
        using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
        if (!json.RootElement.TryGetProperty("hits", out var hits)) throw new InvalidDataException("官方索引缺少搜索结果。");
        return Items(hits).Select(hit => CandidateFor(root, Field(hit, "url"),
            hit.TryGetProperty("hierarchy", out var hierarchy) ? Field(hierarchy, "lvl1") : null))
            .OfType<Candidate>().DistinctBy(hit => hit.Page).Take(5).ToArray();
    }

    private static JsonElement.ArrayEnumerator Items(JsonElement array) => array.ValueKind == JsonValueKind.Array
        ? array.EnumerateArray() : throw new InvalidDataException("官方搜索结果格式已变化。");
    private static string? Field(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static Uri? VersionedPage(Uri root, string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || !Manual.IsBaseOf(source) || source.UserInfo.Length != 0) return null;
        var path = source.AbsolutePath[Manual.AbsolutePath.Length..];
        if (Regex.IsMatch(path, @"^v[0-9]+(?:\.[0-9]+)*/", RegexOptions.IgnoreCase))
        {
            if (!source.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal)) return null;
            path = source.AbsolutePath[root.AbsolutePath.Length..];
        }
        if (path.Length == 0 || path.Split('/').Any(segment => !Regex.IsMatch(segment, @"^[a-zA-Z0-9_-][a-zA-Z0-9._%-]*$") ||
            Uri.UnescapeDataString(segment).IndexOfAny(['/', '\\', ':', '?', '#', '%']) >= 0)) return null;
        var page = new Uri(root, path);
        return root.IsBaseOf(page) ? page : null;
    }

    private static Candidate? CandidateFor(Uri root, string? url, string? title) =>
        VersionedPage(root, url) is { } page ? new(page, title ?? "") : null;

    private static async Task<Article[]> ReadArticle(Candidate hit, string keyword, CancellationToken token)
    {
        // Never pass on mixed-version snippets from discovery. Read the official
        // Markdown export for the exact manual, even for an unversioned hit.
        using var response = await Http.GetAsync(hit.Page.AbsoluteUri + ".md", token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return [];
        response.EnsureSuccessStatusCode();
        var markdown = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).TrimStart('\uFEFF').Trim();
        var lines = markdown.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2 || !lines[0].StartsWith("# ", StringComparison.Ordinal)) throw new InvalidDataException("版本手册没有返回 Markdown 正文。");
        var paragraphs = lines.Skip(1).Where(line => !line.StartsWith('#') && !line.StartsWith("!["))
            .Select(line => WebUtility.HtmlDecode(Regex.Replace(line, "<[^<>]{1,512}>", " ")).Trim())
            .Where(line => line.Length >= 8 && !line.StartsWith(">type=")).ToArray();
        if (paragraphs.Length == 0) return [];
        var excerpt = paragraphs.FirstOrDefault(line => line.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            ?? paragraphs.FirstOrDefault(line => hit.Title.Length > 0 && line.Contains(hit.Title, StringComparison.OrdinalIgnoreCase)) ?? paragraphs[0];
        var at = Math.Max(0, excerpt.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) - 80);
        return [new(lines[0][2..].Trim(), hit.Page.AbsoluteUri, excerpt.Substring(at, Math.Min(450, excerpt.Length - at)))];
    }
}
