using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI;
using YouTubeMetadataForEmby.Configuration;
using YouTubeMetadataForEmby.Storage;
using YouTubeMetadataForEmby.UI;
namespace YouTubeMetadataForEmby;
public sealed class Plugin : BasePlugin,IHasThumbImage,IHasUIPages,IHasPluginConfiguration
{
 private readonly MyOptionsStore store;
 public ImageFormat ThumbImageFormat => ImageFormat.Jpg;
 public Stream GetThumbImage() => typeof(Plugin).Assembly.GetManifestResourceStream("YouTubeMetadataForEmby.PluginThumb.jpg") ?? throw new InvalidOperationException("Plugin artwork resource not found.");
 public ILogger Logger { get; }
 public static Plugin? Instance {get;private set;}
 public PluginConfiguration Options=>store.GetOptions();
 public override string Name=>"Emby YouTube Metadata Plugin";
 public override string Description=>"YouTube metadata from local files and supported metadata sources.";
 public override Guid Id=>new("4b3a71c0-8f92-4a7e-89d7-10eae67acbd3");
 public Plugin(IServerApplicationHost host,ILogManager logManager){Logger=logManager.GetLogger(Name);store=new MyOptionsStore(host,Logger);Instance=this;Logger.Info($"YouTube plugin v{typeof(Plugin).Assembly.GetName().Version} initialized; remote Movie metadata and image providers available.");}
 public IReadOnlyCollection<IPluginUIPageController> UIPageControllers=>new List<IPluginUIPageController>{new SettingsController(GetPluginInfo(),store,"Metadata",true)}.AsReadOnly();
 public Type ConfigurationType=>typeof(BasePluginConfiguration);
 public BasePluginConfiguration Configuration {get;}=new BasePluginConfiguration();
 public void UpdateConfiguration(BasePluginConfiguration configuration){}
 public void SetStartupInfo(Action<string> directoryCreateFn){}
}
