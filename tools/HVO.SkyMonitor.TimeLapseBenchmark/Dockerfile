FROM mcr.microsoft.com/dotnet/aspnet:10.0.11-noble
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg python3 time util-linux libfontconfig1 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_EnableDiagnostics=0 DOTNET_PROCESSOR_COUNT=2
WORKDIR /data
