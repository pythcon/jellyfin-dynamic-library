using System.Net.Http.Json;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

/// <summary>
/// Client for AIOStreams Stremio addon API.
/// </summary>
public class AIOStreamsClient : IAIOStreamsClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AIOStreamsClient> _logger;

    public AIOStreamsClient(IHttpClientFactory httpClientFactory, ILogger<AIOStreamsClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsConfigured => !string.IsNullOrEmpty(GetBaseUrl());

    private string GetBaseUrl()
    {
        var url = DynamicLibraryPlugin.Instance?.Configuration.AIOStreamsUrl ?? string.Empty;
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
    public async Task<AIOStreamsResponse?> GetMovieStreamsAsync(string imdbId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[DynamicLibrary] AIOStreams is not configured");
            return null;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            // Stremio addon format: /stream/movie/{imdbId}.json
            var url = $"{baseUrl}/stream/movie/{imdbId}.json";

            _logger.LogDebug("[DynamicLibrary] Fetching movie streams from AIOStreams: {Url}", url);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DynamicLibrary] AIOStreams returned {StatusCode} for movie {ImdbId}",
                    response.StatusCode, imdbId);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<AIOStreamsResponse>(cancellationToken);

            _logger.LogDebug("[DynamicLibrary] AIOStreams returned {Count} streams for movie {ImdbId}",
                result?.Streams.Count ?? 0, imdbId);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching movie streams from AIOStreams for {ImdbId}", imdbId);
            return null;
        }
    }

    /// <inheritdoc />
    public async Task<AIOStreamsResponse?> GetEpisodeStreamsAsync(string imdbId, int season, int episode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[DynamicLibrary] AIOStreams is not configured");
            return null;
        }

        try
        {
            var client = CreateClient();
            var baseUrl = GetBaseUrl();

            // Stremio addon format: /stream/series/{imdbId}:{season}:{episode}.json
            var url = $"{baseUrl}/stream/series/{imdbId}:{season}:{episode}.json";

            _logger.LogDebug("[DynamicLibrary] Fetching episode streams from AIOStreams: {Url}", url);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("[DynamicLibrary] AIOStreams returned {StatusCode} for series {ImdbId} S{Season:D2}E{Episode:D2}",
                    response.StatusCode, imdbId, season, episode);
                return null;
            }

            var result = await response.Content.ReadFromJsonAsync<AIOStreamsResponse>(cancellationToken);

            _logger.LogDebug("[DynamicLibrary] AIOStreams returned {Count} streams for series {ImdbId} S{Season:D2}E{Episode:D2}",
                result?.Streams.Count ?? 0, imdbId, season, episode);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DynamicLibrary] Error fetching episode streams from AIOStreams for {ImdbId} S{Season:D2}E{Episode:D2}",
                imdbId, season, episode);
            return null;
        }
    }
}
