# CameraAgent Identity

## CameraAgent Local Identity

CameraAgent uses a local SQLite ASP.NET Identity database and operates offline.

- `LocalIdentity:AdminEmail` and a temporary `LocalIdentity:AdminPassword` or
  `AdminPasswordFile` create the site owner once, with the user name
  `LocalIdentity:AdminUserName` (default `admin`). The installer's default
  temporary password is `skymonitor`. Existing owner passwords and user names
  are never reconciled from configuration.
- Sign-in accepts the user name or the email. Passwords need at least 8
  characters without character-class rules and cannot be `skymonitor`, the user
  name, or the current password. Five failed sign-ins lock the account for five
  minutes.
- A newly seeded owner must replace the temporary password at
  `/Account/ReplaceTemporaryPassword` before ordinary owner UI or APIs are
  authorized. The requirement is durable in the local Identity database.
- After durable seeding, an explicit
  `LocalIdentity:AllowMissingAdminPassword=true` permits startup without a
  configured password. It does not permit first-time seeding without one.
- Local cookies are named `CameraAgent.Auth`, last 12 hours with sliding
  expiration, and revalidate the security stamp every 30 minutes.
- The site owner can create scoped local API keys (`Read` or `ReadWrite`) at
  `/Account/Manage/ApiKeys` for the command line and automation. A key is
  accepted only as `Authorization: Bearer hvo_sky_...` under
  `/api/v1/operations`, maps onto the operations read and mutate policies, is
  stored only as a SHA-256 in the local Identity database, and is revoked by
  deleting it; owner password recovery revokes every key. See the [operator settings contract](../contracts/cameraagent-operator-settings-v1.md).
- Local email delivery, roles, two-factor authentication, and passkeys are not
  implemented.
- Local self-registration is disabled. Operator pages, frame previews, gallery,
  artifact retrieval, capture controls, and outbox operations require the
  configured site owner; API denials return `401` or `403` instead of redirects.
- Password replacement rotates the Identity security stamp, refreshes only the
  completing session, and invalidates other owner cookies on their next request.
- Device identity and encrypted secrets are stored under
  `DeviceProvisioning:StateDirectory`.

Compose persists local Identity, Data Protection, and provisioning directories.
The encrypted secret file and Data Protection keys must be backed up and
restored together.

## Central transport compatibility

New CameraAgent installations and the development configuration use local
identity with central integration disabled. Existing configurations can still
use the outbound transport, enrollment envelope, and location acknowledgement
contracts with an explicitly configured legacy central service. The supported
application topology is standalone CameraAgent.

## Operational references

- [Identity operations](operations-runbook.md)
- [Secret configuration](../security/secrets.md)
- [Installer and owner recovery](../runbooks/deployment-installer.md#bootstrap-and-recovery)
