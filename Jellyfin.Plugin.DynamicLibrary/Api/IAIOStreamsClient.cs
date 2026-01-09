using Jellyfin.Plugin.DynamicLibrary.Models;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

/// <summary>
/// Client interface for AIOStreams Stremio addon API.
/// </summary>
public interface IAIOStreamsClient
{
    /// <summary>
    /// Gets whether the client is configured with a valid URL.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Get available streams for a movie.
    /// </summary>
    /// <param name="imdbId">IMDB ID (e.g., "tt1234567").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream response or null if failed.</returns>
    Task<AIOStreamsResponse?> GetMovieStreamsAsync(string imdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get available streams for a TV episode.
    /// </summary>
    /// <param name="imdbId">IMDB ID of the series (e.g., "tt1234567").</param>
    /// <param name="season">Season number.</param>
    /// <param name="episode">Episode number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Stream response or null if failed.</returns>
    Task<AIOStreamsResponse?> GetEpisodeStreamsAsync(string imdbId, int season, int episode, CancellationToken cancellationToken = default);
}
