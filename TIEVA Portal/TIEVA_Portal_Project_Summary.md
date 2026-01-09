# TIEVA Portal - Project Summary

**Last Updated:** January 2025 (v2.1 - Reservations Tier Filtering & PDF Export)

## Overview

TIEVA Portal is an Azure-hosted web application for managing Azure assessments for managed service customers. It runs 10 automated audit modules against customer Azure tenants, parses findings into a database, tracks issues over time, provides remediation roadmaps, and now includes **FinOps cost analysis and reservation management**.

---

## Architecture

### Azure Resources (Resource Group: rg-tievaPortal-prod, UK South)

| Resource | Name | Purpose |
|----------|------|---------|
| SQL Server | sql-tievaPortal-3234.database.windows.net | Entra-only auth |
| SQL Database | TievaPortal | Basic tier, stores all data |
| Key Vault | kv-tievaPortal-874 | Stores customer Service Principal secrets + FinOps SAS tokens |
| Function App (.NET) | func-tievaPortal-6612 | Main API + FinOps APIs |
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
- **func-tievaPortal-6612**: `35697b67-2bb0-4ddd-9b90-ac042918b10d` (SQL access, Key Vault Secrets Officer)
- **func-tieva-audit**: `a2e06bf6-61a2-46b9-92a0-0065b8721235` (Key Vault Secrets User, Storage Blob Data Contributor)

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

### Service Tier Configuration
- Configure which modules are included per tier
- Set module frequency (Monthly, Quarterly, On-Demand)
- Visual tier-module matrix

### Assessment Execution
- Run multi-module assessments from UI
- Real-time progress tracking
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
- **Effort estimation**: Base hours + per-resource hours
- **Impact override**: Override severity-based impact
- **Operational metadata**: Downtime required, change control, maintenance window
- Default owner assignment
- Priority-based matching rules

### FinOps Integration
**Cost Analysis Sub-Tab:**
- Daily/weekly/monthly cost trends from FOCUS parquet data
- Cost breakdown by service category, resource group, subscription
- Anomaly detection and week-over-week changes
- Interactive treemap visualization
- Drill-down to resource level
- Export run trigger

**Reservations Sub-Tab (Live API):**
- Real-time reservation data from Azure APIs
- **Tier-based filtering**: Only shows reservations for Advanced, Premium, and Adhoc tier subscriptions (Standard tier excluded)
- Utilization metrics (1-day, 7-day, 30-day)
- **Intelligent insights with recommendations**:
  - 🚨 Zero utilization - exchange/cancel immediately
  - ✅ High utilization expiring - renew recommendation
  - ⚠️ Low utilization - PAYG comparison analysis
  - 🚫 Low utilization expiring - don't renew warning
  - 💰 Purchase recommendations with annual savings
- Expiry tracking with days remaining
- Auto-renew status visibility
- Purchase recommendations from Azure
- **PDF Export**: Generate professional PDF reports with:
  - Summary statistics and key metrics
  - Active reservations table with utilization
  - Purchase recommendations with savings analysis
  - Actionable insights prioritized by impact
  - Tier filtering applied (Advanced/Premium/Adhoc only)
- **Presentation Mode**: Full-screen display optimized for customer meetings with tier filtering applied

### Scheduling
- Customer next meeting dates
- Pre-meeting assessment triggers (3 days before)
- Module due date tracking based on tier frequency
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
- **FindingMetadata** - Effort/impact/operational configuration (replaces EffortSettings)
- **CustomerRoadmapPlans** - Saved wave assignments for remediation planning

---

## Deployment Commands

**Deploy .NET API:**
```powershell
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions"
dotnet publish -c Release
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
- **FinOps Cost Analysis** (FOCUS parquet data)
- **FinOps Reservations** (live Azure API with intelligent insights)
- **Reservations Tier Filtering** (Advanced/Premium/Adhoc only - Standard excluded)
- **Reservations PDF Export** (professional reports with tier filtering)
- **Reservations Presentation Mode** (customer-ready full-screen display)

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
    -Method Post -Body $body -ContentType "application/json" -TimeoutSec 600
```

---

## App Registration

- **App ID**: 5edd71d4-a519-4900-924c-78c3f0d24fdf
- **Name**: TIEVA Portal
- **Tenant**: 0976df27-8d6a-4158-998c-8dd6650fd495
- **Redirect URI**: https://ambitious-wave-092ef1703.3.azurestaticapps.net/.auth/login/aad/callback

---

## FinOps Requirements

### Customer Azure Tenant Setup
1. **Cost Management Export** configured to export FOCUS parquet to storage account
2. **Storage Account** with container for cost data (typically `ingestion`)
3. **SAS Token** with read access to the container (stored in Key Vault)
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

## Future Enhancements

1. **LogicMonitor Integration** - Pull alerts/monitoring data via REST API v3
2. **PDF Report Generation** - Customer-ready PDF reports for all modules
3. **Email Notifications** - Alerts for due assessments
4. **Trend Analysis** - Score trends over time
5. **Bulk Operations** - Multi-customer assessment runs
