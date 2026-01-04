using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

/// <summary>
/// AniList GraphQL response wrapper.
/// </summary>
public class AniListMediaResponse
{
    [JsonPropertyName("data")]
    public AniListData? Data { get; set; }
}

/// <summary>
/// AniList data container.
/// </summary>
public class AniListData
{
    [JsonPropertyName("Media")]
    public AniListMedia? Media { get; set; }
}

/// <summary>
/// AniList media (anime) object.
/// </summary>
public class AniListMedia
{
    /// <summary>
    /// The AniList ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// The MyAnimeList ID (if linked).
    /// </summary>
    [JsonPropertyName("idMal")]
    public int? IdMal { get; set; }

    /// <summary>
    /// The anime titles in various languages.
    /// </summary>
    [JsonPropertyName("title")]
    public AniListTitle? Title { get; set; }
}

/// <summary>
/// AniList title object with multiple language variants.
/// </summary>
public class AniListTitle
{
    /// <summary>
    /// Romanized Japanese title.
    /// </summary>
    [JsonPropertyName("romaji")]
    public string? Romaji { get; set; }

    /// <summary>
    /// English title.
    /// </summary>
    [JsonPropertyName("english")]
    public string? English { get; set; }

    /// <summary>
    /// Native (Japanese) title.
    /// </summary>
    [JsonPropertyName("native")]
    public string? Native { get; set; }
}

/// <summary>
/// AniList GraphQL response wrapper for Page queries (multiple results).
/// </summary>
public class AniListPageResponse
{
    [JsonPropertyName("data")]
    public AniListPageData? Data { get; set; }
}

/// <summary>
/// AniList page data container.
/// </summary>
public class AniListPageData
{
    [JsonPropertyName("Page")]
    public AniListPage? Page { get; set; }
}

/// <summary>
/// AniList page with media results.
/// </summary>
public class AniListPage
{
    [JsonPropertyName("media")]
    public List<AniListMediaWithYear>? Media { get; set; }
}

/// <summary>
/// AniList media object with year information for matching.
/// </summary>
public class AniListMediaWithYear
{
    /// <summary>
    /// The AniList ID.
    /// </summary>
    [JsonPropertyName("id")]
    public int Id { get; set; }

    /// <summary>
    /// The MyAnimeList ID (if linked).
    /// </summary>
    [JsonPropertyName("idMal")]
    public int? IdMal { get; set; }

    /// <summary>
    /// The year the anime started airing.
    /// </summary>
    [JsonPropertyName("seasonYear")]
    public int? SeasonYear { get; set; }

    /// <summary>
    /// The anime titles in various languages.
    /// </summary>
    [JsonPropertyName("title")]
    public AniListTitle? Title { get; set; }
}
