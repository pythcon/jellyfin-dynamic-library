using System.Net.Http.Json;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Providers;

/// <summary>
/// Catalog provider that uses a Stremio addon for catalog and metadata.
/// Supports addons like Cinemeta, AIOMetadata, and other Stremio-compatible addons.
/// </summary>
public class StremioCatalogProvider : ICatalogProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<StremioCatalogProvider> _logger;

    // Cache key prefixes
    private const string SearchCachePrefix = "stremio:catalog:search:";
    private const string MetaCachePrefix = "stremio:meta:";

    public StremioCatalogProvider(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<StremioCatalogProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    private PluginConfiguration Config =>
        DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    private TimeSpan CacheDuration => TimeSpan.FromMinutes(Config.CacheTtlMinutes);

    /// <inheritdoc />
    public string ProviderName => "Stremio Addon";

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(GetBaseUrl());

    private string GetBaseUrl()
    {
        var url = Config.StremioCatalogUrl ?? string.Empty;
        url = url.TrimEnd('/');

        // Strip manifest.json if present (Stremio addon URLs often include this)
        if (url.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"/manifest.json".Length];
        }
        else if (url.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^"manifest.json".Length].TrimEnd('/');
        }

        return url;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        return client;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogItem>> SearchMoviesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        return await SearchCatalogAsync("movie", query, maxResults, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogItem>> SearchSeriesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        return await SearchCatalogAsync("series", query, maxResults, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CatalogItemDetails?> GetMovieDetailsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await GetMetaAsync("movie", id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CatalogItemDetails?> GetSeriesDetailsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return await GetMetaAsync("series", id, cancellationToken);
    }

    // ==================== Internal Methods ====================

    private async Task<IReadOnlyList<CatalogItem>> SearchCatalogAsync(
        string type,
        string query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[StremioCatalog] Not configured, skipping search");
            return Array.Empty<CatalogItem>();
        }

        // Check cache first
        var cacheKey = $"{SearchCachePrefix}{type}:{query.ToLowerInvariant()}";
        if (_cache.TryGetValue<IReadOnlyList<CatalogItem>>(cacheKey, out var cachedResults) && cachedResults != null)
        {
            _logger.LogDebug("[StremioCatalog] Cache hit for {Type} search: {Query} ({Count} results)",
                type, query, cachedResults.Count);
            return cachedResults.Take(maxResults).ToList();
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();
            var encodedQuery = Uri.EscapeDataString(query);

            // Stremio catalog search format: /catalog/{type}/search/search={query}.json
            // The "search" catalog ID is used by AIOMetadata, Cinemeta, and other Stremio addons
            var url = $"{baseUrl}/catalog/{type}/search/search={encodedQuery}.json";

            _logger.LogDebug("[StremioCatalog] Searching: {Url}", url);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[StremioCatalog] Search failed with status {StatusCode} for {Type}: {Query}",
                    response.StatusCode, type, query);
                return Array.Empty<CatalogItem>();
            }

            var catalogResponse = await response.Content.ReadFromJsonAsync<StremioCatalogResponse>(cancellationToken);

            var results = catalogResponse?.Metas
                .Select(m => ConvertMetaPreviewToCatalogItem(m, type))
                .ToList() ?? new List<CatalogItem>();

            // Cache the results
            _cache.Set(cacheKey, (IReadOnlyList<CatalogItem>)results, CacheDuration);

            _logger.LogDebug("[StremioCatalog] Search returned {Count} results for {Type}: {Query}",
                results.Count, type, query);

            return results.Take(maxResults).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StremioCatalog] Error searching for {Type}: {Query}", type, query);
            return Array.Empty<CatalogItem>();
        }
    }

    private async Task<CatalogItemDetails?> GetMetaAsync(
        string type,
        string id,
        CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[StremioCatalog] Not configured, skipping meta lookup");
            return null;
        }

        // Check cache first
        var cacheKey = $"{MetaCachePrefix}{type}:{id}";
        if (_cache.TryGetValue<CatalogItemDetails>(cacheKey, out var cachedMeta) && cachedMeta != null)
        {
            _logger.LogDebug("[StremioCatalog] Cache hit for {Type} meta: {Id}", type, id);
            return cachedMeta;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            // Stremio meta format: /meta/{type}/{id}.json
            var url = $"{baseUrl}/meta/{type}/{id}.json";

            _logger.LogDebug("[StremioCatalog] Fetching meta: {Url}", url);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[StremioCatalog] Meta lookup failed with status {StatusCode} for {Type}: {Id}",
                    response.StatusCode, type, id);
                return null;
            }

            var metaResponse = await response.Content.ReadFromJsonAsync<StremioMetaResponse>(cancellationToken);
            if (metaResponse?.Meta == null)
            {
                _logger.LogWarning("[StremioCatalog] Meta response was empty for {Type}: {Id}", type, id);
                return null;
            }

            var details = ConvertMetaToCatalogItemDetails(metaResponse.Meta, type);

            // Cache the result
            _cache.Set(cacheKey, details, CacheDuration);

            _logger.LogDebug("[StremioCatalog] Meta fetched for {Type}: {Id}", type, id);

            return details;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[StremioCatalog] Error fetching meta for {Type}: {Id}", type, id);
            return null;
        }
    }

    // ==================== Conversion Methods ====================

    private CatalogItem ConvertMetaPreviewToCatalogItem(StremioMetaPreview preview, string type)
    {
        var contentType = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? CatalogContentType.Movie
            : CatalogContentType.Series;

        return new CatalogItem
        {
            Id = preview.Id,
            Source = CatalogSource.Stremio,
            ImdbId = preview.Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? preview.Id : null,
            Name = preview.Name,
            Overview = preview.Description,
            PosterUrl = preview.Poster,
            BackdropUrl = preview.Background,
            Year = preview.ParsedYear,
            Rating = preview.ParsedRating,
            Type = contentType,
            Genres = preview.Genres ?? new List<string>()
        };
    }

    private CatalogItemDetails ConvertMetaToCatalogItemDetails(StremioMeta meta, string type)
    {
        var contentType = type.Equals("movie", StringComparison.OrdinalIgnoreCase)
            ? CatalogContentType.Movie
            : CatalogContentType.Series;

        var details = new CatalogItemDetails
        {
            Id = meta.Id,
            Source = CatalogSource.Stremio,
            ImdbId = meta.ImdbId ?? (meta.Id.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? meta.Id : null),
            Name = meta.Name,
            Overview = meta.Description,
            PosterUrl = meta.Poster,
            BackdropUrl = meta.Background,
            Year = meta.ParsedYear,
            ReleaseDate = meta.ParsedReleaseDate,
            Rating = meta.ParsedRating,
            Type = contentType,
            RuntimeMinutes = meta.ParsedRuntimeMinutes,
            Status = meta.Status,
            Genres = meta.Genres ?? new List<string>(),
            Directors = meta.Director ?? new List<string>(),
            Countries = !string.IsNullOrEmpty(meta.Country)
                ? new List<string> { meta.Country }
                : new List<string>()
        };

        // Add cast (names only from Stremio)
        if (meta.Cast != null)
        {
            details.Cast = meta.Cast
                .Select((name, index) => new CatalogCastMember
                {
                    Name = name,
                    Order = index
                })
                .ToList();
        }

        // Add episodes for series
        if (contentType == CatalogContentType.Series && meta.Videos != null)
        {
            var episodes = meta.Videos
                .OrderBy(v => v.Season)
                .ThenBy(v => v.Episode)
                .Select(v => new CatalogEpisodeInfo
                {
                    Id = v.Id,
                    SeasonNumber = v.Season,
                    EpisodeNumber = v.Episode,
                    Name = v.BestTitle,
                    Overview = v.BestOverview,
                    AirDate = v.ParsedAirDate,
                    ImageUrl = v.Thumbnail
                })
                .ToList();

            details.Episodes = episodes;

            // Build seasons from episodes
            details.Seasons = episodes
                .GroupBy(e => e.SeasonNumber)
                .Select(g => new CatalogSeasonInfo
                {
                    Number = g.Key,
                    EpisodeCount = g.Count()
                })
                .OrderBy(s => s.Number)
                .ToList();
        }

        return details;
    }
}
