#!/usr/bin/env bash
set -uo pipefail

root=/mnt/hvo-1058
project=/home/roys/development/hvo-1058-comparison/tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/HVO.SkyMonitor.CameraAgent.AcceptanceTests.csproj
assembly=/home/roys/development/hvo-1058-comparison/tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/bin/Release/net10.0/HVO.SkyMonitor.CameraAgent.AcceptanceTests.dll
result=$root/results/100k-ext4
if [[ "$(findmnt -n -o UUID --target "$root")" != 5d6a29c7-eca9-47f5-b5e5-5aa694987ce5 ]]; then
    printf 'Wrong benchmark filesystem; refusing to run.\n' >&2
    exit 2
fi
if [[ -e "$result/exit-code" || -e "$result/console.log" ]]; then
    printf 'Result files already exist; refusing to overwrite.\n' >&2
    exit 2
fi
mkdir -p "$result"
printf 'UTC start: %s\n' "$(date -u --iso-8601=seconds)" > "$result/metadata.txt"
printf 'Repository head: %s\n' "$(git -C /home/roys/development/hvo-1058-comparison rev-parse HEAD)" >> "$result/metadata.txt"
sha256sum /home/roys/development/hvo-1058-comparison/tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/CameraAgentArtifactHistoryComparisonTests.cs /home/roys/development/hvo-1058-comparison/tests/HVO.SkyMonitor.CameraAgent.AcceptanceTests/Infrastructure/GalleryPerformanceFixture.cs >> "$result/metadata.txt"
df -hT "$root" >> "$result/metadata.txt"
dotnet build "$project" --configuration Release -warnaserror > "$result/build.log" 2>&1
build_status=$?
if [[ "$build_status" -ne 0 ]]; then
    printf '%s\n' "$build_status" > "$result/exit-code"
    printf 'UTC build failure: %s\n' "$(date -u --iso-8601=seconds)" >> "$result/metadata.txt"
    exit "$build_status"
fi
sha256sum "$assembly" >> "$result/metadata.txt"
TMPDIR=$root/fixtures HVO_ISSUE1058_RETAIN=1 HVO_ISSUE1058_CAPTURES=100000 \
    dotnet test "$project" --configuration Release --no-build \
    --filter 'FullyQualifiedName~CameraAgentArtifactHistoryComparisonTests.TwoHourSourceAndShadowQueriesReturnIdenticalCaptureAndOutputRows' \
    --logger 'console;verbosity=detailed' --logger 'trx;LogFileName=100k-ext4.trx' \
    --results-directory "$result" > "$result/console.log" 2>&1
status=$?
printf '%s\n' "$status" > "$result/exit-code"
printf 'UTC end: %s\n' "$(date -u --iso-8601=seconds)" >> "$result/metadata.txt"
exit "$status"
