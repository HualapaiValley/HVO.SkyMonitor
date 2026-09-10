# Pin to Ubuntu LTS; the .NET SDK is supplied by the devcontainer feature.
FROM mcr.microsoft.com/devcontainers/base:3.0.7-ubuntu24.04@sha256:456e33716a8570448b7deca1ffd98fc337adc7b446aceeadb8beaf8d660345b4

ENV NUGET_XMLDOC_MODE=
ENV PATH="/home/vscode/.dotnet/tools:${PATH}"

USER root
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

# hadolint ignore=DL3008
RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    python3 \
    python3-venv \
    jq \
    openssl \
    ripgrep \
    shellcheck \
    sqlite3 \
    tar \
    && rm -rf /var/lib/apt/lists/*

USER vscode
