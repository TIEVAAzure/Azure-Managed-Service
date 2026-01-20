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

**Testing**:
- Deploy and start a new history sync
- If it gets stuck, UI should show "Stuck" status
- Click "Force Restart" to restart the sync

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
**Status**: OPEN
**Reported**: 2026-01-20

**Problem**: Many disks show negative percentage calculations and get filtered out as invalid.

**Evidence from logs**:
```
Device 527 Disk C:: RAW VALUES - Capacity[0]=14411145216, FreeSpace[0]=79.1944
Device 527 Disk C:: Unit mismatch detected - converting Capacity from bytes to GB
Device 527 Disk C:: CALC - 100 - (79.1944/13.4214...) = -490.1%
Device 527 Disk C:: Calculated 0 valid percentages from 500 data points

Device 87 Disk C:: RAW VALUES - Capacity[0]=27287760896, FreeSpace[0]=74.4608
Device 87 Disk C:: Unit mismatch detected - converting Capacity from bytes to GB
Device 87 Disk C:: CALC - 100 - (74.4608/25.413707733154297*100) = -193.0%
Device 87 Disk C:: Calculated 0 valid percentages from 500 data points
```

**Root Cause**: FreeSpace is already in GB (79.19 GB, 74.46 GB) while Capacity is in bytes (14.4B, 27.2B). The code converts Capacity to GB but FreeSpace is already in GB, causing calculation: `100 - (79GB / 13.4GB * 100) = -490%`

**Affected Devices**: Multiple Windows servers showing "No disk data found" including devices 87, 527

**Fix Needed**:
- Detect when FreeSpace > Capacity (after conversion) - indicates FreeSpace already in GB
- Don't convert FreeSpace if it's already in the correct unit
- Or check the actual LM datapoint unit metadata

---

### Issue #7: Admin Settings Location Wrong
**Status**: OPEN  
**Reported**: 2026-01-20  

**Problem**: Admin Settings tab is inside Customer > Monitoring > Performance, but it covers configuration for Overview, Alerts, AND Devices - not just Performance.

**Current Location**: Customer > Monitoring > Performance > Admin Settings

**Should Be**: Customer > Monitoring > Admin Settings (at the Monitoring sub-tab level)

**Action Required**:
- Move Admin Settings up one level in the tab hierarchy
- Should be a sibling to Overview, Alerts, Devices, Performance (not a child of Performance)

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
**Status**: OPEN - ENHANCEMENT
**Reported**: 2026-01-20

**Problem**: When clicking on a finding or recommendation in the Overview section, users want to see more detailed information.

**Expected Behavior**:
- Clicking on a finding should show detailed breakdown
- Include root cause, affected resources, and recommended actions
- May link to relevant documentation or remediation steps

**Implementation Notes**:
- Need to determine what data is available for each finding type
- Consider modal or slide-out panel for details
- Link findings to relevant monitoring data where applicable

---

### Issue #15: Monitoring Click-Through for Alerts/Devices
**Status**: OPEN - ENHANCEMENT
**Reported**: 2026-01-20

**Problem**: In Monitoring > Overview, Alerts, and Devices tabs, users want to click on alerts or devices to get more information.

**Expected Behavior**:
- **Overview**: Click items for drill-down details
- **Alerts**: Click alert to see full alert details, history, affected device
- **Devices**: Click device to see device details, metrics, alerts for that device

**Implementation Notes**:
- Alerts may need: severity, timestamp, acknowledgement status, notes, linked device
- Devices may need: link to existing device modal with performance metrics
- Consider consistent UX pattern across all monitoring sub-tabs

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

*Last Updated: 2026-01-20 18:30 UTC*
