# Custom Parse Rules Patch

Adds user-defined regex rules for parsing indexer release titles. A Harmony prefix on
`Parser.ParseTitle` rewrites a matching title into a standard form Sonarr's built-in
parser understands (e.g. `[subgroup] series S01E01 tokens`), then hands it back to
Sonarr's own parser. Non-matching titles are never modified.

## How it works

For a matching rule the raw release title is rewritten as:

```
[subgroup] series-title S{season}E{episode} <tail>
```

- `subgroup` → the release group (`[...]` prefix; omitted when the group is absent).
- `season`/`episode` → converted to standard `S01E01` (or `S02` for a full-season
  release, or absolute `01` with `useAbsolute`).
- `<tail>`:
  - **No `language`/`quality` in the rule** → the original title tail (after the
    season/episode tokens) is preserved, so Sonarr auto-detects languages/quality.
  - **`language` and/or `quality` are specified** → the original tail is **dropped** and
    replaced with synthetic tokens the built-in `LanguageParser`/`QualityParser`
    recognise, forcing those values (e.g. `简繁 eng 1080p WEB-DL`).

## Configuration

Rules live in a JSON file. Path resolution:

1. `CUSTOM_PARSE_RULES_FILE` environment variable, or
2. `<patch dll directory>/config/custom-parse-rules.json` (default)

If the file is missing or contains no enabled rules the patch does nothing and Sonarr
runs completely unmodified. A worked example lives in
`SonarrPatcher.Tests/custom-parse-rules.example.json`.

### Rule schema

```json
[
  {
    "id": "rules.example.fansub",
    "enabled": true,
    "pattern": "^\\[(?<subgroup>示例字幕组)\\](?<title>.*?)[ ._-]S(?<season>\\d{1,2})E(?<episode>\\d{1,3})(?<tokens>.*)$",
    "language": ["简繁", "eng"],
    "quality": { "resolution": 1080, "source": "WEB-DL" },
    "useAbsolute": false
  }
]
```

- `id` (required) — unique rule identifier, used only for logging/dedup.
- `enabled` (optional, default `true`) — set `false` to disable a rule.
- `pattern` (required) — .NET regex. Recognised named groups:
  - `title` (required) — series title (`.`/`_` are converted to spaces).
  - `season` — season number; accepts Arabic digits (incl. full-width), Roman numerals
    (1-99, e.g. `II`, `XII`, `XC`) and Chinese numerals (1-99, single characters
    一二三四五六七八九 / 壹贰叁肆伍陆柒捌玖 and 十/拾 tens compounds like 十, 二十三).
  - `episode` — episode number(s); Arabic digits only. Multiple captures are supported.
  - `absoluteepisode` — absolute episode number(s); Arabic digits only.
  - `subgroup` — release group (overrides automatic detection).
  - `tokens` — release tokens; when absent they are derived from the title tail after
    the last matched season/episode token (used for automatic language detection).
- `language` (optional) — force languages. A list of **raw tokens** the built-in
  `LanguageParser` recognises, e.g. `["简繁", "eng"]`, `["Japanese"]`, `["FR"]`. When
  set, the original tail is dropped and these tokens are appended.
- `quality` (optional) — force quality via `resolution` (480/576/720/1080/2160) and
  `source`, a **raw token** the built-in `QualityParser` SourceRegex recognises (e.g.
  `WEB-DL`, `WEBRip`, `BluRay`, `HDTV`, `DVD`, `RawHD`, `Remux`). When set, the original
  tail is dropped and a `<resolution>p <source>` token (e.g. `1080p WEB-DL`) is appended.
- `useAbsolute` (optional, default `false`) — when true, `episode` captures are treated
  as absolute episode numbers.

Roman numerals and Chinese digits apply to `season` only (both up to the tens place,
1-99); `episode`/`absoluteepisode` accept Arabic digits only.

## Usage

Run it through the Loader (recommended): set `DOTNET_STARTUP_HOOKS` to
`SonarrPatcher.Loader.dll` and the loader auto-discovers this patch. Or run standalone:
point `DOTNET_STARTUP_HOOKS` directly at `SonarrPatcher.Patches.CustomParseRules.dll` —
its `StartupHook.Initialize()` self-bootstraps `0Harmony.dll` and applies the patch.

### Environment variables

- `CUSTOM_PARSE_RULES_FILE` — path to the rules JSON file (overrides the default
  `<patch dll directory>/config/custom-parse-rules.json`).

### Docker example

```yaml
services:
  sonarr:
    image: lscr.io/linuxserver/sonarr:latest
    environment:
      - DOTNET_STARTUP_HOOKS=/custom/SonarrPatcher.Loader.dll
      - CUSTOM_PARSE_RULES_FILE=/config/custom-parse-rules.json
    volumes:
      - /path/to/dist:/custom:ro
      - ./config:/config
```

## Tests

The CustomParseRules integration tests need a Sonarr publish dir containing
`0Harmony.dll` (default `/workspaces/Sonarr/_output/net6.0/linux-x64/publish`); when
absent, those tests are skipped. Unit tests (config parsing, numeral conversion, rule
compilation) run without Sonarr.
