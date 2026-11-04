#!/usr/bin/env bash
# Typed preflight scenario owner (#1593); complete original receipts precede this cutover.
if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard adapter sourced by scripts/test:deploy-environment.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi
preflight_filter='TestCategory=Unit&('
preflight_filter+='FullyQualifiedName~HVO.SkyMonitor.Deployment.Cli.Tests.PreflightTests|'
preflight_filter+='FullyQualifiedName~HVO.SkyMonitor.Deployment.Cli.Tests.PreflightInventoryTests|'
preflight_filter+='FullyQualifiedName~HVO.SkyMonitor.Deployment.Cli.Tests.PreflightAncillaryTests|'
preflight_filter+='FullyQualifiedName~HVO.SkyMonitor.Deployment.Cli.Tests.PreflightSafetyTests|'
preflight_filter+='FullyQualifiedName~HVO.SkyMonitor.Deployment.Cli.Tests.PreflightBoundaryTests|'
preflight_filter+='FullyQualifiedName~HVO.SkyMonitor.Deployment.Cli.Tests.PreflightLayoutTests)'
env -u HVO_PRODUCTION_CATALOG_BUNDLE DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  MSBUILDDISABLENODEREUSE=1 dotnet test \
  "$REPO_ROOT/tests/HVO.SkyMonitor.Deployment.Cli.Tests/HVO.SkyMonitor.Deployment.Cli.Tests.csproj" \
  --configuration Release --filter "$preflight_filter" \
  -nodeReuse:false -p:UseSharedCompilation=false
unset preflight_filter
