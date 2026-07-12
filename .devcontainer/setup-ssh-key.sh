#!/usr/bin/env bash
# Materialize a developer-provided SSH key without keeping it in the repository.

set -euo pipefail

key_value="${SSH_PRIVATE_KEY:-}"
ssh_directory="${HOME}/.ssh"
key_file="${ssh_directory}/id_rsa"
config_file="${ssh_directory}/config"

if [[ -z "$key_value" ]]; then
    exit 0
fi

sudo install -d -m 700 -o "$(id -u)" -g "$(id -g)" "$ssh_directory"
sudo chown "$(id -u):$(id -g)" "$ssh_directory"

temporary_key="$(mktemp)"
trap 'rm -f "$temporary_key"' EXIT
printf '%b' "$key_value" > "$temporary_key"
chmod 600 "$temporary_key"

if ! ssh-keygen -y -f "$temporary_key" >/dev/null 2>&1; then
    echo "SSH_PRIVATE_KEY is not a valid private key." >&2
    exit 1
fi

install -m 600 "$temporary_key" "$key_file"
ssh-keygen -y -f "$key_file" > "${key_file}.pub"
chmod 644 "${key_file}.pub"

cat > "$config_file" <<'EOF'
Host *
    User roys
    IdentityFile ~/.ssh/id_rsa
    IdentitiesOnly yes

Host hvo-docker devpi5
    StrictHostKeyChecking accept-new
EOF
chmod 600 "$config_file"

if [[ -n "${SSH_AUTH_SOCK:-}" ]]; then
    ssh-add "$key_file" >/dev/null 2>&1 || true
fi

echo "Installed the developer-provided SSH key for user roys."
