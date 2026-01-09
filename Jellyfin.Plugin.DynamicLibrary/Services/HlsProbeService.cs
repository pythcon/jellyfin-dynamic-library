using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.DynamicLibrary.Api;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

/// <summary>
/// Service for probing HLS streams to extract duration information.
/// Parses m3u8 playlists to sum segment durations.
/// </summary>
public partial class HlsProbeService : IHlsProbeService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<HlsProbeService> _logger;

    private const string CachePrefix = "hls_duration:";
    private static readonly TimeSpan SuccessCacheDuration = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureCacheDuration = TimeSpan.FromMinutes(5);

    // Ticks per second (100-nanosecond intervals)
    private const long TicksPerSecond = 10_000_000;

    public HlsProbeService(
        IHttpClientFactory httpClientFactory,
        IMemoryCache cache,
        ILogger<HlsProbeService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<long?> GetHlsDurationTicksAsync(string hlsUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(hlsUrl))
        {
            return null;
        }

        // Don't probe URLs that look like they might have one-time tokens
        // These patterns suggest dynamic/session-based URLs that shouldn't be probed
        if (ContainsOneTimeTokenPatterns(hlsUrl))
        {
            _logger.LogDebug("[HlsProbeService] Skipping probe for URL with potential one-time token: {Url}", hlsUrl);
            return null;
        }

        var cacheKey = $"{CachePrefix}{hlsUrl}";

        // Check cache first
        if (_cache.TryGetValue<long?>(cacheKey, out var cachedDuration))
        {
            _logger.LogDebug("[HlsProbeService] Cache hit for HLS duration: {Url}", hlsUrl);
            return cachedDuration;
        }

        try
        {
            _logger.LogDebug("[HlsProbeService] Probing HLS playlist: {Url}", hlsUrl);

            var duration = await ProbePlaylistAsync(hlsUrl, cancellationToken);

            if (duration.HasValue)
            {
                var ticks = (long)(duration.Value * TicksPerSecond);
                _cache.Set(cacheKey, (long?)ticks, SuccessCacheDuration);
                _logger.LogDebug("[HlsProbeService] Probed HLS duration: {Duration}s ({Ticks} ticks) for {Url}",
                    duration.Value, ticks, hlsUrl);
                return ticks;
            }

            // Cache null result with shorter duration
            _cache.Set(cacheKey, (long?)null, FailureCacheDuration);
            _logger.LogDebug("[HlsProbeService] Could not determine HLS duration for {Url}", hlsUrl);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[HlsProbeService] Error probing HLS playlist: {Url}", hlsUrl);
            _cache.Set(cacheKey, (long?)null, FailureCacheDuration);
            return null;
        }
    }

    /// <summary>
    /// Check if URL contains patterns that suggest one-time or session-based tokens.
    /// We should not probe these as it may invalidate the URL for actual playback.
    /// </summary>
    private static bool ContainsOneTimeTokenPatterns(string url)
    {
        // Common patterns for one-time tokens or session URLs
        // These are often found in debrid services, CDNs with signed URLs, etc.
        var suspiciousPatterns = new[]
        {
            "token=",
            "auth=",
            "sig=",
            "signature=",
            "expires=",
            "exp=",
            "X-Amz-",  // AWS signed URLs
            "st=",     // Start time tokens
            "e=",      // Expiry tokens
            "/dl/",    // Download links (often one-time)
            "apikey=",
            "key=",
        };

        foreach (var pattern in suspiciousPatterns)
        {
            if (url.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<double?> ProbePlaylistAsync(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        // Use a HEAD request first to check content type without consuming the URL
        try
        {
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
            using var headResponse = await client.SendAsync(headRequest, cancellationToken);

            if (!headResponse.IsSuccessStatusCode)
            {
                _logger.LogDebug("[HlsProbeService] HEAD request failed with {StatusCode} for {Url}",
                    headResponse.StatusCode, url);
                return null;
            }

            // Check content type - should be application/vnd.apple.mpegurl or similar
            var contentType = headResponse.Content.Headers.ContentType?.MediaType ?? "";
            if (!IsHlsContentType(contentType) && !url.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("[HlsProbeService] Content-Type '{ContentType}' doesn't look like HLS for {Url}",
                    contentType, url);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[HlsProbeService] HEAD request failed for {Url}, trying GET", url);
            // Continue with GET request - some servers don't support HEAD
        }

        // Now fetch the actual content
        var content = await client.GetStringAsync(url, cancellationToken);

        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        // Validate this is actually an HLS playlist
        if (!IsValidHlsPlaylist(content))
        {
            _logger.LogDebug("[HlsProbeService] Content does not appear to be a valid HLS playlist for {Url}", url);
            return null;
        }

        // Check if this is a master playlist (contains variant streams)
        if (IsMasterPlaylist(content))
        {
            _logger.LogDebug("[HlsProbeService] Detected master playlist, fetching variant");
            var variantUrl = ExtractFirstVariantUrl(content, url);

            if (string.IsNullOrEmpty(variantUrl))
            {
                _logger.LogDebug("[HlsProbeService] No variant URL found in master playlist");
                return null;
            }

            // Fetch the variant playlist
            content = await client.GetStringAsync(variantUrl, cancellationToken);

            if (string.IsNullOrEmpty(content) || !IsValidHlsPlaylist(content))
            {
                return null;
            }
        }

        // Parse the media playlist for duration
        return ParseMediaPlaylistDuration(content);
    }

    /// <summary>
    /// Check if the content type indicates an HLS playlist.
    /// </summary>
    private static bool IsHlsContentType(string contentType)
    {
        return contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("x-mpegurl", StringComparison.OrdinalIgnoreCase) ||
               contentType.Contains("vnd.apple", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validate that the content is actually an HLS playlist.
    /// </summary>
    private static bool IsValidHlsPlaylist(string content)
    {
        // All HLS playlists must start with #EXTM3U
        if (!content.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Should contain HLS-specific tags
        return content.Contains("#EXT-X-", StringComparison.OrdinalIgnoreCase) ||
               content.Contains("#EXTINF:", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMasterPlaylist(string content)
    {
        // Master playlists contain #EXT-X-STREAM-INF tags
        return content.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractFirstVariantUrl(string masterPlaylist, string baseUrl)
    {
        var lines = masterPlaylist.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < lines.Length - 1; i++)
        {
            if (lines[i].StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                var variantPath = lines[i + 1].Trim();

                if (string.IsNullOrEmpty(variantPath) || variantPath.StartsWith('#'))
                {
                    continue;
                }

                return ResolveUrl(baseUrl, variantPath);
            }
        }

        return null;
    }

    private double? ParseMediaPlaylistDuration(string content)
    {
        // Only calculate duration for VOD playlists (those with #EXT-X-ENDLIST)
        // Live streams don't have a complete duration
        if (!content.Contains("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug("[HlsProbeService] Playlist does not have ENDLIST tag (live stream?), cannot determine duration");
            return null;
        }

        double totalDuration = 0;
        var matches = ExtInfRegex().Matches(content);

        foreach (Match match in matches)
        {
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var segmentDuration))
            {
                totalDuration += segmentDuration;
            }
        }

        if (totalDuration > 0)
        {
            return totalDuration;
        }

        _logger.LogDebug("[HlsProbeService] No EXTINF segments found in playlist");
        return null;
    }

    private static string ResolveUrl(string baseUrl, string relativePath)
    {
        // If it's already an absolute URL, return as-is
        if (relativePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            relativePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return relativePath;
        }

        try
        {
            var baseUri = new Uri(baseUrl);

            // If relative path starts with /, it's relative to the host
            if (relativePath.StartsWith('/'))
            {
                return new Uri(baseUri, relativePath).ToString();
            }

            // Otherwise, it's relative to the current path
            var basePath = baseUrl.LastIndexOf('/');
            if (basePath > 0)
            {
                var baseDir = baseUrl[..(basePath + 1)];
                return baseDir + relativePath;
            }

            return new Uri(baseUri, relativePath).ToString();
        }
        catch
        {
            // Fallback: just append
            return baseUrl.TrimEnd('/') + "/" + relativePath.TrimStart('/');
        }
    }

    // Regex to match #EXTINF:duration, lines
    // Examples: #EXTINF:10.0, or #EXTINF:9.96667,title
    [GeneratedRegex(@"#EXTINF:(\d+\.?\d*)", RegexOptions.Compiled)]
    private static partial Regex ExtInfRegex();
}
