using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Models;
using Jellyfin.Plugin.AniWorld.Services;

var tests = new (string Name, Action Run)[]
{
    ("available_languages is accepted", AvailableLanguagesIsAccepted),
    ("nullable episode_count is unknown", NullableEpisodeCountIsUnknown),
    ("MangaFire pages may share a chapter URL", MangaFirePagesMayShareUrl),
    ("real duplicate episode URLs are rejected", DuplicateEpisodeUrlsAreRejected),
    ("legacy string queue entries remain readable", LegacyQueueEntriesRemainReadable),
    ("extended queue entries round-trip", ExtendedQueueEntriesRoundTrip),
    ("fork sources are registered", ForkSourcesAreRegistered),
    ("only enabled AniWorld sources are exposed", OnlyEnabledSourcesAreExposed),
    ("site default path is selected", SiteDefaultPathIsSelected),
    ("unrelated site path is not selected", UnrelatedSitePathIsNotSelected),
    ("invalid path response is rejected", InvalidPathResponseIsRejected),
    ("queue sends the site default path ID", QueueSendsSiteDefaultPathId),
};

var failed = 0;
foreach (var test in tests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}

return failed == 0 ? 0 : 1;

static void AvailableLanguagesIsAccepted()
{
    using var document = JsonDocument.Parse(
        """
        {"episodes":[{"url":"https://aniworld.to/anime/stream/example/staffel-1/episode-1","episode_number":1,"available_languages":["German Dub","English Sub"]}]}
        """);
    var episodes = AniWorldEpisodeParser.Parse(document.RootElement, 1);
    Equal(1, episodes.Count);
    True(episodes[0].Languages.SetEquals(["German Dub", "English Sub"]));
}

static void NullableEpisodeCountIsUnknown()
{
    using var document = JsonDocument.Parse("{\"episode_count\":null}");
    True(AniWorldRequestApplicationService.ReadExpectedEpisodeCount(document.RootElement) is null);
    True(AniWorldEpisodeParser.SatisfiesExpectedCount(null, 3));
}

static void MangaFirePagesMayShareUrl()
{
    using var document = JsonDocument.Parse(
        """
        {"episodes":[
          {"url":"https://mangafire.to/title/example/chapter/1","chapter_url":"https://mangafire.to/title/example/chapter/1","episode_number":1,"page_number":1,"available_languages":["English Dub"]},
          {"url":"https://mangafire.to/title/example/chapter/1","chapter_url":"https://mangafire.to/title/example/chapter/1","episode_number":2,"page_number":2,"available_languages":["English Dub"]}
        ]}
        """);
    var episodes = AniWorldEpisodeParser.Parse(document.RootElement, 1);
    Equal(2, episodes.Count);
    Equal(2, episodes[1].PageNumber);
}

static void DuplicateEpisodeUrlsAreRejected()
{
    using var document = JsonDocument.Parse(
        """
        {"episodes":[
          {"url":"https://aniworld.to/anime/stream/example/staffel-1/episode-1","episode_number":1},
          {"url":"https://aniworld.to/anime/stream/example/staffel-1/episode-1","episode_number":1}
        ]}
        """);
    Throws<AniWorldException>(() => AniWorldEpisodeParser.Parse(document.RootElement, 1));
}

static void LegacyQueueEntriesRemainReadable()
{
    const string json = "[\"https://aniworld.to/anime/stream/example/staffel-1/episode-1\"]";
    var entries = JsonSerializer.Deserialize<List<AniWorldDownloadItem>>(json) ?? [];
    Equal(1, entries.Count);
    Equal("https://aniworld.to/anime/stream/example/staffel-1/episode-1", entries[0].Url);
    Equal(json, JsonSerializer.Serialize(entries));
}

static void ExtendedQueueEntriesRoundTrip()
{
    var entries = new List<AniWorldDownloadItem>
    {
        new("https://mangafire.to/title/example/chapter/1")
        {
            SeriesUrl = "https://mangafire.to/title/example",
            SelectedPages = [1, 2],
        },
    };
    var json = JsonSerializer.Serialize(entries);
    var roundTrip = JsonSerializer.Deserialize<List<AniWorldDownloadItem>>(json) ?? [];
    Equal(1, roundTrip.Count);
    True(roundTrip[0].SelectedPages?.SequenceEqual([1, 2]) == true);
    True(json.Contains("selected_pages", StringComparison.Ordinal));
}

static void ForkSourcesAreRegistered()
{
    var sources = AniWorldSiteRegistry.KnownSites.Select(site => site.Id).ToHashSet(StringComparer.Ordinal);
    True(sources.Contains("filmo"));
    True(sources.Contains("moflix"));
}

static void OnlyEnabledSourcesAreExposed()
{
    using var document = JsonDocument.Parse(
        """
        {
          "available_sites":[{"key":"aniworld"},{"key":"sto"},{"key":"moflix"}],
          "enable_aniworld":true,
          "enable_sto":false,
          "enable_moflix":true,
          "enable_filmo":true
        }
        """);
    var enabled = AniWorldRequestApplicationService.ReadEnabledSources(
        document.RootElement,
        AniWorldSiteRegistry.KnownSites);
    True(enabled.Select(source => source.Id).SequenceEqual(["aniworld", "moflix"]));
}

static void SiteDefaultPathIsSelected()
{
    using var document = JsonDocument.Parse(
        """
        {"paths":[
          {"id":4,"path":"/other","default_sites":"sto"},
          {"id":7,"path":"/movies","default_sites":"filmpalast, filmo"},
          {"id":9,"path":"/later","default_sites":"filmo"}
        ]}
        """);
    Equal(7, AniWorldClient.SelectDefaultPathId(document.RootElement, "filmo"));
}

static void UnrelatedSitePathIsNotSelected()
{
    using var document = JsonDocument.Parse(
        """
        {"paths":[{"id":7,"path":"/movies","default_sites":"filmpalast"}]}
        """);
    True(AniWorldClient.SelectDefaultPathId(document.RootElement, "filmo") is null);
}

static void InvalidPathResponseIsRejected()
{
    using var document = JsonDocument.Parse("{\"paths\":[{\"id\":0,\"path\":\"/movies\",\"default_sites\":\"filmo\"}]}");
    Throws<AniWorldException>(() => AniWorldClient.SelectDefaultPathId(document.RootElement, "filmo"));
}

static void QueueSendsSiteDefaultPathId()
{
    var request = new MediaRequest
    {
        Source = "filmo",
        SeriesUrl = "https://example.test/movie",
        Title = "Example",
        EpisodesJson = "[\"https://example.test/movie\"]",
        Language = "German Dub",
        Provider = "VOE",
    };
    using var body = JsonDocument.Parse(JsonSerializer.Serialize(AniWorldClient.CreateDownloadPayload(request, 7)));
    Equal(7, body.RootElement.GetProperty("custom_path_id").GetInt32());
    Equal("https://example.test/movie", body.RootElement.GetProperty("series_url").GetString());
    Equal("https://example.test/movie", body.RootElement.GetProperty("episodes")[0].GetString());
}

static void True(bool condition)
{
    if (!condition)
    {
        throw new InvalidOperationException("Expected condition to be true.");
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }
}

static void Throws<T>(Action action)
    where T : Exception
{
    try
    {
        action();
    }
    catch (T)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}
