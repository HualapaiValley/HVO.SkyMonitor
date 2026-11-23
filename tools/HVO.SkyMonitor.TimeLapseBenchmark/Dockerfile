FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble@sha256:0bfa6494dead26049c1e0d6c9dc890be79259cb7300f5f23ae1df1cdac3da7df
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg python3 time util-linux libfontconfig1 && rm -rf /var/lib/apt/lists/*
ENV DOTNET_EnableDiagnostics=0 DOTNET_PROCESSOR_COUNT=2
WORKDIR /data
