# GitHub Actions Secrets Configuration

## Overview

This document describes all secrets required for CI/CD workflows in the HVO.SkyMonitor project.

**Identity Hardening Enhancement:** For comprehensive Identity Hardening-specific secrets (OpenIddict keys, signed URL HMAC, etc.), see [../docs/security/secrets.md](../docs/security/secrets.md).

## Required Repository Secrets

Configure these secrets in your GitHub repository for CI/CD workflows:

**Repository Settings → Secrets and variables → Actions → New repository secret**

### Infrastructure Secrets

#### Development/Staging Environment

```
Name: MINIO_ROOT_USER_DEV
Value: <your-dev-minio-username>
Description: MinIO admin username for development/CI testing

Name: MINIO_ROOT_PASSWORD_DEV
Value: <your-dev-minio-password>
Description: MinIO admin password for development/CI testing

Name: POSTGRES_PASSWORD_DEV
Value: <your-dev-postgres-password>
Description: PostgreSQL admin password for development/CI testing
```

#### Production Environment

```
Name: MINIO_ROOT_USER_PROD
Value: <your-prod-minio-username>
Description: MinIO admin username for production

Name: MINIO_ROOT_PASSWORD_PROD
Value: <your-prod-minio-password>
Description: MinIO admin password for production

Name: POSTGRES_PASSWORD_PROD
Value: <your-prod-postgres-password>
Description: PostgreSQL admin password for production
```

---

### Identity Hardening - Authentication & Security Secrets

#### OpenIddict Certificates (Production Only)

```
Name: OPENIDDICT_SIGNING_CERT_PASSWORD_PROD
Value: <signing-cert-password>
Description: Password for OpenIddict signing certificate (.pfx)

Name: OPENIDDICT_ENCRYPTION_CERT_PASSWORD_PROD
Value: <encryption-cert-password>
Description: Password for OpenIddict encryption certificate (.pfx)

# Note: Certificate files (.pfx) should be stored in Azure Key Vault
# or as base64-encoded secrets for CI/CD deployment
Name: OPENIDDICT_SIGNING_CERT_BASE64_PROD
Value: <base64-encoded-pfx-file>
Description: Base64-encoded OpenIddict signing certificate for deployment

Name: OPENIDDICT_ENCRYPTION_CERT_BASE64_PROD
Value: <base64-encoded-pfx-file>
Description: Base64-encoded OpenIddict encryption certificate for deployment
```

**How to encode certificate for GitHub Secrets:**
```bash
# Encode .pfx file to base64
base64 -w 0 signing-cert.pfx > signing-cert.base64

# In GitHub workflow, decode and use
echo "${{ secrets.OPENIDDICT_SIGNING_CERT_BASE64_PROD }}" | base64 -d > /tmp/signing-cert.pfx
```

#### Signed URL HMAC Secret (Production)

```
Name: SIGNED_TICKET_SECRET_PROD
Value: <random-256-bit-base64-value>
Description: HMAC-SHA256 secret for signed URL generation (min 32 bytes)

# Generate with:
# openssl rand -base64 32
```

#### API Key Hashing Salt (Production - Optional)

```
Name: API_KEY_HASHING_SALT_PROD
Value: <random-256-bit-base64-value>
Description: Salt for API key hashing (optional enhancement)

# Generate with:
# openssl rand -base64 32
```

---

### Container Registry Secrets

If pushing to Docker Hub or other registry:

```
Name: DOCKER_USERNAME
Value: <your-docker-username>
Description: Docker Hub username for image publishing

Name: DOCKER_PASSWORD
Value: <your-docker-password-or-token>
Description: Docker Hub password or access token (prefer token)
```

---

### Azure Deployment Secrets

If deploying to Azure:

```
Name: AZURE_CREDENTIALS
Value: <azure-service-principal-json>
Description: Azure service principal for deployments
Format: {
  "clientId": "<client-id>",
  "clientSecret": "<client-secret>",
  "subscriptionId": "<subscription-id>",
  "tenantId": "<tenant-id>"
}

Name: AZURE_KEYVAULT_NAME
Value: <your-keyvault-name>
Description: Azure Key Vault name for secret retrieval

Name: AZURE_RESOURCE_GROUP
Value: <resource-group-name>
Description: Azure resource group for deployments

Name: AZURE_CONTAINER_REGISTRY
Value: <acr-name>.azurecr.io
Description: Azure Container Registry for images
```

---

### Identity Hardening - TLS/HTTPS Certificates (Production)

```
Name: HTTPS_CERT_PASSWORD_PROD
Value: <https-cert-password>
Description: Password for HTTPS certificate (.pfx)

Name: HTTPS_CERT_BASE64_PROD
Value: <base64-encoded-pfx-file>
Description: Base64-encoded HTTPS certificate for deployment
```

---

## Secrets Summary by Environment

### Development/CI Environment (8 secrets)

1. `MINIO_ROOT_USER_DEV` - MinIO username
2. `MINIO_ROOT_PASSWORD_DEV` - MinIO password
3. `POSTGRES_PASSWORD_DEV` - PostgreSQL password
4. `DOCKER_USERNAME` - Docker Hub username (optional)
5. `DOCKER_PASSWORD` - Docker Hub password (optional)
6. `SIGNED_TICKET_SECRET_DEV` - Signed URL secret for CI tests (optional)
7. `OPENIDDICT_SIGNING_CERT_PASSWORD_DEV` - For integration tests (optional)
8. `OPENIDDICT_ENCRYPTION_CERT_PASSWORD_DEV` - For integration tests (optional)

### Production Environment (12+ secrets)

1. `MINIO_ROOT_USER_PROD` - MinIO username
2. `MINIO_ROOT_PASSWORD_PROD` - MinIO password
3. `POSTGRES_PASSWORD_PROD` - PostgreSQL password
4. `OPENIDDICT_SIGNING_CERT_PASSWORD_PROD` - Signing cert password
5. `OPENIDDICT_SIGNING_CERT_BASE64_PROD` - Signing cert file
6. `OPENIDDICT_ENCRYPTION_CERT_PASSWORD_PROD` - Encryption cert password
7. `OPENIDDICT_ENCRYPTION_CERT_BASE64_PROD` - Encryption cert file
8. `SIGNED_TICKET_SECRET_PROD` - Signed URL HMAC secret
9. `API_KEY_HASHING_SALT_PROD` - API key salt (optional)
10. `HTTPS_CERT_PASSWORD_PROD` - HTTPS cert password
11. `HTTPS_CERT_BASE64_PROD` - HTTPS cert file
12. `AZURE_CREDENTIALS` - Azure deployment credentials

---

## Example Workflow Usage

### Basic Build Workflow

```yaml
name: Build and Test

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      
      - name: Restore dependencies
        run: dotnet restore
      
      - name: Build
        run: dotnet build --no-restore --configuration Release
      
      - name: Test
        env:
          # Infrastructure secrets for integration tests
          MINIO_ROOT_USER: ${{ secrets.MINIO_ROOT_USER_DEV }}
          MINIO_ROOT_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD_DEV }}
          POSTGRES_PASSWORD: ${{ secrets.POSTGRES_PASSWORD_DEV }}
          # Identity Hardening secrets for auth integration tests
          SIGNED_TICKET_SECRET: ${{ secrets.SIGNED_TICKET_SECRET_DEV }}
        run: dotnet test --no-build --configuration Release
```

### Build and Push Container Images

```yaml
name: Build Container Images

on:
  push:
    branches: [ main ]
    tags: [ 'v*' ]

jobs:
  build-images:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3
      
      - name: Log in to Docker Hub
        uses: docker/login-action@v3
        with:
          username: ${{ secrets.DOCKER_USERNAME }}
          password: ${{ secrets.DOCKER_PASSWORD }}
      
      - name: Build and push SkyMonitor
        uses: docker/build-push-action@v5
        with:
          context: .
          file: src/HVO.SkyMonitor.LogicHost/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ secrets.DOCKER_USERNAME }}/hvo-skymonitor:latest
            ${{ secrets.DOCKER_USERNAME }}/hvo-skymonitor:${{ github.sha }}
      
      - name: Build and push Camera Agent
        uses: docker/build-push-action@v5
        with:
          context: .
          file: src/HVO.SkyMonitor.CameraAgent/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent:latest
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent:${{ github.sha }}
      
      
```

### Deploy with Environment Secrets (Identity Hardening Enhanced)

```yaml
name: Deploy to Production

on:
  release:
    types: [published]

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Prepare certificates
        run: |
          # Decode OpenIddict certificates from base64
          echo "${{ secrets.OPENIDDICT_SIGNING_CERT_BASE64_PROD }}" | base64 -d > /tmp/signing-cert.pfx
          echo "${{ secrets.OPENIDDICT_ENCRYPTION_CERT_BASE64_PROD }}" | base64 -d > /tmp/encryption-cert.pfx
          echo "${{ secrets.HTTPS_CERT_BASE64_PROD }}" | base64 -d > /tmp/https-cert.pfx
      
      - name: Deploy to Azure
        env:
          # Infrastructure secrets
          MINIO_ROOT_USER: ${{ secrets.MINIO_ROOT_USER_PROD }}
          MINIO_ROOT_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD_PROD }}
          POSTGRES_PASSWORD: ${{ secrets.POSTGRES_PASSWORD_PROD }}
          # Identity Hardening authentication secrets
          OPENIDDICT_SIGNING_CERT_PATH: /tmp/signing-cert.pfx
          OPENIDDICT_SIGNING_CERT_PASSWORD: ${{ secrets.OPENIDDICT_SIGNING_CERT_PASSWORD_PROD }}
          OPENIDDICT_ENCRYPTION_CERT_PATH: /tmp/encryption-cert.pfx
          OPENIDDICT_ENCRYPTION_CERT_PASSWORD: ${{ secrets.OPENIDDICT_ENCRYPTION_CERT_PASSWORD_PROD }}
          SIGNED_TICKET_SECRET: ${{ secrets.SIGNED_TICKET_SECRET_PROD }}
          API_KEY_HASHING_SALT: ${{ secrets.API_KEY_HASHING_SALT_PROD }}
          # Identity Hardening TLS/HTTPS secrets
          KESTREL_CERTIFICATES_DEFAULT_PATH: /tmp/https-cert.pfx
          KESTREL_CERTIFICATES_DEFAULT_PASSWORD: ${{ secrets.HTTPS_CERT_PASSWORD_PROD }}
          # Azure credentials
          AZURE_CREDENTIALS: ${{ secrets.AZURE_CREDENTIALS }}
        run: |
          # Your deployment script here
          # Secrets are available as environment variables
          echo "Deploying with secure credentials..."
          ./deploy.sh production
```

---

## Environment-Specific Secrets

Use GitHub Environments for different deployment stages:

1. Go to **Repository Settings → Environments**
2. Create environments: `development`, `staging`, `production`
3. Add environment-specific secrets to each
4. Require approvals for production deployments

Example workflow with environments:

```yaml
jobs:
  deploy-staging:
    runs-on: ubuntu-latest
    environment: staging
    steps:
      - name: Deploy
        env:
          MINIO_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD }}
          SIGNED_TICKET_SECRET: ${{ secrets.SIGNED_TICKET_SECRET }}
        run: ./deploy.sh staging

  deploy-production:
    runs-on: ubuntu-latest
    environment: production
    needs: deploy-staging
    steps:
      - name: Deploy
        env:
          MINIO_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD }}
          SIGNED_TICKET_SECRET: ${{ secrets.SIGNED_TICKET_SECRET }}
        run: ./deploy.sh production
```

---

## Security Best Practices

### General

1. **Rotate secrets regularly**
   - Infrastructure secrets: Every 90 days
   - OpenIddict certificates: Every 12 months
   - Signed URL HMAC: Every 90 days
   - API key hashing salt: Annually or on breach

2. **Use environment protection rules**
   - Require approvals for production deployments
   - Limit who can approve production deployments
   - Add reviewers for sensitive changes

3. **Limit secret access**
   - Use fine-grained permissions
   - Only grant access to necessary workflows
   - Audit secret usage regularly

4. **Audit secret usage**
   - Review Actions logs regularly
   - Monitor for unexpected secret access
   - Set up alerts for sensitive operations

5. **Never log secrets**
   - Secrets are automatically masked in logs by GitHub
   - Avoid printing environment variables
   - Use `echo "::add-mask::$SECRET"` for additional masking

6. **Use short-lived credentials**
   - Prefer Azure Managed Identities or OIDC
   - Use GitHub OIDC with Azure for keyless authentication
   - Minimize secret lifetime

### Identity Hardening Specific

7. **Certificate management**
   - Store certificates in Azure Key Vault (production)
   - Use base64 encoding only for CI/CD (not long-term storage)
   - Rotate before expiration
   - Monitor certificate expiration

8. **HMAC secrets**
   - Use cryptographically secure random generators
   - Minimum 256 bits (32 bytes)
   - Different secrets per environment
   - Plan rotation windows for overlap

9. **Validate secrets on deployment**
   - Check minimum length requirements
   - Verify certificates are not expired
   - Test authentication before full deployment

---

## Verifying Secrets

To verify secrets are set correctly without exposing them:

```yaml
- name: Verify infrastructure secrets exist
  run: |
    if [ -z "${{ secrets.MINIO_ROOT_USER_DEV }}" ]; then
      echo "ERROR: MINIO_ROOT_USER_DEV secret is not set"
      exit 1
    fi
    if [ -z "${{ secrets.POSTGRES_PASSWORD_DEV }}" ]; then
      echo "ERROR: POSTGRES_PASSWORD_DEV secret is not set"
      exit 1
    fi
    echo "✅ All required infrastructure secrets are set"

- name: Verify Identity Hardening production secrets exist
  if: github.ref == 'refs/heads/main'
  run: |
    if [ -z "${{ secrets.OPENIDDICT_SIGNING_CERT_PASSWORD_PROD }}" ]; then
      echo "ERROR: OPENIDDICT_SIGNING_CERT_PASSWORD_PROD secret is not set"
      exit 1
    fi
    if [ -z "${{ secrets.SIGNED_TICKET_SECRET_PROD }}" ]; then
      echo "ERROR: SIGNED_TICKET_SECRET_PROD secret is not set"
      exit 1
    fi
    echo "✅ All required Identity Hardening production secrets are set"

- name: Verify secret minimum lengths
  run: |
    # Check SIGNED_TICKET_SECRET is at least 32 bytes (base64 encoded = 44+ chars)
    SECRET_LENGTH=${#SIGNED_TICKET_SECRET}
    if [ $SECRET_LENGTH -lt 44 ]; then
      echo "ERROR: SIGNED_TICKET_SECRET is too short (min 44 chars for 256 bits)"
      exit 1
    fi
    echo "✅ Secret length validation passed"
  env:
    SIGNED_TICKET_SECRET: ${{ secrets.SIGNED_TICKET_SECRET_PROD }}
```

---

## Secret Rotation in CI/CD

When rotating secrets:

1. **Update secret in GitHub**
   - Settings → Secrets → Edit secret
   - Paste new value
   - Save

2. **Deploy with new secret**
   - CI/CD will automatically use new secret
   - No code changes needed

3. **Verify deployment**
   - Check logs for successful authentication
   - Monitor metrics for errors
   - Test endpoints manually

4. **Emergency rollback**
   - Update GitHub secret back to old value
   - Trigger redeployment
   - Investigate issues

---

## Troubleshooting

### "Secret not found" errors

**Symptom:** Workflow fails with empty environment variable

**Solution:**
1. Verify secret name matches exactly (case-sensitive)
2. Check secret is set in correct environment
3. Ensure workflow has access to environment
4. Verify repository permissions

### Certificate decoding failures

**Symptom:** Base64 decode fails or certificate is invalid

**Solution:**
```bash
# Re-encode certificate correctly
base64 -w 0 cert.pfx > cert.base64

# Verify encoding
base64 -d cert.base64 | openssl pkcs12 -info -nodes -passin pass:PASSWORD
```

### Secret value too large

**Symptom:** GitHub rejects secret (max 64 KB)

**Solution:**
- Store large files (certificates) in Azure Key Vault
- Reference from Key Vault in workflow
- Use managed storage for artifacts

---

## Additional Resources

- [GitHub Actions Secrets Documentation](https://docs.github.com/actions/security-guides/encrypted-secrets)
- [GitHub Environments Documentation](https://docs.github.com/actions/deployment/targeting-different-environments/using-environments-for-deployment)
- [Security Hardening for GitHub Actions](https://docs.github.com/actions/security-guides/security-hardening-for-github-actions)
- [Identity Hardening Secrets Guide](../docs/security/secrets.md)
- [Identity Hardening Operational Runbooks](../docs/identity/operations-runbook.md)

---

## Summary

### Required Secrets for Basic CI/CD (3)
1. `MINIO_ROOT_PASSWORD_DEV`
2. `POSTGRES_PASSWORD_DEV`
3. `SIGNED_TICKET_SECRET_DEV` (for auth tests)

### Required Secrets for Production (8+)
1. `MINIO_ROOT_PASSWORD_PROD`
2. `POSTGRES_PASSWORD_PROD`
3. `OPENIDDICT_SIGNING_CERT_PASSWORD_PROD`
4. `OPENIDDICT_SIGNING_CERT_BASE64_PROD`
5. `OPENIDDICT_ENCRYPTION_CERT_PASSWORD_PROD`
6. `OPENIDDICT_ENCRYPTION_CERT_BASE64_PROD`
7. `SIGNED_TICKET_SECRET_PROD`
8. `HTTPS_CERT_PASSWORD_PROD`
9. Plus Azure credentials if deploying to Azure

All Identity Hardening secrets are documented with:
- ✅ Generation procedures
- ✅ Minimum requirements
- ✅ Rotation schedules
- ✅ Usage examples
- ✅ Validation procedures

## Example Workflow Usage

### Basic Build Workflow

```yaml
name: Build and Test

on:
  push:
    branches: [ main, develop ]
  pull_request:
    branches: [ main ]

jobs:
  build:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      
      - name: Restore dependencies
        run: dotnet restore
      
      - name: Build
        run: dotnet build --no-restore --configuration Release
      
      - name: Test
        run: dotnet test --no-build --configuration Release
```

### Build and Push Container Images

```yaml
name: Build Container Images

on:
  push:
    branches: [ main ]
    tags: [ 'v*' ]

jobs:
  build-images:
    runs-on: ubuntu-latest
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v3
      
      - name: Log in to Docker Hub
        uses: docker/login-action@v3
        with:
          username: ${{ secrets.DOCKER_USERNAME }}
          password: ${{ secrets.DOCKER_PASSWORD }}
      
      - name: Build and push SkyMonitor
        uses: docker/build-push-action@v5
        with:
          context: .
          file: src/HVO.SkyMonitor.LogicHost/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ secrets.DOCKER_USERNAME }}/hvo-skymonitor:latest
            ${{ secrets.DOCKER_USERNAME }}/hvo-skymonitor:${{ github.sha }}
      
      - name: Build and push Camera Agent
        uses: docker/build-push-action@v5
        with:
          context: .
          file: src/HVO.SkyMonitor.CameraAgent/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent:latest
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent:${{ github.sha }}
      
      
```

### Deploy with Environment Secrets

```yaml
name: Deploy to Production

on:
  release:
    types: [published]

jobs:
  deploy:
    runs-on: ubuntu-latest
    environment: production
    
    steps:
      - uses: actions/checkout@v4
      
      - name: Deploy to Azure
        env:
          MINIO_ROOT_USER: ${{ secrets.MINIO_ROOT_USER_PROD }}
          MINIO_ROOT_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD_PROD }}
          POSTGRES_PASSWORD: ${{ secrets.POSTGRES_PASSWORD_PROD }}
        run: |
          # Your deployment script here
          # Secrets are available as environment variables
          echo "Deploying with secure credentials..."
```

## Environment-Specific Secrets

Use GitHub Environments for different deployment stages:

1. Go to **Repository Settings → Environments**
2. Create environments: `development`, `staging`, `production`
3. Add environment-specific secrets to each
4. Require approvals for production deployments

Example workflow with environments:

```yaml
jobs:
  deploy-staging:
    runs-on: ubuntu-latest
    environment: staging
    steps:
      - name: Deploy
        env:
          MINIO_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD }}
        run: ./deploy.sh staging

  deploy-production:
    runs-on: ubuntu-latest
    environment: production
    needs: deploy-staging
    steps:
      - name: Deploy
        env:
          MINIO_PASSWORD: ${{ secrets.MINIO_ROOT_PASSWORD }}
        run: ./deploy.sh production
```

## Security Best Practices

1. **Rotate secrets regularly** - Update in GitHub Secrets and redeploy
2. **Use environment protection rules** - Require approvals for production
3. **Limit secret access** - Use fine-grained permissions
4. **Audit secret usage** - Review Actions logs regularly
5. **Never log secrets** - Secrets are automatically masked in logs, but avoid printing them
6. **Use short-lived credentials** - Prefer Azure Managed Identities or OIDC

## Verifying Secrets

To verify secrets are set correctly without exposing them:

```yaml
- name: Verify secrets exist
  run: |
    if [ -z "${{ secrets.MINIO_ROOT_USER }}" ]; then
      echo "ERROR: MINIO_ROOT_USER secret is not set"
      exit 1
    fi
    echo "All required secrets are set"
```

## Additional Resources

- [GitHub Actions Secrets Documentation](https://docs.github.com/actions/security-guides/encrypted-secrets)
- [GitHub Environments Documentation](https://docs.github.com/actions/deployment/targeting-different-environments/using-environments-for-deployment)
- [Security Hardening for GitHub Actions](https://docs.github.com/actions/security-guides/security-hardening-for-github-actions)
