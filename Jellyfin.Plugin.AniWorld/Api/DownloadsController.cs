using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Jellyfin.Plugin.AniWorld.Configuration;
using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AniWorld.Api;

[ApiController, Authorize, Route("AniWorldRequests"), Produces("application/json")]
public sealed class DownloadsController : ControllerBase
{
    private readonly DownloadManagementService _downloads;
    private readonly MediaTargetRouting _routing;
    private readonly AniWorldClient _client;
    private readonly UserRateLimiter _limiter;
    public DownloadsController(DownloadManagementService downloads, MediaTargetRouting routing, AniWorldClient client, UserRateLimiter limiter)
    { _downloads = downloads; _routing = routing; _client = client; _limiter = limiter; }

    [HttpGet("Downloads/Mine")]
    public Task<IActionResult> Mine(CancellationToken token) => Run(async () => await _downloads.ListAsync(Identity(), false, token).ConfigureAwait(false));

    [HttpPost("Downloads/{id}/Preview")]
    public Task<IActionResult> Preview([MaxLength(128)] string id, CancellationToken token)
        => Run(async () => await _downloads.PreviewAsync(Identity(), false, id, token).ConfigureAwait(false));

    [HttpPost("Downloads/Delete")]
    public Task<IActionResult> Delete([FromBody] ConfirmDelete body, CancellationToken token)
        => Run(async () => await _downloads.ConfirmAsync(Identity(), false, body.Token, token).ConfigureAwait(false));

    [HttpGet("Admin/Downloads"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> All(CancellationToken token) => Run(async () => await _downloads.ListAsync(Identity(), true, token).ConfigureAwait(false));

    [HttpPost("Admin/Downloads/{id}/Preview"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> AdminPreview([MaxLength(128)] string id, CancellationToken token)
        => Run(async () => await _downloads.PreviewAsync(Identity(), true, id, token).ConfigureAwait(false));

    [HttpPost("Admin/Downloads/Delete"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> AdminDelete([FromBody] ConfirmDelete body, CancellationToken token)
        => Run(async () => await _downloads.ConfirmAsync(Identity(), true, body.Token, token).ConfigureAwait(false));

    [HttpGet("Admin/Library"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> Library([MaxLength(120)] string query = "") => Run(() => Task.FromResult(_downloads.ListLibrary(query)));

    [HttpPost("Admin/Library/{id:guid}/Preview"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> LibraryPreview(Guid id) => Run(() => Task.FromResult(_downloads.PreviewLibrary(Identity(), id)));

    [HttpPost("Admin/Library/Delete"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> LibraryDelete([FromBody] ConfirmDelete body) => Run(() =>
    { _downloads.ConfirmLibrary(Identity(), body.Token); return Task.FromResult<object>(new { deleted = true }); });

    [HttpGet("Admin/Targets"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> Targets(CancellationToken token) => Run(async () =>
    {
        var capabilities = await _client.GetConnectorCapabilitiesAsync(token).ConfigureAwait(false);
        return new { libraries = _routing.GetLibraries(), mediaTargets = capabilities.MediaTargets,
            paths = capabilities.Paths.Select(path => new { id = path.Id, name = path.Name, path = path.Path }) };
    });

    [HttpPost("Admin/Targets/Validate"), Authorize(Policy = Policies.RequiresElevation)]
    public Task<IActionResult> Validate([FromBody] PluginConfiguration configuration, CancellationToken token) => Run(async () =>
    {
        await _routing.ValidateAsync(configuration, _client, token).ConfigureAwait(false);
        return new { movieDownloadRoot = configuration.MovieDownloadRoot, seriesDownloadRoot = configuration.SeriesDownloadRoot };
    });

    private string Identity()
        => (User.FindFirst("Jellyfin-UserId")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value)
            is { Length: > 0 and <= 200 } id ? id : throw new UnauthorizedAccessException();

    private async Task<IActionResult> Run(Func<Task<object>> action)
    {
        try
        {
            if (!_limiter.TryConsume(Identity(), "downloads", 60, TimeSpan.FromMinutes(1)))
                return StatusCode(429, new { error = "Zu viele Dateianfragen. Bitte kurz warten." });
            return Ok(await action().ConfigureAwait(false));
        }
        catch (UnauthorizedAccessException) { return Unauthorized(); }
        catch (AniWorldApplicationException error) { return StatusCode((int)error.StatusCode, new { error = error.Message }); }
        catch (AniWorldException error) { return StatusCode((int)error.StatusCode, new { error = error.Message }); }
        catch (IOException) { return Conflict(new { error = "Dateien oder Mounts sind derzeit nicht erreichbar. Bitte erneut prüfen." }); }
    }
}

public sealed class ConfirmDelete
{
    [Required, MaxLength(128)] public string Token { get; set; } = string.Empty;
}
