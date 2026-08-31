# build-only dev container for SonarrPatcher (backend only)
FROM mcr.microsoft.com/devcontainers/dotnet:6.0 AS base

RUN rm -f /etc/apt/sources.list.d/yarn.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates \
        curl \
        git \
        jq \
        unzip \
    && rm -rf /var/lib/apt/lists/*

# builder stage: shallow clone of the Sonarr repository. The actual sync to the
# latest release and the backend publish (linux-x64, AssemblyVersion=0.0.0.0)
# happen at container start via .devcontainer/sonarr-sync.sh (postStartCommand),
# which keeps the dev source identical to what CI builds.
FROM base AS builder

RUN git clone --depth=1 https://github.com/Sonarr/Sonarr.git /tmp/Sonarr

# development image: Sonarr source tree under /workspaces/Sonarr
FROM base

COPY --from=builder /tmp/Sonarr /workspaces/Sonarr
