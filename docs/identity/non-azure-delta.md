# Phase 01 – Non-Azure Identity/Auth Delta

> Working tree compared to commit `d6dca05` ("Added new auth plan for multi tenant using azure entera..."). This file lists the changes that are not tied to Azure Entra / External ID so we can safely re-apply them after resetting the branch to the pre-phase01 baseline.

## How to Use This Document
- Each section scopes a feature area, calls out why it matters, and references the files that were introduced or modified.
- When we roll back to the pre-phase01 commit, cherry-pick or manually port only the files listed here.
- Azure-specific artifacts (B2C/External ID docs, Key Vault wiring, Entra setup scripts) are intentionally excluded so we do not pull them back in.

## Summary Table
| Area | Purpose | Key Files |
| --- | --- | --- |
| Device registration + envelopes (LogicHost) | Adds EF model, APIs, UI, and services for issuing bootstrap envelopes to camera agents. | `src/HVO.SkyMonitor.LogicHost/Data/DeviceRegistration*.cs`, `Services/DeviceRegistration*.cs`, `Controllers/DeviceRegistrationsController.cs`, `Controllers/DeviceBootstrapController.cs`, `Components/Pages/Devices/*`, `Data/Migrations/20251121062327_AddDeviceRegistrations*.cs`, `Program.cs` registrations |
| CameraAgent provisioning + bootstrap UX | Adds local identity/secret stores, encrypted bootstrap workflow, and `/devices/bootstrap` UI. | `src/HVO.SkyMonitor.CameraAgent/Configuration/DeviceProvisioningOptions.cs`, `Services/DeviceIdentityStore.cs`, `DeviceSecretStore.cs`, `DeviceBootstrapWorkflow.cs`, `DeviceBootstrapCrypto.cs`, `Services/Models/DeviceBootstrapModels.cs`, `Components/Pages/Devices/DeviceBootstrap.*`, `Program.cs` DI registrations, nav links |
| Local system-owner login fallback | Adds hashed "system owner" credential path with new login UI + helper methods/tests. | `Authentication/SystemOwnerLoginService.cs`, `Components/Pages/Login.razor*`, `Security/ReturnUrlHelper.cs`, `tests/HVO.SkyMonitor.CameraAgent.Tests/Authentication/SystemOwnerLoginServiceTests.cs`, `ReturnUrlHelperTests.cs`, `Properties/AssemblyInfo.cs` |
| Documentation (non-Azure) | Captures device registration plan + phase tracker needed post-rollback. | `docs/architecture/phase-plan.md`, `docs/identity/agent-registration-plan.md` |

## 1. Device Registration + Bootstrap (LogicHost)
- **Entity + configuration**: `DeviceRegistration`, its EF Core configuration, and the accompanying migration (`Data/DeviceRegistration*.cs`, `Data/Migrations/20251121062327_AddDeviceRegistrations*.cs`). These files define the schema (status tracking, hashed secrets, envelope metadata) we still need for local-only auth.
- **Service layer**: `DeviceRegistrationService`, `DeviceRegistrationEnvelopeService`, `DeviceBootstrapService`, `DeviceRegistrationReadService`, `DeviceRegistrationEnvelopePayload.cs`, and `DeviceRegistrationJson.cs` implement verification, envelope issuance, activation, and read models. None of these depend on Entra-specific concepts—they only manipulate per-device secrets/envelopes.
- **API surface**: `Controllers/DeviceRegistrationsController.cs` exposes `/api/internal/devices/verify` and `/api/internal/devices/envelope`; `Controllers/DeviceBootstrapController.cs` exposes `/api/device/bootstrap`. These endpoints encapsulate the handshake and will still be required when LogicHost authenticates locally.
- **UI**: `Components/Pages/Devices/DeviceRegistrations.razor(+.cs/.css)` plus the navigation entry added in `Components/Layout/MainLayoutNavigation.razor.cs` display pending registrations and copy-ready envelopes. Keep these so operators retain a turnkey flow even without Entra.
- **Program wiring**: `Program.cs` registrations for the new services (`IDeviceRegistrationService`, `IDeviceRegistrationEnvelopeService`, `IDeviceBootstrapService`, `IDeviceRegistrationReadService`) and `ApplicationDbContext` updates must be ported so DI can resolve the new pipeline.

## 2. CameraAgent Provisioning + Bootstrap UX
- **Provisioning options**: `Configuration/DeviceProvisioningOptions.cs` centralizes where the agent writes its identity + secret blobs (defaults to `AppContext.BaseDirectory/data/provisioning`).
- **Identity persistence**: `Services/DeviceIdentityStore.cs` generates/persists a local `DeviceId` + verification code. It relies on `DeviceProvisioningOptions` and `TimeProvider` only—no Azure dependencies.
- **Secret escrow**: `Services/DeviceSecretStore.cs` encrypts bootstrap secrets using ASP.NET Data Protection and stores them on disk; `DeviceSecrets` record mirrors what LogicHost returns.
- **Bootstrap workflow & crypto**: `DeviceBootstrapWorkflow.cs`, `DeviceBootstrapCrypto.cs`, and the DTOs in `Services/Models/DeviceBootstrapModels.cs` implement the HTTP call to `/api/device/bootstrap` and AES-GCM decryption of the payload.
- **UI**: `Components/Pages/Devices/DeviceBootstrap.razor(+.cs/.css)` plus the nav update in `Components/Layout/MainLayoutNavigation.razor.cs` give operators a page to view the generated DeviceId/verification code, paste envelopes, and inspect cached secrets.
- **Program + DI**: CameraAgent `Program.cs` now binds `DeviceProvisioningOptions`, adds `IDeviceIdentityStore`, `IDeviceSecretStore`, and `DeviceBootstrapWorkflow` to the container. Preserve these lines when reapplying.
- **Support files**: `Properties/AssemblyInfo.cs` exposes internals to the test project. Keep this to avoid test build failures post-reset.

## 3. Local System-Owner Login Fallback (CameraAgent)
- **Service**: `Authentication/SystemOwnerLoginService.cs` implements hashed access-code validation, secure comparisons, audit logging, and cookie sign-in via `CameraAgentAuthenticationSchemes.InteractiveCookie`.
- **UI**: The new `/login` page (`Components/Pages/Login.razor` + code-behind) renders both the local fallback form and (optionally) the external redirect button. Even if we drop External ID, the local form plus validation / status messages should remain.
- **Routing helpers**: `Security/ReturnUrlHelper.cs` gained constants for `/login` and `/auth/external` plus `BuildExternalLoginPath`. Keep the `/login` change so redirects continue to function after we remove the external endpoint.
- **Tests**: `tests/HVO.SkyMonitor.CameraAgent.Tests/Authentication/SystemOwnerLoginServiceTests.cs` and the updated `ReturnUrlHelperTests.cs` verify the hashed credential flow and helper changes. These must move with the implementation.

## 4. Documentation Artifacts to Retain
- `docs/identity/agent-registration-plan.md` documents the bootstrap/verifier/envelope flow independent of Azure. This remains the blueprint for the handshake we still intend to support.
- `docs/architecture/phase-plan.md` includes the non-Azure deliverables related to device provisioning and can guide future sequencing even after reverting the Entra work.

## What to Ignore During Re-application
- Files explicitly tied to Azure Entra / External ID (`docs/setup-azure.md`, `docs/identity/external-id/*`, `src/HVO.SkyMonitor.Common/Configuration/KeyVaultConfigurationBuilderExtensions.cs`, etc.) should not be reintroduced when cherry-picking from the phase01 branch.
- Environment/template updates that only exist to pipe Entra or Key Vault settings (e.g., `B2C_*` variables in `.env.template`) can be left behind unless we later decide to support optional cloud sign-in.

By porting only the files listed above we keep the turn-key registration + local admin experience while removing the Azure dependency chain.