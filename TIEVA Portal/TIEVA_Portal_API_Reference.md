# TIEVA Portal - API Reference

**Last Updated:** January 2025 (v2.2 - FinOps Enhancements)

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
    "code": "CUST01",
    "industry": "Technology",
    "primaryContact": "John Smith",
    "email": "john@example.com",
    "phone": "01onal2345",
    "nextMeetingDate": "2025-02-01T00:00:00Z",
    "schedulingEnabled": true,
    "finOpsStorageAccount": "stfinopscustomer",
    "finOpsContainer": "ingestion",
    "finOpsPowerBIUrl": "https://...",
    "finOpsSasExpiry": "2025-06-01T00:00:00Z",
    "isActive": true,
    "createdAt": "2024-12-01T00:00:00Z"
  }
]
```

#### GET /api/customers/{id}
Get single customer with full details.

#### POST /api/customers
Create customer.

**Request:**
```json
{
  "name": "Customer Name",
  "code": "CUST01",
  "industry": "Technology",
  "primaryContact": "John Smith",
  "email": "john@example.com",
  "nextMeetingDate": "2025-02-01",
  "schedulingEnabled": true,
  "finOpsStorageAccount": "stfinopscustomer",
  "finOpsContainer": "ingestion"
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
    "tenantId": "guid",
    "tenantName": "Customer Tenant",
    "clientId": "guid",
    "secretExpiry": "2025-06-01T00:00:00Z",
    "isActive": true,
    "lastValidated": "2025-01-01T00:00:00Z",
    "lastValidationStatus": "Success",
    "subscriptions": [
      {
        "id": "guid",
        "subscriptionId": "azure-sub-guid",
        "subscriptionName": "Production",
        "tierId": "guid",
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
Create connection.

**Request:**
```json
{
  "customerId": "guid",
  "tenantId": "guid",
  "tenantName": "Customer Tenant",
  "clientId": "guid",
  "clientSecret": "secret-value",
  "secretExpiry": "2025-06-01"
}
```

#### PUT /api/connections/{id}
Update connection (leave clientSecret blank to keep existing).

#### DELETE /api/connections/{id}
Delete connection (cascades to subscriptions, assessments).

#### POST /api/connections/{id}/validate
Validate connection credentials against Azure.

**Response:**
```json
{
  "success": true,
  "message": "Connection validated successfully",
  "subscriptionCount": 5
}
```

#### POST /api/connections/{id}/sync
Sync subscriptions from Azure.

**Response:**
```json
{
  "success": true,
  "added": 2,
  "removed": 0,
  "total": 7
}
```

#### GET /api/connections/{connectionId}/audit-subscriptions/{moduleCode}
Get subscriptions to audit for a specific module (filtered by tier-module mappings).

---

### Subscriptions

#### GET /api/subscriptions
List all subscriptions across all connections.

#### PUT /api/subscriptions/{id}
Update subscription (tier, environment, scope).

**Request:**
```json
{
  "tierId": "guid",
  "environment": "Production",
  "isInScope": true
}
```

---

### Service Tiers

#### GET /api/tiers
List all tiers with module mappings.

**Response:**
```json
[
  {
    "id": "guid",
    "name": "Premium",
    "displayName": "Premium",
    "description": "Full assessment coverage",
    "color": "#4F46E5",
    "sortOrder": 1,
    "isActive": true,
    "modules": [
      {
        "id": "guid",
        "moduleCode": "NETWORK",
        "moduleName": "Network Topology",
        "frequency": "Monthly",
        "isIncluded": true
      }
    ]
  }
]
```

#### PUT /api/tiers/modules
Update tier-module matrix (bulk update).

**Request:**
```json
{
  "updates": [
    {
      "tierModuleId": "guid",
      "isIncluded": true,
      "frequency": "Monthly"
    }
  ]
}
```

---

### Assessments

#### GET /api/assessments
List all assessments.

**Query Parameters:**
- `customerId` - Filter by customer
- `status` - Filter by status (Pending, Processing, Completed, Failed)

#### GET /api/assessments/{id}
Get assessment with module results and findings.

**Response:**
```json
{
  "id": "guid",
  "customerId": "guid",
  "customerName": "Customer Name",
  "status": "Completed",
  "startedAt": "2025-01-05T10:00:00Z",
  "completedAt": "2025-01-05T10:15:00Z",
  "startedBy": "user@example.com",
  "triggerType": "Manual",
  "scoreOverall": 78.5,
  "findingsTotal": 45,
  "findingsHigh": 5,
  "findingsMedium": 20,
  "findingsLow": 20,
  "moduleResults": [
    {
      "id": "guid",
      "moduleCode": "NETWORK",
      "status": "Completed",
      "score": 82.0,
      "findingsCount": 15,
      "durationSeconds": 120,
      "blobPath": "assessments/2025/01/05/network.xlsx"
    }
  ],
  "findings": [...]
}
```

#### DELETE /api/assessments/{id}
Delete assessment (cascades to module results and findings).

#### GET /api/assessments/{id}/changes
Get change summary compared to previous assessment.

**Response:**
```json
{
  "newCount": 5,
  "recurringCount": 35,
  "resolvedCount": 8,
  "newFindings": [...],
  "resolvedFindings": [...]
}
```

#### GET /api/assessments/{id}/resolved
Get findings resolved since previous assessment.

#### POST /api/assessments/modules/{moduleResultId}/reparse
Re-parse module results from Excel file.

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

## Finding Metadata Endpoints

### GET /api/settings/metadata
List all finding metadata rules.

**Response:**
```json
[
  {
    "id": 1,
    "moduleCode": "NETWORK",
    "category": "NSG Configuration",
    "findingPattern": null,
    "baseHours": 2.0,
    "perResourceHours": 0.5,
    "impactOverride": "High",
    "defaultOwner": "Network Team",
    "downtimeRequired": "None",
    "changeControlRequired": true,
    "complexity": "Medium",
    "matchPriority": 100,
    "isActive": true
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
  "id": 1,
  "customerId": "guid",
  "wave1Findings": "[\"finding-id-1\", \"finding-id-2\"]",
  "wave2Findings": "[\"finding-id-3\"]",
  "wave3Findings": "[\"finding-id-4\", \"finding-id-5\"]",
  "skippedFindings": "[\"finding-id-6\"]",
  "notes": "Focus on security first",
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
  "notes": "Updated priorities",
  "updatedBy": "Portal User"
}
```

---

## FinOps Endpoints

### GET /api/customers/{id}/finops/cost-analysis
Get cost analysis data from FOCUS parquet files.

**Query Parameters:**
- `period` - Period to analyze: MTD, 30, 60, 90 (default: 30)

**Response:**
```json
{
  "success": true,
  "data": {
    "totalCost": 15234.56,
    "recordCount": 45000,
    "dateRange": { "start": "2024-12-01", "end": "2025-01-01" },
    "byService": [
      { "service": "Virtual Machines", "cost": 8500.00, "percentage": 55.8 }
    ],
    "byResourceGroup": [...],
    "bySubscription": [...],
    "dailyTrend": [
      { "date": "2025-01-01", "cost": 450.00 }
    ],
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
  "reservations": [
    {
      "id": "/providers/Microsoft.Capacity/reservationOrders/...",
      "name": "VM_RI_01",
      "type": "VirtualMachines",
      "sku": "Standard_D4s_v3",
      "quantity": 2,
      "term": "P1Y",
      "expiryDate": "2025-06-15",
      "autoRenew": true,
      "utilization1Day": 95.5,
      "utilization7Day": 92.3,
      "utilization30Day": 88.7,
      "status": "Active"
    }
  ],
  "insights": [
    {
      "type": "renew",
      "severity": "info",
      "reservationId": "...",
      "message": "High utilization reservation expiring soon - recommend renewal",
      "icon": "✅"
    },
    {
      "type": "cancel",
      "severity": "critical",
      "reservationId": "...",
      "message": "Zero utilization - exchange or cancel immediately",
      "icon": "🚨"
    }
  ],
  "summary": {
    "total": 15,
    "active": 12,
    "expiring30Days": 2,
    "averageUtilization": 78.5,
    "totalMonthlySavings": 2500.00
  },
  "purchaseRecommendations": [
    {
      "scope": "subscriptions/...",
      "sku": "Standard_E4s_v3",
      "term": "P1Y",
      "quantity": 3,
      "estimatedSavings": 3600.00,
      "netSavings": 2800.00
    }
  ],
  "lastRefreshed": "2025-01-13T10:00:00Z"
}
```

### POST /api/customers/{id}/finops/reservations/refresh
Trigger async refresh of reservation data.

**Response:**
```json
{
  "status": "Processing",
  "message": "Reservation refresh started"
}
```

### GET /api/customers/{id}/finops/reservations/status
Check reservation cache status.

**Response:**
```json
{
  "status": "Completed",
  "lastRefreshed": "2025-01-13T10:00:00Z"
}
```

### POST /api/customers/{id}/finops/sas
Save SAS token to Key Vault.

**Request:**
```json
{
  "sasToken": "sv=2022-11-02&ss=b&srt=o&sp=rl&se=2025-06-01...",
  "expiryDate": "2025-06-01"
}
```

### POST /api/customers/{id}/finops/run-export
Trigger Cost Management export (discovers and runs exports across subscriptions).

**Response:**
```json
{
  "success": true,
  "message": "Exports triggered",
  "subscriptionResults": [
    {
      "subscription": "Production",
      "subscriptionId": "guid",
      "status": "OK",
      "exportsFound": 2,
      "exportNames": ["tieva-daily-focus-cost", "tieva-monthly-focus-export"]
    }
  ]
}
```

---

## Audit API Endpoints

### POST /api/assessments/start
Queue assessment for processing (async).

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
  "status": "Processing",
  "message": "Assessment queued for processing"
}
```

**Notes:**
- Returns immediately after queuing
- Assessment runs asynchronously via ProcessAssessment queue trigger
- Poll GET /api/assessments/{id} for status updates

### POST /api/finops/setup
Setup FinOps Cost Management exports for a customer tenant.

**Request:**
```json
{
  "connectionId": "guid",
  "storageAccountName": "stfinopscustomer",
  "containerName": "ingestion"
}
```

**Response:**
```json
{
  "success": true,
  "message": "Cost Management exports configured",
  "exportsCreated": [
    { "subscription": "Production", "exportName": "tieva-daily-focus-cost" }
  ]
}
```

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

## Authentication

The portal uses Azure Static Web Apps built-in authentication with Entra ID. API calls from the portal include authentication headers automatically through SWA linking.

**SWA-Linked Endpoints:**
- Browser requests to `/api/*` proxy through SWA
- No API keys needed for portal requests
- External calls (e.g., Postman, scripts) may need direct function URLs

**Direct Function Access:**
- Use full function URL: `https://func-tievaportal-6612.azurewebsites.net/api/...`
- Currently anonymous for testing (should be secured in production)

---

## Rate Limits & Timeouts

| Operation | Timeout | Notes |
|-----------|---------|-------|
| SWA proxy | 4 minutes | Azure Static Web App limit |
| Assessment start | Immediate | Returns after queuing |
| Assessment processing | 10+ minutes | Runs async via queue |
| FinOps reservation refresh | 30+ seconds | Poll for completion |
| Cost analysis | 30 seconds | Depends on data volume |
