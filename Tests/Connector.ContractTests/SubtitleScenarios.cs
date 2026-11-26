using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Models;
using Jellyfin.Plugin.AniWorld.Services;
using MediaBrowser.Model.Entities;

internal static class SubtitleScenarios
{
    internal static void DefaultsAndValidation()
    {
        Check(JsonSerializer.Deserialize<MediaRequest>("{}")!.SubtitleLanguage == "none");
        using var fixture = new MediaLifecycleScenarios.Fixture();
        var request = fixture.Selection("English Dub");
        Check(AniWorldRequestApplicationService.ValidateAutomaticRequest(request, true) is null);
        request.SubtitleLanguage = "deu";
        Check(AniWorldRequestApplicationService.ValidateAutomaticRequest(request, true) is not null);
        request.Source = "sto";
        Check(AniWorldRequestApplicationService.ValidateAutomaticRequest(request, true) is null);
        request.SubtitleLanguage = "eng";
        Check(AniWorldRequestApplicationService.ValidateAutomaticRequest(request, true) is not null);
        request.SubtitleLanguage = "deu"; request.MediaType = "movie";
        Check(AniWorldRequestApplicationService.ValidateAutomaticRequest(request, true) is not null);
    }

    internal static void PersistAndSendSelectedSubtitles() => Run(async fixture =>
    {
        fixture.Config.AutoApproveRequests = true;
        var selection = fixture.Selection("English Dub");
        selection.Source = "sto";
        var none = await fixture.Application.SubmitAutomaticAsync("alice", "Alice", selection, false, default);
        selection.SubtitleLanguage = "deu";
        var german = await fixture.Application.SubmitAutomaticAsync("alice", "Alice", selection, false, default);
        Check(none.Request!.Id != german.Request!.Id);
        Check((await fixture.Application.SubmitAutomaticAsync("alice", "Alice", selection, false, default)).Disposition == SubmitDisposition.Duplicate);
        var reopened = new RequestStore(fixture.Directory);
        Check((await reopened.GetAsync(german.Request.Id, default))!.SubtitleLanguage == "deu");
        // Process both independently, including the explicit none default on the wire.
        await fixture.Application.ProcessReadyRequestsAsync(default);
        var preparedNone = (await fixture.Store.GetAsync(none.Request.Id, default))!;
        var preparedGerman = (await fixture.Store.GetAsync(german.Request.Id, default))!;
        Check(preparedNone.Status == RequestStatuses.Queued && preparedGerman.Status == RequestStatuses.Queued);
        Check(JsonSerializer.SerializeToElement(AniWorldClient.CreateDownloadPayload(preparedNone, null)).GetProperty("subtitle_language").GetString() == "none");
        Check(JsonSerializer.SerializeToElement(AniWorldClient.CreateDownloadPayload(preparedGerman, null)).GetProperty("subtitle_language").GetString() == "deu");
        var intents = await new DownloadOwnershipStore(fixture.Directory).ListAsync("alice", false, default);
        Check(intents.Single(item => item.RequestKey == preparedGerman.SubmissionKey).SubtitleLanguage == "deu");
    });

    internal static void RequestedSoftSubtitlesAreNotAudioOrBurnedInVideo()
    {
        MediaStream[] audio = [new() { Type = MediaStreamType.Audio, Language = "deu" }];
        Check(MediaLanguage.Matches(audio, "German Dub"));
        Check(!MediaLanguage.Matches(audio, "German Dub", "deu"));
        MediaStream[] burnedIn = [..audio, new() { Type = MediaStreamType.Video, Language = "deu" }];
        Check(!MediaLanguage.Matches(burnedIn, "German Dub", "deu"));
        MediaStream[] soft = [..audio, new() { Type = MediaStreamType.Subtitle, Language = "ger" }];
        Check(MediaLanguage.Matches(soft, "German Dub", "deu"));
        Check(!MediaLanguage.Matches(soft, "English Dub", "deu"));
        var receipt = new DownloadArtifact("id", "key", "Episode", "series", "German Dub", 1, "owned",
            JsonSerializer.SerializeToElement(new[] { new { codec_type = "audio", tags = new { language = "deu" } } }));
        Check(receipt.VerifySubtitle("none") == "not_requested" && receipt.VerifySubtitle("deu") == "missing");
        receipt = receipt with { Streams = JsonSerializer.SerializeToElement(new[] { new { codec_type = "subtitle", tags = new { language = "ger" } } }) };
        Check(receipt.VerifySubtitle("deu") == "verified");
    }

    private static void Run(Func<MediaLifecycleScenarios.Fixture, Task> action)
    {
        using var fixture = new MediaLifecycleScenarios.Fixture();
        action(fixture).GetAwaiter().GetResult();
    }
    private static void Check(bool value, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? expression = null)
    {
        if (!value) throw new InvalidOperationException("Subtitle assertion failed: " + expression);
    }
}
