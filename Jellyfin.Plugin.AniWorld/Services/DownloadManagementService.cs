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
            var artifacts = files.Where(file => keys.Contains(file.RequestKey) && file.State != "deleted").ToArray();
            return new { id = group.First().RequestKey, title = group.First().Title, mediaType = group.First().MediaType,
                ownerId = admin ? group.Key.OwnerId : null,
                files = artifacts.Select(file => new { id = file.Id, name = file.Name, language = file.Language, state = file.State,
                    size = file.Size, mediaType = file.MediaType, streams = file.Streams, verification = file.Verification }).ToArray(),
                canDelete = artifacts.Any(file => file.State is "owned" or "missing") };
        }).Where(group => group.files.Length > 0).ToArray();
    }

    public async Task<object> PreviewAsync(string user, bool admin, string requestKey, CancellationToken token)
    {
        var (intents, files) = await ReadAsync(user, admin, token).ConfigureAwait(false);
        var selected = intents.SingleOrDefault(entry => entry.RequestKey == requestKey) ?? throw Missing();
        var keys = intents.Where(entry => entry.OwnerId == selected.OwnerId && entry.Source == selected.Source && entry.SeriesUrl == selected.SeriesUrl)
            .Select(entry => entry.RequestKey).ToHashSet(StringComparer.Ordinal);
        var chosen = files.Where(file => keys.Contains(file.RequestKey) && file.State is "owned" or "missing").ToArray();
        if (chosen.Length is < 1 or > 20_000) throw new AniWorldApplicationException(HttpStatusCode.Conflict, "Bitte zwischen 1 und 20000 eigene Dateien pro Löschvorgang auswählen.");
        var id = Cache(new DeletePreview(user, admin, DateTime.UtcNow.AddMinutes(2), chosen.Select(file => file.Id).ToArray(), keys.ToArray(), null, []));
        return new { token = id, title = selected.Title, files = chosen.Select(file => new { id = file.Id, name = file.Name, size = file.Size }),
            count = chosen.Length, message = admin ? "Admin: Nur die aufgelisteten zugeordneten Dateien werden entfernt. Andere Dateien bleiben erhalten."
                : "Nur die aufgelisteten eigenen Dateien werden entfernt. Andere Dateien bleiben erhalten." };
    }

    public async Task<JsonElement> ConfirmAsync(string user, bool admin, string previewToken, CancellationToken token)
    {
        var preview = Consume(user, admin, previewToken);
        if (preview.LibraryItem.HasValue) throw Missing();
        var (intents, files) = await ReadAsync(user, admin, token).ConfigureAwait(false);
        var allowed = intents.Select(entry => entry.RequestKey).ToHashSet(StringComparer.Ordinal);
        if (preview.Keys.Any(key => !allowed.Contains(key)) || preview.Ids.Any(id => !files.Any(file => file.Id == id && preview.Keys.Contains(file.RequestKey, StringComparer.Ordinal)
            && file.State is "owned" or "missing" or "deleted"))) throw Missing();
        var result = await _client.DeleteArtifactsAsync(preview.Ids, token).ConfigureAwait(false);
        // Physical files are authoritative. Notify Jellyfin even after a partial delete.
        if (files.Any(file => preview.Ids.Contains(file.Id, StringComparer.Ordinal) && file.MediaType == "movie")) _tasks?.QueueScheduledTask<AniWorldMovieLibraryScanTask>();
        if (files.Any(file => preview.Ids.Contains(file.Id, StringComparer.Ordinal) && file.MediaType == "series")) _tasks?.QueueScheduledTask<AniWorldSeriesLibraryScanTask>();
        return result;
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
        var roots = _library.GetVirtualFolders().SelectMany(folder => folder.Locations).Select(Path.GetFullPath).ToArray();
        return paths.Select(path =>
        {
            var full = Path.GetFullPath(path);
            if (!roots.Any(root => !Path.GetRelativePath(root, full).StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(Path.GetRelativePath(root, full)))) throw Missing();
            for (FileSystemInfo? part = new FileInfo(full); part is not null; part = part is FileInfo file ? file.Directory : ((DirectoryInfo)part).Parent)
                if ((part.Attributes & FileAttributes.ReparsePoint) != 0) throw Missing();
            var info = new FileInfo(full);
            return new FileSnapshot(full, info.Length, info.LastWriteTimeUtc.Ticks);
        }).ToArray();
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
    private sealed record DeletePreview(string User, bool Admin, DateTime Expires, string[] Ids, string[] Keys, Guid? LibraryItem, IReadOnlyList<FileSnapshot> Snapshots);
    private sealed record FileSnapshot(string Path, long Size, long Modified);
}
