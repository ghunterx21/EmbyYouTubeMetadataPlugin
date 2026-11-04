using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using YouTubeMetadataForEmby.Core;
namespace YouTubeMetadataForEmby.Providers;

// Read-only artwork discovery. No sidecar writes, deletions, or remote image downloads.
public sealed class LocalArtwork : ILocalImageFileProvider
{
    public string Name => "YouTube Metadata";
    public bool Supports(BaseItem item)
    {
        if (Plugin.Instance?.Options.SourcePreference == YouTubeMetadataForEmby.Configuration.SourcePreference.YouTubeOnly) return false;
        // Emby calls Supports globally, including for disabled libraries.
        // Keep this check cheap and reject unrelated items before any folder scans.
        if (item is Series) return YouTubeIds.IsChannelId(YouTubeIds.ChannelFromPath(item.Path));
        return item is Video &&
            YouTubeIds.IsVideoId(YouTubeIds.VideoFromPath(item.Path));
    }
    public List<LocalImageInfo> GetImages(BaseItem item, LibraryOptions options, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        var result = new List<LocalImageInfo>();
        if (!Supports(item) || string.IsNullOrWhiteSpace(item.Path)) return result;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var image = item is Series ? FindSeriesImage(item.Path) : FindEpisodeImage(item.Path);
            if (image != null)
            {
                // Pinchflat's -thumb.jpg is the preferred lossless-in-layout
                // landscape source when local artwork is enabled. Expose both
                // image roles without changing the source file.
                result.Add(new LocalImageInfo { FileInfo = directoryService.GetFile(image), Type = ImageType.Primary });
                if (item is Video)
                    result.Add(new LocalImageInfo { FileInfo = directoryService.GetFile(image), Type = ImageType.Thumb });
                Plugin.Instance?.Logger.Info("YouTube local thumbnail matched: {0}", image);
            }

        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return result;
    }
    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".webp", ".png" };
    private static readonly string[] Suffixes = { "-thumb", "-thumbnail", ".thumb", ".thumbnail", "", "-poster", ".poster" };
    internal static string? FindEpisodeImage(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null || !Directory.Exists(dir)) return null;
        var stem = Path.GetFileNameWithoutExtension(path);
        // Exact sibling matching is preferred, even when the media name has no YouTube ID.
        foreach (var suffix in Suffixes)
            foreach (var ext in Extensions)
            {
                var file = Path.Combine(dir, stem + suffix + ext);
                if (File.Exists(file)) return file;
            }
        // A generic image is safe only if this folder contains exactly one video.
        var media = Directory.EnumerateFiles(dir).Where(IsVideoFile).Take(2).Count();
        if (media == 1)
            foreach (var name in new[] { "thumb", "thumbnail", "poster", "folder" })
                foreach (var ext in Extensions)
                {
                    var file = Path.Combine(dir, name + ext);
                    if (File.Exists(file)) return file;
                }
        var id = YouTubeIds.VideoFromPath(path);
        var json = MetadataClient.FindLocalJson(path);
        id ??= ReadJsonId(json);
        if (id == null) return null;
        // Require a unique ID-matching candidate; do not pick another video's artwork.
        var matches = Directory.EnumerateFiles(dir)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => YouTubeIds.VideoFromPath(f) == id)
            .Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
    private static string? ReadJsonId(string? json)
    {
        if (json == null) return null;
        try
        {
            using var stream = File.Open(json, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("id", out var value) && value.ValueKind == JsonValueKind.String)
            {
                var id = value.GetString();
                return YouTubeIds.IsVideoId(id) ? id : null;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return null;
    }
    private static bool IsVideoFile(string path) => new[] { ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".avi" }
        .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    internal static string? FindSeriesImage(string path)
    {
        if (!Directory.Exists(path)) return null;
        foreach (var name in new[] { "poster", "folder", "channel", "cover", "banner" })
            foreach (var ext in Extensions)
            {
                var file = Path.Combine(path, name + ext);
                if (File.Exists(file)) return file;
            }
        return null;
    }
}
