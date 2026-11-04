using MediaBrowser.Common;
using MediaBrowser.Model.Logging;
using YouTubeMetadataForEmby.Configuration;
using YouTubeMetadataForEmby.UIBaseClasses.Store;
namespace YouTubeMetadataForEmby.Storage;
public sealed class MyOptionsStore : SimpleFileStore<PluginConfiguration>
{
 public MyOptionsStore(IApplicationHost host, ILogger logger) : base(host,logger,"Emby YouTube Metadata Plugin Fresh 030") {}
}
