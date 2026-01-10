using Jellyfin.Plugin.DynamicLibrary.Api;
using Jellyfin.Plugin.DynamicLibrary.Configuration;
using Jellyfin.Plugin.DynamicLibrary.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Providers;

/// <summary>
/// Catalog provider that uses TVDB and TMDB APIs directly.
/// This is the "Direct" option in the Catalog Provider configuration.
/// </summary>
public class DirectCatalogProvider : ICatalogProvider
{
    private readonly ITmdbClient _tmdbClient;
    private readonly ITvdbClient _tvdbClient;
    private readonly ILogger<DirectCatalogProvider> _logger;

    public DirectCatalogProvider(
        ITmdbClient tmdbClient,
        ITvdbClient tvdbClient,
        ILogger<DirectCatalogProvider> logger)
    {
        _tmdbClient = tmdbClient;
        _tvdbClient = tvdbClient;
        _logger = logger;
    }

    private PluginConfiguration Config =>
        DynamicLibraryPlugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public string ProviderName => "Direct (TVDB/TMDB)";

    /// <inheritdoc />
    public bool IsConfigured
    {
        get
        {
            var config = Config;
            // Configured if at least one API source is enabled and its client is configured
            var movieConfigured = config.MovieApiSource == ApiSource.Tmdb && _tmdbClient.IsConfigured;
            var tvConfigured = config.TvShowApiSource == ApiSource.Tvdb && _tvdbClient.IsConfigured;
            return movieConfigured || tvConfigured;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogItem>> SearchMoviesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var config = Config;

        if (config.MovieApiSource != ApiSource.Tmdb || !_tmdbClient.IsConfigured)
        {
            _logger.LogDebug("[DirectCatalog] Movie search disabled or TMDB not configured");
            return Array.Empty<CatalogItem>();
        }

        var results = await _tmdbClient.SearchMoviesAsync(query, cancellationToken);
        var imageBaseUrl = await _tmdbClient.GetImageBaseUrlAsync(cancellationToken);

        return results
            .Take(maxResults)
            .Select(r => ConvertTmdbMovieResult(r, imageBaseUrl))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CatalogItem>> SearchSeriesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default)
    {
        var config = Config;

        if (config.TvShowApiSource != ApiSource.Tvdb || !_tvdbClient.IsConfigured)
        {
            _logger.LogDebug("[DirectCatalog] TV search disabled or TVDB not configured");
            return Array.Empty<CatalogItem>();
        }

        var results = await _tvdbClient.SearchSeriesAsync(query, cancellationToken);
        var language = config.GetTvdbLanguageCode();

        return results
            .Take(maxResults)
            .Select(r => ConvertTvdbSearchResult(r, language))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CatalogItemDetails?> GetMovieDetailsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(id, out var tmdbId))
        {
            _logger.LogWarning("[DirectCatalog] Invalid TMDB movie ID: {Id}", id);
            return null;
        }

        var details = await _tmdbClient.GetMovieDetailsAsync(tmdbId, cancellationToken);
        if (details == null)
        {
            return null;
        }

        var imageBaseUrl = await _tmdbClient.GetImageBaseUrlAsync(cancellationToken);
        return ConvertTmdbMovieDetails(details, imageBaseUrl);
    }

    /// <inheritdoc />
    public async Task<CatalogItemDetails?> GetSeriesDetailsAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        if (!int.TryParse(id, out var tvdbId))
        {
            _logger.LogWarning("[DirectCatalog] Invalid TVDB series ID: {Id}", id);
            return null;
        }

        var series = await _tvdbClient.GetSeriesExtendedAsync(tvdbId, cancellationToken);
        if (series == null)
        {
            return null;
        }

        // Fetch translation if language override is configured
        var config = Config;
        var language = config.GetTvdbLanguageCode();
        TvdbTranslationData? translation = null;

        if (!string.IsNullOrEmpty(language) && series.HasTranslation(language))
        {
            translation = await _tvdbClient.GetSeriesTranslationAsync(tvdbId, language, cancellationToken);
        }

        return await ConvertTvdbSeriesExtendedAsync(series, translation, language, cancellationToken);
    }

    // ==================== Conversion Methods ====================

    private CatalogItem ConvertTmdbMovieResult(TmdbMovieResult result, string imageBaseUrl)
    {
        return new CatalogItem
        {
            Id = result.Id.ToString(),
            Source = CatalogSource.Tmdb,
            TmdbId = result.Id.ToString(),
            Name = result.Title,
            OriginalName = result.OriginalTitle != result.Title ? result.OriginalTitle : null,
            Overview = result.Overview,
            PosterUrl = !string.IsNullOrEmpty(result.PosterPath)
                ? $"{imageBaseUrl}w500{result.PosterPath}"
                : null,
            BackdropUrl = !string.IsNullOrEmpty(result.BackdropPath)
                ? $"{imageBaseUrl}original{result.BackdropPath}"
                : null,
            Year = result.Year,
            ReleaseDate = ParseDate(result.ReleaseDate),
            Rating = result.VoteAverage > 0 ? result.VoteAverage : null,
            Type = CatalogContentType.Movie,
            OriginalLanguage = result.OriginalLanguage
        };
    }

    private CatalogItemDetails ConvertTmdbMovieDetails(TmdbMovieDetails details, string imageBaseUrl)
    {
        var item = new CatalogItemDetails
        {
            Id = details.Id.ToString(),
            Source = CatalogSource.Tmdb,
            TmdbId = details.Id.ToString(),
            ImdbId = details.ImdbId,
            Name = details.Title,
            OriginalName = details.OriginalTitle != details.Title ? details.OriginalTitle : null,
            Overview = details.Overview,
            PosterUrl = !string.IsNullOrEmpty(details.PosterPath)
                ? $"{imageBaseUrl}w500{details.PosterPath}"
                : null,
            BackdropUrl = !string.IsNullOrEmpty(details.BackdropPath)
                ? $"{imageBaseUrl}original{details.BackdropPath}"
                : null,
            Year = details.Year,
            ReleaseDate = ParseDate(details.ReleaseDate),
            Rating = details.VoteAverage > 0 ? details.VoteAverage : null,
            Type = CatalogContentType.Movie,
            RuntimeMinutes = details.Runtime,
            Status = details.Status,
            Tagline = details.Tagline,
            Genres = details.Genres.Select(g => g.Name).ToList(),
            Studios = details.ProductionCompanies.Select(c => c.Name).ToList(),
            OriginalLanguage = details.SpokenLanguages.FirstOrDefault()?.Iso6391
        };

        // Add directors from crew
        if (details.Credits?.Crew != null)
        {
            item.Directors = details.Credits.Crew
                .Where(c => c.Job.Equals("Director", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Name)
                .ToList();
        }

        // Add cast
        if (details.Credits?.Cast != null)
        {
            item.Cast = details.Credits.Cast
                .OrderBy(c => c.Order)
                .Take(20)
                .Select(c => new CatalogCastMember
                {
                    Name = c.Name,
                    Character = c.Character,
                    ImageUrl = !string.IsNullOrEmpty(c.ProfilePath)
                        ? $"{imageBaseUrl}w185{c.ProfilePath}"
                        : null,
                    Order = c.Order
                })
                .ToList();
        }

        return item;
    }

    private CatalogItem ConvertTvdbSearchResult(TvdbSearchResult result, string? language)
    {
        return new CatalogItem
        {
            Id = result.TvdbIdInt.ToString(),
            Source = CatalogSource.Tvdb,
            TvdbId = result.TvdbIdInt.ToString(),
            Name = result.GetLocalizedName(language),
            OriginalName = result.Name != result.GetLocalizedName(language) ? result.Name : null,
            Overview = result.GetLocalizedOverview(language),
            PosterUrl = result.ImageUrl,
            Year = int.TryParse(result.Year, out var year) ? year : null,
            ReleaseDate = ParseDate(result.FirstAirTime),
            Type = CatalogContentType.Series,
            OriginalLanguage = result.PrimaryLanguage
        };
    }

    private async Task<CatalogItemDetails> ConvertTvdbSeriesExtendedAsync(
        TvdbSeriesExtended series,
        TvdbTranslationData? translation,
        string? language,
        CancellationToken cancellationToken)
    {
        var name = translation?.Name ?? series.Name;
        var overview = translation?.Overview ?? series.Overview;

        var item = new CatalogItemDetails
        {
            Id = series.Id.ToString(),
            Source = CatalogSource.Tvdb,
            TvdbId = series.Id.ToString(),
            ImdbId = series.ImdbId,
            Name = name,
            OriginalName = series.Name != name ? series.Name : null,
            Overview = overview,
            PosterUrl = series.Image,
            Year = int.TryParse(series.Year, out var year) ? year : null,
            ReleaseDate = ParseDate(series.FirstAired),
            Type = CatalogContentType.Series,
            RuntimeMinutes = series.AverageRuntime,
            Status = series.Status?.Name,
            Genres = series.Genres?.Select(g => g.Name).ToList() ?? new List<string>(),
            Studios = series.OriginalNetwork != null ? new List<string> { series.OriginalNetwork.Name } : new List<string>()
        };

        // Add seasons
        if (series.Seasons != null)
        {
            item.Seasons = series.Seasons
                .Where(s => s.Type?.Type == "official" || s.Type == null) // Only official seasons
                .Select(s => new CatalogSeasonInfo
                {
                    Number = s.Number,
                    Name = s.Name,
                    ImageUrl = s.Image,
                    EpisodeCount = series.Episodes?.Count(e => e.SeasonNumber == s.Number) ?? 0
                })
                .OrderBy(s => s.Number)
                .ToList();
        }

        // Add episodes with translations
        if (series.Episodes != null)
        {
            var episodes = new List<CatalogEpisodeInfo>();

            foreach (var episode in series.Episodes.OrderBy(e => e.SeasonNumber).ThenBy(e => e.Number))
            {
                var episodeName = episode.Name;
                var episodeOverview = episode.Overview;

                // Fetch episode translation if language is configured
                if (!string.IsNullOrEmpty(language))
                {
                    var episodeTranslation = await _tvdbClient.GetEpisodeTranslationAsync(
                        episode.Id, language, cancellationToken);

                    if (episodeTranslation != null)
                    {
                        episodeName = episodeTranslation.Name ?? episodeName;
                        episodeOverview = episodeTranslation.Overview ?? episodeOverview;
                    }
                }

                episodes.Add(new CatalogEpisodeInfo
                {
                    Id = episode.Id.ToString(),
                    SeasonNumber = episode.SeasonNumber,
                    EpisodeNumber = episode.Number,
                    AbsoluteNumber = episode.AbsoluteNumber,
                    Name = episodeName ?? $"Episode {episode.Number}",
                    Overview = episodeOverview,
                    AirDate = ParseDate(episode.Aired),
                    RuntimeMinutes = episode.Runtime,
                    ImageUrl = episode.Image
                });
            }

            item.Episodes = episodes;
        }

        return item;
    }

    private static DateTime? ParseDate(string? dateString)
    {
        if (string.IsNullOrEmpty(dateString))
        {
            return null;
        }

        return DateTime.TryParse(dateString, out var date) ? date : null;
    }
}
