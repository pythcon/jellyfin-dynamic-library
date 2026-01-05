using Jellyfin.Plugin.DynamicLibrary.Models;

namespace Jellyfin.Plugin.DynamicLibrary.Api;

public interface IOpenSubtitlesClient
{
    /// <summary>
    /// Search for movie subtitles by IMDB ID.
    /// </summary>
    Task<List<OpenSubtitlesResult>> SearchMovieSubtitlesAsync(
        string imdbId,
        string[] languages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Search for TV episode subtitles by parent IMDB ID and season/episode.
    /// </summary>
    Task<List<OpenSubtitlesResult>> SearchEpisodeSubtitlesAsync(
        string parentImdbId,
        int season,
        int episode,
        string[] languages,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Download a subtitle file and return the SRT content.
    /// </summary>
    Task<string?> DownloadSubtitleAsync(
        int fileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if the client is properly configured with an API key.
    /// </summary>
    bool IsConfigured { get; }
}
