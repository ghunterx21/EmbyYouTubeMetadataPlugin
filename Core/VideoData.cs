using System.Text.Json.Serialization;
namespace YouTubeMetadataForEmby.Core;
public sealed class VideoData
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("upload_date")] public string? UploadDate { get; set; }
    [JsonPropertyName("release_date")] public string? ReleaseDate { get; set; }
    [JsonPropertyName("timestamp")] public long? Timestamp { get; set; }
    [JsonPropertyName("channel_id")] public string? ChannelId { get; set; }
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("uploader")] public string? Uploader { get; set; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
    [JsonPropertyName("webpage_url")] public string? WebpageUrl { get; set; }
    [JsonPropertyName("categories")] public string[]? Categories { get; set; }
    [JsonPropertyName("tags")] public string[]? Tags { get; set; }
    [JsonPropertyName("playlist_index")] public int? PlaylistIndex { get; set; }
    [JsonPropertyName("release_timestamp")] public long? ReleaseTimestamp { get; set; }
    [JsonPropertyName("duration")] public int? Duration { get; set; }
    [JsonPropertyName("view_count")] public long? ViewCount { get; set; }
    [JsonPropertyName("like_count")] public long? LikeCount { get; set; }
    [JsonPropertyName("availability")] public string? Availability { get; set; }
    [JsonPropertyName("live_status")] public string? LiveStatus { get; set; }
    [JsonPropertyName("chapters")] public ChapterData[]? Chapters { get; set; }

    [JsonIgnore] public DateTime? Date
    {
        get
        {
            foreach (var value in new[] { UploadDate, ReleaseDate })
                if (DateTime.TryParseExact(value, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d)) return DateTime.SpecifyKind(d, DateTimeKind.Utc);
            if (Timestamp is > 0 and < 253402300799) return DateTimeOffset.FromUnixTimeSeconds(Timestamp.Value).UtcDateTime;
            return null;
        }
    }
    public void FillMissingFrom(VideoData? other)
    {
        if (other == null) return;
        Id ??= other.Id;
        if (string.IsNullOrWhiteSpace(Title)) Title = other.Title;
        if (string.IsNullOrWhiteSpace(Description)) Description = other.Description;
        UploadDate ??= other.UploadDate; ReleaseDate ??= other.ReleaseDate; Timestamp ??= other.Timestamp;
        ChannelId ??= other.ChannelId; Channel ??= other.Channel; Uploader ??= other.Uploader;
        Thumbnail ??= other.Thumbnail; WebpageUrl ??= other.WebpageUrl;
        Categories ??= other.Categories; Tags ??= other.Tags; PlaylistIndex ??= other.PlaylistIndex;
        ReleaseTimestamp ??= other.ReleaseTimestamp; Duration ??= other.Duration;
        ViewCount ??= other.ViewCount; LikeCount ??= other.LikeCount;
        Availability ??= other.Availability; LiveStatus ??= other.LiveStatus; Chapters ??= other.Chapters;
    }
}

public sealed class ChapterData
{
    [JsonPropertyName("start_time")] public double StartTime { get; set; }
    [JsonPropertyName("end_time")] public double? EndTime { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
}
