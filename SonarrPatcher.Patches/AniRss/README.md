# AniRss Patch

Implements the core functionality of [ani-rss](https://github.com/wushuo894/ani-rss) via a
**new scheduled task on Sonarr's own task manager**: instead of relying on Sonarr's
search/indexer integration, it watches your own RSS feeds with a configurable per-feed
episode-number regex, pushes matching releases to a download client, and **forces the
import to use the episode it grabbed** — no matter how unparseable the downloaded file
names are.

Three Harmony hooks do the wiring: `AssemblyLoader.Load` (this assembly is added to the
list Sonarr scans, so `AniRssCommand` and its executor are wired up by the DI container),
`TaskManager.Handle(ApplicationStartedEvent)` (registering the task into Sonarr's
scheduled-task repository/cache) and `CompletedDownloadService.Import` (routing AniRss
downloads to the manual import). The import step uses Sonarr's own `ManualImportCommand` —
nothing internal is re-implemented.

## What it does

1. **Scheduled task** — registers a new `AniRssCommand` task into Sonarr's own
   scheduled-task repository/cache, so the subscription pass runs every
   `ANIRSS_INTERVAL_MINUTES`.
2. **Subscription pass** (`AniRssCommandExecutor`) — for each subscribed series:
   - loads the subscribe config (file or command payload),
   - resolves the download client (optionally by name),
   - fetches each RSS feed (host-level 500 ms rate limit), extracts the episode number
     from each item title with that feed's `epRegex` entry (falling back to entry 0),
     adds the matching `epOffset` entry, and looks up the Sonarr episode,
   - applies the [skip policy](#skip-policy) — an episode is only pushed when nothing is
     on record for it yet,
   - queues the release with `DownloadService.DownloadReport`, appending
     `#ANIRSS{index}-{urlCrc32}` to the title, which is persisted into the grab history.
   - **Logging** — one summary line per subscription per pass
     (`pushed/upgraded/skipped/unparsed/unmapped`), plus one line per push, upgrade or
     failure. The per-item detail (every skip, every unparsed title, every unmapped
     episode) is `Debug`, so it stays hidden at Sonarr's default log level: a feed is
     re-listed in full on every pass, and per-item lines would drown the summary. Only
     misconfigurations warn — a feed whose regex matched nothing, and a subscription whose
     `rss` is empty (it is skipped).
3. **Import binding** (`AniRssImportBinder`) — when a download completes, Sonarr normally
   re-parses the file/folder names to decide which episode it belongs to and rejects
   anything it can't map. For downloads carrying the `#ANIRSS{index}-{urlCrc32}` marker,
   the patch intercepts `CompletedDownloadService.Import` and hands the download to
   Sonarr's official `ManualImportCommand` with the episodes from the grab history:
   - **Exactly one importable file** → always bound to the grabbed episode(s), whatever
     Sonarr parsed.
   - **Anything else** — nothing importable, or several files — is left to Sonarr's own
     import with a warning. AniRss pushes one episode per release, so a multi-file download
     is not what it pushed and guessing which file is the episode would be worse than
     handing it back.
   - Files rejected for safety reasons (sample, unpacking, free space, dangerous file,
     ...) are left out. What AniRss already answered when it pushed the release is overridden
     instead: identifying the series (Sonarr has only the file name to go on and often fails
     at it), parsing the episode out of the file, matching it against the series, and the
     upgrade checks.
   - Since it goes through `ManualImportService`, the download is completed like a normal
     import: history entry, `EpisodeImportedEvent`, upgrade notifications, and the
     download is removed from the queue. Because manual import bypasses the upgrade
     specification, re-pushing a better source replaces the old file when it is imported.

## Skip policy

An episode is pushed **only when nothing is on record for it yet**. The feed walk only
resolves the inputs; the decision itself lives in one place
(`AniRssCommandExecutor.ShouldSkipEpisodeCore`) and is applied in this order:

1. The episode is already owned by the same feed, or by a **higher-priority** one (lower
   index) → **skip**. A worse source adds nothing, and the duplicate would be rejected by
   the download client. The file state is irrelevant here.
2. No episode file → **skip when any grab is on record, push otherwise**.
3. Episode file present, not grabbed by AniRss → **skip**, never touched.
4. Episode file present, grabbed by a lower-priority feed → **push**: the current,
   higher-priority feed replaces the file through Sonarr's import.

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
    "epRegex": [" (\\d{2,}) ", "第(\\d+)话"],
    "epOffset": [0, 12],
    "rss": [
      "https://feed.example.com/rss?q=show",
      "https://backup.example.com/rss?q=show"
    ]
  }
]
```

| Field | Meaning |
| --- | --- |
| `title` | *Optional.* Human-readable label to make the file easier to read/edit; **not used by any business logic**. |
| `tvdbId` | Series to subscribe, resolved via Sonarr's existing series (TVDB id). |
| `season` | Season number to watch. |
| `epRegex` | *Optional.* Per-feed regexes applied to each RSS item title; the first capture group (or the whole match) is used and its digits are the episode number. A feed with no entry of its own — the array is shorter than `rss`, or the entry is blank — falls back to `epRegex[0]`, and to `` ` (\d{2,}) ` `` when index 0 is unset as well. Unset means the default everywhere. |
| `epOffset` | *Optional.* Per-feed offsets added to the parsed episode number (for series whose numbering starts at a non-1 episode). Indexed and falling back exactly like `epRegex`, defaulting to `0`. |
| `rss` | Feed URLs, **lower index = higher priority**. Used for picking the best source. |

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
subscribe config round-trip, the skip policy (grab history with and without a file, source
resolution, in-flight and upgrade cases) and the import file policy (usable-file filtering,
sample rejection, grabbed-episode binding, multi-file downloads left to Sonarr). Integration
tests drive the real
`CompletedDownloadService.Import` interception with stubbed Sonarr services and verify the
patch targets exist in the running Sonarr build; they need a Sonarr publish dir containing
`Sonarr.Core.dll`, `Sonarr.Common.dll`, `NLog.dll` and `0Harmony.dll` (default
`/workspaces/Sonarr/_output/net6.0/linux-x64/publish`); when absent, those tests are
skipped.
