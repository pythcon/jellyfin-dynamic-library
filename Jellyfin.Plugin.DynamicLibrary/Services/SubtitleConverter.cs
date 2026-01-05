using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.DynamicLibrary.Services;

/// <summary>
/// Utility for converting subtitle formats.
/// </summary>
public static partial class SubtitleConverter
{
    [GeneratedRegex(@"(\d{2}:\d{2}:\d{2}),(\d{3})", RegexOptions.Compiled)]
    private static partial Regex SrtTimestampRegex();

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
}
