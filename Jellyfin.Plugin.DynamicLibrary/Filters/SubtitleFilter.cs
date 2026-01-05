using System.Text;
using Jellyfin.Plugin.DynamicLibrary.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts subtitle requests for dynamic items and serves
/// cached subtitles from OpenSubtitles.
/// </summary>
public class SubtitleFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly SubtitleService _subtitleService;
    private readonly ILogger<SubtitleFilter> _logger;

    private static readonly HashSet<string> SubtitleActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetSubtitle",
        "GetSubtitleWithTicks"
    };

    public SubtitleFilter(
        DynamicItemCache itemCache,
        SubtitleService subtitleService,
        ILogger<SubtitleFilter> logger)
    {
        _itemCache = itemCache;
        _subtitleService = subtitleService;
        _logger = logger;
    }

    // Run before other filters
    public int Order => 0;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

        // Check if this is a subtitle request (controller is "Subtitle" singular)
        if (controllerName != "Subtitle" || actionName == null || !SubtitleActions.Contains(actionName))
        {
            await next();
            return;
        }

        // Get route values
        if (!context.ActionArguments.TryGetValue("routeItemId", out var itemIdObj) || itemIdObj is not Guid itemId)
        {
            // Try alternative parameter name
            if (!context.ActionArguments.TryGetValue("itemId", out itemIdObj) || itemIdObj is not Guid altItemId)
            {
                await next();
                return;
            }
            itemId = altItemId;
        }

        // Check if this is a dynamic item
        if (!_itemCache.HasItem(itemId))
        {
            await next();
            return;
        }

        // Get subtitle index (try both "routeIndex" and "index" parameter names)
        if (!context.ActionArguments.TryGetValue("routeIndex", out var indexObj) || indexObj is not int subtitleIndex)
        {
            // Try alternative parameter name
            if (!context.ActionArguments.TryGetValue("index", out indexObj) || indexObj is not int altIndex)
            {
                _logger.LogWarning("[DynamicLibrary] SubtitleFilter: Missing subtitle index for item {ItemId}", itemId);
                await next();
                return;
            }
            subtitleIndex = altIndex;
        }

        _logger.LogDebug("[DynamicLibrary] SubtitleFilter: Intercepting subtitle request for dynamic item {ItemId}, index {Index}",
            itemId, subtitleIndex);

        // Get cached subtitles
        var subtitles = _itemCache.GetSubtitles(itemId);
        if (subtitles == null || subtitleIndex >= subtitles.Count)
        {
            _logger.LogWarning("[DynamicLibrary] SubtitleFilter: No subtitle at index {Index} for item {ItemId}", subtitleIndex, itemId);
            context.Result = new NotFoundResult();
            return;
        }

        var subtitle = subtitles[subtitleIndex];

        // Get subtitle content
        var content = await _subtitleService.GetSubtitleContentAsync(itemId, subtitle.LanguageCode, context.HttpContext.RequestAborted);
        if (string.IsNullOrEmpty(content))
        {
            _logger.LogWarning("[DynamicLibrary] SubtitleFilter: Failed to get content for subtitle {Language}", subtitle.LanguageCode);
            context.Result = new NotFoundResult();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] SubtitleFilter: Serving subtitle {Language} for item {ItemId}, length={Length}",
            subtitle.LanguageCode, itemId, content.Length);

        // Return WebVTT content
        context.Result = new ContentResult
        {
            Content = content,
            ContentType = "text/vtt; charset=utf-8",
            StatusCode = 200
        };
    }
}
