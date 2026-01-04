using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DynamicLibrary.Configuration;

/// <summary>
/// Preferred provider ID for Embedarr lookups.
/// </summary>
public enum PreferredProviderId
{
    /// <summary>IMDB ID (tt1234567 format). Most widely supported.</summary>
    Imdb = 0,

    /// <summary>TMDB ID (numeric). Available for movies from TMDB.</summary>
    Tmdb = 1,

    /// <summary>TVDB ID (numeric). Available for TV/anime from TVDB.</summary>
    Tvdb = 2,

    /// <summary>AniList ID (numeric). Available for anime from TVDB's remoteIds.</summary>
    AniList = 3
}

/// <summary>
/// Language preference mode.
/// </summary>
public enum LanguageMode
{
    /// <summary>Use the original language from the API (no translation requests).</summary>
    Default = 0,

    /// <summary>Request content in the specified language.</summary>
    Override = 1
}

/// <summary>
/// Stream provider selection for playback.
/// </summary>
public enum StreamProvider
{
    /// <summary>No streaming - just browse metadata.</summary>
    None = 0,

    /// <summary>Use Embedarr API for STRM generation.</summary>
    Embedarr = 1,

    /// <summary>Use URL templates directly for streaming.</summary>
    Direct = 2
}

/// <summary>
/// API source for content metadata.
/// </summary>
public enum ApiSource
{
    /// <summary>Disabled - don't search this content type.</summary>
    None = 0,

    /// <summary>Use TMDB API.</summary>
    Tmdb = 1,

    /// <summary>Use TVDB API.</summary>
    Tvdb = 2
}

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
    /// Gets or sets the cache TTL in minutes.
    /// </summary>
    public int CacheTtlMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets a value indicating whether the plugin is enabled.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Gets or sets the API source for movie searches.
    /// </summary>
    public ApiSource MovieApiSource { get; set; } = ApiSource.Tmdb;

    /// <summary>
    /// Gets or sets the API source for TV show searches.
    /// </summary>
    public ApiSource TvShowApiSource { get; set; } = ApiSource.Tvdb;

    /// <summary>
    /// Gets or sets the maximum number of movie search results to return.
    /// </summary>
    public int MaxMovieResults { get; set; } = 20;

    /// <summary>
    /// Gets or sets the maximum number of TV show search results to return.
    /// </summary>
    public int MaxTvShowResults { get; set; } = 20;

    /// <summary>
    /// Gets or sets a value indicating whether to automatically add media to Embedarr
    /// when viewing item details. When false, media is only added on-demand at playback time.
    /// Default is false.
    /// </summary>
    public bool CreateMediaOnView { get; set; } = false;

    // ==================== Stream Provider Settings ====================

    /// <summary>
    /// Gets or sets the stream provider for playback.
    /// None = browse only, Embedarr = use Embedarr API, Direct = use URL templates.
    /// </summary>
    public StreamProvider StreamProvider { get; set; } = StreamProvider.None;

    /// <summary>
    /// Gets or sets the URL template for movie streams in Direct mode.
    /// Placeholders: {id} (preferred ID), {imdb}, {tmdb}, {title}
    /// </summary>
    public string DirectMovieUrlTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL template for TV show streams in Direct mode.
    /// Placeholders: {id} (preferred ID), {imdb}, {tvdb}, {season}, {episode}, {title}
    /// </summary>
    public string DirectTvUrlTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the URL template for anime streams in Direct mode.
    /// Placeholders: {id} (preferred ID), {imdb}, {tvdb}, {anilist}, {season}, {episode}, {absolute}, {audio}, {title}
    /// Use {audio} placeholder for sub/dub selection (replaced with "sub" or "dub").
    /// </summary>
    public string DirectAnimeUrlTemplate { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets whether to enable sub/dub audio version selection for anime.
    /// When enabled, shows audio track options for anime episodes.
    /// Requires {audio} placeholder in DirectAnimeUrlTemplate.
    /// Default: false
    /// </summary>
    public bool EnableAnimeAudioVersions { get; set; } = false;

    /// <summary>
    /// Gets or sets the comma-separated list of audio track options for anime.
    /// Each value is substituted into the {audio} placeholder in the URL template.
    /// Default: "sub,dub"
    /// </summary>
    public string AnimeAudioTracks { get; set; } = "sub,dub";

    // ==================== Language Settings ====================

    /// <summary>
    /// Gets or sets the language mode.
    /// Default = use original language from API.
    /// Override = force the specified language.
    /// </summary>
    public LanguageMode LanguageMode { get; set; } = LanguageMode.Default;

    /// <summary>
    /// Gets or sets the preferred language for metadata when LanguageMode is Override.
    /// Uses 3-letter ISO 639-2 codes (eng, jpn, spa, fra, deu).
    /// </summary>
    public string PreferredLanguage { get; set; } = "eng";

    // ==================== Provider ID Preferences ====================
    // These control which ID is used when calling Embedarr for playback.
    // Note: IMDB is the most universally available and recommended default.

    /// <summary>
    /// Gets or sets the preferred provider ID for movie lookups.
    /// Available options: IMDB (from TMDB details), TMDB (always available).
    /// Default: IMDB.
    /// </summary>
    public PreferredProviderId MoviePreferredId { get; set; } = PreferredProviderId.Imdb;

    /// <summary>
    /// Gets or sets the preferred provider ID for TV show lookups.
    /// Available options: IMDB (from TVDB RemoteIds), TVDB (always available).
    /// Default: IMDB.
    /// </summary>
    public PreferredProviderId TvShowPreferredId { get; set; } = PreferredProviderId.Imdb;

    /// <summary>
    /// Gets or sets the preferred provider ID for anime lookups.
    /// Available options: IMDB, TVDB, AniList (all from TVDB RemoteIds).
    /// Default: IMDB.
    /// </summary>
    public PreferredProviderId AnimePreferredId { get; set; } = PreferredProviderId.Imdb;

    // ==================== Helper Methods ====================

    /// <summary>
    /// Gets the effective language code for API requests.
    /// Returns null if LanguageMode is Default (use original).
    /// </summary>
    public string? GetEffectiveLanguage()
    {
        return LanguageMode == LanguageMode.Override ? PreferredLanguage : null;
    }

    /// <summary>
    /// Gets the 2-letter ISO 639-1 language code for TMDB API.
    /// Returns null if LanguageMode is Default.
    /// </summary>
    public string? GetTmdbLanguageCode()
    {
        if (LanguageMode != LanguageMode.Override)
        {
            return null;
        }

        return PreferredLanguage.ToLowerInvariant() switch
        {
            "eng" => "en",
            "jpn" => "ja",
            "spa" => "es",
            "fra" => "fr",
            "deu" => "de",
            "ita" => "it",
            "por" => "pt",
            "rus" => "ru",
            "zho" => "zh",
            "kor" => "ko",
            "ara" => "ar",
            "hin" => "hi",
            "tha" => "th",
            "vie" => "vi",
            "nld" => "nl",
            "pol" => "pl",
            "tur" => "tr",
            "swe" => "sv",
            "nor" => "no",
            "dan" => "da",
            "fin" => "fi",
            "ces" => "cs",
            "hun" => "hu",
            "ron" => "ro",
            "ell" => "el",
            "heb" => "he",
            "ind" => "id",
            "msa" => "ms",
            "ukr" => "uk",
            _ => "en" // Default to English if unknown
        };
    }

    /// <summary>
    /// Gets the 3-letter ISO 639-2 language code for TVDB API.
    /// Returns null if LanguageMode is Default.
    /// </summary>
    public string? GetTvdbLanguageCode()
    {
        if (LanguageMode != LanguageMode.Override)
        {
            return null;
        }

        return PreferredLanguage;
    }
}
