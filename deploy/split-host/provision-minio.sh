#!/bin/sh
set -eu

umask 077
policy=""
config_dir=""
temporary=""
cleanup() {
  status=$?
  trap - EXIT
  [ -z "$policy" ] || rm -f -- "$policy" || true
  [ -z "$config_dir" ] || rm -rf -- "$config_dir" || true
  [ -z "$temporary" ] || rm -f -- "$temporary" || true
  exit "$status"
}
trap cleanup EXIT
trap 'exit 1' HUP INT TERM
replace_all() {
  value=$1
  token=$2
  replacement=$3
  result=""
  while [ "${value#*"$token"}" != "$value" ]; do
    result=$result${value%%"$token"*}$replacement
    value=${value#*"$token"}
  done
  printf '%s%s' "$result" "$value"
}
policy=$(mktemp /tmp/hvo-minio-policy.XXXXXX)
config_dir=$(mktemp -d /tmp/hvo-minio-mc.XXXXXX)
cp /run/hvo-mc/config.json "$config_dir/config.json"
account=$(cat /run/hvo-mc/account)
mc --config-dir "$config_dir" mb --ignore-existing "local/$MINIO_ARTIFACT_BUCKET" "local/$MINIO_DIAGNOSTICS_BUCKET" >/dev/null
policy_content=$(cat /run/hvo-minio/policy.template.json)
policy_content=$(replace_all "$policy_content" '@ARTIFACT_BUCKET@' "$MINIO_ARTIFACT_BUCKET")
policy_content=$(replace_all "$policy_content" '@DIAGNOSTICS_BUCKET@' "$MINIO_DIAGNOSTICS_BUCKET")
printf '%s\n' "$policy_content" > "$policy"
credentials=/run/hvo-output/minio-runtime.json
if [ -e "$credentials" ] || [ -L "$credentials" ]; then
  [ -f "$credentials" ] && [ ! -L "$credentials" ] && [ -s "$credentials" ]
else
  temporary=$(mktemp /run/hvo-output/.minio-runtime.XXXXXX)
  if ! mc --config-dir "$config_dir" admin user svcacct add local "$account" --policy "$policy" --json > "$temporary"; then
    printf 'MinIO service account creation failed.\n' >&2
    exit 1
  fi
  chmod 600 "$temporary"
  mv "$temporary" "$credentials"
  temporary=""
fi
chmod 600 "$credentials"
