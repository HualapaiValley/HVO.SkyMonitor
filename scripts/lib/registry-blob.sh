#!/usr/bin/env bash
# Authenticated, bounded registry-blob retrieval shared by the CameraAgent image release and its contract tests.

readonly HVO_REGISTRY_ATTESTATION_MAX_BYTES=$((64 * 1024 * 1024))

docker_registry_auth() {
    local host="$1" config="${DOCKER_CONFIG:-$HOME/.docker}/config.json"
    [[ -f "$config" ]] || return 0
    jq -r --arg host "$host" '.auths[$host].auth // empty' "$config" 2>/dev/null || true
}

registry_blob_sha256() {
    local path="$1"
    if command -v sha256sum >/dev/null 2>&1; then
        sha256sum -- "$path" | awk '{print $1}'
    elif command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$path" | awk '{print $1}'
    else
        printf 'Neither sha256sum nor shasum is available to authenticate a registry blob.\n' >&2
        return 1
    fi
}

# Fetches one blob from the registry holding an image reference, following the standard bearer-token challenge.
# The destination is published to its caller only when the response is within the evidence-asset ceiling and its
# bytes derive the content digest requested from the registry.
registry_blob() {
    local reference="$1" digest="$2" destination="$3"
    local host path challenge realm service auth token actual_digest
    # A reference whose first segment carries no dot or port names Docker Hub rather than a host, and an
    # official image there lives under "library/". The release always uses a fully qualified registry, but the
    # helper is exercised against public Docker Hub images, so it follows the same resolution rule Docker does.
    if [[ "$reference" != */* || ( "${reference%%/*}" != *.* && "${reference%%/*}" != *:* ) ]]; then
        host="registry-1.docker.io"
        path="$reference"
        [[ "$path" == */* ]] || path="library/$path"
    else
        host="${reference%%/*}"
        path="${reference#*/}"
    fi
    challenge="$(curl -sS -o /dev/null -D - "https://${host}/v2/" | tr -d '\r' \
        | sed -n 's/^[Ww][Ww][Ww]-[Aa]uthenticate: *[Bb]earer //p')"
    realm="$(printf '%s' "$challenge" | sed -n 's/.*realm="\([^"]*\)".*/\1/p')"
    service="$(printf '%s' "$challenge" | sed -n 's/.*service="\([^"]*\)".*/\1/p')"
    token=""
    if [[ -n "$realm" ]]; then
        local -a token_auth=()
        auth="$(docker_registry_auth "$host")"
        [[ -n "$auth" ]] && token_auth=(--header "Authorization: Basic ${auth}")
        token="$(curl -sS "${token_auth[@]}" \
            "${realm}?service=${service}&scope=repository:${path}:pull" \
            | jq -r '.token // .access_token // empty')"
    fi
    local -a blob_auth=()
    [[ -n "$token" ]] && blob_auth=(--header "Authorization: Bearer ${token}")
    if ! curl -sSL --fail --max-filesize "$HVO_REGISTRY_ATTESTATION_MAX_BYTES" "${blob_auth[@]}" \
        "https://${host}/v2/${path}/blobs/${digest}" --output "$destination"; then
        rm -f -- "$destination"
        return 1
    fi
    if ! actual_digest="sha256:$(registry_blob_sha256 "$destination")"; then
        rm -f -- "$destination"
        return 1
    fi
    if [[ "$actual_digest" != "$digest" ]]; then
        printf 'Registry blob %s from %s returned content digest %s.\n' "$digest" "$reference" "$actual_digest" >&2
        rm -f -- "$destination"
        return 1
    fi
}
