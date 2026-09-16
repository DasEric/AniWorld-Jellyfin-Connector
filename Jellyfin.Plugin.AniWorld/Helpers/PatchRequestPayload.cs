namespace Jellyfin.Plugin.AniWorld.Helpers;

/// <summary>Payload supplied by the File Transformation plugin.</summary>
public sealed class PatchRequestPayload
{
    public string? Contents { get; set; }
}
