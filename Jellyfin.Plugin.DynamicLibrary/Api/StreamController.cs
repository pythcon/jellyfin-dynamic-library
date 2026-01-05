using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

/// <summary>
/// Controller for stream redirect endpoints.
/// These endpoints are referenced in .strm files and redirect to the actual stream URL
/// based on current plugin configuration.
/// </summary>
[ApiController]
[Route("DynamicLibrary/Stream")]
public class StreamController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<StreamController> _logger;

    public StreamController(
        ILibraryManager libraryManager,
        ILogger<StreamController> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private PluginConfiguration Config => DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>
    /// Redirect to movie stream URL.
    /// </summary>
    /// <param name="imdbId">IMDB ID of the movie (e.g., tt1234567).</param>
    [HttpGet("movie/{imdbId}")]
    public IActionResult GetMovieStream([FromRoute] string imdbId)
    {
        _logger.LogDebug("[DynamicLibrary] Stream request for movie: {ImdbId}", imdbId);

        // Find the movie in Jellyfin by IMDB ID
        var movie = FindItemByProviderId<Movie>("Imdb", imdbId);
        if (movie == null)
        {
            _logger.LogInformation("[DynamicLibrary] Movie not found with IMDB ID: {ImdbId}", imdbId);
            return NotFound($"Movie not found: {imdbId}");
        }

        // Build stream URL from template
        var streamUrl = BuildMovieStreamUrl(movie);
        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogInformation("[DynamicLibrary] No stream URL template configured for movies");
            return BadRequest("No movie stream URL template configured");
        }

        _logger.LogInformation("[DynamicLibrary] Redirecting movie {Title} to: {Url}", movie.Name, streamUrl);
        return Redirect(streamUrl);
    }

    /// <summary>
    /// Redirect to TV episode stream URL.
    /// </summary>
    /// <param name="imdbId">IMDB ID of the series.</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    [HttpGet("tv/{imdbId}/{season:int}/{episode:int}")]
    public IActionResult GetTvStream(
        [FromRoute] string imdbId,
        [FromRoute] int season,
        [FromRoute] int episode)
    {
        _logger.LogDebug("[DynamicLibrary] Stream request for TV: {ImdbId} S{Season}E{Episode}",
            imdbId, season, episode);

        // Find the series by IMDB ID
        var series = FindItemByProviderId<Series>("Imdb", imdbId);
        if (series == null)
        {
            _logger.LogInformation("[DynamicLibrary] Series not found with IMDB ID: {ImdbId}", imdbId);
            return NotFound($"Series not found: {imdbId}");
        }

        // Find the episode
        var episodeItem = FindEpisode(series, season, episode);
        if (episodeItem == null)
        {
            _logger.LogInformation("[DynamicLibrary] Episode not found: {Series} S{Season}E{Episode}",
                series.Name, season, episode);
            return NotFound($"Episode not found: S{season}E{episode}");
        }

        // Build stream URL from template
        var streamUrl = BuildTvStreamUrl(episodeItem, series, season, episode);
        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogInformation("[DynamicLibrary] No stream URL template configured for TV");
            return BadRequest("No TV stream URL template configured");
        }

        _logger.LogInformation("[DynamicLibrary] Redirecting TV {Series} S{Season}E{Episode} to: {Url}",
            series.Name, season, episode, streamUrl);
        return Redirect(streamUrl);
    }

    /// <summary>
    /// Redirect to anime episode stream URL.
    /// </summary>
    /// <param name="anilistId">AniList ID of the anime.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="audio">Audio track (sub/dub).</param>
    [HttpGet("anime/{anilistId}/{episode:int}/{audio}")]
    public IActionResult GetAnimeStream(
        [FromRoute] string anilistId,
        [FromRoute] int episode,
        [FromRoute] string audio)
    {
        _logger.LogDebug("[DynamicLibrary] Stream request for anime: {AnilistId} E{Episode} {Audio}",
            anilistId, episode, audio);

        // Find the series by AniList ID
        var series = FindItemByProviderId<Series>("AniList", anilistId);
        if (series == null)
        {
            _logger.LogInformation("[DynamicLibrary] Anime not found with AniList ID: {AnilistId}", anilistId);
            return NotFound($"Anime not found: {anilistId}");
        }

        // Find the episode (anime typically uses Season 1)
        var episodeItem = FindEpisode(series, 1, episode);
        if (episodeItem == null)
        {
            _logger.LogInformation("[DynamicLibrary] Anime episode not found: {Series} E{Episode}",
                series.Name, episode);
            return NotFound($"Episode not found: E{episode}");
        }

        // Build stream URL from template
        var streamUrl = BuildAnimeStreamUrl(episodeItem, series, episode, audio);
        if (string.IsNullOrEmpty(streamUrl))
        {
            _logger.LogInformation("[DynamicLibrary] No stream URL template configured for anime");
            return BadRequest("No anime stream URL template configured");
        }

        _logger.LogInformation("[DynamicLibrary] Redirecting anime {Series} E{Episode} ({Audio}) to: {Url}",
            series.Name, episode, audio, streamUrl);
        return Redirect(streamUrl);
    }

    /// <summary>
    /// Find an item by provider ID.
    /// </summary>
    private T? FindItemByProviderId<T>(string providerName, string providerId) where T : BaseItem
    {
        // Map type to BaseItemKind
        BaseItemKind? itemKind = typeof(T).Name switch
        {
            nameof(Movie) => BaseItemKind.Movie,
            nameof(Series) => BaseItemKind.Series,
            _ => null
        };

        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { { providerName, providerId } },
            Recursive = true
        };

        if (itemKind.HasValue)
        {
            query.IncludeItemTypes = new[] { itemKind.Value };
        }

        var result = _libraryManager.GetItemsResult(query);
        return result.Items.FirstOrDefault() as T;
    }

    /// <summary>
    /// Find an episode by series, season, and episode number.
    /// </summary>
    private Episode? FindEpisode(Series series, int seasonNumber, int episodeNumber)
    {
        var query = new InternalItemsQuery
        {
            ParentId = series.Id,
            Recursive = true,
            IncludeItemTypes = new[] { BaseItemKind.Episode }
        };

        var episodes = _libraryManager.GetItemsResult(query);

        return episodes.Items
            .OfType<Episode>()
            .FirstOrDefault(e =>
                e.ParentIndexNumber == seasonNumber &&
                e.IndexNumber == episodeNumber);
    }

    /// <summary>
    /// Build stream URL for a movie using the configured template.
    /// </summary>
    private string? BuildMovieStreamUrl(Movie movie)
    {
        var template = Config.DirectMovieUrlTemplate;
        if (string.IsNullOrEmpty(template))
        {
            return null;
        }

        var providerIds = movie.ProviderIds ?? new Dictionary<string, string>();

        // Get preferred ID based on config
        var preferredId = GetPreferredId(providerIds, Config.MoviePreferredId);

        return template
            .Replace("{id}", preferredId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{imdb}", providerIds.GetValueOrDefault("Imdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tmdb}", providerIds.GetValueOrDefault("Tmdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", Uri.EscapeDataString(movie.Name ?? ""), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build stream URL for a TV episode using the configured template.
    /// </summary>
    private string? BuildTvStreamUrl(Episode episode, Series series, int season, int episodeNumber)
    {
        var template = Config.DirectTvUrlTemplate;
        if (string.IsNullOrEmpty(template))
        {
            return null;
        }

        var seriesProviderIds = series.ProviderIds ?? new Dictionary<string, string>();
        var episodeProviderIds = episode.ProviderIds ?? new Dictionary<string, string>();

        // Get preferred ID based on config
        var preferredId = GetPreferredId(seriesProviderIds, Config.TvShowPreferredId);

        return template
            .Replace("{id}", preferredId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{imdb}", seriesProviderIds.GetValueOrDefault("Imdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tvdb}", seriesProviderIds.GetValueOrDefault("Tvdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tmdb}", seriesProviderIds.GetValueOrDefault("Tmdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", season.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", episodeNumber.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", Uri.EscapeDataString(episode.Name ?? ""), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build stream URL for an anime episode using the configured template.
    /// </summary>
    private string? BuildAnimeStreamUrl(Episode episode, Series series, int episodeNumber, string audio)
    {
        var template = Config.DirectAnimeUrlTemplate;
        if (string.IsNullOrEmpty(template))
        {
            return null;
        }

        var seriesProviderIds = series.ProviderIds ?? new Dictionary<string, string>();
        var episodeProviderIds = episode.ProviderIds ?? new Dictionary<string, string>();

        // Get preferred ID based on config
        var preferredId = GetPreferredId(seriesProviderIds, Config.AnimePreferredId);

        // Get absolute episode number if available
        var absoluteNumber = episodeProviderIds.GetValueOrDefault("AbsoluteNumber", episodeNumber.ToString());

        return template
            .Replace("{id}", preferredId ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{imdb}", seriesProviderIds.GetValueOrDefault("Imdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{tvdb}", seriesProviderIds.GetValueOrDefault("Tvdb", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{anilist}", seriesProviderIds.GetValueOrDefault("AniList", ""), StringComparison.OrdinalIgnoreCase)
            .Replace("{season}", "1", StringComparison.OrdinalIgnoreCase)
            .Replace("{episode}", episodeNumber.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("{absolute}", absoluteNumber, StringComparison.OrdinalIgnoreCase)
            .Replace("{audio}", audio ?? "sub", StringComparison.OrdinalIgnoreCase)
            .Replace("{title}", Uri.EscapeDataString(episode.Name ?? ""), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Get the preferred provider ID based on configuration.
    /// </summary>
    private static string? GetPreferredId(Dictionary<string, string> providerIds, PreferredProviderId preference)
    {
        var fallbackOrder = preference switch
        {
            PreferredProviderId.Imdb => new[] { "Imdb", "Tmdb", "Tvdb" },
            PreferredProviderId.Tmdb => new[] { "Tmdb", "Imdb", "Tvdb" },
            PreferredProviderId.Tvdb => new[] { "Tvdb", "Imdb", "Tmdb" },
            PreferredProviderId.AniList => new[] { "AniList", "Tvdb", "Imdb" },
            _ => new[] { "Imdb", "Tmdb", "Tvdb" }
        };

        foreach (var provider in fallbackOrder)
        {
            if (providerIds.TryGetValue(provider, out var id) && !string.IsNullOrEmpty(id))
            {
                return id;
            }
        }

        return null;
    }
}
