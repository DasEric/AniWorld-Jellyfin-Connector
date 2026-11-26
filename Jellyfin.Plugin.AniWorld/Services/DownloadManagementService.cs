using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AniWorld.Services;

public sealed class DownloadManagementService
{
    private readonly DownloadOwnershipStore _store;
    private readonly AniWorldClient _client;
    private readonly ILibraryManager _library;
    private readonly ITaskManager? _tasks;
    private readonly ConcurrentDictionary<string, DeletePreview> _previews = new(StringComparer.Ordinal);
    public DownloadManagementService(DownloadOwnershipStore store, AniWorldClient client, ILibraryManager library, ITaskManager? tasks = null)
    { _store = store; _client = client; _library = library; _tasks = tasks; }

    private async Task<(IReadOnlyList<DownloadIntent> Intents, IReadOnlyList<DownloadArtifact> Files)> ReadAsync(string user, bool admin, CancellationToken token)
    {
        var intents = await _store.ListAsync(user, admin, token).ConfigureAwait(false);
        if (intents.Count == 0) return (intents, []);
        var capabilities = await _client.GetConnectorCapabilitiesAsync(token).ConfigureAwait(false);
        intents = intents.Where(entry => entry.InstanceId == capabilities.InstanceId).ToArray();
        var files = await _client.GetArtifactsAsync(intents.Select(entry => entry.RequestKey).ToArray(), token).ConfigureAwait(false);
        return (intents, files);
    }

    public async Task<object> ListAsync(string user, bool admin, CancellationToken token)
    {
        var (intents, files) = await ReadAsync(user, admin, token).ConfigureAwait(false);
        return intents.GroupBy(entry => (entry.OwnerId, entry.Source, entry.SeriesUrl)).Select(group =>
        {
            var keys = group.Select(entry => entry.RequestKey).ToHashSet(StringComparer.Ordinal);
            var subtitles = group.ToDictionary(entry => entry.RequestKey, entry => entry.SubtitleLanguage);
            var artifacts = files.Where(file => keys.Contains(file.RequestKey) && file.State != "deleted").ToArray();
            var match = MatchLibrary(group.First(), artifacts);
            var libraryDeletable = match.Files.Select(file => file.ArtifactId).ToHashSet(StringComparer.Ordinal);
            return new { id = group.First().RequestKey, title = group.First().Title, mediaType = group.First().MediaType,
                ownerId = admin ? group.Key.OwnerId : null,
                imageItemId = match.ItemId,
                files = artifacts.Select(file => new { id = file.Id, name = file.Name, language = file.Language, state = file.State,
                    size = file.Size, mediaType = file.MediaType, streams = file.Streams, verification = file.Verification,
                    subtitleLanguage = subtitles[file.RequestKey], subtitleVerification = file.VerifySubtitle(subtitles[file.RequestKey]) }).ToArray(),
                libraryFiles = libraryDeletable.Count,
                deleting = IsDeleting(keys),
                canDelete = artifacts.Any(file => file.State is "owned" or "missing") || libraryDeletable.Count > 0 };
        }).Where(group => group.files.Length > 0).ToArray();
    }

    public async Task<object> PreviewAsync(string user, bool admin, string requestKey, CancellationToken token)
    {
        var (intents, files) = await ReadAsync(user, admin, token).ConfigureAwait(false);
        var selected = intents.SingleOrDefault(entry => entry.RequestKey == requestKey) ?? throw Missing();
        var keys = intents.Where(entry => entry.OwnerId == selected.OwnerId && entry.Source == selected.Source && entry.SeriesUrl == selected.SeriesUrl)
            .Select(entry => entry.RequestKey).ToHashSet(StringComparer.Ordinal);
        if (IsDeleting(keys)) throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Dieser Titel wird bereits gelöscht.");
        var chosen = files.Where(file => keys.Contains(file.RequestKey) && file.State is "owned" or "missing").ToArray();
        // Files the downloader can no longer prove (moved, new inode) are still this user's
        // download when Jellyfin holds a file with the same name and exact byte size.
        var roots = LibraryRoots();
        var local = MatchLibrary(selected, files.Where(file => keys.Contains(file.RequestKey) && file.State != "deleted").ToArray()).Files
            .Select(file => { try { return CheckedFile(file.Path, roots); } catch (AniWorldApplicationException) { return null; } })
            .OfType<FileSnapshot>().ToArray();
        if (chosen.Length + local.Length is < 1 or > 20_000) throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Bitte zwischen 1 und 20000 eigene Dateien pro Löschvorgang auswählen.");
        var id = Cache(new DeletePreview(user, admin, DateTime.UtcNow.AddMinutes(2), chosen.Select(file => file.Id).ToArray(), keys.ToArray(), null, local, selected.Title));
        var message = admin ? "Admin: Nur die aufgelisteten zugeordneten Dateien werden entfernt. Andere Dateien bleiben erhalten."
            : "Nur die aufgelisteten eigenen Dateien werden entfernt. Andere Dateien bleiben erhalten.";
        if (local.Length > 0) message += " " + local.Length + " davon wurden nach dem Download verschoben und werden direkt in der Jellyfin-Bibliothek gelöscht.";
        return new { token = id, title = selected.Title,
            files = chosen.Select(file => new { id = file.Id, name = file.Name, size = file.Size })
                .Concat(local.Select(file => new { id = string.Empty, name = Path.GetFileName(file.Path), size = file.Size })),
            count = chosen.Length + local.Length, message };
    }

    public Task<JsonElement> ConfirmAsync(string user, bool admin, string previewToken, CancellationToken token)
    {
        var preview = Consume(user, admin, previewToken);
        if (preview.LibraryItem.HasValue) throw Missing();
        return ExecuteDeleteAsync(user, admin, preview, token);
    }

    // Confirmation returns at once; the deletion runs in the background. Every ownership
    // and file check still happens inside the job, exactly as in a direct confirmation.
    public object StartDelete(string user, bool admin, string previewToken)
    {
        PruneJobs();
        var preview = Consume(user, admin, previewToken);
        if (preview.LibraryItem.HasValue) throw Missing();
        var job = new DeletionJob(Guid.NewGuid().ToString("N"), user, preview.Title, preview.Keys);
        lock (_jobs)
        {
            if (_jobs.Values.Any(other => !other.Finished && other.Keys.Intersect(job.Keys, StringComparer.Ordinal).Any()))
                throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Dieser Titel wird bereits gelöscht.");
            _jobs[job.Id] = job;
        }
        _ = Task.Run(async () =>
        {
            await _jobGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var result = await ExecuteDeleteAsync(user, admin, preview, CancellationToken.None).ConfigureAwait(false);
                var statuses = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
                    ? items.EnumerateArray().Select(item => item.TryGetProperty("status", out var status) ? status.GetString() : null).ToArray() : [];
                job.Complete(statuses.Count(status => status == "deleted"), statuses.Count(status => status != "deleted"), null);
            }
            catch (Exception exception)
            {
                job.Complete(0, 0, exception is AniWorldApplicationException or AniWorldException ? exception.Message : "Das Löschen ist fehlgeschlagen. Bitte erneut versuchen.");
            }
            finally { _jobGate.Release(); }
        });
        return new { jobId = job.Id, title = job.Title, state = "running" };
    }

    public object ListDeletions(string user)
    {
        PruneJobs();
        lock (_jobs)
        {
            return _jobs.Values.Where(job => job.User == user).OrderBy(job => job.Started).Select(job => new { id = job.Id, title = job.Title,
                state = !job.Finished ? "running" : job.Error is not null ? "failed" : "done", deleted = job.Deleted, conflicts = job.Conflicts,
                message = job.Error }).ToArray();
        }
    }

    private bool IsDeleting(IEnumerable<string> keys)
    {
        lock (_jobs) return _jobs.Values.Any(job => !job.Finished && keys.Any(key => job.Keys.Contains(key, StringComparer.Ordinal)));
    }

    private void PruneJobs()
    {
        lock (_jobs)
            foreach (var old in _jobs.Values.Where(job => job.Finished && job.FinishedAt < DateTime.UtcNow.AddMinutes(-15)).Select(job => job.Id).ToArray())
                _jobs.Remove(old);
    }

    private readonly Dictionary<string, DeletionJob> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _jobGate = new(1, 1);

    private sealed class DeletionJob(string id, string user, string title, string[] keys)
    {
        public string Id { get; } = id;
        public string User { get; } = user;
        public string Title { get; } = title;
        public string[] Keys { get; } = keys;
        public DateTime Started { get; } = DateTime.UtcNow;
        public volatile bool Finished;
        public DateTime FinishedAt;
        public int Deleted;
        public int Conflicts;
        public string? Error;
        public void Complete(int deleted, int conflicts, string? error)
        {
            Deleted = deleted; Conflicts = conflicts; Error = error; FinishedAt = DateTime.UtcNow; Finished = true;
        }
    }

    private async Task<JsonElement> ExecuteDeleteAsync(string user, bool admin, DeletePreview preview, CancellationToken token)
    {
        var (intents, files) = await ReadAsync(user, admin, token).ConfigureAwait(false);
        var allowed = intents.Select(entry => entry.RequestKey).ToHashSet(StringComparer.Ordinal);
        if (preview.Keys.Any(key => !allowed.Contains(key)) || preview.Ids.Any(id => !files.Any(file => file.Id == id && preview.Keys.Contains(file.RequestKey, StringComparer.Ordinal)
            && file.State is "owned" or "missing" or "deleted"))) throw Missing();
        var result = preview.Ids.Length > 0 ? await _client.DeleteArtifactsAsync(preview.Ids, token).ConfigureAwait(false) : default;
        // Physical files are authoritative. Notify Jellyfin even after a partial delete.
        if (files.Any(file => preview.Ids.Contains(file.Id, StringComparer.Ordinal) && file.MediaType == "movie")) _tasks?.QueueScheduledTask<AniWorldMovieLibraryScanTask>();
        if (files.Any(file => preview.Ids.Contains(file.Id, StringComparer.Ordinal) && file.MediaType == "series")) _tasks?.QueueScheduledTask<AniWorldSeriesLibraryScanTask>();
        if (preview.Snapshots.Count == 0) return result;

        // Re-prove every moved file at confirmation time: same owner group, same
        // Jellyfin match by name and size, unchanged since the preview, inside a library.
        var keys = preview.Keys.ToHashSet(StringComparer.Ordinal);
        var intent = intents.First(entry => keys.Contains(entry.RequestKey));
        var matched = MatchLibrary(intent, files.Where(file => keys.Contains(file.RequestKey) && file.State != "deleted").ToArray()).Files
            .ToDictionary(file => file.Path, StringComparer.Ordinal);
        var items = new List<object>();
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("items", out var remote) && remote.ValueKind == JsonValueKind.Array)
            items.AddRange(remote.EnumerateArray().Select(item => (object)item.Clone()));
        foreach (var snapshot in preview.Snapshots)
        {
            var status = "conflict";
            try
            {
                if (matched.TryGetValue(snapshot.Path, out var file) && CheckedFile(snapshot.Path) == snapshot)
                {
                    DeleteLibraryFile(file);
                    status = "deleted";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or AniWorldApplicationException)
            {
                status = "conflict";
            }
            items.Add(new { id = Path.GetFileName(snapshot.Path), status });
        }
        lock (_indexGate) _index = null;
        if (intent.MediaType == "movie") _tasks?.QueueScheduledTask<AniWorldMovieLibraryScanTask>();
        else _tasks?.QueueScheduledTask<AniWorldSeriesLibraryScanTask>();
        return JsonSerializer.SerializeToElement(new { items });
    }

    private static readonly string[] SidecarExtensions = [".nfo", ".srt", ".ass", ".ssa", ".sub", ".idx", ".vtt", ".sup", ".jpg", ".jpeg", ".png", ".webp", ".bif"];

    private void DeleteLibraryFile(LibraryFile file)
    {
        var directory = Path.GetDirectoryName(file.Path)!;
        var stem = Path.GetFileNameWithoutExtension(file.Path);
        // Only the video and its own sidecars (same base name); other episodes stay untouched.
        var sidecars = Directory.EnumerateFiles(directory).Where(path =>
        {
            var name = Path.GetFileName(path);
            return !string.Equals(path, file.Path, StringComparison.Ordinal)
                && (name.StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase) || name.StartsWith(stem + "-", StringComparison.OrdinalIgnoreCase))
                && SidecarExtensions.Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase)
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }).ToArray();
        File.Delete(file.Path);
        foreach (var sidecar in sidecars) File.Delete(sidecar);
        var item = _library.GetItemById(file.ItemId);
        if (item is not null) _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = false }, true);
        var roots = LibraryRoots();
        for (var parent = new DirectoryInfo(directory); parent is not null && !roots.Contains(Path.TrimEndingDirectorySeparator(parent.FullName), StringComparer.Ordinal); parent = parent.Parent)
        {
            if (Directory.EnumerateFileSystemEntries(parent.FullName).Any()) break;
            parent.Delete(false); // only empty folders, never recursive
        }
    }

    // Jellyfin-side identity of the user's downloaded files: same file name and exact
    // byte size. The owning series or movie also supplies the poster.
    private LibraryMatch MatchLibrary(DownloadIntent intent, IReadOnlyList<DownloadArtifact> artifacts)
    {
        try
        {
            var index = FileIndex();
            var wantEpisodes = intent.MediaType == "series";
            var used = new HashSet<string>(StringComparer.Ordinal);
            var hits = new List<(DownloadArtifact Artifact, IndexedFile File)>();
            foreach (var artifact in artifacts)
            {
                if (!index.TryGetValue(artifact.Name, out var candidates)) continue;
                var hit = candidates.FirstOrDefault(file => file.IsEpisode == wantEpisodes && !used.Contains(file.Path) && SizeOf(file.Path) == artifact.Size);
                if (hit is null) continue;
                used.Add(hit.Path);
                hits.Add((artifact, hit));
            }
            var owners = hits.GroupBy(hit => hit.File.OwnerId).OrderByDescending(group => group.Count()).ToArray();
            // Files split evenly over two shows are ambiguous: placeholder poster, nothing deletable.
            if (owners.Length == 0 || (owners.Length > 1 && owners[0].Count() == owners[1].Count()))
                return new LibraryMatch(CoverItemId(intent.Title, intent.MediaType), []);
            var owner = owners[0];
            return new LibraryMatch(owner.Key, owner.Where(hit => hit.Artifact.State == "changed")
                .Select(hit => new LibraryFile(hit.Artifact.Id, hit.File.Path, hit.File.ItemId)).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new LibraryMatch(null, []);
        }
    }

    private static long SizeOf(string path)
    {
        try { var info = new FileInfo(path); return info.Exists ? info.Length : -1; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return -1; }
    }

    private readonly object _indexGate = new();
    private (DateTime Built, Dictionary<string, IndexedFile[]> Files)? _index;

    private Dictionary<string, IndexedFile[]> FileIndex()
    {
        lock (_indexGate)
        {
            if (_index is { } cached && cached.Built > DateTime.UtcNow.AddMinutes(-2)) return cached.Files;
            var items = _library.GetItemList(new InternalItemsQuery { Recursive = true, IncludeItemTypes = [BaseItemKind.Episode, BaseItemKind.Movie],
                IsVirtualItem = false, EnableTotalRecordCount = false }) ?? [];
            var files = items.Where(item => !string.IsNullOrEmpty(item.Path))
                .Select(item => item is MediaBrowser.Controller.Entities.TV.Episode episode
                    ? new IndexedFile(item.Id, episode.SeriesId, item.Path, true)
                    : new IndexedFile(item.Id, item.Id, item.Path, false))
                .Where(file => file.OwnerId != Guid.Empty)
                .GroupBy(file => Path.GetFileName(file.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
            _index = (DateTime.UtcNow, files);
            return files;
        }
    }

    private Guid? CoverItemId(string title, string mediaType)
    {
        var wanted = CoverTitle(title);
        if (wanted.Length == 0) return null;
        try
        {
            // Search without a trailing "(2011)" and punctuation, then compare normalized
            // titles, so "Black Mirror (2011)" and "Black Mirror" find each other.
            var term = System.Text.RegularExpressions.Regex.Replace(title, @"\(\s*\d{4}\s*\)", " ").Trim();
            if (term.Length > 120) term = term[..120];
            var matches = _library.GetItemList(new InternalItemsQuery { Recursive = true,
                    IncludeItemTypes = [mediaType == "series" ? BaseItemKind.Series : BaseItemKind.Movie],
                    SearchTerm = term, IsVirtualItem = false, Limit = 50, EnableTotalRecordCount = false })
                ?.Where(item => CoverTitle(item.Name) == wanted || CoverTitle(item.OriginalTitle) == wanted)
                .Select(item => item.Id).Distinct().Take(2).ToArray();
            // Ambiguous titles show the placeholder instead of a possibly wrong poster.
            return matches is { Length: 1 } ? matches[0] : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A poster is optional; a library lookup failure must never hide own media.
            return null;
        }
    }

    // Storage of the media libraries the connector downloads into; all libraries otherwise.
    public object StorageUsage(Configuration.PluginConfiguration? config)
    {
        var configured = new[] { config?.MovieLibraryPath, config?.SeriesLibraryPath }.Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => path!).ToArray();
        string[] paths;
        try { paths = configured.Length > 0 ? configured : LibraryRoots(); }
        catch (Exception exception) when (exception is not OperationCanceledException) { paths = configured; }
        var info = MeasureStorage(paths);
        // Explicit lowercase names: Jellyfin's serializer does not camel-case anonymous results for us.
        return new { available = info.Available, total = info.Total, used = info.Used, free = info.Free, level = info.Level };
    }

    internal static StorageInfo MeasureStorage(IEnumerable<string> paths)
    {
        var drives = new List<(long Total, long Free)>();
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (!Directory.Exists(path)) continue;
                var full = Path.GetFullPath(path);
                var drive = new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full);
                if (!drive.IsReady || drive.TotalSize <= 0) continue;
                // Libraries on the same disk report identical numbers and are counted once.
                var key = (drive.TotalSize, drive.AvailableFreeSpace);
                if (!drives.Contains(key)) drives.Add(key);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable mount is left out instead of failing the page.
            }
        }
        if (drives.Count == 0) return new StorageInfo(false, 0, 0, 0, "unknown");
        var total = drives.Sum(drive => drive.Total);
        var free = Math.Clamp(drives.Sum(drive => drive.Free), 0, total);
        var freeShare = (double)free / total;
        return new StorageInfo(true, total, total - free, free, freeShare >= 0.30 ? "good" : freeShare >= 0.10 ? "medium" : "low");
    }

    internal static string CoverTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        var withoutYear = System.Text.RegularExpressions.Regex.Replace(title, @"\(\s*\d{4}\s*\)", " ");
        return new string(withoutYear.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    public object ListLibrary(string query)
    {
        var items = _library.GetItemList(new InternalItemsQuery { Recursive = true, IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series],
            IsVirtualItem = false, SearchTerm = query.Length > 120 ? query[..120] : query, Limit = 100, EnableTotalRecordCount = false });
        return items.Select(item => new { id = item.Id, title = item.Name, mediaType = item is MediaBrowser.Controller.Entities.TV.Series ? "series" : "movie" }).ToArray();
    }

    public object PreviewLibrary(string user, Guid itemId)
    {
        var item = _library.GetItemById(itemId);
        if (item is not MediaBrowser.Controller.Entities.Movies.Movie and not MediaBrowser.Controller.Entities.TV.Series) throw Missing();
        var snapshots = Snapshot(item);
        var id = Cache(new DeletePreview(user, true, DateTime.UtcNow.AddMinutes(2), [], [], itemId, snapshots));
        return new { token = id, title = item.Name, files = snapshots.Select(file => new { name = Path.GetFileName(file.Path), size = file.Size }),
            count = snapshots.Count, message = "Admin: Der gesamte gewählte Bibliothekseintrag einschließlich seiner Dateien wird gelöscht." };
    }

    public void ConfirmLibrary(string user, string previewToken)
    {
        var preview = Consume(user, true, previewToken);
        var item = preview.LibraryItem.HasValue ? _library.GetItemById(preview.LibraryItem.Value) : null;
        if (item is null || !Snapshot(item).SequenceEqual(preview.Snapshots)) throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Der Bibliothekseintrag wurde geändert. Bitte die Vorschau neu öffnen.");
        _library.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, true);
    }

    private IReadOnlyList<FileSnapshot> Snapshot(BaseItem item)
    {
        var paths = item.GetDeletePaths().SelectMany(path => path.IsDirectory
            ? Directory.EnumerateFiles(path.FullName, "*", SearchOption.AllDirectories) : [path.FullName])
            .Where(File.Exists).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (paths.Length > 20_000) throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Bitte einen kleineren Bibliothekseintrag auswählen.");
        var roots = LibraryRoots();
        return paths.Select(path => CheckedFile(path, roots)).ToArray();
    }

    private string[] LibraryRoots()
        => _library.GetVirtualFolders().SelectMany(folder => folder.Locations)
            .Select(location => Path.TrimEndingDirectorySeparator(Path.GetFullPath(location))).ToArray();

    // A file is deletable only inside a Jellyfin library root and never through a link.
    private FileSnapshot CheckedFile(string path, string[]? roots = null)
    {
        var full = Path.GetFullPath(path);
        if (!(roots ?? LibraryRoots()).Any(root => !Path.GetRelativePath(root, full).StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(Path.GetRelativePath(root, full)))) throw Missing();
        for (FileSystemInfo? part = new FileInfo(full); part is not null; part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
            if ((part.Attributes & FileAttributes.ReparsePoint) != 0) throw Missing();
        var info = new FileInfo(full);
        return new FileSnapshot(full, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    private string Cache(DeletePreview preview)
    {
        foreach (var old in _previews.Where(pair => pair.Value.Expires <= DateTime.UtcNow).Select(pair => pair.Key)) _previews.TryRemove(old, out _);
        if (_previews.Count >= 1000) throw new AniWorldApplicationException(HttpStatusCode.TooManyRequests, "Bitte kurz warten.");
        var id = Guid.NewGuid().ToString("N"); _previews[id] = preview; return id;
    }
    private DeletePreview Consume(string user, bool admin, string id)
    {
        if (!_previews.TryGetValue(id, out var value) || value.User != user || value.Admin != admin || value.Expires <= DateTime.UtcNow
            || !_previews.TryRemove(id, out value)) throw Missing();
        return value;
    }
    private static AniWorldApplicationException Missing() => new(HttpStatusCode.NotFound, "Keine passende eigene Datei oder gültige Löschvorschau gefunden.");
    private sealed record DeletePreview(string User, bool Admin, DateTime Expires, string[] Ids, string[] Keys, Guid? LibraryItem, IReadOnlyList<FileSnapshot> Snapshots, string Title = "");
    private sealed record FileSnapshot(string Path, long Size, long Modified);
    internal sealed record StorageInfo(bool Available, long Total, long Used, long Free, string Level);
    private sealed record LibraryFile(string ArtifactId, string Path, Guid ItemId);
    private sealed record LibraryMatch(Guid? ItemId, IReadOnlyList<LibraryFile> Files);
    private sealed record IndexedFile(Guid ItemId, Guid OwnerId, string Path, bool IsEpisode);
}
