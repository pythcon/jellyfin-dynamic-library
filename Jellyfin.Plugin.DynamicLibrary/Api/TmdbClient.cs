using System.Net.Http.Json;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public class TmdbClient : ITmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string DefaultImageBaseUrl = "https://image.tmdb.org/t/p/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TmdbClient> _logger;

    private string? _imageBaseUrl;
    private readonly SemaphoreSlim _configLock = new(1, 1);

    // Cache key prefixes
    private const string SearchCachePrefix = "tmdb:search:";
    private const string MovieCachePrefix = "tmdb:movie:";

    public TmdbClient(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    private TimeSpan CacheDuration => TimeSpan.FromMinutes(
        DynamicLibraryPlugin.Instance?.Configuration.CacheTtlMinutes ?? 60);

    public bool IsConfigured => !string.IsNullOrEmpty(GetApiKey());

    private string GetApiKey()
    {
        return DynamicLibraryPlugin.Instance?.Configuration.TmdbApiKey ?? string.Empty;
    }

    private HttpClient CreateClient()
    {
        return _httpClientFactory.CreateClient();
    }

    private string AppendApiKey(string url)
    {
        var apiKey = GetApiKey();
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}api_key={apiKey}";
    }

    /// <summary>
    /// Get the language code for TMDB API requests.
    /// Returns null if LanguageMode is Default (will use TMDB's default behavior).
    /// </summary>
    private string? GetLanguageCode()
    {
        return DynamicLibraryPlugin.Instance?.Configuration.GetTmdbLanguageCode();
    }

    public async Task<IReadOnlyList<TmdbMovieResult>> SearchMoviesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("TMDB client not configured, skipping search");
            return Array.Empty<TmdbMovieResult>();
        }

        // Check cache first (case-insensitive)
        var language = GetLanguageCode();
        var cacheKey = $"{SearchCachePrefix}{query.ToLowerInvariant()}:{language ?? "default"}";
        if (_cache.TryGetValue<IReadOnlyList<TmdbMovieResult>>(cacheKey, out var cachedResults) && cachedResults != null)
        {
            _logger.LogDebug("TMDB search cache hit for: {Query} ({Count} results)", query, cachedResults.Count);
            return cachedResults;
        }

        try
        {
            var client = CreateClient();
            var encodedQuery = Uri.EscapeDataString(query);
            var languageParam = !string.IsNullOrEmpty(language) ? $"&language={language}" : "";
            var url = AppendApiKey($"{BaseUrl}/search/movie?query={encodedQuery}&include_adult=false{languageParam}");

            _logger.LogDebug("Searching TMDB for: {Query} (language: {Language})", query, language ?? "default");

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB search failed: {StatusCode}", response.StatusCode);
                return Array.Empty<TmdbMovieResult>();
            }

            var searchResponse = await response.Content.ReadFromJsonAsync<TmdbSearchResponse>(cancellationToken);
            var results = searchResponse?.Results ?? new List<TmdbMovieResult>();

            // Cache the results
            _cache.Set(cacheKey, (IReadOnlyList<TmdbMovieResult>)results, CacheDuration);
            _logger.LogDebug("TMDB search returned {Count} results (cached)", results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching TMDB for: {Query}", query);
            return Array.Empty<TmdbMovieResult>();
        }
    }

    public async Task<TmdbMovieDetails?> GetMovieDetailsAsync(int movieId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("TMDB client not configured, skipping movie lookup");
            return null;
        }

        // Check cache first
        var language = GetLanguageCode();
        var cacheKey = $"{MovieCachePrefix}{movieId}:{language ?? "default"}";
        if (_cache.TryGetValue<TmdbMovieDetails>(cacheKey, out var cachedMovie) && cachedMovie != null)
        {
            _logger.LogDebug("TMDB movie cache hit for: {MovieId}", movieId);
            return cachedMovie;
        }

        try
        {
            var client = CreateClient();
            var languageParam = !string.IsNullOrEmpty(language) ? $"&language={language}" : "";
            // Use append_to_response to get credits in the same request
            var url = AppendApiKey($"{BaseUrl}/movie/{movieId}?append_to_response=credits{languageParam}");

            _logger.LogDebug("Fetching TMDB movie with credits: {MovieId} (language: {Language})", movieId, language ?? "default");

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TMDB movie lookup failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var movie = await response.Content.ReadFromJsonAsync<TmdbMovieDetails>(cancellationToken);

            // Cache the result
            if (movie != null)
            {
                _cache.Set(cacheKey, movie, CacheDuration);
                _logger.LogDebug("TMDB movie cached: {MovieId}", movieId);
            }

            return movie;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TMDB movie: {MovieId}", movieId);
            return null;
        }
    }

    public async Task<string> GetImageBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(_imageBaseUrl))
        {
            return _imageBaseUrl;
        }

        if (!IsConfigured)
        {
            return DefaultImageBaseUrl;
        }

        await _configLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrEmpty(_imageBaseUrl))
            {
                return _imageBaseUrl;
            }

            var client = CreateClient();
            var url = AppendApiKey($"{BaseUrl}/configuration");

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to get TMDB configuration: {StatusCode}", response.StatusCode);
                return DefaultImageBaseUrl;
            }

            var config = await response.Content.ReadFromJsonAsync<TmdbConfigurationResponse>(cancellationToken);
            _imageBaseUrl = config?.Images.SecureBaseUrl ?? DefaultImageBaseUrl;

            return _imageBaseUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TMDB configuration");
            return DefaultImageBaseUrl;
        }
        finally
        {
            _configLock.Release();
        }
    }
}
