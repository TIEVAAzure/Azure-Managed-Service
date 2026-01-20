# TIEVA Portal - Database Schema

**Last Updated:** January 2026 (v2.3 - Performance V2 Tables)

## Connection Details

| Property | Value |
|----------|-------|
| Server | sql-tievaPortal-3234.database.windows.net |
| Database | TievaPortal |
| Auth | Entra-only (no SQL auth) |
| Tier | Basic |

---

## Schema Overview

```
┌─────────────────┐     ┌─────────────────┐
│ ServiceTiers    │     │AssessmentModules│
└────────┬────────┘     └────────┬────────┘
         │                       │
         └───────┬───────────────┘
                 ▼
         ┌──────────────┐
         │ TierModules  │
         └──────────────┘

┌─────────────────┐                                            ┌─────────────────┐
│   Customers     │◄───────────────────────────────────────────┤ LMResourceTypes │
└────────┬────────┘                                            └────────┬────────┘
         │                                                            │
         ├──────────────┐                                             │
         ▼              ▼                                             ▼
┌─────────────────┐ ┌──────────────────────────┐         ┌──────────────────┐
│AzureConnections │ │ CustomerRoadmapPlans (1:1)│         │ LMMetricMappings │
└────────┬────────┘ └──────────────────────────┘         └──────────────────┘
         │                                          
         ▼                                          ┌──────────────────┐
┌──────────────────────┐                          │ LMDeviceMetricsV2│
│ CustomerSubscriptions │                          └──────────────────┘
└──────────────────────┘
```

---

## Performance V2 Tables (NEW)

### LMResourceTypes
Resource type definitions for LogicMonitor device categorization.

| Column | Type | Description |
|--------|------|-------------|
| Id | int | PK (identity) |
| Code | nvarchar(50) | Unique identifier (WindowsServer, AzureVM, etc.) |
| DisplayName | nvarchar(100) | UI display name |
| Category | nvarchar(50) | Grouping: Compute, Database, Storage, Network, Platform, Container, Identity, Monitoring, Other |
| Icon | nvarchar(20) | Emoji icon (🖥️, 🗄️, etc.) |
| DetectionPatternsJson | nvarchar(max) | JSON array of datasource patterns for matching |
| SortOrder | int | Priority for matching (lower = checked first) |
| ShowInDashboard | bit | Include in summary dashboard |
| HasPerformanceMetrics | bit | Has performance metrics to fetch |
| IsActive | bit | Active flag |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

**Key Points:**
- **DetectionPatternsJson**: `["WinCPU", "WinMemory"]` - device matches if ANY datasource contains ANY pattern
- **SortOrder**: Lower values checked first. MetadataOnly (900) should be last fallback
- 22 pre-seeded types covering Windows, Linux, Azure VMs, SQL, Storage, Network, etc.

**Sample Data:**
| Code | DisplayName | Category | SortOrder | DetectionPatterns |
|------|-------------|----------|-----------|-------------------|
| WindowsServer | Windows Server | Compute | 10 | WinCPU, WinMemory, WinOS |
| AzureVM | Azure Virtual Machine | Compute | 30 | Microsoft_Azure_VirtualMachine |
| AzureSQL | Azure SQL Database | Database | 30 | Microsoft_Azure_Sql |
| AzureDisk | Azure Managed Disk | Storage | 35 | Microsoft_Azure_Disk |
| MetadataOnly | Metadata Only | Other | 900 | Whois_TTL_Expiry |

---

### LMMetricMappings
Metric definitions per resource type - which datapoints to fetch and how to evaluate them.

| Column | Type | Description |
|--------|------|-------------|
| Id | int | PK (identity) |
| ResourceTypeId | int | FK to LMResourceTypes |
| MetricName | nvarchar(100) | Internal name (CPU, Memory, IOPS) |
| DisplayName | nvarchar(100) | UI display name |
| Unit | nvarchar(20) | Unit of measurement (%, IOPS, MB, ms) |
| DatasourcePatternsJson | nvarchar(max) | JSON array of datasource name patterns |
| DatapointPatternsJson | nvarchar(max) | JSON array of datapoint name patterns |
| WarningThreshold | decimal(10,2) | Warning threshold value |
| CriticalThreshold | decimal(10,2) | Critical threshold value |
| InvertThreshold | bit | True = lower is worse (availability), False = higher is worse (CPU) |
| OversizedBelow | decimal(10,2) | Below this = oversized (for rightsizing) |
| UndersizedAbove | decimal(10,2) | Above this = undersized (for rightsizing) |
| SortOrder | int | Display order |
| IsActive | bit | Active flag |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

**Key Points:**
- **Pattern Matching**: Datasource name must contain ANY DatasourcePattern AND datapoint name must contain ANY DatapointPattern
- **InvertThreshold**: For availability metrics where 99% is good and 50% is bad, set InvertThreshold=true
- Metrics can be added via UI without code changes

**Sample Data:**
| ResourceType | MetricName | DatasourcePatterns | DatapointPatterns | Thresholds |
|--------------|------------|-------------------|-------------------|------------|
| WindowsServer | CPU | WinCPU | CPUBusyPercent | W:70, C:90 |
| AzureVM | CPU | Microsoft_Azure_VirtualMachine | Percentage_CPU | W:70, C:90 |
| AzureStorage | Availability | Microsoft_Azure_StorageAccount | Availability | W:99, C:95 (inv) |

---

### LMDeviceMetricsV2
Actual device metrics - flexible JSON storage for any metric combination.

| Column | Type | Description |
|--------|------|-------------|
| Id | int | PK (identity) |
| CustomerId | uniqueidentifier | FK to Customers |
| DeviceId | int | LogicMonitor device ID |
| DeviceName | nvarchar(255) | Device display name |
| ResourceTypeCode | nvarchar(50) | Matched resource type code |
| MetricsJson | nvarchar(max) | JSON object of metric values |
| HealthStatus | nvarchar(20) | Healthy, Warning, Critical, Unknown |
| AvailableDatasourcesJson | nvarchar(max) | JSON array of device's datasources |
| LastSyncedAt | datetime2 | Last sync timestamp |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

**MetricsJson Format:**
```json
{
  "CPU": { "value": 45.2, "unit": "%", "status": "Healthy" },
  "Memory": { "value": 72.1, "unit": "%", "status": "Warning" },
  "DiskIOPS": { "value": 85.5, "unit": "IOPS", "status": "Healthy" }
}
```

**HealthStatus Calculation:**
- If ANY metric is Critical → "Critical"
- Else if ANY metric is Warning → "Warning"
- Else if ALL metrics are Healthy → "Healthy"
- Else → "Unknown"

**Indexes:**
- (CustomerId, DeviceId) - Unique per customer/device
- (CustomerId, ResourceTypeCode) - For filtering by type

---

## Tables

### ServiceTiers
Service tiers that define assessment scope and frequency.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| Name | nvarchar(50) | Internal name (Premium, Standard, Basic, AdHoc) |
| DisplayName | nvarchar(50) | UI display name (Premium, Advanced, Standard, Ad-hoc) |
| Description | nvarchar(500) | Tier description |
| Color | nvarchar(20) | UI color code |
| SortOrder | int | Display order |
| IsActive | bit | Active flag |
| CreatedAt | datetime2 | Created timestamp |

**Seed Data:**

| Name | DisplayName | FinOps Reservations |
|------|-------------|---------------------|
| Premium | Premium | ✅ Included |
| Standard | Advanced | ✅ Included |
| Basic | Standard | ❌ Excluded |
| AdHoc | Ad-hoc | ✅ Included |

> **Note:** Reservations tier filtering (Advanced/Premium/Adhoc only) is applied client-side in the portal UI

---

### AssessmentModules
Available assessment modules (10 active).

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| Code | nvarchar(20) | Module code |
| Name | nvarchar(100) | Display name |
| Description | nvarchar(500) | Module description |
| Icon | nvarchar(20) | Emoji icon |
| Category | nvarchar(50) | Module category |
| EstimatedMinutes | int | Expected run time |
| SortOrder | int | Display order |
| IsActive | bit | Active flag |

**Active Modules:**

| Code | Name | Status |
|------|------|--------|
| NETWORK | Network Topology | Active |
| BACKUP | Backup Posture | Active |
| COST | Cost Management | Active |
| IDENTITY | Identity & Access | Active |
| POLICY | Policy Compliance | Active |
| RESOURCE | Resource Inventory | Active |
| SECURITY | Defender for Cloud | Active |
| PATCH | VM Patch Compliance | Active |
| PERFORMANCE | Right-sizing Analysis | Active |
| COMPLIANCE | Regulatory Compliance | Active |

> **Note:** RESERVATION module marked inactive - replaced by live Azure API in FinOps tab

---

### TierModules
Maps tiers to modules with frequency.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| TierId | uniqueidentifier | FK to ServiceTiers |
| ModuleId | uniqueidentifier | FK to AssessmentModules |
| Frequency | nvarchar(20) | Monthly, Quarterly, OnDemand |
| IsIncluded | bit | Module included for tier |

---

### Customers
Customer records with scheduling and FinOps configuration.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| Name | nvarchar(200) | Customer name |
| Code | nvarchar(20) | Short code |
| Industry | nvarchar(100) | Industry sector |
| PrimaryContact | nvarchar(200) | Primary contact name |
| Email | nvarchar(200) | Contact email |
| Phone | nvarchar(50) | Contact phone |
| Notes | nvarchar(max) | Internal notes |
| IsActive | bit | Active flag |
| NextMeetingDate | datetime2 | Next scheduled meeting |
| SchedulingEnabled | bit | Auto-scheduling enabled |
| **FinOpsStorageAccount** | nvarchar(200) | FinOps storage account name |
| **FinOpsContainer** | nvarchar(100) | FinOps blob container (default: ingestion) |
| **FinOpsPowerBIUrl** | nvarchar(500) | Power BI report URL |
| **FinOpsSasKeyVaultRef** | nvarchar(200) | Key Vault secret name for SAS token |
| **FinOpsSasExpiry** | datetime2 | SAS token expiry date |
| **LogicMonitorGroupId** | int | LM device group ID (for TIEVA shared portal) |
| **LMEnabled** | bit | Enable LM integration for this customer |
| **LMHasCustomCredentials** | bit | True if customer has their own LM portal credentials |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

**LogicMonitor Configuration Modes:**

| Mode | LMHasCustomCredentials | LogicMonitorGroupId | Credentials Source |
|------|------------------------|---------------------|--------------------|
| Customer Portal | true | optional | Key Vault: `LM-{CustomerId}-*` |
| TIEVA Shared | false | required | Key Vault: `LM-Company`, `LM-AccessId`, `LM-AccessKey` |
| Not Configured | false | null | N/A |

---

### AzureConnections
Customer Azure tenant connections.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| CustomerId | uniqueidentifier | FK to Customers |
| TenantId | nvarchar(50) | Azure tenant ID (GUID) |
| TenantName | nvarchar(200) | Tenant display name |
| ClientId | nvarchar(50) | Service Principal app ID |
| SecretKeyVaultRef | nvarchar(200) | Key Vault secret name |
| SecretExpiry | datetime2 | SP secret expiry date |
| IsActive | bit | Connection active |
| LastValidated | datetime2 | Last validation timestamp |
| LastValidationStatus | nvarchar(50) | Success, Failed, etc. |
| CreatedAt | datetime2 | Created timestamp |

---

### CustomerSubscriptions
Synced Azure subscriptions.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| ConnectionId | uniqueidentifier | FK to AzureConnections |
| SubscriptionId | nvarchar(50) | Azure subscription ID (GUID) |
| SubscriptionName | nvarchar(200) | Subscription display name |
| TierId | uniqueidentifier | FK to ServiceTiers (nullable) |
| Environment | nvarchar(50) | Production, Development, Test, Staging, Sandbox |
| IsInScope | bit | Include in assessments |
| CreatedAt | datetime2 | Created timestamp |

**Notes:**
- TierId determines which modules run for this subscription
- TierName used for reservations filtering (Advanced/Premium/Adhoc included, Standard excluded)
- Environment used for filtering/reporting purposes

---

### Assessments
Assessment run records.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| CustomerId | uniqueidentifier | FK to Customers |
| ConnectionId | uniqueidentifier | FK to AzureConnections (nullable) |
| Status | nvarchar(20) | Pending, Processing, Completed, Failed |
| StartedAt | datetime2 | Start timestamp |
| CompletedAt | datetime2 | Completion timestamp |
| StartedBy | nvarchar(200) | User who started |
| TriggerType | nvarchar(20) | Manual, Scheduled, PreMeeting |
| ScoreOverall | decimal(5,2) | Overall assessment score |
| FindingsTotal | int | Total findings count |
| FindingsHigh | int | High severity count |
| FindingsMedium | int | Medium severity count |
| FindingsLow | int | Low severity count |
| CreatedAt | datetime2 | Created timestamp |

---

### AssessmentModuleResults
Per-module results with blob paths.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| AssessmentId | uniqueidentifier | FK to Assessments |
| ModuleCode | nvarchar(20) | Module code |
| SubscriptionId | nvarchar(50) | Subscription assessed (nullable) |
| Status | nvarchar(20) | Pending, Running, Completed, Failed, Skipped |
| Score | decimal(5,2) | Module score |
| FindingsCount | int | Findings for this module |
| StartedAt | datetime2 | Start timestamp |
| CompletedAt | datetime2 | Completion timestamp |
| DurationSeconds | int | Run duration |
| ErrorMessage | nvarchar(max) | Error details if failed |
| BlobPath | nvarchar(500) | Path to Excel results in blob storage |

---

### Findings
Individual findings from each assessment.

| Column | Type | EF Property | Description |
|--------|------|-------------|-------------|
| Id | uniqueidentifier | Id | PK |
| AssessmentId | uniqueidentifier | AssessmentId | FK to Assessments |
| ModuleCode | nvarchar(20) | ModuleCode | Module code |
| SubscriptionId | nvarchar(50) | SubscriptionId | Subscription ID |
| Severity | nvarchar(20) | Severity | High, Medium, Low, Info |
| Category | nvarchar(100) | Category | Finding category |
| ResourceType | nvarchar(100) | ResourceType | Azure resource type |
| ResourceName | nvarchar(200) | ResourceName | Resource display name |
| ResourceId | nvarchar(500) | ResourceId | Azure resource ID |
| **Finding** | nvarchar(500) | **FindingText** | Main finding description |
| Recommendation | nvarchar(max) | Recommendation | Remediation recommendation |
| EffortHours | decimal(6,2) | EffortHours | Estimated effort |
| Owner | nvarchar(100) | Owner | Assigned owner |
| Status | nvarchar(20) | Status | Open, Resolved |
| ChangeStatus | nvarchar(20) | ChangeStatus | New, Recurring, Resolved |
| Hash | nvarchar(100) | Hash | Unique finding identifier |
| FirstSeenAt | datetime2 | FirstSeenAt | First detection date |
| LastSeenAt | datetime2 | LastSeenAt | Most recent detection |
| ResolvedAt | datetime2 | ResolvedAt | Resolution date |
| OccurrenceCount | int | OccurrenceCount | Times seen across assessments |
| PreviousFindingId | uniqueidentifier | PreviousFindingId | Link to previous occurrence |

> **⚠️ Important:** Database column `Finding` maps to EF property `FindingText`. PowerShell scripts must insert into `Finding` column.

---

### CustomerFindings
Aggregated findings per customer (deduplicated across assessments).

| Column | Type | EF Property | Description |
|--------|------|-------------|-------------|
| Id | uniqueidentifier | Id | PK |
| CustomerId | uniqueidentifier | CustomerId | FK to Customers |
| ModuleCode | nvarchar(20) | ModuleCode | Module code |
| Hash | nvarchar(100) | Hash | Unique finding identifier |
| Severity | nvarchar(20) | Severity | High, Medium, Low, Info |
| Category | nvarchar(100) | Category | Finding category |
| ResourceType | nvarchar(100) | ResourceType | Azure resource type |
| ResourceId | nvarchar(500) | ResourceId | Azure resource ID |
| **Finding** | nvarchar(500) | **FindingText** | Main finding description |
| Recommendation | nvarchar(max) | Recommendation | Remediation recommendation |
| Status | nvarchar(20) | Status | Open, Resolved |
| FirstSeenAt | datetime2 | FirstSeenAt | First detection |
| LastSeenAt | datetime2 | LastSeenAt | Most recent detection |
| ResolvedAt | datetime2 | ResolvedAt | Resolution date |
| OccurrenceCount | int | OccurrenceCount | Times seen |
| LastAssessmentId | uniqueidentifier | LastAssessmentId | Most recent assessment |
| CreatedAt | datetime2 | CreatedAt | Created timestamp |
| UpdatedAt | datetime2 | UpdatedAt | Updated timestamp |

**Indexes:**
- Unique: (CustomerId, Hash) - One record per unique finding per customer

---

### FindingMetadata
Unified configuration for effort, impact, and operational metadata.

| Column | Type | Description |
|--------|------|-------------|
| Id | int | PK (identity) |
| ModuleCode | nvarchar(20) | Pattern: module code filter |
| Category | nvarchar(100) | Pattern: category filter |
| FindingPattern | nvarchar(500) | Pattern: finding text match |
| RecommendationPattern | nvarchar(500) | Pattern: recommendation match |
| BaseHours | decimal(6,2) | Fixed hours for this finding type |
| PerResourceHours | decimal(6,2) | Additional hours per resource |
| ImpactOverride | nvarchar(20) | Override impact: High, Medium, Low |
| DefaultOwner | nvarchar(100) | Default assignment |
| DowntimeRequired | nvarchar(20) | None, Partial, Full |
| DowntimeMinutes | int | Expected downtime duration |
| ChangeControlRequired | bit | Requires change control |
| MaintenanceWindowRequired | bit | Requires maintenance window |
| AffectsProduction | bit | Impacts production |
| CostImplication | nvarchar(20) | None, Low, Medium, High |
| Complexity | nvarchar(20) | Low, Medium, High |
| RiskLevel | nvarchar(20) | Low, Medium, High |
| Notes | nvarchar(max) | Additional notes |
| MatchPriority | int | Higher = matched first |
| IsActive | bit | Active flag |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |
| UpdatedBy | nvarchar(200) | Last modified by |

**Indexes:**
- ModuleCode, Category, MatchPriority, IsActive

---

### CustomerRoadmapPlans
Saved wave assignments for remediation planning.

| Column | Type | Description |
|--------|------|-------------|
| Id | int | PK (identity) |
| CustomerId | uniqueidentifier | FK to Customers |
| Wave1Findings | nvarchar(max) | JSON array of finding IDs |
| Wave2Findings | nvarchar(max) | JSON array of finding IDs |
| Wave3Findings | nvarchar(max) | JSON array of finding IDs |
| SkippedFindings | nvarchar(max) | Explicitly skipped findings |
| Notes | nvarchar(max) | Plan notes |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |
| UpdatedBy | nvarchar(200) | Last modified by |

**Indexes:**
- Unique: CustomerId - One plan per customer

---

### CustomerReservationCache
Cached reservation data for async processing.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| CustomerId | uniqueidentifier | FK to Customers |
| Status | nvarchar(20) | Pending, Running, Completed, Failed |
| LastRefreshed | datetime2 | Last data refresh time |
| ReservationsJson | nvarchar(max) | Cached reservation data |
| InsightsJson | nvarchar(max) | Cached insights |
| SummaryJson | nvarchar(max) | Cached summary stats |
| PurchaseRecommendationsJson | nvarchar(max) | Cached purchase recommendations |
| ErrorsJson | nvarchar(max) | Errors during fetch |
| ErrorMessage | nvarchar(max) | Fatal error message |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

**Indexes:**
- Unique: CustomerId - One cache per customer

**Usage:** Portal polls this table for reservation data. When user clicks "Refresh", backend updates cache asynchronously. Frontend polls until Status = "Completed".

---

### EffortSettings (DEPRECATED)
Legacy effort estimation table - replaced by FindingMetadata.

| Column | Type | Description |
|--------|------|-------------|
| Id | int | PK (identity) |
| Category | nvarchar(100) | Finding category |
| Severity | nvarchar(20) | Severity fallback |
| RecommendationPattern | nvarchar(500) | Pattern match |
| BaseHours | decimal(6,2) | Fixed hours |
| PerResourceHours | decimal(6,2) | Per-resource hours |
| ImpactOverride | nvarchar(20) | Impact override |
| Description | nvarchar(500) | Description |
| Owner | nvarchar(100) | Default owner |
| IsActive | bit | Active flag |
| MatchPriority | int | Match priority |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |
| UpdatedBy | nvarchar(200) | Last modified by |

> **⚠️ Deprecated:** Use FindingMetadata instead. Data may have been migrated.

---

## Relationships Summary

```
Customers (1) ─────┬──── (N) AzureConnections
                   ├──── (N) Assessments
                   ├──── (N) CustomerFindings
                   ├──── (1) CustomerRoadmapPlan
                   └──── (1) CustomerReservationCache

AzureConnections (1) ──── (N) CustomerSubscriptions

CustomerSubscriptions (N) ──── (1) ServiceTiers

Assessments (1) ────┬──── (N) AssessmentModuleResults
                    └──── (N) Findings

ServiceTiers (1) ──── (N) TierModules ──── (N) AssessmentModules
```

---

## Cascading Delete Rules

| Delete | Cascades To |
|--------|-------------|
| Customer | Connections, Subscriptions, Assessments, Findings, CustomerFindings, RoadmapPlan, ReservationCache |
| Connection | Subscriptions, Assessments (via connection), Findings |
| Assessment | ModuleResults, Findings |

---

## SQL Useful Queries

### View all tables
```sql
SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
```

### Check finding column mapping
```sql
SELECT COLUMN_NAME, DATA_TYPE FROM INFORMATION_SCHEMA.COLUMNS 
WHERE TABLE_NAME = 'Findings' ORDER BY ORDINAL_POSITION
```

### Count findings by module
```sql
SELECT ModuleCode, COUNT(*) as Count 
FROM Findings 
GROUP BY ModuleCode
```

### Check reservation cache status
```sql
SELECT c.Name, rc.Status, rc.LastRefreshed, rc.ErrorMessage
FROM CustomerReservationCache rc
JOIN Customers c ON rc.CustomerId = c.Id
```
