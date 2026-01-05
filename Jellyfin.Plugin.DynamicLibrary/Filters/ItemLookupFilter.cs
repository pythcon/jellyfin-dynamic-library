using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Dto;
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
        ILogger<ItemLookupFilter> logger)
    {
        _itemCache = itemCache;
        _searchResultFactory = searchResultFactory;
        _dynamicLibraryService = dynamicLibraryService;
        _persistenceService = persistenceService;
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
                cachedItem = _itemCache.GetItem(episodeId.Value);
                requestedMediaSourceId = mediaSourceIdStr; // Remember which MediaSource was requested
            }

            if (cachedItem == null)
            {
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

        _logger.LogWarning("[DynamicLibrary] ItemLookup: Returning cached item: {Name} ({Id}), Type={Type}, HasMediaSources={HasSources}, MediaSourceCount={Count}",
            cachedItem.Name, itemId, cachedItem.Type, cachedItem.MediaSources != null, cachedItem.MediaSources?.Length ?? 0);

        // Log MediaSource details for episodes to debug playback issues
        if (cachedItem.Type == BaseItemKind.Episode && cachedItem.MediaSources != null)
        {
            foreach (var source in cachedItem.MediaSources)
            {
                _logger.LogWarning("[DynamicLibrary] Episode MediaSource: Id={Id}, Name={Name}, Path={Path}, SupportsDirectPlay={DirectPlay}",
                    source.Id, source.Name, source.Path, source.SupportsDirectPlay);
            }
        }

        // If this was a MediaSource ID lookup, filter to only that MediaSource
        if (!string.IsNullOrEmpty(requestedMediaSourceId) && cachedItem.MediaSources != null)
        {
            var selectedSource = cachedItem.MediaSources.FirstOrDefault(s => s.Id == requestedMediaSourceId);
            if (selectedSource != null)
            {
                _logger.LogWarning("[DynamicLibrary] Filtering to requested MediaSource: {Name} ({Id})",
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
}
