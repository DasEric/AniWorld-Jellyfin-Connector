using System.Text.Json.Serialization;
using Jellyfin.Plugin.AniWorld.Configuration;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Statische Registry aller bekannten AniWorld-Sites. Die Registry liefert
/// Bezeichnung und Medientypen; ob eine Quelle tatsächlich angezeigt werden
/// darf, wird zusätzlich mit den aktuellen /api/settings abgeglichen.
/// </summary>
public static class AniWorldSiteRegistry
{
    /// <summary>
    /// Alle bekannten AniWorld-kompatiblen Sites in Anzeigereihenfolge.
    /// media_types: "series" = nur Serien/Anime, "movie" = nur Filme, beide = gemischt.
    /// </summary>
    public static readonly IReadOnlyList<AniWorldSiteInfo> KnownSites =
    [
        new("aniworld",       "AniWorld",       ["movie", "series"], BrowsePaths: ["api/new-animes", "api/popular-animes"]),
        new("sto",            "Serienstream",   ["series"], BrowsePaths: ["api/new-series", "api/popular-series"]),
        new("kinox",          "Kinox",          ["movie", "series"], BrowsePaths: ["api/kinox-movies"]),
        new("burningseries",  "BurningSeries",  ["series"], BrowsePaths: ["api/burningseries-series"]),
        new("megakino",       "MegaKino",       ["movie", "series"], BrowsePaths: ["api/popular-movies"]),
        new("cineby",         "Cineby",         ["movie", "series"], BrowsePaths: ["api/cineby-movies"]),
        new("filmpalast",     "FilmPalast",     ["movie"], BrowsePaths: ["api/filmpalast-movies"]),
        new("filmo",          "Filmo",          ["movie"], BrowsePaths: ["api/filmo-movies"]),
        new("moflix",         "Moflix",         ["movie", "series"], BrowsePaths: ["api/moflix-movies"]),
        new("mangafire",      "MangaFire",      ["manga"], BrowsePaths: ["api/mangafire-trending"]),
        new("htv",            "HanimeTV",       ["series"], BrowsePaths: ["api/htv-trending"]),
    ];

    /// <summary>
    /// Gibt die gefilterte und auf die Konfiguration zugeschnittene Site-Liste zurück.
    /// Berücksichtigt die AllowedSources-Allowlist aus der Plugin-Konfiguration.
    /// Unbekannte Site-IDs in der Konfig werden ignoriert, aber für zukünftige
    /// Sites (die noch nicht in KnownSites enthalten sind) wird trotzdem ein
    /// generischer Eintrag erzeugt, damit sie per Konfiguration vorab aktiviert
    /// werden können.
    /// </summary>
    public static List<AniWorldSiteInfo> GetAllowedSites(PluginConfiguration? configuration = null)
    {
        configuration ??= Plugin.Instance?.Configuration ?? new PluginConfiguration();
        var allowlist = (configuration.AllowedSources ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var output = new List<AniWorldSiteInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Bekannte Sites in definierter Reihenfolge
        foreach (var site in KnownSites)
        {
            if (allowlist.Count > 0 && !allowlist.Contains(site.Id))
            {
                continue;
            }

            if (seen.Add(site.Id))
            {
                output.Add(site);
            }
        }

        // Unbekannte Sites aus der Allowlist (zukünftige Sites)
        if (allowlist.Count > 0)
        {
            foreach (var id in allowlist)
            {
                if (!seen.Add(id))
                {
                    continue;
                }

                // Generischer Eintrag für noch nicht in KnownSites bekannte Sites
                output.Add(new AniWorldSiteInfo(id, id, ["movie", "series"]));
            }
        }

        return output;
    }
}

/// <summary>Beschreibung einer AniWorld-kompatiblen Site.</summary>
public sealed record AniWorldSiteInfo(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("media_types")] IReadOnlyList<string> MediaTypes,
    [property: JsonPropertyName("backend")] string Backend = "video",
    [property: JsonPropertyName("browse_paths")] IReadOnlyList<string>? BrowsePaths = null);
