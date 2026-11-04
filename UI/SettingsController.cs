using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Plugins.UI.Views;
using YouTubeMetadataForEmby.Storage;
using YouTubeMetadataForEmby.UIBaseClasses;
namespace YouTubeMetadataForEmby.UI;
internal sealed class SettingsController : ControllerBase
{
    private readonly MyOptionsStore store;
    private readonly string section;
    public SettingsController(PluginInfo info, MyOptionsStore store, string section, bool main) : base(info.Id)
    {
        this.store = store; this.section = section;
        PageInfo = new PluginPageInfo { Name = "EmbyYT03Metadata", DisplayName = "YouTube Metadata",
            EnableInMainMenu = true, MenuIcon = "settings", IsMainConfigPage = main };
    }
    public override PluginPageInfo PageInfo { get; }
    public override Task<IPluginUIView> CreateDefaultPageView() => Task.FromResult<IPluginUIView>(new SettingsView(PluginId, store, section));
}
