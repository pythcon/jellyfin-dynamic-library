using System.Net.Http.Headers;
using System.Net.Http.Json;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public class TvdbClient : ITvdbClient
{
    private const string BaseUrl = "https://api4.thetvdb.com/v4";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TvdbClient> _logger;

    private string? _authToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    public TvdbClient(IHttpClientFactory httpClientFactory, ILogger<TvdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

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

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            var encodedQuery = Uri.EscapeDataString(query);
            var url = $"{BaseUrl}/search?query={encodedQuery}&type=series";

            _logger.LogDebug("Searching TVDB for: {Query}", query);

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TVDB search failed: {StatusCode}", response.StatusCode);
                return Array.Empty<TvdbSearchResult>();
            }

            var searchResponse = await response.Content.ReadFromJsonAsync<TvdbSearchResponse>(cancellationToken);
            var results = searchResponse?.Data ?? new List<TvdbSearchResult>();

            _logger.LogDebug("TVDB search returned {Count} results", results.Count);
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

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            var url = $"{BaseUrl}/series/{seriesId}/extended?meta=episodes";

            _logger.LogDebug("Fetching TVDB series: {SeriesId}", seriesId);

            var response = await client.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("TVDB series lookup failed: {StatusCode}", response.StatusCode);
                return null;
            }

            var seriesResponse = await response.Content.ReadFromJsonAsync<TvdbSeriesResponse>(cancellationToken);
            return seriesResponse?.Data;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TVDB series: {SeriesId}", seriesId);
            return null;
        }
    }
}
