#!/usr/bin/env bash
# Deployment contract shard body: preflight (part 2 of 3). Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

  # The harness is no longer one file (#875 stage 2): its sourced library and every
  # shard body are part of the same syntax contract, so they are checked here too.
  bash -n "$REPO_ROOT/scripts/deploy:environment" "$REPO_ROOT/scripts/deploy/"*.sh "$REPO_ROOT/scripts/test:deploy-environment" \
    "$REPO_ROOT/scripts/lib/deploy-test-lifecycle.sh" "$REPO_ROOT/scripts/deploy-contracts/"*.sh \
    "$REPO_ROOT/deploy/split-host/provision-sql.sh"
