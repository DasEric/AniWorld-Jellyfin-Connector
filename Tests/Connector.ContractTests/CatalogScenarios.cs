using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.AniWorld.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Jellyfin.Plugin.AniWorld.Configuration;
using Jellyfin.Plugin.AniWorld.Models;
using Jellyfin.Plugin.AniWorld.Services;

internal static class CatalogScenarios
{
    internal static void JellixSelectionUsesCurrentConnectorPolicy()
    {
        foreach (var automatic in new[] { false, true })
        {
            using var fixture = new Fixture { VideoPlanning = true };
            fixture.Config.AllowedSources = "moflix";
            // Change the mode after search: submission must read the current connector setting.
            fixture.Config.AutoApproveRequests = !automatic;
            var selection = fixture.Application.SearchAsync("user", "Gravity Falls", "all", "series", true, CancellationToken.None).GetAwaiter().GetResult();
            var token = selection.Items.Single(item => item.Title == "Willkommen in Gravity Falls").Token;
            fixture.Config.AutoApproveRequests = automatic;
            Reject(() => fixture.Application.SubmitSelectionAsync("other-user", "Other", token, CancellationToken.None).GetAwaiter().GetResult());
            var result = fixture.Application.SubmitSelectionAsync("user", "User", token, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Request?.UserId == "user");
            Check(result.Request?.Status == (automatic ? RequestStatuses.Queued : RequestStatuses.Pending));
            Check(fixture.DownloadCalls == (automatic ? 1 : 0));
            Check((fixture.Application.ListForUserAsync("other-user", 500, false, CancellationToken.None).GetAwaiter().GetResult()).Count == 0);
            Reject(() => fixture.Application.SubmitSelectionAsync("user", "User", token, CancellationToken.None).GetAwaiter().GetResult());
            if (!automatic)
            {
                var approved = fixture.Application.ApproveAsync(result.Request!.Id, "admin", CancellationToken.None).GetAwaiter().GetResult();
                if (approved.Status != RequestStatuses.Queued) throw new InvalidOperationException("Approval result: " + approved.Status + " / " + approved.Error);
                Check(approved.Status == RequestStatuses.Queued && fixture.DownloadCalls == 1);
                fixture.Application.ApproveAsync(result.Request.Id, "admin", CancellationToken.None).GetAwaiter().GetResult();
                Check(fixture.DownloadCalls == 1);
            }
            Check(fixture.LastPayload.GetProperty("language").GetString() == fixture.Config.DefaultLanguage);
            Check(fixture.LastPayload.GetProperty("provider").GetString() == fixture.Config.DefaultProvider);
            Check(fixture.LastPayload.GetProperty("episodes").GetArrayLength() == 2);
        }
    }

    internal static void JellixSelectionRechecksMaintenanceAndSourcePolicy()
    {
        using var fixture = new Fixture { VideoPlanning = true };
        fixture.Config.AllowedSources = "moflix";
        var selection = fixture.Application.SearchAsync("user", "Gravity Falls", "all", "series", true, CancellationToken.None).GetAwaiter().GetResult();
        fixture.Config.MaintenanceMode = true;
        Reject(() => fixture.Application.SubmitSelectionAsync("user", "User", selection.Items.Single(item => item.Title == "Willkommen in Gravity Falls").Token, CancellationToken.None).GetAwaiter().GetResult());
        fixture.Config.MaintenanceMode = false;
        selection = fixture.Application.SearchAsync("user", "Gravity Falls", "all", "series", true, CancellationToken.None).GetAwaiter().GetResult();
        fixture.Config.AllowedSources = "sto";
        Reject(() => fixture.Application.SubmitSelectionAsync("user", "User", selection.Items.Single(item => item.Title == "Willkommen in Gravity Falls").Token, CancellationToken.None).GetAwaiter().GetResult());
        Check(fixture.DownloadCalls == 0);
        Check(fixture.Store.ListAllAsync(100, CancellationToken.None).GetAwaiter().GetResult().Count == 0);
    }

    internal static void ControllerJsonMatchesJellyfinDefaultAndCamelCase()
    {
        using var fixture = new Fixture { MissingBrowseTypes = true };
        var controller = fixture.Controller;
        foreach (var options in new[] { JsonDefaults.Options, JsonDefaults.PascalCaseOptions, JsonDefaults.CamelCaseOptions })
        {
            var discovery = (OkObjectResult)controller.Discover("video", "moflix", CancellationToken.None).GetAwaiter().GetResult();
            using var wire = JsonDocument.Parse(JsonSerializer.Serialize(discovery.Value, options));
            var section = wire.RootElement.GetProperty("sections")[0];
            Check(section.TryGetProperty("source", out var source) && source.GetString() == "moflix");
            Check(section.GetProperty("label").GetString() == "Moflix");
            Check(section.GetProperty("items").GetArrayLength() == 1);
            Check(!section.TryGetProperty("error", out var noError) || noError.ValueKind == JsonValueKind.Null);
            fixture.FailMoflix = true;
            var failure = (OkObjectResult)controller.Discover("video", "moflix", CancellationToken.None).GetAwaiter().GetResult();
            using var errorWire = JsonDocument.Parse(JsonSerializer.Serialize(failure.Value, options));
            Check(errorWire.RootElement.GetProperty("sections")[0].GetProperty("error").ValueKind == JsonValueKind.String);
            fixture.FailMoflix = false;
            var search = (OkObjectResult)controller.Search("gravity falls", "moflix", "video", CancellationToken.None).GetAwaiter().GetResult();
            using var searchWire = JsonDocument.Parse(JsonSerializer.Serialize(search.Value, options));
            var group = searchWire.RootElement.GetProperty("groups")[0];
            Check(group.GetProperty("source").GetString() == "moflix");
            Check(group.GetProperty("data").GetProperty("results").GetArrayLength() == 3);
        }
    }

    internal static void SearchErrorsKeepHttpContextAndOtherSources()
    {
        using var fixture = new Fixture { FailVideoSearch = true };
        var result = fixture.Application.SearchAsync("user", "gravity falls", "all", "video", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(result.Groups.Count == 3);
        var failure = result.Groups.Single(group => group.Source == "moflix");
        Check(failure.Error!.Contains("HTTP 500", StringComparison.Ordinal));
        Check(failure.Error.Contains("api/search", StringComparison.Ordinal));
        Check(!failure.Error.Contains("contract-test-key", StringComparison.Ordinal));
        Check(result.Groups.Where(group => group.Source != "moflix").All(group => group.Error is null && group.Data!.Value.GetProperty("results").GetArrayLength() > 0));
    }

    internal static void LegacySources404UsesSettingsFallback()
    {
        using var fixture = new Fixture { LegacySources = true };
        var sources = fixture.Application.GetAllowedSourcesAsync("user", CancellationToken.None).GetAwaiter().GetResult();
        Check(sources.Count == 3 && sources.Any(source => source.Id == "sto"));
    }
    internal static void MegaKinoSelectorsStayDistinct()
    {
        using var data = JsonDocument.Parse("""{"episodes":[{"url":"https://megakino.test/serials/example#mkep=1","episode_number":1},{"url":"https://megakino.test/serials/example#mkep=2","episode_number":2}]}""");
        var episodes = AniWorldEpisodeParser.Parse(data.RootElement, 1);
        Check(episodes.Count == 2 && episodes[0].Url.EndsWith("#mkep=1", StringComparison.Ordinal));
        Check(episodes[1].Url.EndsWith("#mkep=2", StringComparison.Ordinal));
        Check(!MediaAccessGrantStore.TryNormalizeUrl("https://megakino.test/serials/example#mkep=0", out _));
    }

    internal static void GravityFallsRelevance()
    {
        using var data = JsonDocument.Parse("""
            {"results":[
              {"title":"Cat Gravity","url":"https://example.test/cat","media_type":"series"},
              {"title":"True True","url":"https://example.test/true","media_type":"series"},
              {"title":"Willkommen in Gravity Falls","url":"https://example.test/gravity","media_type":"series"}]}
            """);
        var filtered = SearchRelevance.Filter(data.RootElement, "gravity falls", "series");
        Check(filtered.GetProperty("results").GetArrayLength() == 1);
        Check(filtered.GetProperty("results")[0].GetProperty("title").GetString() == "Willkommen in Gravity Falls");
    }

    internal static void AliasesYearsAndEditions()
    {
        using var alias = JsonDocument.Parse("""{"title":"Anderer Titel","original_title":"Gravity Falls"}""");
        Check(SearchRelevance.Score(alias.RootElement, "gravity falls") > 0);
        using var year = JsonDocument.Parse("""{"title":"Dune","year":"1984"}""");
        Check(SearchRelevance.Score(year.RootElement, "Dune 2021") == 0);
        using var book = JsonDocument.Parse("""{"title":"Dune","author":"Frank Herbert","media_kind":"ebook"}""");
        Check(SearchRelevance.Score(book.RootElement, "Herbert") > 0);
        using var editions = JsonDocument.Parse("""{"results":[{"title":"Dune","url":"https://example.test/1984"},{"title":"Dune","url":"https://example.test/2021"}]}""");
        Check(SearchRelevance.Filter(editions.RootElement, "Dune", "movie").GetProperty("results").GetArrayLength() == 2);
    }

    internal static void MangaIsNotSeries()
    {
        var source = AniWorldSiteRegistry.KnownSites.Single(item => item.Id == "mangafire");
        Check(source.MediaTypes.SequenceEqual(["manga"]));
        Check(!MediaCategories.IsAllowed("ebook", new PluginConfiguration()));
        Check(!MediaCategories.IsAllowed("audiobook", new PluginConfiguration()));
        Check(!MediaCategories.IsAllowed("podcast", new PluginConfiguration()));
    }

    internal static void CapabilityPolicy()
    {
        using var fixture = new Fixture();
        using var settings = JsonDocument.Parse(Fixture.Settings);
        using var metadata = JsonDocument.Parse(Fixture.Sources);
        fixture.Config.AllowEbookRequests = false;
        Check(AniWorldRequestApplicationService.ReadSourceCapabilities(settings.RootElement, metadata.RootElement, fixture.Config).All(item => item.Backend != "catalog"));
        fixture.Config.AllowEbookRequests = true;
        fixture.Config.AllowedSources = "gutenberg";
        var allowed = AniWorldRequestApplicationService.ReadSourceCapabilities(settings.RootElement, metadata.RootElement, fixture.Config);
        Check(allowed.Count == 1 && allowed[0].Id == "gutenberg");
    }

    internal static void CatalogLifecycle()
    {
        using var fixture = new Fixture();
        fixture.Grants.GrantCatalogItem("user", "gutenberg", "42");
        var detail = fixture.Application.GetCatalogDetailAsync("user", "gutenberg", "42", "ebook", CancellationToken.None).GetAwaiter().GetResult();
        Check(detail.Assets.Count == 1 && detail.Assets[0].Extension == "epub");
        var submitted = fixture.Submit();
        var request = submitted.Request!;
        Check(request.Backend == "catalog" && request.CatalogItemId == "42" && request.CatalogAssetId == "epub");
        Check(request.Episodes.Count == 0 && request.SeriesUrl == string.Empty);
        var duplicate = fixture.Submit();
        Check(duplicate.Disposition == SubmitDisposition.Duplicate);
        var queued = fixture.Application.ApproveAsync(request.Id, "admin", CancellationToken.None).GetAwaiter().GetResult();
        Check(queued.Status == RequestStatuses.Queued && queued.AniWorldQueueId == 77);
        fixture.Application.ApproveAsync(request.Id, "admin", CancellationToken.None).GetAwaiter().GetResult();
        Check(fixture.DownloadCalls == 1);
        Check(fixture.LastPayload.GetProperty("source").GetString() == "gutenberg");
        Check(fixture.LastPayload.GetProperty("id").GetString() == "42");
        Check(fixture.LastPayload.GetProperty("asset_id").GetString() == "epub");
        Check(fixture.LastPayload.GetProperty("request_key").GetString() == request.SubmissionKey);
        var reloaded = new RequestStore(fixture.Directory).GetAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult()!;
        Check(reloaded.Backend == "catalog" && reloaded.SubmissionKey == request.SubmissionKey);
        fixture.Store.SyncQueueStatesForAllAsync(new Dictionary<long, string> { [77] = "completed" }, CancellationToken.None).GetAwaiter().GetResult();
        Check(fixture.Store.ListPendingLibraryScansAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 0);
    }

    internal static void DisabledCatalogIsEnforcedEverywhere()
    {
        using var fixture = new Fixture();
        fixture.Grants.GrantCatalogItem("user", "gutenberg", "42");
        var request = fixture.Submit().Request!;
        fixture.Config.AllowEbookRequests = false;
        Reject(() => fixture.Application.SearchAsync("user", "dune", "all", "ebook", false, CancellationToken.None).GetAwaiter().GetResult());
        Reject(() => fixture.Application.GetCatalogDetailAsync("user", "gutenberg", "42", "ebook", CancellationToken.None).GetAwaiter().GetResult());
        Reject(() => fixture.Application.DiscoverAsync("user", "ebook", "all", CancellationToken.None).GetAwaiter().GetResult());
        Reject(() => fixture.Application.GetCatalogCoverAsync("user", "gutenberg", "ebook", "signed.token", CancellationToken.None).GetAwaiter().GetResult());
        Reject(() => fixture.Submit());
        Reject(() => fixture.Application.ApproveAsync(request.Id, "admin", CancellationToken.None).GetAwaiter().GetResult());
        Check(fixture.Store.GetAsync(request.Id, CancellationToken.None).GetAwaiter().GetResult()!.Status == "pending");
        Check(fixture.DownloadCalls == 0);
    }

    internal static void AssetChangesDoNotQueueAnotherFormat()
    {
        using var fixture = new Fixture();
        fixture.Grants.GrantCatalogItem("user", "gutenberg", "42");
        var request = fixture.Submit().Request!;
        fixture.AssetAvailable = false;
        var result = fixture.Application.ApproveAsync(request.Id, "admin", CancellationToken.None).GetAwaiter().GetResult();
        Check(result.Status == "failed" && fixture.DownloadCalls == 0);
    }

    internal static void CatalogGrantIsBoundToUserAndSource()
    {
        var grants = new MediaAccessGrantStore();
        grants.GrantCatalogItem("alice", "gutenberg", "42");
        Check(grants.IsCatalogItemGranted("alice", "gutenberg", "42"));
        Check(!grants.IsCatalogItemGranted("bob", "gutenberg", "42"));
        Check(!grants.IsCatalogItemGranted("alice", "standard_ebooks", "42"));
    }

    internal static void AllSourcesAreSearchedWithConcurrencyOne()
    {
        using var fixture = new Fixture();
        fixture.Config.MaxSearchSources = 1;
        var result = fixture.Application.SearchAsync("user", "Dune", "all", "ebook", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(result.Groups.Count == 2 && result.Groups.All(group => group.Error is null));
        Check(fixture.SearchCalls == 2);
    }

    internal static void DiscoveryKeepsActualSources()
    {
        using var fixture = new Fixture();
        var sections = fixture.Application.DiscoverAsync("user", "series", "all", CancellationToken.None).GetAwaiter().GetResult();
        Check(sections.Count == 2);
        foreach (var section in sections)
        {
            Check(section.Items.Count > 0);
            Check(section.Items.All(item => item.GetProperty("source").GetString() == section.Source));
        }
        Check(sections.Any(section => section.Source == "moflix"));
        fixture.FailMoflix = true;
        sections = fixture.Application.DiscoverAsync("user", "series", "all", CancellationToken.None).GetAwaiter().GetResult();
        Check(sections.Single(section => section.Source == "sto").Items.Count > 0);
        Check(sections.Single(section => section.Source == "moflix").Error is not null);
    }

    internal static void UnknownTypesAreRejected()
    {
        using var fixture = new Fixture();
        Reject(() => fixture.Application.SearchAsync("user", "dune", "all", "unknown", false, CancellationToken.None).GetAwaiter().GetResult());
        using var wrong = JsonDocument.Parse("""{"id":"42","source":"gutenberg","media_kind":"podcast","title":"Dune","assets":[]}""");
        Reject(() => AniWorldRequestApplicationService.ReadCatalogDetail(wrong.RootElement, "gutenberg", "42", "ebook"));
    }

    internal static void TwoSearchGroupsRespectPermissions()
    {
        using var config = JsonDocument.Parse(JsonSerializer.Serialize(MediaCategories.Available(new PluginConfiguration())));
        Check(config.RootElement.GetArrayLength() == 1);
        Check(config.RootElement[0].GetProperty("id").GetString() == "video");
        using var enabled = JsonDocument.Parse(JsonSerializer.Serialize(MediaCategories.Available(new PluginConfiguration { AllowEbookRequests = true })));
        Check(enabled.RootElement.GetArrayLength() == 2);
        Check(enabled.RootElement[1].GetProperty("id").GetString() == "catalog");
    }

    internal static void GroupedVideoSearchKeepsMixedAndUnknownTypes()
    {
        using var fixture = new Fixture();
        var response = fixture.Application.SearchAsync("user", "gravity falls", "all", "video", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(response.Groups.Count == 3);
        Check(response.Groups.All(group => group.Error is null && group.Data!.Value.GetProperty("results").GetArrayLength() > 0));
        Check(response.Groups.Single(group => group.Source == "moflix").Data!.Value.GetProperty("results").GetArrayLength() == 3);
        Check(fixture.VideoRequests.All(payload => !payload.TryGetProperty("category", out _)));
    }

    internal static void GroupedDiscoveryKeepsMissingTypes()
    {
        using var fixture = new Fixture { MissingBrowseTypes = true };
        var sections = fixture.Application.DiscoverAsync("user", "video", "all", CancellationToken.None).GetAwaiter().GetResult();
        Check(sections.Count == 3 && sections.All(section => section.Items.Count == 1));
        Check(sections.Single(section => section.Source == "mangafire").Items[0].GetProperty("media_type").GetString() == "manga");
        Check(sections.Single(section => section.Source == "moflix").Items[0].GetProperty("media_type").GetString() == string.Empty);
        Check(fixture.BrowseQueries.All(query => !query.Contains("category=", StringComparison.Ordinal)));
    }

    internal static void MalformedSearchIsAnErrorNotNoResults()
    {
        using var fixture = new Fixture { MalformedSearch = true };
        var response = fixture.Application.SearchAsync("user", "gravity falls", "sto", "series", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(response.Groups.Single().Error is not null);
    }

    internal static void CatalogGroupUsesOnlyAllowedKinds()
    {
        using var fixture = new Fixture();
        var response = fixture.Application.SearchAsync("user", "Dune", "all", "catalog", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(response.Groups.Count == 2 && response.Groups.All(group => group.Error is null));
        fixture.Config.AllowAudiobookRequests = true;
        response = fixture.Application.SearchAsync("user", "Dune", "all", "catalog", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(response.Groups.Count == 3);
        Check(response.Groups.Single(group => group.Source == "librivox").Data!.Value.GetProperty("results")[0].GetProperty("media_kind").GetString() == "audiobook");
        fixture.Config.AllowEbookRequests = false;
        response = fixture.Application.SearchAsync("user", "Dune", "all", "catalog", false, CancellationToken.None).GetAwaiter().GetResult();
        Check(response.Groups.Count == 1 && response.Groups[0].Source == "librivox");
        fixture.Config.AllowAudiobookRequests = false;
        Reject(() => fixture.Application.SearchAsync("user", "Dune", "all", "catalog", false, CancellationToken.None).GetAwaiter().GetResult());
    }

    internal static void MangaPlanRetainsConcreteType()
    {
        using var fixture = new Fixture();
        const string url = "https://mangafire.to/title/example";
        fixture.Grants.GrantUrl("user", "mangafire", url);
        var selection = new AutomaticMediaRequest { Title = "Example", Source = "mangafire", SeriesUrl = url, MediaType = "manga", Language = "MangaFire", Provider = "MangaFire" };
        var plan = fixture.Application.PlanAsync("user", selection, true, false, CancellationToken.None).GetAwaiter().GetResult();
        Check(!plan.IsMovie && plan.MissingItems.Count == 1);
        selection.PlanToken = plan.PlanToken;
        var submitted = fixture.Application.SubmitAutomaticAsync("user", "User", selection, true, CancellationToken.None).GetAwaiter().GetResult();
        Check(submitted.Request!.MediaType == "manga" && submitted.Request.Status == "pending");
        Check(submitted.Request.Episodes[0].SelectedPages!.SequenceEqual([1]));
    }

    private static void Check(bool value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (!value) throw new InvalidOperationException("Catalog contract assertion failed: " + expression);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (AniWorldApplicationException) { return; }
        throw new InvalidOperationException("Expected application rejection.");
    }

    internal sealed class Fixture : IDisposable
    {
        internal const string Settings = """{"available_sites":[{"key":"sto"},{"key":"moflix"},{"key":"mangafire"}],"enable_sto":true,"enable_moflix":true,"enable_mangafire":true}""";
        internal const string Sources = """
            {"schema_version":1,"sources":[
              {"id":"sto","label":"Serienstream","backend":"video","enabled":true,"media_types":["series"],"browse_paths":["api/new-series"]},
              {"id":"moflix","label":"Moflix","backend":"video","enabled":true,"media_types":["movie","series"],"browse_paths":["api/moflix-movies"]},
              {"id":"mangafire","label":"MangaFire","backend":"video","enabled":true,"media_types":["manga"],"browse_paths":["api/mangafire-trending"]},
              {"id":"gutenberg","label":"Gutenberg","backend":"catalog","enabled":true,"media_types":["ebook"],"browse_paths":[]},
              {"id":"standard_ebooks","label":"Standard Ebooks","backend":"catalog","enabled":true,"media_types":["ebook"],"browse_paths":[]},
              {"id":"librivox","label":"LibriVox","backend":"catalog","enabled":true,"media_types":["audiobook"],"browse_paths":[]}]}
            """;
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "connector-catalog-" + Guid.NewGuid().ToString("N"));
        internal PluginConfiguration Config { get; } = new() { AniWorldUrl = "http://downloader.test/prefix", AllowEbookRequests = true };
        internal MediaAccessGrantStore Grants { get; } = new();
        internal RequestStore Store { get; }
        internal AniWorldRequestApplicationService Application { get; }
        internal AniWorldRequestsController Controller { get; }
        internal AniWorldClient Client { get; }
        internal SecretStore Secrets { get; }
        internal int DownloadCalls;
        internal int SearchCalls;
        internal bool AssetAvailable = true;
        internal bool FailMoflix;
        internal bool MissingBrowseTypes;
        internal bool MalformedSearch;
        internal bool FailVideoSearch;
        internal bool LegacySources;
        internal bool VideoPlanning;
        internal List<JsonElement> VideoRequests { get; } = [];
        internal List<string> BrowseQueries { get; } = [];
        internal JsonElement LastPayload;
        private readonly HttpClient _http;

        internal Fixture()
        {
            Store = new RequestStore(Directory);
            var secrets = new SecretStore(Directory);
            secrets.SetApiKey("contract-test-key");
            Secrets = secrets;
            _http = new HttpClient(new Handler(Respond));
            var client = new AniWorldClient(_http, secrets, () => Config);
            Client = client;
            Application = new AniWorldRequestApplicationService(client, Store, Grants, new UserRateLimiter(),
                new JellyfinLibraryAvailabilityService(System.Reflection.DispatchProxy.Create<MediaBrowser.Controller.Library.ILibraryManager, EmptyLibraryProxy>()), new JellixSelectionTokenStore(), () => Config);
            Controller = new AniWorldRequestsController(client, Grants, new UserRateLimiter(), secrets, Application)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
            };
        }

        internal SubmitMediaRequestResult Submit() => Application.SubmitCatalogAsync("user", "User", new CatalogMediaRequest
        {
            Source = "gutenberg", ItemId = "42", AssetId = "epub", MediaType = "ebook",
        }, CancellationToken.None).GetAwaiter().GetResult();

        private async Task<HttpResponseMessage> Respond(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Check(path.StartsWith("/prefix/api/", StringComparison.Ordinal));
            Check(request.Headers.Contains("X-API-Key"));
            object data;
            if (path.EndsWith("/api/settings", StringComparison.Ordinal)) data = JsonSerializer.Deserialize<JsonElement>(Settings);
            else if (path.EndsWith("/api/sources", StringComparison.Ordinal))
            {
                if (LegacySources) return new HttpResponseMessage(HttpStatusCode.NotFound);
                data = JsonSerializer.Deserialize<JsonElement>(Sources);
            }
            else if (path.EndsWith("/api/catalog/details", StringComparison.Ordinal))
                data = new { id = "42", source = "gutenberg", media_kind = "ebook", title = "Dune", author = "Frank Herbert", language = "en", assets = AssetAvailable ? new[] { new { id = "epub", label = "EPUB", extension = "epub", downloaded = false } } : [] };
            else if (path.EndsWith("/api/catalog/download", StringComparison.Ordinal))
            {
                DownloadCalls++;
                LastPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                data = new { queue_id = 77 };
            }
            else if (path.EndsWith("/api/catalog/search", StringComparison.Ordinal))
            {
                SearchCalls++;
                var kind = request.RequestUri.Query.Contains("source=librivox", StringComparison.Ordinal) ? "audiobook" : "ebook";
                data = new { items = new[] { new { id = "42", title = "Dune", media_kind = kind, author = "Frank Herbert" } } };
            }
            else if (path.EndsWith("/api/series", StringComparison.Ordinal))
                data = VideoPlanning ? new { title = "Gravity Falls", is_movie = false } : (object)new { title = "Example", description = "Manga" };
            else if (path.EndsWith("/api/seasons", StringComparison.Ordinal))
                data = VideoPlanning
                    ? new { seasons = new[] { new { url = "https://example.test/series/season-1", season_number = 1, episode_count = 2, are_movies = false } } }
                    : (object)new { seasons = new[] { new { url = "https://mangafire.to/title/example/chapter/1", season_number = 1, episode_count = 1, are_movies = false } } };
            else if (path.EndsWith("/api/episodes", StringComparison.Ordinal))
                data = VideoPlanning
                    ? new { episodes = new[] {
                        new { url = "https://example.test/series/season-1/episode-1", episode_number = 1, available_languages = new[] { "German Dub" } },
                        new { url = "https://example.test/series/season-1/episode-2", episode_number = 2, available_languages = new[] { "German Dub" } } } }
                    : (object)new { episodes = new[] { new { url = "https://mangafire.to/title/example/chapter/1", chapter_url = "https://mangafire.to/title/example/chapter/1", episode_number = 1, page_number = 1 } } };
            else if (path.EndsWith("/api/providers", StringComparison.Ordinal)) data = new { providers = new Dictionary<string, string[]> { ["German Dub"] = ["VOE"] } };
            else if (path.EndsWith("/api/custom-paths", StringComparison.Ordinal)) data = new { paths = Array.Empty<object>() };
            else if (path.EndsWith("/api/download", StringComparison.Ordinal))
            {
                DownloadCalls++;
                LastPayload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                data = new { queue_id = 88 };
            }
            else if (path.EndsWith("/api/search", StringComparison.Ordinal))
            {
                var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
                lock (VideoRequests) VideoRequests.Add(payload);
                if (FailVideoSearch && payload.GetProperty("site").GetString() == "moflix") return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                data = MalformedSearch ? new { unexpected = true } : (object)new { results = new object[] {
                    new { title = "Gravity Falls", url = "https://example.test/movie", media_type = "movie" },
                    new { title = "Willkommen in Gravity Falls", url = "https://example.test/series", media_type = "series" },
                    new { title = "Gravity Falls", url = "https://example.test/unknown" },
                    new { title = "Cat Gravity", url = "https://example.test/unrelated" } } };
            }
            else if (path.EndsWith("/api/new-series", StringComparison.Ordinal) || path.EndsWith("/api/moflix-movies", StringComparison.Ordinal) || path.EndsWith("/api/mangafire-trending", StringComparison.Ordinal))
            {
                lock (BrowseQueries) BrowseQueries.Add(request.RequestUri.Query);
                if (FailMoflix && path.EndsWith("/api/moflix-movies", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.BadGateway);
                data = MissingBrowseTypes
                    ? new { results = new object[] { new { title = "Example", url = "https://example.test" + path } } }
                    : (object)new { results = new[] { new { title = "Example", url = "https://example.test" + path, media_type = "series" } } };
            }
            else throw new InvalidOperationException("Unexpected upstream request: " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data), Encoding.UTF8, "application/json") };
        }

        public void Dispose()
        {
            _http.Dispose();
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
}

public class EmptyLibraryProxy : System.Reflection.DispatchProxy
{
    protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        => targetMethod?.Name == "GetItemList" ? Array.Empty<MediaBrowser.Controller.Entities.BaseItem>() : null;
}
