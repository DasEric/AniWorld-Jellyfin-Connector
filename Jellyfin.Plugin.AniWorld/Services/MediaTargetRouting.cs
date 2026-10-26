using System.Net;
using Jellyfin.Plugin.AniWorld.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.AniWorld.Services;

public sealed class MediaTargetRouting
{
    private readonly ILibraryManager _library;
    private readonly Func<PluginConfiguration> _configuration;
    public MediaTargetRouting(ILibraryManager library) : this(library, () => Plugin.Instance!.Configuration) { }
    internal MediaTargetRouting(ILibraryManager library, Func<PluginConfiguration> configuration)
    { _library = library; _configuration = configuration; }

    public object GetLibraries() => _library.GetVirtualFolders()
        .Where(folder => folder.CollectionType?.ToString() is "movies" or "tvshows")
        .Select(folder => new { id = folder.ItemId, name = folder.Name, type = folder.CollectionType!.ToString(), paths = folder.Locations }).ToArray();

    public int Resolve(string type, IReadOnlyList<ConnectorPath> paths)
    {
        var config = _configuration();
        var movie = type == "movie";
        var id = movie ? config.MoviePathId : config.SeriesPathId;
        var root = movie ? config.MovieDownloadRoot : config.SeriesDownloadRoot;
        CheckLibrary(config, movie);
        if (id <= 0 || !paths.Any(path => path.Id == id && path.Path == root)) throw InvalidTarget();
        return id;
    }

    public async Task ValidateAsync(PluginConfiguration config, AniWorldClient client, CancellationToken token)
    {
        if (!config.EnableMediaRouting) return;
        if (config.MoviePathId == config.SeriesPathId || config.MovieLibraryId == config.SeriesLibraryId) throw InvalidTarget();
        var capabilities = await client.GetConnectorCapabilitiesAsync(token).ConfigureAwait(false);
        foreach (var movie in new[] { true, false })
        {
            CheckLibrary(config, movie);
            var id = movie ? config.MoviePathId : config.SeriesPathId;
            var target = capabilities.Paths.SingleOrDefault(path => path.Id == id && id > 0) ?? throw InvalidTarget();
            await client.VerifyMountAsync(id, movie ? config.MovieLibraryPath : config.SeriesLibraryPath, token).ConfigureAwait(false);
            if (movie) config.MovieDownloadRoot = target.Path; else config.SeriesDownloadRoot = target.Path;
        }
        if (capabilities.VideoGroups >= 1 && (capabilities.MediaTargets?.Enabled != true
            || capabilities.MediaTargets.Movie != config.MoviePathId || capabilities.MediaTargets.Series != config.SeriesPathId))
            await client.SaveMediaTargetsAsync(config.MoviePathId, config.SeriesPathId, capabilities.MediaTargets?.Revision ?? 0, token).ConfigureAwait(false);
    }

    private void CheckLibrary(PluginConfiguration config, bool movie)
    {
        var id = movie ? config.MovieLibraryId : config.SeriesLibraryId;
        var root = movie ? config.MovieLibraryPath : config.SeriesLibraryPath;
        var folder = _library.GetVirtualFolders().SingleOrDefault(item => item.ItemId == id);
        if (folder?.CollectionType?.ToString() != (movie ? "movies" : "tvshows")
            || string.IsNullOrWhiteSpace(root) || !folder.Locations.Contains(root, StringComparer.Ordinal)
            || !Directory.Exists(root)) throw InvalidTarget();
    }

    private static AniWorldException InvalidTarget() => new(HttpStatusCode.Conflict,
        "Film-/Serienziele fehlen oder wurden geändert. Bitte beide Mounts und die passenden Jellyfin-Bibliotheken neu zuordnen und prüfen.");
}
