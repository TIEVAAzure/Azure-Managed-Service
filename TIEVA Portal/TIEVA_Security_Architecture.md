# TIEVA Portal - Security & Managed Identity Architecture

## Principle: Zero Credentials in Code

**Every connection uses Managed Identity or Entra ID authentication. No passwords, connection strings, or secrets are ever stored in code or configuration files.**

---

## Identity Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────────────────────────┐
│                              IDENTITY ARCHITECTURE                                       │
├─────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                         │
│  ┌─────────────────────────────────────────────────────────────────────────────────┐    │
│  │                         YOUR ENTRA ID TENANT                                    │    │
│  │                                                                                 │    │
│  │   ┌─────────────────┐    ┌─────────────────┐    ┌─────────────────┐            │    │
│  │   │  User Accounts  │    │ Security Groups │    │ App Registrations│            │    │
│  │   │  (Consultants)  │    │                 │    │                 │            │    │
│  │   │                 │    │ • TIEVA-Admins  │    │ • TIEVA-Portal  │            │    │
│  │   │ • chris@tieva   │    │ • TIEVA-Viewers │    │   (for SSO)     │            │    │
│  │   │ • alex@tieva    │    │                 │    │                 │            │    │
│  │   └─────────────────┘    └─────────────────┘    └─────────────────┘            │    │
│  │                                                                                 │    │
│  │   ┌─────────────────────────────────────────────────────────────────────────┐  │    │
│  │   │                    MANAGED IDENTITIES                                   │  │    │
│  │   │                    (System-Assigned)                                    │  │    │
│  │   │                                                                         │  │    │
│  │   │   ┌─────────────────┐         ┌─────────────────┐                      │  │    │
│  │   │   │ Function App    │         │ Static Web App  │                      │  │    │
│  │   │   │ Managed Identity│         │ (uses Entra ID  │                      │  │    │
│  │   │   │                 │         │  for user auth) │                      │  │    │
│  │   │   │ ID: xxxxxxxx    │         │                 │                      │  │    │
│  │   │   └─────────────────┘         └─────────────────┘                      │  │    │
│  │   │                                                                         │  │    │
│  │   └─────────────────────────────────────────────────────────────────────────┘  │    │
│  │                                                                                 │    │
│  └─────────────────────────────────────────────────────────────────────────────────┘    │
│                                                                                         │
└─────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Connection Types & Authentication Methods

### 1. User → Portal (Entra ID SSO)

```
┌──────────────┐         ┌─────────────────┐         ┌─────────────────┐
│              │  HTTPS  │  Azure Static   │  OAuth  │                 │
│  Consultant  │────────►│  Web Apps       │────────►│   Entra ID      │
│  (Browser)   │         │                 │◄────────│                 │
│              │         │                 │  Token  │                 │
└──────────────┘         └─────────────────┘         └─────────────────┘

Authentication: OAuth 2.0 / OpenID Connect
Credentials: User's Entra ID credentials (supports MFA)
Session: JWT token stored in secure HTTP-only cookie
```

**Configuration (staticwebapp.config.json):**
```json
{
  "auth": {
    "identityProviders": {
      "azureActiveDirectory": {
        "registration": {
          "openIdIssuer": "https://login.microsoftonline.com/<TENANT_ID>/v2.0",
          "clientIdSettingName": "AAD_CLIENT_ID",
          "clientSecretSettingName": "AAD_CLIENT_SECRET"
        }
      }
    }
  },
  "routes": [
    {
      "route": "/*",
      "allowedRoles": ["authenticated"]
    }
  ]
}
```

---

### 2. Portal → Function App API (Forwarded Auth Token)

```
┌─────────────────┐         ┌─────────────────┐
│  Static Web App │  HTTPS  │  Function App   │
│  (Portal)       │────────►│  (API)          │
│                 │ + Token │                 │
└─────────────────┘         └─────────────────┘

Authentication: Forwarded user token from Static Web Apps
Validation: Function validates token came from Static Web Apps
Credentials: None - token-based
```

**Function App validates the user:**
```csharp
// User identity automatically available via Static Web Apps integration
var clientPrincipal = req.Headers["x-ms-client-principal"];
var user = DecodeClientPrincipal(clientPrincipal);
// user.Identity.Name = "chris@tieva.com"
// user.Claims = ["TIEVA-Admins", ...]
```

---

### 3. Function App → Azure SQL (Managed Identity)

```
┌─────────────────┐                    ┌─────────────────┐
│  Function App   │  Managed Identity  │  Azure SQL      │
│                 │───────────────────►│  Database       │
│  (System MI)    │   No password!     │                 │
└─────────────────┘                    └─────────────────┘

Authentication: Azure AD Managed Identity
Credentials: NONE - Azure handles it automatically
Connection String: "Server=xxx.database.windows.net;Database=TievaPortal;Authentication=Active Directory Managed Identity"
```

**Setup Required:**
```sql
-- Run in Azure SQL as admin
CREATE USER [func-tieva-prod] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [func-tieva-prod];
ALTER ROLE db_datawriter ADD MEMBER [func-tieva-prod];
```

**C# Code:**
```csharp
// No password anywhere!
services.AddDbContext<TievaDbContext>(options =>
    options.UseSqlServer(
        "Server=sql-tieva.database.windows.net;Database=TievaPortal;Authentication=Active Directory Managed Identity"
    ));
```

---

### 4. Function App → Key Vault (Managed Identity)

```
┌─────────────────┐                    ┌─────────────────┐
│  Function App   │  Managed Identity  │  Azure Key      │
│                 │───────────────────►│  Vault          │
│  (System MI)    │   No password!     │                 │
└─────────────────┘                    └─────────────────┘

Authentication: Azure AD Managed Identity
Credentials: NONE - Azure handles it automatically
Access: Function App's MI granted "Key Vault Secrets User" role
```

**Setup Required:**
```powershell
# Grant Function App access to Key Vault
az keyvault set-policy --name kv-tieva `
  --object-id <function-app-managed-identity-id> `
  --secret-permissions get list
```

**C# Code:**
```csharp
// No password anywhere!
var client = new SecretClient(
    new Uri("https://kv-tieva.vault.azure.net/"),
    new DefaultAzureCredential()  // Uses Managed Identity automatically
);
var secret = await client.GetSecretAsync("customer-contoso-sp-secret");
```

---

### 5. Function App → Customer Azure Tenant (Service Principal)

```
┌─────────────────┐         ┌─────────────────┐         ┌─────────────────┐
│  Function App   │────────►│  Key Vault      │────────►│  Customer       │
│                 │  Get SP │  (SP secrets)   │  Auth   │  Azure Tenant   │
│                 │  Secret │                 │  with   │                 │
│                 │◄────────│                 │  SP     │                 │
│                 │─────────────────────────────────────►│  (Reader only)  │
└─────────────────┘                                      └─────────────────┘

Step 1: Function uses Managed Identity to get SP secret from Key Vault
Step 2: Function authenticates to customer tenant using SP credentials
Credentials: SP secret stored ONLY in Key Vault, never in code
Customer Control: Customer creates SP, grants Reader role, can revoke anytime
```

**C# Code:**
```csharp
// Step 1: Get secret from Key Vault (using Managed Identity)
var kvClient = new SecretClient(
    new Uri("https://kv-tieva.vault.azure.net/"),
    new DefaultAzureCredential()
);
var spSecret = await kvClient.GetSecretAsync($"sp-{customerId}");

// Step 2: Authenticate to customer tenant
var credential = new ClientSecretCredential(
    tenantId: connection.TenantId,
    clientId: connection.ClientId,
    clientSecret: spSecret.Value.Value
);

// Step 3: Query customer's Azure (read-only)
var armClient = new ArmClient(credential);
var subscriptions = armClient.GetSubscriptions();
```

---

## Complete Security Flow Diagram

```
┌────────────────────────────────────────────────────────────────────────────────────────────────────┐
│                                    COMPLETE AUTHENTICATION FLOW                                     │
├────────────────────────────────────────────────────────────────────────────────────────────────────┤
│                                                                                                    │
│   ┌─────────┐                                                                                      │
│   │  User   │                                                                                      │
│   │ Browser │                                                                                      │
│   └────┬────┘                                                                                      │
│        │ 1. Access portal                                                                          │
│        ▼                                                                                           │
│   ┌─────────────────────┐                                                                          │
│   │  Static Web Apps    │                                                                          │
│   │  ┌───────────────┐  │     2. Redirect to login                                                 │
│   │  │ Auth Middleware│──┼────────────────────────────┐                                            │
│   │  └───────────────┘  │                             │                                            │
│   └─────────────────────┘                             ▼                                            │
│                                              ┌─────────────────┐                                   │
│                                              │    Entra ID     │                                   │
│                                              │  ┌───────────┐  │                                   │
│        ┌─────────────────────────────────────│  │   Login   │  │                                   │
│        │ 3. Token returned                   │  │   + MFA   │  │                                   │
│        ▼                                     │  └───────────┘  │                                   │
│   ┌─────────────────────┐                    └─────────────────┘                                   │
│   │  Static Web Apps    │                                                                          │
│   │  ┌───────────────┐  │     4. API call + forwarded token                                        │
│   │  │ Portal HTML   │──┼────────────────────────────┐                                             │
│   │  └───────────────┘  │                            │                                             │
│   └─────────────────────┘                            ▼                                             │
│                                              ┌─────────────────────────────────────┐               │
│                                              │         Function App                │               │
│                                              │  ┌───────────────────────────────┐  │               │
│                                              │  │  5. Validate user token       │  │               │
│                                              │  │  6. Check user roles          │  │               │
│                                              │  │  7. Process request           │  │               │
│                                              │  └───────────────┬───────────────┘  │               │
│                                              │                  │                  │               │
│                                              │         ┌────────┴────────┐         │               │
│                                              │         ▼                 ▼         │               │
│                                              │  ┌─────────────┐  ┌─────────────┐   │               │
│                                              │  │ 8. SQL via  │  │ 9. KeyVault │   │               │
│                                              │  │ Managed ID  │  │ via Mgd ID  │   │               │
│                                              │  └──────┬──────┘  └──────┬──────┘   │               │
│                                              └─────────┼─────────────────┼─────────┘               │
│                                                        │                 │                         │
│                         NO PASSWORDS                   ▼                 ▼                         │
│                         ──────────────         ┌─────────────┐   ┌─────────────┐                   │
│                                                │  Azure SQL  │   │  Key Vault  │                   │
│                                                │  Database   │   │  (secrets)  │                   │
│                                                └─────────────┘   └──────┬──────┘                   │
│                                                                         │                          │
│                                                                         │ 10. Get customer         │
│                                                                         │     SP secret            │
│                                                                         ▼                          │
│                                              ┌─────────────────────────────────────┐               │
│                                              │         Function App                │               │
│                                              │  ┌───────────────────────────────┐  │               │
│                                              │  │ 11. Auth to customer tenant   │  │               │
│                                              │  │     using SP credentials      │  │               │
│                                              │  └───────────────┬───────────────┘  │               │
│                                              └──────────────────┼──────────────────┘               │
│                                                                 │                                  │
│                                                                 ▼                                  │
│                                              ┌─────────────────────────────────────┐               │
│                                              │      Customer Azure Tenant          │               │
│                                              │  ┌───────────────────────────────┐  │               │
│                                              │  │  12. Read-only API calls      │  │               │
│                                              │  │      (Reader role only)       │  │               │
│                                              │  └───────────────────────────────┘  │               │
│                                              └─────────────────────────────────────┘               │
│                                                                                                    │
└────────────────────────────────────────────────────────────────────────────────────────────────────┘
```

---

## Compliance Alignment

| Framework | Requirement | How TIEVA Meets It |
|-----------|-------------|-------------------|
| **ISO 27001** | A.9.4.3 - Password management | No passwords - Managed Identity |
| **ISO 27001** | A.9.2.3 - Privileged access | Role-based access, audit logs |
| **ISO 27001** | A.10.1.1 - Cryptographic controls | TLS 1.2+, encryption at rest |
| **SOC 2** | CC6.1 - Logical access | Entra ID SSO with MFA |
| **SOC 2** | CC6.3 - Role-based access | Security groups, RBAC |
| **NIST CSF** | PR.AC-1 - Identity management | Managed Identity, Entra ID |
| **NIST CSF** | PR.DS-1 - Data protection | Encryption in transit/at rest |
| **CIS Azure** | 1.3 - MFA for admins | Enforced via Entra ID |
| **CIS Azure** | 5.1 - Diagnostic logging | Application Insights enabled |
| **GDPR** | Art 32 - Security of processing | Encryption, access controls |

---

## Secrets Management Policy

### What Goes in Key Vault

| Secret Type | Naming Convention | Rotation Policy |
|-------------|------------------|-----------------|
| Customer SP secrets | `sp-{customerId}` | Track expiry, alert 30 days before |
| SMTP credentials (if used) | `smtp-password` | Annual |
| Any third-party API keys | `api-{service}` | As required |

### What Does NOT Go in Key Vault

| Item | Where It Lives | Why |
|------|----------------|-----|
| SQL connection | Managed Identity | No password needed |
| Key Vault connection | Managed Identity | No password needed |
| User passwords | Entra ID | Microsoft manages |
| Function App secrets | Managed Identity | No secrets needed |

---

## Role-Based Access Control

### Portal Roles

| Role | Permissions | Who Gets It |
|------|-------------|-------------|
| TIEVA-Admins | Full access - manage customers, run assessments, configure tiers | Senior consultants |
| TIEVA-Viewers | Read-only - view assessments, reports | Junior consultants |
| TIEVA-Billing | View cost reports only | Finance team |

### Azure RBAC (on TIEVA resources)

| Role | Scope | Who Gets It |
|------|-------|-------------|
| Owner | Resource Group | 1-2 platform admins only |
| Contributor | Function App | DevOps for deployments |
| Reader | All resources | All TIEVA team |
| Key Vault Secrets User | Key Vault | Function App Managed Identity only |

### Customer Tenant RBAC

| Role | Scope | Who Gets It |
|------|-------|-------------|
| Reader | Subscriptions in scope | TIEVA Service Principal only |

---

## Network Security (Optional Enhancement)

For maximum security, you can add:

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                         OPTIONAL: PRIVATE NETWORKING                         │
├─────────────────────────────────────────────────────────────────────────────┤
│                                                                             │
│  ┌─────────────────┐         ┌─────────────────┐         ┌───────────────┐  │
│  │  Static Web App │────────►│  Function App   │────────►│  SQL + KV     │  │
│  │  (Public)       │  VNet   │  (VNet Integrated)│ Private│  (Private     │  │
│  │                 │  Linked │                 │ Endpoint│   Endpoints)  │  │
│  └─────────────────┘         └─────────────────┘         └───────────────┘  │
│                                                                             │
│  • SQL and Key Vault have no public endpoints                               │
│  • Function App connects via private network                                │
│  • Adds ~£50/month for Private Endpoints                                    │
│  • Recommended for production with sensitive customers                      │
│                                                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## Audit & Monitoring

| What | Where | Retention |
|------|-------|-----------|
| User logins | Entra ID Sign-in Logs | 30 days (free) |
| API calls | Application Insights | 90 days |
| Key Vault access | Key Vault Diagnostic Logs | 90 days |
| SQL queries | SQL Auditing (optional) | As configured |
| Assessment runs | TIEVA database | Forever |

---

## Summary: Zero Trust Architecture

1. **No hardcoded credentials** - Everything uses Managed Identity
2. **No shared passwords** - Each user has own Entra ID account
3. **Least privilege** - Reader role only on customer tenants
4. **Secrets encrypted** - Key Vault with access policies
5. **All traffic encrypted** - TLS 1.2+ everywhere
6. **Full audit trail** - Every action logged
7. **MFA supported** - Via Entra ID policies
8. **Role-based access** - Security groups control portal features

This architecture meets enterprise security requirements and is compliant with major frameworks.

---

## Ready to Implement?

This security architecture is built into the Phase 1 deployment. When you're ready, we'll:

1. Create Resource Group
2. Create Key Vault with access policies
3. Create SQL with Entra ID admin (no SQL password)
4. Create Function App with System Managed Identity
5. Grant Function App access to SQL and Key Vault
6. Create Static Web App with Entra ID auth
7. Deploy code

All using Azure CLI commands - no portal clicking, fully repeatable.
