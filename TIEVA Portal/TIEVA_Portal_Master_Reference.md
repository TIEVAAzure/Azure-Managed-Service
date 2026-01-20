# TIEVA Portal - Master Reference

**Last Updated:** January 2026 (v2.4 - Performance V2 Metrics Fix)

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
│                         TIEVA CloudOps Portal                       │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  ┌──────────────┐     ┌──────────────────┐     ┌────────────────┐  │
│  │ Static Web   │────▶│ .NET Function    │────▶│ SQL Database   │  │
│  │ App (SPA)    │     │ App (API)        │     │ TievaPortal    │  │
│  │              │     │ func-tievaPortal │     │                │  │
│  └──────────────┘     └────────┬─────────┘     └────────────────┘  │
│         │                      │                                    │
│         │ SWA-Linked           │                                    │
│         │ (/api proxy)         ▼                                    │
│         │             ┌────────────────┐                           │
│         │             │ Key Vault      │◀──────────────────────┐   │
│         │             │ (SP Secrets +  │                       │   │
│         │             │  SAS Tokens)   │                       │   │
│         │             └────────────────┘                       │   │
│         │                                                      │   │
│  ┌──────┴───────────────────────────────────────────────────┐  │   │
│  │ PowerShell Function App (func-tieva-audit)              │  │   │
│  │ ┌─────────────────┐    ┌─────────────────────────────┐  │  │   │
│  │ │ StartAssessment │───▶│ Assessment Queue            │  │──┘   │
│  │ │ (HTTP Trigger)  │    │ (async processing)          │  │      │
│  │ └─────────────────┘    └──────────┬──────────────────┘  │      │
│  │                                   │                      │      │
│  │ ┌─────────────────┐               ▼                      │      │
│  │ │ProcessAssessment│    ┌─────────────────────────────┐  │      │
│  │ │ (Queue Trigger) │───▶│ Scripts/                    │  │      │
│  │ └─────────────────┘    │ - NetworkAudit.ps1          │  │      │
│  │         │              │ - BackupAudit.ps1           │  │      │
│  │         ▼              │ - CostManagementAudit.ps1   │  │      │
│  │ ┌─────────────────┐    │ - IdentityAudit.ps1         │  │      │
│  │ │ Blob Storage    │    │ - PolicyAudit.ps1           │  │      │
│  │ │ (audit-results) │    │ - ResourceAudit.ps1         │  │      │
│  │ └─────────────────┘    │ - SecurityAudit.ps1         │  │      │
│  │                        │ - PatchAudit.ps1            │  │      │
│  │ ┌─────────────────┐    │ - PerformanceAudit.ps1      │  │      │
│  │ │ SetupFinOps     │    │ - ComplianceAudit.ps1       │  │      │
│  │ │ (HTTP Trigger)  │    └─────────────────────────────┘  │      │
│  │ └─────────────────┘                                      │      │
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
| Key Vault | kv-tievaPortal-874 | SP secrets + FinOps SAS tokens |
| Function App (.NET) | func-tievaPortal-6612 | Main API + FinOps APIs |
| Function App (PS) | func-tieva-audit | Audit scripts + FinOps setup |
| Static Web App | swa-tievaPortal-portal | Portal UI (SWA-linked to API) |
| Storage Account | sttieva3420 | .NET Function storage |
| Storage Account | sttievaaudit | Audit results (blob) |
| Blob Container | audit-results | Excel outputs |
| Storage Queue | assessment-queue | Async assessment processing |

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

> **Note:** RESERVATION module removed from assessments - now uses live Azure API in FinOps tab. ReservationAudit.ps1 still exists but is not used.

---

## Performance Monitoring V2 - LogicMonitor Integration

### Overview

Performance V2 provides real-time metrics from LogicMonitor with intelligent SKU recommendations. It replaces the static Performance audit module with live data.

### Database Tables

| Table | Purpose |
|-------|--------|
| `LMResourceTypes` | 22 pre-seeded resource type definitions |
| `LMMetricMappings` | Which metrics to fetch per resource type |
| `LMDeviceMetricsV2` | Actual device metrics with flexible JSON storage |
| `LMDeviceMetricHistory` | Daily aggregates for 90-day graphs |
| `AzureSkuFamilies` | SKU family definitions for recommendations |

### API Endpoints

| Method | Route | Purpose |
|--------|-------|--------|
| GET | `/v2/performance/customers/{id}/summary` | Summary by resource type |
| GET | `/v2/performance/customers/{id}/devices/{deviceId}` | Single device metrics |
| GET | `/v2/performance/customers/{id}/devices/{deviceId}/history` | 90-day history |
| POST | `/v2/performance/customers/{id}/sync/run` | Run sync synchronously |
| GET | `/v2/performance/customers/{id}/sync/status` | Check sync progress |

### Metrics Calculation Logic

**Windows Server (Working)**:
- **CPU**: Uses `CPUBusyPercent` datapoint with percentage validation (0-100 range)
- **Memory**: Tries `MemoryUtilizationPercent` first, falls back to `100 - (FreePhysicalMemory / TotalVisibleMemorySize * 100)`
- **Disk**: Processes ALL disk instances via `FetchAllDiskMetricsAsync()`
  - Auto-detects unit mismatch (Capacity in bytes, FreeSpace in GB)
  - Converts bytes to GB when `capacity > 1 billion && free < 100,000`
  - Stores each drive: `Disk (C:)`, `Disk (D:)`, etc.
  - Overall `Disk` shows worst (highest usage) drive

**Known Limitations (Azure PaaS)**:
- Storage latency metrics are milliseconds, not percentages
- Azure Disk IOPS%/Bandwidth% datapoints don't exist in LogicMonitor
- Some Azure VM metrics exceed 0-100 range (raw counters)
- See `ISSUES_TRACKER.md` for details

### Frontend Display

Device modal shows:
1. **Main cards**: CPU, Memory, Disk (overall)
2. **Individual Disks section**: All drives with color-coded status
3. **90-day trend charts** (if history data available)
4. **SKU recommendation** badge

---

## Local File Structure

```
C:\VS Code\Azure-Managed-Service\TIEVA Portal\
├── portal\                          # Static Web App (GitHub linked)
│   ├── index.html                   # Main SPA (~200KB, 4000+ lines)
│   ├── staticwebapp.config.json     # Auth config
│   └── CHANGELOG.md                 # Portal change history
├── functions\
│   ├── TIEVA.Functions\             # .NET 8 API
│   │   ├── Functions\
│   │   │   ├── CustomerFunctions.cs
│   │   │   ├── ConnectionFunctions.cs
│   │   │   ├── TierFunctions.cs
│   │   │   ├── SubscriptionFunctions.cs
│   │   │   ├── AssessmentFunctions.cs
│   │   │   ├── FinOpsFunctions.cs       # FinOps + Reservations APIs
│   │   │   ├── SettingsFunctions.cs     # FindingMetadata CRUD
│   │   │   ├── SchedulerFunctions.cs
│   │   │   ├── DashboardFunctions.cs
│   │   │   └── AuditProxyFunctions.cs
│   │   ├── Models\Entities.cs
│   │   ├── Services\TievaDbContext.cs
│   │   └── deploy.ps1               # Deploy script
│   └── TIEVA.Audit\                 # PowerShell Function App
│       ├── StartAssessment\         # HTTP trigger - queues assessment
│       │   ├── function.json
│       │   └── run.ps1
│       ├── ProcessAssessment\       # Queue trigger - runs audit
│       │   ├── function.json
│       │   └── run.ps1
│       ├── SetupFinOps\             # HTTP trigger - configures Cost Exports
│       │   ├── function.json
│       │   └── run.ps1
│       ├── Scripts\                 # All 11 audit scripts + README
│       │   ├── README.md            # Module development guide
│       │   ├── NetworkAudit.ps1
│       │   ├── BackupAudit.ps1
│       │   ├── ... (10 more scripts)
│       │   └── TIEVA_Master_Audit.ps1
│       └── requirements.psd1        # Az modules + ImportExcel
├── scripts\                         # Utility scripts
├── PowerBI-storage\                 # Power BI templates
├── TIEVA_Portal_*.md                # Documentation files
├── SQLCommands.md                   # SQL reference
├── tieva-config.json                # Configuration reference
├── TIEVA-Onboarding.ps1             # Customer onboarding script
└── Build.ps1                        # Build/deploy helper
```

---

## C# Function Files Reference

| File | Purpose |
|------|---------|
| CustomerFunctions.cs | Customer CRUD with cascading deletes |
| ConnectionFunctions.cs | Azure connection management, validation, sync |
| TierFunctions.cs | Service tier configuration, tier-module matrix |
| SubscriptionFunctions.cs | Customer subscription management |
| AssessmentFunctions.cs | Assessment execution, findings, module results |
| FinOpsFunctions.cs | Cost analysis, reservations, SAS tokens, exports |
| SettingsFunctions.cs | FindingMetadata CRUD |
| SchedulerFunctions.cs | Pre-meeting assessment scheduling |
| DashboardFunctions.cs | Portal dashboard stats |
| AuditProxyFunctions.cs | Proxy for audit function calls |
| LogicMonitorFunctions.cs | LM alerts, devices, sync (V1) |
| LMPerformanceV2Functions.cs | Performance V2 sync, metrics, admin endpoints |

---

## Deployment Commands

### Deploy .NET API
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions"
dotnet build
func azure functionapp publish func-tievaportal-6612
```

### Deploy PowerShell Audit Function
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Audit"
func azure functionapp publish func-tieva-audit
```

### Deploy Portal (via GitHub)
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\portal"
git add -A
git commit -m "message"
git push
```

> **Note:** SWA has aggressive caching. If git shows clean tree but changes aren't showing, add a timestamp comment to index.html to force cache invalidation.

---

## Testing Commands

### Health Check
```powershell
Invoke-RestMethod https://func-tievaportal-6612.azurewebsites.net/api/health
```

### List Connections
```powershell
Invoke-RestMethod https://func-tievaportal-6612.azurewebsites.net/api/connections
```

### Run Assessment (Async)
```powershell
$body = @{
    connectionId = "e08a13c4-4696-49a9-98e5-d19a67e7caba"
    modules = @("NETWORK", "BACKUP", "SECURITY")
} | ConvertTo-Json

Invoke-WebRequest -Uri "https://func-tieva-audit.azurewebsites.net/api/assessments/start" `
    -Method Post -Body $body -ContentType "application/json"
```

### List Assessments
```powershell
Invoke-RestMethod https://func-tievaportal-6612.azurewebsites.net/api/assessments
```

### Get Customer Findings
```powershell
Invoke-RestMethod "https://func-tievaportal-6612.azurewebsites.net/api/customers/{customerId}/findings"
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
- **Async processing** via queue (addresses timeout issues)
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
- **Finding Metadata** (effort, impact, operational config)

**FinOps:**
- Cost Analysis from FOCUS parquet data
- Daily/weekly cost trends (MTD, 30/60/90 days)
- Service/ResourceGroup/Subscription breakdowns
- **Live Reservations** via Azure API
- **Async reservation caching** (CustomerReservationCache)
- **Intelligent insights** (renew/cancel/PAYG recommendations)
- Utilization tracking (1/7/30 day)
- Purchase recommendations
- **Tier-based filtering** (Advanced/Premium/Adhoc only - Standard excluded)
- **PDF Export** with professional formatting and tier filtering
- **Presentation Mode** for customer meetings with tier filtering
- **SetupFinOps function** for configuring Cost Management exports

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

## Async Assessment Processing

Assessments use queue-based async processing to avoid frontend timeout issues:

```
1. Portal calls POST /api/assessments/start
2. StartAssessment creates assessment record, queues job
3. Returns immediately with assessmentId + "Processing" status
4. ProcessAssessment (queue trigger) runs audits
5. Portal polls GET /api/assessments/{id} for status
6. UI updates when status = "Completed"
```

**Queue:** `assessment-queue` in storage account `sttievaaudit`

---

## Reservations Tier Filtering

The Reservations tab implements tier-based filtering:

**Filter Logic:**
- **Included Tiers:** Advanced, Premium, Adhoc
- **Excluded Tiers:** Standard
- **Tenant-Level Data:** Always included

**Filtered Views:** Main tab, Presentation Mode, PDF Export

**Filtered Data:** Active reservations, purchase recommendations, insights, summary statistics

---

## Reservations PDF Export

PDF export using jsPDF includes:

1. **Header** - Customer name, date, TIEVA branding
2. **Summary** - Key metrics (total, active, expiring, utilization)
3. **Active Reservations Table** - Name, type, SKU, quantity, term, utilization, expiry
4. **Purchase Recommendations** - Subscription, SKU, savings
5. **Insights Section** - Actionable recommendations with icons

---

## Key Architecture Decisions

### SWA Linking
- Static Web App is linked to func-tievaportal-6612
- Browser requests to `/api/*` proxy through SWA (no API keys needed)
- External API calls (e.g., from func-tieva-audit) blocked by SWA linking
- Solution: Audit functions write directly to database, not via API callbacks

### Entity Framework Column Mappings
- `Finding.FindingText` maps to database column `Finding` (not `FindingText`)
- `CustomerFinding.FindingText` maps to database column `Finding`
- Always align PowerShell inserts with existing EF mappings

### Findings Worksheet Standard
Every audit module must have a `Findings` worksheet with columns:
- SubscriptionName, SubscriptionId, Severity, Category
- ResourceType, ResourceName, ResourceId, Detail, Recommendation

---

## Known Issues & Workarounds

| Issue | Workaround |
|-------|------------|
| SWA caching | Add timestamp comment to force refresh |
| Frontend timeouts | Use async queue processing |
| External API calls blocked | Direct database access from audit functions |
| Column name mismatches | Align with EF mappings in TievaDbContext.cs |

---

## 🔜 Potential Enhancements

1. LogicMonitor API integration for alerts/monitoring
2. PDF report generation for all assessment modules
3. Email notifications for due assessments
4. Trend analysis charts across assessments
5. Multi-tenant cost comparison dashboards
