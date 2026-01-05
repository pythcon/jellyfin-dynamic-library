using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

/// <summary>
/// Utility for converting subtitle formats.
/// </summary>
public static partial class SubtitleConverter
{
    [GeneratedRegex(@"(\d{2}:\d{2}:\d{2}),(\d{3})", RegexOptions.Compiled)]
    private static partial Regex SrtTimestampRegex();

    [GeneratedRegex(@"(\d{2}:\d{2}:\d{2}\.\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2}\.\d{3})[^\n]*\n([\s\S]*?)(?=\n\n|\n*$)", RegexOptions.Compiled)]
    private static partial Regex VttCueRegex();

    /// <summary>
    /// Convert SRT subtitle content to WebVTT format.
    /// </summary>
    /// <param name="srtContent">The SRT content to convert.</param>
    /// <returns>WebVTT formatted content.</returns>
    public static string SrtToWebVtt(string srtContent)
    {
        if (string.IsNullOrEmpty(srtContent))
        {
            return "WEBVTT\n\n";
        }

        var sb = new StringBuilder();
        sb.AppendLine("WEBVTT");
        sb.AppendLine();

        // Replace SRT timestamp separator (comma) with WebVTT separator (period)
        // SRT: 00:01:23,456 --> 00:01:25,789
        // VTT: 00:01:23.456 --> 00:01:25.789
        var converted = SrtTimestampRegex().Replace(srtContent, "$1.$2");

        // Remove BOM if present
        if (converted.StartsWith('\uFEFF'))
        {
            converted = converted[1..];
        }

        // Normalize line endings
        converted = converted.Replace("\r\n", "\n").Replace("\r", "\n");

        // Remove sequence numbers at the start of subtitle blocks
        // SRT format: "1\n00:00:00,000 --> 00:00:01,000\nText"
        // WebVTT doesn't require them but they're allowed
        sb.Append(converted.TrimStart());

        return sb.ToString();
    }

    /// <summary>
    /// Get ISO 639-1 language code from ISO 639-2 or other formats.
    /// </summary>
    public static string NormalizeLanguageCode(string language)
    {
        if (string.IsNullOrEmpty(language))
        {
            return "en";
        }

        // OpenSubtitles uses ISO 639-1 (2-letter) codes
        return language.ToLowerInvariant() switch
        {
            "eng" => "en",
            "spa" => "es",
            "fra" or "fre" => "fr",
            "deu" or "ger" => "de",
            "ita" => "it",
            "por" => "pt",
            "rus" => "ru",
            "jpn" => "ja",
            "kor" => "ko",
            "zho" or "chi" => "zh",
            "ara" => "ar",
            "hin" => "hi",
            "nld" or "dut" => "nl",
            "pol" => "pl",
            "tur" => "tr",
            "swe" => "sv",
            "nor" => "no",
            "dan" => "da",
            "fin" => "fi",
            "ces" or "cze" => "cs",
            "hun" => "hu",
            "ron" or "rum" => "ro",
            "ell" or "gre" => "el",
            "heb" => "he",
            "ind" => "id",
            "msa" or "may" => "ms",
            "ukr" => "uk",
            "tha" => "th",
            "vie" => "vi",
            _ => language.Length > 2 ? language[..2].ToLowerInvariant() : language.ToLowerInvariant()
        };
    }

    /// <summary>
    /// Get display name for a language code.
    /// </summary>
    public static string GetLanguageDisplayName(string languageCode)
    {
        if (string.IsNullOrEmpty(languageCode))
        {
            return "Unknown";
        }

        return languageCode.ToLowerInvariant() switch
        {
            "en" => "English",
            "es" => "Spanish",
            "fr" => "French",
            "de" => "German",
            "it" => "Italian",
            "pt" => "Portuguese",
            "ru" => "Russian",
            "ja" => "Japanese",
            "ko" => "Korean",
            "zh" => "Chinese",
            "ar" => "Arabic",
            "hi" => "Hindi",
            "nl" => "Dutch",
            "pl" => "Polish",
            "tr" => "Turkish",
            "sv" => "Swedish",
            "no" => "Norwegian",
            "da" => "Danish",
            "fi" => "Finnish",
            "cs" => "Czech",
            "hu" => "Hungarian",
            "ro" => "Romanian",
            "el" => "Greek",
            "he" => "Hebrew",
            "id" => "Indonesian",
            "ms" => "Malay",
            "uk" => "Ukrainian",
            "th" => "Thai",
            "vi" => "Vietnamese",
            _ => languageCode.ToUpperInvariant()
        };
    }

    /// <summary>
    /// Convert WebVTT content to Jellyfin TrackEvents JSON format.
    /// Used when player requests .js format for custom subtitle rendering.
    /// </summary>
    /// <param name="vttContent">The WebVTT content to convert.</param>
    /// <returns>JSON string with TrackEvents array.</returns>
    public static string WebVttToTrackEvents(string vttContent)
    {
        var trackEvents = new List<TrackEvent>();

        if (string.IsNullOrEmpty(vttContent))
        {
            return JsonSerializer.Serialize(new { TrackEvents = trackEvents });
        }

        // Normalize line endings
        var content = vttContent.Replace("\r\n", "\n").Replace("\r", "\n");

        var id = 1;
        foreach (Match match in VttCueRegex().Matches(content))
        {
            var startTicks = ParseVttTimestamp(match.Groups[1].Value);
            var endTicks = ParseVttTimestamp(match.Groups[2].Value);
            var text = match.Groups[3].Value.Trim();

            // Skip empty cues
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            trackEvents.Add(new TrackEvent
            {
                Id = id.ToString(),
                Text = text,
                StartPositionTicks = startTicks,
                EndPositionTicks = endTicks
            });
            id++;
        }

        return JsonSerializer.Serialize(new { TrackEvents = trackEvents });
    }

    /// <summary>
    /// Parse VTT timestamp to ticks (100-nanosecond units).
    /// Format: HH:MM:SS.mmm
    /// </summary>
    private static long ParseVttTimestamp(string timestamp)
    {
        var parts = timestamp.Split(':');
        if (parts.Length != 3)
        {
            return 0;
        }

        if (!int.TryParse(parts[0], out var hours) ||
            !int.TryParse(parts[1], out var minutes))
        {
            return 0;
        }

        var secondsParts = parts[2].Split('.');
        if (secondsParts.Length != 2 ||
            !int.TryParse(secondsParts[0], out var seconds) ||
            !int.TryParse(secondsParts[1], out var milliseconds))
        {
            return 0;
        }

        var totalMs = ((hours * 3600L) + (minutes * 60L) + seconds) * 1000L + milliseconds;
        return totalMs * 10000L;  // Convert to ticks (10,000 ticks = 1ms)
    }

    /// <summary>
    /// TrackEvent for Jellyfin JSON subtitle format.
    /// </summary>
    private sealed class TrackEvent
    {
        public string Id { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public long StartPositionTicks { get; set; }
        public long EndPositionTicks { get; set; }
    }
}
