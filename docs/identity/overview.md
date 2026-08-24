# Identity Program Overview

This document defines the current device-registration and local-identity
architecture. Portfolio sequencing lives in `docs/roadmap.md`, virtual-first
completion history in `docs/project-plan.md`, and live execution state in the
linked GitHub issues.

## Device Registration and Bootstrap

1. On first use, CameraAgent creates a local device ID and self-attested
   verification code in `device-identity.json`.
2. An authenticated LogicHost user submits that pair at `/devices/register`,
   selects an observatory, and creates a pending registration. Current source
   stores a hash of the submitted code but has no independent channel that
   proves the code came from that device.
3. LogicHost issues a short-lived, Data Protection-protected envelope containing
   a unique 256-bit device key, registration token, endpoints, and the
   configured fleet OAuth client. New envelopes also pin the current immutable
   Observatory location version and hash.
4. The operator imports the envelope at CameraAgent `/devices/bootstrap`.
   CameraAgent posts it to LogicHost `/api/device/bootstrap`, decrypts the
   AES-256-GCM response, and writes `device-secrets.dat` with ASP.NET Data
   Protection.
5. CameraAgent sends its protected deployment-location snapshot during new
   bootstrap. LogicHost records the exact version as acknowledged or pending;
   pending resolution never blocks activation or replaces local capture geometry.
6. Successful sequential redemption changes the registration from Pending to
   Active, so later redemption fails. Concurrent redemption is not guarded by a
   database concurrency token and must be treated as an implementation gap. The
   device key then authenticates heartbeat, upload, and rig-profile requests.
7. An active CameraAgent idempotently retries its protected location version at
   `/api/device/deployment-location`. LogicHost returns the current acknowledgment,
   which CameraAgent stores in its protected secrets without changing the active
   local snapshot.
8. LogicHost can revoke the device at `/devices`. Device-key renewal and overlap
   are not currently implemented; recovery after revocation is a new
   registration.

The device key is per-device. The OAuth client credentials embedded in the
current payload are shared `DeviceBootstrap:CentralIdentity` configuration, not
a separately generated client for every device.

## Observatory and Deployment Authority

An Observatory is a LogicHost-owned nominal physical site, timezone, and optional
horizontal geodesic deployment radius. Multiple cameras may share it only when
they are co-located within that site boundary. A geographically remote camera is
a different Observatory. Longitude is decimal degrees east-positive in the
closed interval `[-180, 180]`; west longitude is negative. Elevation is metadata
and is not part of the horizontal boundary decision.

Each camera has a separate immutable deployment-location history containing its
precise coordinates, source classification, accuracy, and effective interval.
The protected CameraAgent version is authoritative for capture geometry while
offline. LogicHost is authoritative for Observatory membership and fallback,
records proposals, and requires explicit owner resolution for mismatches. It
never silently switches CameraAgent geometry.

Capture manifests carry only coordinate-free deployment ID, version, source,
accuracy, and interval. LogicHost freezes those facts and the matching historical
Observatory membership on the central frame. Unknown or pending versions remain
retrievable, but location-dependent central annotation work is quarantined until
acknowledgment. Legacy frames remain explicitly incomplete and are never assigned
current coordinates.

## CameraAgent Local Identity

CameraAgent uses a separate SQLite ASP.NET Identity database and remains usable
offline. It does not share LogicHost users, cookies, or SQL Server tables.

- `LocalIdentity:AdminEmail` and a temporary `LocalIdentity:AdminPassword` or
  `AdminPasswordFile` create the site owner once. Existing owner passwords are
  never reconciled from configuration.
- A newly seeded owner must replace the temporary password at
  `/Account/ReplaceTemporaryPassword` before ordinary owner UI or APIs are
  authorized. The requirement is durable in the local Identity database.
- After durable seeding, an explicit
  `LocalIdentity:AllowMissingAdminPassword=true` permits startup without a
  configured password. It does not permit first-time seeding without one.
- Local cookies are named `CameraAgent.Auth`, last 12 hours with sliding
  expiration, and revalidate the security stamp every 30 minutes.
- CameraAgent has no inbound bearer or API-key authentication. Its central OAuth
  and API-key modes authenticate outbound requests to LogicHost.
- Local email delivery, local API keys, roles, two-factor authentication, and
  passkeys are not implemented.
- Local self-registration is disabled. Operator pages, frame previews, gallery,
  artifact retrieval, capture controls, and outbox operations require the
  configured site owner; API denials return `401` or `403` instead of redirects.
- Password replacement rotates the Identity security stamp, refreshes only the
  completing session, and invalidates other owner cookies on their next request.
- Device identity and encrypted secrets are stored under
  `DeviceProvisioning:StateDirectory`.

Compose persists local Identity, Data Protection, and provisioning directories.
The encrypted secret file and Data Protection keys must be backed up and
restored together.

## Central Identity

LogicHost owns ASP.NET Identity and OpenIddict in its `SkyMonitor` SQL Server
database. Supported inbound authentication is the LogicHost Identity cookie,
`X-API-Key`, and locally validated OpenIddict bearer tokens.

OpenIddict exposes `/connect/authorize` and `/connect/token`. Development and
Testing use development certificates. Production loads configured signing and
encryption PFX files through `OpenIddictCertificates`; the split-host deployment
mounts those files and supplies their passwords through KeyPerFile. Coordinated
key overlap, automatic certificate rotation, token revocation, and a production
TLS topology are not implemented.

Device inventory and envelope issuance require the authenticated owner to match
both the registration snapshot and its current observatory. Most bearer APIs do
not yet enforce endpoint-specific scopes, so current LogicHost deployment must
still be treated as trusted until those controls are added.

## Security Invariants

- No usable owner password, application API key, OAuth client secret, or MinIO
  credential is committed in application settings. Integration fixtures inject
  isolated test credentials.
- Bootstrap material is encrypted, short-lived, single-use, and excluded from
  logs and retained evidence for the supported sequential workflow. The
  encrypted response includes its device key, so confidentiality still depends
  on trusted transport in Development and TLS in any reachable deployment.
- Device-key credentials are separate from local operator identity and can be
  centrally revoked. The fleet OAuth client is shared and requires separate
  rotation if an agent may have disclosed it.
- A registration is visible and eligible for envelope issuance only when its
  owner and its linked observatory owner both match the authenticated user.
  Legacy registrations with a missing observatory or inconsistent ownership
  fail closed: they are omitted from every owner's inventory, cannot receive a
  new envelope, and retain their existing durable record for investigation.
  Remediation requires an approved migration or data-repair procedure that
  establishes authoritative ownership or revokes the registration; the portal
  never guesses ownership from denormalized snapshot fields.
- Data Protection keys and local Identity/provisioning files are persistent
  runtime state and are never committed.
- CameraAgent operator APIs expose opaque artifact IDs and time-limited outbox
  references. Legacy raw-root, idempotency-key, and internal-record-ID routes
  are intentionally retired and have no compatibility alias.
- SQL Server, Redis, and MinIO ownership is explicit; Redis and MinIO root access
  are never substitutes for identity revocation.
- Azure Key Vault may be one future provider, but no cloud secret provider is
  required by the architecture or currently wired by the repository.

## Operational References

| Document | Purpose |
| --- | --- |
| [Identity operations](operations-runbook.md) | Current onboarding, rotation, revocation, incident, backup, and troubleshooting procedures. |
| [Secrets](../security/secrets.md) | Consumed secret catalog, providers, scope, rotation capability, and leakage controls. |
| [Local development](../runbooks/local-dev.md) | Supported direct-host and application-container workflows. |
| [Infrastructure operations](../runbooks/infra-operations.md) | Shared-service ownership, persistent mounts, reset, and recovery boundaries. |
| [Deployment-location reconciliation](../runbooks/deployment-location-reconciliation.md) | Inspect, acknowledge, reject, and troubleshoot deployment-location proposals. |
