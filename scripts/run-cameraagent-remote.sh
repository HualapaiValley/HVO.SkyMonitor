#!/usr/bin/env bash
# Helper for starting the camera agent in a remote Docker context
# and pointing it back to the correct logic host.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

usage() {
    cat <<'EOF'
Usage: ./scripts/run-cameraagent-remote.sh [options] [-- <infra:start args>]

Options:
  -c, --context CONTEXT       Docker context name (default: "default")
  -l, --logic-host HOST       IP/hostname for the logic host (defaults to first non-loopback address)
      --logic-port PORT       Port that the logic host listens on (default: 5174)
      --identity-host HOST    IP/host for the identity service (defaults to logic host)
      --identity-port PORT    Port for the identity service (default: 5174)
      --dry-run               Print the resolved values instead of running infra:start
  -h, --help                  Show this message

Any arguments after "--" are appended to ./scripts/infra:start cameraagent (for example "--reset" or "--rebuild").
EOF
}

CONTEXT="default"
LOGIC_HOST=""
LOGIC_PORT="5174"
IDENTITY_HOST=""
IDENTITY_PORT="5174"
DRY_RUN=false

while [[ $# -gt 0 ]]; do
    case "$1" in
        -c|--context)
            CONTEXT="$2"
            shift 2
            ;;
        -l|--logic-host)
            LOGIC_HOST="$2"
            shift 2
            ;;
        --logic-port)
            LOGIC_PORT="$2"
            shift 2
            ;;
        --identity-host)
            IDENTITY_HOST="$2"
            shift 2
            ;;
        --identity-port)
            IDENTITY_PORT="$2"
            shift 2
            ;;
        --dry-run)
            DRY_RUN=true
            shift
            ;;
        -h|--help)
            usage
            exit 0
            ;;
        --)
            shift
            break
            ;;
        *)
            break
            ;;
    esac

done

PASS_THROUGH_ARGS=("$@")

if [[ -z "$LOGIC_HOST" ]]; then
    LOGIC_HOST="$(ip -4 route get 1 >/dev/null 2>&1 && ip -4 route get 1 | awk 'NR==1 {print $7}')"
    if [[ -z "$LOGIC_HOST" ]]; then
        LOGIC_HOST="$(hostname -I | awk '{print $1}')"
    fi
    if [[ -z "$LOGIC_HOST" ]]; then
        LOGIC_HOST="localhost"
    fi
fi

if [[ -z "$IDENTITY_HOST" ]]; then
    IDENTITY_HOST="$LOGIC_HOST"
fi

sanitize_context() {
    local input="${1:-default}"
    input="${input^^}"
    input="${input//[^A-Z0-9]/_}"
    if [[ -z "$input" ]]; then
        input="DEFAULT"
    fi
    printf '%s' "$input"
}

SANITIZED_CONTEXT="$(sanitize_context "$CONTEXT")"
LOGIC_URL="http://${LOGIC_HOST}:${LOGIC_PORT}"
IDENTITY_URL="http://${IDENTITY_HOST}:${IDENTITY_PORT}"

export CAMERAAGENT_DOCKER_CONTEXT="$CONTEXT"
export CAMERA_AGENT_LOGIC_BASEURL="${LOGIC_URL}"
export CAMERA_AGENT_IDENTITY_URL="${IDENTITY_URL}"

export "CAMERA_AGENT_LOGIC_BASEURL_${SANITIZED_CONTEXT}=${LOGIC_URL}"
export "CAMERA_AGENT_IDENTITY_URL_${SANITIZED_CONTEXT}=${IDENTITY_URL}"

info() {
    echo "Context: $CONTEXT"
    echo "Docker context: $CONTEXT"
    echo "Logic URL: $LOGIC_URL"
    echo "Identity URL: $IDENTITY_URL"
    echo "Infra command: ./scripts/infra:start cameraagent ${PASS_THROUGH_ARGS[*]}"
}

if [[ "$DRY_RUN" == true ]]; then
    info
    exit 0
fi

info
./scripts/infra:start cameraagent "${PASS_THROUGH_ARGS[@]}"
