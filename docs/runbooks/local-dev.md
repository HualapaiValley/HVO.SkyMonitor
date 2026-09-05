# Local Development Runbook

This runbook describes supported day-to-day development workflows for
HVO.SkyMonitor. Native Linux development, including SSH and remote-agent
sessions, is the primary workflow. The optional devcontainer provides the same
toolchain for cloud or isolated workspaces.

## Prerequisites

- Docker Engine with Compose.
- Exact .NET SDK from `global.json`.
- `jq` for protected JSON input to .NET User Secrets.
- Access to development shared-service credentials.
- An ignored root `.env` based on `.env.template`.

Redis, MinIO, and Mailpit topology is defined by
`deploy/hvo-docker/docker-compose.shared-services.yml` and normally runs
persistently on `hvo-docker.hvo.lan`. SQL Server is provisioned separately on
that host.

## Environment Setup

```bash
cp .env.template .env
chmod 600 .env
```

Populate every required password in `.env`. In particular,
`CAMERA_AGENT_ADMIN_PASSWORD` and `CAMERA_AGENT_OAUTH_CLIENT_SECRET` have no
Compose fallback. Use unique values for every reachable development deployment.

Start and stop application containers without changing shared-service data:

```bash
./scripts/infra:start
./scripts/infra:status
./scripts/infra:stop logichost cameraagent
```

The application Compose topology exposes LogicHost at
`http://localhost:5174` and CameraAgent at `http://localhost:5130` by default.
It is Development-only and HTTP-only.

## Direct Hosts

Do not run a direct host while its container owns the same port.

Both direct hosts require a verified production snapshot under
`${HVO_RUNTIME_DATA_ROOT:-./data}/catalog`. Build and install it once using
[`docs/catalog/production-install.md`](../catalog/production-install.md).
`with-env` supplies this root and requires production package kind; it never
falls back to the nine-row test fixture.

LogicHost requires the root `.env` translation performed by `with-env`:

```bash
./scripts/with-env dotnet run \
  --project src/HVO.SkyMonitor.LogicHost/HVO.SkyMonitor.LogicHost.csproj
```

CameraAgent supports project User Secrets. Set its local owner password through
the prompt before the first direct run:

```bash
./scripts/user-secret:set \
  src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj \
  'LocalIdentity:AdminPassword'
./scripts/with-env dotnet run \
  --project src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj
```

Current CameraAgent module files use one explicit contract: a top-level
`cameraagent-capture-pipeline-v2` graph with stable step aliases, a recurring
weekly `schedule`, and explicit `rig.controlPolicy.exposureControl` and
`rig.controlPolicy.gainControl` ownership. Missing sections are configuration
errors; the host does not infer processing dependencies, control ownership, or
an always-open schedule.

On first login, replace that temporary password. Then remove the user-secret
value and configure `LocalIdentity:AllowMissingAdminPassword=true` for later
starts. Existing passwords are never reconciled from configuration.

The launch profiles include HTTPS endpoints for direct development. Repository
Compose does not provide HTTPS termination and must not be described as a
production identity deployment.

## Identity Workflow

After startup:

```bash
./scripts/identity:smoke
```

- LogicHost user registration is `/Account/Register`.
- LogicHost API-key management is `/Account/Manage/ApiKeys`.
- Device registration is `/devices/register` and inventory is `/devices`.
- CameraAgent import is `/devices/bootstrap`.
- CameraAgent temporary-owner replacement is
  `/Account/ReplaceTemporaryPassword`; bounded authenticated status is
  `/api/internal/owner-bootstrap/status`.
- API-key proof uses `/api/v1.0/status/detailed`; anonymous
  `/api/v1.0/status` does not validate a key.

Use Mailpit for LogicHost confirmation/reset email in the development topology.
CameraAgent's local email sender does not deliver recovery links.

## Testing

Use the exact SDK and canonical commands from the
[CI pipeline runbook](ci-pipeline.md). Fast selections are:

```bash
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  dotnet test HVO.SkyMonitor.v9.slnx --filter 'TestCategory=Unit'

dotnet test HVO.SkyMonitor.v9.slnx --filter 'TestCategory=Integration'
```

Integration selection requires Docker. Manual and Soak remain opt-in; External
validation is owned by its separate workflow; no Hardware MSTest cases
currently exist.

Validate infrastructure and active documentation commands with:

```bash
./scripts/test:infra
./scripts/docs:audit-operations
```

The infrastructure test uses a temporary runtime-data root and does not clear
developer state.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Database migration fails | Verify `SQLSERVER_*` endpoint/database/login values in `.env` and confirm the login owns approved migration rights on `SkyMonitor`. |
| MinIO authorization fails | Verify `MINIO_ACCESS_KEY` and `MINIO_SECRET_KEY` and the scoped policy for the two approved buckets. Root credentials are not application credentials. |
| Redis connection times out | Verify `REDIS_*`, the selected endpoint, and the `skymonitor:` instance prefix. |
| SMTP email is missing | Verify `SMTP_*`, then inspect the configured Mailpit instance without retaining confirmation links. |
| CameraAgent startup rejects configuration | Set `CAMERA_AGENT_ADMIN_PASSWORD` for Compose or `LocalIdentity:AdminPassword` in CameraAgent User Secrets. |
| CameraAgent owner cannot access operations after login | Complete `/Account/ReplaceTemporaryPassword`; pending setup intentionally denies ordinary owner UI and APIs without stopping capture. |
| Cookies fail after reset | Reset removes the selected host's Data Protection keys. Restore the approved key-ring backup or sign in/bootstrap again. |
| Device secrets cannot decrypt | Restore CameraAgent provisioning and Data Protection state from the same backup set. |

## References

- [Identity operations](../identity/operations-runbook.md)
- [Secrets and configuration](../security/secrets.md)
- [Infrastructure operations](infra-operations.md)
