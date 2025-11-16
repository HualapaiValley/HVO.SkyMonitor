# Phase 0 Inventory - Existing Identity/API-Key Code Paths

**Date:** 2025-11-14
**Purpose:** Document current state before central identity refactor

## Executive Summary

The current implementation has:
- **3 separate Identity stores** (HVO.SkyMonitor, Simulator Agent, ZWO Agent)
- **Shared API key infrastructure** in HVO.SkyMonitor.Common
- **Per-service ApplicationDbContext and migrations**
- **Complete Identity UI** in all three services

## Database Configuration

### HVO.SkyMonitor (Main Application)
- **Database:** PostgreSQL via Aspire (`skymonitordb`)
- **Connection:** Managed by Aspire orchestration
- **DbContext:** `HVO.SkyMonitor.Data.ApplicationDbContext`
- **Migrations Location:** `src/HVO.SkyMonitor/Migrations/`
- **Migrations:**
  - `20251114000956_InitialCreate.cs` - ASP.NET Identity + ApiKeys table
  - `ApplicationDbContextModelSnapshot.cs`

### HVO.SkyMonitor.CameraAgent.Simulator
- **Database:** SQLite (`DefaultConnection`)
- **DbContext:** `HVO.SkyMonitor.CameraAgent.Simulator.Data.ApplicationDbContext`
- **Migrations Location:** `src/HVO.SkyMonitor.CameraAgent.Simulator/Data/Migrations/`
- **Migrations:**
  - `00000000000000_CreateIdentitySchema.cs` - Initial Identity schema
  - `20251109001020_AddUserApiKeys.cs` - Added ApiKeys table
  - `ApplicationDbContextModelSnapshot.cs`

### HVO.SkyMonitor.CameraAgent.ZWO
- **Database:** SQLite (`DefaultConnection`)
- **DbContext:** `HVO.SkyMonitor.CameraAgent.ZWO.Data.ApplicationDbContext`
- **Migrations Location:** `src/HVO.SkyMonitor.CameraAgent.ZWO/Data/Migrations/`
- **Migrations:**
  - `00000000000000_CreateIdentitySchema.cs` - Initial Identity schema
  - `20251109001020_AddUserApiKeys.cs` - Added ApiKeys table
  - `ApplicationDbContextModelSnapshot.cs`

## Identity Configuration

### HVO.SkyMonitor
**File:** `src/HVO.SkyMonitor/Program.cs` (lines 129-165)
```csharp
builder.AddNpgsqlDbContext<ApplicationDbContext>("skymonitordb");

builder.Services.AddIdentityCore<ApplicationUser>(options => { ... })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

authenticationBuilder.AddIdentityCookies();
```

**ApplicationUser:** `src/HVO.SkyMonitor/Data/ApplicationUser.cs`
- Extends `IdentityUser`
- Has `ICollection<ApiKey> ApiKeys` navigation property

### Camera Agents (Simulator & ZWO)
**Files:** 
- `src/HVO.SkyMonitor.CameraAgent.Simulator/Program.cs` (lines 187-195)
- `src/HVO.SkyMonitor.CameraAgent.ZWO/Program.cs` (lines 185-193)

```csharp
builder.AddSqliteDbContext<ApplicationDbContext>("DefaultConnection");

builder.Services.AddIdentityCore<ApplicationUser>(options => { ... })
    .AddEntityFrameworkStores<ApplicationDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

authenticationBuilder.AddIdentityCookies();
```

**ApplicationUser:**
- `src/HVO.SkyMonitor.CameraAgent.Simulator/Data/ApplicationUser.cs`
- `src/HVO.SkyMonitor.CameraAgent.ZWO/Data/ApplicationUser.cs`
- Both extend `IdentityUser` with ApiKeys collection

## API Key Infrastructure

### Shared Components (HVO.SkyMonitor.Common)
**Location:** `src/HVO.SkyMonitor.Common/Security/`

**Core Files:**
- `ApiKey.cs` - Entity model with HashedKey, AccessLevel, expiration
- `ApiKeyAccessLevel.cs` - Enum: ReadOnly, ReadWrite, Admin
- `ApiKeyHasher.cs` - PBKDF2-based key hashing
- `ApiKeyClaims.cs` - Claim type constants
- `ApiKeyConstants.cs` - Scheme name, header name constants
- `ApiKeyAuthenticationOptions.cs` - Authentication scheme options
- `ApiKeyAuthenticationHandler.cs` - Base handler
- `DefaultApiKeyAuthenticationHandler.cs` - Default implementation
- `ApiKeyAuthenticationExtensions.cs` - DI registration extensions
- `AuthenticationBuilderExtensions.cs` - Builder extensions
- `AuthorizationPolicyNames.cs` - Policy name constants

### Per-Service Validators
**HVO.SkyMonitor:**
- `src/HVO.SkyMonitor/Data/DatabaseApiKeyValidator.cs`
- Validates against PostgreSQL ApplicationDbContext

**Camera Agents:**
- `src/HVO.SkyMonitor.CameraAgent.Simulator/Security/CameraAgentApiKeyValidator.cs`
- `src/HVO.SkyMonitor.CameraAgent.ZWO/Security/CameraAgentApiKeyValidator.cs`
- Validate against local SQLite ApplicationDbContext

## Identity UI Components

All three services have complete Identity UI in `Components/Account/`:

### Shared Components (All Services)
- `IdentityComponentsEndpointRouteBuilderExtensions.cs` - Endpoint mapping
- `IdentityNoOpEmailSender.cs` - Email sender placeholder
- `IdentityRedirectManager.cs` - Post-auth navigation
- `IdentityRevalidatingAuthenticationStateProvider.cs` - Auth state management

### HVO.SkyMonitor Only
- `IdentityUserAccessor.cs` - User accessor helper

### Account Management Pages (All Services)
**Location:** `Components/Account/Pages/Manage/`
- `ApiKeys.razor` + `.razor.cs` - API key management UI
- `EnableAuthenticator.razor` + `.razor.cs` - TOTP setup
- `ResetAuthenticator.razor` + `.razor.cs` - TOTP reset
- `TwoFactorAuthentication.razor` + `.razor.cs` - 2FA management

Plus additional pages for:
- Login, Register, Logout
- Email confirmation, password reset
- External logins (GitHub, Microsoft)
- Passkey (WebAuthn) support
- Personal data management

## Authentication Schemes in Use

### HVO.SkyMonitor
1. **Identity Cookies** - User authentication
2. **API Key** - Programmatic access via `DatabaseApiKeyValidator`

### Camera Agents
1. **Identity Cookies** - Local user authentication
2. **API Key** - Programmatic access via `CameraAgentApiKeyValidator`

## Current Authorization Policies

**Defined in:** `HVO.SkyMonitor.Common/Security/AuthorizationPolicyNames.cs`
- Currently contains policy name constants
- No AccountType-based policies yet

## Data Protection

**HVO.SkyMonitor:**
- Uses PostgreSQL for data protection keys (configured in Program.cs)

**Camera Agents:**
- Uses local filesystem or SQLite (needs verification)

## Docker Compose / Testcontainers Management

- **Entry Points:** `docker-compose.dev.yml` plus helper scripts in `./scripts/infra:*`
- **Infrastructure Containers:** PostgreSQL (`skymonitordb`), Redis, MinIO, SMTP/MailHog
- **Lifecycle:** Compose stack runs independently of the .NET process; Testcontainers spin up per integration test suite
- **Credential Flow:** Secrets supplied through User Secrets, `.env`, `.devcontainer/devcontainer.local.env`, or CI/CD variables

## To Be Removed (Phase 0 Tasks)

### Migrations to Delete
1. All files in `src/HVO.SkyMonitor/Migrations/`
2. All files in `src/HVO.SkyMonitor.CameraAgent.Simulator/Data/Migrations/`
3. All files in `src/HVO.SkyMonitor.CameraAgent.ZWO/Data/Migrations/`

### Database Cleanup
- Stop the Compose stack (`docker compose -f docker-compose.dev.yml down`) before removing data
- Remove any local SQLite files from camera agents
- Prune Docker volumes if needed: `docker volume prune`

### Code to Retain (Needs Refactoring)
- `HVO.SkyMonitor.Common/Security/*` - Will be enhanced, not removed
- Identity UI components - Will be consolidated to main app only
- ApplicationUser models - Will be extended with AccountType

## Target Architecture (Post Phase 0)

### Central Identity (HVO.SkyMonitor)
- Single PostgreSQL database provisioned via Docker Compose/Testcontainers
- ASP.NET Identity + OpenIddict entities
- `ApplicationUser` with `AccountType` enum (USER vs SYSTEM)
- Centralized API key management
- Token issuance (Authorization Code + PKCE, Client Credentials)
- Signed URL generation

### Camera Agents (Simulator & ZWO)
- **No local Identity** - All removed
- Use central HVO.SkyMonitor for authentication
- Consume JWTs or API keys from central service
- Minimal auth middleware for inbound requests (if needed)

## Key Observations

1. **Redundant Identity Stores:** All three services maintain independent user databases
2. **API Key Duplication:** Each service validates keys against its own database
3. **UI Duplication:** Complete Identity UI exists in all three projects
4. **Shared Infrastructure:** HVO.SkyMonitor.Common has good foundation for shared auth
5. **Compose/Testcontainers Ready:** PostgreSQL, Redis, MinIO scaffolding already exists
6. **No OpenIddict:** No OAuth2/OIDC infrastructure exists yet
7. **No AccountType:** No distinction between user and system accounts
8. **No Signed URLs:** No implementation for high-volume media endpoints

## Next Steps (Phase 0 Continuation)

1. ✅ Inventory complete
2. Delete all migration files
3. Define target schema (ERD or table list)
4. Document data protection and connection strings
5. Update README.md with Identity rebuild notice
6. Commit Phase 0 completion

## References

- Central Identity Plan: `docs/projects/auth/central-identity-plan.md`
- Aspire setup notes were removed (see git history for `docs/ASPIRE_SETUP.md` if needed)
- Project README: `README.md`
