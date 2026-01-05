using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.DynamicLibrary.Models;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public class OpenSubtitlesClient : IOpenSubtitlesClient
{
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1";
    private const string UserAgent = "Jellyfin.Plugin.DynamicLibrary v1.0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly IApplicationPaths _appPaths;
    private readonly ILogger<OpenSubtitlesClient> _logger;

    private string? _authToken;
    private DateTime _tokenExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _authLock = new(1, 1);

    // Cache key prefixes
    private const string SearchCachePrefix = "opensubtitles:search:";
    private const string SubtitleContentPrefix = "opensubtitles:content:";

    public OpenSubtitlesClient(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        IApplicationPaths appPaths,
        ILogger<OpenSubtitlesClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _appPaths = appPaths;
        _logger = logger;
    }

    private TimeSpan CacheDuration => TimeSpan.FromMinutes(
        DynamicLibraryPlugin.Instance?.Configuration.CacheTtlMinutes ?? 60);

    public bool IsConfigured => !string.IsNullOrEmpty(GetApiKey());

    private string? GetApiKey()
    {
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        if (config == null) return null;

        // First try plugin's own API key
        if (!string.IsNullOrEmpty(config.OpenSubtitlesApiKey))
        {
            return config.OpenSubtitlesApiKey;
        }

        return null;
    }

    private (string? username, string? password) GetCredentials()
    {
        var config = DynamicLibraryPlugin.Instance?.Configuration;
        if (config == null) return (null, null);

        // Try reading from Jellyfin's OpenSubtitles plugin config
        if (config.UseJellyfinOpenSubtitlesCredentials)
        {
            return ReadJellyfinOpenSubtitlesCredentials();
        }

        return (null, null);
    }

    private (string? username, string? password) ReadJellyfinOpenSubtitlesCredentials()
    {
        try
        {
            var configPath = Path.Combine(
                _appPaths.PluginConfigurationsPath,
                "Jellyfin.Plugin.OpenSubtitles.xml");

            if (!File.Exists(configPath))
            {
                _logger.LogDebug("[OpenSubtitles] Jellyfin OpenSubtitles config not found: {Path}", configPath);
                return (null, null);
            }

            var doc = XDocument.Load(configPath);
            var root = doc.Root;

            var username = root?.Element("Username")?.Value;
            var password = root?.Element("Password")?.Value;

            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                _logger.LogDebug("[OpenSubtitles] Found credentials from Jellyfin OpenSubtitles plugin");
            }

            return (username, password);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OpenSubtitles] Failed to read Jellyfin OpenSubtitles config");
            return (null, null);
        }
    }

    private async Task<string?> GetAuthTokenAsync(CancellationToken cancellationToken)
    {
        var (username, password) = GetCredentials();

        // If no credentials, we'll make unauthenticated requests (limited downloads)
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
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

            _logger.LogDebug("[OpenSubtitles] Authenticating with OpenSubtitles API");

            var client = CreateBaseClient();
            var request = new OpenSubtitlesLoginRequest
            {
                Username = username,
                Password = password
            };

            var response = await client.PostAsJsonAsync($"{BaseUrl}/login", request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("[OpenSubtitles] Login failed: {StatusCode} - {Error}", response.StatusCode, error);
                return null;
            }

            var loginResponse = await response.Content.ReadFromJsonAsync<OpenSubtitlesLoginResponse>(cancellationToken);
            if (string.IsNullOrEmpty(loginResponse?.Token))
            {
                _logger.LogWarning("[OpenSubtitles] Login response did not contain token");
                return null;
            }

            _authToken = loginResponse.Token;
            _tokenExpiry = DateTime.UtcNow.AddHours(23); // OpenSubtitles tokens last 24h

            _logger.LogDebug("[OpenSubtitles] Successfully authenticated, downloads allowed: {Count}",
                loginResponse.User?.AllowedDownloads ?? 0);

            return _authToken;
        }
        finally
        {
            _authLock.Release();
        }
    }

    private HttpClient CreateBaseClient()
    {
        var client = _httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var apiKey = GetApiKey();
        if (!string.IsNullOrEmpty(apiKey))
        {
            client.DefaultRequestHeaders.Add("Api-Key", apiKey);
        }

        return client;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken)
    {
        var client = CreateBaseClient();

        var token = await GetAuthTokenAsync(cancellationToken);
        if (!string.IsNullOrEmpty(token))
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }

    public async Task<List<OpenSubtitlesResult>> SearchMovieSubtitlesAsync(
        string imdbId,
        string[] languages,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[OpenSubtitles] Client not configured, skipping search");
            return new List<OpenSubtitlesResult>();
        }

        // Normalize IMDB ID (ensure tt prefix)
        var normalizedImdb = imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? imdbId : $"tt{imdbId}";
        var langKey = string.Join(",", languages.OrderBy(l => l));
        var cacheKey = $"{SearchCachePrefix}movie:{normalizedImdb}:{langKey}";

        if (_cache.TryGetValue<List<OpenSubtitlesResult>>(cacheKey, out var cached) && cached != null)
        {
            _logger.LogDebug("[OpenSubtitles] Cache hit for movie: {ImdbId}", normalizedImdb);
            return cached;
        }

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            var languagesParam = string.Join(",", languages);
            var url = $"{BaseUrl}/subtitles?imdb_id={normalizedImdb}&languages={languagesParam}";

            _logger.LogDebug("[OpenSubtitles] Searching for movie: {ImdbId}, languages: {Languages}",
                normalizedImdb, languagesParam);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("[OpenSubtitles] Search failed: {StatusCode} - {Error}", response.StatusCode, error);
                return new List<OpenSubtitlesResult>();
            }

            var searchResponse = await response.Content.ReadFromJsonAsync<OpenSubtitlesSearchResponse>(cancellationToken);
            var results = searchResponse?.Data ?? new List<OpenSubtitlesResult>();

            _cache.Set(cacheKey, results, CacheDuration);
            _logger.LogDebug("[OpenSubtitles] Found {Count} subtitles for movie {ImdbId}", results.Count, normalizedImdb);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenSubtitles] Error searching for movie: {ImdbId}", normalizedImdb);
            return new List<OpenSubtitlesResult>();
        }
    }

    public async Task<List<OpenSubtitlesResult>> SearchEpisodeSubtitlesAsync(
        string parentImdbId,
        int season,
        int episode,
        string[] languages,
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[OpenSubtitles] Client not configured, skipping search");
            return new List<OpenSubtitlesResult>();
        }

        var normalizedImdb = parentImdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase) ? parentImdbId : $"tt{parentImdbId}";
        var langKey = string.Join(",", languages.OrderBy(l => l));
        var cacheKey = $"{SearchCachePrefix}episode:{normalizedImdb}:s{season}e{episode}:{langKey}";

        if (_cache.TryGetValue<List<OpenSubtitlesResult>>(cacheKey, out var cached) && cached != null)
        {
            _logger.LogDebug("[OpenSubtitles] Cache hit for episode: {ImdbId} S{Season}E{Episode}",
                normalizedImdb, season, episode);
            return cached;
        }

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);
            var languagesParam = string.Join(",", languages);
            var url = $"{BaseUrl}/subtitles?parent_imdb_id={normalizedImdb}&season_number={season}&episode_number={episode}&languages={languagesParam}";

            _logger.LogDebug("[OpenSubtitles] Searching for episode: {ImdbId} S{Season}E{Episode}, languages: {Languages}",
                normalizedImdb, season, episode, languagesParam);

            var response = await client.GetAsync(url, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("[OpenSubtitles] Search failed: {StatusCode} - {Error}", response.StatusCode, error);
                return new List<OpenSubtitlesResult>();
            }

            var searchResponse = await response.Content.ReadFromJsonAsync<OpenSubtitlesSearchResponse>(cancellationToken);
            var results = searchResponse?.Data ?? new List<OpenSubtitlesResult>();

            _cache.Set(cacheKey, results, CacheDuration);
            _logger.LogDebug("[OpenSubtitles] Found {Count} subtitles for episode {ImdbId} S{Season}E{Episode}",
                results.Count, normalizedImdb, season, episode);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenSubtitles] Error searching for episode: {ImdbId} S{Season}E{Episode}",
                normalizedImdb, season, episode);
            return new List<OpenSubtitlesResult>();
        }
    }

    public async Task<string?> DownloadSubtitleAsync(int fileId, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogDebug("[OpenSubtitles] Client not configured, skipping download");
            return null;
        }

        // Check cache first
        var cacheKey = $"{SubtitleContentPrefix}{fileId}";
        if (_cache.TryGetValue<string>(cacheKey, out var cached) && cached != null)
        {
            _logger.LogDebug("[OpenSubtitles] Cache hit for subtitle file: {FileId}", fileId);
            return cached;
        }

        try
        {
            var client = await CreateAuthenticatedClientAsync(cancellationToken);

            // Step 1: Request download link
            var downloadRequest = new OpenSubtitlesDownloadRequest { FileId = fileId };
            var response = await client.PostAsJsonAsync($"{BaseUrl}/download", downloadRequest, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("[OpenSubtitles] Download request failed: {StatusCode} - {Error}",
                    response.StatusCode, error);
                return null;
            }

            var downloadResponse = await response.Content.ReadFromJsonAsync<OpenSubtitlesDownloadResponse>(cancellationToken);
            if (string.IsNullOrEmpty(downloadResponse?.Link))
            {
                _logger.LogWarning("[OpenSubtitles] Download response did not contain link");
                return null;
            }

            _logger.LogDebug("[OpenSubtitles] Got download link for file {FileId}, remaining downloads: {Remaining}",
                fileId, downloadResponse.Remaining);

            // Step 2: Download the actual subtitle file
            var downloadClient = _httpClientFactory.CreateClient();
            var content = await downloadClient.GetStringAsync(downloadResponse.Link, cancellationToken);

            // Cache the content (subtitles don't change often)
            _cache.Set(cacheKey, content, TimeSpan.FromHours(24));

            return content;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OpenSubtitles] Error downloading subtitle file: {FileId}", fileId);
            return null;
        }
    }
}
