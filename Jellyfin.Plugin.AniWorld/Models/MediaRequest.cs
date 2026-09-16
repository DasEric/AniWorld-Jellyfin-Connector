using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.AniWorld.Models;

/// <summary>Persistente Benutzeranfrage und das zugehörige AniWorld-Queue-Ergebnis.</summary>
public sealed class MediaRequest
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("userId")]
    public string UserId { get; set; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("seriesUrl")]
    public string SeriesUrl { get; set; } = string.Empty;

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = "series";

    [JsonPropertyName("selectionLabel")]
    public string SelectionLabel { get; set; } = string.Empty;

    [JsonPropertyName("episodesJson")]
    public string EpisodesJson { get; set; } = "[]";

    [JsonPropertyName("language")]
    public string Language { get; set; } = string.Empty;

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = RequestStatuses.Pending;

    [JsonPropertyName("createdUtc")]
    public DateTime CreatedUtc { get; set; }

    [JsonPropertyName("decidedUtc")]
    public DateTime? DecidedUtc { get; set; }

    [JsonPropertyName("decidedBy")]
    public string? DecidedBy { get; set; }

    [JsonPropertyName("aniWorldQueueId")]
    public long? AniWorldQueueId { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("episodes")]
    public IReadOnlyList<string> Episodes
    {
        get
        {
            try
            {
                return JsonSerializer.Deserialize<List<string>>(EpisodesJson) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }

    /// <summary>Gets or sets transient synchronized progress for in-process consumers.</summary>
    [JsonIgnore]
    public int? Progress { get; set; }

    /// <summary>Gets or sets whether AniWorld reports an actively running queue item.</summary>
    [JsonIgnore]
    public bool QueueRunning { get; set; }
}

/// <summary>Bekannte Anfrage-Statuswerte.</summary>
public static class RequestStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Queued = "queued";
    public const string Completed = "completed";
    public const string Available = "available";
    public const string Partial = "partial";
    public const string Cancelled = "cancelled";
    public const string Rejected = "rejected";
    public const string Withdrawn = "withdrawn";
    public const string Failed = "failed";
}
