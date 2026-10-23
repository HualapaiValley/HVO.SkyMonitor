#!/usr/bin/env bash
# Typed partial-prepare scenario owner (#1588). The original shell assertions were
# retired only after the retained complete source/input/result comparison passed.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard adapter sourced by scripts/test:deploy-environment.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi
env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  MSBUILDDISABLENODEREUSE=1 dotnet test \
  "$REPO_ROOT/tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj" \
  --configuration Release --filter 'FullyQualifiedName~PartialPrepareRecoveryTests' \
  -nodeReuse:false -p:UseSharedCompilation=false
