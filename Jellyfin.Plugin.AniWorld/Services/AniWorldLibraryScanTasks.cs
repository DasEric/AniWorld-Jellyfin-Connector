using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AniWorld.Services;

/// <summary>Scans only the physical folders of the matching Jellyfin libraries.</summary>
public abstract class AniWorldLibraryScanTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;

    protected AniWorldLibraryScanTask(ILibraryManager libraryManager, IFileSystem fileSystem)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
    }

    public abstract string Name { get; }

    public abstract string Key { get; }

    public abstract string Description { get; }

    public string Category => "Bibliotheken";

    protected abstract string CollectionType { get; }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var physicalFolders = _libraryManager.GetVirtualFolders()
            .Where(folder => string.Equals(folder.CollectionType.ToString(), CollectionType, StringComparison.OrdinalIgnoreCase))
            .Select(folder => Guid.TryParse(folder.ItemId, out var id) ? _libraryManager.GetItemById(id) : null)
            .OfType<CollectionFolder>()
            .SelectMany(folder => folder.GetPhysicalFolders())
            .DistinctBy(folder => folder.Id)
            .ToArray();

        if (physicalFolders.Length == 0)
        {
            throw new InvalidOperationException($"No physical Jellyfin {CollectionType} library folders were found.");
        }

        _libraryManager.ClearIgnoreRuleCache();
        progress.Report(0);
        for (var index = 0; index < physicalFolders.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = physicalFolders[index];
            await folder.RefreshMetadata(cancellationToken).ConfigureAwait(false);
            await folder.ValidateChildren(
                new Progress<double>(),
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.Default,
                },
                recursive: true,
                allowRemoveRoot: false,
                cancellationToken).ConfigureAwait(false);
            progress.Report(100.0 * (index + 1) / physicalFolders.Length);
        }
    }
}

/// <summary>Visible scheduled scan for movie libraries after complete film downloads.</summary>
public sealed class AniWorldMovieLibraryScanTask : AniWorldLibraryScanTask
{
    public AniWorldMovieLibraryScanTask(ILibraryManager libraryManager, IFileSystem fileSystem)
        : base(libraryManager, fileSystem)
    {
    }

    public override string Name => "AniWorld: Filmbibliotheken scannen";

    public override string Key => "AniWorldMovieLibraryScan";

    public override string Description => "Scannt nach abgeschlossenen AniWorld-Filmdownloads die physischen Ordner der Filmbibliotheken.";

    protected override string CollectionType => "movies";
}

/// <summary>Visible scheduled scan for series libraries after complete series downloads.</summary>
public sealed class AniWorldSeriesLibraryScanTask : AniWorldLibraryScanTask
{
    public AniWorldSeriesLibraryScanTask(ILibraryManager libraryManager, IFileSystem fileSystem)
        : base(libraryManager, fileSystem)
    {
    }

    public override string Name => "AniWorld: Serienbibliotheken scannen";

    public override string Key => "AniWorldSeriesLibraryScan";

    public override string Description => "Scannt nach vollständig abgeschlossenen AniWorld-Seriendownloads die physischen Ordner der Serienbibliotheken.";

    protected override string CollectionType => "tvshows";
}
