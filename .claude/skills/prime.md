---
name: prime
description: Load full context of the Jellyfin Dynamic Library plugin architecture, components, and data flow
user_invocable: true
---

Read the following key files to build context of the plugin. Use parallel reads where possible.

## Step 0: Git Context

Run these git commands to understand current state and recent work:
- `git log --oneline -20` — Recent commit history to see where development stands
- `git log --oneline --since="2 weeks ago"` — What's been worked on recently
- `git status` — Any uncommitted work in progress
- `git diff --stat HEAD~5` — Files changed in last 5 commits to see active areas

## Step 1: Core Architecture (read in parallel)

Read these files to understand plugin registration and configuration:
- `Jellyfin.Plugin.DynamicLibrary/Plugin.cs` — Plugin entry point
- `Jellyfin.Plugin.DynamicLibrary/ServiceRegistrator.cs` — DI registration, filter ordering
- `Jellyfin.Plugin.DynamicLibrary/Configuration/PluginConfiguration.cs` — All settings

## Step 2: ID Generation & Item Creation (read in parallel)

Read these files to understand how virtual items are created and identified:
- `Jellyfin.Plugin.DynamicLibrary/Models/DynamicUri.cs` — Deterministic GUID generation from `dynamic://{source}/{id}` URIs using MD5 hash with prefix `"jellyfin-dynamiclibrary-plugin:"`
- `Jellyfin.Plugin.DynamicLibrary/Services/SearchResultFactory.cs` — Converts TMDB/TVDB/Stremio API results to `BaseItemDto` objects with proper IDs, provider IDs, images, seasons, and episodes
- `Jellyfin.Plugin.DynamicLibrary/Services/DynamicItemCache.cs` — In-memory cache (1hr TTL) keyed by GUID for items, images, seasons, episodes, subtitles, and MediaSource mappings

## Step 3: Request Interception Filters (read in parallel)

These 8 MVC action filters intercept Jellyfin API requests. They are the core of how the plugin works — no Jellyfin provider/resolver interfaces are implemented:
- `Jellyfin.Plugin.DynamicLibrary/Filters/SearchActionFilter.cs` — Merges dynamic search results with native Jellyfin results, deduplicates by provider ID
- `Jellyfin.Plugin.DynamicLibrary/Filters/ItemLookupFilter.cs` — Intercepts item detail lookups, returns cached BaseItemDto with MediaSources, handles enrichment and persistence triggers
- `Jellyfin.Plugin.DynamicLibrary/Filters/ImageFilter.cs` — Proxies poster/backdrop/logo images from TMDB/TVDB URLs stored in cache
- `Jellyfin.Plugin.DynamicLibrary/Filters/SeasonEpisodeFilter.cs` — Returns virtual seasons/episodes for dynamic series, handles Android TV quirks
- `Jellyfin.Plugin.DynamicLibrary/Filters/PlaybackInfoFilter.cs` — Generates stream URLs based on provider (Direct/Embedarr/AIOStreams), handles anime multi-audio, fetches subtitles
- `Jellyfin.Plugin.DynamicLibrary/Filters/SubtitleFilter.cs` — Serves cached OpenSubtitles content as WebVTT or TrackEvents JSON
- `Jellyfin.Plugin.DynamicLibrary/Filters/DynamicItemEndpointsFilter.cs` — Returns empty responses for unsupported endpoints (similar items, themes, trailers)
- `Jellyfin.Plugin.DynamicLibrary/Filters/RequestLoggerFilter.cs` — Debug request logging

## Step 4: API Clients & Controllers (read in parallel)

- `Jellyfin.Plugin.DynamicLibrary/Api/DynamicLibraryController.cs` — Plugin API endpoints: GetItem, GetItemImage, Subtitles, PersistItem
- `Jellyfin.Plugin.DynamicLibrary/Api/StreamController.cs` — Stream redirect endpoints for .strm files (movie/tv/anime)
- `Jellyfin.Plugin.DynamicLibrary/Api/TmdbClient.cs` — TMDB v3 API client
- `Jellyfin.Plugin.DynamicLibrary/Api/TvdbClient.cs` — TVDB v4 API client with bearer token auth
- `Jellyfin.Plugin.DynamicLibrary/Api/AIOStreamsClient.cs` — Stremio addon stream queries
- `Jellyfin.Plugin.DynamicLibrary/Api/EmbedarrClient.cs` — Embedarr STRM generation API

## Step 5: Services & Providers (read in parallel)

- `Jellyfin.Plugin.DynamicLibrary/Services/DynamicLibraryService.cs` — Search orchestration, deduplication against existing library
- `Jellyfin.Plugin.DynamicLibrary/Services/PersistenceService.cs` — Creates .strm files with `dynamiclibrary://` URIs for persistent library items
- `Jellyfin.Plugin.DynamicLibrary/Services/SubtitleService.cs` — OpenSubtitles integration, SRT→WebVTT conversion
- `Jellyfin.Plugin.DynamicLibrary/Providers/ICatalogProvider.cs` — Catalog provider interface
- `Jellyfin.Plugin.DynamicLibrary/Providers/DirectCatalogProvider.cs` — Direct TMDB/TVDB API catalog
- `Jellyfin.Plugin.DynamicLibrary/Providers/StremioCatalogProvider.cs` — Stremio addon catalog
- `Jellyfin.Plugin.DynamicLibrary/Providers/CatalogModels.cs` — Unified catalog data models
- `Jellyfin.Plugin.DynamicLibrary/Extensions/ActionContextExtensions.cs` — Helper extensions for filter context

## After reading, provide a brief summary

After reading all files, provide the user with:
1. A one-line summary of the plugin's purpose
2. Current version number
3. Summary of recent git activity — what's been worked on, any patterns in recent commits
4. Current branch and working tree status
5. Any TODO comments, known issues, or areas of active development visible in the code
6. Confirmation you're ready to work on the codebase
