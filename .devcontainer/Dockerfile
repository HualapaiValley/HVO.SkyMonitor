FROM mcr.microsoft.com/devcontainers/dotnet:1-10.0

# ── Fix the Yarn apt source that ships broken in the base image ──
# The base image includes an apt source for Yarn that can cause
# "Release file is not valid yet" errors during apt-get update.
# Removing it avoids transient CI/build failures.
RUN rm -f /etc/apt/sources.list.d/yarn.list \
    && apt-get update \
    && apt-get clean && rm -rf /var/lib/apt/lists/* /tmp/* /var/tmp/*
