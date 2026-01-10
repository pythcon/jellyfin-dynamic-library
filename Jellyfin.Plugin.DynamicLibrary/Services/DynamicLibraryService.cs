using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Jellyfin.Plugin.DynamicLibrary.Providers;
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
    private readonly DirectCatalogProvider _directProvider;
    private readonly StremioCatalogProvider _stremioProvider;
    private readonly SearchResultFactory _searchResultFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DynamicLibraryService> _logger;

    public DynamicLibraryService(
        ITvdbClient tvdbClient,
        ITmdbClient tmdbClient,
        IEmbedarrClient embedarrClient,
        DirectCatalogProvider directProvider,
        StremioCatalogProvider stremioProvider,
        SearchResultFactory searchResultFactory,
        ILibraryManager libraryManager,
        IMemoryCache cache,
        ILogger<DynamicLibraryService> logger)
    {
        _tvdbClient = tvdbClient;
        _tmdbClient = tmdbClient;
        _embedarrClient = embedarrClient;
        _directProvider = directProvider;
        _stremioProvider = stremioProvider;
        _searchResultFactory = searchResultFactory;
        _libraryManager = libraryManager;
        _cache = cache;
        _logger = logger;
    }

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Gets the active catalog provider based on configuration.
    /// </summary>
    public ICatalogProvider GetCatalogProvider()
    {
        var config = Config;
        return config.CatalogProvider switch
        {
            Configuration.CatalogProvider.StremioAddon => _stremioProvider,
            _ => _directProvider
        };
    }

    public bool IsEnabled
    {
        get
        {
            var config = Config;
            if (!config.Enabled)
            {
                return false;
            }

            return GetCatalogProvider().IsConfigured;
        }
    }

    /// <summary>
    /// Log the current status of the service for debugging.
    /// </summary>
    public void LogStatus(ILogger logger)
    {
        var config = Config;
        var pluginInstance = DynamicLibraryPlugin.Instance;
        var provider = GetCatalogProvider();
        logger.LogInformation(
            "[DynamicLibrary] Status: PluginInstance={Instance}, ConfigEnabled={Enabled}, CatalogProvider={Provider}, ProviderConfigured={Configured}",
            pluginInstance != null ? "exists" : "NULL",
            config.Enabled,
            provider.ProviderName,
            provider.IsConfigured);
    }

    /// <summary>
    /// Search for content using the configured catalog provider.
    /// </summary>
    public async Task<List<BaseItemDto>> SearchAsync(
        string query,
        HashSet<BaseItemKind> requestedTypes,
        CancellationToken cancellationToken = default)
    {
        var config = Config;
        var provider = GetCatalogProvider();

        if (!IsEnabled)
        {
            _logger.LogDebug("[DynamicLibrary] Service is not enabled - returning empty results");
            return new List<BaseItemDto>();
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<BaseItemDto>();
        }

        // Use case-insensitive cache key that includes provider
        var cacheKey = $"search:{provider.ProviderName}:{query.ToLowerInvariant()}:{string.Join(",", requestedTypes.Order())}";

        // Check cache first
        if (_cache.TryGetValue<List<BaseItemDto>>(cacheKey, out var cachedResults) && cachedResults != null)
        {
            _logger.LogDebug("[DynamicLibrary] Returning cached search results for: {Query} ({Count} items)", query, cachedResults.Count);
            return cachedResults;
        }

        _logger.LogDebug("[DynamicLibrary] Searching via {Provider} for: {Query}", provider.ProviderName, query);

        var tasks = new List<Task<IReadOnlyList<CatalogItem>>>();

        // Search movies
        if (requestedTypes.Contains(BaseItemKind.Movie))
        {
            tasks.Add(provider.SearchMoviesAsync(query, config.MaxMovieResults, cancellationToken));
        }

        // Search TV shows
        if (requestedTypes.Contains(BaseItemKind.Series))
        {
            tasks.Add(provider.SearchSeriesAsync(query, config.MaxTvShowResults, cancellationToken));
        }

        var catalogResults = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();

        // Get existing provider IDs to filter out items already in library
        var existingImdbIds = GetExistingProviderIds("Imdb", null);
        var existingTmdbIds = GetExistingProviderIds("Tmdb", typeof(MediaBrowser.Controller.Entities.Movies.Movie));
        var existingTvdbIds = GetExistingProviderIds("Tvdb", typeof(MediaBrowser.Controller.Entities.TV.Series));

        // Convert CatalogItems to BaseItemDtos, filtering out existing items
        var results = new List<BaseItemDto>();
        foreach (var item in catalogResults)
        {
            // Skip if already in library
            if (!string.IsNullOrEmpty(item.ImdbId) && existingImdbIds.Contains(item.ImdbId))
            {
                _logger.LogDebug("[DynamicLibrary] Skipping '{Name}' (IMDB:{Id}) - already in library", item.Name, item.ImdbId);
                continue;
            }
            if (!string.IsNullOrEmpty(item.TmdbId) && existingTmdbIds.Contains(item.TmdbId))
            {
                _logger.LogDebug("[DynamicLibrary] Skipping '{Name}' (TMDB:{Id}) - already in library", item.Name, item.TmdbId);
                continue;
            }
            if (!string.IsNullOrEmpty(item.TvdbId) && existingTvdbIds.Contains(item.TvdbId))
            {
                _logger.LogDebug("[DynamicLibrary] Skipping '{Name}' (TVDB:{Id}) - already in library", item.Name, item.TvdbId);
                continue;
            }

            var dto = await _searchResultFactory.CreateDtoFromCatalogItemAsync(item, cancellationToken);
            results.Add(dto);
        }

        // Cache results
        var cacheDuration = TimeSpan.FromMinutes(config.CacheTtlMinutes);
        _cache.Set(cacheKey, results, cacheDuration);

        _logger.LogDebug("[DynamicLibrary] Search for '{Query}' returned {Count} results", query, results.Count);
        return results;
    }

    /// <summary>
    /// Legacy search method for backwards compatibility - uses Direct provider explicitly.
    /// </summary>
    [Obsolete("Use SearchAsync instead which uses the configured catalog provider")]
    public async Task<List<BaseItemDto>> SearchDirectAsync(
        string query,
        HashSet<BaseItemKind> requestedTypes,
        CancellationToken cancellationToken = default)
    {
        var config = Config;
        var tasks = new List<Task<IEnumerable<BaseItemDto>>>();

        // Search movies based on configured API source
        if (requestedTypes.Contains(BaseItemKind.Movie) && config.MovieApiSource != ApiSource.None)
        {
            if (config.MovieApiSource == ApiSource.Tmdb && _tmdbClient.IsConfigured)
            {
                tasks.Add(SearchMoviesViaTmdbAsync(query, config.MaxMovieResults, cancellationToken));
            }
        }

        // Search TV shows based on configured API source
        if (requestedTypes.Contains(BaseItemKind.Series) && config.TvShowApiSource != ApiSource.None)
        {
            if (config.TvShowApiSource == ApiSource.Tvdb && _tvdbClient.IsConfigured)
            {
                tasks.Add(SearchSeriesViaTvdbAsync(query, config.MaxTvShowResults, cancellationToken));
            }
        }

        return (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
    }

    private async Task<IEnumerable<BaseItemDto>> SearchMoviesViaTmdbAsync(string query, int maxResults, CancellationToken cancellationToken)
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

    private async Task<IEnumerable<BaseItemDto>> SearchSeriesViaTvdbAsync(string query, int maxResults, CancellationToken cancellationToken)
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
    /// <param name="providerName">The provider name (Imdb, Tmdb, Tvdb)</param>
    /// <param name="itemType">The item type to filter by, or null for both movies and series</param>
    private HashSet<string> GetExistingProviderIds(string providerName, Type? itemType)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            BaseItemKind[] includeTypes;
            if (itemType == null)
            {
                // Include both movies and series
                includeTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series };
            }
            else if (itemType == typeof(MediaBrowser.Controller.Entities.Movies.Movie))
            {
                includeTypes = new[] { BaseItemKind.Movie };
            }
            else
            {
                includeTypes = new[] { BaseItemKind.Series };
            }

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = includeTypes,
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
            _logger.LogError(ex, "[DynamicLibrary] Error getting existing provider IDs for {Provider}", providerName);
        }

        return ids;
    }

    /// <summary>
    /// Trigger Embedarr to add media to library (creates STRM files).
    /// </summary>
    /// <param name="item">The item DTO with provider IDs</param>
    public async Task<EmbedarrResponse?> AddToEmbedarrAsync(BaseItemDto item, CancellationToken cancellationToken = default)
    {
        if (!_embedarrClient.IsConfigured)
        {
            _logger.LogWarning("Embedarr is not configured, cannot add to library");
            return new EmbedarrResponse { Success = false, Error = "Embedarr is not configured" };
        }

        var config = Config;

        // For movies, use configured preference with fallback
        if (item.Type == BaseItemKind.Movie)
        {
            var (id, idType) = GetMovieId(item, config.MoviePreferredId);

            if (id == null)
            {
                return new EmbedarrResponse { Success = false, Error = "No IMDB or TMDB ID found for movie" };
            }

            _logger.LogDebug("[DynamicLibrary] Adding movie to Embedarr: {Name} using {IdType} ID: {Id}", item.Name, idType, id);
            return await _embedarrClient.AddMovieAsync(id, cancellationToken);
        }

        // For TV series (including anime), use configured preference with fallback
        if (item.Type == BaseItemKind.Series)
        {
            var isAnime = IsAnime(item);
            var preference = isAnime ? config.AnimePreferredId : config.TvShowPreferredId;
            var (id, idType) = GetSeriesId(item, preference);

            if (id == null)
            {
                return new EmbedarrResponse { Success = false, Error = "No IMDB or TVDB ID found for series" };
            }

            _logger.LogDebug("[DynamicLibrary] Adding TV series to Embedarr: {Name} (isAnime={IsAnime}) using {IdType} ID: {Id}",
                item.Name, isAnime, idType, id);
            return await _embedarrClient.AddTvSeriesAsync(id, cancellationToken);
        }

        return new EmbedarrResponse { Success = false, Error = $"Unsupported item type: {item.Type}" };
    }

    /// <summary>
    /// Get the movie ID based on preference with fallback.
    /// Movies can have: IMDB, TMDB, TVDB
    /// </summary>
    private (object? Id, string IdType) GetMovieId(BaseItemDto item, PreferredProviderId preference)
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
                // TMDB and TVDB IDs are numeric, IMDB is string
                if (provider != "Imdb" && int.TryParse(id, out var numericId))
                {
                    return (numericId, provider.ToUpperInvariant());
                }
                return (id, provider.ToUpperInvariant());
            }
        }

        return (null, "none");
    }

    /// <summary>
    /// Get the series ID based on preference with fallback.
    /// TV/Anime can have: IMDB, TVDB, TMDB
    /// </summary>
    private (object? Id, string IdType) GetSeriesId(BaseItemDto item, PreferredProviderId preference)
    {
        if (item.ProviderIds == null)
        {
            return (null, "none");
        }

        // Try preferred ID first, then fall back to others
        var fallbackOrder = preference switch
        {
            PreferredProviderId.Imdb => new[] { "Imdb", "Tvdb", "Tmdb" },
            PreferredProviderId.Tvdb => new[] { "Tvdb", "Imdb", "Tmdb" },
            PreferredProviderId.Tmdb => new[] { "Tmdb", "Imdb", "Tvdb" },
            _ => new[] { "Imdb", "Tvdb", "Tmdb" }
        };

        foreach (var provider in fallbackOrder)
        {
            if (item.ProviderIds.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id))
            {
                // TMDB and TVDB IDs are numeric, IMDB is string
                if (provider != "Imdb" && int.TryParse(id, out var numericId))
                {
                    return (numericId, provider.ToUpperInvariant());
                }
                return (id, provider.ToUpperInvariant());
            }
        }

        return (null, "none");
    }

    /// <summary>
    /// Determine if a series is anime based on genres or other metadata.
    /// </summary>
    public static bool IsAnime(BaseItemDto item)
    {
        if (item.Genres == null || item.Genres.Length == 0)
        {
            return false;
        }

        // Check for anime-related genres
        var animeGenres = new[] { "Anime", "Animation" };
        var hasAnimeGenre = item.Genres.Any(g =>
            animeGenres.Any(ag => g.Contains(ag, StringComparison.OrdinalIgnoreCase)));

        if (!hasAnimeGenre)
        {
            return false;
        }

        // If it has Animation genre, also check for Japanese origin indicators
        // Animation alone doesn't mean anime (could be Western animation)
        if (item.Genres.Any(g => g.Equals("Anime", StringComparison.OrdinalIgnoreCase)))
        {
            return true; // Explicit "Anime" genre
        }

        // For "Animation" genre, check if it's from Japan or has anime-related tags
        // Check production country if available
        if (item.ProductionLocations != null &&
            item.ProductionLocations.Any(c => c.Contains("Japan", StringComparison.OrdinalIgnoreCase) ||
                                               c.Equals("JP", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
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
