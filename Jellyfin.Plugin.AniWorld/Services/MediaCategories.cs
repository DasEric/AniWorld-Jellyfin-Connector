using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AniWorld.Configuration;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>Canonical request categories, independent of a provider's legacy series API.</summary>
public static class MediaCategories
{
    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["movie"] = "Filme", ["series"] = "Serien / Anime", ["manga"] = "Manga",
        ["ebook"] = "E-Books", ["audiobook"] = "Hörbücher", ["podcast"] = "Podcasts",
    };

    public static bool IsCatalog(string? value) => value is "ebook" or "audiobook" or "podcast";

    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" or "film" or "filme" => "movie",
        "series" or "tv" or "serie" or "serien" => "series",
        "manga" => "manga", "ebook" => "ebook", "audiobook" => "audiobook", "podcast" => "podcast", _ => null,
    };

    public static bool IsAllowed(string category, PluginConfiguration config) => category switch
    {
        "movie" or "series" or "manga" => true,
        "ebook" => config.AllowEbookRequests, "audiobook" => config.AllowAudiobookRequests,
        "podcast" => config.AllowPodcastRequests, _ => false,
    };

    public static IReadOnlyList<object> Available(PluginConfiguration config) => Labels
        .Where(pair => IsAllowed(pair.Key, config))
        .Select(pair => (object)new { id = pair.Key, label = pair.Value }).ToArray();
}

/// <summary>All meaningful words must match a title/alias or a catalog author.</summary>
internal static class SearchRelevance
{
    private static readonly HashSet<string> StopWords = ["the", "a", "an", "der", "die", "das", "ein", "eine", "film", "movie", "serie", "series"];

    internal static string Normalize(string? value)
    {
        var text = System.Net.WebUtility.HtmlDecode(Regex.Replace(value ?? string.Empty, "<[^>]*>", " "));
        var buffer = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            buffer.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Regex.Replace(buffer.ToString(), @"\s+", " ").Trim();
    }

    internal static int Score(JsonElement item, string query)
    {
        if (item.ValueKind != JsonValueKind.Object) return 0;
        string Read(string name) => item.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? field.ToString() : string.Empty;
        var normalizedQuery = Normalize(query);
        var words = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (words.Count == 0) return 0;
        var year = words.Count > 1 ? words.FirstOrDefault(word => Regex.IsMatch(word, @"^(19|20)\d{2}$")) : null;
        if (year is not null)
        {
            var knownYear = Read("year");
            if (string.IsNullOrEmpty(knownYear)) knownYear = Read("release_year");
            if (string.IsNullOrEmpty(knownYear)) knownYear = Regex.Match(Read("title"), @"\b(19|20)\d{2}\b").Value;
            if (knownYear.Length >= 4 && knownYear[..4] != year) return 0;
            words.Remove(year);
        }
        var significant = words.Where(word => !StopWords.Contains(word)).ToArray();
        if (significant.Length == 0) significant = words.ToArray();
        if (significant.Length == 0) return 0;
        var titles = new List<string> { Read("title"), Read("name"), Read("original_title") };
        if (item.TryGetProperty("alternative_titles", out var aliases))
        {
            if (aliases.ValueKind == JsonValueKind.String) titles.Add(aliases.GetString()!);
            else if (aliases.ValueKind == JsonValueKind.Array)
                titles.AddRange(aliases.EnumerateArray().Where(alias => alias.ValueKind == JsonValueKind.String).Select(alias => alias.GetString()!));
        }
        var author = MediaCategories.IsCatalog(Read("media_kind")) ? Normalize(Read("author")) : string.Empty;
        var phrase = string.Join(' ', words);
        var best = 0;
        foreach (var title in titles)
        {
            var text = Normalize(title);
            if (text.Length == 0) continue;
            var tokens = (text + " " + author).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!significant.All(word => tokens.Any(token => token == word || (word.Length >= 3 && token.StartsWith(word, StringComparison.Ordinal))))) continue;
            var score = text == normalizedQuery || text == phrase ? 1000 : text.StartsWith(phrase, StringComparison.Ordinal) ? 900 : 800;
            best = Math.Max(best, score - Math.Min(100, Math.Max(0, tokens.Length - significant.Length)));
        }
        return best;
    }

    internal static JsonElement Filter(JsonElement response, string query, string? category)
    {
        var key = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("items", out _) ? "items" : "results";
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array)
            return JsonSerializer.SerializeToElement(new { results = Array.Empty<object>() });
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var filtered = items.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object)
            .Select(item => (Item: item, Score: Score(item, query)))
            .Where(entry => entry.Score > 0)
            .Where(entry =>
            {
                if (category is null) return true;
                if (!entry.Item.TryGetProperty("media_type", out var kind) && !entry.Item.TryGetProperty("media_kind", out kind)) return true;
                return kind.ValueKind == JsonValueKind.String && (string.IsNullOrWhiteSpace(kind.GetString()) || MediaCategories.Normalize(kind.GetString()) == category);
            })
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Item.TryGetProperty("title", out var title) ? title.ToString() : string.Empty, StringComparer.OrdinalIgnoreCase)
            .Where(entry => seen.Add(entry.Item.TryGetProperty("url", out var url) ? url.ToString() : entry.Item.TryGetProperty("id", out var id) ? id.ToString() : entry.Item.GetRawText()))
            .Take(100)
            .Select(entry =>
            {
                var fields = entry.Item.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
                fields["relevance"] = entry.Score;
                return fields;
            }).ToArray();
        return JsonSerializer.SerializeToElement(new { results = filtered });
    }
}
