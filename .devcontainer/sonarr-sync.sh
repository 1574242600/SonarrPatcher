#!/usr/bin/env bash
# Sync the development-container Sonarr source to the latest GitHub release
# (the same source CI builds) and publish it with AssemblyVersion=0.0.0.0.
#
# Idempotent: when the checkout is already at the latest release and the
# publish output exists, it just prints the version. Otherwise it clones the
# source if missing, checks out the latest tag, builds, and prints the version.
#
# Usage: sonarr-sync.sh            (uses defaults below)
#        SONARR_SRC=/path sonarr-sync.sh
set -euo pipefail

# GitHub sometimes breaks under HTTP/2 from containers/proxies
# ("error: RPC failed; curl 16 Error in the HTTP2 framing layer").
export GIT_CONFIG_COUNT=1
export GIT_CONFIG_KEY_0=http.version
export GIT_CONFIG_VALUE_0=HTTP/1.1

SONARR_SRC="${SONARR_SRC:-/workspaces/Sonarr}"
PUBLISH_DIR="${PUBLISH_DIR:-$SONARR_SRC/_output/net6.0/linux-x64/publish}"

if ! command -v curl >/dev/null || ! command -v jq >/dev/null; then
    echo "error: curl and jq are required" >&2
    exit 1
fi

asm_version() {
    # Prints the AssemblyVersion of a managed DLL (pwsh), or "unknown".
    if command -v pwsh >/dev/null; then
        pwsh -NoProfile -Command "[System.Reflection.AssemblyName]::GetAssemblyName('$1').Version.ToString()" 2>/dev/null || echo unknown
    else
        echo unknown
    fi
}

echo "==> Resolving latest Sonarr release"
LATEST_TAG=$(curl -sS https://api.github.com/repos/Sonarr/Sonarr/releases/latest | jq -r .tag_name)
if [[ -z "$LATEST_TAG" || "$LATEST_TAG" == "null" ]]; then
    echo "error: could not resolve the latest release tag (GitHub API rate limited?)" >&2
    exit 1
fi
echo "    latest release: $LATEST_TAG"

echo "==> Syncing $SONARR_SRC"
if [[ ! -d "$SONARR_SRC/.git" ]]; then
    if [[ -e "$SONARR_SRC" && ! -d "$SONARR_SRC" ]]; then
        echo "error: $SONARR_SRC exists and is not a directory" >&2
        exit 1
    fi
    echo "    source missing, cloning"
    mkdir -p "$(dirname "$SONARR_SRC")"
    git clone --depth=1 https://github.com/Sonarr/Sonarr.git "$SONARR_SRC"
fi

git -C "$SONARR_SRC" fetch --tags --quiet --force

CURRENT_HEAD=$(git -C "$SONARR_SRC" rev-parse HEAD)
LATEST_COMMIT=$(git -C "$SONARR_SRC" rev-parse "$LATEST_TAG^{commit}")
DIRTY=$(git -C "$SONARR_SRC" status --porcelain | head -1 || true)

if [[ "$CURRENT_HEAD" == "$LATEST_COMMIT" ]]; then
    echo "    already at latest ($LATEST_TAG)"
    STATUS="already up-to-date"
else
    if [[ -n "$DIRTY" ]]; then
        echo "error: $SONARR_SRC has uncommitted changes; commit or stash them first:" >&2
        git -C "$SONARR_SRC" status --short >&2
        exit 1
    fi
    BEFORE=$(git -C "$SONARR_SRC" describe --tags --always 2>/dev/null || echo unknown)
    git -C "$SONARR_SRC" checkout --quiet "$LATEST_TAG"
    echo "    updated: $BEFORE -> $LATEST_TAG"
    STATUS="updated to $LATEST_TAG"
fi

# Skip the build only when the checkout is already at the latest release AND
# the existing publish output was produced with AssemblyVersion=0.0.0.0 (a
# stale build from an earlier source/flag combination must be rebuilt).
CURRENT_VER="$(asm_version "$PUBLISH_DIR/Sonarr.Core.dll")"
if [[ -f "$PUBLISH_DIR/Sonarr.Core.dll" && "$CURRENT_HEAD" == "$LATEST_COMMIT" && "$CURRENT_VER" == "0.0.0.0" ]]; then
    echo "    publish output already exists (AssemblyVersion=0.0.0.0), skipping build"
else
    echo "==> Publishing Sonarr.Console (net6.0, linux-x64, AssemblyVersion=0.0.0.0)"
    dotnet publish "$SONARR_SRC/src/NzbDrone.Console/Sonarr.Console.csproj" \
        -c Release -f net6.0 -r linux-x64 --self-contained false \
        -p:EnableAnalyzers=false -p:AssemblyVersion=0.0.0.0 \
        -o "$PUBLISH_DIR"
fi

echo "==> Result"
echo "    Sonarr: $LATEST_TAG ($STATUS)"
echo "    publish: $PUBLISH_DIR"
V="$(asm_version "$PUBLISH_DIR/Sonarr.Core.dll")"
echo "    Sonarr.Core.dll AssemblyVersion: $V"
