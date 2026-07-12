#!/usr/bin/env bash
# Load ignored developer configuration for devcontainer lifecycle scripts.

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

load_env_file() {
    local env_file="$1"

    if [[ -f "$env_file" ]]; then
        echo "Loading developer environment from $env_file"
        set -a
        # shellcheck disable=SC1090
        source "$env_file"
        set +a
    fi
}

# Repository settings provide the baseline; devcontainer-local values override them.
load_env_file "$repo_root/.env"
load_env_file "$repo_root/.devcontainer/devcontainer.local.env"

# HVO.WebSite uses this conventional SQL Server variable. Keep the SkyMonitor
# compose and application settings on their existing SQLSERVER_* names.
if [[ -z "${SQLSERVER_PASSWORD:-}" && -n "${MSSQL_SA_PASSWORD:-}" ]]; then
    export SQLSERVER_PASSWORD="$MSSQL_SA_PASSWORD"
fi
