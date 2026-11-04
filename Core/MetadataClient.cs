using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YouTubeMetadataForEmby.Configuration;
namespace YouTubeMetadataForEmby.Core;
public static class MetadataClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    // Short-lived per-ID cache avoids duplicate watch-page downloads from Emby's
    // local/remote providers during a single library refresh. A short TTL deliberately
    // avoids stale view counts across separately initiated scheduled refreshes.
    // Fixed lock stripes avoid retaining one semaphore for every ID ever encountered.
    private static readonly SemaphoreSlim[] FetchLocks = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset Expires, VideoData Data)> FetchCache = new();
    private static readonly TimeSpan FetchTtl = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };
    public static string? FindLocalJson(string? path, bool series = false)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!series)
        {
            var sidecar = Path.ChangeExtension(path, ".info.json");
            if (File.Exists(sidecar)) return sidecar;
            var folder = Path.GetDirectoryName(path);
            var id = YouTubeIds.VideoFromPath(path);
            if (folder == null || !Directory.Exists(folder)) return null;
            var jsonFiles = Directory.EnumerateFiles(folder, "*.info.json").ToArray();
            if (id != null)
            {
                var byId = jsonFiles.FirstOrDefault(f => YouTubeIds.VideoFromPath(f) == id);
                if (byId != null) return byId;
            }
            // If a single video and a single info.json share a directory, matching is unambiguous.
            var videoCount = Directory.EnumerateFiles(folder).Count(f => new[] { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".avi" }
                .Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));
            return videoCount == 1 && jsonFiles.Length == 1 ? jsonFiles[0] : null;
        }
        return Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.info.json").FirstOrDefault(f => Path.GetFileName(f).StartsWith("channel.", StringComparison.OrdinalIgnoreCase)) : null;
    }
    public static async Task<VideoData?> ReadLocalAsync(string? path, CancellationToken ct)
    {
        if (path == null) return null;
        try { await using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return await JsonSerializer.DeserializeAsync<VideoData>(stream, Json, ct); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static async Task<VideoData?> GetAsync(string id, bool channel, PluginConfiguration cfg, CancellationToken ct)
    {
        if (!cfg.Enabled || !(channel ? YouTubeIds.IsChannelId(id) : YouTubeIds.IsVideoId(id))) return null;
        if (!cfg.UseBuiltInExtractor) { Plugin.Instance?.Logger.Info("YTDIAG extraction skipped id={0} reason=built-in-extractor-disabled", id); return null; }
        var key = (channel ? "channel:" : "video:") + id;
        if (FetchCache.TryGetValue(key, out var cached) && cached.Expires > DateTimeOffset.UtcNow)
        {
            Plugin.Instance?.Logger.Info("YTDIAG metadata-cache id={0} result=hit", id);
            return cached.Data;
        }
        var gate = FetchLocks[(int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)FetchLocks.Length)];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Recheck after acquiring the gate: another caller may have fetched it.
            if (FetchCache.TryGetValue(key, out cached) && cached.Expires > DateTimeOffset.UtcNow)
            {
                Plugin.Instance?.Logger.Info("YTDIAG metadata-cache id={0} result=hit-after-wait", id);
                return cached.Data;
            }
            Plugin.Instance?.Logger.Info("YTDIAG metadata-cache id={0} result=fetch", id);
            var result = await FromPage(id, channel, ct).ConfigureAwait(false);
            if (result != null)
                FetchCache[key] = (DateTimeOffset.UtcNow.Add(FetchTtl), result);
            // Bounded housekeeping for long-lived servers; never evict live entries.
            if (FetchCache.Count > 2048)
            {
                foreach (var entry in FetchCache)
                    if (entry.Value.Expires <= DateTimeOffset.UtcNow) FetchCache.TryRemove(entry.Key, out _);
                // Enforce a hard capacity even when many distinct IDs arrive inside the TTL.
                if (FetchCache.Count > 2048)
                    foreach (var entry in FetchCache.OrderBy(e => e.Value.Expires).Take(FetchCache.Count - 2048))
                        FetchCache.TryRemove(entry.Key, out _);
            }
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException or TaskCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            Plugin.Instance?.Logger.Warn("YouTube online extractor failed for {0}: {1}: {2}", id, ex.GetType().Name, ex.Message);
            return null;
        }
        finally { gate.Release(); }
    }
    private static async Task<VideoData?> FromPage(string id, bool channel, CancellationToken ct)
    {
        var url = channel ? "https://www.youtube.com/channel/" + id + "/about" : "https://www.youtube.com/watch?v=" + id;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; EmbyYouTubeMetadata/0.3)");
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        Plugin.Instance?.Logger.Info("YTDIAG watch-response id={0} http={1} htmlChars={2}", id, (int)response.StatusCode, html.Length);
        // YouTube embeds structured data in the watch page. This extractor is deliberately isolated.
        var embedded = ExtractPlayerJson(html);
        Plugin.Instance?.Logger.Info("YTDIAG player-json id={0} found={1} jsonChars={2}", id, embedded != null, embedded?.Length ?? 0);
        if (!channel && embedded != null)
        {
            using var doc = JsonDocument.Parse(embedded);
            var hasDetails = doc.RootElement.TryGetProperty("videoDetails", out var details) && details.ValueKind == JsonValueKind.Object;
            Plugin.Instance?.Logger.Info("YTDIAG player-structure id={0} videoDetails={1} microformat={2} playabilityStatus={3}",
                id, hasDetails, doc.RootElement.TryGetProperty("microformat", out _), doc.RootElement.TryGetProperty("playabilityStatus", out _));
            if (hasDetails)
            {
                string? Read(string name) => details.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;
                long? Number(JsonElement obj, string name)
                {
                    if (!obj.TryGetProperty(name, out var value)) return null;
                    return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? n :
                        value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var n2) ? n2 : null;
                }
                string? Text(JsonElement obj, string name) => obj.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                string? DateOnly(string? value) => DateTimeOffset.TryParse(value, out var date) ? date.ToString("yyyyMMdd") : null;
                var micro = doc.RootElement.TryGetProperty("microformat", out var mfRoot) &&
                    mfRoot.TryGetProperty("playerMicroformatRenderer", out var pmRoot) ? pmRoot : default;
                var upload = micro.ValueKind == JsonValueKind.Object ? DateOnly(Text(micro, "uploadDate")) : null;
                var publish = micro.ValueKind == JsonValueKind.Object ? DateOnly(Text(micro, "publishDate")) : null;
                var duration = Number(details, "lengthSeconds");
                var count = Number(details, "viewCount");
                var tags = details.TryGetProperty("keywords", out var keywords) && keywords.ValueKind == JsonValueKind.Array
                    ? keywords.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : null;
                var category = micro.ValueKind == JsonValueKind.Object ? Text(micro, "category") : null;
                var live = details.TryGetProperty("isLiveContent", out var liveValue) && liveValue.ValueKind == JsonValueKind.True;
                var thumb = details.TryGetProperty("thumbnail", out var th) && th.TryGetProperty("thumbnails", out var arr) && arr.GetArrayLength() > 0
                    ? arr[arr.GetArrayLength()-1].GetProperty("url").GetString() : null;
                Plugin.Instance?.Logger.Info("YTDIAG extracted id={0} source=player title={1} description={2} uploadDate={3} releaseDate={4} duration={5} viewCount={6} tags={7} category={8} thumbnail={9}",
                    id, !string.IsNullOrWhiteSpace(Read("title")), !string.IsNullOrWhiteSpace(Read("shortDescription")),
                    upload ?? publish ?? "missing", "not-provided", duration?.ToString() ?? "missing", count?.ToString() ?? "missing",
                    tags?.Length ?? 0, category != null, thumb != null);
                return new VideoData { Id = id, Title = Read("title"), Description = Read("shortDescription"),
                    ChannelId = Read("channelId"), Channel = Read("author"), Uploader = Read("author"),
                    Thumbnail = thumb, WebpageUrl = url, UploadDate = upload ?? publish,
                    ReleaseDate = null, Duration = duration is >= 0 and <= int.MaxValue ? (int)duration.Value : null,
                    ViewCount = count, Tags = tags, Categories = category == null ? null : new[] { category },
                    LiveStatus = live ? "is_live" : "not_live" };
            }
        }
        // Fallback: OpenGraph tags work for some public watch and channel pages.
        string? Meta(string property) {
            var pattern = "<meta[^>]+(?:property|name)=[\\\"']" + Regex.Escape(property) + "[\\\"'][^>]+content=[\\\"']([^\\\"']*)";
            var m = Regex.Match(html, pattern, RegexOptions.IgnoreCase);
            return m.Success ? System.Net.WebUtility.HtmlDecode(m.Groups[1].Value) : null;
        }
        var title = Meta("og:title");
        Plugin.Instance?.Logger.Warn("YTDIAG extracted id={0} source=opengraph-fallback title={1} description={2} thumbnail={3}; extended fields unavailable",
            id, title != null, Meta("og:description") != null, Meta("og:image") != null);
        return title == null ? null : new VideoData { Id = id, ChannelId = channel ? id : null,
            Title = title, Description = Meta("og:description"), Thumbnail = Meta("og:image") };
    }
    private static string? ExtractPlayerJson(string html)
    {
        var match = Regex.Match(html, @"ytInitialPlayerResponse\s*=\s*\{");
        if (!match.Success) return null;
        var start = match.Index + match.Length - 1;
        var depth = 0; var inString = false; var escaped = false;
        for (var i = start; i < html.Length; i++)
        {
            var c = html[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
            }
            else if (c == '"') inString = true;
            else if (c == '{') depth++;
            else if (c == '}' && --depth == 0) return html.Substring(start, i - start + 1);
        }
        return null;
    }
}
