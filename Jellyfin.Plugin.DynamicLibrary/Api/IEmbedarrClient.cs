using Jellyfin.Plugin.DynamicLibrary.Models;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public interface IEmbedarrClient
{
    /// <summary>
    /// Generate STRM files for a movie.
    /// </summary>
    Task<EmbedarrResponse?> GenerateMovieAsync(int tmdbId, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Generate STRM files for a TV series.
    /// </summary>
    Task<EmbedarrResponse?> GenerateSeriesAsync(int tvdbId, string targetPath, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if Embedarr is configured and reachable.
    /// </summary>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the client is configured with a valid URL.
    /// </summary>
    bool IsConfigured { get; }
}
