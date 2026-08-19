#!/usr/bin/env bash
# All SSH and context-selected Docker invocations for split-host deployment live here.

deploy_transport_ssh_probe() {
    local ssh_host="$1" runtime_root="$2" ports_csv="$3" output status
    if output="$(ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$runtime_root" "$ports_csv" 2>/dev/null <<'REMOTE'
set -euo pipefail
root=$1
ports=$2
identity=$(cat /etc/machine-id 2>/dev/null || hostname)
host_name=$(hostname)
architecture=$(uname -m)
case "$architecture" in x86_64) architecture=amd64 ;; aarch64|arm64) architecture=arm64 ;; esac
cpu=$(getconf _NPROCESSORS_ONLN 2>/dev/null || printf unknown)
memory=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo 2>/dev/null || printf unknown)
current=/
ancestor=/
IFS=/ read -r -a components <<< "${root#/}"
for component in "${components[@]}"; do
  [[ -n "$component" ]] || continue
  [[ "$current" == / ]] && current="/$component" || current="$current/$component"
  if [[ -L "$current" ]]; then path_status=unsafe; ancestor=$current; break; fi
  if [[ -e "$current" ]]; then
    [[ -d "$current" ]] || { path_status=unsafe; ancestor=$current; break; }
    ancestor=$current
  else
    path_status=${path_status:-needs_prepare}
    break
  fi
done
path_status=${path_status:-ready}
owner=$(id -u)
ancestor_owner=$(stat -c %u "$ancestor" 2>/dev/null || printf x)
mode=$(stat -c %a "$ancestor" 2>/dev/null || printf 777)
if [[ "$path_status" != unsafe ]] && (( (8#$mode & 0022) == 0 )) &&
   [[ "$ancestor_owner" == 0 || "$ancestor_owner" == "$owner" ]]; then
  root_status=$path_status
  if [[ "$path_status" == ready && "$ancestor_owner" == 0 && "$owner" != 0 ]]; then root_status=needs_prepare; fi
else
  root_status=unsafe
fi
free=$(df -Pk "$ancestor" 2>/dev/null | awk 'NR==2 {print $4}')
clock=$(date +%s)
thermal=unsupported
if [ -r /sys/class/thermal/thermal_zone0/temp ]; then
  thermal_value=$(cat /sys/class/thermal/thermal_zone0/temp 2>/dev/null || true)
  case "$thermal_value" in ''|*[!0-9-]*) ;; *) thermal=reported:$thermal_value ;; esac
fi
throttle=unsupported
if command -v vcgencmd >/dev/null 2>&1; then
  throttle_value=$(vcgencmd get_throttled 2>/dev/null || true)
  case "$throttle_value" in throttled=0x[0-9a-fA-F]*) throttle=reported:${throttle_value#throttled=} ;; esac
fi
if ! command -v ss >/dev/null 2>&1; then
  conflicts=unsupported
else
  conflicts=none
  IFS=, read -r -a selected_ports <<< "$ports"
  for port in "${selected_ports[@]}"; do
    if ! socket_output=$(ss -H -ltn "sport = :$port" 2>/dev/null); then conflicts=unsupported; break; fi
    if [[ -n "$socket_output" ]]; then conflicts=present; fi
  done
fi
printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$identity" "$host_name" "$architecture" "$cpu" "${memory:-unknown}" "${free:-unknown}" "$clock" "$thermal" "$throttle" "$root_status:$conflicts"
REMOTE
)"; then status=0; else status=$?; fi
    if [[ "$status" == 127 ]]; then printf 'tool-unavailable\n'; return 1; fi
    [[ "$status" == 0 ]] || return 1
    [[ "$output" != *$'\n'* && "$output" == *$'\t'* ]] || return 1
    printf '%s\n' "$output"
}

deploy_transport_ssh_tcp() {
    local ssh_host="$1" host="$2" port="$3" output status
    if output="$(ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$host" "$port" 2>/dev/null <<'REMOTE'
set -euo pipefail
if ! command -v bash >/dev/null || ! command -v timeout >/dev/null; then printf 'tool-unavailable\n'; exit 90; fi
if timeout 8 bash -c 'exec 3<>"/dev/tcp/$1/$2"' _ "$1" "$2"; then printf 'reachable\n'; else printf 'unreachable\n'; exit 91; fi
REMOTE
)"; then status=0; else status=$?; fi
    if [[ "$status" == 0 && "$output" == reachable ]]; then printf 'reachable\n'
    elif [[ "$status" == 90 && "$output" == tool-unavailable || "$status" == 127 ]]; then printf 'tool-unavailable\n'
    else printf 'unreachable\n'; fi
}

deploy_transport_ssh_http() {
    local ssh_host="$1" url="$2" output status
    if output="$(ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$url" 2>/dev/null <<'REMOTE'
set -euo pipefail
if ! command -v bash >/dev/null || ! command -v curl >/dev/null; then printf 'tool-unavailable\n'; exit 90; fi
if curl --silent --show-error --max-time 8 --output /dev/null "$1"; then printf 'reachable\n'; else printf 'unreachable\n'; exit 91; fi
REMOTE
)"; then status=0; else status=$?; fi
    if [[ "$status" == 0 && "$output" == reachable ]]; then printf 'reachable\n'
    elif [[ "$status" == 90 && "$output" == tool-unavailable || "$status" == 127 ]]; then printf 'tool-unavailable\n'
    else printf 'unreachable\n'; fi
}

deploy_transport_docker_context() {
    docker context inspect --format '{{.Name}}' "$1" 2>/dev/null
}

deploy_transport_docker_info() {
    docker --context "$1" info --format '{"ID":{{json .ID}},"Name":{{json .Name}},"Architecture":{{json .Architecture}},"OSType":{{json .OSType}},"ServerVersion":{{json .ServerVersion}}}' 2>/dev/null
}

deploy_transport_docker_compose_version() {
    docker --context "$1" compose version --short 2>/dev/null
}

deploy_transport_docker_conflicts() {
    local context="$1" ports_csv="$2" port result
    IFS=, read -r -a ports <<< "$ports_csv"
    for port in "${ports[@]}"; do
        [[ -n "$port" ]] || continue
        result="$(docker --context "$context" ps --quiet --filter "publish=$port" 2>/dev/null)" || return 1
        [[ -z "$result" ]] || { printf 'conflict\n'; return 0; }
    done
    printf 'none\n'
}

deploy_transport_registry_pull() {
    docker --context "$1" image pull "$2" >/dev/null 2>&1
}

deploy_transport_archive_load() {
    docker --context "$1" image load --input "$2" >/dev/null 2>&1
}

deploy_transport_image_inspect() {
    docker --context "$1" image inspect --format \
      '{"id":{{json .Id}},"architecture":{{json .Architecture}},"os":{{json .Os}},"repoDigests":{{json .RepoDigests}},"labels":{{json .Config.Labels}}}' \
      "$2" 2>/dev/null
}

deploy_transport_copy() {
    local source="$1" ssh_host="$2" destination="$3"
    scp -q -o BatchMode=yes -o ConnectTimeout=8 -r -- "$source" "$ssh_host:$destination" >/dev/null 2>&1
}

deploy_transport_copy_private_file() {
    local source="$1" ssh_host="$2" destination="$3" target runtime_root upload_root token temporary
    [[ -f "$source" && ! -L "$source" ]] || return 1
    target="$(deploy_transport_private_upload_target "$ssh_host")" || return 1
    runtime_root="$(jq -r '.runtimeRoot' <<< "$target")"; upload_root="$runtime_root/.hvo-deploy/uploads"
    [[ "$destination" == "$runtime_root"/* ]] || return 1
    token="$(printf '%s' "${DEPLOY_PRIVATE_UPLOAD_PHASE:?}|$destination|$$" | sha256sum | cut -c1-32)"
    temporary="$upload_root/hvo-upload-${DEPLOY_PRIVATE_UPLOAD_PHASE}-$$-$token.tmp"
    deploy_transport_remote_directories "$ssh_host" "$runtime_root/.hvo-deploy" "$upload_root" || return 1
    deploy_transport_register_private_upload "$ssh_host" "$temporary" || return 1
    if ! scp -q -o BatchMode=yes -o ConnectTimeout=8 -- "$source" "$ssh_host:$temporary" >/dev/null 2>&1; then
        deploy_transport_complete_private_upload "$ssh_host" "$temporary" || return 1
        return 1
    fi
    [[ "${DEPLOY_TEST_FAILPOINT:-}" != abrupt-after-private-scp-upload ]] || exit 75
    if ! ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$temporary" "$destination" 2>/dev/null <<'REMOTE'
set -euo pipefail
temporary=$1; destination=$2; parent=${destination%/*}
trap 'rm -f -- "$temporary"' EXIT
[[ "$temporary" == /* && "$destination" == /* && "$parent" != "$destination" && -d "$parent" && ! -L "$parent" ]] || exit 90
[[ -f "$temporary" && ! -L "$temporary" && "$(stat -c %h "$temporary")" == 1 ]] || exit 91
chmod 600 "$temporary"
mv -Tf -- "$temporary" "$destination"
trap - EXIT
REMOTE
    then
        deploy_transport_complete_private_upload "$ssh_host" "$temporary" || return 1
        return 1
    fi
    deploy_transport_forget_private_upload "$ssh_host" "$temporary"
}

deploy_transport_private_upload_target() {
    local ssh_host="$1" target
    [[ -n "${DEPLOY_PRIVATE_UPLOAD_INVENTORY:-}" ]] || return 1
    target="$(jq -c --arg ssh "$ssh_host" '([.logicHost] + .cameraAgents + (if .sharedServices then [.sharedServices] else [] end)) |
      [.[] | select(.sshHost == $ssh)] | if length == 1 then .[0] else empty end' "$DEPLOY_PRIVATE_UPLOAD_INVENTORY")"
    [[ -n "$target" ]] || return 1
    printf '%s\n' "$target"
}

deploy_transport_initialize_private_upload_registry() {
    local registry="$1" inventory="$2" phase="$3"
    DEPLOY_PRIVATE_UPLOAD_REGISTRY="$registry"; DEPLOY_PRIVATE_UPLOAD_INVENTORY="$inventory"; DEPLOY_PRIVATE_UPLOAD_PHASE="$phase"
    if [[ -e "$registry" || -L "$registry" ]]; then
        [[ -f "$registry" && ! -L "$registry" && "$(stat -c '%u:%h:%a' "$registry" 2>/dev/null)" == "$(id -u):1:600" ]] || return 1
        jq -e --argjson inventory "$(jq -c . "$inventory")" '
          def targets: ([$inventory.logicHost] + $inventory.cameraAgents + (if $inventory.sharedServices then [$inventory.sharedServices] else [] end));
          type == "array" and length <= 256 and all(.[]; . as $entry |
            (keys | sort) == (["phase","kind","target","path"] | sort) and
            (.phase | test("^(up|bootstrap|smoke|measure|acceptance-run|transient-confirm|down)$")) and
            (if .kind == "upload-temp" then
              any(targets[]; . == $entry.target) and
              ($entry.path | startswith($entry.target.runtimeRoot + "/.hvo-deploy/uploads/") and
              (split("/")[-1] | test("^hvo-upload-(up|bootstrap|smoke|measure|acceptance-run|transient-confirm|down)-[0-9]+-[0-9a-f]{32}[.]tmp$")))
             elif .kind == "remote-private" then
               any(targets[]; . == $entry.target) and ($entry.path | startswith($entry.target.runtimeRoot + "/.hvo-deploy/")) and
               (($entry.path | test("/(owner-password|owner[.]cookies|owner[.]headers|[A-Za-z0-9._-]+-control[.]headers|LocalIdentity__AdminPasswordFile|bootstrap-request[.]json|[A-Za-z0-9._-]+-envelope[.]json)$")) or
                 ($entry.path | test("/[.]hvo-deploy/(bootstrap|smoke|measure|campaign|down)-[A-Za-z0-9._-]+/(login[.]html|login-response[.]html|owner-verification[.]json|antiforgery[.]json)$")) or
                 ($entry.path | test("/[.]hvo-deploy/down-[A-Za-z0-9._-]+/(pause|pause-response|final-pause|final-pause-response|continuity-boundary|continuity-[0-9]+|summary-[0-9]+)[.]json$")))
             elif .kind == "local-private" then
               any(targets[]; . == $entry.target) and ($entry.path | startswith(($registry | sub("/private-upload-registry[.]json$"; "")) + "/")) and
               ($entry.path | test("/(central[.]headers|[A-Za-z0-9._-]+-(owner-password|antiforgery[.]headers|antiforgery[.]json|envelope[.]json|bootstrap-request[.]json))$")) and
               (($entry.path | split("/")[-1]) as $basename |
                 if $basename == "central.headers" then $entry.target == $inventory.logicHost
                 else ($basename | startswith($entry.target.name + "-")) end)
             else false end))' --arg registry "$registry" "$registry" >/dev/null || return 1
    else
        deploy_publish_json "$registry" '[]' || return 1
    fi
}

deploy_transport_publish_private_registration() {
    local path="$1" json="$2" failpoint="${DEPLOY_TEST_FAILPOINT:-}" match=
    [[ "$failpoint" != private-registry-publication:* ]] || match="${failpoint#private-registry-publication:}"
    [[ -z "$match" || "$path" != *"$match"* ]] || return 75
    deploy_publish_json "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" "$json" || return 1
}

deploy_transport_register_private_upload() {
    local ssh_host="$1" temporary="$2" target updated
    if [[ -z "${DEPLOY_PRIVATE_UPLOAD_REGISTRY:-}" ]]; then
        DEPLOY_PRIVATE_UPLOADS+="${DEPLOY_PRIVATE_UPLOADS:+$'\n'}$ssh_host"$'\t'"$temporary"
        return 0
    fi
    target="$(deploy_transport_private_upload_target "$ssh_host")" || return 1
    jq -e --arg path "$temporary" --arg phase "$DEPLOY_PRIVATE_UPLOAD_PHASE" '
      .runtimeRoot as $root | ($path | startswith($root + "/.hvo-deploy/uploads/") and
      (split("/")[-1] | test("^hvo-upload-" + $phase + "-[0-9]+-[0-9a-f]{32}[.]tmp$")))' <<< "$target" >/dev/null || return 1
    updated="$(jq -c --arg phase "$DEPLOY_PRIVATE_UPLOAD_PHASE" --arg path "$temporary" --argjson target "$target" '
      if any(.[]; .path == $path) then . else . + [{phase:$phase,kind:"upload-temp",target:$target,path:$path}] end' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY")" || return 1
    deploy_transport_publish_private_registration "$temporary" "$updated" || return 1
}

deploy_transport_register_remote_private() {
    local target="$1" path="$2" updated
    jq -e --arg path "$path" '.runtimeRoot as $root | ($path | startswith($root + "/.hvo-deploy/")) and
      (($path | test("/(owner-password|owner[.]cookies|owner[.]headers|[A-Za-z0-9._-]+-control[.]headers|LocalIdentity__AdminPasswordFile|bootstrap-request[.]json|[A-Za-z0-9._-]+-envelope[.]json)$")) or
        ($path | test("/[.]hvo-deploy/(bootstrap|smoke|measure|campaign|down)-[A-Za-z0-9._-]+/(login[.]html|login-response[.]html|owner-verification[.]json|antiforgery[.]json)$")) or
        ($path | test("/[.]hvo-deploy/down-[A-Za-z0-9._-]+/(pause|pause-response|final-pause|final-pause-response|continuity-boundary|continuity-[0-9]+|summary-[0-9]+)[.]json$")))' \
      <<< "$target" >/dev/null || return 1
    updated="$(jq -c --arg phase "$DEPLOY_PRIVATE_UPLOAD_PHASE" --arg path "$path" --argjson target "$target" '
      if any(.[]; .path == $path and .phase == $phase and .kind == "remote-private" and .target == $target) then .
      elif any(.[]; .path == $path) then error("private path already registered to another entry")
      else . + [{phase:$phase,kind:"remote-private",target:$target,path:$path}] end' \
      "$DEPLOY_PRIVATE_UPLOAD_REGISTRY")" || return 1
    deploy_transport_publish_private_registration "$path" "$updated" || return 1
}

deploy_transport_register_local_private() {
    local target="$1" path="$2" state_root updated
    state_root="$(dirname "$DEPLOY_PRIVATE_UPLOAD_REGISTRY")"
    jq -e --argjson inventory "$(jq -c . "$DEPLOY_PRIVATE_UPLOAD_INVENTORY")" '. as $target |
      any(([$inventory.logicHost] + $inventory.cameraAgents + (if $inventory.sharedServices then [$inventory.sharedServices] else [] end))[]; . == $target)' \
      <<< "$target" >/dev/null || return 1
    [[ "$path" == "$state_root"/* && "$path" =~ /(central[.]headers|[A-Za-z0-9._-]+-(owner-password|antiforgery[.]headers|antiforgery[.]json|envelope[.]json|bootstrap-request[.]json))$ ]] || return 1
    if [[ "${path##*/}" == central.headers ]]; then
        jq -e --argjson inventory "$(jq -c . "$DEPLOY_PRIVATE_UPLOAD_INVENTORY")" '. == $inventory.logicHost' <<< "$target" >/dev/null || return 1
    else
        [[ "${path##*/}" == "$(jq -r '.name' <<< "$target")-"* ]] || return 1
    fi
    updated="$(jq -c --arg phase "$DEPLOY_PRIVATE_UPLOAD_PHASE" --arg path "$path" --argjson target "$target" '
      if any(.[]; .path == $path and .phase == $phase and .kind == "local-private" and .target == $target) then .
      elif any(.[]; .path == $path) then error("private path already registered to another entry")
      else . + [{phase:$phase,kind:"local-private",target:$target,path:$path}] end' \
      "$DEPLOY_PRIVATE_UPLOAD_REGISTRY")" || return 1
    deploy_transport_publish_private_registration "$path" "$updated" || return 1
}

deploy_transport_forget_private_path() {
    local path="$1" updated
    updated="$(jq -c --arg path "$path" '[.[] | select(.path != $path)]' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY")" || return 1
    deploy_publish_json "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" "$updated" || return 1
}

deploy_transport_forget_private_upload() {
    local ssh_host="$1" temporary="$2" updated
    if [[ -z "${DEPLOY_PRIVATE_UPLOAD_REGISTRY:-}" ]]; then
        DEPLOY_PRIVATE_UPLOADS="$(printf '%s\n' "${DEPLOY_PRIVATE_UPLOADS:-}" | grep -Fvx "$ssh_host"$'\t'"$temporary" || true)"
        return 0
    fi
    updated="$(jq -c --arg path "$temporary" '[.[] | select(.path != $path)]' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY")" || return 1
    deploy_publish_json "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" "$updated" || return 1
}

deploy_transport_complete_private_upload() {
    local ssh_host="$1" temporary="$2"
    deploy_transport_cleanup_private_upload "$ssh_host" "$temporary" || return 1
    deploy_transport_forget_private_upload "$ssh_host" "$temporary"
}

deploy_transport_reconcile_private_uploads() {
    local policy="${1:-strict}" entry kind target ssh path retained='[]' failed=false
    [[ -n "${DEPLOY_PRIVATE_UPLOAD_REGISTRY:-}" ]] || return 0
    while IFS= read -r entry; do
        kind="$(jq -r '.kind' <<< "$entry")"; path="$(jq -r '.path' <<< "$entry")"
        if [[ "$kind" == local-private ]]; then
            if [[ ! -L "$path" ]] && { rm -f -- "$path" 2>/dev/null || [[ ! -e "$path" ]]; } && [[ ! -e "$path" && ! -L "$path" ]]; then continue; fi
        else
            target="$(jq -c '.target' <<< "$entry")"; ssh="$(jq -r '.sshHost' <<< "$target")"
            if deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" >/dev/null 2>&1 &&
              { [[ "$kind" == upload-temp ]] && deploy_transport_cleanup_private_upload "$ssh" "$path" >/dev/null 2>&1 ||
                [[ "$kind" == remote-private ]] && deploy_transport_remove_private_files "$ssh" "$path" >/dev/null 2>&1; } &&
              deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" >/dev/null 2>&1; then continue; fi
        fi
        failed=true; retained="$(jq -c --argjson entry "$entry" '. + [$entry]' <<< "$retained")"
    done < <(jq -c '.[]' "$DEPLOY_PRIVATE_UPLOAD_REGISTRY")
    deploy_publish_json "$DEPLOY_PRIVATE_UPLOAD_REGISTRY" "$retained" || return 1
    [[ "$failed" == false || "$policy" != strict ]]
}

deploy_transport_cleanup_private_upload() {
    local ssh_host="$1" temporary="$2" target
    target="$(deploy_transport_private_upload_target "$ssh_host")" || return 1
    jq -e --arg path "$temporary" '.runtimeRoot as $root |
      ($path | startswith($root + "/.hvo-deploy/uploads/") and
        (split("/")[-1] | test("^hvo-upload-(up|bootstrap|smoke|measure|acceptance-run|transient-confirm|down)-[0-9]+-[0-9a-f]{32}[.]tmp$")))' \
      <<< "$target" >/dev/null || return 1
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$temporary" 2>/dev/null <<'REMOTE'
set -euo pipefail
temporary=$1
[[ "$temporary" == /* && "$temporary" != *//* && "$temporary" != */../* && "$temporary" != */./* ]] || exit 90
if [[ -e "$temporary" || -L "$temporary" ]]; then [[ -f "$temporary" && ! -L "$temporary" ]] || exit 91; rm -f -- "$temporary"; fi
[[ ! -e "$temporary" && ! -L "$temporary" ]]
REMOTE
}

deploy_transport_fetch_private_file() {
    local ssh_host="$1" source="$2" destination="$3" expected_uid="${4:-}" expected_gid="${5:-}"
    rm -f -- "$destination"
    if [[ -n "$expected_uid" || -n "$expected_gid" ]]; then
        [[ -n "$expected_uid" && -n "$expected_gid" ]] || return 1
        ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$source" "$expected_uid" "$expected_gid" 2>/dev/null <<'REMOTE' || return 1
set -euo pipefail
source=$1; expected_uid=$2; expected_gid=$3
[[ "$source" == /* && -f "$source" && ! -L "$source" && "$(stat -c '%u:%g:%h:%a' "$source")" == "$expected_uid:$expected_gid:1:600" ]] || exit 90
REMOTE
    fi
    scp -q -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host:$source" "$destination" >/dev/null 2>&1 || { rm -f -- "$destination"; return 1; }
    [[ -f "$destination" && ! -L "$destination" && "$(stat -c %h "$destination" 2>/dev/null)" == 1 ]] || { rm -f -- "$destination"; return 1; }
    chmod 600 "$destination" || { rm -f -- "$destination"; return 1; }
}

deploy_transport_owner_identity() {
    local ssh_host="$1" runtime_owner="$2"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$runtime_owner" 2>/dev/null <<'REMOTE'
set -euo pipefail
runtime_owner=$1
runtime_uid=$(id -u "$runtime_owner"); runtime_gid=$(id -g "$runtime_owner")
[[ "$(id -u)" == "$runtime_uid" && "$(id -g)" == "$runtime_gid" ]] || exit 90
printf '%s\t%s\n' "$runtime_uid" "$runtime_gid"
REMOTE
}

deploy_transport_validate_private_file_identity() {
    local ssh_host="$1" source="$2" expected_uid="$3" expected_gid="$4"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$source" "$expected_uid" "$expected_gid" 2>/dev/null <<'REMOTE'
set -euo pipefail
source=$1; expected_uid=$2; expected_gid=$3
[[ "$source" == /* && -f "$source" && ! -L "$source" && "$(stat -c '%u:%g:%h:%a' "$source")" == "$expected_uid:$expected_gid:1:600" ]] || exit 90
REMOTE
}

deploy_transport_remote_directories() {
    local ssh_host="$1"
    shift
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$@" 2>/dev/null <<'REMOTE'
set -euo pipefail
for path in "$@"; do
  [[ "$path" == /* && "$path" != / && "$path" != *//* && "$path" != */../* && "$path" != */./* ]] || exit 90
  if [[ -e "$path" || -L "$path" ]]; then [[ -d "$path" && ! -L "$path" ]] || exit 91
  else mkdir -m 700 -- "$path"; fi
  chmod 700 -- "$path"
done
REMOTE
}

deploy_transport_remote_seed_state() {
    local ssh_host="$1"
    shift
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$@" 2>/dev/null <<'REMOTE'
set -euo pipefail
for dir in "$@"; do
  [[ "$dir" == /* && "$dir" != / && "$dir" != *//* && "$dir" != */../* && "$dir" != */./* ]] || exit 90
  [[ -d "$dir" && ! -L "$dir" ]] || exit 91
  : > "$dir/.hvo-seed"
done
REMOTE
}

deploy_transport_catalog_install() {
    local ssh_host="$1"
    shift
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$@" 2>/dev/null <<'REMOTE'
set -euo pipefail
stage=$1; install_root=$2; kind=$3; version=$4; expected_sha=$5; expected_length=$6; expected_rows=$7
bundle="$stage/bundle"; database="$bundle/hyg_v42.sqlite"
[[ -d "$bundle" && ! -L "$bundle" && -f "$database" && ! -L "$database" ]] || exit 92
[[ "$(sha256sum "$database" | cut -d' ' -f1)" == "$expected_sha" && "$(wc -c < "$database")" == "$expected_length" ]] || exit 93
[[ "$(sqlite3 -batch -noheader -readonly "$database" 'PRAGMA integrity_check;')" == ok ]] || exit 94
[[ "$(sqlite3 -batch -noheader -readonly "$database" 'SELECT count(*) FROM celestial_objects;')" == "$expected_rows" ]] || exit 95
if [[ "$kind" == production ]]; then
  "$stage/scripts/catalog/install-hyg-v42.sh" install "$bundle" "$install_root" >/dev/null
else
  destination="$install_root/versions/$version"
  mkdir -p -- "$install_root/versions"
  if [[ ! -e "$destination" ]]; then mkdir -m 755 -- "$destination"; cp -a -- "$bundle/." "$destination/"; fi
  temporary="$install_root/.current.tmp.$$"
  ln -s "versions/$version" "$temporary"; mv -Tf -- "$temporary" "$install_root/current"
fi
current="$(readlink "$install_root/current")"; installed="$install_root/$current/hyg_v42.sqlite"
[[ -f "$installed" && "$(sha256sum "$installed" | cut -d' ' -f1)" == "$expected_sha" && "$(wc -c < "$installed")" == "$expected_length" ]] || exit 96
[[ "$(sqlite3 -batch -noheader -readonly "$installed" 'PRAGMA integrity_check; SELECT count(*) FROM celestial_objects;')" == $'ok\n'"$expected_rows" ]] || exit 97
printf 'installed\t%s\t%s\n' "$current" "$expected_sha"
REMOTE
}

deploy_transport_catalog_prepare_scripts() {
    local ssh_host="$1" stage="$2"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$stage" 2>/dev/null <<'REMOTE'
set -euo pipefail
stage=$1
[[ "$stage" == /* && -d "$stage/scripts/catalog" && ! -L "$stage/scripts" && ! -L "$stage/scripts/catalog" ]] || exit 90
for path in "$stage/scripts/catalog/catalog-common.sh" "$stage/scripts/catalog/install-hyg-v42.sh" "$stage/scripts/infra:operation-lock"; do
  [[ -f "$path" && ! -L "$path" && "$(stat -c %h "$path")" == 1 ]] || exit 91
  chmod 700 "$path"
done
REMOTE
}

deploy_transport_catalog_verify() {
    local ssh_host="$1"
    shift
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$@" 2>/dev/null <<'REMOTE'
set -euo pipefail
install_root=$1; expected_version=$2; expected_sha=$3; expected_length=$4; expected_rows=$5
[[ -L "$install_root/current" ]] || exit 90
current=$(readlink "$install_root/current")
[[ "$current" == "versions/$expected_version" ]] || exit 91
database="$install_root/$current/hyg_v42.sqlite"
[[ -f "$database" && ! -L "$database" && "$(sha256sum "$database" | cut -d' ' -f1)" == "$expected_sha" &&
   "$(wc -c < "$database")" == "$expected_length" ]] || exit 92
[[ "$(sqlite3 -batch -noheader -readonly "$database" 'PRAGMA integrity_check; SELECT count(*) FROM celestial_objects;')" == $'ok\n'"$expected_rows" ]] || exit 93
printf 'verified\t%s\t%s\n' "$current" "$expected_sha"
REMOTE
}

deploy_transport_compose() {
    local context="$1" project="$2" env_file="$3" compose_file="$4"
    shift 4
    docker --context "$context" compose --project-name "$project" --env-file "$env_file" --file "$compose_file" "$@" >/dev/null 2>&1
}

deploy_transport_compose_stats() {
    local context="$1" project="$2" env_file="$3" compose_file="$4" service="$5" container
    container="$(docker --context "$context" compose --project-name "$project" --env-file "$env_file" --file "$compose_file" ps -q "$service" 2>/dev/null)" || return 1
    [[ "$container" =~ ^[a-f0-9]{12,64}$ ]] || return 1
    docker --context "$context" stats --no-stream --format \
      '{"cpu":{{json .CPUPerc}},"memory":{{json .MemUsage}},"memoryPercent":{{json .MemPerc}},"blockIo":{{json .BlockIO}},"networkIo":{{json .NetIO}},"pids":{{json .PIDs}}}' \
      "$container" 2>/dev/null
}

deploy_transport_docker_inspect_json() {
    local context="$1" kind="$2" name="$3" format="$4" output error status
    error="$(mktemp /tmp/hvo-docker-inspect.XXXXXX)" || return 1
    if output="$(docker --context "$context" "$kind" inspect --format "$format" "$name" 2>"$error")"; then
        rm -f -- "$error"
        [[ -n "$output" && "$output" != *$'\n'* ]] || return 1
        printf '%s\n' "$output"
        return 0
    else
        status=$?
    fi
    if [[ "$status" == 1 ]] && { grep -Eq "^(Error: No such $kind: $name|Error response from daemon: (get $name: no such $kind|$kind $name not found))$" "$error" ||
      grep -Fqx "Error response from daemon: No such $kind: $name" "$error"; }; then
        rm -f -- "$error"
        return 44
    fi
    rm -f -- "$error"
    return 1
}

deploy_transport_require_container_absent() {
    local status
    deploy_transport_docker_inspect_json "$1" container "$2" '{{json .Id}}' >/dev/null && return 1
    status=$?
    [[ "$status" == 44 ]]
}

deploy_transport_cleanup_helper_cid_path() {
    local directory="$1" cidfile="$2"
    [[ "$directory" == /tmp/hvo-runtime-helper.* && "$cidfile" == "$directory/container.cid" ]] || return 1
    if [[ -e "$cidfile" || -L "$cidfile" ]]; then rm -f "$cidfile" || return 1; fi
    rmdir "$directory" || return 1
    [[ ! -e "$directory" && ! -L "$directory" ]]
}

deploy_transport_compose_logs() {
    local context="$1" project="$2" env_file="$3" compose_file="$4" service="$5" since="$6" destination="$7" temporary
    [[ "$since" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]] || return 1
    temporary="$(mktemp "${destination}.tmp.XXXXXX")" || return 1
    chmod 600 "$temporary" || { rm -f -- "$temporary"; return 1; }
    if ! docker --context "$context" compose --project-name "$project" --env-file "$env_file" --file "$compose_file" \
      logs --no-color --since "$since" --tail 200 "$service" > "$temporary" 2>/dev/null; then
        rm -f -- "$temporary"
        return 1
    fi
    [[ -f "$temporary" && ! -L "$temporary" && "$(stat -c '%h:%a:%s' "$temporary")" =~ ^1:600:[0-9]+$ ]] ||
      { rm -f -- "$temporary"; return 1; }
    mv -fT -- "$temporary" "$destination"
}

deploy_transport_remove_volume() {
    local context="$1" volume="$2" run_id="$3" inventory_hash="$4" allow_absent="${5:-false}" identity status
    [[ "$volume" =~ ^[a-z0-9][a-z0-9_.-]{0,127}$ ]] || return 1
    if identity="$(deploy_transport_docker_inspect_json "$context" volume "$volume" \
       '{"runId":{{json (index .Labels "io.hvoskymonitor.run-id")}},"inventorySha256":{{json (index .Labels "io.hvoskymonitor.inventory-sha256")}}}')"; then :
    else status=$?; [[ "$status" == 44 && "$allow_absent" == true ]] || return 1; return 0; fi
    jq -e --arg run "$run_id" --arg hash "$inventory_hash" '.runId == $run and .inventorySha256 == $hash' <<< "$identity" >/dev/null || return 1
    docker --context "$context" volume rm "$volume" >/dev/null 2>&1
}

deploy_transport_classify_volume() {
    local context="$1" volume="$2" run_id="$3" inventory_hash="$4" identity classification
    identity="$(deploy_transport_docker_inspect_json "$context" volume "$volume" \
      '{"runId":{{json (index .Labels "io.hvoskymonitor.run-id")}},"inventorySha256":{{json (index .Labels "io.hvoskymonitor.inventory-sha256")}}}')" || {
        local status=$?
        [[ "$status" == 44 ]] || return 1
        printf 'absent\n'
        return 0
      }
    classification="$(jq -er --arg run "$run_id" --arg hash "$inventory_hash" '
      if .runId == $run and .inventorySha256 == $hash then "exact"
      elif .runId == $run and (.inventorySha256 | type == "string" and test("^[0-9a-f]{64}$")) and .inventorySha256 != $hash
      then "inventory-label-drift" else empty end' <<< "$identity")" || return 1
    [[ -n "$classification" ]] || return 1
    printf '%s\n' "$classification"
}

deploy_transport_require_volume_absent() {
    local status
    deploy_transport_docker_inspect_json "$1" volume "$2" '{{json .Name}}' >/dev/null && return 1
    status=$?
    [[ "$status" == 44 ]]
}

deploy_transport_remove_network() {
    local context="$1" network="$2" project="$3" run_id="$4" inventory_hash="$5" allow_absent="${6:-false}" identity status
    [[ "$network" =~ ^[a-z0-9][a-z0-9_.-]{0,127}$ && "$project" =~ ^[a-z0-9][a-z0-9_.-]{0,127}$ ]] || return 1
    if identity="$(deploy_transport_docker_inspect_json "$context" network "$network" \
      '{"name":{{json .Name}},"project":{{json (index .Labels "com.docker.compose.project")}},"network":{{json (index .Labels "com.docker.compose.network")}},"runId":{{json (index .Labels "io.hvoskymonitor.run-id")}},"inventorySha256":{{json (index .Labels "io.hvoskymonitor.inventory-sha256")}}}')"; then :
    else status=$?; [[ "$status" == 44 && "$allow_absent" == true ]] || return 1; return 0; fi
    jq -e --arg name "$network" --arg project "$project" --arg run "$run_id" --arg hash "$inventory_hash" \
      '.name == $name and .project == $project and .network == "default" and .runId == $run and .inventorySha256 == $hash' <<< "$identity" >/dev/null || return 1
    docker --context "$context" network rm "$network" >/dev/null 2>&1
}

deploy_transport_validate_network() {
    local context="$1" network="$2" project="$3" run_id="$4" inventory_hash="$5" identity
    identity="$(deploy_transport_docker_inspect_json "$context" network "$network" \
      '{"name":{{json .Name}},"project":{{json (index .Labels "com.docker.compose.project")}},"network":{{json (index .Labels "com.docker.compose.network")}},"runId":{{json (index .Labels "io.hvoskymonitor.run-id")}},"inventorySha256":{{json (index .Labels "io.hvoskymonitor.inventory-sha256")}}}')" || return 1
    jq -e --arg name "$network" --arg project "$project" --arg run "$run_id" --arg hash "$inventory_hash" \
      '.name == $name and .project == $project and .network == "default" and .runId == $run and .inventorySha256 == $hash' <<< "$identity" >/dev/null
}

deploy_transport_require_network_absent() {
    local status
    deploy_transport_docker_inspect_json "$1" network "$2" '{{json .Name}}' >/dev/null && return 1
    status=$?
    [[ "$status" == 44 ]]
}

deploy_transport_compose_service_state() {
    local context="$1" project="$2" env_file="$3" compose_file="$4" service="$5" container identity
    container="$(docker --context "$context" compose --project-name "$project" --env-file "$env_file" --file "$compose_file" ps --all -q "$service" 2>/dev/null)" || return 1
    if [[ -z "$container" ]]; then printf 'absent\n'; return 0; fi
    [[ "$container" =~ ^[a-f0-9]{12,64}$ ]] || return 1
    identity="$(docker --context "$context" container inspect --format \
      '{"project":{{json (index .Config.Labels "com.docker.compose.project")}},"service":{{json (index .Config.Labels "com.docker.compose.service")}},"running":{{json .State.Running}}}' \
      "$container" 2>/dev/null)" || return 1
    jq -e --arg project "$project" --arg service "$service" '.project == $project and .service == $service and (.running | type == "boolean")' <<< "$identity" >/dev/null || return 1
    if [[ "$(jq -r '.running' <<< "$identity")" == true ]]; then printf 'running\n'; else printf 'stopped\n'; fi
}

deploy_transport_require_runtime_root_absent() {
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$1" bash -s -- "$2" 2>/dev/null <<'REMOTE'
set -euo pipefail
root=$1
[[ "$root" == /* && "$root" != / && "$root" != *//* && "$root" != */../* && "$root" != */./* ]] || exit 90
[[ ! -e "$root" && ! -L "$root" ]]
REMOTE
}

deploy_transport_valid_helper_repository() {
    local repository="$1" segment
    [[ "$repository" =~ ^[a-z0-9]([a-z0-9._-]*[a-z0-9])?/[a-z0-9]([a-z0-9._/-]*[a-z0-9])?$ ]] || return 1
    [[ "$repository" != *//* && "$repository" != *'@'* && "$repository" != *':'* ]] || return 1
    IFS=/ read -r -a segments <<< "$repository"
    ((${#segments[@]} >= 2)) || return 1
    for segment in "${segments[@]}"; do
        [[ -n "$segment" && "$segment" != . && "$segment" != .. &&
           "$segment" =~ ^[a-z0-9]([a-z0-9._-]*[a-z0-9])?$ ]] || return 1
    done
}

deploy_transport_helper_image() {
    local env_file="$1" key="$2" line value='' count=0 mode repository digest
    [[ -f "$env_file" && ! -L "$env_file" ]] || return 1
    mode="$(stat -c '%u:%h:%a' -- "$env_file" 2>/dev/null)" || return 1
    [[ "$mode" =~ ^$(id -u):1:([46]00)$ ]] || return 1
    while IFS= read -r line || [[ -n "$line" ]]; do
        [[ "$line" != "$key="* ]] || { value="${line#*=}"; count=$((count + 1)); }
    done < "$env_file"
    [[ "$count" == 1 ]] || return 1
    if [[ "$value" =~ ^sha256:[0-9a-f]{64}$ ]]; then
        :
    elif [[ "$value" == *@sha256:* ]]; then
        repository="${value%@sha256:*}"; digest="${value##*@sha256:}"
        [[ "$value" == "$repository@sha256:$digest" && "$digest" =~ ^[0-9a-f]{64}$ ]] || return 1
        deploy_transport_valid_helper_repository "$repository" || return 1
    else
        return 1
    fi
    printf '%s\n' "$value"
}

# Revalidation prevents accidental or foreign-path deletion; the runtime owner remains trusted not to race its owner-controlled parent.
deploy_transport_remove_runtime_root() {
    local ssh_host="$1" root="$2" marker_digest="$3" allow_absent="${4:-false}" context="${5:-}" helper_env="${6:-}" helper_key="${7:-}" target="${8:-}"
    local inspection runtime_uid mixed helper_image ciddir cidfile cid='' helper_status lifecycle_ok cid_valid
    [[ "$root" != *'\'* ]] || return 1
    inspection="$(timeout --signal=TERM --kill-after=30s 3600 ssh -o BatchMode=yes -o ConnectTimeout=8 \
      -o ServerAliveInterval=10 -o ServerAliveCountMax=3 -- "$ssh_host" bash -s -- "$root" "$marker_digest" "$allow_absent" 2>/dev/null <<'REMOTE'
set -euo pipefail
export LC_ALL=C
root=$1; marker_digest=$2; allow_absent=$3
[[ "$root" == /* && "$root" != / && "$root" != *//* && "$root" != */../* && "$root" != */./* ]] || exit 90
[[ "$marker_digest" =~ ^[0-9a-f]{64}$ ]] || exit 91
if [[ ! -e "$root" && ! -L "$root" && "$allow_absent" == true ]]; then printf 'absent\n'; exit 0; fi
current=/
IFS=/ read -r -a components <<< "${root#/}"
for component in "${components[@]}"; do
  [[ -n "$component" ]] || continue
  [[ "$current" == / ]] && current="/$component" || current="$current/$component"
  [[ -d "$current" && ! -L "$current" ]] || exit 92
done
runtime_uid=$(id -u); control="$root/.hvo-deploy"; marker="$control/ownership"
expected=$'HVO-DEPLOY-ROOT\t1\nmarker\t'"$marker_digest"
expected_bytes=$((${#expected} + 1))
[[ -d "$control" && ! -L "$control" && "$(stat -c %u "$root")" == "$runtime_uid" && "$(stat -c %a "$root")" == 700 &&
   "$(stat -c %u "$control")" == "$runtime_uid" && "$(stat -c %a "$control")" == 700 ]] || exit 93
[[ -f "$marker" && ! -L "$marker" && "$(stat -c %u "$marker")" == "$runtime_uid" &&
   "$(stat -c %h "$marker")" == 1 && "$(stat -c %a "$marker")" == 600 &&
   "$(wc -c < "$marker")" == "$expected_bytes" && "$(<"$marker")" == "$expected" ]] || exit 94
awk -v root="$root" '
  function escaped(path, result, position, character) {
    result=""
    for (position=1; position<=length(path); position++) {
      character=substr(path,position,1)
      if (character == "\\") result=result "\\134"
      else if (character == " ") result=result "\\040"
      else if (character == sprintf("%c",9)) result=result "\\011"
      else if (character == sprintf("%c",10)) result=result "\\012"
      else result=result character
    }
    return result
  }
  BEGIN { root=escaped(root) }
  $5 == root || index($5, root "/") == 1 { exit 42 }
' /proc/self/mountinfo || exit 95
mixed=false
if foreign=$(find -P "$root" -xdev ! -user "$runtime_uid" ! -user 0 -print -quit); then
  [[ -z "$foreign" ]] || exit 97
  if root_owned=$(find -P "$root" -xdev -user 0 -print -quit); then
    [[ -z "$root_owned" ]] || mixed=true
  else
    mixed=true
  fi
else
  # Inaccessible descendants require privileged revalidation; they never authorize host deletion.
  mixed=true
fi
printf '%s\t%s\n' "$runtime_uid" "$mixed"
REMOTE
)" || return 1
    if [[ "$inspection" == absent ]]; then return 0; fi
    IFS=$'\t' read -r runtime_uid mixed <<< "$inspection"
    [[ "$runtime_uid" =~ ^[0-9]+$ && ( "$mixed" == true || "$mixed" == false ) ]] || return 1
    if [[ "$mixed" == true ]]; then
        [[ "$context" =~ ^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$ ]] || return 1
        [[ "$root" != *','* && "$root" != *'"'* ]] || return 1
        helper_image="$(deploy_transport_helper_image "$helper_env" "$helper_key")" || return 1
        ciddir="$(mktemp -d /tmp/hvo-runtime-helper.XXXXXX)" || return 1
        chmod 700 "$ciddir" || { rmdir "$ciddir"; return 1; }
        cidfile="$ciddir/container.cid"
        [[ -d "$ciddir" && ! -L "$ciddir" && "$(stat -c '%u:%a' "$ciddir" 2>/dev/null)" == "$(id -u):700" &&
           ! -e "$cidfile" && ! -L "$cidfile" ]] || { rmdir "$ciddir"; return 1; }
        if [[ -n "$target" ]] && ! deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON"; then
            deploy_transport_cleanup_helper_cid_path "$ciddir" "$cidfile" || return 1
            return 1
        fi
        if (umask 077; timeout --signal=TERM --kill-after=30s 3600 docker --context "$context" run --pull never --rm --interactive --network none --read-only \
          --pids-limit 64 --user 0:0 --cap-drop ALL --cap-add DAC_OVERRIDE --cap-add FOWNER \
          --security-opt no-new-privileges --cidfile "$cidfile" --mount "type=bind,source=$root,target=/runtime-root" \
          --entrypoint /bin/sh "$helper_image" -s -- "$runtime_uid" "$marker_digest" <<'HELPER'
set -eu
export LC_ALL=C
runtime_uid=$1; marker_digest=$2; root=/runtime-root; control=$root/.hvo-deploy; marker=$control/ownership
case "$runtime_uid" in ''|*[!0-9]*) exit 90 ;; esac
expected=$(printf 'HVO-DEPLOY-ROOT\t1\nmarker\t%s' "$marker_digest")
expected_bytes=$((${#expected} + 1))
[ -d "$root" ] && [ ! -L "$root" ] && [ -d "$control" ] && [ ! -L "$control" ] || exit 90
[ "$(stat -c %u "$root")" = "$runtime_uid" ] && [ "$(stat -c %a "$root")" = 700 ] &&
  [ "$(stat -c %u "$control")" = "$runtime_uid" ] && [ "$(stat -c %a "$control")" = 700 ] || exit 91
[ -f "$marker" ] && [ ! -L "$marker" ] && [ "$(stat -c %u "$marker")" = "$runtime_uid" ] &&
  [ "$(stat -c %h "$marker")" = 1 ] && [ "$(stat -c %a "$marker")" = 600 ] &&
  [ "$(wc -c < "$marker")" = "$expected_bytes" ] && [ "$(cat "$marker")" = "$expected" ] || exit 92
awk -v root="$root" '
  function escaped(path, result, position, character) {
    result=""
    for (position=1; position<=length(path); position++) {
      character=substr(path,position,1)
      if (character == "\\") result=result "\\134"
      else if (character == " ") result=result "\\040"
      else if (character == sprintf("%c",9)) result=result "\\011"
      else if (character == sprintf("%c",10)) result=result "\\012"
      else result=result character
    }
    return result
  }
  BEGIN { root=escaped(root) }
  index($5, root "/") == 1 { exit 42 }
' /proc/self/mountinfo || exit 93
if ! foreign=$(find -P "$root" -xdev ! -user "$runtime_uid" ! -user 0 -print -quit); then exit 94; fi
[ -z "$foreign" ] || exit 95
find -P "$root" -xdev -type d -exec chmod u+rwx {} + || exit 96
find -P "$root" -xdev -depth -mindepth 1 ! -path "$marker" ! -type d -exec rm -f {} + || exit 97
find -P "$root" -xdev -depth -mindepth 1 ! -path "$control" -type d -exec rmdir {} \; || exit 98
[ -f "$marker" ] && [ ! -L "$marker" ] && [ "$(stat -c %u "$marker")" = "$runtime_uid" ] &&
  [ "$(stat -c %h "$marker")" = 1 ] && [ "$(stat -c %a "$marker")" = 600 ] &&
  [ "$(wc -c < "$marker")" = "$expected_bytes" ] && [ "$(cat "$marker")" = "$expected" ] || exit 99
if ! remaining=$(find -P "$root" -xdev -mindepth 1 ! -path "$control" ! -path "$marker" -print -quit); then exit 100; fi
[ -z "$remaining" ] || exit 101
HELPER
        ); then helper_status=0; else helper_status=$?; fi
        lifecycle_ok=true; cid_valid=false
        if [[ -f "$cidfile" && ! -L "$cidfile" && "$(stat -c '%u:%h:%a:%s' "$cidfile" 2>/dev/null)" == "$(id -u):1:600:64" ]]; then
            cid="$(<"$cidfile")"
            if [[ "$cid" =~ ^[0-9a-f]{64}$ ]]; then cid_valid=true; fi
        fi
        if [[ "$helper_status" == 0 ]]; then
            if [[ "$cid_valid" != true ]]; then
                lifecycle_ok=false
            elif ! deploy_transport_require_container_absent "$context" "$cid"; then
                docker --context "$context" container rm -f "$cid" >/dev/null 2>&1 || true
                deploy_transport_require_container_absent "$context" "$cid" || lifecycle_ok=false
                lifecycle_ok=false
            fi
        elif [[ "$cid_valid" == true ]]; then
            docker --context "$context" container rm -f "$cid" >/dev/null 2>&1 || true
            deploy_transport_require_container_absent "$context" "$cid" || lifecycle_ok=false
        fi
        deploy_transport_cleanup_helper_cid_path "$ciddir" "$cidfile" || lifecycle_ok=false
        [[ -z "$target" ]] || deploy_phase_correlate_target "$target" "$DEPLOY_IMAGES_PREFLIGHT_JSON" || return 1
        [[ "$helper_status" == 0 && "$lifecycle_ok" == true ]] || return 1
        [[ "${DEPLOY_TEST_FAILPOINT:-}" != after-mixed-runtime-helper-clear ]] || return 75
    fi
    timeout --signal=TERM --kill-after=30s 3600 ssh -o BatchMode=yes -o ConnectTimeout=8 \
      -o ServerAliveInterval=10 -o ServerAliveCountMax=3 -- "$ssh_host" bash -s -- \
      "$root" "$marker_digest" "$runtime_uid" "$mixed" 2>/dev/null <<'REMOTE'
set -euo pipefail
export LC_ALL=C
root=$1; marker_digest=$2; runtime_uid=$3; mixed=$4; control="$root/.hvo-deploy"; marker="$control/ownership"
expected=$'HVO-DEPLOY-ROOT\t1\nmarker\t'"$marker_digest"
expected_bytes=$((${#expected} + 1))
[[ "$root" == /* && "$root" != / && "$root" != *//* && "$root" != */../* && "$root" != */./* ]] || exit 90
current=/; IFS=/ read -r -a components <<< "${root#/}"
for component in "${components[@]}"; do
  [[ -n "$component" ]] || continue
  [[ "$current" == / ]] && current="/$component" || current="$current/$component"
  [[ -d "$current" && ! -L "$current" ]] || exit 91
done
[[ "$(id -u)" == "$runtime_uid" && -d "$control" && ! -L "$control" &&
   "$(stat -c %u "$root")" == "$runtime_uid" && "$(stat -c %a "$root")" == 700 &&
   "$(stat -c %u "$control")" == "$runtime_uid" && "$(stat -c %a "$control")" == 700 ]] || exit 92
[[ -f "$marker" && ! -L "$marker" && "$(stat -c %u "$marker")" == "$runtime_uid" &&
   "$(stat -c %h "$marker")" == 1 && "$(stat -c %a "$marker")" == 600 &&
   "$(wc -c < "$marker")" == "$expected_bytes" && "$(<"$marker")" == "$expected" ]] || exit 93
awk -v root="$root" '
  function escaped(path, result, position, character) {
    result=""
    for (position=1; position<=length(path); position++) {
      character=substr(path,position,1)
      if (character == "\\") result=result "\\134"
      else if (character == " ") result=result "\\040"
      else if (character == sprintf("%c",9)) result=result "\\011"
      else if (character == sprintf("%c",10)) result=result "\\012"
      else result=result character
    }
    return result
  }
  BEGIN { root=escaped(root) }
  $5 == root || index($5, root "/") == 1 { exit 42 }
' /proc/self/mountinfo || exit 94
if ! foreign=$(find -P "$root" -xdev ! -user "$runtime_uid" ! -user 0 -print -quit); then exit 95; fi
[[ -z "$foreign" ]] || exit 96
if ! root_owned=$(find -P "$root" -xdev -user 0 -print -quit); then exit 97; fi
[[ "$mixed" == true || -z "$root_owned" ]] || exit 98
if [[ "$mixed" == false ]]; then
  find -P "$root" -xdev -type d -exec chmod u+rwx -- {} + || exit 99
  find -P "$root" -xdev -depth -mindepth 1 ! -path "$marker" ! -type d -exec rm -f -- {} + || exit 100
  find -P "$root" -xdev -depth -mindepth 1 ! -path "$control" -type d -exec rmdir -- {} + || exit 101
fi
if ! remaining=$(find -P "$root" -xdev -mindepth 1 ! -path "$control" ! -path "$marker" -print -quit); then exit 102; fi
[[ -z "$remaining" ]] || exit 103
[[ -f "$marker" && ! -L "$marker" && "$(stat -c %u "$marker")" == "$runtime_uid" &&
   "$(stat -c %h "$marker")" == 1 && "$(stat -c %a "$marker")" == 600 &&
   "$(wc -c < "$marker")" == "$expected_bytes" && "$(<"$marker")" == "$expected" ]] || exit 104
rm -f -- "$marker"; rmdir -- "$control"; rmdir -- "$root"
[[ ! -e "$root" && ! -L "$root" ]]
REMOTE
}

deploy_transport_remove_prepare_lock() {
    local ssh_host="$1" root="$2" marker_digest="$3" run_id="$4" lock_name="$5"
    local root_new="$6" control_new="$7" marker_new="$8" allow_partial="${9:-false}" failpoint="${10:-none}" target="${11:-}"
    timeout --signal=TERM --kill-after=30s 3600 ssh -o BatchMode=yes -o ConnectTimeout=8 \
      -o ServerAliveInterval=10 -o ServerAliveCountMax=3 -- "$ssh_host" bash -s -- \
      "$root" "$marker_digest" "$run_id" "$lock_name" "$root_new" "$control_new" "$marker_new" "$allow_partial" "$failpoint" "$target" 2>/dev/null <<'REMOTE'
set -euo pipefail
export LC_ALL=C
root=$1; marker_digest=$2; run_id=$3; lock_name=$4; root_new=$5; control_new=$6; marker_new=$7; allow_partial=$8; failpoint=$9; target=${10}
[[ "$root" == /* && "$root" != / && "$root" != *//* && "$root" != */../* && "$root" != */./* ]] || exit 90
[[ "$marker_digest" =~ ^[0-9a-f]{64}$ && "$run_id" =~ ^[a-z0-9][a-z0-9-]{0,31}$ &&
   "$lock_name" =~ ^\.hvo-deploy-prepare-[0-9a-f]{32}\.lock$ ]] || exit 91
[[ "$root_new" == true && ( "$control_new" == true || "$control_new" == false ) &&
   ( "$marker_new" == true || "$marker_new" == false ) && ( "$allow_partial" == true || "$allow_partial" == false ) ]] || exit 92
parent=${root%/*}; [[ -n "$parent" ]] || parent=/
current=/; IFS=/ read -r -a components <<< "${parent#/}"
for component in "${components[@]}"; do
  [[ -n "$component" ]] || continue
  [[ "$current" == / ]] && current="/$component" || current="$current/$component"
  [[ -d "$current" && ! -L "$current" ]] || exit 93
done
runtime_uid=$(id -u); parent_mode=$(stat -c %a "$parent")
[[ "$(stat -c %u "$parent")" == "$runtime_uid" ]] || exit 94
(( (8#$parent_mode & 0200) != 0 && (8#$parent_mode & 0022) == 0 )) || exit 94
[[ ! -e "$root" && ! -L "$root" ]] || exit 95
lock_path="$parent/$lock_name"; state_path="$lock_path.state"
if [[ ! -e "$lock_path" && ! -L "$lock_path" && ! -e "$state_path" && ! -L "$state_path" && "$allow_partial" == true ]]; then exit 0; fi
[[ -f "$lock_path" && ! -L "$lock_path" && "$(stat -c %u:%h:%a "$lock_path")" == "$runtime_uid:1:600" ]] || exit 96
exec 9<>"$lock_path" || exit 96
flock -n 9 || exit 97
lock_expected=$'HVO-DEPLOY-PREPARE-LOCK\t1\nmarker\t'"$marker_digest"
lock_bytes=$((${#lock_expected} + 1))
[[ "$(wc -c < "$lock_path")" == "$lock_bytes" && "$(<"$lock_path")" == "$lock_expected" ]] || exit 98
if [[ -e "$state_path" || -L "$state_path" ]]; then
  [[ -f "$state_path" && ! -L "$state_path" && "$(stat -c %u:%h:%a "$state_path")" == "$runtime_uid:1:600" ]] || exit 99
  state_expected=$(printf 'HVO-DEPLOY-PREPARE-STATE\t1\nmarker\t%s\ncreatingRun\t%s\nrootNew\t%s\ncontrolNew\t%s\nmarkerNew\t%s' \
    "$marker_digest" "$run_id" "$root_new" "$control_new" "$marker_new")
  state_bytes=$((${#state_expected} + 1))
  [[ "$(wc -c < "$state_path")" == "$state_bytes" && "$(<"$state_path")" == "$state_expected" ]] || exit 100
  rm -f -- "$state_path" || exit 101
  [[ "$failpoint" != "after-delete-prepare-lock-state:$target" ]] || exit 75
else
  [[ "$allow_partial" == true ]] || exit 102
fi
[[ ! -e "$root" && ! -L "$root" && -f "$lock_path" && ! -L "$lock_path" &&
   "$(stat -c %u:%h:%a "$lock_path")" == "$runtime_uid:1:600" &&
   "$(wc -c < "$lock_path")" == "$lock_bytes" && "$(<"$lock_path")" == "$lock_expected" ]] || exit 103
rm -f -- "$lock_path" || exit 104
[[ ! -e "$lock_path" && ! -L "$lock_path" && ! -e "$state_path" && ! -L "$state_path" ]]
REMOTE
}

deploy_transport_require_prepare_lock_absent() {
    local ssh_host="$1" root="$2" lock_name="$3"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$root" "$lock_name" 2>/dev/null <<'REMOTE'
set -euo pipefail
root=$1; lock_name=$2
[[ "$root" == /* && "$root" != / && "$lock_name" =~ ^\.hvo-deploy-prepare-[0-9a-f]{32}\.lock$ ]] || exit 90
parent=${root%/*}; [[ -n "$parent" ]] || parent=/
current=/; IFS=/ read -r -a components <<< "${parent#/}"
for component in "${components[@]}"; do
  [[ -n "$component" ]] || continue
  [[ "$current" == / ]] && current="/$component" || current="$current/$component"
  [[ -d "$current" && ! -L "$current" ]] || exit 91
done
lock_path="$parent/$lock_name"
[[ ! -e "$root" && ! -L "$root" && ! -e "$lock_path" && ! -L "$lock_path" && ! -e "$lock_path.state" && ! -L "$lock_path.state" ]]
REMOTE
}

deploy_transport_validate_runtime_root() {
    local ssh_host="$1" root="$2" marker_digest="$3"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$root" "$marker_digest" 2>/dev/null <<'REMOTE'
set -euo pipefail
root=$1; marker_digest=$2
[[ "$root" == /* && "$root" != / && "$marker_digest" =~ ^[0-9a-f]{64}$ ]] || exit 90
current=/
IFS=/ read -r -a components <<< "${root#/}"
for component in "${components[@]}"; do
  [[ -n "$component" ]] || continue
  [[ "$current" == / ]] && current="/$component" || current="$current/$component"
  [[ -d "$current" && ! -L "$current" ]] || exit 91
done
marker="$root/.hvo-deploy/ownership"; expected=$'HVO-DEPLOY-ROOT\t1\nmarker\t'"$marker_digest"
expected_bytes=$((${#expected} + 1))
[[ -f "$marker" && ! -L "$marker" && "$(stat -c %h "$marker")" == 1 &&
   "$(wc -c < "$marker")" == "$expected_bytes" && "$(<"$marker")" == "$expected" ]] || exit 92
REMOTE
}

deploy_transport_http_ready() {
    local ssh_host="$1" url="$2"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$url" 2>/dev/null <<'REMOTE'
set -euo pipefail
for _ in $(seq 1 60); do
  if curl --fail --silent --show-error --max-time 5 "$1" >/dev/null; then exit 0; fi
  sleep 2
done
exit 1
REMOTE
}

deploy_transport_oidc_ready() {
    local ssh_host="$1" authority="$2" discovery issuer token_endpoint discovery_json poll_seconds
    discovery="${authority%/}/.well-known/openid-configuration"
    issuer="${authority%/}/"
    token_endpoint="${authority%/}/connect/token"
    poll_seconds="${DEPLOY_TEST_POLL_SECONDS:-2}"
    [[ "$poll_seconds" =~ ^[0-9]+$ ]] || return 1
    if ! discovery_json="$(timeout --signal=TERM --kill-after=5s 430 ssh -o BatchMode=yes -o ConnectTimeout=8 -o ServerAliveInterval=10 \
      -o ServerAliveCountMax=3 -- "$ssh_host" bash -s -- "$discovery" "$poll_seconds" 2>/dev/null <<'REMOTE'
set -euo pipefail
for _ in $(seq 1 60); do
  if ! response=$(curl --silent --show-error --max-time 5 --max-filesize 65536 --write-out $'\n%{http_code}' "$1"); then
    sleep "$2"
    continue
  fi
  status=${response##*$'\n'}
  body=${response%$'\n'*}
  if [[ "$status" == 200 && "${#body}" -le 65536 ]]; then
    printf '%s' "$body"
    exit 0
  fi
  sleep "$2"
done
exit 1
REMOTE
)"; then
        return 1
    fi
    jq -e --arg issuer "$issuer" --arg token "$token_endpoint" '
      type == "object" and (.issuer | type == "string") and (.token_endpoint | type == "string") and
      .issuer == $issuer and .token_endpoint == $token
    ' <<< "$discovery_json" >/dev/null 2>&1
}

deploy_transport_http_private() {
    local ssh_host="$1" method="$2" url="$3" body_path="$4" header_path="$5" cookie_path="$6" output_path="$7"
    local remote_url="${url//&/\\&}"
    [[ -n "$body_path" ]] || body_path=-
    [[ -n "$header_path" ]] || header_path=-
    [[ -n "$cookie_path" ]] || cookie_path=-
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- \
      "$method" "$remote_url" "$body_path" "$header_path" "$cookie_path" "$output_path" 2>/dev/null <<'REMOTE'
set -euo pipefail
method=$1; url=$2; body=$3; headers=$4; cookies=$5; output=$6
[[ "$body" != - ]] || body=
[[ "$headers" != - ]] || headers=
[[ "$cookies" != - ]] || cookies=
[[ "$method" == GET || "$method" == POST ]] || exit 90
[[ "$output" == /* && "$output" != *//* && "$output" != */../* && "$output" != */./* ]] || exit 91
[[ -z "$body" || ( "$body" == /* && "$body" != *//* && "$body" != */../* && "$body" != */./* ) ]] || exit 91
[[ -z "$headers" || ( "$headers" == /* && "$headers" != *//* && "$headers" != */../* && "$headers" != */./* ) ]] || exit 91
[[ -z "$body" || ( -f "$body" && ! -L "$body" && "$(stat -c %a "$body")" == 600 ) ]] || exit 92
[[ -z "$headers" || ( -f "$headers" && ! -L "$headers" && "$(stat -c %a "$headers")" == 600 ) ]] || exit 93
args=(--silent --show-error --max-time 30 --max-filesize 1048576 --request "$method" --output "$output" --write-out '%{http_code}')
[[ -z "$body" ]] || args+=(--header 'Content-Type: application/json' --data-binary "@$body")
[[ -z "$headers" ]] || args+=(--header "@$headers")
if [[ -n "$cookies" ]]; then
  [[ "$cookies" == /* ]] || exit 94
  args+=(--cookie "$cookies" --cookie-jar "$cookies")
fi
status=$(curl "${args[@]}" "$url") || exit 95
[[ "$status" =~ ^[0-9]{3}$ && -f "$output" && ! -L "$output" ]] || exit 96
if jq -e . "$output" >/dev/null 2>&1; then
  normalized="$output.normalized"; trap 'rm -f -- "$normalized"' EXIT
  [[ ! -e "$normalized" && ! -L "$normalized" ]] || exit 97
  (umask 077; jq -c 'walk(if type == "object" then with_entries(.key = ((.key[0:1] | ascii_downcase) + .key[1:])) else . end)' "$output" > "$normalized") || exit 98
  chmod 600 "$normalized" && mv -T "$normalized" "$output" || exit 99
fi
chmod 600 "$output"
[[ -z "$cookies" || ( -f "$cookies" && ! -L "$cookies" ) ]] || exit 97
[[ -z "$cookies" ]] || chmod 600 "$cookies"
printf '%s\n' "$status"
REMOTE
}

deploy_transport_http_hash_private() {
    local ssh_host="$1" url="$2" header_path="$3" temporary_root="$4"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$url" "$header_path" "$temporary_root" 2>/dev/null <<'REMOTE'
set -euo pipefail
url=$1; headers=$2; root=$3
[[ "$url" == http://* && "$headers" == /* && "$root" == /* && -d "$root" && ! -L "$root" ]] || exit 90
[[ -f "$headers" && ! -L "$headers" && "$(stat -c '%h:%a' "$headers")" == "1:600" ]] || exit 91
payload="$root/retrieval.$$.bin"; response_headers="$root/retrieval.$$.headers"
authorization="$root/retrieval.$$.authorization.json"; cookies="$root/retrieval.$$.cookies"
cleanup() {
  rm -f -- "$payload" "$response_headers" "$authorization" "$cookies"
  [[ ! -e "$payload" && ! -L "$payload" && ! -e "$response_headers" && ! -L "$response_headers" &&
     ! -e "$authorization" && ! -L "$authorization" && ! -e "$cookies" && ! -L "$cookies" ]]
}
trap cleanup EXIT
umask 077
authorization_status=$(curl --silent --show-error --max-time 30 --output "$authorization" --write-out '%{http_code}' \
  --header "@$headers" --header 'Content-Type: application/json' --cookie-jar "$cookies" \
  --data-binary '{"range":null}' "${url%/content}/download-authorizations") || exit 92
[[ "$authorization_status" == 200 && -f "$authorization" && ! -L "$authorization" && -f "$cookies" && ! -L "$cookies" ]] || exit 93
chmod 600 "$authorization" "$cookies"
status=$(curl --silent --show-error --max-time 60 --output "$payload" --dump-header "$response_headers" --write-out '%{http_code}' \
  --header "@$headers" --cookie "$cookies" "$url") || exit 92
[[ "$status" =~ ^[0-9]{3}$ && -f "$payload" && ! -L "$payload" && -f "$response_headers" && ! -L "$response_headers" ]] || exit 93
chmod 600 "$payload" "$response_headers"
hash=$(sha256sum "$payload"); hash=${hash%% *}; bytes=$(wc -c < "$payload")
declared=$(awk 'BEGIN{IGNORECASE=1} /^X-Artifact-SHA256:/ {gsub("\r", "", $2); print toupper($2)}' "$response_headers")
[[ "$declared" =~ ^[0-9A-F]{64}$ && "$bytes" =~ ^[0-9]+$ ]] || exit 94
printf '%s\t%s\t%s\t%s\n' "$status" "${hash^^}" "$bytes" "$declared"
REMOTE
}

deploy_transport_owner_login() {
    local ssh_host="$1" endpoint="$2" email="$3" password_path="$4" cookie_path="$5" scratch_root="$6"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- \
      "$endpoint" "$email" "$password_path" "$cookie_path" "$scratch_root" 2>/dev/null <<'REMOTE'
set -euo pipefail
endpoint=$1; email=$2; password=$3; cookies=$4; scratch=$5
[[ "$endpoint" == http://* && "$password" == /* && "$cookies" == /* && "$scratch" == /* ]] || exit 90
[[ -f "$password" && ! -L "$password" && "$(stat -c %a "$password")" == 600 ]] || exit 91
[[ -d "$scratch" && ! -L "$scratch" ]] || exit 91
chmod 700 "$scratch"
login="$scratch/login.html"; response="$scratch/login-response.html"; verification="$scratch/owner-verification.json"
cleanup() { rm -f -- "$login" "$response" "$verification" "$password"; }
trap cleanup EXIT
status=$(curl --silent --show-error --max-time 30 --output "$login" --write-out '%{http_code}' \
  --cookie "$cookies" --cookie-jar "$cookies" "$endpoint/Account/Login") || exit 92
[[ "$status" == 200 && -f "$login" ]] || exit 93
html=$(<"$login")
pattern='name="__RequestVerificationToken"[^>]*value="([^"]+)"'
[[ "$html" =~ $pattern ]] || exit 94
token=${BASH_REMATCH[1]}
status=$(curl --silent --show-error --max-time 30 --location --output "$response" --write-out '%{http_code}' \
  --cookie "$cookies" --cookie-jar "$cookies" \
  --data-urlencode "__RequestVerificationToken=$token" --data-urlencode "Input.Email=$email" \
  --data-urlencode "Input.Password@$password" --data-urlencode 'Input.RememberMe=false' --data-urlencode '_handler=login' \
  "$endpoint/Account/Login") || exit 95
[[ "$status" == 200 && -f "$cookies" && ! -L "$cookies" ]] || exit 96
chmod 600 "$cookies"
status=$(curl --silent --show-error --max-time 30 --output "$verification" --write-out '%{http_code}' \
  --cookie "$cookies" --cookie-jar "$cookies" "$endpoint/api/internal/deployment/antiforgery") || exit 97
[[ "$status" == 200 && -f "$verification" && ! -L "$verification" ]] || exit 98
REMOTE
}

deploy_transport_derive_idempotent_headers() {
    local ssh_host="$1" base_path="$2" derived_path="$3" key="$4"
    [[ "$key" =~ ^[A-Za-z0-9._:-]{1,128}$ ]] || return 1
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$base_path" "$derived_path" "$key" 2>/dev/null <<'REMOTE'
set -euo pipefail
base=$1; derived=$2; key=$3
[[ "$base" == /* && "$derived" == /* && "$base" != "$derived" && -f "$base" && ! -L "$base" && "$(stat -c '%h:%a' "$base")" == "1:600" ]] || exit 90
[[ ! -e "$derived" && ! -L "$derived" ]] || exit 91
[[ "$(grep -c '^Idempotency-Key:' "$base" || true)" == 0 && "$(grep -Ec '^[A-Za-z0-9-]+: .+$' "$base" || true)" -ge 1 ]] || exit 92
umask 077
cp -- "$base" "$derived"
printf 'Idempotency-Key: %s\n' "$key" >> "$derived"
chmod 600 "$derived"
[[ "$(grep -c '^Idempotency-Key:' "$derived")" == 1 && "$(grep -Fxc "Idempotency-Key: $key" "$derived")" == 1 ]] || exit 93
REMOTE
}

deploy_transport_derive_review_headers() {
    local ssh_host="$1" base_path="$2" derived_path="$3" key="$4" etag="$5"
    [[ "$key" =~ ^[A-Za-z0-9._:-]{1,128}$ && "$etag" =~ ^\"[A-Za-z0-9_-]+\"$ ]] || return 1
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$base_path" "$derived_path" "$key" "$etag" 2>/dev/null <<'REMOTE'
set -euo pipefail
base=$1; derived=$2; key=$3; etag=$4
[[ "$base" == /* && "$derived" == /* && "$base" != "$derived" && -f "$base" && ! -L "$base" && "$(stat -c '%h:%a' "$base")" == "1:600" ]] || exit 90
[[ ! -e "$derived" && ! -L "$derived" ]] || exit 91
[[ "$(grep -Ec '^(Idempotency-Key|If-Match):' "$base" || true)" == 0 && "$(grep -Ec '^[A-Za-z0-9-]+: .+$' "$base" || true)" -ge 1 ]] || exit 92
umask 077
cp -- "$base" "$derived"
printf 'Idempotency-Key: %s\nIf-Match: %s\n' "$key" "$etag" >> "$derived"
chmod 600 "$derived"
[[ "$(grep -Fxc "Idempotency-Key: $key" "$derived")" == 1 && "$(grep -Fxc "If-Match: $etag" "$derived")" == 1 ]] || exit 93
REMOTE
}

deploy_transport_remove_private_files() {
    local ssh_host="$1"
    shift
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$@" 2>/dev/null <<'REMOTE'
set -euo pipefail
for path in "$@"; do
  [[ "$path" == /* && "$path" != *//* && "$path" != */../* && "$path" != */./* ]] || exit 90
  if [[ -e "$path" || -L "$path" ]]; then [[ -f "$path" && ! -L "$path" ]] || exit 91; rm -f -- "$path"; fi
  [[ ! -e "$path" && ! -L "$path" ]] || exit 92
done
REMOTE
}

deploy_transport_remove_campaign_directory() {
    local ssh_host="$1" path="$2"
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$path" 2>/dev/null <<'REMOTE'
set -euo pipefail
path=$1
[[ "$path" == /*/.hvo-deploy/campaign-[a-z0-9]* && "$path" != *//* && "$path" != */../* && "$path" != */./* ]] || exit 90
if [[ -e "$path" || -L "$path" ]]; then [[ -d "$path" && ! -L "$path" ]] || exit 91; rm -rf -- "$path"; fi
[[ ! -e "$path" && ! -L "$path" ]]
REMOTE
}

deploy_transport_prepare_target() {
    local ssh_host="$1"
    shift
    local remote_script output status reason prepared_root prepared_control prepared_marker prepared_digest prepared_run expected_digest="${8}"
    remote_script="$(cat <<'REMOTE'
set -euo pipefail
root=$1; mode=$2; runtime_owner=$3; run_id=$4; inventory_hash=$5; target=$6
installation_id=$7; marker_digest=$8; lock_name=$9; expected_machine=${10}; expected_host=${11}
if (( $# == 12 )); then failpoint=; operation=${12}; else failpoint=${12}; operation=${13}; fi
fail() { printf 'failed\t%s\n' "$1"; exit 1; }
[[ "$(cat /etc/machine-id 2>/dev/null || hostname)" == "$expected_machine" && "$(hostname)" == "$expected_host" ]] || fail identity-mismatch
runtime_uid=$(id -u "$runtime_owner" 2>/dev/null) || fail owner-unavailable
[[ "$(id -u)" == "$runtime_uid" ]] || fail owner-unavailable
[[ "$root" == /* && "$root" != / && "$root" != *//* ]] || fail unsafe-path
IFS=/ read -r -a lexical <<< "${root#/}"
for component in "${lexical[@]}"; do [[ "$component" != . && "$component" != .. && -n "$component" ]] || fail unsafe-path; done
parent=${root%/*}; [[ -n "$parent" ]] || parent=/
validate_components() {
  local limit=$1 current=/ component owner mode_value
  IFS=/ read -r -a parts <<< "${limit#/}"
  for component in "${parts[@]}"; do
    [[ -n "$component" ]] || continue
    [[ "$current" == / ]] && current="/$component" || current="$current/$component"
    [[ ! -L "$current" ]] || fail unsafe-path
    if [[ -e "$current" ]]; then
      [[ -d "$current" ]] || fail unsafe-path
      owner=$(stat -c %u "$current" 2>/dev/null) || fail unsafe-path
      mode_value=$(stat -c %a "$current" 2>/dev/null) || fail unsafe-path
      (( (8#$mode_value & 0022) == 0 )) || fail unsafe-path
      [[ "$owner" == 0 || "$owner" == "$runtime_uid" ]] || fail unsafe-owner
    else
      return 2
    fi
  done
}
validate_components "$parent" || fail parent-missing
parent_owner=$(stat -c %u "$parent" 2>/dev/null) || fail unsafe-path
parent_mode=$(stat -c %a "$parent" 2>/dev/null) || fail unsafe-path
[[ "$parent_owner" == "$runtime_uid" ]] || fail owner-controlled-parent-required
(( (8#$parent_mode & 0200) != 0 && (8#$parent_mode & 0022) == 0 )) || fail owner-controlled-parent-required
lock_path="$parent/$lock_name"
if [[ -L "$lock_path" ]]; then fail unsafe-lock; fi
if [[ ! -e "$lock_path" ]]; then
  [[ "$operation" != validate ]] || fail completed-drift
  (set -o noclobber; umask 077; : > "$lock_path") 2>/dev/null || true
fi
[[ -f "$lock_path" && ! -L "$lock_path" && "$(stat -c %h "$lock_path" 2>/dev/null)" == 1 ]] || fail unsafe-lock
if [[ "$operation" == validate ]]; then exec 9<"$lock_path" || fail unsafe-lock
else exec 9<>"$lock_path" || fail unsafe-lock; fi
flock -n 9 || fail lock-contended
if [[ "$operation" == validate ]]; then [[ "$(stat -c %u:%a "$lock_path" 2>/dev/null)" == "$runtime_uid:600" ]] || fail completed-drift
else chmod 600 "$lock_path" || fail unsafe-lock; fi
lock_expected=$'HVO-DEPLOY-PREPARE-LOCK\t1\nmarker\t'"$marker_digest"
lock_actual=$(<"$lock_path")
if [[ -z "$lock_actual" ]]; then
  [[ "$operation" != validate ]] || fail completed-drift
  printf '%s\n' "$lock_expected" >&9; lock_actual=$lock_expected
fi
[[ "$lock_actual" == "$lock_expected" ]] || fail lock-content-mismatch
validate_components "$parent" || fail unsafe-path
state_path="$lock_path.state"; state_preexisting=false
if [[ -e "$state_path" || -L "$state_path" ]]; then
  [[ -f "$state_path" && ! -L "$state_path" && "$(stat -c %h "$state_path" 2>/dev/null)" == 1 ]] || fail state-mismatch
  state_preexisting=true; state_header=; state_marker=; creating_run=; root_new=; control_new=; marker_new=
  while IFS=$'\t' read -r key value; do
    case "$key" in HVO-DEPLOY-PREPARE-STATE) state_header=$value ;; marker) state_marker=$value ;; creatingRun) creating_run=$value ;; rootNew) root_new=$value ;; controlNew) control_new=$value ;; markerNew) marker_new=$value ;; *) fail state-mismatch ;; esac
  done < "$state_path"
  [[ "$state_header" == 1 && "$state_marker" == "$marker_digest" && "$creating_run" =~ ^[a-z0-9][a-z0-9-]{0,31}$ && ( "$root_new" == true || "$root_new" == false ) &&
     ( "$control_new" == true || "$control_new" == false ) && ( "$marker_new" == true || "$marker_new" == false ) ]] || fail state-mismatch
else
  creating_run=$run_id; root_new=false; control_new=false; marker_new=false
fi
validate_completed_layout() {
  [[ "$state_preexisting" == true && -d "$root" && ! -L "$root" && -d "$root/.hvo-deploy" && ! -L "$root/.hvo-deploy" ]] || fail completed-drift
  validate_components "$root/.hvo-deploy" || fail completed-drift
  marker="$root/.hvo-deploy/ownership"
  [[ -f "$marker" && ! -L "$marker" && "$(stat -c %h "$marker" 2>/dev/null)" == 1 ]] || fail completed-drift
  marker_expected=$'HVO-DEPLOY-ROOT\t1\nmarker\t'"$marker_digest"
  [[ "$(<"$marker")" == "$marker_expected" ]] || fail completed-drift
  [[ "$(stat -c %u:%a "$root" 2>/dev/null)" == "$runtime_uid:700" &&
     "$(stat -c %u:%a "$root/.hvo-deploy" 2>/dev/null)" == "$runtime_uid:700" &&
     "$(stat -c %u:%a "$marker" 2>/dev/null)" == "$runtime_uid:600" &&
     "$(stat -c %u:%a "$state_path" 2>/dev/null)" == "$runtime_uid:600" ]] || fail completed-drift
}
if [[ "$operation" == validate ]]; then
  validate_completed_layout
  if [[ "$creating_run" == "$run_id" ]]; then effective_root=$root_new; effective_control=$control_new; effective_marker=$marker_new
  elif [[ "$mode" == persistent ]]; then effective_root=false; effective_control=false; effective_marker=false
  else fail completed-drift; fi
  printf 'validated\t%s\t%s\t%s\t%s\t%s\n' "$marker_digest" "$creating_run" "$effective_root" "$effective_control" "$effective_marker"
  exit 0
fi
if [[ "$state_preexisting" == true && "$creating_run" != "$run_id" ]]; then
  [[ "$mode" == persistent ]] || fail state-mismatch
  validate_completed_layout
  printf 'prepared\tfalse\tfalse\tfalse\t%s\t%s\n' "$marker_digest" "$creating_run"
  exit 0
fi
control="$root/.hvo-deploy"; marker="$control/ownership"
if [[ -e "$root" || -L "$root" ]]; then
  [[ -d "$root" && ! -L "$root" ]] || fail unsafe-path
  validate_components "$root" || fail unsafe-path
  if [[ -e "$control" || -L "$control" ]]; then [[ -d "$control" && ! -L "$control" ]] || fail unsafe-path; validate_components "$control" || fail unsafe-path; fi
  if [[ -f "$marker" && ! -L "$marker" ]]; then
    [[ "$(stat -c %h "$marker" 2>/dev/null)" == 1 ]] || fail marker-mismatch
    marker_actual=$(<"$marker")
    marker_expected=$'HVO-DEPLOY-ROOT\t1\nmarker\t'"$marker_digest"
    [[ "$marker_actual" == "$marker_expected" ]] || fail marker-mismatch
    [[ "$state_preexisting" == true ]] || fail state-mismatch
  else
    nonempty=$(find "$root" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null) || fail unsafe-path
    if [[ "$mode" == isolated ]]; then
      [[ -z "$nonempty" && "$state_preexisting" == true && "$root_new" == true ]] || fail unmarked-root
    else
      [[ -z "$nonempty" ]] || fail nonempty-unmarked-root
    fi
  fi
else
  [[ "$state_preexisting" == false || "$root_new" == true ]] || fail state-mismatch
  root_new=true
fi
if [[ "$state_preexisting" == false ]]; then
  [[ ! -e "$control" && ! -L "$control" ]] && control_new=true
  [[ ! -e "$marker" && ! -L "$marker" ]] && marker_new=true
  state_expected=$(printf 'HVO-DEPLOY-PREPARE-STATE\t1\nmarker\t%s\ncreatingRun\t%s\nrootNew\t%s\ncontrolNew\t%s\nmarkerNew\t%s\n' "$marker_digest" "$creating_run" "$root_new" "$control_new" "$marker_new")
  state_temporary="$state_path.tmp.$marker_digest"
  if [[ ! -e "$state_temporary" ]]; then (umask 077; printf '%s\n' "$state_expected" > "$state_temporary") || fail create-failed; fi
  [[ -f "$state_temporary" && ! -L "$state_temporary" && "$(<"$state_temporary")" == "$state_expected" ]] || fail state-mismatch
  chmod 600 "$state_temporary" || fail create-failed
  mv -T "$state_temporary" "$state_path" || fail create-failed
fi
if [[ ! -e "$root" ]]; then mkdir -m 700 -- "$root" || fail create-failed; fi
[[ "$failpoint" != after-root-creation ]] || exit 70
if [[ ! -e "$control" ]]; then mkdir -m 700 -- "$control" || fail create-failed; control_new=true; fi
[[ -d "$control" && ! -L "$control" ]] || fail unsafe-path
validate_components "$control" || fail unsafe-path
marker_expected=$'HVO-DEPLOY-ROOT\t1\nmarker\t'"$marker_digest"
if [[ ! -e "$marker" ]]; then
  temporary="$control/.ownership.tmp.$marker_digest"
  if [[ ! -e "$temporary" ]]; then (umask 077; printf '%s\n' "$marker_expected" > "$temporary") || fail create-failed; fi
  [[ -f "$temporary" && ! -L "$temporary" && "$(<"$temporary")" == "$marker_expected" ]] || fail marker-mismatch
  chmod 600 "$temporary" || fail create-failed
  mv -T "$temporary" "$marker" || fail create-failed
  marker_new=true
fi
[[ -f "$marker" && ! -L "$marker" && "$(<"$marker")" == "$marker_expected" ]] || fail marker-mismatch
chmod 700 "$root" "$control" || fail create-failed
chmod 600 "$marker" || fail create-failed
[[ "$(stat -c %u:%a "$root" 2>/dev/null)" == "$runtime_uid:700" && "$(stat -c %u:%a "$control" 2>/dev/null)" == "$runtime_uid:700" &&
   "$(stat -c %u:%a "$marker" 2>/dev/null)" == "$runtime_uid:600" ]] || fail owner-failed
[[ "$failpoint" != after-marker-creation ]] || exit 71
printf 'prepared\t%s\t%s\t%s\t%s\t%s\n' "$root_new" "$control_new" "$marker_new" "$marker_digest" "$creating_run"
REMOTE
)"
    if output="$(printf '%s\n' "$remote_script" | ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$@" 2>/dev/null)"; then status=0; else status=$?; fi
    if [[ "$status" == 0 && "$output" == validated$'\t'* ]]; then
        IFS=$'\t' read -r _ prepared_digest prepared_run prepared_root prepared_control prepared_marker <<< "$output"
        if [[ "$prepared_digest" == "$expected_digest" && "$prepared_run" =~ ^[a-z0-9][a-z0-9-]{0,31}$ &&
              ( "$prepared_root" == true || "$prepared_root" == false ) && ( "$prepared_control" == true || "$prepared_control" == false ) &&
              ( "$prepared_marker" == true || "$prepared_marker" == false ) ]]; then printf '%s\n' "$output"
        else printf 'failed\tinvalid-response\n'; fi
    elif [[ "$status" == 0 && "$output" == prepared$'\t'* ]]; then
        IFS=$'\t' read -r _ prepared_root prepared_control prepared_marker prepared_digest prepared_run <<< "$output"
        if [[ ( "$prepared_root" == true || "$prepared_root" == false ) && ( "$prepared_control" == true || "$prepared_control" == false ) &&
              ( "$prepared_marker" == true || "$prepared_marker" == false ) && "$prepared_digest" == "$expected_digest" &&
              "$prepared_run" =~ ^[a-z0-9][a-z0-9-]{0,31}$ ]]; then printf '%s\n' "$output"
        else printf 'failed\tinvalid-response\n'; fi
    elif [[ "$output" == failed$'\t'* ]]; then
        reason="${output#*$'\t'}"
        case "$reason" in
          identity-mismatch|owner-unavailable|owner-controlled-parent-required|unsafe-path|unsafe-owner|parent-missing|unsafe-lock|lock-contended|lock-content-mismatch|state-mismatch|completed-drift|unmarked-root|nonempty-unmarked-root|marker-mismatch|create-failed|owner-failed) printf 'failed\t%s\n' "$reason" ;;
          *) printf 'failed\tinvalid-response\n' ;;
        esac
    else printf 'failed\ttransport-failed\n'; fi
}
