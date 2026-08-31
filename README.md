# SonarrPatcher

.NET startup hooks that load Harmony-based patches into Sonarr.

Sonarr (v4 main branch) already ships `0Harmony.dll` as part of its runtime patches, so no injector is needed — the hook DLLs are loaded by the .NET runtime through `DOTNET_STARTUP_HOOKS` before `Main` runs.

## How it works

1. `DOTNET_STARTUP_HOOKS` points at `SonarrPatcher.Loader.dll`; the runtime calls its `StartupHook.Initialize()` before Sonarr's `Main`.
2. `Loader.LoadAll()` scans its own directory for every other `SonarrPatcher*.dll`, loads it into the default `AssemblyLoadContext`, and invokes its `StartupHook.Initialize()` — the same entry point used when a patch runs standalone. `EnsureLoaded` is idempotent, so the Loader pre-loading `0Harmony.dll` from the application base directory first is just an optimization, not a requirement.

A patch can also run standalone: point `DOTNET_STARTUP_HOOKS` directly at the patch DLL — its `StartupHook.Initialize()` self-bootstraps dependencies and applies the patch.

## Patches

| Name | Description | README |
| --- | --- | --- |
| SkyHookPatch | Redirects Sonarr's `SkyHookTvdb` metadata requests to a custom host and language | [SkyHook/README.md](SonarrPatcher.Patches/SkyHook/README.md) |
| NameTruncatePatch | Makes `{name:N}` truncate by characters (grapheme clusters) instead of bytes, fixing non-English (CJK/emoji/combining) names | [NameTruncate/README.md](SonarrPatcher.Patches/NameTruncate/README.md) |
| XemAliasesPatch | Redirects the XEM `allNames` scene-mapping request to a custom URL and relaxes the alias English-only filter (allows CJK, keeps a 255-char ceiling) | [XemAliases/README.md](SonarrPatcher.Patches/XemAliases/README.md) |
| AniRssPatch | Ani-rss style subscription downloader: watches your own RSS feeds with a per-feed episode regex, pushes releases to the download client, and forces the import to use the grabbed episode via Sonarr's own `ManualImportCommand` | [AniRss/README.md](SonarrPatcher.Patches/AniRss/README.md) |

## Deploy

Mount the built DLLs (all in the same directory) and set the env vars required by the patches you want to enable (see [Patches](#patches)). No injection, no modified Sonarr files.

```yaml
services:
  sonarr:
    image: lscr.io/linuxserver/sonarr:latest
    environment:
      - DOTNET_STARTUP_HOOKS=/custom/SonarrPatcher.Loader.dll
    volumes:
      - /path/to/dist:/custom:ro
      - ./config:/config
```

- `DOTNET_STARTUP_HOOKS` — path to the mounted `SonarrPatcher.Loader.dll`; it loads all co-located `SonarrPatcher.Patches.*.dll` (use `:` to list multiple hooks). For standalone, point it directly at a patch DLL instead.

> Hooks run on the same managed thread before `Main` and swallow all errors, so Sonarr startup is never blocked.

## Development

### Build & Test

```sh
./build.sh                            # dist/SonarrPatcher.Loader.dll + dist/SonarrPatcher.Patches.*.dll (Release, net6.0)
./test.sh                             # restore/build/run the xunit suite
./test.sh -s /path/to/sonarr-publish  # run against a different Sonarr publish dir
```
