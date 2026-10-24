#!/usr/bin/env bash
# Reproduce the affected rendering boundary against the exact baseline source and current candidate.
set -euo pipefail
scratch="${1:?Pass an existing hvo-recorded scratch directory.}"
[[ "$scratch" = /* && -d "$scratch" ]] || { printf 'Scratch must be an existing absolute directory.\n' >&2; exit 2; }
if [[ "${HVO_SCENE_STYLE_MEASURE_LOCKED:-}" != 1 ]]; then
    exec flock -w 900 -o /tmp/hvo-heavy-gate.lock timeout --kill-after=30 1200 \
        env HVO_SCENE_STYLE_MEASURE_LOCKED=1 "$0" "$scratch"
fi
script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(git -C "$script_dir" rev-parse --show-toplevel)"
base=9c8f4c4eefd0fe14fc7a1a0d90b97e40c695ff37
run_root="$scratch/scene-style-comparison"
[[ ! -e "$run_root" ]] || { printf 'Use a scratch directory without a prior scene-style-comparison child.\n' >&2; exit 2; }
mkdir -p "$run_root/baseline-source" "$run_root/measure/baseline" "$run_root/measure/candidate" "$run_root/results"
git -C "$repo" archive "$base" | tar -x -C "$run_root/baseline-source"
python3 - "$run_root" "$repo" "$script_dir" <<'PY'
from pathlib import Path
from xml.sax.saxutils import escape
import shutil
import sys
root, repo, source = map(Path, sys.argv[1:])
for lane in ('baseline', 'candidate'):
    project = (root / 'baseline-source' if lane == 'baseline' else repo) / 'src/HVO.SkyMonitor.Processing/HVO.SkyMonitor.Processing.csproj'
    directory = root / 'measure' / lane
    flags = 'SCENE_STYLE' if lane == 'candidate' else ''
    shutil.copyfile((root / 'baseline-source' if lane == 'baseline' else repo) / 'Directory.Packages.props', directory / 'Directory.Packages.props')
    (directory / 'Measure.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><DefineConstants>{flags}</DefineConstants></PropertyGroup><ItemGroup><ProjectReference Include="{escape(str(project))}" /><PackageReference Include="SkiaSharp.NativeAssets.Linux.NoDependencies" /></ItemGroup></Project>')
    shutil.copyfile(source / 'Measure.cs.txt', directory / 'Program.cs')
PY
export DOTNET_PROCESSOR_COUNT=2 MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false
cd "$repo"
git rev-parse HEAD > "$run_root/results/candidate-head.txt"
git diff HEAD -- src tests > "$run_root/results/candidate-source-delta.patch"
for lane in baseline candidate; do
    dotnet build "$run_root/measure/$lane/Measure.csproj" --configuration Release -warnaserror \
        -nodeReuse:false -p:UseSharedCompilation=false -maxcpucount:2
done
dotnet "$run_root/measure/baseline/bin/Release/net10.0/Measure.dll" "$script_dir/input-scene.json" \
    "$run_root/results/baseline-9c8f4c4.json" baseline-v13 "$base"
for mode in baseline-v13 default-v14 changed-v14; do
    dotnet "$run_root/measure/candidate/bin/Release/net10.0/Measure.dll" "$script_dir/input-scene.json" \
        "$run_root/results/candidate-$mode.json" "$mode" "$(git rev-parse HEAD)"
done
printf 'Rendering comparison retained at %s\n' "$run_root/results"
