# Identity and Security Operations Runbook

This runbook describes operations supported by the current HVO.SkyMonitor
source and development deployment. It does not invent administrative controls
for capabilities that are not implemented.

## Supported Boundary

| Area | Current support |
| --- | --- |
| LogicHost users | Self-registration, email confirmation, login, password reset, two-factor authentication, passkeys, and self-service account management under `/Account/*`. |
| API keys | Owner-managed `Read` and `ReadWrite` keys at `/Account/Manage/ApiKeys`; create, activate, deactivate, and delete are supported. |
| OAuth | Authorization-code with PKCE, client credentials, password, and refresh-token grants through `/connect/authorize` and `/connect/token`. |
| Devices | Registration at `/devices/register`, inventory and revocation at `/devices`, and CameraAgent import at `/devices/bootstrap`. |
| Local CameraAgent users | SQLite-backed local Identity and one configuration-seeded site owner. This identity is separate from LogicHost. |
| Runtime state | SQL Server `SkyMonitor`, the two approved MinIO buckets, prefixed Redis cache keys, both hosts' Data Protection keys, CameraAgent local Identity, and CameraAgent provisioning files. |
| Observability | `/alive`, `/health`, `/metrics`, structured token/API-key events, and ASP.NET request traces. |

The repository does not currently provide production OpenIddict certificate
loading, signing-key overlap, OAuth token revocation, revoke-all sessions,
cross-user API-key administration, device-key rotation, roles, signed-URL
routes, or an administrator user-management portal. Do not replace those gaps
with direct database edits.

Application Compose is a Development topology over HTTP. A production TLS
terminator, forwarded-header policy, external issuer, and OpenIddict signing and
encryption certificate loader must be implemented and tested before this
Compose file can be treated as a production identity deployment.

Current authorization still has deployment limits: CameraAgent
self-registration is disabled and its operator, frame, gallery, artifact, and
outbox surfaces are restricted to the configured local site owner, but most
LogicHost bearer-protected APIs do not enforce a route-specific scope. Device
listings and envelope issuance are owner-scoped, but the remaining limits still
require a trusted development environment. Do not expose either host to an
untrusted network or claim complete least privilege until those controls are
implemented and tested.

## Safety Rules

1. Never include passwords, connection strings, API keys, client secrets,
   bearer or refresh tokens, bootstrap envelopes, device keys, Data Protection
   keys, or full environment dumps in tickets, logs, shell history, or retained
   evidence.
2. Use only the `SkyMonitor` SQL Server database, Redis keys beginning with
   `skymonitor:`, and MinIO buckets `skymonitor-diagnostics` and
   `skymonitor-artifacts`.
3. Never clear a Redis database. Redis is not the Identity or OpenIddict store,
   so deleting Redis data does not revoke cookies or tokens.
4. Never use MinIO root credentials from an application. They are reserved for
   provisioning the scoped application service account.
5. Preserve evidence and obtain an approved backup before reset, deletion, or
   credential revocation. Record the operator, approver, UTC time, affected
   identity, reason, validation result, and rollback decision without recording
   the credential.
6. Use Identity/OpenIddict services and current UI actions. Direct SQL edits can
   bypass password hashing, security stamps, concurrency checks, and audits.

## Preflight and Smoke Checks

From the repository root, inspect application state and run the no-secret smoke
checks:

```bash
./scripts/infra:status
./scripts/identity:smoke
```

The smoke script expects the supported environment to be healthy and verifies:

- `200` from `/alive`, `/health`, OpenID discovery, and anonymous
  `/api/v1.0/status`.
- `401` from `/api/v1.0/status/detailed` without an API key.
- `401` from `/api/v1.0/status/protected` without a bearer token.

To include the API-key success probe without putting the key in the command
line, read it into the process environment temporarily:

```bash
read -rsp 'SkyMonitor API key: ' SKYMONITOR_API_KEY
export SKYMONITOR_API_KEY
./scripts/identity:smoke
unset SKYMONITOR_API_KEY
```

Do not retain terminal capture from a secret-entry session. The anonymous
`/api/v1.0/status` route is a liveness response and is not evidence that a
credential works.

## Onboarding

### LogicHost user

1. Open `/Account/Register` and create the user-owned account.
2. In the development topology, retrieve the confirmation message from the
   configured Mailpit instance. In another environment, use the configured SMTP
   provider.
3. Follow the confirmation link and sign in at `/Account/Login`.
4. Verify account management at `/Account/Manage`.

There is no invitation workflow, role assignment, or administrator-created
human account flow. Production onboarding therefore requires a separate
approved implementation before public self-registration can be enabled.

### CameraAgent site owner

For Compose, set `CAMERA_AGENT_ADMIN_PASSWORD` and the fleet-scoped
`CAMERA_AGENT_OAUTH_CLIENT_SECRET` in the ignored `.env` before starting the
stack. For a direct host, store the owner password in the CameraAgent
user-secrets store:

```bash
./scripts/user-secret:set \
  src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj \
  'LocalIdentity:AdminPassword'
```

The host creates or reconciles the configured `LocalIdentity:AdminEmail` owner
at startup. It also reconciles the password to
`LocalIdentity:AdminPassword`, so a password changed in the UI will be reverted
on the next startup unless configuration is changed at the same time.

CameraAgent email delivery is not implemented; its email sender only records a
development message. Do not rely on local password-reset email as a recovery
path.

### Device registration and bootstrap

1. Start the CameraAgent, sign in locally, and open `/devices/bootstrap`.
2. Record the displayed device ID and verification code without placing them in
   retained logs or screenshots.
3. Sign in to LogicHost, create an active observatory at `/observatories` if one
   does not already exist, and open `/devices/register`.
4. Enter the device ID, self-attested code, observatory, and friendly name. The
   resulting pending registration and envelope are short-lived.
5. Return to CameraAgent `/devices/bootstrap`, import the envelope, and wait for
   successful secret persistence and rig-profile seeding.
6. Confirm the registration is Active at LogicHost `/devices`, then confirm the
   `fleet-heartbeat` CameraAgent health check reaches Healthy after the first
   acknowledged status report.

The verification code is not checked against an independent device channel;
current registration is operator self-attestation, not proof of device
possession. Successful sequential bootstrap changes the central registration
from Pending to Active and later replay fails. Concurrent redemption is not
protected by a SQL concurrency token. Perform bootstrap only on a trusted
network with one operator until that gap is fixed.

LogicHost lists and issues envelopes only when the authenticated user owns both
the registration snapshot and its current observatory. A legacy registration
whose observatory is missing or owned by a different user is hidden from all
owner inventories and envelope issuance fails before credential fields change.
Preserve the record for investigation and use only an approved migration or
data-repair procedure to establish authoritative ownership or revoke it; do not
reassign it from snapshot display fields or ad hoc portal actions.

The generated device key is unique, but the OAuth client credentials included
in the current envelope come from shared `DeviceBootstrap:CentralIdentity`
configuration; they are not generated per device. Treat that client as
fleet-scoped. The encrypted bootstrap payload and its AES key travel in the
same HTTP response, so payload encryption is not a substitute for TLS.

Compose persists these CameraAgent files together:

- `/app/App_Data/identity/cameraagent_identity.db`
- `/app/DataProtection-Keys/*`
- `/app/data/provisioning/device-identity.json`
- `/app/data/provisioning/device-secrets.dat`

The encrypted secrets file and its Data Protection key ring are one recovery
unit. Restoring only one of them makes the secrets unreadable.

The split-host orchestrator performs the same workflow by automating the existing
`/Account/Login` antiforgery form without retaining POST across the login
redirect, verifying the resulting cookie against an owner-only endpoint, then calling `/identity`, `/bootstrap`, and
`/continuity`. There is no anonymous deployment password endpoint. Identity and
bootstrap require the normal owner cookie plus antiforgery validation; continuity
requires owner-read authorization. LogicHost observatory, registration,
envelope, and continuity calls require an owner-scoped API key. Continuity
responses contain identifiers, queue counts, durable sequence maxima, bounded
artifact checksum metadata, and expected/current rig-profile versions and hashes
only, never verification codes, envelopes, credentials, paths, or payloads. The
owner-only deployment telemetry projection requires a capture sequence and
artifact ID and returns only the matching bounded capture-pipeline capture ID,
trace ID, and span ID. It never uses the telemetry HTTP request Activity and does
not expose request or response bodies.

After the owner database is seeded, CameraAgent can restart without retaining the
configuration password. Split-host automation deletes the remote plaintext
password and its password-file setting immediately after login, then records the
non-secret `AllowMissingAdminPassword` restart opt-in. A missing password is valid
only when that opt-in is explicit and the durable owner already exists; initial
seeding still fails.

Every temporary remote credential registration binds the full inventory target
identity. Cleanup re-correlates that identity before and after deletion and
fails the phase if an alias or target changes. A retained credential is safer
than deleting the same path on an unverified host; investigate and remove it on
the originally correlated target.

### Fleet heartbeat operation

- CameraAgent stores status reports in `<RawIngressRoot>/.fleet/fleet-status.db`
  using SQLite WAL before delivery. Preserve that database with the raw-ingress
  state when recovering an agent. It contains operational status but no device
  key, registration token, storage path, or frame payload.
- Reports use a durable installation-wide sequence and a new boot-session ID on
  each process start. Significant health, pressure, configuration, quarantine,
  and availability transitions are immutable. Only the newest never-attempted
  routine report is coalesced during an outage.
- LogicHost uses the last advancing server receipt time for liveness: under 150
  seconds is current, 150 to under 300 seconds is degraded, and 300 seconds or
  more is offline. Agent time is retained only as an apparent clock-skew or
  delayed-delivery diagnostic and never controls ordering.
- LogicHost retains compact routine receipts for 24 hours and significant
  snapshots for 30 days. The hourly retention worker deletes indexed batches of
  at most 5,000 records and never age-deletes the current fleet-state row.
- A cross-agent credential attempt returns the same generic `401` as another
  invalid key and emits audit event `2404` with bounded registration identifiers
  and reason `cross-agent-credential`; it never logs the key or payload.
- `fleet-heartbeat` is Degraded while retrying and Unhealthy for durable queue
  corruption, overflow, quarantine, or blocked credentials. `fleet-status` on
  LogicHost reports identifier-free online/degraded/offline counts and becomes
  Unhealthy only when central fleet persistence or retention fails.

## API-Key Lifecycle

### Create and validate

1. Sign in to LogicHost and open `/Account/Manage/ApiKeys`.
2. Select the least privilege required: `Read` or `ReadWrite`.
3. Set an expiration when the client supports planned replacement.
4. Create the key and place its one-time plaintext value directly into the
   approved secret store. Do not paste it into an issue or chat.
5. Run the API-key smoke probe above. The protected evidence route is
   `/api/v1.0/status/detailed`.
6. Confirm the `API Key Created` and `API key used` structured events by key ID,
   not by raw key.

### Make-before-break rotation

1. Create replacement key B with the same or narrower access and a distinct
   display name.
2. Update one client to key B and validate
   `/api/v1.0/status/detailed` plus its real least-privilege operation.
3. Deploy key B to the remaining clients.
4. Deactivate old key A. Validate that clients continue to work and that an
   isolated probe using A receives `401`.
5. Keep A deactivated through the approved rollback interval. Reactivation is
   the rollback.
6. Delete A only after rollback is no longer required and audit evidence has
   been retained.

Rotation is a manual overlap workflow; there is no atomic rotate action and the
database `LastUsedUtc` field is not currently updated. Use structured API-key
events for use review.

### Compromise or revocation

The owner can immediately deactivate or delete a key at
`/Account/Manage/ApiKeys`. Prefer deactivation while investigation and rollback
remain possible. Deletion is irreversible and removes the SQL row. There is no
cross-user administrator page, so a separate incident-approved implementation
is required when the owner cannot authenticate.

## OAuth Client Credentials

Confidential clients are configuration-seeded with these exact sections:

- `DatabaseSeed:ConfidentialClients:<index>:ClientId`
- `DatabaseSeed:ConfidentialClients:<index>:ClientSecret`
- `DatabaseSeed:ConfidentialClients:<index>:Scopes:<index>`
- `DeviceBootstrap:CentralIdentity:ClientCredentials:ClientId`
- `DeviceBootstrap:CentralIdentity:ClientCredentials:ClientSecret`
- `DeviceBootstrap:CentralIdentity:ClientCredentials:Scopes:<index>`

Current scopes are `api.admin`, `api.camera`, `api.frames`, `api.images`,
`api.viewer`, and `api.webhooks`. Use only scopes consumed by the client.

Startup reconciles a changed confidential-client secret immediately. It does
not retain the old secret, so zero-downtime overlap is not supported through
configuration seeding. Schedule a coordinated maintenance window or use a
separate client ID for make-before-break migration. Existing CameraAgents do
not receive a changed bootstrap client secret until they are reprovisioned.

## Device Revocation

1. Open LogicHost `/devices` and identify the exact registration by device ID,
   owner, and observatory.
2. Preserve sanitized heartbeat and incident evidence.
3. Use the registration's Delete action and type the device ID when prompted.
   The UI records a fixed portal-revocation note; retain the incident reason in
   the approved incident record.
4. Confirm status is Revoked and that heartbeat, upload, and rig-profile calls
   using the old device key fail.
5. After central revocation is confirmed, clear imported credentials from the
   CameraAgent bootstrap page if the local host is controlled.

Clearing local credentials does not revoke the central registration. A revoked
registration cannot be reactivated with the current workflow; recovery is a
new registration and bootstrap. Device-key renewal or overlap is not
implemented. Revocation also does not disable the shared fleet OAuth client. If
an agent may have disclosed that secret, schedule fleet-client replacement and
reprovision every agent; there is no per-device OAuth containment today.

## Certificates, TLS, and Data Protection

Development and Testing use OpenIddict development signing and encryption
certificates. Production source currently has no certificate loader, option
schema, secure mount, or overlap test. Therefore there is no supported
production OpenIddict certificate-rotation command. Do not adapt an Azure,
Kubernetes, or certificate-store example without first implementing and testing
the chosen provider.

TLS certificate renewal belongs to the actual TLS termination point. The
repository's application Compose topology is HTTP-only and does not define that
owner. Record the terminator, SANs, issuer, expiry, renewal method, deployment,
and rollback in the production deployment's own runbook.

Both hosts persist ASP.NET Data Protection keys to
`DataProtection-Keys/`. Compose bind-mounts those paths and LogicHost's
Development certificate-store home. Losing LogicHost keys
invalidates cookies and outstanding bootstrap envelopes; losing CameraAgent
keys makes `device-secrets.dat` unreadable. A rebuild preserves keys. An
explicit reset deletes the selected host's keys.

## Backup, Restore, and Reset

### Recovery set

| State | Authority | Required recovery owner |
| --- | --- | --- |
| SQL Server `SkyMonitor` | Users, API keys, OpenIddict, registrations, metadata | SQL Server operator; use an approved SQL Server backup with encryption, retention, and restore verification. |
| `skymonitor-artifacts` | Immutable artifact payloads | MinIO operator; preserve object keys, metadata, checksums, and version/lifecycle state. |
| `skymonitor-diagnostics` | Diagnostic objects | MinIO operator; retain only as required by policy. |
| Redis `skymonitor:*` | Disposable cache | No identity restore dependency; rebuild from authoritative state. |
| LogicHost Data Protection | Cookies and protected envelopes | Application operator; back up with restricted permissions. |
| CameraAgent Identity, Data Protection, provisioning | Local users and encrypted device credentials | Site operator; back up and restore as one consistency unit. |
| CameraAgent `data/agent` and `data/archive` | Capture payloads and durable outbox state for the packaged sample configuration | Site operator; preserve files, sidecars, indexes, and pending upload manifests. |
| Mailpit | Development email capture | Disposable; never an authoritative account record. |

SQL Server is provisioned separately from repository Compose. The SQL Server
operator must provide the exact instance/container, backup destination,
encryption, retention, and tested restore command. Do not substitute a command
for another database engine. Likewise, MinIO backup must use the approved
site-specific replication or snapshot procedure; copying SQL metadata without
the corresponding objects is not a complete backup.

The application connection must use a login scoped to `SkyMonitor`, never `sa`
or another instance administrator. Production separates the one-shot
`ConnectionStrings:skymonitordb-migrations` principal from runtime
`ConnectionStrings:skymonitordb`; runtime receives no migration secret and
startup rejects effective DDL authority. The schema-v6 split-host workflow
applies the migration role before controlled initialization and the runtime role
afterward. Redis similarly uses a runtime ACL identity restricted to the
declared prefix. See
[`logichost-database-initialization.md`](../runbooks/logichost-database-initialization.md)
and [`split-host-preflight.md`](../runbooks/split-host-preflight.md).

Back up bind-mounted application state to an encrypted, access-controlled
destination outside the runtime data root:

```bash
./scripts/infra:backup-app-state /approved/encrypted/backup-directory
```

The script stops both applications, archives only the supported LogicHost and
CameraAgent application paths, creates a versioned internal inventory and an
adjacent relocatable one-line `.sha256` checksum, and leaves both applications
stopped for coordinated SQL Server and MinIO backups. It never archives the
catalog or arbitrary runtime-root content. Do not print or attach the archive,
inventory, or checksum. Start applications only after all authoritative
backups complete.

After the SQL Server and MinIO operator restores are staged, restore application
state with:

```bash
./scripts/infra:restore-app-state \
  /approved/encrypted/backup-directory/hvo-application-state-UTC_TIMESTAMP.tgz
```

The restore supports both new inventory archives and legacy `.tgz` plus
`.sha256` pairs. It rejects unsafe or inconsistent entries before installation,
and, for new archives, checks that the extracted inventory has an exact
one-to-one file/directory mapping including nested directories and modes. Legacy
archives retain structural and post-walk type checks without inventory-level
verification. Restore preserves the target catalog regardless of legacy archive
catalog content and retains the old runtime root as a rollback directory. It
starts LogicHost and waits for `/health` before starting
CameraAgent and waiting for CameraAgent `/health`. On failure it attempts exact
old-tree rollback and leaves both applications stopped. If a collision prevents
exact rollback, the durable marker and rollback state remain for operator
recovery. Delete the rollback directory only after all post-restore checks pass.
Recovery and rollback never rename or delete a state tree unless both
applications stop successfully. If stop fails, stop both applications manually
and rerun the same restore command; do not delete or edit the marker or rollback
directory.

Restore order is:

1. Validate recovery-set authorization, the target environment, and all
   operator-managed backup evidence.
2. Stop both applications and keep them stopped through shared-service restore.
3. Restore MinIO buckets and verify object inventory and checksums.
4. Restore SQL Server, run database consistency checks, and validate its MinIO
   object references.
5. Run `infra:restore-app-state` to validate and install, through staged
   same-filesystem renames and a durable phase marker, both hosts' Data
   Protection state plus CameraAgent Identity, provisioning, payload,
   sidecar/index, outbox, quarantine, and archive state as one local unit.
6. Allow the script to start LogicHost, wait for LogicHost `/health`, start
   CameraAgent, and wait for CameraAgent `/health`.
7. Run authentication smoke checks and verify users, clients, API-key state,
   registration state, MinIO object references, and direct credential
   rejection/acceptance without exposing secrets.
8. Confirm durable fleet heartbeats resume, pending heartbeat delivery drains,
   and fleet-heartbeat health returns to its expected state.
9. Wait for the central recovery inventory to complete or explicitly disposition
   its bounded findings. Verify SQL rows, MinIO objects, checksums, derivative
   jobs, lineage, and retention references before deleting rollback or
   quarantine state.

The seeder may reconcile configured user passwords and confidential-client
secrets after restore. Confirm the effective secret source before startup.

These commands are destructive and have no automatic rollback:

```bash
./scripts/infra:start --reset logichost
./scripts/infra:start --reset cameraagent
./scripts/infra:reset logichost cameraagent
```

`--rebuild` preserves mounted state. `--reset logichost` removes LogicHost Data
Protection keys and its Development certificate store. `--reset cameraagent`
removes local Identity, Data Protection,
provisioning, packaged sample payload, archive, and outbox state. Shared SQL
Server, Redis, MinIO, and Mailpit data are never deleted by these scripts.

## Redis and MinIO Safety

Redis is currently a cache, not an authentication/session authority. To inspect
repository-owned keys, use cursor-based scanning with the password supplied
through the process environment and retain only the key-name manifest:

```bash
(
  read -rsp 'Redis password: ' REDISCLI_AUTH
  export REDISCLI_AUTH
  trap 'unset REDISCLI_AUTH' EXIT
  umask 077
  REDIS_KEY_MANIFEST="$(mktemp -t hvo-redis-keys.XXXXXX)"
  if ! redis-cli -h "$REDIS_HOST" -p "$REDIS_PORT" -n 0 --scan \
    --pattern 'skymonitor:*' > "$REDIS_KEY_MANIFEST"; then
    rm -f "$REDIS_KEY_MANIFEST"
    exit 1
  fi
  printf 'Review Redis key manifest: %s\n' "$REDIS_KEY_MANIFEST"
)
```

Review endpoint, logical database, prefix, count, and every matched key before
any deletion. If deletion is approved, delete only reviewed keys one at a time
with `UNLINK`; never use a database-wide clear operation. Cache deletion does
not revoke Identity cookies, OpenIddict tokens, API keys, or device keys. Delete
the manifest securely after approved review; do not retain it in a support
bundle.

LogicHost uses `MINIO_ACCESS_KEY` and `MINIO_SECRET_KEY`. The root account is
used only by `./scripts/infra:provision-minio-account`. The current provisioning
script does not rotate an existing service-account secret, so MinIO credential
rotation requires an operator-owned MinIO procedure and validation with the
scoped policy before applications are restarted.

## Incident Response

1. **Detect and classify:** identify affected identities, hosts, UTC interval,
   and observed behavior from sanitized logs and metrics.
2. **Preserve evidence:** capture status, selected structured events, bounded
   metrics, and approved service snapshots. Redact personal data and exclude all
   credential material and bootstrap payloads.
3. **Contain:** deactivate the exact API key, revoke the exact device, isolate a
   client, or stop the affected host. Do not clear shared services.
4. **Investigate:** correlate key/client/device IDs, grant type, route, status,
   and correlation ID. Treat unknown secret exposure as compromise.
5. **Recover:** replace supported credentials, restore authoritative state if
   required, start dependencies in order, and run the smoke checks.
6. **Verify:** old credentials fail, replacement credentials have least
   privilege, health is stable, no unexpected retries occur, and retained
   evidence contains no secrets.
7. **Close:** record cause, impact, actions, validation, residual unsupported
   controls, owner, and follow-up issue.

User offboarding cannot currently disable another user's account or revoke all
cookies/tokens through a supported operator API. Preserve the requirement and
escalate it as an implementation gap; do not publish emergency SQL edits.

## Monitoring and Troubleshooting

Current authentication instruments from meter `HVO.SkyMonitor.Authentication`
are:

| Instrument | Unit | Bounded labels |
| --- | --- | --- |
| `auth.token_requests` | requests | `grant_type`, `result` |
| `auth.token_request_duration` | milliseconds | `grant_type`, `result` |
| `auth.apikey_authentication` | attempts | `result`, successful `access_level` |

`auth.login_attempts` and `auth.authentication_duration` are defined but not
currently called. Do not build mandatory alerts from them. Authentication
loggers do not yet assign stable nonzero event IDs; query by message template
and structured fields such as client ID, grant type, key ID, access level,
endpoint, result, and correlation ID. Never use a raw credential or user email
as a metric label.

| Symptom | Check |
| --- | --- |
| `401` from `/api/v1.0/status/detailed` | Confirm `X-API-Key` is present, active, unexpired, and owned by the caller. Validate with `identity:smoke`; do not use anonymous `/status`. |
| `401` from `/api/v1.0/status/protected` | Acquire a fresh bearer token and confirm token endpoint, client ID, secret source, expiry, and signature validation. Do not decode or paste the token into support evidence. |
| `401` or `403` on a mutation after a read succeeds | A multi-scheme challenge or endpoint policy rejected access. Check API-key `Read`/`ReadWrite`, required bearer scope, and the endpoint's current authorization attribute. |
| Token request fails | Check `/health`, `/connect/token`, configured client permissions, grant type, and sanitized LogicHost logs. |
| Device bootstrap fails | Confirm Pending status, envelope lifetime, exact device ID, and one-time use. Generate a new pending registration after expiry or consumption. |
| Device calls fail after restore | Restore CameraAgent `device-secrets.dat` with its matching Data Protection key ring and confirm central registration is Active and unexpired. |
| Cookies fail after rebuild | Verify the matching host's `DataProtection-Keys` bind mount was preserved and readable. |
| MinIO access fails | Check scoped application credentials and the two approved buckets; do not switch the application to root credentials. |

## Validation Evidence

The executable documentation gate is:

```bash
./scripts/docs:audit-operations
bash -n scripts/identity:smoke scripts/infra:start scripts/infra:reset \
  scripts/infra:backup-app-state scripts/infra:restore-app-state scripts/test:infra
./scripts/test:infra
```

Behavioral evidence is provided by the existing Unit and Integration suites for
token issuance, API-key/bearer protection, client reconciliation, device
credential validation, CameraAgent bootstrap, dependency health, Redis, MinIO,
and Mailpit. See [CI pipeline](../runbooks/ci-pipeline.md) for the canonical
commands and [secrets guidance](../security/secrets.md) for the configuration
catalog.
