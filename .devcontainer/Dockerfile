# Pin to Ubuntu LTS; the .NET SDK is supplied by the devcontainer feature.
FROM mcr.microsoft.com/devcontainers/base:3.0.10-ubuntu24.04@sha256:21e9e3275736cb3bc33a2904b279a2ab292c51fedd53c2963463627d4586c3e6

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
