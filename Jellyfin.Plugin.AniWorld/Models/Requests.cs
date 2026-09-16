using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Plugin.AniWorld.Models;

/// <summary>Payload, mit dem ein Jellyfin-Benutzer Inhalte anfordert.</summary>
public sealed class CreateMediaRequest
{
    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    public string SeriesUrl { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(20)]
    public string MediaType { get; set; } = "series";

    [MaxLength(300)]
    public string SelectionLabel { get; set; } = string.Empty;

    [Required]
    public List<string> Episodes { get; set; } = [];

    [Required]
    [MaxLength(100)]
    public string Language { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Provider { get; set; } = string.Empty;
}

/// <summary>Payload für eine server-berechnete Anfrage mit nur fehlenden Inhalten.</summary>
public sealed class AutomaticMediaRequest
{
    [Required]
    [MaxLength(300)]
    public string Title { get; set; } = string.Empty;

    [Required]
    [MaxLength(2048)]
    public string SeriesUrl { get; set; } = string.Empty;

    [Required]
    [MaxLength(80)]
    public string Source { get; set; } = string.Empty;

    [MaxLength(20)]
    public string MediaType { get; set; } = "series";

    [MaxLength(100)]
    public string Language { get; set; } = string.Empty;

    [MaxLength(100)]
    public string Provider { get; set; } = string.Empty;
}

/// <summary>Optionaler Ablehnungsgrund durch einen Administrator.</summary>
public sealed class RejectMediaRequest
{
    [MaxLength(500)]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>Payload für einen Administrator zum Austauschen des AniWorld API-Keys.</summary>
public sealed class UpdateApiKeyRequest
{
    [Required]
    [MaxLength(512)]
    public string ApiKey { get; set; } = string.Empty;
}
