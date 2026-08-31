# dev container for SonarrPatcher (backend only).
# Sonarr itself is not built here: on container start .devcontainer/sonarr-sync.sh
# (postStartCommand) clones it (if missing), syncs to the latest release and
# publishes the backend with AssemblyVersion=0.0.0.0 — the same source CI builds.
FROM mcr.microsoft.com/devcontainers/dotnet:6.0

RUN rm -f /etc/apt/sources.list.d/yarn.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        jq \
        unzip \
    && rm -rf /var/lib/apt/lists/*
