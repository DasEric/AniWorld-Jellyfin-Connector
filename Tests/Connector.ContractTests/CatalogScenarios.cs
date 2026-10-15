using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Configuration;
using Jellyfin.Plugin.AniWorld.Models;
using Jellyfin.Plugin.AniWorld.Services;

internal static class CatalogScenarios
{
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

    private static void Check(bool value)
    {
        if (!value) throw new InvalidOperationException("Catalog contract assertion failed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (AniWorldApplicationException) { return; }
        throw new InvalidOperationException("Expected application rejection.");
    }

    private sealed class Fixture : IDisposable
    {
        internal const string Settings = """{"available_sites":[{"key":"sto"},{"key":"moflix"}],"enable_sto":true,"enable_moflix":true}""";
        internal const string Sources = """
            {"schema_version":1,"sources":[
              {"id":"sto","label":"Serienstream","backend":"video","enabled":true,"media_types":["series"],"browse_paths":["api/new-series"]},
              {"id":"moflix","label":"Moflix","backend":"video","enabled":true,"media_types":["movie","series"],"browse_paths":["api/moflix-movies"]},
              {"id":"gutenberg","label":"Gutenberg","backend":"catalog","enabled":true,"media_types":["ebook"],"browse_paths":[]},
              {"id":"standard_ebooks","label":"Standard Ebooks","backend":"catalog","enabled":true,"media_types":["ebook"],"browse_paths":[]}]}
            """;
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "connector-catalog-" + Guid.NewGuid().ToString("N"));
        internal PluginConfiguration Config { get; } = new() { AniWorldUrl = "http://downloader.test/prefix", AllowEbookRequests = true };
        internal MediaAccessGrantStore Grants { get; } = new();
        internal RequestStore Store { get; }
        internal AniWorldRequestApplicationService Application { get; }
        internal int DownloadCalls;
        internal int SearchCalls;
        internal bool AssetAvailable = true;
        internal bool FailMoflix;
        internal JsonElement LastPayload;
        private readonly HttpClient _http;

        internal Fixture()
        {
            Store = new RequestStore(Directory);
            var secrets = new SecretStore(Directory);
            secrets.SetApiKey("contract-test-key");
            _http = new HttpClient(new Handler(Respond));
            var client = new AniWorldClient(_http, secrets, () => Config);
            Application = new AniWorldRequestApplicationService(client, Store, Grants, new UserRateLimiter(),
                new JellyfinLibraryAvailabilityService(null!), new JellixSelectionTokenStore(), () => Config);
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
            else if (path.EndsWith("/api/sources", StringComparison.Ordinal)) data = JsonSerializer.Deserialize<JsonElement>(Sources);
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
                data = new { items = new[] { new { id = "42", title = "Dune", media_kind = "ebook", author = "Frank Herbert" } } };
            }
            else if (path.EndsWith("/api/new-series", StringComparison.Ordinal) || path.EndsWith("/api/moflix-movies", StringComparison.Ordinal))
            {
                if (FailMoflix && path.EndsWith("/api/moflix-movies", StringComparison.Ordinal)) return new HttpResponseMessage(HttpStatusCode.BadGateway);
                data = new { results = new[] { new { title = "Example", url = "https://example.test" + path, media_type = "series" } } };
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
