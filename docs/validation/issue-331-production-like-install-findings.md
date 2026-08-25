# Issue 331 Production-Like Installation Findings

This sanitized historical record captures the deployment assessment begun on
2026-08-14 for issue #331. It is point-in-time validation context, not a current
operations runbook or evidence that a persistent deployment passed every gate.
Issue #331 was delivered by PR #332; wired-network guidance followed in PR #405,
and PR #407 promoted the campaign with workflow/runtime corrections.

## Topology And Capacity

The assessed topology used one AMD64 central host for LogicHost and isolated
shared services, two ARM64 CameraAgent hosts, and a separate AMD64 control/build
host. The central host had 8 CPUs, 32 GiB RAM, and a dedicated 3 TiB runtime
mount. The edge hosts had 4 CPUs each and approximately 1 TiB or 4 TiB free.

Earlier host-preparation transfers measured 111.6 MiB/s from one edge and 16.8
MiB/s from the other, above the approximately 1.2 MiB/s uncompressed raw rate per
camera at that time. A later 25 MiB upload took 140.2 seconds over degraded Wi-Fi
and 0.744 seconds after selecting Ethernet. This established that reachability
alone was insufficient; sustained operation required a representative payload
test with margin under the artifact-upload deadline.

Each 3552 x 3552 16-bit frame is 25,233,408 bytes. At a 20-second start interval,
both agents produce about 218 GB/day of raw input. The inspected edge upload and
central derivative graph could retain about 681 GB/day before temporary
processing I/O, so the deployment remained a bounded campaign requiring explicit
retention and headroom decisions.

## Host And Tooling Findings

The central runtime mount was provisioned separately from the Docker root. An
initial storage mountpoint mismatch was corrected without leaving guest changes
or orphan volumes; pre-existing containers and volumes restarted healthy.
Shared SQL Server, Redis, and MinIO data used project-labeled bind mounts beneath
the dedicated runtime root. Digest-pinned images persisted and recovered test
state across restart, and temporary containers and credentials were removed.

Both ARM64 targets used the isolated checksum-pinned SQLite 3.45.1 CLI without
replacing the distribution SQLite library. Fixture and production catalog
lifecycle tests passed on AMD64 and both ARM64 hosts. The retained
`hyg-v4.2-p3-s2-r1` production bundle was built on 2026-08-14; its manifest-file
SHA-256 was
`2277dcf395f678c8222097616590ff4c268b7e384aefda5d533780cfe2cec0f1`.

The Buildx builder used native AMD64 and ARM64 nodes. Deployment validation was
updated to support healthy multi-node builders rather than requiring emulation
on one node.

## Retained Lessons

- Use wired Ethernet for sustained edge workloads unless representative payload
  testing demonstrates sufficient Wi-Fi margin.
- Keep shared-service state on a dedicated, capacity-reviewed runtime mount and
  verify restart recovery before application rollout.
- Keep deployments isolated from unrelated existing services through distinct
  projects, ports, credentials, roots, and volumes.
- Generate target-specific calibration, certificates, and deployment credentials
  before enabling dependent processing or conducting a bounded campaign.
- Establish application-consistent backup and retention before sustained
  operation on storage excluded from platform-level backup.

Hostnames, addresses, private paths, credential-store identifiers, SSH aliases,
backup identifiers, and unused candidate ports were removed because they are not
required to reproduce these conclusions.
