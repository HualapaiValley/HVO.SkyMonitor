#!/usr/bin/env python3
"""Reproduce warm direct-engine/facade observations outside the source checkout."""
import datetime
import hashlib
import html
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time

if len(sys.argv) != 3:
    raise SystemExit('Usage: reproduce.py REPOSITORY NEW_EXTERNAL_OUTPUT_DIRECTORY')
source = Path(__file__).resolve().parent
repo = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
if output == repo or repo in output.parents or output.exists():
    raise SystemExit('Output must be a new directory outside the repository.')
output.mkdir(parents=True)
shutil.copyfile(source / 'Program.cs.txt', output / 'Program.cs')
(output / 'Harness.csproj').write_text((source / 'Harness.csproj.template').read_text().replace(
    '__REPOSITORY_ROOT__', html.escape(str(repo), quote=True)))
head = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
manifest = {'sourceHead': head, 'sdk': subprocess.check_output(['dotnet', '--version'], cwd=repo,
            text=True).strip(), 'recordedUtc': datetime.datetime.now(datetime.timezone.utc).isoformat(),
            'environment': {'DOTNET_TieredCompilation': '0'}, 'runs': []}
if manifest['sdk'] != '10.0.402':
    raise SystemExit('Use the repository-pinned SDK10.0.402.')
argv = ['dotnet', 'build', str(output / 'Harness.csproj'), '--configuration', 'Release',
        '-warnaserror', '-m:1']
with (output / 'build.log').open('w') as log:
    subprocess.run(argv, cwd=repo, stdout=log, stderr=subprocess.STDOUT, check=True)
manifest['buildCommand'] = argv
manifest['hashes'] = {str(p.relative_to(output)): hashlib.sha256(p.read_bytes()).hexdigest().upper()
                      for p in sorted((output / 'bin/Release/net10.0').glob('*.dll'))}
env = os.environ.copy()
env.update(manifest['environment'])
expected = {x['case']: (x['canonicalCombinedSha256'], x['passes'], x['arcSamples'],
                       x['actualPropagationEvaluations'])
            for x in json.loads((source / 'summary.json').read_text())['identityChecks']}
for ordinal, mode in enumerate(('core', 'facade', 'facade', 'core'), 1):
    result = output / f'measurement-{ordinal}-{mode}.json'
    argv = ['dotnet', str(output / 'bin/Release/net10.0/Harness.dll'), str(repo), mode, str(result)]
    started = time.monotonic()
    with (output / f'measurement-{ordinal}-{mode}.log').open('w') as log:
        code = subprocess.run(argv, cwd=repo, env=env, stdout=log, stderr=subprocess.STDOUT).returncode
    manifest['runs'].append({'mode': mode, 'command': argv, 'exitCode': code,
                             'elapsedSeconds': time.monotonic() - started})
    (output / 'reproduction-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
    if code:
        raise SystemExit(code)
    data = json.loads(result.read_text())
    for row in data['rows']:
        actual = (row['hash'], row['passes'], row['samples'], row['evaluations'])
        if actual != expected[row['name']]:
            raise SystemExit(f'Canonical result/work identity changed for {row["name"]}; preserve outputs and investigate.')
    manifest['hashes'][result.name] = hashlib.sha256(result.read_bytes()).hexdigest().upper()
manifest['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
manifest['postRunHead'] = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=repo, text=True).strip()
(output / 'reproduction-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
print(output)
