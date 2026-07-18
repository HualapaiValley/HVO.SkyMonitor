#!/usr/bin/env bash
set -euo pipefail

readonly OPENCODE_CONFIG_DIR="${OPENCODE_CONFIG_DIR:-$HOME/.config/opencode}"
readonly OPENCODE_DATA_DIR="${OPENCODE_DATA_DIR:-$HOME/.local/share/opencode}"
readonly OPENCODE_CONFIG_FILE="$OPENCODE_CONFIG_DIR/opencode.jsonc"
readonly OPENCODE_SERVER_PASSWORD_FILE="$OPENCODE_DATA_DIR/server-password"
readonly LEGACY_OPENCODE_CONFIG=$'{\n\t"$schema": "https://opencode.ai/config.json",\n\t"permission": "allow"\n}'

mkdir -p "$OPENCODE_CONFIG_DIR" "$OPENCODE_DATA_DIR"
chmod 700 "$OPENCODE_CONFIG_DIR" "$OPENCODE_DATA_DIR"
umask 077
if [[ ! -s "$OPENCODE_SERVER_PASSWORD_FILE" ]]; then
	openssl rand -hex 32 > "$OPENCODE_SERVER_PASSWORD_FILE"
fi
if [[ ! -s "$OPENCODE_CONFIG_FILE" || "$(<"$OPENCODE_CONFIG_FILE")" == "$LEGACY_OPENCODE_CONFIG" ]]; then
	temporary_config="$(mktemp "$OPENCODE_CONFIG_DIR/.opencode.jsonc.XXXXXX")"
	cat > "$temporary_config" <<'EOF'
{
	"$schema": "https://opencode.ai/config.json"
}
EOF
	chmod 600 "$temporary_config"
	mv -f "$temporary_config" "$OPENCODE_CONFIG_FILE"
fi
chmod 600 "$OPENCODE_SERVER_PASSWORD_FILE" "$OPENCODE_CONFIG_FILE"
