#!/bin/sh
set -eu

policy=$(mktemp /tmp/hvo-minio-policy.XXXXXX)
temporary=""
cleanup() {
  rm -f -- "$policy"
  [ -z "$temporary" ] || rm -f -- "$temporary"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
mc --config-dir /run/hvo-mc mb --ignore-existing "local/$MINIO_ARTIFACT_BUCKET" "local/$MINIO_DIAGNOSTICS_BUCKET" >/dev/null
sed -e "s/@ARTIFACT_BUCKET@/$MINIO_ARTIFACT_BUCKET/g" -e "s/@DIAGNOSTICS_BUCKET@/$MINIO_DIAGNOSTICS_BUCKET/g" \
  /run/hvo-minio/policy.template.json > "$policy"
credentials=/run/hvo-output/minio-runtime.json
if [ -e "$credentials" ] || [ -L "$credentials" ]; then
  [ -f "$credentials" ] && [ ! -L "$credentials" ] && [ -s "$credentials" ]
else
  temporary=$(mktemp /run/hvo-output/.minio-runtime.XXXXXX)
  umask 077
  mc --config-dir /run/hvo-mc admin user svcacct add local --policy "$policy" --json > "$temporary"
  chmod 600 "$temporary"
  mv "$temporary" "$credentials"
  temporary=""
fi
chmod 600 "$credentials"
