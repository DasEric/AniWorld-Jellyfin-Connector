using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AniWorld.Services;

public sealed partial class AniWorldClient
{
    private static readonly JsonSerializerOptions ConnectorJson = new(JsonSerializerDefaults.Web);

    public async Task SaveMediaTargetsAsync(int movie, int series, int revision, CancellationToken token)
        => _ = await SendAsync(HttpMethod.Put, "api/media-targets", new { movie, series, revision }, token).ConfigureAwait(false);

    public async Task<ConnectorCapabilities> GetConnectorCapabilitiesAsync(CancellationToken token)
    {
        JsonElement json;
        try { json = await GetMetadataAsync("api/connector/capabilities", token).ConfigureAwait(false); }
        catch (AniWorldException error) when (error.UpstreamStatusCode == HttpStatusCode.NotFound)
        { throw new AniWorldException(HttpStatusCode.ServiceUnavailable, "Für diese Funktion H0melab Downloader 2.3.0 oder neuer installieren."); }
        var value = json.Deserialize<ConnectorCapabilities>(ConnectorJson);
        if (value is null || value.Protocol != 1 || !Guid.TryParseExact(value.InstanceId, "N", out _)
            || !value.FileReceipts || !value.EntryOptions || value.VideoGroups < 1 || value.MaxEntries < 500 || value.Paths is null || value.Paths.Count == 0
            || value.Paths.Select(path => path.Id).Distinct().Count() != value.Paths.Count
            || value.Paths.Any(path => string.IsNullOrWhiteSpace(path.Path)))
            throw new AniWorldException(HttpStatusCode.BadGateway, "Bitte H0melab Downloader 2.3.0 oder neuer mit Seriengruppen und zentralen Medienzielen installieren.");
        return value;
    }

    public async Task<IReadOnlyList<DownloadArtifact>> GetArtifactsAsync(IReadOnlyList<string> keys, CancellationToken token)
    {
        var output = new List<DownloadArtifact>();
        foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(20))
        {
            var response = await SendAsync(HttpMethod.Post, "api/connector/artifacts/query", new { request_keys = batch }, token).ConfigureAwait(false);
            if (!response.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                throw new AniWorldException(HttpStatusCode.BadGateway, "Ungültige Dateinachweise vom Downloader.");
            var rows = items.Deserialize<List<DownloadArtifact>>(ConnectorJson) ?? [];
            if (rows.Any(row => !batch.Contains(row.RequestKey, StringComparer.Ordinal) || !Guid.TryParseExact(row.Id, "N", out _)))
                throw new AniWorldException(HttpStatusCode.BadGateway, "Die Dateinachweise passen nicht zu den Anfragen.");
            output.AddRange(rows);
        }
        return output;
    }

    public async Task<JsonElement> DeleteArtifactsAsync(IReadOnlyList<string> ids, CancellationToken token)
    {
        var results = new List<JsonElement>();
        foreach (var batch in ids.Chunk(500))
        {
            var response = await SendAsync(HttpMethod.Post, "api/connector/artifacts/delete", new { ids = batch }, token).ConfigureAwait(false);
            if (!response.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() != batch.Length || items.EnumerateArray().Any(item => !item.TryGetProperty("id", out var id) || !batch.Contains(id.GetString(), StringComparer.Ordinal)))
                throw new AniWorldException(HttpStatusCode.BadGateway, "Der Downloader hat die Dateilöschung nicht vollständig bestätigt. Bitte den Bestand aktualisieren.");
            results.AddRange(items.EnumerateArray().Select(item => item.Clone()));
        }
        return JsonSerializer.SerializeToElement(new { items = results });
    }

    public async Task VerifyMountAsync(int pathId, string jellyfinRoot, CancellationToken token)
    {
        var probe = await SendAsync(HttpMethod.Post, "api/connector/path-probe", new { path_id = pathId }, token).ConfigureAwait(false);
        var id = probe.GetProperty("probe_id").GetString() ?? string.Empty;
        if (!Guid.TryParseExact(id, "N", out _)) throw new AniWorldException(HttpStatusCode.BadGateway, "Ungültige Pfadprüfung.");
        try
        {
            var path = Path.Combine(jellyfinRoot, ".connector-probe-" + id);
            if (!File.Exists(path) || await File.ReadAllTextAsync(path, token).ConfigureAwait(false) != probe.GetProperty("nonce").GetString())
                throw new AniWorldException(HttpStatusCode.Conflict, "Downloader-Ziel und Jellyfin-Pfad zeigen nicht auf denselben Ordner.");
        }
        finally
        {
            await SendAsync(HttpMethod.Post, "api/connector/path-probe", new { path_id = pathId, probe_id = id, cleanup = true }, CancellationToken.None).ConfigureAwait(false);
        }
    }
}

public sealed record ConnectorPath(int? Id, string Name, string Path);
public sealed record ConnectorCapabilities(int Protocol, [property: JsonPropertyName("instance_id")] string InstanceId,
    [property: JsonPropertyName("file_receipts")] bool FileReceipts, [property: JsonPropertyName("entry_options")] bool EntryOptions,
    IReadOnlyList<ConnectorPath> Paths)
{
    [JsonPropertyName("video_groups")] public int VideoGroups { get; init; }
    [JsonPropertyName("max_entries")] public int MaxEntries { get; init; }
    [JsonPropertyName("media_targets")] public MediaDestinationSettings? MediaTargets { get; init; }
}
public sealed record MediaDestinationSettings([property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("revision")] int Revision, [property: JsonPropertyName("movie")] int? Movie,
    [property: JsonPropertyName("series")] int? Series);
public sealed record DownloadArtifact(string Id, [property: JsonPropertyName("request_key")] string RequestKey,
    string Name, [property: JsonPropertyName("media_type")] string MediaType, string Language, long Size, string State,
    JsonElement Streams)
{
    [JsonIgnore]
    public string Verification
    {
        get
        {
            if (Streams.ValueKind != JsonValueKind.Array) return "unknown";
            var expected = Language switch { "German Dub" => "deu", "English Dub" => "eng", "German Sub" or "English Sub" or "Japanese" => "jpn", _ => "" };
            var audio = Languages("audio").Where(code => code.Length > 0).ToArray();
            if (expected.Length == 0 || audio.Length == 0) return "unknown";
            if (!audio.Contains(expected, StringComparer.Ordinal)) return "mismatch";
            var subtitle = Language switch { "German Sub" => "deu", "English Sub" => "eng", _ => "" };
            return subtitle.Length == 0 || Languages("subtitle").Concat(Languages("video")).Contains(subtitle, StringComparer.Ordinal) ? "verified" : "unknown";
        }
    }
    private IEnumerable<string> Languages(string type)
        => Streams.EnumerateArray().Where(stream => stream.ValueKind == JsonValueKind.Object
            && stream.TryGetProperty("codec_type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == type)
            .Select(stream => stream.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object
                && tags.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String
                ? MediaLanguage.Normalize(language.GetString()) : string.Empty);
}
