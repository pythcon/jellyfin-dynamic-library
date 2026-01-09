namespace Jellyfin.Plugin.DynamicLibrary.Api;

/// <summary>
/// Service for probing HLS streams to extract duration information.
/// </summary>
public interface IHlsProbeService
{
    /// <summary>
    /// Gets the duration of an HLS stream in ticks by parsing the m3u8 playlist.
    /// </summary>
    /// <param name="hlsUrl">The URL of the HLS m3u8 playlist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Duration in ticks (100-nanosecond intervals), or null if unable to determine.</returns>
    Task<long?> GetHlsDurationTicksAsync(string hlsUrl, CancellationToken cancellationToken = default);
}
