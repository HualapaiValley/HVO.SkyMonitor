FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble@sha256:d5633778c754e89a386882eddf8dbee6942447a8a4b8267c19cf1e1158d0b0ac
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg python3 time util-linux libfontconfig1 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_EnableDiagnostics=0 DOTNET_PROCESSOR_COUNT=2
WORKDIR /data
