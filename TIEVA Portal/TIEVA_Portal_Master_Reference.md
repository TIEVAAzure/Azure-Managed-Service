# TIEVA Portal - Master Reference

**Last Updated:** January 2025 (v2.1)

## Quick Reference

| Item | Value |
|------|-------|
| **Portal URL** | https://ambitious-wave-092ef1703.3.azurestaticapps.net |
| **API URL** | https://func-tievaportal-6612.azurewebsites.net/api |
| **Audit API URL** | https://func-tieva-audit.azurewebsites.net/api |
| **GitHub** | https://github.com/TIEVAAzure/tieva-portal |
| **Resource Group** | rg-tievaPortal-prod (UK South) |
| **Test Connection ID** | e08a13c4-4696-49a9-98e5-d19a67e7caba |

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                         TIEVA Portal                                │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌──────────────┐     ┌──────────────────┐     ┌────────────────┐  │
│  │ Static Web   │────▶│ .NET Function    │────▶│ SQL Database   │  │
│  │ App (SPA)    │     │ App (API)        │     │ TievaPortal    │  │
│  │              │     │ func-tievaPortal │     │                │  │
│  └──────────────┘     └────────┬─────────┘     └────────────────┘  │
│                                │                                    │
│                                ▼                                    │
│                       ┌────────────────┐                           │
│                       │ Key Vault      │◀──────────────────────┐   │
│                       │ (SP Secrets)   │                       │   │
│                       └────────────────┘                       │   │
│                                                                │   │
│  ┌──────────────────────────────────────────────────────────┐  │   │
│  │ PowerShell Function App (func-tieva-audit)              │  │   │
│  │ ┌─────────────────┐    ┌─────────────────────────────┐  │  │   │
│  │ │ StartAssessment │───▶│ Scripts/                    │  │──┘   │
│  │ │ (HTTP Trigger)  │    │ - NetworkAudit.ps1          │  │      │
│  │ └─────────────────┘    │ - BackupAudit.ps1           │  │      │
│  │         │              │ - CostManagementAudit.ps1   │  │      │
│  │         ▼              │ - IdentityAudit.ps1         │  │      │
│  │ ┌─────────────────┐    │ - PolicyAudit.ps1           │  │      │
│  │ │ Blob Storage    │    │ - ResourceAudit.ps1         │  │      │
│  │ │ (audit-results) │    │ - ReservationAudit.ps1      │  │      │
│  │ └─────────────────┘    │ - SecurityAudit.ps1         │  │      │
│  │                        │ - PatchAudit.ps1            │  │      │
│  │                        │ - PerformanceAudit.ps1      │  │      │
│  │                        │ - ComplianceAudit.ps1       │  │      │
│  │                        └─────────────────────────────┘  │      │
│  └──────────────────────────────────────────────────────────┘      │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘
                                │
                                ▼
                    ┌───────────────────────┐
                    │ Customer Azure Tenant │
                    │ (via Service Principal)│
                    └───────────────────────┘
```

---

## Azure Resources

| Resource Type | Name | Purpose |
|---------------|------|---------|
| SQL Server | sql-tievaPortal-3234.database.windows.net | Entra-only auth |
| SQL Database | TievaPortal | Basic tier, all data |
| Key Vault | kv-tievaPortal-874 | Customer SP secrets |
| Function App (.NET) | func-tievaPortal-6612 | Main API |
| Function App (PS) | func-tieva-audit | Runs audit scripts |
| Static Web App | swa-tievaPortal-portal | Portal UI |
| Storage Account | sttieva3420 | .NET Function storage |
| Storage Account | sttievaaudit | Audit results (blob) |
| Blob Container | audit-results | Excel outputs |

### Managed Identities

| Function App | Principal ID | Roles |
|--------------|--------------|-------|
| func-tievaPortal-6612 | 35697b67-2bb0-4ddd-9b90-ac042918b10d | SQL access, Key Vault Secrets Officer |
| func-tieva-audit | a2e06bf6-61a2-46b9-92a0-0065b8721235 | Key Vault Secrets User, Storage Blob Data Contributor |

### App Registration

| Property | Value |
|----------|-------|
| App ID | 5edd71d4-a519-4900-924c-78c3f0d24fdf |
| Name | TIEVA Portal |
| Tenant | 0976df27-8d6a-4158-998c-8dd6650fd495 |
| Redirect URI | https://ambitious-wave-092ef1703.3.azurestaticapps.net/.auth/login/aad/callback |

---

## Assessment Modules (10 Active)

| Code | Name | Script | Status |
|------|------|--------|--------|
| NETWORK | Network Topology | NetworkAudit.ps1 | ✅ Working |
| BACKUP | Backup Posture | BackupAudit.ps1 | ✅ Working |
| COST | Cost Management | CostManagementAudit.ps1 | ✅ Working |
| IDENTITY | Identity & Access | IdentityAudit.ps1 | ✅ Working |
| POLICY | Policy Compliance | PolicyAudit.ps1 | ✅ Working |
| RESOURCE | Resource Inventory | ResourceAudit.ps1 | ✅ Working |
| SECURITY | Defender for Cloud | SecurityAudit.ps1 | ✅ Working |
| PATCH | VM Patch Compliance | PatchAudit.ps1 | ✅ Working |
| PERFORMANCE | Right-sizing | PerformanceAudit.ps1 | ✅ Working |
| COMPLIANCE | Regulatory Compliance | ComplianceAudit.ps1 | ✅ Working |

> **Note:** RESERVATION module removed - now uses live Azure API in FinOps tab

---

## Local File Structure

```
C:\VS Code\Azure-Managed-Service\TIEVA Portal\
├── portal\                          # Static Web App (GitHub linked)
│   ├── index.html                   # Main SPA (~180KB, 4000+ lines)
│   └── staticwebapp.config.json     # Auth config
├── functions\
│   ├── TIEVA.Functions\             # .NET 8 API
│   │   ├── Functions\
│   │   │   ├── CustomerFunctions.cs
│   │   │   ├── ConnectionFunctions.cs
│   │   │   ├── TierFunctions.cs
│   │   │   ├── SubscriptionFunctions.cs
│   │   │   ├── AssessmentFunctions.cs
│   │   │   ├── FindingsFunctions.cs
│   │   │   ├── EffortSettingsFunctions.cs
│   │   │   ├── SchedulerFunctions.cs
│   │   │   └── DashboardFunctions.cs
│   │   ├── Models\Entities.cs
│   │   ├── Services\TievaDbContext.cs
│   │   └── deploy.ps1               # Deploy script
│   └── TIEVA.Audit\                 # PowerShell Function App
│       ├── StartAssessment\run.ps1  # Main assessment trigger
│       ├── Scripts\                 # All 11 audit scripts
│       └── requirements.psd1        # Az modules + ImportExcel
├── TIEVA_Portal_*.md                # Documentation (this file)
└── TIEVA_*.html                     # Analyzer HTML tools
```

---

## Deployment Commands

### Deploy .NET API
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions"
.\deploy.ps1
```

### Deploy PowerShell Audit Function
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Audit"
func azure functionapp publish func-tieva-audit
```

### Deploy Portal (via GitHub)
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\portal"
git add .
git commit -m "message"
git push
```

---

## Testing Commands

### Health Check
```powershell
curl https://func-tievaportal-6612.azurewebsites.net/api/health
```

### List Connections
```powershell
curl https://func-tievaportal-6612.azurewebsites.net/api/connections
```

### Run Assessment
```powershell
$body = @{
    connectionId = "e08a13c4-4696-49a9-98e5-d19a67e7caba"
    modules = @("NETWORK", "BACKUP", "SECURITY")
} | ConvertTo-Json

Invoke-WebRequest -Uri "https://func-tieva-audit.azurewebsites.net/api/assessments/start" `
    -Method Post -Body $body -ContentType "application/json" -TimeoutSec 600
```

### List Assessments
```powershell
curl https://func-tievaportal-6612.azurewebsites.net/api/assessments
```

### Get Customer Findings
```powershell
curl https://func-tievaportal-6612.azurewebsites.net/api/customers/{customerId}/findings
```

---

## Current State (January 2025)

### ✅ Working Features

**Core Portal:**
- Entra ID SSO login
- Customer CRUD with cascading deletes
- Connection management (add, validate, sync, delete with cascade)
- Subscription configuration (tier, environment, scope)
- Service tier configuration with module matrix

**Assessments:**
- Run assessments from portal UI (multi-module)
- All 10 audit modules working
- Results stored in blob storage
- Findings parsed and stored in database
- Assessment deletion (single + bulk) with cascade
- Re-parse module results
- Download Excel results

**Findings & Analysis:**
- CustomerFindings aggregation across assessments
- Change tracking (New/Recurring/Resolved)
- Occurrence counting with history
- Module filter tabs (10 modules)
- Severity filtering
- **Priority Matrix** (Impact vs Effort grid)
- **Remediation Roadmap** (3-wave with auto-population)
- **Roadmap plan save/load**
- Recommendations tab
- **Finding Metadata** (effort, impact, operational)

**FinOps:**
- Cost Analysis from FOCUS parquet data
- Daily/weekly cost trends
- Service/ResourceGroup/Subscription breakdowns
- **Live Reservations** via Azure API
- **Intelligent insights** (renew/cancel/PAYG recommendations)
- Utilization tracking (1/7/30 day)
- Purchase recommendations
- **Tier-based filtering** (Advanced/Premium/Adhoc only - Standard excluded)
- **PDF Export** with professional formatting and tier filtering
- **Presentation Mode** for customer meetings with tier filtering

**Scheduling:**
- Customer next meeting date
- Pre-meeting assessment triggers
- Module frequency tracking
- Scheduling status dashboard

**Performance Optimizations:**
- Parallel API calls with Promise.all()
- O(1) lookup Maps for customers/connections
- Debounced filter functions
- DOM reference caching

---

## Reservations Tier Filtering

The Reservations tab implements tier-based filtering to show only relevant subscription data:

### Filter Logic
- **Included Tiers**: Advanced, Premium, Adhoc
- **Excluded Tiers**: Standard
- **Tenant-Level Data**: Always included (reservations without subscription info)

### Filtered Views
All reservation views apply the same tier filtering:
1. **Main Reservations Tab** - Active reservations table
2. **Presentation Mode** - Full-screen customer display
3. **PDF Export** - Professional PDF reports

### Filtered Data
- Active reservations (matched by SubscriptionId or SubscriptionName)
- Purchase recommendations (matched by scope containing subscription ID/name)
- Insights (filtered to reference only included reservations)
- Summary statistics (recalculated from filtered data)

---

## Reservations PDF Export

The Reservations tab includes PDF export functionality using jsPDF:

### PDF Contents
1. **Header** - Customer name, generation date, TIEVA branding
2. **Summary Section** - Key metrics (total, active, expiring, utilization stats)
3. **Active Reservations Table** - Name, type, SKU, quantity, term, utilization, expiry, auto-renew
4. **Purchase Recommendations Table** - Subscription, SKU, term, quantity, savings, net savings
5. **Insights Section** - Prioritized actionable recommendations with icons

### Features
- Tier filtering applied (Advanced/Premium/Adhoc only)
- Professional formatting with color-coded sections
- Automatic page breaks for large datasets
- Currency formatting (GBP)
- Date formatting for readability

---

## 🔜 Potential Enhancements
1. LogicMonitor API integration for alerts/monitoring
2. PDF report generation for all modules
3. Email notifications for due assessments
4. Trend analysis across assessments
