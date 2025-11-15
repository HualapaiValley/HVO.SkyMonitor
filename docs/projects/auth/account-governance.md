# Account Governance

**Last Updated:** 2025-11-15
**Status:** Phase 1 Implementation
**Applies To:** HVO.SkyMonitor Central Identity System

## Overview

This document defines the governance procedures for managing user accounts and system service accounts in the HVO.SkyMonitor central identity system. It covers account creation, lifecycle management, access control, and revocation procedures.

## Account Types

### USER Accounts

**Purpose:** Human users requiring interactive access to the system

**Characteristics:**
- Interactive login via password, passkeys, or external providers (GitHub, Microsoft)
- Full access to Blazor UI and web interfaces
- Subject to standard password policies and security requirements
- Can enable two-factor authentication (2FA)
- Email confirmation required for activation

**Use Cases:**
- System administrators
- Operators and monitoring personnel
- Developers and testers
- Any human requiring UI access

### SYSTEM Accounts

**Purpose:** Non-interactive service accounts for automated processes and integrations

**Characteristics:**
- **Cannot** sign in interactively (no password authentication)
- Authenticate via API keys or OAuth2 client credentials flow
- No access to Blazor UI or interactive features
- Designed for machine-to-machine communication
- No email confirmation required

**Use Cases:**
- Camera agent services
- Automated data collection processes
- Integration services
- CI/CD pipelines
- Monitoring and alerting systems

## Account Creation

### Creating USER Accounts

#### Via Seeding (Development)
Default admin account is automatically created on first startup:
- Email: `admin@skymonitor.local`
- Username: `admin`
- Default Password: `Admin@123456` (change immediately!)
- AccountType: `User`

#### Via UI (Production)
1. Navigate to the registration page
2. Provide email address and password
3. Confirm email via verification link
4. Administrator approves account (if approval required)

#### Via Administrative Tools
```csharp
var user = new ApplicationUser
{
    UserName = "username",
    Email = "user@domain.com",
    EmailConfirmed = true,  // Set to false to require email verification
    AccountType = AccountType.User
};

var result = await userManager.CreateAsync(user, "SecurePassword123!");
```

### Creating SYSTEM Accounts

#### Via Seeding (Development)
Default system account is automatically created on first startup:
- Email: `system@skymonitor.local`
- Username: `system-service`
- AccountType: `System`
- No password (API key authentication only)

#### Via Administrative Tools
```csharp
var systemAccount = new ApplicationUser
{
    UserName = "service-name",
    Email = "service@skymonitor.local",
    EmailConfirmed = true,
    AccountType = AccountType.System,
    PasswordHash = null  // System accounts don't use passwords
};

var result = await userManager.CreateAsync(systemAccount);

// Generate API key for authentication
var apiKey = await apiKeyService.CreateKeyAsync(systemAccount.Id, "Service API Key", ApiKeyAccessLevel.ReadWrite);
```

## Account Lifecycle

### Activation

**USER Accounts:**
1. Account created with `EmailConfirmed = false`
2. Verification email sent to user
3. User clicks confirmation link
4. Account activated and login enabled

**SYSTEM Accounts:**
1. Account created with `EmailConfirmed = true`
2. API key(s) generated immediately
3. Credentials securely provided to service owner
4. Service begins authentication

### Active Use

**Monitoring:**
- Failed login attempts logged
- Successful authentications tracked
- API key usage monitored
- Suspicious activity alerts

**Maintenance:**
- Regular password rotation (USER accounts - every 90 days recommended)
- API key rotation (SYSTEM accounts - every 30-90 days)
- Review of access levels quarterly
- Removal of unused accounts

### Deactivation

**Temporary Suspension:**
```csharp
user.LockoutEnd = DateTimeOffset.UtcNow.AddDays(30);
await userManager.UpdateAsync(user);
```

**Account Disable:**
- Future implementation will use `IsActive` flag
- Currently managed via lockout mechanism

### Deletion

**Soft Delete (Recommended):**
- Set permanent lockout: `LockoutEnd = DateTimeOffset.MaxValue`
- Revoke all API keys
- Remove from roles/groups
- Archive account data

**Hard Delete (Data Removal):**
```csharp
var result = await userManager.DeleteAsync(user);
```

**Considerations:**
- Cascade deletes API keys
- May break audit trails
- Consider GDPR/data retention requirements

## Credential Management

### Password Policies (USER Accounts)

**Requirements:**
- Minimum 6 characters (configurable)
- Email confirmation required
- Lockout after failed attempts (configurable)

**Best Practices:**
- Use strong, unique passwords
- Enable two-factor authentication (2FA)
- Use passkeys where supported
- Regular password rotation

**Configuration (Program.cs):**
```csharp
builder.Services.AddIdentityCore<ApplicationUser>(options =>
{
    options.SignIn.RequireConfirmedAccount = true;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Password.RequiredLength = 8;
    // Add more password requirements as needed
});
```

### API Key Management (SYSTEM Accounts)

**Creation:**
- Generate via API or administrative interface
- Display raw key only once at creation
- Store hashed value in database (PBKDF2)

**Storage:**
- Raw keys never stored in database
- Client stores in secure configuration (Azure Key Vault, AWS Secrets Manager, environment variables)
- Never commit to source control

**Rotation:**
- Generate new key before expiring old key
- Update service configuration with new key
- Test authentication with new key
- Revoke old key after grace period

**Revocation:**
- Immediate via admin interface or API
- Set `IsActive = false` on API key record
- Future requests with that key fail authentication

## Access Control

### Role-Based Access

**Future Implementation:**
- Define roles (Admin, Operator, ReadOnly, etc.)
- Assign users to roles
- Policy-based authorization

**Current State:**
- AccountType provides basic distinction
- API key AccessLevel (ReadOnly, ReadWrite, Admin) for granular control

### Least Privilege Principle

- Grant minimum permissions necessary
- Use SYSTEM accounts for automation (not personal USER accounts)
- Regular access reviews
- Time-limited access for temporary needs

## Recovery Procedures

### USER Account Recovery

**Forgot Password:**
1. User requests password reset
2. Reset link sent to confirmed email
3. User creates new password
4. Old password invalidated

**Locked Account:**
1. Verify account lockout reason
2. Administrator unlocks account
3. User required to change password
4. Review security logs for suspicious activity

**Compromised Account:**
1. Immediately lock account
2. Revoke all active sessions
3. Revoke API keys (if any)
4. Investigate security breach
5. Force password reset
6. Enable 2FA before reactivation

### SYSTEM Account Recovery

**Lost API Key:**
1. Generate new API key
2. Update service configuration
3. Revoke old key after verification

**Compromised API Key:**
1. Immediately revoke compromised key
2. Generate replacement key
3. Update service with new credentials
4. Review audit logs for unauthorized access
5. Assess scope of data breach

## Access Revocation

### Immediate Revocation (Emergency)

**USER Account:**
```csharp
user.LockoutEnd = DateTimeOffset.MaxValue;
await userManager.UpdateAsync(user);
await signInManager.SignOutAsync();
```

**SYSTEM Account:**
```csharp
// Revoke all API keys for the account
var apiKeys = await apiKeyService.GetKeysForUserAsync(systemAccount.Id);
foreach (var key in apiKeys)
{
    key.IsActive = false;
    key.RevokedUtc = DateTime.UtcNow;
    key.RevokedBy = adminUsername;
    key.RevokedReason = "Emergency security lockdown";
}
await dbContext.SaveChangesAsync();
```

### Planned Revocation (Employee Departure)

**Day Before Departure:**
1. Document all accounts and access
2. Identify knowledge transfer needs
3. Prepare replacement accounts if needed

**On Departure:**
1. Lock user account
2. Revoke API keys
3. Remove from all roles/groups
4. Archive account data
5. Update documentation

**30 Days After:**
1. Review for any remaining dependencies
2. Permanently delete if no retention requirements
3. Update access control documentation

## Audit and Compliance

### Logging Requirements

**Must Log:**
- Account creation (type, creator, timestamp)
- Successful logins (user, IP, timestamp)
- Failed login attempts (user, IP, timestamp, reason)
- Password changes
- API key creation/rotation/revocation
- Account lockouts
- Account deletions
- Permission/role changes

**Log Retention:**
- Security logs: 1 year minimum
- Audit logs: Per compliance requirements
- Debug logs: 30 days

### Regular Reviews

**Quarterly:**
- Review all SYSTEM accounts and verify they're still needed
- Check for unused USER accounts (no login in 90+ days)
- Review API key usage patterns
- Audit role assignments

**Annually:**
- Comprehensive access review
- Update governance documentation
- Review and update security policies
- Compliance audit preparation

### Compliance Considerations

- GDPR: Right to erasure, data portability
- SOC 2: Access control, monitoring, audit trails
- Industry-specific: Healthcare (HIPAA), Financial (PCI-DSS), etc.

## Troubleshooting

### Common Issues

**USER cannot log in:**
1. Check account is not locked out (`LockoutEnd`)
2. Verify email is confirmed
3. Check password hasn't expired
4. Review recent failed login attempts

**SYSTEM account authentication fails:**
1. Verify API key is active (`IsActive = true`)
2. Check key hasn't expired (`ExpiresUtc`)
3. Ensure correct key value (hash mismatch)
4. Verify AccountType is System
5. Check service configuration

**"System accounts cannot sign in interactively" error:**
- This is expected behavior for SYSTEM accounts
- Use API key authentication instead
- If interactive access needed, create a USER account

## References

- Central Identity Plan: `docs/projects/auth/central-identity-plan.md`
- Target Schema: `docs/projects/auth/target-schema.md`
- Data Protection: `docs/projects/auth/data-protection-and-connections.md`
- API Key Documentation: `HVO.SkyMonitor.Common/Security/README.md` (future)

## Change History

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2025-11-15 | 1.0 | Initial governance documentation for Phase 1 | Copilot |
