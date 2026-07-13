# Identity Hardening - Operational Runbooks

> **Historical and non-executable:** This document contains PostgreSQL,
> Kubernetes, Azure-only, Redis flush, route, and UI procedures that do not
> match the current SQL Server and Docker Compose repository topology. Do not
> execute commands from this file. Use `docs/runbooks/local-dev.md`,
> `docs/runbooks/infra-operations.md`, and current application routes until the
> issue #120 replaces and validates this
> runbook. This warning is intentionally retained with the historical material
> so unsafe instructions cannot be mistaken for supported operations.

This document provides step-by-step procedures for common operational tasks related to the Central Identity system's security and authentication infrastructure.

## Table of Contents

1. [Key Rotation Procedures](#key-rotation-procedures)
2. [Account Onboarding](#account-onboarding)
3. [API Key Management](#api-key-management)
4. [Access Revocation](#access-revocation)
5. [Incident Response](#incident-response)
6. [Troubleshooting 401/403 Errors](#troubleshooting-401403-errors)
7. [Certificate Management](#certificate-management)
8. [Monitoring & Alerts](#monitoring--alerts)

---

## Key Rotation Procedures

### Overview

Regular key rotation is critical for maintaining security. This section covers rotation procedures for all cryptographic keys used in the Central Identity system.

### 1. OpenIddict Signing Certificate Rotation

**Frequency:** Every 12 months  
**Downtime Required:** No (zero-downtime rotation supported)  
**Estimated Time:** 30 minutes

**Prerequisites:**
- New X.509 certificate (.pfx) with private key
- Certificate password
- Production Key Vault access
- Maintenance window scheduled (off-peak hours recommended)

**Procedure:**

```bash
# Step 1: Generate new signing certificate
openssl req -x509 -newkey rsa:4096 -sha256 -days 365 \
  -nodes -keyout new-signing-key.pem -out new-signing-cert.pem \
  -subj "/CN=HVO.SkyMonitor.Signing/O=HVO Observatory/C=US"

openssl pkcs12 -export -out new-signing-cert.pfx \
  -inkey new-signing-key.pem -in new-signing-cert.pem \
  -password pass:YourSecurePassword

# Step 2: Upload new certificate to Azure Key Vault
az keyvault certificate import \
  --vault-name hvo-skymonitor-prod \
  --name openiddict-signing-cert-new \
  --file new-signing-cert.pfx

# Step 3: Update application configuration to use BOTH old and new certificates
# Edit appsettings.Production.json or environment variables
# OpenIddict supports multiple signing keys for validation

# Step 4: Deploy updated configuration
# This allows tokens signed with OLD cert to still validate
# while NEW tokens will be signed with NEW cert

# Step 5: Wait for token lifetime (30 minutes + buffer = 1 hour)
sleep 3600

# Step 6: Remove old certificate from configuration
# Edit appsettings.Production.json to remove old certificate reference

# Step 7: Deploy configuration update

# Step 8: Delete old certificate from Key Vault (after verification)
az keyvault certificate delete \
  --vault-name hvo-skymonitor-prod \
  --name openiddict-signing-cert-old

# Step 9: Verify new certificate is in use
# Check logs for token issuance events
# Monitor error rates for authentication failures
```

**Rollback Procedure:**

If issues arise after deploying new certificate:
1. Restore old certificate to active configuration
2. Redeploy immediately
3. Investigate issues before retrying rotation

**Post-Rotation Checklist:**
- [ ] New certificate deployed and active
- [ ] Old certificate removed from configuration
- [ ] Authentication metrics show no errors
- [ ] All clients can obtain and validate tokens
- [ ] Certificate expiration monitoring updated

---

### 2. Signed URL HMAC Secret Rotation

**Frequency:** Every 90 days  
**Downtime Required:** No (overlap window supported)  
**Estimated Time:** 15 minutes

**Procedure:**

```bash
# Step 1: Generate new HMAC secret (256-bit minimum)
NEW_SECRET=$(openssl rand -base64 32)

# Step 2: Store new secret in Key Vault with temporary name
az keyvault secret set \
  --vault-name hvo-skymonitor-prod \
  --name SignedTicket--Secret-New \
  --value "$NEW_SECRET"

# Step 3: Update application to accept BOTH old and new secrets
# Modify SignedTicketService to validate with both secrets
# This requires code change to support dual-secret validation

# Step 4: Deploy updated application
# Now accepts signed URLs created with either old or new secret

# Step 5: Wait for overlap period (default TTL + buffer = 10 minutes)
sleep 600

# Step 6: Update ticket generation to use NEW secret only
# Update configuration to point to new secret
az keyvault secret set \
  --vault-name hvo-skymonitor-prod \
  --name SignedTicket--Secret \
  --value "$NEW_SECRET"

# Step 7: Deploy configuration update

# Step 8: Remove old secret after validation period
az keyvault secret delete \
  --vault-name hvo-skymonitor-prod \
  --name SignedTicket--Secret-Old
```

**Validation:**

```bash
# Generate test signed URL with new secret
curl -X POST https://skymonitor.hvo.org/api/v1.0/frame/generate-signed-url \
  -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"path": "/api/v1.0/frame/latest", "ttl": 300}'

# Verify signed URL works
curl "https://skymonitor.hvo.org/api/v1.0/frame/latest?st=<signed-ticket>"
```

**Post-Rotation Checklist:**
- [ ] New secret deployed
- [ ] Signed URL generation working
- [ ] Signed URL validation working
- [ ] Old secret removed
- [ ] No error spikes in logs
- [ ] Next rotation scheduled (90 days)

---

### 3. API Key Hashing Salt Rotation

**Frequency:** Annually or on security incident  
**Downtime Required:** Yes (requires database migration)  
**Estimated Time:** 1-2 hours (depends on number of API keys)

**⚠️ WARNING:** This procedure requires re-hashing ALL existing API keys and is destructive. Coordinate with all API key holders.

**Prerequisites:**
- Database backup completed
- Maintenance window scheduled
- Communication sent to all API key holders
- New API keys ready to distribute

**Procedure:**

```bash
# Step 1: Create database backup
pg_dump skymonitordb > backup_before_salt_rotation_$(date +%Y%m%d).sql

# Step 2: Generate new salt
NEW_SALT=$(openssl rand -base64 32)

# Step 3: Store new salt in Key Vault
az keyvault secret set \
  --vault-name hvo-skymonitor-prod \
  --name ApiKey--HashingSalt \
  --value "$NEW_SALT"

# Step 4: OPTION A: Invalidate all existing API keys (recommended)
# Mark all existing API keys as inactive in database
# Require users to create new API keys with new salt

# Step 4: OPTION B: Re-hash existing keys (complex, requires plaintext keys)
# This is NOT recommended as we don't store plaintext API keys
# Users must create new API keys

# Step 5: Update application configuration
# Deploy application with new salt

# Step 6: Notify users to regenerate API keys
# Send email to all API key holders
# Update documentation with cutover date

# Step 7: Monitor for support requests
```

**Note:** Because API keys are hashed on creation, rotation of the hashing salt effectively invalidates all existing keys. This is by design for security.

**Alternative Approach:** Don't rotate salt; instead rotate individual API keys as needed. The salt rotation is primarily needed after a security breach.

---

## Account Onboarding

### Creating a New User Account

**Prerequisites:**
- Admin access to HVO.SkyMonitor
- User's email address
- Assigned roles/permissions

**Procedure:**

1. **Navigate to Admin Portal**
   - Log in to https://skymonitor.hvo.org
   - Navigate to Account Management

2. **Create New User**
   ```
   - Click "Create User Account"
   - Enter email address
   - Select Account Type: USER
   - Assign roles (Viewer, Operator, Admin)
   - Click "Create"
   ```

3. **Send Invitation Email**
   - System automatically sends invitation to user's email
   - Link expires in 7 days
   - User must confirm email and set password

4. **Verify Account Creation**
   ```bash
   # Check audit logs for account creation event
   az monitor log-analytics query \
     --workspace <workspace-id> \
     --analytics-query "AppEvents | where Name == 'UserAccountCreated' | where UserId == '<user-id>'"
   ```

5. **Document in Access Control Log**
   - Record user email, creation date, assigned roles
   - Update team roster

**Post-Creation Checklist:**
- [ ] User account created in database
- [ ] Invitation email sent and received
- [ ] User confirmed email
- [ ] User set strong password
- [ ] Roles/permissions verified
- [ ] Account documented in access log

---

### Creating a New System Account (Service/Agent)

**Prerequisites:**
- Service name and description
- Justification for system account
- Required scopes/permissions

**Procedure:**

1. **Navigate to Admin Portal**
   - Log in to https://skymonitor.hvo.org
   - Navigate to System Accounts

2. **Create System Account**
   ```
   - Click "Create System Account"
   - Enter account name (e.g., "camera-agent-01")
   - Enter description
   - Select Account Type: SYSTEM
   - Assign scopes (api:read, api:write, frames:access)
   - Click "Create"
   ```

3. **Generate Client Credentials**
   - System generates Client ID and Client Secret
   - **IMPORTANT:** Copy Client Secret immediately (shown only once)
   - Store in secure location (Key Vault, password manager)

4. **Configure Client Application**
   ```bash
   # Set environment variables for client
   export CLIENT_ID="<generated-client-id>"
   export CLIENT_SECRET="<generated-client-secret>"
   export TOKEN_ENDPOINT="https://skymonitor.hvo.org/connect/token"
   ```

5. **Test Authentication**
   ```bash
   # Test client credentials flow
   curl -X POST https://skymonitor.hvo.org/connect/token \
     -H "Content-Type: application/x-www-form-urlencoded" \
     -d "grant_type=client_credentials" \
     -d "client_id=$CLIENT_ID" \
     -d "client_secret=$CLIENT_SECRET" \
     -d "scope=api:read api:write"
   ```

6. **Verify in Logs**
   - Check for successful token issuance
   - Verify correct scopes in token

**Post-Creation Checklist:**
- [ ] System account created
- [ ] Client credentials generated and stored securely
- [ ] Client application configured
- [ ] Authentication tested successfully
- [ ] Scopes verified correct
- [ ] Account documented

---

## API Key Management

### Creating a New API Key (User Self-Service)

**Prerequisites:**
- Valid user account
- Logged in to HVO.SkyMonitor

**Procedure:**

1. **Navigate to API Keys Page**
   - Log in to https://skymonitor.hvo.org
   - Go to Account → API Keys

2. **Create New API Key**
   ```
   - Click "Generate New API Key"
   - Enter display name (e.g., "Python Script - Data Analysis")
   - Select access level (Read-only or Read/Write)
   - Set expiration (optional, recommended: 90 days)
   - Click "Generate"
   ```

3. **Copy API Key**
   - **CRITICAL:** Copy the API key immediately
   - It will only be displayed once
   - Store in secure location (password manager, Key Vault)

4. **Test API Key**
   ```bash
   # Test API key authentication
   curl -X GET https://skymonitor.hvo.org/api/v1.0/status \
     -H "X-API-Key: <your-api-key>"
   ```

5. **Document Usage**
   - Note what the API key is used for
   - Set calendar reminder for rotation/expiration

**Post-Creation Checklist:**
- [ ] API key generated
- [ ] API key copied and stored securely
- [ ] API key tested successfully
- [ ] Usage documented
- [ ] Expiration/rotation scheduled

---

### Rotating an Existing API Key

**Frequency:** Every 90 days or on security event  
**Procedure:**

1. **Create New API Key**
   - Follow creation procedure above
   - Use same display name with version (e.g., "Python Script - v2")

2. **Update Client Application**
   ```bash
   # Update environment variable or configuration
   export HVO_API_KEY="<new-api-key>"
   ```

3. **Test New API Key**
   ```bash
   curl -X GET https://skymonitor.hvo.org/api/v1.0/status \
     -H "X-API-Key: $HVO_API_KEY"
   ```

4. **Deactivate Old API Key**
   - Navigate to API Keys page
   - Find old API key
   - Click "Deactivate" (keeps for audit, stops working)
   - OR Click "Delete" (removes completely)

5. **Monitor for Errors**
   - Watch logs for authentication failures with old key
   - Indicates client still using old key

**Rollback:**
If new key doesn't work, reactivate old key temporarily while troubleshooting.

---

### Revoking a Compromised API Key

**Urgency:** IMMEDIATE  
**Procedure:**

```bash
# Option 1: Via Web UI
1. Log in to https://skymonitor.hvo.org
2. Navigate to Account → API Keys
3. Find compromised key
4. Click "Delete" immediately

# Option 2: Via Admin Console (for admin revoking user's key)
1. Log in as admin
2. Navigate to Admin → API Key Management
3. Search for user or key ID
4. Click "Revoke" or "Delete"
5. Document incident
```

**Post-Revocation Actions:**
- [ ] Key deleted from database
- [ ] User notified (if not their action)
- [ ] Audit logs checked for unauthorized usage
- [ ] Incident report filed
- [ ] New key generated if needed
- [ ] Security team notified if breach suspected

---

## Access Revocation

### Revoking User Access (Offboarding)

**Prerequisites:**
- Offboarding ticket or HR notification
- Admin access

**Procedure:**

1. **Disable User Account**
   ```
   - Navigate to Admin → User Management
   - Find user account
   - Click "Disable Account"
   - Enter reason (e.g., "Employee Terminated")
   - Confirm
   ```

2. **Revoke All API Keys**
   ```
   - Navigate to user's API keys
   - Select all active keys
   - Click "Revoke All"
   - Confirm
   ```

3. **Revoke Active Sessions**
   ```
   - Navigate to user's active sessions
   - Click "End All Sessions"
   - User will be logged out immediately
   ```

4. **Revoke OAuth Tokens**
   ```
   - Navigate to user's authorized applications
   - Revoke all access tokens
   - Revoke all refresh tokens
   ```

5. **Audit Recent Activity**
   ```bash
   # Check user's activity in last 30 days
   az monitor log-analytics query \
     --workspace <workspace-id> \
     --analytics-query "AppEvents | where UserId == '<user-id>' | where TimeGenerated > ago(30d)"
   ```

6. **Document Revocation**
   - Record date and time of revocation
   - Note who performed revocation
   - File in offboarding records

**Post-Revocation Checklist:**
- [ ] User account disabled
- [ ] All API keys revoked
- [ ] All sessions terminated
- [ ] OAuth tokens revoked
- [ ] Activity audited
- [ ] Revocation documented
- [ ] No unauthorized access since revocation

---

### Emergency Access Revocation

**When to Use:** Security incident, suspected compromise, immediate threat

**Procedure:**

```bash
# Step 1: Identify affected accounts/keys
# From security alert or incident report

# Step 2: Immediately disable/revoke via database (fastest)
# Connect to database
psql skymonitordb

# Disable user account
UPDATE "AspNetUsers" SET "LockoutEnabled" = true, "LockoutEnd" = '9999-12-31' 
WHERE "Id" = '<user-id>';

# Revoke all API keys for user
UPDATE "ApiKeys" SET "IsActive" = false 
WHERE "UserId" = '<user-id>';

# Commit
COMMIT;

# Step 3: Invalidate all sessions (requires application restart or cache clear)
# Clear session cache
redis-cli FLUSHDB

# Step 4: Notify security team
# Send alert to security@hvo.org

# Step 5: Begin incident response procedure (see below)
```

---

## Incident Response

### Security Incident Response Procedure

**Severity Levels:**
- **CRITICAL:** Active breach, data exfiltration, or widespread compromise
- **HIGH:** Single account compromise, exposed credentials
- **MEDIUM:** Suspicious activity, potential vulnerability
- **LOW:** Policy violation, unusual but benign activity

### Incident Response Workflow

**Phase 1: Detection & Triage (0-15 minutes)**

1. **Receive Alert**
   - Monitor alerts from logs, metrics, or user reports
   - Document time, source, and nature of alert

2. **Initial Assessment**
   ```
   - What happened?
   - When did it happen?
   - What systems/accounts affected?
   - Is it still ongoing?
   - What's the severity?
   ```

3. **Notify Stakeholders**
   - CRITICAL/HIGH: Notify security team immediately
   - MEDIUM: Notify on-call engineer
   - LOW: Create ticket for investigation

**Phase 2: Containment (15-60 minutes)**

1. **Isolate Affected Systems**
   - Revoke compromised credentials immediately
   - Disable compromised accounts
   - Block suspicious IP addresses
   - Invalidate active sessions

2. **Preserve Evidence**
   ```bash
   # Export relevant logs
   az monitor log-analytics query \
     --workspace <workspace-id> \
     --analytics-query "AppEvents | where TimeGenerated > ago(24h)" \
     --output tsv > incident_logs_$(date +%Y%m%d_%H%M%S).tsv
   
   # Backup database state
   pg_dump skymonitordb > incident_backup_$(date +%Y%m%d_%H%M%S).sql
   ```

3. **Stop the Bleeding**
   - Prevent further damage
   - Block attack vectors
   - Implement temporary mitigations

**Phase 3: Investigation (1-4 hours)**

1. **Root Cause Analysis**
   ```
   - How did the incident occur?
   - What vulnerability was exploited?
   - Timeline of events
   - Scope of impact
   ```

2. **Impact Assessment**
   ```
   - What data was accessed?
   - What systems were compromised?
   - How many users affected?
   - What's the business impact?
   ```

3. **Document Findings**
   - Create incident report
   - Include timeline, root cause, impact
   - Attach logs and evidence

**Phase 4: Remediation (4-24 hours)**

1. **Fix Vulnerability**
   - Patch security holes
   - Update configurations
   - Deploy fixes

2. **Rotate All Affected Credentials**
   - Follow key rotation procedures
   - Force password resets for affected users
   - Regenerate API keys

3. **Restore Services**
   - Bring systems back online
   - Verify functionality
   - Monitor for issues

**Phase 5: Post-Incident (1-7 days)**

1. **Complete Incident Report**
   ```
   - Executive summary
   - Detailed timeline
   - Root cause analysis
   - Impact assessment
   - Remediation actions
   - Lessons learned
   - Prevention recommendations
   ```

2. **Implement Preventive Measures**
   - Add monitoring/alerting
   - Update security policies
   - Enhance controls
   - Conduct training

3. **Communication**
   - Notify affected users (if required)
   - Update stakeholders
   - File breach reports (if required by law)

---

## Troubleshooting 401/403 Errors

### 401 Unauthorized Errors

**Common Causes:**

1. **Missing or Invalid API Key**
   ```
   Symptom: "401 Unauthorized"
   Solution: Verify API key is present in X-API-Key header
   ```

2. **Expired Token**
   ```
   Symptom: "401 Unauthorized" with OpenIddict validation
   Solution: Refresh access token using refresh token
   ```

3. **Invalid Bearer Token**
   ```
   Symptom: "401 Unauthorized" on /api endpoints
   Solution: Verify token is valid JWT and not expired
   ```

**Troubleshooting Steps:**

```bash
# Step 1: Verify API key exists and is active
# Via Web UI: Account → API Keys → Check status

# Step 2: Test API key directly
curl -v -X GET https://skymonitor.hvo.org/api/v1.0/status \
  -H "X-API-Key: <api-key>"
# Look for 401 response and response body for details

# Step 3: Check token expiration
# Decode JWT token
echo '<jwt-token>' | cut -d. -f2 | base64 -d | jq .
# Check 'exp' claim (Unix timestamp)

# Step 4: Request new token
curl -X POST https://skymonitor.hvo.org/connect/token \
  -H "Content-Type: application/x-www-form-urlencoded" \
  -d "grant_type=refresh_token" \
  -d "refresh_token=<refresh-token>" \
  -d "client_id=<client-id>"

# Step 5: Check server logs for more details
# Look for authentication handler errors
```

---

### 403 Forbidden Errors

**Common Causes:**

1. **Insufficient Permissions**
   ```
   Symptom: "403 Forbidden" despite valid authentication
   Cause: User/key lacks required role or scope
   Solution: Grant appropriate permissions
   ```

2. **Account Type Mismatch**
   ```
   Symptom: "403 Forbidden" on endpoints requiring USER or SYSTEM account
   Cause: SYSTEM account trying to access USER-only endpoint (or vice versa)
   Solution: Use correct account type for the operation
   ```

3. **API Key Access Level Too Low**
   ```
   Symptom: "403 Forbidden" on write operations with Read-only key
   Cause: API key has Read access but operation requires ReadWrite
   Solution: Use ReadWrite API key or create new one
   ```

**Troubleshooting Steps:**

```bash
# Step 1: Check user/key permissions
# Decode token to check scopes
echo '<jwt-token>' | cut -d. -f2 | base64 -d | jq .scope

# Step 2: Verify account type
# Check 'account_type' claim in token
echo '<jwt-token>' | cut -d. -f2 | base64 -d | jq .account_type

# Step 3: Check API key access level
# Via Web UI: Account → API Keys → View access level

# Step 4: Review endpoint requirements
# Check controller code for [Authorize] attributes and required policies

# Step 5: Grant appropriate permissions
# Via Admin Portal: Update user roles or API key access level
```

---

## Certificate Management

### Monitoring Certificate Expiration

**Automated Monitoring:**

```bash
# Add to monitoring system (Azure Monitor, Prometheus, etc.)
# Alert 30 days before expiration

# Check certificate expiration
openssl x509 -in /path/to/cert.pem -noout -enddate

# Check HTTPS endpoint certificate
echo | openssl s_client -connect skymonitor.hvo.org:443 2>/dev/null | \
  openssl x509 -noout -enddate
```

**Calendar Reminders:**
- Set reminder 60 days before expiration
- Set reminder 30 days before expiration
- Set reminder 7 days before expiration

---

### Certificate Renewal (Let's Encrypt)

**Prerequisites:**
- Certbot installed
- DNS or HTTP challenge configured

**Procedure:**

```bash
# Step 1: Test renewal (dry run)
sudo certbot renew --dry-run

# Step 2: Renew certificate
sudo certbot renew

# Step 3: Convert to PFX format
sudo openssl pkcs12 -export \
  -out /etc/letsencrypt/live/skymonitor.hvo.org/cert.pfx \
  -inkey /etc/letsencrypt/live/skymonitor.hvo.org/privkey.pem \
  -in /etc/letsencrypt/live/skymonitor.hvo.org/cert.pem \
  -certfile /etc/letsencrypt/live/skymonitor.hvo.org/chain.pem \
  -password pass:YourSecurePassword

# Step 4: Upload to Key Vault
az keyvault certificate import \
  --vault-name hvo-skymonitor-prod \
  --name https-cert \
  --file /etc/letsencrypt/live/skymonitor.hvo.org/cert.pfx

# Step 5: Restart application to pick up new certificate
kubectl rollout restart deployment/skymonitor
```

**Automated Renewal:**

```bash
# Add to cron (runs twice daily)
0 0,12 * * * sudo certbot renew --quiet --deploy-hook "/usr/local/bin/deploy-cert.sh"
```

---

## Monitoring & Alerts

### Key Metrics to Monitor

**Authentication Metrics:**
- Token requests per minute (by client)
- Authentication success/failure rate
- API key usage (by key)
- Active sessions count

**Security Metrics:**
- Failed login attempts (by IP, by user)
- Rate limit violations
- Signed URL validation failures
- Certificate expiration dates

**Performance Metrics:**
- Token endpoint latency (p50, p95, p99)
- API endpoint latency
- Error rates (4xx, 5xx)

### Setting Up Alerts

**Critical Alerts (Immediate):**
```
- Certificate expiring in < 7 days
- Authentication failure rate > 10% for 5 minutes
- Rate limit violations from single IP > 100/min
- System account login attempt (should be impossible)
```

**Warning Alerts (Next Business Day):**
```
- Certificate expiring in < 30 days
- API key nearing expiration (< 14 days)
- Unusual spike in token requests
- New API key created by SYSTEM account
```

**Info Alerts (Weekly Digest):**
```
- New user accounts created
- API keys created/deleted
- Key rotation performed
- Configuration changes
```

### Log Queries

**Find failed authentication attempts:**
```kusto
AppEvents
| where Name == "ApiKeyAuthenticationFailed" or Name == "LoginFailed"
| where TimeGenerated > ago(1h)
| summarize Count=count() by UserId, IpAddress
| order by Count desc
```

**Find suspicious API key usage:**
```kusto
AppEvents  
| where Name == "ApiKeyUsed"
| where TimeGenerated > ago(24h)
| summarize Count=count(), DistinctIPs=dcount(IpAddress) by ApiKeyId
| where DistinctIPs > 5  // Same key used from many IPs
```

**Monitor token issuance rate:**
```kusto
AppEvents
| where Name == "TokenIssued"
| where TimeGenerated > ago(1h)
| summarize Count=count() by bin(TimeGenerated, 1m), ClientId
| order by TimeGenerated desc
```

---

## Summary

This runbook covers the essential operational procedures for Identity Hardening:

- ✅ Key rotation for OpenIddict, signed URLs, API keys
- ✅ Account onboarding for users and system accounts
- ✅ API key lifecycle management
- ✅ Access revocation and offboarding
- ✅ Security incident response
- ✅ Troubleshooting authentication and authorization errors
- ✅ Certificate management and renewal
- ✅ Monitoring, metrics, and alerting

For additional information:
- See [../security/secrets.md](../security/secrets.md) for the consolidated secrets catalog and rotation cadence
- See logs and metrics dashboards for real-time monitoring

**Next Steps:**
1. Review and familiarize with procedures
2. Set up monitoring and alerts
3. Schedule key rotations on calendar
4. Conduct incident response drill
5. Update procedures based on operational experience
