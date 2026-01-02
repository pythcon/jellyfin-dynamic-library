using Jellyfin.Plugin.DynamicLibrary.Models;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public interface IEmbedarrClient
{
    /// <summary>
    /// Add a movie to Embedarr library (triggers STRM file creation).
    /// </summary>
    /// <param name="id">IMDB ID (string like "tt0137523") or TMDB ID (integer)</param>
    Task<EmbedarrResponse?> AddMovieAsync(object id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add a TV series to Embedarr library (triggers STRM file creation).
    /// </summary>
    /// <param name="id">IMDB ID (string like "tt0944947") or TVDB ID (integer)</param>
    Task<EmbedarrResponse?> AddTvSeriesAsync(object id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Add an anime series to Embedarr library (triggers STRM file creation).
    /// </summary>
    /// <param name="id">TVDB ID or other anime ID</param>
    Task<EmbedarrResponse?> AddAnimeAsync(object id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the stream URL for a movie.
    /// </summary>
    /// <param name="imdbId">IMDB ID (e.g., "tt0137523")</param>
    Task<EmbedarrUrlResponse?> GetMovieStreamUrlAsync(string imdbId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the stream URL for a TV episode.
    /// </summary>
    /// <param name="imdbId">IMDB ID of the series (e.g., "tt0944947")</param>
    /// <param name="season">Season number</param>
    /// <param name="episode">Episode number</param>
    Task<EmbedarrUrlResponse?> GetTvEpisodeStreamUrlAsync(string imdbId, int season, int episode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get the stream URL for an anime episode.
    /// </summary>
    /// <param name="id">Anime ID</param>
    /// <param name="episode">Episode number</param>
    /// <param name="audioType">Audio type (sub or dub)</param>
    Task<EmbedarrUrlResponse?> GetAnimeStreamUrlAsync(string id, int episode, string audioType = "sub", CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if Embedarr is configured and reachable.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the client is configured with a valid URL.
    /// </summary>
    bool IsConfigured { get; }
}
