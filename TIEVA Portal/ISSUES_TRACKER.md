# TIEVA Portal - Active Issues Tracker

> **IMPORTANT**: Only remove items when user confirms fix is working in production.

---

## 🔴 OPEN ISSUES

### Issue #3: 90-Day History Not Available
**Status**: OPEN (not started)  
**Reported**: 2026-01-20  

**Problem**: Modal shows "No 90-day history data available"

**Investigation Needed**:
- Check if `/v2/performance/customers/{id}/devices/{deviceId}/history` endpoint returns data
- Verify `LMDeviceMetricHistory` table has data
- May need to run history sync first

---

### Issue #5: Azure Resource Type Metric Mappings Need Review
**Status**: OPEN - LOW PRIORITY  
**Reported**: 2026-01-20  

**Problem**: Many Azure resource types have metric mappings that don't match available datapoints or return non-percentage values.

**Summary from Logs**:

| Resource Type | Issue Count | Primary Problem |
|--------------|-------------|-----------------|
| **AzureStorage** | ~15 devices | Latency values are milliseconds (9-290ms), not percentages; Capacity/Availability datapoints outside column range |
| **AzureDisk** | ~8 devices | IOPS% and Bandwidth% datapoints don't exist; only raw byte/operation counts available |
| **AzureVM** | ~3 devices | NetworkIn/DiskOps are raw counters; PercentageDisk doesn't exist |
| **AzureNetwork** | ~6 devices | PingMesh latency is milliseconds; some devices have NetworkInterface not VirtualNetworks datasource |

**Detailed Findings**:

1. **AzureStorage (Microsoft_Azure_StorageAccount)**
   - SuccessE2ELatency: Returns 9-290ms (not percentage)
   - SuccessServerLatency: Returns ms values
   - Capacity: Looking for `UsedCapacity`/`BlobCapacity` but not in available columns
   - Availability: Datapoint exists but outside valid column range
   - **Available datapoints that work**: `Availability_raw` (may be 0-100%)

2. **AzureDisk (Microsoft_Azure_Disk)**
   - Looking for: `DiskIOPSConsumedPercentage`, `DiskBandwidthConsumedPercentage`
   - Actually available: `DiskReadBytesSec`, `DiskWriteBytesSec`, `DiskReadOperationsSec`, `DiskOnDemandBurstOperations`
   - **Note**: Percentage datapoints may require Premium disks or specific Azure metrics

3. **AzureVM (Microsoft_Azure_VMs)**
   - NetworkIn/Out: Raw byte counts (millions)
   - VmAvailabilityMetric: Raw counter (billions)
   - DiskReadOperations: Raw counter
   - AvailableMemoryPercentage: Exists but outside column range
   - **Working**: `PercentageCPU` (within 0-100 range)

4. **AzureNetwork (Microsoft_Azure_VirtualNetworks)**
   - PingMeshAverageRoundtripMs: Milliseconds, not percentage
   - Some devices have `Microsoft_Azure_NetworkInterface` instead of expected datasource

**Root Causes**:
1. Metric mappings assume percentage values but LM returns raw counters/ms
2. Some datapoints exist in datasource definition but not populated with data
3. Different Azure resource configurations have different available datasources

**Potential Fixes** (Future Sprint):
1. Add `UnitType` column to `LMMetricMappings` (Percentage, Milliseconds, Bytes, Count)
2. Remove percentage validation for non-percentage metrics
3. Add alternative datasource patterns (e.g., `Microsoft_Azure_BlobStorage` for storage)
4. Consider using `Availability_raw` for storage availability

**Note**: Windows Server metrics (CPU, Memory, Disk) are working correctly. This only affects Azure-native PaaS resource types.

---

## 🟡 PENDING DEPLOYMENT

### Frontend: Individual Disks in Modal
**Status**: CODE READY - NEEDS GIT PUSH  

**File**: `portal/index.html`

**Change**: Added "💾 Individual Disks" section showing Disk (C:), Disk (D:), etc.

**Deploy**:
```bash
cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal"
git add -A && git commit -m "Add individual disk metrics to device modal" && git push
```

---

## ✅ COMPLETED ISSUES

| Issue | Fixed | Solution |
|-------|-------|----------|
| Device Modal "No metrics found" | 2026-01-20 | Changed frontend to V2 endpoint |
| CPU showing millions % | 2026-01-20 | Percentage validation (reject >100) |
| Memory showing "-" | 2026-01-20 | Try MemoryUtilizationPercent first, then CALC:MEMORY |
| Disk showing 100% | 2026-01-20 | Unit conversion (bytes to GB when mismatch detected) |
| Only one disk shown | 2026-01-20 | FetchAllDiskMetricsAsync processes all instances |
| Individual disks in UI | 2026-01-20 | Added disk cards to modal (pending git push) |

---

## 📋 CURRENT STATUS

**Working** ✅:
| Resource Type | CPU | Memory | Disk |
|--------------|-----|--------|------|
| WindowsServer | ✅ | ✅ | ✅ (all drives) |
| LinuxServer | ✅ | ✅ | ✅ |

**Not Working** ⚠️:
| Resource Type | Issue |
|--------------|-------|
| AzureStorage | Latency/Capacity metrics fail percentage validation |
| AzureDisk | IOPS%/Bandwidth% datapoints don't exist |
| AzureVM | Some metrics outside 0-100 range |
| AzureNetwork | Latency in ms, datasource mismatch |

---

*Last Updated: 2026-01-20 16:30 UTC*
