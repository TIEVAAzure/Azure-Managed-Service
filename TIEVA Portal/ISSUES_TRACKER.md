# TIEVA Portal - Active Issues Tracker

> **IMPORTANT**: Only remove items when user confirms fix is working in production.

---

## 🔴 OPEN ISSUES

### Issue #3: Move 90-Day Performance to Customer Level
**Status**: ✅ CHANGES READY - NEEDS TESTING  
**Reported**: 2026-01-20  
**Updated**: 2026-01-20 18:30 UTC  

**Problem**: 90-day history sync was in Performance Admin (global), but should be at **Customer** level

**Root Cause Found**:
1. Backend APIs were already at customer level but frontend had field name mismatches
2. Orphaned History Sync tab in Performance Admin was confusing

**Changes Made** (2026-01-20 18:30 UTC):
1. ✅ Fixed field name mappings in `syncPerformanceV2()` function:
   - `devicesQueued` → `totalDevices`
   - `devicesProcessed` → `progress`
   - `devicesTotal` → `totalDevices`
   - `devicesWithData` → `devicesWithHistory`
2. ✅ Removed orphaned History Sync tab content from Performance Admin
3. ✅ Removed orphaned `showPerfAdminTab` references to history-sync
4. ✅ Removed orphaned functions: `loadHistorySyncTab`, `startHistorySync`, `refreshHistorySyncStatus`, etc.

**Testing Required**:
- ⏳ Deploy frontend and run "Sync All Data" from Customer > Monitoring > Performance
- ⏳ Verify `LMDeviceMetricHistory` table has data populated
- ⏳ Verify 90-day charts display data in device modal

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
**Status**: OPEN  
**Reported**: 2026-01-20  

**Problem**: Performance sync fails with HTTP 429 (Too Many Requests) even after detecting rate limit and waiting.

**Error Log**:
```
2026-01-20T16:09:58 [Warning] Rate limit approaching, waiting 59.3s
2026-01-20T16:09:58 [Error]   LM API error: TooManyRequests - HTTP Status 429
```

**Root Cause Analysis** (in `LogicMonitorService.cs`):

| Issue | Description | Severity |
|-------|-------------|----------|
| No thread safety | Static `_remainingRequests` not locked - parallel requests bypass check | High |
| No retry on 429 | Request fails immediately instead of retrying after wait | High |
| Batch parallelism | `GetAllDevicesInGroupAsync` uses `Task.WhenAll` for 5 parallel requests | Medium |
| No semaphore | No limit on concurrent API calls | Medium |

**Evidence**: Warning and Error logged at same timestamp (16:09:58) - concurrent requests bypassed the wait.

**Current Code** (Lines 23-24, 64-70):
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
