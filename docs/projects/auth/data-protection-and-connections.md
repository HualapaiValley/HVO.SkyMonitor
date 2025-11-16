# Data Protection and Connection Strings Documentation

**Date:** 2025-11-14
**Purpose:** Document data protection keys and database connection configuration for central identity system

## Connection Strings

### Development Environment (Docker Compose / Testcontainers)

#### HVO.SkyMonitor (PostgreSQL)
**Managed by:** `docker-compose.dev.yml` + helper scripts in `./scripts/infra:*`
**Configuration:** Explicit environment variables injected into the app container (and mirrored for local `dotnet run` via `.env`/User Secrets)

```yaml
# docker-compose.dev.yml
skymonitor:
    environment:
        ConnectionStrings__skymonitordb: "Host=postgres;Port=5432;Database=${POSTGRES_DB:-skymonitordb};Username=${POSTGRES_USER:-skymonitor};Password=${POSTGRES_PASSWORD:-skymonitor_dev_password}"
```

Within `Program.cs`, `builder.Configuration.GetConnectionString("skymonitordb")` resolves to the same value whether you're running with Compose, Testcontainers, or `dotnet run` locally.

**Effective Connection String (Development):**
```
Host=localhost;Port=5432;Database=skymonitordb;Username=skymonitor;Password=skymonitor_dev_password
```

**How to Access:**
1. Start infrastructure: `./scripts/infra:start postgres`
2. PostgreSQL listens on `${POSTGRES_PORT:-5432}` (default 5432)
3. Database name: `${POSTGRES_DB:-skymonitordb}`
4. Credentials supplied via `.env`, `.devcontainer/devcontainer.local.env`, or User Secrets

#### Camera Agents (SQLite) - TO BE REMOVED
**Simulator Agent:**
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=simulator-agent.db;Cache=Shared"
  }
}
```

**ZWO Agent:**
```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=zwo-agent.db;Cache=Shared"
  }
}
```

**Note:** These will be REMOVED in Phase 4. Camera agents will authenticate to central HVO.SkyMonitor service instead of maintaining local Identity databases.

### Production Environment

**Connection String Format:**
```json
{
  "ConnectionStrings": {
    "skymonitordb": "Host={db-host};Port=5432;Database=skymonitordb;Username={db-user};Password={secret};SSL Mode=Require;Trust Server Certificate=false"
  }
}
```

**Secret Management:**
- **Azure:** Use Azure Key Vault
- **AWS:** Use AWS Secrets Manager
- **On-Premises:** Environment variables or Docker secrets

**Example with Azure Key Vault:**
```csharp
// In Program.cs (production configuration)
if (builder.Environment.IsProduction())
{
    var keyVaultUrl = builder.Configuration["KeyVault:Url"];
    builder.Configuration.AddAzureKeyVault(
        new Uri(keyVaultUrl),
        new DefaultAzureCredential());
}
```

**Example Connection String Retrieval:**
```csharp
var connectionString = builder.Configuration.GetConnectionString("skymonitordb");
// Or from Azure Key Vault:
// var connectionString = builder.Configuration["ConnectionStrings--skymonitordb"];
```

## Data Protection Keys

ASP.NET Core Data Protection is used for:
- Cookie encryption (authentication cookies)
- CSRF token generation and validation
- Temporary data encryption (e.g., password reset tokens)
- State protection in OAuth flows

### Development Environment

**Storage:** PostgreSQL provisioned through Docker Compose/Testcontainers

**Configuration:**
```csharp
// In HVO.SkyMonitor/Program.cs
builder.Services.AddDataProtection()
    .PersistKeysToDbContext<ApplicationDbContext>()
    .SetApplicationName("HVO.SkyMonitor");
```

**Table:** `DataProtectionKeys` (automatically created by EF Core)

**Schema:**
| Column | Type | Description |
|--------|------|-------------|
| Id | int | Primary key |
| FriendlyName | string | Key identifier |
| Xml | string | Encrypted key data |

**Location:** Same PostgreSQL database as Identity (`skymonitordb`)

**Lifecycle:** 
- Keys auto-rotate every 90 days by default
- Old keys retained for decryption (default: 90 day overlap)
- No manual management needed in development

### Production Environment

**Recommended:** Azure Key Vault, AWS KMS, or similar HSM-backed storage

**Azure Key Vault Configuration:**
```csharp
if (builder.Environment.IsProduction())
{
    var keyVaultUrl = builder.Configuration["KeyVault:Url"];
    var keyVaultKeyId = builder.Configuration["KeyVault:DataProtectionKeyId"];
    
    builder.Services.AddDataProtection()
        .PersistKeysToAzureBlobStorage(/* blob storage connection */)
        .ProtectKeysWithAzureKeyVault(new Uri(keyVaultKeyId), new DefaultAzureCredential())
        .SetApplicationName("HVO.SkyMonitor");
}
```

**AWS KMS Configuration:**
```csharp
if (builder.Environment.IsProduction())
{
    builder.Services.AddDataProtection()
        .PersistKeysToAWSSystemsManager(/* parameter store path */)
        .ProtectKeysWithAwsKms(/* KMS key ARN */)
        .SetApplicationName("HVO.SkyMonitor");
}
```

**On-Premises (File System):**
```csharp
if (builder.Environment.IsProduction())
{
    var keyPath = builder.Configuration["DataProtection:KeyPath"];
    
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
        .ProtectKeysWithDpapi() // Windows only
        // Or: .ProtectKeysWithCertificate(cert) // Cross-platform
        .SetApplicationName("HVO.SkyMonitor");
}
```

## OpenIddict Key Management (Phase 2+)

### Signing Keys

**Development:** Auto-generated ephemeral keys (in-memory)
```csharp
services.AddOpenIddict()
    .AddServer(options =>
    {
        if (builder.Environment.IsDevelopment())
        {
            options.AddDevelopmentEncryptionCertificate()
                   .AddDevelopmentSigningCertificate();
        }
    });
```

**Production:** X.509 certificates from Key Vault or certificate store
```csharp
services.AddOpenIddict()
    .AddServer(options =>
    {
        if (builder.Environment.IsProduction())
        {
            options.AddEncryptionCertificate(encryptionCert)
                   .AddSigningCertificate(signingCert);
        }
    });
```

**Certificate Requirements:**
- RSA 2048-bit minimum (RSA 4096-bit recommended)
- Valid for at least 1 year
- Stored securely (Azure Key Vault, AWS Certificate Manager, etc.)
- Rotation strategy: Overlap old and new keys for grace period

### Encryption Keys

**Purpose:** Encrypt refresh tokens, authorization codes

**Development:** Auto-generated (same as signing keys)

**Production:** Separate certificate from signing key
- Different certificate for encryption vs signing
- Allows independent rotation
- Stored in same secure location as signing keys

## API Key Hashing

**Algorithm:** PBKDF2 with SHA256
**Iterations:** 10,000 (configurable)
**Salt:** 128-bit random salt per key
**Output:** Base64-encoded hash

**Implementation:** `HVO.SkyMonitor.Common/Security/ApiKeyHasher.cs`

```csharp
public static string HashApiKey(string apiKey)
{
    byte[] salt = new byte[128 / 8];
    using var rng = RandomNumberGenerator.Create();
    rng.GetBytes(salt);
    
    byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
        Encoding.UTF8.GetBytes(apiKey),
        salt,
        iterations: 10000,
        hashAlgorithm: HashAlgorithmName.SHA256,
        outputLength: 256 / 8);
    
    return $"{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
}
```

**Storage:** PostgreSQL `ApiKeys.HashedKey` column (max length: 128 characters)

**Validation:** Compare hashed incoming key with stored hash

## Signed URL HMAC Keys (Phase 5)

**Algorithm:** HMAC-SHA256
**Key Size:** 256 bits (32 bytes)
**Storage:** PostgreSQL `SignedUrlSecrets` table (encrypted at rest)

**Key Rotation:**
- New key generated monthly (configurable)
- Old key valid for 24 hours after new key created (overlap period)
- Validation tries active key first, then recent expired keys

**Configuration:**
```csharp
builder.Services.Configure<SignedUrlOptions>(options =>
{
    options.DefaultTtl = TimeSpan.FromMinutes(15);
    options.MaxTtl = TimeSpan.FromHours(1);
    options.ClockSkew = TimeSpan.FromMinutes(5);
    options.KeyRotationInterval = TimeSpan.FromDays(30);
});
```

## Database Cleanup Procedures

### Development Environment

**Stop Compose Stack and Clean Containers:**
```bash
docker compose -f docker-compose.dev.yml down
docker volume rm skymonitor-postgres-data skymonitor-minio-data skymonitor-redis-data 2>/dev/null || true
```

**Verify Cleanup:**
```bash
docker ps -a  # Should not list skymonitor-* services
docker volume ls | grep skymonitor  # Optional sanity check
```

**Restart Fresh:**
```bash
./scripts/infra:start postgres minio redis smtp
dotnet run --project src/HVO.SkyMonitor --configuration Debug
```

### Camera Agent SQLite Files

**Location:**
- `src/HVO.SkyMonitor.CameraAgent.Simulator/simulator-agent.db`
- `src/HVO.SkyMonitor.CameraAgent.ZWO/zwo-agent.db`

**Delete:**
```bash
find ./src -name "*.db" -type f -delete
find ./src -name "*.db-shm" -type f -delete
find ./src -name "*.db-wal" -type f -delete
```

**Note:** These files will no longer be created after Phase 4 (camera agents will not have local Identity).

## Migration Application

### Initial Migration (Phase 1)

**Create Migration:**
```bash
# Ensure PostgreSQL is running (Compose or Testcontainers)
./scripts/infra:start postgres

# In another terminal:
dotnet ef migrations add InitialCentralIdentity \
    --project src/HVO.SkyMonitor \
    --context ApplicationDbContext \
    --output-dir Data/Migrations
```

**Apply Migration:**
```bash
# Automatic on app startup (recommended for dev):
# Program.cs already applies pending migrations

# Manual (for production or testing):
dotnet ef database update \
    --project src/HVO.SkyMonitor \
    --context ApplicationDbContext
```

### Subsequent Migrations

**Phase 2 - OpenIddict:**
```bash
dotnet ef migrations add AddOpenIddict \
    --project src/HVO.SkyMonitor \
    --context ApplicationDbContext
```

**Phase 5 - Signed URLs:**
```bash
dotnet ef migrations add AddSignedUrlSecrets \
    --project src/HVO.SkyMonitor \
    --context ApplicationDbContext
```

**Identity Hardening - Audit Logging:**
```bash
dotnet ef migrations add AddSecurityAuditLog \
    --project src/HVO.SkyMonitor \
    --context ApplicationDbContext
```

## Backup and Restore

### Development Backup
```bash
# Backup PostgreSQL from the compose container
docker exec skymonitor-postgres pg_dump -U ${POSTGRES_USER:-skymonitor} ${POSTGRES_DB:-skymonitordb} > backup.sql

# Restore
docker exec -i skymonitor-postgres psql -U ${POSTGRES_USER:-skymonitor} ${POSTGRES_DB:-skymonitordb} < backup.sql
```

### Production Backup
Use PostgreSQL's standard backup tools:
- `pg_dump` for logical backups
- `pg_basebackup` for physical backups
- Consider continuous archiving with WAL shipping

## Environment Variables

### Development (Docker Compose / Testcontainers)
```bash
# Managed via .env and docker-compose.dev.yml
ASPNETCORE_ENVIRONMENT=Development
ConnectionStrings__skymonitordb=Host=localhost;Port=5432;Database=skymonitordb;Username=skymonitor;Password=skymonitor_dev_password
POSTGRES_USER=skymonitor
POSTGRES_PASSWORD=skymonitor_dev_password
```

### Production
```bash
ASPNETCORE_ENVIRONMENT=Production
ConnectionStrings__skymonitordb={from-key-vault}
KeyVault__Url=https://your-vault.vault.azure.net/
OpenIddict__SigningCertificate__Thumbprint={cert-thumbprint}
OpenIddict__EncryptionCertificate__Thumbprint={cert-thumbprint}
```

## Security Best Practices

1. **Never commit connection strings** - Use User Secrets (dev) or Key Vault (prod)
2. **Rotate database passwords** - Quarterly or when personnel changes occur
3. **Rotate OpenIddict certificates** - Annually or per security policy
4. **Monitor data protection key rotation** - Ensure automatic rotation is working
5. **Use SSL/TLS for database connections** - Always in production
6. **Encrypt data at rest** - Enable PostgreSQL encryption (TDE) in production
7. **Audit access to secrets** - Log all Key Vault/secret manager access
8. **Principle of least privilege** - Database user should only have necessary permissions

## Troubleshooting

### "Cannot connect to database" Error
1. Verify the compose stack is running: `./scripts/infra:status`
2. Check PostgreSQL container: `docker ps | grep skymonitor-postgres`
3. Test connection: `docker exec -it skymonitor-postgres psql -U ${POSTGRES_USER:-skymonitor} -d ${POSTGRES_DB:-skymonitordb}`

### "Pending migrations" Warning
1. Ensure database container is healthy
2. Apply migrations: `dotnet ef database update --project src/HVO.SkyMonitor`
3. Or restart application (auto-migration enabled)

### Data Protection Key Errors
1. Verify ApplicationDbContext is registered
2. Check DataProtectionKeys table exists
3. Ensure `PersistKeysToDbContext` is configured
4. Clear browser cookies and retry

## References

- ASP.NET Core Data Protection: https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/
- PostgreSQL Connection Strings: https://www.npgsql.org/doc/connection-string-parameters.html
- OpenIddict Key Management: https://documentation.openiddict.com/configuration/encryption-and-signing-credentials.html
- Azure Key Vault: https://learn.microsoft.com/en-us/azure/key-vault/
