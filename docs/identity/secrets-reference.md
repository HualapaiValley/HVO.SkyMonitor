# Identity Secrets & Configuration Guide

This document provides comprehensive guidance for all secrets, keys, and configuration values required for identity hardening (Central Identity security, operations, and observability).

## Overview

Identity hardening introduces production-grade security measures including:
- Proper secret storage for OpenIddict signing/encryption keys
- API key hashing with configurable salt/secret
- Signed URL HMAC key management
- Rate limiting configuration
- TLS certificate management
- Structured logging and metrics

## Secret Categories

### 1. OpenIddict Signing & Encryption Keys

**Purpose:** Cryptographic keys used to sign and encrypt OAuth2/OIDC tokens.

#### Development Environment

In development, OpenIddict automatically generates ephemeral certificates:

```csharp
// From Program.cs - Development only
options.AddDevelopmentEncryptionCertificate()
       .AddDevelopmentSigningCertificate();
```

**No configuration needed for development.**

#### Production Environment

**Required Secrets:**

| Secret Name | Type | Description | Storage Location |
|------------|------|-------------|------------------|
| `OpenIddict:SigningCertificate:Path` | String | Path to X.509 signing certificate (.pfx) | Azure Key Vault / File System |
| `OpenIddict:SigningCertificate:Password` | Secret | Password for signing certificate | Azure Key Vault |
| `OpenIddict:EncryptionCertificate:Path` | String | Path to X.509 encryption certificate (.pfx) | Azure Key Vault / File System |
| `OpenIddict:EncryptionCertificate:Password` | Secret | Password for encryption certificate | Azure Key Vault |

**Environment Variables:**

```bash
# Production - use certificate from Azure Key Vault or file system
OPENIDDICT_SIGNING_CERT_PATH=/path/to/signing-cert.pfx
OPENIDDICT_SIGNING_CERT_PASSWORD=<secure-password>
OPENIDDICT_ENCRYPTION_CERT_PATH=/path/to/encryption-cert.pfx
OPENIDDICT_ENCRYPTION_CERT_PASSWORD=<secure-password>
```

**User Secrets (Local Testing):**

```bash
cd src/HVO.SkyMonitor
dotnet user-secrets set "OpenIddict:SigningCertificate:Path" "/path/to/signing-cert.pfx"
dotnet user-secrets set "OpenIddict:SigningCertificate:Password" "cert-password"
dotnet user-secrets set "OpenIddict:EncryptionCertificate:Path" "/path/to/encryption-cert.pfx"
dotnet user-secrets set "OpenIddict:EncryptionCertificate:Password" "cert-password"
```

**Certificate Generation:**

```bash
# Generate self-signed certificate for testing (NOT for production)
openssl req -x509 -newkey rsa:4096 -sha256 -days 365 \
  -nodes -keyout signing-key.pem -out signing-cert.pem \
  -subj "/CN=HVO.SkyMonitor.Signing"

# Convert to PKCS#12 format with password
openssl pkcs12 -export -out signing-cert.pfx \
  -inkey signing-key.pem -in signing-cert.pem \
  -password pass:YourSecurePassword

# Repeat for encryption certificate
openssl req -x509 -newkey rsa:4096 -sha256 -days 365 \
  -nodes -keyout encryption-key.pem -out encryption-cert.pem \
  -subj "/CN=HVO.SkyMonitor.Encryption"

openssl pkcs12 -export -out encryption-cert.pfx \
  -inkey encryption-key.pem -in encryption-cert.pem \
  -password pass:YourSecurePassword
```

**Production Recommendations:**
- Use certificates from a trusted CA
- Store certificates in Azure Key Vault
- Rotate certificates every 12 months
- Use separate certificates for signing and encryption
- Use at least RSA 4096-bit keys

---

### 2. API Key Hashing Secret

**Purpose:** Salt/secret used for hashing API keys before storage in the database.

**Current Implementation:** Uses SHA256 without additional salt (basic security).

**Security Note:** The current `ApiKeyHasher` implementation uses plain SHA256 hashing without a salt. For Identity Hardening, we document the current approach but recommend adding a configurable salt for enhanced security.

**Optional Enhancement Configuration:**

| Secret Name | Type | Description | Storage Location |
|------------|------|-------------|------------------|
| `ApiKey:HashingSalt` | Secret | Salt added to API key before hashing | User Secrets / Azure Key Vault |

**Environment Variables:**

```bash
# Optional - for enhanced security
API_KEY_HASHING_SALT=<random-256-bit-value>
```

**User Secrets:**

```bash
cd src/HVO.SkyMonitor
dotnet user-secrets set "ApiKey:HashingSalt" "<generate-random-value>"
```

**Generate Random Salt:**

```bash
# Generate 256-bit random value (Linux/macOS)
openssl rand -base64 32

# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
```

**Production Recommendations:**
- Generate a unique salt per environment
- Store in Azure Key Vault
- Rotate salt requires re-hashing all existing API keys (requires migration)
- Consider using Argon2 or PBKDF2 for future enhancements

---

### 3. Signed URL HMAC Secret

**Purpose:** HMAC-SHA256 secret key for generating and validating signed URLs for high-volume media endpoints.

**Required Configuration:**

| Secret Name | Type | Description | Min Length | Storage Location |
|------------|------|-------------|------------|------------------|
| `SignedTicket:Secret` | Secret | HMAC-SHA256 secret key | 32 bytes (256 bits) | User Secrets / Azure Key Vault |

**Environment Variables:**

```bash
# Required for signed URL functionality
SIGNED_TICKET_SECRET=<random-256-bit-value>
SIGNED_TICKET_DEFAULT_TTL_SECONDS=300
SIGNED_TICKET_MAX_CLOCK_SKEW_SECONDS=30
SIGNED_TICKET_ENFORCE_ALLOWED_PATHS=true
SIGNED_TICKET_ALLOWED_PATHS=/api/v1.0/frame/,/api/v1.0/image/
```

**User Secrets:**

```bash
cd src/HVO.SkyMonitor
dotnet user-secrets set "SignedTicket:Secret" "<generate-random-256-bit-value>"
dotnet user-secrets set "SignedTicket:DefaultTtlSeconds" "300"
dotnet user-secrets set "SignedTicket:MaxClockSkewSeconds" "30"
dotnet user-secrets set "SignedTicket:EnforceAllowedPaths" "true"
dotnet user-secrets set "SignedTicket:AllowedPaths:0" "/api/v1.0/frame/"
dotnet user-secrets set "SignedTicket:AllowedPaths:1" "/api/v1.0/image/"
```

**appsettings.json (non-sensitive defaults):**

```json
{
  "SignedTicket": {
    "DefaultTtlSeconds": 300,
    "MaxClockSkewSeconds": 30,
    "EnforceAllowedPaths": true,
    "AllowedPaths": [
      "/api/v1.0/frame/",
      "/api/v1.0/image/"
    ]
  }
}
```

**Generate Secret:**

```bash
# Generate 256-bit random secret (Linux/macOS)
openssl rand -base64 32

# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Minimum 0 -Maximum 256 }))
```

**Production Recommendations:**
- Use a cryptographically secure random generator
- Store in Azure Key Vault
- Rotate every 90 days (requires coordination window where old and new secrets are both valid)
- Use different secrets per environment

---

### 4. Data Protection Keys

**Purpose:** ASP.NET Core Data Protection keys for encrypting cookies, tokens, and other sensitive data.

**Current Implementation:** Keys are persisted to file system in `DataProtection-Keys` directory.

**Location:** `{ContentRootPath}/DataProtection-Keys`

**Production Recommendations:**

| Secret Name | Type | Description | Storage Location |
|------------|------|-------------|------------------|
| `DataProtection:KeyVaultUri` | String | Azure Key Vault URI for key storage | Environment Variable / appsettings.json |
| `DataProtection:ApplicationName` | String | Consistent app name for key sharing | Environment Variable / appsettings.json |

**Environment Variables (Production):**

```bash
DATA_PROTECTION_KEY_VAULT_URI=https://your-keyvault.vault.azure.net/
DATA_PROTECTION_APPLICATION_NAME=HVO.SkyMonitor
```

**Production Configuration Update (Required):**

```csharp
// Replace file system persistence with Azure Key Vault
builder.Services.AddDataProtection()
    .PersistKeysToAzureBlobStorage(new Uri("..."))
    .ProtectKeysWithAzureKeyVault(new Uri("..."), new DefaultAzureCredential())
    .SetApplicationName("HVO.SkyMonitor");
```

**Development:** File system persistence is acceptable (already configured).

---

### 5. TLS/HTTPS Certificates

**Purpose:** Secure HTTPS communication for production deployments.

**Development:** Uses Kestrel development certificate (auto-generated).

**Production Configuration:**

| Secret Name | Type | Description | Storage Location |
|------------|------|-------------|------------------|
| `Kestrel:Certificates:Default:Path` | String | Path to HTTPS certificate (.pfx) | File System / Azure Key Vault |
| `Kestrel:Certificates:Default:Password` | Secret | Certificate password | Azure Key Vault |

**Environment Variables:**

```bash
# Production HTTPS configuration
ASPNETCORE_HTTPS_PORT=443
KESTREL_CERTIFICATES_DEFAULT_PATH=/path/to/cert.pfx
KESTREL_CERTIFICATES_DEFAULT_PASSWORD=<cert-password>
```

**appsettings.Production.json:**

```json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://*:443",
        "Certificate": {
          "Path": "${KESTREL_CERTIFICATES_DEFAULT_PATH}",
          "Password": "${KESTREL_CERTIFICATES_DEFAULT_PASSWORD}"
        }
      }
    }
  }
}
```

**Production Recommendations:**
- Use certificates from trusted CA (Let's Encrypt, DigiCert, etc.)
- Automate renewal (Let's Encrypt via Certbot)
- Store certificates in Azure Key Vault
- Use Azure Application Gateway or Azure Front Door for TLS termination

---

### 6. Rate Limiting Configuration

**Purpose:** Protect sensitive endpoints from abuse and denial-of-service attacks.

**Configuration (Non-Sensitive):**

```bash
# Rate limiting thresholds (non-sensitive, can be in appsettings.json)
RATE_LIMIT_TOKEN_ENDPOINT_PERMITS_PER_MINUTE=60
RATE_LIMIT_TOKEN_ENDPOINT_QUEUE_LIMIT=10
RATE_LIMIT_API_ENDPOINT_PERMITS_PER_MINUTE=1000
RATE_LIMIT_GLOBAL_PERMITS_PER_MINUTE=10000
```

**appsettings.json:**

```json
{
  "RateLimiting": {
    "TokenEndpoint": {
      "PermitsPerMinute": 60,
      "QueueLimit": 10
    },
    "ApiEndpoint": {
      "PermitsPerMinute": 1000
    },
    "Global": {
      "PermitsPerMinute": 10000
    }
  }
}
```

**No secrets required** - all values are operational thresholds, not sensitive.

---

## Environment Setup Summary

### Development Environment

**Required:**
- Nothing! Development uses ephemeral keys and defaults.

**Optional (for production-like testing):**
```bash
cd src/HVO.SkyMonitor
dotnet user-secrets set "SignedTicket:Secret" "$(openssl rand -base64 32)"
```

### DevContainer Setup

**`.devcontainer/devcontainer.json`** - Non-sensitive defaults only:

```jsonc
{
  "containerEnv": {
    "ASPNETCORE_ENVIRONMENT": "Development",
    "DOTNET_ENVIRONMENT": "Development",
    "USE_CONTAINERS": "true",
    "ASPIRE_ALLOW_UNSECURED_TRANSPORT": "true"
  }
}
```

**Secrets:** Automatically loaded from user secrets directory (mounted from host).

### CI/CD (GitHub Actions)

**Required Repository Secrets:**

```
# Database credentials
POSTGRES_PASSWORD_DEV
POSTGRES_PASSWORD_PROD

# Object storage credentials  
MINIO_ROOT_USER_DEV
MINIO_ROOT_PASSWORD_DEV
MINIO_ROOT_USER_PROD
MINIO_ROOT_PASSWORD_PROD

# Optional: Production certificates and keys
OPENIDDICT_SIGNING_CERT_PASSWORD_PROD
OPENIDDICT_ENCRYPTION_CERT_PASSWORD_PROD
SIGNED_TICKET_SECRET_PROD
API_KEY_HASHING_SALT_PROD

# Deployment credentials
AZURE_CREDENTIALS
DOCKER_USERNAME
DOCKER_PASSWORD
```

**GitHub Actions Workflow Example:**

```yaml
- name: Run tests
  env:
    POSTGRES_PASSWORD: ${{ secrets.POSTGRES_PASSWORD_DEV }}
    MINIO_ROOT_USER: ${{ secrets.MINIO_ROOT_USER_DEV }}
    MINIO_ROOT_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD_DEV }}
  run: dotnet test
```

### Production Environment

**Azure Key Vault Integration:**

```bash
# Set Key Vault reference in environment
KEY_VAULT_NAME=hvo-skymonitor-prod
AZURE_CLIENT_ID=<service-principal-id>
AZURE_TENANT_ID=<tenant-id>
AZURE_CLIENT_SECRET=<service-principal-secret>
```

**Key Vault Secrets:**

```bash
# OpenIddict certificates
az keyvault certificate import --vault-name hvo-skymonitor-prod \
  --name openiddict-signing-cert --file signing-cert.pfx

az keyvault certificate import --vault-name hvo-skymonitor-prod \
  --name openiddict-encryption-cert --file encryption-cert.pfx

# HMAC secrets
az keyvault secret set --vault-name hvo-skymonitor-prod \
  --name SignedTicket--Secret --value "$(openssl rand -base64 32)"

az keyvault secret set --vault-name hvo-skymonitor-prod \
  --name ApiKey--HashingSalt --value "$(openssl rand -base64 32)"

# Database credentials
az keyvault secret set --vault-name hvo-skymonitor-prod \
  --name PostgreSQL--Password --value "<secure-password>"

# MinIO credentials
az keyvault secret set --vault-name hvo-skymonitor-prod \
  --name MinIO--RootUser --value "<admin-username>"

az keyvault secret set --vault-name hvo-skymonitor-prod \
  --name MinIO--RootPassword --value "<secure-password>"
```

---

## Security Best Practices

### Secret Generation

1. **Use cryptographically secure random generators**
   ```bash
   # Good
   openssl rand -base64 32
   
   # Bad
   echo "mypassword123"
   ```

2. **Minimum key lengths:**
   - HMAC secrets: 256 bits (32 bytes)
   - Passwords: 16+ characters with complexity
   - RSA keys: 4096 bits
   - Certificates: RSA 4096 or ECDSA P-384

3. **Different secrets per environment:**
   - Development, Staging, Production must have different secrets
   - Never reuse production secrets in lower environments

### Secret Storage

1. **Development:** User Secrets (already configured)
2. **Production:** Azure Key Vault (recommended)
3. **Never commit secrets to source control**
4. **Use `.env` for local overrides (git-ignored)**

### Secret Rotation

**Rotation Schedule:**
- OpenIddict certificates: Every 12 months
- HMAC secrets: Every 90 days
- Passwords: Every 90 days
- API keys: On-demand or per security policy

**Rotation Procedure:**
1. Generate new secret
2. Add new secret alongside old (overlap period)
3. Update applications to accept both old and new
4. Wait for propagation (24-48 hours)
5. Remove old secret
6. Verify applications use new secret
7. Audit logs for old secret usage

### Monitoring & Auditing

1. **Monitor secret access** in Azure Key Vault
2. **Log authentication failures** (already implemented)
3. **Alert on unusual patterns** (high failure rates, unknown clients)
4. **Audit secret rotation events**
5. **Track secret age** and alert before expiration

---

## Troubleshooting

### "SignedTicketOptions.Secret must be configured"

**Error:** Service throws exception on startup.

**Solution:**
```bash
# Set the secret
cd src/HVO.SkyMonitor
dotnet user-secrets set "SignedTicket:Secret" "$(openssl rand -base64 32)"
```

### "OpenIddict certificates not found in production"

**Error:** Production deployment fails to start.

**Solution:**
1. Verify certificate files exist at configured paths
2. Check file permissions (app must have read access)
3. Verify certificate passwords are correct
4. Check Key Vault access policies (if using Azure Key Vault)

### Development certificates expired

**Error:** HTTPS development certificate is not trusted.

**Solution:**
```bash
# Clean old certificates
dotnet dev-certs https --clean

# Generate new certificate and trust it
dotnet dev-certs https --trust
```

### User Secrets not found

**Error:** Application can't find user secrets.

**Solution:**
```bash
# Verify UserSecretsId is set in .csproj
grep UserSecretsId src/HVO.SkyMonitor/HVO.SkyMonitor.csproj

# List all secrets to verify they're set
cd src/HVO.SkyMonitor
dotnet user-secrets list

# Re-initialize if needed
dotnet user-secrets init
```

---

## Configuration Validation

**Startup Validation Checklist:**

- [ ] All required secrets are configured
- [ ] Secrets meet minimum length requirements
- [ ] Certificates are valid and not expired
- [ ] Key Vault access is configured (production)
- [ ] Data Protection keys are accessible
- [ ] Rate limiting configuration is present
- [ ] Logging configuration is correct
- [ ] Metrics exporters are configured

**Validation Code (recommended addition):**

```csharp
// Add to Program.cs startup
var signedTicketOptions = app.Services.GetRequiredService<IOptions<SignedTicketOptions>>().Value;
if (signedTicketOptions.Secret.Length < 32)
{
    throw new InvalidOperationException("SignedTicket:Secret must be at least 32 bytes");
}
```

---

## Additional Resources

- [ASP.NET Core Data Protection](https://learn.microsoft.com/aspnet/core/security/data-protection/)
- [Azure Key Vault Configuration Provider](https://learn.microsoft.com/azure/key-vault/general/tutorial-net-create-vault-azure-web-app)
- [OpenIddict Documentation](https://documentation.openiddict.com/)
- [Let's Encrypt - Free TLS Certificates](https://letsencrypt.org/)
- [OWASP Key Management Cheat Sheet](https://cheatsheetseries.owasp.org/cheatsheets/Key_Management_Cheat_Sheet.html)

---

## Summary

Identity Hardening introduces several secrets and configuration values:

**Secrets (must be stored securely):**
1. OpenIddict signing/encryption certificate passwords (production only)
2. Signed URL HMAC secret (required if using signed URLs)
3. API key hashing salt (optional enhancement)
4. HTTPS certificate password (production only)

**Non-sensitive configuration:**
1. Rate limiting thresholds
2. Signed URL TTL and allowed paths
3. Certificate file paths
4. Application names and identifiers

**All secrets are documented for:**
- ✅ Development setup (User Secrets)
- ✅ DevContainer configuration
- ✅ CI/CD (GitHub Secrets)
- ✅ Production deployment (Azure Key Vault)

Follow the security best practices and rotation procedures to maintain a secure production environment.
