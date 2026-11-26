using Jellyfin.Plugin.AniWorld.Models;
using Jellyfin.Plugin.AniWorld.Services;

internal static class BackgroundRequestScenarios
{
    internal static void AdmissionIsDurableWithoutRemoteCalls() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        fixture.RejectRemoteCalls = true;
        var result = await fixture.Submit("English Dub", false);
        Check(result.Disposition == SubmitDisposition.Stored && result.Request!.Status == RequestStatuses.Ready);
        Check(fixture.RemoteCalls == 0 && fixture.DownloadCalls == 0);
        var reopened = new RequestStore(fixture.Directory);
        var saved = (await reopened.GetAsync(result.Request!.Id, default))!;
        Check(saved.FullSeries && !saved.PreparationComplete && saved.Episodes.Count == 0);
        fixture.RejectRemoteCalls = false;
        await fixture.CreateApplication(reopened).ProcessReadyRequestsAsync(default);
        Check((await reopened.GetAsync(saved.Id, default))!.Status == RequestStatuses.Queued);
        Check(fixture.DownloadCalls == 1);
    });

    internal static void ApprovalGateAndDuplicateAdmission() => Run(async fixture =>
    {
        var result = await fixture.Submit("English Dub", false);
        await fixture.Application.ProcessReadyRequestsAsync(default);
        Check(fixture.RemoteCalls == 0 && fixture.DownloadCalls == 0);
        var duplicate = await fixture.Submit("English Dub", false);
        Check(duplicate.Disposition == SubmitDisposition.Duplicate && duplicate.Duplicate!.Id == result.Request!.Id);
        var approved = await fixture.Application.ApproveAsync(result.Request!.Id, "admin", default);
        Check(approved.Status == RequestStatuses.Ready && fixture.RemoteCalls == 0);
        await Task.WhenAll(fixture.Application.ProcessReadyRequestsAsync(default), fixture.Application.ProcessReadyRequestsAsync(default));
        Check(fixture.DownloadCalls == 1);
    });

    internal static void UpstreamErrorsDoNotBecomeMissingLanguages() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        fixture.FailProviders = true;
        var result = await fixture.Submit("English Dub", false);
        var processed = await fixture.Process(result.Request!.Id);
        Check(processed.Status == RequestStatuses.Ready && processed.AttemptCount == 1);
        Check(processed.NextAttemptUtc > DateTime.UtcNow && !processed.PreparationComplete);
        Check(processed.Episodes.Count == 0 && processed.Error is not null && fixture.DownloadCalls == 0);
        Check(!await fixture.Store.TryClaimAsync(processed.Id, default));
        var reopened = new RequestStore(fixture.Directory);
        Check((await reopened.GetAsync(processed.Id, default))!.NextAttemptUtc == processed.NextAttemptUtc);
    });

    internal static void InterruptedPreparationResumesAfterRestart() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        var result = await fixture.Submit("English Dub", false);
        Check(await fixture.Store.TryClaimAsync(result.Request!.Id, default));
        var reopened = new RequestStore(fixture.Directory);
        Check((await reopened.GetAsync(result.Request.Id, default))!.Status == RequestStatuses.Ready);
        await fixture.CreateApplication(reopened).ProcessReadyRequestsAsync(default);
        var processed = (await reopened.GetAsync(result.Request.Id, default))!;
        Check(processed.Status == RequestStatuses.Queued && fixture.DownloadCalls == 1);
        Check(processed.SubmissionKey == result.Request.SubmissionKey);
    });

    internal static void PreparedPayloadAndKeySurviveRestart() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        var result = await fixture.Submit("English Dub", false);
        var id = result.Request!.Id;
        Check(await fixture.Store.TryClaimAsync(id, default));
        // This persisted boundary is immediately before the upstream POST. The old
        // process may have sent it; replay must use exactly the same prepared items.
        await fixture.Store.TryUpdateProcessingPlanAsync(id, "Example", "series", "Frozen selection",
            [new AniWorldDownloadItem(MediaLifecycleScenarios.Fixture.SeriesUrl + "/episode-1")
                { Language = "English Dub", Provider = "VOE", MediaType = "series" }], default);
        var reopened = new RequestStore(fixture.Directory);
        fixture.FailProviders = true; // Replanning would now fail or change the payload.
        await fixture.CreateApplication(reopened).ProcessReadyRequestsAsync(default);
        var processed = (await reopened.GetAsync(id, default))!;
        Check(processed.Status == RequestStatuses.Queued && processed.Episodes.Count == 1);
        Check(processed.SubmissionKey == result.Request.SubmissionKey && fixture.Probed.Count == 0);
        Check(fixture.LastPayload.GetProperty("request_key").GetString() == result.Request.SubmissionKey);
    });

    internal static void MaintenanceAndRevokedSourcePreventDownload() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        var result = await fixture.Submit("English Dub", false);
        fixture.Config.MaintenanceMode = true;
        await fixture.Application.ProcessReadyRequestsAsync(default);
        Check(fixture.RemoteCalls == 0 && (await fixture.Store.GetAsync(result.Request!.Id, default))!.Status == RequestStatuses.Ready);
        fixture.Config.MaintenanceMode = false;
        fixture.Config.AllowedSources = "sto";
        Check((await fixture.Process(result.Request!.Id)).Status == RequestStatuses.Failed);
        Check(fixture.DownloadCalls == 0);
    });

    internal static void RetryBudgetAndShutdownRecovery() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        var result = await fixture.Submit("English Dub", false);
        var id = result.Request!.Id;
        // Crashes count toward the retry budget too, preventing an infinite restart loop.
        var store = fixture.Store;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Check(await store.TryClaimAsync(id, default));
            store = new RequestStore(fixture.Directory);
            Check((await store.GetAsync(id, default))!.Status == (attempt < 5 ? RequestStatuses.Ready : RequestStatuses.Failed));
        }
        Check(!await store.TryClaimAsync(id, default));
        await store.ScheduleAsync(id, "admin", default);
        Check(await store.TryClaimAsync(id, default));
        await store.RecordAttemptFailureAsync(id, "Shutdown", true, true, default);
        var recovered = (await store.GetAsync(id, default))!;
        Check(recovered.Status == RequestStatuses.Ready && recovered.AttemptCount == 0);
        Check(recovered.SubmissionKey == result.Request.SubmissionKey);
    });

    private static void Run(Func<MediaLifecycleScenarios.Fixture, Task> action)
    {
        using var fixture = new MediaLifecycleScenarios.Fixture();
        action(fixture).GetAwaiter().GetResult();
    }
    private static void Check(bool value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (!value) throw new InvalidOperationException("Background request assertion failed: " + expression);
    }
}
