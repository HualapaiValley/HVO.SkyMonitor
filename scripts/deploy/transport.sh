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
