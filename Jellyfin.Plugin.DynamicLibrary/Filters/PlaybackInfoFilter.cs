using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Jellyfin.Plugin.DynamicLibrary.Services;
using MediaBrowser.Model.Dlna;
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
    private readonly SubtitleService _subtitleService;
    private readonly ILogger<PlaybackInfoFilter> _logger;

    private static readonly HashSet<string> PlaybackInfoActions = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetPlaybackInfo",
        "GetPostedPlaybackInfo"
    };

    public PlaybackInfoFilter(
        DynamicItemCache itemCache,
        IEmbedarrClient embedarrClient,
        SubtitleService subtitleService,
        ILogger<PlaybackInfoFilter> logger)
    {
        _itemCache = itemCache;
        _embedarrClient = embedarrClient;
        _subtitleService = subtitleService;
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

        var config = Config;

        // Extract selected mediaSourceId from request (query string or body)
        var selectedMediaSourceId = GetSelectedMediaSourceId(context);
        if (!string.IsNullOrEmpty(selectedMediaSourceId))
        {
            _logger.LogDebug("[DynamicLibrary] Selected mediaSourceId: {MediaSourceId}", selectedMediaSourceId);
        }

        // Handle Direct mode with potential multi-URL support for anime versions
        if (config.StreamProvider == StreamProvider.Direct)
        {
            var streamUrls = BuildDirectStreamUrls(cachedItem);

            if (streamUrls.Count == 0)
            {
                _logger.LogWarning("[DynamicLibrary] No stream URLs available for {Name}, playback may fail", cachedItem.Name);
                await next();
                return;
            }

            // Fetch subtitles if enabled
            List<CachedSubtitle>? subtitles = null;
            if (_subtitleService.IsEnabled)
            {
                subtitles = await FetchSubtitlesAsync(cachedItem, context.HttpContext.RequestAborted);
                _logger.LogDebug("[DynamicLibrary] Fetched {Count} subtitles for {Name}", subtitles?.Count ?? 0, cachedItem.Name);
            }

            // If a specific mediaSource was selected, filter to just that one
            if (!string.IsNullOrEmpty(selectedMediaSourceId) && streamUrls.Count > 1)
            {
                var selectedUrl = FindSelectedStreamUrl(streamUrls, selectedMediaSourceId, cachedItem.Id);
                if (selectedUrl.HasValue)
                {
                    _logger.LogWarning("[DynamicLibrary] PlaybackInfo: Matched version '{AudioType}' for {Name}, URL={Url}",
                        selectedUrl.Value.AudioType, cachedItem.Name, selectedUrl.Value.Url);
                    var response = BuildPlaybackInfoResponse(cachedItem, new List<(string, string)> { selectedUrl.Value }, subtitles);
                    context.Result = new OkObjectResult(response);
                    return;
                }
                _logger.LogWarning("[DynamicLibrary] Selected mediaSourceId '{Id}' not found, returning all sources", selectedMediaSourceId);
            }

            _logger.LogDebug("[DynamicLibrary] Got {Count} stream URL(s) for {Name}", streamUrls.Count, cachedItem.Name);

            var allSourcesResponse = BuildPlaybackInfoResponse(cachedItem, streamUrls, subtitles);
            context.Result = new OkObjectResult(allSourcesResponse);
            return;
        }

        // For Embedarr or other providers, use single URL method
        var streamUrl = await GetStreamUrlAsync(cachedItem, context.HttpContext.RequestAborted);

        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogWarning("[DynamicLibrary] No stream URL available for {Name}, playback may fail", cachedItem.Name);
            await next();
            return;
        }

        _logger.LogDebug("[DynamicLibrary] Got stream URL for {Name}: {Url}", cachedItem.Name, streamUrl);

        var response2 = BuildPlaybackInfoResponse(cachedItem, streamUrl);
        context.Result = new OkObjectResult(response2);
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
    /// Build stream URLs for Direct mode. Returns multiple URLs for anime with audio versions enabled.
    /// </summary>
    private List<(string Url, string AudioType)> BuildDirectStreamUrls(BaseItemDto item)
    {
        var config = Config;
        var urls = new List<(string Url, string AudioType)>();

        if (item.Type == BaseItemKind.Movie)
        {
            var template = config.DirectMovieUrlTemplate;
            if (!string.IsNullOrEmpty(template))
            {
                var url = ReplacePlaceholders(template, item, null, null);
                urls.Add((url, string.Empty));
            }
            return urls;
        }

        if (item.Type == BaseItemKind.Episode)
        {
            var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;
            var isAnime = series != null && DynamicLibraryService.IsAnime(series);

            var template = isAnime ? config.DirectAnimeUrlTemplate : config.DirectTvUrlTemplate;
            if (string.IsNullOrEmpty(template))
            {
                _logger.LogWarning("[DynamicLibrary] Direct {Type} URL template is not configured",
                    isAnime ? "anime" : "TV");
                return urls;
            }

            var season = item.ParentIndexNumber ?? 1;
            var episode = item.IndexNumber ?? 1;

            // For anime with audio versions enabled, return multiple URLs (one per track)
            if (isAnime && config.EnableAnimeAudioVersions)
            {
                var tracks = config.AnimeAudioTracks?
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (tracks == null || tracks.Length == 0)
                {
                    tracks = new[] { "sub" };
                }

                _logger.LogDebug("[DynamicLibrary] Building {Count} audio versions for anime: {Tracks}",
                    tracks.Length, string.Join(", ", tracks));

                foreach (var track in tracks)
                {
                    var url = ReplacePlaceholders(template, item, season, episode, series, track);
                    urls.Add((url, track));
                }
            }
            else
            {
                // Single URL for non-anime or when audio versions disabled
                var url = ReplacePlaceholders(template, item, season, episode, series, null);
                urls.Add((url, string.Empty));
            }
        }

        return urls;
    }

    /// <summary>
    /// Build a stream URL from templates for Direct mode (single URL).
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

    /// <summary>
    /// Fetch subtitles for an item based on its type.
    /// </summary>
    private async Task<List<CachedSubtitle>> FetchSubtitlesAsync(BaseItemDto item, CancellationToken cancellationToken)
    {
        try
        {
            if (item.Type == BaseItemKind.Movie)
            {
                return await _subtitleService.FetchMovieSubtitlesAsync(item, cancellationToken);
            }

            if (item.Type == BaseItemKind.Episode)
            {
                var series = item.SeriesId.HasValue ? _itemCache.GetItem(item.SeriesId.Value) : null;
                return await _subtitleService.FetchEpisodeSubtitlesAsync(item, series, cancellationToken);
            }

            return new List<CachedSubtitle>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching subtitles for {Name}", item.Name);
            return new List<CachedSubtitle>();
        }
    }

    /// <summary>
    /// Build subtitle MediaStreams from cached subtitles.
    /// Uses Jellyfin's standard subtitle URL format so our SubtitleFilter can intercept.
    /// </summary>
    private List<MediaStream> BuildSubtitleMediaStreams(Guid itemId, string mediaSourceId, List<CachedSubtitle>? subtitles)
    {
        var mediaStreams = new List<MediaStream>();

        if (subtitles == null || subtitles.Count == 0)
        {
            return mediaStreams;
        }

        var subtitleIndex = 0;

        foreach (var subtitle in subtitles)
        {
            // Use our custom DynamicLibrary endpoint for subtitles
            var deliveryUrl = $"/DynamicLibrary/Subtitles/{itemId}/{subtitle.LanguageCode}.vtt";

            _logger.LogDebug("[DynamicLibrary] Building subtitle stream: Index={Index}, Language={Language}",
                subtitleIndex, subtitle.LanguageCode);

            mediaStreams.Add(new MediaStream
            {
                Type = MediaStreamType.Subtitle,
                Index = subtitleIndex++,
                Codec = "webvtt",
                Language = subtitle.LanguageCode,
                Title = subtitle.Language + (subtitle.HearingImpaired ? " [CC]" : ""),
                IsExternal = true,
                SupportsExternalStream = true,
                DeliveryMethod = SubtitleDeliveryMethod.External,
                DeliveryUrl = deliveryUrl
            });
        }

        _logger.LogDebug("[DynamicLibrary] Built {Count} subtitle streams for item {ItemId}",
            mediaStreams.Count, itemId);

        return mediaStreams;
    }

    /// <summary>
    /// Build PlaybackInfoResponse with multiple MediaSources for version selection.
    /// </summary>
    private PlaybackInfoResponse BuildPlaybackInfoResponse(BaseItemDto item, List<(string Url, string AudioType)> streamUrls, List<CachedSubtitle>? subtitles = null)
    {
        var mediaSources = streamUrls.Select(s =>
        {
            // Generate same ID as SearchResultFactory for consistency
            var sourceId = GenerateMediaSourceGuid(item.Id, s.AudioType);

            // Build subtitle streams with the correct mediaSourceId for this source
            var subtitleStreams = BuildSubtitleMediaStreams(item.Id, sourceId, subtitles);

            // Display name for version selector
            var displayName = string.IsNullOrEmpty(s.AudioType)
                ? item.Name
                : s.AudioType.ToUpperInvariant();

            return new MediaSourceInfo
            {
                // Required identification
                Id = sourceId,
                Name = displayName,
                Path = s.Url,

                // Protocol settings for remote HLS
                Protocol = MediaProtocol.Http,
                Type = MediaSourceType.Default,
                Container = "hls",

                // Remote stream settings
                IsRemote = true,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = false,
                SupportsProbing = false,
                RequiresOpening = false,
                RequiresClosing = false,

                // Runtime from item
                RunTimeTicks = item.RunTimeTicks,

                // Include subtitle streams (or empty list if none)
                MediaStreams = subtitleStreams.Count > 0 ? subtitleStreams : new List<MediaStream>()
            };
        }).ToArray();

        _logger.LogDebug("[DynamicLibrary] Built PlaybackInfoResponse with {Count} media sources",
            mediaSources.Length);

        return new PlaybackInfoResponse
        {
            MediaSources = mediaSources,
            PlaySessionId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>
    /// Build PlaybackInfoResponse with a single MediaSource.
    /// </summary>
    private PlaybackInfoResponse BuildPlaybackInfoResponse(BaseItemDto item, string streamUrl)
    {
        return BuildPlaybackInfoResponse(item, new List<(string, string)> { (streamUrl, string.Empty) }, null);
    }

    /// <summary>
    /// Extract the selected mediaSourceId from the request (query string or body).
    /// </summary>
    private string? GetSelectedMediaSourceId(ActionExecutingContext context)
    {
        // Try query string first
        if (context.HttpContext.Request.Query.TryGetValue("mediaSourceId", out var queryValue) &&
            !string.IsNullOrEmpty(queryValue))
        {
            return queryValue.ToString();
        }

        // Try from action arguments (GetPostedPlaybackInfo has a playbackInfoDto parameter)
        if (context.ActionArguments.TryGetValue("playbackInfoDto", out var playbackInfoObj) &&
            playbackInfoObj != null)
        {
            // Use reflection to get MediaSourceId property
            var mediaSourceIdProp = playbackInfoObj.GetType().GetProperty("MediaSourceId");
            var value = mediaSourceIdProp?.GetValue(playbackInfoObj)?.ToString();
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Find the stream URL matching the selected mediaSourceId.
    /// </summary>
    private (string Url, string AudioType)? FindSelectedStreamUrl(
        List<(string Url, string AudioType)> streamUrls,
        string selectedMediaSourceId,
        Guid itemId)
    {
        foreach (var stream in streamUrls)
        {
            // Generate the same ID that SearchResultFactory uses
            var sourceId = GenerateMediaSourceGuid(itemId, stream.AudioType);

            if (sourceId.Equals(selectedMediaSourceId, StringComparison.OrdinalIgnoreCase))
            {
                return stream;
            }
        }

        return null;
    }

    /// <summary>
    /// Generate a deterministic GUID for a MediaSource based on item ID and audio track.
    /// Must match the implementation in SearchResultFactory.
    /// </summary>
    private static string GenerateMediaSourceGuid(Guid itemId, string audioTrack)
    {
        if (string.IsNullOrEmpty(audioTrack))
        {
            return itemId.ToString("N");
        }

        var input = $"{itemId:N}:{audioTrack.ToLowerInvariant()}";
        using var md5 = System.Security.Cryptography.MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return new Guid(hash).ToString("N");
    }
}
