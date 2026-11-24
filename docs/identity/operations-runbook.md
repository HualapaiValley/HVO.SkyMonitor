# CameraAgent Identity and Security Operations

CameraAgent owns its local user database, cookies, API keys and Data Protection
state. Use the [installer](../runbooks/deployment-installer.md) for deployed
instances and the [local development runbook](../runbooks/local-dev.md) for
HTTP development Compose and direct execution.

## Health and authorization

Check `/alive` for liveness and `/health` for readiness. The operator workspace
requires the local site owner. `/metrics` accepts a ready site-owner session or
an operations-read API key in the Authorization header.

Keep passwords, API keys, device credentials and Data Protection key material
out of tickets, evidence, shell history and logs. Use the authentication UI
and supported installer or API operations. Record the affected identity and
validation outcome when you rotate or revoke a credential.

## Onboarding

### CameraAgent site owner

For development Compose, set `CAMERA_AGENT_ADMIN_PASSWORD` in the ignored
`.env` before startup. For a direct host, store the owner password in the CameraAgent
user-secrets store:

```bash
./scripts/user-secret:set \
  src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj \
  'LocalIdentity:AdminPassword'
```

The host creates the configured `LocalIdentity:AdminEmail` owner once with the
temporary password and the user name `LocalIdentity:AdminUserName` (default
`admin`). The email stays the durable owner key for authorization, bootstrap
state, and recovery. It never reconciles an existing owner's password or user
name from configuration, so an owner seeded before user names existed keeps its
email as its user name. The first authenticated owner session is redirected to
`/Account/ReplaceTemporaryPassword` and must supply the current temporary
password, a policy-valid new password, and matching confirmation. Until that
transaction commits, ordinary CameraAgent pages and APIs are denied.

CameraAgent reads `AdminPasswordFile` as strict UTF-8, ignores a leading
byte-order mark, and removes only trailing carriage returns and line feeds;
spaces are part of the password. The installer validates, authenticates with,
and reads recovery passwords from files through the same reader.

The installer's documented default temporary password is `skymonitor`, so a new
installation's first sign-in is `admin` / `skymonitor`. A configured
`AdminPassword` of exactly `skymonitor` is the only password the host seeds
without applying the replacement policy; it is still always marked for
replacement. Until the first sign-in replaces it, anyone who can reach the
CameraAgent port can sign in with the default and claim the camera, so keep the
loopback binding until the replacement is done (see the
[installer runbook](../runbooks/deployment-installer.md#bootstrap-and-recovery)).

The sign-in form accepts a user name or an email: a value containing `@` is
looked up as an email, any other value as a user name. Its field keeps the
form name `Input.Email` for existing automation. Every password set after
seeding needs at least 8 characters with no character-class rules and cannot be
`skymonitor` in any letter case, the account's user name, or its current
password. Five failed sign-ins lock the account for five minutes; the sign-in
page reports the lockout and logs event `4188`.

After the owner exists durably, remove `AdminPassword`/`AdminPasswordFile` from
runtime configuration and set
`LocalIdentity:AllowMissingAdminPassword=true`. Startup still fails if both the
durable owner and configured password are absent. Reintroducing a password file
does not reset a ready owner and is not a recovery workflow.

CameraAgent email delivery is not implemented; its email sender only records a
development message. Do not rely on local password-reset email as a recovery
path.

### Owner bootstrap status contract

`GET /api/internal/owner-bootstrap/status` requires the configured local owner,
including while that owner still requires password replacement, and returns only
`state` and `passwordChangeRequired`. Other authenticated local users are denied.
It is deliberately small and stable for installer issue #415. States are:

| State | Meaning |
| --- | --- |
| `owner-uninitialized` | No durable site owner exists. Initial startup normally fails before serving this state. |
| `owner-temporary-password` | The durable owner requires replacement and a temporary password remains configured. |
| `owner-password-change-required` | The durable owner requires replacement and runtime password authority has been removed. |
| `owner-ready` | Password replacement is complete. |
| `owner-recovery-required` | Durable owner identity is ambiguous or does not match configured ownership. |

The `owner-bootstrap` health check remains Healthy while interactive password
replacement is pending, so capture readiness is independent of owner setup. It
is Degraded only for `owner-recovery-required`; anonymous `/health` uses only a
generic operational or operator-attention description and never returns the
exact bootstrap state. Pending owner API requests return `403`, header
`X-HVO-Authorization-Reason: owner-password-change-required`, and the same code
in the JSON body. Login, logout, replacement, bootstrap status, and required
static assets remain available.

Structured events contain no email, password, token, path, or credential value:

| Event ID | Name | Meaning |
| --- | --- | --- |
| `4180` | `OwnerTemporaryPasswordSeeded` | A new durable owner was seeded and requires replacement. |
| `4181` | `OwnerTemporaryPasswordMismatch` | Configured temporary input conflicts with still-pending durable state. |
| `4182` | `OwnerPasswordBootstrapCompleted` | Replacement committed and the completing session was refreshed. |
| `4183` | `OwnerOperationDeniedDuringBootstrap` | A pending owner attempted an ordinary API operation. |
| `4184` | `OwnerPasswordRecoveryRequested` | A lifecycle-authorized Unix-socket recovery challenge was accepted. |
| `4185` | `OwnerPasswordRecoveryCompleted` | The configured owner credential and security stamp changed atomically. |
| `4186` | `OwnerPasswordRecoveryRejected` | Recovery failed closed with a bounded reason and operation ID. |
| `4187` | `OwnerPasswordRecoveryEndpointFailed` | A recovery endpoint failed unexpectedly and returned only a fixed diagnostic. |
| `4188` | `LocalSignInLockedOut` | A sign-in was refused because repeated failures locked the account. |

No anonymous account creation or browser email-reset endpoint is added. The
login recovery link contains static local-host instructions and never accepts an
email address. Generated confirmation/reset/change-email routes do not issue or
consume tokens, and the configured owner email is read-only in the browser.

Offline owner recovery uses
`hvo-sky-install cameraagent recover-owner --instance-id <uuid>
(--generate-password|--password-file <absolute-owner-only-file>)`. The CLI must
run locally as the installed runtime user. The recorded installation must be
running on Linux with explicit missing-password mode, removed password
authority, and the lifecycle-control credential configured. Generated Compose
uses the existing Identity state mount to expose `/app/App_Data/owner.sock` as
`<state-root>/identity/owner.sock`, including after an image-only upgrade from an
installation created before recovery support. No recovery enable setting or
host-port restriction is required. The CLI requires the parent directory to be
mode `0700` and the single-link mode `0600` Unix socket to be owned by the exact
installation runtime UID/GID. Recovery routes return `404` on every TCP
listener. On restart after a process kill or power loss, CameraAgent locks and
authenticates the owner-only parent, refuses a concurrent startup, active
listener, or unexpected node, and rechecks an inactive runtime-owned socket
immediately before unlinking it. The mode `0700` parent remains the access
boundary if termination precedes the new listener's mode `0600` restriction. The
CLI disables redirects and system proxies, sends a random nonce
without either recovery secret, and requires an operation-bound HMAC-SHA256
lifecycle proof from the socket before disclosure. It then authenticates
bounded binary challenge/complete messages with the owner-only
lifecycle-control credential over that socket, never a public installation
URL. The five-minute
challenge binds operation ID, owner ID, and current security stamp. Completion
requires exactly one site owner matching `LocalIdentity:AdminEmail` and removed
bootstrap password authority and a temporary password of 8 to 256 printable
ASCII characters that satisfies the password policy; it resets only that owner,
restores `PasswordChangeRequired`, clears any sign-in lockout, rotates the
security stamp, and records the last operation in existing `AspNetUserTokens`
state for idempotent resume. Old
cookies and connected circuits lose owner authorization on their next request
or protected action. Capture and storage are not paused or restarted.

Recovery responses and structured events contain no owner email, password,
challenge, lifecycle token, secret hash, or filesystem path. The lifecycle
credential is site-owner-equivalent recovery authority. Device verification
codes and `device-secrets.dat` are not substitutes. Installer and split-host
configuration transitions remove temporary runtime authority and correlated
bootstrap files; never emulate either transition or recovery with direct SQLite
edits.

Resume only an interrupted or ambiguous attempt so the same staged credential
and operation ID are reused. A definitive CameraAgent rejection marks the
attempt closed and requires a fresh command without `--resume`. Human output may
report the private password-file path needed by the local operator; structured
`--json` output omits it.

Every temporary remote credential registration binds the full inventory target
identity. Cleanup re-correlates that identity before and after deletion and
fails the phase if an alias or target changes. A retained credential is safer
than deleting the same path on an unverified host; investigate and remove it on
the originally correlated target.

## API-key lifecycle

### CameraAgent API keys

Each key belongs to the CameraAgent instance that issued it.

1. Sign in to CameraAgent as the site owner and open `/Account/Manage/ApiKeys`.
2. Choose `Read` unless the client changes settings or runs operations, then
   create the key. Copy it from that page straight into the client's credential
   store; it is shown once and cannot be shown again.
3. Send it only as `Authorization: Bearer <key>` to `/api/v1/operations/...` or `GET /metrics`.
   A key in a URL is refused. Any other path ignores the key and uses the browser session.
4. Rotate make-before-break: create a new key, move each client to it, then
   revoke the old one on the same page. Revocation takes effect at the next
   request.
5. Review use by key identifier in the `API key ... authenticated` and
   `Rejected an API key` events; the key itself is never logged.

Owner password recovery through `owner.sock` revokes every CameraAgent key, so
recreate keys after a recovery. Keys also stop working while the owner must
replace a temporary password, and when the configured owner changes.

## State recovery

Keep the local Identity database, provisioning files and Data Protection keys
with the instance's state. Use the [installer's backup and restore procedure](../runbooks/deployment-installer.md)
for the supported standalone layout. Follow the [Silo lifecycle restrictions](../runbooks/cameraagent-silo-runtime.md)
for Silo instances. Keep the development state directories private.
[Development infrastructure](../runbooks/infra-operations.md).
