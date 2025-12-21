# TIEVA Portal - Project Summary

## Overview
TIEVA Portal is an Azure-hosted web application for managing Azure assessments for customers. It runs automated audit scripts against customer Azure tenants, stores results, and provides analysis tools for remediation planning.

## Architecture

### Azure Resources (Resource Group: rg-tievaPortal-prod, UK South)

| Resource | Name | Purpose |
|----------|------|---------|
| SQL Server | sql-tievaPortal-3234.database.windows.net | Entra-only auth |
| SQL Database | TievaPortal | Basic tier, stores all data |
| Key Vault | kv-tievaPortal-874 | Stores customer Service Principal secrets |
| Function App (.NET) | func-tievaPortal-6612 | Main API (customers, connections, tiers, assessments) |
| Function App (PowerShell) | func-tieva-audit | Runs audit scripts |
| Static Web App | swa-tievaPortal-portal | Portal UI |
| Storage Account | sttieva3420 | Function App storage |
| Storage Account | sttievaaudit | Audit results blob storage |

### URLs
- **Portal**: https://ambitious-wave-092ef1703.3.azurestaticapps.net
- **API**: https://func-tievaportal-6612.azurewebsites.net/api
- **Audit API**: https://func-tieva-audit.azurewebsites.net/api
- **GitHub**: https://github.com/TIEVAAzure/tieva-portal

### Managed Identities
- **func-tievaPortal-6612**: `35697b67-2bb0-4ddd-9b90-ac042918b10d` (has SQL access, Key Vault Secrets Officer)
- **func-tieva-audit**: `a2e06bf6-61a2-46b9-92a0-0065b8721235` (has Key Vault Secrets User, Storage Blob Data Contributor)

## Local File Structure

```
C:\VS Code\Azure-Managed-Service\TIEVA Portal\
├── portal\                          # Static Web App (GitHub linked)
│   ├── index.html                   # Main SPA (~3100 lines)
│   └── staticwebapp.config.json     # Auth config
├── functions\
│   ├── TIEVA.Functions\             # .NET 8 API
│   │   ├── Functions\
│   │   │   ├── CustomerFunctions.cs
│   │   │   ├── ConnectionFunctions.cs
│   │   │   ├── TierFunctions.cs
│   │   │   ├── SubscriptionFunctions.cs
│   │   │   ├── AssessmentFunctions.cs
│   │   │   └── DashboardFunctions.cs
│   │   ├── Models\Entities.cs
│   │   ├── Services\TievaDbContext.cs
│   │   └── deploy.ps1               # Deploy script
│   └── TIEVA.Audit\                 # PowerShell Function App
│       ├── StartAssessment\run.ps1  # Main assessment trigger
│       ├── Scripts\                 # Audit scripts
│       │   ├── NetworkAudit.ps1
│       │   ├── BackupAudit.ps1
│       │   ├── CostManagementAudit.ps1
│       │   ├── IdentityAudit.ps1
│       │   ├── PolicyAudit.ps1
│       │   ├── ResourceAudit.ps1
│       │   └── ReservationAudit.ps1
│       └── requirements.psd1        # Az modules + ImportExcel
├── *Audit.ps1                       # Original audit scripts
├── TIEVA_*.html                     # Analyzer HTML tools
└── SCALABILITY_NOTES.md             # Performance & scaling documentation
```

## API Endpoints

### Main API (func-tievaPortal-6612)

**Customers**
- `GET /api/customers` - List customers
- `GET /api/customers/{id}` - Get customer
- `GET /api/customers/{id}/findings` - Get consolidated findings (CustomerFindings table)
- `POST /api/customers` - Create customer
- `PUT /api/customers/{id}` - Update customer
- `DELETE /api/customers/{id}` - Soft delete customer

**Connections**
- `GET /api/connections` - List connections
- `GET /api/connections/{id}` - Get connection with subscriptions
- `POST /api/connections` - Create connection (validates & stores secret in Key Vault)
- `PUT /api/connections/{id}` - Update connection
- `POST /api/connections/{id}/validate` - Validate connection
- `POST /api/connections/{id}/sync` - Sync subscriptions from Azure
- `DELETE /api/connections/{id}` - Delete connection

**Subscriptions**
- `GET /api/subscriptions` - List all subscriptions
- `PUT /api/subscriptions/{id}` - Update subscription (tier, environment, scope)
- `PUT /api/connections/{connectionId}/subscriptions` - Bulk update
- `GET /api/connections/{connectionId}/audit-subscriptions/{moduleCode}` - Get subscriptions for audit

**Tiers**
- `GET /api/tiers` - List tiers with modules
- `GET /api/modules` - List modules
- `PUT /api/tiers/{id}` - Update tier
- `PUT /api/tiers/modules` - Update tier module assignments

**Assessments**
- `GET /api/assessments` - List assessments
- `GET /api/assessments/{id}` - Get assessment with findings
- `GET /api/assessments/{id}/resolved` - Get resolved findings for comparison
- `POST /api/assessments` - Create assessment
- `PUT /api/assessments/{id}` - Update assessment
- `POST /api/assessments/{id}/modules/{moduleCode}/parse` - Re-parse module findings

**Dashboard**
- `GET /api/dashboard/stats` - Dashboard stats (deduplicated findings counts)
- `GET /api/health` - Health check

### Audit API (func-tieva-audit)

- `POST /api/assessments/start` - Run assessment
  - Body: `{ "connectionId": "guid", "module": "NETWORK" }`
  - Runs audit against subscriptions that have tiers with the module enabled
  - Uploads results to blob storage
  - Parses Excel and stores findings in database
  - Updates CustomerFindings for deduplication

## Database Schema

### Core Tables
- **ServiceTiers** - Premium, Advanced, Standard, Ad-hoc
- **AssessmentModules** - NETWORK, BACKUP, COST, IDENTITY, POLICY, RESOURCE, RESERVATION
- **TierModules** - Maps tiers to modules with frequency
- **Customers** - Customer records
- **AzureConnections** - Customer Azure connections (tenant, client ID, secret ref)
- **CustomerSubscriptions** - Synced subscriptions with tier assignment

### Assessment Tables
- **Assessments** - Assessment records with scores
- **AssessmentModuleResults** - Per-module results with BlobPath and score
- **Findings** - Individual findings per assessment (with changeStatus: New/Recurring)
- **CustomerFindings** - Deduplicated findings across all assessments (status: Open/Resolved)

## Deployment Commands

**Deploy .NET API:**
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions"
.\deploy.ps1
```

**Deploy PowerShell Audit Function:**
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Audit"
func azure functionapp publish func-tieva-audit
```

**Deploy Portal:**
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal"
git add -A
git commit -m "message"
git push
```

## Current State (2024-12-21)

### Working Features
- ✅ Entra ID SSO login
- ✅ Customer CRUD (add, edit, delete)
- ✅ Connection management (add, validate, sync, edit, delete)
- ✅ Subscription configuration (tier, environment, scope)
- ✅ Service tier configuration with module matrix
- ✅ Run assessments from portal UI (all 7 modules)
- ✅ Results stored in blob storage
- ✅ Automatic Excel parsing into Findings table
- ✅ CustomerFindings deduplication (Open/Resolved tracking)
- ✅ Change tracking (New/Recurring findings)
- ✅ Dashboard with deduplicated stats
- ✅ Customer detail page with consolidated findings
- ✅ Remediation Roadmap (3-wave priority view)
- ✅ Recommendations tab
- ✅ Assessment detail with Changes tab
- ✅ Module sub-tabs for filtering
- ✅ Average score display across all assessments

### Performance Optimizations (Dec 2024)
- ✅ Parallel API calls with Promise.all()
- ✅ O(1) lookup Maps for customers/connections
- ✅ Debounced filter functions
- ✅ DOM reference caching
- ✅ Scalability documentation added

### Assessment Modules
| Module | Status | Notes |
|--------|--------|-------|
| NETWORK | ✅ Working | Full end-to-end |
| IDENTITY | ✅ Working | Full end-to-end |
| BACKUP | ✅ Working | Full end-to-end |
| COST | ✅ Working | Full end-to-end |
| POLICY | ✅ Working | Full end-to-end |
| RESOURCE | ✅ Working | Full end-to-end |
| RESERVATION | ✅ Working | Full end-to-end |

### Next Steps
1. **PDF/Excel Export** - Generate downloadable reports
2. **Scheduling** - Automated assessment runs based on tier frequency
3. **Email notifications** - Alert on new high-severity findings
4. **Pagination** - When data volume increases (see SCALABILITY_NOTES.md)

## Test Data

**Connection ID**: `e08a13c4-4696-49a9-98e5-d19a67e7caba`
**Customer**: TIEVA Dev
**Tenant**: `0976df27-8d6a-4158-998c-8dd6650fd495`

**Test Assessment:**
```powershell
$body = @{
    connectionId = "e08a13c4-4696-49a9-98e5-d19a67e7caba"
    module = "NETWORK"
} | ConvertTo-Json

Invoke-WebRequest -Uri "https://func-tieva-audit.azurewebsites.net/api/assessments/start" -Method Post -Body $body -ContentType "application/json" -TimeoutSec 600
```

## App Registration

- **App ID**: 5edd71d4-a519-4900-924c-78c3f0d24fdf
- **Name**: TIEVA Portal
- **Tenant**: 0976df27-8d6a-4158-998c-8dd6650fd495
- **Redirect URI**: https://ambitious-wave-092ef1703.3.azurestaticapps.net/.auth/login/aad/callback
