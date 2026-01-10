using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts playback info requests for dynamic items
/// and returns media source info with stream URL from Embedarr.
/// </summary>
public class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly IEmbedarrClient _embedarrClient;
    private readonly IAIOStreamsClient _aiostreamsClient;
    private readonly IHlsProbeService _hlsProbeService;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly ITmdbClient _tmdbClient;
    private readonly ITvdbClient _tvdbClient;
    private readonly SubtitleService _subtitleService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaybackInfoFilter> _logger;

    private static readonly HashSet<string> PlaybackInfoActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetPlaybackInfo",
        "GetPostedPlaybackInfo"
    };

    public PlaybackInfoFilter(
        DynamicItemCache itemCache,
        IEmbedarrClient embedarrClient,
        IAIOStreamsClient aiostreamsClient,
        IHlsProbeService hlsProbeService,
        SearchResultFactory searchResultFactory,
        ITmdbClient tmdbClient,
        ITvdbClient tvdbClient,
        SubtitleService subtitleService,
        ILibraryManager libraryManager,
        ILogger<PlaybackInfoFilter> logger)
    {
        _itemCache = itemCache;
        _embedarrClient = embedarrClient;
        _aiostreamsClient = aiostreamsClient;
        _hlsProbeService = hlsProbeService;
        _searchResultFactory = searchResultFactory;
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
        _subtitleService = subtitleService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // Run early to intercept before Jellyfin tries to look up the item
    public int Order => 0;

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Check if this is a playback info action on MediaInfo controller
        if (controllerName != "MediaInfo" || actionName == null || !PlaybackInfoActions.Contains(actionName))
        {
            await next();
            return;
        }

        // Get item ID from route
        if (!context.ActionArguments.TryGetValue("itemId", out var itemIdObj) || itemIdObj is not Guid itemId)
        {
            await next();
            return;
        }

        _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Processing PlaybackInfo for item {ItemId}", itemId);

        // Extract selected mediaSourceId early - we might need it for AIOStreams even without cached item
        var selectedMediaSourceId = GetSelectedMediaSourceId(context);

        // For AIOStreams mode: if we have a selected MediaSource ID, try to resolve it directly
        // This handles the case where the item isn't in cache but we have the stream URL mapping
        if (Config.StreamProvider == StreamProvider.AIOStreams && !string.IsNullOrEmpty(selectedMediaSourceId))
        {
            var aioStreamUrl = _itemCache.GetAIOStreamsStreamUrl(selectedMediaSourceId);
            if (!string.IsNullOrEmpty(aioStreamUrl))
            {
                _logger.LogInformation("[DynamicLibrary] PlaybackInfoFilter: Found AIOStreams mapping for {MediaSourceId}, returning stream URL",
                    selectedMediaSourceId);

                var response = await BuildAIOStreamsDirectResponseAsync(itemId, aioStreamUrl, selectedMediaSourceId, context.HttpContext.RequestAborted);
                context.Result = new OkObjectResult(response);
                return;
            }
        }

        // Check if this is a dynamic item in cache
        var cachedItem = _itemCache.GetItem(itemId);

        // If not in cache, check if it's a persisted DynamicLibrary item
        if (cachedItem == null)
        {
            var libraryItem = _libraryManager.GetItemById(itemId);
            _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Item not in cache. LibraryItem={Found}, Path={Path}",
                libraryItem != null, libraryItem?.Path ?? "null");

            if (libraryItem != null)
            {
                // Check if it's a .strm file - need to read contents to get the actual URL
                string? dynamicLibraryUrl = null;

                if (libraryItem.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Read .strm file contents to get the actual URL
                    try
                    {
                        if (File.Exists(libraryItem.Path))
                        {
                            var strmContents = File.ReadAllText(libraryItem.Path).Trim();
                            _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Read .strm contents: {Contents}", strmContents);
                            if (IsDynamicLibraryPath(strmContents))
                            {
                                dynamicLibraryUrl = strmContents;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: .strm file does not exist: {Path}", libraryItem.Path);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[DynamicLibrary] Failed to read .strm file: {Path}", libraryItem.Path);
                    }
                }
                else if (IsDynamicLibraryPath(libraryItem.Path))
                {
                    // Direct dynamiclibrary:// path (shouldn't happen but handle it)
                    dynamicLibraryUrl = libraryItem.Path;
                }

                if (!string.IsNullOrEmpty(dynamicLibraryUrl))
                {
                    // Check if we should skip unreleased content
                    if (!Config.ShowUnreleasedStreams)
                    {
                        var premiereDate = libraryItem.PremiereDate;
                        if (premiereDate == null || premiereDate > DateTime.UtcNow)
                        {
                            _logger.LogDebug("[DynamicLibrary] Skipping stream for unreleased persisted content: {Name} (Premiere: {Date})",
                                libraryItem.Name, premiereDate?.ToString("yyyy-MM-dd") ?? "unknown");
                            await next();
                            return;
                        }
                    }

                    _logger.LogWarning("[DynamicLibrary] PlaybackInfo: Found persisted item {Name} with dynamicLibraryUrl {Url}",
                        libraryItem.Name, dynamicLibraryUrl);

                    // Handle AIOStreams mode for persisted items
                    if (Config.StreamProvider == StreamProvider.AIOStreams)
                    {
                        var aioResponse = await HandleAIOStreamsPlaybackForPersistedItemAsync(
                            libraryItem, selectedMediaSourceId, context.HttpContext.RequestAborted);
                        if (aioResponse != null)
                        {
                            _logger.LogInformation("[DynamicLibrary] PlaybackInfo: Returning AIOStreams response for persisted item {Name}",
                                libraryItem.Name);
                            context.Result = new OkObjectResult(aioResponse);
                            return;
                        }

                        _logger.LogWarning("[DynamicLibrary] AIOStreams returned no streams for persisted item {Name}", libraryItem.Name);
                        await next();
                        return;
                    }

                    // Fetch subtitles for persisted items (Direct mode)
                    List<CachedSubtitle>? subtitles = null;
                    if (_subtitleService.IsEnabled)
                    {
                        subtitles = await FetchSubtitlesForPersistedItemAsync(libraryItem, context.HttpContext.RequestAborted);
                        _logger.LogDebug("[DynamicLibrary] Fetched {Count} subtitles for persisted item {Name}",
                            subtitles?.Count ?? 0, libraryItem.Name);
                    }

                    // Check if this is anime with multiple audio versions enabled (Direct mode)
                    var persistedConfig = Config;
                    _logger.LogWarning("[DynamicLibrary] Anime check: URL={Url}, IsAnimeUrl={IsAnime}, EnableAudioVersions={Enable}, AudioTracks={Tracks}",
                        dynamicLibraryUrl,
                        IsAnimeUrl(dynamicLibraryUrl),
                        persistedConfig.EnableAnimeAudioVersions,
                        persistedConfig.AnimeAudioTracks ?? "null");

                    if (IsAnimeUrl(dynamicLibraryUrl) && persistedConfig.EnableAnimeAudioVersions && !string.IsNullOrEmpty(persistedConfig.AnimeAudioTracks))
                    {
                        var response = await BuildAnimePlaybackInfoResponseAsync(libraryItem, dynamicLibraryUrl, subtitles, context.HttpContext.RequestAborted);
                        if (response != null)
                        {
                            // Check if a specific MediaSource was requested and filter to that one
                            var requestedSourceId = GetSelectedMediaSourceId(context);
                            var itemIdStr = libraryItem.Id.ToString("N");

                            // If client passed episode ID (like Android TV), check for stored selection
                            if (requestedSourceId == itemIdStr)
                            {
                                var storedSelection = _itemCache.GetSelectedMediaSource(libraryItem.Id);
                                if (!string.IsNullOrEmpty(storedSelection))
                                {
                                    _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Client passed episode ID, using stored selection: {StoredId}",
                                        storedSelection);
                                    requestedSourceId = storedSelection;
                                }
                            }

                            _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: requestedSourceId={Requested}, itemIdStr={ItemId}",
                                requestedSourceId ?? "null", itemIdStr);
                            if (!string.IsNullOrEmpty(requestedSourceId) && requestedSourceId != itemIdStr && response.MediaSources?.Count > 1)
                            {
                                var selected = response.MediaSources.FirstOrDefault(s => s.Id == requestedSourceId);
                                if (selected != null)
                                {
                                    response.MediaSources = new[] { selected };
                                    _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Filtered to selected MediaSource {Name} (ID: {Id})",
                                        selected.Name, requestedSourceId);
                                }
                                else
                                {
                                    _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Selected MediaSource {Id} not found, returning all sources",
                                        requestedSourceId);
                                }
                            }

                            _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Returning anime response with {Count} MediaSources",
                                response.MediaSources?.Count ?? 0);
                            context.Result = new OkObjectResult(response);
                            return;
                        }
                    }

                    // Standard single-URL handling
                    var persistedStreamUrl = BuildStreamUrlFromPath(dynamicLibraryUrl);
                    if (!string.IsNullOrEmpty(persistedStreamUrl))
                    {
                        var response = await BuildPlaybackInfoResponseFromPathAsync(libraryItem, persistedStreamUrl, subtitles, context.HttpContext.RequestAborted);
                        _logger.LogWarning("[DynamicLibrary] PlaybackInfoFilter: Returning response with URL={Url}, Runtime={Runtime}, DirectPlay={DirectPlay}, DirectStream={DirectStream}",
                            persistedStreamUrl,
                            response.MediaSources?.FirstOrDefault()?.RunTimeTicks,
                            response.MediaSources?.FirstOrDefault()?.SupportsDirectPlay,
                            response.MediaSources?.FirstOrDefault()?.SupportsDirectStream);
                        context.Result = new OkObjectResult(response);
                        return;
                    }
                    _logger.LogInformation("[DynamicLibrary] PlaybackInfo: Could not build stream URL from path {Path}", dynamicLibraryUrl);
                }
            }

            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Intercepting PlaybackInfo for dynamic item: {Name} ({Id})",
            cachedItem.Name, itemId);

        var config = Config;

        // Check if we should skip unreleased content
        if (!config.ShowUnreleasedStreams)
        {
            var premiereDate = cachedItem.PremiereDate;
            if (premiereDate == null || premiereDate > DateTime.UtcNow)
            {
                _logger.LogDebug("[DynamicLibrary] Skipping stream for unreleased dynamic content: {Name} (Premiere: {Date})",
                    cachedItem.Name, premiereDate?.ToString("yyyy-MM-dd") ?? "unknown");
                await next();
                return;
            }
        }

        // Handle AIOStreams mode - query AIOStreams for available streams
        if (config.StreamProvider == StreamProvider.AIOStreams)
        {
            var aioResponse = await HandleAIOStreamsPlaybackAsync(cachedItem, selectedMediaSourceId, context.HttpContext.RequestAborted);
            if (aioResponse != null)
            {
                context.Result = new OkObjectResult(aioResponse);
                return;
            }

            _logger.LogInformation("[DynamicLibrary] AIOStreams returned no streams for {Name}", cachedItem.Name);
            await next();
            return;
        }

        // Handle Direct mode with potential multi-URL support for anime versions
        if (config.StreamProvider == StreamProvider.Direct)
        {
            var streamUrls = BuildDirectStreamUrls(cachedItem);

            if (streamUrls.Count == 0)
            {
                _logger.LogInformation("[DynamicLibrary] No stream URLs available for {Name}, playback may fail", cachedItem.Name);
                await next();
                return;
            }

            // Fetch subtitles if enabled
            List<CachedSubtitle>? subtitles = null;
            if (_subtitleService.IsEnabled)
            {
                subtitles = await FetchSubtitlesAsync(cachedItem, context.HttpContext.RequestAborted);
                _logger.LogDebug("[DynamicLibrary] Fetched {Count} subtitles for {Name}", subtitles?.Count ?? 0, cachedItem.Name);
            }

            // Handle multiple stream sources (e.g., anime with SUB/DUB)
            if (streamUrls.Count > 1)
            {
                // If a specific mediaSource was selected, filter to just that one
                if (!string.IsNullOrEmpty(selectedMediaSourceId))
                {
                    var selectedUrl = FindSelectedStreamUrl(streamUrls, selectedMediaSourceId, cachedItem.Id);
                    if (selectedUrl.HasValue)
                    {
                        _logger.LogDebug("[DynamicLibrary] PlaybackInfo: Matched version '{AudioType}' for {Name}, URL={Url}",
                            selectedUrl.Value.AudioType, cachedItem.Name, selectedUrl.Value.Url);
                        var response = BuildPlaybackInfoResponse(cachedItem, new List<(string, string)> { selectedUrl.Value }, subtitles);
                        context.Result = new OkObjectResult(response);
                        return;
                    }
                    _logger.LogDebug("[DynamicLibrary] Selected mediaSourceId '{Id}' not found, defaulting to first", selectedMediaSourceId);
                }

                // Default to first MediaSource for direct play (no version selected)
                var firstUrl = streamUrls[0];
                _logger.LogDebug("[DynamicLibrary] PlaybackInfo: Defaulting to first version '{AudioType}' for {Name}",
                    firstUrl.AudioType, cachedItem.Name);
                var defaultResponse = BuildPlaybackInfoResponse(cachedItem, new List<(string, string)> { firstUrl }, subtitles);
                context.Result = new OkObjectResult(defaultResponse);
                return;
            }

            _logger.LogDebug("[DynamicLibrary] Got {Count} stream URL(s) for {Name}", streamUrls.Count, cachedItem.Name);

            var allSourcesResponse = BuildPlaybackInfoResponse(cachedItem, streamUrls, subtitles);
            context.Result = new OkObjectResult(allSourcesResponse);
            return;
        }

        // For Embedarr or other providers, use single URL method
        var streamUrl = await GetStreamUrlAsync(cachedItem, context.HttpContext.RequestAborted);

        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogInformation("[DynamicLibrary] No stream URL available for {Name}, playback may fail", cachedItem.Name);
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Got stream URL for {Name}: {Url}", cachedItem.Name, streamUrl);

        var response2 = BuildPlaybackInfoResponse(cachedItem, streamUrl);
        context.Result = new OkObjectResult(response2);
    }

    /// <summary>
    /// Get the stream URL based on configured stream provider.
    /// </summary>
    private async Task<string?> GetStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        var config = Config;

        switch (config.StreamProvider)
        {
            case StreamProvider.None:
                _logger.LogDebug("[DynamicLibrary] Stream provider is None, no streaming available");
                return null;

            case StreamProvider.Embedarr:
                return await GetEmbedarrStreamUrlAsync(item, cancellationToken);

            case StreamProvider.Direct:
                return BuildDirectStreamUrl(item);

            case StreamProvider.AIOStreams:
                // AIOStreams is handled separately with multi-source support
                _logger.LogDebug("[DynamicLibrary] AIOStreams mode - use HandleAIOStreamsPlaybackAsync instead");
                return null;

            default:
                _logger.LogError("[DynamicLibrary] Unknown stream provider: {Provider}", config.StreamProvider);
                return null;
        }
    }

    /// <summary>
    /// Get the stream URL from Embedarr based on item type and configured ID preferences.
    /// </summary>
    private async Task<string?> GetEmbedarrStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        if (item.Type == BaseItemKind.Movie)
        {
            return await GetMovieStreamUrlAsync(item, cancellationToken);
        }

        if (item.Type == BaseItemKind.Episode)
        {
            return await GetEpisodeStreamUrlAsync(item, cancellationToken);
        }

        _logger.LogError("[DynamicLibrary] Unsupported item type for playback: {Type}", item.Type);
        return null;
    }

    /// <summary>
    /// Build stream URLs for Direct mode. Returns multiple URLs for anime with audio versions enabled.
    /// </summary>
    private List<(string Url, string AudioType)> BuildDirectStreamUrls(BaseItemDto item)
    {
        var config = Config;
        var urls = new List<(string Url, string AudioType)>();

        if (item.Type == BaseItemKind.Movie)
        {
            var template = config.DirectMovieUrlTemplate;
            if (!string.IsNullOrEmpty(template))
            {
                var url = ReplacePlaceholders(template, item, null, null);
                urls.Add((url, string.Empty));
            }
            return urls;
        }

        if (item.Type == BaseItemKind.Episode)
        {
            var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;
            var isAnime = series != null && DynamicLibraryService.IsAnime(series);

            var template = isAnime ? config.DirectAnimeUrlTemplate : config.DirectTvUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogInformation("[DynamicLibrary] Direct {Type} URL template is not configured",
                    isAnime ? "anime" : "TV");
                return urls;
            }

            var season = item.ParentIndexNumber ?? 1;
            var episode = item.IndexNumber ?? 1;

            // For anime with audio versions enabled, return multiple URLs (one per track)
            if (isAnime && config.EnableAnimeAudioVersions)
            {
                var tracks = config.AnimeAudioTracks?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (tracks == null || tracks.Length == 0)
                {
                    tracks = new[] { "sub" };
                }

                _logger.LogDebug("[DynamicLibrary] Building {Count} audio versions for anime: {Tracks}",
                    tracks.Length, string.Join(", ", tracks));

                foreach (var track in tracks)
                {
                    var url = ReplacePlaceholders(template, item, season, episode, series, track);
                    urls.Add((url, track));
                }
            }
            else
            {
                // Single URL for non-anime or when audio versions disabled
                var url = ReplacePlaceholders(template, item, season, episode, series, null);
                urls.Add((url, string.Empty));
            }
        }

        return urls;
    }

    /// <summary>
    /// Build a stream URL from templates for Direct mode (single URL).
    /// </summary>
    private string? BuildDirectStreamUrl(BaseItemDto item)
    {
        var config = Config;
        string template;

        if (item.Type == BaseItemKind.Movie)
        {
            template = config.DirectMovieUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogInformation("[DynamicLibrary] Direct movie URL template is not configured");
                return null;
            }

            return ReplacePlaceholders(template, item, null, null);
        }

        if (item.Type == BaseItemKind.Episode)
        {
            // Get the series to determine if it's anime
            var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;
            var isAnime = series != null && DynamicLibraryService.IsAnime(series);

            template = isAnime ? config.DirectAnimeUrlTemplate : config.DirectTvUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogInformation("[DynamicLibrary] Direct {Type} URL template is not configured",
                    isAnime ? "anime" : "TV");
                return null;
            }

            var season = item.ParentIndexNumber ?? 1;
            var episode = item.IndexNumber ?? 1;

            // For anime with audio selection enabled, use the first configured audio track
            string? audioType = null;
            if (isAnime && config.EnableAnimeAudioVersions)
            {
                var tracks = config.AnimeAudioTracks?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                audioType = tracks?.Length > 0 ? tracks[0] : "sub";
                _logger.LogDebug("[DynamicLibrary] Using audio track '{AudioType}' for anime episode", audioType);
            }

            return ReplacePlaceholders(template, item, season, episode, series, audioType);
        }

        _logger.LogError("[DynamicLibrary] Unsupported item type for Direct playback: {Type}", item.Type);
        return null;
    }

    /// <summary>
    /// Replace placeholders in URL template with actual values.
    /// </summary>
    /// <param name="template">URL template with placeholders.</param>
    /// <param name="item">The item (movie or episode).</param>
    /// <param name="season">Season number for episodes.</param>
    /// <param name="episode">Episode number for episodes.</param>
    /// <param name="series">Series for episodes (optional).</param>
    /// <param name="audioType">Audio type for anime ("sub" or "dub"), null for default.</param>
    private string ReplacePlaceholders(string template, BaseItemDto item, int? season, int? episode, BaseItemDto? series = null, string? audioType = null)
    {
        var config = Config;
        var providerIds = item.ProviderIds ?? new Dictionary<string, string>();

        // For episodes, prefer series provider IDs for the main ID
        var seriesProviderIds = series?.ProviderIds ?? providerIds;

        // Get preferred ID based on item type and config
        string? preferredId = null;
        if (item.Type == BaseItemKind.Movie)
        {
            var (id, _) = GetMovieId(item, config.MoviePreferredId);
            preferredId = id;
        }
        else if (item.Type == BaseItemKind.Episode)
        {
            var isAnime = series != null && DynamicLibraryService.IsAnime(series);
            var preference = isAnime ? config.AnimePreferredId : config.TvShowPreferredId;
            var (id, _) = GetSeriesId(item, series, preference);
            preferredId = id;
        }

        // Replace placeholders
        var absoluteEpisode = providerIds.GetValueOrDefault("AbsoluteNumber", "");
        var url = template
            .Replace("{id}", preferredId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{imdb}", seriesProviderIds.GetValueOrDefault("Imdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tmdb}", providerIds.GetValueOrDefault("Tmdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tvdb}", seriesProviderIds.GetValueOrDefault("Tvdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{anilist}", seriesProviderIds.GetValueOrDefault("AniList", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", season?.ToString() ?? "1", StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", episode?.ToString() ?? "1", StringComparison.OrdinalIgnoreCase)
            .Replace("{absolute}", absoluteEpisode, StringComparison.OrdinalIgnoreCase)
            .Replace("{audio}", audioType ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", Uri.EscapeDataString(item.Name ?? ""), StringComparison.OrdinalIgnoreCase);

        _logger.LogDebug("[DynamicLibrary] Direct URL built: {Url}", url);
        return url;
    }

    /// <summary>
    /// Get stream URL for a movie using configured ID preference.
    /// </summary>
    private async Task<string?> GetMovieStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        var config = Config;
        var preferredId = config.MoviePreferredId;

        _logger.LogDebug("[DynamicLibrary] Movie playback: {Name}, PreferredId={PreferredId}, ProviderIds={ProviderIds}",
            item.Name, preferredId,
            item.ProviderIds != null ? string.Join(", ", item.ProviderIds.Select(p => $"{p.Key}={p.Value}")) : "null");

        // Get the ID based on preference with fallback
        var (id, idType) = GetMovieId(item, preferredId);

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogInformation("[DynamicLibrary] No suitable ID found for movie: {Name}", item.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] Using {IdType} ID for movie {Name}: {Id}", idType, item.Name, id);

        var result = await _embedarrClient.GetMovieStreamUrlAsync(id, cancellationToken);
        return result?.Url;
    }

    /// <summary>
    /// Get stream URL for an episode using configured ID preference.
    /// </summary>
    private async Task<string?> GetEpisodeStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        var config = Config;

        // Get the series to determine if it's anime
        var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;
        var isAnime = series != null && DynamicLibraryService.IsAnime(series);

        // Use anime or TV show preference based on content type
        var preferredId = isAnime ? config.AnimePreferredId : config.TvShowPreferredId;

        _logger.LogDebug("[DynamicLibrary] Episode playback: {Name}, IsAnime={IsAnime}, PreferredId={PreferredId}",
            item.Name, isAnime, preferredId);
        _logger.LogDebug("[DynamicLibrary] Episode ProviderIds: {ProviderIds}",
            item.ProviderIds != null ? string.Join(", ", item.ProviderIds.Select(p => $"{p.Key}={p.Value}")) : "null");
        _logger.LogDebug("[DynamicLibrary] Series: {SeriesName}, ProviderIds={ProviderIds}",
            series?.Name ?? "null",
            series?.ProviderIds != null ? string.Join(", ", series.ProviderIds.Select(p => $"{p.Key}={p.Value}")) : "null");

        // Get the ID based on preference with fallback
        var (id, idType) = GetSeriesId(item, series, preferredId);

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogInformation("[DynamicLibrary] No suitable ID found for episode: {Name} (isAnime={IsAnime})", item.Name, isAnime);
            return null;
        }

        var season = item.ParentIndexNumber ?? 1;
        var episode = item.IndexNumber ?? 1;

        // Warn if using fallback values
        if (!item.ParentIndexNumber.HasValue)
        {
            _logger.LogInformation("[DynamicLibrary] Episode {Name} has no season number, defaulting to 1", item.Name);
        }
        if (!item.IndexNumber.HasValue)
        {
            _logger.LogInformation("[DynamicLibrary] Episode {Name} has no episode number, defaulting to 1", item.Name);
        }

        _logger.LogDebug("[DynamicLibrary] Calling Embedarr: GET /api/url/tv/{Id}/{Season}/{Episode} (IdType={IdType}, IsAnime={IsAnime})",
            id, season, episode, idType, isAnime);

        var result = await _embedarrClient.GetTvEpisodeStreamUrlAsync(id, season, episode, cancellationToken);

        _logger.LogDebug("[DynamicLibrary] Embedarr response: Url={Url}", result?.Url ?? "null");

        return result?.Url;
    }

    /// <summary>
    /// Get the movie ID based on preference with fallback.
    /// Movies can have: IMDB, TMDB, TVDB
    /// </summary>
    private (string? Id, string IdType) GetMovieId(BaseItemDto item, PreferredProviderId preference)
    {
        if (item.ProviderIds == null)
        {
            return (null, "none");
        }

        // Try preferred ID first, then fall back to others
        var fallbackOrder = preference switch
        {
            PreferredProviderId.Imdb => new[] { "Imdb", "Tmdb", "Tvdb" },
            PreferredProviderId.Tmdb => new[] { "Tmdb", "Imdb", "Tvdb" },
            PreferredProviderId.Tvdb => new[] { "Tvdb", "Imdb", "Tmdb" },
            _ => new[] { "Imdb", "Tmdb", "Tvdb" }
        };

        foreach (var provider in fallbackOrder)
        {
            if (item.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id))
            {
                return (id, provider.ToUpperInvariant());
            }
        }

        return (null, "none");
    }

    /// <summary>
    /// Get the series ID for an episode based on preference with fallback.
    /// TV/Anime can have: IMDB, TVDB, TMDB
    /// For IMDB, we NEVER return episode.ProviderIds["Imdb"] as it could be an episode-specific IMDB ID.
    /// </summary>
    private (string? Id, string IdType) GetSeriesId(BaseItemDto episode, BaseItemDto? series, PreferredProviderId preference)
    {
        // For IMDB preference, first check if episode has SeriesImdb stored (most reliable)
        if (preference == PreferredProviderId.Imdb && episode.ProviderIds?.TryGetValue("SeriesImdb", out var storedSeriesImdb) == true && !string.IsNullOrEmpty(storedSeriesImdb))
        {
            return (storedSeriesImdb, "IMDB");
        }

        // Try preferred ID first, then fall back to others
        var fallbackOrder = preference switch
        {
            PreferredProviderId.Imdb => new[] { "Imdb", "Tvdb", "AniList", "Tmdb" },
            PreferredProviderId.Tvdb => new[] { "Tvdb", "Imdb", "AniList", "Tmdb" },
            PreferredProviderId.Tmdb => new[] { "Tmdb", "Imdb", "Tvdb", "AniList" },
            PreferredProviderId.AniList => new[] { "AniList", "Tvdb", "Imdb", "Tmdb" },
            _ => new[] { "Imdb", "Tvdb", "AniList", "Tmdb" }
        };

        foreach (var provider in fallbackOrder)
        {
            // For IMDB: ONLY use SeriesImdb or series.ProviderIds - NEVER episode.ProviderIds["Imdb"]
            // because TVDB sometimes returns episode-level IMDB IDs which we don't want
            if (provider == "Imdb")
            {
                // Check SeriesImdb first (stored in episode during creation)
                if (episode.ProviderIds?.TryGetValue("SeriesImdb", out var seriesImdb) == true && !string.IsNullOrEmpty(seriesImdb))
                {
                    return (seriesImdb, "IMDB");
                }
                // Only check series provider IDs, NOT episode provider IDs
                if (series?.ProviderIds?.TryGetValue("Imdb", out var seriesProviderImdb) == true && !string.IsNullOrEmpty(seriesProviderImdb))
                {
                    return (seriesProviderImdb, "IMDB");
                }
                // Skip to next provider type - explicitly don't check episode.ProviderIds["Imdb"]
                continue;
            }

            // For non-IMDB providers, check series first, then episode
            if (series?.ProviderIds?.TryGetValue(provider, out var seriesId) == true && !string.IsNullOrEmpty(seriesId))
            {
                return (seriesId, provider.ToUpperInvariant());
            }
            if (episode.ProviderIds?.TryGetValue(provider, out var episodeId) == true && !string.IsNullOrEmpty(episodeId))
            {
                return (episodeId, provider.ToUpperInvariant());
            }
        }

        return (null, "none");
    }

    /// <summary>
    /// Fetch subtitles for an item based on its type.
    /// </summary>
    private async Task<List<CachedSubtitle>> FetchSubtitlesAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.Type == BaseItemKind.Movie)
            {
                return await _subtitleService.FetchMovieSubtitlesAsync(item, cancellationToken);
            }

            if (item.Type == BaseItemKind.Episode)
            {
                var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;

                // For persisted items, series won't be in cache - get from library
                if (series == null && item.SeriesId.HasValue)
                {
                    var libraryItem = _libraryManager.GetItemById(item.SeriesId.Value);
                    if (libraryItem != null)
                    {
                        series = new BaseItemDto
                        {
                            Id = libraryItem.Id,
                            Name = libraryItem.Name,
                            ProviderIds = libraryItem.ProviderIds
                        };
                    }
                }

                return await _subtitleService.FetchEpisodeSubtitlesAsync(item, series, cancellationToken);
            }

            return new List<CachedSubtitle>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching subtitles for {Name}", item.Name);
            return new List<CachedSubtitle>();
        }
    }

    /// <summary>
    /// Build subtitle MediaStreams from cached subtitles.
    /// Uses Jellyfin's standard subtitle URL format so our SubtitleFilter can intercept.
    /// </summary>
    private List<MediaStream> BuildSubtitleMediaStreams(Guid itemId, string mediaSourceId, List<CachedSubtitle>? subtitles)
    {
        var mediaStreams = new List<MediaStream>();

        if (subtitles == null || subtitles.Count == 0)
        {
            return mediaStreams;
        }

        var subtitleIndex = 0;

        foreach (var subtitle in subtitles)
        {
            // Use our custom DynamicLibrary endpoint for subtitles
            var deliveryUrl = $"/DynamicLibrary/Subtitles/{itemId}/{subtitle.LanguageCode}.vtt";

            _logger.LogDebug("[DynamicLibrary] Building subtitle stream: Index={Index}, Language={Language}",
                subtitleIndex, subtitle.LanguageCode);

            mediaStreams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = subtitleIndex++,
                Codec = "webvtt",
                Language = subtitle.LanguageCode,
                Title = subtitle.Language + (subtitle.HearingImpaired ? " [CC]" : ""),
                IsExternal = true,
                SupportsExternalStream = true,
                DeliveryMethod = SubtitleDeliveryMethod.External,
                DeliveryUrl = deliveryUrl
            });
        }

        _logger.LogDebug("[DynamicLibrary] Built {Count} subtitle streams for item {ItemId}",
            mediaStreams.Count, itemId);

        return mediaStreams;
    }

    /// <summary>
    /// Build default video/audio MediaStreams for non-HLS sources.
    /// HLS streams don't need this - the player parses streams from the m3u8.
    /// Non-HLS containers (MP4, MKV, etc.) need stream metadata for the player.
    /// </summary>
    private static List<MediaStream> BuildDefaultMediaStreams()
    {
        return new List<MediaStream>
        {
            new MediaStream
            {
                Type = MediaStreamType.Video,
                Index = 0,
                Codec = "h264",
                IsDefault = true,
            },
            new MediaStream
            {
                Type = MediaStreamType.Audio,
                Index = 1,
                Codec = "aac",
                IsDefault = true,
                Language = "und",
                Channels = 2
            }
        };
    }

    /// <summary>
    /// Build PlaybackInfoResponse with multiple MediaSources for version selection.
    /// </summary>
    private PlaybackInfoResponse BuildPlaybackInfoResponse(BaseItemDto item, List<(string Url, string AudioType)> streamUrls, List<CachedSubtitle>? subtitles = null)
    {
        var mediaSources = streamUrls.Select(s =>
        {
            // Generate same ID as SearchResultFactory for consistency
            var sourceId = GenerateMediaSourceGuid(item.Id, s.AudioType);

            // Build subtitle streams with the correct mediaSourceId for this source
            var subtitleStreams = BuildSubtitleMediaStreams(item.Id, sourceId, subtitles);

            // Display name for version selector
            var displayName = string.IsNullOrEmpty(s.AudioType)
                ? item.Name
                : s.AudioType.ToUpperInvariant();

            return new MediaSourceInfo
            {
                // Required identification
                Id = sourceId,
                Name = displayName,
                Path = s.Url,

                // Protocol settings for remote HLS
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = "hls",

                // Remote stream settings - DirectStream must be false to prevent
                // Jellyfin from using the database Path (dynamiclibrary://) with FFmpeg
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = false,
                SupportsTranscoding = false,
                SupportsProbing = false,
                RequiresOpening = false,
                RequiresClosing = false,

                // Runtime from item
                RunTimeTicks = item.RunTimeTicks,

                // Include subtitle streams (or empty list if none)
                MediaStreams = subtitleStreams.Count > 0 ? subtitleStreams : new List<MediaStream>()
            };
        }).ToArray();

        _logger.LogDebug("[DynamicLibrary] Built PlaybackInfoResponse with {Count} media sources",
            mediaSources.Length);

        return new PlaybackInfoResponse
        {
            MediaSources = mediaSources,
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Build PlaybackInfoResponse with a single MediaSource.
    /// </summary>
    private PlaybackInfoResponse BuildPlaybackInfoResponse(BaseItemDto item, string streamUrl)
    {
        return BuildPlaybackInfoResponse(item, new List<(string, string)> { (streamUrl, string.Empty) }, null);
    }

    /// <summary>
    /// Extract the selected mediaSourceId from the request (query string or body).
    /// </summary>
    private string? GetSelectedMediaSourceId(ActionExecutingContext context)
    {
        // Try query string first
        if (context.HttpContext.Request.Query.TryGetValue("mediaSourceId", out var queryValue) &&
            !string.IsNullOrEmpty(queryValue))
        {
            return queryValue.ToString();
        }

        // Try from action arguments (GetPostedPlaybackInfo has a playbackInfoDto parameter)
        if (context.ActionArguments.TryGetValue("playbackInfoDto", out var playbackInfoObj) &&
            playbackInfoObj != null)
        {
            // Use reflection to get MediaSourceId property
            var mediaSourceIdProp = playbackInfoObj.GetType().GetProperty("MediaSourceId");
            var value = mediaSourceIdProp?.GetValue(playbackInfoObj)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the stream URL matching the selected mediaSourceId.
    /// </summary>
    private (string Url, string AudioType)? FindSelectedStreamUrl(
        List<(string Url, string AudioType)> streamUrls,
        string selectedMediaSourceId,
        Guid itemId)
    {
        foreach (var stream in streamUrls)
        {
            // Generate the same ID that SearchResultFactory uses
            var sourceId = GenerateMediaSourceGuid(itemId, stream.AudioType);

            if (sourceId.Equals(selectedMediaSourceId, StringComparison.OrdinalIgnoreCase))
            {
                return stream;
            }
        }

        return null;
    }

    /// <summary>
    /// Generate a deterministic GUID for a MediaSource based on item ID and audio track.
    /// Must match the implementation in SearchResultFactory.
    /// </summary>
    private static string GenerateMediaSourceGuid(Guid itemId, string audioTrack)
    {
        if (string.IsNullOrEmpty(audioTrack))
        {
            return itemId.ToString("N");
        }

        var input = $"{itemId:N}:{audioTrack.ToLowerInvariant()}";
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString("N");
    }

    /// <summary>
    /// Check if the item path is a DynamicLibrary placeholder URL.
    /// </summary>
    private static bool IsDynamicLibraryPath(string? path)
    {
        return path?.StartsWith("dynamiclibrary://", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Check if a dynamiclibrary:// URL is for anime content.
    /// </summary>
    private static bool IsAnimeUrl(string path)
    {
        return path.StartsWith("dynamiclibrary://anime/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build PlaybackInfoResponse for a persisted anime item with multiple audio versions.
    /// Returns multiple MediaSources (SUB, DUB, etc.) for version selector in player.
    /// </summary>
    private async Task<PlaybackInfoResponse?> BuildAnimePlaybackInfoResponseAsync(
        BaseItem item,
        string dynamicLibraryUrl,
        List<CachedSubtitle>? subtitles,
        CancellationToken cancellationToken)
    {
        try
        {
            var uri = new Uri(dynamicLibraryUrl);
            var segments = uri.AbsolutePath.Trim('/').Split('/');

            if (segments.Length < 2)
            {
                _logger.LogWarning("[DynamicLibrary] Invalid anime URL format: {Url}", dynamicLibraryUrl);
                return null;
            }

            var anilistId = segments[0];
            var episodeNum = segments[1];

            var config = Config;
            var template = config.DirectAnimeUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogWarning("[DynamicLibrary] DirectAnimeUrlTemplate is not configured");
                return null;
            }

            // Build subtitle streams
            var subtitleStreams = BuildSubtitleMediaStreams(item.Id, item.Id.ToString("N"), subtitles);

            // Get runtime from API
            var runTimeTicks = await GetRuntimeForPersistedItemAsync(item, cancellationToken);

            // CRITICAL: Update database item so Jellyfin can track progress correctly
            // Jellyfin uses the database RunTimeTicks for progress calculation, not the one we return
            if (runTimeTicks.HasValue && item.RunTimeTicks != runTimeTicks.Value)
            {
                item.RunTimeTicks = runTimeTicks.Value;
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
                _logger.LogInformation("[DynamicLibrary] Updated database RunTimeTicks for {Name}: {Ticks} ({Minutes} min)",
                    item.Name, runTimeTicks.Value, runTimeTicks.Value / 600_000_000);
            }

            // Parse audio tracks from config
            var audioTracks = config.AnimeAudioTracks
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            if (audioTracks.Length == 0)
            {
                audioTracks = new[] { "sub", "dub" };
            }

            // Build MediaSource for each audio track
            var mediaSources = audioTracks.Select(track =>
            {
                var streamUrl = template
                    .Replace("{id}", anilistId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{anilist}", anilistId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{episode}", episodeNum, StringComparison.OrdinalIgnoreCase)
                    .Replace("{absolute}", episodeNum, StringComparison.OrdinalIgnoreCase)
                    .Replace("{audio}", track.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase);

                // Generate unique ID for this MediaSource
                var sourceId = GeneratePersistedAnimeMediaSourceId(item.Id, track);

                _logger.LogDebug("[DynamicLibrary] Built anime MediaSource for '{Name}' ({Track}): {Url}",
                    item.Name, track, streamUrl);

                return new MediaSourceInfo
                {
                    Id = sourceId,
                    Name = track.ToUpperInvariant(),  // "SUB", "DUB" shown in version selector
                    Path = streamUrl,
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "hls",
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = false,
                    SupportsTranscoding = false,
                    SupportsProbing = false,
                    RequiresOpening = false,
                    RequiresClosing = false,
                    RunTimeTicks = runTimeTicks,
                    MediaStreams = subtitleStreams.Count > 0 ? subtitleStreams : new List<MediaStream>()
                };
            }).ToArray();

            _logger.LogDebug("[DynamicLibrary] Built {Count} MediaSources for anime episode {Name}",
                mediaSources.Length, item.Name);

            return new PlaybackInfoResponse
            {
                MediaSources = mediaSources,
                PlaySessionId = Guid.NewGuid().ToString("N")
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error building anime playback info: {Url}", dynamicLibraryUrl);
            return null;
        }
    }

    /// <summary>
    /// Generate a deterministic ID for a persisted anime MediaSource.
    /// </summary>
    private static string GeneratePersistedAnimeMediaSourceId(Guid itemId, string audioTrack)
    {
        var input = $"persisted:{itemId:N}:{audioTrack.ToLowerInvariant()}";
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString("N");
    }

    /// <summary>
    /// Parse a DynamicLibrary placeholder URL and build the actual stream URL.
    /// Format: dynamiclibrary://movie/{id}
    ///         dynamiclibrary://tv/{id}/{season}/{episode}
    ///         dynamiclibrary://anime/{id}/{episode}/{audio}
    /// </summary>
    private string? BuildStreamUrlFromPath(string path)
    {
        try
        {
            var uri = new Uri(path);
            var type = uri.Host; // "movie", "tv", or "anime"
            var segments = uri.AbsolutePath.Trim('/').Split('/');

            var config = Config;

            if (type.Equals("movie", StringComparison.OrdinalIgnoreCase) && segments.Length >= 1)
            {
                var template = config.DirectMovieUrlTemplate;
                if (string.IsNullOrEmpty(template)) return null;

                return template
                    .Replace("{id}", segments[0], StringComparison.OrdinalIgnoreCase)
                    .Replace("{imdb}", segments[0], StringComparison.OrdinalIgnoreCase)
                    .Replace("{tmdb}", segments[0], StringComparison.OrdinalIgnoreCase);
            }
            else if (type.Equals("tv", StringComparison.OrdinalIgnoreCase) && segments.Length >= 3)
            {
                var template = config.DirectTvUrlTemplate;
                if (string.IsNullOrEmpty(template)) return null;

                return template
                    .Replace("{id}", segments[0], StringComparison.OrdinalIgnoreCase)
                    .Replace("{imdb}", segments[0], StringComparison.OrdinalIgnoreCase)
                    .Replace("{tvdb}", segments[0], StringComparison.OrdinalIgnoreCase)
                    .Replace("{season}", segments[1], StringComparison.OrdinalIgnoreCase)
                    .Replace("{episode}", segments[2], StringComparison.OrdinalIgnoreCase);
            }
            else if (type.Equals("anime", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            {
                var template = config.DirectAnimeUrlTemplate;
                if (string.IsNullOrEmpty(template)) return null;

                var audio = segments.Length > 2 ? segments[2] : "sub";
                return template
                    .Replace("{id}", segments[0], StringComparison.OrdinalIgnoreCase)
                    .Replace("{anilist}", segments[0], StringComparison.OrdinalIgnoreCase)
                    .Replace("{episode}", segments[1], StringComparison.OrdinalIgnoreCase)
                    .Replace("{absolute}", segments[1], StringComparison.OrdinalIgnoreCase)
                    .Replace("{audio}", audio, StringComparison.OrdinalIgnoreCase);
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error parsing DynamicLibrary path: {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Build PlaybackInfoResponse for a persisted library item with a stream URL.
    /// </summary>
    private async Task<PlaybackInfoResponse> BuildPlaybackInfoResponseFromPathAsync(BaseItem item, string streamUrl, List<CachedSubtitle>? subtitles, CancellationToken cancellationToken)
    {
        // Build subtitle streams
        var subtitleStreams = BuildSubtitleMediaStreams(item.Id, item.Id.ToString("N"), subtitles);

        // Get runtime from API (Jellyfin doesn't scrape .strm files)
        var runTimeTicks = await GetRuntimeForPersistedItemAsync(item, cancellationToken);

        // CRITICAL: Update database item so Jellyfin can track progress correctly
        // Jellyfin uses the database RunTimeTicks for progress calculation, not the one we return
        if (runTimeTicks.HasValue && item.RunTimeTicks != runTimeTicks.Value)
        {
            item.RunTimeTicks = runTimeTicks.Value;
            await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
            _logger.LogInformation("[DynamicLibrary] Updated database RunTimeTicks for {Name}: {Ticks} ({Minutes} min)",
                item.Name, runTimeTicks.Value, runTimeTicks.Value / 600_000_000);
        }

        // Detect container from URL (persisted .strm files can point to HLS, MKV, etc.)
        var container = StreamContainerHelper.DetectContainer(streamUrl, null);

        // HLS: use subtitles only - player parses video/audio from m3u8
        // Non-HLS: need default video/audio streams plus subtitles for seek support
        var mediaStreams = container == "hls"
            ? (subtitleStreams.Count > 0 ? subtitleStreams : new List<MediaStream>())
            : BuildDefaultMediaStreams().Concat(subtitleStreams).ToList();

        var mediaSource = new MediaSourceInfo
        {
            Id = item.Id.ToString("N"),
            Name = item.Name,
            Path = streamUrl,

            // Protocol settings for remote streams
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Container = container,

            // Remote stream settings - DirectStream must be false to prevent
            // Jellyfin from using the database Path (dynamiclibrary://) with FFmpeg
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,
            SupportsTranscoding = false,
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false,

            // Runtime from API lookup
            RunTimeTicks = runTimeTicks,

            // Include media streams (video/audio for non-HLS, plus subtitles)
            MediaStreams = mediaStreams,

            // For MKV/non-HLS: Add bitrate for time-based seek calculations
            // HLS uses segment-based seeking from m3u8 manifest and doesn't need this
            Bitrate = container != "hls" ? 5_000_000 : null  // ~5 Mbps estimate
        };

        _logger.LogDebug("[DynamicLibrary] Built PlaybackInfoResponse for persisted item {Name} with URL {Url}, Runtime {Runtime}, Container {Container}, {SubCount} subtitles",
            item.Name, streamUrl, runTimeTicks, container, subtitleStreams.Count);

        return new PlaybackInfoResponse
        {
            MediaSources = new[] { mediaSource },
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Get runtime for a persisted item from cache or API.
    /// </summary>
    private async Task<long?> GetRuntimeForPersistedItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        return await _searchResultFactory.GetRuntimeAsync(item, cancellationToken);
    }

    /// <summary>
    /// Fetch subtitles for a persisted library item by converting it to a BaseItemDto format.
    /// </summary>
    private async Task<List<CachedSubtitle>> FetchSubtitlesForPersistedItemAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            // Create a minimal DTO with required provider IDs for subtitle lookup
            var dto = new BaseItemDto
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.GetBaseItemKind(),
                ProviderIds = item.ProviderIds
            };

            // For episodes, add series info needed for subtitle lookup
            if (item is Episode episode)
            {
                dto.SeriesId = episode.SeriesId;
                dto.ParentIndexNumber = episode.ParentIndexNumber;
                dto.IndexNumber = episode.IndexNumber;

                // Get series name from library
                var series = _libraryManager.GetItemById(episode.SeriesId);
                if (series != null)
                {
                    dto.SeriesName = series.Name;
                }
            }

            return await FetchSubtitlesAsync(dto, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching subtitles for persisted item {Name}", item.Name);
            return new List<CachedSubtitle>();
        }
    }

    /// <summary>
    /// Handle playback via AIOStreams. Queries the AIOStreams addon for available streams
    /// and returns them as MediaSources for the version selector.
    /// </summary>
    private async Task<PlaybackInfoResponse?> HandleAIOStreamsPlaybackAsync(
        BaseItemDto item,
        string? selectedMediaSourceId,
        CancellationToken cancellationToken)
    {
        if (!_aiostreamsClient.IsConfigured)
        {
            _logger.LogWarning("[DynamicLibrary] AIOStreams is not configured");
            return null;
        }

        // Get IMDB ID - required for AIOStreams
        var imdbId = GetImdbIdForItem(item);
        if (string.IsNullOrEmpty(imdbId))
        {
            _logger.LogWarning("[DynamicLibrary] No IMDB ID found for {Name}, cannot query AIOStreams", item.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] Querying AIOStreams for {Type} {Name} (IMDB: {ImdbId})",
            item.Type, item.Name, imdbId);

        // Query AIOStreams based on item type
        AIOStreamsResponse? response;
        if (item.Type == BaseItemKind.Movie)
        {
            response = await _aiostreamsClient.GetMovieStreamsAsync(imdbId, cancellationToken);
        }
        else if (item.Type == BaseItemKind.Episode)
        {
            var season = item.ParentIndexNumber ?? 1;
            var episode = item.IndexNumber ?? 1;
            response = await _aiostreamsClient.GetEpisodeStreamsAsync(imdbId, season, episode, cancellationToken);
        }
        else
        {
            _logger.LogWarning("[DynamicLibrary] Unsupported item type for AIOStreams: {Type}", item.Type);
            return null;
        }

        if (response == null || response.Streams.Count == 0)
        {
            _logger.LogInformation("[DynamicLibrary] AIOStreams returned no streams for {Name}", item.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] AIOStreams returned {Count} streams for {Name}",
            response.Streams.Count, item.Name);

        // Get runtime - this is critical for Android TV and other players that need runtime for scrubbing
        long? runTimeTicks = item.RunTimeTicks;
        _logger.LogInformation("[DynamicLibrary] RunTimeTicks for {Name}: Initial={InitialTicks} (from item), Type={Type}",
            item.Name, runTimeTicks, item.Type);

        // Probe HLS for duration if enabled and item doesn't have a good runtime
        if (Config.EnableHlsProbing && (!runTimeTicks.HasValue || runTimeTicks.Value <= 0))
        {
            // Find the first HLS stream to probe
            var hlsStream = response.Streams.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.Url) &&
                StreamContainerHelper.DetectContainer(s.Url, s.BehaviorHints?.Filename) == "hls");

            if (hlsStream != null)
            {
                _logger.LogDebug("[DynamicLibrary] Probing HLS stream for duration: {Url}", hlsStream.Url);
                var probedDuration = await _hlsProbeService.GetHlsDurationTicksAsync(hlsStream.Url!, cancellationToken);
                if (probedDuration.HasValue)
                {
                    runTimeTicks = probedDuration.Value;
                    _logger.LogInformation("[DynamicLibrary] HLS probe found duration for {Name}: {Ticks} ticks ({Minutes} min)",
                        item.Name, runTimeTicks.Value, runTimeTicks.Value / 600_000_000);
                }
            }
        }

        // Fallback to default duration if we still don't have a runtime
        // This is critical for Android TV which won't play without RunTimeTicks
        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0)
        {
            // Default durations: Movies = 2 hours, Episodes = 45 minutes
            var defaultMinutes = item.Type == BaseItemKind.Movie ? 120 : 45;
            runTimeTicks = defaultMinutes * 60L * 10_000_000L; // Convert minutes to ticks
            _logger.LogInformation("[DynamicLibrary] Using default runtime for {Name}: {Minutes} min ({Ticks} ticks)",
                item.Name, defaultMinutes, runTimeTicks.Value);
        }

        _logger.LogInformation("[DynamicLibrary] Final RunTimeTicks for {Name}: {Ticks}",
            item.Name, runTimeTicks);

        // If a specific stream was selected, redirect to it
        if (!string.IsNullOrEmpty(selectedMediaSourceId))
        {
            var streamUrl = _itemCache.GetAIOStreamsStreamUrl(selectedMediaSourceId);
            if (!string.IsNullOrEmpty(streamUrl))
            {
                _logger.LogDebug("[DynamicLibrary] Returning selected AIOStreams source: {MediaSourceId} -> {Url}",
                    selectedMediaSourceId, streamUrl);

                // Return single MediaSource with the selected stream URL
                return BuildAIOStreamsSingleResponse(item, streamUrl, selectedMediaSourceId, runTimeTicks);
            }

            _logger.LogDebug("[DynamicLibrary] Selected MediaSource {Id} not found in cache, showing all streams",
                selectedMediaSourceId);
        }

        // Build MediaSources from all available streams
        var mediaSources = new List<MediaSourceInfo>();

        foreach (var stream in response.Streams)
        {
            if (string.IsNullOrEmpty(stream.Url))
            {
                continue; // Skip streams without direct URL
            }

            // Generate deterministic ID for this stream
            var sourceId = GenerateAIOStreamsMediaSourceId(stream.Url);

            // Get filename hint for container detection
            var filename = stream.BehaviorHints?.Filename;

            // Store mapping so we can resolve it later
            _itemCache.StoreAIOStreamsMapping(sourceId, item.Id, stream.Url, filename);

            // Detect container from URL/filename
            var container = StreamContainerHelper.DetectContainer(stream.Url, filename);

            // HLS: don't add fake MediaStreams - player parses them from the m3u8
            // Non-HLS (MP4, MKV, etc.): need default streams for player metadata
            var mediaStreams = container == "hls"
                ? new List<MediaStream>()
                : BuildDefaultMediaStreams();

            mediaSources.Add(new MediaSourceInfo
            {
                Id = sourceId,
                Name = stream.DisplayName,
                Path = stream.Url,
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = container,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = false,  // Force Jellyfin to proxy - enables range requests for large files
                SupportsTranscoding = false,
                SupportsProbing = false,
                RequiresOpening = false,
                RequiresClosing = false,
                RunTimeTicks = runTimeTicks,
                MediaStreams = mediaStreams,
                // For MKV/non-HLS: Add bitrate for time-based seek calculations
                // HLS uses segment-based seeking from m3u8 manifest and doesn't need this
                Bitrate = container != "hls" ? 5_000_000 : null  // ~5 Mbps estimate
            });
        }

        if (mediaSources.Count == 0)
        {
            _logger.LogInformation("[DynamicLibrary] No valid streams with URLs found for {Name}", item.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] Built {Count} MediaSources from AIOStreams for {Name} with runtime {Runtime}",
            mediaSources.Count, item.Name, runTimeTicks);

        return new PlaybackInfoResponse
        {
            MediaSources = mediaSources.ToArray(),
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Handle playback via AIOStreams for persisted library items.
    /// </summary>
    private async Task<PlaybackInfoResponse?> HandleAIOStreamsPlaybackForPersistedItemAsync(
        MediaBrowser.Controller.Entities.BaseItem libraryItem,
        string? selectedMediaSourceId,
        CancellationToken cancellationToken)
    {
        if (!_aiostreamsClient.IsConfigured)
        {
            _logger.LogWarning("[DynamicLibrary] AIOStreams is not configured");
            return null;
        }

        // Get IMDB ID from library item
        var imdbId = GetImdbIdFromLibraryItem(libraryItem);
        if (string.IsNullOrEmpty(imdbId))
        {
            _logger.LogWarning("[DynamicLibrary] No IMDB ID found for persisted item {Name}, cannot query AIOStreams", libraryItem.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] Querying AIOStreams for persisted {Type} {Name} (IMDB: {ImdbId})",
            libraryItem.GetType().Name, libraryItem.Name, imdbId);

        // Fetch runtime from API if not set (critical for player progress tracking)
        var runTimeTicks = await GetRuntimeForPersistedItemAsync(libraryItem, cancellationToken);
        _logger.LogInformation("[DynamicLibrary] RunTimeTicks for persisted {Name}: Initial={InitialTicks} (from API), Type={Type}",
            libraryItem.Name, runTimeTicks, libraryItem.GetType().Name);

        // CRITICAL: Update database item so Jellyfin can track progress correctly
        // Jellyfin uses the database RunTimeTicks for progress calculation, not the one we return
        if (runTimeTicks.HasValue && libraryItem.RunTimeTicks != runTimeTicks.Value)
        {
            libraryItem.RunTimeTicks = runTimeTicks.Value;
            await _libraryManager.UpdateItemAsync(libraryItem, libraryItem.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
            _logger.LogInformation("[DynamicLibrary] Updated database RunTimeTicks for {Name}: {Ticks} ({Minutes} min)",
                libraryItem.Name, runTimeTicks.Value, runTimeTicks.Value / 600_000_000);
        }

        // Fetch subtitles for the item
        List<CachedSubtitle>? subtitles = null;
        if (_subtitleService.IsEnabled)
        {
            subtitles = await FetchSubtitlesForPersistedItemAsync(libraryItem, cancellationToken);
            _logger.LogDebug("[DynamicLibrary] Fetched {Count} subtitles for persisted AIOStreams item {Name}",
                subtitles?.Count ?? 0, libraryItem.Name);
        }

        // Build subtitle MediaStreams (we'll use the library item ID as base for subtitle streams)
        var subtitleMediaSourceId = libraryItem.Id.ToString("N");
        var subtitleStreams = subtitles != null ? BuildSubtitleMediaStreams(libraryItem.Id, subtitleMediaSourceId, subtitles) : new List<MediaStream>();

        // Query AIOStreams based on item type
        AIOStreamsResponse? response;
        if (libraryItem is Movie)
        {
            response = await _aiostreamsClient.GetMovieStreamsAsync(imdbId, cancellationToken);
        }
        else if (libraryItem is Episode episode)
        {
            var season = episode.ParentIndexNumber ?? 1;
            var episodeNum = episode.IndexNumber ?? 1;
            response = await _aiostreamsClient.GetEpisodeStreamsAsync(imdbId, season, episodeNum, cancellationToken);
        }
        else
        {
            _logger.LogWarning("[DynamicLibrary] Unsupported item type for AIOStreams: {Type}", libraryItem.GetType().Name);
            return null;
        }

        if (response == null || response.Streams.Count == 0)
        {
            _logger.LogInformation("[DynamicLibrary] AIOStreams returned no streams for persisted item {Name}", libraryItem.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] AIOStreams returned {Count} streams for persisted item {Name}",
            response.Streams.Count, libraryItem.Name);

        // Probe HLS for duration if we still don't have a good runtime
        // This is critical for Android TV and other players that need runtime for scrubbing
        if (Config.EnableHlsProbing && (!runTimeTicks.HasValue || runTimeTicks.Value <= 0))
        {
            var hlsStream = response.Streams.FirstOrDefault(s =>
                !string.IsNullOrEmpty(s.Url) &&
                StreamContainerHelper.DetectContainer(s.Url, s.BehaviorHints?.Filename) == "hls");

            if (hlsStream != null)
            {
                _logger.LogDebug("[DynamicLibrary] Probing HLS stream for duration (persisted): {Url}", hlsStream.Url);
                var probedDuration = await _hlsProbeService.GetHlsDurationTicksAsync(hlsStream.Url!, cancellationToken);
                if (probedDuration.HasValue)
                {
                    runTimeTicks = probedDuration.Value;
                    _logger.LogInformation("[DynamicLibrary] HLS probe found duration for persisted {Name}: {Ticks} ticks ({Minutes} min)",
                        libraryItem.Name, runTimeTicks.Value, runTimeTicks.Value / 600_000_000);
                }
            }
        }

        // Fallback to default duration if we still don't have a runtime
        // This is critical for Android TV which won't play without RunTimeTicks
        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0)
        {
            // Default durations: Movies = 2 hours, Episodes = 45 minutes
            var defaultMinutes = libraryItem is Movie ? 120 : 45;
            runTimeTicks = defaultMinutes * 60L * 10_000_000L; // Convert minutes to ticks
            _logger.LogInformation("[DynamicLibrary] Using default runtime for persisted {Name}: {Minutes} min ({Ticks} ticks)",
                libraryItem.Name, defaultMinutes, runTimeTicks.Value);
        }

        _logger.LogInformation("[DynamicLibrary] Final RunTimeTicks for persisted {Name}: {Ticks}",
            libraryItem.Name, runTimeTicks);

        // If a specific stream was selected, redirect to it
        if (!string.IsNullOrEmpty(selectedMediaSourceId))
        {
            var streamUrl = _itemCache.GetAIOStreamsStreamUrl(selectedMediaSourceId);
            if (!string.IsNullOrEmpty(streamUrl))
            {
                _logger.LogDebug("[DynamicLibrary] Returning selected AIOStreams source for persisted item: {MediaSourceId} -> {Url}",
                    selectedMediaSourceId, streamUrl);

                return BuildAIOStreamsSingleResponseForPersistedItem(libraryItem, streamUrl, selectedMediaSourceId, runTimeTicks, subtitleStreams);
            }

            _logger.LogDebug("[DynamicLibrary] Selected MediaSource {Id} not found in cache for persisted item, showing all streams",
                selectedMediaSourceId);
        }

        // Build MediaSources from all available streams
        var mediaSources = new List<MediaSourceInfo>();

        foreach (var stream in response.Streams)
        {
            if (string.IsNullOrEmpty(stream.Url))
            {
                continue;
            }

            var sourceId = GenerateAIOStreamsMediaSourceId(stream.Url);
            var filename = stream.BehaviorHints?.Filename;

            _itemCache.StoreAIOStreamsMapping(sourceId, libraryItem.Id, stream.Url, filename);

            var container = StreamContainerHelper.DetectContainer(stream.Url, filename);

            // HLS: use subtitles only - player parses video/audio from m3u8
            // Non-HLS: need default video/audio streams plus subtitles
            var mediaStreams = container == "hls"
                ? (subtitleStreams.Count > 0 ? subtitleStreams : new List<MediaStream>())
                : BuildDefaultMediaStreams().Concat(subtitleStreams).ToList();

            mediaSources.Add(new MediaSourceInfo
            {
                Id = sourceId,
                Name = stream.DisplayName,
                Path = stream.Url,
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = container,
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = false,  // Force Jellyfin to proxy - enables range requests for large files
                SupportsTranscoding = false,
                SupportsProbing = false,
                RequiresOpening = false,
                RequiresClosing = false,
                RunTimeTicks = runTimeTicks,
                MediaStreams = mediaStreams,
                // For MKV/non-HLS: Add bitrate for time-based seek calculations
                // HLS uses segment-based seeking from m3u8 manifest and doesn't need this
                Bitrate = container != "hls" ? 5_000_000 : null  // ~5 Mbps estimate
            });
        }

        if (mediaSources.Count == 0)
        {
            _logger.LogInformation("[DynamicLibrary] No valid streams with URLs found for persisted item {Name}", libraryItem.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] Built {Count} MediaSources from AIOStreams for persisted item {Name}",
            mediaSources.Count, libraryItem.Name);

        return new PlaybackInfoResponse
        {
            MediaSources = mediaSources.ToArray(),
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Build a single-stream PlaybackInfoResponse for AIOStreams with a persisted library item.
    /// </summary>
    private PlaybackInfoResponse BuildAIOStreamsSingleResponseForPersistedItem(
        MediaBrowser.Controller.Entities.BaseItem libraryItem,
        string streamUrl,
        string mediaSourceId,
        long? runTimeTicks,
        List<MediaStream> subtitleStreams)
    {
        var mapping = _itemCache.GetAIOStreamsMapping(mediaSourceId);
        var container = StreamContainerHelper.DetectContainer(streamUrl, mapping?.Filename);

        // HLS: use subtitles only - player parses video/audio from m3u8
        // Non-HLS: need default video/audio streams plus subtitles
        var mediaStreams = container == "hls"
            ? (subtitleStreams.Count > 0 ? subtitleStreams : new List<MediaStream>())
            : BuildDefaultMediaStreams().Concat(subtitleStreams).ToList();

        var mediaSource = new MediaSourceInfo
        {
            Id = mediaSourceId,
            Name = libraryItem.Name,
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Container = container,
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,  // Force Jellyfin to proxy - enables range requests for large files
            SupportsTranscoding = false,
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false,
            RunTimeTicks = runTimeTicks,
            MediaStreams = mediaStreams,
            // For MKV/non-HLS: Add bitrate for time-based seek calculations
            // HLS uses segment-based seeking from m3u8 manifest and doesn't need this
            Bitrate = container != "hls" ? 5_000_000 : null  // ~5 Mbps estimate
        };

        return new PlaybackInfoResponse
        {
            MediaSources = new[] { mediaSource },
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Get the IMDB ID from a library item, checking item and series provider IDs.
    /// For episodes, always prefer the series IMDB ID (required for streaming APIs).
    /// </summary>
    private string? GetImdbIdFromLibraryItem(MediaBrowser.Controller.Entities.BaseItem item)
    {
        // For episodes, ALWAYS check series IMDB first (required for streaming APIs)
        // Episode items can have episode-level IMDB IDs which don't work with streaming services
        if (item is Episode episode && episode.SeriesId != Guid.Empty)
        {
            var series = _libraryManager.GetItemById(episode.SeriesId);
            if (series?.ProviderIds?.TryGetValue("Imdb", out var seriesImdbId) == true && !string.IsNullOrEmpty(seriesImdbId))
            {
                return seriesImdbId;
            }
        }

        // For non-episodes, or if series IMDB not found, use item's own IMDB
        if (item.ProviderIds?.TryGetValue("Imdb", out var imdbId) == true && !string.IsNullOrEmpty(imdbId))
        {
            return imdbId;
        }

        return null;
    }

    /// <summary>
    /// Build a single-stream PlaybackInfoResponse for AIOStreams.
    /// </summary>
    private PlaybackInfoResponse BuildAIOStreamsSingleResponse(BaseItemDto item, string streamUrl, string mediaSourceId, long? runTimeTicks)
    {
        // Get the mapping to retrieve filename hint for container detection
        var mapping = _itemCache.GetAIOStreamsMapping(mediaSourceId);
        var container = StreamContainerHelper.DetectContainer(streamUrl, mapping?.Filename);

        _logger.LogInformation("[DynamicLibrary] BuildAIOStreamsSingleResponse for {Name}: RunTimeTicks={Ticks}, Container={Container}",
            item.Name, runTimeTicks, container);

        // HLS: don't add fake MediaStreams - player parses them from the m3u8
        // Non-HLS: need default video/audio streams for player metadata
        var mediaStreams = container == "hls"
            ? new List<MediaStream>()
            : BuildDefaultMediaStreams();

        var mediaSource = new MediaSourceInfo
        {
            Id = mediaSourceId,
            Name = item.Name,
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Container = container,
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,  // Force Jellyfin to proxy - enables range requests for large files
            SupportsTranscoding = false,
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false,
            RunTimeTicks = runTimeTicks,
            MediaStreams = mediaStreams,
            // For MKV/non-HLS: Add bitrate for time-based seek calculations
            // HLS uses segment-based seeking from m3u8 manifest and doesn't need this
            Bitrate = container != "hls" ? 5_000_000 : null  // ~5 Mbps estimate
        };

        return new PlaybackInfoResponse
        {
            MediaSources = new[] { mediaSource },
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Build a PlaybackInfoResponse directly from AIOStreams stream URL mapping.
    /// Used when the item isn't in cache but we have the MediaSource → URL mapping.
    /// </summary>
    private async Task<PlaybackInfoResponse> BuildAIOStreamsDirectResponseAsync(Guid itemId, string streamUrl, string mediaSourceId, CancellationToken cancellationToken)
    {
        // Get the mapping to retrieve filename hint for container detection
        var mapping = _itemCache.GetAIOStreamsMapping(mediaSourceId);
        var container = StreamContainerHelper.DetectContainer(streamUrl, mapping?.Filename);

        // Try to get runtime from library item or API
        // This is critical for Android TV which won't play without RunTimeTicks
        long? runTimeTicks = null;
        string itemName = "Stream";

        var libraryItem = _libraryManager.GetItemById(itemId);
        if (libraryItem != null)
        {
            itemName = libraryItem.Name;
            // Use the async method that queries TVDB/TMDB APIs for runtime
            runTimeTicks = await GetRuntimeForPersistedItemAsync(libraryItem, cancellationToken);
            _logger.LogInformation("[DynamicLibrary] BuildAIOStreamsDirectResponseAsync: Library item {Name} has RunTimeTicks={Ticks} (from API lookup)",
                itemName, runTimeTicks);

            // CRITICAL: Update database item so Jellyfin can track progress correctly
            // Jellyfin uses the database RunTimeTicks for progress calculation, not the one we return
            if (runTimeTicks.HasValue && libraryItem.RunTimeTicks != runTimeTicks.Value)
            {
                libraryItem.RunTimeTicks = runTimeTicks.Value;
                await _libraryManager.UpdateItemAsync(libraryItem, libraryItem.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken);
                _logger.LogInformation("[DynamicLibrary] Updated database RunTimeTicks for {Name}: {Ticks} ({Minutes} min)",
                    libraryItem.Name, runTimeTicks.Value, runTimeTicks.Value / 600_000_000);
            }
        }

        // Fallback to default duration if API lookup failed or no library item
        if (!runTimeTicks.HasValue || runTimeTicks.Value <= 0)
        {
            // Use smart defaults based on item type
            var defaultMinutes = libraryItem is MediaBrowser.Controller.Entities.TV.Episode ? 24 : 120;
            runTimeTicks = defaultMinutes * 60L * 10_000_000L;
            _logger.LogWarning("[DynamicLibrary] BuildAIOStreamsDirectResponseAsync: Using fallback runtime {Minutes} min for {Name}",
                defaultMinutes, itemName);
        }

        _logger.LogInformation("[DynamicLibrary] BuildAIOStreamsDirectResponse: Final RunTimeTicks={Ticks}, Container={Container}",
            runTimeTicks, container);

        // HLS: don't add fake MediaStreams - player parses them from the m3u8
        // Non-HLS (MP4, etc.): need default video/audio streams for player metadata
        var mediaStreams = container == "hls"
            ? new List<MediaStream>()
            : BuildDefaultMediaStreams();

        var mediaSource = new MediaSourceInfo
        {
            Id = mediaSourceId,
            Name = itemName,
            Path = streamUrl,
            Protocol = MediaProtocol.Http,
            Type = MediaSourceType.Default,
            Container = container,
            IsRemote = true,
            SupportsDirectPlay = true,
            SupportsDirectStream = false,  // Force Jellyfin to proxy - enables range requests for large files
            SupportsTranscoding = false,
            SupportsProbing = false,
            RequiresOpening = false,
            RequiresClosing = false,
            RunTimeTicks = runTimeTicks,
            MediaStreams = mediaStreams,
            // For MKV/non-HLS: Add bitrate for time-based seek calculations
            // HLS uses segment-based seeking from m3u8 manifest and doesn't need this
            Bitrate = container != "hls" ? 5_000_000 : null  // ~5 Mbps estimate
        };

        return new PlaybackInfoResponse
        {
            MediaSources = new[] { mediaSource },
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Get the IMDB ID for an item, checking both item and series provider IDs.
    /// For episodes, always prefer the series IMDB ID (required for AIOStreams).
    /// </summary>
    private string? GetImdbIdForItem(BaseItemDto item)
    {
        // For episodes, ALWAYS prefer series IMDB ID (required for AIOStreams)
        if (item.Type == BaseItemKind.Episode)
        {
            // First check if episode has SeriesImdb stored directly (most reliable)
            if (item.ProviderIds?.TryGetValue("SeriesImdb", out var storedSeriesImdb) == true && !string.IsNullOrEmpty(storedSeriesImdb))
            {
                return storedSeriesImdb;
            }

            // Fall back to cache lookup if episode doesn't have SeriesImdb
            if (item.SeriesId.HasValue)
            {
                var series = _itemCache.GetItem(item.SeriesId.Value);
                if (series?.ProviderIds?.TryGetValue("Imdb", out var seriesImdbId) == true && !string.IsNullOrEmpty(seriesImdbId))
                {
                    return seriesImdbId;
                }
            }
        }

        // For non-episodes, or if series IMDB not found, try item's own IMDB ID
        if (item.ProviderIds?.TryGetValue("Imdb", out var imdbId) == true && !string.IsNullOrEmpty(imdbId))
        {
            return imdbId;
        }

        return null;
    }

    /// <summary>
    /// Generate a deterministic GUID for an AIOStreams MediaSource based on the stream URL.
    /// </summary>
    private static string GenerateAIOStreamsMediaSourceId(string streamUrl)
    {
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes($"aiostreams:{streamUrl}"));
        return new Guid(hash).ToString("N");
    }
}
