using System;
using System.IO;
using Emby.Web.GenericEdit;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace YouTubeMetadataForEmby.UIBaseClasses.Store
{
    public class SimpleFileStore<TOptionType> : SimpleContentStore<TOptionType>
        where TOptionType : EditableOptionsBase, new()
    {
        private readonly ILogger logger;
        private readonly string pluginFullName;
        private readonly IJsonSerializer jsonSerializer;
        private readonly IFileSystem fileSystem;
        private readonly string pluginconfigPath;

        private TOptionType? options;

        public SimpleFileStore(IApplicationHost applicationHost, ILogger logger, string pluginFullName)
        {
            this.logger = logger;
            this.pluginFullName = pluginFullName;
            this.jsonSerializer = applicationHost.Resolve<IJsonSerializer>();
            this.fileSystem = applicationHost.Resolve<IFileSystem>();

            var applicationPaths = applicationHost.Resolve<IApplicationPaths>();
            this.pluginconfigPath = applicationPaths.PluginConfigurationsPath;

            if (!this.fileSystem.DirectoryExists(this.pluginconfigPath))
            {
                this.fileSystem.CreateDirectory(this.pluginconfigPath);
            }

            this.OptionsFileName = string.Format("{0}.json", pluginFullName);
        }

        public event EventHandler<FileSavingEventArgs>? FileSaving;

        public event EventHandler<FileSavedEventArgs>? FileSaved;

        public virtual string OptionsFileName { get; }

        public string OptionsFilePath => Path.Combine(this.pluginconfigPath, this.OptionsFileName);

        public override TOptionType GetOptions()
        {
            lock (this.LockObj)
            {
                if (this.options == null)
                {
                    return this.ReloadOptions();
                }

                return this.options;
            }
        }

        public TOptionType ReloadOptions()
        {
            lock (this.LockObj)
            {
                var tempOptions = this.options ?? new TOptionType();

                try
                {
                    if (!this.fileSystem.FileExists(this.OptionsFilePath))
                    {
                        this.options = tempOptions;
                        return tempOptions;
                    }

                    using (var stream = this.fileSystem.OpenRead(this.OptionsFilePath))
                    {
                        var deserialized = tempOptions.DeserializeFromJsonStream(stream, this.jsonSerializer);

                        this.options = deserialized as TOptionType ?? tempOptions;
                    }
                }
                catch (Exception ex)
                {
                    this.logger.ErrorException("Error loading plugin options for {0} from {1}", ex, this.pluginFullName, this.OptionsFilePath);
                    this.options = tempOptions;
                }

                return this.options ?? new TOptionType();
            }
        }

        public override void SetOptions(TOptionType newOptions)
        {
            if (newOptions == null)
            {
                throw new ArgumentNullException(nameof(newOptions));
            }

            var savingArgs = new FileSavingEventArgs(newOptions);
            this.FileSaving?.Invoke(this, savingArgs);

            if (savingArgs.Cancel)
            {
                return;
            }

            lock (this.LockObj)
            {
                // Serialize completely before replacing the existing configuration.
                // Keep the publication and in-memory commit in the same lock.
                var tempPath = this.OptionsFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        this.jsonSerializer.SerializeToStream(newOptions, stream, new JsonSerializerOptions { Indent = true });
                        stream.Flush(flushToDisk: true);
                    }

                    // Rename replaces the file object, not just its contents. Carry over
                    // the prior Unix mode rather than publishing the temp file's umask mode.
                    if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                        && File.Exists(this.OptionsFilePath))
                    {
                        File.SetUnixFileMode(tempPath, File.GetUnixFileMode(this.OptionsFilePath));
                    }

                    File.Move(tempPath, this.OptionsFilePath, overwrite: true);
                    this.options = newOptions;
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
            }

            var savedArgs = new FileSavedEventArgs(newOptions);
            this.FileSaved?.Invoke(this, savedArgs);
        }
    }
}

