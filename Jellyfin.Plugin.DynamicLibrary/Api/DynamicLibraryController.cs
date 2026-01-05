using System.Text;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
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
    private readonly SubtitleService _subtitleService;
    private readonly PersistenceService _persistenceService;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DynamicLibraryController> _logger;

    public DynamicLibraryController(
        DynamicItemCache itemCache,
        SubtitleService subtitleService,
        PersistenceService persistenceService,
        IHttpClientFactory httpClientFactory,
        ILogger<DynamicLibraryController> logger)
    {
        _itemCache = itemCache;
        _subtitleService = subtitleService;
        _persistenceService = persistenceService;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

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

    /// <summary>
    /// Get subtitle for a dynamic item in WebVTT format.
    /// </summary>
    [HttpGet("Subtitles/{itemId}/{languageCode}.vtt")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSubtitleVtt(
        [FromRoute] Guid itemId,
        [FromRoute] string languageCode,
        CancellationToken cancellationToken)
    {
        var content = await _subtitleService.GetSubtitleContentAsync(itemId, languageCode, cancellationToken);
        if (content == null)
        {
            _logger.LogWarning("[DynamicLibrary] Subtitle not found: {ItemId}, {Language}", itemId, languageCode);
            return NotFound();
        }

        _logger.LogDebug("[DynamicLibrary] Serving VTT subtitle: {ItemId}, {Language}, ContentLength={Length}",
            itemId, languageCode, content.Length);

        return Content(content, "text/vtt", Encoding.UTF8);
    }

    /// <summary>
    /// Get subtitle for a dynamic item in JSON TrackEvents format.
    /// Used by Jellyfin web player for custom subtitle rendering.
    /// </summary>
    [HttpGet("Subtitles/{itemId}/{languageCode}.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSubtitleJs(
        [FromRoute] Guid itemId,
        [FromRoute] string languageCode,
        CancellationToken cancellationToken)
    {
        var content = await _subtitleService.GetSubtitleContentAsync(itemId, languageCode, cancellationToken);
        if (content == null)
        {
            _logger.LogWarning("[DynamicLibrary] Subtitle not found: {ItemId}, {Language}", itemId, languageCode);
            return NotFound();
        }

        // Convert WebVTT to TrackEvents JSON
        var trackEventsJson = SubtitleConverter.WebVttToTrackEvents(content);

        _logger.LogDebug("[DynamicLibrary] Serving JS subtitle: {ItemId}, {Language}, ContentLength={Length}",
            itemId, languageCode, trackEventsJson.Length);

        return Content(trackEventsJson, "application/json", Encoding.UTF8);
    }

    /// <summary>
    /// Persist a dynamic item to the library as a .strm file.
    /// </summary>
    /// <param name="itemId">The ID of the dynamic item to persist.</param>
    /// <returns>The path to the created item, or an error if persistence failed.</returns>
    [HttpPost("Persist/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PersistItem(
        [FromRoute] Guid itemId,
        CancellationToken cancellationToken)
    {
        // Check if persistence is enabled
        if (!Config.EnablePersistence)
        {
            _logger.LogWarning("[DynamicLibrary] Persistence is not enabled");
            return BadRequest(new { Error = "Persistence is not enabled in plugin settings" });
        }

        // Get the item from cache
        var item = _itemCache.GetItem(itemId);
        if (item == null)
        {
            _logger.LogWarning("[DynamicLibrary] Item not found in cache for persistence: {ItemId}", itemId);
            return NotFound(new { Error = "Item not found in cache" });
        }

        _logger.LogInformation("[DynamicLibrary] Persisting item: {Name} ({Type})", item.Name, item.Type);

        string? createdPath = null;

        try
        {
            // Handle based on item type
            if (item.Type == BaseItemKind.Movie)
            {
                createdPath = await _persistenceService.PersistMovieAsync(item, cancellationToken);
            }
            else if (item.Type == BaseItemKind.Series)
            {
                createdPath = await _persistenceService.PersistSeriesAsync(item, cancellationToken);
            }
            else
            {
                _logger.LogWarning("[DynamicLibrary] Cannot persist item type: {Type}", item.Type);
                return BadRequest(new { Error = $"Cannot persist item type: {item.Type}" });
            }

            if (createdPath == null)
            {
                _logger.LogDebug("[DynamicLibrary] Item already exists in library or could not be created: {Name}", item.Name);
                return Ok(new { Message = "Item already exists in library", AlreadyExists = true });
            }

            // Trigger library scan if configured
            _persistenceService.TriggerLibraryScan();

            _logger.LogInformation("[DynamicLibrary] Successfully persisted: {Name} to {Path}", item.Name, createdPath);
            return Ok(new { Message = "Item persisted successfully", Path = createdPath });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error persisting item: {Name}", item.Name);
            return BadRequest(new { Error = $"Failed to persist item: {ex.Message}" });
        }
    }
}
