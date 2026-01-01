using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DynamicLibrary.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the TVDB API key for TV shows and anime.
    /// </summary>
    public string TvdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TMDB API key for movies.
    /// </summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Embedarr URL for STRM file generation.
    /// </summary>
    public string EmbedarrUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the Embedarr API key (if required).
    /// </summary>
    public string EmbedarrApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path where movie STRM files should be created.
    /// </summary>
    public string MovieLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path where TV show STRM files should be created.
    /// </summary>
    public string TvLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path where anime STRM files should be created.
    /// </summary>
    public string AnimeLibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the path for caching API responses.
    /// </summary>
    public string CachePath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the cache TTL in minutes.
    /// </summary>
    public int CacheTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to search movies.
    /// </summary>
    public bool SearchMovies { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to search TV shows.
    /// </summary>
    public bool SearchTvShows { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether to search anime.
    /// </summary>
    public bool SearchAnime { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of search results to return per API.
    /// </summary>
    public int MaxSearchResults { get; set; } = 20;

    /// <summary>
    /// Gets or sets the preferred language for metadata.
    /// Uses 3-letter ISO 639-2 codes (eng, jpn, spa, fra, deu).
    /// Default is English.
    /// </summary>
    public string PreferredLanguage { get; set; } = "eng";
}
