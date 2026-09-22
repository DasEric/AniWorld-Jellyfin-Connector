using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>
/// Tracks AniWorld downloads independently of any open Jellyfin browser and
/// starts a visible Jellyfin task only after a whole queue item has completed.
/// </summary>
public sealed class AniWorldQueueMonitor : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly AniWorldClient _aniWorld;
    private readonly RequestStore _store;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<AniWorldQueueMonitor> _logger;
    private readonly HashSet<string> _missingTasksLogged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _nextScanAttemptUtc = new(StringComparer.Ordinal);

    public AniWorldQueueMonitor(
        AniWorldClient aniWorld,
        RequestStore store,
        ITaskManager taskManager,
        ILogger<AniWorldQueueMonitor> logger)
    {
        _aniWorld = aniWorld;
        _store = store;
        _taskManager = taskManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Allow Jellyfin's scheduled tasks and the plugin configuration to initialize.
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
        await _store.RequeueLegacyLibraryScansAsync(cancellationToken).ConfigureAwait(false);
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
            var taskType = group.Key switch
            {
                "movie" => typeof(AniWorldMovieLibraryScanTask),
                "series" => typeof(AniWorldSeriesLibraryScanTask),
                _ => null,
            };
            if (taskType is null)
            {
                _logger.LogWarning("Completed AniWorld requests have an unsupported media type {MediaType}.", group.Key);
                continue;
            }

            if (_nextScanAttemptUtc.TryGetValue(group.Key, out var nextAttemptUtc) && nextAttemptUtc > DateTime.UtcNow)
            {
                continue;
            }

            var worker = _taskManager.ScheduledTasks.FirstOrDefault(item => item.ScheduledTask.GetType() == taskType);
            if (worker is null)
            {
                if (_missingTasksLogged.Add(group.Key))
                {
                    _logger.LogWarning("The Jellyfin {MediaType} scan task is not registered; completed requests remain pending.", group.Key);
                }

                continue;
            }

            _missingTasksLogged.Remove(group.Key);
            if (worker.State != TaskState.Idle)
            {
                continue;
            }

            try
            {
                _logger.LogInformation("Starting the Jellyfin {MediaType} scan after {RequestCount} completed AniWorld requests.", group.Key, group.Count());
                await _taskManager.Execute(worker, new TaskOptions()).ConfigureAwait(false);
                if (worker.LastExecutionResult?.Status != TaskCompletionStatus.Completed)
                {
                    _nextScanAttemptUtc[group.Key] = DateTime.UtcNow.AddMinutes(5);
                    _logger.LogWarning("The Jellyfin {MediaType} scan did not complete successfully; completed requests remain pending.", group.Key);
                    continue;
                }

                _nextScanAttemptUtc.Remove(group.Key);
                await _store.MarkLibraryScansTriggeredAsync(
                    group.Select(item => item.Id).ToArray(),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Completed the Jellyfin {MediaType} scan after {RequestCount} finished AniWorld requests.",
                    group.Key,
                    group.Count());
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _nextScanAttemptUtc[group.Key] = DateTime.UtcNow.AddMinutes(5);
                _logger.LogWarning(exception, "Could not run the Jellyfin {MediaType} scan task; it will be retried.", group.Key);
            }
        }
    }
}
