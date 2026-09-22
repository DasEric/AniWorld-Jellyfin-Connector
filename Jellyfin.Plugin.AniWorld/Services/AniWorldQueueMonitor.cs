using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Tracks AniWorld downloads independently of any open Jellyfin browser and
/// schedules a scan only after a whole queue item has completed successfully.
/// </summary>
public sealed class AniWorldQueueMonitor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly AniWorldClient _aniWorld;
    private readonly RequestStore _store;
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<AniWorldQueueMonitor> _logger;
    private readonly HashSet<string> _missingLibrariesLogged = new(StringComparer.Ordinal);

    public AniWorldQueueMonitor(
        AniWorldClient aniWorld,
        RequestStore store,
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<AniWorldQueueMonitor> logger)
    {
        _aniWorld = aniWorld;
        _store = store;
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Allow Jellyfin's libraries and the plugin's configuration to initialize.
            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(PollInterval);
            do
            {
                try
                {
                    await PollOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "AniWorld queue monitoring failed; it will be retried.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal server shutdown.
        }
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var queued = await _store.ListQueuedAsync(cancellationToken).ConfigureAwait(false);
        if (queued.Count > 0)
        {
            try
            {
                var queueIds = queued.Select(item => item.AniWorldQueueId!.Value).Distinct().ToArray();
                var upstream = await _aniWorld.GetQueueAsync(cancellationToken).ConfigureAwait(false);
                var states = AniWorldRequestApplicationService.ReadProgress(upstream, queueIds);
                await _store.SyncQueueStatesForAllAsync(
                    states.ToDictionary(item => item.QueueId, item => item.Status),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (AniWorldException exception)
            {
                _logger.LogWarning(exception, "Could not read the AniWorld queue; completion will be checked again.");
            }
        }

        var pending = await _store.ListPendingLibraryScansAsync(cancellationToken).ConfigureAwait(false);
        foreach (var group in pending.GroupBy(item => item.MediaType?.Trim().ToLowerInvariant() ?? string.Empty))
        {
            if (group.Key is not ("movie" or "series"))
            {
                _logger.LogWarning("Completed AniWorld requests have an unsupported media type {MediaType}.", group.Key);
                continue;
            }

            try
            {
                var scanned = await ScanLibrariesAsync(group.Key, cancellationToken).ConfigureAwait(false);
                if (scanned == 0)
                {
                    if (_missingLibrariesLogged.Add(group.Key))
                    {
                        _logger.LogWarning("No Jellyfin {MediaType} library exists; the scan remains pending.", group.Key);
                    }

                    continue;
                }

                _missingLibrariesLogged.Remove(group.Key);
                await _store.MarkLibraryScansTriggeredAsync(
                    group.Select(item => item.Id).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Scanned {LibraryCount} Jellyfin {MediaType} libraries after {RequestCount} completed AniWorld requests.",
                    scanned,
                    group.Key,
                    group.Count());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not scan the Jellyfin {MediaType} libraries; it will be retried.", group.Key);
            }
        }
    }

    private async Task<int> ScanLibrariesAsync(string mediaType, CancellationToken cancellationToken)
    {
        var collectionType = mediaType == "movie" ? "movies" : "tvshows";
        var folders = _libraryManager.GetVirtualFolders()
            .Where(folder => string.Equals(folder.CollectionType.ToString(), collectionType, StringComparison.OrdinalIgnoreCase))
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();

        var scanned = 0;
        _libraryManager.ClearIgnoreRuleCache();
        foreach (var itemId in folders)
        {
            if (_libraryManager.GetItemById(itemId) is not Folder library)
            {
                continue;
            }

            await library.ValidateChildren(
                new Progress<double>(),
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                },
                recursive: true,
                allowRemoveRoot: false,
                cancellationToken).ConfigureAwait(false);
            scanned++;
        }

        return scanned;
    }
}
