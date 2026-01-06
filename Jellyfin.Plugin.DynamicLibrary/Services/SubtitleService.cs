using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

/// <summary>
/// Service for fetching and managing subtitles for dynamic items.
/// </summary>
public class SubtitleService
{
    private readonly IOpenSubtitlesClient _openSubtitlesClient;
    private readonly DynamicItemCache _itemCache;
    private readonly ILogger<SubtitleService> _logger;

    public SubtitleService(
        IOpenSubtitlesClient openSubtitlesClient,
        DynamicItemCache itemCache,
        ILogger<SubtitleService> logger)
    {
        _openSubtitlesClient = openSubtitlesClient;
        _itemCache = itemCache;
        _logger = logger;
    }

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Check if subtitles are enabled and configured.
    /// </summary>
    public bool IsEnabled
    {
        get
        {
            var configEnabled = Config.EnableSubtitles;
            var clientConfigured = _openSubtitlesClient.IsConfigured;
            _logger.LogDebug("[SubtitleService] IsEnabled check: EnableSubtitles={ConfigEnabled}, ClientConfigured={ClientConfigured}",
                configEnabled, clientConfigured);
            return configEnabled && clientConfigured;
        }
    }

    /// <summary>
    /// Get the subtitle cache path, ensuring the directory exists.
    /// </summary>
    private string GetSubtitleCachePath()
    {
        var path = Config.SubtitleCachePath;
        if (string.IsNullOrEmpty(path))
        {
            path = "/tmp/jellyfin-dynamiclibrary-subtitles";
        }

        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
            _logger.LogDebug("[SubtitleService] Created subtitle cache directory: {Path}", path);
        }

        return path;
    }

    /// <summary>
    /// Get configured subtitle languages.
    /// </summary>
    private string[] GetLanguages()
    {
        var languages = Config.SubtitleLanguages;
        if (string.IsNullOrEmpty(languages))
        {
            return new[] { "en" };
        }

        return languages
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => SubtitleConverter.NormalizeLanguageCode(l))
            .Distinct()
            .ToArray();
    }

    /// <summary>
    /// Fetch and cache subtitles for a movie.
    /// </summary>
    public async Task<List<CachedSubtitle>> FetchMovieSubtitlesAsync(
        BaseItemDto movie,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new List<CachedSubtitle>();
        }

        // Check cache first
        var cached = _itemCache.GetSubtitles(movie.Id);
        if (cached != null)
        {
            _logger.LogDebug("[SubtitleService] Using cached subtitles for movie: {Name}", movie.Name);
            return cached;
        }

        // Get IMDB ID
        var imdbId = movie.ProviderIds?.GetValueOrDefault("Imdb");
        if (string.IsNullOrEmpty(imdbId))
        {
            _logger.LogDebug("[SubtitleService] No IMDB ID for movie: {Name}", movie.Name);
            return new List<CachedSubtitle>();
        }

        var languages = GetLanguages();
        _logger.LogDebug("[SubtitleService] Fetching subtitles for movie: {Name} ({ImdbId}), languages: {Languages}",
            movie.Name, imdbId, string.Join(",", languages));

        var results = await _openSubtitlesClient.SearchMovieSubtitlesAsync(imdbId, languages, cancellationToken);

        var subtitles = await ProcessSearchResultsAsync(movie.Id, results, languages, cancellationToken);

        _itemCache.StoreSubtitles(movie.Id, subtitles);
        return subtitles;
    }

    /// <summary>
    /// Fetch and cache subtitles for an episode.
    /// </summary>
    public async Task<List<CachedSubtitle>> FetchEpisodeSubtitlesAsync(
        BaseItemDto episode,
        BaseItemDto? series,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return new List<CachedSubtitle>();
        }

        // Check cache first
        var cached = _itemCache.GetSubtitles(episode.Id);
        if (cached != null)
        {
            _logger.LogDebug("[SubtitleService] Using cached subtitles for episode: {Name}", episode.Name);
            return cached;
        }

        // Get parent IMDB ID from series
        var parentImdbId = series?.ProviderIds?.GetValueOrDefault("Imdb")
            ?? episode.ProviderIds?.GetValueOrDefault("Imdb");

        if (string.IsNullOrEmpty(parentImdbId))
        {
            _logger.LogDebug("[SubtitleService] No IMDB ID for episode: {Name}", episode.Name);
            return new List<CachedSubtitle>();
        }

        var season = episode.ParentIndexNumber ?? 1;
        var episodeNum = episode.IndexNumber ?? 1;
        var languages = GetLanguages();

        _logger.LogDebug("[SubtitleService] Fetching subtitles for episode: {Name} S{Season}E{Episode} ({ImdbId}), languages: {Languages}",
            episode.Name, season, episodeNum, parentImdbId, string.Join(",", languages));

        var results = await _openSubtitlesClient.SearchEpisodeSubtitlesAsync(
            parentImdbId, season, episodeNum, languages, cancellationToken);

        var subtitles = await ProcessSearchResultsAsync(episode.Id, results, languages, cancellationToken);

        _itemCache.StoreSubtitles(episode.Id, subtitles);
        return subtitles;
    }

    /// <summary>
    /// Process search results and download subtitles for each language.
    /// </summary>
    private async Task<List<CachedSubtitle>> ProcessSearchResultsAsync(
        Guid itemId,
        List<OpenSubtitlesResult> results,
        string[] requestedLanguages,
        CancellationToken cancellationToken)
    {
        var subtitles = new List<CachedSubtitle>();
        var cachePath = GetSubtitleCachePath();
        var maxPerLanguage = Math.Max(1, Config.MaxSubtitlesPerLanguage);

        foreach (var language in requestedLanguages)
        {
            // Find top N subtitles for this language (highest download count, prefer non-machine translated)
            var humanResults = results
                .Where(r => r.Attributes.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                .Where(r => !r.Attributes.MachineTranslated)
                .OrderByDescending(r => r.Attributes.DownloadCount)
                .Take(maxPerLanguage)
                .ToList();

            // If we don't have enough human translated, fill with machine translated
            if (humanResults.Count < maxPerLanguage)
            {
                var machineResults = results
                    .Where(r => r.Attributes.Language.Equals(language, StringComparison.OrdinalIgnoreCase))
                    .Where(r => r.Attributes.MachineTranslated)
                    .OrderByDescending(r => r.Attributes.DownloadCount)
                    .Take(maxPerLanguage - humanResults.Count)
                    .ToList();
                humanResults.AddRange(machineResults);
            }

            if (humanResults.Count == 0)
            {
                _logger.LogDebug("[SubtitleService] No subtitle found for language: {Language}", language);
                continue;
            }

            // Process each result for this language
            for (var i = 0; i < humanResults.Count; i++)
            {
                var result = humanResults[i];
                if (result.Attributes.Files.Count == 0)
                {
                    continue;
                }

                var fileId = result.Attributes.Files[0].FileId;

                // Use index in filename to support multiple subtitles per language
                var fileName = maxPerLanguage > 1 ? $"{itemId}_{language}_{i}.vtt" : $"{itemId}_{language}.vtt";
                var filePath = Path.Combine(cachePath, fileName);

                // Check if we already have this file cached on disk
                if (File.Exists(filePath))
                {
                    _logger.LogDebug("[SubtitleService] Using cached subtitle file: {Path}", filePath);
                    subtitles.Add(new CachedSubtitle
                    {
                        Language = SubtitleConverter.GetLanguageDisplayName(language) + (i > 0 ? $" ({i + 1})" : ""),
                        LanguageCode = maxPerLanguage > 1 ? $"{language}_{i}" : language,
                        FilePath = filePath,
                        CachedAt = File.GetLastWriteTimeUtc(filePath),
                        HearingImpaired = result.Attributes.HearingImpaired
                    });
                    continue;
                }

                var srtContent = await _openSubtitlesClient.DownloadSubtitleAsync(fileId, cancellationToken);

                if (string.IsNullOrEmpty(srtContent))
                {
                    _logger.LogInformation("[SubtitleService] Failed to download subtitle file: {FileId}", fileId);
                    continue;
                }

                // Convert SRT to WebVTT
                var vttContent = SubtitleConverter.SrtToWebVtt(srtContent);

                // Save to disk
                try
                {
                    await File.WriteAllTextAsync(filePath, vttContent, cancellationToken);
                    _logger.LogDebug("[SubtitleService] Saved subtitle to: {Path}", filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[SubtitleService] Failed to save subtitle to: {Path}", filePath);
                    continue;
                }

                subtitles.Add(new CachedSubtitle
                {
                    Language = SubtitleConverter.GetLanguageDisplayName(language) + (i > 0 ? $" ({i + 1})" : ""),
                    LanguageCode = maxPerLanguage > 1 ? $"{language}_{i}" : language,
                    FilePath = filePath,
                    CachedAt = DateTime.UtcNow,
                    HearingImpaired = result.Attributes.HearingImpaired
                });

                _logger.LogDebug("[SubtitleService] Downloaded and converted subtitle: {Language} ({Index})", language, i + 1);
            }
        }

        return subtitles;
    }

    /// <summary>
    /// Get the WebVTT content for a cached subtitle.
    /// </summary>
    public async Task<string?> GetSubtitleContentAsync(Guid itemId, string languageCode, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("[SubtitleService] GetSubtitleContent request: ItemId={ItemId}, Language={Language}", itemId, languageCode);

        // First try the memory cache
        var subtitle = _itemCache.GetSubtitle(itemId, languageCode);
        if (subtitle != null)
        {
            _logger.LogDebug("[SubtitleService] Found subtitle in cache: {FilePath}", subtitle.FilePath);

            if (File.Exists(subtitle.FilePath))
            {
                _logger.LogDebug("[SubtitleService] Serving subtitle from cache: {FilePath}", subtitle.FilePath);
                return await File.ReadAllTextAsync(subtitle.FilePath, cancellationToken);
            }

            _logger.LogInformation("[SubtitleService] Cached subtitle file not found on disk: {Path}", subtitle.FilePath);
        }
        else
        {
            _logger.LogDebug("[SubtitleService] Subtitle not found in memory cache: {ItemId}, {Language}", itemId, languageCode);
        }

        // Fall back to checking disk directly (handles cache expiration)
        var normalizedLanguage = SubtitleConverter.NormalizeLanguageCode(languageCode);
        var cachePath = GetSubtitleCachePath();
        var expectedFilePath = Path.Combine(cachePath, $"{itemId}_{normalizedLanguage}.vtt");

        _logger.LogDebug("[SubtitleService] Checking disk fallback: {Path}", expectedFilePath);

        if (File.Exists(expectedFilePath))
        {
            _logger.LogDebug("[SubtitleService] Found subtitle on disk (cache miss recovery): {Path}", expectedFilePath);
            return await File.ReadAllTextAsync(expectedFilePath, cancellationToken);
        }

        _logger.LogInformation("[SubtitleService] Subtitle not found in cache or on disk: {ItemId}, {Language}, ExpectedPath={Path}",
            itemId, languageCode, expectedFilePath);
        return null;
    }
}
