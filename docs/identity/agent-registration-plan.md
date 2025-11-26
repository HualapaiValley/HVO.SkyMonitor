# CameraAgent Registration & Secret Escrow Plan

## 1. Objectives & Constraints
- **Dual-mode operation:** CameraAgent must run standalone (local admin + custom integrations) yet optionally pair with a LogicHost for sync/remote control.
- **No embedded cloud secrets:** Agents run in untrusted networks; they cannot contain Azure credentials, Key Vault references, or shared keys extracted from container images.
- **Operator-driven trust establishment:** Only a human with physical/SSH access to the agent and an authorized LogicHost identity can complete registration.
- **Least-privilege secrets:** After registration, the agent only receives scoped credentials (per-observatory, per-device) and can be revoked independently.
- **Auditability & rotation:** LogicHost must track which devices are registered, rotate shared material, and invalidate compromised agents without affecting others.

## 2. Actors & Components
- **CameraAgent:** Generates a bootstrap identity, exposes a local admin UI, and manages cached credentials + periodic sync.
- **LogicHost:** Hosts the registration portal/API, stores device records, encrypts envelopes, and issues short-lived access tokens/keys.
- **Operator:** Authenticated via Entra External ID, owns an observatory, and bridges the agent ↔ LogicHost trust by moving codes/envelopes between UIs.

## 3. Workflow Overview
1. **Bootstrap identity (Agent):**
   - On first launch, agent creates `DeviceId` (GUID + hardware fingerprint hash) and `VerificationCode` (short alphanumeric). Stored locally.
   - UI shows `DeviceId`, `VerificationCode`, and instructions for pairing.

2. **Portal registration (LogicHost):**
   - Operator logs into LogicHost, navigates to "Observatory → Devices → Add camera".
   - Portal requests `DeviceId` + `VerificationCode` + desired friendly name.
   - Backend validates the code via `POST /api/internal/devices/verify` (see API section).

3. **Envelope creation:**
   - LogicHost generates `DeviceKey` (random 256-bit), `DevicePublicId`, and optional `RegistrationToken` (JWT) scoped to the observatory.
   - Values are wrapped inside an **envelope** encrypted with a LogicHost-managed master key (per cluster) and signed.
   - Envelope expires in e.g. 15 minutes.
   - Portal displays envelope as Base64 string + QR code.

4. **Agent import & confirmation:**
   - Operator pastes/scans envelope into the agent UI.
   - Agent calls LogicHost `POST /api/device/bootstrap` with the envelope, its `DeviceId`, and a nonce.
   - LogicHost validates the envelope signature, checks observatory membership, and ensures the device record is still pending.
   - LogicHost responds with escrowed secrets encrypted to the agent using `DeviceKey-derived` AES key. Payload includes:
     - `ClientCredentials` (client_id, client_secret) scoped to LogicHost
     - `ApiKeySalt` / signed ticket secret (if applicable)
     - JWT signing public keys for offline validation
     - Initial sync settings (upload endpoints, intervals)
       - `CentralIdentityOptions` snapshot (authority, token endpoint override, client credentials, interactive client metadata) so the agent can talk to Azure Entra immediately after bootstrap

5. **Local storage & activation:**
   - Agent decrypts payload, stores secrets in its secure store (DPAPI on Windows, gnome-keyring/libsecret, or encrypted JSON with master key derived from OS keyring + DeviceKey).
   - Agent marks itself "Registered" and begins using LogicHost tokens for uploads.

6. **Ongoing rotation:**
   - Agent maintains a refresh channel (e.g., `POST /api/device/heartbeat`) that can deliver new secrets encrypted with the still-valid DeviceKey.
   - LogicHost can mark a device as `revoked`. Agent stops syncing until re-registered.

## 4. API Surface (first draft)
| Endpoint | Auth | Purpose | Notes |
| --- | --- | --- | --- |
| `POST /api/internal/devices/verify` | LogicHost admin cookie/token | Validates DeviceId + VerificationCode before issuing envelope | Returns temporary `registrationId` |
| `POST /api/internal/devices/envelope` | LogicHost admin cookie/token | Generates encrypted envelope for display | Accepts `registrationId`, validates pending record, and returns opaque Base64 envelope + metadata |
| `POST /api/device/bootstrap` | None (envelope proves trust) | Agent submits envelope + DeviceId to fetch secrets | Returns AES-256-GCM payload (ciphertext + nonce + tag) encrypted with DeviceKey |
| `POST /api/device/heartbeat` | Device JWT (issued at bootstrap) | Rotates tokens/secrets, uploads health | Response can include revoked flag or new secrets |
| `POST /api/device/upload` | Device JWT | Existing telemetry/image uploads | Already planned; uses same auth |

### Envelope format (v1 draft)
- **Envelope body**: JSON payload serialized with camelCase property names before encryption.
- **Required fields**:
   - `registrationId` (Guid)
   - `deviceId` (string)
   - `devicePublicId` (Guid) – stable identifier LogicHost assigns after verification.
   - `observatoryId` (Guid)
   - `friendlyName` (string)
   - `deviceKey` (Base64 256-bit random)
   - `registrationToken` (Base64 256-bit random, single-use)
   - `issuedAtUtc` / `expiresAtUtc` (ISO-8601 strings)
- **Protection**: JSON payload is encrypted and signed via ASP.NET Data Protection (`IDataProtector.CreateProtector("LogicHost","DeviceRegistration","Envelope","v1")`). Output string is safe to display and can optionally be QR-encoded.
- **Lifetime**: default 10 minutes; configurable per request up to 30 minutes. After expiry or first bootstrap use, envelope is invalidated and the registration must restart from verification.

### Bootstrap response payload (v1 draft)
- **Transport contract** (`POST /api/device/bootstrap` response):
   ```json
   {
      "registrationId": "...",
      "devicePublicId": "...",
      "envelopeVersion": "v1",
      "payload": {
         "ciphertext": "<Base64>",
         "nonce": "<Base64 12 bytes>",
         "tag": "<Base64 16 bytes>",
         "algorithm": "AES-256-GCM"
      }
   }
   ```
- **Ciphertext layout**: plaintext is serialized JSON (camelCase) that includes `devicePublicId`, `observatoryId`, `friendlyName`, `registrationToken`, `heartbeatEndpoint`, `uploadEndpoint`, `heartbeatIntervalSeconds`, `issuedAtUtc`, `expiresAtUtc`.
- **Central identity bundle**: the decrypted JSON also carries `centralIdentity` which mirrors the `CentralIdentityOptions` type (authority, token endpoint, client credentials, interactive client settings, fallback hashes). Agents persist this bundle to `device-secrets.dat` and the runtime overrides `CentralIdentityOptions` from it on startup, eliminating manual Entra configuration on the device.
- **Key material**: agents derive the AES key by Base64-decoding the `deviceKey` contained in the envelope. Nonce/tag are transmitted separately to simplify decryption. Clients must treat ciphertext as single-use; LogicHost rotates the payload on subsequent heartbeats.

## 5. Data Model Additions
- **DeviceRegistration** table (LogicHost):
   - Base columns: `Id`, `DeviceId`, `ObservatoryId`, `FriendlyName`, `Status (Pending/Active/Revoked)`, `VerificationCodeHash`, `IssuedAt`, `ExpiresAt`, `LastSeenUtc`, `RevokedReason`.
   - Envelope-specific columns: `DevicePublicId`, `DeviceKeyHash`, `RegistrationTokenHash` (all nullable until issuance), `ActivatedAtUtc` (when agent consumes envelope), and `EnvelopeVersion` for forward compatibility.
   - Observatory snapshot columns: `ObservatoryName`, `ObservatoryLatitudeDegrees`, `ObservatoryLongitudeDegrees`, `ObservatoryElevationMeters`, `ObservatoryTimeZoneId` to preserve operator-friendly metadata even if the source record changes later.
    - Owner confirmation columns: `OwnerUserId`, `OwnerDisplayName`, `OwnerEmail`, `OwnerConfirmationMethod`, `OwnerConfirmationNotes`, `OwnerConfirmedAtUtc` so every registration is tied to a human who asserted ownership and when/how that confirmation occurred.
- **DeviceKey envelope master key**: stored in Key Vault or HSM, rotated on schedule; used only to encrypt envelopes. After bootstrap, per-device keys suffice.
- **Audit logs** for registration attempts, successful envelopes, heartbeats, revocations.

## 6. Security Considerations
- **Physical access assumption:** Anyone with shell/root access to the agent can view cached secrets; we rely on logging + revocation to mitigate compromise, but still harden storage with OS keyrings to make theft non-trivial.
- **Replay protection:** Envelopes contain nonce + expiry and are single-use. LogicHost invalidates the pending registration once used.
- **Mutual confirmation:** Agent must echo `DeviceId` and signed challenge so LogicHost ensures the envelope wasn’t replayed on another device.
- **Rate limits & lockouts:** Verification endpoint throttles invalid code attempts; heartbeat endpoints enforce token expiry (e.g., Device JWT valid 24h; refresh requires stored DeviceKey).
- **Secret scoping:** Credentials obtained through this flow are limited to the assigned observatory and device; they cannot manage other resources. We employ the best available platform storage (Linux keyring via .NET `ProtectedData`/`DataProtectionProvider`) plus per-device keys so exfiltrated blobs remain unusable elsewhere.

## 7. Open Questions
1. **Secure storage implementation:** CameraAgent containers always run on Linux, so we can standardize on the platform keyring (libsecret/SecretService) via .NET's protected storage APIs for DeviceKey + secrets; confirm feasibility across distros and document the dependency.
2. **Offline grace period:** How long can the agent operate offline before LogicHost requires a re-sync/refresh? (Proposal: 30 days.)
3. **Multiple LogicHosts:** Not supported; each CameraAgent pairs with exactly one LogicHost pipeline per environment to avoid credential proliferation. Operators who need staging + prod must deploy distinct agent instances.
4. **Device replacement:** Provide an "adopt existing device" flow allowing operators to manually supply the old DeviceId when swapping hardware so historical data and permissions carry forward.
5. **Incident response:** Define automated path to flag suspicious agents (e.g., abnormal upload rates) and auto-revoke, and make the notification channel pluggable (email today, but architect for SMS/push/webhooks later).

## 8. Next Steps
1. Finalize API contracts + DTOs and add them to `docs/architecture/contracts.md`.
2. Prototype DeviceRegistration EF model + migration in LogicHost.
3. Implement agent bootstrap UI (Blazor component) and secure storage abstraction.
4. Build portal UX for registration/envelope + admin list of devices with revoke action.
5. Add integration tests covering bootstrap, rotation, revocation, and fallback-to-standalone behavior.
