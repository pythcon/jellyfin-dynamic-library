using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

public class EmbedarrRequest
{
    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = string.Empty;

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; set; }

    [JsonPropertyName("tvdbId")]
    public int? TvdbId { get; set; }

    [JsonPropertyName("targetPath")]
    public string TargetPath { get; set; } = string.Empty;
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
