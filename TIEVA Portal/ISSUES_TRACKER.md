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
**Status**: ✅ FIXED
**Reported**: 2026-01-20
**Updated**: 2026-01-21

**Problem**: Many disks show negative percentage calculations and get filtered out as invalid.

**Evidence from logs**:
```
Device 527 Disk C:: RAW VALUES - Capacity[0]=14411145216, FreeSpace[0]=79.1944
Device 527 Disk C:: Unit mismatch detected - converting Capacity from bytes to GB
Device 527 Disk C:: CALC - 100 - (79.1944/13.4214...) = -490.1%
Device 527 Disk C:: Calculated 0 valid percentages from 500 data points
```

**Root Cause**: FreeSpace is already in GB (79.19 GB) while Capacity is in bytes (14.4B). The code was converting Capacity to GB but assuming FreeSpace was also in bytes, causing: `100 - (79GB / 13.4GB * 100) = -490%`

**Fix Applied** (in `LMPerformanceV2Functions.cs`):
- Added smart unit detection for three cases:
  1. **Both in bytes** → Convert both to GB
  2. **Capacity in bytes, FreeSpace in GB** → Convert only Capacity
  3. **Both in same unit** → No conversion needed
- Logic: If `Capacity > 1 billion` and `FreeSpace < 1 billion`, FreeSpace is already in GB
- Added detailed logging for each conversion scenario

**Code Change**:
```csharp
// Before: Only converted Capacity, assumed FreeSpace needed same treatment
var needsConversion = sampleCap > 1_000_000_000 && sampleFree < 100_000;

// After: Detect each value's unit independently
if (sampleCap > 1_000_000_000) // Capacity is in bytes
{
    if (sampleFree > 1_000_000_000) // Both in bytes
    {
        convertCapacityToGB = true;
        convertFreeToGB = true;
    }
    else // Capacity in bytes, FreeSpace already in GB
    {
        convertCapacityToGB = true;
        convertFreeToGB = false;
    }
}
```

**Expected Result**: Devices 87, 527 and others should now show valid disk percentages instead of "No disk data found"

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
**Status**: OPEN  
**Reported**: 2026-01-20  

**Problem**: There was a "Browse Group" button to select which LogicMonitor device group to sync from. This appears to be missing.

**Expected Behavior**:
- User should be able to browse/select LogicMonitor groups
- Sets which group to pull devices from for this customer

**Investigation Needed**:
- Check if this was removed or never implemented
- Determine where it should appear (Customer settings? Monitoring tab?)

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

## ✅ COMPLETED ISSUES

| Issue | Fixed | Solution |
|-------|-------|----------|
| Device Modal "No metrics found" | 2026-01-20 | Changed frontend to V2 endpoint |
| CPU showing millions % | 2026-01-20 | Percentage validation (reject >100) |
| Memory showing "-" | 2026-01-20 | MemoryUtilizationPercent + CALC fallback |
| Disk showing 100% | 2026-01-20 | Unit conversion (bytes to GB) |
| Only one disk shown | 2026-01-20 | FetchAllDiskMetricsAsync processes all |
| Individual disks in UI | 2026-01-20 | Added disk cards ✅ Deployed |

---

## 📋 CURRENT STATUS

**Working** ✅:
| Resource Type | Count | CPU | Memory | Disk |
|--------------|-------|-----|--------|------|
| Windows Server | 4 | ✅ | ✅ | ✅ |
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

*Last Updated: 2026-01-21 22:00 UTC*
