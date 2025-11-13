# GitHub Actions Secrets Configuration

## Required Repository Secrets

Configure these secrets in your GitHub repository for CI/CD workflows:

**Repository Settings → Secrets and variables → Actions → New repository secret**

### Development/Staging Environment

```
Name: MINIO_ROOT_USER_DEV
Value: <your-dev-minio-username>

Name: MINIO_ROOT_PASSWORD_DEV
Value: <your-dev-minio-password>

Name: POSTGRES_PASSWORD_DEV
Value: <your-dev-postgres-password>
```

### Production Environment

```
Name: MINIO_ROOT_USER_PROD
Value: <your-prod-minio-username>

Name: MINIO_ROOT_PASSWORD_PROD
Value: <your-prod-minio-password>

Name: POSTGRES_PASSWORD_PROD
Value: <your-prod-postgres-password>
```

### Optional: Container Registry

If pushing to Docker Hub or other registry:

```
Name: DOCKER_USERNAME
Value: <your-docker-username>

Name: DOCKER_PASSWORD
Value: <your-docker-password-or-token>
```

### Optional: Azure Deployment

If deploying to Azure:

```
Name: AZURE_CREDENTIALS
Value: <azure-service-principal-json>

Name: AZURE_KEYVAULT_NAME
Value: <your-keyvault-name>
```

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
          file: src/HVO.SkyMonitor/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ secrets.DOCKER_USERNAME }}/hvo-skymonitor:latest
            ${{ secrets.DOCKER_USERNAME }}/hvo-skymonitor:${{ github.sha }}
      
      - name: Build and push Simulator Agent
        uses: docker/build-push-action@v5
        with:
          context: .
          file: src/HVO.SkyMonitor.CameraAgent.Simulator/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: |
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent-simulator:latest
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent-simulator:${{ github.sha }}
      
      - name: Build and push ZWO Agent
        uses: docker/build-push-action@v5
        with:
          context: .
          file: src/HVO.SkyMonitor.CameraAgent.ZWO/Dockerfile
          platforms: linux/amd64,linux/arm64,linux/arm/v7
          push: true
          tags: |
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent-zwo:latest
            ${{ secrets.DOCKER_USERNAME }}/hvo-cameraagent-zwo:${{ github.sha }}
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
