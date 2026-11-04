using System.Text.RegularExpressions;
namespace YouTubeMetadataForEmby.Core;
public static class YouTubeIds
{
    private static readonly Regex Video = new(@"\[(?:youtube-)?(?<id>[A-Za-z0-9_-]{11})\]", RegexOptions.Compiled);
    private static readonly Regex Channel = new(@"\[(?:youtube-)?(?<id>(?:UC|HC)[A-Za-z0-9_-]{22})\]", RegexOptions.Compiled);
    public static string? VideoFromPath(string? path)
    {
        if (path == null) return null;
        var name = Path.GetFileName(path);
        var match = Video.Match(name);
        if (match.Success) return match.Groups["id"].Value;
        // ID-only files and common suffixes: VIDEOID.mp4, VIDEOID-thumb.jpg.
        match = Regex.Match(name, @"^(?<id>[A-Za-z0-9_-]{11})(?:[-.]?(?:thumb|thumbnail|poster))?(?:\.[^.]+)+$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["id"].Value : null;
    }
    public static string? ChannelFromPath(string? path) => path == null ? null : Channel.Match(path) is var m && m.Success ? m.Groups["id"].Value : null;
    public static bool IsVideoId(string? id) => id != null && Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{11}\z");
    public static bool IsChannelId(string? id) => id != null && Regex.IsMatch(id, @"\A(?:UC|HC)[A-Za-z0-9_-]{22}\z");
}
