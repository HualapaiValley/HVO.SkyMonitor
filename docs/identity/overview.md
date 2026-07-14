# Identity Program Overview

This document defines the current device-registration and local-identity
architecture. Live sequencing and completion remain owned by
`docs/project-plan.md`, epic #89, and their linked issues.

## Device Registration and Bootstrap

1. On first use, CameraAgent creates a local device ID and self-attested
   verification code in `device-identity.json`.
2. An authenticated LogicHost user submits that pair at `/devices/register`,
   selects an observatory, and creates a pending registration. Current source
   stores a hash of the submitted code but has no independent channel that
   proves the code came from that device.
3. LogicHost issues a short-lived, Data Protection-protected envelope containing
   a unique 256-bit device key, registration token, endpoints, and the
   configured fleet OAuth client.
4. The operator imports the envelope at CameraAgent `/devices/bootstrap`.
   CameraAgent posts it to LogicHost `/api/device/bootstrap`, decrypts the
   AES-256-GCM response, and writes `device-secrets.dat` with ASP.NET Data
   Protection.
5. Successful sequential redemption changes the registration from Pending to
   Active, so later redemption fails. Concurrent redemption is not guarded by a
   database concurrency token and must be treated as an implementation gap. The
   device key then authenticates heartbeat, upload, and rig-profile requests.
6. LogicHost can revoke the device at `/devices`. Device-key renewal and overlap
   are not currently implemented; recovery after revocation is a new
   registration.

The device key is per-device. The OAuth client credentials embedded in the
current payload are shared `DeviceBootstrap:CentralIdentity` configuration, not
a separately generated client for every device.

## CameraAgent Local Identity

CameraAgent uses a separate SQLite ASP.NET Identity database and remains usable
offline. It does not share LogicHost users, cookies, or SQL Server tables.

- `LocalIdentity:AdminEmail` and `LocalIdentity:AdminPassword` create and
  reconcile the site owner at startup.
- Local cookies are named `CameraAgent.Auth`, last 12 hours with sliding
  expiration, and revalidate the security stamp every 30 minutes.
- CameraAgent has no inbound bearer or API-key authentication. Its central OAuth
  and API-key modes authenticate outbound requests to LogicHost.
- Local email delivery, local API keys, roles, two-factor authentication, and
  passkeys are not implemented.
- Local self-registration, the dashboard, and frame endpoints are currently
  anonymous. The host is not ready for an untrusted network.
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
Testing use development certificates. Production signing/encryption certificate
loading, key overlap, token revocation, and a production TLS topology are not
implemented.

Device inventory/envelope issuance is not owner-filtered, and most bearer APIs
do not enforce endpoint-specific scopes. Current LogicHost deployment must be
treated as trusted and single-tenant until those controls are added.

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
- Data Protection keys and local Identity/provisioning files are persistent
  runtime state and are never committed.
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
