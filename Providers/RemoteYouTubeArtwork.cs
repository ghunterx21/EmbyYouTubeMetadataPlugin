using System.Net.Http;
using System.Diagnostics;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Net;
using YouTubeMetadataForEmby.Core;

namespace YouTubeMetadataForEmby.Providers;

// Remote image fetcher for Movies. Emby controls image replacement and cache
// invalidation; this provider never changes or deletes files in the media folder.
public sealed class RemoteYouTubeArtwork : IRemoteImageProviderWithOptions
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static int _constructCount;
    private static int _supportsCount;
    private static int _getImagesCount;
    private static int _imageTypesCount;
    public RemoteYouTubeArtwork()
    {
        if (System.Threading.Interlocked.Increment(ref _constructCount) <= 5)
            Plugin.Instance?.Logger.Info("YT image diagnostic: provider constructed, count={0}", _constructCount);
    }
    public string Name => "YouTube Thumbnail";

    // Emby can pass a generic Video rather than Movie in the image-search UI.
    // Support the generic video type, but never offer artwork without a YouTube ID.
    // Avoid filesystem and network work in Supports.
    public bool Supports(BaseItem item)
    {
        var isVideo = item is Video;
        var enabled = Plugin.Instance?.Options.Enabled == true;
        var id = isVideo ? YouTubeIds.VideoFromPath(item.Path) : null;
        // Probe Emby's provider eligibility independently of video-ID extraction.
        // GetImages still validates the ID before returning a URL.
        var result = isVideo && enabled && Plugin.Instance?.Options.SourcePreference != YouTubeMetadataForEmby.Configuration.SourcePreference.LocalOnly;
        var count = System.Threading.Interlocked.Increment(ref _supportsCount);
        // Bounded diagnostics: show the first 40 calls, plus YouTube matches.
        if (isVideo || count <= 8)
            Plugin.Instance?.Logger.Info("YT image diagnostic: Supports call={0}, type={1}, video={2}, enabled={3}, id={4}, result={5}",
                count, item.GetType().FullName ?? "(unknown)", isVideo, enabled, id ?? "(none)", result);
        return result;
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        var count = System.Threading.Interlocked.Increment(ref _imageTypesCount);
        if (item is Video || count <= 8)
            Plugin.Instance?.Logger.Info("YT image diagnostic: GetSupportedImages call={0}, type={1}", count, item.GetType().FullName ?? "(unknown)");
        return new[] { ImageType.Primary, ImageType.Thumb };
    }

    // Emby 4.10 image provider interface contract. Resolve the item defensively
    // because SDK versions expose different members on RemoteImageFetchOptions.
    public Task<IEnumerable<RemoteImageInfo>> GetImages(RemoteImageFetchOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (options is null)
            return Task.FromResult<IEnumerable<RemoteImageInfo>>(Array.Empty<RemoteImageInfo>());

        var optionType = options.GetType();
        BaseItem? item = null;
        foreach (var propertyName in new[] { "Item", "BaseItem" })
        {
            var property = optionType.GetProperty(propertyName);
            if (property?.GetValue(options) is BaseItem found)
            {
                item = found;
                break;
            }
        }
        if (item is null)
        {
            Plugin.Instance?.Logger.Warn("YT image diagnostic: RemoteImageFetchOptions has no accessible Item/BaseItem; properties={0}",
                string.Join(",", optionType.GetProperties().Select(p => p.Name)));
            return Task.FromResult<IEnumerable<RemoteImageInfo>>(Array.Empty<RemoteImageInfo>());
        }
        Plugin.Instance?.Logger.Info("YT image diagnostic: GetImages(RemoteImageFetchOptions) dispatched for {0}", item.GetType().FullName ?? "(unknown)");
        return GetImages(item, cancellationToken);
    }

    // Compatibility overload for hosts that call the original remote-image contract.
    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
        => GetImages(item, new LibraryOptions(), cancellationToken);

    public Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, LibraryOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var id = YouTubeIds.VideoFromPath(item.Path);
        var request = System.Threading.Interlocked.Increment(ref _getImagesCount);
        Plugin.Instance?.Logger.Info("YT image diagnostic: GetImages call={0}, type={1}, path={2}, id={3}, enabled={4}, source={5}",
            request, item.GetType().FullName ?? "(unknown)", item.Path ?? "(none)", id ?? "(none)",
            Plugin.Instance?.Options.Enabled == true, Plugin.Instance?.Options.SourcePreference.ToString() ?? "(none)");
        if (!Supports(item) || !YouTubeIds.IsVideoId(id) || Plugin.Instance?.Options.SourcePreference == YouTubeMetadataForEmby.Configuration.SourcePreference.LocalOnly)
            {
            Plugin.Instance?.Logger.Info("YT image diagnostic: GetImages skipped, call={0}, id={1}", request, id ?? "(none)");
            return Task.FromResult<IEnumerable<RemoteImageInfo>>(Array.Empty<RemoteImageInfo>());
        }

        // Offer both types: Emby movie grids use Primary, while landscape-aware
        // clients can select Thumb. The actual JPEG bytes are never resized.
        var url = $"https://i.ytimg.com/vi/{id}/maxresdefault.jpg";
        var images = new[] { ImageType.Primary, ImageType.Thumb }
            .Select(type => new RemoteImageInfo
            {
                ProviderName = Name,
                Type = type,
                Url = url
            }).ToArray();
        Plugin.Instance?.Logger.Info("YouTube remote thumbnail offered: id={0}, types=Primary,Thumb, url={1}", id!, url);
        return Task.FromResult<IEnumerable<RemoteImageInfo>>(images);
    }

    public async Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "i.ytimg.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only HTTPS YouTube thumbnail URLs are allowed.", nameof(url));

        // YouTube's maxres URL is not guaranteed for every video. Keep every
        // candidate landscape-oriented and avoid the 4:3 hqdefault image.
        var timer = Stopwatch.StartNew();
        var videoId = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (videoId.Length != 3 || videoId[0] != "vi" ||
            !YouTubeIds.IsVideoId(videoId[1]) ||
            videoId[2] != "maxresdefault.jpg" ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Unexpected YouTube thumbnail URL.", nameof(url));

        foreach (var quality in new[] { "maxresdefault", "hq720", "mqdefault" })
        {
            var candidate = new Uri($"https://i.ytimg.com/vi/{videoId[1]}/{quality}.jpg");
            try
            {
                using var response = await Http.GetAsync(candidate, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    Plugin.Instance?.Logger.Info("YT image candidate unavailable: {0}, status={1}", quality, (int)response.StatusCode);
                    continue;
                }
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (bytes.Length is < 1024 or > 20_000_000 ||
                    bytes[0] != 0xff || bytes[1] != 0xd8)
                {
                    Plugin.Instance?.Logger.Warn("YT image candidate rejected: {0}, bytes={1}", quality, bytes.Length);
                    continue;
                }
                Plugin.Instance?.Logger.Info("YT image selected: {0}, bytes={1}, elapsedMs={2}", quality, bytes.Length, timer.ElapsedMilliseconds);
                return new HttpResponseInfo
                {
                    Content = new MemoryStream(bytes, writable: false),
                    ContentType = "image/jpeg"
                };
            }
            catch (HttpRequestException ex)
            {
                Plugin.Instance?.Logger.Warn("YT image candidate failed: {0}: {1}", quality, ex.Message);
            }
        }
        throw new HttpRequestException("No usable landscape YouTube thumbnail found.");
    }
}
