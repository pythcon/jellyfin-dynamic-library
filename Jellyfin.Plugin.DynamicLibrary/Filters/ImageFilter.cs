using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts image requests for dynamic items and proxies
/// the images from TMDB/TVDB.
/// </summary>
public class ImageFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageFilter> _logger;

    public ImageFilter(
        DynamicItemCache itemCache,
        IHttpClientFactory httpClientFactory,
        ILogger<ImageFilter> logger)
    {
        _itemCache = itemCache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    // Run before other filters
    public int Order => 0;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Check if this is an image request (Image controller, various Get methods)
        if (controllerName != "Image" && controllerName != "Images")
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

        // Determine which image type is being requested (Primary, Backdrop, Logo, etc.)
        string? imageUrl = null;
        if (context.ActionArguments.TryGetValue("imageType", out var imageTypeObj) &&
            imageTypeObj is ImageType imageType)
        {
            imageUrl = imageType switch
            {
                ImageType.Backdrop => _itemCache.GetBackdropUrl(itemId) ?? _itemCache.GetImageUrl(itemId),
                ImageType.Logo => _itemCache.GetLogoUrl(itemId),
                _ => _itemCache.GetImageUrl(itemId)
            };
        }
        else
        {
            imageUrl = _itemCache.GetImageUrl(itemId);
        }

        if (string.IsNullOrEmpty(imageUrl))
        {
            // Not a dynamic item with image, let Jellyfin handle it
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Proxying {ImageType} image for dynamic item {Id} from {Url}",
            imageTypeObj ?? "Primary", itemId, imageUrl);

        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(imageUrl, context.HttpContext.RequestAborted);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DynamicLibrary] Failed to fetch image: {Status}", response.StatusCode);
                await next();
                return;
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var imageData = await response.Content.ReadAsByteArrayAsync(context.HttpContext.RequestAborted);

            context.Result = new FileContentResult(imageData, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching image from {Url}", imageUrl);
            // Let Jellyfin try to handle it (will probably 404)
            await next();
        }
    }
}
