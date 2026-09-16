using MediaBrowser.Model.Plugins;
using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace Jellyfin.Plugin.AniWorld.Configuration;

/// <summary>Konfiguration für das AniWorld Requests Plugin.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the AniWorld base URL.</summary>
    public string AniWorldUrl { get; set; } = "http://127.0.0.1:8080";

    /// <summary>
    /// Gets or sets a legacy plaintext API key only long enough to migrate it
    /// into the encrypted secret store. It is never exposed through JSON.
    /// </summary>
    [JsonIgnore]
    [Newtonsoft.Json.JsonIgnore]
    [XmlElement("AniWorldApiKey")]
    public string AniWorldApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets whether requests are queued without admin approval.</summary>
    public bool AutoApproveRequests { get; set; }

    /// <summary>Gets or sets whether the sidebar entry is injected for every user.</summary>
    public bool EnableAllUsers { get; set; } = true;

    /// <summary>Gets or sets whether new requests are temporarily blocked.</summary>
    public bool MaintenanceMode { get; set; }

    /// <summary>Gets or sets the maintenance message displayed to users.</summary>
    public string MaintenanceMessage { get; set; } = "Anfragen sind derzeit vorübergehend deaktiviert.";

    /// <summary>Gets or sets the maximum number of open requests per user.</summary>
    public int MaxPendingRequestsPerUser { get; set; } = 10;

    /// <summary>Gets or sets an optional comma-separated site allowlist (e.g. aniworld,sto,kinox).</summary>
    public string AllowedSources { get; set; } = string.Empty;

    /// <summary>Gets or sets the initial language in the request dialog.</summary>
    public string DefaultLanguage { get; set; } = "German Dub";

    /// <summary>Gets or sets the initial provider in the request dialog.</summary>
    public string DefaultProvider { get; set; } = "VOE";

    /// <summary>Gets or sets how many AniWorld sites an all-source search may fan out to.</summary>
    public int MaxSearchSources { get; set; } = 8;
}
