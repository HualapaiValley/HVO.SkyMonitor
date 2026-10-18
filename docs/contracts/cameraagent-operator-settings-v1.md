# CameraAgent Operator Settings and API Keys v1

CameraAgent describes each operator setting once, in a catalogue its operations API serves, so the web UI, the
`hvo-sky` command line and its terminal UI build the same forms and apply the same rules. Non-browser clients reach
the operations API with owner-created API keys. This implements section 8 of the
[HVO installation contract](../hvo-install-contract.md) for CameraAgent. The command line is `hvo-sky`
([hvo-sky.md](../runbooks/hvo-sky.md), #1317), which also uses the operations routes `GET info` and `POST restart`
that runbook describes.

## Catalogue

`GET /api/v1/operations/settings/catalogue` returns `schemaVersion` (1) and the groups in display order. Each group
names the store that keeps it, and a group is the unit of one write.

| Group | Settings | Store | Applies |
| --- | --- | --- | --- |
| `site` | `site.observatoryName`, `site.cameraName`, `site.ownerName`, `site.ownerContact` | operator settings file (`CameraAgent:Site`) | at once |
| `location` | `location.latitudeDegrees`, `location.longitudeDegrees`, `location.elevationMeters`, `location.timeZoneId` | protected deployment-location history (a new manual version) | at the next CameraAgent start; LogicHost reviews it first when central integration is on |
| `skyMap` | `skyMap.maximumObjects` | operator settings file (`CameraAgent:SkyMap:MaximumObjects`) | at once |
| `camera` | `camera.skyLighting` (VirtualSky only) | a new camera revision and a staged rig revision | at the next CameraAgent start |
| `schedule` | `schedule.activeRevision` | capture schedule revisions | at the next capture boundary |

Every setting carries `key`, `group`, `title`, `description`, `type` (`Integer`, `Number`, `String`, `Enum`),
`nullable`, `minimum`/`maximum` (numbers), `maximumLength` (text), `allowedValues` (enum value, label and
description), `unit`, `default`, `readRole` and `writeRole`, `safety`, `application`, `appliesAfterRestart`, `secret`
and, for a settings-file setting, its host `configurationKey`.

- **Roles.** `OperationsRead` is the owner's session or any of the owner's API keys; `OperationsMutate` is the
  owner's session or a `ReadWrite` key. They are the operations read and mutate policies.
- **Safety.** `Descriptive` (labels only; nothing operational reads it), `CaptureOutput` (changes what future
  captures record) or `CaptureGeometry` (changes the location and time basis future captures are stamped with).
  Captures already taken never change.
- **Application.** `Immediate`, `NextCaptureBoundary` or `Restart`; `appliesAfterRestart` is true only for `Restart`.
- **Secret.** No setting is secret today. A secret setting would be write-only and never returned.
- An `Enum` whose catalogue `allowedValues` is null lists its current values with each read of the values
  (`schedule.activeRevision` lists the active, draft and recent revisions).

Deployment settings — paths, listeners, central integration and credentials — are not in the catalogue; the installer
owns them.

## Values

`GET /api/v1/operations/settings` returns every group; `GET /api/v1/operations/settings/{group}` returns one. A group
carries its opaque `version`, `canWrite` and `readOnlyReason`. Each setting carries `value` (the value in effect),
`isSet`, `source`, `sourceDetail`, `canWrite`, `readOnlyReason`, `pendingValue` (saved but not in effect yet), `problem`
(a stored value the host cannot use) and, for a state-dependent enum, `allowedValues`.

`source` is one of `SettingsFile`, `Environment`, `SecretsDirectory` (installer key-per-file), `CommandLine`,
`AppSettings` (the image's `appsettings*.json`), `Host`, `Installation` (the installer-configured camera name, shown
until the settings file has a site profile), `Default`, `DeploymentLocation`, `RigRevision`, `ScheduleRevision` or
`Unavailable`. A settings-file setting that an environment variable, secrets file, command line or other source above
the settings file supplies is read-only here, with the reason naming that source: saving it in the file would have no
effect.

## Writes

`PUT /api/v1/operations/settings/{group}` with `{ "expectedVersion": "<the group's version>", "values": { "<key>":
<value> }, "reason": "<optional, at most 512 characters>" }`. Settings left out keep their value; `null` clears a
nullable setting, so its default or fallback applies. A location change may send any of its four values; the rest
keep the coordinates that govern the next start. The reason and an optional `Idempotency-Key` header (at most 128
characters) are recorded with a location or schedule change; a settings-file change records the actor and keys.

Every value is validated against the catalogue before any store sees it, and the group's store then checks the
caller's version atomically, so a change made in between — including a hand edit of the settings file — is never
overwritten. For location and schedule commands, retrying the original request with its original `expectedVersion`
and `Idempotency-Key` resolves the durable replay before checking for a fresh version conflict. Omitted location
fields keep the coordinates resolved for the original command, even after later changes. Reusing the key with
different durable content is refused by the store. A successful replay returns the successful status with the group
as it is now and never reapplies the old state. The location store's replay window is its 500 retained commands.

| Result | Status | `code` |
| --- | --- | --- |
| Saved and in effect, unchanged, or saved for the next start | 200, `status` `Applied`, `Unchanged` or `PendingRestart`, the group, and `recorded` steps | |
| A value or the request is invalid | 400 validation problem, `errors` keyed by setting | `settings.invalid` |
| The group changed since `expectedVersion` was read, or its state refused the change | 409, the group as it is now | `settings.conflict` |
| A setting cannot be changed here (overridden, not applicable, a change awaiting restart) | 409, `settings` reasons | `settings.readOnly` |
| No such group | 404 | `settings.notFound` |
| The store could not be read or written | 503 | `settings.unavailable` |

Refusals never echo a submitted value. A browser session must send an antiforgery token with a write; an API key
needs none, because it travels only in the `Authorization` header, which a browser never adds to a cross-site
request.

## API keys

The signed-in site owner creates, lists and revokes keys at `/Account/Manage/ApiKeys`. A key has one access level:

- `Read` satisfies the operations read policy.
- `ReadWrite` satisfies the operations read and mutate policies.

A key never satisfies owner bootstrap, key management, the account pages, the web UI, the Blazor circuit or
`/api/internal/*`. There is no key-management API; a key cannot create, list or revoke keys.

- **Format.** `hvo_sky_` + a 16-character public key identifier + `_` + 256 random bits in base64url (68 characters).
  The identifier is safe to show and log; the prefix lets secret scanners recognise a leaked key.
- **Transport.** Only `Authorization: Bearer <key>`, and only under `/api/v1/operations`. A key in a query string,
  another header or another scheme is never accepted. Elsewhere the header is ignored and the session decides.
  Under the operations path, the literal, case-sensitive `hvo_sky_` prefix at the start of an `Authorization`
  value or after any character outside the token alphabet counts as presenting a key, under any scheme. Token
  characters are ASCII letters, digits, `-`, `.`, `_`, `~`, `+` and `/`; a prefix embedded in an unrelated token,
  such as `abc.hvo_sky_x.def`, does not count. Quoting, `=`, `:` and `;` do count as boundaries.
  A presented key decides the request alone: a session cookie on the same request is ignored. Exactly one
  `Authorization` value containing a valid `Bearer <key>` credential is required; embedded whitespace, trailing
  junk, quoted keys, malformed separators, comma-coalesced values, duplicate values and a key under another scheme
  or raw in the header receive the same generic 401 as a bad key. Basic credentials and unrelated bearer tokens
  without a key-prefixed token are left to the session as before.
- **Storage.** The local identity database keeps each key as an authentication token of the owner account: the
  identifier, the SHA-256 of the whole key, the access level, the label and the creation time. The key itself is shown
  once, in the response that creates it (`Cache-Control: no-store`), and is never stored or logged. The identity
  schema already has this table, so keys add no migration.
- **Validation.** A presented key must have the exact format before it is looked up; the stored hash is compared in
  constant time. An unknown identifier, a wrong secret and a revoked key are refused alike with `401` and
  `WWW-Authenticate: Bearer error="invalid_token"`. The key's principal is its owner account with the account's current
  security stamp, so the same site-owner checks apply as for the session: a key stops working while the owner must
  replace a temporary password, and when the account stops being the configured owner.
- **Audit.** Creation and revocation are logged with the key identifier and owner (events 4190 and 4191). A rejected
  key is logged at Warning with the reason, the key identifier when the key is well formed, the remote address and the
  path, never the key (4193). A key-authenticated change is logged at Information with the identifier, access, method
  and path (4194); a read at Debug (4195). `camera_agent.api_key.authentications` counts keys by outcome.
- **Limits.** At most 20 keys per owner; labels are 1 to 64 characters without control characters.
- **Revocation and rotation.** Revoking deletes the record; the key fails at its next request. Rotation is
  make-before-break: create a new key, move each client to it, then revoke the old one. Owner password recovery
  through `owner.sock` revokes every key in the same transaction (event 4192), because recovery answers a lost or
  exposed password.
