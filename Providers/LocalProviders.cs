using System.Diagnostics;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using YouTubeMetadataForEmby.Core;
using YouTubeMetadataForEmby.Configuration;

namespace YouTubeMetadataForEmby.Providers;

// Emby's original ILocalMetadataProvider contract is used deliberately.
// This avoids the version-dependent IRemoteSearchProvider.GetImageResponse API.
public abstract class LocalProvider<T> : ILocalMetadataProvider<T>, IHasItemChangeMonitor
    where T : BaseItem
{
    public string Name => "YouTube Metadata";

    public bool HasChanged(BaseItem item, LibraryOptions options, IDirectoryService directoryService)
    {
        var config = Plugin.Instance?.Options;
        if (config?.Enabled != true || config.SourcePreference == SourcePreference.YouTubeOnly) return false;
        var json = MetadataClient.FindLocalJson(item.Path, item is Series);
        return json != null && File.GetLastWriteTimeUtc(json) > item.DateLastSaved.ToUniversalTime();
    }

    public async Task<MetadataResult<T>> GetMetadata(ItemInfo info, LibraryOptions options,
        IDirectoryService directoryService, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        Plugin.Instance?.Logger.Info("YouTube local provider invoked: type={0}, path={1}", typeof(T).Name, info.Path ?? "(null)");
        var config = Plugin.Instance?.Options;
        if (config?.Enabled != true) { Plugin.Instance?.Logger.Info("YouTube local provider skipped: disabled"); return new MetadataResult<T>(); }

        bool isSeries = typeof(T) == typeof(Series);
        var id = isSeries ? YouTubeIds.ChannelFromPath(info.Path) : YouTubeIds.VideoFromPath(info.Path);
        var (merged, online, local) = await MetadataSourceResolver.ResolveAsync(info.Path, id, isSeries, config, ct);
        // SAFETY GATE: Emby does not expose an unambiguous user-initiated
        // refresh signal through this provider contract. Never write or delete media
        // sidecars during metadata discovery, regardless of previously saved settings.
        if (merged == null) { Plugin.Instance?.Logger.Info("YouTube provider no matching metadata: type={0}, id={1}, elapsedMs={2}", typeof(T).Name, id ?? "none", sw.ElapsedMilliseconds); return new MetadataResult<T>(); }
        // Explicit opt-in only: create missing sidecars when online data was actually fetched.
        // This runs during Emby discovery/refresh, so never overwrite existing sidecars.
        if (!isSeries && online != null && config.AllowSidecarWrites && !string.IsNullOrWhiteSpace(info.Path))
            await SidecarWriter.CreateMissingAsync(info.Path, merged, ct);
        if (isSeries && YouTubeIds.IsChannelId(id)) merged.ChannelId ??= id;
        Plugin.Instance?.Logger.Info("YouTube provider success: type={0}, source={1}, id={2}, elapsedMs={3}", typeof(T).Name, MetadataSourceResolver.DescribeSources(merged, online, local), id ?? "none", sw.ElapsedMilliseconds);
        return Convert(merged, info.Path);
    }

    protected abstract MetadataResult<T> Convert(VideoData data, string? path);
}

public sealed class LocalEpisodeProvider : LocalProvider<Episode>
{
    protected override MetadataResult<Episode> Convert(VideoData data, string? path)
        => Mapping.Episode(data, path);
}

public sealed class LocalSeriesProvider : LocalProvider<Series>
{
    protected override MetadataResult<Series> Convert(VideoData data, string? path)
        => Mapping.Series(data);
}

public sealed class LocalMovieProvider : LocalProvider<Movie>
{
    protected override MetadataResult<Movie> Convert(VideoData data, string? path)
        => Mapping.Movie(data, path);
}
