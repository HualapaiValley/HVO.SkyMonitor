# Authentication & Authorization Strategy (Option B.1)

> Target: Phase 01 multi-tenant support with a **centralized Azure Entra External ID (B2C) tenant** that brokers social/enterprise identity providers, plus a configuration-driven local admin fallback. This document explains how LogicHost, the camera agent, and observatory tenants authenticate, and how this differs from the legacy `HVOv9` stacks.

## 1. Objectives
- **Central identity hub** – all observatories live under a single Azure Entra External ID (formerly Azure AD B2C) tenant that we own. Users choose any approved external IdP (Google, Microsoft, Facebook, Apple, etc.) and Entra issues the final token—no per-observatory app registrations required.
- **Tenant scoping without hosting auth** – LogicHost receives Entra-issued tokens containing `tenantId/observatoryId` claims, so we stay multi-tenant without standing up IdentityServer unless offline installs demand it.
- **Pre-approved IdP lists** – observatories can restrict which social/enterprise providers are permissible for their users (e.g., Observatory A allows Google+Microsoft; Observatory B allows Microsoft-only). Enforcement happens via Entra user flows and custom policies.
- **Local admin fallback** – retain a configuration-based super-user credential (hashed + rotated) for air-gapped installs or disaster recovery.
- **Minimal surface area** – reuse `CentralIdentityOptions` and `CentralAuthenticationService` for both programmatic calls and interactive operator sessions.
- **Zero trust assumptions** – no implicit trust based on network location; every request carries a bearer token or signed cookie scoped to an observatory.

## 2. High-Level Flow (Option B.1)
1. **Identity hub provisioning**
   - We host a single Azure Entra External ID tenant (B2C) that contains:
     - Custom policies for each observatory (exposes `observatoryId`, `plan`, `role` claims).
     - Federated identity providers (Google, Microsoft Account, Facebook, Apple, generic OIDC/SAML) configured once and reused across observatories.
   - Observatory onboarding only registers business metadata (name, billing, allowed IdPs) inside LogicHost—**no Azure app creation per tenant**.
2. **User sign-in**
   - Operators hit LogicHost or camera agent UI → redirected to Entra External ID user flow.
   - User chooses an approved external provider; Google/Microsoft/Facebook handle credentials.
   - Upon success, Entra emits ID/Access tokens to LogicHost/camera agent containing both issuer metadata and observatory claims managed centrally.
3. **Camera agent bootstrap**
   - `CentralIdentityOptions` points to the central Entra authority (e.g., `https://{our-tenant}.b2clogin.com/{tenant}.onmicrosoft.com/{policy}/`).
   - `CentralAuthenticationService` performs client credentials flow using confidential app credentials issued by the **same** hub tenant (not per-observatory). Observatories are scoped via claims, not directories.
4. **LogicHost validation & fan-out**
   - `HVO.SkyMonitor.LogicHost/Program.cs` configures JWT bearer to trust the External ID issuer and map `observatoryId` to tenancy boundaries.
   - Authorization policies evaluate both scopes (`api.camera`, `api.frames`, `api.images`) and per-observatory roles.
5. **Local/admin fallback**
   - When `CentralIdentity:Mode = ApiKey` (offline builds) or when Entra is unavailable, we fall back to Identity Framework + OpenIddict, mirroring the legacy host but still honoring observatory contexts.
   - LogicHost’s admin surface is **system-owner only**; observatories never receive direct access to local admin portals. They interact exclusively through their camera agents or APIs with delegated scopes.
   - This fallback can also federate to external IdPs through ASP.NET Core Identity external login providers if a customer demands a self-hosted option later.

Refer to `docs/architecture/diagrams/auth-sequence.mmd` for the request/response sequence (updated to show External ID as broker).

## 3. Components & Code References
| Component | File / Namespace | Notes |
| --- | --- | --- |
| Central identity options | `src/HVO.SkyMonitor.CameraAgent/Configuration/CentralIdentityOptions.cs` | Defines `ServiceUrl`, `Mode`, `ClientCredentials`, `ApiKey`, `InteractiveClient`. Same class is reused by LogicHost for strongly typed config validation. |
| External ID policies | Azure Entra External ID tenant (infra-as-code TBD) | Holds custom policies/user flows that inject `observatoryId`, `tenantPlan`, `role` claims and enforce IdP allow-lists per observatory. Documented under `docs/identity/operations-index.md` (Phase 01 addendum pending). |
| Token acquisition | `CentralAuthenticationService` | Implements client credentials & API-key modes, caches tokens using `TimeProvider`. Uses a single confidential application against the External ID tenant; observatory scoping arrives via claims. |
| Delegating handler | `CentralIdentityDelegatingHandler` | Attaches bearer tokens to `HttpClient` calls (`SkyMonitor.Api`). |
| Interactive UI flows | `AuthenticationController`, `RedirectToLogin` component | Handle login/logout redirections. Mirrors the `HVOv9` Razor Pages structure but Blazorized. |
| LogicHost identity configuration | `src/HVO.SkyMonitor.LogicHost/Program.cs` + `DatabaseSeeder.cs` | Configures JWT bearer to trust External ID and optional OpenIddict fallback. Seeds app roles/scopes so `observatoryId` is enforced in auth handlers. |
| Legacy reference | `HVOv9-SkyMonitorv6/src/HVO.SkyMonitorV6.CameraAgent.RPiCam/Program.cs` | Shows the previous auth pipeline (cookie + IdentityServer4). Useful when mapping missing behaviors (e.g., device code flow). |

## 4. Local Admin Fallback (Config-Based)
- **Configuration** – define `CentralIdentity:Mode = ApiKey` along with `CentralIdentity:ApiKey:{Name,Key,Scopes}` in a secure store (User Secrets, Key Vault, or `.devcontainer/devcontainer.local.env`).
- **Hashing/rotation** – store hashes rather than plaintext where possible, following `docs/identity/secrets-reference.md`.
- **Usage** – camera agent HTTP clients inject `X-API-Key` header; LogicHost’s `ApiKeyAuthenticationHandler` matches hashed keys and issues claims identical to the Entra scopes.
- **Runbooks** – `docs/identity/operations-runbook.md` will be updated to include rotation scripts (TBD under Phase 02).

## 5. Legacy Behavior Callouts
| Legacy behavior | Current stance | Action |
| --- | --- | --- |
| LogicHost acted as its own identity provider (OpenIddict single-tenant). | Still supported for fallbacks, but Azure Entra is preferred for multi-tenant scale. | Keep OpenIddict issuer enabled but treat as “local tenant” in diagrams and docs. |
| Each observatory registered its own Azure AD app + IdP list. | Replaced by one External ID tenant that centrally manages social/enterprise providers. | Provide runbook automation that stamps observatory metadata/allow lists without touching Azure app registrations. |
| Camera agents stored long-lived refresh tokens locally. | Tokens are short-lived; we rely on client credentials + caching (default 5 minutes). | Document operational guidance: avoid storing refresh tokens on disk unless we add device code flow for constrained modules. |
| Multi-tenant separation handled via hostname-based routing. | Separation now occurs through Azure Entra tenant IDs + storage namespaces. | Ensure queue/bucket naming matches tenant ID; detail in `contracts.md`. |

## 6. Implementation Checklist
1. **Provision External ID tenant** – add infrastructure scripts (Bicep/Azure CLI) that create the B2C tenant, upload custom policies, and configure Google/Microsoft/Facebook providers.
2. **Observatory metadata + allow lists** – extend LogicHost admin UI/API to store `allowedIdProviders` per observatory; expose these to External ID policies via REST technical profile.
3. **Config validation** – extend `CentralIdentityOptions` validators to ensure External ID metadata (authority, policy name, client ID/secret) exists when `Mode=ClientCredentials`.
4. **Admin fallback hardening** –
   - Hash API keys via `HmacSha256` + salt.
   - Introduce rotation telemetry (log warning when keys near expiry).
5. **Hybrid token acceptance** – update LogicHost’s `JwtBearerOptions` to trust both External ID and local issuers simultaneously. Add tests under `tests/HVO.SkyMonitor.IntegrationTests` verifying both flows.
6. **Docs & diagrams** – keep this document synchronized with `docs/architecture/diagrams/auth-sequence.mmd` and add External ID runbooks in `docs/identity/*.md`.

## 7. Open Questions
- **Device code / interactive fallback** – do we need device code flow for headless camera agents (similar to `HVOv9` CLI enrollments)? If yes, extend `CentralAuthenticationService` accordingly.
- **Tenant onboarding portal** – should tenants self-register via LogicHost UI, or will onboarding remain ops-driven for Phase 01? This affects what automation we build around Azure Entra registrations.

Track decisions here and mirror into future runbooks as they solidify.

## 8. When External ID Is Not Possible
- **Deploy Identity Framework + external logins** – LogicHost already carries ASP.NET Core Identity + OpenIddict. For customers that cannot use Azure Entra External ID, we can flip `CentralIdentity:Mode = LocalIdentity`, enable ASP.NET Identity’s Google/Microsoft/Facebook handlers, and continue brokering users centrally.
- **Per-observatory allow lists** – observatory metadata describes which external handlers are enabled; login UI hides providers that are disallowed for that tenant.
- **Claims parity** – regardless of IdP source, we emit `observatoryId`, `plan`, and scope claims via OpenIddict so downstream services behave identically.
- **Operations guidance** – document key rotation and client-secret distribution inside `docs/identity/operations-runbook.md` to minimize drift between the External ID and self-hosted modes.
