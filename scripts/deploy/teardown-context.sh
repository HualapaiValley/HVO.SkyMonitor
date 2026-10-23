#!/usr/bin/env bash
# Deliberately retained Docker CLI boundary. The typed phase owns endpoint
# correlation, arguments, ordering, state, refusal and cleanup policy.
set -euo pipefail
exec env -u DOCKER_CONTEXT -u DOCKER_HOST -u DOCKER_TLS_VERIFY -u DOCKER_CERT_PATH docker "$@"
