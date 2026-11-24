# Pin to Ubuntu LTS; the .NET SDK is supplied by the devcontainer feature.
FROM mcr.microsoft.com/devcontainers/base:3.0.14-ubuntu24.04@sha256:88171bb581e19bbb45db77a16a3a5475e2f9ddce025b044c2e5a69a6a1e24fa1

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
