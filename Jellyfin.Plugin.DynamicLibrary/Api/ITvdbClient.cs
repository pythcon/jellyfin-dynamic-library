using Jellyfin.Plugin.DynamicLibrary.Models;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public interface ITvdbClient
{
    /// <summary>
    /// Search for series by name.
    /// </summary>
    Task<IReadOnlyList<TvdbSearchResult>> SearchSeriesAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get detailed series information including seasons and episodes.
    /// </summary>
    Task<TvdbSeriesExtended?> GetSeriesExtendedAsync(int seriesId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get translation for a series in a specific language.
    /// </summary>
    Task<TvdbTranslationData?> GetSeriesTranslationAsync(int seriesId, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get translation for an episode in a specific language.
    /// </summary>
    Task<TvdbTranslationData?> GetEpisodeTranslationAsync(int episodeId, string language, CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the client is configured with a valid API key.
    /// </summary>
    bool IsConfigured { get; }
}
