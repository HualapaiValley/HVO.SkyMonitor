FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:aac50ceef10391e78a899e3d2ccd00bbafa2dcb56ccf4586cbccf00edb84fc21
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg python3 time util-linux libfontconfig1 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_EnableDiagnostics=0 DOTNET_PROCESSOR_COUNT=2
WORKDIR /data
