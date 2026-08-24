# XemAliases Patch

Two independent, individually toggleable changes to how Sonarr sources and uses scene aliases:

1. **Redirect the XEM `allNames` request** to a custom URL (`XEM_ALLNAMES_URL`), leaving `/havemap` and `/all` untouched.
2. **Relax Sonarr's alias "English-only" filter** so non-ASCII (CJK, accented) aliases are usable for search, while keeping a 255-character length ceiling.

## Feature A: Redirect `allNames`

Sonarr builds its `scene_mapping` table (the series aliases shown in the UI and used as search terms) from the bulk dump at `https://thexem.info/map/allNames?origin=tvdb&seasonNumbers=true`, fetched in `NzbDrone.Core.DataAugmentation.Xem.XemProxy.GetSceneTvdbNames`. This patch replaces that method so the request is issued against a custom URL instead.

### Behavior

- Only the `allNames` request is affected — `GetXemSeriesIds` (`/havemap`) and `GetSceneTvdbMappings` (`/all`) keep their original URLs.
- The patch appends `origin=tvdb` and `seasonNumbers=true` to whatever URL you configure, so the custom endpoint should return the same JSON shape Sonarr expects (`{"result":"success","data":{<tvdbId>:[{<title>:<season>},...]}}`).
- If the redirect fails (network error, bad response, missing type), the patch logs the error and falls back to the original thexem.info request.

### Environment variables

- `XEM_ALLNAMES_URL` — full URL to the `allNames` endpoint, e.g. `https://mirror.example.net/map/allNames`. Unset/empty → redirect disabled and Feature A is skipped.

## Feature B: Allow non-English aliases

Sonarr's `SceneMappingService.GetSceneNames` filters aliases through `IsEnglish` (`title.All(c => c <= 255)`), so non-ASCII aliases never become search terms. This patch replaces `IsEnglish` with a **character-count** check instead of a per-character encoding check:

```csharp
title.Length <= 255
```

### Behavior

- CJK / accented / other non-ASCII aliases (e.g. `無職転生`, `境界線上のホライゾン`) are now allowed as search terms.
- A 255-character ceiling is still enforced, so absurdly long strings are rejected.
- Affects the alias-based search path (`GetSceneNames` → `SceneTitles`), used by Anime and Daily series searches. Standard-series single/season searches already used non-English aliases and are unchanged.
- Database columns are SQLite `TEXT` (unlimited), so no migration is needed.

### Environment variables

- `DISABLE_NONENGLISH_ALIASES_PATCH` — set to `1` to keep Sonarr's original ASCII-only filter.

## Usage

Run it through the Loader (recommended): set `DOTNET_STARTUP_HOOKS` to `SonarrPatcher.Loader.dll` and the loader auto-discovers this patch. Or run standalone: point `DOTNET_STARTUP_HOOKS` directly at `SonarrPatcher.Patches.XemAliases.dll` — its `StartupHook.Initialize()` self-bootstraps `0Harmony.dll` and `Sonarr.Common.dll` and applies the patch.

With no environment variables set, Feature B is on by default and Feature A is off.

### Docker example

```yaml
services:
  sonarr:
    image: lscr.io/linuxserver/sonarr:latest
    environment:
      - DOTNET_STARTUP_HOOKS=/custom/SonarrPatcher.Loader.dll
      - XEM_ALLNAMES_URL=https://mirror.example.net/map/allNames
    volumes:
      - /path/to/dist:/custom:ro
      - ./config:/config
```

> Aliases are refreshed on series add/import and manual series refresh, so the redirect takes effect the next time Sonarr updates its scene mappings.

## Pairing with anime-title-lists

Feature A and Feature B work together to consume the curated anime data from [anime-title-lists](https://github.com/1574242600/anime-title-lists): its [`build-xem-all-names.ts`](https://github.com/1574242600/anime-title-lists/blob/main/scripts/build-xem-all-names.ts) generates an `allNames`-formatted `dist/xem/all-names-{lang}.json` (keyed by TVDB id, each title marked with season `-1`) from AniDB-based title lists, so pointing `XEM_ALLNAMES_URL` at a self-hosted copy of that file replaces XEM's sparse anime aliases with comprehensive romaji/official/`ja`/`en`/`{lang}` titles, and — together with Feature B, which lifts the ASCII-only filter — makes Japanese/Chinese aliases actually usable as search terms; because all generated titles are global (`-1`), they match every season of a series instead of XEM's season-bound aliases, at the cost of per-season precision.

## Tests

Unit tests cover the `IsEnglish` replacement logic. Integration tests patch the real `NzbDrone.Core.DataAugmentation.Scene.SceneMappingService.IsEnglish` and `NzbDrone.Core.DataAugmentation.Xem.XemProxy.GetSceneTvdbNames`, and verify the built `allNames` URL; they need a Sonarr publish dir containing `Sonarr.Core.dll`, `Sonarr.Common.dll`, `NLog.dll` and `0Harmony.dll` (default `/workspaces/Sonarr/_output/net6.0/linux-x64/publish`); when absent, those tests are skipped.
