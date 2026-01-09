using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

/// <summary>
/// Response from AIOStreams stream endpoint.
/// </summary>
public class AIOStreamsResponse
{
    /// <summary>
    /// Gets or sets the list of available streams.
    /// </summary>
    [JsonPropertyName("streams")]
    public List<AIOStream> Streams { get; set; } = new();
}

/// <summary>
/// Individual stream from AIOStreams.
/// </summary>
public class AIOStream
{
    /// <summary>
    /// Gets or sets the stream source name (e.g., "AIOStreams", "Torrentio").
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the stream title/description (e.g., "1080p WEB-DL x264").
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets the direct stream URL.
    /// </summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>
    /// Gets or sets the info hash for torrent streams.
    /// </summary>
    [JsonPropertyName("infoHash")]
    public string? InfoHash { get; set; }

    /// <summary>
    /// Gets or sets the file index for torrent streams.
    /// </summary>
    [JsonPropertyName("fileIdx")]
    public int? FileIndex { get; set; }

    /// <summary>
    /// Gets or sets behavior hints for the stream.
    /// </summary>
    [JsonPropertyName("behaviorHints")]
    public AIOBehaviorHints? BehaviorHints { get; set; }

    /// <summary>
    /// Gets a display name combining source and title.
    /// </summary>
    public string DisplayName => !string.IsNullOrEmpty(Title)
        ? Title
        : Name ?? "Unknown Stream";
}

/// <summary>
/// Behavior hints for AIOStreams streams.
/// </summary>
public class AIOBehaviorHints
{
    /// <summary>
    /// Gets or sets the binge group for grouping related streams.
    /// </summary>
    [JsonPropertyName("bingeGroup")]
    public string? BingeGroup { get; set; }

    /// <summary>
    /// Gets or sets whether the stream should not be downloaded.
    /// </summary>
    [JsonPropertyName("notWebReady")]
    public bool? NotWebReady { get; set; }

    /// <summary>
    /// Gets or sets proxy headers for the stream.
    /// </summary>
    [JsonPropertyName("proxyHeaders")]
    public Dictionary<string, string>? ProxyHeaders { get; set; }

    /// <summary>
    /// Gets or sets the filename hint for the stream.
    /// This can be used to determine the container type.
    /// </summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }
}

/// <summary>
/// Helper class for detecting container types from URLs/filenames.
/// </summary>
public static class StreamContainerHelper
{
    // Browser-compatible containers that can be played directly
    private static readonly HashSet<string> BrowserCompatibleContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "webm", "hls"
    };

    // Containers that typically require transcoding/proxy for browser playback
    private static readonly HashSet<string> NonBrowserContainers = new(StringComparer.OrdinalIgnoreCase)
    {
        "mkv", "avi", "mov", "wmv", "flv", "ts"
    };

    private static readonly Dictionary<string, string> ExtensionToContainer = new(StringComparer.OrdinalIgnoreCase)
    {
        { ".mkv", "mkv" },
        { ".mp4", "mp4" },
        { ".avi", "avi" },
        { ".webm", "webm" },
        { ".m3u8", "hls" },
        { ".ts", "ts" },
        { ".mov", "mov" },
        { ".wmv", "wmv" },
        { ".flv", "flv" }
    };

    /// <summary>
    /// Detect container type from URL or filename.
    /// HLS URLs (.m3u8) are checked first since they are definitive.
    /// Defaults to MKV when unknown (safest for debrid/usenet streams).
    /// </summary>
    /// <param name="url">The stream URL.</param>
    /// <param name="filename">Optional filename hint (from behaviorHints).</param>
    /// <returns>Container type string.</returns>
    public static string DetectContainer(string? url, string? filename = null)
    {
        // 1. FIRST: Check URL for HLS indicators - these are definitive
        // HLS URLs should always be detected as HLS regardless of filename hint
        if (!string.IsNullOrEmpty(url))
        {
            // Check for .m3u8 extension in URL path (handles query strings)
            try
            {
                var uri = new Uri(url);
                var pathExt = Path.GetExtension(uri.AbsolutePath);
                if (pathExt.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    return "hls";
                }
            }
            catch
            {
                // URL parsing failed, try fallback
            }

            // Fallback: check for .m3u8 anywhere in URL
            if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                return "hls";
            }
        }

        // 2. Check filename extension (for non-HLS streams)
        if (!string.IsNullOrEmpty(filename))
        {
            var ext = Path.GetExtension(filename);
            if (!string.IsNullOrEmpty(ext) && ExtensionToContainer.TryGetValue(ext, out var container))
            {
                return container;
            }
        }

        // 3. Check URL path extension (non-HLS containers)
        if (!string.IsNullOrEmpty(url))
        {
            try
            {
                var uri = new Uri(url);
                var ext = Path.GetExtension(uri.AbsolutePath);
                if (!string.IsNullOrEmpty(ext) && ExtensionToContainer.TryGetValue(ext, out var container))
                {
                    return container;
                }
            }
            catch
            {
                // URL parsing failed
            }
        }

        // 4. Default to mkv (safest for debrid/usenet - Jellyfin will handle it)
        return "mkv";
    }

    /// <summary>
    /// Check if a container is natively supported by browsers.
    /// </summary>
    public static bool IsBrowserCompatible(string container)
    {
        return BrowserCompatibleContainers.Contains(container);
    }
}
