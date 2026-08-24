#!/usr/bin/env bash
set -e

# Build script for SonarrPatcher.
# Produces dist/SonarrPatcher.Loader.dll + dist/SonarrPatcher.Patches.SkyHook.dll (co-located for deploy).
# Usage:
#   build.sh                build with default version (Directory.Build.props)
#   VERSION=0.0.1 build.sh  override the assembly version

ROOT="$(cd "$(dirname "$0")" && pwd)"
cd "$ROOT"

echo "==> Building SonarrPatcher.sln (Release)"
EXTRA=()
if [[ -n "${VERSION:-}" ]]; then
    echo "    Version: $VERSION"
    EXTRA+=("-p:Version=$VERSION")
fi
dotnet build SonarrPatcher.sln -c Release -m --nologo "${EXTRA[@]}"

DIST="$ROOT/dist"
mkdir -p "$DIST"

cp "$ROOT/SonarrPatcher.Loader/bin/Release/net6.0/SonarrPatcher.Loader.dll" "$DIST/"
cp "$ROOT/SonarrPatcher.Patches/SkyHook/bin/Release/net6.0/SonarrPatcher.Patches.SkyHook.dll" "$DIST/"
cp "$ROOT/SonarrPatcher.Patches/NameTruncate/bin/Release/net6.0/SonarrPatcher.Patches.NameTruncate.dll" "$DIST/"
cp "$ROOT/SonarrPatcher.Patches/XemAliases/bin/Release/net6.0/SonarrPatcher.Patches.XemAliases.dll" "$DIST/"

echo "OK: $DIST"
ls -1 "$DIST"
