# Phase 6 Documentation Index

This document provides a quick reference to all Phase 6 (Hardening, Operations & Observability) documentation.

## Overview

Phase 6 focuses on productionizing the Central Identity system with:
- Proper secret storage and management
- Production-grade TLS configuration
- Structured logging and audit trails
- Metrics and observability
- Rate limiting and security controls
- Operational runbooks and procedures

## Documentation Files

### 1. PHASE6_SECRETS.md (17.5 KB)
**Purpose:** Comprehensive guide to all secrets and configuration required for Phase 6.

**Covers:**
- OpenIddict signing/encryption certificates
- Signed URL HMAC secrets
- API key hashing salt
- Data Protection key storage
- TLS/HTTPS certificates
- Rate limiting configuration
- Environment setup (Development, DevContainer, CI/CD, Production)
- Secret generation and rotation procedures
- Security best practices

**Use When:**
- Setting up a new development environment
- Configuring CI/CD pipelines
- Deploying to production
- Rotating secrets
- Troubleshooting configuration issues

### 2. PHASE6_RUNBOOKS.md (24.7 KB)
**Purpose:** Step-by-step operational procedures for common tasks.

**Covers:**
- Key rotation procedures (OpenIddict, HMAC, API keys)
- Account onboarding (USER and SYSTEM accounts)
- API key lifecycle management
- Access revocation (standard and emergency)
- Security incident response
- Troubleshooting 401/403 errors
- Certificate management and renewal
- Monitoring, metrics, and alerting

**Use When:**
- Rotating cryptographic keys
- Onboarding new users or services
- Responding to security incidents
- Troubleshooting authentication issues
- Managing certificates
- Setting up monitoring

### 3. SECRETS_MANAGEMENT.md (Existing, Updated)
**Purpose:** General secrets management guidance for the entire project.

**Covers:**
- Overview of secrets management strategy
- .NET User Secrets setup
- Azure Key Vault integration
- GitHub Secrets configuration
- Configuration priority
- Secret rotation best practices
- Troubleshooting

**Use When:**
- Getting started with the project
- Understanding overall secrets strategy
- Setting up User Secrets
- Configuring Key Vault

### 4. .env.template (Updated)
**Purpose:** Template for local environment variables.

**Covers:**
- All non-sensitive environment variables
- Phase 6 rate limiting configuration
- Phase 6 signed URL configuration
- Logging configuration
- Comments for all secret placeholders

**Use When:**
- Setting up local development environment
- Understanding available configuration options
- Creating .env file for local overrides

### 5. .github/workflows/README.md (Updated)
**Purpose:** GitHub Actions secrets configuration guide.

**Covers:**
- Required repository secrets
- Phase 6 authentication secrets
- Container registry secrets
- Azure deployment secrets
- Example workflows
- Secret verification procedures
- Troubleshooting

**Use When:**
- Setting up CI/CD pipelines
- Configuring GitHub Actions secrets
- Deploying to production via GitHub Actions
- Troubleshooting workflow failures

### 6. .devcontainer/devcontainer.json (Updated)
**Purpose:** Development container configuration.

**Covers:**
- Phase 6 non-sensitive environment variables
- User secrets mounting
- Port forwarding configuration
- VS Code settings

**Use When:**
- Using VS Code Dev Containers
- Setting up remote development environment
- Understanding available ports and services

### 7. central-identity-plan.md (Updated)
**Purpose:** Overall Central Identity Program plan.

**Covers:**
- Complete Phase 0-8 plan
- Phase 6 detailed task breakdown
- Implementation status
- Documentation status

**Use When:**
- Understanding the overall identity program
- Tracking Phase 6 progress
- Planning remaining implementation work

## Quick Reference by Scenario

### Scenario: I'm a new developer setting up my environment

1. Read `SECRETS_MANAGEMENT.md` for overview
2. Copy `.env.template` to `.env` (optional for overrides)
3. Set up User Secrets using `PHASE6_SECRETS.md` (optional for production-like testing)
4. Open project in Dev Container (reads `.devcontainer/devcontainer.json`)
5. Run `dotnet run --project src/HVO.SkyMonitor.AppHost`

**Secrets needed:** None! Development uses defaults and auto-generated certificates.

### Scenario: I'm setting up CI/CD

1. Read `.github/workflows/README.md`
2. Generate required secrets using commands in `PHASE6_SECRETS.md`
3. Add secrets to GitHub repository (Settings → Secrets)
4. Verify secrets using validation workflow examples

**Minimum secrets needed:**
- `MINIO_ROOT_PASSWORD_DEV`
- `POSTGRES_PASSWORD_DEV`

### Scenario: I'm deploying to production

1. Read `PHASE6_SECRETS.md` - Production Environment section
2. Generate all required secrets and certificates
3. Store in Azure Key Vault
4. Configure application to load from Key Vault
5. Set up monitoring using `PHASE6_RUNBOOKS.md` - Monitoring section

**All secrets needed:**
- Infrastructure (MinIO, PostgreSQL)
- Authentication (OpenIddict certs, HMAC secret)
- TLS (HTTPS certificate)
- Azure credentials

### Scenario: I need to rotate a secret

1. Find the secret in `PHASE6_RUNBOOKS.md` - Key Rotation Procedures
2. Follow the step-by-step procedure
3. Verify rotation using validation steps
4. Update monitoring/calendar for next rotation

### Scenario: I'm responding to a security incident

1. Follow `PHASE6_RUNBOOKS.md` - Incident Response section
2. Use emergency access revocation if needed
3. Rotate compromised secrets using key rotation procedures
4. Document incident and follow post-incident procedures

### Scenario: I'm troubleshooting authentication errors

1. Check `PHASE6_RUNBOOKS.md` - Troubleshooting 401/403 section
2. Use diagnostic commands to identify issue
3. Follow solution steps
4. Verify fix

### Scenario: I need to onboard a new user or service

1. For interactive users: `PHASE6_RUNBOOKS.md` - Creating a New User Account
2. For services/agents: `PHASE6_RUNBOOKS.md` - Creating a New System Account
3. Follow post-creation checklist

## Configuration Hierarchy

Understanding how configuration is loaded (in order, later overrides earlier):

1. `appsettings.json` - Non-sensitive defaults
2. `appsettings.{Environment}.json` - Environment-specific defaults
3. User Secrets - Development secrets (dev only)
4. Environment Variables - Runtime configuration
5. Azure Key Vault - Production secrets
6. Command-line arguments - Override everything

**In DevContainer:**
- `.devcontainer/devcontainer.json` → Environment Variables
- User Secrets mounted from host
- `.env` file (if present, via docker-compose)

**In CI/CD:**
- GitHub Secrets → Environment Variables
- Workflow files control flow

**In Production:**
- Azure Key Vault → Configuration Provider
- Environment Variables as fallback
- No User Secrets (not available)

## Secret Categories

### Non-Sensitive (Can commit to git)
- Port numbers
- Rate limiting thresholds
- Signed URL TTL values
- Logging levels
- Feature flags

**Storage:** `appsettings.json`, `.env.template`, `devcontainer.json`

### Sensitive (Never commit to git)
- Passwords
- API keys
- Certificates
- HMAC secrets
- Connection strings with credentials

**Storage:** User Secrets (dev), Azure Key Vault (prod), GitHub Secrets (CI/CD)

## Validation Checklist

Before considering Phase 6 complete:

- [ ] All secrets documented in `PHASE6_SECRETS.md`
- [ ] All procedures documented in `PHASE6_RUNBOOKS.md`
- [ ] `.env.template` updated with all configuration options
- [ ] `.devcontainer/devcontainer.json` updated
- [ ] `.github/workflows/README.md` updated with CI/CD secrets
- [ ] `central-identity-plan.md` updated with progress
- [ ] Development environment works with defaults (no secrets needed)
- [ ] DevContainer configuration tested
- [ ] GitHub Actions secrets configured (for actual CI/CD)
- [ ] Production deployment tested with Key Vault
- [ ] Secret rotation procedures tested in staging
- [ ] Monitoring and alerting configured
- [ ] Incident response procedure practiced
- [ ] All documentation reviewed and accurate

## Key Metrics for Phase 6

**Documentation Quality:**
- ✅ 7 documentation files created/updated
- ✅ 42+ KB of documentation
- ✅ All secrets documented with generation commands
- ✅ All procedures documented with step-by-step instructions
- ✅ Examples provided for all scenarios

**Coverage:**
- ✅ Development environment setup
- ✅ DevContainer configuration
- ✅ CI/CD pipeline configuration
- ✅ Production deployment
- ✅ Operational procedures
- ✅ Incident response
- ✅ Troubleshooting

**Completeness:**
- ✅ Secret management: 100% documented
- ✅ Operational runbooks: 100% documented
- 🔄 TLS configuration: Documented, implementation pending
- 🔄 Structured logging: Partially implemented
- 🔄 Metrics: Infrastructure ready, custom metrics pending
- 🔄 Rate limiting: Documented, implementation pending

## Next Steps for Phase 6

1. **Implement Production TLS Configuration**
   - Add certificate loading from Azure Key Vault
   - Configure Kestrel HTTPS endpoints
   - Test certificate rotation

2. **Enhance Structured Logging**
   - Add login success/failure logging
   - Add OAuth2 token issuance logging
   - Add signed URL validation logging
   - Document log format and queries

3. **Implement Custom Metrics**
   - Add authentication metrics
   - Add rate limit metrics
   - Create Grafana dashboards
   - Document metrics

4. **Implement Rate Limiting**
   - Add ASP.NET Core rate limiting middleware
   - Configure endpoint-specific limits
   - Test behavior under load

5. **Validate in Staging**
   - Test all procedures
   - Practice incident response
   - Verify monitoring
   - Load test

6. **Production Deployment**
   - Deploy with full Phase 6 configuration
   - Monitor metrics
   - Validate procedures
   - Document any deviations

## Support and Questions

For questions about Phase 6 documentation or procedures:

1. Check this index for the relevant documentation
2. Review the specific documentation file
3. Check the troubleshooting sections
4. Refer to the central-identity-plan.md for context

## Summary

Phase 6 documentation is now complete and comprehensive:

- **100% of secrets documented** with generation, storage, and rotation procedures
- **100% of operational procedures documented** with step-by-step runbooks
- **All environments covered:** Development, DevContainer, CI/CD, Production
- **All scenarios covered:** Setup, deployment, operations, troubleshooting, incidents

The remaining work for Phase 6 is implementing the documented features (TLS, enhanced logging, metrics, rate limiting) and validating everything in production.
