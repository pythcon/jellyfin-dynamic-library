using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Filters;

/// <summary>
/// Filter that intercepts playback info requests for dynamic items
/// and returns media source info with stream URL from Embedarr.
/// </summary>
public class PlaybackInfoFilter : IAsyncActionFilter, IOrderedFilter
{
    private readonly DynamicItemCache _itemCache;
    private readonly IEmbedarrClient _embedarrClient;
    private readonly ILogger<PlaybackInfoFilter> _logger;

    private static readonly HashSet<string> PlaybackInfoActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetPlaybackInfo",
        "GetPostedPlaybackInfo"
    };

    public PlaybackInfoFilter(
        DynamicItemCache itemCache,
        IEmbedarrClient embedarrClient,
        ILogger<PlaybackInfoFilter> logger)
    {
        _itemCache = itemCache;
        _embedarrClient = embedarrClient;
        _logger = logger;
    }

    // Run early to intercept before Jellyfin tries to look up the item
    public int Order => 0;

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var actionName = (context.ActionDescriptor as ControllerActionDescriptor)?.ActionName;
        var controllerName = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerName;

        // Check if this is a playback info action on MediaInfo controller
        if (controllerName != "MediaInfo" || actionName == null || !PlaybackInfoActions.Contains(actionName))
        {
            await next();
            return;
        }

        // Get item ID from route
        if (!context.ActionArguments.TryGetValue("itemId", out var itemIdObj) || itemIdObj is not Guid itemId)
        {
            await next();
            return;
        }

        // Check if this is a dynamic item
        var cachedItem = _itemCache.GetItem(itemId);
        if (cachedItem == null)
        {
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Intercepting PlaybackInfo for dynamic item: {Name} ({Id})",
            cachedItem.Name, itemId);

        // Get stream URL from Embedarr or Direct
        var streamUrl = await GetStreamUrlAsync(cachedItem, context.HttpContext.RequestAborted);

        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogWarning("[DynamicLibrary] No stream URL available for {Name}, playback may fail", cachedItem.Name);
            // Let Jellyfin handle it - will likely fail but at least gives an error
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Got stream URL for {Name}: {Url}", cachedItem.Name, streamUrl);

        // Build PlaybackInfoResponse with the stream URL
        var response = BuildPlaybackInfoResponse(cachedItem, streamUrl);

        context.Result = new OkObjectResult(response);
    }

    /// <summary>
    /// Get the stream URL based on configured stream provider.
    /// </summary>
    private async Task<string?> GetStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        var config = Config;

        switch (config.StreamProvider)
        {
            case StreamProvider.None:
                _logger.LogDebug("[DynamicLibrary] Stream provider is None, no streaming available");
                return null;

            case StreamProvider.Embedarr:
                return await GetEmbedarrStreamUrlAsync(item, cancellationToken);

            case StreamProvider.Direct:
                return BuildDirectStreamUrl(item);

            default:
                _logger.LogWarning("[DynamicLibrary] Unknown stream provider: {Provider}", config.StreamProvider);
                return null;
        }
    }

    /// <summary>
    /// Get the stream URL from Embedarr based on item type and configured ID preferences.
    /// </summary>
    private async Task<string?> GetEmbedarrStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        if (item.Type == BaseItemKind.Movie)
        {
            return await GetMovieStreamUrlAsync(item, cancellationToken);
        }

        if (item.Type == BaseItemKind.Episode)
        {
            return await GetEpisodeStreamUrlAsync(item, cancellationToken);
        }

        _logger.LogWarning("[DynamicLibrary] Unsupported item type for playback: {Type}", item.Type);
        return null;
    }

    /// <summary>
    /// Build a stream URL from templates for Direct mode.
    /// </summary>
    private string? BuildDirectStreamUrl(BaseItemDto item)
    {
        var config = Config;
        string template;

        if (item.Type == BaseItemKind.Movie)
        {
            template = config.DirectMovieUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogWarning("[DynamicLibrary] Direct movie URL template is not configured");
                return null;
            }

            return ReplacePlaceholders(template, item, null, null);
        }

        if (item.Type == BaseItemKind.Episode)
        {
            // Get the series to determine if it's anime
            var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;
            var isAnime = series != null && DynamicLibraryService.IsAnime(series);

            template = isAnime ? config.DirectAnimeUrlTemplate : config.DirectTvUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogWarning("[DynamicLibrary] Direct {Type} URL template is not configured",
                    isAnime ? "anime" : "TV");
                return null;
            }

            var season = item.ParentIndexNumber ?? 1;
            var episode = item.IndexNumber ?? 1;

            // For anime with audio selection enabled, use the first configured audio track
            string? audioType = null;
            if (isAnime && config.EnableAnimeAudioVersions)
            {
                var tracks = config.AnimeAudioTracks?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                audioType = tracks?.Length > 0 ? tracks[0] : "sub";
                _logger.LogDebug("[DynamicLibrary] Using audio track '{AudioType}' for anime episode", audioType);
            }

            return ReplacePlaceholders(template, item, season, episode, series, audioType);
        }

        _logger.LogWarning("[DynamicLibrary] Unsupported item type for Direct playback: {Type}", item.Type);
        return null;
    }

    /// <summary>
    /// Replace placeholders in URL template with actual values.
    /// </summary>
    /// <param name="template">URL template with placeholders.</param>
    /// <param name="item">The item (movie or episode).</param>
    /// <param name="season">Season number for episodes.</param>
    /// <param name="episode">Episode number for episodes.</param>
    /// <param name="series">Series for episodes (optional).</param>
    /// <param name="audioType">Audio type for anime ("sub" or "dub"), null for default.</param>
    private string ReplacePlaceholders(string template, BaseItemDto item, int? season, int? episode, BaseItemDto? series = null, string? audioType = null)
    {
        var config = Config;
        var providerIds = item.ProviderIds ?? new Dictionary<string, string>();

        // For episodes, prefer series provider IDs for the main ID
        var seriesProviderIds = series?.ProviderIds ?? providerIds;

        // Get preferred ID based on item type and config
        string? preferredId = null;
        if (item.Type == BaseItemKind.Movie)
        {
            var (id, _) = GetMovieId(item, config.MoviePreferredId);
            preferredId = id;
        }
        else if (item.Type == BaseItemKind.Episode)
        {
            var isAnime = series != null && DynamicLibraryService.IsAnime(series);
            var preference = isAnime ? config.AnimePreferredId : config.TvShowPreferredId;
            var (id, _) = GetSeriesId(item, series, preference);
            preferredId = id;
        }

        // Replace placeholders
        var absoluteEpisode = providerIds.GetValueOrDefault("AbsoluteNumber", "");
        var url = template
            .Replace("{id}", preferredId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{imdb}", seriesProviderIds.GetValueOrDefault("Imdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tmdb}", providerIds.GetValueOrDefault("Tmdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tvdb}", seriesProviderIds.GetValueOrDefault("Tvdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{anilist}", seriesProviderIds.GetValueOrDefault("AniList", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", season?.ToString() ?? "1", StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", episode?.ToString() ?? "1", StringComparison.OrdinalIgnoreCase)
            .Replace("{absolute}", absoluteEpisode, StringComparison.OrdinalIgnoreCase)
            .Replace("{audio}", audioType ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", Uri.EscapeDataString(item.Name ?? ""), StringComparison.OrdinalIgnoreCase);

        _logger.LogDebug("[DynamicLibrary] Direct URL built: {Url}", url);
        return url;
    }

    /// <summary>
    /// Get stream URL for a movie using configured ID preference.
    /// </summary>
    private async Task<string?> GetMovieStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        var config = Config;
        var preferredId = config.MoviePreferredId;

        _logger.LogDebug("[DynamicLibrary] Movie playback: {Name}, PreferredId={PreferredId}, ProviderIds={ProviderIds}",
            item.Name, preferredId,
            item.ProviderIds != null ? string.Join(", ", item.ProviderIds.Select(p => $"{p.Key}={p.Value}")) : "null");

        // Get the ID based on preference with fallback
        var (id, idType) = GetMovieId(item, preferredId);

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogWarning("[DynamicLibrary] No suitable ID found for movie: {Name}", item.Name);
            return null;
        }

        _logger.LogDebug("[DynamicLibrary] Using {IdType} ID for movie {Name}: {Id}", idType, item.Name, id);

        var result = await _embedarrClient.GetMovieStreamUrlAsync(id, cancellationToken);
        return result?.Url;
    }

    /// <summary>
    /// Get stream URL for an episode using configured ID preference.
    /// </summary>
    private async Task<string?> GetEpisodeStreamUrlAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        var config = Config;

        // Get the series to determine if it's anime
        var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;
        var isAnime = series != null && DynamicLibraryService.IsAnime(series);

        // Use anime or TV show preference based on content type
        var preferredId = isAnime ? config.AnimePreferredId : config.TvShowPreferredId;

        _logger.LogDebug("[DynamicLibrary] Episode playback: {Name}, IsAnime={IsAnime}, PreferredId={PreferredId}",
            item.Name, isAnime, preferredId);
        _logger.LogDebug("[DynamicLibrary] Episode ProviderIds: {ProviderIds}",
            item.ProviderIds != null ? string.Join(", ", item.ProviderIds.Select(p => $"{p.Key}={p.Value}")) : "null");
        _logger.LogDebug("[DynamicLibrary] Series: {SeriesName}, ProviderIds={ProviderIds}",
            series?.Name ?? "null",
            series?.ProviderIds != null ? string.Join(", ", series.ProviderIds.Select(p => $"{p.Key}={p.Value}")) : "null");

        // Get the ID based on preference with fallback
        var (id, idType) = GetSeriesId(item, series, preferredId);

        if (string.IsNullOrEmpty(id))
        {
            _logger.LogWarning("[DynamicLibrary] No suitable ID found for episode: {Name} (isAnime={IsAnime})", item.Name, isAnime);
            return null;
        }

        var season = item.ParentIndexNumber ?? 1;
        var episode = item.IndexNumber ?? 1;

        // Warn if using fallback values
        if (!item.ParentIndexNumber.HasValue)
        {
            _logger.LogWarning("[DynamicLibrary] Episode {Name} has no season number, defaulting to 1", item.Name);
        }
        if (!item.IndexNumber.HasValue)
        {
            _logger.LogWarning("[DynamicLibrary] Episode {Name} has no episode number, defaulting to 1", item.Name);
        }

        _logger.LogDebug("[DynamicLibrary] Calling Embedarr: GET /api/url/tv/{Id}/{Season}/{Episode} (IdType={IdType}, IsAnime={IsAnime})",
            id, season, episode, idType, isAnime);

        var result = await _embedarrClient.GetTvEpisodeStreamUrlAsync(id, season, episode, cancellationToken);

        _logger.LogDebug("[DynamicLibrary] Embedarr response: Url={Url}", result?.Url ?? "null");

        return result?.Url;
    }

    /// <summary>
    /// Get the movie ID based on preference with fallback.
    /// Movies can have: IMDB, TMDB, TVDB
    /// </summary>
    private (string? Id, string IdType) GetMovieId(BaseItemDto item, PreferredProviderId preference)
    {
        if (item.ProviderIds == null)
        {
            return (null, "none");
        }

        // Try preferred ID first, then fall back to others
        var fallbackOrder = preference switch
        {
            PreferredProviderId.Imdb => new[] { "Imdb", "Tmdb", "Tvdb" },
            PreferredProviderId.Tmdb => new[] { "Tmdb", "Imdb", "Tvdb" },
            PreferredProviderId.Tvdb => new[] { "Tvdb", "Imdb", "Tmdb" },
            _ => new[] { "Imdb", "Tmdb", "Tvdb" }
        };

        foreach (var provider in fallbackOrder)
        {
            if (item.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id))
            {
                return (id, provider.ToUpperInvariant());
            }
        }

        return (null, "none");
    }

    /// <summary>
    /// Get the series ID for an episode based on preference with fallback.
    /// TV/Anime can have: IMDB, TVDB, TMDB
    /// </summary>
    private (string? Id, string IdType) GetSeriesId(BaseItemDto episode, BaseItemDto? series, PreferredProviderId preference)
    {
        // Try to get IDs from series first (preferred), then from episode
        var providerIds = series?.ProviderIds ?? episode.ProviderIds;

        if (providerIds == null)
        {
            return (null, "none");
        }

        // Try preferred ID first, then fall back to others
        var fallbackOrder = preference switch
        {
            PreferredProviderId.Imdb => new[] { "Imdb", "Tvdb", "AniList", "Tmdb" },
            PreferredProviderId.Tvdb => new[] { "Tvdb", "Imdb", "AniList", "Tmdb" },
            PreferredProviderId.Tmdb => new[] { "Tmdb", "Imdb", "Tvdb", "AniList" },
            PreferredProviderId.AniList => new[] { "AniList", "Tvdb", "Imdb", "Tmdb" },
            _ => new[] { "Imdb", "Tvdb", "AniList", "Tmdb" }
        };

        foreach (var provider in fallbackOrder)
        {
            if (providerIds.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id))
            {
                return (id, provider.ToUpperInvariant());
            }
        }

        return (null, "none");
    }

    private PlaybackInfoResponse BuildPlaybackInfoResponse(BaseItemDto item, string streamUrl)
    {
        var mediaSourceId = item.Id.ToString("N"); // GUID without hyphens

        return new PlaybackInfoResponse
        {
            MediaSources = new[]
            {
                new MediaSourceInfo
                {
                    Id = mediaSourceId,
                    Path = streamUrl,
                    Name = item.Name,
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "hls",
                    IsRemote = true,
                    SupportsDirectPlay = true,
                    SupportsDirectStream = true,
                    SupportsTranscoding = false,
                    SupportsProbing = false,
                    RequiresOpening = false,
                    RequiresClosing = false,
                    RunTimeTicks = item.RunTimeTicks,
                    MediaStreams = new List<MediaStream>()
                }
            },
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }
}
