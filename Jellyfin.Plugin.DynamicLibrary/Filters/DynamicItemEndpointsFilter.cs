using System.Linq;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts secondary item endpoints (Similar, ThemeMedia, etc.)
/// for dynamic items and returns appropriate empty responses instead of 404s.
/// </summary>
public class DynamicItemEndpointsFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly ILogger<DynamicItemEndpointsFilter> _logger;

    // Endpoints that we should intercept for dynamic items
    private static readonly HashSet<string> InterceptedActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetSimilarItems",
        "GetThemeMedia",
        "GetThemeSongs",
        "GetThemeVideos",
        "GetSpecialFeatures",
        "GetLocalTrailers",
        "GetIntros",
    };

    public DynamicItemEndpointsFilter(
        DynamicItemCache itemCache,
        ILogger<DynamicItemEndpointsFilter> logger)
    {
        _itemCache = itemCache;
        _logger = logger;
    }

    // Run before other filters
    public int Order => 0;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Check if this is an action we should intercept
        if (actionName == null || !InterceptedActions.Contains(actionName))
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

        // Check if this is a dynamic item
        if (!_itemCache.HasItem(itemId))
        {
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Intercepting {Action} for dynamic item {Id}", actionName, itemId);

        // Return appropriate empty response based on the action
        context.Result = actionName switch
        {
            "GetSimilarItems" => new OkObjectResult(new QueryResult<BaseItemDto>
            {
                Items = Array.Empty<BaseItemDto>(),
                TotalRecordCount = 0
            }),
            "GetThemeMedia" => new OkObjectResult(new AllThemeMediaResult
            {
                ThemeVideosResult = new ThemeMediaResult { Items = Array.Empty<BaseItemDto>() },
                ThemeSongsResult = new ThemeMediaResult { Items = Array.Empty<BaseItemDto>() },
                SoundtrackSongsResult = new ThemeMediaResult { Items = Array.Empty<BaseItemDto>() }
            }),
            "GetThemeSongs" or "GetThemeVideos" => new OkObjectResult(new ThemeMediaResult
            {
                Items = Array.Empty<BaseItemDto>()
            }),
            "GetSpecialFeatures" or "GetLocalTrailers" or "GetIntros" => new OkObjectResult(Array.Empty<BaseItemDto>()),
            _ => new OkObjectResult(new { })
        };
    }
}
