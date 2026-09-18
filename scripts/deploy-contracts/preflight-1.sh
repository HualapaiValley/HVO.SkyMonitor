#!/usr/bin/env bash
# Deployment contract shard body: preflight (part 1 of 3). Extracted verbatim from
# scripts/test:deploy-environment (#875 stage 2). It is sourced by that harness
# inside the fixture it establishes, at the exact point the body used to sit, so
# every global it reads and writes has the same value and the same scope as
# before. It runs nothing on its own and refuses direct execution.

if [[ "${BASH_SOURCE[0]}" == "$0" ]]; then
    printf '%s is a shard body sourced by scripts/test:deploy-environment; do not run it directly.\n' "${BASH_SOURCE[0]}" >&2
    exit 2
fi

legacy_login_status="$(FAKE_REMOTE_HOST=east PATH="$REMOTE_BIN" curl --silent --location --request POST \
  --output "$TEMP_DIR/legacy-login.out" --write-out '%{http_code}' --cookie-jar "$TEMP_DIR/legacy.cookies" \
  --data-urlencode 'Input.Email=owner@example.test' 'http://127.0.0.1/Account/Login')"
test "$legacy_login_status" = 405
rm -f "$TEMP_DIR/legacy-login.out" "$TEMP_DIR/legacy.cookies"

# A completed SCP followed by a failed remote rename removes and verifies the registered upload temp.
(
  source "$REPO_ROOT/scripts/deploy/common.sh"
  source "$REPO_ROOT/scripts/deploy/run-state.sh"
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  jq -n --arg root "$TEMP_DIR/remote/east" '{logicHost:{name:"logic",sshHost:"logic@example",runtimeRoot:"/unused"},
    cameraAgents:[{name:"east",sshHost:"east@example",runtimeRoot:$root}],sharedServices:null}' > "$TEMP_DIR/private-upload-inventory.json"
  deploy_transport_initialize_private_upload_registry "$TEMP_DIR/private-upload-registry.json" "$TEMP_DIR/private-upload-inventory.json" smoke
  printf 'private-upload\n' > "$TEMP_DIR/private-upload-source"; chmod 600 "$TEMP_DIR/private-upload-source"
  mkdir -m 700 "$TEMP_DIR/remote/east/upload-test"
  for private_kind in secret certificate; do
    private_destination="$TEMP_DIR/remote/east/upload-test/$private_kind"
    if PATH="$BIN:$PATH" DEPLOY_TEST_FAILPOINT=private-registry-publication:hvo-upload-smoke \
      deploy_transport_copy_private_file "$TEMP_DIR/private-upload-source" east@example "$private_destination"; then exit 90; fi
    [[ ! -e "$private_destination" ]]
    jq -e 'length == 0' "$TEMP_DIR/private-upload-registry.json" >/dev/null
  done
  if PATH="$BIN:$PATH" FAKE_SSH_FAIL_UPLOAD_MOVE_MATCH=destination deploy_transport_copy_private_file "$TEMP_DIR/private-upload-source" east@example \
    "$TEMP_DIR/remote/east/upload-test/destination"; then exit 91; fi
  upload_temps=("$TEMP_DIR/remote/east/.hvo-deploy/uploads"/hvo-upload-smoke-*.tmp)
  [[ ! -e "${upload_temps[0]}" ]]
  jq -e 'length == 0' "$TEMP_DIR/private-upload-registry.json" >/dev/null
)

# Acceptance campaigns can persist and resume their registered private session files.
(
  source "$REPO_ROOT/scripts/deploy/common.sh"
  source "$REPO_ROOT/scripts/deploy/run-state.sh"
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  registry="$TEMP_DIR/acceptance-private-upload-registry.json"
  inventory="$TEMP_DIR/private-upload-inventory.json"
  deploy_transport_initialize_private_upload_registry "$registry" "$inventory" acceptance-run
  target="$(jq -c '.cameraAgents[0]' "$inventory")"
  deploy_transport_register_remote_private "$target" "$TEMP_DIR/remote/east/.hvo-deploy/campaign-phase14/login.html"
  upload="$TEMP_DIR/remote/east/.hvo-deploy/uploads/hvo-upload-acceptance-run-123-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.tmp"
  install -d -m 700 "${upload%/*}"; printf 'private\n' > "$upload"; chmod 600 "$upload"
  deploy_transport_register_private_upload east@example "$upload"
  deploy_transport_initialize_private_upload_registry "$registry" "$inventory" acceptance-run
  jq -e 'length == 2 and all(.[]; .phase == "acceptance-run") and any(.[]; .kind == "remote-private") and any(.[]; .kind == "upload-temp")' "$registry" >/dev/null
  PATH="$BIN:$PATH" deploy_transport_cleanup_private_upload east@example "$upload"
  deploy_transport_forget_private_upload east@example "$upload"
  [[ ! -e "$upload" ]]; jq -e 'length == 1 and .[0].kind == "remote-private"' "$registry" >/dev/null
)

# Docker not-found is accepted as absence; transport/auth failures are never absence proof.
(
  source "$REPO_ROOT/scripts/deploy/transport.sh"
  PATH="$BIN:$PATH" deploy_transport_require_volume_absent shared-context absent-volume
  PATH="$BIN:$PATH" deploy_transport_require_network_absent shared-context absent-network
  if PATH="$BIN:$PATH" FAKE_DOCKER_INSPECT_OUTAGE=shared-context-volume deploy_transport_require_volume_absent shared-context absent-volume; then exit 92; fi
  if PATH="$BIN:$PATH" FAKE_DOCKER_INSPECT_OUTAGE=shared-context-network deploy_transport_require_network_absent shared-context absent-network; then exit 93; fi
)
