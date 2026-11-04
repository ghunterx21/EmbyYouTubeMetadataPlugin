using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;
namespace YouTubeMetadataForEmby.Core;

// Opt-in only. Creates missing sidecars, never modifies or deletes existing files.
internal static class SidecarWriter
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private const int MaxJsonBytes = 16 * 1024 * 1024;
    // Fixed stripes bound synchronization memory while serializing plugin writes per path.
    private static readonly SemaphoreSlim[] WriteGates = Enumerable.Range(0, 128).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static SemaphoreSlim GateFor(string path) => WriteGates[(int)((uint)StringComparer.Ordinal.GetHashCode(path) % (uint)WriteGates.Length)];
    public static async Task CreateMissingAsync(string mediaPath, VideoData data, CancellationToken ct)
    {
        if (!File.Exists(mediaPath)) return;
        var stem = Path.Combine(Path.GetDirectoryName(mediaPath)!, Path.GetFileNameWithoutExtension(mediaPath));
        var json = stem + ".info.json";
        var nfo = stem + ".nfo";
        try
        {
            if (!File.Exists(json))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
                await WriteNewAsync(json, bytes, ct);
                Plugin.Instance?.Logger.Info("YTDIAG sidecar-created id={0} jsonBytes={1} uploadDate={2} duration={3} tags={4}",
                    data.Id, bytes.Length, data.UploadDate ?? "missing", data.Duration?.ToString() ?? "missing", data.Tags?.Length ?? 0);
            }
            if (!File.Exists(nfo))
            {
                var root = new XElement("episodedetails",
                    new XElement("title", data.Title ?? ""),
                    new XElement("showtitle", data.Channel ?? data.Uploader ?? ""),
                    new XElement("plot", data.Description ?? ""),
                    new XElement("uniqueid", new XAttribute("type", "youtube"), new XAttribute("default", "true"), data.Id ?? ""));
                if (data.Date is DateTime date) root.Add(new XElement("aired", date.ToString("yyyy-MM-dd")));
                var xml = new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root);
                await WriteNewAsync(nfo, System.Text.Encoding.UTF8.GetBytes(xml.ToString()), ct);
            }
            if (LocalArtworkExists(mediaPath) || !Uri.TryCreate(data.Thumbnail, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, "i.ytimg.com", StringComparison.OrdinalIgnoreCase)) return;
            using var response = await Http.GetAsync(uri, ct);
            if (!response.IsSuccessStatusCode) return;
            var ext = response.Content.Headers.ContentType?.MediaType switch
            {
                "image/jpeg" => ".jpg", "image/png" => ".png", "image/webp" => ".webp", _ => null
            };
            if (ext == null || response.Content.Headers.ContentLength is > 20_000_000) return;
            var bytesImage = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytesImage.Length is < 64 or > 20_000_000) return;
            if (!LocalArtworkExists(mediaPath)) await WriteNewAsync(stem + "-thumb" + ext, bytesImage, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Respect caller cancellation, including during image download.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException)
        { /* Best effort: filesystem failures and independent HTTP timeouts must not break metadata refresh. */ }
    }
    // Scheduled task only: merge selected online metadata into an existing JSON object.
    // Preserve all unknown fields and all existing values when online has no replacement.
    // Never edit NFO, thumbnails or the video. No write if serialized content is unchanged.
    public static async Task<string> MergeExistingJsonAsync(string mediaPath, VideoData fresh, CancellationToken ct)
    {
        var stem = Path.Combine(Path.GetDirectoryName(mediaPath)!, Path.GetFileNameWithoutExtension(mediaPath));
        var path = stem + ".info.json";
        if (!File.Exists(mediaPath) || !File.Exists(path)) return "not-present";
        var expectedId = YouTubeIds.VideoFromPath(mediaPath);
        if (!YouTubeIds.IsVideoId(expectedId) || !string.Equals(expectedId, fresh.Id, StringComparison.Ordinal)) return "filename-id-mismatch-preserved";
        var gate = GateFor(Path.GetFullPath(path));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (new FileInfo(path).Length > MaxJsonBytes) return "oversized-json-preserved";
            var originalBytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (originalBytes.Length > MaxJsonBytes) return "oversized-json-preserved";
            var original = System.Text.Encoding.UTF8.GetString(originalBytes, originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF ? 3 : 0, originalBytes.Length >= 3 && originalBytes[0] == 0xEF && originalBytes[1] == 0xBB && originalBytes[2] == 0xBF ? originalBytes.Length - 3 : originalBytes.Length);
            var root = System.Text.Json.Nodes.JsonNode.Parse(original) as System.Text.Json.Nodes.JsonObject;
            if (root == null) return "invalid-json-preserved";
            // Refuse to merge into a JSON sidecar that belongs to a different video.
            if (root.TryGetPropertyValue("id", out var existingId) && existingId != null &&
                (!string.Equals(existingId.ToString(), fresh.Id, StringComparison.Ordinal))) return "id-mismatch-preserved";
            var before = root.ToJsonString();
            void Put(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) root[key] = value; }
            void PutNumber(string key, long? value) { if (value.HasValue) root[key] = value.Value; }
            Put("id", fresh.Id); Put("title", fresh.Title); Put("description", fresh.Description);
            Put("upload_date", fresh.UploadDate); Put("channel_id", fresh.ChannelId);
            Put("channel", fresh.Channel); Put("uploader", fresh.Uploader);
            Put("thumbnail", fresh.Thumbnail); Put("webpage_url", fresh.WebpageUrl);
            PutNumber("duration", fresh.Duration); PutNumber("view_count", fresh.ViewCount);
            PutNumber("like_count", fresh.LikeCount);
            if (fresh.Tags is { Length: > 0 }) root["tags"] = System.Text.Json.JsonSerializer.SerializeToNode(fresh.Tags);
            if (fresh.Categories is { Length: > 0 }) root["categories"] = System.Text.Json.JsonSerializer.SerializeToNode(fresh.Categories);
            // Don't infer release_date or live_status from publishDate/isLiveContent.
            if (before == root.ToJsonString()) return "unchanged";
            var updatedBytes = System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // Refuse to overwrite a snapshot that has changed since it was read.
            // External processes cannot participate in our lock, so detect and skip conflicts.
            var result = await ReplaceIfUnchangedAsync(path, originalBytes, updatedBytes, ct).ConfigureAwait(false);
            return result ? "updated" : "concurrent-change-preserved";
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Plugin.Instance?.Logger.Warn("YTDIAG json-merge-failed id={0} reason={1}", fresh.Id, ex.GetType().Name);
            return "error-preserved";
        }
        finally { gate.Release(); }
    }

    private static async Task<bool> ReplaceIfUnchangedAsync(string target, byte[] original, byte[] updated, CancellationToken ct)
    {
        var temp = target + ".embymetadata-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, updated, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(target) || new FileInfo(target).Length != original.Length) return false;
            var current = await File.ReadAllBytesAsync(target, ct).ConfigureAwait(false);
            if (!current.AsSpan().SequenceEqual(original)) return false;
            // Best-effort optimistic concurrency: external writers may still race between
            // comparison and rename. A truly atomic CAS requires downloader cooperation.
            File.Move(temp, target, true);
            return true;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private static bool LocalArtworkExists(string path) => Providers.LocalArtwork.FindEpisodeImage(path) != null;
    private static async Task WriteNewAsync(string path, byte[] data, CancellationToken ct)
    {
        var temp = path + ".embymetadata-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            try { File.Move(temp, path, false); }
            catch (IOException) when (File.Exists(path)) { /* Competing creator won. */ }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
