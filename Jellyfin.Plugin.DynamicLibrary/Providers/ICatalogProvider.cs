namespace Jellyfin.Plugin.DynamicLibrary.Providers;

/// <summary>
/// Abstraction for catalog/metadata providers.
/// Implementations include Direct (TVDB/TMDB) and Stremio addon providers.
/// </summary>
public interface ICatalogProvider
{
    /// <summary>
    /// Gets whether the provider is configured and ready to use.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Gets the provider name for logging purposes.
    /// </summary>
    string ProviderName { get; }

    /// <summary>
    /// Search for movies by query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching catalog items.</returns>
    Task<IReadOnlyList<CatalogItem>> SearchMoviesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for TV series by query.
    /// </summary>
    /// <param name="query">The search query.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of matching catalog items.</returns>
    Task<IReadOnlyList<CatalogItem>> SearchSeriesAsync(
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed metadata for a movie.
    /// </summary>
    /// <param name="id">The movie ID (format depends on provider).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detailed catalog item with full metadata, or null if not found.</returns>
    Task<CatalogItemDetails?> GetMovieDetailsAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed metadata for a series including seasons and episodes.
    /// </summary>
    /// <param name="id">The series ID (format depends on provider).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Detailed catalog item with full metadata including episodes, or null if not found.</returns>
    Task<CatalogItemDetails?> GetSeriesDetailsAsync(
        string id,
        CancellationToken cancellationToken = default);
}
