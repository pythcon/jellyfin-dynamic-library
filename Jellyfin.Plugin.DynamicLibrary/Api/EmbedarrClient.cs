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

    public async Task<EmbedarrResponse?> GenerateMovieAsync(int tmdbId, string targetPath, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Embedarr is not configured");
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

            var request = new EmbedarrRequest
            {
                MediaType = "movie",
                TmdbId = tmdbId,
                TargetPath = targetPath
            };

            _logger.LogInformation("Requesting Embedarr to generate STRM for movie TMDB:{TmdbId} at {Path}", tmdbId, targetPath);

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/strm/generate", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Embedarr request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new EmbedarrResponse
                {
                    Success = false,
                    Error = $"HTTP {response.StatusCode}: {errorContent}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrResponse>(cancellationToken);
            _logger.LogInformation("Embedarr generated {Count} files for movie TMDB:{TmdbId}", result?.FilesCreated.Count ?? 0, tmdbId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Embedarr for movie TMDB:{TmdbId}", tmdbId);
            return new EmbedarrResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }

    public async Task<EmbedarrResponse?> GenerateSeriesAsync(int tvdbId, string targetPath, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogWarning("Embedarr is not configured");
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

            var request = new EmbedarrRequest
            {
                MediaType = "series",
                TvdbId = tvdbId,
                TargetPath = targetPath
            };

            _logger.LogInformation("Requesting Embedarr to generate STRM for series TVDB:{TvdbId} at {Path}", tvdbId, targetPath);

            var response = await client.PostAsJsonAsync($"{baseUrl}/api/strm/generate", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogError("Embedarr request failed: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return new EmbedarrResponse
                {
                    Success = false,
                    Error = $"HTTP {response.StatusCode}: {errorContent}"
                };
            }

            var result = await response.Content.ReadFromJsonAsync<EmbedarrResponse>(cancellationToken);
            _logger.LogInformation("Embedarr generated {Count} files for series TVDB:{TvdbId}", result?.FilesCreated.Count ?? 0, tvdbId);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling Embedarr for series TVDB:{TvdbId}", tvdbId);
            return new EmbedarrResponse
            {
                Success = false,
                Error = ex.Message
            };
        }
    }
}
