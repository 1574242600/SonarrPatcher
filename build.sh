#!/usr/bin/env bash
set -e

# Build script for SonarrPatcher.
# Produces dist/SonarrPatcher.Loader.dll + dist/SonarrPatcher.Patches.*.dll (co-located for deploy).
# Usage:
#   build.sh                    build with default Sonarr publish dir
#   build.sh -s <dir>           override Sonarr publish dir (Sonarr.Common.dll/0Harmony.dll parent)
#   VERSION=0.0.1 build.sh      override the assembly version

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

DEFAULT_SONARR_DIR="/workspaces/Sonarr/_output/net6.0/linux-x64/publish"

SONARR_DIR="$DEFAULT_SONARR_DIR"
while [[ $# -gt 0 ]]; do
    case "$1" in
        -s|--sonarr-dir)
            SONARR_DIR="$2"
            shift 2
            ;;
        *)
            echo "Unknown argument: $1" >&2
            exit 2
            ;;
    esac
done

echo "==> Building SonarrPatcher.sln (Release)"
EXTRA=()
if [[ -n "${VERSION:-}" ]]; then
    echo "    Version: $VERSION"
    EXTRA+=("-p:Version=$VERSION")
fi
EXTRA+=("-p:SonarrDir=$SONARR_DIR")
dotnet build SonarrPatcher.sln -c Release -m --nologo "${EXTRA[@]}"

DIST="$ROOT/dist"
mkdir -p "$DIST"

cp "$ROOT/SonarrPatcher.Loader/bin/Release/net6.0/SonarrPatcher.Loader.dll" "$DIST/"
cp "$ROOT/SonarrPatcher.Patches/SkyHook/bin/Release/net6.0/SonarrPatcher.Patches.SkyHook.dll" "$DIST/"
cp "$ROOT/SonarrPatcher.Patches/NameTruncate/bin/Release/net6.0/SonarrPatcher.Patches.NameTruncate.dll" "$DIST/"
cp "$ROOT/SonarrPatcher.Patches/XemAliases/bin/Release/net6.0/SonarrPatcher.Patches.XemAliases.dll" "$DIST/"
cp "$ROOT/SonarrPatcher.Patches/AniRss/bin/Release/net6.0/SonarrPatcher.Patches.AniRss.dll" "$DIST/"

echo "OK: $DIST"
ls -1 "$DIST"
