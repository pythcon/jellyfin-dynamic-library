using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts video stream requests for persisted dynamic library items.
/// When a client requests /Videos/{id}/stream for a .strm file with a dynamiclibrary:// URL,
/// this filter redirects to the actual stream URL from the AIOStreams cache instead of
/// letting Jellyfin try to proxy the custom protocol (which would fail).
/// </summary>
public class VideoStreamFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<VideoStreamFilter> _logger;

    private static readonly HashSet<string> StreamActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetVideoStream",
        "GetVideoStreamByContainer",
        "HeadVideoStream",
        "HeadVideoStreamByContainer"
    };

    public VideoStreamFilter(
        DynamicItemCache itemCache,
        ILibraryManager libraryManager,
        ILogger<VideoStreamFilter> logger)
    {
        _itemCache = itemCache;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public int Order => 0;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;

        if (controllerName != "Videos" || actionName == null || !StreamActions.Contains(actionName))
        {
            await next();
            return;
        }

        if (!context.ActionArguments.TryGetValue("itemId", out var itemIdObj) || itemIdObj is not Guid itemId)
        {
            await next();
            return;
        }

        // Extract mediaSourceId from all possible sources
        string? mediaSourceId = null;
        if (context.ActionArguments.TryGetValue("mediaSourceId", out var msIdObj) && msIdObj is string msId && !string.IsNullOrEmpty(msId))
        {
            mediaSourceId = msId;
        }

        if (string.IsNullOrEmpty(mediaSourceId) && context.HttpContext.Request.Query.TryGetValue("mediaSourceId", out var queryMsId))
        {
            mediaSourceId = queryMsId.ToString();
        }

        _logger.LogDebug("[DynamicLibrary] VideoStream: Controller={Controller}, Action={Action}, ItemId={ItemId}, MediaSourceId={MediaSourceId}",
            controllerName, actionName, itemId, mediaSourceId ?? "null");

        // Try to find the actual stream URL from AIOStreams cache by mediaSourceId
        if (!string.IsNullOrEmpty(mediaSourceId))
        {
            var streamUrl = _itemCache.GetAIOStreamsStreamUrl(mediaSourceId);
            if (!string.IsNullOrEmpty(streamUrl))
            {
                _logger.LogInformation("[DynamicLibrary] VideoStream: Redirecting to AIOStreams URL for MediaSource {Id}", mediaSourceId);
                context.Result = new RedirectResult(streamUrl, permanent: false);
                return;
            }

            // Also try looking up the full mapping
            var mapping = _itemCache.GetAIOStreamsMapping(mediaSourceId);
            if (mapping != null && !string.IsNullOrEmpty(mapping.StreamUrl))
            {
                _logger.LogInformation("[DynamicLibrary] VideoStream: Redirecting via mapping for MediaSource {Id}", mediaSourceId);
                context.Result = new RedirectResult(mapping.StreamUrl, permanent: false);
                return;
            }
        }

        // Check if this is a persisted dynamic library item with dynamiclibrary:// path
        var libraryItem = _libraryManager.GetItemById(itemId);
        if (libraryItem?.Path?.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                if (File.Exists(libraryItem.Path))
                {
                    var strmContents = File.ReadAllText(libraryItem.Path).Trim();
                    if (strmContents.StartsWith("dynamiclibrary://", StringComparison.OrdinalIgnoreCase))
                    {
                        // Try to find cached stream URL by item ID
                        // Some clients pass the itemId as the mediaSourceId
                        var itemStreamUrl = _itemCache.GetItemStreamUrl(itemId);
                        if (!string.IsNullOrEmpty(itemStreamUrl))
                        {
                            _logger.LogInformation("[DynamicLibrary] VideoStream: Redirecting to cached stream URL for item {Name}", libraryItem.Name);
                            context.Result = new RedirectResult(itemStreamUrl, permanent: false);
                            return;
                        }

                        _logger.LogWarning("[DynamicLibrary] VideoStream: Item {Name} has dynamiclibrary:// path but no cached stream URL. MediaSourceId={MediaSourceId}",
                            libraryItem.Name, mediaSourceId ?? "null");
                        // Return 404 instead of letting Jellyfin crash trying to proxy dynamiclibrary://
                        context.Result = new NotFoundResult();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DynamicLibrary] VideoStream: Error reading .strm file for {Name}", libraryItem.Name);
            }
        }

        await next();
    }
}
