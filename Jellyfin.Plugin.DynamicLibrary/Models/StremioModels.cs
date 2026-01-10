using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

/// <summary>
/// JSON converter that handles fields that can be either a string or an array of strings.
/// Stremio API inconsistently returns some fields as string or string[].
/// </summary>
public class StringOrArrayConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrEmpty(value) ? new List<string>() : new List<string> { value };
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray)
                    break;

                if (reader.TokenType == JsonTokenType.String)
                {
                    var item = reader.GetString();
                    if (!string.IsNullOrEmpty(item))
                        list.Add(item);
                }
            }
            return list;
        }

        // Skip unexpected token types
        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Stremio catalog response from /catalog/{type}/{id}.json or /catalog/{type}/{id}/search={query}.json.
/// </summary>
public class StremioCatalogResponse
{
    [JsonPropertyName("metas")]
    public List<StremioMetaPreview> Metas { get; set; } = new();
}

/// <summary>
/// Preview metadata from catalog (lightweight).
/// Used in search results and browse lists.
/// </summary>
public class StremioMetaPreview
{
    /// <summary>
    /// Item ID, usually IMDB ID (e.g., "tt1234567").
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Content type: "movie" or "series".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Display title.
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL to poster image.
    /// </summary>
    [JsonPropertyName("poster")]
    public string? Poster { get; set; }

    /// <summary>
    /// URL to background/fanart image.
    /// </summary>
    [JsonPropertyName("background")]
    public string? Background { get; set; }

    /// <summary>
    /// URL to logo image.
    /// </summary>
    [JsonPropertyName("logo")]
    public string? Logo { get; set; }

    /// <summary>
    /// Plot description.
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Release year or year range (e.g., "2019" or "2019-2023").
    /// </summary>
    [JsonPropertyName("releaseInfo")]
    public string? ReleaseInfo { get; set; }

    /// <summary>
    /// IMDB rating as string (e.g., "8.5").
    /// </summary>
    [JsonPropertyName("imdbRating")]
    public string? ImdbRating { get; set; }

    /// <summary>
    /// Genre list.
    /// </summary>
    [JsonPropertyName("genres")]
    public List<string>? Genres { get; set; }

    /// <summary>
    /// Runtime string (e.g., "2h 28min").
    /// </summary>
    [JsonPropertyName("runtime")]
    public string? Runtime { get; set; }

    /// <summary>
    /// Parses the release year from ReleaseInfo.
    /// </summary>
    public int? ParsedYear
    {
        get
        {
            if (string.IsNullOrEmpty(ReleaseInfo))
                return null;

            // Handle year ranges like "2019-2023" or single years like "2019"
            var yearStr = ReleaseInfo.Split('-')[0].Trim();
            return int.TryParse(yearStr, out var year) ? year : null;
        }
    }

    /// <summary>
    /// Parses the IMDB rating to a double.
    /// </summary>
    public double? ParsedRating
    {
        get
        {
            if (string.IsNullOrEmpty(ImdbRating))
                return null;

            return double.TryParse(ImdbRating, out var rating) ? rating : null;
        }
    }
}

/// <summary>
/// Full metadata response from /meta/{type}/{id}.json.
/// </summary>
public class StremioMetaResponse
{
    [JsonPropertyName("meta")]
    public StremioMeta? Meta { get; set; }
}

/// <summary>
/// Full metadata object with all details.
/// Extends StremioMetaPreview with additional fields.
/// </summary>
public class StremioMeta : StremioMetaPreview
{
    /// <summary>
    /// Cast members (names only).
    /// Stremio may return this as a string or array.
    /// </summary>
    [JsonPropertyName("cast")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string>? Cast { get; set; }

    /// <summary>
    /// Directors.
    /// Stremio may return this as a string or array.
    /// </summary>
    [JsonPropertyName("director")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string>? Director { get; set; }

    /// <summary>
    /// Writers.
    /// Stremio may return this as a string or array.
    /// </summary>
    [JsonPropertyName("writer")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string>? Writer { get; set; }

    /// <summary>
    /// Episodes/videos for series.
    /// </summary>
    [JsonPropertyName("videos")]
    public List<StremioVideo>? Videos { get; set; }

    /// <summary>
    /// External links (IMDB, Wikipedia, etc.).
    /// </summary>
    [JsonPropertyName("links")]
    public List<StremioLink>? Links { get; set; }

    /// <summary>
    /// IMDB ID explicitly.
    /// </summary>
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    /// <summary>
    /// Content country.
    /// </summary>
    [JsonPropertyName("country")]
    public string? Country { get; set; }

    /// <summary>
    /// Awards information.
    /// </summary>
    [JsonPropertyName("awards")]
    public string? Awards { get; set; }

    /// <summary>
    /// Website URL.
    /// </summary>
    [JsonPropertyName("website")]
    public string? Website { get; set; }

    /// <summary>
    /// Popularity score.
    /// </summary>
    [JsonPropertyName("popularity")]
    public double? Popularity { get; set; }

    /// <summary>
    /// Release date as ISO string.
    /// </summary>
    [JsonPropertyName("released")]
    public string? Released { get; set; }

    /// <summary>
    /// Trailer URL or ID.
    /// </summary>
    [JsonPropertyName("trailers")]
    public List<StremioTrailer>? Trailers { get; set; }

    /// <summary>
    /// Movie/show status.
    /// </summary>
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>
    /// Parses runtime string like "2h 28min" or "148 min" to minutes.
    /// </summary>
    public int? ParsedRuntimeMinutes
    {
        get
        {
            if (string.IsNullOrEmpty(Runtime))
                return null;

            var runtime = Runtime.ToLowerInvariant();
            int totalMinutes = 0;

            // Match hours
            var hourMatch = System.Text.RegularExpressions.Regex.Match(runtime, @"(\d+)\s*h");
            if (hourMatch.Success && int.TryParse(hourMatch.Groups[1].Value, out var hours))
            {
                totalMinutes += hours * 60;
            }

            // Match minutes
            var minMatch = System.Text.RegularExpressions.Regex.Match(runtime, @"(\d+)\s*min");
            if (minMatch.Success && int.TryParse(minMatch.Groups[1].Value, out var minutes))
            {
                totalMinutes += minutes;
            }

            // If only a number, assume minutes
            if (totalMinutes == 0 && int.TryParse(runtime.Trim(), out var onlyMins))
            {
                totalMinutes = onlyMins;
            }

            return totalMinutes > 0 ? totalMinutes : null;
        }
    }

    /// <summary>
    /// Parses the released date string to DateTime.
    /// </summary>
    public DateTime? ParsedReleaseDate
    {
        get
        {
            if (string.IsNullOrEmpty(Released))
                return null;

            return DateTime.TryParse(Released, out var date) ? date : null;
        }
    }
}

/// <summary>
/// Episode/video info for series.
/// </summary>
public class StremioVideo
{
    /// <summary>
    /// Video ID, typically "{imdbId}:{season}:{episode}" for series.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Episode title.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Season number.
    /// </summary>
    [JsonPropertyName("season")]
    public int Season { get; set; }

    /// <summary>
    /// Episode number within season.
    /// </summary>
    [JsonPropertyName("episode")]
    public int Episode { get; set; }

    /// <summary>
    /// Air date as ISO string.
    /// </summary>
    [JsonPropertyName("released")]
    public string? Released { get; set; }

    /// <summary>
    /// Episode overview/description.
    /// </summary>
    [JsonPropertyName("overview")]
    public string? Overview { get; set; }

    /// <summary>
    /// Episode description (alternative to overview).
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// Episode thumbnail URL.
    /// </summary>
    [JsonPropertyName("thumbnail")]
    public string? Thumbnail { get; set; }

    /// <summary>
    /// Episode name (alternative to title).
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets the best available title.
    /// </summary>
    public string BestTitle => Title ?? Name ?? $"Episode {Episode}";

    /// <summary>
    /// Gets the best available description.
    /// </summary>
    public string? BestOverview => Overview ?? Description;

    /// <summary>
    /// Parses the released date to DateTime.
    /// </summary>
    public DateTime? ParsedAirDate
    {
        get
        {
            if (string.IsNullOrEmpty(Released))
                return null;

            return DateTime.TryParse(Released, out var date) ? date : null;
        }
    }
}

/// <summary>
/// External link information.
/// </summary>
public class StremioLink
{
    /// <summary>
    /// Link name/category (e.g., "imdb", "wikipedia").
    /// </summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Link category.
    /// </summary>
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>
    /// Full URL.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

/// <summary>
/// Trailer information.
/// </summary>
public class StremioTrailer
{
    /// <summary>
    /// Trailer source (e.g., "youtube").
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Trailer type (e.g., "Trailer").
    /// </summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }
}
