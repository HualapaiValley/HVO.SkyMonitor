# HVO.SkyMonitor.CameraAgent

## Local Owner Bootstrap

Initial startup requires a temporary owner password from
`LocalIdentity:AdminPassword` or `LocalIdentity:AdminPasswordFile`. The owner is
seeded with the user name `LocalIdentity:AdminUserName` (default `admin`) and
the email `LocalIdentity:AdminEmail` (default `owner@cameraagent.local`), and
can sign in with either. A newly seeded owner must replace the temporary
password at `/Account/ReplaceTemporaryPassword` before using ordinary
CameraAgent UI or APIs. After seeding, remove the password setting and set
`LocalIdentity:AllowMissingAdminPassword=true`; the durable replacement is not
reconciled from configuration on restart.

The installer seeds the documented default sign-in `admin` / `skymonitor`
unless it is given `--password-file` or `--generate-password`. **Until the first
sign-in replaces that password, anyone who can reach the CameraAgent port can
sign in and claim the camera.** Keep the installer's default loopback binding
until the password is replaced; a non-loopback binding still requires the
explicit `--acknowledge-plaintext-http` acknowledgement.

Passwords need at least 8 characters, with no character-class rules, and cannot
be `skymonitor` in any letter case, the user name, or the current password. Five
failed sign-ins lock the account for five minutes. An owner created before user
names existed keeps its email as its user name and signs in with that email.

Authenticated installer status is available at
`/api/internal/owner-bootstrap/status`. See
`docs/identity/operations-runbook.md` for states, health, logs, and exclusions.

Blazor Server host for a self-contained SkyMonitor camera agent. It provides local identity, operational UI, capture APIs, and configuration-driven camera/pipeline hosting while remaining independent of LogicHost.

## Highlights

- **Modern UI**: Main layout, reconnect modal, scoped CSS/JS, and shared components using the local `hvo-dark` theme.
- **Identity**: Local-only ASP.NET Core Identity with cookie auth and profile/email/password management pages. Local email delivery is not implemented.
- **Frame APIs**: Versioned `/api/v1.0/frames` endpoints that stream the most recent exposure from the simulated capture pipeline.
- **Diagnostics**: Structured JSON logging, custom correlation-id middleware, ProblemDetails enrichment, OpenTelemetry metrics/traces, Scalar UI, Prometheus scraping, and health checks.
- **SQLite Storage**: Identity tables managed through EF Core migrations stored under `Data/Migrations`.

## Run It

```bash
./scripts/user-secret:set \
  src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj \
  'LocalIdentity:AdminPassword'
./scripts/with-env dotnet run \
  --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

- UI: `http://localhost:5130/`
- API: `http://localhost:5130/api/v1.0/frames/latest`
- Health: `http://localhost:5130/health`
- Scalar UI: `http://localhost:5130/scalar/v1`
- Prometheus: `http://localhost:5130/metrics`

## Database & Migrations

SQLite lives under `App_Data/cameraagent_identity.db` by default (override via `LocalIdentity:DatabasePath`). Apply or create migrations with:

```bash
dotnet ef database update --project src/HVO.SkyMonitor.CameraAgent

dotnet ef migrations add <MigrationName> --project src/HVO.SkyMonitor.CameraAgent
```

The app applies pending migrations automatically on startup.

## Configuration

- `LocalIdentity` configures the seeded owner email, user name, and temporary password and the optional `DatabasePath`. Use CameraAgent User Secrets for the password in a direct run and `CAMERA_AGENT_ADMIN_PASSWORD` for Compose.
- Data-protection keys persist under `DataProtection-Keys/` (or the path you mount in Docker) so browser sessions survive restarts.
- Device identity and encrypted central credentials persist under `DeviceProvisioning:StateDirectory`; restore them with the matching Data Protection keys.

## Theme Usage

`Components/App.razor` references the local copy of the HVO Dark theme. Scoped CSS files under `Components/**/*.razor.css` build on that palette to keep parity with SkyMonitor V6.

## Docker

The existing `Dockerfile` still works for container builds. Run `docker build -t hvo-cameraagent -f src/HVO.SkyMonitor.CameraAgent/Dockerfile .` from the repo root when you need an image.
