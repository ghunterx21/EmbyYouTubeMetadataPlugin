using System.ComponentModel;
using Emby.Web.GenericEdit;
using YouTubeMetadataForEmby.Configuration;
namespace YouTubeMetadataForEmby.UI;
public sealed class MetadataPage : EditableOptionsBase
{
    public override string EditorTitle => "YouTube Metadata — Settings";
    [Browsable(false)] public bool SavedSuccessfully { get; set; }
    public override string EditorDescription => (SavedSuccessfully ? "Settings saved successfully. " : "") + "Choose whether YouTube or local files take priority for metadata and thumbnails. Optional file creation saves missing metadata and thumbnail images beside each video, without overwriting existing files. For manually downloaded videos, include the YouTube ID in square brackets in the filename, e.g. Example Video Name [vb34fsns_2].mp4. The plugin does not rename videos. Enable JSON updates separately to merge online metadata into existing JSON (useful with yt-dlp, Pinchflat, Tube Archivist and similar tools); NFO files and artwork are never replaced.";
    [DisplayName("Enable Metadata Provider")] public bool Enabled { get; set; } = true;
    [DisplayName("Create Metadata and Thumbnail Images")] public bool AllowSidecarWrites { get; set; } = false;
    [DisplayName("Update Existing JSON (Preserve Other Fields)")] public bool UpdateExistingJson { get; set; } = false;
    [DisplayName("Source Preference")] public SourcePreference SourcePreference { get; set; } = SourcePreference.YouTubeFirstLocalFallback;
    [DisplayName("Mixed / TV Show Libraries — Season Grouping")] public SeasonGrouping SeasonGrouping { get; set; } = SeasonGrouping.Year;
    [DisplayName("Episode Numbering")] public EpisodeNumbering EpisodeNumbering { get; set; } = EpisodeNumbering.DateStable;
    [DisplayName("Online Request Timeout (Seconds)")] public int OnlineTimeoutSeconds { get; set; } = 25;
    [DisplayName("Online Metadata Cache Duration (Hours)")] public int CacheHours { get; set; } = 24;
    [Browsable(false)] public bool UseLocalJson { get; set; } = true;
    [Browsable(false)] public bool UseBuiltInExtractor { get; set; } = true;
    [Browsable(false)] public bool CreateMissingSidecars { get; set; } = false;
}
