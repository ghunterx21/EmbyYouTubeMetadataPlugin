# Emby YouTube Metadata Plugin

A metadata and artwork plugin for YouTube videos in Emby libraries, including videos downloaded using yt-dlp or Pinchflat.

**Version:** 1.0.0  
**Target:** Emby Server 4.10.2.0 · .NET 8

## Features

- Reads local YouTube `.info.json` metadata and supports online YouTube metadata lookup.
- Provides configurable metadata source preferences: YouTube Only, YouTube First (Local Fallback), Local First (YouTube Fallback), and Local Only.
- Supports local and online artwork providers.
- Includes a **Refresh YouTube Metadata** scheduled task to update titles, descriptions, tags, genres, studios, release dates, and production years when appropriate.
- Respects full-item and individual metadata locks. Release date and production year are updated conservatively only when the item has no metadata locks.
- Preserves existing Tags and Genres when the selected source has no usable replacement values.
- Can optionally create missing metadata sidecars without replacing existing downloader-owned files.

## Installation

Requires an Emby Server 4.10.2.0 installation and the .NET 8 SDK to build from source.

```sh
dotnet restore
dotnet build -c Release
```

Copy `bin/Release/net8.0/EmbyYouTubeMetadataPlugin.dll` to the Emby plugins directory, then restart Emby Server. Back up your Emby configuration before installing or upgrading.

## Configuration

Open the plugin settings in Emby to enable or disable the plugin, choose a metadata source preference, configure local/online metadata and artwork options, and optionally allow creation of missing sidecar files.

Run **Refresh YouTube Metadata** from Emby's scheduled tasks to update supported library metadata.

## Metadata and file safety

The scheduled task updates only supported, eligible Emby records. Individual metadata field locks are respected; fully locked items are skipped. Because Emby does not expose a suitable individual lock for release date and production year in the targeted SDK, those values are changed only on completely unlocked items.

The scheduled task does not overwrite existing `.info.json`, `.nfo`, artwork, or video files. Optional sidecar creation is limited to missing files. The `UpdateExistingJson` configuration option does not enable rewriting existing JSON during the scheduled task.

Metadata changes are made through Emby's library update API rather than a full metadata refresh. If the outcome of a database update is uncertain, the affected item is blocked from further scheduled updates until the Emby process restarts. Verify its metadata before retrying.

Avoid manually editing the same item's metadata while the scheduled task is running: Emby's UI edits and plugin updates are not one atomic operation.

## Compatibility

- Emby Server: 4.10.2.0
- Target framework: .NET 8
- Emby SDK package: `MediaBrowser.Server.Core` 4.9.1.90

The plugin retains its existing identifier so installations can be upgraded without creating a separate plugin entry.
