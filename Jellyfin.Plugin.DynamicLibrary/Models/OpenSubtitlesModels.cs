using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

public class OpenSubtitlesSearchResponse
{
    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("data")]
    public List<OpenSubtitlesResult> Data { get; set; } = new();
}

public class OpenSubtitlesResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("attributes")]
    public OpenSubtitlesAttributes Attributes { get; set; } = new();
}

public class OpenSubtitlesAttributes
{
    [JsonPropertyName("subtitle_id")]
    public string SubtitleId { get; set; } = string.Empty;

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("download_count")]
    public int DownloadCount { get; set; }

    [JsonPropertyName("hearing_impaired")]
    public bool HearingImpaired { get; set; }

    [JsonPropertyName("fps")]
    public double Fps { get; set; }

    [JsonPropertyName("votes")]
    public int Votes { get; set; }

    [JsonPropertyName("ratings")]
    public double Ratings { get; set; }

    [JsonPropertyName("from_trusted")]
    public bool FromTrusted { get; set; }

    [JsonPropertyName("foreign_parts_only")]
    public bool ForeignPartsOnly { get; set; }

    [JsonPropertyName("ai_translated")]
    public bool AiTranslated { get; set; }

    [JsonPropertyName("machine_translated")]
    public bool MachineTranslated { get; set; }

    [JsonPropertyName("release")]
    public string? Release { get; set; }

    [JsonPropertyName("feature_details")]
    public OpenSubtitlesFeatureDetails? FeatureDetails { get; set; }

    [JsonPropertyName("files")]
    public List<OpenSubtitlesFile> Files { get; set; } = new();
}

public class OpenSubtitlesFeatureDetails
{
    [JsonPropertyName("feature_id")]
    public int FeatureId { get; set; }

    [JsonPropertyName("feature_type")]
    public string? FeatureType { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("movie_name")]
    public string? MovieName { get; set; }

    [JsonPropertyName("imdb_id")]
    public int? ImdbId { get; set; }

    [JsonPropertyName("tmdb_id")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("season_number")]
    public int? SeasonNumber { get; set; }

    [JsonPropertyName("episode_number")]
    public int? EpisodeNumber { get; set; }

    [JsonPropertyName("parent_imdb_id")]
    public int? ParentImdbId { get; set; }

    [JsonPropertyName("parent_title")]
    public string? ParentTitle { get; set; }
}

public class OpenSubtitlesFile
{
    [JsonPropertyName("file_id")]
    public int FileId { get; set; }

    [JsonPropertyName("cd_number")]
    public int CdNumber { get; set; }

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;
}

public class OpenSubtitlesDownloadRequest
{
    [JsonPropertyName("file_id")]
    public int FileId { get; set; }
}

public class OpenSubtitlesDownloadResponse
{
    [JsonPropertyName("link")]
    public string Link { get; set; } = string.Empty;

    [JsonPropertyName("file_name")]
    public string FileName { get; set; } = string.Empty;

    [JsonPropertyName("requests")]
    public int Requests { get; set; }

    [JsonPropertyName("remaining")]
    public int Remaining { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("reset_time")]
    public string? ResetTime { get; set; }

    [JsonPropertyName("reset_time_utc")]
    public DateTime? ResetTimeUtc { get; set; }
}

public class OpenSubtitlesLoginRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public class OpenSubtitlesLoginResponse
{
    [JsonPropertyName("user")]
    public OpenSubtitlesUser? User { get; set; }

    [JsonPropertyName("base_url")]
    public string? BaseUrl { get; set; }

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public int Status { get; set; }
}

public class OpenSubtitlesUser
{
    [JsonPropertyName("allowed_downloads")]
    public int AllowedDownloads { get; set; }

    [JsonPropertyName("allowed_translations")]
    public int AllowedTranslations { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = string.Empty;

    [JsonPropertyName("user_id")]
    public int UserId { get; set; }

    [JsonPropertyName("ext_installed")]
    public bool ExtInstalled { get; set; }

    [JsonPropertyName("vip")]
    public bool Vip { get; set; }
}

/// <summary>
/// Cached subtitle data for a specific item and language.
/// </summary>
public class CachedSubtitle
{
    public string Language { get; set; } = string.Empty;
    public string LanguageCode { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
    public bool HearingImpaired { get; set; }
}
