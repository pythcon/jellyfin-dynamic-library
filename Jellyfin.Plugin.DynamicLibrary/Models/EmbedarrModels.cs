using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

/// <summary>
/// Request model for adding media to Embedarr library.
/// Used for both /api/admin/library/movies and /api/admin/library/tv endpoints.
/// </summary>
public class EmbedarrAddRequest
{
    /// <summary>
    /// Media ID - can be IMDB ID (string like "tt0137523") or TMDB/TVDB ID (integer).
    /// </summary>
    [JsonPropertyName("id")]
    public object Id { get; set; } = null!;
}

public class EmbedarrResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("filesCreated")]
    public List<string> FilesCreated { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

public enum MediaType
{
    Movie,
    Series,
    Anime
}

/// <summary>
/// Response from Embedarr URL endpoint containing stream URL.
/// </summary>
public class EmbedarrUrlResponse
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    [JsonPropertyName("season")]
    public int? Season { get; set; }

    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    [JsonPropertyName("audioType")]
    public string? AudioType { get; set; }
}
