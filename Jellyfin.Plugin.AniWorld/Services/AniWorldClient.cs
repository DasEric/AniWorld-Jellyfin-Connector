using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Models;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>Server-side client für den AniWorld-Downloader.</summary>
public sealed class AniWorldClient
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private const int MaxImageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> AllowedImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/jpeg",
        "image/png",
        "image/webp",
        "image/gif",
        "image/avif",
    };
    private readonly HttpClient _httpClient;
    private readonly SecretStore _secrets;
    private readonly Func<Configuration.PluginConfiguration?> _configuration;
    private readonly SemaphoreSlim _metadataLock = new(1, 1);
    private readonly Dictionary<string, (string Identity, DateTime Expires, JsonElement Data)> _metadata = new(StringComparer.Ordinal);

    public AniWorldClient(HttpClient httpClient, SecretStore secrets)
        : this(httpClient, secrets, () => Plugin.Instance?.Configuration)
    {
    }

    internal AniWorldClient(
        HttpClient httpClient,
        SecretStore secrets,
        Func<Configuration.PluginConfiguration?> configuration)
    {
        _httpClient = httpClient;
        _secrets = secrets;
        _configuration = configuration;
    }

    /// <summary>Prüft ob AniWorld erreichbar ist.</summary>
    public Task<JsonElement> GetHealthAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/queue/counts", null, cancellationToken);

    /// <summary>Lädt Identität, Version und Berechtigungsumfang des API-Keys.</summary>
    public Task<JsonElement> GetIdentityAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/ping", null, cancellationToken);

    /// <summary>Lädt die Einstellungen von AniWorld (inkl. aktiver Provider).</summary>
    public Task<JsonElement> GetSettingsAsync(CancellationToken cancellationToken)
        => GetMetadataAsync("api/settings", cancellationToken);

    public Task<JsonElement> GetSourcesAsync(CancellationToken cancellationToken)
        => GetMetadataAsync("api/sources", cancellationToken);

    private async Task<JsonElement> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        var identity = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes((_configuration()?.AniWorldUrl ?? string.Empty) + "\n" + _secrets.GetApiKey())));
        await _metadataLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_metadata.TryGetValue(path, out var cached) && cached.Identity == identity && cached.Expires > DateTime.UtcNow)
                return cached.Data;
            var data = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
            _metadata[path] = (identity, DateTime.UtcNow.AddSeconds(5), data);
            return data;
        }
        finally { _metadataLock.Release(); }
    }

    /// <summary>Lädt die benutzerdefinierten Pfade samt Standardquellen.</summary>
    public Task<JsonElement> GetCustomPathsAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/custom-paths", null, cancellationToken);

    /// <summary>Gibt einen sanitizierten Verbindungsstatus zurück.</summary>
    public async Task<AniWorldConnectionStatus> CheckHealthAsync(CancellationToken cancellationToken)
    {
        var config = _configuration();
        var configured = config is not null
            && IsValidBaseUrl(config.AniWorldUrl);
        if (!configured)
        {
            return new AniWorldConnectionStatus(false, false, true, false, string.Empty, string.Empty);
        }

        try
        {
            var identity = await GetIdentityAsync(cancellationToken).ConfigureAwait(false);
            var scope = identity.TryGetProperty("scope", out var scopeValue)
                && scopeValue.ValueKind == JsonValueKind.String
                ? scopeValue.GetString()?.Trim().ToLowerInvariant() ?? string.Empty
                : string.Empty;
            var version = identity.TryGetProperty("version", out var versionValue)
                && versionValue.ValueKind == JsonValueKind.String
                ? versionValue.GetString()?.Trim() ?? string.Empty
                : string.Empty;
            var canWrite = scope is "write" or "admin";
            return new AniWorldConnectionStatus(true, true, true, canWrite, scope, version);
        }
        catch (AniWorldException exception)
        {
            var authenticationFailed = exception.UpstreamStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;
            return new AniWorldConnectionStatus(false, true, !authenticationFailed, false, string.Empty, string.Empty);
        }
    }

    /// <summary>Sucht nach einem Suchbegriff auf einer bestimmten Site.</summary>
    public Task<JsonElement> SearchAsync(string keyword, string site, CancellationToken cancellationToken)
        => SendAsync(
            HttpMethod.Post,
            "api/search",
            new { keyword, site },
            cancellationToken,
            SearchTimeout);

    public Task<JsonElement> SearchCatalogAsync(string query, string source, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, $"api/catalog/search?source={Uri.EscapeDataString(source)}&q={Uri.EscapeDataString(query)}", null, cancellationToken, SearchTimeout);

    public Task<JsonElement> GetCatalogDetailsAsync(string source, string id, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, $"api/catalog/details?source={Uri.EscapeDataString(source)}&id={Uri.EscapeDataString(id)}", null, cancellationToken);

    public Task<JsonElement> BrowseAsync(string path, string source, bool catalog, CancellationToken cancellationToken)
    {
        if (catalog)
            return SendAsync(HttpMethod.Get, $"api/catalog/browse?source={Uri.EscapeDataString(source)}", null, cancellationToken, SearchTimeout);
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, "^api/[a-z0-9-]+$"))
            throw new AniWorldException(HttpStatusCode.BadRequest, "Ungültiger Katalogpfad.");
        return SendAsync(HttpMethod.Get, path, null, cancellationToken, SearchTimeout);
    }

    public async Task<AniWorldQueueResult> QueueCatalogAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        var response = await SendAsync(HttpMethod.Post, "api/catalog/download", new
        {
            source = request.Source, id = request.CatalogItemId, asset_id = request.CatalogAssetId,
            request_key = request.SubmissionKey,
        }, cancellationToken).ConfigureAwait(false);
        if (response.TryGetProperty("queue_id", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0)
            return new AniWorldQueueResult(id, 1);
        throw new AniWorldException(HttpStatusCode.BadGateway, "Der Downloader hat keine gültige Katalog-Warteschlangen-ID geliefert.");
    }

    public Task<AniWorldImage> GetCatalogCoverAsync(string token, CancellationToken cancellationToken)
        => GetImageFromPathAsync("api/catalog/cover?token=" + Uri.EscapeDataString(token), cancellationToken);

    /// <summary>Lädt Seriendetails (Titel, Poster, Beschreibung, Jahr).</summary>
    public Task<JsonElement> GetSeriesAsync(string url, CancellationToken cancellationToken)
        => GetWithUrlAsync("api/series", url, cancellationToken);

    /// <summary>Lädt die Staffelliste einer Serie.</summary>
    public Task<JsonElement> GetSeasonsAsync(string url, CancellationToken cancellationToken)
        => GetWithUrlAsync("api/seasons", url, cancellationToken);

    /// <summary>Lädt die Episodenliste einer Staffel.</summary>
    public Task<JsonElement> GetEpisodesAsync(string url, CancellationToken cancellationToken)
        => GetEpisodesAsync(url, null, cancellationToken);

    /// <summary>Lädt eine Staffel mit dem von einigen Quellen benötigten Serienkontext.</summary>
    public Task<JsonElement> GetEpisodesAsync(
        string url,
        string? seriesUrl,
        CancellationToken cancellationToken)
    {
        var path = $"api/episodes?url={Uri.EscapeDataString(url)}";
        if (!string.IsNullOrWhiteSpace(seriesUrl))
        {
            path += $"&series_url={Uri.EscapeDataString(seriesUrl)}";
        }

        return SendAsync(HttpMethod.Get, path, null, cancellationToken);
    }

    /// <summary>Lädt die verfügbaren Provider/Hoster für eine Episode.</summary>
    public Task<JsonElement> GetProvidersAsync(string url, CancellationToken cancellationToken)
        => GetWithUrlAsync("api/providers", url, cancellationToken);

    /// <summary>
    /// Lädt den aktuellen Queue-Status. Da AniWorld keinen gefilterten
    /// Progress-Endpunkt hat, wird die gesamte Queue geladen und client-seitig gefiltert.
    /// </summary>
    public Task<JsonElement> GetQueueAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/queue", null, cancellationToken);

    /// <summary>Lädt Discover-Inhalte (neue Anime).</summary>
    public Task<JsonElement> GetNewAnimesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/new-animes", null, cancellationToken);

    /// <summary>Lädt Discover-Inhalte (beliebte Anime).</summary>
    public Task<JsonElement> GetPopularAnimesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/popular-animes", null, cancellationToken);

    /// <summary>Lädt Discover-Inhalte (beliebte Filme, oft MegaKino).</summary>
    public Task<JsonElement> GetPopularMoviesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/popular-movies", null, cancellationToken);

    public Task<JsonElement> GetNewSeriesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/new-series", null, cancellationToken);

    public Task<JsonElement> GetPopularSeriesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/popular-series", null, cancellationToken);

    public Task<JsonElement> GetKinoxMoviesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/kinox-movies", null, cancellationToken);

    public Task<JsonElement> GetFilmpalastMoviesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/filmpalast-movies", null, cancellationToken);

    public Task<JsonElement> GetCinebyMoviesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/cineby-movies", null, cancellationToken);

    public Task<JsonElement> GetBurningSeriesAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/burningseries-series", null, cancellationToken);

    public Task<JsonElement> GetHtvTrendingAsync(CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, "api/htv-trending", null, cancellationToken);

    /// <summary>Lädt ein Bild über den AniWorld-Bild-Proxy.</summary>
    public async Task<AniWorldImage> GetImageAsync(string url, CancellationToken cancellationToken)
    {
        var encodedUrl = Uri.EscapeDataString(url);
        return await GetImageFromPathAsync(
            "api/proxy-image?url=" + encodedUrl,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<AniWorldImage> GetImageFromPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(30));
        var requestToken = timeoutSource.Token;
        var config = _configuration()
            ?? throw new AniWorldException(HttpStatusCode.ServiceUnavailable, "Plugin-Konfiguration ist nicht verfügbar.");

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(config.AniWorldUrl, path));
        AttachApiKey(request);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                requestToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw SafeUpstreamError(response.StatusCode, HttpMethod.Get.Method, path);
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!AllowedImageTypes.Contains(mediaType)
                || response.Content.Headers.ContentLength > MaxImageBytes)
            {
                throw new AniWorldException(HttpStatusCode.BadGateway, "AniWorld hat keine gültige Bildantwort geliefert.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > MaxImageBytes)
                {
                    throw new AniWorldException(HttpStatusCode.BadGateway, "AniWorld hat eine unerwartet große Bildantwort geliefert.");
                }

                buffer.Write(chunk, 0, read);
            }

            return new AniWorldImage(buffer.ToArray(), mediaType);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AniWorldException(HttpStatusCode.GatewayTimeout, "AniWorld hat beim Laden des Bildes nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            throw new AniWorldException(HttpStatusCode.BadGateway, "Das Bild konnte nicht von AniWorld geladen werden.");
        }
    }

    /// <summary>Reiht einen Download in die AniWorld-Queue ein.</summary>
    public async Task<AniWorldQueueResult> QueueAsync(MediaRequest request, CancellationToken cancellationToken)
    {
        // AniWorld wählt den Standardpfad einer Quelle nur in seiner Weboberfläche
        // vor. Die API benötigt die custom_path_id ausdrücklich im Downloadaufruf.
        var customPaths = await GetCustomPathsAsync(cancellationToken).ConfigureAwait(false);
        var customPathId = SelectDefaultPathId(customPaths, request.Source);
        var response = await SendAsync(
            HttpMethod.Post,
            "api/download",
            CreateDownloadPayload(request, customPathId),
            cancellationToken).ConfigureAwait(false);

        long? parsedQueueId = null;
        if (response.TryGetProperty("queue_id", out var queueId))
        {
            if (queueId.ValueKind == JsonValueKind.Number
                && queueId.TryGetInt64(out var numeric)
                && numeric > 0)
            {
                parsedQueueId = numeric;
            }

            if (!parsedQueueId.HasValue
                && queueId.ValueKind == JsonValueKind.String
                && long.TryParse(queueId.GetString(), out numeric)
                && numeric > 0)
            {
                parsedQueueId = numeric;
            }
        }

        if (parsedQueueId.HasValue)
        {
            // AniWorld liefert kein accepted_episode_count – wird als null behandelt.
            int? acceptedEpisodeCount = null;
            if (response.TryGetProperty("accepted_episode_count", out var count)
                && count.ValueKind == JsonValueKind.Number
                && count.TryGetInt32(out var numericCount)
                && numericCount is >= 0 and <= 500)
            {
                acceptedEpisodeCount = numericCount;
            }

            return new AniWorldQueueResult(parsedQueueId.Value, acceptedEpisodeCount);
        }

        throw new AniWorldException(
            HttpStatusCode.BadGateway,
            "AniWorld hat keine gültige Warteschlangen-ID zurückgegeben.");
    }

    internal static int? SelectDefaultPathId(JsonElement response, string source)
    {
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("paths", out var paths)
            || paths.ValueKind != JsonValueKind.Array)
        {
            throw InvalidCustomPaths();
        }

        foreach (var path in paths.EnumerateArray())
        {
            if (path.ValueKind != JsonValueKind.Object
                || !path.TryGetProperty("default_sites", out var defaultSites)
                || defaultSites.ValueKind != JsonValueKind.String)
            {
                throw InvalidCustomPaths();
            }

            var isDefault = (defaultSites.GetString() ?? string.Empty)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Contains(source, StringComparer.OrdinalIgnoreCase);
            if (!isDefault)
            {
                continue;
            }

            if (!path.TryGetProperty("id", out var id)
                || id.ValueKind != JsonValueKind.Number
                || !id.TryGetInt32(out var pathId)
                || pathId <= 0
                || !path.TryGetProperty("path", out var directory)
                || directory.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(directory.GetString()))
            {
                throw InvalidCustomPaths();
            }

            // Die AniWorld-Oberfläche verwendet ebenfalls den ersten Treffer.
            return pathId;
        }

        return null;
    }

    internal static object CreateDownloadPayload(MediaRequest request, int? customPathId)
        => new
        {
            episodes = request.Episodes,
            language = request.Language,
            provider = request.Provider,
            title = request.Title,
            series_url = request.SeriesUrl,
            custom_path_id = customPathId,
            request_key = request.SubmissionKey,
        };

    private static AniWorldException InvalidCustomPaths()
        => new(
            HttpStatusCode.BadGateway,
            "AniWorld hat keine gültigen Downloadpfade geliefert. Es wurde nichts eingereiht, damit kein falscher Speicherort verwendet wird.");

    private Task<JsonElement> GetWithUrlAsync(string path, string url, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Get, $"{path}?url={Uri.EscapeDataString(url)}", null, cancellationToken);

    private async Task<JsonElement> SendAsync(
        HttpMethod method,
        string relativePath,
        object? body,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(90));
        var requestToken = timeoutSource.Token;
        var config = _configuration()
            ?? throw new AniWorldException(HttpStatusCode.ServiceUnavailable, "Plugin-Konfiguration ist nicht verfügbar.");

        var requestUri = BuildUri(config.AniWorldUrl, relativePath);
        using var request = new HttpRequestMessage(method, requestUri);
        AttachApiKey(request);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, requestToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AniWorldException(HttpStatusCode.GatewayTimeout, "AniWorld hat nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            throw new AniWorldException(HttpStatusCode.BadGateway, "AniWorld ist vom Jellyfin-Server aus nicht erreichbar.");
        }

        try
        {
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw SafeUpstreamError(response.StatusCode, method.Method, relativePath);
                }

                if (response.Content.Headers.ContentLength > MaxResponseBytes)
                {
                    throw new AniWorldException(HttpStatusCode.BadGateway, "AniWorld hat eine unerwartet große Antwort geliefert.");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(requestToken).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                while (true)
                {
                    var read = await stream.ReadAsync(chunk, requestToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > MaxResponseBytes)
                    {
                        throw new AniWorldException(HttpStatusCode.BadGateway, "AniWorld hat eine unerwartet große Antwort geliefert.");
                    }

                    buffer.Write(chunk, 0, read);
                }

                try
                {
                    using var document = buffer.Length == 0
                        ? JsonDocument.Parse("{}")
                        : JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
                    return document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    throw new AniWorldException(HttpStatusCode.BadGateway, "AniWorld hat keine gültige JSON-Antwort geliefert.");
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AniWorldException(HttpStatusCode.GatewayTimeout, "AniWorld hat nicht rechtzeitig geantwortet.");
        }
        catch (HttpRequestException)
        {
            throw new AniWorldException(HttpStatusCode.BadGateway, "Die Antwort von AniWorld wurde unterbrochen.");
        }
    }

    private void AttachApiKey(HttpRequestMessage request)
    {
        var apiKey = _secrets.GetApiKey();
        if (apiKey is not null)
        {
            request.Headers.Add("X-API-Key", apiKey);
        }
    }

    private static Uri BuildUri(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)
            || baseUrl.Length > 2048
            || !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var root)
            || (root.Scheme != Uri.UriSchemeHttp && root.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(root.Host)
            || !string.IsNullOrEmpty(root.UserInfo)
            || !string.IsNullOrEmpty(root.Query)
            || !string.IsNullOrEmpty(root.Fragment))
        {
            throw new AniWorldException(HttpStatusCode.ServiceUnavailable, "Die konfigurierte AniWorld-URL ist ungültig.");
        }

        return new Uri(root.AbsoluteUri.TrimEnd('/') + "/" + relativePath.TrimStart('/'), UriKind.Absolute);
    }

    internal static bool IsValidBaseUrl(string baseUrl)
    {
        try
        {
            _ = BuildUri(baseUrl, "api/queue/counts");
            return true;
        }
        catch (AniWorldException)
        {
            return false;
        }
    }

    private static AniWorldException SafeUpstreamError(HttpStatusCode statusCode, string method, string relativePath)
    {
        // Only the fixed API path is diagnostic context. Never expose query
        // strings, credentials, response bodies or deployment URLs to clients.
        var endpoint = relativePath.Split('?', 2)[0].TrimStart('/');
        if (!System.Text.RegularExpressions.Regex.IsMatch(endpoint, "^api/[a-zA-Z0-9/-]+$")) endpoint = "Downloader-API";
        AniWorldException error = statusCode switch
        {
            HttpStatusCode.BadRequest => new(statusCode, "AniWorld hat die Anfrage abgelehnt."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
                HttpStatusCode.BadGateway,
                "AniWorld-Authentifizierung ist ungültig. Bitte API-Key prüfen.",
                statusCode),
            HttpStatusCode.NotFound => new(statusCode, "Der angeforderte Inhalt wurde in AniWorld nicht gefunden."),
            HttpStatusCode.TooManyRequests => new(statusCode, "AniWorld begrenzt derzeit weitere Anfragen. Bitte später erneut versuchen."),
            HttpStatusCode.ServiceUnavailable => new(statusCode, "AniWorld ist derzeit nicht verfügbar."),
            _ => new(HttpStatusCode.BadGateway, $"H0melab Downloader meldet HTTP {(int)statusCode} bei {method} /{endpoint}. Details stehen im Downloader-Protokoll."),
        };
        // Preserve 404/500/etc. as well as auth errors: capability fallback and
        // per-source diagnostics depend on the actual upstream status.
        return new AniWorldException(error.StatusCode, error.Message, statusCode);
    }
}

public sealed record AniWorldImage(byte[] Data, string MediaType);

/// <summary>Verifiziertes Queue-Ergebnis vom AniWorld-Download-Endpunkt.</summary>
public sealed record AniWorldQueueResult(long QueueId, int? AcceptedEpisodeCount);

/// <summary>Sanitizierter Verbindungsstatus für interne Nutzung.</summary>
public sealed record AniWorldConnectionStatus(
    bool Healthy,
    bool Configured,
    bool ApiKeyValid,
    bool CanWrite,
    string Scope,
    string Version);

/// <summary>Fehler beim Kommunizieren mit AniWorld.</summary>
public sealed class AniWorldException : Exception
{
    public AniWorldException(
        HttpStatusCode statusCode,
        string message,
        HttpStatusCode? upstreamStatusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
        UpstreamStatusCode = upstreamStatusCode;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>Gets an upstream status only when it is safe and required for internal classification.</summary>
    public HttpStatusCode? UpstreamStatusCode { get; }
}
