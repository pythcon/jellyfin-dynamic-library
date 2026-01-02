using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DynamicLibrary.Models;

/// <summary>
/// Represents a URI for virtual items from external APIs.
/// Format: dynamic://{source}/{externalId}
/// Examples: dynamic://tmdb/12345, dynamic://tvdb/67890
/// </summary>
public sealed class DynamicUri
{
    public DynamicSource Source { get; }
    public string ExternalId { get; }

    public DynamicUri(DynamicSource source, string externalId)
    {
        if (string.IsNullOrWhiteSpace(externalId))
            throw new ArgumentException("externalId cannot be null or empty.", nameof(externalId));

        Source = source;
        ExternalId = externalId;
    }

    private static readonly Regex Rx = new(
        @"^dynamic://(?<source>tmdb|tvdb)/(?<id>[^/\s]+)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static DynamicUri? TryParse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var m = Rx.Match(value);
        if (!m.Success)
            return null;

        var sourceStr = m.Groups["source"].Value.ToLowerInvariant();
        var id = m.Groups["id"].Value;

        var source = sourceStr switch
        {
            "tmdb" => DynamicSource.Tmdb,
            "tvdb" => DynamicSource.Tvdb,
            _ => throw new FormatException($"Unknown source: {sourceStr}")
        };

        return new DynamicUri(source, id);
    }

    public static DynamicUri FromTmdb(int id) => new(DynamicSource.Tmdb, id.ToString());
    public static DynamicUri FromTvdb(int id) => new(DynamicSource.Tvdb, id.ToString());

    public override string ToString()
    {
        var source = Source switch
        {
            DynamicSource.Tmdb => "tmdb",
            DynamicSource.Tvdb => "tvdb",
            _ => throw new InvalidOperationException($"Unknown source: {Source}")
        };
        return $"dynamic://{source}/{ExternalId}";
    }

    /// <summary>
    /// Generate a stable GUID from this URI for use as a Jellyfin item ID.
    /// Uses a unique prefix to avoid collisions with real Jellyfin items.
    /// </summary>
    public Guid ToGuid()
    {
        // Add unique prefix to avoid GUID collisions with real Jellyfin items
        const string UniquePrefix = "jellyfin-dynamiclibrary-plugin:";
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes(UniquePrefix + ToString()));
        return new Guid(hash);
    }

    /// <summary>
    /// Create a provider ID string for marking items as dynamic.
    /// </summary>
    public string ToProviderIdValue() => $"pending:{Source.ToString().ToLower()}:{ExternalId}";

    /// <summary>
    /// Try to parse a provider ID value back to a DynamicUri.
    /// </summary>
    public static DynamicUri? FromProviderIdValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("pending:"))
            return null;

        var parts = value.Split(':');
        if (parts.Length != 3)
            return null;

        var source = parts[1].ToLowerInvariant() switch
        {
            "tmdb" => DynamicSource.Tmdb,
            "tvdb" => DynamicSource.Tvdb,
            _ => (DynamicSource?)null
        };

        if (source is null)
            return null;

        return new DynamicUri(source.Value, parts[2]);
    }
}

public enum DynamicSource
{
    Tmdb,
    Tvdb
}
