# build-only dev container for the Sonarr SkyHook patch (backend only)
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

# builder stage: shallow clone of the Sonarr main branch, backend build only
FROM base AS builder

RUN git clone --depth=1 --branch=main \
        https://github.com/Sonarr/Sonarr.git /tmp/Sonarr

# build the linux-x64 backend (net6.0) only, mirroring Sonarr's CI build
# action; no frontend (yarn/webpack) is built
WORKDIR /tmp/Sonarr

RUN dotnet msbuild -restore src/Sonarr.sln \
        -p:Configuration=Release \
        -p:Platform=Posix \
        -p:RuntimeIdentifiers=linux-x64 \
        -p:SelfContained=true \
        -p:EnableWindowsTargeting=true \
        -t:PublishAllRids

# development image: Sonarr source and build products under /workspaces/Sonarr
FROM base

COPY --from=builder /tmp/Sonarr /workspaces/Sonarr