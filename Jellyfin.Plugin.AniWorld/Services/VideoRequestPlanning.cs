using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Models;

namespace Jellyfin.Plugin.AniWorld.Services;

public sealed partial class AniWorldRequestApplicationService
{
    private async Task<MissingMediaPlan> BuildMissingPlanAsync(string userId, AutomaticMediaRequest request,
        CancellationToken cancellationToken, bool requireGrant, bool refreshOffers = false)
    {
        if (request.Source == "mangafire")
            return await BuildLegacyMissingPlanAsync(userId, request, cancellationToken, requireGrant).ConfigureAwait(false);
        if (requireGrant && (!_grants.IsGranted(userId, request.SeriesUrl, out var source) || source != request.Source))
            throw new AniWorldApplicationException(HttpStatusCode.BadRequest, "Bitte den Titel erneut über die Suche öffnen.");
        var detailTask = _aniWorld.GetSeriesAsync(request.SeriesUrl, cancellationToken);
        var seasonsTask = _aniWorld.GetSeasonsAsync(request.SeriesUrl, cancellationToken);
        await Task.WhenAll(detailTask, seasonsTask).ConfigureAwait(false);
        var detail = await detailTask.ConfigureAwait(false);
        var response = await seasonsTask.ConfigureAwait(false);
        if (!response.TryGetProperty("seasons", out var values) || values.ValueKind != JsonValueKind.Array
            || values.GetArrayLength() is < 1 or > 1000) throw InvalidSeasonList();
        var title = ReadJsonString(detail, "title", 300);
        if (title.Length == 0) title = request.Title;
        var targets = new List<VideoTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var explicitMovie = detail.TryGetProperty("is_movie", out var movieValue)
            && movieValue.ValueKind is JsonValueKind.True or JsonValueKind.False ? movieValue.GetBoolean() : (bool?)null;
        foreach (var season in values.EnumerateArray())
        {
            var seasonUrl = ReadJsonString(season, "url", 2048);
            if (!MediaAccessGrantStore.TryNormalizeUrl(seasonUrl, out var normalized)) throw InvalidSeasonList();
            var isMovie = season.TryGetProperty("are_movies", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? flag.GetBoolean() : explicitMovie ?? throw new AniWorldApplicationException(HttpStatusCode.Conflict,
                    "Der Inhaltstyp ist unbekannt. Bitte die Quelle prüfen; es wurde kein Downloadziel geraten.");
            var seasonNumber = ReadOptionalInt(season, "season_number");
            var episodes = await AniWorldEpisodeParser.FetchCompleteAsync(
                token => _aniWorld.GetEpisodesAsync(normalized, request.SeriesUrl, token),
                data => _grants.GrantFromJson(userId, request.Source, data), seasonNumber,
                ReadExpectedEpisodeCount(season), cancellationToken).ConfigureAwait(false);
            foreach (var episode in episodes)
            {
                if (!seen.Add(episode.Url)) throw InvalidSeasonList();
                if (!isMovie && (episode.SeasonNumber is null or < 0 || episode.EpisodeNumber is null or <= 0)) throw InvalidSeasonList();
                if (targets.Count >= MaxEpisodesPerRequest)
                    throw new AniWorldApplicationException(HttpStatusCode.BadRequest, $"Bitte höchstens {MaxEpisodesPerRequest} Inhalte gleichzeitig auswählen.");
                // A film belonging to a franchise must not match the franchise's series item.
                var identityTitle = isMovie && explicitMovie != true && values.GetArrayLength() > 1 ? episode.Title : title;
                targets.Add(new VideoTarget(episode, isMovie, new LibraryMediaIdentity(identityTitle,
                    ReadReleaseYear(detail), isMovie, values.GetArrayLength() == 1 ? ReadProviderIds(detail) : new Dictionary<string, string>())));
            }
        }
        if (targets.Count == 0) throw new AniWorldApplicationException(HttpStatusCode.NotFound, "Keine verfügbaren Inhalte gefunden.");
        using var concurrency = new SemaphoreSlim(4);
        var offers = await Task.WhenAll(targets.Select(async target =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await ReadProviderOptionsAsync(userId, request.Source, target.Episode.Url, cancellationToken, refreshOffers).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is AniWorldException or AniWorldApplicationException)
            {
                return (IReadOnlyDictionary<string, IReadOnlyList<string>>)new Dictionary<string, IReadOnlyList<string>>();
            }
            finally { concurrency.Release(); }
        })).ConfigureAwait(false);
        var languages = offers.SelectMany(offer => offer.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (request.Language.Length > 0 && !languages.Contains(request.Language, StringComparer.OrdinalIgnoreCase)) languages.Add(request.Language);
        var variants = new Dictionary<string, VideoLanguagePlan>(StringComparer.OrdinalIgnoreCase);
        var localByIdentity = new Dictionary<(string, string), LibraryAvailability>();
        foreach (var language in languages)
        {
            var missing = new List<AniWorldDownloadItem>();
            HashSet<string>? common = null;
            var existingUrls = new HashSet<string>(StringComparer.Ordinal);
            var unavailableUrls = new HashSet<string>(StringComparer.Ordinal);
            var available = 0;
            for (var index = 0; index < targets.Count; index++)
            {
                var target = targets[index];
                var localKey = (JsonSerializer.Serialize(target.Identity), language);
                if (!localByIdentity.TryGetValue(localKey, out var local))
                {
                    local = target.Identity.Title.Length == 0 ? new LibraryAvailability(false, new HashSet<LibraryEpisodeKey>())
                        : _libraryAvailability.GetAvailability(target.Identity, language);
                    localByIdentity[localKey] = local;
                }
                var exists = target.IsMovie ? local.ItemExists : local.Episodes.Contains(new LibraryEpisodeKey(target.Episode.SeasonNumber!.Value, target.Episode.EpisodeNumber!.Value));
                var option = offers[index].FirstOrDefault(pair => pair.Key.Equals(language, StringComparison.OrdinalIgnoreCase));
                if (option.Value?.Count > 0) available++;
                if (exists) { existingUrls.Add(target.Episode.Url); continue; }
                if (option.Value is null || option.Value.Count == 0) { unavailableUrls.Add(target.Episode.Url); continue; }
                common = common is null ? option.Value.ToHashSet(StringComparer.OrdinalIgnoreCase) : common.Intersect(option.Value, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
                missing.Add(new AniWorldDownloadItem(target.Episode.Url)
                {
                    Language = option.Key, Provider = option.Value[0], MediaType = target.IsMovie ? "movie" : "series",
                });
            }
            var providers = common?.Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
            if (missing.Count > 0) providers.Add("Automatic");
            variants[language] = new VideoLanguagePlan(available, existingUrls.Count, unavailableUrls.Count, missing, providers)
            { ExistingUrls = existingUrls, UnavailableUrls = unavailableUrls };
        }
        var allMissing = variants.Values.SelectMany(value => value.Items).DistinctBy(item => item.Url).ToArray();
        var plan = new MissingMediaPlan(title, ReadJsonString(detail, "description", 4000), targets.All(target => target.IsMovie),
            targets.Count, allMissing, $"{allMissing.Length} fehlende Inhalte", languages,
            variants.ToDictionary(pair => pair.Key, pair => pair.Value.Providers, StringComparer.OrdinalIgnoreCase), string.Empty) { Variants = variants };
        return request.Language.Length == 0 ? plan : SelectVariant(plan, request.Language, request.Provider);
    }

    private static MissingMediaPlan SelectVariant(MissingMediaPlan plan, string language, string provider)
    {
        if (plan.Variants.Count == 0) return plan;
        if (!plan.Variants.TryGetValue(language, out var variant)) throw DownloadOptionsChanged();
        var items = variant.Items.Select(item => new AniWorldDownloadItem(item.Url)
        {
            Language = item.Language, MediaType = item.MediaType,
            Provider = provider.Length > 0 && !provider.Equals("Automatic", StringComparison.OrdinalIgnoreCase) && variant.Providers.Contains(provider, StringComparer.OrdinalIgnoreCase)
                ? provider : item.Provider,
        }).ToArray();
        var label = items.Length == 1 ? "1 fehlender Inhalt" : $"{items.Length} fehlende Inhalte";
        if (items.Length > 500) label += $" · {(items.Length + 499) / 500} Arbeitsabschnitte";
        return plan with { MissingItems = items, SelectionLabel = label, UnavailableCount = variant.UnavailableCount };
    }

    private sealed record VideoTarget(AniWorldEpisode Episode, bool IsMovie, LibraryMediaIdentity Identity);
}
