# Identity Program Overview

This file replaces `hardening-summary.md`, `operations-index.md`,
`agent-registration-plan.md`, and `non-azure-delta.md`. It captures the
current status of the Central Identity initiative, summarizes the device
registration workflow, and points to the remaining authoritative docs.

## 1. Phase Status

- **Identity Hardening:** ✅ Complete as of **15 Nov 2025**.
- Scope covered: secret storage + rotation, TLS readiness, structured
  logging, authentication metrics, rate limiting, and runbooks.
- See `docs/security/secrets.md` for the consolidated secrets catalog and
  `.env.template` for non-sensitive defaults.

### Key Achievements

| Area | Highlights |
| --- | --- |
| Secret storage | Azure Key Vault recipes for OpenIddict certs, signed-ticket HMAC, API-key salt, device bootstrap bundle, and TLS certs. |
| Operations | `docs/identity/operations-runbook.md` documents key rotation, onboarding, incident response, certificate renewal, and troubleshooting. |
| Observability | `AuthenticationEventLogger`, `AuthenticationMetrics`, structured logging for auth flows, and Prometheus metrics (`auth.token_requests`, `auth.apikey_authentication`, etc.). |
| Rate limiting | Token endpoint (60 req/min), API endpoints (1000 req/min), and global policies (10000 req/min) configurable via appsettings/env vars. |
| Documentation | Runbooks + reference docs now live under `docs/security` and this folder; legacy Aspire/Azure-only guides were removed. |

## 2. Device Registration & Bootstrap Flow

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

## 3. Camera-Agent Local Identity Stack

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

## 4. Operational References

| Document | Purpose |
| --- | --- |
| `docs/security/secrets.md` | Single source for secrets, certificates, and rotation cadence across the platform. |
| `docs/identity/operations-runbook.md` | Step-by-step procedures (key rotation, onboarding, incident response, certificate mgmt, monitoring). |
| `docs/runbooks/local-dev.md` | How to launch the stack locally, including identity prerequisites. |
| `docs/runbooks/infra-operations.md` | Docker/Testcontainers maintenance flows referenced by operators. |

## 5. Next Steps & Recommendations

1. **Deploy to staging** with Key Vault-backed secrets, confirm
   rate-limiting behavior, and validate monitoring dashboards.
2. **Load testing** to exercise authentication throughput and confirm
   rate-limit thresholds.
3. **Security audit** focusing on structured logging (ensure no sensitive
   data is logged) and envelope revocation paths.
4. **Production rollout** once staging validation is complete; integrate
   with TLS automation and incident runbooks.
5. **Future enhancements** tracked under `docs/projects/infra-modernization.md`
   (certificate automation, HTTPS hardening, additional Testcontainers
   coverage).

## 6. File Inventory After Cleanup

```
docs/identity/
  overview.md              # (this file)
  operations-runbook.md    # authoritative operational procedures
```

Use git history if you need the detailed chronicles originally stored in
`hardening-summary.md` or the planning prompts.
