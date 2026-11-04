using System.Reflection;
using System;
using MediaBrowser.Model.GenericEdit;
using YouTubeMetadataForEmby.Configuration;
using YouTubeMetadataForEmby.Storage;
using YouTubeMetadataForEmby.UIBaseClasses.Views;
namespace YouTubeMetadataForEmby.UI;
internal sealed class SettingsView : PluginPageView
{
    private readonly MyOptionsStore store;
    public SettingsView(string id, MyOptionsStore store, string section) : base(id)
    {
        this.store = store;
        var opts = store.GetOptions();
        IEditableObject page = new MetadataPage();
        foreach (var dest in page.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var source = opts.GetType().GetProperty(dest.Name);
            if (dest.CanWrite && source?.CanRead == true && source.PropertyType == dest.PropertyType)
                dest.SetValue(page, source.GetValue(opts));
        }
        ContentData = page;
    }
    public override Task<MediaBrowser.Model.Plugins.UI.Views.IPluginUIView> OnSaveCommand(string itemId, string commandId, string data)
    {
        // Work on a detached copy: failed persistence must not mutate live options.
        var current = store.GetOptions();
        var opts = new PluginConfiguration();
        foreach (var property in typeof(PluginConfiguration).GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                property.SetValue(opts, property.GetValue(current));
        }
        foreach (var source in ContentData.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            var dest = opts.GetType().GetProperty(source.Name);
            if (source.CanRead && dest?.CanWrite == true && source.PropertyType == dest.PropertyType)
                dest.SetValue(opts, source.GetValue(ContentData));
        }
        store.SetOptions(opts);
        if (ContentData is MetadataPage page) page.SavedSuccessfully = true;
        return base.OnSaveCommand(itemId, commandId, data);
    }
}
