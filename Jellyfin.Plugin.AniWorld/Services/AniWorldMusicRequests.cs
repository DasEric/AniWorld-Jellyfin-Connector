using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AniWorld.Models;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Music requests via the downloader's Soulseek client (Music tab). The downloader
/// searches, downloads, tags/sorts into the music library and scans Jellyfin itself;
/// the connector only offers the search, stores the chosen album and follows its transfer.
/// </summary>
public sealed partial class AniWorldRequestApplicationService
{
    internal const string MusicSourceId = "soulseek";
    internal const string MusicSourceLabel = "Soulseek";
    private const int MaxMusicFiles = 500;
    private const int MaxMusicReleases = 150;
    private const int MaxCachedMusicReleases = 5000;
    private static readonly TimeSpan MusicStatusLifetime = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MusicReleaseLifetime = TimeSpan.FromHours(2);
    private static readonly TimeSpan MusicSearchDuration = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan MusicTransferGrace = TimeSpan.FromHours(24);
    private static readonly Regex MusicSearchId = new("^[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}$", RegexOptions.Compiled);
    private static readonly Regex MusicCoverKey = new("^[0-9a-f]{40}$", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, CachedMusicRelease> _musicReleases = new(StringComparer.Ordinal);
    private MusicAvailability? _musicAvailability;

    /// <summary>Adds the Soulseek source when music is allowed here and enabled in the downloader.</summary>
    private async Task<IReadOnlyList<AniWorldSiteInfo>> WithMusicSourceAsync(IReadOnlyList<AniWorldSiteInfo> sources, CancellationToken cancellationToken)
    {
        var config = CurrentConfiguration;
        var allowlist = (config.AllowedSources ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!MediaCategories.IsAllowed("music", config)
            || (allowlist.Length > 0 && !allowlist.Contains(MusicSourceId, StringComparer.OrdinalIgnoreCase))
            || sources.Any(item => item.Id == MusicSourceId)
            || !await IsMusicAvailableAsync(cancellationToken).ConfigureAwait(false))
            return sources;
        return sources.Append(new AniWorldSiteInfo(MusicSourceId, MusicSourceLabel, ["music"], "music")).ToArray();
    }

    private async Task<bool> IsMusicAvailableAsync(CancellationToken cancellationToken)
    {
        var cached = _musicAvailability;
        if (cached is not null && cached.ValidUntilUtc > DateTime.UtcNow) return cached.Available;
        bool available;
        try
        {
            var status = await _aniWorld.GetMusicStatusAsync(cancellationToken).ConfigureAwait(false);
            available = status.ValueKind == JsonValueKind.Object
                && status.TryGetProperty("configured", out var configured) && configured.ValueKind == JsonValueKind.True;
        }
        catch (AniWorldException)
        {
            // Older downloaders have no Music tab (404); an outage simply hides the category.
            available = false;
        }

        _musicAvailability = new MusicAvailability(available, DateTime.UtcNow + MusicStatusLifetime);
        return available;
    }

    /// <summary>
    /// Runs one Soulseek search to completion (the downloader stops it after 15 seconds),
    /// keeps the releases server-side and returns them as ordinary search results.
    /// </summary>
    private async Task<JsonElement> SearchMusicAsync(string userId, string query, CancellationToken cancellationToken)
    {
        var started = await _aniWorld.StartMusicSearchAsync(query, cancellationToken).ConfigureAwait(false);
        var searchId = started.ValueKind == JsonValueKind.Object ? ReadJsonString(started, "id", 64) : string.Empty;
        if (!MusicSearchId.IsMatch(searchId))
            throw new AniWorldApplicationException(HttpStatusCode.BadGateway, "Der Downloader hat keine gültige Musiksuche gestartet.");

        JsonElement state = default;
        try
        {
            var deadline = DateTime.UtcNow + MusicSearchDuration;
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                state = await _aniWorld.GetMusicSearchAsync(searchId, cancellationToken).ConfigureAwait(false);
                var complete = state.ValueKind == JsonValueKind.Object
                    && state.TryGetProperty("complete", out var done) && done.ValueKind == JsonValueKind.True;
                if (complete || DateTime.UtcNow >= deadline) break;
            }
        }
        finally
        {
            // The releases are kept here; the downloader does not need to hold the search.
            try { await _aniWorld.DeleteMusicSearchAsync(searchId, CancellationToken.None).ConfigureAwait(false); }
            catch (AniWorldException) { /* it expires in the downloader as well */ }
        }

        var results = ReadMusicReleases(state).Select(release => ToMusicResult(GrantMusicRelease(userId, release), release)).ToArray();
        return JsonSerializer.SerializeToElement(new { results });
    }

    internal static IReadOnlyList<MusicRelease> ReadMusicReleases(JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object || !state.TryGetProperty("releases", out var releases) || releases.ValueKind != JsonValueKind.Array)
            return [];
        var output = new List<MusicRelease>();
        foreach (var item in releases.EnumerateArray().Take(MaxMusicReleases))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var username = ReadExact(item, "username", 128);
            var directory = ReadExact(item, "directory", 1024);
            if (username.Trim().Length == 0 || !item.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) continue;
            var tracks = new List<MusicTrack>();
            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;
                var filename = ReadExact(file, "filename", 1024);
                if (filename.Length == 0 || !file.TryGetProperty("size", out var sizeValue) || sizeValue.ValueKind != JsonValueKind.Number
                    || !sizeValue.TryGetInt64(out var size) || size < 0) continue;
                var name = ReadJsonString(file, "name", 300);
                tracks.Add(new MusicTrack(filename, name.Length > 0 ? name : filename.Replace('\\', '/').Split('/')[^1], size));
            }

            // The downloader accepts at most 500 files per album request.
            if (tracks.Count == 0 || tracks.Count > MaxMusicFiles) continue;
            var formats = item.TryGetProperty("formats", out var formatValues) && formatValues.ValueKind == JsonValueKind.Array
                ? formatValues.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!.Trim())
                    .Where(value => Regex.IsMatch(value, "^[A-Za-z0-9]{1,10}$")).Take(8).ToArray()
                : [];
            var cover = item.TryGetProperty("cover", out var coverValue) && coverValue.ValueKind == JsonValueKind.Object
                && coverValue.TryGetProperty("cached", out var cached) && cached.ValueKind == JsonValueKind.True
                ? ReadJsonString(coverValue, "key", 40) : string.Empty;
            var title = ReadJsonString(item, "title", 300);
            output.Add(new MusicRelease(
                username,
                directory,
                title.Length > 0 ? title : username,
                ReadJsonString(item, "parent", 300),
                tracks,
                formats,
                item.TryGetProperty("lossless", out var lossless) && lossless.ValueKind == JsonValueKind.True,
                ReadOptionalInt(item, "bitrate"),
                item.TryGetProperty("free_slot", out var slot) && slot.ValueKind == JsonValueKind.True,
                MusicCoverKey.IsMatch(cover) ? cover : string.Empty));
        }

        return output;
    }

    // Soulseek paths must stay byte-identical, so they are neither trimmed nor rewritten.
    private static string ReadExact(JsonElement item, string name, int maximum)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { } text && text.Length <= maximum && !text.Any(char.IsControl) ? text : string.Empty;

    private string GrantMusicRelease(string userId, MusicRelease release)
    {
        var now = DateTime.UtcNow;
        foreach (var expired in _musicReleases.Where(pair => pair.Value.ExpiresUtc <= now).Select(pair => pair.Key).Take(500))
            _musicReleases.TryRemove(expired, out _);
        if (_musicReleases.Count >= MaxCachedMusicReleases)
        {
            foreach (var oldest in _musicReleases.OrderBy(pair => pair.Value.ExpiresUtc).Take(_musicReleases.Count - MaxCachedMusicReleases + 1).Select(pair => pair.Key).ToArray())
                _musicReleases.TryRemove(oldest, out _);
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _musicReleases[token] = new CachedMusicRelease(userId, release, now + MusicReleaseLifetime);
        return token;
    }

    private static object ToMusicResult(string token, MusicRelease release)
    {
        // The folder above the album is usually the artist; the share owner is no useful author.
        var artist = release.Parent.Length > 0 && !string.Equals(release.Parent, release.Username, StringComparison.Ordinal) ? release.Parent : string.Empty;
        return new
        {
            id = token,
            title = release.Title,
            author = artist,
            media_kind = "music",
            media_type = "music",
            cover_key = release.CoverKey,
            track_count = release.Files.Count,
            size = release.Files.Sum(file => file.Size),
            formats = release.Formats,
            lossless = release.Lossless,
            bitrate = release.Bitrate,
            free_slot = release.FreeSlot,
            tracks = release.Files.Select(file => file.Name).ToArray(),
        };
    }

    public async Task<SubmitMediaRequestResult> SubmitMusicAsync(string userId, string username, MusicMediaRequest selection, CancellationToken cancellationToken)
    {
        if (CurrentConfiguration.MaintenanceMode) throw MaintenanceActive(CurrentConfiguration);
        if (!Allow(userId, "request", 10)) throw TooManyRequests();
        var token = selection.Token?.Trim() ?? string.Empty;
        if (!_musicReleases.TryGetValue(token, out var cached) || cached.UserId != userId || cached.ExpiresUtc <= DateTime.UtcNow)
            throw new AniWorldApplicationException(HttpStatusCode.BadRequest, "Die Musikauswahl ist abgelaufen. Bitte erneut suchen.");
        await RequireSourceAsync(userId, MusicSourceId, "music", cancellationToken).ConfigureAwait(false);
        var release = cached.Release;
        var limit = Math.Clamp(CurrentConfiguration.MaxPendingRequestsPerUser, 1, 100);
        var formats = release.Formats.Count > 0 ? " · " + string.Join("/", release.Formats) : string.Empty;
        var result = await _store.TryAddAsync(userId, username, new CreateMediaRequest
        {
            Backend = "music", Title = release.Title, Source = MusicSourceId, MediaType = "music",
            CatalogItemId = MusicReleaseKey(release), Format = string.Join("/", release.Formats),
            MusicUser = release.Username, MusicDirectory = release.Directory,
            MusicFilesJson = JsonSerializer.Serialize(release.Files),
            SelectionLabel = release.Files.Count + " Titel" + formats,
            Language = string.Empty, Provider = MusicSourceId,
        }, CurrentConfiguration.AutoApproveRequests ? RequestStatuses.Ready : RequestStatuses.Pending, limit, cancellationToken).ConfigureAwait(false);
        if (result.Duplicate is not null) return new SubmitMediaRequestResult(SubmitDisposition.Duplicate, null, result.Duplicate, limit);
        if (result.LimitReached) return new SubmitMediaRequestResult(SubmitDisposition.LimitReached, null, null, limit);
        if (result.StoreCapacityReached) return new SubmitMediaRequestResult(SubmitDisposition.StoreCapacityReached, null, null, limit);
        var stored = result.Request ?? throw new InvalidOperationException("Music request was not stored.");
        return new SubmitMediaRequestResult(SubmitDisposition.Stored, stored, null, limit);
    }

    internal static string MusicReleaseKey(MusicRelease release)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(release.Username + "\n" + release.Directory.Replace('\\', '/')))).ToLowerInvariant()[..32];

    private async Task<MediaRequest> QueueMusicRequestAsync(MediaRequest request, string decidedBy, CancellationToken cancellationToken)
    {
        if (!MediaCategories.IsAllowed("music", CurrentConfiguration))
            throw new AniWorldApplicationException(HttpStatusCode.Forbidden, "Musik-Anfragen sind in den Admin-Einstellungen deaktiviert.");
        List<MusicTrack> files;
        try { files = JsonSerializer.Deserialize<List<MusicTrack>>(request.MusicFilesJson) ?? []; }
        catch (JsonException) { files = []; }
        if (request.MusicUser.Trim().Length == 0 || files.Count is 0 or > MaxMusicFiles)
            throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Die Musikanfrage enthält keine gültige Dateiauswahl. Bitte neu suchen und anfragen.");
        await _aniWorld.QueueMusicAsync(request.MusicUser, files, cancellationToken).ConfigureAwait(false);
        await _store.MarkQueuedAsync(request.Id, null, decidedBy, cancellationToken).ConfigureAwait(false);
        return await _store.GetAsync(request.Id, CancellationToken.None).ConfigureAwait(false) ?? request;
    }

    public async Task<AniWorldImage> GetMusicCoverAsync(string userId, string key, CancellationToken cancellationToken)
    {
        if (!MusicCoverKey.IsMatch(key ?? string.Empty))
            throw new AniWorldApplicationException(HttpStatusCode.BadRequest, "Ungültiges Albumcover.");
        await RequireSourceAsync(userId, MusicSourceId, "music", cancellationToken).ConfigureAwait(false);
        return await _aniWorld.GetMusicCoverAsync(key!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Maps the downloader's Soulseek transfers onto queued music requests. Only final
    /// states are returned; running or waiting albums stay queued.
    /// </summary>
    internal static IReadOnlyDictionary<long, (string Status, string? Error)> ReadMusicStates(
        JsonElement response, IReadOnlyList<MediaRequest> requests, DateTime nowUtc)
    {
        var output = new Dictionary<long, (string Status, string? Error)>();
        var groups = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("transfers", out var transfers) && transfers.ValueKind == JsonValueKind.Array
            ? transfers.EnumerateArray().Where(group => group.ValueKind == JsonValueKind.Object).ToArray()
            : null;
        if (groups is null) return output;
        foreach (var request in requests.Where(item => item.Backend == "music"))
        {
            List<MusicTrack> files;
            try { files = JsonSerializer.Deserialize<List<MusicTrack>>(request.MusicFilesJson) ?? []; }
            catch (JsonException) { continue; }
            var wanted = files.Select(file => BaseName(file.Filename)).ToHashSet(StringComparer.Ordinal);
            var directory = request.MusicDirectory.Replace('\\', '/').TrimEnd('/');
            var candidates = groups.Where(group => ReadExact(group, "username", 128) == request.MusicUser).ToArray();
            var sameFolder = candidates.Where(group => string.Equals(ReadExact(group, "directory", 1024).Replace('\\', '/').TrimEnd('/'), directory, StringComparison.OrdinalIgnoreCase)).ToArray();
            var states = new Dictionary<string, (string State, bool Converting)>(StringComparer.Ordinal);
            foreach (var group in sameFolder.Length > 0 ? sameFolder : candidates)
            {
                if (!group.TryGetProperty("files", out var entries) || entries.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in entries.EnumerateArray().Where(entry => entry.ValueKind == JsonValueKind.Object))
                {
                    var name = ReadExact(entry, "name", 1024);
                    if (!wanted.Contains(name)) continue;
                    states[name] = (ReadJsonString(entry, "state", 20), entry.TryGetProperty("converting", out var converting) && converting.ValueKind == JsonValueKind.True);
                }
            }

            if (states.Count == 0)
            {
                // Finished transfers can be cleared in the downloader; an album that never shows
                // up there within a day is reported instead of waiting forever.
                var since = request.DecidedUtc ?? request.CreatedUtc;
                if (nowUtc - since > MusicTransferGrace)
                    output[request.Id] = (RequestStatuses.Failed, "Der Musik-Download ist in der Soulseek-Warteschlange des Downloaders nicht mehr auffindbar.");
                continue;
            }

            // Files that are still transferring, waiting or being converted keep the album open.
            if (states.Count < wanted.Count && states.Values.All(value => value.State == "completed")
                && nowUtc - (request.DecidedUtc ?? request.CreatedUtc) <= MusicTransferGrace) continue;
            if (states.Values.Any(value => value.State is "running" or "queued" || value.Converting)) continue;
            var completed = states.Values.Count(value => value.State == "completed");
            var missing = wanted.Count - completed;
            if (missing == 0) output[request.Id] = (RequestStatuses.Completed, null);
            else if (completed > 0) output[request.Id] = (RequestStatuses.Partial, $"{missing} von {wanted.Count} Titeln konnten über Soulseek nicht geladen werden.");
            else if (states.Values.All(value => value.State == "cancelled")) output[request.Id] = (RequestStatuses.Cancelled, "Der Musik-Download wurde im Downloader abgebrochen.");
            else output[request.Id] = (RequestStatuses.Failed, "Soulseek konnte das Album nicht laden. Bitte später erneut oder eine andere Quelle anfragen.");
        }

        return output;
    }

    private static string BaseName(string filename) => filename.Replace('\\', '/').Split('/')[^1];
}

public sealed record MusicTrack(
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("size")] long Size);

public sealed record MusicRelease(
    string Username,
    string Directory,
    string Title,
    string Parent,
    IReadOnlyList<MusicTrack> Files,
    IReadOnlyList<string> Formats,
    bool Lossless,
    int? Bitrate,
    bool FreeSlot,
    string CoverKey);

internal sealed record CachedMusicRelease(string UserId, MusicRelease Release, DateTime ExpiresUtc);

internal sealed record MusicAvailability(bool Available, DateTime ValidUntilUtc);
