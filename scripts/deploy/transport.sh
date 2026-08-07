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
    local source="$1" ssh_host="$2" destination="$3" temporary="$3.hvo-upload.$$"
    [[ -f "$source" && ! -L "$source" ]] || return 1
    scp -q -o BatchMode=yes -o ConnectTimeout=8 -- "$source" "$ssh_host:$temporary" >/dev/null 2>&1 || return 1
    ssh -o BatchMode=yes -o ConnectTimeout=8 -- "$ssh_host" bash -s -- "$temporary" "$destination" 2>/dev/null <<'REMOTE'
set -euo pipefail
temporary=$1; destination=$2; parent=${destination%/*}
trap 'rm -f -- "$temporary"' EXIT
[[ "$temporary" == /* && "$destination" == /* && "$parent" != "$destination" && -d "$parent" && ! -L "$parent" ]] || exit 90
[[ -f "$temporary" && ! -L "$temporary" && "$(stat -c %h "$temporary")" == 1 ]] || exit 91
chmod 600 "$temporary"
mv -Tf -- "$temporary" "$destination"
trap - EXIT
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

deploy_transport_prepare_target() {
    local ssh_host="$1"
    shift
    local remote_script output status reason prepared_root prepared_control prepared_marker prepared_digest prepared_run expected_digest="${8}"
    remote_script="$(cat <<'REMOTE'
set -euo pipefail
root=$1; mode=$2; runtime_owner=$3; run_id=$4; inventory_hash=$5; target=$6
installation_id=$7; marker_digest=$8; lock_name=$9; expected_machine=${10}; expected_host=${11}; failpoint=${12}; operation=${13}
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
