# Pin to Ubuntu LTS; the .NET SDK is supplied by the devcontainer feature.
FROM mcr.microsoft.com/devcontainers/base:3.0.13-ubuntu24.04@sha256:48be4adec1c1ecbe030064700aef85382f5205786a8f82937fc44a7473717eae

ENV NUGET_XMLDOC_MODE=
ENV PATH="/home/vscode/.dotnet/tools:${PATH}"

USER root
SHELL ["/bin/bash", "-o", "pipefail", "-c"]

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
    && rm -rf /var/lib/apt/lists/*

USER vscode
