using System.Net.Http.Json;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public class EmbedarrClient : IEmbedarrClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EmbedarrClient> _logger;

    public EmbedarrClient(IHttpClientFactory httpClientFactory, ILogger<EmbedarrClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrEmpty(GetBaseUrl());

    private string GetBaseUrl()
    {
        var url = DynamicLibraryPlugin.Instance?.Configuration.EmbedarrUrl ?? string.Empty;
        return url.TrimEnd('/');
    }

    private string? GetApiKey()
    {
        var key = DynamicLibraryPlugin.Instance?.Configuration.EmbedarrApiKey;
        return string.IsNullOrEmpty(key) ? null : key;
    }

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient();
        var apiKey = GetApiKey();

        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return false;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            // Try to reach the health endpoint or root
            var response = await client.GetAsync($"{baseUrl}/health", cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Embedarr is not available");
            return false;
        }
    }

    public async Task<EmbedarrResponse?> AddMovieAsync(object id, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Embedarr is not configured");
            return new EmbedarrResponse
            {
                Success = false,
                Error = "Embedarr is not configured"
            };
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            var request = new EmbedarrAddRequest { Id = id };

            _logger.LogDebug("[DynamicLibrary] Adding movie to Embedarr: {Id}", id);

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/admin/library/movies", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[DynamicLibrary] Embedarr request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new EmbedarrResponse
                {
                    Success = false,
                    Error = $"HTTP {response.StatusCode}: {errorContent}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrResponse>(cancellationToken);
            _logger.LogDebug("[DynamicLibrary] Embedarr added movie {Id}, files created: {Count}", id, result?.FilesCreated.Count ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error calling Embedarr for movie: {Id}", id);
            return new EmbedarrResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public async Task<EmbedarrResponse?> AddTvSeriesAsync(object id, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Embedarr is not configured");
            return new EmbedarrResponse
            {
                Success = false,
                Error = "Embedarr is not configured"
            };
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            var request = new EmbedarrAddRequest { Id = id };

            _logger.LogDebug("[DynamicLibrary] Adding TV series to Embedarr: {Id}", id);

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/admin/library/tv", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[DynamicLibrary] Embedarr request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new EmbedarrResponse
                {
                    Success = false,
                    Error = $"HTTP {response.StatusCode}: {errorContent}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrResponse>(cancellationToken);
            _logger.LogDebug("[DynamicLibrary] Embedarr added TV series {Id}, files created: {Count}", id, result?.FilesCreated.Count ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error calling Embedarr for TV series: {Id}", id);
            return new EmbedarrResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public async Task<EmbedarrResponse?> AddAnimeAsync(object id, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Embedarr is not configured");
            return new EmbedarrResponse
            {
                Success = false,
                Error = "Embedarr is not configured"
            };
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            var request = new EmbedarrAddRequest { Id = id };

            _logger.LogDebug("[DynamicLibrary] Adding anime to Embedarr: {Id}", id);

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/admin/library/anime", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[DynamicLibrary] Embedarr request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new EmbedarrResponse
                {
                    Success = false,
                    Error = $"HTTP {response.StatusCode}: {errorContent}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrResponse>(cancellationToken);
            _logger.LogDebug("[DynamicLibrary] Embedarr added anime {Id}, files created: {Count}", id, result?.FilesCreated.Count ?? 0);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error calling Embedarr for anime: {Id}", id);
            return new EmbedarrResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public async Task<EmbedarrUrlResponse?> GetMovieStreamUrlAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Embedarr is not configured");
            return null;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            _logger.LogDebug("[DynamicLibrary] Getting stream URL for movie: {ImdbId}", imdbId);

            var response = await client.GetAsync($"{baseUrl}/api/url/movie/{imdbId}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[DynamicLibrary] Embedarr URL request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrUrlResponse>(cancellationToken);
            _logger.LogDebug("[DynamicLibrary] Got stream URL for movie {ImdbId}: {Url}", imdbId, result?.Url);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error getting stream URL for movie: {ImdbId}", imdbId);
            return null;
        }
    }

    public async Task<EmbedarrUrlResponse?> GetTvEpisodeStreamUrlAsync(string imdbId, int season, int episode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Embedarr is not configured");
            return null;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            _logger.LogDebug("[DynamicLibrary] Getting stream URL for TV episode: {ImdbId} S{Season}E{Episode}", imdbId, season, episode);

            var response = await client.GetAsync($"{baseUrl}/api/url/tv/{imdbId}/{season}/{episode}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[DynamicLibrary] Embedarr URL request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrUrlResponse>(cancellationToken);
            _logger.LogDebug("[DynamicLibrary] Got stream URL for TV {ImdbId} S{Season}E{Episode}: {Url}", imdbId, season, episode, result?.Url);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error getting stream URL for TV: {ImdbId} S{Season}E{Episode}", imdbId, season, episode);
            return null;
        }
    }

    public async Task<EmbedarrUrlResponse?> GetAnimeStreamUrlAsync(string id, int episode, string audioType = "sub", CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("Embedarr is not configured");
            return null;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            _logger.LogDebug("[DynamicLibrary] Getting stream URL for anime: {Id} E{Episode} ({AudioType})", id, episode, audioType);

            var response = await client.GetAsync($"{baseUrl}/api/url/anime/{id}/{episode}/{audioType}", cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("[DynamicLibrary] Embedarr URL request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrUrlResponse>(cancellationToken);
            _logger.LogDebug("[DynamicLibrary] Got stream URL for anime {Id} E{Episode}: {Url}", id, episode, result?.Url);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error getting stream URL for anime: {Id} E{Episode}", id, episode);
            return null;
        }
    }
}
