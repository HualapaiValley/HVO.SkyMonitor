# Secrets and Configuration Guide

This guide is the source of truth for secret-bearing configuration currently
consumed by HVO.SkyMonitor. It distinguishes repository-supported providers
from possible production providers that are not wired in source.

## Configuration Layers

Later .NET providers override earlier providers:

1. `appsettings.json` and `appsettings.{Environment}.json` contain defaults and
   Development-only fixture credentials.
2. .NET User Secrets are enabled for both web projects and are preferred for
   direct local runs.
3. Environment variables use double underscores for nested .NET keys.
4. Command-line configuration has the highest normal application precedence.

Repository helpers also load the ignored root `.env` and optional ignored
`.devcontainer/devcontainer.local.env`. Root `.env` names such as
`REDIS_PASSWORD` are a Compose/script contract; Compose and
`./scripts/with-env` translate them to .NET names such as
`Redis__Configuration`.

`CAMERA_AGENT_LOGIC_BASEURL` is the container-internal LogicHost URL.
`CAMERA_AGENT_PUBLIC_LOGIC_BASEURL` is the agent-reachable URL embedded by a
direct LogicHost bootstrap response. Do not use Docker DNS names in direct-host
credentials.

The repository does not currently call `AddAzureKeyVault`, configure another
cloud secret provider, or protect Data Protection keys with a key-management
service. A production deployment may add a provider-neutral or cloud-specific
store, but that provider, workload identity, availability model, rotation, and
recovery must be implemented and tested in that deployment.

Release automation is a separate control plane and does not add Azure Key Vault
as an application runtime provider. The `production-release` GitHub environment
uses OIDC subject
`repo:RoySalisbury/HVO.SkyMonitor:environment:production-release` and the
least-privileged `Key Vault Crypto User` assignment scoped to the single
non-exported P-256 release key. Workflow variables identify the Azure tenant,
subscription, client, vault, key name, and pinned key version; there is no Azure
client secret or GitHub-held private signing key. The public key and fingerprint
are committed trust roots. Owner recovery material and the encrypted Key Vault
backup remain outside Git.

The current GitHub billing plan cannot enforce required reviewers or a wait
timer on the environment. Publication therefore remains manual, requires an
exact protected-main SHA with a completed main-push CI workflow whose `Required
CI` job succeeded, separates OIDC signing from release-write permission, and
fails on tag or asset collision. Enable environment reviewers as soon as
repository plan support is available.

## Local Setup

Keep `.env` mode `0600` and shell-compatible because repository helpers source
it. Do not commit it.

```bash
cp .env.template .env
chmod 600 .env
```

Choose one application workflow:

- Containers: populate required `.env` values, including
  `CAMERA_AGENT_ADMIN_PASSWORD` and `CAMERA_AGENT_OAUTH_CLIENT_SECRET`, then run
  `./scripts/infra:start`.
- Direct LogicHost: run `./scripts/with-env dotnet run --project
  src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj`.
- Direct CameraAgent: set project User Secrets and run `./scripts/with-env
  dotnet run --project
  src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj` so Docker
  DNS defaults are replaced with the configured public LogicHost URL.

Do not start a LogicHost container and direct LogicHost on the same host port.

Set a direct-run secret without placing its value in the command line:

```bash
./scripts/user-secret:set \
  src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj \
  'LocalIdentity:AdminPassword'
```

The helper keeps the value out of the process command line and removes its
owner-only temporary input file on exit. Use the corresponding LogicHost
project for LogicHost keys.

## Secret Catalog

| Secret | Consumer and purpose | Supported local source |
| --- | --- | --- |
| `SQLSERVER_PASSWORD` / `ConnectionStrings:skymonitordb` | LogicHost SQL Server `SkyMonitor` runtime principal | Ignored `.env`; direct nested environment override |
| `ConnectionStrings:skymonitordb-migrations` | LogicHost controlled database-initialization principal | Deployment secret store; expose only to the one-shot initialization command |
| `REDIS_PASSWORD` / `Redis:Configuration` | LogicHost prefixed distributed cache | Ignored `.env`; direct nested environment override |
| `HVO_OBJECT_STORE_ROOT` | LogicHost object-store root path; not a credential. Access is filesystem ownership and mode only | Ignored `.env`; declared in the split-host inventory |
| `Smtp:Username`, `Smtp:Password` | Authenticated SMTP where required | User Secrets or environment variables |
| `DatabaseSeed:Users:*:Password` | Optional configured LogicHost seed users | User Secrets or environment variables |
| `DatabaseSeed:ApiKeys:*:RawKey` | Optional configured integration keys | User Secrets or environment variables |
| `DatabaseSeed:ConfidentialClients:*:ClientSecret` | Optional OpenIddict clients | User Secrets or environment variables |
| `DeviceBootstrap:CentralIdentity:ClientCredentials:ClientSecret` | Fleet-scoped client included in bootstrap responses | User Secrets or environment variables |
| `CentralIdentity:ClientCredentials:ClientSecret` | CameraAgent outbound client-credentials mode | Imported encrypted device state, User Secrets, or environment variables |
| `CentralIdentity:ApiKey:Key` | CameraAgent outbound API-key mode | Imported encrypted device state, User Secrets, or environment variables |
| `CAMERA_AGENT_ADMIN_PASSWORD` / `LocalIdentity:AdminPassword` or `AdminPasswordFile` | One-time temporary credential for initial local site-owner seeding and verification | Ignored `.env`, CameraAgent User Secrets, or an owner-only deployment file; remove runtime authority after durable seeding |
| Split-host owner API key and per-agent owner passwords | LogicHost owner automation and temporary local Identity login | Schema-v8 owner-only `secretSource`; automation removes runtime password authority and correlated bootstrap files after durable seeding and authentication, then the operator replaces the temporary password through the required first-login flow |
| `CentralIdentity:LocalFallback:AccessCodeHash` | Reserved option; no runtime fallback handler is implemented | Do not configure as an active control |
| OpenIddict signing/encryption PFX files and passwords | Token, code, and refresh-token cryptography | Development certificate store locally; split-host production mounts owner-supplied files and reads passwords through KeyPerFile |
| Kestrel/TLS private key and password | HTTPS when Kestrel owns TLS | Framework configuration is available, but repository Compose has no HTTPS profile or secure mount |
| LogicHost and CameraAgent Data Protection key rings | Cookies, bootstrap envelopes, and CameraAgent encrypted secrets | Bind-mounted `DataProtection-Keys` directories; never configuration values |
| CameraAgent device key, registration token, and OAuth secret | Device authentication and outbound central access | Data Protection-encrypted `device-secrets.dat` |
| `GIST_TOKEN` | Optional CI coverage badge publication to the public aggregate-metrics Gist | Repository-level GitHub Actions secret; owner recovery copy in `hvo-central-kv` as `HVO-SkyMonitor--GitHub--GistToken` |
| `COVERAGE_GIST_ID` | Public identifier selecting the coverage badge Gist; not a credential | Repository-level GitHub Actions variable; recovery mirror in `hvo-central-kv` as `HVO-SkyMonitor--GitHub--CoverageGistId` |

Application appsettings contain no usable owner password, API key, or OAuth
client secret. Integration tests inject isolated fixture
credentials. Compose requires explicit CameraAgent owner and fleet OAuth client
secrets from the ignored `.env`.

## Runtime Secret Files

The following paths contain secret or security-sensitive state and must stay
ignored, access-controlled, and out of support bundles:

- `data/logichost/dataprotection/`
- `data/logichost/home/` (Development certificate store)
- `data/cameraagent/identity/`
- `data/cameraagent/dataprotection/`
- `data/cameraagent/provisioning/`
- `data/agent/` and `data/archive/`
- `src/HVO.SkyMonitor.CameraAgent/App_Data/`
- any host `DataProtection-Keys/` directory
- `.env` and `.devcontainer/devcontainer.local.env`
- legacy `.devcontainer/state/` content left by the retired agent integration
- the ignored schema-v8 split-host `secretSource` file and transient remote `.hvo-deploy/{up,bootstrap,smoke,measure,down}-<run-id>/` private files (credentials are removed at phase exit)

Every local credential staging file, remote credential/session file, and private
SCP temporary is atomically registered with its phase, kind, path, and complete
non-secret target identity in
`<state-root>/private-upload-registry.json` before creation. Successful deletion
is followed by registry removal. Every private phase entry and exit re-correlates
and reconciles retained entries. Transfer, process interruption, or follow-up SSH
failure therefore leaves an authoritative retry record; failed cleanup remains
registered for the next invocation. Cleanup never substitutes another inventory
alias when target correlation fails. The registry contains paths and target
identity but no credential value, request body, or payload. Owner cookies,
owner/central headers, and the exact generated login and antiforgery response
files are registered before either local creation or remote staging.

Every registered temporary path must be beneath the exact target
`<runtimeRoot>/.hvo-deploy/uploads/` directory and use the generated
`hvo-upload-<phase>-<pid>-<32 lowercase hex>.tmp` basename. Registry load,
registration, reconciliation, and deletion all enforce the same target/root/path
contract. Local and final remote credential entries have separate basename and
runtime/state-root constraints. An arbitrary or tampered path is rejected
without deletion. `bootstrap`, `smoke`, `measure`, and `down` reconcile cleanup
before passed publication; the shared publisher rejects passed state while the
registry is nonempty. A resume performs reconciliation even when phase work or
down actions were already completed.

Capture-control requests never append idempotency material to the reusable owner
antiforgery header. Each request derives a new owner-only header containing the
original antiforgery value and exactly one boundary/attempt-specific idempotency
key. The derived header is registered before creation and deleted before its
cleanup entry is removed. An unverified measure failure pause retains the owner
session and derived-header cleanup entries for the next safety reconciliation.

`device-secrets.dat` is encrypted with CameraAgent Data Protection. Encryption
does not make it safe to publish, and it cannot be recovered without the
matching key ring. `device-identity.json` contains a verification code and is
also sensitive operational state.

## Credential Scope

- SQL Server must point only to `SkyMonitor`. The `.env` template names the
  database-scoped `skymonitor-app` login; the SQL operator creates it and grants
  only the `SkyMonitor` rights required by startup migrations and runtime use.
  Instance-administrator credentials never belong in application configuration.
- Redis uses the `skymonitor-app` ACL user restricted to `skymonitor:*` keys,
  with administrative and dangerous command categories denied. Keep
  `Redis:InstanceName=skymonitor:` aligned with that ACL. Redis is not an
  identity authority or session revocation store.
- LogicHost uses only `skymonitor-diagnostics` and `skymonitor-artifacts`
  inside the qualified object-store root.
- The object store has no credential of any kind. Its authorization boundary is
  ownership `4242:4343` and mode `0750` on the root and both bucket
  directories, enforced by `./scripts/qualify:filesystem-object-store`.
- Split-host secret sources reject every control character, including carriage
  return, before any KeyPerFile, Redis, SQL, client, or certificate
  configuration is rendered.
- Every secret-source reference must occur exactly once. Duplicate required or
  unrequired names and missing required names are rejected before host contact;
  extraction independently enforces the same exact-count and control-character
  rules before rendering.
- OAuth scopes are dot-separated: `api.admin`, `api.artifacts.read`,
  `api.camera`, `api.frames`, `api.images`, `api.owner.write`, `api.viewer`, and
  `api.webhooks`.
- API keys use `Read` or `ReadWrite`; choose `Read` unless mutation is required.

## Rotation Capability

| Credential | Current supported operation |
| --- | --- |
| User password | User self-service change/reset; CameraAgent seeds its temporary owner password once and never reverts a later replacement from configuration. |
| LogicHost API key | Manual make-before-break create, deploy, validate, deactivate, rollback/reactivate, then delete. |
| Device key | Central revocation and full re-registration only; renewal/overlap is not implemented. |
| Confidential OAuth client | Startup replaces a changed secret immediately; use coordinated downtime or a new client ID because same-client overlap is not implemented. |
| Fleet bootstrap OAuth client | Change affects newly issued envelopes; existing agents require reprovisioning. |
| Object store | No credential to rotate. Reassert root and bucket ownership/mode and rerun the qualifier after any operator action that could change them. |
| SQL Server or Redis password | Rotate server side using the service owner's procedure, update the secret source, then restart and validate applications. Repository code does not orchestrate overlap. |
| OpenIddict signing/encryption certificate | Split-host production loading is implemented; coordinated overlap/automatic rotation is not. |
| TLS certificate | Owned by the actual TLS terminator, which repository Compose does not define. |
| Data Protection keys | Automatic key generation in the persisted ring; deletion is not rotation and invalidates protected data. |

Use the step-by-step procedures and rollback rules in the
[identity operations runbook](../identity/operations-runbook.md).

## Leakage Controls

- Never run a full environment dump for troubleshooting. Query only a known
  non-secret key or verify that a secret is present without printing its value.
- Do not put secrets directly in command arguments. Prefer an interactive
  prompt, User Secrets prompt, protected input file, or deployment secret
  provider.
- HTTP logging is limited to method, path, status, duration, and correlation
  headers. Request and response bodies and authorization headers must remain
  disabled.
- CameraAgent token and bootstrap failures log status only; identity-provider
  and bootstrap response bodies are not retained.
- Metrics may include bounded grant type, result, and access level. Do not add
  user, key, client, device, path, IP, or token values as labels.
- Redact personal data from retained evidence. Prefer internal IDs and UTC
  intervals when correlation is required.
- Treat terminal scrollback, shell history, CI output, traces, crash dumps, and
  support archives as possible leakage channels.

## Certificates and Data Protection

Development and Testing use OpenIddict development signing and encryption
certificates. Production loads absolute-path signing and encryption PFX files
through `OpenIddictCertificates`; split-host deployment supplies owner-provided
mounts and KeyPerFile passwords. Coordinated overlap and automatic rotation are
not implemented.

Both hosts persist Data Protection keys to filesystem directories. Compose
mounts those directories so rebuilds preserve cookies and protected state.
Back up LogicHost keys with protected envelopes and CameraAgent keys with
`device-secrets.dat`. Never reset a key ring as a certificate-rotation method.
Any key ring committed to source control is compromised even after deletion
because Git history retains it. Never reuse such keys; invalidate dependent
development sessions and reprovision or discard protected local state.

## Troubleshooting

| Symptom | Safe check |
| --- | --- |
| Configuration is ignored | Confirm whether the value uses a root `.env` name or a nested .NET name, and confirm the intended project/environment without printing the value. |
| CameraAgent owner password replacement is pending | Authenticate with the temporary credential, complete `/Account/ReplaceTemporaryPassword`, remove runtime password authority, and retain only the approved installer-side cleanup record. |
| Token acquisition fails | Verify configured service URL, client ID, grant permissions, scopes, and secret presence; inspect status-only logs. |
| API key fails | Use `/api/v1.0/status/detailed`, verify active/expiry/access state, and inspect key-ID audit events. |
| CameraAgent secrets cannot decrypt | Restore matching provisioning and Data Protection state; do not fall back silently to fixture credentials. |
| Object-store access fails | Verify root and bucket ownership `4242:4343` and mode `0750`, that both buckets exist, and that no second writer holds the root. |
| Cookies fail after recreation | Verify the correct Data Protection bind mount exists and is readable by the container. |

Run `./scripts/docs:audit-operations` after changing identity routes,
configuration, deployment, or this guide.
