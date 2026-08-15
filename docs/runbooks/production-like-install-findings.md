# Production-Like Installation Findings

This non-secret record captures the deployment assessment started on
2026-08-14 for issue #331. It is an operator aid, not evidence that a persistent
deployment passed the repository gates. Do not add secret values, machine or
daemon identities, or private evidence paths.

## Selected Topology

| Role | Host | Architecture | Capacity |
| --- | --- | --- | --- |
| LogicHost and fresh shared services | `home-docker` (`192.168.2.104`) | AMD64 | 8 CPUs, 32 GiB RAM, dedicated 3000 GiB runtime mount |
| Color CameraAgent | `allsky01` (`192.168.2.168`) | ARM64 | 4 CPUs, 17 GB RAM, about 3.9 TB free |
| Mono CameraAgent | `hvo-edge-01` (`192.168.2.185`) | ARM64 Pi 5 | 4 CPUs, 8.4 GB RAM, about 979 GB free under Docker root |
| Control/build host | `hvo-dev-02` (`192.168.1.13`) | AMD64 | 8 CPUs, 33.7 GB RAM |

Direct measured payload rates were 111.6 MiB/s from `allsky01` to
`home-docker` and 16.8 MiB/s from `hvo-edge-01` to `home-docker`. Both exceed
the roughly 1.2 MiB/s uncompressed raw rate per camera. The earlier cross-subnet
central topology did not provide adequate headroom.

Each 3552 x 3552 16-bit frame is 25,233,408 bytes. At a 20-second start
interval, both agents produce about 218 GB/day of raw input. The inspected edge
upload and central derivative graph can retain about 681 GB/day before temporary
processing I/O, so the first run remains a bounded campaign with measured
retention decisions.

## Host Preparation

Home Proxmox is `192.168.2.244`; `home-docker` is privileged LXC 104 with
identity UID/GID mapping. A stopped, protected pre-change backup is retained as
`local:backup/vzdump-lxc-104-2026_08_14-13_58_37.tar.zst`; its Zstandard stream
and tar traversal passed. The LXC was raised from 8 GiB to 32 GiB RAM and given
`nvme-vm:subvol-104-disk-0` at
`/srv/hvo/skymonitor/observatory-main`, size 3000 GiB, `backup=0`, owner
1000:1000, mode `0700`.

The first allocation exposed `nvme-vm/proxmox` with `mountpoint=none` despite
advertised `rootdir` support. No guest change or orphan volume remained. Its
mountpoint was corrected to `/nvme-vm/proxmox`; existing VM block volumes were
unaffected. All pre-existing `home-docker` containers and volumes restarted
healthy on their original Docker root.

Shared SQL Server, Redis, and MinIO volumes now remain project-named and labeled
but bind to `<shared runtimeRoot>/application/{sql,redis,minio}`. Digest-pinned
images started as UID/GID 1000:1000, persisted test state, restarted, and
recovered it. Temporary containers and credentials were removed.

## Access And Tooling

Azure Key Vault `hvo-central-kv` is reached through
`/home/roys/.config/hvo-bootstrap`. Relevant secret names include
`obs-access-shared-password`, `obs-proxmox-shared-admin-password`,
`obs-ssh-default-private-id-roys-hvo`,
`obs-ssh-default-public-id-roys-hvo`, and
`obs-proxmox-home-proxmox-api-token`. Values never belong in repository files
or evidence.

The approved `id_roys_hvo` key is installed where required. Local SSH aliases
and Docker contexts exist for `home-docker`, `home-proxmox`, `hvo-edge-01`, and
`allsky01`; `allsky01` uses its stable IP because DNS was unreliable.

Both Debian 13 ARM targets use the isolated checksum-pinned SQLite 3.45.1 CLI at
`/usr/local/lib/hvo/sqlite-3.45.1` through `/usr/local/bin/sqlite3`; Debian's
SQLite library remains untouched. Fixture and full production catalog lifecycle
tests pass on AMD64 and both ARM64 hosts. The retained production bundle is:

```text
/home/roys/.config/hvo-skymonitor/catalog-builds/
  hyg-v4.2-p3-s2-r1-20260814/hyg-v4.2-p3-s2-r1.bundle
```

Its manifest SHA-256 is
`2277dcf395f678c8222097616590ff4c268b7e384aefda5d533780cfe2cec0f1`.

Buildx builder `hvo-production-like` has a native AMD64 node on the control
daemon and a native ARM64 node on `hvo-edge-01`. Deployment validation now
supports healthy multi-node builders rather than requiring emulation on one
node.

## Existing State To Preserve

`home-docker` already runs unrelated `skymonitor-smtp`, `skymonitor-redis`, and
`skymonitor-minio` containers. The new deployment uses a distinct project,
ports, credentials, runtime root, and volumes. Candidate central ports 11433,
16379, 19000, 19001, 11025, 18025, 15174, 5174, and 25174 were free. Candidate
agent ports 5130/5131, 15130/15131, and 25130/25131 were free.

## Remaining Gates

- Complete review and merge of issue #331 from a clean revision.
- Generate target-specific calibration and clear-reference artifacts before
  enabling calibration/cloud-dependent processing.
- Stage observatory, environmental acquisition, transient mode, central worker,
  SMTP, and deployment-location configuration through the inventory contract.
- Exercise the implemented `transient-confirm` phase against generated allowlisted
  events and retain its run-owned Mailpit evidence without claiming general
  exactly-once SMTP.
- Generate isolated certificates and deployment-specific credentials.
- Pass read-only preflight, then a bounded six-hour ARM64/full-central campaign
  with CPU, RSS, cadence, temperature, throttling, network, storage, worker
  latency, backlog, provenance, review, and notification evidence.
- Establish application-consistent backup and retention for the `backup=0`
  runtime mount before sustained operation.
