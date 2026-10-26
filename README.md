# AniWorld Requests for Jellyfin

Search your enabled H0melab Downloader sources and request films, series and manga directly from Jellyfin Web. Administrators can also enable eBooks, audiobooks and podcasts.

## Requirements

- Jellyfin **12** and the Jellyfin Web client.
- [H0melab Downloader](https://github.com/DasEric/H0melab-Downloader-V2) **2.3.0 or newer**, including grouped series and shared media destinations.
- A downloader API key with **administrator / full access**.
- The Jellyfin server must reach the downloader. Downloaded films and series must be accessible through the corresponding Jellyfin library folders.

## Installation

1. Open **Dashboard → Plugins → Repositories** in Jellyfin.
2. Add a repository named **AniWorld Connector** with this URL:
   ```text
   https://daseric.github.io/AniWorld-Jellyfin-Connector/manifest.json
   ```
3. Install **AniWorld Requests** from the catalog.
4. Restart Jellyfin and reload the web client.

## Connect your downloader

1. In H0melab Downloader, create an **administrator / full-access API key** under **Settings → API keys**.
2. Open **Dashboard → Plugins → AniWorld Requests** in Jellyfin.
3. Enter the downloader's base URL and API key, save, then test the connection.

Use an address reachable **from the Jellyfin server**, not only from your browser. The saved API key is not shown again. Only sources enabled in the downloader and allowed by the plugin are available.

## Choose movie and series destinations

Create your movie and series mount points / custom download paths in H0melab Downloader first. Under **Settings → Custom Paths → Movie and show destinations**, choose the two separate, non-overlapping mounts. Add the same storage folders to Jellyfin libraries of type **Movies** and **Shows**.

In the plugin settings:

1. Reload the available mounts and libraries. The downloader's central movie/show destinations are selected automatically.
2. Select the **movie mount** and its **Jellyfin movie-library folder**.
3. Select the **series mount** and its **Jellyfin show-library folder**.
4. Enable the library mapping check and save. You can also initially choose both destinations here; after the shared-folder checks, they are saved centrally in the downloader.

The plugin verifies the mapping with a temporary file, which it removes afterward. Once central destinations are configured, the actual content type takes priority over the source's default path, including downloads started directly in the downloader. A series found on a movie site goes to the series destination; films go to the movie destination. Separate films within a series collection are routed individually. A conflicting manual path is reported, not silently used.

Container paths may differ. For example, the downloader can see a series folder as `/downloads/series`, while Jellyfin sees the **same folder** as `/media/tv`. Select the corresponding paths on each side. Jellyfin must have access to the mapped storage.

If a configured mount disappears or changes, requests stop rather than silently using another destination. Reload, verify and save the mapping again. Existing files are not moved. Until central destinations are configured, the previous source defaults remain in use. Turning off the connector's library mapping check does not disable central downloader destinations.

### Series from individual episode pages

Supported episode-page sources such as FilmPalast and Filmo show one series card per source rather than separate cards for `SiloS01E01`, `SiloS01E02`, and later seasons. Open the series to check its current seasons and episodes. An incomplete source response is reported rather than presented as a complete series.

One request downloads the available missing episodes as separate files, organized as `Silo/Season 01/Silo - S01E01 - german-dub.mkv`. A verified series year is included when available. Different language variants keep separate filenames instead of adding an extra language-level library folder. Larger selections use resumable 500-entry work sections within one request, up to 20,000 entries; they are not silently truncated at 500.

Jellyfin and Plex should both point their movie and show libraries at the corresponding separate roots. No Plex login is required to write this folder structure. Already misplaced episodes need a separate, reviewed move and a library rescan; enabling the feature does not rearrange your existing media.

## Request media and select a language

Open **Media requests** in Jellyfin and search the **Series & Movies** section. The optional **Books & Audio** section appears when an administrator enables one of its categories.

For videos, the detail dialog checks individual episodes and their providers:

- **Dub** means audio; **Sub** means subtitles. They are not interchangeable.
- Language coverage is shown separately, such as **German: 8/10 episodes** and **English: 10/10 episodes**.
- Partially available languages stay visible. Confirm the partial-selection checkbox to request only the available missing episodes; the rest remains outstanding.
- **Automatic** selects an available provider for each episode without changing the chosen language.
- An existing English file does not automatically satisfy a German request. A suitable multi-audio file can satisfy both.

Source availability can change. The selection is checked again before submission or administrator approval. Actual downloaded audio tracks are shown under **My downloads**; missing language tags remain **unconfirmed**, rather than being guessed.

Requests either wait for administrator approval or start immediately, depending on the plugin's approval setting. After successful downloads, the plugin scans the appropriate library. A finished download and an item already indexed by Jellyfin are different states.

## Delete your own downloads

Open **My requests → My downloads**, choose **Delete own downloads / series**, and review the listed files before confirming.

- You can delete only files proven to have been newly created by your own connector requests.
- Deleting a series includes your associated downloads across requests, but preserves other users' episodes and manually added files.
- Requesting an existing file does not transfer ownership.
- Older downloads without file receipts are managed by an administrator.
- Withdrawing a pending request does not delete downloaded files.

Deletion permanently removes the confirmed files. If a file was replaced, changed or is in use, the operation reports a conflict instead of deleting an unrelated version. Refresh the list to review any remaining files. Jellyfin updates its library afterward.

Administrators can manage all connector downloads and use the separate **entire movie/show library** section to delete any selected library title, including manually added content. A confirmation preview shows the affected files.

## Other settings

- Require approval or allow direct downloads.
- Limit open requests per user.
- Allow specific sources and optional book/audio categories.
- Set a preferred language and provider.
- Temporarily pause new requests using maintenance mode.

The optional Jellix integration uses the connector's existing settings and approval policy; it does not need a second downloader API key. If its preferred language is unavailable, select a different variant explicitly in the connector.

## Troubleshooting

| Problem | Check |
| --- | --- |
| Connection or source-list error | Downloader URL, server-to-server connectivity and a full-access API key. |
| File-receipt / protocol error | Update the downloader to the matching 2.3.0-or-newer build, then restart Jellyfin. |
| Mount verification fails | Both selected paths must reference the same storage; check mounts and read/write permissions. |
| Download finished but missing in Jellyfin | Correct library mapping, readable media files and the movie/show scan task in **Dashboard → Scheduled Tasks**. |
| A language is only partly available | Review the per-language coverage and explicitly confirm a partial request if desired. |
| No delete button for an older title | There is no proven ownership receipt; ask an administrator to manage it. |
| Series details cannot be verified | Retry the source. Missing season numbers or incomplete pagination are not guessed. |
| A file cannot be deleted | Refresh after an active download finishes, or ask an administrator to review changed files. |

## License

[MIT](LICENSE). Copyright © 2026 Eric / @DasEric. See [NOTICE](NOTICE) for acknowledgements.
