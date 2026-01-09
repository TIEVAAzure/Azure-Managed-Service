# TIEVA Portal - Database Schema

**Last Updated:** January 2025 (v2.1)

## Connection Details

| Property | Value |
|----------|-------|
| Server | sql-tievaPortal-3234.database.windows.net |
| Database | TievaPortal |
| Auth | Entra-only (no SQL auth) |
| Tier | Basic |

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
| IsActive | bit | Active flag |

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
| IsActive | bit | Active flag |

**Seed Data:**
| Code | Name | Status |
|------|------|--------|
| NETWORK | Network Topology | Active |
| BACKUP | Backup Posture | Active |
| COST | Cost Management | Active |
| IDENTITY | Identity & Access | Active |
| POLICY | Policy Compliance | Active |
| RESOURCE | Resource Inventory | Active |
| RESERVATION | Reservations & Savings | Inactive (replaced by live API) |
| SECURITY | Defender for Cloud | Active |
| PATCH | VM Patch Compliance | Active |
| PERFORMANCE | Right-sizing Analysis | Active |
| COMPLIANCE | Regulatory Compliance | Active |

---

### TierModules
Maps tiers to modules with frequency.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| ServiceTierId | uniqueidentifier | FK to ServiceTiers |
| ModuleId | uniqueidentifier | FK to AssessmentModules |
| Frequency | nvarchar(20) | Monthly, Quarterly, OnDemand |
| IsIncluded | bit | Module included for tier |

---

### Customers
Customer records.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| Name | nvarchar(200) | Customer name |
| Industry | nvarchar(100) | Industry sector |
| PrimaryContact | nvarchar(200) | Primary contact name |
| ContactEmail | nvarchar(200) | Contact email |
| NextMeetingDate | datetime2 | Next scheduled meeting |
| SchedulingEnabled | bit | Auto-scheduling enabled |
| FinOpsStorageAccount | nvarchar(200) | FinOps storage account name |
| FinOpsContainer | nvarchar(100) | FinOps blob container (default: ingestion) |
| FinOpsPowerBIUrl | nvarchar(500) | Power BI report URL |
| FinOpsSasKeyVaultRef | nvarchar(200) | Key Vault secret name for SAS token |
| FinOpsSasExpiry | datetime2 | SAS token expiry date |
| IsDeleted | bit | Soft delete flag |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

---

### AzureConnections
Customer Azure tenant connections.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| CustomerId | uniqueidentifier | FK to Customers |
| TenantId | nvarchar(50) | Azure tenant ID |
| TenantName | nvarchar(200) | Tenant display name |
| ClientId | nvarchar(50) | Service Principal app ID |
| KeyVaultSecretName | nvarchar(200) | Key Vault secret name |
| SecretExpiry | datetime2 | SP secret expiry date |
| Status | nvarchar(20) | Active, Expired, Invalid |
| IsActive | bit | Connection active |
| LastValidated | datetime2 | Last validation timestamp |
| IsDeleted | bit | Soft delete flag |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

---

### CustomerSubscriptions
Synced Azure subscriptions.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| ConnectionId | uniqueidentifier | FK to AzureConnections |
| SubscriptionId | nvarchar(50) | Azure subscription ID |
| SubscriptionName | nvarchar(200) | Subscription display name |
| ServiceTierId | uniqueidentifier | FK to ServiceTiers (nullable) |
| Environment | nvarchar(50) | Production, Development, Test, Staging, Sandbox |
| IsInScope | bit | Include in assessments |
| IsDeleted | bit | Soft delete flag |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

**Tier Usage:**
- TierName is used for reservations filtering (Advanced/Premium/Adhoc included, Standard excluded)
- Filtering is performed client-side in the portal UI

---

### Assessments
Assessment run records.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| ConnectionId | uniqueidentifier | FK to AzureConnections |
| CustomerId | uniqueidentifier | FK to Customers (denormalized) |
| CustomerName | nvarchar(200) | Customer name (denormalized) |
| Name | nvarchar(200) | Assessment name/description |
| Status | nvarchar(20) | Pending, Running, Completed, Failed |
| ScoreOverall | decimal(5,2) | Overall assessment score |
| FindingsTotal | int | Total findings count |
| FindingsHigh | int | High severity count |
| FindingsMedium | int | Medium severity count |
| FindingsLow | int | Low severity count |
| StartedAt | datetime2 | Start timestamp |
| CompletedAt | datetime2 | Completion timestamp |
| CreatedBy | nvarchar(200) | User who initiated |
| Notes | nvarchar(max) | Assessment notes |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

---

### AssessmentModuleResults
Per-module results within an assessment.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| AssessmentId | uniqueidentifier | FK to Assessments |
| ModuleId | uniqueidentifier | FK to AssessmentModules |
| ModuleCode | nvarchar(20) | Module code (denormalized) |
| Status | nvarchar(20) | Pending, Running, Completed, Failed |
| BlobPath | nvarchar(500) | Path to Excel in blob storage |
| FindingsCount | int | Total findings count |
| HighCount | int | High severity count |
| MediumCount | int | Medium severity count |
| LowCount | int | Low severity count |
| Score | decimal(5,2) | Calculated module score |
| StartedAt | datetime2 | Start timestamp |
| CompletedAt | datetime2 | Completion timestamp |
| ErrorMessage | nvarchar(max) | Error details if failed |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

---

### Findings
Individual findings from assessments.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| ModuleResultId | uniqueidentifier | FK to AssessmentModuleResults |
| AssessmentId | uniqueidentifier | FK to Assessments |
| ModuleCode | nvarchar(20) | Module code |
| Category | nvarchar(100) | Finding category |
| FindingText | nvarchar(500) | Main finding description |
| Detail | nvarchar(max) | Additional details |
| Severity | nvarchar(20) | High, Medium, Low, Info |
| ResourceId | nvarchar(500) | Azure resource ID |
| ResourceName | nvarchar(200) | Resource display name |
| ResourceType | nvarchar(100) | Azure resource type |
| SubscriptionId | nvarchar(50) | Subscription ID |
| SubscriptionName | nvarchar(200) | Subscription name |
| Recommendation | nvarchar(max) | Remediation recommendation |
| Impact | nvarchar(max) | Business impact |
| Status | nvarchar(20) | Open, Resolved, Acknowledged |
| ChangeStatus | nvarchar(20) | New, Recurring, Resolved |
| OccurrenceCount | int | Times seen across assessments |
| FirstSeenAt | datetime2 | First detection date |
| LastSeenAt | datetime2 | Most recent detection |
| ResolvedAt | datetime2 | Resolution date |
| CreatedAt | datetime2 | Created timestamp |

---

### CustomerFindings
Aggregated findings per customer (deduplicated across assessments).

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| CustomerId | uniqueidentifier | FK to Customers |
| ModuleCode | nvarchar(20) | Module code |
| Category | nvarchar(100) | Finding category |
| FindingText | nvarchar(500) | Main finding description |
| Detail | nvarchar(max) | Additional details |
| Severity | nvarchar(20) | High, Medium, Low, Info |
| ResourceId | nvarchar(500) | Azure resource ID |
| ResourceName | nvarchar(200) | Resource display name |
| ResourceType | nvarchar(100) | Azure resource type |
| SubscriptionId | nvarchar(50) | Subscription ID |
| SubscriptionName | nvarchar(200) | Subscription name |
| Recommendation | nvarchar(max) | Remediation recommendation |
| Status | nvarchar(20) | Open, Resolved |
| OccurrenceCount | int | Times seen |
| FirstSeenAt | datetime2 | First detection |
| LastSeenAt | datetime2 | Most recent detection |
| ResolvedAt | datetime2 | Resolution date |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

---

### FindingMetadata (replaces EffortSettings)
Unified configuration for effort, impact, and operational metadata.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| ModuleCode | nvarchar(20) | Module code (null = all modules) |
| Category | nvarchar(100) | Finding category (null = all categories) |
| FindingPattern | nvarchar(500) | Text pattern match (null = no pattern) |
| BaseHours | decimal(5,2) | Fixed hours for remediation |
| PerResourceHours | decimal(5,2) | Additional hours per affected resource |
| ImpactOverride | nvarchar(20) | Override severity-based impact (High/Medium/Low) |
| DefaultOwner | nvarchar(100) | Suggested remediation owner |
| DowntimeRequired | nvarchar(20) | None, Partial, Full |
| DowntimeMinutes | int | Expected downtime in minutes |
| Complexity | nvarchar(20) | Low, Medium, High |
| RiskLevel | nvarchar(20) | Low, Medium, High |
| CostImplication | nvarchar(20) | None, Minor, Significant |
| ChangeControlRequired | bit | Requires CAB approval |
| MaintenanceWindowRequired | bit | Requires maintenance window |
| AffectsProduction | bit | Affects production workloads |
| MatchPriority | int | Higher = checked first |
| IsActive | bit | Active flag |
| Notes | nvarchar(max) | Additional notes |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

---

### CustomerRoadmapPlans
Saved remediation roadmap wave assignments.

| Column | Type | Description |
|--------|------|-------------|
| Id | uniqueidentifier | PK |
| CustomerId | uniqueidentifier | FK to Customers |
| Wave1Findings | nvarchar(max) | JSON array of finding IDs |
| Wave2Findings | nvarchar(max) | JSON array of finding IDs |
| Wave3Findings | nvarchar(max) | JSON array of finding IDs |
| SkippedFindings | nvarchar(max) | JSON array of finding IDs |
| UpdatedBy | nvarchar(200) | User who last saved |
| CreatedAt | datetime2 | Created timestamp |
| UpdatedAt | datetime2 | Updated timestamp |

---

## Entity Relationships

```
Customers
    ├── AzureConnections (1:many)
    │       ├── CustomerSubscriptions (1:many)
    │       │       └── ServiceTiers (many:1)
    │       └── Assessments (1:many)
    │               └── AssessmentModuleResults (1:many)
    │                       ├── AssessmentModules (many:1)
    │                       └── Findings (1:many)
    ├── CustomerFindings (1:many)
    └── CustomerRoadmapPlans (1:1)

ServiceTiers
    └── TierModules (1:many)
            └── AssessmentModules (many:1)

FindingMetadata (standalone reference table)
```

---

## Cascading Delete Behavior

### Customer Delete
Deletes in order:
1. CustomerRoadmapPlans (for customer)
2. CustomerFindings (for customer)
3. Findings (for customer's assessments)
4. AssessmentModuleResults (for customer's assessments)
5. Assessments (for customer's connections)
6. CustomerSubscriptions (for customer's connections)
7. AzureConnections (for customer)
8. Customer record

### Connection Delete
Deletes in order:
1. Findings (for connection's assessments)
2. AssessmentModuleResults (for connection's assessments)
3. Assessments (for connection)
4. CustomerSubscriptions (for connection)
5. Connection record
6. Updates CustomerFindings (recalculates status)

### Assessment Delete
Deletes in order:
1. Findings (for assessment)
2. AssessmentModuleResults (for assessment)
3. Assessment record
4. Updates CustomerFindings (recalculates status)

---

## Key SQL Scripts

### Add New Modules
```sql
INSERT INTO AssessmentModules (Id, Code, Name, Description, IsActive)
VALUES 
    (NEWID(), 'SECURITY', 'Defender for Cloud', 'Security posture and alerts', 1),
    (NEWID(), 'PATCH', 'VM Patch Compliance', 'OS update compliance', 1),
    (NEWID(), 'PERFORMANCE', 'Right-sizing Analysis', 'VM utilization and sizing', 1),
    (NEWID(), 'COMPLIANCE', 'Regulatory Compliance', 'Framework compliance scores', 1);
```

### Check Module Coverage by Tier
```sql
SELECT 
    st.DisplayName as Tier,
    am.Code as Module,
    tm.Frequency,
    tm.IsIncluded
FROM TierModules tm
JOIN ServiceTiers st ON tm.ServiceTierId = st.Id
JOIN AssessmentModules am ON tm.ModuleId = am.Id
WHERE tm.IsIncluded = 1
ORDER BY st.DisplayName, am.Code;
```

### Customer Findings Summary
```sql
SELECT 
    c.Name as Customer,
    cf.ModuleCode,
    COUNT(*) as TotalFindings,
    SUM(CASE WHEN cf.Severity = 'High' THEN 1 ELSE 0 END) as High,
    SUM(CASE WHEN cf.Severity = 'Medium' THEN 1 ELSE 0 END) as Medium,
    SUM(CASE WHEN cf.Severity = 'Low' THEN 1 ELSE 0 END) as Low
FROM CustomerFindings cf
JOIN Customers c ON cf.CustomerId = c.Id
WHERE cf.Status = 'Open'
GROUP BY c.Name, cf.ModuleCode
ORDER BY c.Name, cf.ModuleCode;
```

### Assessment History with Scores
```sql
SELECT 
    a.Id,
    c.Name as Customer,
    a.Name as AssessmentName,
    a.Status,
    a.ScoreOverall,
    a.FindingsTotal,
    a.FindingsHigh,
    a.StartedAt,
    a.CompletedAt
FROM Assessments a
JOIN AzureConnections ac ON a.ConnectionId = ac.Id
JOIN Customers c ON ac.CustomerId = c.Id
ORDER BY a.StartedAt DESC;
```

### Subscriptions by Tier (for Reservations Filtering Reference)
```sql
-- Get subscriptions that would be included in Reservations view
SELECT 
    cs.SubscriptionId,
    cs.SubscriptionName,
    st.DisplayName as TierName,
    c.Name as CustomerName
FROM CustomerSubscriptions cs
JOIN AzureConnections ac ON cs.ConnectionId = ac.Id
JOIN Customers c ON ac.CustomerId = c.Id
LEFT JOIN ServiceTiers st ON cs.ServiceTierId = st.Id
WHERE st.DisplayName IN ('Advanced', 'Premium', 'Ad-hoc')
   OR st.DisplayName IS NULL  -- Unassigned subscriptions
ORDER BY c.Name, cs.SubscriptionName;
```
