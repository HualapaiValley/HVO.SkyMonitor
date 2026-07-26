#!/usr/bin/env bash
# Install OpenCode once per workspace. Existing persisted binaries are never replaced.

set -euo pipefail

readonly OPENCODE_BIN_DIR="${OPENCODE_BIN_DIR:-$HOME/.local/bin}"
readonly OPENCODE_BINARY="$OPENCODE_BIN_DIR/opencode"

mkdir -p "$OPENCODE_BIN_DIR"
chmod 700 "$OPENCODE_BIN_DIR"

if [[ -x "$OPENCODE_BINARY" ]]; then
    echo "Using persisted OpenCode: $($OPENCODE_BINARY --version)"
    exit 0
fi

existing_opencode="$(command -v opencode 2>/dev/null || true)"
if [[ -n "$existing_opencode" && -x "$existing_opencode" ]]; then
    install -m 0755 "$existing_opencode" "$OPENCODE_BINARY"
    echo "Preserved existing OpenCode: $($OPENCODE_BINARY --version)"
    exit 0
fi

case "$(dpkg --print-architecture)" in
    amd64) archive_name="opencode-linux-x64-baseline.tar.gz" ;;
    arm64) archive_name="opencode-linux-arm64.tar.gz" ;;
    *)
        echo "Unsupported OpenCode architecture: $(dpkg --print-architecture)" >&2
        exit 1
        ;;
esac

release_json="$(mktemp)"
archive_file="$(mktemp)"
trap 'rm -f "$release_json" "$archive_file"' EXIT
curl --fail --silent --show-error --location --retry 3 \
    https://api.github.com/repos/anomalyco/opencode/releases/latest \
    -o "$release_json"

download_url="$(jq -r --arg name "$archive_name" '.assets[] | select(.name == $name) | .browser_download_url' "$release_json")"
digest="$(jq -r --arg name "$archive_name" '.assets[] | select(.name == $name) | .digest' "$release_json")"
if [[ -z "$download_url" || "$download_url" == null || ! "$digest" =~ ^sha256:([0-9a-f]{64})$ ]]; then
    echo "The latest OpenCode release did not provide a verified $archive_name archive." >&2
    exit 1
fi

curl --fail --silent --show-error --location --retry 3 "$download_url" -o "$archive_file"
printf '%s  %s\n' "${BASH_REMATCH[1]}" "$archive_file" | sha256sum -c -
tar -xzf "$archive_file" -O opencode > "$OPENCODE_BINARY"
chmod 0755 "$OPENCODE_BINARY"
echo "Installed OpenCode: $($OPENCODE_BINARY --version)"