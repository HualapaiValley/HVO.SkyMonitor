# CameraAgent Secrets and Configuration

## Configuration layers

CameraAgent reads application settings, Development-only .NET User Secrets and
environment overrides. Environment variable names use double underscores for
nested .NET keys. Development Compose reads the ignored root `.env`.
`scripts/with-env` also reads the optional ignored
`.devcontainer/devcontainer.local.env`.

## Local setup

Copy `.env.template` to `.env` and set its permissions to `0600`. Set
`CAMERA_AGENT_ADMIN_PASSWORD` for development Compose. For a direct run use the
protected prompt:

```bash
./scripts/user-secret:set \
  src/HVO.SkyMonitor.CameraAgent/HVO.SkyMonitor.CameraAgent.csproj \
  'LocalIdentity:AdminPassword'
```

CameraAgent accepts an absolute `LocalIdentity:AdminPasswordFile` where a
secret file is preferable. Successful first sign-in includes replacing the
temporary password. [Identity operations](../identity/operations-runbook.md#onboarding).

## Runtime secret files

- Keep the Identity database, Data Protection keys and encrypted device
  provisioning files within the instance's protected state. Preserve them
  together in a supported backup.
- Supply installed-instance credentials through the installer's protected
  prompts or owner-only files. [Installer runbook](../runbooks/deployment-installer.md).
- Silo uses instance-scoped file-based credentials and private storage.
  Follow the credential rotation and archive trust procedure for that layout.
  [Silo runbook](../runbooks/cameraagent-silo-runtime.md).
- Scope CameraAgent API keys to the operations a client needs. Revoke a
  compromised key in the local account UI. [API-key lifecycle](../identity/operations-runbook.md#api-key-lifecycle).

## Evidence and release

Keep secret values, token bodies, key files and complete environment dumps out
of logs and Git history. Record credential identifiers and scope in evidence.

Release automation is a separate control plane and does not add Azure Key Vault as an application runtime provider.
Since the owner retired the key-signed manifests and indexes on 2026-10-16, a release uses no signing key and no
Azure login: it is trusted by `SHA256SUMS` and GitHub build-provenance attestations, which the release workflow makes
with its own OIDC token, and no trust root is committed (#1310). The retired signing job's federated credential for
`repo:HualapaiValley@330983554/HVO.SkyMonitor@1095187036:environment:production-release`, its `Key Vault Crypto User`
assignment, the P-256 release key and the workflow variables that named them are no longer used; retiring them is the
owner's choice. Owner recovery material and any Key Vault backup remain outside Git.

The `production-release` environment now holds a release's publish job until a required reviewer approves it, which
GitHub supports for this public repository. The release workflow refuses to publish while the environment requires no
reviewer, and the owner applies its settings ([releases.md](../runbooks/releases.md#owner-settings-this-relies-on)).
