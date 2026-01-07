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

        // Check release date if CreateUnreleasedMedia is disabled
        if (!Config.CreateUnreleasedMedia)
        {
            var releaseDate = movie.PremiereDate;
            if (releaseDate == null || releaseDate > DateTime.UtcNow)
            {
                _logger.LogDebug("[PersistenceService] Movie not yet released, skipping: {Name} (Release: {Date})",
                    movie.Name, releaseDate?.ToString("yyyy-MM-dd") ?? "unknown");
                return null;
            }
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

        // Filter to only aired episodes if CreateUnreleasedMedia is disabled
        if (!Config.CreateUnreleasedMedia)
        {
            var now = DateTime.UtcNow;
            episodes = episodes
                .Where(e => e.PremiereDate.HasValue && e.PremiereDate.Value <= now)
                .ToList();

            if (episodes.Count == 0)
            {
                _logger.LogDebug("[PersistenceService] No aired episodes for series, skipping: {Name}", series.Name);
                return null;
            }

            _logger.LogDebug("[PersistenceService] Filtered to {Count} aired episodes for series: {Name}",
                episodes.Count, series.Name);
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
            // Single .strm file - audio version selection happens at playback time via MediaSources
            strmContent = $"dynamiclibrary://anime/{anilistId}/{episodeNumber}";
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

    /// <summary>
    /// Get the folder path for a persisted series.
    /// </summary>
    public string GetSeriesFolderPath(BaseItemDto series)
    {
        var year = series.ProductionYear?.ToString() ?? "Unknown";
        var title = SanitizeFileName(series.Name ?? "Unknown");
        var providerId = GetProviderIdTag(series);
        var isAnime = DynamicLibraryService.IsAnime(series);

        var subFolder = isAnime ? "anime" : "tv";
        var folderName = $"{title} ({year}) {providerId}";
        return Path.Combine(Config.PersistentLibraryPath, subFolder, folderName);
    }

    /// <summary>
    /// Check for and persist any new episodes for an existing persisted series.
    /// </summary>
    /// <returns>The number of new episodes added.</returns>
    public async Task<int> UpdateSeriesAsync(BaseItemDto series, CancellationToken cancellationToken = default)
    {
        if (series.Type != BaseItemKind.Series)
        {
            return 0;
        }

        // Get cached episodes (already fetched by EnrichSeriesDtoAsync)
        var cachedEpisodes = _itemCache.GetEpisodesForSeries(series.Id);
        if (cachedEpisodes == null || cachedEpisodes.Count == 0)
        {
            _logger.LogDebug("[PersistenceService] No cached episodes for series: {Name}", series.Name);
            return 0;
        }

        // Get series folder path
        var seriesPath = GetSeriesFolderPath(series);
        if (!Directory.Exists(seriesPath))
        {
            _logger.LogDebug("[PersistenceService] Series not persisted yet: {Name}", series.Name);
            return 0;
        }

        // Get existing .strm files on disk
        var existingFiles = Directory.GetFiles(seriesPath, "*.strm", SearchOption.AllDirectories);
        var existingEpisodes = ParseExistingEpisodes(existingFiles);

        // Find new episodes (skip specials - season 0, filter to aired only)
        var now = DateTime.UtcNow;
        var newEpisodes = cachedEpisodes
            .Where(e => e.ParentIndexNumber.HasValue && e.ParentIndexNumber > 0)
            .Where(e => e.IndexNumber.HasValue)
            .Where(e => e.PremiereDate.HasValue && e.PremiereDate.Value <= now) // Only aired episodes
            .Where(e => !existingEpisodes.Contains((e.ParentIndexNumber!.Value, e.IndexNumber!.Value)))
            .ToList();

        if (newEpisodes.Count == 0)
        {
            _logger.LogDebug("[PersistenceService] No new episodes for series: {Name}", series.Name);
            return 0;
        }

        _logger.LogInformation("[PersistenceService] Found {Count} new episodes for series: {Name}",
            newEpisodes.Count, series.Name);

        // Get series info for persistence
        var imdbId = series.ProviderIds?.GetValueOrDefault("Imdb");
        var tvdbId = series.ProviderIds?.GetValueOrDefault("Tvdb");
        var seriesIdForUrl = imdbId ?? tvdbId;
        var isAnime = DynamicLibraryService.IsAnime(series);

        // Persist each new episode
        foreach (var episode in newEpisodes)
        {
            var seasonNumber = episode.ParentIndexNumber!.Value;
            var seasonFolder = $"Season {seasonNumber:D2}";
            var seasonPath = Path.Combine(seriesPath, seasonFolder);

            // Create season folder if needed
            Directory.CreateDirectory(seasonPath);

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

        _logger.LogInformation("[PersistenceService] Added {Count} new episodes for series: {Name}",
            newEpisodes.Count, series.Name);

        return newEpisodes.Count;
    }

    /// <summary>
    /// Parse existing .strm files to get a set of (season, episode) tuples.
    /// </summary>
    private static HashSet<(int Season, int Episode)> ParseExistingEpisodes(string[] files)
    {
        var episodes = new HashSet<(int, int)>();

        // Pattern matches "S01E01" or "S1E1" format
        var regex = new Regex(@"S(\d+)E(\d+)", RegexOptions.IgnoreCase);

        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var match = regex.Match(fileName);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, out var season) &&
                int.TryParse(match.Groups[2].Value, out var episode))
            {
                episodes.Add((season, episode));
            }
        }

        return episodes;
    }
}
