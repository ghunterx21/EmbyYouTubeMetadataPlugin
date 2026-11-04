using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Net;
using MediaBrowser.Common.Net;
using YouTubeMetadataForEmby.Core;
using YouTubeMetadataForEmby.Configuration;

namespace YouTubeMetadataForEmby.Providers;

// Remote provider makes the plugin eligible for the Movie library's
// Metadata Downloaders list. All network requests require a recognizable ID.
public sealed class RemoteMovieProvider : IRemoteMetadataProvider<Movie, MovieInfo>
{
    public string Name => "YouTube Metadata";

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance?.Options;
        Plugin.Instance?.Logger.Info("YouTube remote Movie provider invoked: path={0}", info.Path ?? "(none)");
        if (cfg?.Enabled != true) return new MetadataResult<Movie>();
        var id = YouTubeIds.VideoFromPath(info.Path);
        if (!YouTubeIds.IsVideoId(id)) return new MetadataResult<Movie>();
        var (merged, online, local) = await MetadataSourceResolver.ResolveAsync(info.Path, id, false, cfg, cancellationToken);
        if (merged == null) return new MetadataResult<Movie>();
        if (online != null && cfg.AllowSidecarWrites && !string.IsNullOrWhiteSpace(info.Path))
            await SidecarWriter.CreateMissingAsync(info.Path, merged, cancellationToken);
        Plugin.Instance?.Logger.Info("YouTube remote Movie provider success: id={0}, source={1}", id!, MetadataSourceResolver.DescribeSources(merged, online, local));
        return Mapping.Movie(merged, info.Path);
    }

    public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        // Avoid searching by arbitrary movie title: only ID-bearing filenames
        // are YouTube items. Metadata retrieval is handled by GetMetadata.
        return Task.FromResult<IEnumerable<RemoteSearchResult>>(Array.Empty<RemoteSearchResult>());
    }

    public Task<HttpResponseInfo> GetImageResponse(string url, CancellationToken cancellationToken)
        => throw new NotSupportedException("Artwork is provided by the local YouTube artwork provider.");
}
