using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

/// <summary>
/// Service for persisting dynamic items as .strm files in the filesystem.
/// </summary>
public class PersistenceService
{
    private readonly ILibraryManager _libraryManager;
    private readonly DynamicItemCache _itemCache;
    private readonly ILogger<PersistenceService> _logger;

    // Invalid filesystem characters
    private static readonly Regex InvalidCharsRegex = new(@"[<>:""/\\|?*]", RegexOptions.Compiled);

    public PersistenceService(
        ILibraryManager libraryManager,
        DynamicItemCache itemCache,
        ILogger<PersistenceService> logger)
    {
        _libraryManager = libraryManager;
        _itemCache = itemCache;
        _logger = logger;
    }

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Persist a movie as a .strm file.
    /// </summary>
    /// <returns>The path to the created folder, or null if already exists or failed.</returns>
    public async Task<string?> PersistMovieAsync(BaseItemDto movie, CancellationToken cancellationToken = default)
    {
        if (movie.Type != BaseItemKind.Movie)
        {
            _logger.LogDebug("[PersistenceService] Item is not a movie: {Name} ({Type})", movie.Name, movie.Type);
            return null;
        }

        // Check if already exists in library
        var imdbId = movie.ProviderIds?.GetValueOrDefault("Imdb");
        var tmdbId = movie.ProviderIds?.GetValueOrDefault("Tmdb");

        if (!string.IsNullOrEmpty(imdbId) && ItemExistsInLibrary("Imdb", imdbId))
        {
            _logger.LogDebug("[PersistenceService] Movie already exists in library: {Name} (IMDB: {ImdbId})", movie.Name, imdbId);
            return null;
        }

        // Build folder and file paths
        var year = movie.ProductionYear?.ToString() ?? "Unknown";
        var title = SanitizeFileName(movie.Name ?? "Unknown");
        var providerId = GetProviderIdTag(movie);

        var folderName = $"{title} ({year}) {providerId}";
        var fileName = $"{title} ({year}).strm";

        var folderPath = Path.Combine(Config.PersistentLibraryPath, "movies", folderName);
        var filePath = Path.Combine(folderPath, fileName);

        // Create folder
        Directory.CreateDirectory(folderPath);

        // Build .strm content with placeholder URL
        // Uses dynamiclibrary:// scheme so PlaybackInfoFilter can intercept and generate real stream URL
        var strmContent = $"dynamiclibrary://movie/{imdbId ?? tmdbId}";

        // Write .strm file
        await File.WriteAllTextAsync(filePath, strmContent, cancellationToken);

        _logger.LogInformation("[PersistenceService] Created movie: {Path}", filePath);
        return folderPath;
    }

    /// <summary>
    /// Persist a TV series with all its episodes as .strm files.
    /// </summary>
    /// <returns>The path to the created folder, or null if already exists or failed.</returns>
    public async Task<string?> PersistSeriesAsync(BaseItemDto series, CancellationToken cancellationToken = default)
    {
        if (series.Type != BaseItemKind.Series)
        {
            _logger.LogDebug("[PersistenceService] Item is not a series: {Name} ({Type})", series.Name, series.Type);
            return null;
        }

        // Check if already exists in library
        var imdbId = series.ProviderIds?.GetValueOrDefault("Imdb");
        var tvdbId = series.ProviderIds?.GetValueOrDefault("Tvdb");

        if (!string.IsNullOrEmpty(imdbId) && ItemExistsInLibrary("Imdb", imdbId))
        {
            _logger.LogDebug("[PersistenceService] Series already exists in library: {Name} (IMDB: {ImdbId})", series.Name, imdbId);
            return null;
        }

        if (!string.IsNullOrEmpty(tvdbId) && ItemExistsInLibrary("Tvdb", tvdbId))
        {
            _logger.LogDebug("[PersistenceService] Series already exists in library: {Name} (TVDB: {TvdbId})", series.Name, tvdbId);
            return null;
        }

        // Get episodes from cache
        var episodes = _itemCache.GetEpisodesForSeries(series.Id);
        if (episodes == null || episodes.Count == 0)
        {
            _logger.LogInformation("[PersistenceService] No episodes found for series: {Name}", series.Name);
            return null;
        }

        // Build folder path
        var year = series.ProductionYear?.ToString() ?? "Unknown";
        var title = SanitizeFileName(series.Name ?? "Unknown");
        var providerId = GetProviderIdTag(series);
        var isAnime = DynamicLibraryService.IsAnime(series);

        var subFolder = isAnime ? "anime" : "tv";
        var folderName = $"{title} ({year}) {providerId}";
        var seriesPath = Path.Combine(Config.PersistentLibraryPath, subFolder, folderName);

        // Create series folder
        Directory.CreateDirectory(seriesPath);

        var seriesIdForUrl = imdbId ?? tvdbId;

        // Group episodes by season
        var episodesBySeason = episodes
            .Where(e => e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue)
            .GroupBy(e => e.ParentIndexNumber!.Value)
            .OrderBy(g => g.Key);

        foreach (var seasonGroup in episodesBySeason)
        {
            var seasonNumber = seasonGroup.Key;
            var seasonFolder = $"Season {seasonNumber:D2}";
            var seasonPath = Path.Combine(seriesPath, seasonFolder);

            Directory.CreateDirectory(seasonPath);

            foreach (var episode in seasonGroup.OrderBy(e => e.IndexNumber))
            {
                await PersistEpisodeAsync(
                    episode,
                    series,
                    seriesPath,
                    seasonPath,
                    seasonNumber,
                    seriesIdForUrl!,
                    isAnime,
                    cancellationToken);
            }
        }

        _logger.LogInformation("[PersistenceService] Created series with {Count} episodes: {Path}",
            episodes.Count, seriesPath);
        return seriesPath;
    }

    /// <summary>
    /// Persist a single episode as a .strm file.
    /// </summary>
    private async Task PersistEpisodeAsync(
        BaseItemDto episode,
        BaseItemDto series,
        string seriesPath,
        string seasonPath,
        int seasonNumber,
        string seriesIdForUrl,
        bool isAnime,
        CancellationToken cancellationToken)
    {
        var episodeNumber = episode.IndexNumber ?? 1;
        var seriesTitle = SanitizeFileName(series.Name ?? "Unknown");
        var episodeTitle = SanitizeFileName(episode.Name ?? $"Episode {episodeNumber}");

        // Format: "Series Name - S01E01 - Episode Title.strm"
        var fileName = $"{seriesTitle} - S{seasonNumber:D2}E{episodeNumber:D2} - {episodeTitle}.strm";
        var filePath = Path.Combine(seasonPath, fileName);

        // Build .strm content with placeholder URL
        // Uses dynamiclibrary:// scheme so PlaybackInfoFilter can intercept and generate real stream URL
        string strmContent;
        if (isAnime)
        {
            var anilistId = series.ProviderIds?.GetValueOrDefault("AniList") ?? seriesIdForUrl;
            // For anime, use absolute episode number and default to sub
            strmContent = $"dynamiclibrary://anime/{anilistId}/{episodeNumber}/sub";
        }
        else
        {
            strmContent = $"dynamiclibrary://tv/{seriesIdForUrl}/{seasonNumber}/{episodeNumber}";
        }

        await File.WriteAllTextAsync(filePath, strmContent, cancellationToken);

        _logger.LogDebug("[PersistenceService] Created episode: {Path}", filePath);
    }

    /// <summary>
    /// Check if an item with the given provider ID exists in Jellyfin's library.
    /// </summary>
    public bool ItemExistsInLibrary(string providerName, string providerId)
    {
        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { { providerName, providerId } },
            Recursive = true
        };

        var result = _libraryManager.GetItemsResult(query);
        return result.TotalRecordCount > 0;
    }

    /// <summary>
    /// Get the provider ID tag for folder naming (e.g., "[imdbid-tt1234567]").
    /// </summary>
    private static string GetProviderIdTag(BaseItemDto item)
    {
        var providerIds = item.ProviderIds;
        if (providerIds == null)
        {
            return "";
        }

        // Check for AniList first (for anime)
        if (providerIds.TryGetValue("AniList", out var anilistId) && !string.IsNullOrEmpty(anilistId))
        {
            return $"[anilistid-{anilistId}]";
        }

        // Then IMDB
        if (providerIds.TryGetValue("Imdb", out var imdbId) && !string.IsNullOrEmpty(imdbId))
        {
            return $"[imdbid-{imdbId}]";
        }

        // Then TVDB
        if (providerIds.TryGetValue("Tvdb", out var tvdbId) && !string.IsNullOrEmpty(tvdbId))
        {
            return $"[tvdbid-{tvdbId}]";
        }

        // Then TMDB
        if (providerIds.TryGetValue("Tmdb", out var tmdbId) && !string.IsNullOrEmpty(tmdbId))
        {
            return $"[tmdbid-{tmdbId}]";
        }

        return "";
    }

    /// <summary>
    /// Sanitize a string for use in file/folder names.
    /// </summary>
    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return "Unknown";
        }

        // Remove invalid characters
        var sanitized = InvalidCharsRegex.Replace(name, "");

        // Trim whitespace and dots from ends
        sanitized = sanitized.Trim().TrimEnd('.');

        // Ensure not empty after sanitization
        return string.IsNullOrEmpty(sanitized) ? "Unknown" : sanitized;
    }

    /// <summary>
    /// Trigger a library scan for the specified path.
    /// </summary>
    public void TriggerLibraryScan()
    {
        if (!Config.TriggerLibraryScan)
        {
            _logger.LogDebug("[PersistenceService] Library scan disabled in config");
            return;
        }

        try
        {
            // Queue a library scan
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("[PersistenceService] Triggered library scan");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PersistenceService] Failed to trigger library scan");
        }
    }
}
