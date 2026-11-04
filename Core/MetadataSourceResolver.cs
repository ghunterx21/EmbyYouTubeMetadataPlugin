using YouTubeMetadataForEmby.Configuration;

namespace YouTubeMetadataForEmby.Core;

// Shared source policy for Emby providers and scheduled-task verification.
// Never trust a sidecar whose declared identity disagrees with the media path.
public static class MetadataSourceResolver
{
    public static string DescribeSources(VideoData? merged, VideoData? online, VideoData? local) =>
        merged == null ? "none" : online != null && local != null ? "merged" :
        online != null ? "online" : local != null ? "local" : "none";

    public static bool HasDisplayMetadata(VideoData? data) => data != null &&
        (!string.IsNullOrWhiteSpace(data.Title) || !string.IsNullOrWhiteSpace(data.Description));

    public static bool IsComplete(VideoData? data) => data != null &&
        !string.IsNullOrWhiteSpace(data.Title) && !string.IsNullOrWhiteSpace(data.Description);

    public static bool IsMatchingLocal(VideoData? data, string? id, bool channel)
    {
        if (data == null) return false;
        if (channel)
        {
            var declared = data.ChannelId ?? data.Id;
            return YouTubeIds.IsChannelId(declared) &&
                (id == null || string.Equals(declared, id, StringComparison.Ordinal));
        }
        return YouTubeIds.IsVideoId(data.Id) &&
            (id == null || string.Equals(data.Id, id, StringComparison.Ordinal));
    }

    public static async Task<(VideoData? Merged, VideoData? Online, VideoData? Local)> ResolveAsync(
        string? path, string? id, bool channel, PluginConfiguration config, CancellationToken ct)
    {
        var preference = config.SourcePreference;
        var onlineFirst = preference is SourcePreference.YouTubeOnly or SourcePreference.YouTubeFirstLocalFallback;
        var allowLocal = preference != SourcePreference.YouTubeOnly;
        var allowOnline = preference != SourcePreference.LocalOnly;
        string? effectiveId = channel ? (YouTubeIds.IsChannelId(id) ? id : null) : (YouTubeIds.IsVideoId(id) ? id : null);
        VideoData? local = null, online = null;
        async Task ReadLocal()
        {
            if (!allowLocal) return;
            var candidate = await MetadataClient.ReadLocalAsync(MetadataClient.FindLocalJson(path, channel), ct).ConfigureAwait(false);
            if (IsMatchingLocal(candidate, id, channel))
            {
                local = candidate;
                if (effectiveId == null) effectiveId = channel ? (candidate!.ChannelId ?? candidate.Id) : candidate!.Id;
            }
            else if (candidate != null) Plugin.Instance?.Logger.Warn("YouTube local sidecar rejected: missing or mismatched ID; path={0}", path ?? "(null)");
        }
        async Task ReadOnline()
        {
            if (allowOnline && effectiveId != null)
                online = await MetadataClient.GetAsync(effectiveId, channel, config, ct).ConfigureAwait(false);
        }
        if (onlineFirst)
        {
            await ReadOnline().ConfigureAwait(false);
            if (!IsComplete(online))
            {
                await ReadLocal().ConfigureAwait(false);
                // A valid sidecar can establish identity when the filename has no ID.
                // Retry the preferred online source once, only when the initial attempt
                // was impossible due to missing identity. YouTube-only never reads local.
                if (online == null && effectiveId != null && id == null)
                    await ReadOnline().ConfigureAwait(false);
            }
        }
        else
        {
            await ReadLocal().ConfigureAwait(false);
            if (!IsComplete(local)) await ReadOnline().ConfigureAwait(false);
        }
        if (local == null && online == null) return (null, online, local);
        var merged = new VideoData();
        if (onlineFirst) { merged.FillMissingFrom(online); merged.FillMissingFrom(local); }
        else { merged.FillMissingFrom(local); merged.FillMissingFrom(online); }
        if (!HasDisplayMetadata(merged)) return (null, online, local);
        merged.Id ??= effectiveId;
        if (channel && YouTubeIds.IsChannelId(effectiveId)) merged.ChannelId ??= effectiveId;
        return (merged, online, local);
    }
}
