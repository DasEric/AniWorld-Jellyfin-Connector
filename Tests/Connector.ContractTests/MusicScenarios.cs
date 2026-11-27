using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Configuration;
using Jellyfin.Plugin.AniWorld.Models;
using Jellyfin.Plugin.AniWorld.Services;

internal static class MusicScenarios
{
    private const string Release = """
        {"id":"x","complete":true,"releases":[
          {"id":"0","username":"peer","directory":"Music/Daft Punk/Discovery (2001)","title":"Discovery (2001)","parent":"Daft Punk",
           "free_slot":true,"formats":["FLAC"],"lossless":true,"bitrate":null,
           "cover":{"filename":"x","size":1,"key":"0123456789abcdef0123456789abcdef01234567","cached":true},
           "files":[
             {"filename":"Music\\Daft Punk\\Discovery (2001)\\01 - One More Time.flac","name":"01 - One More Time.flac","size":1000},
             {"filename":"Music\\Daft Punk\\Discovery (2001)\\02 - Aerodynamic .flac","name":"02 - Aerodynamic .flac","size":2000}]},
          {"id":"1","username":"","directory":"broken","files":[{"filename":"a.mp3","size":1}]},
          {"id":"2","username":"other","directory":"Empty","files":[]}]}
        """;

    internal static void CategoryAndSourcePolicy()
    {
        var config = new PluginConfiguration();
        Check(MediaCategories.IsAllowed("music", config));
        Check(MediaCategories.Normalize("Musik") == "music");
        Check(!MediaCategories.Matches("video", "music") && !MediaCategories.Matches("catalog", "music"));
        Check(MediaCategories.SearchTypes("music", config).SequenceEqual(["music"]));
        var groups = JsonSerializer.Serialize(MediaCategories.Available(config));
        Check(groups.IndexOf("\"video\"", StringComparison.Ordinal) < groups.IndexOf("\"music\"", StringComparison.Ordinal));
        config.AllowMusicRequests = false;
        Check(!JsonSerializer.Serialize(MediaCategories.Available(config)).Contains("\"music\"", StringComparison.Ordinal));

        using var fixture = new CatalogScenarios.Fixture();
        var sources = fixture.Application.GetAllowedSourcesAsync("user", CancellationToken.None, false).GetAwaiter().GetResult();
        Check(sources.All(item => item.Id != "soulseek"));

        using var enabled = new CatalogScenarios.Fixture { MusicEnabled = true };
        var music = enabled.Application.GetAllowedSourcesAsync("user", CancellationToken.None, false).GetAwaiter().GetResult()
            .Single(item => item.Id == "soulseek");
        Check(music.Backend == "music" && music.MediaTypes.SequenceEqual(["music"]));
        enabled.Application.GetAllowedSourcesAsync("user", CancellationToken.None, false).GetAwaiter().GetResult();
        Check(enabled.MusicStatusCalls == 1);

        using var disabled = new CatalogScenarios.Fixture { MusicEnabled = true };
        disabled.Config.AllowMusicRequests = false;
        Check(disabled.Application.GetAllowedSourcesAsync("user", CancellationToken.None, false).GetAwaiter().GetResult().All(item => item.Id != "soulseek"));
        Check(disabled.MusicStatusCalls == 0);

        using var allowlisted = new CatalogScenarios.Fixture { MusicEnabled = true };
        allowlisted.Config.AllowedSources = "sto";
        Check(allowlisted.Application.GetAllowedSourcesAsync("user", CancellationToken.None, false).GetAwaiter().GetResult().All(item => item.Id != "soulseek"));

        // A video source can never smuggle music in through its capability list.
        using var settings = JsonDocument.Parse(CatalogScenarios.Fixture.Settings);
        using var advertised = JsonDocument.Parse("""{"schema_version":1,"sources":[{"id":"sto","label":"Serienstream","backend":"video","enabled":true,"media_types":["series","music"]}]}""");
        var video = AniWorldRequestApplicationService.ReadSourceCapabilities(settings.RootElement, advertised.RootElement, new PluginConfiguration());
        Check(video.Single().MediaTypes.SequenceEqual(["series"]));
    }

    internal static void ReleasesAreParsedExactly()
    {
        using var state = JsonDocument.Parse(Release);
        var releases = AniWorldRequestApplicationService.ReadMusicReleases(state.RootElement);
        Check(releases.Count == 1);
        var release = releases[0];
        Check(release.Username == "peer" && release.Title == "Discovery (2001)" && release.Parent == "Daft Punk");
        // Soulseek file names must reach the downloader byte-identical (backslashes, trailing spaces).
        Check(release.Files[1].Filename == "Music\\Daft Punk\\Discovery (2001)\\02 - Aerodynamic .flac");
        Check(release.CoverKey == "0123456789abcdef0123456789abcdef01234567" && release.Lossless && release.FreeSlot);
    }

    internal static void MusicLifecycle()
    {
        using var fixture = new CatalogScenarios.Fixture { MusicEnabled = true, MusicSearchState = Release };
        var search = fixture.Application.SearchAsync("user", "Daft Punk Discovery", "soulseek", "music", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(fixture.MusicQuery == "Daft Punk Discovery" && fixture.MusicSearchDeleted);
        var group = search.Groups.Single();
        Check(group.Error is null && group.Source == "soulseek");
        var result = group.Data!.Value.GetProperty("results").EnumerateArray().Single();
        Check(result.GetProperty("media_kind").GetString() == "music" && result.GetProperty("author").GetString() == "Daft Punk");
        Check(result.GetProperty("track_count").GetInt32() == 2 && result.GetProperty("tracks").GetArrayLength() == 2);
        // The client only ever sees an opaque token, never the Soulseek user or paths.
        Check(!result.ToString().Contains("peer", StringComparison.Ordinal) && !result.ToString().Contains("Music\\\\", StringComparison.Ordinal));
        var token = result.GetProperty("id").GetString()!;

        Reject(() => fixture.Application.SubmitMusicAsync("intruder", "Intruder", new MusicMediaRequest { Token = token }, CancellationToken.None).GetAwaiter().GetResult());
        Reject(() => fixture.Application.SubmitMusicAsync("user", "User", new MusicMediaRequest { Token = "unknown" }, CancellationToken.None).GetAwaiter().GetResult());
        var submitted = fixture.Application.SubmitMusicAsync("user", "User", new MusicMediaRequest { Token = token }, CancellationToken.None).GetAwaiter().GetResult();
        Check(submitted.Disposition == SubmitDisposition.Stored);
        var request = submitted.Request!;
        Check(request.Backend == "music" && request.MediaType == "music" && request.Source == "soulseek" && request.Status == RequestStatuses.Pending);
        Check(request.MusicUser == "peer" && request.SelectionLabel == "2 Titel · FLAC");
        Check(fixture.Application.SubmitMusicAsync("user", "User", new MusicMediaRequest { Token = token }, CancellationToken.None).GetAwaiter().GetResult().Disposition == SubmitDisposition.Duplicate);

        fixture.Application.ApproveAsync(request.Id, "admin", CancellationToken.None).GetAwaiter().GetResult();
        fixture.Application.ProcessReadyRequestsAsync(default).GetAwaiter().GetResult();
        var queued = fixture.Store.GetAsync(request.Id, default).GetAwaiter().GetResult()!;
        Check(queued.Status == RequestStatuses.Queued && queued.AniWorldQueueId is null && fixture.MusicDownloadCalls == 1 && fixture.DownloadCalls == 0);
        Check(fixture.LastPayload.GetProperty("username").GetString() == "peer");
        var files = fixture.LastPayload.GetProperty("files").EnumerateArray().ToArray();
        Check(files.Length == 2 && files[1].GetProperty("filename").GetString() == "Music\\Daft Punk\\Discovery (2001)\\02 - Aerodynamic .flac" && files[1].GetProperty("size").GetInt64() == 2000);

        // Persisted music requests survive a restart with their file list.
        var reloaded = new RequestStore(fixture.Directory).GetAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult()!;
        Check(reloaded.MusicUser == "peer" && reloaded.MusicFilesJson.Contains("Aerodynamic", StringComparison.Ordinal));

        Sync(fixture, Transfers("running", "completed"));
        Check(fixture.Store.GetAsync(request.Id, default).GetAwaiter().GetResult()!.Status == RequestStatuses.Queued);
        Sync(fixture, Transfers("completed", "completed", converting: true));
        Check(fixture.Store.GetAsync(request.Id, default).GetAwaiter().GetResult()!.Status == RequestStatuses.Queued);
        Sync(fixture, Transfers("completed", "completed"));
        var done = fixture.Store.GetAsync(request.Id, default).GetAwaiter().GetResult()!;
        Check(done.Status == RequestStatuses.Completed && !done.LibraryScanPending);
        Check(fixture.Store.ListPendingLibraryScansAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 0);
    }

    internal static void TransferOutcomes()
    {
        var request = new MediaRequest
        {
            Id = 5, Backend = "music", Status = RequestStatuses.Queued, MusicUser = "peer", MusicDirectory = "Music/Daft Punk/Discovery (2001)",
            MusicFilesJson = JsonSerializer.Serialize(new[]
            {
                new MusicTrack("Music\\Daft Punk\\Discovery (2001)\\01 - One More Time.flac", "01 - One More Time.flac", 1000),
                new MusicTrack("Music\\Daft Punk\\Discovery (2001)\\02 - Aerodynamic .flac", "02 - Aerodynamic .flac", 2000),
            }),
            DecidedUtc = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc),
        };
        var now = request.DecidedUtc!.Value.AddMinutes(10);
        State(request, Transfers("completed", "failed"), now, RequestStatuses.Partial);
        State(request, Transfers("failed", "failed"), now, RequestStatuses.Failed);
        State(request, Transfers("cancelled", "cancelled"), now, RequestStatuses.Cancelled);
        State(request, Transfers("queued", "failed"), now, null);
        // Another user's folder with the same file names is not this album.
        State(request, Transfers("completed", "completed", username: "someone"), now, null);
        // Nothing in the queue: wait, but report it after a day instead of waiting forever.
        State(request, """{"transfers":[]}""", now, null);
        State(request, """{"transfers":[]}""", now.AddDays(2), RequestStatuses.Failed);
    }

    internal static void AutomaticPathsRejectMusic()
    {
        using var fixture = new CatalogScenarios.Fixture { MusicEnabled = true };
        var request = new AutomaticMediaRequest { Title = "Discovery", SeriesUrl = "https://example.test/music", Source = "soulseek", MediaType = "music", Language = "German Dub", Provider = "VOE" };
        Reject(() => fixture.Application.PlanAsync("user", request, false, false, CancellationToken.None).GetAwaiter().GetResult());
        Check(fixture.MusicDownloadCalls == 0 && fixture.DownloadCalls == 0);
    }

    private static string Transfers(string first, string second, bool converting = false, string username = "peer")
        => JsonSerializer.Serialize(new
        {
            transfers = new[]
            {
                new
                {
                    username,
                    directory = "Music\\Daft Punk\\Discovery (2001)",
                    files = new[]
                    {
                        new { name = "01 - One More Time.flac", state = first, converting = false },
                        new { name = "02 - Aerodynamic .flac", state = second, converting },
                    },
                },
            },
        });

    private static void Sync(CatalogScenarios.Fixture fixture, string transfers)
    {
        using var document = JsonDocument.Parse(transfers);
        var queued = fixture.Store.ListQueuedMusicAsync(default).GetAwaiter().GetResult();
        fixture.Store.SyncMusicStatesAsync(AniWorldRequestApplicationService.ReadMusicStates(document.RootElement, queued, DateTime.UtcNow), default).GetAwaiter().GetResult();
    }

    private static void State(MediaRequest request, string transfers, DateTime now, string? expected)
    {
        using var document = JsonDocument.Parse(transfers);
        var states = AniWorldRequestApplicationService.ReadMusicStates(document.RootElement, [request], now);
        Check(expected is null ? states.Count == 0 : states.TryGetValue(request.Id, out var state) && state.Status == expected);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (AniWorldApplicationException) { return; }
        throw new InvalidOperationException("Expected the music request to be rejected.");
    }

    private static void Check(bool value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (!value) throw new InvalidOperationException("Music assertion failed: " + expression);
    }
}
