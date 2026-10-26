using System.Text.Json;
using Jellyfin.Plugin.AniWorld.Models;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>Independent durable ownership intents; never pruned with request history.</summary>
public sealed class DownloadOwnershipStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<DownloadIntent> _entries;
    public DownloadOwnershipStore() : this(Plugin.Instance!.DataFolderPath) { }
    internal DownloadOwnershipStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "download-ownership.json");
        if (File.Exists(_path) && new FileInfo(_path).Length > 64 * 1024 * 1024) throw new IOException("Ownership store exceeds its size limit.");
        _entries = File.Exists(_path) ? JsonSerializer.Deserialize<List<DownloadIntent>>(File.ReadAllText(_path))
            ?? throw new IOException("Ownership store is invalid; restore its backup.") : [];
        if (_entries.Any(entry => entry.OwnerId.Length == 0 || entry.RequestKey.Length == 0)
            || _entries.Select(entry => entry.RequestKey).Distinct(StringComparer.Ordinal).Count() != _entries.Count)
            throw new IOException("Ownership store contains invalid or duplicate identities.");
    }

    public async Task RegisterAsync(MediaRequest request, string instanceId, CancellationToken token)
    {
        if (request.UserId.Length == 0 || request.SubmissionKey.Length == 0) throw new InvalidOperationException("Missing authenticated download identity.");
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var intent = new DownloadIntent(request.SubmissionKey, request.UserId, instanceId, request.SeriesUrl, request.Source, request.Title, request.MediaType);
            var old = _entries.SingleOrDefault(entry => entry.RequestKey == request.SubmissionKey);
            if (old is not null)
            {
                if (old.OwnerId != intent.OwnerId || old.InstanceId != intent.InstanceId || old.SeriesUrl != intent.SeriesUrl
                    || old.Source != intent.Source || old.MediaType != intent.MediaType)
                    throw new AniWorldApplicationException(System.Net.HttpStatusCode.Conflict, "Eine Downloadidentität darf nicht auf einen anderen Benutzer oder Downloader übertragen werden.");
                return;
            }
            if (_entries.Count >= 100_000) throw new IOException("Ownership capacity reached; existing ownership is retained.");
            var entries = _entries.Append(intent).ToList();
            var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(entries), token).ConfigureAwait(false);
                File.Move(temporary, _path, true);
                _entries = entries;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<DownloadIntent>> ListAsync(string userId, bool admin, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return _entries.Where(entry => admin || entry.OwnerId == userId).ToArray(); }
        finally { _gate.Release(); }
    }
}

public sealed record DownloadIntent(string RequestKey, string OwnerId, string InstanceId, string SeriesUrl, string Source, string Title, string MediaType);
