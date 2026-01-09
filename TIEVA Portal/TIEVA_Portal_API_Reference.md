# TIEVA Portal - API Reference

**Last Updated:** January 2025 (v2.1)

## Base URLs

| API | URL |
|-----|-----|
| Main API | https://func-tievaportal-6612.azurewebsites.net/api |
| Audit API | https://func-tieva-audit.azurewebsites.net/api |

---

## Main API Endpoints

### Health Check

#### GET /api/health
Returns API health status.

**Response:**
```json
{
  "status": "healthy",
  "timestamp": "2025-01-05T12:00:00Z"
}
```

---

### Customers

#### GET /api/customers
List all customers.

**Response:**
```json
[
  {
    "id": "guid",
    "name": "Customer Name",
    "industry": "Technology",
    "primaryContact": "John Smith",
    "contactEmail": "john@example.com",
    "nextMeetingDate": "2025-02-01T00:00:00Z",
    "schedulingEnabled": true,
    "createdAt": "2024-12-01T00:00:00Z"
  }
]
```

#### GET /api/customers/{id}
Get single customer.

#### POST /api/customers
Create customer.

**Request:**
```json
{
  "name": "Customer Name",
  "industry": "Technology",
  "primaryContact": "John Smith",
  "contactEmail": "john@example.com",
  "nextMeetingDate": "2025-02-01",
  "schedulingEnabled": true
}
```

#### PUT /api/customers/{id}
Update customer.

#### DELETE /api/customers/{id}
Delete customer (cascades to connections, assessments, findings).

**Response:**
```json
{
  "message": "Customer deleted successfully",
  "connectionsDeleted": 2,
  "subscriptionsDeleted": 5,
  "assessmentsDeleted": 10,
  "findingsDeleted": 156
}
```

---

### Customer Findings

#### GET /api/customers/{id}/findings
Get aggregated findings for customer (deduplicated across assessments).

**Response:**
```json
{
  "customerId": "guid",
  "customerName": "Customer Name",
  "summary": {
    "total": 156,
    "high": 23,
    "medium": 67,
    "low": 66,
    "byModule": {
      "NETWORK": { "high": 5, "medium": 12, "low": 8 },
      "BACKUP": { "high": 8, "medium": 15, "low": 10 }
    }
  },
  "findings": [
    {
      "id": "guid",
      "moduleCode": "NETWORK",
      "category": "NSG Configuration",
      "findingText": "NSG allows unrestricted SSH access",
      "severity": "High",
      "resourceName": "nsg-web-prod",
      "resourceType": "Microsoft.Network/networkSecurityGroups",
      "subscriptionName": "Production",
      "recommendation": "Restrict SSH access to known IP ranges",
      "status": "Open",
      "occurrenceCount": 3,
      "firstSeenAt": "2024-12-01T00:00:00Z",
      "lastSeenAt": "2025-01-05T00:00:00Z"
    }
  ]
}
```

---

### Connections

#### GET /api/connections
List all connections with subscriptions.

**Response:**
```json
[
  {
    "id": "guid",
    "customerId": "guid",
    "customerName": "Customer Name",
    "tenantId": "tenant-guid",
    "tenantName": "contoso.onmicrosoft.com",
    "clientId": "app-guid",
    "status": "Active",
    "isActive": true,
    "lastValidated": "2025-01-05T00:00:00Z",
    "secretExpiry": "2026-12-31T00:00:00Z",
    "subscriptions": [
      {
        "id": "guid",
        "subscriptionId": "sub-guid",
        "subscriptionName": "Production",
        "tierId": "tier-guid",
        "tierName": "Premium",
        "environment": "Production",
        "isInScope": true
      }
    ]
  }
]
```

#### GET /api/connections/{id}
Get single connection with subscriptions.

#### POST /api/connections
Create connection (validates credentials, stores secret in Key Vault).

**Request:**
```json
{
  "customerId": "guid",
  "tenantId": "tenant-guid",
  "tenantName": "contoso.onmicrosoft.com",
  "clientId": "app-guid",
  "clientSecret": "secret-value"
}
```

#### PUT /api/connections/{id}
Update connection.

#### DELETE /api/connections/{id}
Delete connection (cascades to subscriptions, assessments, findings).

**Response:**
```json
{
  "message": "Connection deleted successfully",
  "subscriptionsDeleted": 5,
  "assessmentsDeleted": 10,
  "findingsDeleted": 156
}
```

#### POST /api/connections/{id}/validate
Validate connection credentials against Azure.

**Response:**
```json
{
  "isValid": true,
  "message": "Connection validated successfully",
  "subscriptionCount": 5
}
```

#### POST /api/connections/{id}/sync
Sync subscriptions from Azure.

**Response:**
```json
{
  "message": "Synced 5 subscriptions",
  "added": 2,
  "updated": 3,
  "removed": 0
}
```

---

### Subscriptions

#### GET /api/subscriptions
List all subscriptions.

#### PUT /api/subscriptions/{id}
Update subscription (tier, environment, scope).

**Request:**
```json
{
  "tierId": "tier-guid",
  "environment": "Production",
  "isInScope": true
}
```

#### PUT /api/connections/{connectionId}/subscriptions
Bulk update subscriptions for a connection.

**Request:**
```json
{
  "subscriptions": [
    { "id": "guid", "tierId": "tier-guid", "environment": "Production", "isInScope": true }
  ]
}
```

#### GET /api/connections/{connectionId}/audit-subscriptions/{moduleCode}
Get subscriptions eligible for a specific module audit.

**Response:**
```json
[
  {
    "subscriptionId": "sub-guid",
    "subscriptionName": "Production",
    "tierName": "Premium"
  }
]
```

---

### Service Tiers

#### GET /api/tiers
List all tiers with module configuration.

**Response:**
```json
[
  {
    "id": "guid",
    "name": "Premium",
    "displayName": "Premium",
    "description": "Full managed service",
    "isActive": true,
    "tierModules": [
      {
        "id": "guid",
        "moduleId": "mod-guid",
        "moduleCode": "NETWORK",
        "moduleName": "Network Topology",
        "frequency": "Monthly",
        "isIncluded": true
      }
    ]
  }
]
```

#### GET /api/modules
List all available modules.

**Response:**
```json
[
  {
    "id": "guid",
    "code": "NETWORK",
    "name": "Network Topology",
    "description": "NSG rules, route tables, VNet peerings",
    "isActive": true
  }
]
```

#### PUT /api/tiers/{id}
Update tier details.

#### PUT /api/tiers/{id}/modules
Update tier module configuration.

**Request:**
```json
{
  "modules": [
    { "moduleId": "guid", "frequency": "Monthly", "isIncluded": true }
  ]
}
```

---

### Assessments

#### GET /api/assessments
List all assessments with summary.

**Response:**
```json
[
  {
    "id": "guid",
    "connectionId": "guid",
    "customerId": "guid",
    "customerName": "Customer Name",
    "name": "Monthly Review - January 2025",
    "status": "Completed",
    "scoreOverall": 78.5,
    "findingsTotal": 45,
    "findingsHigh": 5,
    "findingsMedium": 20,
    "findingsLow": 20,
    "startedAt": "2025-01-05T10:00:00Z",
    "completedAt": "2025-01-05T10:15:00Z",
    "moduleResults": [
      {
        "moduleCode": "NETWORK",
        "status": "Completed",
        "score": 82,
        "findingsCount": 15
      }
    ]
  }
]
```

#### GET /api/assessments/{id}
Get assessment with full details and findings.

**Response:**
```json
{
  "id": "guid",
  "customerName": "Customer Name",
  "status": "Completed",
  "scoreOverall": 78.5,
  "startedAt": "2025-01-05T10:00:00Z",
  "completedAt": "2025-01-05T10:15:00Z",
  "moduleResults": [
    {
      "id": "guid",
      "moduleCode": "NETWORK",
      "status": "Completed",
      "score": 82,
      "findingsCount": 15,
      "highCount": 2,
      "mediumCount": 8,
      "lowCount": 5,
      "blobPath": "assessments/2025-01-05/network-audit.xlsx"
    }
  ],
  "findings": [...]
}
```

#### POST /api/assessments
Create assessment record.

#### DELETE /api/assessments/{id}
Delete assessment (cascades to findings).

#### DELETE /api/assessments/bulk
Bulk delete assessments.

**Request:**
```json
{
  "assessmentIds": ["guid1", "guid2", "guid3"]
}
```

#### POST /api/assessments/{id}/reparse/{moduleCode}
Re-parse module results from blob storage.

#### GET /api/assessments/{id}/download/{moduleCode}
Download module Excel results.

---

### Effort Settings (Legacy)

#### GET /api/effort-settings
List effort settings rules.

**Response:**
```json
[
  {
    "id": "guid",
    "matchType": "severity",
    "severity": "High",
    "category": null,
    "recommendationPattern": null,
    "baseHours": 4.0,
    "perResourceHours": 0.5,
    "defaultOwner": "Cloud Ops",
    "description": "Default for high severity",
    "matchPriority": 0,
    "isActive": true
  },
  {
    "id": "guid",
    "matchType": "category",
    "severity": null,
    "category": "Orphaned Resource",
    "recommendationPattern": null,
    "baseHours": 0.5,
    "perResourceHours": 0.1,
    "defaultOwner": "Cloud Ops",
    "description": "Quick cleanup tasks",
    "matchPriority": 10,
    "isActive": true
  }
]
```

#### POST /api/effort-settings
Create effort setting.

#### PUT /api/effort-settings/{id}
Update effort setting.

#### DELETE /api/effort-settings/{id}
Delete effort setting.

---

### Scheduler

#### GET /api/scheduler/status
Get scheduling status for all customers.

**Response:**
```json
[
  {
    "customerId": "guid",
    "customerName": "Customer Name",
    "schedulingEnabled": true,
    "nextMeetingDate": "2025-02-01T00:00:00Z",
    "daysUntilMeeting": 27,
    "preMeetingDue": false,
    "totalModulesDue": 2,
    "subscriptions": [
      {
        "subscriptionId": "sub-guid",
        "subscriptionName": "Production",
        "tierName": "Premium",
        "modules": [
          {
            "moduleCode": "NETWORK",
            "frequency": "Monthly",
            "lastRun": "2024-12-15T00:00:00Z",
            "daysSinceLastRun": 21,
            "daysUntilDue": 9,
            "isDue": false
          }
        ]
      }
    ]
  }
]
```

---

### Dashboard

#### GET /api/dashboard
Get dashboard statistics.

**Response:**
```json
{
  "customerCount": 15,
  "connectionCount": 22,
  "assessmentCount": 156,
  "findingsTotal": 1250,
  "findingsHigh": 85,
  "findingsMedium": 450,
  "findingsLow": 715,
  "averageScore": 76.5,
  "recentAssessments": [...]
}
```

---

## Audit API Endpoints

### POST /api/assessments/start
Run assessment against customer Azure tenant.

**Request:**
```json
{
  "connectionId": "guid",
  "modules": ["NETWORK", "BACKUP", "SECURITY"],
  "assessmentName": "Monthly Review - January 2025"
}
```

**Response:**
```json
{
  "assessmentId": "guid",
  "status": "Running",
  "modules": [
    { "code": "NETWORK", "status": "Completed", "findingsCount": 15 },
    { "code": "BACKUP", "status": "Completed", "findingsCount": 12 },
    { "code": "SECURITY", "status": "Completed", "findingsCount": 8 }
  ],
  "totalFindings": 35,
  "duration": "00:05:23"
}
```

**Notes:**
- Runs modules sequentially
- Stores Excel results in blob storage
- Parses findings into database
- Updates assessment scores
- Updates CustomerFindings table

---

## Error Responses

All endpoints return standard error format:

```json
{
  "error": "Error message",
  "details": "Additional details if available",
  "statusCode": 400
}
```

### Common Status Codes

| Code | Meaning |
|------|---------|
| 200 | Success |
| 201 | Created |
| 400 | Bad Request |
| 404 | Not Found |
| 500 | Internal Server Error |

---

## FinOps Endpoints

### GET /api/customers/{id}/finops/cost-analysis
Get cost analysis data from FOCUS parquet files.

**Query Parameters:**
- `days` (optional): Number of days to analyze (default: 30)

**Response:**
```json
{
  "success": true,
  "data": {
    "totalCost": 15234.56,
    "recordCount": 45000,
    "dateRange": { "start": "2024-12-01", "end": "2025-01-01" },
    "byService": [...],
    "byResourceGroup": [...],
    "bySubscription": [...],
    "dailyTrend": [...],
    "resources": [...]
  }
}
```

### GET /api/customers/{id}/finops/reservations
Get live reservation data from Azure APIs with intelligent insights.

**Response:**
```json
{
  "success": true,
  "hasData": true,
  "lastUpdated": "2025-01-07T12:00:00Z",
  "summary": {
    "TotalReservations": 5,
    "ActiveReservations": 4,
    "ExpiringSoon": 1,
    "LowUtilization": 2,
    "FullUtilization": 2,
    "ZeroUtilization": 0,
    "PurchaseRecommendations": 3,
    "PotentialAnnualSavings": 12500.00
  },
  "reservations": [
    {
      "ReservationId": "guid",
      "DisplayName": "VM Reserved Instance",
      "ResourceType": "VirtualMachines",
      "SkuName": "Standard_D4s_v3",
      "Quantity": 2,
      "Term": "P1Y",
      "Status": "Succeeded",
      "Utilization30Day": 95.5,
      "Utilization7Day": 98.2,
      "ExpiryDate": "2025-06-15",
      "DaysToExpiry": 159,
      "Renew": true,
      "Location": "uksouth",
      "SubscriptionId": "sub-guid",
      "SubscriptionName": "Production"
    }
  ],
  "insights": [
    {
      "Priority": "High",
      "Type": "RenewHighUtilization",
      "Icon": "✅",
      "Title": "VM Reserved Instance expires in 30 days - 100% utilized",
      "Description": "This reservation is fully utilized and expiring soon. Auto-renew is OFF!",
      "Recommendation": "ENABLE AUTO-RENEW or manually renew to maintain savings.",
      "Action": "Enable Auto-Renew",
      "ReservationId": "guid"
    },
    {
      "Priority": "High",
      "Type": "LowUtilization",
      "Icon": "⚠️",
      "Title": "SQL Database RI only 45% utilized",
      "Description": "At 45% utilization, you're wasting ~55% of this reservation's value. PAYG would likely be cheaper.",
      "Recommendation": "Consider exchanging for smaller reservation or switching to PAYG.",
      "Action": "Exchange to PAYG",
      "WastePercent": 55
    },
    {
      "Priority": "Medium",
      "Type": "PurchaseRecommendation",
      "Icon": "💰",
      "Title": "Buy Standard_E4s_v5 reservation - save £8,500/year",
      "Description": "Azure recommends purchasing 3 x Standard_E4s_v5 (P1Y) based on your usage.",
      "Recommendation": "Review workload stability before purchasing.",
      "Action": "Purchase RI",
      "AnnualSavings": 8500
    }
  ],
  "purchaseRecommendations": [...],
  "errors": []
}
```

**Insight Types:**
- `ZeroUtilization` (Critical): 0% usage - wasting money
- `RenewHighUtilization` (High): >95% usage, expiring soon - renew
- `LowUtilization` (High/Medium): <80% usage - consider PAYG
- `DontRenew` (High/Medium): Low usage + expiring - don't renew
- `PurchaseRecommendation` (High/Medium): Buy new reservations
- `HealthySummary` (Info): Count of healthy reservations

**UI Tier Filtering (Client-Side):**
The portal UI applies tier-based filtering to reservation data before display:
- **Included Tiers**: Advanced, Premium, Adhoc
- **Excluded Tiers**: Standard
- **Scope**: Main tab, presentation mode, and PDF export
- **Tenant-Level**: Reservations without subscription info are always included

### POST /api/customers/{id}/finops/sas
Save SAS token for FinOps storage access.

**Request:**
```json
{
  "sasToken": "sv=2022-11-02&ss=b&srt=sco&sp=rl&se=2026-01-01&sig=..."
}
```

### POST /api/customers/{id}/finops/run-export
Trigger Azure Cost Management exports to run immediately.

**Response:**
```json
{
  "success": true,
  "exports": [
    { "subscriptionId": "guid", "exportName": "DailyCostExport", "status": "Triggered" }
  ],
  "message": "Triggered 2 export(s). Data should appear within 5-15 minutes."
}
```

---

## Finding Metadata Endpoints

### GET /api/settings/metadata
List all finding metadata rules.

**Response:**
```json
[
  {
    "id": "guid",
    "moduleCode": "NETWORK",
    "category": "NSG Configuration",
    "findingPattern": null,
    "baseHours": 2.0,
    "perResourceHours": 0.5,
    "impactOverride": "High",
    "defaultOwner": "Network Team",
    "downtimeRequired": "None",
    "downtimeMinutes": 0,
    "complexity": "Medium",
    "riskLevel": "Medium",
    "costImplication": "None",
    "changeControlRequired": true,
    "maintenanceWindowRequired": false,
    "affectsProduction": true,
    "matchPriority": 50,
    "isActive": true,
    "notes": "NSG changes require CAB approval"
  }
]
```

### POST /api/settings/metadata
Create metadata rule.

### PUT /api/settings/metadata/{id}
Update metadata rule.

### DELETE /api/settings/metadata/{id}
Delete metadata rule.

### GET /api/settings/effort/categories
Get list of all finding categories for autocomplete.

---

## Roadmap Plan Endpoints

### GET /api/customers/{id}/roadmap-plan
Get saved remediation roadmap plan.

**Response:**
```json
{
  "id": "guid",
  "customerId": "guid",
  "wave1Findings": "[\"finding-id-1\", \"finding-id-2\"]",
  "wave2Findings": "[\"finding-id-3\"]",
  "wave3Findings": "[\"finding-id-4\", \"finding-id-5\"]",
  "skippedFindings": "[\"finding-id-6\"]",
  "updatedBy": "Portal User",
  "updatedAt": "2025-01-07T12:00:00Z"
}
```

### POST /api/customers/{id}/roadmap-plan
Save remediation roadmap plan.

**Request:**
```json
{
  "wave1Findings": "[\"id1\", \"id2\"]",
  "wave2Findings": "[\"id3\"]",
  "wave3Findings": "[\"id4\"]",
  "skippedFindings": "[\"id5\"]",
  "updatedBy": "Portal User"
}
```

---

## Authentication

The portal uses Azure Static Web Apps built-in authentication with Entra ID. API calls from the portal include authentication headers automatically.

For direct API testing, endpoints are currently anonymous but should be secured in production.
