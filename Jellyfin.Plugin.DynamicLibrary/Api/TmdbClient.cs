using System.Net.Http.Json;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public class TmdbClient : ITmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string DefaultImageBaseUrl = "https://image.tmdb.org/t/p/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<TmdbClient> _logger;

    private string? _imageBaseUrl;
    private readonly SemaphoreSlim _configLock = new(1, 1);

    public TmdbClient(IHttpClientFactory httpClientFactory, ILogger<TmdbClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

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

        try
        {
            var client = CreateClient();
            var encodedQuery = Uri.EscapeDataString(query);
            var language = GetLanguageCode();
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

            _logger.LogDebug("TMDB search returned {Count} results", results.Count);
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

        try
        {
            var client = CreateClient();
            var language = GetLanguageCode();
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

            return await response.Content.ReadFromJsonAsync<TmdbMovieDetails>(cancellationToken);
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
