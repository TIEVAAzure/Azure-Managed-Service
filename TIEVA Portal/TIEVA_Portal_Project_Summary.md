# TIEVA Portal - Project Summary

**Last Updated:** January 2025 (v2.2 - Async Processing & FinOps Enhancements)

## Overview

TIEVA Portal (branded as "TIEVA CloudOps") is an Azure-hosted web application for managing Azure assessments for managed service customers. It runs 10 automated audit modules against customer Azure tenants, parses findings into a database, tracks issues over time, provides remediation roadmaps, and includes **FinOps cost analysis and reservation management**.

---

## Architecture

### Azure Resources (Resource Group: rg-tievaPortal-prod, UK South)

| Resource | Name | Purpose |
|----------|------|---------|
| SQL Server | sql-tievaPortal-3234.database.windows.net | Entra-only auth |
| SQL Database | TievaPortal | Basic tier, stores all data |
| Key Vault | kv-tievaPortal-874 | SP secrets + FinOps SAS tokens |
| Function App (.NET) | func-tievaPortal-6612 | Main API + FinOps APIs |
| Function App (PowerShell) | func-tieva-audit | Audit scripts + async processing |
| Static Web App | swa-tievaPortal-portal | Portal UI (SWA-linked) |
| Storage Account | sttieva3420 | Function App storage |
| Storage Account | sttievaaudit | Audit results + assessment queue |

### URLs

| Purpose | URL |
|---------|-----|
| Portal | https://ambitious-wave-092ef1703.3.azurestaticapps.net |
| API | https://func-tievaportal-6612.azurewebsites.net/api |
| Audit API | https://func-tieva-audit.azurewebsites.net/api |
| GitHub | https://github.com/TIEVAAzure/tieva-portal |

### Managed Identities

| Function App | Principal ID | Roles |
|--------------|--------------|-------|
| func-tievaPortal-6612 | 35697b67-2bb0-4ddd-9b90-ac042918b10d | SQL access, Key Vault Secrets Officer |
| func-tieva-audit | a2e06bf6-61a2-46b9-92a0-0065b8721235 | Key Vault Secrets User, Storage Blob Data Contributor |

---

## Assessment Modules (10 Active)

| Code | Name | Description |
|------|------|-------------|
| NETWORK | Network Topology | NSG rules, route tables, VNet peerings, public IPs |
| BACKUP | Backup Posture | VM backup status, policies, vault configuration |
| COST | Cost Management | Spending analysis, anomalies, optimization |
| IDENTITY | Identity & Access | Role assignments, PIM, guest access, MFA |
| POLICY | Policy Compliance | Azure Policy compliance state |
| RESOURCE | Resource Inventory | Orphaned resources, tagging compliance |
| SECURITY | Defender for Cloud | Security posture score, alerts, recommendations |
| PATCH | VM Patch Compliance | OS update status, missing patches |
| PERFORMANCE | Right-sizing | VM utilization, resize recommendations |
| COMPLIANCE | Regulatory Compliance | Framework scores (CIS, NIST, etc.) |

> **Note:** RESERVATION module removed from assessments - now uses live Azure API in FinOps tab

---

## Portal Features

### Customer Management
- Add/edit/delete customers with cascading deletes
- Industry classification
- Primary contact information
- Next meeting date scheduling
- Auto-scheduling toggle
- **FinOps configuration** (storage account, container, Power BI URL)

### Connection Management
- Add Azure tenant connections via Service Principal
- Validate credentials against Azure
- Sync subscriptions from Azure
- Cascading deletes (subscriptions, assessments, findings)
- Onboarding workflow with PowerShell script

### Subscription Configuration
- Assign service tiers (Premium, Advanced, Standard, Ad-hoc)
- Set environment (Production, Development, Test, Staging, Sandbox)
- Mark in/out of scope for assessments
- Tier determines which modules run and reservation visibility

### Service Tier Configuration
- Configure which modules are included per tier
- Set module frequency (Monthly, Quarterly, On-Demand)
- Visual tier-module matrix

### Assessment Execution
- Run multi-module assessments from UI
- **Async processing** via queue (avoids frontend timeouts)
- Real-time progress tracking (poll for status)
- Results stored in blob storage
- Automatic findings parsing
- Score calculation

### Findings Analysis
- **Customer Findings**: Aggregated, deduplicated across assessments
- **Change Tracking**: New, Recurring, Resolved status
- **Occurrence Counting**: Track how many times issues seen
- **Module Filtering**: 10 module tabs for focused view
- **Severity Filtering**: High, Medium, Low
- **Priority Matrix**: Impact vs Effort grid with auto-wave assignment
- **Remediation Roadmap**: 3-wave prioritized view with effort estimates
- **Recommendations**: Grouped by category

### Finding Metadata (Effort & Impact)
- Configurable metadata by module, category, or pattern
- **Effort**: baseHours + perResourceHours calculation
- **Impact Override**: Override severity-based impact assessment
- **Operational Metadata**: Downtime, change control, maintenance window
- **Complexity & Risk**: Low/Medium/High classifications
- Pattern-based matching with priorities

### FinOps Cost Analysis
- Cost Analysis from **FOCUS parquet** data
- Periods: MTD, 30/60/90 days
- Daily/weekly cost trends
- Service/ResourceGroup/Subscription breakdowns
- Week-over-week change detection
- Run Export button to trigger Azure Cost Management exports

### FinOps Reservations
- **Live Reservations** via Azure API (not Excel-based audits)
- **Async caching** (CustomerReservationCache table)
- Utilization tracking (1/7/30 day)
- **Intelligent insights engine**:
  - 🚨 Zero utilization alerts - exchange/cancel immediately
  - ✅ High utilization expiring - renew recommendation
  - ⚠️ Low utilization - PAYG comparison (breakeven ~65%)
  - 🚫 Low utilization + auto-renew ON - disable warning
  - 💰 Purchase recommendations with annual savings
- **Tier-based filtering** (Advanced/Premium/Adhoc only - Standard excluded)
- **PDF Export** with professional formatting
- **Presentation Mode** for customer meetings

### Scheduling
- Customer next meeting date tracking
- Pre-meeting assessment triggers
- Module frequency tracking based on tier
- Scheduling status dashboard

---

## Database Schema

### Core Tables
- **Customers** - Customer records with scheduling + FinOps settings
- **AzureConnections** - Tenant connections with SP credentials
- **CustomerSubscriptions** - Synced subscriptions with tier assignment
- **ServiceTiers** - Premium, Advanced, Standard, Ad-hoc
- **AssessmentModules** - 10 audit module definitions
- **TierModules** - Tier-to-module mapping with frequency

### Assessment Tables
- **Assessments** - Assessment run records with scores
- **AssessmentModuleResults** - Per-module results with blob paths
- **Findings** - Individual findings from each assessment

### Aggregation Tables
- **CustomerFindings** - Deduplicated findings per customer
- **FindingMetadata** - Effort/impact/operational configuration
- **CustomerRoadmapPlans** - Saved wave assignments for remediation planning

### Caching Tables
- **CustomerReservationCache** - Async reservation data caching

### Deprecated Tables
- **EffortSettings** - Replaced by FindingMetadata

---

## Key Files Reference

### C# Functions (TIEVA.Functions)

| File | Purpose |
|------|---------|
| CustomerFunctions.cs | Customer CRUD with cascading deletes |
| ConnectionFunctions.cs | Azure connections, validation, sync |
| TierFunctions.cs | Service tier configuration |
| SubscriptionFunctions.cs | Subscription management |
| AssessmentFunctions.cs | Assessment execution, findings |
| FinOpsFunctions.cs | Cost analysis, reservations, exports |
| SettingsFunctions.cs | FindingMetadata CRUD |
| SchedulerFunctions.cs | Pre-meeting assessment scheduling |
| DashboardFunctions.cs | Portal dashboard stats |

### PowerShell Functions (TIEVA.Audit)

| Function | Trigger | Purpose |
|----------|---------|---------|
| StartAssessment | HTTP | Queue assessment for processing |
| ProcessAssessment | Queue | Run audit scripts (async) |
| SetupFinOps | HTTP | Configure Cost Management exports |

### Audit Scripts

| Script | Output |
|--------|--------|
| NetworkAudit.ps1 | Network_Audit.xlsx |
| BackupAudit.ps1 | Backup_Audit.xlsx |
| CostManagementAudit.ps1 | Cost_Management_Audit.xlsx |
| IdentityAudit.ps1 | Identity_Audit.xlsx |
| PolicyAudit.ps1 | Policy_Audit.xlsx |
| ResourceAudit.ps1 | Resource_Audit.xlsx |
| SecurityAudit.ps1 | Security_Audit.xlsx |
| PatchAudit.ps1 | Patch_Audit.xlsx |
| PerformanceAudit.ps1 | Performance_Audit.xlsx |
| ComplianceAudit.ps1 | Compliance_Audit.xlsx |

---

## Deployment Commands

**Deploy .NET API:**
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions"
dotnet build
func azure functionapp publish func-tievaportal-6612
```

**Deploy PowerShell Audit Function:**
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Audit"
func azure functionapp publish func-tieva-audit
```

**Deploy Portal:**
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\portal"
git add -A
git commit -m "message"
git push
```

---

## Current State (January 2025)

### ✅ Fully Working
- Entra ID SSO login
- Customer CRUD with cascading deletes
- Connection management with cascading deletes
- Subscription configuration with tiers
- Service tier configuration with module matrix
- All 10 audit modules executing
- **Async assessment processing** (queue-based)
- Findings parsing and storage
- CustomerFindings aggregation
- Change tracking (New/Recurring/Resolved)
- Module filter tabs
- **Priority Matrix** with Impact vs Effort grid
- **Remediation roadmap** with wave auto-population
- **Roadmap plan save/load**
- Recommendations display
- **Finding Metadata** (effort, impact, operational)
- Scheduling status
- Assessment deletion (single + bulk)
- Excel download
- Re-parse functionality
- **FinOps Cost Analysis** (FOCUS parquet data with MTD support)
- **FinOps Reservations** (live Azure API with intelligent insights)
- **Async reservation caching**
- **Reservations Tier Filtering** (Advanced/Premium/Adhoc only)
- **Reservations PDF Export**
- **Reservations Presentation Mode**

### Performance Optimizations
- Parallel API calls with Promise.all()
- O(1) lookup Maps for customers/connections
- Debounced filter functions
- DOM reference caching

---

## Test Data

**Connection ID**: `e08a13c4-4696-49a9-98e5-d19a67e7caba`
**Customer**: TIEVA Dev
**Tenant**: `0976df27-8d6a-4158-998c-8dd6650fd495`

**Test Assessment:**
```powershell
$body = @{
    connectionId = "e08a13c4-4696-49a9-98e5-d19a67e7caba"
    modules = @("NETWORK", "BACKUP", "SECURITY")
} | ConvertTo-Json

Invoke-WebRequest -Uri "https://func-tieva-audit.azurewebsites.net/api/assessments/start" `
    -Method Post -Body $body -ContentType "application/json"
```

---

## App Registration

| Property | Value |
|----------|-------|
| App ID | 5edd71d4-a519-4900-924c-78c3f0d24fdf |
| Name | TIEVA Portal |
| Tenant | 0976df27-8d6a-4158-998c-8dd6650fd495 |
| Redirect URI | https://ambitious-wave-092ef1703.3.azurestaticapps.net/.auth/login/aad/callback |

---

## FinOps Requirements

### Customer Azure Tenant Setup
1. **Cost Management Export** configured to export FOCUS parquet to storage account
2. **Storage Account** with container for cost data (typically `ingestion`)
3. **SAS Token** with read+list access to the container (stored in Key Vault)
4. **Reservation Reader** role at tenant/billing level for live reservation data

### Portal Configuration
- Set `FinOpsStorageAccount` on customer record
- Set `FinOpsContainer` (default: `ingestion`)
- Optionally set `FinOpsPowerBIUrl` for Power BI report link

### Reservations Tier Filtering
The Reservations tab automatically filters data based on subscription service tiers:
- **Included Tiers**: Advanced, Premium, Adhoc
- **Excluded Tiers**: Standard
- **Scope**: Filtering applies to all views (main tab, presentation mode, PDF export)
- **Tenant-Level**: Reservations without subscription info (tenant-level) are always included
- **Statistics**: All summary metrics are recalculated from filtered data

---

## Architecture Decisions

### SWA Linking
- Static Web App is linked to func-tievaportal-6612
- Browser requests proxy through SWA (no API keys needed in frontend)
- External API calls blocked by SWA linking
- **Solution**: Audit functions write directly to database, not via API callbacks

### Async Processing
- Assessments use queue-based processing to avoid frontend timeouts
- StartAssessment queues job, returns immediately
- ProcessAssessment runs audits asynchronously
- Portal polls for completion status

### Entity Framework Mappings
- `Finding.FindingText` maps to database column `Finding`
- PowerShell scripts must insert into `Finding` column (not `FindingText`)

---

## Future Enhancements

1. **LogicMonitor Integration** - Pull alerts/monitoring data via REST API v3
2. **PDF Report Generation** - Customer-ready PDF reports for all modules
3. **Email Notifications** - Alerts for due assessments
4. **Trend Analysis** - Score trends over time
5. **Multi-tenant Cost Comparison** - Compare costs across customers
