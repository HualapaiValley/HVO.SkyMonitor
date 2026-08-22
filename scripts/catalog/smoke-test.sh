#!/usr/bin/env bash
set -euo pipefail
umask 077

readonly SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
temporary_directory="$(mktemp -d "${TMPDIR:-/tmp}/hvo-catalog-smoke.XXXXXX")"
cleanup() {
    local status=$?
    trap - EXIT
    rm -rf -- "$temporary_directory"
    exit "$status"
}
trap cleanup EXIT

bundle="$temporary_directory/fixture.bundle"
install_root="$temporary_directory/install"
mkdir -p "$bundle" "$install_root/versions/sentinel" "$install_root/.staging.orphan"
cat > "$bundle/manifest.json" <<JSON
{
  "manifestVersion": 2,
  "package": {
    "kind": "fixture",
    "version": "fixture-1"
  },
  "catalog": {
    "id": "fixture-smoke",
    "name": "fixture",
    "version": "fixture-1"
  },
  "schemaVersion": "2",
  "preprocessingVersion": "3",
  "database": {
    "relativePath": "catalog.sqlite",
    "sha256": "0000000000000000000000000000000000000000000000000000000000000000",
    "length": 1,
    "rowCount": 1
  }
}
JSON
printf 'fixture\n' > "$bundle/hyg_v42.sqlite"
printf 'fixture\n' > "$bundle/LICENSE-HYG.md"
printf 'fixture\n' > "$bundle/ATTRIBUTION-HYG.md"
printf 'sentinel\n' > "$install_root/versions/sentinel/value"
printf 'orphan\n' > "$install_root/.staging.orphan/value"
ln -s versions/sentinel "$install_root/current"

if "$SCRIPT_DIR/install-hyg-v42.sh" install "$bundle" "$install_root" >/dev/null 2>&1; then
    printf 'fixture bundle was incorrectly accepted\n' >&2
    exit 1
fi
[[ "$(readlink "$install_root/current")" == "versions/sentinel" ]] || {
    printf 'failed fixture install changed the active pointer\n' >&2
    exit 1
}
[[ ! -e "$install_root/.staging.orphan" ]] || {
    printf 'orphan staging directory was not cleaned\n' >&2
    exit 1
}

cat > "$bundle/manifest.json" <<JSON
{
  "manifestVersion": 2,
  "package": {
    "kind": "production",
    "kind": "production",
    "version": "hyg-v4.2-p3-s2-r1"
  }
}
JSON
set +e
duplicate_output="$("$SCRIPT_DIR/install-hyg-v42.sh" install "$bundle" "$install_root" 2>&1)"
duplicate_status=$?
set -e
if [[ $duplicate_status -eq 0 || "$duplicate_output" != *"duplicate properties"* ]]; then
    printf 'duplicate JSON properties were not rejected explicitly\n' >&2
    exit 1
fi
[[ "$(readlink "$install_root/current")" == "versions/sentinel" ]] || {
    printf 'duplicate manifest install changed the active pointer\n' >&2
    exit 1
}

printf '%*s' 65537 '' > "$bundle/manifest.json"
set +e
oversized_output="$("$SCRIPT_DIR/install-hyg-v42.sh" install "$bundle" "$install_root" 2>&1)"
oversized_status=$?
set -e
if [[ $oversized_status -eq 0 || "$oversized_output" != *"exceeds the maximum byte length"* ]]; then
    printf 'oversized JSON manifest was not rejected before parsing\n' >&2
    exit 1
fi

ln -s versions/missing "$install_root/previous"
if "$SCRIPT_DIR/rollback-hyg-v42.sh" "$install_root" >/dev/null 2>&1; then
    printf 'rollback incorrectly accepted an invalid previous pointer\n' >&2
    exit 1
fi
[[ "$(readlink "$install_root/current")" == "versions/sentinel" ]] || {
    printf 'failed rollback changed the active pointer\n' >&2
    exit 1
}

printf 'Catalog fixture-mode smoke checks passed.\n'
