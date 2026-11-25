# Local Development Runbook

This runbook describes supported day-to-day development workflows for
HVO.SkyMonitor. Native Linux development, including SSH and remote-agent
sessions, is the primary workflow. The optional devcontainer provides the same
toolchain for cloud or isolated workspaces.

## Prerequisites

- Docker Engine with Compose.
- Exact .NET SDK from `global.json`.
- `jq` for protected JSON input to .NET User Secrets.
- An ignored root `.env` based on `.env.template`.

## Environment Setup

```bash
cp .env.template .env
chmod 600 .env
```

Set `CAMERA_AGENT_ADMIN_PASSWORD` in `.env` and install the
[approved catalog](../catalog/production-install.md). The development
container listens on `http://localhost:5130` by default.

```bash
./scripts/infra:start
./scripts/infra:status
./scripts/infra:stop
```

## Direct execution

Stop the development container before starting another process on its port.
`with-env` selects the verified production catalog at
`${HVO_RUNTIME_DATA_ROOT:-./data}/catalog`.

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

Sign in as `admin`, or with `LocalIdentity:AdminEmail`, using that password;
setting it to `skymonitor` reproduces the installer's documented default. On
first login, replace that temporary password with one of at least 8 characters
that is not `skymonitor`, the user name, or the current password. Five failed
sign-ins lock the account for five minutes. Then remove the user-secret value
and configure `LocalIdentity:AllowMissingAdminPassword=true` for later starts.
Existing passwords are never reconciled from configuration, and an existing
development owner keeps its email as its user name.

The launch profiles include HTTPS endpoints for direct development. Repository
Compose does not provide HTTPS termination and must not be described as a
production identity deployment.

## Identity

Sign in with the local owner account to view the CameraAgent operator UI,
including Operations and its authenticated endpoints.
See the [identity runbook](../identity/operations-runbook.md) for owner
bootstrap, recovery and API keys.

## Testing

Use the exact SDK and canonical commands from the
[CI pipeline runbook](ci-pipeline.md). Use the [active test selection](ci-pipeline.md#active-test-selection).

```bash
DOCKER_HOST=unix:///tmp/hvo-no-docker.sock \
  dotnet test HVO.SkyMonitor.v9.slnx \
    --filter 'TestCategory=Unit&TestCategory!=Performance'


dotnet test HVO.SkyMonitor.v9.slnx \
  --filter 'TestCategory=Integration'

```

Docker-backed CameraAgent acceptance requires an isolated Docker environment. Manual, Soak, Performance, External and Hardware selections follow the CI runbook.

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
| CameraAgent startup rejects configuration | Set `CAMERA_AGENT_ADMIN_PASSWORD` for Compose or `LocalIdentity:AdminPassword` in CameraAgent User Secrets; it needs at least 8 characters. |
| CameraAgent sign-in says the account is locked | Wait five minutes after five failed attempts, or recover the owner. |
| CameraAgent owner cannot access operations after login | Complete `/Account/ReplaceTemporaryPassword`; pending setup intentionally denies ordinary owner UI and APIs without stopping capture. |
| Cookies fail after reset | Reset removes the selected host's Data Protection keys. Restore the approved key-ring backup or sign in/bootstrap again. |
| Device secrets cannot decrypt | Restore CameraAgent provisioning and Data Protection state from the same backup set. |

## References

- [Identity operations](../identity/operations-runbook.md)
- [Secrets and configuration](../security/secrets.md)
- [Infrastructure operations](infra-operations.md)
