# CameraAgent ARM64 CI Runner

This runbook owns the dedicated native Linux ARM64 runner used by
`.github/workflows/cameraagent-arm64.yml`. The workflow is advisory: it is not
part of `Required CI`, and a failure must be investigated without moving x64,
SQL Server, Testcontainers, physical-camera, or deployment-contract work onto
the ARM64 host.

## Workload Boundary

The repository-scoped runner advertises these routing labels:

- GitHub system labels: `self-hosted`, `Linux`, and `ARM64`;
- workload label: `hvo-skymonitor-arm64`;
- capability labels: `dotnet`, `docker`, and `pi5`.

The job selector is always
`[self-hosted, linux, ARM64, hvo-skymonitor-arm64]`. Do not add the generic
`hvo-skymonitor` label: required x64 jobs use it, and architecture must remain an
explicit part of every self-hosted selector. `pi5` describes the current
hardware but is not the workload contract.

The workflow runs only from protected `main`, a daily schedule, or a
`repository_dispatch` event that GitHub resolves from the default branch. It
does not run for pull requests or branch-selectable `workflow_dispatch` events.
A persistent self-hosted runner
must not execute unreviewed pull-request code or receive repository,
environment, deployment, camera, or application secrets.

## Host Qualification

Use a dedicated native Linux ARM64 host with:

- at least 8 GB installed memory and 7.5 GB usable memory reported to Linux;
- wired networking when available, with a stable physical WiFi link permitted as
  a recorded fallback while Ethernet has no carrier;
- NVMe or SSD storage for the runner work directory and Docker root, with at
  least 20 GiB and 100,000 inodes available for each runner filesystem;
- active cooling and stable power;
- a native ARM64 Docker daemon;
- the SDK pinned by `global.json`, Git, Bash 5.1 or newer, `curl`, `file`,
  `cc`, `jq`, `readelf`, exactly SQLite 3.45.1, and standard GNU utilities; and
- no production application state, camera access, interactive credentials,
  passwordless sudo, or unrelated workloads.

The systemd runner service must set `NoNewPrivileges=true`; the harness verifies
the inherited kernel flag before using Docker. Keep the service account out of
`sudo`, `wheel`, and `admin` groups and do not grant command-specific sudoers
entries. Docker access remains the runner's only host-root-equivalent boundary.

The runner service account needs access to Docker. Docker-group membership is
host-root-equivalent, so the host is disposable CI infrastructure and must be
network-isolated from observatory, camera, deployment, and administrative
systems. Allow only the outbound endpoints needed for GitHub Actions, NuGet,
Microsoft container/package sources, Debian packages, and the canonical catalog
source. Prefer rootless Docker when it has been proven compatible with the
container smoke.

For Raspberry Pi hosts, `vcgencmd get_throttled` must report `0x0` before and
after every claimable run. Docker or runner state on `mmcblk` fails preflight.
The evidence sampler records temperature, throttle state, available memory,
load, and filesystem headroom throughout the job.

## Registration

Create a dedicated service account and place both the runner installation and
work directory on the qualified SSD/NVMe filesystem. Obtain the current ARM64
runner archive URL and SHA-256 from the official `actions/runner` release, then
verify the archive before extraction. Never retain a registration or removal
token in shell history, a file, a password manager export, or repository state.

Create a short-lived repository registration token immediately before running.
Read it without adding the value to shell history and unset it immediately after
registration; local administrators may still observe process arguments during
the bounded registration command:

```bash
read -rsp 'Runner registration token: ' RUNNER_TOKEN
printf '\n'
./config.sh \
  --url https://github.com/RoySalisbury/HVO.SkyMonitor \
  --token "$RUNNER_TOKEN" \
  --name github-runner-pi-01 \
  --labels hvo-skymonitor-arm64,dotnet,docker,pi5 \
  --work WORK_DIRECTORY \
  --unattended \
  --replace
unset RUNNER_TOKEN
```

Install and start the generated service using the host's administrative account;
the runner service account itself must not have passwordless sudo. Confirm the
GitHub runner inventory reports it online, idle, `Linux`, and `ARM64` with the
closed label set above. Remove any accidental generic labels through repository
runner administration before dispatching work.

Add a systemd drop-in to the generated service before making it claimable:

```ini
[Service]
NoNewPrivileges=true
```

Run `systemctl daemon-reload`, restart the runner service, and verify
`NoNewPrivs: 1` in `/proc/MAIN_PID/status` for its main process.

Retain automatic runner updates initially. If updates are disabled later, the
operator must own an image-based update process and GitHub's maximum supported
update interval. Subscribe to `actions/runner` security and release notices.

## Advisory Workflow

The hosted catalog job builds the exact canonical production bundle on
`ubuntu-24.04`; the ARM64 host only downloads and installs that immutable
bundle. The native job then:

1. verifies native host and Docker architecture, memory, physical network link
   and transport, SSD/NVMe placement, service-account privilege, temperature,
   and throttle state;
2. builds CameraAgent and its CameraAgent, acceptance, and SQLite catalog Unit
   projects in Release with warnings as errors and an invalid Docker endpoint;
3. publishes `linux-arm64`, inspects AArch64 ELF identities, rejects test
   assemblies, and writes a sorted SHA-256 manifest;
4. installs and integrity-checks the canonical SQLite catalog;
5. resolves and retains immutable base-image digests, builds the production
   CameraAgent image natively, verifies Linux/ARM64, source revision, and
   content-addressed image identity, and retains image history for inspection;
6. starts one read-only, run-scoped VirtualSky container with central and
   environmental delivery disabled, verifies module selection and catalog
   identity, and waits for a durable capture;
7. checks raw payload/manifest SHA-256 agreement, required preview and annotated
   artifacts, completed-only processing drain, SQLite integrity, throttle state,
   and owned-resource cleanup; and
8. uploads sanitized evidence for 30 days even when the native job fails.

Trigger a trusted default-branch run manually with:

```bash
gh api --method POST repos/RoySalisbury/HVO.SkyMonitor/dispatches \
  -f event_type=cameraagent-arm64
```

The harness is also locally callable on a qualified native host:

```bash
./scripts/test:cameraagent-arm64-ci /absolute/path/to/hyg-v4.2-p3-s2-r1.bundle
```

Raw evidence is ignored under `TestResults/issue-381/<run>-<attempt>/`. Workflow
artifacts contain no runner IP address, runner credentials, owner password,
installation token, coordinates, or application secrets. GitHub's workflow job
timestamps remain the queue-delay authority.

## Routine Operation

Before OS, firmware, Docker, .NET, or runner maintenance, remove
`hvo-skymonitor-arm64` or disable the runner and wait for the active job to
finish. After maintenance or reboot:

1. verify stable power, cooling, the preferred wired link or recorded WiFi
   fallback, storage mounts, free space, Docker, SDK, runner version, and the
   exact label set;
2. inspect the runner service and Docker logs for filesystem, I/O, network,
   thermal, power, or daemon failures;
3. dispatch one bounded advisory workflow; and
4. restore scheduling only after preflight, smoke, postflight, and cleanup pass.

Review disk headroom and BuildKit/NuGet cache growth monthly. Remove only
inactive cache owned by this runner; never use an unbounded global Docker prune
while a job may be active. Quarantine the runner after any unexplained reboot,
disconnect, throttle bit, undervoltage bit, filesystem error, SQLite integrity
failure, leaked container/network, or source-revision mismatch. Do not blindly
rerun those failures.

The harness rejects any container or image carrying its ownership label before a
run starts. Treat that condition as evidence of an interrupted cleanup: disable
the workload label, inspect the prior run and daemon logs, and remove only the
verified ARM64 CI resources before returning the runner to service.

## Recovery

Runner state is replaceable and is not backup material. For suspected
corruption or compromise:

1. disable the runner or remove its workload label;
2. retain only sanitized workflow artifacts and external service diagnostics;
3. delete the repository runner registration if compromise is plausible;
4. reimage the host and reinstall from checksum-verified sources;
5. register with a new short-lived token; and
6. repeat host qualification and the bounded advisory workflow.

Do not restore `_work`, `_diag`, `.credentials`, runner caches, Docker writable
layers, or service credentials from the old installation.

## Decommissioning

Remove `hvo-skymonitor-arm64`, drain or cancel active work, stop and disable the
service, and deregister with a fresh short-lived removal token. If the host is
lost, delete the runner through repository administration. Verify the runner is
absent from the API, remove runner credentials and work/cache/container state,
revoke exceptional network rules, and securely wipe or reimage the disk before
reuse.

## Promotion Decision

Keep the workflow advisory for at least 20 successful scheduled runs spanning
14 days. Promotion requires zero test or smoke flakes, throttle/power flags,
SQLite integrity failures, leaked resources, runner disconnects, or unexplained
reboots; stable queue and execution time; documented memory/storage/thermal
headroom; and a demonstrated update/reboot recovery. Even then, promote only
the bounded native build, Unit, publish, catalog, image, and VirtualSky smoke.
SQL Server Integration, full deployment contracts, physical ZWO work, and long
soaks remain separate.
