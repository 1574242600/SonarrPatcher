# AniRss Patch

Implements the core functionality of [ani-rss](https://github.com/wushuo894/ani-rss) via a
**new scheduled task on Sonarr's own task manager**: instead of relying on Sonarr's
search/indexer integration, it watches your own RSS feeds with a configurable per-feed
episode-number regex, pushes matching releases to a download client, and **forces the
import to use the episode it grabbed** — no matter how unparseable the downloaded file
names are.

Two Harmony patches do the wiring (registering the `AniRssCommand` task into Sonarr's
scheduled-task repository/cache + service capture), and the import step uses Sonarr's own
`ManualImportCommand` — nothing internal is re-implemented.

## What it does

1. **Scheduled task** — registers a new `AniRssCommand` task into Sonarr's own
   scheduled-task repository/cache, so the subscription pass runs every
   `ANIRSS_INTERVAL_MINUTES`.
2. **Subscription pass** (`AniRssCommandExecutor`) — for each subscribed series:
   - loads the subscribe config (file or command payload),
   - resolves the download client (optionally by name),
   - fetches each RSS feed (host-level 500 ms rate limit), extracts the episode number
     from each item title with `epRegex`, adds `epOffset`, and looks up the Sonarr episode,
   - skips episodes that already have a file, unless the file came from a lower-priority
     AniRss source (releases are tagged `#ANIRSS{index}`, where `{index}` is the feed
     priority) — in that case it pushes again so Sonarr upgrades the file,
   - queues the release with `DownloadService.DownloadReport`, appending `#ANIRSS{index}`
     to the title, which is persisted into the grab history.
3. **Import binding** (`AniRssImportBinder`) — when a download completes, Sonarr normally
   re-parses the file/folder names to decide which episode it belongs to and rejects
   anything it can't map. For downloads carrying the `#ANIRSS` marker, the patch
   intercepts `CompletedDownloadService.Import` and hands the download to Sonarr's
   official `ManualImportCommand` with the episodes from the grab history:
   - **Single file** → always bound to the grabbed episode(s).
   - **Several files** → files Sonarr can already map keep their mapping; only the largest
     unmapped file is bound to the grabbed episode, so a batch can never collapse onto one
     episode.
   - Files rejected for safety reasons (sample, unpacking, free space, dangerous file,
     ...) are left out — only episode-matching/upgrade rejections are overridden.
   - Since it goes through `ManualImportService`, the download is completed like a normal
     import: history entry, `EpisodeImportedEvent`, upgrade notifications, and the
     download is removed from the queue. Because manual import bypasses the upgrade
     specification, re-pushing a better source always replaces the old file.

## Environment variables

| Variable | Meaning | Default |
| --- | --- | --- |
| `ANIRSS_SUBSCRIBE_FILE` | Path to the subscribe config JSON. Unset → `config/anirss.subscribe.json` next to the patch DLL. | `<patch DLL dir>/config/anirss.subscribe.json` |
| `ANIRSS_INTERVAL_MINUTES` | How often the subscription pass runs. `0` disables the patch entirely. | `60` |
| `ANIRSS_DOWNLOAD_CLIENT_NAME` | Download client to use (match by Sonarr client name). Unset → first configured client. | — |

When `ANIRSS_INTERVAL_MINUTES=0`, `ShouldPatch()` returns false and the whole patch (task +
import binding) is inactive. When the subscribe file is missing, the task is still registered
but every pass skips execution (with a warning) until the file appears.

## Subscribe file

A JSON array of subscription entries. The file is read every pass, so editing it takes
effect on the next run; it can also be updated through the `AniRss` command payload, in
which case it is persisted back to `ANIRSS_SUBSCRIBE_FILE` (formatted JSON).

```json
[
  {
    "title": "我的订阅示例",
    "tvdbId": 100,
    "season": 1,
    "epRegex": " ([0-9]{2,}) ",
    "epOffset": 0,
    "rss": [
      "https://feed.example.com/rss?q=show",
      "https://backup.example.com/rss?q=show"
    ]
  }
]
```

| Field | Meaning |
| --- | --- |
| `title` | *Optional.* Human-readable label to make the file easier to read/edit; **not used by any business logic**. Omitted from the written file when not set. |
| `tvdbId` | Series to subscribe, resolved via Sonarr's existing series (TVDB id). |
| `season` | Season number to watch. |
| `epRegex` | *Optional.* Regex applied to each RSS item title; the first capture group (or the whole match) is used and its digits are the episode number. Unset means `` ` ([0-9]{2,}) ` ``; omitted from the written file when unset. |
| `epOffset` | *Optional.* Added to the parsed episode number (for series whose numbering starts at a non-1 episode). Defaults to `0`; omitted from the written file when `0`. |
| `rss` | Feed URLs, **lower index = higher priority**. Used both for picking the best source and for the `#ANIRSS{index}` upgrade marker. |

## Usage

Run it through the Loader (recommended): set `DOTNET_STARTUP_HOOKS` to
`SonarrPatcher.Loader.dll` and the loader auto-discovers this patch. Or run standalone:
point `DOTNET_STARTUP_HOOKS` directly at `SonarrPatcher.Patches.AniRss.dll` — its
`StartupHook.Initialize()` self-bootstraps `0Harmony.dll`/`Sonarr.Common.dll` and applies
the patch.

### Docker example

```yaml
services:
  sonarr:
    image: lscr.io/linuxserver/sonarr:latest
    environment:
      - DOTNET_STARTUP_HOOKS=/custom/SonarrPatcher.Loader.dll
      # optional: defaults to <patch dll dir>/config/anirss.subscribe.json
      # - ANIRSS_SUBSCRIBE_FILE=/config/anirss_subscribe.json
      - ANIRSS_INTERVAL_MINUTES=60
      - ANIRSS_DOWNLOAD_CLIENT_NAME=qBittorrent   # optional
    volumes:
      - /path/to/dist:/custom:ro
      - ./config:/config
```

## Tests

Unit tests cover the episode-number regex parsing, the `#ANIRSS` marker handling, the
subscribe config round-trip, and the import file-selection policy (single-file binding,
multi-file handling, sample rejection). Integration tests drive the real
`CompletedDownloadService.Import` interception with stubbed Sonarr services and verify the
patch targets exist in the running Sonarr build; they need a Sonarr publish dir containing
`Sonarr.Core.dll`, `Sonarr.Common.dll`, `NLog.dll` and `0Harmony.dll` (default
`/workspaces/Sonarr/_output/net6.0/linux-x64/publish`); when absent, those tests are
skipped.
