using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.AniWorld.Services;

internal static class MediaLanguage
{
    internal static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "de" or "ger" or "deu" or "german" => "deu",
        "en" or "eng" or "english" => "eng",
        "ja" or "jpn" or "japanese" => "jpn",
        _ => string.Empty,
    };

    internal static bool Matches(IEnumerable<MediaStream> streams, string variant)
    {
        var (audio, subtitles) = variant.ToLowerInvariant() switch
        {
            "german dub" => ("deu", ""),
            "english dub" => ("eng", ""),
            "german sub" => ("jpn", "deu"),
            "english sub" => ("jpn", "eng"),
            "japanese" => ("jpn", ""),
            _ => ("", ""),
        };
        var values = streams.ToArray();
        return audio.Length > 0 && values.Any(s => s.Type == MediaStreamType.Audio && Normalize(s.Language) == audio)
            && (subtitles.Length == 0 || values.Any(s => s.Type is MediaStreamType.Subtitle or MediaStreamType.Video && Normalize(s.Language) == subtitles));
    }
}
