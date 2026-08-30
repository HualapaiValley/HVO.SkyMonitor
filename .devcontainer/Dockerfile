# Pin to Ubuntu LTS; the .NET SDK is supplied by the devcontainer feature.
FROM mcr.microsoft.com/devcontainers/base:3.0.7-ubuntu24.04@sha256:456e33716a8570448b7deca1ffd98fc337adc7b446aceeadb8beaf8d660345b4

ARG TARGETARCH
ARG TAILSCALE_VERSION=1.102.3
ARG OPENCODE_VERSION=1.18.25

ENV NUGET_XMLDOC_MODE=
ENV PATH="/home/vscode/.dotnet/tools:${PATH}"

USER root
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# Tailscale packages are authenticated by its pinned repository key. OpenCode
# release archives are pinned and checked against the immutable GitHub digest.
# hadolint ignore=DL3008
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    jq \
    openssl \
    ripgrep \
    shellcheck \
    sqlite3 \
    tar \
    tmux \
    && curl -fsSL --retry 3 \
    https://pkgs.tailscale.com/stable/ubuntu/noble.noarmor.gpg \
    -o /tmp/tailscale-archive-keyring.gpg \
    && echo "3e03dacf222698c60b8e2f990b809ca1b3e104de127767864284e6c228f1fb39  /tmp/tailscale-archive-keyring.gpg" \
    | sha256sum -c - \
    && install -m 0644 /tmp/tailscale-archive-keyring.gpg \
    /usr/share/keyrings/tailscale-archive-keyring.gpg \
    && printf '%s\n' \
    'deb [signed-by=/usr/share/keyrings/tailscale-archive-keyring.gpg] https://pkgs.tailscale.com/stable/ubuntu noble main' \
    > /etc/apt/sources.list.d/tailscale.list \
    && apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    "tailscale=${TAILSCALE_VERSION}" \
    && architecture="${TARGETARCH:-$(dpkg --print-architecture)}" \
    && case "$architecture" in \
    amd64) \
    opencode_archive="opencode-linux-x64-baseline.tar.gz"; \
    opencode_sha256="ccd10586611b598b1eaed7c05cfbcbc68e3ec09e736b360da09b1d615d922968" \
    ;; \
    arm64) \
    opencode_archive="opencode-linux-arm64.tar.gz"; \
    opencode_sha256="35ef77897425e41b5183a2c21ac4fb1d4d944d82a94e3c920f57b5490af11ac5" \
    ;; \
    *) \
    echo "Unsupported OpenCode architecture: $architecture" >&2; \
    exit 1 \
    ;; \
    esac \
    && curl -fL --retry 3 \
    "https://github.com/anomalyco/opencode/releases/download/v${OPENCODE_VERSION}/${opencode_archive}" \
    -o "/tmp/${opencode_archive}" \
    && echo "${opencode_sha256}  /tmp/${opencode_archive}" | sha256sum -c - \
    && tar -xzf "/tmp/${opencode_archive}" -C /usr/local/bin opencode \
    && chmod 0755 /usr/local/bin/opencode \
    && mkdir -p \
    /home/vscode/.config/opencode \
    /home/vscode/.local/share/opencode \
    /var/lib/tailscale \
    /var/run/tailscale \
    && chown -R vscode:vscode \
    /home/vscode/.config/opencode \
    /home/vscode/.local \
    && chmod 0700 \
    /home/vscode/.config/opencode \
    /home/vscode/.local/share/opencode \
    && rm -rf /tmp/opencode-*.tar.gz /tmp/tailscale-archive-keyring.gpg /var/lib/apt/lists/*

USER vscode
