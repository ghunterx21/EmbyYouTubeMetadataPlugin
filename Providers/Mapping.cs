using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using YouTubeMetadataForEmby.Core;
using YouTubeMetadataForEmby.Configuration;
using System.Text.RegularExpressions;

namespace YouTubeMetadataForEmby.Providers;
public static class Mapping
{
    public const string ProviderKey = "YouTube";
    public static MetadataResult<Episode> Episode(VideoData data, string? path)
    {
        var item = new Episode();
        if (!string.IsNullOrWhiteSpace(data.Title)) item.Name = data.Title.Trim();
        if (!string.IsNullOrWhiteSpace(data.Description)) item.Overview = data.Description.Trim();
        if (YouTubeIds.IsVideoId(data.Id)) item.ProviderIds[ProviderKey] = data.Id!;
        var date = data.Date ?? DateFromFilename(path);
        if (date.HasValue)
        {
            item.PremiereDate = date.Value;
            item.ProductionYear = date.Value.Year;
            item.SortName = date.Value.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + "-" + (item.Name ?? "");
            var opts = Plugin.Instance?.Options;
            var grouping = opts?.SeasonGrouping ?? SeasonGrouping.Year;
            item.ParentIndexNumber = grouping switch
            {
                SeasonGrouping.Single => 1,
                SeasonGrouping.YearMonth => date.Value.Year * 100 + date.Value.Month,
                SeasonGrouping.Month => date.Value.Month,
                SeasonGrouping.None => null,
                SeasonGrouping.Folder => SeasonFromFolder(path),
                _ => date.Value.Year
            };
            var numbering = opts?.EpisodeNumbering ?? EpisodeNumbering.DateStable;
            var suffix = StableSuffix(data.Id ?? path ?? "");
            item.IndexNumber = numbering switch
            {
                EpisodeNumbering.DateOnly => date.Value.Month * 100 + date.Value.Day,
                EpisodeNumbering.PlaylistOrder when data.PlaylistIndex is > 0 => data.PlaylistIndex.Value,
                _ => (date.Value.Month * 100 + date.Value.Day) * 10000 + suffix
            };
        }
        return new MetadataResult<Episode> { HasMetadata = true, Item = item };
    }
    public static MetadataResult<Series> Series(VideoData data)
    {
        var item = new Series();
        if (!string.IsNullOrWhiteSpace(data.Title)) item.Name = data.Title.Trim();
        if (!string.IsNullOrWhiteSpace(data.Description)) item.Overview = data.Description.Trim();
        var id = data.ChannelId ?? data.Id;
        if (YouTubeIds.IsChannelId(id)) item.ProviderIds[ProviderKey] = id!;
        return new MetadataResult<Series> { HasMetadata = true, Item = item };
    }
    public static MetadataResult<Movie> Movie(VideoData data, string? path)
    {
        var item = new Movie();
        if (!string.IsNullOrWhiteSpace(data.Title)) item.Name = data.Title.Trim();
        if (!string.IsNullOrWhiteSpace(data.Description)) item.Overview = data.Description.Trim();
        if (YouTubeIds.IsVideoId(data.Id)) item.ProviderIds[ProviderKey] = data.Id!;
        var date = data.Date ?? DateFromFilename(path);
        if (date.HasValue) { item.PremiereDate = date.Value; item.ProductionYear = date.Value.Year; }
        return new MetadataResult<Movie> { HasMetadata = true, Item = item };
    }
    private static int? SeasonFromFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var dir = Path.GetDirectoryName(path);
        for (var i = 0; i < 3 && !string.IsNullOrEmpty(dir); i++, dir = Path.GetDirectoryName(dir))
        {
            var match = Regex.Match(Path.GetFileName(dir), @"^(?:Season[ _.-]*|S)(?<n>\d{1,5})$", RegexOptions.IgnoreCase);
            if (match.Success && int.TryParse(match.Groups["n"].Value, out var season)) return season;
        }
        return null;
    }
    private static DateTime? DateFromFilename(string? path)
    {
        if (path == null) return null;
        var filename = Path.GetFileName(path);
        if (filename.Length >= 10 && DateTime.TryParseExact(filename[..10], "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return DateTime.SpecifyKind(date, DateTimeKind.Utc);
        return null;
    }
    private static int StableSuffix(string input)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input.ToLowerInvariant()));
        return (hash[0] * 256 + hash[1]) % 10000;
    }
}
