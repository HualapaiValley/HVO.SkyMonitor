# Target Identity Schema - Central Identity System

**Date:** 2025-11-14
**Status:** Planning Phase
**Database:** PostgreSQL via Aspire (`skymonitordb`)

## Schema Overview

The centralized identity system will consist of:
1. **ASP.NET Core Identity tables** (standard schema with extensions)
2. **OpenIddict tables** (OAuth2/OIDC flows)
3. **API Key tables** (programmatic access)
4. **Audit/Logging tables** (security events)

## Extended Identity Tables

### AspNetUsers (Extended)
Extends standard ASP.NET Core Identity with:

| Column | Type | Description |
|--------|------|-------------|
| Id | string | Primary key (inherited) |
| UserName | string | Username (inherited) |
| Email | string | Email address (inherited) |
| EmailConfirmed | bool | Email verification status (inherited) |
| PasswordHash | string | Hashed password (inherited) |
| SecurityStamp | string | Security token (inherited) |
| ... | ... | Other standard Identity columns |
| **AccountType** | int/enum | **NEW:** USER (0) or SYSTEM (1) |
| **CreatedUtc** | timestamp | **NEW:** Account creation timestamp |
| **LastLoginUtc** | timestamp | **NEW:** Last successful login |
| **IsActive** | bool | **NEW:** Account enabled/disabled flag |

**Constraints:**
- SYSTEM accounts cannot have passwords (enforce at application level)
- SYSTEM accounts cannot sign in interactively (enforce via login UI)
- UserName unique constraint (inherited)
- Email unique constraint (optional, per Identity config)

### AspNetRoles
Standard ASP.NET Core Identity roles table (no changes planned initially)

### AspNetUserRoles
Standard many-to-many table linking users to roles (no changes)

### AspNetUserClaims
Standard user claims table (no changes)

### AspNetUserLogins
Standard external login providers table (no changes)

### AspNetUserTokens
Standard user tokens table (no changes)

### AspNetRoleClaims
Standard role claims table (no changes)

## API Key Tables

### ApiKeys
Centralized API key storage and management

| Column | Type | Nullable | Description |
|--------|------|----------|-------------|
| Id | uuid | No | Primary key |
| UserId | string | No | Foreign key to AspNetUsers.Id |
| DisplayName | string(200) | No | Human-friendly key name |
| HashedKey | string | No | PBKDF2 hashed API key value |
| AccessLevel | int | No | ReadOnly(0), ReadWrite(1), Admin(2) |
| Scopes | string[] | Yes | Optional scope restrictions (future) |
| CreatedUtc | timestamp | No | Creation timestamp |
| CreatedBy | string(256) | Yes | User/system that created the key |
| ExpiresUtc | timestamp | Yes | Optional expiration timestamp |
| LastUsedUtc | timestamp | Yes | Last successful authentication |
| IsActive | bool | No | Key enabled/disabled flag |
| RevokedUtc | timestamp | Yes | Revocation timestamp |
| RevokedBy | string(256) | Yes | User/system that revoked the key |
| RevokedReason | string(500) | Yes | Reason for revocation |

**Indexes:**
- `IX_ApiKeys_UserId` - Foreign key index
- `IX_ApiKeys_IsActive_ExpiresUtc` - Active key lookups
- `IX_ApiKeys_LastUsedUtc` - Usage tracking queries

**Constraints:**
- Foreign key to AspNetUsers.Id with CASCADE delete
- Only one API key can have same DisplayName per user (unique constraint)
- HashedKey must be stored, never plain text
- Raw key shown only once at creation time

## OpenIddict Tables

### OpenIddictApplications
OAuth2/OIDC client applications (camera agents, UI clients, automation tools)

| Column | Type | Description |
|--------|------|-------------|
| Id | string | Primary key |
| ClientId | string | OAuth2 client identifier |
| ClientSecret | string | Hashed client secret (for confidential clients) |
| DisplayName | string | Human-friendly application name |
| Type | string | public, confidential, etc. |
| ConsentType | string | explicit, implicit, systematic |
| Permissions | string | JSON array of allowed permissions |
| Requirements | string | JSON array of requirements |
| ... | ... | Other OpenIddict fields |

**Example Applications:**
- `camera-agent-simulator` - Confidential client for simulator
- `camera-agent-zwo-{id}` - Confidential client per ZWO instance
- `skymonitor-ui` - Public client for Blazor UI (Authorization Code + PKCE)

### OpenIddictAuthorizations
User authorizations/consents for client applications

### OpenIddictScopes
Available OAuth2 scopes (e.g., `skymonitor:read`, `skymonitor:write`, `skymonitor:admin`)

### OpenIddictTokens
Issued access tokens, refresh tokens, authorization codes

## Audit/Logging Tables (Identity Hardening)

### SecurityAuditLog (Future)
Security event logging for compliance and forensics

| Column | Type | Description |
|--------|------|-------------|
| Id | bigint | Primary key |
| EventType | string | LoginSuccess, LoginFailed, ApiKeyCreated, etc. |
| UserId | string | Nullable FK to AspNetUsers |
| Timestamp | timestamp | Event timestamp |
| IpAddress | string | Client IP address |
| UserAgent | string | Client user agent |
| Details | jsonb | Additional event-specific data |
| Severity | int | Info, Warning, Error, Critical |

## Signed URL Infrastructure (Phase 5)

### SignedUrlSecrets (Future)
HMAC secret key storage and rotation

| Column | Type | Description |
|--------|------|-------------|
| Id | uuid | Primary key |
| KeyVersion | int | Version number for rotation |
| SecretKey | bytea | HMAC secret key (encrypted at rest) |
| CreatedUtc | timestamp | Key creation timestamp |
| ExpiresUtc | timestamp | Key expiration (for rotation overlap) |
| IsActive | bool | Currently in use for signing |

**Note:** Signed URLs themselves are ephemeral and not stored in DB

## Data Protection Keys

Uses ASP.NET Core Data Protection with PostgreSQL storage:
- Table name: `DataProtectionKeys` (created by framework)
- Stores encrypted keys for cookie encryption, CSRF tokens, etc.
- Managed automatically by ASP.NET Core

## Migration Strategy

### Phase 1 Migration
1. Drop existing `HVO.SkyMonitor/Migrations/` directory
2. Create initial migration with:
   - Standard ASP.NET Identity tables
   - Extended AspNetUsers with AccountType, CreatedUtc, etc.
   - ApiKeys table
   - Data Protection keys table
3. Seed data:
   - Admin USER account
   - System SERVICE account

### Phase 2 Migration
1. Add OpenIddict tables via OpenIddict.EntityFrameworkCore
2. Seed initial applications and scopes

### Phase 5 Migration
1. Add SignedUrlSecrets table

### Identity Hardening Migration
1. Add SecurityAuditLog table

## Connection Strings

### Development (Aspire)
```json
{
  "ConnectionStrings": {
    "skymonitordb": "Host=localhost;Port=5432;Database=skymonitordb;Username=postgres;Password={aspire-parameter}"
  }
}
```

### Production
```json
{
  "ConnectionStrings": {
    "skymonitordb": "Host={prod-host};Port=5432;Database=skymonitordb;Username={prod-user};Password={secret};SSL Mode=Require"
  }
}
```

## AccountType Enum

```csharp
namespace HVO.SkyMonitor.Data;

/// <summary>
/// Defines the type of account for authorization and authentication purposes.
/// </summary>
public enum AccountType
{
    /// <summary>
    /// Human user account with interactive login capabilities.
    /// Can sign in via password, passkeys, or external providers.
    /// </summary>
    User = 0,
    
    /// <summary>
    /// System/service account for non-interactive authentication.
    /// Can only authenticate via API keys or client credentials flow.
    /// Cannot sign in interactively or use passwords.
    /// </summary>
    System = 1
}
```

## Authorization Policies

### Policy Definitions

```csharp
// In HVO.SkyMonitor.Common/Security/AuthorizationPolicyNames.cs

public static class AuthorizationPolicyNames
{
    public const string RequireUserAccount = "RequireUserAccount";
    public const string RequireSystemAccount = "RequireSystemAccount";
    public const string RequireApiKeyReadOnly = "RequireApiKeyReadOnly";
    public const string RequireApiKeyReadWrite = "RequireApiKeyReadWrite";
    public const string RequireApiKeyAdmin = "RequireApiKeyAdmin";
}
```

### Policy Implementation

```csharp
// In Program.cs

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(AuthorizationPolicyNames.RequireUserAccount, policy =>
        policy.RequireClaim("account_type", "User"))
    .AddPolicy(AuthorizationPolicyNames.RequireSystemAccount, policy =>
        policy.RequireClaim("account_type", "System"))
    .AddPolicy(AuthorizationPolicyNames.RequireApiKeyReadWrite, policy =>
        policy.RequireClaim(ApiKeyClaims.AccessLevel, "ReadWrite", "Admin"))
    .AddPolicy(AuthorizationPolicyNames.RequireApiKeyAdmin, policy =>
        policy.RequireClaim(ApiKeyClaims.AccessLevel, "Admin"));
```

## Entity Relationships

```
AspNetUsers (1) -----> (*) ApiKeys
    |
    |----> (*) OpenIddictAuthorizations
    |
    |----> (*) OpenIddictTokens (via Authorizations)
    |
    +----> (*) SecurityAuditLog (future)

OpenIddictApplications (1) -----> (*) OpenIddictAuthorizations
                          |
                          +----> (*) OpenIddictTokens

OpenIddictScopes (*) <-----> (*) OpenIddictTokens
```

## Security Considerations

1. **Password Storage:** BCrypt via ASP.NET Core Identity (default)
2. **API Key Storage:** PBKDF2 with 10,000 iterations (via ApiKeyHasher)
3. **Client Secrets:** Hashed by OpenIddict
4. **HMAC Secrets:** AES-256 encrypted at rest (Phase 5)
5. **Connection Strings:** Stored in Azure Key Vault or equivalent (production)
6. **Data Protection Keys:** Encrypted at rest in PostgreSQL

## Next Steps

1. ✅ Schema documented
2. Delete existing migrations (all projects)
3. Create new initial migration with extended schema
4. Test migration against Aspire PostgreSQL container
5. Seed initial admin and system accounts
6. Proceed to Phase 1 implementation

## References

- ASP.NET Core Identity Schema: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/identity
- OpenIddict Entity Framework Core: https://documentation.openiddict.com/configuration/entity-framework-core.html
- PostgreSQL Data Types: https://www.postgresql.org/docs/current/datatype.html
