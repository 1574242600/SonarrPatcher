#!/usr/bin/env bash
set -e

# Test script for SonarrPatcher: builds and runs the xunit test suite.
# Usage:
#   test.sh                 build + run with default Sonarr publish dir
#   test.sh -s <dir>        override Sonarr publish dir (Sonarr.Common.dll/0Harmony.dll parent)

ROOT="$(cd "$(dirname "$0")" && pwd)"
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

cd "$ROOT"

PROP_SONARR="-p:SonarrDir=$SONARR_DIR"

echo "==> Running tests (xunit, net6.0)"
dotnet test SonarrPatcher.sln $PROP_SONARR
