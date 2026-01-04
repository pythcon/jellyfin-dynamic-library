using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

public class TvdbAuthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public TvdbAuthData? Data { get; set; }
}

public class TvdbAuthData
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;
}

public class TvdbSearchResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public List<TvdbSearchResult> Data { get; set; } = new();
}

public class TvdbSearchResult
{
    [JsonPropertyName("objectID")]
    public string ObjectId { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("first_air_time")]
    public string? FirstAirTime { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("primary_language")]
    public string? PrimaryLanguage { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("tvdb_id")]
    public string TvdbId { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("network")]
    public string? Network { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("translations")]
    public Dictionary<string, string>? Translations { get; set; }

    [JsonPropertyName("overviews")]
    public Dictionary<string, string>? Overviews { get; set; }

    public int TvdbIdInt => int.TryParse(TvdbId, out var id) ? id : 0;

    /// <summary>
    /// Gets the localized name for the specified language code.
    /// Falls back to the default Name if translation is not available.
    /// </summary>
    /// <param name="languageCode">3-letter ISO 639-2 code (e.g., "eng", "jpn")</param>
    public string GetLocalizedName(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return Name;
        }

        if (Translations?.TryGetValue(languageCode, out var translated) == true
            && !string.IsNullOrEmpty(translated))
        {
            return translated;
        }
        return Name;
    }

    /// <summary>
    /// Gets the localized overview for the specified language code.
    /// Falls back to the default Overview if translation is not available.
    /// </summary>
    /// <param name="languageCode">3-letter ISO 639-2 code (e.g., "eng", "jpn")</param>
    public string? GetLocalizedOverview(string? languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return Overview;
        }

        if (Overviews?.TryGetValue(languageCode, out var translated) == true
            && !string.IsNullOrEmpty(translated))
        {
            return translated;
        }
        return Overview;
    }
}

public class TvdbSeriesResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public TvdbSeriesExtended? Data { get; set; }
}

public class TvdbSeriesExtended
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = string.Empty;

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("firstAired")]
    public string? FirstAired { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("year")]
    public string? Year { get; set; }

    [JsonPropertyName("status")]
    public TvdbStatus? Status { get; set; }

    [JsonPropertyName("originalNetwork")]
    public TvdbNetwork? OriginalNetwork { get; set; }

    [JsonPropertyName("averageRuntime")]
    public int? AverageRuntime { get; set; }

    [JsonPropertyName("genres")]
    public List<TvdbGenre>? Genres { get; set; }

    [JsonPropertyName("seasons")]
    public List<TvdbSeason>? Seasons { get; set; }

    [JsonPropertyName("episodes")]
    public List<TvdbEpisode>? Episodes { get; set; }

    [JsonPropertyName("remoteIds")]
    public List<TvdbRemoteId>? RemoteIds { get; set; }

    /// <summary>
    /// List of available language codes for name translations.
    /// Actual translations must be fetched via /series/{id}/translations/{language}
    /// </summary>
    [JsonPropertyName("nameTranslations")]
    public List<string>? NameTranslations { get; set; }

    /// <summary>
    /// List of available language codes for overview translations.
    /// Actual translations must be fetched via /series/{id}/translations/{language}
    /// </summary>
    [JsonPropertyName("overviewTranslations")]
    public List<string>? OverviewTranslations { get; set; }

    /// <summary>
    /// Fetched translation data (populated separately via translation API call).
    /// </summary>
    [JsonIgnore]
    public TvdbTranslationData? Translation { get; set; }

    public string? ImdbId => RemoteIds?.FirstOrDefault(r => r.SourceName == "IMDB")?.Id;

    /// <summary>
    /// Gets the AniList ID from remote IDs if available.
    /// </summary>
    public string? AniListId => RemoteIds?.FirstOrDefault(r =>
        r.SourceName.Equals("AniList", StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// Gets the MyAnimeList (MAL) ID from remote IDs if available.
    /// </summary>
    public string? MalId => RemoteIds?.FirstOrDefault(r =>
        r.SourceName.Equals("MyAnimeList", StringComparison.OrdinalIgnoreCase) ||
        r.SourceName.Equals("MAL", StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>
    /// Check if a translation is available for the given language.
    /// </summary>
    public bool HasTranslation(string languageCode)
    {
        return NameTranslations?.Contains(languageCode, StringComparer.OrdinalIgnoreCase) == true;
    }
}

public class TvdbStatus
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class TvdbNetwork
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class TvdbGenre
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class TvdbSeason
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("type")]
    public TvdbSeasonType? Type { get; set; }
}

public class TvdbSeasonType
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;
}

public class TvdbEpisode
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("seriesId")]
    public int SeriesId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; }

    [JsonPropertyName("number")]
    public int Number { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }

    [JsonPropertyName("aired")]
    public string? Aired { get; set; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; set; }

    [JsonPropertyName("absoluteNumber")]
    public int? AbsoluteNumber { get; set; }
}

public class TvdbRemoteId
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; set; }

    [JsonPropertyName("sourceName")]
    public string SourceName { get; set; } = string.Empty;
}

/// <summary>
/// Response from /series/{id}/translations/{language} endpoint.
/// </summary>
public class TvdbTranslationResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("data")]
    public TvdbTranslationData? Data { get; set; }
}

/// <summary>
/// Translation data for a specific language.
/// </summary>
public class TvdbTranslationData
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("aliases")]
    public List<string>? Aliases { get; set; }

    [JsonPropertyName("isAlias")]
    public bool IsAlias { get; set; }

    [JsonPropertyName("isPrimary")]
    public bool IsPrimary { get; set; }
}
