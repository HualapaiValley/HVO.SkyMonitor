#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
output="hyg-v42-bright-stars.sqlite"
rm -f "$output"
sqlite3 "$output" < build-fixture.sql
sha256sum --check SHA256SUMS
