# Identity Program Overview

This file replaces historical identity planning narratives and defines the
device-registration and local-identity architecture only. Live implementation
status, sequencing, and completion are owned by `docs/project-plan.md`, epic
#89, and their linked issues.

## 1. Device Registration And Bootstrap Flow

The camera-agent registration model remains cloud-independent so it can
be replayed after any future rollback. Operators perform the following
steps:

1. **Agent bootstrap identity** – on first launch the agent creates a
   `DeviceId`, verification code, and friendly metadata stored locally.
2. **Portal verification** – operator enters the `DeviceId` + code inside
   LogicHost ("Observatory → Devices"), which records a pending
   registration.
3. **Envelope issuance** – LogicHost generates an envelope that contains
   a per-device 256-bit `DeviceKey`, scoped client credentials, and
   optional registration token. The envelope is encrypted and single-use.
4. **Agent import** – operator pastes or scans the envelope into the
   agent UI. The agent posts to `/api/device/bootstrap`, decrypts the
   response with the `DeviceKey`, and stores credentials inside its
   secure store (DPAPI/libsecret + ASP.NET Data Protection).
5. **Activation & rotation** – the agent starts sending heartbeats;
   LogicHost can rotate credentials or revoke the device using the same
   `DeviceKey` channel.

Design goals preserved from the previous plan:
- No cloud credentials ship inside the agent container image.
- Every device receives least-privilege credentials (`api.camera`,
  `api.frames`, `api.images`).
- Envelope material expires within ~10 minutes and is single-use to
  avoid replay attacks.

## 2. CameraAgent Local Identity Stack

The CameraAgent ships its own ASP.NET Identity instance (SQLite backing)
so it can run fully offline:

- `LocalIdentityOptions` config section seeds the admin account during
  provisioning.
- Shared Identity UI (`Components/Account/**`) mirrors LogicHost so the
  operator experience stays consistent.
- `CameraAgentApiKeyValidator` and `CameraAgentIdentitySeeder` enforce
  API-key audits and initial bootstrap without Azure dependencies.
- Device provisioning data (`DeviceIdentityStore`, `DeviceSecretStore`,
  `DeviceBootstrapWorkflow`) persists under
  `Configuration/DeviceProvisioningOptions` and survives container
  restarts.
- Its astronomy catalog is a separate local, read-only SQLite snapshot; it is
  not part of local Identity or any shared SQL Server database.

## 3. Operational References

| Document | Purpose |
| --- | --- |
| `docs/security/secrets.md` | Single source for secrets, certificates, and rotation cadence across the platform. |
| `docs/identity/operations-runbook.md` | Quarantined historical procedures pending a SQL Server/Compose/current-route replacement; do not execute stale PostgreSQL/Kubernetes/Azure-only commands. |
| `docs/runbooks/local-dev.md` | How to launch the stack locally, including identity prerequisites. |
| `docs/runbooks/infra-operations.md` | Docker/Testcontainers maintenance flows referenced by operators. |

## 4. Security Invariants

- No cloud credentials ship in a CameraAgent image.
- Device credentials are scoped, revocable, and distinct from local operator
  identity.
- Bootstrap/envelope material is encrypted, single-use, short-lived, and
  audited without logging secret content.
- Data-protection keys and local Identity/device state are persistent runtime
  state and are never committed.
- Azure Key Vault may be one production secret provider; it is not required by
  the platform architecture.
- Current implementation and operational gaps are tracked only in the project
  plan and production-readiness issues.

## 5. File Inventory

```
docs/identity/
  overview.md              # (this file)
  operations-runbook.md    # quarantined historical procedures pending replacement
```

Use git history if you need the detailed chronicles originally stored in
`hardening-summary.md` or the planning prompts.
