using Jellyfin.Plugin.DynamicLibrary.Models;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public interface ITmdbClient
{
    /// <summary>
    /// Search for movies by title.
    /// </summary>
    Task<IReadOnlyList<TmdbMovieResult>> SearchMoviesAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed movie information.
    /// </summary>
    Task<TmdbMovieDetails?> GetMovieDetailsAsync(int movieId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get series information by external ID (e.g. IMDB ID).
    /// </summary>
    Task<TmdbSeriesDetails?> GetSeriesByExternalIdAsync(string externalId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the base URL for images.
    /// </summary>
    Task<string> GetImageBaseUrlAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the client is configured with a valid API key.
    /// </summary>
    bool IsConfigured { get; }
}
