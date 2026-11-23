# CameraAgent stock Silo runtime

This runbook covers the stock Silo runtime that a Silo-layout CameraAgent instance installs with (#1966). The
[CameraAgent Silo storage and cutover contract](../planning/cameraagent-silo-cutover.md) defines the layout. Until
#1968 the CameraAgent store does not yet write to Silo; this runtime provisions, binds and verifies the store and
holds capture paused until it is authorized.

The first Silo release uses the centered profile and launches only under the launch hold of contract section 9. Use
this runtime for qualification and disposable installs until then.

## Supported topology

- Each instance has one CameraAgent container and one stock Silo container in one Compose project, on one Linux host
  and Docker daemon, amd64 or ARM64 (Raspberry Pi 5 class).
- The Silo data root is `<product-root>/cameraagents/<instance>/silo`, beside the CameraAgent state root `state/`. It
  must be on local ext4. NFS, overlay and other filesystems are refused before anything is created.
- Silo listens only on the instance's Compose network `silo` (`hvo-skymonitor-<instance>_silo`), which is
  `internal: true` and labelled with the instance. Of the containers, only this instance's CameraAgent reaches it, at
  `http://hvo-silo:9000`. It publishes no host port and its console is off.
- **Trusted host.** Processes on the host itself can still reach the instance's bridge address, which needs no
  published port. The instance boundary is between containers and against published ports, not against the host's own
  users and processes.
- The Silo container runs as the instance's runtime user with a read-only root filesystem, no capabilities and
  `no-new-privileges`, with 1 CPU, 1 GiB and 256 PIDs.

## Pinned image, release record and distribution

| Item | Value |
| --- | --- |
| Image | `docker.io/pgsty/silo@sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329` (multi-platform index) |
| Release | `RELEASE.2026-11-12T00-00-00Z`, source revision `44e848fedf8cec3d8c0da4b1aa8422390a2b7a8b` |
| linux/amd64 | manifest `sha256:6f48a60830eb101e19f955124914554d73c4a4721396d3d13d61634e1396eadf`, config `sha256:f8cec3d6c63585d664c035acecfacb92dc974ab70c1f57a528d5baa3c224e082` |
| linux/arm64 | manifest `sha256:dad2a4bc279f9095d9835398dda266abd4f2f7486d0cb66a071951f1e4a6791d`, config `sha256:66e09caa8c3e0e834de2202d9a4c45a9a0608f63ab2061a8f3953ae23fc135e7` |
| License | AGPL-3.0-or-later (`org.opencontainers.image.licenses`); text, NOTICE and CREDITS in the image at `/licenses` |

- **One pin.** The installer and the release tool share one pin, so they cannot name different images. Changing it is a
  new installer release, which first qualifies the new image with `scripts/qualify:silo-runtime`.
- **Release record.** `release.json` names the image under `images.silo`: repository, tag, index digest, reference,
  each platform's manifest and config digests, source revision, licence, source URL and `"supplier": "upstream"`. An
  installer released before #1966 reads only its own image keys and ignores this one. A Silo-layout install refuses,
  before changing anything, a release that names no Silo image (it predates the layout) or names any other.
- **Accepted image.** The image must be this daemon's own platform child of the pin. Docker's classic store names it by
  its config digest, which fixes the platform. The containerd store names a loaded single-platform image by its
  platform manifest digest, and a pulled one by the pinned index. Only `docker image inspect --platform linux/<arch>`
  resolves the index's child for this platform, which needs Docker Engine 28 or later. A cached image of another
  platform, a foreign child of the same index included, is not the pinned image and never suppresses a staged archive.
  The OS, architecture and the release, revision and licence labels must also match. The Compose model runs the
  verified local image with an explicit `platform`.
- **Running image.** A container created from the shared index reports the index as its image whichever child it runs.
  Every verification therefore checks the manifest the container actually runs (its `ImageManifestDescriptor`, under
  the containerd store) or its config ID (classic store). Any representation of the same pin passes, so a same-pin
  replacement verifies. A foreign child does not.
- **Distribution.** HVO does not ship the Silo image. The release names Docker Hub as its supplier, and an online
  install pulls the unmodified upstream image by digest. The image carries AGPL-3.0-or-later Silo and Red Hat UBI
  content with its licence, notice and credits. Corresponding source is at
  <https://github.com/pgsty/silo/tree/RELEASE.2026-11-12T00-00-00Z>. Attaching the image to a release would make HVO a
  distributor of that object code, with the obligation to offer its corresponding source and to pass on the UBI terms;
  this route avoids that. The CameraAgent service talks to Silo over S3, so it is not a derivative of Silo.

### Who provisions and verifies the image

| Situation | Who provides the image | Who verifies it, and how |
| --- | --- | --- |
| Clean install, online | The installer pulls the pinned index digest for the daemon's platform. | The installer: accepted image ID, OS, architecture and labels, before any configuration names it. |
| Clean install, offline | The operator stages a `docker save` archive (below). | The installer verifies the archive before `docker load`, then the loaded image as online. |
| Resume of an interrupted install | Already present from the first run, or loaded again from the same staged archive. | The installer, as for a clean install, and the container shape again before bootstrap. |
| Re-run of a completed install | Nothing is pulled or loaded. | The installer verifies the running container's image ID, store, binding and listeners, and changes nothing. |
| `silo-verify` | Nothing is pulled or loaded. | The running container's image ID must be one the pin accepts. |
| Stock image replacement | The operator, from the same pin. | The operator's replacement check (below) and `silo-verify`. |
| Uninstall and purge | The image stays in Docker. | Nothing to verify. Remove it with `docker image rm` when no instance uses it. |

## Install

```bash
hvo-sky-install cameraagent install --storage-layout silo --friendly-name "North Camera" \
  --catalog-bundle /path/to/catalog.bundle --release /path/to/release   # or --image-ref for an image-only install
```

The interactive install wizard does not ask for the Silo layout. It refuses `--storage-layout silo` and every
`--silo-*` option before it opens, so give every answer on the command line as above.

Before anything is created, the installer refuses an install when any of the following holds:
- the instance already exists in the other layout, or is an installed Silo-layout instance given any other input (an
  install re-run with the same inputs verifies it, and an interrupted install resumes; see below);
- it uses `--replay-profile local-runner`;
- the data root exists or is not on local ext4;
- the data root's filesystem lacks headroom;
- the release names no Silo image or another one;
- a staged archive is not the pinned image for this architecture;
- `--no-download` is given and the image is absent;
- the optional archive cannot be verified;
- the CameraAgent image does not declare `io.hvo.skymonitor.first-boot-contract=cameraagent-first-boot-pause-v1`.

A release image is imported before the label check, so that check precedes anything the install claims. An image that
is not local yet is checked as soon as the image phase has it.

- **Online:** the installer pulls the pinned image by index digest for the daemon's platform.
- **Offline:** on a connected host that uses Docker's containerd image store (the default for new Docker Engine 29
  installs; `docker info` shows `driver-type io.containerd.snapshotter.v1`), save the pinned reference for the target's
  platform:

  ```bash
  docker pull --platform linux/arm64 docker.io/pgsty/silo@sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329
  docker save --platform linux/arm64 -o silo-arm64.tar \
    docker.io/pgsty/silo@sha256:c20d112390b9a9ce8c11b8af95ad35bcaa62f4759663c09c936cd8d2b511f329
  ```

  Carry the archive across. Install with `--silo-image-archive /path/to/silo-arm64.tar` and, for a fully offline host,
  `--no-download`. Before anything is loaded, the installer checks the following:
  - every blob hashes to its name;
  - the index names the pinned platform manifest exactly once, plus only provenance attestations of it;
  - the image carries no name other than `pgsty/silo:RELEASE.2026-11-12T00-00-00Z`;
  - `manifest.json`, which the classic store loads, names the pinned config and layers;
  - the config is the pinned release for the target's OS and architecture;
  - the archive holds no unused blob.

  A save from Docker's classic store regenerates the manifest, so it cannot match the pin and is refused. Saving the
  release tag instead of the digest is accepted.

  The installer reads the staged archive once, without following a symbolic link, into a private copy in `TMPDIR`.
  The copy is owner-only and unlinked at once. It verifies that copy and loads it through its own handle
  (`/proc/<pid>/fd/<n>`), so Docker loads exactly the verified bytes even if the staged path is replaced or rewritten
  in the meantime. `TMPDIR` needs free space for one archive (about 60 MB). Nothing remains after the install or a
  crash.
- **Headroom (G2):** the data-root filesystem must keep free the larger of 10 GiB and 5 % of its size. 10 GiB holds the
  contract's proposed two-hour spool bound (9.08 GB, section 10.3) beside the store. Install, `first-resume` and
  `silo-verify` treat less as not ready. The measured spool and reclaim budgets and the pressure pause belong to #1970
  and #1973.

Before CameraAgent first starts, the installer does the following, in order:

1. Starts only the Silo service and waits for it to report healthy. It then checks the container: the pinned image,
   no published port, only the internal network, read-only root, non-root user, console off, `MINIO_FS_OSYNC=on`,
   and this data root. No other container may mount the data root.
2. On an empty store, creates the versioned bucket `hvo-<instance>` and a server-generated writer access key. The key
   is limited to that bucket: object reads and writes and exact-version deletes. It has no bucket or admin rights and
   no write or delete under `meta/`. A store that already holds a bucket is refused.
3. Creates `meta/instance.v1.json` (the instance marker) and `meta/fence.json` (writer generation 1 and its key ID)
   create-only (`If-None-Match: *`), using the root credential. It reads each back as the writer by exact version
   and SHA-256.
4. Runs CameraAgent once in its initialize-only mode, with no web host, camera or capture loop. This creates the
   capture journal, whose schema transaction writes the installer's first-boot pause. The installer then reads the
   database itself and proves the pause is durable.

The installer then starts CameraAgent and completes owner bootstrap. It verifies the store, binding, listeners,
headroom and optional archive again, and confirms that CameraAgent reports the first-boot pause still in force.

### Resume and re-run

- **Resume.** An interrupted install resumes with `--resume` and exactly the same inputs, including the same
  `--silo-image-archive` and archive options. A fresh bootstrap first proves, read-only, that the store holds no
  bucket and no access key. Only then does it write an owner-only, durable bootstrap intent naming the instance,
  store, bucket and data root. A store that was not empty therefore never becomes adoptable. It writes one durable
  record of the writer credential before either CameraAgent setting file, and reuses the exact marker and fence bytes.
  - If the bucket or writer key was created but its acknowledgement was lost, resume reads the store's
    machine-readable `mcli --json` answers whole and judges them in the installer, not with a shell pattern:
    - every bucket row must be this instance's bucket, and it must hold no object version;
    - no temporary key may exist, and no key may inherit its parent's policy;
    - every access key must be an earlier writer: the shape Silo generates, the root user as parent, the writer's name
      and description, and exactly the writer's least-privilege policy (compared as sets, since Silo reorders
      actions).
    Only then does it adopt the bucket, enable its versioning if needed, remove those earlier keys (whose secret was
    never recorded) and create the writer again. The changing step acts only while the store still has the same bucket,
    object and versioning answers and the same keys. Anything else, including an answer it cannot read whole, is
    refused with nothing changed.
  - A resume after the binding already completed writes the marker and fence create-only again. It accepts them only
    as the exact recorded bytes.
  - If the run stopped between the two setting files, resume writes them again from the record.
  - An intent for another instance or data root, a setting file without a record or differing from it, or a store
    holding anything else is refused before the store is touched.
- **Re-run of a completed install.** With the same inputs, the installer verifies the instance as `silo-verify` does:
  container, store, binding, listeners, headroom and archive. It reports every item unchanged and changes nothing.
  With any other input, it refuses and names `silo-verify` as the supported check. A Silo-layout instance is never
  upgraded, reconfigured or converted in place in this release.

## Optional archive configuration

An instance may name one archive store for #1969's replication, held to this boundary: the installer verifies it
read-only and sets up no replication, hold or copy.

```bash
  --silo-archive-endpoint https://archive.example:9443 --silo-archive-bucket hvo-archive \
  --silo-archive-ca-file /path/to/archive-ca.pem --silo-archive-credential-file /path/to/archive-credential
```

- The four options go together and need `--storage-layout silo`. The endpoint is `https://` with a host and optional
  port only. Plain HTTP is refused, and there is no lab exception.
- The server's certificate must be valid now, name the endpoint's host, allow TLS server authentication, and chain to
  the given PEM authority alone. System trust, proxies and redirects are not used.
- The credential file is owner-only (mode 0600 or 0400) and holds the access key ID and the secret on two lines.
- The probe is one AWS Signature Version 4 `GET /<bucket>?versioning=`, with a 10-second budget. The bucket must be
  versioned.
- Install refuses unless the archive is verified. The installer keeps owner-only copies of the authority and
  credential under `config/silo-archive/`. The manifest records the endpoint, bucket, authority SHA-256, access key ID
  and the server certificate SHA-256 seen at install, never the secret.
- `silo-verify` probes again and reports the archive's state and a fixed reason; it never prints server text.

| State | Reasons | Readiness |
| --- | --- | --- |
| verified | `archive-verified` | ready |
| temporarily unavailable | `archive-unreachable`, `archive-timeout`, `archive-server-error` | degraded: local capture is unaffected |
| refused | `archive-tls-untrusted`, `archive-tls-failed`, `archive-credential-refused`, `archive-bucket-missing`, `archive-versioning-not-enabled`, `archive-unexpected-response`, `archive-authority-changed`, `archive-credential-changed` | not ready |

**Replica version pairing.** The archive should run the same qualified Silo release as the instance. This boundary
verifies the archive's identity, authentication and versioning, not its release. #1969 qualifies the pairing.

## Credentials

| Credential | Where | Seen by |
| --- | --- | --- |
| Silo root user and password | `config/silo-root/` (0600), mounted read-only at `/run/hvo-silo` | Silo only |
| Writer access key and secret | `config/secrets/CameraAgent__Silo__AccessKey`, `__SecretKey` (0600) | CameraAgent only |
| Archive authority and credential | `config/silo-archive/authority.pem`, `credential` (0600) | The installer only, until #1969 |

- No secret is ever on a command line. The installer runs its scripts inside the Silo container through standard
  input. Silo itself generates the writer key.
- The fence names the writer key's ID; the manifest records the ID but never a secret.
- **Root credential lifecycle (limitation).** The installer generates the Silo root user and password once, at
  install. Only the Silo container reads them, through a read-only mount; no CameraAgent setting, manifest or other
  container holds them. This release has no supported way to rotate or replace them. Editing the files and restarting
  Silo is not a qualified procedure. Use the root credential only for the scripts in this runbook.

### Writer key rotation (G3)

**Scope.** This rotates the CameraAgent writer access key within writer generation 1 on a running, installed
instance. It is a supported manual operation, qualified on real installs (four complete rotations, each interrupted
state below, and a stale-fence guard) and by `scripts/qualify:silo-runtime` (`rotation`). The fence keeps its
generation and names the new key; a new writer generation is a takeover (#1972). The root credential is not rotated
(see its limitation above). Run it as the user that installed the instance, with `jq` on the host. Secrets travel only
through files and standard input; none is ever an argument.

```bash
ID='<instance-id>'; P='<product-root>'; HVO=hvo-sky-install
N=${ID//-/}; I=$P/cameraagents/$ID; SILO=hvo-skymonitor-$N-silo; CA=hvo-skymonitor-$N; BUCKET=hvo-$N
SECRETS=$I/config/secrets; set -euo pipefail; umask 077
ROOT_ENV='export MC_HOST_root="http://$(cat /run/hvo-silo/root-user):$(cat /run/hvo-silo/root-password)@127.0.0.1:9000"'
W=$(mktemp -d)   # owner-only working directory; the new secret is in it until step 8
```

1. **Check before.** The instance must verify, and the fence's key must be the one CameraAgent holds.

   ```bash
   $HVO cameraagent silo-verify --instance-id "$ID" --product-root "$P" --json > "$W/before.json"
   jq -e '.readiness != "not-ready"' "$W/before.json" >/dev/null
   OLD=$(jq -r .writerAccessKeyId "$W/before.json")
   ```

2. **Create the new key** with the old key's exact policy. Silo generates it; the secret goes only to `$W/new.json`.

   ```bash
   docker exec -i "$SILO" bash -c "set -euo pipefail; $ROOT_ENV; mcli admin accesskey info root/ '$OLD' --json" |
     jq -e '.policy | select(.Statement | length > 0)' > "$W/policy.json"
   docker exec -i "$SILO" bash -c "set -euo pipefail; umask 077; $ROOT_ENV; f=\$(mktemp); cat > \"\$f\";
     mcli admin accesskey create root/ --policy \"\$f\" --name hvo-writer-g1 --description 'HVO CameraAgent writer generation 1' --json; rm -f \"\$f\"" \
     < "$W/policy.json" > "$W/new.json"
   NEW=$(jq -er .accessKey "$W/new.json")
   ```

3. **Prove the new key reads the marker.**

   ```bash
   jq -r '.accessKey, .secretKey' "$W/new.json" | docker exec -i "$SILO" bash -c "set -euo pipefail; read -r a; read -r s;
     export MC_HOST_w=\"http://\$a:\$s@127.0.0.1:9000\"; mcli stat 'w/$BUCKET/meta/instance.v1.json' >/dev/null && echo new-key-reads-marker"
   ```

4. **Point the fence at the new key** as root, only if the fence is still the version just read (`If-Match` on its
   ETag). A fence that changed in between is refused ("At least one of the pre-conditions you specified did not hold")
   and stays as it was.

   ```bash
   docker exec -i "$SILO" bash -c "set -euo pipefail; $ROOT_ENV; mcli stat 'root/$BUCKET/meta/fence.json' --json" > "$W/fence.json"
   docker exec -i "$SILO" bash -c "set -euo pipefail; $ROOT_ENV; mcli cat 'root/$BUCKET/meta/fence.json'" > "$W/fence.old"
   [[ "$(grep -o "\"writerAccessKeyId\":\"$OLD\"" "$W/fence.old" | wc -l)" == 1 ]]
   sed "s/\"writerAccessKeyId\":\"$OLD\"/\"writerAccessKeyId\":\"$NEW\"/" "$W/fence.old" > "$W/fence.new"
   ETAG=$(jq -er .etag "$W/fence.json")
   docker exec -i "$SILO" bash -c "set -euo pipefail; umask 077; $ROOT_ENV; f=\$(mktemp); cat > \"\$f\";
     mcli put --disable-multipart --checksum SHA256 -H 'If-Match: \"$ETAG\"' \"\$f\" 'root/$BUCKET/meta/fence.json' >/dev/null; rm -f \"\$f\"" \
     < "$W/fence.new"
   ```

5. **Give CameraAgent the new key**, keeping each file's owner and mode, and restart it.

   ```bash
   jq -j .accessKey "$W/new.json" > "$SECRETS/.CameraAgent__Silo__AccessKey.new"
   jq -j .secretKey "$W/new.json" > "$SECRETS/.CameraAgent__Silo__SecretKey.new"
   for name in AccessKey SecretKey; do
     chown --reference="$SECRETS/CameraAgent__Silo__$name" "$SECRETS/.CameraAgent__Silo__$name.new"
     chmod --reference="$SECRETS/CameraAgent__Silo__$name" "$SECRETS/.CameraAgent__Silo__$name.new"
   done
   mv -f "$SECRETS/.CameraAgent__Silo__AccessKey.new" "$SECRETS/CameraAgent__Silo__AccessKey"
   mv -f "$SECRETS/.CameraAgent__Silo__SecretKey.new" "$SECRETS/CameraAgent__Silo__SecretKey"
   docker restart --timeout 30 "$CA" >/dev/null
   for _ in $(seq 1 90); do [[ "$(docker inspect --format '{{.State.Health.Status}}' "$CA")" == healthy ]] && break; sleep 2; done
   ```

6. **Verify.** `silo-verify` reads the fence from the store and requires CameraAgent's key to be the one it names. It
   proves the key from CameraAgent's own network namespace and live settings mount. It requires CameraAgent to be
   healthy and to have started after both key files were published (renamed into place). That proves the running
   process crossed the replacement before the old key is retired.

   ```bash
   $HVO cameraagent silo-verify --instance-id "$ID" --product-root "$P" --json > "$W/after.json"
   jq -e --arg new "$NEW" '.readiness != "not-ready" and .writerAccessKeyId == $new and .writerRotated' "$W/after.json"
   ```

7. **Retire the old key**: disable it, so that it is refused (`InvalidAccessKeyId`) and `accesskey info` no longer
   finds it, then remove it. Until removal, `mcli admin accesskey enable` undoes the disable.

   ```bash
   docker exec -i "$SILO" bash -c "set -euo pipefail; $ROOT_ENV; mcli admin accesskey disable root/ '$OLD' >/dev/null"
   docker exec -i "$SILO" bash -c "$ROOT_ENV; mcli admin accesskey info root/ '$OLD' --json || true" |
     jq -e '.error.cause.error.Code == "XMinioAdminNoSuchAccessKey"'
   docker exec -i "$SILO" bash -c "set -euo pipefail; $ROOT_ENV; mcli admin accesskey rm root/ '$OLD' >/dev/null"
   ```

8. **Remove the working directory**, which holds the new secret: `rm -rf -- "$W"`.

**Interrupted rotation and recovery.** `silo-verify` names every intermediate state with a fixed diagnostic and never
prints a secret.

| Stopped after | `silo-verify` | Recovery |
| --- | --- | --- |
| Step 2 or 3 (new key exists) | Ready on the old key | Continue at step 4, or remove the new key (`mcli admin accesskey rm`) |
| Step 4 (fence names the new key) | Refused, exit 3: "The writer credential CameraAgent holds is not the one the fence names." | Continue at step 5. To abandon, put `$W/fence.old` back with step 4's conditional put against the current ETag; it is then ready on the old key |
| Part of step 5 (one file replaced) | Refused, exit 3: `writer-cannot-read-bucket` | Write both files from `$W/new.json` (step 5) and restart |
| Step 5 before the restart, or a restart before both files were renamed into place | Refused, exit 3: "CameraAgent has not restarted since its Silo writer credential was written" | Restart CameraAgent after both renames (step 5) |
| Step 6 or 7 (old key still enabled) | Ready on the new key | Complete step 7 |
| `$W/new.json` lost before step 5 | As above, by step | Silo never shows a secret again: put the old fence back, remove the new key and start over |

Never disable the old key before step 6 shows the new key ready.

## First boot, device allocation and the first resume

- The instance starts paused under the installer's first-boot pause. The pause holds through restart and owner
  bootstrap. The camera module may initialize, but nothing is admitted and no capture sequence is allocated.
- While that pause is the latest capture command, only `hvo-sky-install cameraagent first-resume --instance-id <id>`
  can resume. An operator resume is refused.
- If an operator pauses later, even a pause that changes nothing, the first resume is refused. The operator who paused
  then decides when to resume.
- An installer lifecycle command can only lift a pause its own operation placed.
- **Device allocation.** The installer maps no camera device. Map the real camera into the candidate only after the
  former owner's container is removed and no process holds the device (`fuser`/`lsof` on the device node). Keep that
  mapping the only one until the handoff ends. Run the first resume only after that.
- `first-resume` refuses, and capture stays paused, until the installation is recorded complete: its journal says
  completed and names the same installation as the instance's manifest and result. It takes the installer's locks
  first and holds them until CameraAgent has recorded the resume, so it never runs beside an install of the instance.
  An interrupted install is finished by running the same install again.
- `first-resume` verifies the store, binding, listeners and data root first. It then resumes only if the pause is still
  in force, and reports the resulting capture state and version.

## Verify, readiness, health and capacity

- `hvo-sky-install cameraagent silo-verify --instance-id <id> [--json]` changes nothing except a throwaway probe
  container (below). It first correlates the instance's manifest and result. A result of another instance or another
  endpoint is refused before the lifecycle token is read or anything is called. It then checks:
  - the container shape and that the data root is exclusive to this Silo. Every listed container must be inspected,
    and only Docker's own answer that one is gone counts as its absence;
  - that the container runs this daemon's own platform child of the pin;
  - that the private network is the instance's, internal, labelled, and holds only this Silo, under its alias
    `hvo-silo`, and this instance's running CameraAgent;
  - that CameraAgent is running now, healthy, on the image the instance records, and consumes this instance's
    settings. That means a read-only bind of `config/secrets` at `/run/hvo-secrets`, with no environment variable
    overriding a Silo setting;
  - that CameraAgent started after its active writer credential pair was published. Publication is the later change
    (rename into place) of the two key files, not a staged file's write time, so the running process read this pair;
  - that the bucket, store, binding, writer-generation and state-contract settings CameraAgent is configured with hold
    exactly the values the manifest records, and that none of them, nor the endpoint, changed after CameraAgent
    started. CameraAgent reads its setting files once, when it starts, so a setting corrected afterwards is refused
    until CameraAgent is restarted;
  - the filesystem and device recorded at install;
  - bucket versioning;
  - that the marker's latest version is the recorded one and binds this instance;
  - that the fence in the store is a writer fence of this instance at its generation, and names the writer key
    CameraAgent holds. While the fence is the install-time version, its bytes must be the recorded ones;
  - the data-root headroom and the optional archive (above);
  - the first-boot pause status.

  The binding is read back as CameraAgent would reach it. The probe is a `docker run --rm` of the verified local Silo
  image joined to CameraAgent's network (`--network container:<id>`) and to its mounts, read-only
  (`--volumes-from <id>:ro`). Before it reads a credential or sends anything, it proves the settings directory and
  files it sees are this instance's own (same device and inode as on the host, no symbolic link). It also proves the
  endpoint file holds exactly this instance's endpoint, byte for byte. Only then does it read the writer credential
  and reach the store, at that expected address. It reports the endpoint, the key ID and the secret's SHA-256, which
  must match this instance's endpoint and recorded credential. It never pulls, runs unprivileged with a read-only root and no
  capabilities, is labelled `io.hvo.skymonitor.component=SiloProbe`, and is sent no credential. A wrong endpoint,
  bucket, store, fence, writer key, network member, image, settings mount or data root, or a stopped, unhealthy or
  stale CameraAgent, is refused (exit 3) with a fixed message.
- **Readiness** (G2) is reported as `readiness` and decides the exit code:

  | Readiness | When | `silo-verify` | `first-resume` |
  | --- | --- | --- | --- |
  | `ready` | Every check above passes, headroom is kept and, if configured, the archive verified | exit 0 | proceeds |
  | `degraded` | Only the optional archive is temporarily unavailable; local capture is unaffected | exit 0 | proceeds |
  | `not-ready` | Headroom below the reserve, or the archive answered and was refused | exit 3 | refused; capture stays paused |

  A completed install re-run with the same inputs makes the same checks and refuses when the instance is not
  ready.

  `--json` also reports `headroom` (`availableBytes`, `totalBytes`, `requiredBytes`, `state`), `archive` (`state`,
  `reason`, `certificateMatchesInstall`), `writerAccessKeyId`, `writerRotated` (the fence has changed since install)
  and `fenceVersionId`.
- **Health:** the Compose health check calls Silo's `/minio/health/live`. CameraAgent's own health is unchanged.
- **Capacity:** the headroom reserve above is the readiness signal. Silo trash is under `.minio.sys/tmp/.trash` and is
  purged on Silo restart (contract 7.4). Spool and reclaim budgets and the pressure pause belong to #1970 and #1973.

## Durability setting

`MINIO_FS_OSYNC=on` is part of the qualified profile:

- In the pinned release, part data and `xl.meta` are always `fdatasync`ed.
- The commit renames in `RenameData` are made durable before the PUT is acknowledged only when this setting is on. Silo
  then issues `sync(2)` before returning.
- With it on, an acknowledged version is meant to survive power loss on ext4 that honors flush (contract 5.2). That is
  the design, not a measured guarantee. A process crash (SIGKILL) has been qualified. A physical power cut has not been
  measured, and that proof is #1968/#1974 work.
- `scripts/qualify:silo-runtime` measures the cost (`osync-cost`). `sync(2)` is host-wide, so the cost depends on other
  dirty data on the host. The native ARM64 measurement on physical storage is the relevant one.

## Uninstall, purge and unsupported operations

- `cameraagent uninstall` pauses capture, then `compose down` removes both containers and both networks. It keeps the
  state root and the Silo data root. The Silo image stays in Docker.
- `cameraagent purge` deletes the whole instance root, including the Silo data root.
- `upgrade`, `rollback`, `reinstall`, `backup`, `restore`, `reset-state` and the catalog commands refuse a Silo-layout
  instance before changing anything. Installers released before #1966 refuse it as well, because its manifest declares
  `cameraagent-silo-install-v1`.
- `cameraagent owner-recovery` and the lifecycle control worker refuse a Silo-layout instance as well. Only status,
  `first-resume`, `silo-verify`, `uninstall` and `purge` act on one.
- **An uninstalled Silo-layout instance cannot be started again in this release.** No command starts it, and an install
  re-run refuses it because it is no longer installed. Keep its state and data root for #1972's recovery, or purge it.
- New-layout backup and recovery are #1972.

## Stock image replacement on the retained data root

`scripts/qualify:silo-runtime` qualifies this procedure (`exit-exclusive` and `replacement`). Contract 7.2 takeover
uses the same storage drain:

1. Stop the Silo container (`docker stop --timeout 30`) and verify its process has exited: the daemon reports it not
   running with PID 0, and the former PID no longer runs `silo`.
2. Verify that no other container mounts the data root.
3. Start the replacement stock container on the same data root, with the pinned image and the same profile. Wait for
   it to report healthy.
4. Read back the marker and earlier object versions by exact version and SHA-256.

On an installed instance, as the user that installed it, with `jq` on the host. Before the stop it records every
object version in the bucket with the SHA-256 of its bytes, read through the store. After the replacement it reads them
again and requires the same versions with the same bytes (step 4), then runs `silo-verify`, which also reads the marker
back from CameraAgent's network. The first-boot pause and capture state are unchanged by the replacement. Any failed
step stops the procedure and says which command failed; if Silo was already stopped, the message says how to start it
again. Reading every version takes time in proportion to the data stored.

```bash
ID='<instance-id>'; P='<product-root>'; HVO=hvo-sky-install
N=${ID//-/}; I=$P/cameraagents/$ID; SILO=hvo-skymonitor-$N-silo; DATA=$I/silo; BUCKET=hvo-$N; set -Eeuo pipefail; umask 077
ROOT_ENV='export MC_HOST_root="http://$(cat /run/hvo-silo/root-user):$(cat /run/hvo-silo/root-password)@127.0.0.1:9000"'
COMPOSE=(docker compose --project-name "hvo-skymonitor-$N" --env-file "$I/config/compose/instance.env"
  --file "$I/config/compose/compose.yml")
W=$(mktemp -d)
trap 'echo "Stopped: \"$BASH_COMMAND\" failed. If Silo is stopped, start it again with: docker start $SILO" >&2' ERR
versions() {   # every object version in the bucket and the SHA-256 of its bytes, into $W/$1
  docker exec "$SILO" bash -c "set -euo pipefail; $ROOT_ENV; mcli ls --versions --recursive --json 'root/$BUCKET'" |
    jq -er 'select(.status == "success" and .type == "file" and (.isDeleteMarker // false | not)) | [.key, .versionId] | @tsv' |
    sort > "$W/$1.list"
  while IFS=$'\t' read -r key version; do
    sha=$(printf '%s\t%s\n' "$key" "$version" | docker exec -i "$SILO" bash -c "set -euo pipefail; $ROOT_ENV
      IFS=\$'\t' read -r key version; mcli cat --version-id \"\$version\" \"root/$BUCKET/\$key\"" | sha256sum)
    printf '%s\t%s\t%s\n' "$key" "$version" "${sha%% *}"
  done < "$W/$1.list" > "$W/$1"
}
"$HVO" cameraagent silo-verify --instance-id "$ID" --product-root "$P" --json | jq -e '.readiness != "not-ready"' >/dev/null
versions before
[[ -s $W/before ]]                                                                   # the marker and fence at least
PID=$(docker inspect --format '{{.State.Pid}}' "$SILO")
docker stop --timeout 30 "$SILO"                                                     # step 1
[[ "$(docker inspect --format '{{.State.Running}} {{.State.Pid}}' "$SILO")" == "false 0" ]]
if [[ -r /proc/$PID/cmdline ]] && tr '\0' ' ' < "/proc/$PID/cmdline" | grep -q silo; then echo "Silo $PID still runs" >&2; exit 1; fi
ids=$(docker ps -aq --no-trunc)                                                      # step 2: an unanswered inventory stops here
for c in $ids; do
  if [[ "$(docker inspect --format '{{.Name}}' "$c")" == "/$SILO" ]]; then continue; fi
  mounts=$(docker inspect --format '{{range .Mounts}}{{println .Source}}{{end}}' "$c")
  while IFS= read -r s; do
    if [[ -n $s && ( $s == "$DATA" || $DATA == "${s%/}/"* || $s == "$DATA/"* ) ]]; then
      echo "$c mounts $s; Silo is stopped. Start it again with: docker start $SILO" >&2; exit 1
    fi
  done <<< "$mounts"
done
docker rm "$SILO"
"${COMPOSE[@]}" up --detach --no-deps silo                                           # step 3
for _ in $(seq 90); do
  if [[ "$(docker inspect --format '{{.State.Health.Status}}' "$SILO")" == healthy ]]; then break; fi
  sleep 2
done
[[ "$(docker inspect --format '{{.State.Health.Status}}' "$SILO")" == healthy ]]
versions after                                                                       # step 4
diff "$W/before" "$W/after"
"$HVO" cameraagent silo-verify --instance-id "$ID" --product-root "$P" --json | jq -e '.readiness == "ready"' >/dev/null
echo "Replaced: $(wc -l < "$W/after") object versions read back by version and SHA-256; silo-verify ready."
rm -rf -- "$W"
```

**Replica pairing.** The optional archive (#1969) runs a separately qualified Silo on another host over authenticated
TLS. This installer only verifies its configuration read-only (above); replication and holds are #1969's.

## Qualification

On the host under test:

```bash
scripts/qualify:silo-runtime --root /owner-only/ext4/parent/new-run-dir [--samples 20] [--max-seconds 1800]
```

**Refused before anything is created** (the script exits 3):
- `--root` is not absolute and canonical, or it already exists.
- Its parent is not owned by the user, is group- or world-writable, is not on local ext4, or has less than 2 GiB free.
- An existing container mounts the root or any ancestor of it.
- Any container, network, label or evidence file the run would use already exists.
- Shell tracing is on.

**Isolation:**
- Docker is reached only through the local socket `--docker-socket` (default `/var/run/docker.sock`). `DOCKER_HOST`,
  `DOCKER_CONTEXT`, TLS settings and the user's Docker configuration are ignored.
- The name prefix is generated with 64 bits of entropy unless `--name` is given. A name is not ownership: every
  container and network the run creates also carries `io.hvo.qualification.invocation`, a 128-bit identity drawn for
  this invocation alone, and is read back by the ID Docker returned.
- Every inventory, before startup, before the replacement and at cleanup, must be answered. A query the daemon does
  not answer never reads as empty: it refuses the run before anything is created, or fails it before the next step.

**Cleanup:**
- The script removes only the IDs this invocation recorded and resources the daemon reports carrying its identity. A
  resource of someone else's holding the run's name, label or a reserved name is never removed.
- Cleanup is complete only when a final read-only inventory is answered in full and lists nothing. It covers the
  invocation identity, the recorded IDs, the run's name label and the reserved names. If a query fails, the cleanup
  deadline elapses, or anything remains, the script exits nonzero and keeps the run root.
- Credentials are deleted at exit, success or failure. A deletion that fails makes the run fail, naming paths only.
- On success the evidence is published beside the root without replacing anything, and then the root is removed. If
  either fails, the script exits nonzero and says what it kept. On failure or interruption, the data and evidence are
  kept, owner-only, for diagnosis.
- `scripts/test:silo-runtime-qualification` proves this against a fake daemon, without Docker.
- Every Docker call and the whole run are time-bounded.

**Credentials** stay in owner-only files or in script text sent on standard input. None is ever an argument to docker
or `mcli`.

**Host-wide effect.** The script itself never runs `sync`. The qualified profile's `MINIO_FS_OSYNC=on` makes Silo call
`sync(2)` before each acknowledged PUT, which flushes host-wide. The `osync-cost` probe measures that with `--samples`
PUTs per size.

**Evidence.** Each check writes one JSON line: platform, listener, bootstrap, writer denials, versioned I/O, osync cost,
kill recovery, exit and exclusivity, replacement, rotation, wrong endpoint and isolation. On success the evidence is
published as `<parent>/<name>.evidence.jsonl`.

- `listener` asserts no published port, an internal network, and that `:9000` is Silo's only TCP listener (apart from
  Docker's resolver on `127.0.0.11`), with no console listener.
- `isolation` asserts no TCP connection between the instance and a sibling instance's store, either way, by name or by
  address.
- It also records `hostBridgeReachable`: whether processes on the host itself can open the instance's bridge address,
  which needs no published port.

### What the qualification proves, and what it does not

The independent runs on one native ARM64 host and two native amd64 hosts used an earlier revision of this qualifier,
with the same probes, workloads and checks as below. The integrated revision only tightens ownership and cleanup. Each
container and network it creates carries this run's own identity and is read back by the ID Docker returned. An
inventory the daemon does not answer refuses or fails the run, including the data-root exclusivity check. The run root
is removed only after a complete, empty final inventory.

- **Process crash.** `kill-recovery` sends one SIGKILL to the Silo process after five acknowledged PUTs. All five
  versions verify by exact version and SHA-256 after restart. That is a short, single-event sample. It is not a
  long-running or concurrent-write proof.
- **Replacement.** `replacement` replaces the stock container once on the retained data root. Eight earlier versions
  verify by exact version and SHA-256, in about 2.5–3.7 s from removal to the last verified version on the three hosts.
- **Disk-sync cost.** `osync-cost` times single PUTs of 4 KiB (4096 bytes) and 7 MiB (7340032 bytes) of random data,
  `--samples` per size (default 20), with `MINIO_FS_OSYNC` on and off.
  - Server: the pinned image (`RELEASE.2026-11-12T00-00-00Z`, revision `44e848fedf8cec3d8c0da4b1aa8422390a2b7a8b`).
  - Client: the `mcli` it ships (`RELEASE.2026-11-12T00-00-00Z`, commit `a37a9f054fba828a0bbbceb4e9ff247ccf09c28d`),
    run by `docker exec` in the Silo container.
  - Each sample therefore includes the client's start, and measures the client path end to end, not the server's sync
    alone.
  - On the native ARM64 host, 20 samples per size gave a p50 of 23.9 ms (on) versus 22.8 ms (off) at 4 KiB, and 81.2
    versus 86.7 ms at 7 MiB.
  - One amd64 run was on a virtual disk, which does not establish the cost for physical storage.
- **Trusted host.** All three hosts recorded `hostBridgeReachable`: processes on the host itself reach the instance's
  bridge address. See the topology caveat above.
- **Power loss.** No run cut power. That an acknowledged version survives an abrupt power cut on ext4 that honors
  flush is the contract's design (5.2), not a measured result; the physical power-cut proof is #1968/#1974.
