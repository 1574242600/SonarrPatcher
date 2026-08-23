# SkyHook Patch

Redirects Sonarr's `SkyHookTvdb` metadata requests to a custom host and language.

When `SKYHOOK_HOST` is set, the patch Harmony-patches the `NzbDrone.Common.Cloud.SonarrCloudRequestBuilder` constructor so its `SkyHookTvdb` factory becomes `{SKYHOOK_HOST}/v1/tvdb/{route}/{language}/` with the `language` segment set to `{SKYHOOK_LANG}` (default `eng`). If `SKYHOOK_HOST` already includes a scheme (`http://` or `https://`) it is used as-is, otherwise `http://` is added automatically. If `SKYHOOK_HOST` is unset, the patch does nothing and Sonarr runs completely unmodified.

## Usage

Run it through the Loader (recommended): set `DOTNET_STARTUP_HOOKS` to `SonarrPatcher.Loader.dll` and the loader auto-discovers this patch. Or run standalone: point `DOTNET_STARTUP_HOOKS` directly at `SonarrPatcher.Patches.SkyHook.dll` — its `StartupHook.Initialize()` self-bootstraps `0Harmony.dll` and applies the patch.

### Environment variables

- `SKYHOOK_HOST` — host that replaces `skyhook.sonarr.tv`. May include a scheme (`https://` or `http://`); without one, `http://` is added automatically. Unset/empty → no patch.
- `SKYHOOK_LANG` — the `{language}` segment value (default `eng`).

### Docker example

```yaml
services:
  sonarr:
    image: lscr.io/linuxserver/sonarr:latest
    environment:
      - DOTNET_STARTUP_HOOKS=/custom/SonarrPatcher.Loader.dll
      - SKYHOOK_HOST=skyhook.example.com
      - SKYHOOK_LANG=eng
    volumes:
      - /path/to/dist:/custom:ro
      - ./config:/config
```

## Tests

The SkyHook integration tests need a Sonarr publish dir containing `0Harmony.dll` (default `/workspaces/Sonarr/_output/net6.0/linux-x64/publish`); when absent, those tests are skipped.
