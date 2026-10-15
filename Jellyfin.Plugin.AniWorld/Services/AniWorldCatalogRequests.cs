using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.AniWorld.Models;

namespace Jellyfin.Plugin.AniWorld.Services;

public sealed partial class AniWorldRequestApplicationService
{
    private void GrantCatalogResults(string userId, string source, JsonElement response)
    {
        if (!response.TryGetProperty("results", out var items) || items.ValueKind != JsonValueKind.Array) return;
        foreach (var item in items.EnumerateArray())
        {
            var id = ReadJsonString(item, "id", 2048);
            if (id.Length > 0) _grants.GrantCatalogItem(userId, source, id);
        }
    }

    private async Task<AniWorldSiteInfo> RequireSourceAsync(string userId, string source, string category, CancellationToken cancellationToken)
    {
        if (!MediaCategories.IsAllowed(category, CurrentConfiguration))
            throw new AniWorldApplicationException(HttpStatusCode.Forbidden, "Diese Kategorie ist in den Admin-Einstellungen deaktiviert.");
        var sources = await GetAllowedSourcesAsync(userId, cancellationToken, applyRateLimit: false).ConfigureAwait(false);
        return sources.FirstOrDefault(item => item.Id == source && item.MediaTypes.Contains(category, StringComparer.Ordinal))
            ?? throw SourceNotAllowed();
    }

    public async Task<CatalogDetail> GetCatalogDetailAsync(string userId, string source, string id, string category, CancellationToken cancellationToken, bool requireGrant = true)
    {
        if (!MediaCategories.IsCatalog(category) || id.Length is < 1 or > 2048 || id.Any(char.IsControl))
            throw new AniWorldApplicationException(HttpStatusCode.BadRequest, "Ungültige Katalogauswahl.");
        var info = await RequireSourceAsync(userId, source, category, cancellationToken).ConfigureAwait(false);
        if (info.Backend != "catalog" || (requireGrant && !_grants.IsCatalogItemGranted(userId, source, id)))
            throw new AniWorldApplicationException(HttpStatusCode.BadRequest, "Die Katalogauswahl ist abgelaufen. Bitte erneut suchen.");
        var data = await _aniWorld.GetCatalogDetailsAsync(source, id, cancellationToken).ConfigureAwait(false);
        return ReadCatalogDetail(data, source, id, category);
    }

    internal static CatalogDetail ReadCatalogDetail(JsonElement data, string source, string id, string category)
    {
        if (data.ValueKind != JsonValueKind.Object || ReadJsonString(data, "id", 2048) != id
            || ReadJsonString(data, "source", 80) != source || ReadJsonString(data, "media_kind", 20) != category
            || !data.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            throw new AniWorldApplicationException(HttpStatusCode.BadGateway, "Die Katalogdetails stimmen nicht mit der Auswahl überein.");
        var title = ReadJsonString(data, "title", 300);
        if (title.Length == 0) throw new AniWorldApplicationException(HttpStatusCode.BadGateway, "Der Katalogtitel fehlt.");
        var options = assets.EnumerateArray().Where(asset => asset.ValueKind == JsonValueKind.Object).Take(500)
            .Select(asset => new CatalogAssetOption(ReadJsonString(asset, "id", 2048), ReadJsonString(asset, "label", 300), ReadJsonString(asset, "extension", 20),
                asset.TryGetProperty("downloaded", out var downloaded) && downloaded.ValueKind == JsonValueKind.True,
                ReadJsonString(asset, "target_path", 4096)))
            .Where(asset => asset.Id.Length > 0 && asset.Extension.Length > 0).DistinctBy(asset => asset.Id).ToArray();
        return new CatalogDetail(id, source, category, title, ReadJsonString(data, "author", 300), ReadJsonString(data, "year", 16),
            ReadJsonString(data, "language", 100), ReadJsonString(data, "description", 8000), ReadJsonString(data, "cover_url", 4096), options);
    }

    public async Task<SubmitMediaRequestResult> SubmitCatalogAsync(string userId, string username, CatalogMediaRequest selection, CancellationToken cancellationToken)
    {
        if (CurrentConfiguration.MaintenanceMode)
            throw new AniWorldApplicationException(HttpStatusCode.ServiceUnavailable, "Neue Anfragen sind vorübergehend deaktiviert.");
        if (!Allow(userId, "request", 10)) throw TooManyRequests();
        if (selection.AssetId.Length is < 1 or > 2048 || selection.AssetId.Any(char.IsControl))
            throw new AniWorldApplicationException(HttpStatusCode.BadRequest, "Bitte eine gültige Katalogdatei auswählen.");
        var detail = await GetCatalogDetailAsync(userId, selection.Source, selection.ItemId, selection.MediaType, cancellationToken).ConfigureAwait(false);
        var asset = detail.Assets.FirstOrDefault(item => item.Id == selection.AssetId)
            ?? throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Die ausgewählte Datei ist nicht mehr verfügbar. Bitte den Titel erneut öffnen.");
        if (asset.Downloaded) return new SubmitMediaRequestResult(SubmitDisposition.AlreadyAvailable, null, null, 0);
        var limit = Math.Clamp(CurrentConfiguration.MaxPendingRequestsPerUser, 1, 100);
        var result = await _store.TryAddAsync(userId, username, new CreateMediaRequest
        {
            Backend = "catalog", Title = detail.Title, Source = detail.Source, MediaType = detail.MediaKind,
            CatalogItemId = detail.Id, CatalogAssetId = asset.Id, Format = asset.Extension,
            SelectionLabel = MediaCategories.Labels[detail.MediaKind] + " · " + asset.Extension.ToUpperInvariant(),
            Language = detail.Language, Provider = "catalog:" + detail.Source,
        }, RequestStatuses.Pending, limit, cancellationToken).ConfigureAwait(false);
        if (result.Duplicate is not null) return new SubmitMediaRequestResult(SubmitDisposition.Duplicate, null, result.Duplicate, limit);
        if (result.LimitReached) return new SubmitMediaRequestResult(SubmitDisposition.LimitReached, null, null, limit);
        if (result.StoreCapacityReached) return new SubmitMediaRequestResult(SubmitDisposition.StoreCapacityReached, null, null, limit);
        var stored = result.Request ?? throw new InvalidOperationException("Catalog request was not stored.");
        if (!CurrentConfiguration.AutoApproveRequests) return new SubmitMediaRequestResult(SubmitDisposition.Stored, stored, null, limit);
        var queued = await QueueRequestAsync(stored.Id, "automatic", cancellationToken).ConfigureAwait(false);
        return new SubmitMediaRequestResult(queued.Status is RequestStatuses.Queued or RequestStatuses.Completed ? SubmitDisposition.Queued : SubmitDisposition.QueueFailed, queued, null, limit);
    }

    private async Task<MediaRequest> QueueCatalogRequestAsync(MediaRequest request, string decidedBy, CancellationToken cancellationToken)
    {
        var detail = await GetCatalogDetailAsync(request.UserId, request.Source, request.CatalogItemId, request.MediaType, cancellationToken, requireGrant: false).ConfigureAwait(false);
        var asset = detail.Assets.FirstOrDefault(item => item.Id == request.CatalogAssetId && item.Extension == request.Format)
            ?? throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Das angefragte Format ist nicht mehr verfügbar. Bitte neu auswählen.");
        if (asset.Downloaded)
        {
            // Completed is a download state, not a claim of Jellyfin availability.
            await _store.MarkCatalogCompletedAsync(request.Id, decidedBy, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var queued = await _aniWorld.QueueCatalogAsync(request, cancellationToken).ConfigureAwait(false);
            await _store.MarkQueuedAsync(request.Id, queued.QueueId, decidedBy, cancellationToken).ConfigureAwait(false);
        }
        return await _store.GetAsync(request.Id, CancellationToken.None).ConfigureAwait(false) ?? request;
    }

    public async Task<AniWorldImage> GetCatalogCoverAsync(string userId, string source, string category, string token, CancellationToken cancellationToken)
    {
        var info = await RequireSourceAsync(userId, source, category, cancellationToken).ConfigureAwait(false);
        if (info.Backend != "catalog" || token.Length is < 1 or > 4096
            || !System.Text.RegularExpressions.Regex.IsMatch(token, "^[A-Za-z0-9_.-]+$"))
            throw new AniWorldApplicationException(HttpStatusCode.BadRequest, "Ungültiges Katalogcover.");
        return await _aniWorld.GetCatalogCoverAsync(token, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DiscoverySection>> DiscoverAsync(string userId, string category, string source, CancellationToken cancellationToken)
    {
        if (!MediaCategories.IsAllowed(category, CurrentConfiguration))
            throw new AniWorldApplicationException(HttpStatusCode.Forbidden, "Diese Kategorie ist deaktiviert.");
        var sources = (await GetAllowedSourcesAsync(userId, cancellationToken, applyRateLimit: false).ConfigureAwait(false))
            .Where(item => item.MediaTypes.Contains(category, StringComparer.Ordinal) && (source == "all" || item.Id == source)).ToArray();
        using var concurrency = new SemaphoreSlim(Math.Clamp(CurrentConfiguration.MaxSearchSources, 1, 4));
        return await Task.WhenAll(sources.Select(async info =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var items = new List<JsonElement>();
                var errors = new List<string>();
                foreach (var path in info.Backend == "catalog" ? new[] { "api/catalog/browse" } : info.BrowsePaths ?? [])
                {
                    try
                    {
                        var data = await _aniWorld.BrowseAsync(path, info.Id, category, info.Backend == "catalog", cancellationToken).ConfigureAwait(false);
                        var key = info.Backend == "catalog" ? "items" : "results";
                        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(key, out var values) || values.ValueKind != JsonValueKind.Array)
                        {
                            errors.Add("Die Quelle hat keine gültige Inhaltsliste geliefert.");
                            continue;
                        }
                        foreach (var value in values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.Object))
                        {
                            if (ReadJsonString(value, "title", 300).Length == 0 && ReadJsonString(value, "name", 300).Length == 0) continue;
                            var kind = ReadJsonString(value, info.Backend == "catalog" ? "media_kind" : "media_type", 20);
                            if (kind.Length == 0 && info.MediaTypes.Count == 1) kind = info.MediaTypes[0];
                            if (MediaCategories.Normalize(kind) != category) continue;
                            var fields = value.EnumerateObject().ToDictionary(property => property.Name, property => (object)property.Value);
                            fields["source"] = info.Id;
                            fields["source_label"] = info.Label;
                            fields["media_type"] = category;
                            if (info.Backend == "catalog")
                            {
                                var id = ReadJsonString(value, "id", 2048);
                                if (id.Length == 0) continue;
                                _grants.GrantCatalogItem(userId, info.Id, id);
                            }
                            else
                            {
                                var rawUrl = ReadJsonString(value, "url", 2048);
                                if (!MediaAccessGrantStore.TryNormalizeUrl(rawUrl, out var normalized)) continue;
                                fields["url"] = normalized;
                                _grants.GrantUrl(userId, info.Id, normalized);
                            }
                            items.Add(JsonSerializer.SerializeToElement(fields));
                        }
                    }
                    catch (AniWorldException exception) { errors.Add(exception.Message); }
                }
                var unique = items.DistinctBy(item => ReadJsonString(item, info.Backend == "catalog" ? "id" : "url", 2048)).Take(18).ToArray();
                return new DiscoverySection(info.Id, info.Label, unique, errors.Count > 0 ? string.Join(" ", errors.Distinct()) : null);
            }
            finally { concurrency.Release(); }
        })).ConfigureAwait(false);
    }
}

public sealed record CatalogAssetOption(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("extension")] string Extension,
    [property: JsonPropertyName("downloaded")] bool Downloaded,
    [property: JsonPropertyName("target_path")] string TargetPath);

public sealed record CatalogDetail(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("media_kind")] string MediaKind,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string Author,
    [property: JsonPropertyName("year")] string Year,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("cover_url")] string CoverUrl,
    [property: JsonPropertyName("assets")] IReadOnlyList<CatalogAssetOption> Assets);

public sealed record DiscoverySection(string Source, string Label, IReadOnlyList<JsonElement> Items, string? Error);
