using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

public class DynamicLibraryService
{
    private readonly ITvdbClient _tvdbClient;
    private readonly ITmdbClient _tmdbClient;
    private readonly IEmbedarrClient _embedarrClient;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DynamicLibraryService> _logger;

    public DynamicLibraryService(
        ITvdbClient tvdbClient,
        ITmdbClient tmdbClient,
        IEmbedarrClient embedarrClient,
        SearchResultFactory searchResultFactory,
        ILibraryManager libraryManager,
        IMemoryCache cache,
        ILogger<DynamicLibraryService> logger)
    {
        _tvdbClient = tvdbClient;
        _tmdbClient = tmdbClient;
        _embedarrClient = embedarrClient;
        _searchResultFactory = searchResultFactory;
        _libraryManager = libraryManager;
        _cache = cache;
        _logger = logger;
    }

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    public bool IsEnabled => Config.Enabled && (_tvdbClient.IsConfigured || _tmdbClient.IsConfigured);

    /// <summary>
    /// Log the current status of the service for debugging.
    /// </summary>
    public void LogStatus(ILogger logger)
    {
        var config = Config;
        var pluginInstance = DynamicLibraryPlugin.Instance;
        logger.LogWarning(
            "[DynamicLibrary] Status: PluginInstance={Instance}, ConfigEnabled={Enabled}, TvdbConfigured={Tvdb}, TmdbConfigured={Tmdb}",
            pluginInstance != null ? "exists" : "NULL",
            config.Enabled,
            _tvdbClient.IsConfigured,
            _tmdbClient.IsConfigured);
    }

    /// <summary>
    /// Search for content across configured APIs.
    /// </summary>
    public async Task<List<BaseItemDto>> SearchAsync(
        string query,
        HashSet<BaseItemKind> requestedTypes,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            _logger.LogDebug("[DynamicLibrary] Service is not enabled");
            return new List<BaseItemDto>();
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<BaseItemDto>();
        }

        var cacheKey = $"search:{query}:{string.Join(",", requestedTypes.Order())}";

        // Check cache first
        if (_cache.TryGetValue<List<BaseItemDto>>(cacheKey, out var cachedResults) && cachedResults != null)
        {
            _logger.LogDebug("Returning cached search results for: {Query}", query);
            return cachedResults;
        }

        var tasks = new List<Task<IEnumerable<BaseItemDto>>>();
        var config = Config;
        var maxResults = config.MaxSearchResults;

        // Search movies via TMDB
        if (requestedTypes.Contains(BaseItemKind.Movie) && config.SearchMovies && _tmdbClient.IsConfigured)
        {
            tasks.Add(SearchMoviesAsync(query, maxResults, cancellationToken));
        }

        // Search TV shows via TVDB
        if (requestedTypes.Contains(BaseItemKind.Series) && config.SearchTvShows && _tvdbClient.IsConfigured)
        {
            tasks.Add(SearchSeriesAsync(query, maxResults, cancellationToken));
        }

        var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();

        // Cache results
        var cacheDuration = TimeSpan.FromMinutes(config.CacheTtlMinutes);
        _cache.Set(cacheKey, results, cacheDuration);

        _logger.LogDebug("[DynamicLibrary] Search for '{Query}' returned {Count} results", query, results.Count);
        return results;
    }

    private async Task<IEnumerable<BaseItemDto>> SearchMoviesAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        try
        {
            var movies = await _tmdbClient.SearchMoviesAsync(query, cancellationToken);
            var dtos = new List<BaseItemDto>();

            // Get existing TMDB IDs from the library
            var existingTmdbIds = GetExistingProviderIds("Tmdb", typeof(MediaBrowser.Controller.Entities.Movies.Movie));

            // Sort by popularity (most popular first), filter out existing items
            foreach (var movie in movies.OrderByDescending(m => m.Popularity).Take(maxResults * 2)) // Take extra to account for filtering
            {
                // Skip if this movie already exists in the library
                if (existingTmdbIds.Contains(movie.Id.ToString()))
                {
                    _logger.LogDebug("[DynamicLibrary] Skipping movie '{Title}' (TMDB:{Id}) - already in library", movie.Title, movie.Id);
                    continue;
                }

                var dto = await _searchResultFactory.CreateMovieDtoAsync(movie, cancellationToken);
                dtos.Add(dto);

                if (dtos.Count >= maxResults)
                {
                    break;
                }
            }

            return dtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching movies for: {Query}", query);
            return Enumerable.Empty<BaseItemDto>();
        }
    }

    private async Task<IEnumerable<BaseItemDto>> SearchSeriesAsync(string query, int maxResults, CancellationToken cancellationToken)
    {
        try
        {
            var series = await _tvdbClient.SearchSeriesAsync(query, cancellationToken);

            // Get existing TVDB IDs from the library
            var existingTvdbIds = GetExistingProviderIds("Tvdb", typeof(MediaBrowser.Controller.Entities.TV.Series));

            var dtos = new List<BaseItemDto>();
            foreach (var s in series.Take(maxResults * 2)) // Take extra to account for filtering
            {
                // Skip if this series already exists in the library
                if (existingTvdbIds.Contains(s.TvdbId))
                {
                    _logger.LogDebug("[DynamicLibrary] Skipping series '{Name}' (TVDB:{Id}) - already in library", s.Name, s.TvdbId);
                    continue;
                }

                dtos.Add(_searchResultFactory.CreateSeriesDto(s));

                if (dtos.Count >= maxResults)
                {
                    break;
                }
            }

            return dtos;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching series for: {Query}", query);
            return Enumerable.Empty<BaseItemDto>();
        }
    }

    /// <summary>
    /// Get all provider IDs of a specific type from existing library items.
    /// </summary>
    private HashSet<string> GetExistingProviderIds(string providerName, Type itemType)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = itemType == typeof(MediaBrowser.Controller.Entities.Movies.Movie)
                    ? new[] { BaseItemKind.Movie }
                    : new[] { BaseItemKind.Series },
                Recursive = true
            };

            var items = _libraryManager.GetItemList(query);

            foreach (var item in items)
            {
                if (item.TryGetProviderId(providerName, out var providerId) && !string.IsNullOrEmpty(providerId))
                {
                    ids.Add(providerId);
                }
            }

            _logger.LogDebug("[DynamicLibrary] Found {Count} existing {Provider} IDs in library", ids.Count, providerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[DynamicLibrary] Error getting existing provider IDs for {Provider}", providerName);
        }

        return ids;
    }

    /// <summary>
    /// Trigger Embedarr to create STRM files for a dynamic item.
    /// </summary>
    public async Task<EmbedarrResponse?> CreateStrmFilesAsync(DynamicUri dynamicUri, CancellationToken cancellationToken = default)
    {
        if (!_embedarrClient.IsConfigured)
        {
            _logger.LogWarning("Embedarr is not configured, cannot create STRM files");
            return new EmbedarrResponse { Success = false, Error = "Embedarr is not configured" };
        }

        var config = Config;

        return dynamicUri.Source switch
        {
            DynamicSource.Tmdb => await _embedarrClient.GenerateMovieAsync(
                int.Parse(dynamicUri.ExternalId),
                config.MovieLibraryPath,
                cancellationToken),

            DynamicSource.Tvdb => await _embedarrClient.GenerateSeriesAsync(
                int.Parse(dynamicUri.ExternalId),
                config.TvLibraryPath,
                cancellationToken),

            _ => new EmbedarrResponse { Success = false, Error = $"Unknown source: {dynamicUri.Source}" }
        };
    }

    /// <summary>
    /// Get the library path for a given media type.
    /// </summary>
    public string? GetLibraryPath(MediaType mediaType)
    {
        var config = Config;
        return mediaType switch
        {
            MediaType.Movie => config.MovieLibraryPath,
            MediaType.Series => config.TvLibraryPath,
            MediaType.Anime => config.AnimeLibraryPath,
            _ => null
        };
    }

    /// <summary>
    /// Clear all cached data.
    /// </summary>
    public void ClearCache()
    {
        // IMemoryCache doesn't have a Clear method, but we can use a custom implementation
        // For now, individual entries will expire based on TTL
        _logger.LogInformation("Cache clear requested (entries will expire based on TTL)");
    }
}

public enum MediaType
{
    Movie,
    Series,
    Anime
}
