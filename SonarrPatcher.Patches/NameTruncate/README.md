# NameTruncate Patch

Fixes Sonarr's `{name:N}` naming-format truncation for non-English text. Sonarr's original `FileNameBuilder.Truncate` mixes a UTF-16 `string.Length` gate with a UTF-8 byte-based cut, so a limit like `{Series Title:30}` on CJK, emoji, or combining characters is either silently ignored (≤30 code units) or truncated to roughly a third of the requested length (30 bytes ≈ 9 CJK characters). This patch replaces the method so `N` means "N characters" (Unicode text elements / grapheme clusters), cutting on grapheme boundaries without splitting surrogate pairs, combining marks, or ZWJ emoji sequences.

## Scope

This patch only replaces `NzbDrone.Core.Organizer.FileNameBuilder.Truncate`. It covers the name/series/group tokens that route through it: `{Series Title}`, `{Series CleanTitle}`, `{Series TitleYear}`, `{Release Group}`, and friends.

**Episode title truncation is NOT handled.** `{Episode Title:N}` and `{Episode CleanTitle:N}` go through `FileNameBuilder.GetEpisodeTitle` (a separate method that combines the format limit with the filesystem byte budget), so they keep Sonarr's original byte-based, script-unaware truncation. Only episode *file/series* names benefit from this patch; episode *title* limits are left unmodified.

## Usage

Run it through the Loader (recommended): set `DOTNET_STARTUP_HOOKS` to `SonarrPatcher.Loader.dll` and the loader auto-discovers this patch. Or run standalone: point `DOTNET_STARTUP_HOOKS` directly at `SonarrPatcher.Patches.NameTruncate.dll` — its `StartupHook.Initialize()` self-bootstraps `0Harmony.dll` and `Sonarr.Core.dll` and applies the patch.

The patch is enabled by default and requires no configuration.

### Behavior

- `{name:30}` — up to 30 characters (grapheme clusters); longer names keep 27 characters plus `...`.
- `{name:-30}` — truncates from the front (keeps the last 27 characters, `...` first).
- `:0` or a non-numeric value — name returned unchanged (same as Sonarr's original behaviour).
- Numeric zero-padding like `{Season:00}` / `{Episode:00}` is untouched.

### Environment variables

- `DISABLE_NAMETRUNCATE_PATCH` — set to `1` to skip the patch and keep Sonarr's original truncation.

### Docker example

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

## Tests

Unit tests cover the replacement logic directly. Integration tests patch the real `NzbDrone.Core.Organizer.FileNameBuilder.Truncate` and need a Sonarr publish dir containing `Sonarr.Core.dll` and `0Harmony.dll` (default `/workspaces/Sonarr/_output/net6.0/linux-x64/publish`); when absent, those tests are skipped.
