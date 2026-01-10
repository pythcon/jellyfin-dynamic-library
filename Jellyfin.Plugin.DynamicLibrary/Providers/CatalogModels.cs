namespace Jellyfin.Plugin.DynamicLibrary.Providers;

/// <summary>
/// Content type enumeration.
/// </summary>
public enum CatalogContentType
{
    Movie,
    Series
}

/// <summary>
/// Unified catalog item representation from any provider.
/// Contains basic information for search results and browse lists.
/// </summary>
public class CatalogItem
{
    /// <summary>
    /// Primary ID for this item (format depends on provider source).
    /// For Stremio: typically IMDB ID (tt1234567).
    /// For Direct/TMDB: TMDB ID as string.
    /// For Direct/TVDB: TVDB ID as string.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The source provider that supplied this item.
    /// </summary>
    public CatalogSource Source { get; set; }

    /// <summary>
    /// IMDB ID if available (e.g., "tt1234567").
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// TMDB ID if available.
    /// </summary>
    public string? TmdbId { get; set; }

    /// <summary>
    /// TVDB ID if available.
    /// </summary>
    public string? TvdbId { get; set; }

    /// <summary>
    /// Display name/title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Original title (if different from localized name).
    /// </summary>
    public string? OriginalName { get; set; }

    /// <summary>
    /// Plot summary or description.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// URL to poster image.
    /// </summary>
    public string? PosterUrl { get; set; }

    /// <summary>
    /// URL to backdrop/fanart image.
    /// </summary>
    public string? BackdropUrl { get; set; }

    /// <summary>
    /// Release year.
    /// </summary>
    public int? Year { get; set; }

    /// <summary>
    /// Full release date if available.
    /// </summary>
    public DateTime? ReleaseDate { get; set; }

    /// <summary>
    /// Community rating (e.g., IMDB rating).
    /// </summary>
    public double? Rating { get; set; }

    /// <summary>
    /// Content type (Movie or Series).
    /// </summary>
    public CatalogContentType Type { get; set; }

    /// <summary>
    /// List of genres.
    /// </summary>
    public List<string> Genres { get; set; } = new();

    /// <summary>
    /// Original language code.
    /// </summary>
    public string? OriginalLanguage { get; set; }
}

/// <summary>
/// Extended catalog item with full metadata details.
/// Used when viewing item details (not search results).
/// </summary>
public class CatalogItemDetails : CatalogItem
{
    /// <summary>
    /// Runtime in minutes.
    /// </summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>
    /// Status (e.g., "Released", "Continuing", "Ended").
    /// </summary>
    public string? Status { get; set; }

    /// <summary>
    /// Tagline if available.
    /// </summary>
    public string? Tagline { get; set; }

    /// <summary>
    /// List of directors.
    /// </summary>
    public List<string> Directors { get; set; } = new();

    /// <summary>
    /// Cast members with roles.
    /// </summary>
    public List<CatalogCastMember> Cast { get; set; } = new();

    /// <summary>
    /// Seasons (for series only).
    /// </summary>
    public List<CatalogSeasonInfo> Seasons { get; set; } = new();

    /// <summary>
    /// Episodes (for series only).
    /// </summary>
    public List<CatalogEpisodeInfo> Episodes { get; set; } = new();

    /// <summary>
    /// Studios or production companies.
    /// </summary>
    public List<string> Studios { get; set; } = new();

    /// <summary>
    /// Country/countries of origin.
    /// </summary>
    public List<string> Countries { get; set; } = new();
}

/// <summary>
/// Cast member information.
/// </summary>
public class CatalogCastMember
{
    /// <summary>
    /// Actor's name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Character name played.
    /// </summary>
    public string? Character { get; set; }

    /// <summary>
    /// URL to actor's image.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Sort order in credits.
    /// </summary>
    public int Order { get; set; }
}

/// <summary>
/// Season information for series.
/// </summary>
public class CatalogSeasonInfo
{
    /// <summary>
    /// Season number (0 for specials).
    /// </summary>
    public int Number { get; set; }

    /// <summary>
    /// Season name if available.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// URL to season poster.
    /// </summary>
    public string? ImageUrl { get; set; }

    /// <summary>
    /// Number of episodes in this season.
    /// </summary>
    public int EpisodeCount { get; set; }
}

/// <summary>
/// Episode information for series.
/// </summary>
public class CatalogEpisodeInfo
{
    /// <summary>
    /// Episode ID (format depends on provider).
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Season number.
    /// </summary>
    public int SeasonNumber { get; set; }

    /// <summary>
    /// Episode number within the season.
    /// </summary>
    public int EpisodeNumber { get; set; }

    /// <summary>
    /// Absolute episode number (for anime).
    /// </summary>
    public int? AbsoluteNumber { get; set; }

    /// <summary>
    /// Episode title.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Episode plot summary.
    /// </summary>
    public string? Overview { get; set; }

    /// <summary>
    /// Air date.
    /// </summary>
    public DateTime? AirDate { get; set; }

    /// <summary>
    /// Runtime in minutes.
    /// </summary>
    public int? RuntimeMinutes { get; set; }

    /// <summary>
    /// URL to episode thumbnail.
    /// </summary>
    public string? ImageUrl { get; set; }
}

/// <summary>
/// Source of catalog data.
/// </summary>
public enum CatalogSource
{
    /// <summary>
    /// TMDB (The Movie Database).
    /// </summary>
    Tmdb,

    /// <summary>
    /// TVDB (The TV Database).
    /// </summary>
    Tvdb,

    /// <summary>
    /// Stremio addon (Cinemeta, AIOMetadata, etc.).
    /// </summary>
    Stremio
}
