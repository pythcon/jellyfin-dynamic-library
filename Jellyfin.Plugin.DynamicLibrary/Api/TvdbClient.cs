using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public class TvdbClient : ITvdbClient
{
    private const string BaseUrl = "https://api4.thetvdb.com/v4";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<TvdbClient> _logger;

    private string? _authToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    // Cache key prefixes
    private const string SearchCachePrefix = "tvdb:search:";
    private const string SeriesCachePrefix = "tvdb:series:";
    private const string TranslationCachePrefix = "tvdb:translation:";
    private const string EpisodeTranslationCachePrefix = "tvdb:episode:translation:";

    public TvdbClient(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<TvdbClient> logger)
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
        return DynamicLibraryPlugin.Instance?.Configuration.TvdbApiKey ?? string.Empty;
    }

    private async Task<string?> GetAuthTokenAsync(CancellationToken cancellationToken)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
        {
            _logger.LogWarning("TVDB API key is not configured");
            return null;
        }

        // Check if we have a valid token
        if (!string.IsNullOrEmpty(_authToken) && DateTime.UtcNow < _tokenExpiry)
        {
            return _authToken;
        }

        await _authLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_authToken) && DateTime.UtcNow < _tokenExpiry)
            {
                return _authToken;
            }

            _logger.LogDebug("Authenticating with TVDB API");

            var client = _httpClientFactory.CreateClient();
            var response = await client.PostAsJsonAsync(
                $"{BaseUrl}/login",
                new { apikey = apiKey },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("Failed to authenticate with TVDB: {StatusCode}", response.StatusCode);
                return null;
            }

            var authResponse = await response.Content.ReadFromJsonAsync<TvdbAuthResponse>(cancellationToken);
            if (authResponse?.Data?.Token is null)
            {
                _logger.LogError("TVDB authentication response did not contain a token");
                return null;
            }

            _authToken = authResponse.Data.Token;
            _tokenExpiry = DateTime.UtcNow.AddDays(25); // TVDB tokens expire after 30 days

            _logger.LogDebug("Successfully authenticated with TVDB");
            return _authToken;
        }
        finally
        {
            _authLock.Release();
        }
    }

    /// <summary>
    /// Get the language code for TVDB API requests.
    /// Returns null if LanguageMode is Default (will use TVDB's default behavior).
    /// </summary>
    private string? GetLanguageCode()
    {
        return DynamicLibraryPlugin.Instance?.Configuration.GetTvdbLanguageCode();
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken)
    {
        var token = await GetAuthTokenAsync(cancellationToken);
        var client = _httpClientFactory.CreateClient();

        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public async Task<IReadOnlyList<TvdbSearchResult>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("TVDB client not configured, skipping search");
            return Array.Empty<TvdbSearchResult>();
        }

        // Check cache first (case-insensitive)
        var cacheKey = $"{SearchCachePrefix}{query.ToLowerInvariant()}";
        if (_cache.TryGetValue<IReadOnlyList<TvdbSearchResult>>(cacheKey, out var cachedResults) && cachedResults != null)
        {
            _logger.LogDebug("TVDB search cache hit for: {Query} ({Count} results)", query, cachedResults.Count);
            return cachedResults;
        }

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{BaseUrl}/search?query={encodedQuery}&type=series";
            var language = GetLanguageCode();

            _logger.LogDebug("Searching TVDB for: {Query} (language: {Language})", query, language ?? "default");

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TVDB search failed: {StatusCode}", response.StatusCode);
                return Array.Empty<TvdbSearchResult>();
            }

            var searchResponse = await response.Content.ReadFromJsonAsync<TvdbSearchResponse>(cancellationToken);
            var results = searchResponse?.Data ?? new List<TvdbSearchResult>();

            // Cache the results
            _cache.Set(cacheKey, (IReadOnlyList<TvdbSearchResult>)results, CacheDuration);
            _logger.LogDebug("TVDB search returned {Count} results (cached)", results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching TVDB for: {Query}", query);
            return Array.Empty<TvdbSearchResult>();
        }
    }

    public async Task<TvdbSeriesExtended?> GetSeriesExtendedAsync(int seriesId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("TVDB client not configured, skipping series lookup");
            return null;
        }

        // Check cache first
        var cacheKey = $"{SeriesCachePrefix}{seriesId}";
        if (_cache.TryGetValue<TvdbSeriesExtended>(cacheKey, out var cachedSeries) && cachedSeries != null)
        {
            _logger.LogDebug("TVDB series cache hit for: {SeriesId}", seriesId);
            return cachedSeries;
        }

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            // Request both episodes and translations metadata
            var url = $"{BaseUrl}/series/{seriesId}/extended?meta=episodes&meta=translations";
            var language = GetLanguageCode();

            _logger.LogDebug("Fetching TVDB series: {SeriesId} (language: {Language})", seriesId, language ?? "default");

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TVDB series lookup failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var seriesResponse = await response.Content.ReadFromJsonAsync<TvdbSeriesResponse>(cancellationToken);
            var series = seriesResponse?.Data;

            // Cache the result
            if (series != null)
            {
                _cache.Set(cacheKey, series, CacheDuration);
                _logger.LogDebug("TVDB series cached: {SeriesId}", seriesId);
            }

            return series;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TVDB series: {SeriesId}", seriesId);
            return null;
        }
    }

    public async Task<TvdbTranslationData?> GetSeriesTranslationAsync(int seriesId, string language, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("TVDB client not configured, skipping translation lookup");
            return null;
        }

        // Check cache first
        var cacheKey = $"{TranslationCachePrefix}{seriesId}:{language}";
        if (_cache.TryGetValue<TvdbTranslationData>(cacheKey, out var cachedTranslation) && cachedTranslation != null)
        {
            _logger.LogDebug("TVDB series translation cache hit: SeriesId={SeriesId}, Language={Language}", seriesId, language);
            return cachedTranslation;
        }

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            var url = $"{BaseUrl}/series/{seriesId}/translations/{language}";

            _logger.LogDebug("Fetching TVDB translation: SeriesId={SeriesId}, Language={Language}", seriesId, language);

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TVDB translation lookup failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var translationResponse = await response.Content.ReadFromJsonAsync<TvdbTranslationResponse>(cancellationToken);
            var translation = translationResponse?.Data;

            // Cache the result
            if (translation != null)
            {
                _cache.Set(cacheKey, translation, CacheDuration);
            }

            return translation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TVDB translation: SeriesId={SeriesId}, Language={Language}", seriesId, language);
            return null;
        }
    }

    public async Task<TvdbTranslationData?> GetEpisodeTranslationAsync(int episodeId, string language, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("TVDB client not configured, skipping episode translation lookup");
            return null;
        }

        // Check cache first - this is critical as we may fetch 100+ episode translations
        var cacheKey = $"{EpisodeTranslationCachePrefix}{episodeId}:{language}";
        if (_cache.TryGetValue<TvdbTranslationData?>(cacheKey, out var cachedTranslation))
        {
            // Note: cachedTranslation may be null (cached negative result)
            return cachedTranslation;
        }

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            var url = $"{BaseUrl}/episodes/{episodeId}/translations/{language}";

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // Cache the negative result to avoid repeated API calls for missing translations
                _cache.Set(cacheKey, (TvdbTranslationData?)null, CacheDuration);

                // 404 is common for episodes without translations, don't log as warning
                if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    _logger.LogWarning("TVDB episode translation lookup failed: {StatusCode}", response.StatusCode);
                }
                return null;
            }

            var translationResponse = await response.Content.ReadFromJsonAsync<TvdbTranslationResponse>(cancellationToken);
            var translation = translationResponse?.Data;

            // Cache the result (including null)
            _cache.Set(cacheKey, translation, CacheDuration);

            return translation;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TVDB episode translation: EpisodeId={EpisodeId}, Language={Language}", episodeId, language);
            return null;
        }
    }
}
