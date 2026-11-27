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
        ["music"] = "Musik",
    };

    public static bool IsCatalog(string? value) => value is "ebook" or "audiobook" or "podcast";

    /// <summary>Music is served by the downloader's Soulseek client, not by a video or catalog source.</summary>
    public static bool IsMusic(string? value) => value == "music";

    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "movie" or "movies" or "film" or "filme" => "movie",
        "series" or "tv" or "serie" or "serien" => "series",
        "manga" => "manga", "ebook" => "ebook", "audiobook" => "audiobook", "podcast" => "podcast",
        "music" or "musik" => "music", _ => null,
    };

    public static bool IsAllowed(string category, PluginConfiguration config) => category switch
    {
        "movie" or "series" or "manga" => true,
        "ebook" => config.AllowEbookRequests, "audiobook" => config.AllowAudiobookRequests,
        "podcast" => config.AllowPodcastRequests, "music" => config.AllowMusicRequests, _ => false,
    };

    // Search groups are UI/API filters, never persisted request media types.
    public static string? NormalizeSearch(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "video" => "video", "catalog" => "catalog", _ => Normalize(value),
    };

    public static IReadOnlyList<string> SearchTypes(string category, PluginConfiguration config) => Labels.Keys
        .Where(kind => IsAllowed(kind, config) && Matches(category, kind)).ToArray();

    public static bool Matches(string category, string kind) => category switch
    {
        "video" => kind is "movie" or "series" or "manga",
        "catalog" => IsCatalog(kind),
        _ => category == kind,
    };

    public static IReadOnlyList<object> Available(PluginConfiguration config) => new[]
        {
            (Id: "video", Label: "Serien & Filme"),
            (Id: "catalog", Label: "Bücher & Audio"),
            (Id: "music", Label: "Musik"),
        }
        .Select(group => new { id = group.Id, label = group.Label, media_types = SearchTypes(group.Id, config) })
        .Where(group => group.media_types.Count > 0).Cast<object>().ToArray();
}

/// <summary>Preserves upstream search results while enforcing the requested category.</summary>
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

    // Relevance from 0 to 1 with typo tolerance: every significant query word is
    // compared with the best title word (exact, prefix, or up to 1-2 typos).
    internal static double Relevance(JsonElement item, string query)
    {
        if (item.ValueKind != JsonValueKind.Object) return 0;
        string Read(string name) => item.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? field.ToString() : string.Empty;
        var words = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
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
        if (significant.Length == 0) return year is not null ? 1 : 0;
        var titles = new List<string> { Read("title"), Read("name"), Read("original_title") };
        if (item.TryGetProperty("alternative_titles", out var aliases))
        {
            if (aliases.ValueKind == JsonValueKind.String) titles.Add(aliases.GetString()!);
            else if (aliases.ValueKind == JsonValueKind.Array)
                titles.AddRange(aliases.EnumerateArray().Where(alias => alias.ValueKind == JsonValueKind.String).Select(alias => alias.GetString()!));
        }
        var author = MediaCategories.IsCatalog(Read("media_kind")) ? Normalize(Read("author")) : string.Empty;
        var compactQuery = string.Concat(significant);
        var best = 0d;
        foreach (var title in titles)
        {
            var text = Normalize(title);
            if (text.Length == 0) continue;
            var tokens = (text + " " + author).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var score = significant.Sum(word => tokens.Max(token => WordSimilarity(word, token))) / significant.Length;
            // "brokegirls" or "2broke girls": compare the joined spelling as well.
            var compactTitle = string.Concat(text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(token => !StopWords.Contains(token)));
            if (compactQuery.Length >= 5 && compactTitle.Length is > 0 and <= 60)
            {
                var whole = Similarity(compactQuery, compactTitle);
                if (whole >= 0.8) score = Math.Max(score, whole);
            }
            best = Math.Max(best, score);
        }
        return best;
    }

    private static double WordSimilarity(string word, string token)
    {
        if (word == token) return 1;
        // Numbers and very short words must match exactly: "2" is no hint for "2 Broke Girls".
        if (word.Length <= 2 || word.All(char.IsDigit) || token.All(char.IsDigit)) return 0;
        if (word.Length >= 3 && token.StartsWith(word, StringComparison.Ordinal)) return 0.9;
        if (word.Length < 4) return 0;
        var similarity = Similarity(word, token);
        var allowed = word.Length >= 8 ? 2 : 1;
        return OptimalStringAlignment(word, token) <= allowed ? similarity : 0;
    }

    private static double Similarity(string left, string right)
        => 1d - (double)OptimalStringAlignment(left, right) / Math.Max(left.Length, right.Length);

    // Edit distance counting a swapped letter pair ("grils" -> "girls") as one typo.
    private static int OptimalStringAlignment(string left, string right)
    {
        var d = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= right.Length; j++) d[0, j] = j;
        for (var i = 1; i <= left.Length; i++)
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[left.Length, right.Length];
    }

    private const double StrongMatch = 0.75;
    private const double KeepMatch = 0.7;

    internal static JsonElement Filter(JsonElement response, string query, string? category, AniWorldSiteInfo? source = null)
    {
        var key = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("items", out _) ? "items" : "results";
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty(key, out var items) || items.ValueKind != JsonValueKind.Array)
            throw new AniWorldApplicationException(System.Net.HttpStatusCode.BadGateway, "Die Quelle hat keine gültige Trefferliste geliefert.");
        if (response.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(error.GetString()))
            throw new AniWorldApplicationException(System.Net.HttpStatusCode.BadGateway, "Die Quellensuche ist fehlgeschlagen.");
        JsonElement? NormalizeItem(JsonElement item)
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            if (source is null) return item;
            var fields = item.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
            var field = source.Backend == "catalog" ? "media_kind" : "media_type";
            var kind = item.TryGetProperty(field, out var raw) && raw.ValueKind == JsonValueKind.String ? MediaCategories.Normalize(raw.GetString()) : null;
            if (kind is null && source.MediaTypes.Count == 1) kind = source.MediaTypes[0];
            if (kind is not null && !source.MediaTypes.Contains(kind, StringComparer.Ordinal)) return null;
            if (source.Backend == "catalog" && kind is null) return null;
            fields[field] = kind ?? string.Empty;
            fields["source"] = source.Id;
            return JsonSerializer.SerializeToElement(fields);
        }
        var filtered = items.EnumerateArray()
            .Select(NormalizeItem).Where(item => item.HasValue).Select(item => item!.Value)
            .Where(item =>
            {
                if (category is null) return true;
                if (!item.TryGetProperty("media_type", out var kind) && !item.TryGetProperty("media_kind", out kind)) return true;
                // Legacy responses may omit the concrete type. A grouped video
                // search does not need a detail request for every result.
                var normalized = kind.ValueKind == JsonValueKind.String ? MediaCategories.Normalize(kind.GetString()) : null;
                return normalized is null || MediaCategories.Matches(category, normalized);
            })
            .ToArray();
        // When the source returned at least one title that really matches (typos allowed),
        // unrelated filler such as every title containing "2" is dropped and the best
        // matches come first. Without any matching title the source's own hits stay
        // untouched: they usually match a localized title the response does not carry.
        var scored = filtered.Select((item, index) => (Item: item, Index: index, Score: Relevance(item, query))).ToArray();
        if (scored.Any(entry => entry.Score >= StrongMatch))
            scored = scored.Where(entry => entry.Score >= KeepMatch)
                .OrderByDescending(entry => entry.Score).ThenBy(entry => entry.Index).ToArray();
        // The score travels with each hit so the page can also drop filler from a source
        // without any match once another source found the real title.
        var results = scored.Select(entry =>
        {
            var fields = entry.Item.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
            fields["relevance"] = Math.Round(entry.Score, 3);
            return fields;
        }).ToArray();
        return JsonSerializer.SerializeToElement(new { results });
    }
}
