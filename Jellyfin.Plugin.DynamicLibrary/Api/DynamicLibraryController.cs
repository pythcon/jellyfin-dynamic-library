using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

/// <summary>
/// Controller for Dynamic Library API endpoints.
/// </summary>
[ApiController]
[Route("DynamicLibrary")]
public class DynamicLibraryController : ControllerBase
{
    private readonly DynamicItemCache _itemCache;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DynamicLibraryController> _logger;

    public DynamicLibraryController(
        DynamicItemCache itemCache,
        IHttpClientFactory httpClientFactory,
        ILogger<DynamicLibraryController> logger)
    {
        _itemCache = itemCache;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Get a dynamic item by ID.
    /// </summary>
    [HttpGet("Items/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<BaseItemDto> GetItem([FromRoute] Guid itemId)
    {
        var item = _itemCache.GetItem(itemId);
        if (item == null)
        {
            _logger.LogWarning("[DynamicLibrary] Item not found in cache: {ItemId}", itemId);
            return NotFound();
        }

        _logger.LogDebug("[DynamicLibrary] Returning cached item: {Name} ({Id})", item.Name, itemId);
        return Ok(item);
    }

    /// <summary>
    /// Get image for a dynamic item.
    /// </summary>
    [HttpGet("Items/{itemId}/Images/Primary")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetItemImage(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        var imageUrl = _itemCache.GetImageUrl(itemId);
        if (string.IsNullOrEmpty(imageUrl))
        {
            _logger.LogWarning("[DynamicLibrary] No image URL for item: {ItemId}", itemId);
            return NotFound();
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var response = await client.GetAsync(imageUrl, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DynamicLibrary] Failed to fetch image from {Url}: {Status}",
                    imageUrl, response.StatusCode);
                return NotFound();
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var imageData = await response.Content.ReadAsByteArrayAsync(cancellationToken);

            return File(imageData, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching image from {Url}", imageUrl);
            return NotFound();
        }
    }
}
