#!/usr/bin/env bash
set -euo pipefail
umask 077

usage() {
    cat >&2 <<USAGE
Usage:
  $0 VERSION OUTPUT_DIRECTORY [RELEASE_BASE]
  $0 --index INDEX_LOCATOR [--version VERSION] [--asset-base BASE] OUTPUT_DIRECTORY

Locators and bases may be absolute local paths or HTTPS URLs. Set
HVO_INSTALLER_NO_DOWNLOAD=1 to permit only local or verified cached bytes.
USAGE
}

index_locator=""
version=""
asset_base=""
if [[ ${1:-} == --index ]]; then
    [[ $# -ge 3 ]] || { usage; exit 2; }
    index_locator="$2"
    shift 2
    while [[ $# -gt 1 ]]; do
        case "$1" in
            --version) [[ $# -ge 3 ]] || { usage; exit 2; }; version="$2"; shift 2 ;;
            --asset-base) [[ $# -ge 3 ]] || { usage; exit 2; }; asset_base="$2"; shift 2 ;;
            *) usage; exit 2 ;;
        esac
    done
    [[ $# -eq 1 ]]
    output_directory="$1"
else
    [[ $# -ge 2 && $# -le 3 ]] || { usage; exit 2; }
    version="$1"
    output_directory="$2"
    asset_base="${3:-}"
fi

[[ "$output_directory" == /* && "$output_directory" != / ]] || { printf 'Output directory must be an absolute path other than root.\n' >&2; exit 2; }
for command in base64 curl cut df find flock id jq mktemp od openssl sha256sum stat tail tar tr; do
    command -v "$command" >/dev/null || { printf 'Required command is unavailable: %s\n' "$command" >&2; exit 1; }
done
case "$(uname -m)" in
    x86_64) architecture="x64" ;;
    aarch64|arm64) architecture="arm64" ;;
    *) printf 'Unsupported Linux architecture.\n' >&2; exit 1 ;;
esac

temporary="$(mktemp -d)"
created_output=false
cleanup() {
    local status=$?
    trap - EXIT
    rm -rf -- "$temporary"
    if [[ $status -ne 0 && "$created_output" == true ]]; then rm -rf -- "$output_directory"; fi
    exit "$status"
}
trap cleanup EXIT

cache_root="${HVO_INSTALLER_CACHE:-${XDG_CACHE_HOME:-$HOME/.cache}/hvo/skymonitor/bootstrap}"
[[ ! -L "$cache_root" ]] || { printf 'Bootstrap cache root must not be a symbolic link.\n' >&2; exit 1; }
mkdir -p -m 700 "$cache_root"
[[ "$(stat -c '%u' "$cache_root")" == "$(id -u)" && "$(stat -c '%a' "$cache_root")" == 700 ]] || {
    printf 'Bootstrap cache root must be owner-owned mode 0700.\n' >&2
    exit 1
}
exec 9>"$cache_root/.lock"
flock 9
find "$cache_root" -maxdepth 1 -type f -name '*.partial' -mtime +1 -delete
find "$cache_root" -maxdepth 1 -type f ! -name '.lock' ! -name '*.uri' -mtime +90 -delete
no_download="${HVO_INSTALLER_NO_DOWNLOAD:-0}"
readonly cache_quota=2147483648
last_resolved_locator=""
last_cache_path=""

validate_cache_file() {
    local path="$1" maximum="$2"
    if [[ ! -f "$path" || -L "$path" ]]; then return 1; fi
    [[ "$(stat -c '%u' "$path")" == "$(id -u)" && "$(stat -c '%h' "$path")" == 1 ]]
    [[ "$(stat -c '%a' "$path")" == 600 && "$(stat -c '%s' "$path")" -le "$maximum" ]]
}

remove_cached_files() {
    local path
    for path in "$@"; do
        if [[ -n "$path" ]]; then rm -f -- "$path" "$path.uri"; fi
    done
}

url_host() {
    local value="${1#https://}"
    printf '%s' "${value%%/*}"
}

fetch() {
    local locator="$1" destination="$2" maximum="$3"
    if [[ "$locator" == /* ]]; then
        [[ -f "$locator" && ! -L "$locator" ]]
        [[ "$(stat -c '%s' "$locator")" -le "$maximum" ]]
        cp -- "$locator" "$destination"
        last_resolved_locator="$locator"
        last_cache_path=""
        return
    fi
    [[ "$locator" =~ ^https://[^/@[:space:]]+(/[^[:space:]#]*)?$ ]] || { printf 'Invalid HTTPS locator: %s\n' "$locator" >&2; exit 1; }
    local key cached pending current initial code redirect next host current_host total available
    key="$(printf '%s' "$locator" | sha256sum | cut -d' ' -f1)"
    cached="$cache_root/$key"
    if validate_cache_file "$cached" "$maximum"; then
        cp -- "$cached" "$destination"
        if validate_cache_file "$cached.uri" 2048; then last_resolved_locator="$(<"$cached.uri")"; else last_resolved_locator="$locator"; fi
        last_cache_path="$cached"
        return
    fi
    [[ "$no_download" != 1 ]] || { printf 'Required verified cache entry is unavailable.\n' >&2; exit 1; }
    total=0
    for cache_file in "$cache_root"/*; do [[ -f "$cache_file" && ! -L "$cache_file" ]] && ((total += $(stat -c '%s' "$cache_file"))); done
    (( total + maximum <= cache_quota )) || { printf 'Bounded bootstrap cache quota would be exceeded.\n' >&2; exit 1; }
    available="$(df --output=avail -B1 "$cache_root" | tr -cd '0-9\n' | tail -n 1)"
    (( available >= maximum + 67108864 )) || { printf 'Insufficient bootstrap cache disk space.\n' >&2; exit 1; }
    pending="$(mktemp "$cache_root/.download.XXXXXX.partial")"
    trap 'rm -f -- "$pending"' RETURN
    initial="$locator"
    current="$locator"
    for ((redirect = 0; redirect <= 5; redirect++)); do
        auth=()
        if [[ -n "${HVO_GITHUB_TOKEN:-}" && "$(url_host "$initial")" == github.com && "$(url_host "$current")" == github.com ]]; then
            [[ "$HVO_GITHUB_TOKEN" =~ ^[A-Za-z0-9_]+$ ]] || { printf 'GitHub token format is invalid.\n' >&2; exit 1; }
            auth_config="$(mktemp "$temporary/curl-auth.XXXXXX.conf")"
            printf 'oauth2-bearer = "%s"\n' "$HVO_GITHUB_TOKEN" > "$auth_config"
            chmod 0600 "$auth_config"
            auth=(--config "$auth_config")
        fi
        mapfile -t transfer < <(curl --silent --show-error --proto '=https' --connect-timeout 10 --max-time 300 \
            --retry 2 --retry-all-errors --max-filesize "$maximum" "${auth[@]}" --output "$pending" \
            --write-out $'%{http_code}\n%{redirect_url}\n' "$current")
        if [[ -n "${auth_config:-}" ]]; then rm -f -- "$auth_config"; auth_config=""; fi
        code="${transfer[0]:-000}"
        if [[ "$code" =~ ^2[0-9][0-9]$ ]]; then break; fi
        [[ "$code" =~ ^3[0-9][0-9]$ && $redirect -lt 5 ]] || { printf 'Bootstrap download failed with HTTP %s.\n' "$code" >&2; exit 1; }
        next="${transfer[1]:-}"
        [[ "$next" =~ ^https://[^/@[:space:]]+(/[^[:space:]#]*)?$ ]] || { printf 'Bootstrap redirect is invalid.\n' >&2; exit 1; }
        host="$(url_host "$next")"
        current_host="$(url_host "$current")"
        [[ "$(url_host "$initial")" == github.com && "$current_host" == github.com &&
           "$host" =~ ^(objects\.githubusercontent\.com|release-assets\.githubusercontent\.com|github-releases\.githubusercontent\.com)$ ]] || {
            printf 'Bootstrap redirect crosses an unapproved origin.\n' >&2
            exit 1
        }
        current="$next"
    done
    [[ "$code" =~ ^2[0-9][0-9]$ ]]
    chmod 0600 "$pending"
    mv -n -- "$pending" "$cached"
    rm -f -- "$pending"
    validate_cache_file "$cached" "$maximum"
    uri_pending="$(mktemp "$cache_root/.uri.XXXXXX.partial")"
    printf '%s' "$current" > "$uri_pending"
    chmod 0600 "$uri_pending"
    mv -f -- "$uri_pending" "$cached.uri"
    validate_cache_file "$cached.uri" 2048
    cp -- "$cached" "$destination"
    last_resolved_locator="$current"
    last_cache_path="$cached"
    trap - RETURN
}

cat > "$temporary/release-public.pem" <<'PUBLIC_KEY'
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEgwMP09gTaE2W/JfzUV6lM/O3Q7Mi
Wyav66imtQwi163v4af/36BKI5oDe6r0g50zj0vEKLjgsJcFp0LDkwk9Uw==
-----END PUBLIC KEY-----
PUBLIC_KEY

verify_signature() {
    local input="$1" signature_file="$2" der_file="$3"
    local signature_hex r s der
    signature_hex="$(base64 --decode < "$signature_file" | od -An -v -tx1 | tr -d ' \n')"
    [[ ${#signature_hex} -eq 128 ]] || { printf 'Signature has the wrong length.\n' >&2; return 1; }
    der_integer() {
        local value="$1"
        while [[ ${#value} -gt 2 && "${value:0:2}" == 00 ]]; do value="${value:2}"; done
        [[ "${value:0:1}" =~ [89a-f] ]] && value="00$value"
        printf '02%02x%s' "$(( ${#value} / 2 ))" "$value"
    }
    r="$(der_integer "${signature_hex:0:64}")"
    s="$(der_integer "${signature_hex:64:64}")"
    der="$(printf '30%02x%s%s' "$(( (${#r} + ${#s}) / 2 ))" "$r" "$s")"
    : > "$der_file"
    for ((offset = 0; offset < ${#der}; offset += 2)); do printf '%b' "\\x${der:offset:2}" >> "$der_file"; done
    openssl dgst -sha256 -verify "$temporary/release-public.pem" -signature "$der_file" "$input" >/dev/null
}

join_locator() {
    local base="${1%/}" name="$2"
    if [[ "$base" == /* ]]; then printf '%s/%s' "$base" "$name"; else printf '%s/%s' "$base" "$name"; fi
}

if [[ -n "$index_locator" ]]; then
    fetch "$index_locator" "$temporary/installer-release-index.json" 262144
    index_cache_path="$last_cache_path"
    fetch "$index_locator.sig" "$temporary/installer-release-index.json.sig" 128
    index_signature_cache_path="$last_cache_path"
    if ! verify_signature "$temporary/installer-release-index.json" "$temporary/installer-release-index.json.sig" "$temporary/index.der.sig"; then
        remove_cached_files "$index_cache_path" "$index_signature_cache_path"
        [[ "$no_download" != 1 && "$index_locator" == https://* ]] || exit 1
        fetch "$index_locator" "$temporary/installer-release-index.json" 262144
        fetch "$index_locator.sig" "$temporary/installer-release-index.json.sig" 128
        verify_signature "$temporary/installer-release-index.json" "$temporary/installer-release-index.json.sig" "$temporary/index.der.sig"
    fi
    jq --exit-status '
      .schemaVersion == 1 and .manifestKind == "release-index" and .train == "installer" and
      .sequence >= 1 and .signing.algorithm == "ecdsa-p256-sha256-p1363" and
      .signing.keyId == "p256-sha256:64aa88e5fd4750839ac5f30fdd4e32c2652eaff9994173ae4b47c799ac215aeb"
    ' "$temporary/installer-release-index.json" >/dev/null
    [[ -n "$version" ]] || version="$(jq -er .defaultVersion "$temporary/installer-release-index.json")"
    release_reference="$(jq -cer --arg version "$version" '[.releases[] | select(.version == $version)] | if length == 1 then .[0] else error("version is missing or ambiguous") end' "$temporary/installer-release-index.json")"
    tag="$(jq -r .tag <<<"$release_reference")"
    manifest_name="$(jq -r .manifestAsset <<<"$release_reference")"
    manifest_signature_name="$(jq -r .signatureAsset <<<"$release_reference")"
    if [[ -z "$asset_base" ]]; then
        if [[ "$index_locator" == https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/* ]]; then
            release_base="https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/$tag"
        elif [[ "$index_locator" == /* ]]; then
            release_base="$(dirname "$index_locator")/$tag"
        else
            printf 'A mirrored index requires --asset-base.\n' >&2
            exit 1
        fi
    else
        release_base="$(join_locator "$asset_base" "$tag")"
    fi
else
    [[ "$version" =~ ^[A-Za-z0-9]([A-Za-z0-9.-]{0,126}[A-Za-z0-9])?$ && "$version" != *..* ]] || { printf 'Invalid installer version.\n' >&2; exit 2; }
    tag="installer-v$version"
    manifest_name="release-manifest.json"
    manifest_signature_name="release-manifest.json.sig"
    release_base="${asset_base:-https://github.com/RoySalisbury/HVO.SkyMonitor/releases/download/$tag}"
fi

fetch "$(join_locator "$release_base" "$manifest_name")" "$temporary/release-manifest.json" 262144
manifest_locator="$(join_locator "$release_base" "$manifest_name")"
manifest_cache_path="$last_cache_path"
fetch "$(join_locator "$release_base" "$manifest_signature_name")" "$temporary/release-manifest.json.sig" 128
manifest_signature_locator="$(join_locator "$release_base" "$manifest_signature_name")"
manifest_signature_cache_path="$last_cache_path"
if [[ -n "$index_locator" ]]; then
    expected_manifest_length="$(jq -r .manifestLength <<<"$release_reference")"
    expected_manifest_sha256="$(jq -r .manifestSha256 <<<"$release_reference")"
    [[ "$(stat -c '%s' "$temporary/release-manifest.json")" == "$expected_manifest_length" ]]
    printf '%s  %s\n' "$expected_manifest_sha256" "$temporary/release-manifest.json" | sha256sum --check --status
fi
if ! verify_signature "$temporary/release-manifest.json" "$temporary/release-manifest.json.sig" "$temporary/manifest.der.sig"; then
    remove_cached_files "$manifest_cache_path" "$manifest_signature_cache_path"
    [[ "$no_download" != 1 && "$manifest_locator" == https://* ]] || exit 1
    fetch "$manifest_locator" "$temporary/release-manifest.json" 262144
    fetch "$manifest_signature_locator" "$temporary/release-manifest.json.sig" 128
    verify_signature "$temporary/release-manifest.json" "$temporary/release-manifest.json.sig" "$temporary/manifest.der.sig"
fi
jq --exit-status --arg version "$version" --arg tag "$tag" '
    .schemaVersion == 1 and .manifestKind == "InstallerRelease" and .release.train == "installer" and
    .release.version == $version and .release.tag == $tag and
    .signing.algorithm == "ecdsa-p256-sha256-p1363" and
    .signing.keyId == "p256-sha256:64aa88e5fd4750839ac5f30fdd4e32c2652eaff9994173ae4b47c799ac215aeb"
' "$temporary/release-manifest.json" >/dev/null

asset="$(jq -cer --arg architecture "$architecture" '[.artifacts[] | select(.role == "Installer" and .operatingSystem == "linux" and .architecture == $architecture)] | if length == 1 then .[0] else error("installer architecture is missing or ambiguous") end' "$temporary/release-manifest.json")"
asset_name="$(jq -r .assetName <<<"$asset")"
asset_length="$(jq -r .length <<<"$asset")"
asset_sha256="$(jq -r .sha256 <<<"$asset")"
[[ "$asset_name" =~ ^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}$ && "$asset_length" =~ ^[1-9][0-9]*$ && "$asset_length" -le 536870912 && "$asset_sha256" =~ ^[a-f0-9]{64}$ ]] || { printf 'Installer asset identity is invalid.\n' >&2; exit 1; }
fetch "$(join_locator "$release_base" "$asset_name")" "$temporary/$asset_name" "$asset_length"
resolved_asset_locator="$last_resolved_locator"
asset_locator="$(join_locator "$release_base" "$asset_name")"
asset_cache_path="$last_cache_path"
if [[ "$(stat -c '%s' "$temporary/$asset_name")" != "$asset_length" ]] ||
   ! printf '%s  %s\n' "$asset_sha256" "$temporary/$asset_name" | sha256sum --check --status; then
    remove_cached_files "$asset_cache_path"
    [[ "$no_download" != 1 && "$asset_locator" == https://* ]] || exit 1
    fetch "$asset_locator" "$temporary/$asset_name" "$asset_length"
    resolved_asset_locator="$last_resolved_locator"
    [[ "$(stat -c '%s' "$temporary/$asset_name")" == "$asset_length" ]]
    printf '%s  %s\n' "$asset_sha256" "$temporary/$asset_name" | sha256sum --check --status
fi

mapfile -t entries < <(tar -tzf "$temporary/$asset_name")
mapfile -t entry_types < <(tar -tvzf "$temporary/$asset_name" | cut -c1)
[[ ${#entries[@]} -eq 2 && ${#entry_types[@]} -eq 2 && "${entries[0]}" == hvo-skymonitor && \
   "${entries[1]}" == THIRD-PARTY-NOTICES.md && "${entry_types[0]}" == - && "${entry_types[1]}" == - ]] || {
    printf 'Installer archive has an unexpected file set.\n' >&2; exit 1;
}
[[ ! -e "$output_directory" ]]
mkdir -m 700 "$output_directory"
created_output=true
tar -xOzf "$temporary/$asset_name" hvo-skymonitor > "$output_directory/hvo-skymonitor"
tar -xOzf "$temporary/$asset_name" THIRD-PARTY-NOTICES.md > "$output_directory/THIRD-PARTY-NOTICES.md"
elf_magic="$(od -An -N4 -tx1 "$output_directory/hvo-skymonitor" | tr -d ' \n')"
elf_machine="$(od -An -j18 -N2 -tu2 "$output_directory/hvo-skymonitor" | tr -d ' ')"
if [[ "$architecture" == x64 ]]; then expected_machine=62; else expected_machine=183; fi
[[ "$elf_magic" == 7f454c46 && "$elf_machine" == "$expected_machine" ]] || { printf 'Installer executable architecture does not match this host.\n' >&2; exit 1; }
chmod 0755 "$output_directory/hvo-skymonitor"
chmod 0644 "$output_directory/THIRD-PARTY-NOTICES.md"
cp "$temporary/release-manifest.json" "$temporary/release-manifest.json.sig" "$output_directory/"
cat > "$output_directory/installer-distribution.txt" <<EVIDENCE
releaseTag=$tag
releaseVersion=$version
manifestSha256=$(sha256sum "$temporary/release-manifest.json" | cut -d' ' -f1)
assetName=$asset_name
assetLength=$asset_length
assetSha256=$asset_sha256
signingKeyId=p256-sha256:64aa88e5fd4750839ac5f30fdd4e32c2652eaff9994173ae4b47c799ac215aeb
releaseBase=$release_base
resolvedAsset=$resolved_asset_locator
verificationResult=verified
EVIDENCE
chmod 0600 "$output_directory/installer-distribution.txt"
printf 'Verified installer %s at %s/hvo-skymonitor\n' "$tag" "$output_directory"
