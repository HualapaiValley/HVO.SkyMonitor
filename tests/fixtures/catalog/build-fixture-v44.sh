#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
output="hyg-v44-subset.sqlite"
rm -f "$output"
sqlite3 "$output" < build-fixture-v44.sql
sha256sum --check SHA256SUMS-v44
