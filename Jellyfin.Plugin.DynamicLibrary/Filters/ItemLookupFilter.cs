using Jellyfin.Data.Enums;
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
/// Also triggers Embedarr to add items to the library when user views item details.
/// </summary>
public class ItemLookupFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly DynamicLibraryService _dynamicLibraryService;
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
        ILogger<ItemLookupFilter> logger)
    {
        _itemCache = itemCache;
        _searchResultFactory = searchResultFactory;
        _dynamicLibraryService = dynamicLibraryService;
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
        if (cachedItem == null)
        {
            // Not a dynamic item, let Jellyfin handle it
            await next();
            return;
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

        _logger.LogDebug("[DynamicLibrary] Returning cached dynamic item: {Name} ({Id})",
            cachedItem.Name, itemId);

        // Enrich with full details based on item type
        if (cachedItem.Type == BaseItemKind.Movie)
        {
            cachedItem = await _searchResultFactory.EnrichMovieDtoAsync(cachedItem, context.HttpContext.RequestAborted);
            // Trigger Embedarr in background (fire-and-forget) to add to library - if enabled
            if (DynamicLibraryPlugin.Instance?.Configuration.CreateMediaOnView == true)
            {
                _ = TriggerEmbedarrAsync(cachedItem);
            }
        }
        else if (cachedItem.Type == BaseItemKind.Series)
        {
            cachedItem = await _searchResultFactory.EnrichSeriesDtoAsync(cachedItem, context.HttpContext.RequestAborted);
            // Trigger Embedarr in background (fire-and-forget) to add to library - if enabled
            if (DynamicLibraryPlugin.Instance?.Configuration.CreateMediaOnView == true)
            {
                _ = TriggerEmbedarrAsync(cachedItem);
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
}
