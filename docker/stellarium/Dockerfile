FROM alpine:3.22@sha256:7c8cb692ae09657cbc4a3f3cbd0e8d5a2690ba38386aaaf252dbb060bf5eb2e6 AS certificates

FROM ubuntu:24.04@sha256:52df9b1ee71626e0088f7d400d5c6b5f7bb916f8f0c82b474289a4ece6cf3faf

ARG DEBIAN_FRONTEND=noninteractive

COPY --from=certificates /etc/ssl/certs/ca-certificates.crt /etc/ssl/certs/ca-certificates.crt

RUN printf '%s\n' \
        'Types: deb' \
        'URIs: https://snapshot.ubuntu.com/ubuntu/20260701T000000Z/' \
        'Suites: noble noble-updates noble-security' \
        'Components: main universe' \
        'Signed-By: /usr/share/keyrings/ubuntu-archive-keyring.gpg' \
        'Check-Valid-Until: no' \
        > /etc/apt/sources.list.d/ubuntu.sources \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        ca-certificates=20240203 \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
        stellarium=23.4-2build3 \
        stellarium-data=23.4-2build3 \
        xvfb=2:21.1.12-1ubuntu1.6 \
        xauth=1:1.1.2-1build1 \
        libgl1-mesa-dri=25.2.8-0ubuntu0.24.04.2 \
        imagemagick=8:6.9.12.98+dfsg1-5.2build2 \
        jq=1.7.1-3ubuntu0.24.04.2 \
        fonts-dejavu-core=2.37-8 \
        fontconfig=2.15.0-1.1ubuntu2 \
    && rm -rf /var/lib/apt/lists/*

ENV TZ=UTC \
    LANG=C.UTF-8 \
    LC_ALL=C.UTF-8 \
    LIBGL_ALWAYS_SOFTWARE=1 \
    GALLIUM_DRIVER=llvmpipe \
    QT_SCALE_FACTOR=1 \
    HOME=/tmp/hvo-home \
    STELLARIUM_RESULTS_ROOT=/results

WORKDIR /opt/hvo

COPY scripts/validate:stellarium scripts/validate:stellarium
COPY scripts/stellarium/hualapai-fisheye.ssc scripts/stellarium/hualapai-fisheye.ssc
COPY tests/fixtures/stellarium/hualapai-fisheye-v1.json tests/fixtures/stellarium/hualapai-fisheye-v1.json
COPY tests/fixtures/stellarium/parse-output.jq tests/fixtures/stellarium/parse-output.jq
COPY tests/fixtures/stellarium/validate.jq tests/fixtures/stellarium/validate.jq
COPY tests/fixtures/stellarium/SIMBAD_ENDPOINTS.md tests/fixtures/stellarium/SIMBAD_ENDPOINTS.md
COPY src/HVO.SkyMonitor.Astronomy/Data/d3-celestial-v0.7.32-topology.tsv src/HVO.SkyMonitor.Astronomy/Data/d3-celestial-v0.7.32-topology.tsv

ENTRYPOINT ["/opt/hvo/scripts/validate:stellarium"]
