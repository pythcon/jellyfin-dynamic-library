using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts item lookup requests and returns cached data
/// for dynamic (virtual) items that don't exist in Jellyfin's database.
/// Also triggers Embedarr/Persistence to add items to the library when user views item details.
/// </summary>
public class ItemLookupFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly DynamicLibraryService _dynamicLibraryService;
    private readonly PersistenceService _persistenceService;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ItemLookupFilter> _logger;

    // Action names that look up individual items
    // UserLibrary.GetItem = /Users/{userId}/Items/{itemId}
    // UserLibrary.GetItemLegacy = legacy endpoint, same URL pattern
    // Items.GetItem = /Items/{itemId} (may not exist)
    private static readonly HashSet<string> ItemLookupActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetItem",
        "GetItemLegacy",
        "GetItemByNameType",
    };

    // Controllers that have item lookup
    private static readonly HashSet<string> ItemLookupControllers = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserLibrary",
        "Items",
    };

    public ItemLookupFilter(
        DynamicItemCache itemCache,
        SearchResultFactory searchResultFactory,
        DynamicLibraryService dynamicLibraryService,
        PersistenceService persistenceService,
        ILibraryManager libraryManager,
        ILogger<ItemLookupFilter> logger)
    {
        _itemCache = itemCache;
        _searchResultFactory = searchResultFactory;
        _dynamicLibraryService = dynamicLibraryService;
        _persistenceService = persistenceService;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    // Run before other filters
    public int Order => 0;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Check if this is an item lookup action on a relevant controller
        var isLookupAction = actionName != null &&
            ItemLookupActions.Contains(actionName) &&
            controllerName != null &&
            ItemLookupControllers.Contains(controllerName);

        if (!isLookupAction)
        {
            await next();
            return;
        }

        // Try to get the item ID from the action arguments
        if (!context.ActionArguments.TryGetValue("itemId", out var itemIdObj) || itemIdObj is not Guid itemId)
        {
            await next();
            return;
        }

        // Check if this is a dynamic item in our cache
        var cachedItem = _itemCache.GetItem(itemId);
        var requestedMediaSourceId = (string?)null;

        if (cachedItem == null)
        {
            // Check if this is a MediaSource ID that maps to an episode
            var mediaSourceIdStr = itemId.ToString("N");
            var episodeId = _itemCache.GetEpisodeIdForMediaSource(mediaSourceIdStr);
            if (episodeId.HasValue)
            {
                _logger.LogWarning("[DynamicLibrary] ItemLookup: Resolved MediaSource ID {MediaSourceId} to Episode {EpisodeId}",
                    itemId, episodeId.Value);

                // Try cache first (for dynamic items)
                cachedItem = _itemCache.GetItem(episodeId.Value);
                requestedMediaSourceId = mediaSourceIdStr;

                // If not in cache, check if it's a persisted anime episode in the library
                if (cachedItem == null)
                {
                    var libraryEpisode = _libraryManager.GetItemById(episodeId.Value) as Episode;
                    if (libraryEpisode != null && IsPersistedAnimeEpisode(libraryEpisode))
                    {
                        _logger.LogWarning("[DynamicLibrary] ItemLookup: Found persisted anime episode for MediaSource ID");

                        // Build a BaseItemDto from the library episode
                        // We can't call next() because the request is for the MediaSource ID, not the episode ID
                        var dto = BuildBaseItemDtoFromEpisode(libraryEpisode);

                        // Add MediaSources
                        await AddAnimeMediaSourcesToDto(dto, libraryEpisode, context.HttpContext.RequestAborted);

                        // Filter to just the requested MediaSource
                        if (dto.MediaSources != null && dto.MediaSources.Length > 0)
                        {
                            var selectedSource = dto.MediaSources.FirstOrDefault(s => s.Id == requestedMediaSourceId);
                            if (selectedSource != null)
                            {
                                dto.MediaSources = new[] { selectedSource };
                                // Store the selection for clients that pass episode ID on playback (like Android TV)
                                _itemCache.StoreSelectedMediaSource(libraryEpisode.Id, requestedMediaSourceId);
                                _logger.LogWarning("[DynamicLibrary] ItemLookup: Returning episode {Name} with MediaSource {Source}",
                                    libraryEpisode.Name, selectedSource.Name);
                            }
                        }

                        context.Result = new OkObjectResult(dto);
                        return;
                    }
                }
            }

            if (cachedItem == null)
            {
                // Check if this is a real persisted item that needs update checking
                var pluginConfig = DynamicLibraryPlugin.Instance?.Configuration;
                if (pluginConfig?.CheckForUpdatesOnView == true && pluginConfig?.EnablePersistence == true)
                {
                    _ = CheckPersistedItemForUpdatesAsync(itemId, context.HttpContext.RequestAborted);
                }

                // Check if this is a persisted anime episode that needs MediaSources added
                var libraryItem = _libraryManager.GetItemById(itemId);
                _logger.LogWarning("[DynamicLibrary] ItemLookup persisted check: ItemId={Id}, Found={Found}, IsEpisode={IsEp}, Path={Path}",
                    itemId, libraryItem != null, libraryItem is Episode, libraryItem?.Path ?? "null");

                if (libraryItem is Episode episode)
                {
                    var isPersistedAnime = IsPersistedAnimeEpisode(episode);
                    _logger.LogWarning("[DynamicLibrary] ItemLookup: Episode check - IsPersistedAnime={IsAnime}", isPersistedAnime);

                    if (isPersistedAnime)
                    {
                        // Let Jellyfin handle the request first
                        var result = await next();

                        _logger.LogWarning("[DynamicLibrary] ItemLookup: Jellyfin result Type={Type}, ResultType={ResultType}, ValueType={ValueType}",
                            result?.GetType().Name ?? "null", result?.Result?.GetType().Name ?? "null",
                            (result?.Result as ObjectResult)?.Value?.GetType().Name ?? "not ObjectResult");

                        // Then add MediaSources to the response for anime version selector
                        // Jellyfin returns ObjectResult (not OkObjectResult), so check for base class
                        if (result.Result is ObjectResult objResult && objResult.Value is BaseItemDto dto)
                        {
                            await AddAnimeMediaSourcesToDto(dto, episode, context.HttpContext.RequestAborted);
                            _logger.LogWarning("[DynamicLibrary] Added {Count} MediaSources to persisted anime episode {Name}",
                                dto.MediaSources?.Length ?? 0, dto.Name);
                        }
                        else
                        {
                            _logger.LogWarning("[DynamicLibrary] ItemLookup: Result was not ObjectResult with BaseItemDto, actual value type={Type}",
                                (result?.Result as ObjectResult)?.Value?.GetType().Name ?? "unknown");
                        }
                        return;
                    }
                }

                // Not a dynamic item, let Jellyfin handle it
                await next();
                return;
            }
        }

        // Verify this is actually a dynamic item by checking for our provider ID
        // This prevents returning cached items when GUIDs collide with real items
        if (!SearchResultFactory.IsDynamicItem(cachedItem))
        {
            _logger.LogDebug("[DynamicLibrary] Cached item {Id} does not have DynamicLibrary provider ID, passing to Jellyfin",
                itemId);
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] ItemLookup: Returning cached item: {Name} ({Id}), Type={Type}, HasMediaSources={HasSources}, MediaSourceCount={Count}",
            cachedItem.Name, itemId, cachedItem.Type, cachedItem.MediaSources != null, cachedItem.MediaSources?.Length ?? 0);

        // Log MediaSource details for episodes to debug playback issues
        if (cachedItem.Type == BaseItemKind.Episode && cachedItem.MediaSources != null)
        {
            foreach (var source in cachedItem.MediaSources)
            {
                _logger.LogDebug("[DynamicLibrary] Episode MediaSource: Id={Id}, Name={Name}, Path={Path}, SupportsDirectPlay={DirectPlay}",
                    source.Id, source.Name, source.Path, source.SupportsDirectPlay);
            }
        }

        // If this was a MediaSource ID lookup, filter to only that MediaSource
        if (!string.IsNullOrEmpty(requestedMediaSourceId) && cachedItem.MediaSources != null)
        {
            var selectedSource = cachedItem.MediaSources.FirstOrDefault(s => s.Id == requestedMediaSourceId);
            if (selectedSource != null)
            {
                _logger.LogDebug("[DynamicLibrary] Filtering to requested MediaSource: {Name} ({Id})",
                    selectedSource.Name, selectedSource.Id);
                cachedItem.MediaSources = new[] { selectedSource };
            }
        }

        // Enrich with full details based on item type
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        var shouldTriggerEmbedarr = config?.CreateMediaOnView == true &&
                                    config?.StreamProvider == StreamProvider.Embedarr;
        var shouldTriggerPersistence = config?.EnablePersistence == true;

        if (cachedItem.Type == BaseItemKind.Movie)
        {
            cachedItem = await _searchResultFactory.EnrichMovieDtoAsync(cachedItem, context.HttpContext.RequestAborted);
            // Trigger Embedarr in background (fire-and-forget) to add to library - if enabled and using Embedarr provider
            if (shouldTriggerEmbedarr)
            {
                _ = TriggerEmbedarrAsync(cachedItem);
            }
            // Trigger persistence in background (fire-and-forget) to create .strm files - if enabled
            if (shouldTriggerPersistence)
            {
                _ = TriggerPersistenceAsync(cachedItem);
            }
        }
        else if (cachedItem.Type == BaseItemKind.Series)
        {
            cachedItem = await _searchResultFactory.EnrichSeriesDtoAsync(cachedItem, context.HttpContext.RequestAborted);
            // Trigger Embedarr in background (fire-and-forget) to add to library - if enabled and using Embedarr provider
            if (shouldTriggerEmbedarr)
            {
                _ = TriggerEmbedarrAsync(cachedItem);
            }
            // Trigger persistence in background (fire-and-forget) to create .strm files - if enabled
            if (shouldTriggerPersistence)
            {
                _ = TriggerPersistenceAsync(cachedItem);
            }
            // Check for new episodes in already-persisted series (fire-and-forget)
            if (config?.CheckForUpdatesOnView == true && config?.EnablePersistence == true)
            {
                _ = TriggerSeriesUpdateAsync(cachedItem);
            }
        }

        // Return our cached item directly
        context.Result = new OkObjectResult(cachedItem);
    }

    /// <summary>
    /// Trigger Embedarr to add the item to the library in the background.
    /// This ensures the item is available for streaming when user clicks play.
    /// </summary>
    private async Task TriggerEmbedarrAsync(BaseItemDto item)
    {
        try
        {
            // Skip if already added to Embedarr
            if (_itemCache.IsAddedToEmbedarr(item.Id))
            {
                _logger.LogDebug("[DynamicLibrary] Item {Name} already added to Embedarr, skipping", item.Name);
                return;
            }

            _logger.LogDebug("[DynamicLibrary] Triggering Embedarr for: {Name} ({Id})", item.Name, item.Id);

            var result = await _dynamicLibraryService.AddToEmbedarrAsync(item);

            if (result?.Success == true)
            {
                _itemCache.MarkAddedToEmbedarr(item.Id);
                _logger.LogDebug("[DynamicLibrary] Successfully added {Name} to Embedarr", item.Name);
            }
            else
            {
                _logger.LogDebug("[DynamicLibrary] Failed to add {Name} to Embedarr: {Error}",
                    item.Name, result?.Error ?? "Unknown error");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] Error triggering Embedarr for {Name}", item.Name);
        }
    }

    /// <summary>
    /// Trigger persistence to create .strm files for the item in the background.
    /// This ensures the item is added to the Jellyfin library when user views item details.
    /// </summary>
    private async Task TriggerPersistenceAsync(BaseItemDto item)
    {
        try
        {
            // Skip if already persisted
            if (_itemCache.IsAddedToEmbedarr(item.Id)) // Reuse the "added" tracking
            {
                _logger.LogDebug("[DynamicLibrary] Item {Name} already persisted, skipping", item.Name);
                return;
            }

            _logger.LogInformation("[DynamicLibrary] Triggering persistence for: {Name} ({Id})", item.Name, item.Id);

            string? createdPath = null;

            if (item.Type == BaseItemKind.Movie)
            {
                createdPath = await _persistenceService.PersistMovieAsync(item);
            }
            else if (item.Type == BaseItemKind.Series)
            {
                createdPath = await _persistenceService.PersistSeriesAsync(item);
            }

            if (!string.IsNullOrEmpty(createdPath))
            {
                _itemCache.MarkAddedToEmbedarr(item.Id); // Reuse the tracking to prevent re-persistence
                _logger.LogInformation("[DynamicLibrary] Successfully persisted {Name} to: {Path}", item.Name, createdPath);

                // Trigger library scan if configured
                _persistenceService.TriggerLibraryScan();
            }
            else
            {
                _logger.LogDebug("[DynamicLibrary] Item {Name} was not persisted (already exists or failed)", item.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] Error triggering persistence for {Name}", item.Name);
        }
    }

    /// <summary>
    /// Check for and add new episodes to an already-persisted series.
    /// This ensures ongoing shows stay up-to-date with new episodes.
    /// </summary>
    private async Task TriggerSeriesUpdateAsync(BaseItemDto series)
    {
        try
        {
            // Check if series is already persisted (folder exists)
            var seriesPath = _persistenceService.GetSeriesFolderPath(series);
            if (!Directory.Exists(seriesPath))
            {
                _logger.LogDebug("[DynamicLibrary] Series {Name} not persisted, skipping update check", series.Name);
                return;
            }

            _logger.LogDebug("[DynamicLibrary] Checking for new episodes: {Name}", series.Name);

            var newCount = await _persistenceService.UpdateSeriesAsync(series);

            if (newCount > 0)
            {
                _logger.LogInformation("[DynamicLibrary] Added {Count} new episodes for {Name}", newCount, series.Name);

                // Trigger library scan to pick up new episodes
                _persistenceService.TriggerLibraryScan();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] Error checking for series updates: {Name}", series.Name);
        }
    }

    /// <summary>
    /// Check if a real (already persisted) library item needs updates.
    /// For series: check for new aired episodes.
    /// For movies: check if movie is now released and create .strm if needed.
    /// </summary>
    private async Task CheckPersistedItemForUpdatesAsync(Guid itemId, CancellationToken cancellationToken)
    {
        try
        {
            var realItem = _libraryManager.GetItemById(itemId);
            if (realItem == null)
            {
                return;
            }

            // Handle Series
            if (realItem is Series series)
            {
                await CheckPersistedSeriesForUpdatesAsync(series, cancellationToken);
            }
            // Handle Movies - check if movie folder exists but .strm doesn't (was unreleased, now released)
            else if (realItem is MediaBrowser.Controller.Entities.Movies.Movie movie)
            {
                await CheckPersistedMovieForUpdatesAsync(movie, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DynamicLibrary] Error checking persisted item for updates: {Id}", itemId);
        }
    }

    /// <summary>
    /// Check if a persisted series has new aired episodes to add.
    /// </summary>
    private async Task CheckPersistedSeriesForUpdatesAsync(Series series, CancellationToken cancellationToken)
    {
        try
        {
            // Check if this series has .strm files (indicating it's from our plugin)
            var episodes = series.GetRecursiveChildren().OfType<Episode>().ToList();
            if (episodes.Count == 0)
            {
                return;
            }

            var firstEpisode = episodes.FirstOrDefault();
            if (firstEpisode?.Path == null || !firstEpisode.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Read .strm content to verify it's ours
            if (!File.Exists(firstEpisode.Path))
            {
                return;
            }

            var strmContent = await File.ReadAllTextAsync(firstEpisode.Path, cancellationToken);
            if (!strmContent.StartsWith("dynamiclibrary://", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _logger.LogDebug("[DynamicLibrary] Checking persisted series for updates: {Name}", series.Name);

            // Convert to BaseItemDto for the persistence service
            var seriesDto = new BaseItemDto
            {
                Id = series.Id,
                Name = series.Name,
                Type = BaseItemKind.Series,
                ProductionYear = series.ProductionYear,
                ProviderIds = series.ProviderIds,
                Genres = series.Genres
            };

            // Fetch fresh episode data and update
            var enrichedSeries = await _searchResultFactory.EnrichSeriesDtoAsync(seriesDto, cancellationToken);

            var newCount = await _persistenceService.UpdateSeriesAsync(enrichedSeries);
            if (newCount > 0)
            {
                _logger.LogInformation("[DynamicLibrary] Added {Count} new episodes for persisted series: {Name}",
                    newCount, series.Name);
                _persistenceService.TriggerLibraryScan();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DynamicLibrary] Error checking persisted series for updates: {Name}", series.Name);
        }
    }

    /// <summary>
    /// Check if a movie that was previously unreleased is now released.
    /// </summary>
    private async Task CheckPersistedMovieForUpdatesAsync(MediaBrowser.Controller.Entities.Movies.Movie movie, CancellationToken cancellationToken)
    {
        try
        {
            // Check if movie has a .strm file
            if (movie.Path == null || !movie.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // If the .strm file already exists, nothing to do
            if (File.Exists(movie.Path))
            {
                return;
            }

            // Movie folder exists but no .strm - check if it's now released
            var movieDto = new BaseItemDto
            {
                Id = movie.Id,
                Name = movie.Name,
                Type = BaseItemKind.Movie,
                ProductionYear = movie.ProductionYear,
                ProviderIds = movie.ProviderIds,
                PremiereDate = movie.PremiereDate
            };

            // Try to create the movie now
            var createdPath = await _persistenceService.PersistMovieAsync(movieDto, cancellationToken);
            if (!string.IsNullOrEmpty(createdPath))
            {
                _logger.LogInformation("[DynamicLibrary] Created .strm for now-released movie: {Name}", movie.Name);
                _persistenceService.TriggerLibraryScan();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[DynamicLibrary] Error checking persisted movie for updates: {Name}", movie.Name);
        }
    }

    /// <summary>
    /// Check if an episode is a persisted anime that needs MediaSources added for version selector.
    /// </summary>
    private bool IsPersistedAnimeEpisode(Episode episode)
    {
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        _logger.LogWarning("[DynamicLibrary] IsPersistedAnimeEpisode: Config={HasConfig}, EnableAudioVersions={Enable}, AudioTracks={Tracks}",
            config != null, config?.EnableAnimeAudioVersions, config?.AnimeAudioTracks ?? "null");

        if (config?.EnableAnimeAudioVersions != true || string.IsNullOrEmpty(config.AnimeAudioTracks))
        {
            _logger.LogWarning("[DynamicLibrary] IsPersistedAnimeEpisode: Config check failed, returning false");
            return false;
        }

        // Check if it has a .strm file
        if (string.IsNullOrEmpty(episode.Path) || !episode.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("[DynamicLibrary] IsPersistedAnimeEpisode: Not a .strm file - Path={Path}", episode.Path ?? "null");
            return false;
        }

        // Read .strm to check if it's an anime URL
        try
        {
            if (!File.Exists(episode.Path))
            {
                _logger.LogWarning("[DynamicLibrary] IsPersistedAnimeEpisode: File does not exist - Path={Path}", episode.Path);
                return false;
            }

            var strmContent = File.ReadAllText(episode.Path).Trim();
            _logger.LogWarning("[DynamicLibrary] IsPersistedAnimeEpisode: StrmContent={Content}", strmContent);
            var isAnime = strmContent.StartsWith("dynamiclibrary://anime/", StringComparison.OrdinalIgnoreCase);
            _logger.LogWarning("[DynamicLibrary] IsPersistedAnimeEpisode: IsAnimeUrl={IsAnime}", isAnime);
            return isAnime;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] Error reading .strm file for anime check: {Path}", episode.Path);
            return false;
        }
    }

    /// <summary>
    /// Add MediaSources to a BaseItemDto for persisted anime episodes.
    /// This allows clients to show the version selector (SUB/DUB) in the player.
    /// </summary>
    private async Task AddAnimeMediaSourcesToDto(BaseItemDto dto, Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            var config = DynamicLibraryPlugin.Instance?.Configuration;
            if (config == null)
            {
                return;
            }

            // Read .strm content to get the anime URL
            if (string.IsNullOrEmpty(episode.Path) || !File.Exists(episode.Path))
            {
                return;
            }

            var strmContent = await File.ReadAllTextAsync(episode.Path, cancellationToken);
            strmContent = strmContent.Trim();

            if (!strmContent.StartsWith("dynamiclibrary://anime/", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            // Parse the URL: dynamiclibrary://anime/{anilistId}/{episode}
            var uri = new Uri(strmContent);
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length < 2)
            {
                _logger.LogWarning("[DynamicLibrary] Invalid anime URL format: {Url}", strmContent);
                return;
            }

            var anilistId = segments[0];
            var episodeNum = segments[1];

            var template = config.DirectAnimeUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogDebug("[DynamicLibrary] DirectAnimeUrlTemplate not configured for persisted anime");
                return;
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

                // Generate unique ID for this MediaSource (same algorithm as PlaybackInfoFilter)
                var input = $"persisted:{episode.Id:N}:{track.ToLowerInvariant()}";
                using var md5 = System.Security.Cryptography.MD5.Create();
                var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                var sourceId = new Guid(hash).ToString("N");

                // Store the mapping so we can resolve this MediaSource ID to the episode later
                _itemCache.StoreMediaSourceMapping(sourceId, episode.Id);

                _logger.LogDebug("[DynamicLibrary] Built MediaSource for persisted anime '{Name}' ({Track}): Id={SourceId}",
                    episode.Name, track, sourceId);

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
                    RunTimeTicks = dto.RunTimeTicks
                };
            }).ToArray();

            dto.MediaSources = mediaSources;

            _logger.LogInformation("[DynamicLibrary] Added {Count} MediaSources to persisted anime episode {Name}: {Sources}",
                mediaSources.Length, episode.Name, string.Join(", ", audioTracks.Select(t => t.ToUpperInvariant())));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error adding MediaSources to persisted anime: {Name}", episode.Name);
        }
    }

    /// <summary>
    /// Build a BaseItemDto from a library Episode.
    /// Used when returning episode data for a MediaSource ID lookup.
    /// </summary>
    private BaseItemDto BuildBaseItemDtoFromEpisode(Episode episode)
    {
        return new BaseItemDto
        {
            Id = episode.Id,
            Name = episode.Name,
            Type = BaseItemKind.Episode,
            SeriesId = episode.SeriesId,
            SeriesName = episode.SeriesName,
            SeasonId = episode.SeasonId,
            ParentIndexNumber = episode.ParentIndexNumber,
            IndexNumber = episode.IndexNumber,
            Overview = episode.Overview,
            PremiereDate = episode.PremiereDate,
            ProductionYear = episode.ProductionYear,
            RunTimeTicks = episode.RunTimeTicks,
            ProviderIds = episode.ProviderIds,
            Path = episode.Path
        };
    }
}
