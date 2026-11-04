using System.ComponentModel;
using Emby.Web.GenericEdit;
namespace YouTubeMetadataForEmby.Configuration;
public enum SourcePreference
{
    [Description("YouTube Only")] YouTubeOnly,
    [Description("YouTube First (Local Fallback)")] YouTubeFirstLocalFallback,
    [Description("Local First (YouTube Fallback)")] LocalFirstYouTubeFallback,
    [Description("Local Only")] LocalOnly
}
public enum SeasonGrouping { Year, Single, YearMonth, Month, Folder, None }
public enum EpisodeNumbering { [Description("Date Stable")] DateStable, [Description("Date Only")] DateOnly, [Description("Playlist Order")] PlaylistOrder }
public sealed class PluginConfiguration : EditableOptionsBase
{
    public override string EditorTitle => "YouTube Metadata";
    public bool Enabled { get; set; } = true;
    public SourcePreference SourcePreference { get; set; } = SourcePreference.YouTubeFirstLocalFallback;
    public bool AllowSidecarWrites { get; set; } = false;
    public bool UpdateExistingJson { get; set; } = false;
    public bool UseLocalJson { get; set; } = true;
    public bool UseBuiltInExtractor { get; set; } = true;
    public SeasonGrouping SeasonGrouping { get; set; } = SeasonGrouping.Year;
    public EpisodeNumbering EpisodeNumbering { get; set; } = EpisodeNumbering.DateStable;
    public bool WriteLocalJson { get; set; } = false;
    public bool CreateMissingSidecars { get; set; } = false;
    public bool UseLocalArtwork { get; set; } = true;
    public bool DownloadArtwork { get; set; } = false;
    public bool ReplaceLocalArtwork { get; set; } = false;
    public int CacheHours { get; set; } = 24;
    public int OnlineTimeoutSeconds { get; set; } = 25;
}
