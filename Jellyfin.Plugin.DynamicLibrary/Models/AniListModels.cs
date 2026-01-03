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
