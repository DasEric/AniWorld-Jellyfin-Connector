using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Configuration;
using Jellyfin.Plugin.AniWorld.Models;
using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;

internal static class MediaLifecycleScenarios
{
    internal static void LanguagesCoverAllEpisodes() => Run(async fixture =>
    {
        var plan = await fixture.Plan();
        Check(plan.Languages.Contains("German Dub") && plan.Languages.Contains("English Dub"));
        Check(plan.Variants["German Dub"].AvailableCount == 1 && plan.Variants["German Dub"].UnavailableCount == 1);
        Check(plan.Variants["English Dub"].AvailableCount == 2);
        Check(fixture.Probed.Count == 2);
        var json = JsonSerializer.SerializeToElement(plan.ToResponse());
        Check(json.GetProperty("coverage").GetProperty("German Dub").GetProperty("missing_count").GetInt32() == 1);
    });

    internal static void PartialSelectionDownloadsAvailableAndRemainsPartial() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        var result = await fixture.Submit("German Dub", false);
        Check(result.Request!.Status == RequestStatuses.Ready && fixture.DownloadCalls == 0);
        var processed = await fixture.Process(result.Request.Id);
        Check(processed.Status == RequestStatuses.Queued && processed.Episodes.Count == 1);
        Check(processed.Episodes[0].Language == "German Dub" && processed.UnavailableCount == 1);
        await fixture.Store.SyncQueueStatesForAllAsync(new Dictionary<long, string> { [42] = "completed" }, default);
        Check((await fixture.Store.GetAsync(processed.Id, default))!.Status == RequestStatuses.Partial);
    });

    internal static void ProviderNeverComesFromOnlyTheFirstEpisode() => Run(async fixture =>
    {
        fixture.Offers[1] = new() { ["German Dub"] = ["Filemoon"] };
        var plan = await fixture.Plan();
        Check(plan.Providers["German Dub"].SequenceEqual(["Automatic"]));
        var result = await fixture.Submit("German Dub", false, "Automatic");
        await fixture.Application.ApproveAsync(result.Request!.Id, "admin", default);
        var processed = await fixture.Process(result.Request.Id);
        Check(processed.Episodes.Select(item => item.Provider).SequenceEqual(["VOE", "Filemoon"]));
        await fixture.Store.SyncQueueStatesForAllAsync(new Dictionary<long, string> { [42] = "completed" }, default);
        var invalid = await fixture.Submit("German Dub", false, "VOE");
        await fixture.Application.ApproveAsync(invalid.Request!.Id, "admin", default);
        Check((await fixture.Process(invalid.Request.Id)).Status == RequestStatuses.Failed);
        Check(fixture.DownloadCalls == 1);
    });

    internal static void NoInventedLanguageOrJellixFallback() => Run(async fixture =>
    {
        fixture.Offers[0] = new() { ["English Dub"] = ["VOE"] };
        var plan = await fixture.Plan();
        Check(!plan.Languages.Contains("German Dub"));
        var result = await fixture.Submit("German Dub", true);
        await fixture.Application.ApproveAsync(result.Request!.Id, "admin", default);
        var processed = await fixture.Process(result.Request.Id);
        Check(processed.Status == RequestStatuses.Ready && fixture.DownloadCalls == 0);
        Check(processed.Error is not null && processed.NextAttemptUtc.HasValue);
    });

    internal static void AudioAndSubtitlesAreDifferent()
    {
        MediaStream[] english = [new() { Type = MediaStreamType.Audio, Language = "eng" }, new() { Type = MediaStreamType.Subtitle, Language = "deu" }];
        Check(MediaLanguage.Matches(english, "English Dub"));
        Check(!MediaLanguage.Matches(english, "German Dub") && !MediaLanguage.Matches(english, "German Sub"));
        MediaStream[] dual = [new() { Type = MediaStreamType.Audio, Language = "ger" }, new() { Type = MediaStreamType.Audio, Language = "en" }];
        Check(MediaLanguage.Matches(dual, "German Dub") && MediaLanguage.Matches(dual, "English Dub"));
        Check(!MediaLanguage.Matches([new() { Type = MediaStreamType.Audio, Language = "und" }], "German Dub"));
        Check(MediaLanguage.Matches([new() { Type = MediaStreamType.Audio, Language = "jpn" }, new() { Type = MediaStreamType.Video, Language = "deu" }], "German Sub"));
    }

    internal static void TypedClientConstructionIsUnambiguous()
    {
        var factory = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateFactory(typeof(AniWorldClient), [typeof(HttpClient)]);
        Check(factory is not null);
        var request = new MediaRequest { MediaType = "series", EpisodesJson = JsonSerializer.Serialize(new[] { new AniWorldDownloadItem("https://example.test/film") { MediaType = "movie" } }) };
        Check(AniWorldQueueMonitor.GetScanKind(request) == "movie");
    }

    internal static void ReceiptsReportActualLanguageInsteadOfRequestedLabel()
    {
        var germanRequest = new DownloadArtifact(new string('1', 32), new string('a', 32), "Film", "movie", "German Dub", 1, "owned",
            JsonSerializer.SerializeToElement(new[] { new { codec_type = "audio", tags = new { language = "eng" } } }));
        Check(germanRequest.Verification == "mismatch");
        Check((germanRequest with { Language = "English Dub" }).Verification == "verified");
        Check((germanRequest with { Streams = JsonSerializer.SerializeToElement(Array.Empty<object>()) }).Verification == "unknown");
    }

    internal static void DifferentLanguagesAreNotDuplicateRequests() => Run(async fixture =>
    {
        fixture.Offers[1] = new() { ["German Dub"] = ["VOE"], ["English Dub"] = ["VOE"] };
        var german = await fixture.Submit("German Dub", false);
        var english = await fixture.Submit("English Dub", false);
        Check(german.Request!.Id != english.Request!.Id && english.Disposition == SubmitDisposition.Stored);
        Check((await fixture.Submit("German Dub", false)).Disposition == SubmitDisposition.Duplicate);
    });

    internal static void SeriesRoutingOverridesMovieSiteDefault() => Run(async fixture =>
    {
        fixture.Offers[1] = new() { ["German Dub"] = ["VOE"] };
        fixture.Config.AutoApproveRequests = true;
        fixture.EnableRouting();
        var request = await fixture.Submit("German Dub", false);
        Check((await fixture.Process(request.Request!.Id)).Status == RequestStatuses.Queued);
        var payload = fixture.LastPayload;
        Check(payload.GetProperty("connector_protocol").GetInt32() == 1);
        Check(payload.GetProperty("episodes").EnumerateArray().All(item => item.GetProperty("custom_path_id").GetInt32() == 2));
        Check((await fixture.Ownership.ListAsync("alice", false, default)).Count == 1);
    });

    internal static void MissingCentralDestinationsNeverUseASiteDefault() => Run(async fixture =>
    {
        // MegaKino's site default is the movie folder; a detected show must not land there.
        fixture.Central = false;
        fixture.Offers[1] = new() { ["German Dub"] = ["VOE"] };
        fixture.Config.AutoApproveRequests = true;
        var request = await fixture.Submit("German Dub", false);
        var processed = await fixture.Process(request.Request!.Id);
        Check(processed.Status == RequestStatuses.Failed && fixture.DownloadCalls == 0);
        Check(processed.Error!.Contains("Film- und Serienziele", StringComparison.Ordinal));
    });

    internal static void CentralDestinationsWorkWithoutLocalOverride() => Run(async fixture =>
    {
        fixture.Central = true;
        fixture.Config.AutoApproveRequests = true;
        var result = await fixture.Submit("English Dub", false);
        Check((await fixture.Process(result.Request!.Id)).Status == RequestStatuses.Queued);
        Check(fixture.LastPayload.GetProperty("episodes").EnumerateArray().All(item => item.GetProperty("custom_path_id").GetInt32() == 2));
    });

    internal static void ConflictingCentralDestinationsAreNotOverridden() => Run(async fixture =>
    {
        fixture.Central = true;
        fixture.EnableRouting();
        fixture.Config.MoviePathId = 2; fixture.Config.SeriesPathId = 1;
        fixture.Config.MovieDownloadRoot = fixture.Series; fixture.Config.SeriesDownloadRoot = fixture.Movies;
        fixture.Config.AutoApproveRequests = true;
        var result = await fixture.Submit("English Dub", false);
        Check((await fixture.Process(result.Request!.Id)).Status == RequestStatuses.Failed && fixture.DownloadCalls == 0);
    });

    internal static void MediaDestinationWireFieldsAreExplicit()
    {
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            var value = JsonSerializer.SerializeToElement(new MediaDestinationSettings(true, 3, 1, 2), options);
            Check(value.GetProperty("enabled").GetBoolean() && value.GetProperty("series").GetInt32() == 2);
        }
        var source = new AniWorldSiteInfo("filmpalast", "FilmPalast", ["movie", "series"], "video", []);
        var filtered = SearchRelevance.Filter(JsonSerializer.SerializeToElement(new { results = new[] { new { title = "Silo", url = "https://filmpalast.to/stream/silos01e01", media_type = "series", grouped_series = true, video_id = "source-bound" } } }), "Silo", "video", source);
        Check(filtered.GetProperty("results").GetArrayLength() == 1 && filtered.GetProperty("results")[0].GetProperty("grouped_series").GetBoolean());
    }

    internal static void MixedMoviesAndSeriesKeepTheirOwnTargets() => Run(async fixture =>
    {
        fixture.Offers[1] = new() { ["German Dub"] = ["VOE"] };
        fixture.Mixed = true;
        fixture.Config.AutoApproveRequests = true;
        fixture.EnableRouting();
        var request = await fixture.Submit("German Dub", false);
        Check((await fixture.Process(request.Request!.Id)).Status == RequestStatuses.Queued);
        var items = fixture.LastPayload.GetProperty("episodes").EnumerateArray().ToArray();
        Check(items[0].GetProperty("media_type").GetString() == "series" && items[0].GetProperty("custom_path_id").GetInt32() == 2);
        Check(items[1].GetProperty("media_type").GetString() == "movie" && items[1].GetProperty("custom_path_id").GetInt32() == 1);
    });

    internal static void MissingMountNeverFallsBack() => Run(async fixture =>
    {
        fixture.EnableRouting();
        fixture.Config.SeriesPathId = 9999;
        fixture.Config.AutoApproveRequests = true;
        var request = await fixture.Submit("English Dub", false);
        Check((await fixture.Process(request.Request!.Id)).Status == RequestStatuses.Failed && fixture.DownloadCalls == 0);
    });

    internal static void OwnershipPersistsWithoutRequestHistory() => Run(async fixture =>
    {
        var request = new MediaRequest { SubmissionKey = "a".PadRight(32, 'a'), UserId = "alice", Source = "sto", SeriesUrl = Fixture.SeriesUrl, Title = "Example", MediaType = "series" };
        await fixture.Ownership.RegisterAsync(request, Fixture.Instance, default);
        var reopened = new DownloadOwnershipStore(fixture.Directory);
        Check((await reopened.ListAsync("alice", false, default)).Count == 1);
        Check((await reopened.ListAsync("bob", false, default)).Count == 0);
        Check((await reopened.ListAsync("admin", true, default)).Count == 1);
        request.UserId = "bob";
        try { await reopened.RegisterAsync(request, Fixture.Instance, default); throw new Exception("Ownership transferred"); }
        catch (AniWorldApplicationException error) { Check(error.StatusCode == HttpStatusCode.Conflict); }
    });

    internal static void MovedOwnFilesAreDeletedThroughJellyfinOnly() => Run(async fixture =>
    {
        var season = Path.Combine(fixture.Series, "Show", "Season 1");
        System.IO.Directory.CreateDirectory(season);
        string Episode(string name) { var path = Path.Combine(season, name); File.WriteAllBytes(path, new byte[100]); return path; }
        var own = Episode("Show.S01E01.mkv"); var sidecar = Path.ChangeExtension(own, ".nfo"); File.WriteAllText(sidecar, "x");
        var foreign = Episode("Show.S01E02.mkv"); var existed = Episode("Show.S01E03.mkv");
        var seriesId = Guid.NewGuid();
        var proxy = (MediaLibraryProxy)fixture.Library;
        foreach (var path in new[] { own, foreign, existed })
            proxy.Items.Add(new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), Path = path, SeriesId = seriesId });
        await fixture.AddReceipt("alice", 'a', '1');
        fixture.Artifacts[^1] = fixture.Artifacts[^1] with { Name = "Show.S01E01.mkv", State = "changed" };
        await fixture.AddReceipt("bob", 'b', '2');
        fixture.Artifacts[^1] = fixture.Artifacts[^1] with { Name = "Show.S01E02.mkv", State = "changed" };
        await fixture.AddReceipt("alice", 'c', '3');
        fixture.Artifacts[^1] = fixture.Artifacts[^1] with { Name = "Show.S01E03.mkv", State = "existing" };
        var service = new DownloadManagementService(fixture.Ownership, fixture.Client, fixture.Library);
        var list = JsonSerializer.SerializeToElement(await service.ListAsync("alice", false, default));
        Check(list.GetArrayLength() == 1 && list[0].GetProperty("canDelete").GetBoolean() && list[0].GetProperty("libraryFiles").GetInt32() == 1);
        Check(list[0].GetProperty("imageItemId").GetGuid() == seriesId);
        await ExpectApplication(HttpStatusCode.NotFound, () => service.PreviewAsync("bob", false, new string('a', 32), default));
        var preview = JsonSerializer.SerializeToElement(await service.PreviewAsync("alice", false, new string('a', 32), default));
        Check(preview.GetProperty("count").GetInt32() == 1);
        var token = preview.GetProperty("token").GetString()!;
        await ExpectApplication(HttpStatusCode.NotFound, () => service.ConfirmAsync("bob", false, token, default));
        var result = await service.ConfirmAsync("alice", false, token, default);
        Check(result.GetProperty("items")[0].GetProperty("status").GetString() == "deleted");
        Check(!File.Exists(own) && !File.Exists(sidecar) && File.Exists(foreign) && File.Exists(existed));
        Check(proxy.Deleted.Count == 1 && fixture.DeletedIds.Count == 0);
    });

    internal static void DeletionChecksUserAgainAndIncludesOwnSeriesOnly() => Run(async fixture =>
    {
        await fixture.AddReceipt("alice", 'a', '1');
        await fixture.AddReceipt("bob", 'b', '2');
        await fixture.AddReceipt("alice", 'c', '3');
        var service = new DownloadManagementService(fixture.Ownership, fixture.Client, fixture.Library);
        await ExpectApplication(HttpStatusCode.NotFound, () => service.PreviewAsync("bob", false, new string('a', 32), default));
        var preview = JsonSerializer.SerializeToElement(await service.PreviewAsync("alice", false, new string('a', 32), default));
        Check(preview.GetProperty("count").GetInt32() == 2);
        var token = preview.GetProperty("token").GetString()!;
        await ExpectApplication(HttpStatusCode.NotFound, () => service.ConfirmAsync("bob", false, token, default));
        await service.ConfirmAsync("alice", false, token, default);
        Check(fixture.DeletedIds.Order().SequenceEqual([new string('1', 32), new string('3', 32)]));
        await ExpectApplication(HttpStatusCode.NotFound, () => service.ConfirmAsync("alice", false, token, default));
        var adminPreview = JsonSerializer.SerializeToElement(await service.PreviewAsync("admin", true, new string('b', 32), default));
        await service.ConfirmAsync("admin", true, adminPreview.GetProperty("token").GetString()!, default);
        Check(fixture.DeletedIds.Contains(new string('2', 32)));
    });

    internal static void SharedMountProbeChecksActualFile() => Run(async fixture =>
    {
        fixture.EnableRouting();
        await fixture.Routing.ValidateAsync(fixture.Config, fixture.Client, default);
        Check(!System.IO.Directory.EnumerateFiles(fixture.Movies, ".connector-probe-*").Any());
        fixture.ProbeWrongPath = true;
        try { await fixture.Routing.ValidateAsync(fixture.Config, fixture.Client, default); throw new Exception("Invalid mount was accepted"); }
        catch (AniWorldException error) { Check(error.StatusCode == HttpStatusCode.Conflict); }
    });

    internal static void SavedDestinationsAreUsedImmediately() => Run(async fixture =>
    {
        // The settings page reads capabilities, saves the targets, and a request follows at once.
        fixture.Central = false;
        Check((await fixture.Client.GetConnectorCapabilitiesAsync(default)).MediaTargets?.Enabled == false);
        fixture.EnableRouting();
        await fixture.Routing.ValidateAsync(fixture.Config, fixture.Client, default);
        Check((await fixture.Client.GetConnectorCapabilitiesAsync(default)).MediaTargets?.Enabled == true);
        fixture.Offers[1] = new() { ["German Dub"] = ["VOE"] };
        fixture.Config.AutoApproveRequests = true;
        var request = await fixture.Submit("German Dub", false);
        Check((await fixture.Process(request.Request!.Id)).Status == RequestStatuses.Queued && fixture.DownloadCalls == 1);
    });

    internal static async Task ExpectApplication(HttpStatusCode code, Func<Task> action)
    {
        try { await action(); }
        catch (AniWorldApplicationException error) { Check(error.StatusCode == code); return; }
        throw new InvalidOperationException("Expected HTTP " + code);
    }
    private static void Run(Func<Fixture, Task> scenario) { using var fixture = new Fixture(); scenario(fixture).GetAwaiter().GetResult(); }
    internal static void Check(bool value) { if (!value) throw new InvalidOperationException("Media lifecycle assertion failed."); }

    internal sealed class Fixture : IDisposable
    {
        internal const string Instance = "00000000000000000000000000000001";
        internal const string SeriesUrl = "https://example.test/series";
        internal readonly string Directory = Path.Combine(Path.GetTempPath(), "connector-media-" + Guid.NewGuid().ToString("N"));
        internal string Movies => Path.Combine(Directory, "movies");
        internal string Series => Path.Combine(Directory, "tv");
        internal readonly PluginConfiguration Config = new() { AniWorldUrl = "http://upstream.test", AllowedSources = "sto,megakino" };
        internal readonly List<Dictionary<string, string[]>> Offers = [new() { ["German Dub"] = ["VOE"], ["English Dub"] = ["VOE"] }, new() { ["English Dub"] = ["VOE"] }];
        internal readonly List<string> Probed = [];
        internal readonly List<DownloadArtifact> Artifacts = [];
        internal readonly List<string> DeletedIds = [];
        internal JsonElement LastPayload;
        internal int DownloadCalls;
        internal int RemoteCalls;
        internal bool RejectRemoteCalls, FailProviders;
        internal bool Mixed, ProbeWrongPath;
        // A correctly set up downloader 2.5+ has central movie/show destinations.
        internal bool Central = true;
        internal readonly ILibraryManager Library;
        internal readonly DownloadOwnershipStore Ownership;
        internal readonly RequestStore Store;
        internal readonly AniWorldClient Client;
        internal readonly MediaTargetRouting Routing;
        internal readonly AniWorldRequestApplicationService Application;
        internal readonly SecretStore Secrets;
        private readonly HttpClient _http;
        internal Fixture()
        {
            System.IO.Directory.CreateDirectory(Movies); System.IO.Directory.CreateDirectory(Series);
            Library = DispatchProxy.Create<ILibraryManager, MediaLibraryProxy>();
            ((MediaLibraryProxy)Library).Folders = [new() { ItemId = "movies", Name = "Movies", CollectionType = CollectionTypeOptions.movies, Locations = [Movies] }, new() { ItemId = "series", Name = "Series", CollectionType = CollectionTypeOptions.tvshows, Locations = [Series] }];
            Routing = new MediaTargetRouting(Library, () => Config);
            Ownership = new DownloadOwnershipStore(Directory); Store = new RequestStore(Directory);
            Secrets = new SecretStore(Directory); Secrets.SetApiKey("test-key");
            _http = new HttpClient(new Handler(Respond));
            Client = new AniWorldClient(_http, Secrets, () => Config, Routing, Ownership);
            Application = CreateApplication(Store);
        }
        internal AniWorldRequestApplicationService CreateApplication(RequestStore store) => new(Client, store,
            new MediaAccessGrantStore(), new UserRateLimiter(), new JellyfinLibraryAvailabilityService(Library), new JellixSelectionTokenStore(), () => Config);
        internal void EnableRouting()
        {
            Config.EnableMediaRouting = true; Config.MoviePathId = 1; Config.SeriesPathId = 2;
            Config.MovieLibraryId = "movies"; Config.SeriesLibraryId = "series";
            Config.MovieLibraryPath = Movies; Config.SeriesLibraryPath = Series;
            Config.MovieDownloadRoot = Movies; Config.SeriesDownloadRoot = Series;
        }
        internal AutomaticMediaRequest Selection(string language = "", bool partial = false, string provider = "VOE") => new()
        { Title = "Example", SeriesUrl = SeriesUrl, Source = "megakino", MediaType = "series", Language = language, Provider = provider, AllowPartial = partial };
        internal Task<MissingMediaPlan> Plan() => Application.PlanAsync("alice", Selection(), false, false, default);
        internal Task<SubmitMediaRequestResult> Submit(string language, bool partial, string provider = "VOE")
            => Application.SubmitAutomaticAsync("alice", "Alice", Selection(language, partial, provider), false, default);
        internal async Task<MediaRequest> Process(long id)
        {
            await Application.ProcessReadyRequestsAsync(default);
            return (await Store.GetAsync(id, default))!;
        }
        internal async Task AddReceipt(string user, char key, char identity)
        {
            var requestKey = new string(key, 32);
            await Ownership.RegisterAsync(new MediaRequest { SubmissionKey = requestKey, UserId = user, SeriesUrl = SeriesUrl, Source = "sto", Title = "Example", MediaType = "series" }, Instance, default);
            Artifacts.Add(new DownloadArtifact(new string(identity, 32), requestKey, "Episode " + identity, "series", "German Dub", 100, "owned", JsonSerializer.SerializeToElement(Array.Empty<object>())));
        }
        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request)
        {
            Interlocked.Increment(ref RemoteCalls);
            if (RejectRemoteCalls) throw new InvalidOperationException("No remote request allowed during admission.");
            var path = request.RequestUri!.AbsolutePath;
            if (FailProviders && path == "/api/providers") return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            object data;
            if (path == "/api/settings") data = new { enable_sto = true, enable_megakino = true };
            else if (path == "/api/sources") return new HttpResponseMessage(HttpStatusCode.NotFound);
            else if (path == "/api/series") data = new { title = "Example", is_movie = false };
            else if (path == "/api/seasons") data = Mixed ? new { seasons = new object[] { new { url = SeriesUrl + "/season-1", season_number = 1, episode_count = 1, are_movies = false }, new { url = SeriesUrl + "/films", season_number = 0, episode_count = 1, are_movies = true } } }
                : (object)new { seasons = new[] { new { url = SeriesUrl + "/season-1", season_number = 1, episode_count = 2, are_movies = false } } };
            else if (path == "/api/episodes")
            {
                var movie = request.RequestUri.Query.Contains("films", StringComparison.Ordinal);
                data = new { episodes = Enumerable.Range(movie ? 2 : 1, Mixed ? 1 : 2).Select(number => new { url = SeriesUrl + "/episode-" + number, episode_number = number, title_en = "Film Example" }).ToArray() };
            }
            else if (path == "/api/providers")
            {
                var index = request.RequestUri.Query.Contains("episode-2", StringComparison.Ordinal) ? 1 : 0;
                lock (Probed) Probed.Add(request.RequestUri.Query);
                data = new { providers = Offers[index] };
            }
            else if (path == "/api/custom-paths") data = new { paths = new[] { new { id = 1, path = Movies, default_sites = "megakino" }, new { id = 2, path = Series, default_sites = "sto" } } };
            else if (path == "/api/connector/capabilities") data = new { protocol = 1, instance_id = Instance, file_receipts = true, entry_options = true, video_groups = 1, max_entries = 20000,
                media_targets = new { enabled = Central, revision = 1, movie = 1, series = 2 },
                paths = new object[] { new { id = 1, name = "Movies", path = Movies }, new { id = 2, name = "Series", path = Series } } };
            else if (path == "/api/media-targets") { Central = true; data = new { enabled = true, movie = 1, series = 2, revision = 2 }; }
            else if (path == "/api/download")
            { DownloadCalls++; LastPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.Clone(); data = new { queue_id = 42, accepted_episode_count = LastPayload.GetProperty("episodes").GetArrayLength() }; }
            else if (path == "/api/connector/artifacts/query")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                var keys = body.GetProperty("request_keys").EnumerateArray().Select(item => item.GetString()).ToArray();
                data = new { items = Artifacts.Where(item => keys.Contains(item.RequestKey)).Select(item => new { id = item.Id, request_key = item.RequestKey, name = item.Name, media_type = item.MediaType, language = item.Language, size = item.Size,
                    state = DeletedIds.Contains(item.Id) ? "deleted" : item.State, streams = Array.Empty<object>() }) };
            }
            else if (path == "/api/connector/artifacts/delete")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                var ids = body.GetProperty("ids").EnumerateArray().Select(item => item.GetString()!).ToArray(); DeletedIds.AddRange(ids);
                data = new { items = ids.Select(id => new { id, status = "deleted" }) };
            }
            else if (path == "/api/connector/path-probe")
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement;
                var root = body.GetProperty("path_id").GetInt32() == 1 ? Movies : Series;
                var id = body.TryGetProperty("probe_id", out var existing) ? existing.GetString()! : Guid.NewGuid().ToString("N");
                var probe = Path.Combine(root, ".connector-probe-" + id);
                if (body.TryGetProperty("cleanup", out var cleanup) && cleanup.GetBoolean()) File.Delete(probe);
                else if (!ProbeWrongPath) await File.WriteAllTextAsync(probe, "test-nonce");
                data = new { probe_id = id, relative_path = Path.GetFileName(probe), nonce = "test-nonce" };
            }
            else throw new InvalidOperationException("Unexpected request: " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        }
        public void Dispose() { _http.Dispose(); System.IO.Directory.Delete(Directory, true); }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => respond(request); }
}

public class MediaLibraryProxy : DispatchProxy
{
    internal List<VirtualFolderInfo> Folders = [];
    internal List<BaseItem> Items = [];
    internal List<Guid> Deleted = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method?.Name)
        {
            case "GetVirtualFolders": return Folders;
            case "GetItemList": return Items.ToList();
            case "GetItemById": return args is { Length: > 0 } && args[0] is Guid id ? Items.FirstOrDefault(item => item.Id == id) : null;
            case "DeleteItem": if (args is { Length: > 0 } && args[0] is BaseItem item) { Deleted.Add(item.Id); Items.Remove(item); } return null;
            default: return null;
        }
    }
}
