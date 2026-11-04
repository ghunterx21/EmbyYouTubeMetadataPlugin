using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using YouTubeMetadataForEmby.Core;
using YouTubeMetadataForEmby.Configuration;

namespace YouTubeMetadataForEmby;

/// <summary>
/// Manually scheduled, fail-closed YouTube-only refresh. Never issues a library-wide refresh.
/// Reflection is limited to the SDK-dependent refresh overload and library provider settings.
/// </summary>
public sealed class RefreshYouTubeMetadataTask : IScheduledTask
{
    private readonly ILibraryManager library;
    private readonly IProviderManager providers;
    private readonly ILogger log;
    private readonly IFileSystem fileSystem;
    // An ambiguous host persistence failure must not be mistaken for an ordinary
    // no-change result on later runs in this server process. Restart to reconcile.
    private static readonly HashSet<string> uncertainItems = new(StringComparer.Ordinal);
    private static readonly object uncertainItemsLock = new();

    public RefreshYouTubeMetadataTask(ILibraryManager library, IProviderManager providers, ILogManager logs, IFileSystem fileSystem)
    {
        this.library = library;
        this.providers = providers;
        this.fileSystem = fileSystem;
        log = logs.GetLogger("YouTubeMetadataScheduledTask");
    }

    public string Name => "Refresh YouTube Metadata";
    public string Key => "YouTubeMetadataRefresh";
    public string Description => "Refreshes only YouTube-ID videos in libraries with the YouTube metadata provider enabled.";
    public string Category => "YouTube Metadata";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Enumerable.Empty<TaskTriggerInfo>();

    public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
    {
        if (Plugin.Instance?.Options.Enabled != true)
        {
            log.Info("YouTube scheduled refresh: provider disabled; nothing to do.");
            progress.Report(100);
            return;
        }

        var items = library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { "Movie", "Episode" },
            Recursive = true
        });
        int refreshed = 0, skipped = 0, failed = 0, eligible = 0, unchanged = 0;
        log.Info("YouTube scheduled refresh: scanning {0} movie/episode records.", items.Length);
        for (int i = 0; i < items.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            try
            {
                var path = item.Path;
                var id = YouTubeIds.VideoFromPath(path);
                if (string.IsNullOrWhiteSpace(path) || !YouTubeIds.IsVideoId(id))
                {
                    skipped++;
                    continue;
                }
                var itemKey = ItemKey(item);
                lock (uncertainItemsLock)
                {
                    if (uncertainItems.Contains(itemKey))
                        throw new InvalidOperationException("Prior metadata persistence outcome is UNKNOWN for this item; restart Emby and verify its metadata before retrying: " + path);
                }
                var options = library.GetLibraryOptions(item);
                if (!HasEnabledYouTubeProvider(options, item is MediaBrowser.Controller.Entities.TV.Episode ? "Episode" : "Movie"))
                {
                    skipped++;
                    continue;
                }
                // Fully locked items cannot be updated. Partially locked items use
                // a narrow library update instead of Emby's destructive full refresh.
                if (item.IsLocked)
                {
                    skipped++;
                    log.Warn("YouTube scheduled refresh LOCK-SAFE SKIP fully locked {0}: {1}", path, DescribeItem(item));
                    continue;
                }
                // One lock-aware library persistence path for locked and unlocked items.
                // Do not run RefreshFullItem: Emby 4.10 can leave the unlocked title unchanged.
                eligible++;
                if (await RefreshUnlockedFieldsOnly(item, id!, cancellationToken).ConfigureAwait(false)) refreshed++;
                else unchanged++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                log.ErrorException("YouTube scheduled refresh failed for {0}", ex, item.Path ?? item.Name);
            }
            finally
            {
                progress.Report(100.0 * (i + 1) / Math.Max(items.Length, 1));
            }
        }
        progress.Report(100);
        log.Info("YouTube scheduled refresh finished: eligible={0}, refreshed={1}, skipped={2}, failed={3}, unchanged={4} (readback is not durable verification).", eligible, refreshed, skipped, failed, unchanged);
    }

    // Partially locked items must never pass through RefreshFullItem. Persist only
    // the selected unlocked Emby fields using Emby's library update API; fail closed if
    // this server exposes an unfamiliar signature.
    private async Task<bool> RefreshUnlockedFieldsOnly(BaseItem item, string id, CancellationToken ct)
    {
        var path = item.Path ?? item.Name;
        var originalLocks = item.LockedFields?.ToArray() ?? Array.Empty<MetadataFields>();
        var originalName = item.Name;
        var originalOverview = item.Overview;
        var originalDate = item.PremiereDate;
        var originalYear = item.ProductionYear;
        var originalTags = item.Tags?.ToArray() ?? Array.Empty<string>();
        var originalGenres = item.Genres?.ToArray() ?? Array.Empty<string>();
        var originalStudios = item.Studios?.ToArray() ?? Array.Empty<string>();
        bool nameLocked = originalLocks.Contains(MetadataFields.Name);
        bool overviewLocked = originalLocks.Contains(MetadataFields.Overview);
        bool tagsLocked = originalLocks.Contains(MetadataFields.Tags);
        bool genresLocked = originalLocks.Contains(MetadataFields.Genres);
        bool studiosLocked = originalLocks.Contains(MetadataFields.Studios);
        // Emby's MetadataFields enum does not expose PremiereDate or ProductionYear
        // lock identifiers. Apply date/year ONLY if the entire item is unlocked AND
        // there are no individually locked fields. This is intentionally more
        // conservative than per-field locking; never infer date permission from
        // an unsupported enum member.
        bool datesWritable = !item.IsLocked && originalLocks.Length == 0;
        if (!datesWritable)
            log.Info("YouTube scheduled refresh DATE/YEAR SKIPPED {0}: item or individual metadata fields locked; lock-safe date write unavailable.", path);
        var cfg = Plugin.Instance!.Options;
        var (expected, _, _) = await MetadataSourceResolver.ResolveAsync(item.Path!, id, false, cfg, ct).ConfigureAwait(false);
        if (expected == null) throw new InvalidOperationException("No metadata available for partially locked item: " + path);
        ct.ThrowIfCancellationRequested();
        if (cfg.AllowSidecarWrites)
            await SidecarWriter.CreateMissingAsync(item.Path!, expected, ct).ConfigureAwait(false);
        // Re-read immediately before modifying anything, to detect user edits/locks.
        var current = ReadBackItem(item) ?? throw new InvalidOperationException("Cannot re-read partially locked item: " + path);
        if (current.IsLocked || !(SameLocks(current.LockedFields, originalLocks)) ||
            current.Name != originalName || current.Overview != originalOverview ||
            current.PremiereDate != originalDate || current.ProductionYear != originalYear ||
            !Same(current.Tags, originalTags) || !Same(current.Genres, originalGenres) || !Same(current.Studios, originalStudios))
            throw new InvalidOperationException("Metadata changed concurrently; refusing partial update: " + path);
        var newName = !nameLocked && !string.IsNullOrWhiteSpace(expected.Title) ? expected.Title.Trim() : originalName;
        var newOverview = !overviewLocked && !string.IsNullOrWhiteSpace(expected.Description) ? expected.Description.Trim() : originalOverview;
        // Follow the existing provider mapping: upload_date first, then release_date,
        // then timestamp. Never fabricate dates from missing/invalid JSON here.
        var selectedDate = expected.Date;
        var newDate = datesWritable && selectedDate.HasValue ? selectedDate.Value : originalDate;
        var newYear = datesWritable && selectedDate.HasValue ? selectedDate.Value.Year : originalYear;
        if (datesWritable && selectedDate.HasValue)
            log.Info("YouTube scheduled refresh DATE/YEAR CANDIDATE {0}: source={1:yyyy-MM-dd}, PremiereDate={2}, ProductionYear={3}; previous PremiereDate={4}, ProductionYear={5}.",
                path, selectedDate.Value, newDate, newYear, originalDate, originalYear);
        var usableTags = expected.Tags == null ? Array.Empty<string>() : Normalize(expected.Tags);
        var usableGenres = expected.Categories == null ? Array.Empty<string>() : Normalize(expected.Categories);
        // Empty or whitespace-only source collections mean no usable metadata, not
        // an instruction to erase existing Emby metadata.
        var newTags = !tagsLocked && usableTags.Length > 0 ? usableTags : originalTags;
        var newGenres = !genresLocked && usableGenres.Length > 0 ? usableGenres : originalGenres;
        var channel = !string.IsNullOrWhiteSpace(expected.Channel) ? expected.Channel : expected.Uploader;
        var newStudios = !studiosLocked && !string.IsNullOrWhiteSpace(channel) ? Normalize(new[] { channel! }) : originalStudios;
        if (newName == originalName && newOverview == originalOverview &&
            newDate == originalDate && newYear == originalYear &&
            Same(newTags, originalTags) && Same(newGenres, originalGenres) && Same(newStudios, originalStudios))
        {
            log.Info("YouTube scheduled refresh PARTIAL NO-CHANGE {0}; locks preserved: {1}", path, string.Join(",", originalLocks));
            return false;
        }
        // Emby 4.10 documents UpdateItems(List<BaseItem>, BaseItem, ItemUpdateType,
        // bool setDateLastSaved, bool saveMetadata, MetadataRefreshOptions, CancellationToken).
        // Explicitly disable sidecar metadata saving: update the library record only.
        var update = library.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name == "UpdateItems")
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 7 && p[0].ParameterType == typeof(List<BaseItem>) &&
                    p[1].ParameterType.IsAssignableFrom(current.GetType()) &&
                    p[2].ParameterType.IsEnum && p[2].ParameterType.Name == "ItemUpdateType" &&
                    p[3].ParameterType == typeof(bool) && p[4].ParameterType == typeof(bool) &&
                    p[5].ParameterType.Name == "MetadataRefreshOptions" &&
                    p[6].ParameterType == typeof(CancellationToken);
            });
        if (update == null)
            throw new NotSupportedException("Safe UpdateItems(..., saveMetadata=false, ...) API unavailable; partially locked item untouched: " + path);
        // The supported Emby 4.10 overload returns void. Reject unknown contracts before mutation.
        if (update.ReturnType != typeof(void))
            throw new NotSupportedException("Unexpected UpdateItems return signature; partially locked item untouched: " + path);
        var updateType = update.GetParameters()[2].ParameterType;
        if (!Enum.GetNames(updateType).Contains("MetadataEdit"))
            throw new NotSupportedException("ItemUpdateType.MetadataEdit unavailable; partially locked item untouched: " + path);
        // Parent is required by the documented overload. Resolve it without guessing an ID.
        var getParent = current.GetType().GetMethod("GetParent", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
        var parent = getParent?.Invoke(current, null) as BaseItem;
        if (parent == null)
            throw new NotSupportedException("Unable to resolve item's parent safely; partially locked item untouched: " + path);
        var options = ConstructRefreshOptions(update.GetParameters()[5].ParameterType);
        ct.ThrowIfCancellationRequested();
        // Recheck at the last practical moment. This is not an atomic CAS against Emby UI edits.
        var last = ReadBackItem(item) ?? throw new InvalidOperationException("Cannot re-read item before partial commit: " + path);
        if (last.IsLocked || !(SameLocks(last.LockedFields, originalLocks)) ||
            last.Name != originalName || last.Overview != originalOverview ||
            last.PremiereDate != originalDate || last.ProductionYear != originalYear ||
            !Same(last.Tags, originalTags) || !Same(last.Genres, originalGenres) || !Same(last.Studios, originalStudios))
            throw new InvalidOperationException("Concurrent metadata/lock edit detected before commit: " + path);
        // Do not assign LockedFields: preserving the server's current lock list is essential.
        // This object may be shared by Emby's cache. Never blindly restore it after an
        // ambiguous failure: the server may have committed, or another user may have edited it.
        if (newName != originalName) last.Name = newName;
        if (newOverview != originalOverview) last.Overview = newOverview;
        if (newDate != originalDate) last.PremiereDate = newDate;
        if (newYear != originalYear) last.ProductionYear = newYear;
        if (!Same(newTags, originalTags)) last.Tags = newTags;
        if (!Same(newGenres, originalGenres)) last.Genres = newGenres;
        if (!Same(newStudios, originalStudios)) last.Studios = newStudios;
        try
        {
            update.Invoke(library, new object?[] {
                new List<BaseItem> { last }, parent, Enum.Parse(updateType, "MetadataEdit"),
                true, false, options, ct
            });
        }
        catch (TargetInvocationException ex)
        {
            MarkUncertain(item, path);
            throw ex.InnerException ?? ex;
        }
        catch
        {
            MarkUncertain(item, path);
            throw;
        }
        var saved = ReadBackItem(item);
        if (saved == null)
        {
            MarkUncertain(item, path);
            throw new InvalidOperationException("Cannot verify partial update; outcome UNKNOWN: " + path);
        }
        // Report exactly which fields diverge. Emby may reorder or normalize its
        // collection values; their order is not metadata meaning.
        var mismatches = new List<string>();
        if (saved.IsLocked) mismatches.Add("IsLocked became true");
        if (!(SameLocks(saved.LockedFields, originalLocks)))
            mismatches.Add("LockedFields changed");
        if (saved.Name != newName) mismatches.Add("Name expected=" + Short(newName) + " actual=" + Short(saved.Name));
        if (saved.Overview != newOverview) mismatches.Add("Overview expected=" + Short(newOverview) + " actual=" + Short(saved.Overview));
        if (saved.PremiereDate != newDate) mismatches.Add("PremiereDate expected=" + newDate + " actual=" + saved.PremiereDate);
        if (saved.ProductionYear != newYear) mismatches.Add("ProductionYear expected=" + newYear + " actual=" + saved.ProductionYear);
        if (!Same(saved.Tags, newTags)) mismatches.Add("Tags expected=" + ShortList(newTags) + " actual=" + ShortList(saved.Tags));
        if (!Same(saved.Genres, newGenres)) mismatches.Add("Genres expected=" + ShortList(newGenres) + " actual=" + ShortList(saved.Genres));
        if (!Same(saved.Studios, newStudios)) mismatches.Add("Studios expected=" + ShortList(newStudios) + " actual=" + ShortList(saved.Studios));
        if (mismatches.Count != 0)
        {
            MarkUncertain(item, path);
            log.Warn("YouTube scheduled refresh FIELD VERIFICATION MISMATCH {0}: {1}; update outcome not proven durable.", path, string.Join(" | ", mismatches));
            throw new InvalidOperationException("Partial update failed field verification: " + string.Join("; ", mismatches.Select(x => x.Split(' ')[0])) + "; " + path);
        }
        log.Info("YouTube scheduled refresh PARTIAL LIBRARY-READBACK (disk persistence unverified) {0}: locked title={1}, locked overview={2}; {3}",
            path, nameLocked, overviewLocked, DescribeItem(saved));
        return true;
    }

    private static bool SameLocks(IEnumerable<MetadataFields>? a, IEnumerable<MetadataFields>? b) =>
        new HashSet<MetadataFields>(a ?? Enumerable.Empty<MetadataFields>())
            .SetEquals(b ?? Enumerable.Empty<MetadataFields>());

    private static string ItemKey(BaseItem item)
    {
        foreach (var propertyName in new[] { "InternalId", "Id" })
        {
            var value = item.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
            if (value != null) return propertyName + ":" + value;
        }
        // Fail closed if the host SDK stops exposing stable database item identity.
        throw new NotSupportedException("Cannot identify Emby library record safely: " + (item.Path ?? item.Name));
    }

    private void MarkUncertain(BaseItem item, string path)
    {
        lock (uncertainItemsLock) uncertainItems.Add(ItemKey(item));
        log.Warn("YouTube partial update outcome UNKNOWN for {0}; subsequent scheduled runs will fail closed for this item until Emby restarts and metadata is verified.", path);
    }

    private static string[] Normalize(IEnumerable<string> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static bool Same(IEnumerable<string>? a, IEnumerable<string>? b) =>
        Normalize(a ?? Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(Normalize(b ?? Enumerable.Empty<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
    private static string Short(string? value) => value == null ? "<null>" : value.Length > 120 ? value.Substring(0, 120) + "..." : value;
    private static string ShortList(IEnumerable<string>? values) =>
        "[" + string.Join(",", (values ?? Enumerable.Empty<string>()).Take(15).Select(Short)) + "]";

    // Diagnostics intentionally do not unlock fields or alter user-edited values.
    private static string DescribeItem(BaseItem item)
    {
        string Get(string name)
        {
            try
            {
                var value = item.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
                if (value is string str) return str.Length > 160 ? str.Substring(0, 160) + "..." : str;
                if (value is IEnumerable values && value is not string)
                    return string.Join(",", values.Cast<object>().Select(x => x?.ToString()).Take(12));
                return value?.ToString() ?? "<null>";
            }
            catch (Exception ex) { return "<unavailable:" + ex.GetType().Name + ">"; }
        }
        return "Name=" + Get("Name") + "; Overview=" + Get("Overview") +
            "; PremiereDate=" + Get("PremiereDate") + "; ProductionYear=" + Get("ProductionYear") +
            "; LockedFields=" + Get("LockedFields") + "; IsLocked=" + Get("IsLocked") +
            "; ImageInfos=" + Get("ImageInfos");
    }

    private BaseItem? ReadBackItem(BaseItem item)
    {
        try
        {
            // SDKs differ in whether their item key is exposed as InternalId or Id.
            // Match the actual GetItemById argument type instead of guessing a conversion.
            var methods = library.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "GetItemById" && m.GetParameters().Length == 1);
            foreach (var method in methods)
            {
                var parameterType = method.GetParameters()[0].ParameterType;
                foreach (var keyName in new[] { "InternalId", "Id" })
                {
                    var key = item.GetType().GetProperty(keyName, BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
                    if (key == null || !parameterType.IsInstanceOfType(key)) continue;
                    if (method.Invoke(library, new[] { key }) is BaseItem current) return current;
                }
            }
        }
        catch (Exception ex)
        {
            log.Warn("YouTube scheduled refresh: library re-read failed for {0}: {1}", item.Path ?? item.Name, ex.Message);
        }
        return null;
    }

    private static string DescribeOptions(object options)
    {
        var names = new[] { "MetadataRefreshMode", "ImageRefreshMode", "ReplaceAllMetadata", "ReplaceExistingImages", "ForceSave", "EnableRemoteContentProbe", "IsAutomated", "DirectoryService" };
        return string.Join("; ", names.Select(name =>
        {
            try
            {
                var property = options.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (property == null) return name + "=<not exposed>";
                // Avoid logging service object details.
                if (name == "DirectoryService") return name + "=<service>";
                return name + "=" + (property.GetValue(options)?.ToString() ?? "<null>");
            }
            catch (Exception ex) { return name + "=<unavailable:" + ex.GetType().Name + ">"; }
        }));
    }

    private static bool HasEnabledYouTubeProvider(object? libraryOptions, string itemType)
    {
        if (libraryOptions == null) return false;
        // Emby stores enabled metadata fetchers per media type in TypeOptions.
        var typeOptions = libraryOptions.GetType().GetProperty("TypeOptions")?.GetValue(libraryOptions) as IEnumerable;
        if (typeOptions == null) return false;
        foreach (var typeOption in typeOptions)
        {
            if (typeOption == null) continue;
            var declaredType = typeOption.GetType().GetProperty("Type")?.GetValue(typeOption)?.ToString();
            if (!string.Equals(declaredType, itemType, StringComparison.OrdinalIgnoreCase)) continue;
            var fetchers = typeOption.GetType().GetProperty("MetadataFetchers")?.GetValue(typeOption) as IEnumerable;
            if (fetchers == null) continue;
            foreach (var fetcher in fetchers)
                if (string.Equals(fetcher?.ToString(), "YouTube Metadata", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false; // fail closed if the library configuration cannot be verified
    }

    private object ConstructRefreshOptions(Type type)
    {
        // Emby 4.10.2.0 exposes MetadataRefreshOptions(IFileSystem).
        // Use the server-provided IFileSystem, not a guessed or null dependency.
        var constructor = type.GetConstructors()
            .FirstOrDefault(c =>
            {
                var args = c.GetParameters();
                return args.Length == 1 &&
                    args[0].ParameterType.IsInstanceOfType(fileSystem);
            });
        if (constructor == null)
            throw new NotSupportedException("No MetadataRefreshOptions constructor accepts Emby's IFileSystem service. Constructors: " +
                string.Join("; ", type.GetConstructors().Select(c => c.ToString())));
        return constructor.Invoke(new object[] { fileSystem });
    }

    private static void SetRequiredBooleanOption(Type type, object options, string name, bool value)
    {
        var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || !property.CanWrite || property.PropertyType != typeof(bool))
            throw new NotSupportedException("Emby refresh option " + name + " is unavailable or not writable; refusing an incomplete replacement refresh.");
        property.SetValue(options, value);
        if (!Equals(property.GetValue(options), value))
            throw new InvalidOperationException("Emby refresh option " + name + " could not be verified.");
    }

    private async Task RefreshItem(BaseItem item, CancellationToken cancellationToken)
    {
        // Locate Emby's per-item refresh overload without assuming a specific SDK version.
        var method = providers.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "RefreshFullItem" && m.GetParameters().Length == 3 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(item.GetType()) &&
                m.GetParameters()[2].ParameterType == typeof(CancellationToken));
        if (method == null) throw new NotSupportedException("Emby per-item RefreshFullItem API was not found.");
        var optionType = method.GetParameters()[1].ParameterType;
        object? options;
        try { options = ConstructRefreshOptions(optionType); }
        catch (Exception ex) { throw new NotSupportedException("Emby refresh options cannot be constructed safely. Constructors: " + string.Join("; ", optionType.GetConstructors().Select(c => c.ToString())), ex); }
        if (options == null) throw new NotSupportedException("Emby refresh options unavailable.");
        // Refresh without forcing metadata replacement. Emby 4.10.2.0 cleared
        // LockedFields during a ReplaceAllMetadata=True full refresh.
        // Deliberately do NOT set any video preview / chapter thumbnail refresh options:
        // those remain governed by Emby library settings and manual refresh actions.
        foreach (var propertyName in new[] { "MetadataRefreshMode", "ImageRefreshMode" })
        {
            var property = optionType.GetProperty(propertyName);
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
                throw new NotSupportedException("Emby refresh option " + propertyName + " is unavailable.");
            var full = Enum.GetNames(property.PropertyType).FirstOrDefault(x => x == "FullRefresh");
            if (full == null) throw new NotSupportedException("Emby FullRefresh mode unavailable.");
            property.SetValue(options, Enum.Parse(property.PropertyType, full));
        }
        // Explicitly prohibit replacing all metadata. Fail closed if unsupported.
        SetRequiredBooleanOption(optionType, options, "ReplaceAllMetadata", false);
        SetRequiredBooleanOption(optionType, options, "ForceSave", true);
        log.Info("YouTube scheduled refresh OPTIONS {0}: {1}; video preview settings unchanged.", item.Path ?? item.Name, DescribeOptions(options));
        object? result;
        try { result = method.Invoke(providers, new[] { item, options, (object)cancellationToken }); }
        catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        if (result is Task task) await task.ConfigureAwait(false);
        else throw new NotSupportedException("Emby refresh method did not return a Task.");
    }
}
