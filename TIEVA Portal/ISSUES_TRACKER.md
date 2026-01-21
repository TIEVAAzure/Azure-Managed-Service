# TIEVA Portal - Active Issues Tracker

> **IMPORTANT**: Only remove items when user confirms fix is working in production.

---

## 🔴 OPEN ISSUES

### Issue #3: Historical Performance Data Sync
**Status**: ✅ FIXED - PARTIALLY WORKING
**Reported**: 2026-01-20
**Updated**: 2026-01-20

**Problem**: Historical history sync wasn't working - "No 90-day history data available" shown

**Root Causes Found & Fixed**:
1. ✅ **LM API data format wrong** - `ProcessDailyAggregates` assumed timestamps embedded in Values array, but LM returns separate `Time[]` array
2. ✅ **CALC: patterns not mapped** - `CALC:MEMORY` and `CALC:DISK` needed mapping to actual LM datapoints (`MemoryUtilizationPercent`, `PercentUsed`)
3. ✅ **Frontend data transform** - API returns `{ cpu: [], memory: [], disk: [] }` but frontend expected flat array

**Changes Made**:
1. ✅ `LMPerformanceGraphFunctions.cs` - Fixed `ProcessDailyAggregates` to use separate `Time[]` array (epoch milliseconds)
2. ✅ `LMPerformanceGraphFunctions.cs` - Added CALC: pattern mapping to `SyncDeviceHistory` (single device test endpoint)
3. ✅ `LMPerformanceGraphFunctions.cs` - Added CALC: pattern mapping to `SyncDeviceHistoryInternal` (bulk sync method)
4. ✅ `portal/index.html` - Transform history API response to flat array format for charts

**Current Status**:
- ✅ Sync creates records (tested: 27 records for device 519)
- ✅ API returns history data correctly
- ✅ Bulk sync now has CALC: pattern mapping (was missing, causing some resources to fail)
- ⚠️ Only 9 days showing (not full 90 days) - see Issue #11

**Remaining**:
- ✅ Changed "No 90-day history data available" → "No historical data available"
- ✅ Changed "90-Day History" tab → "Historical Data"
- ✅ Changed "90-Day Trends" → "Historical Trends"
- ✅ Changed "90-Day Historical Metrics" → "Historical Metrics"

---

### Issue #13: History Sync Getting Stuck
**Status**: ✅ FIXED
**Reported**: 2026-01-20
**Updated**: 2026-01-20

**Problem**: History sync started at 18:12:54, processed 143/269 devices (82 with data), then appeared to get stuck.

**Root Cause**: When individual device sync failed with an exception, the message was re-thrown and moved to poison queue. Progress counter was never incremented for failed devices, causing sync to appear stuck.

**Fixes Applied**:

1. **ProcessHistorySyncQueue** - Moved progress tracking to `finally` block:
   - Progress now ALWAYS increments, even on failure
   - Exceptions caught but not re-thrown (avoids poison queue)
   - Failed devices logged with error message

2. **StartBulkHistorySync** - Added stuck detection and force restart:
   - Detects stuck sync (no update in 10+ minutes)
   - Returns `appearsStuck: true` flag
   - Added `?force=true` query param to restart stuck sync

3. **GetHistorySyncStatus** - Added `lastUpdated` and `appearsStuck` to response

4. **Frontend** - Added stuck sync UI:
   - Shows "Stuck" status tag when sync appears stuck
   - Button changes to "Force Restart" when stuck
   - One-click force restart functionality

5. **HistorySyncWatchdog** - Timer function (runs every 5 minutes):
   - Automatically detects stuck syncs (no progress in 5+ minutes)
   - Re-queues remaining devices without manual intervention
   - Marks complete syncs that got stuck at 100%

6. **Rate limit wait capped** - Reduced from 60s to 10s max to prevent Azure scale-down

**Verified Working**: Sync progressed from 147 → 206 → 269 with watchdog auto-restarting

---

### Issue #11: Historical Sync Only Returns ~9 Days Instead of 90
**Status**: OPEN - INVESTIGATION NEEDED
**Reported**: 2026-01-20

**Problem**: History sync for device 519 only created 9 days of data instead of 90 days.

**Observations**:
- Sync processes 3 chunks (30 days each): Oct 22, Nov 21, Dec 21
- Each chunk returns 500 raw data points from LM
- But only ~3 unique dates per chunk pass validation (9 total)

**Possible Causes**:
1. **LM data aggregation** - LogicMonitor may aggregate older data, reducing granularity
2. **Percentage validation too strict** - `value < 0 || value > 100` filters out valid data
3. **Data gaps** - Device may not have been monitored continuously
4. **Timestamp parsing** - Some timestamps may be invalid/skipped

**Investigation Needed**:
- Check raw LM data to see actual date distribution
- Review if percentage validation is filtering too aggressively
- Check if LM returns daily data or higher-frequency samples that need different aggregation

---

### Issue #12: Disk Calculation Producing Negative Percentages
**Status**: ✅ FIXED - VERIFIED WORKING
**Reported**: 2026-01-20
**Updated**: 2026-01-21

**Problem**: Many disks show negative percentage calculations and get filtered out as invalid. Also C: drive missing on some servers.

**Root Causes Found & Fixed**:

1. **Unit mismatch** - Capacity in bytes, FreeSpace in GB
   - `100 - (79GB / 13.4GB * 100) = -490%` ❌

2. **FreeSpace already a percentage** - Some devices return FreeSpace as % not GB
   - Device 535: Capacity=29.3GB, FreeSpace=63.05 → FreeSpace is 63% free, not 63GB

3. **Only first datasource checked** - C: might be in `WinVolumeUsage`, D:/L:/S: in `WinLogicalDisk`

**Fixes Applied** (in `LMPerformanceV2Functions.cs`):

| Fix | Description |
|-----|-------------|
| ✅ Unit detection | Detect if both values in bytes, mixed units, or same unit |
| ✅ FreeSpace % detection | If FreeSpace > Capacity (in GB), FreeSpace is already a percentage |
| ✅ All disk datasources | Fetch from ALL matching datasources, not just first match |
| ✅ Detailed logging | Log conversion strategy for each disk |

**Commits**:
- `b674ed8` - Initial unit mismatch fix
- `95970a3` - Fetch from all disk datasources
- `9afce4f` - Detect FreeSpace as percentage

**Verified Working**:
- Device 535: Now shows C: (37%), D: (98.8%), L: (92.9%), S: (97.1%)
- Device 517: Now shows C: (13.5%), D: (96.4%)
- Device 518: C: detected as percentage (55.1%)

---

### Issue #7: Admin Settings Location Wrong
**Status**: ✅ FIXED
**Reported**: 2026-01-20
**Updated**: 2026-01-21

**Problem**: LM Config (customer LogicMonitor credentials) was inside Performance tab, but it affects ALL monitoring features (Overview, Alerts, Devices, Performance).

**Fixes Applied**:
1. **Moved LM Config** to Monitoring sub-tab level (sibling to Overview, Alerts, Devices, Performance)
2. **Renamed "Admin Settings" button** → **"Performance Admin"** (for global resource types & metric mappings)
3. **Added tooltip** to Performance Admin button explaining its purpose
4. **Removed duplicate LM Config** from inside Performance tab

**Current Structure**:
- Customer > Monitoring > **Overview** | **Alerts** | **Devices** | **Performance** | **LM Config** | Performance Admin
- **LM Config** tab → Customer-specific LogicMonitor credentials (shared or own portal)
- **Performance Admin** button → Opens global perf-admin page (resource types, metric mappings)

---

### Issue #8: Browse Group Button Missing
**Status**: ✅ FIXED
**Reported**: 2026-01-20
**Updated**: 2026-01-21

**Problem**: There was a "Browse Group" button to select which LogicMonitor device group to sync from. This was missing from the new LM Config tab.

**Root Cause**: The Browse Groups functionality existed in the OLD settings modal but was not added to the new Monitoring > LM Config tab.

**Fixes Applied**:

| Component | Change |
|-----------|--------|
| **portal/index.html** | Added "Browse Groups" button to Shared Portal config |
| **portal/index.html** | Added "Browse Groups" button to Customer Portal config |
| **portal/index.html** | Added tree-view group picker with expand/collapse |
| **portal/index.html** | Click group to auto-fill Group ID and Path fields |
| **LogicMonitorFunctions.cs** | Added `GetCustomerLMSubgroups` endpoint |

**New Endpoint**:
```
GET /logicmonitor/customers/{customerId}/groups/{groupId}/subgroups
```

**Commits**:
- `a2cd099` - Backend: Customer-specific subgroups endpoint
- `6e10d85` - Frontend: Browse Groups UI in LM Config tab

**Usage**:
1. Go to Customer > Monitoring > LM Config
2. Click "Browse Groups" button
3. Expand folders with ▶ button
4. Click group name to select it

---

### Issue #9: FinOps Cost Analysis Missing Subscriptions
**Status**: OPEN  
**Reported**: 2026-01-20  

**Problem**: FinOps Cost Analysis is not bringing through all the data - some subscriptions are missing.

**Investigation Needed**:
- Which subscriptions are missing?
- Is this a sync issue or a data source issue?
- Check Azure Cost Management API scope/permissions

---

### Issue #10: LogicMonitor API Rate Limiting Failures
**Status**: ✅ FIXED - NEEDS TESTING
**Reported**: 2026-01-20
**Updated**: 2026-01-20

**Problem**: Performance sync fails with HTTP 429 (Too Many Requests) even after detecting rate limit and waiting.

**Root Cause**: Concurrent requests bypassing rate limit check due to lack of thread safety.

**Fixes Applied** (in `LogicMonitorService.cs`):

| Fix | Description |
|-----|-------------|
| ✅ SemaphoreSlim | Added `_apiSemaphore` (max 3 concurrent requests) |
| ✅ Thread-safe locking | Added `_rateLimitLock` for rate limit variables |
| ✅ Retry with backoff | 3 retries with exponential delays (1s, 5s, 15s) |
| ✅ Retry-After header | Parses header for 429 responses |
| ✅ Reduced batch size | `GetAllDevicesInGroupAsync` now uses batch of 3 (was 5) |

**Code Changes**:
```csharp
// NEW: Thread-safe rate limiting
private static readonly object _rateLimitLock = new object();
private static readonly SemaphoreSlim _apiSemaphore = new SemaphoreSlim(3, 3);
private const int MaxRetries = 3;
private static readonly TimeSpan[] RetryDelays = { 1s, 5s, 15s };

// ExecuteRequestAsync now:
// 1. Acquires semaphore before request
// 2. Uses lock for rate limit checks
// 3. Retries on 429 with Retry-After header support
// 4. Retries on HttpRequestException
```

**Previous Code** (for reference):
```csharp
// Static fields - NOT thread-safe
private static int _remainingRequests = 100;
private static DateTime _rateLimitReset = DateTime.UtcNow;

// Checks BEFORE request, but no retry on 429
if (_remainingRequests <= 5 && DateTime.UtcNow < _rateLimitReset)
{
    await Task.Delay(waitTime);
}
```

**Recommended Fix**:
1. Add `SemaphoreSlim` to limit concurrent requests (max 3-5)
2. Implement retry with exponential backoff for 429 responses (up to 3 retries)
3. Use `lock` or `Interlocked` for thread-safe rate tracking
4. Parse `Retry-After` header when present
5. Reduce parallel batch size in `GetAllDevicesInGroupAsync`

**Security Consideration**: ✅ Rate limiting is a best practice - prevents API abuse and ensures fair usage.

**Well-Architected**: ⚠️ Current implementation violates Reliability pillar - no retry pattern for transient failures.

**Testing Strategy**:
1. Add diagnostic logging to count concurrent requests
2. Test with single device first to verify retry logic
3. Gradually increase parallelism while monitoring rate limit headers

---

### Issue #5: Azure Resource Type Metric Mappings Need Review
**Status**: OPEN - LOW PRIORITY  
**Reported**: 2026-01-20  

**Problem**: Many Azure resource types have metric mappings that don't match available datapoints or return non-percentage values.

**Key Stats from Dashboard**:
- **Unknown**: 438 resources (insufficient data)
- **Critical**: 70 resources (needs attention)
- **Azure VM**: 37 resources - ALL showing ✗ (failed metrics)

**Affected Types** (from logs):
| Resource Type | Issue |
|--------------|-------|
| AzureStorage | Latency in ms, Capacity datapoints unavailable |
| AzureDisk | IOPS%/Bandwidth% don't exist |
| AzureVM | 37 resources all failing - metrics outside 0-100 range |
| AzureNetwork | Latency in ms, datasource mismatch |

---

### Issue #14: Overview Findings/Recommendations Click-Through
**Status**: ✅ FIXED
**Reported**: 2026-01-20
**Updated**: 2026-01-21

**Problem**: When clicking on a finding or recommendation in the Overview section, users want to see more detailed information.

**Fixes Applied**:
1. **Severity summary cards now clickable** - Click High/Medium/Low to filter findings
2. **Switches to Findings tab** with selected severity filter applied
3. **Hover effects** added to show cards are interactive

---

### Issue #15: Monitoring Click-Through for Alerts/Devices
**Status**: ✅ FIXED
**Reported**: 2026-01-20
**Updated**: 2026-01-21

**Problem**: In Monitoring > Overview, Alerts, and Devices tabs, users want to click on alerts or devices to get more information.

**Fixes Applied**:
1. **Alert severity cards now clickable** - Click Critical/Error/Warning/Info to filter alerts
2. **Switches to Alerts tab** with selected severity filter applied
3. **Alerts tab has filter buttons** - All, Critical, Error, Warning, Info
4. **Hover effects** added to show cards are interactive

---

### Issue #17: Assessment Scoring Too Aggressive
**Status**: OPEN - INVESTIGATION NEEDED
**Reported**: 2026-01-21

**Problem**: Assessment scoring appears too aggressive - customers showing low scores (e.g., 40%) that may not accurately reflect their actual posture.

**Investigation Needed**:
- Review scoring algorithm and weight distribution
- Check if findings are being counted correctly
- Verify severity multipliers are appropriate
- Consider if passing checks should contribute positively to score
- Compare scores across multiple customers to identify patterns

**Expected Behavior**:
- Scores should reflect actual compliance/health posture
- A customer with minor issues shouldn't score 40%
- Scoring should be balanced and actionable

---

### Issue #6: Limited PaaS Service Coverage
**Status**: OPEN - ENHANCEMENT
**Reported**: 2026-01-20

**Problem**: Most Azure PaaS services have no performance metrics configured.

**Dashboard Evidence**:

| Category | Resource Type | Count | Status |
|----------|--------------|-------|--------|
| **Other** | LogUsage | 114 | ❌ No metrics configured |
| **Other** | AzurePublicIPStandard | 16 | ❌ No metrics configured |
| **Other** | AzurePublicIP | 4 | ❌ No metrics configured |
| **Other** | AzureFileStorage | 41 | ❓ All unknown |
| **Other** | AzureBackupProtectedItemHealth | 10 | ❌ No metrics configured |
| **Other** | AzureIntegrationAuthentication | 2 | ❌ No metrics configured |
| **Other** | Metadata Only | 169 | ❓ Unmatched devices |
| **Network** | Azure Network | 35 | ❓ Unknown |

**Total "Other" Category**: 356 resources with no/limited metrics

**Missing PaaS Coverage** (not in system at all):
- Azure SQL Database
- Azure Cosmos DB
- Azure Redis Cache
- Azure Service Bus
- Azure Event Hubs
- Azure Functions
- Azure Logic Apps
- Azure Key Vault
- Azure Container Instances
- Azure Kubernetes Service (AKS)
- Azure API Management
- Azure Front Door
- Azure CDN

**Root Cause**:
1. `LMResourceTypes` table only has ~22 resource types defined
2. Many don't have `HasPerformanceMetrics = true`
3. `LMMetricMappings` needs entries for each metric per resource type
4. LogicMonitor may not have datasources for some Azure services

**Potential Fix**:
1. Use Performance Admin > Bulk Discovery to find available datasources
2. Add metric mappings for high-value PaaS services
3. Consider marking some types as "not applicable" for metrics

---

### Issue #16: Right-Sizing Recommendations Limited Scope
**Status**: OPEN - ENHANCEMENT
**Reported**: 2026-01-20

**Problem**: Right-sizing recommendations only consider CPU, memory, and disk metrics. Recommendations should be based on all relevant metrics for each resource type.

**Expected Behavior**:
- Recommendations should include all applicable metrics for the resource type
- Azure VMs: CPU, memory, disk, network I/O, IOPS
- Azure SQL: DTU, storage, query performance
- Azure App Service: requests, response time, connections
- Storage accounts: transactions, latency, capacity
- etc.

**Implementation Notes**:
- Review current recommendation logic to identify which metrics are considered
- Expand metric coverage based on resource type
- Consider cost optimization opportunities beyond compute resources

---

## 🟡 PENDING DEPLOYMENT

*No pending deployments*

---

### Issue #18: Memory Showing "-" on Some Servers
**Status**: ✅ FIXED - VERIFIED WORKING
**Reported**: 2026-01-21
**Updated**: 2026-01-21

**Problem**: Memory showing "-" on Device 535 and others despite having memory data available.

**Root Causes Found & Fixed**:

1. **Free > Total impossible** - Device 535 showed Free=1,775,596, Total=1,397,320
   - This is mathematically impossible, indicates wrong datapoint indices

2. **Limited percentage datapoints checked** - Only tried `MemoryUtilizationPercent`
   - Device 535 had `PercentVirtualMemoryInUse` available but not checked

**Fixes Applied** (in `LMPerformanceV2Functions.cs`):

| Fix | Description |
|-----|-------------|
| ✅ Multiple % datapoints | Try: MemoryUtilizationPercent, PercentMemoryUsed, PercentVirtualMemoryInUse, UsedMemoryPercent |
| ✅ Auto-swap Free/Total | If Free > Total, swap values (handles wrong indices) |
| ✅ Better logging | Log which datapoint was used |

**Commit**: `6e4f88f`

**Verified Working**: Memory now displays correctly using available percentage datapoints

---

## ✅ COMPLETED ISSUES

| Issue | Fixed | Solution |
|-------|-------|----------|
| Device Modal "No metrics found" | 2026-01-20 | Changed frontend to V2 endpoint |
| CPU showing millions % | 2026-01-20 | Percentage validation (reject >100) |
| Memory showing "-" | 2026-01-21 | Multiple % datapoints + auto-swap Free/Total |
| Disk showing 100% | 2026-01-20 | Unit conversion (bytes to GB) |
| Only one disk shown | 2026-01-20 | FetchAllDiskMetricsAsync processes all |
| Individual disks in UI | 2026-01-20 | Added disk cards ✅ Deployed |
| Disk negative percentages | 2026-01-21 | Unit detection + FreeSpace % detection |
| Missing C: drive | 2026-01-21 | Check ALL disk datasources |

---

## 📋 CURRENT STATUS

**Working** ✅:
| Resource Type | Count | CPU | Memory | Disk |
|--------------|-------|-----|--------|------|
| Windows Server | 4 | ✅ | ✅ | ✅ (all drives incl. C:) |
| Azure App Service | 35 | ✅ | - | - |
| Azure Managed Disk | 27 | - | - | ✅ (16) |
| Azure Storage Account | 82 | - | - | ✅ (16) |

**Not Working** ❌:
| Resource Type | Count | Issue |
|--------------|-------|-------|
| Azure Virtual Machine | 37 | All failing - metrics outside range |
| LogUsage | 114 | No metrics configured |
| Metadata Only | 169 | Unmatched resource types |
| Various "Other" | 356 | No metrics configured |

---

## 📝 Today's Fixes (2026-01-21)

| Commit | Fix |
|--------|-----|
| `b674ed8` | Disk: Initial unit mismatch fix (Capacity bytes, FreeSpace GB) |
| `95970a3` | Disk: Fetch from ALL matching datasources (finds C: drive) |
| `9afce4f` | Disk: Detect when FreeSpace is already a percentage |
| `6e4f88f` | Memory: Multiple % datapoints + auto-swap Free/Total |
| `a2cd099` | Browse Groups: Backend endpoint for customer subgroups |
| `6e10d85` | Browse Groups: UI in LM Config tab |

---

## 🗺️ REMEDIATION ROADMAP

### Phase 1: Quick Wins (1-2 days)
*Low effort, high impact fixes*

| Priority | Issue | Category | Effort | Impact |
|----------|-------|----------|--------|--------|
| 🔴 High | #11 - Historical Sync 9 Days | Data Quality | Low | Medium |
| ✅ Done | #8 - Browse Group Button | UI/UX | Low | Medium |
| 🟡 Medium | #10 - Rate Limiting (needs testing) | Reliability | Done | High |

---

### Phase 2: Metric Coverage (3-5 days)
*Expand monitoring coverage for Azure resources*

| Priority | Issue | Category | Effort | Impact |
|----------|-------|----------|--------|--------|
| 🔴 High | #5 - Azure VM Metrics Failing | Metrics | Medium | High |
| 🔴 High | #5 - Azure Storage Metrics | Metrics | Medium | Medium |
| 🔴 High | #5 - Azure Disk Metrics | Metrics | Medium | Medium |
| 🟡 Medium | #5 - Azure Network Metrics | Metrics | Medium | Low |

**Related Issues**: #5, #6 (grouped as "Azure PaaS Metrics")

**Approach**:
1. Use Performance Admin > Bulk Discovery to find available LM datasources
2. Map correct datapoints for each resource type
3. Handle non-percentage metrics (latency in ms, IOPS counts, etc.)
4. Add `UnitType` to `LMMetricMappings` table

---

### Phase 3: Data Completeness (1 week)
*Ensure all customers have complete data*

| Priority | Issue | Category | Effort | Impact |
|----------|-------|----------|--------|--------|
| 🟡 Medium | #9 - FinOps Missing Subscriptions | FinOps | Medium | High |
| 🟡 Medium | #6 - Limited PaaS Coverage | Metrics | High | Medium |

**Related Issues**: #6, #9 (grouped as "Data Coverage")

---

### Phase 4: Scoring & Recommendations (1-2 weeks)
*Improve assessment accuracy and recommendations*

| Priority | Issue | Category | Effort | Impact |
|----------|-------|----------|--------|--------|
| 🟡 Medium | #17 - Scoring Too Aggressive | Assessment | Medium | High |
| 🟢 Low | #16 - Right-Sizing Limited | Recommendations | High | Medium |

**Related Issues**: #16, #17 (grouped as "Assessment Quality")

**Approach**:
1. Review scoring algorithm weights
2. Add positive contribution for passing checks
3. Expand right-sizing to include all relevant metrics per resource type

---

### Grouped Issue Summary

| Group | Issues | Status |
|-------|--------|--------|
| **Disk Metrics** | #12 | ✅ FIXED |
| **Memory Metrics** | #18 | ✅ FIXED |
| **Historical Data** | #3, #11, #13 | ⚠️ Partially Fixed |
| **Azure PaaS Metrics** | #5, #6 | 🔴 Open |
| **Data Coverage** | #6, #9 | 🔴 Open |
| **Assessment Quality** | #16, #17 | 🔴 Open |
| **UI/UX** | #7, #8, #14, #15 | ✅ All Fixed |
| **Reliability** | #10, #13 | ✅ Fixed (testing) |

---

*Last Updated: 2026-01-21 11:30 UTC*
