FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble@sha256:de0ee358cdd672f7017a003bed21e9ac9ed48a27c922a59b18175b6e82fa7fee
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg python3 time util-linux libfontconfig1 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_EnableDiagnostics=0 DOTNET_PROCESSOR_COUNT=2
WORKDIR /data
