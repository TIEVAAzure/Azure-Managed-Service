# TIEVA Portal - Project Status

## Last Updated: 2026-01-20 (Performance V2 - Metrics Calculation Fixes)

---

## Recent Changes (2026-01-20 - Afternoon Session)

### Performance V2 - Complete Metrics Fix

**Status:** ✅ Complete - Deployed & Verified Working

**Issues Fixed:**

#### 1. CPU Showing Millions Percent
- **Problem:** CPU values like 8,302,396% displayed instead of proper percentages
- **Root Cause:** LogicMonitor `CPUBusyPercent` returns raw counter values, not percentages
- **Fix:** Added percentage validation - skip datapoints where values > 100
- **Result:** CPU now shows correct values like 3.4%

#### 2. Memory Showing "-" (Missing)
- **Problem:** Memory metric completely missing from device data
- **Root Cause:** CALC:MEMORY formula couldn't find datapoints within valid column range
- **Fix:** 
  - Try `MemoryUtilizationPercent` directly first (if available)
  - Fallback to calculating `100 - (FreePhysicalMemory / TotalVisibleMemorySize * 100)`
  - Added detailed logging for debugging
- **Result:** Memory now shows correct values like 37.7%

#### 3. Disk Showing 100% (All Drives)
- **Problem:** All disks showing 100% usage which was clearly wrong
- **Root Cause:** Unit mismatch - Capacity returned in **bytes**, FreeSpace in **GB**
  ```
  Capacity[0] = 113075974144 (bytes = ~105 GB)
  FreeSpace[0] = 16.7161 (already in GB)
  Calculation: 100 - (16.7 / 113075974144 * 100) = 99.999999% ❌
  ```
- **Fix:** Auto-detect unit mismatch and convert:
  ```csharp
  if (capacity > 1_000_000_000 && free < 100_000)  // bytes vs GB
      capacity = capacity / 1073741824.0;  // convert to GB
  ```
- **Result:** Disk now shows correct values like 87.8%

#### 4. Only One Disk Instance Shown
- **Problem:** Code used `instances.Items.First()` which picked D:\ (recovery partition) instead of C:\
- **Fix:** New `FetchAllDiskMetricsAsync()` method processes ALL disk instances:
  - Each drive stored separately: `Disk (C:)`, `Disk (D:)`, etc.
  - Overall `Disk` metric shows the WORST (highest usage) drive for alerting
- **Result:** All drives now visible with individual metrics

#### 5. Frontend Individual Disks Display
- **Problem:** Only CPU/Memory/Disk cards shown in modal, no individual drives
- **Fix:** Added "💾 Individual Disks" section to device modal:
  - Displays all disk metrics from `allMetrics` that start with "Disk ("
  - Color-coded by status (green/yellow/red)
  - Shows avg, max, and recommendation badge
- **Status:** Code ready, pending frontend deploy

**Files Modified:**
- `functions/TIEVA.Functions/Functions/LMPerformanceV2Functions.cs`
  - `FetchMetricDataAsync()` - Added percentage validation
  - `TryCalculateMetric()` - Added MemoryUtilizationPercent fallback, detailed logging
  - `FetchAllDiskMetricsAsync()` - NEW - Processes all disk instances
  - Unit conversion for bytes→GB mismatch
- `portal/index.html`
  - `showDevicePerformance()` - Added individual disks section

**Verified Working:**
```
CPU: 0.3% (Avg | Max: 11.0%) - Oversized ✅
Memory: 37.7% (Avg | Max: 38.6%) - Right-sized ✅
Disk: 87.8% (Avg | Max: 87.8%) - Undersized ✅
```

**Known Issues Remaining:**
- Azure resource types (Storage, Disk, VM, Network) have metric mapping issues
- See `ISSUES_TRACKER.md` for details

**To Deploy Frontend:**
```bash
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal"
git add -A && git commit -m "Add individual disk metrics to device modal" && git push
```

---

## Recent Changes (2026-01-20 - Evening Session)

### Performance V2 - Device Detail Modal Fix

**Status:** ✅ Complete - Ready for Deployment

**Issue:** Performance monitoring information not displaying correctly in device detail modal. When clicking on a device in the Performance V2 tab, it showed "No metrics found" or stale data.

**Root Cause:**
- The V2 Performance system stores data in `LMDeviceMetricsV2` table
- But the `showDevicePerformance()` frontend function was calling the V1 API endpoint
- V1 endpoint: `/logicmonitor/customers/{id}/devices/{deviceId}/performance` → reads from `LMDeviceMetrics` table
- V2 endpoint (new): `/v2/performance/customers/{id}/devices/{deviceId}` → reads from `LMDeviceMetricsV2` table
- Data source mismatch = device clicks showed no data

**What was fixed:**

1. **Backend - New V2 Device Endpoint** (`LMPerformanceV2Functions.cs`)
   - Added `GetPerformanceV2Device` function
   - Route: `GET /v2/performance/customers/{customerId}/devices/{deviceId}`
   - Returns CPU/Memory/Disk metrics in both V1-compatible format AND full V2 format
   - Includes SKU info, overall status, recommendations, all flexible metrics

2. **Frontend - Updated API Call** (`portal/index.html`)
   - Changed `showDevicePerformance()` to call V2 endpoint
   - Before: `/logicmonitor/customers/${customerId}/devices/${deviceId}/performance`
   - After: `/v2/performance/customers/${customerId}/devices/${deviceId}`

**New V2 Device Endpoint Response Format:**
```json
{
  "source": "v2",
  "deviceId": 12345,
  "deviceName": "VM-01",
  "resourceType": "WindowsServer",
  "overallStatus": "Healthy",
  "overallRecommendation": "Right-sized",
  "lastSyncedAt": "2026-01-20T10:00:00Z",
  "cpu": { "avg": 25.5, "max": 45.2, "avg7d": 25.5, "max7d": 45.2, "status": "Healthy" },
  "memory": { "avg": 60.1, "max": 72.3, "status": "Warning" },
  "disk": { "avg": 35.0, "max": 40.0, "status": "Healthy" },
  "allMetrics": [...]
}
```

**Files Modified:**
- `functions/TIEVA.Functions/Functions/LMPerformanceV2Functions.cs` - Added new endpoint
- `portal/index.html` - Updated API call in showDevicePerformance()

**To Deploy:**
```bash
# Backend - Deploy Azure Functions
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions"
func azure functionapp publish func-tievaportal-6612

# Frontend - Deploy Static Web App
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\portal"
git add -A
git commit -m "Fix: Device detail modal now uses V2 endpoint for LMDeviceMetricsV2 data"
git push
```

**Security Review:** ✅ Follows existing patterns
- Uses same authentication as other V2 endpoints (Anonymous with Azure AD protection)
- Input validation for customerId (GUID) and deviceId (int)
- No new secrets or permissions required

**Best Practices Review:** ✅ Well-Architected compliant
- Minimal change - single endpoint addition + single line frontend fix
- Backwards compatible - V1 endpoint still exists for legacy callers
- Consistent with existing V2 API patterns

---

## Recent Changes (2026-01-20 - Morning Session)

### History Sync Tab - UI Consistency Update

**Status:** ✅ Complete - Ready for Deployment

**Issue:** The sync button in the Performance Admin > History Sync tab was embedded inside a card, inconsistent with the customer Monitoring section where sync buttons are in a header bar at the top of the tab.

**What was changed:**

1. **Moved sync bar to top of tab** - Consistent with customer Monitoring pattern
   - Customer dropdown, "Last synced" status, and sync buttons are now in a header bar
   - Layout: `[Customer Dropdown] [Last synced: timestamp] ... [Refresh Status] [Start Sync]`

2. **Updated `showPerfAdminTab` function**
   - Added 'history-sync' to the tab hiding logic
   - Added `loadHistorySyncTab()` call when tab is selected

3. **New JavaScript functions added:**
   - `loadHistorySyncTab()` - Populates customer dropdown (LM-enabled only), sets up onchange handler
   - `startHistorySync()` - Starts 90-day history sync with button state management
   - `refreshHistorySyncStatus()` - Checks current sync status for selected customer
   - `updateHistorySyncDisplay()` - Updates all status display elements
   - `startHistorySyncPolling()` - Polls status every 3 seconds during active sync

4. **Improved status card**
   - Added "Elapsed" time display
   - Color-coded status tags (green=Completed, blue=Processing, orange=Queued, red=Error)
   - Progress bar with brand color
   - 4-column grid layout for metrics

**UI Pattern (now consistent with customer Monitoring):**
```html
<!-- Sync Bar at top -->
<div style="display:flex;justify-content:space-between;align-items:center">
  <div style="display:flex;align-items:center;gap:16px">
    [Customer Select] [Last synced: timestamp]
  </div>
  <div style="display:flex;gap:8px">
    [Refresh Status Button] [Start Sync Button]
  </div>
</div>

<!-- Status Card below -->
<div class="card">...</div>
```

**Files Modified:**
- `portal/index.html` - HTML structure and JavaScript functions

**To Deploy:**
```bash
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\portal"
git add -A
git commit -m "History Sync tab - sync bar moved to top for UI consistency"
git push
```

---

## Previous Changes (2026-01-19 - Performance Graphs & SKU Recommendations)

---

## Recent Changes (2026-01-19 - Evening Session)

### Performance Graphs & SKU-Based Recommendations

**Status:** ✅ Backend Complete (with Queue-based Bulk Sync)

**Purpose:**
Provide 90-day performance graphs for resources and intelligent SKU sizing recommendations that consider current SKU family to suggest appropriate up/down sizing.

**What was added:**

1. **New Database Tables:**
   - `LMDeviceMetricHistory` - Daily metric aggregates for 90-day graphs (CPU, Memory, Disk)
   - `AzureSkuFamilies` - SKU family definitions with size ordering for VMs, Disks, App Service Plans

2. **Updated LMDeviceMetricsV2:**
   - `CurrentSku` - Current Azure SKU (e.g., Standard_D4s_v5)
   - `SkuFamily` - SKU family (e.g., Dsv5)
   - `RecommendedSku` - Suggested SKU based on 90-day analysis
   - `SkuRecommendationReason` - Why this recommendation
   - `PotentialMonthlySavings` - Estimated savings from right-sizing
   - `Metrics90DayJson` - 90-day aggregate metrics (CPU/Mem/Disk P95, Avg, Max)

3. **New API Endpoints:**
   | Method | Route | Purpose |
   |--------|-------|--------|
   | GET | `/v2/performance/customers/{id}/devices/{deviceId}/history` | 90-day performance data for graphs |
   | GET | `/v2/performance/sku-families` | Get all SKU family definitions |
   | GET | `/v2/performance/customers/{id}/devices/{deviceId}/sku-recommendation` | Get SKU recommendation |
   | POST | `/v2/performance/customers/{id}/devices/{deviceId}/sync-history` | Sync 90-day history (single device) |
   | POST | `/v2/performance/customers/{id}/sync-history/start` | **Start bulk sync (queue-based)** |
   | GET | `/v2/performance/customers/{id}/sync-history/status` | **Get bulk sync progress** |

4. **SKU Recommendation Logic:**
   - Uses 90-day P95 metrics (not just averages)
   - CPU P95 < 30% AND Memory P95 < 40% → Recommend downsize
   - CPU P95 > 85% OR Memory P95 > 90% → Recommend upsize
   - Already at smallest SKU? Suggests B-series alternative
   - Already at largest SKU? Suggests different SKU family
   - Calculates potential monthly savings

5. **Pre-seeded SKU Families:**
   - VMs: Dsv5, Esv5, Bs (burstable), Fsv2
   - Managed Disks: Premium_LRS (P4-P80), StandardSSD_LRS (E4-E50)
   - App Service Plans: Pv3, Sv1, Bv1

6. **Queue-based Bulk Sync:**
   - Uses `lm-history-sync` Azure Storage Queue
   - Avoids timeout issues (Static Web App limits)
   - Processes one device at a time with rate limiting (500ms between chunks)
   - Tracks progress in `LMSyncStatuses` table
   - Frontend can poll status endpoint for progress

**Files Created:**
- `sql/PerformanceGraphsAndSku.sql` - Database schema and seed data
- `functions/TIEVA.Functions/Functions/LMPerformanceGraphFunctions.cs` - New API endpoints

**Files Modified:**
- `functions/TIEVA.Functions/Models/Entities.cs` - Added new entities
- `functions/TIEVA.Functions/Services/TievaDbContext.cs` - Added DbSet for new tables

**To Deploy:**
1. Run SQL script: `sql/PerformanceGraphsAndSku.sql`
2. Deploy functions: `func azure functionapp publish func-tievaportal-6612`
3. Frontend changes (pending)

---

## Earlier Changes (2026-01-19 - PM Session)

### Performance Admin - Bulk Discovery Tab Fix

**Status:** ✅ Complete - Ready for Deployment

**Issue Fixed:**
The Performance Admin page had a "🔍 Bulk Discovery" tab button, but the tab content (`perfAdminTab-discovery`) did not exist. Clicking the tab would fail.

**What was added:**

1. **Bulk Discovery Tab Content** - New complete tab in Performance Admin page
   - Customer dropdown to select which customer to discover for
   - "⚡ Run Bulk Discovery" button - scans all resource types and generates SQL
   - "🔎 Scan for Unmapped Devices" button - finds devices without matching resource types
   - Current Mappings Overview - shows all resource types with collapsible mapping details
   - Per-type discovery - click "Discover" on any resource type to find datapoints
   - Click-to-add workflow - discovered datapoints can be added as mappings with one click

2. **Updated showPerfAdminTab Function** - Now properly handles the 'discovery' tab
   - Hides/shows the new `perfAdminTab-discovery` div
   - Calls `loadPerfAdminDiscovery()` when tab is selected

3. **New JavaScript Functions Added:**
   - `loadPerfAdminDiscovery()` - Loads customer dropdown and initializes tab
   - `loadPerfAdminDiscoveryTypes()` - Fetches and renders resource types with mappings
   - `toggleAdminRtMappings(rtId)` - Expands/collapses mappings for a resource type
   - `discoverFromAdmin(rtId, rtCode, rtName)` - Discovers devices for a resource type
   - `runDiscoveryFromAdmin(rtId, rtCode)` - Runs discovery on selected device
   - `quickAddMappingFromAdmin(rtId, datasourceName, datapointName)` - Click-to-add mapping
   - `runBulkDiscoveryFromAdmin()` - Runs bulk discovery for all resource types
   - `copyBulkSQLFromAdmin()` - Copies generated SQL to clipboard
   - `scanForUnmappedFromAdmin()` - Shows unmapped devices

**Files Modified:**
- `portal/index.html` - Added discovery tab content, updated showPerfAdminTab function, added JavaScript functions

---

## Previous Changes (2026-01-19 - AM Session)

### Performance V2 - Admin UI & Discovery Tools

**Status:** ✅ Complete - Ready for Deployment

**What was added:**

1. **Settings Tab** - New sub-tab in Performance Monitoring for admin configuration
   - View all resource types grouped by category
   - Edit resource type properties (display name, detection patterns, hasPerformanceMetrics)
   - CRUD operations for metric mappings (no SQL required)

2. **Inline Discovery** - Click "🔍 Discover" on any resource type
   - Shows devices of that type
   - Fetches all datasources/datapoints from LogicMonitor
   - Click any datapoint → opens pre-filled "Add Mapping" modal
   - No more copy/paste!

3. **Bulk Discovery & SQL Generation** - "⚡ Generate SQL for All Mappings" button
   - Scans all resource types with HasPerformanceMetrics = true
   - Discovers datapoints from sample devices
   - Generates INSERT SQL for unmapped performance datapoints
   - Copy SQL to clipboard and run in Azure SQL

4. **Scan for Unmapped Types** - "🔎 Scan for Unmapped Resource Types" button
   - Finds devices that don't match any resource type
   - Shows datasource patterns for creating new types

**New API Endpoints:**
| Method | Route | Purpose |
|--------|-------|--------|
| GET | `/v2/performance/resource-types` | All resource types with mappings |
| PUT | `/v2/performance/resource-types/{id}` | Update resource type |
| POST | `/v2/performance/resource-types/{id}/mappings` | Create mapping |
| PUT | `/v2/performance/mappings/{id}` | Update mapping |
| DELETE | `/v2/performance/mappings/{id}` | Delete mapping |
| GET | `/v2/performance/customers/{id}/devices/{deviceId}/discover` | Discover datapoints |
| GET | `/v2/performance/customers/{id}/bulk-discover` | Bulk discover + generate SQL |

**Files Modified:**
- `functions/TIEVA.Functions/Functions/LMPerformanceV2Functions.cs` - Added 7 new endpoints
- `portal/index.html` - Added Settings sub-tab with full admin UI

---

## Recent Changes (2026-01-18)

### Performance V2 - Sync Debugging & Fixes

**Issues Fixed:**

1. **Resource Type Matching Bug**
   - Problem: All 268 devices showing as "Unknown" or "MetadataOnly"
   - Root cause: `OrderByDescending(rt => rt.SortOrder)` - MetadataOnly (SortOrder=900) checked FIRST
   - Fix: Changed to `OrderBy(rt => rt.SortOrder)` - lower SortOrder = higher priority

2. **Queue Processor Failures**
   - Problem: Messages moving to poison queue, 4ms duration
   - Root causes: Double-decode of Base64, DbContext scoping issues
   - Fix: Switched to synchronous `/sync/run` endpoint

3. **Credential Configuration**
   - V2 functions now use Azure Key Vault (matching V1 pattern)
   - GetLogicMonitorServiceAsync tries per-customer credentials first, falls back to global

4. **Frontend Timeout Handling**
   - Detects timeout errors (502, 504, "Backend call failure")
   - Treats timeout as "sync started", begins polling immediately
   - Poll every 2 seconds until Completed/Error/Idle

**Device Distribution After Fixes:**
- AzureDisk: 103 devices
- AzureNetwork: 34
- MetadataOnly: 34
- AzureBackup: 27
- AzureVM: 26 (22 Healthy, 3 Warning, 1 Unknown)
- WindowsServer: 23 (all Healthy)
- AzureStorage: 16 (5 Critical, 11 Unknown)
- Plus others...

---

### Performance Monitoring V2 - Data-Driven Architecture

**Status:** ✅ Backend Complete, Frontend Complete, Deployed

**Problem Solved:**
The original performance system forced all resources into CPU/Memory/Disk buckets, which doesn't work for:
- Azure SQL (DTU%, CPU%, Storage%)
- App Services (Requests/sec, Response time)
- Storage Accounts (Capacity, Transactions)
- And many other Azure PaaS resources

**Solution: Self-Discovering, Data-Driven System**

New resources types are auto-detected from LogicMonitor datasources. Admin can configure them via UI without code changes.

**New Database Tables:**
| Table | Purpose |
|-------|--------|
| `LMResourceTypes` | 22 pre-seeded resource type definitions (WindowsServer, AzureSQL, AppService, etc.) |
| `LMMetricMappings` | Which metrics to fetch per type (CPU, Memory, DTU, IOPS, etc.) |
| `LMDeviceMetricsV2` | Actual device metrics with flexible JSON storage |

**Core API Endpoints (V2):**
| Method | Route | Purpose |
|--------|-------|--------|
| GET | `/v2/performance/customers/{id}/summary` | Summary grouped by resource type |
| GET | `/v2/performance/customers/{id}/types/{code}` | Devices of a specific type with metrics |
| GET | `/v2/performance/customers/{id}/sync/status` | Check sync progress |
| POST | `/v2/performance/customers/{id}/sync/run` | Run sync synchronously |

**How It Works:**
```
1. Sync starts → Loads resource types from database
2. For each device → Gets datasources from LogicMonitor
3. Matches datasource to resource type using DetectionPatternsJson (by SortOrder priority)
4. Fetches metrics based on MetricMappings for that type
5. Stores results in LMDeviceMetricsV2 as flexible JSON
6. Admin can add new resource types and mappings via UI - no code changes!
```

**Frontend Features:**
- Performance tab shows resources grouped by type (Compute, Database, Storage, etc.)
- Click on resource type card to see all devices with type-specific metrics
- Sync button with progress polling and timeout handling
- Settings sub-tab for admin configuration

---

---

## Recent Changes (2026-01-15)

### Per-Customer LogicMonitor Portal Integration

**Status:** ⏳ Ready for Deployment

**What was done:**
- Full support for customers with their own LogicMonitor portal (not using TIEVA's shared portal)
- Credentials stored securely in Azure Key Vault (auto-created when saved)
- Frontend already complete with test connection and delete credentials buttons
- Backend auto-creates Key Vault secrets: `LM-{CustomerId}-Company`, `LM-{CustomerId}-AccessId`, `LM-{CustomerId}-AccessKey`
- Falls back to TIEVA global credentials if customer-specific not configured

**Files Modified:**
- `functions/TIEVA.Functions/Models/Entities.cs` - Added `LMHasCustomCredentials` property
- `functions/TIEVA.Functions/Functions/LogicMonitorFunctions.cs` - Updated Save/Delete to track flag

**Manual Steps Required:**
1. Run SQL migration: `AddPerCustomerLMCredentials.sql`
2. Grant Function App Key Vault role: "Key Vault Secrets Officer"
3. Redeploy backend: `func azure functionapp publish func-tievaportal-6612`

**Security Notes:**
- Credentials never stored in database - only in Key Vault
- Only LM Company name returned to frontend, never keys
- Key Vault accessed via Managed Identity (DefaultAzureCredential)

---

## Recent Changes (2026-01-13)

### LogicMonitor Group ID - Per-Customer Configuration

**Status:** ✅ Complete

**What was done:**
- Added "LogicMonitor Group ID" field to Customer edit modal
- Per-customer mapping to LM device groups now configurable
- Global API credentials remain in Monitoring > Settings (correct - TIEVA's master credentials)

---

### Alert Table Column Fix

**Status:** ✅ Fixed

**Issue:** Device name showing in Alert column instead of Device column

**Fix:** 
- **Device column**: Now shows `monitorObjectName` (the monitored object/device)
- **Alert column**: Now shows `resourceTemplateName` (the alert type like "Volume Capacity")

---

### Customer Tabs - Icons & Reordering

**Status:** ✅ Complete

**Changes:**
- Added icons to ALL tabs
- Moved Monitoring tab to right after FinOps
- Tab order: Overview → Findings → Roadmap → FinOps → Monitoring → Module Coverage → Scheduling → Assessments → Connections → Subscriptions

---

### Bug Fix - FinOps Reservations Sync Button

**Status:** ✅ Fixed

**Issue:** Button onclick called `syncReservationData()` but function is named `refreshReservationData()`

**Fix:** Changed onclick to call correct function name

---

### Monitoring Tab - SQL Caching with Sync Buttons

**Status:** Frontend updated, Backend needs redeployment, SQL table needed

**What was done:**
1. Added sync bar + "Sync All" button to **Overview** sub-tab
2. Added sync bar + "Sync" button to **Alerts** sub-tab  
3. Added `syncMonitoringAlerts()` JavaScript function
4. Added `syncMonitoringAll()` JavaScript function
5. Both show "Last synced: [timestamp]" like Devices tab

**Files Modified:**
- `portal/index.html` - Frontend sync buttons and functions

**Backend APIs (already in code):**
- `POST /api/logicmonitor/customers/{id}/alerts/sync` - Sync alerts only
- `POST /api/logicmonitor/customers/{id}/sync` - Sync devices + alerts

**Database Tables Required:**
- `LMAlerts` - May need to be created (see SQL below)
- `LMDevices` - Already exists
- `LMSyncStatuses` - Already exists

**Deployment Steps:**
1. Git push frontend (index.html)
2. Deploy backend: `func azure functionapp publish func-tievaportal-6612`
3. Run SQL to create LMAlerts table if missing

---

### FinOps Reservations - Button Relocation

**Status:** ✅ Complete

**What was done:**
- Moved "Refresh" button to top (renamed to "Sync")
- Moved "Export Analysis" button to top, inline with tabs on right side
- Buttons only visible when on Reservations sub-tab

---

## Pending SQL Scripts

### LMAlerts Table (if not exists)
```sql
IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='LMAlerts' AND xtype='U')
BEGIN
    CREATE TABLE LMAlerts (
        Id nvarchar(50) PRIMARY KEY,
        CustomerId uniqueidentifier NOT NULL,
        DeviceId int NULL,
        DeviceDisplayName nvarchar(255) NULL,
        MonitorObjectName nvarchar(255) NULL,
        AlertValue nvarchar(500) NULL,
        Severity int NOT NULL,
        SeverityText nvarchar(50) NULL,
        StartTime datetime2 NOT NULL,
        EndTime datetime2 NULL,
        Cleared bit NOT NULL DEFAULT 0,
        Acked bit NOT NULL DEFAULT 0,
        InSDT bit NOT NULL DEFAULT 0,
        ResourceTemplateName nvarchar(255) NULL,
        LastSyncedAt datetime2 NOT NULL,
        CreatedAt datetime2 NOT NULL
    );
    
    CREATE INDEX IX_LMAlerts_CustomerId ON LMAlerts(CustomerId);
    CREATE INDEX IX_LMAlerts_Severity ON LMAlerts(Severity);
END
```

---

## Architecture Notes

### LogicMonitor Integration Pattern
- **Devices tab**: SQL-cached, manual sync button
- **Alerts tab**: SQL-cached, manual sync button  
- **Overview tab**: Combined sync (devices + alerts)
- All use same `LMSyncStatuses` table for tracking last sync time

### Security
- LM credentials stored in Key Vault (LM-Company, LM-AccessId, LM-AccessKey)
- No API keys in frontend code
- Tier-restricted access (Premium, Standard, AdHoc)

### Best Practices (Well-Architected)
- Caching reduces API calls to LogicMonitor
- User-controlled sync prevents rate limiting issues
- SQL storage enables fast page loads
- Cascading deletes maintain data integrity

---

## Testing Checklist

- [ ] Monitoring Overview tab shows "Last synced" timestamp
- [ ] Monitoring Overview "Sync All" button works
- [ ] Monitoring Alerts tab shows "Last synced" timestamp
- [ ] Monitoring Alerts "Sync" button works
- [ ] Devices tab sync still works (regression test)
- [ ] FinOps Reservations buttons at top, inline with tabs
