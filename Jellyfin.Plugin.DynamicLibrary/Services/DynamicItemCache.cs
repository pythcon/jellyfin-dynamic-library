using System.Linq;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

/// <summary>
/// Cache for storing dynamic item data and image URLs.
/// This allows us to serve item details and images for virtual items
/// that don't exist in Jellyfin's database.
/// </summary>
public class DynamicItemCache
{
    private readonly IMemoryCache _cache;
    private readonly ILogger<DynamicItemCache> _logger;
    private readonly TimeSpan _cacheDuration = TimeSpan.FromHours(1);

    private const string ItemPrefix = "dynamic_item:";
    private const string ImagePrefix = "dynamic_image:";
    private const string SeasonsPrefix = "dynamic_seasons:";
    private const string EpisodesPrefix = "dynamic_episodes:";
    private const string EmbedarrAddedPrefix = "embedarr_added:";
    private const string MediaSourcePrefix = "mediasource_to_episode:";

    public DynamicItemCache(IMemoryCache cache, ILogger<DynamicItemCache> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Store a dynamic item in the cache.
    /// </summary>
    public void StoreItem(BaseItemDto item, string? imageUrl = null)
    {
        if (item.Id == Guid.Empty)
        {
            _logger.LogWarning("[DynamicItemCache] Attempted to store item with empty GUID");
            return;
        }

        var itemKey = $"{ItemPrefix}{item.Id}";
        _cache.Set(itemKey, item, _cacheDuration);

        if (!string.IsNullOrEmpty(imageUrl))
        {
            var imageKey = $"{ImagePrefix}{item.Id}";
            _cache.Set(imageKey, imageUrl, _cacheDuration);
        }

        _logger.LogDebug("[DynamicItemCache] Stored item: {Name} ({Id}), HasImage: {HasImage}",
            item.Name, item.Id, !string.IsNullOrEmpty(imageUrl));
    }

    /// <summary>
    /// Get a dynamic item from the cache.
    /// </summary>
    public BaseItemDto? GetItem(Guid itemId)
    {
        var key = $"{ItemPrefix}{itemId}";
        return _cache.TryGetValue<BaseItemDto>(key, out var item) ? item : null;
    }

    /// <summary>
    /// Get the image URL for a dynamic item.
    /// </summary>
    public string? GetImageUrl(Guid itemId)
    {
        var key = $"{ImagePrefix}{itemId}";
        return _cache.TryGetValue<string>(key, out var url) ? url : null;
    }

    /// <summary>
    /// Check if an item exists in the cache.
    /// </summary>
    public bool HasItem(Guid itemId)
    {
        var key = $"{ItemPrefix}{itemId}";
        return _cache.TryGetValue<BaseItemDto>(key, out _);
    }

    /// <summary>
    /// Store seasons for a series.
    /// </summary>
    public void StoreSeasonsForSeries(Guid seriesId, List<BaseItemDto> seasons)
    {
        var key = $"{SeasonsPrefix}{seriesId}";
        _cache.Set(key, seasons, _cacheDuration);

        // Also store each season individually
        foreach (var season in seasons)
        {
            StoreItem(season);
        }

        _logger.LogDebug("[DynamicItemCache] Stored {Count} seasons for series {SeriesId}",
            seasons.Count, seriesId);
    }

    /// <summary>
    /// Get seasons for a series.
    /// </summary>
    public List<BaseItemDto>? GetSeasonsForSeries(Guid seriesId)
    {
        var key = $"{SeasonsPrefix}{seriesId}";
        return _cache.TryGetValue<List<BaseItemDto>>(key, out var seasons) ? seasons : null;
    }

    /// <summary>
    /// Store episodes for a series.
    /// </summary>
    public void StoreEpisodesForSeries(Guid seriesId, List<BaseItemDto> episodes)
    {
        var key = $"{EpisodesPrefix}{seriesId}";
        _cache.Set(key, episodes, _cacheDuration);

        // Also store each episode individually
        foreach (var episode in episodes)
        {
            StoreItem(episode);
        }

        _logger.LogDebug("[DynamicItemCache] Stored {Count} episodes for series {SeriesId}",
            episodes.Count, seriesId);
    }

    /// <summary>
    /// Get all episodes for a series.
    /// </summary>
    public List<BaseItemDto>? GetEpisodesForSeries(Guid seriesId)
    {
        var key = $"{EpisodesPrefix}{seriesId}";
        return _cache.TryGetValue<List<BaseItemDto>>(key, out var episodes) ? episodes : null;
    }

    /// <summary>
    /// Get episodes for a specific season of a series.
    /// </summary>
    public List<BaseItemDto>? GetEpisodesForSeason(Guid seriesId, int seasonNumber)
    {
        var episodes = GetEpisodesForSeries(seriesId);
        return episodes?.Where(e => e.ParentIndexNumber == seasonNumber).ToList();
    }

    /// <summary>
    /// Mark an item as having been added to Embedarr.
    /// </summary>
    public void MarkAddedToEmbedarr(Guid itemId)
    {
        var key = $"{EmbedarrAddedPrefix}{itemId}";
        _cache.Set(key, true, TimeSpan.FromHours(24)); // Longer duration since files persist
        _logger.LogDebug("[DynamicItemCache] Marked item {ItemId} as added to Embedarr", itemId);
    }

    /// <summary>
    /// Check if an item has been added to Embedarr.
    /// </summary>
    public bool IsAddedToEmbedarr(Guid itemId)
    {
        var key = $"{EmbedarrAddedPrefix}{itemId}";
        return _cache.TryGetValue<bool>(key, out var added) && added;
    }

    /// <summary>
    /// Store a mapping from MediaSource ID to Episode ID.
    /// This allows us to resolve MediaSource IDs when the client tries to fetch them as items.
    /// </summary>
    public void StoreMediaSourceMapping(string mediaSourceId, Guid episodeId)
    {
        var key = $"{MediaSourcePrefix}{mediaSourceId}";
        _cache.Set(key, episodeId, _cacheDuration);
        _logger.LogDebug("[DynamicItemCache] Stored MediaSource mapping: {MediaSourceId} -> {EpisodeId}",
            mediaSourceId, episodeId);
    }

    /// <summary>
    /// Get the Episode ID for a MediaSource ID.
    /// Returns null if the MediaSource ID is not in the cache.
    /// </summary>
    public Guid? GetEpisodeIdForMediaSource(string mediaSourceId)
    {
        var key = $"{MediaSourcePrefix}{mediaSourceId}";
        return _cache.TryGetValue<Guid>(key, out var episodeId) ? episodeId : null;
    }
}
