#!/usr/bin/env bash
# Fail-closed host and cleanup helpers for the destructive installer qualification campaign.
# Bash nameref targets are associative arrays supplied by the caller.
# shellcheck disable=SC2178

hvo_installer_resolve_local_docker_endpoint() {
  local endpoint endpoint_source context_name scheme

  if [[ -n "${DOCKER_CONTEXT:-}" ]]; then
    context_name="$DOCKER_CONTEXT"
    endpoint="$(docker context inspect "$context_name" --format '{{.Endpoints.docker.Host}}')"
    endpoint_source="Docker context $context_name"
  elif [[ -n "${DOCKER_HOST:-}" ]]; then
    endpoint="$DOCKER_HOST"
    endpoint_source=DOCKER_HOST
  else
    context_name="$(docker context show)"
    endpoint="$(docker context inspect "$context_name" --format '{{.Endpoints.docker.Host}}')"
    endpoint_source="Docker context $context_name"
  fi

  scheme="${endpoint%%:*}"
  if [[ ! "$scheme" =~ ^[a-zA-Z][a-zA-Z0-9+.-]*$ ]]; then
    scheme=unknown
  fi
  if [[ "$endpoint" != unix:///* || "$endpoint" == *[$'\t\r\n ']* ||
        "$endpoint" == *\?* || "$endpoint" == *\#* || "${endpoint#unix://}" == / ]]; then
    printf 'Installer qualification refuses the non-local Docker transport %s selected by %s.\n' \
      "$scheme" "$endpoint_source" >&2
    return 2
  fi

  HVO_INSTALLER_DOCKER_ENDPOINT="$endpoint"
  HVO_INSTALLER_DOCKER_ENDPOINT_SOURCE="$endpoint_source"
  # Pin every later call, including the EXIT trap, to the endpoint that passed validation. A mutable named context
  # must not be able to redirect destructive cleanup after this check.
  unset DOCKER_CONTEXT DOCKER_TLS_VERIFY DOCKER_CERT_PATH
  export DOCKER_HOST="$HVO_INSTALLER_DOCKER_ENDPOINT"
  printf 'Installer qualification accepted local Docker endpoint %s from %s.\n' \
    "$HVO_INSTALLER_DOCKER_ENDPOINT" "$HVO_INSTALLER_DOCKER_ENDPOINT_SOURCE" >&2
}

hvo_installer_snapshot_image_ids() {
  local map_name="$1" image_ids image_id
  local -n snapshot="$map_name"

  image_ids="$(docker image ls --all --no-trunc --quiet)"
  while IFS= read -r image_id; do
    if [[ -n "$image_id" ]]; then
      snapshot["$image_id"]=1
    fi
  done <<< "$image_ids"
  return 0
}

hvo_installer_register_owned_image_pair() {
  local snapshot_name="$1" cleanup_name="$2" first_identity="$3" second_identity="$4"
  local identity resolved_id
  local -a identities=("$first_identity" "$second_identity")
  local -A resolved_ids=()
  local -n snapshot="$snapshot_name"
  local -n cleanup_ref="$cleanup_name"

  # The two manifest identities name one native image. If either resolves to an image present before the campaign,
  # preserve the pair. Otherwise retain both raw identities so cleanup still works when a cached archive is loaded
  # only later by the installer CLI.
  for identity in "${identities[@]}"; do
    if resolved_id="$(docker image inspect --format '{{.Id}}' "$identity" 2>/dev/null)"; then
      resolved_ids["$identity"]="$resolved_id"
      if [[ -n "${snapshot[$identity]:-}" || -n "${snapshot[$resolved_id]:-}" ]]; then
        return 0
      fi
    fi
  done
  for identity in "${identities[@]}"; do
    resolved_id="${resolved_ids[$identity]:-$identity}"
    # shellcheck disable=SC2034 # The caller consumes the associative array populated through this nameref.
    cleanup_ref["$resolved_id"]=1
  done
}

hvo_installer_verify_signed_candidate() {
  local release_root="$1" revision="$2" tree="$3" version="$4" tag="$5" public_key="$6"
  local manifest="$release_root/image-manifest.json"

  for required_path in \
    "$manifest" \
    "$manifest.sig" \
    "$release_root/SHA256SUMS" \
    "$release_root/SHA256SUMS.sig" \
    "$public_key"; do
    if [[ ! -f "$required_path" ]]; then
      printf 'Signed candidate verification requires %s.\n' "$required_path" >&2
      return 1
    fi
  done

  signed_release_tool verify \
    --manifest "$manifest" \
    --signature "$manifest.sig" \
    --asset-root "$release_root" \
    --public-key "$public_key"
  signed_release_tool verify-signature \
    --input "$release_root/SHA256SUMS" \
    --signature "$release_root/SHA256SUMS.sig" \
    --public-key "$public_key"
  jq -e \
    --arg revision "$revision" \
    --arg tree "$tree" \
    --arg version "$version" \
    --arg tag "$tag" '
      .release.version == $version and
      .release.tag == $tag and
      .release.sourceRevision == $revision and
      .release.sourceTree == $tree and
      (.images | length) == 1 and
      .images[0].sourceRevision == $revision and
      .images[0].sourceTree == $tree
    ' "$manifest" >/dev/null
}
