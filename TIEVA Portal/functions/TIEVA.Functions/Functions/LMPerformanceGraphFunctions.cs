using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Queues;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

/// <summary>
/// Performance Graphs & SKU-Based Recommendations
/// Provides 90-day historical data for graphs and intelligent SKU sizing recommendations
/// </summary>
public class LMPerformanceGraphFunctions
{
    private readonly TievaDbContext _db;
    private readonly ILogger<LMPerformanceGraphFunctions> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _keyVaultUrl;

    public LMPerformanceGraphFunctions(TievaDbContext db, ILogger<LMPerformanceGraphFunctions> logger, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _db = db;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    /// <summary>
    /// Get 90-day performance history for a device (for graphs)
    /// </summary>
    [Function("GetDevicePerformanceHistory")]
    public async Task<HttpResponseData> GetDeviceHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/devices/{deviceId}/history")] 
        HttpRequestData req, string customerId, string deviceId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        if (!int.TryParse(deviceId, out var devId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid device ID");
            return badRequest;
        }

        // Parse optional days parameter (default 90)
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var days = int.TryParse(query["days"], out var d) ? Math.Min(d, 90) : 90;
        var cutoffDate = DateTime.UtcNow.Date.AddDays(-days);

        // Get device info
        var device = await _db.LMDeviceMetricsV2
            .FirstOrDefaultAsync(m => m.CustomerId == custId && m.DeviceId == devId);

        if (device == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Device not found");
            return notFound;
        }

        // Get historical data
        var history = await _db.LMDeviceMetricHistory
            .Where(h => h.CustomerId == custId && h.DeviceId == devId && h.MetricDate >= cutoffDate)
            .OrderBy(h => h.MetricDate)
            .ToListAsync();

        // Group by metric name
        var cpuHistory = history.Where(h => h.MetricName == "CPU").ToList();
        var memHistory = history.Where(h => h.MetricName == "Memory").ToList();
        var diskHistory = history.Where(h => h.MetricName == "Disk").ToList();

        // Get 90-day aggregates from device (if available)
        var metrics90Day = device.GetMetrics90Day();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            deviceId = device.DeviceId,
            deviceName = device.DeviceName,
            resourceType = device.ResourceTypeCode,
            currentSku = device.CurrentSku,
            skuFamily = device.SkuFamily,
            recommendedSku = device.RecommendedSku,
            skuRecommendationReason = device.SkuRecommendationReason,
            potentialMonthlySavings = device.PotentialMonthlySavings,
            
            // 90-day aggregates for summary
            summary = new
            {
                cpu = new { avg = metrics90Day?.CpuAvg, max = metrics90Day?.CpuMax, p95 = metrics90Day?.CpuP95 },
                memory = new { avg = metrics90Day?.MemAvg, max = metrics90Day?.MemMax, p95 = metrics90Day?.MemP95 },
                disk = new { avg = metrics90Day?.DiskAvg, max = metrics90Day?.DiskMax, p95 = metrics90Day?.DiskP95 },
                dataPoints = metrics90Day?.DataPoints ?? 0,
                periodStart = metrics90Day?.PeriodStart,
                periodEnd = metrics90Day?.PeriodEnd
            },
            
            // Daily data for graphs
            history = new
            {
                cpu = cpuHistory.Select(h => new { date = h.MetricDate.ToString("yyyy-MM-dd"), avg = h.AvgValue, max = h.MaxValue, min = h.MinValue, p95 = h.P95Value }),
                memory = memHistory.Select(h => new { date = h.MetricDate.ToString("yyyy-MM-dd"), avg = h.AvgValue, max = h.MaxValue, min = h.MinValue, p95 = h.P95Value }),
                disk = diskHistory.Select(h => new { date = h.MetricDate.ToString("yyyy-MM-dd"), avg = h.AvgValue, max = h.MaxValue, min = h.MinValue, p95 = h.P95Value })
            },
            
            daysRequested = days,
            dataPointsReturned = history.Count
        });
        return response;
    }

    /// <summary>
    /// Get all SKU families for admin UI
    /// </summary>
    [Function("GetSkuFamilies")]
    public async Task<HttpResponseData> GetSkuFamilies(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/sku-families")] 
        HttpRequestData req)
    {
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var resourceType = query["resourceType"];

        var skusQuery = _db.AzureSkuFamilies.Where(s => s.IsActive);
        
        if (!string.IsNullOrEmpty(resourceType))
        {
            skusQuery = skusQuery.Where(s => s.ResourceType == resourceType);
        }

        var skus = await skusQuery
            .OrderBy(s => s.ResourceType)
            .ThenBy(s => s.SkuFamily)
            .ThenBy(s => s.SizeOrder)
            .ToListAsync();

        // Group by resource type and family
        var grouped = skus
            .GroupBy(s => s.ResourceType)
            .Select(rt => new
            {
                resourceType = rt.Key,
                families = rt.GroupBy(s => s.SkuFamily).Select(f => new
                {
                    family = f.Key,
                    skus = f.OrderBy(s => s.SizeOrder).Select(s => new
                    {
                        id = s.Id,
                        skuName = s.SkuName,
                        displayName = s.DisplayName,
                        sizeOrder = s.SizeOrder,
                        vCPUs = s.vCPUs,
                        memoryGB = s.MemoryGB,
                        maxIOPS = s.MaxIOPS,
                        maxThroughputMBps = s.MaxThroughputMBps,
                        monthlyCostEstimate = s.MonthlyCostEstimate,
                        notes = s.Notes
                    })
                })
            });

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            totalSkus = skus.Count,
            resourceTypes = grouped
        });
        return response;
    }

    /// <summary>
    /// Get SKU recommendation for a specific device
    /// Analyzes 90-day performance data and suggests appropriate SKU
    /// </summary>
    [Function("GetSkuRecommendation")]
    public async Task<HttpResponseData> GetSkuRecommendation(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/devices/{deviceId}/sku-recommendation")] 
        HttpRequestData req, string customerId, string deviceId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        if (!int.TryParse(deviceId, out var devId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid device ID");
            return badRequest;
        }

        var device = await _db.LMDeviceMetricsV2
            .FirstOrDefaultAsync(m => m.CustomerId == custId && m.DeviceId == devId);

        if (device == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Device not found");
            return notFound;
        }

        // Get 90-day aggregates
        var metrics90Day = device.GetMetrics90Day();
        
        // Get current SKU info
        AzureSkuFamily? currentSkuInfo = null;
        AzureSkuFamily? recommendedSkuInfo = null;
        string recommendationReason = "Unknown";
        decimal? potentialSavings = null;

        if (!string.IsNullOrEmpty(device.CurrentSku))
        {
            currentSkuInfo = await _db.AzureSkuFamilies
                .FirstOrDefaultAsync(s => s.SkuName == device.CurrentSku && s.IsActive);

            if (currentSkuInfo != null && metrics90Day != null)
            {
                var recommendation = CalculateSkuRecommendation(currentSkuInfo, metrics90Day);
                recommendedSkuInfo = recommendation.RecommendedSku;
                recommendationReason = recommendation.Reason;
                potentialSavings = recommendation.PotentialSavings;
            }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            deviceId = device.DeviceId,
            deviceName = device.DeviceName,
            resourceType = device.ResourceTypeCode,
            
            currentSku = new
            {
                skuName = currentSkuInfo?.SkuName ?? device.CurrentSku,
                displayName = currentSkuInfo?.DisplayName,
                skuFamily = currentSkuInfo?.SkuFamily ?? device.SkuFamily,
                sizeOrder = currentSkuInfo?.SizeOrder,
                vCPUs = currentSkuInfo?.vCPUs,
                memoryGB = currentSkuInfo?.MemoryGB,
                monthlyCost = currentSkuInfo?.MonthlyCostEstimate
            },
            
            metrics90Day = new
            {
                cpuAvg = metrics90Day?.CpuAvg,
                cpuMax = metrics90Day?.CpuMax,
                cpuP95 = metrics90Day?.CpuP95,
                memAvg = metrics90Day?.MemAvg,
                memMax = metrics90Day?.MemMax,
                memP95 = metrics90Day?.MemP95,
                dataPoints = metrics90Day?.DataPoints ?? 0
            },
            
            recommendation = new
            {
                action = recommendedSkuInfo?.SkuName == currentSkuInfo?.SkuName ? "KeepCurrent" 
                       : recommendedSkuInfo == null ? "Unknown"
                       : recommendedSkuInfo.SizeOrder < currentSkuInfo?.SizeOrder ? "Downsize" 
                       : "Upsize",
                skuName = recommendedSkuInfo?.SkuName,
                displayName = recommendedSkuInfo?.DisplayName,
                reason = recommendationReason,
                potentialMonthlySavings = potentialSavings,
                vCPUs = recommendedSkuInfo?.vCPUs,
                memoryGB = recommendedSkuInfo?.MemoryGB,
                monthlyCost = recommendedSkuInfo?.MonthlyCostEstimate
            },
            
            isAtSmallestSku = currentSkuInfo?.SizeOrder == 1,
            isAtLargestSku = await IsLargestInFamily(currentSkuInfo)
        });
        return response;
    }

    /// <summary>
    /// Calculate SKU recommendation based on 90-day metrics
    /// </summary>
    private (AzureSkuFamily? RecommendedSku, string Reason, decimal? PotentialSavings) CalculateSkuRecommendation(
        AzureSkuFamily currentSku, Metrics90Day metrics)
    {
        // Get all SKUs in the same family
        var familySkus = _db.AzureSkuFamilies
            .Where(s => s.ResourceType == currentSku.ResourceType && s.SkuFamily == currentSku.SkuFamily && s.IsActive)
            .OrderBy(s => s.SizeOrder)
            .ToList();

        if (familySkus.Count <= 1)
        {
            return (currentSku, "Only one SKU in family - no alternative available", null);
        }

        var currentIndex = familySkus.FindIndex(s => s.Id == currentSku.Id);
        if (currentIndex < 0)
        {
            return (currentSku, "Current SKU not found in family", null);
        }

        // Thresholds for recommendations (using P95 for reliability)
        const decimal cpuOversizedThreshold = 30;  // CPU P95 < 30% = oversized
        const decimal cpuUndersizedThreshold = 85; // CPU P95 > 85% = undersized
        const decimal memOversizedThreshold = 40;  // Memory P95 < 40% = oversized
        const decimal memUndersizedThreshold = 90; // Memory P95 > 90% = undersized

        bool isOversized = false;
        bool isUndersized = false;
        var reasons = new List<string>();

        // Check CPU
        if (metrics.CpuP95.HasValue)
        {
            if (metrics.CpuP95 < cpuOversizedThreshold)
            {
                isOversized = true;
                reasons.Add($"CPU P95 is {metrics.CpuP95:F1}% (below {cpuOversizedThreshold}% threshold)");
            }
            else if (metrics.CpuP95 > cpuUndersizedThreshold)
            {
                isUndersized = true;
                reasons.Add($"CPU P95 is {metrics.CpuP95:F1}% (above {cpuUndersizedThreshold}% threshold)");
            }
        }

        // Check Memory
        if (metrics.MemP95.HasValue)
        {
            if (metrics.MemP95 < memOversizedThreshold)
            {
                isOversized = isOversized && true; // Both must be oversized
                reasons.Add($"Memory P95 is {metrics.MemP95:F1}% (below {memOversizedThreshold}% threshold)");
            }
            else if (metrics.MemP95 > memUndersizedThreshold)
            {
                isUndersized = true; // Either being undersized triggers
                reasons.Add($"Memory P95 is {metrics.MemP95:F1}% (above {memUndersizedThreshold}% threshold)");
            }
        }

        // Determine recommendation
        if (isUndersized)
        {
            // Need to upsize - but check if already at largest
            if (currentIndex >= familySkus.Count - 1)
            {
                return (currentSku, "Already at largest SKU in family - consider different SKU family. " + string.Join("; ", reasons), null);
            }
            
            var nextSku = familySkus[currentIndex + 1];
            var costIncrease = nextSku.MonthlyCostEstimate - currentSku.MonthlyCostEstimate;
            return (nextSku, "Undersized - recommend upsizing. " + string.Join("; ", reasons), -costIncrease);
        }
        
        if (isOversized)
        {
            // Can downsize - but check if already at smallest
            if (currentIndex <= 0)
            {
                return (currentSku, "Already at smallest SKU in family - consider B-series for burstable workloads. " + string.Join("; ", reasons), null);
            }
            
            var prevSku = familySkus[currentIndex - 1];
            var savings = currentSku.MonthlyCostEstimate - prevSku.MonthlyCostEstimate;
            return (prevSku, "Oversized - recommend downsizing. " + string.Join("; ", reasons), savings);
        }

        // Right-sized
        return (currentSku, "Right-sized based on 90-day P95 metrics", null);
    }

    private async Task<bool> IsLargestInFamily(AzureSkuFamily? sku)
    {
        if (sku == null) return false;
        
        var maxOrder = await _db.AzureSkuFamilies
            .Where(s => s.ResourceType == sku.ResourceType && s.SkuFamily == sku.SkuFamily && s.IsActive)
            .MaxAsync(s => (int?)s.SizeOrder) ?? 0;
        
        return sku.SizeOrder >= maxOrder;
    }

    /// <summary>
    /// Sync historical performance data for a device (fetches 90 days)
    /// </summary>
    [Function("SyncDeviceHistory")]
    public async Task<HttpResponseData> SyncDeviceHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/customers/{customerId}/devices/{deviceId}/sync-history")] 
        HttpRequestData req, string customerId, string deviceId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        if (!int.TryParse(deviceId, out var devId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid device ID");
            return badRequest;
        }

        var device = await _db.LMDeviceMetricsV2
            .Include(m => m.ResourceType)
            .ThenInclude(rt => rt!.MetricMappings)
            .FirstOrDefaultAsync(m => m.CustomerId == custId && m.DeviceId == devId);

        if (device == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Device not found");
            return notFound;
        }

        var lmService = await GetLogicMonitorServiceAsync(custId);
        if (lmService == null)
        {
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("LogicMonitor service unavailable");
            return err;
        }

        try
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-90);
            var daysProcessed = 0;
            var recordsCreated = 0;
            var debugInfo = new List<object>(); // Debug tracking

            // Get datasources for this device
            var datasources = await lmService.GetDeviceDatasourcesAsync(devId);
            if (datasources?.Items == null || !datasources.Items.Any())
            {
                var noData = req.CreateResponse(HttpStatusCode.OK);
                await noData.WriteAsJsonAsync(new { message = "No datasources found for device", daysProcessed = 0 });
                return noData;
            }

            debugInfo.Add(new { step = "datasources", count = datasources.Items.Count, names = datasources.Items.Select(d => d.DataSourceName).ToList() });

            // For each metric type (CPU, Memory, Disk), fetch 90 days of data
            var metricMappings = device.ResourceType?.MetricMappings?.Where(m => m.IsActive).ToList() ?? new();

            debugInfo.Add(new { step = "mappings", resourceType = device.ResourceType?.Code, count = metricMappings.Count, mappings = metricMappings.Select(m => new { m.MetricName, dsPatterns = m.GetDatasourcePatterns(), dpPatterns = m.GetDatapointPatterns() }).ToList() });

            foreach (var mapping in metricMappings)
            {
                var dsPatterns = mapping.GetDatasourcePatterns();
                var dpPatterns = mapping.GetDatapointPatterns();

                // Find matching datasource
                var matchedDs = datasources.Items.FirstOrDefault(ds =>
                    dsPatterns.Any(p => ds.DataSourceName?.Contains(p, StringComparison.OrdinalIgnoreCase) == true));

                if (matchedDs == null)
                {
                    debugInfo.Add(new { step = "no_match", metric = mapping.MetricName, dsPatterns, availableDs = datasources.Items.Select(d => d.DataSourceName).ToList() });
                    continue;
                }

                // Fetch instances
                var instances = await lmService.GetDatasourceInstancesAsync(devId, matchedDs.Id);
                if (instances?.Items == null || !instances.Items.Any())
                {
                    debugInfo.Add(new { step = "no_instances", metric = mapping.MetricName, datasource = matchedDs.DataSourceName });
                    continue;
                }

                var instance = instances.Items.First();
                debugInfo.Add(new { step = "matched", metric = mapping.MetricName, datasource = matchedDs.DataSourceName, instanceId = instance.Id, instanceName = instance.DisplayName });

                // Fetch 90-day data in chunks (LM may have limits)
                var chunkDays = 30;
                var currentStart = start;

                while (currentStart < end)
                {
                    var chunkEnd = currentStart.AddDays(chunkDays);
                    if (chunkEnd > end) chunkEnd = end;

                    try
                    {
                        var data = await lmService.GetInstanceDataAsync(devId, matchedDs.Id, instance.Id, currentStart, chunkEnd);

                        if (data?.DataPoints == null || !data.Values.HasValue)
                        {
                            debugInfo.Add(new { step = "no_data", metric = mapping.MetricName, chunkStart = currentStart.ToString("yyyy-MM-dd"), hasDataPoints = data?.DataPoints != null, hasValues = data?.Values.HasValue });
                        }

                        if (data?.DataPoints != null && data.Values.HasValue)
                        {
                            // Check if Time is separate from Values
                            var hasTimeArray = data.Time.HasValue && data.Time.Value.ValueKind == JsonValueKind.Array;
                            var timeCount = hasTimeArray ? data.Time.Value.GetArrayLength() : 0;
                            string? firstTime = null;
                            if (hasTimeArray)
                            {
                                var firstTimeEl = data.Time.Value.EnumerateArray().FirstOrDefault();
                                firstTime = firstTimeEl.ToString();
                            }

                            debugInfo.Add(new { step = "got_data", metric = mapping.MetricName, chunkStart = currentStart.ToString("yyyy-MM-dd"), dataPoints = data.DataPoints, valuesCount = data.Values.Value.GetArrayLength(), hasTimeArray, timeCount, firstTime });

                            // Find matching datapoint index
                            // Handle CALC: patterns by mapping to actual LM datapoint names
                            var effectiveDpPatterns = dpPatterns.SelectMany(p =>
                            {
                                if (p.StartsWith("CALC:", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Map CALC patterns to actual LM datapoints
                                    return p.ToUpperInvariant() switch
                                    {
                                        "CALC:MEMORY" => new[] { "MemoryUtilizationPercent", "PercentMemoryUsed" },
                                        "CALC:DISK" => new[] { "PercentUsed", "UsedPercent" },
                                        _ => new[] { p }
                                    };
                                }
                                return new[] { p };
                            }).ToArray();

                            var matchedDp = false;
                            for (int dpIdx = 0; dpIdx < data.DataPoints.Count; dpIdx++)
                            {
                                var dpName = data.DataPoints[dpIdx];
                                if (!effectiveDpPatterns.Any(p => dpName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                                    continue;

                                matchedDp = true;

                                // Process values into daily aggregates
                                var dailyData = ProcessDailyAggregates(data, dpIdx, currentStart, chunkEnd);

                                debugInfo.Add(new { step = "processed", metric = mapping.MetricName, dpIdx, dpName, dailyCount = dailyData.Count });

                                // If no daily data, log sample of raw values for debugging
                                if (dailyData.Count == 0 && data.Values.HasValue)
                                {
                                    var sampleValues = new List<object>();
                                    var rowCount = 0;
                                    foreach (var row in data.Values.Value.EnumerateArray())
                                    {
                                        if (rowCount++ >= 3) break;
                                        if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() > dpIdx + 1)
                                        {
                                            sampleValues.Add(new {
                                                timestamp = row[0].ToString(),
                                                value = row[dpIdx + 1].ToString()
                                            });
                                        }
                                    }
                                    debugInfo.Add(new { step = "sample_values", metric = mapping.MetricName, samples = sampleValues });
                                }

                                foreach (var daily in dailyData)
                                {
                                    // Upsert into history table
                                    var existing = await _db.LMDeviceMetricHistory
                                        .FirstOrDefaultAsync(h => h.CustomerId == custId && 
                                                                   h.DeviceId == devId && 
                                                                   h.MetricName == mapping.MetricName && 
                                                                   h.MetricDate == daily.Date);

                                    if (existing == null)
                                    {
                                        _db.LMDeviceMetricHistory.Add(new LMDeviceMetricHistory
                                        {
                                            CustomerId = custId,
                                            DeviceId = devId,
                                            MetricName = mapping.MetricName,
                                            MetricDate = daily.Date,
                                            AvgValue = daily.Avg,
                                            MaxValue = daily.Max,
                                            MinValue = daily.Min,
                                            P95Value = daily.P95,
                                            SampleCount = daily.SampleCount,
                                            CreatedAt = DateTime.UtcNow
                                        });
                                        recordsCreated++;
                                    }
                                    else
                                    {
                                        existing.AvgValue = daily.Avg;
                                        existing.MaxValue = daily.Max;
                                        existing.MinValue = daily.Min;
                                        existing.P95Value = daily.P95;
                                        existing.SampleCount = daily.SampleCount;
                                    }
                                    daysProcessed++;
                                }

                                break; // Found matching datapoint, move to next mapping
                            }

                            if (!matchedDp)
                            {
                                debugInfo.Add(new { step = "no_dp_match", metric = mapping.MetricName, dpPatterns, effectiveDpPatterns, availableDps = data.DataPoints });
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        debugInfo.Add(new { step = "error", metric = mapping.MetricName, chunkStart = currentStart.ToString("yyyy-MM-dd"), error = ex.Message });
                        _logger.LogWarning(ex, "Error fetching chunk {Start} to {End}", currentStart, chunkEnd);
                    }

                    currentStart = chunkEnd;
                    await Task.Delay(200); // Rate limiting
                }
            }

            await _db.SaveChangesAsync();

            // Update 90-day aggregates on device
            await UpdateDevice90DayAggregates(device);
            
            // Detect current SKU from LogicMonitor properties
            await DetectDeviceSku(device, lmService);

            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                deviceId = devId,
                daysProcessed,
                recordsCreated,
                currentSku = device.CurrentSku,
                recommendedSku = device.RecommendedSku,
                debug = debugInfo // Include debug info in response
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing history for device {DeviceId}", devId);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Process raw LM data into daily aggregates
    /// LM returns Time[] as separate array from Values[]
    /// </summary>
    private List<(DateTime Date, decimal? Avg, decimal? Max, decimal? Min, decimal? P95, int SampleCount)> ProcessDailyAggregates(
        LMInstanceDataResponse data, int datapointIndex, DateTime start, DateTime end)
    {
        var result = new List<(DateTime Date, decimal? Avg, decimal? Max, decimal? Min, decimal? P95, int SampleCount)>();

        if (!data.Values.HasValue || data.Values.Value.ValueKind != JsonValueKind.Array)
            return result;

        // LM returns timestamps in a separate Time array, not embedded in Values
        // Time[] = [timestamp1, timestamp2, ...] (epoch milliseconds)
        // Values[] = [[dp0val, dp1val, dp2val], [dp0val, dp1val, dp2val], ...]
        var hasTimeArray = data.Time.HasValue && data.Time.Value.ValueKind == JsonValueKind.Array;
        if (!hasTimeArray)
            return result;

        // Build list of timestamps
        var timestamps = new List<DateTime>();
        foreach (var timeEl in data.Time.Value.EnumerateArray())
        {
            try
            {
                if (timeEl.ValueKind != JsonValueKind.Number)
                {
                    timestamps.Add(DateTime.MinValue);
                    continue;
                }

                var epochMs = timeEl.GetInt64();
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds(epochMs).UtcDateTime;
                timestamps.Add(timestamp);
            }
            catch
            {
                timestamps.Add(DateTime.MinValue);
            }
        }

        // Group values by date
        var dailyValues = new Dictionary<DateTime, List<decimal>>();
        var rowIndex = 0;

        foreach (var row in data.Values.Value.EnumerateArray())
        {
            try
            {
                if (rowIndex >= timestamps.Count)
                    break;

                var timestamp = timestamps[rowIndex];
                rowIndex++;

                if (timestamp == DateTime.MinValue)
                    continue;

                var date = timestamp.Date;

                // Values array: each row is [dp0, dp1, dp2, ...] - NO timestamp prefix
                if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() <= datapointIndex)
                    continue;

                var valueElement = row[datapointIndex];
                if (valueElement.ValueKind != JsonValueKind.Number)
                    continue;

                var value = valueElement.GetDecimal();
                if (value < 0 || value > 100) // Skip invalid percentages
                    continue;

                if (!dailyValues.ContainsKey(date))
                    dailyValues[date] = new List<decimal>();

                dailyValues[date].Add(value);
            }
            catch
            {
                rowIndex++;
                continue;
            }
        }

        // Calculate aggregates for each day
        foreach (var day in dailyValues.OrderBy(d => d.Key))
        {
            if (!day.Value.Any()) continue;

            var sorted = day.Value.OrderBy(v => v).ToList();
            var p95Index = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
            if (p95Index < 0) p95Index = 0;
            if (p95Index >= sorted.Count) p95Index = sorted.Count - 1;

            result.Add((
                Date: day.Key,
                Avg: (decimal)day.Value.Average(),
                Max: day.Value.Max(),
                Min: day.Value.Min(),
                P95: sorted[p95Index],
                SampleCount: day.Value.Count
            ));
        }

        return result;
    }

    /// <summary>
    /// Update 90-day aggregates on the device record
    /// </summary>
    private async Task UpdateDevice90DayAggregates(LMDeviceMetricsV2 device)
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-90);
        
        var history = await _db.LMDeviceMetricHistory
            .Where(h => h.CustomerId == device.CustomerId && h.DeviceId == device.DeviceId && h.MetricDate >= cutoff)
            .ToListAsync();

        var cpuData = history.Where(h => h.MetricName == "CPU" && h.AvgValue.HasValue).ToList();
        var memData = history.Where(h => h.MetricName == "Memory" && h.AvgValue.HasValue).ToList();
        var diskData = history.Where(h => h.MetricName == "Disk" && h.AvgValue.HasValue).ToList();

        var metrics = new Metrics90Day
        {
            CpuAvg = cpuData.Any() ? (decimal)cpuData.Average(c => (double)c.AvgValue!.Value) : null,
            CpuMax = cpuData.Any() ? cpuData.Max(c => c.MaxValue) : null,
            CpuP95 = cpuData.Any() ? CalculateP95(cpuData.Select(c => c.P95Value ?? c.AvgValue!.Value)) : null,
            MemAvg = memData.Any() ? (decimal)memData.Average(c => (double)c.AvgValue!.Value) : null,
            MemMax = memData.Any() ? memData.Max(c => c.MaxValue) : null,
            MemP95 = memData.Any() ? CalculateP95(memData.Select(c => c.P95Value ?? c.AvgValue!.Value)) : null,
            DiskAvg = diskData.Any() ? (decimal)diskData.Average(c => (double)c.AvgValue!.Value) : null,
            DiskMax = diskData.Any() ? diskData.Max(c => c.MaxValue) : null,
            DiskP95 = diskData.Any() ? CalculateP95(diskData.Select(c => c.P95Value ?? c.AvgValue!.Value)) : null,
            DataPoints = history.Count,
            PeriodStart = history.Any() ? history.Min(h => h.MetricDate) : null,
            PeriodEnd = history.Any() ? history.Max(h => h.MetricDate) : null
        };

        device.SetMetrics90Day(metrics);

        // Update recommendations based on 90-day data
        if (metrics.DataPoints > 7) // Need at least a week of data
        {
            await UpdateSkuRecommendation(device, metrics);
        }
    }

    private decimal CalculateP95(IEnumerable<decimal> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (!sorted.Any()) return 0;
        
        var index = (int)Math.Ceiling(sorted.Count * 0.95) - 1;
        if (index < 0) index = 0;
        if (index >= sorted.Count) index = sorted.Count - 1;
        
        return sorted[index];
    }

    /// <summary>
    /// Detect current SKU from LogicMonitor device properties
    /// </summary>
    private async Task DetectDeviceSku(LMDeviceMetricsV2 device, LogicMonitorService lmService)
    {
        try
        {
            var details = await lmService.GetDeviceDetailsAsync(device.DeviceId);
            if (details == null) return;

            // Azure VMs have system.azure.vmSize property
            var vmSize = details.GetProperty("system.azure.vmSize") 
                      ?? details.GetProperty("auto.azure.vmSize")
                      ?? details.GetProperty("azure.vm.size");

            if (!string.IsNullOrEmpty(vmSize))
            {
                device.CurrentSku = vmSize;
                
                // Try to determine family from SKU name
                device.SkuFamily = ExtractSkuFamily(vmSize);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect SKU for device {DeviceId}", device.DeviceId);
        }
    }

    /// <summary>
    /// Extract SKU family from SKU name (e.g., Standard_D4s_v5 -> Dsv5)
    /// </summary>
    private string? ExtractSkuFamily(string skuName)
    {
        // Common patterns:
        // Standard_D4s_v5 -> Dsv5
        // Standard_E8s_v5 -> Esv5
        // Standard_B2ms -> Bs
        // Standard_F4s_v2 -> Fsv2

        if (string.IsNullOrEmpty(skuName)) return null;

        // Remove "Standard_" prefix
        var name = skuName.Replace("Standard_", "");
        
        // Extract series letter and version
        // D4s_v5 -> D, s, v5
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^([A-Z])(\d+)([a-z]*)(_v\d+)?");
        if (match.Success)
        {
            var series = match.Groups[1].Value; // D
            var suffix = match.Groups[3].Value; // s
            var version = match.Groups[4].Value.Replace("_", ""); // v5
            
            return $"{series}{suffix}{version}"; // Dsv5
        }

        return null;
    }

    /// <summary>
    /// Update SKU recommendation on device
    /// </summary>
    private async Task UpdateSkuRecommendation(LMDeviceMetricsV2 device, Metrics90Day metrics)
    {
        if (string.IsNullOrEmpty(device.CurrentSku)) return;

        var currentSkuInfo = await _db.AzureSkuFamilies
            .FirstOrDefaultAsync(s => s.SkuName == device.CurrentSku && s.IsActive);

        if (currentSkuInfo == null) return;

        var recommendation = CalculateSkuRecommendation(currentSkuInfo, metrics);
        
        device.RecommendedSku = recommendation.RecommendedSku?.SkuName;
        device.SkuRecommendationReason = recommendation.Reason;
        device.PotentialMonthlySavings = recommendation.PotentialSavings;
    }

    /// <summary>
    /// Get LogicMonitor service for customer
    /// </summary>
    private async Task<LogicMonitorService?> GetLogicMonitorServiceAsync(Guid customerId)
    {
        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            
            try
            {
                var prefix = $"LM-{customerId}";
                var companyTask = client.GetSecretAsync($"{prefix}-Company");
                var accessIdTask = client.GetSecretAsync($"{prefix}-AccessId");
                var accessKeyTask = client.GetSecretAsync($"{prefix}-AccessKey");
                
                await Task.WhenAll(companyTask, accessIdTask, accessKeyTask);
                
                return new LogicMonitorService(
                    _loggerFactory,
                    companyTask.Result.Value.Value,
                    accessIdTask.Result.Value.Value,
                    accessKeyTask.Result.Value.Value
                );
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 404)
            {
                _logger.LogInformation("No per-customer LM credentials for {CustomerId}, using global", customerId);
            }
            
            var globalCompanyTask = client.GetSecretAsync("LM-Company");
            var globalAccessIdTask = client.GetSecretAsync("LM-AccessId");
            var globalAccessKeyTask = client.GetSecretAsync("LM-AccessKey");
            
            await Task.WhenAll(globalCompanyTask, globalAccessIdTask, globalAccessKeyTask);
            
            return new LogicMonitorService(
                _loggerFactory,
                globalCompanyTask.Result.Value.Value,
                globalAccessIdTask.Result.Value.Value,
                globalAccessKeyTask.Result.Value.Value
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve LogicMonitor credentials from Key Vault");
            return null;
        }
    }

    #region Bulk History Sync (Queue-based)

    /// <summary>
    /// Start bulk 90-day history sync for all devices (queue-based to avoid timeouts)
    /// </summary>
    [Function("StartBulkHistorySync")]
    public async Task<HttpResponseData> StartBulkHistorySync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/customers/{customerId}/sync-history/start")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        // Check if sync already in progress
        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
        if (syncStatus?.Status == "SyncingHistory")
        {
            var inProgress = req.CreateResponse(HttpStatusCode.OK);
            await inProgress.WriteAsJsonAsync(new
            {
                status = "in_progress",
                message = "History sync already in progress",
                progress = syncStatus.HistorySyncProgress ?? 0,
                totalDevices = syncStatus.HistorySyncTotal ?? 0
            });
            return inProgress;
        }

        // Get devices that have performance metrics
        var devices = await _db.LMDeviceMetricsV2
            .Where(d => d.CustomerId == custId && d.ResourceTypeCode != null && d.ResourceTypeCode != "Unknown")
            .Select(d => d.DeviceId)
            .ToListAsync();

        if (!devices.Any())
        {
            var noDevices = req.CreateResponse(HttpStatusCode.BadRequest);
            await noDevices.WriteStringAsync("No devices with performance metrics found. Run a Performance V2 sync first.");
            return noDevices;
        }

        // Update sync status
        if (syncStatus == null)
        {
            syncStatus = new LMSyncStatus { CustomerId = custId, CreatedAt = DateTime.UtcNow };
            _db.LMSyncStatuses.Add(syncStatus);
        }
        syncStatus.Status = "SyncingHistory";
        syncStatus.HistorySyncProgress = 0;
        syncStatus.HistorySyncTotal = devices.Count;
        syncStatus.HistorySyncStarted = DateTime.UtcNow;
        syncStatus.HistorySyncCompleted = null;
        syncStatus.ErrorMessage = null;
        syncStatus.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Queue messages for each device (batch to avoid flooding)
        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        var queueClient = new Azure.Storage.Queues.QueueClient(connectionString, "lm-history-sync");
        await queueClient.CreateIfNotExistsAsync();

        foreach (var deviceId in devices)
        {
            var message = JsonSerializer.Serialize(new { customerId = custId, deviceId });
            await queueClient.SendMessageAsync(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(message)));
        }

        _logger.LogInformation("Queued {Count} devices for history sync for customer {CustomerId}", devices.Count, custId);

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            status = "started",
            message = $"History sync started for {devices.Count} devices",
            totalDevices = devices.Count
        });
        return response;
    }

    /// <summary>
    /// Get history sync status
    /// </summary>
    [Function("GetHistorySyncStatus")]
    public async Task<HttpResponseData> GetHistorySyncStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/sync-history/status")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            status = syncStatus?.Status ?? "never_synced",
            progress = syncStatus?.HistorySyncProgress ?? 0,
            totalDevices = syncStatus?.HistorySyncTotal ?? 0,
            devicesWithHistory = syncStatus?.HistorySyncWithData ?? 0,
            startedAt = syncStatus?.HistorySyncStarted,
            completedAt = syncStatus?.HistorySyncCompleted,
            errorMessage = syncStatus?.ErrorMessage
        });
        return response;
    }

    /// <summary>
    /// Queue processor for history sync - processes one device at a time
    /// </summary>
    [Function("ProcessHistorySyncQueue")]
    public async Task ProcessHistorySyncQueue(
        [QueueTrigger("lm-history-sync", Connection = "AzureWebJobsStorage")] string message)
    {
        _logger.LogInformation("Processing history sync message: {Message}", message);

        try
        {
            // Parse message (handle both plain JSON and Base64 encoded)
            string jsonMessage = message;
            try
            {
                jsonMessage = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(message));
            }
            catch { /* Already plain text */ }

            var data = JsonSerializer.Deserialize<JsonElement>(jsonMessage);
            var customerId = Guid.Parse(data.GetProperty("customerId").GetString()!);
            var deviceId = data.GetProperty("deviceId").GetInt32();

            _logger.LogInformation("Syncing history for device {DeviceId} customer {CustomerId}", deviceId, customerId);

            // Process this device
            await SyncDeviceHistoryInternal(customerId, deviceId);

            // Update progress
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TievaDbContext>();
            var syncStatus = await db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == customerId);
            if (syncStatus != null)
            {
                syncStatus.HistorySyncProgress = (syncStatus.HistorySyncProgress ?? 0) + 1;
                syncStatus.UpdatedAt = DateTime.UtcNow;

                // Check if complete
                if (syncStatus.HistorySyncProgress >= syncStatus.HistorySyncTotal)
                {
                    syncStatus.Status = "Completed";
                    syncStatus.HistorySyncCompleted = DateTime.UtcNow;
                    _logger.LogInformation("History sync completed for customer {CustomerId}", customerId);
                }

                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing history sync message: {Message}", message);
            throw; // Re-throw to move to poison queue
        }
    }

    /// <summary>
    /// Internal method to sync history for a single device
    /// </summary>
    private async Task SyncDeviceHistoryInternal(Guid customerId, int deviceId)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TievaDbContext>();

        var device = await db.LMDeviceMetricsV2
            .Include(m => m.ResourceType)
            .ThenInclude(rt => rt!.MetricMappings)
            .FirstOrDefaultAsync(m => m.CustomerId == customerId && m.DeviceId == deviceId);

        if (device == null)
        {
            _logger.LogWarning("Device {DeviceId} not found for customer {CustomerId}", deviceId, customerId);
            return;
        }

        var lmService = await GetLogicMonitorServiceAsync(customerId);
        if (lmService == null)
        {
            _logger.LogError("Could not get LM service for customer {CustomerId}", customerId);
            return;
        }

        try
        {
            var end = DateTime.UtcNow;
            var start = end.AddDays(-90);
            var recordsCreated = 0;

            // Get datasources for this device
            var datasources = await lmService.GetDeviceDatasourcesAsync(deviceId);
            if (datasources?.Items == null || !datasources.Items.Any())
            {
                _logger.LogInformation("No datasources found for device {DeviceId}", deviceId);
                return;
            }

            // For each metric mapping, fetch 90 days of data
            var metricMappings = device.ResourceType?.MetricMappings?.Where(m => m.IsActive).ToList() ?? new();

            foreach (var mapping in metricMappings.Take(3)) // Limit to CPU/Mem/Disk
            {
                var dsPatterns = mapping.GetDatasourcePatterns();
                var dpPatterns = mapping.GetDatapointPatterns();

                // Find matching datasource
                var matchedDs = datasources.Items.FirstOrDefault(ds =>
                    dsPatterns.Any(p => ds.DataSourceName?.Contains(p, StringComparison.OrdinalIgnoreCase) == true));

                if (matchedDs == null) continue;

                // Fetch instances
                var instances = await lmService.GetDatasourceInstancesAsync(deviceId, matchedDs.Id);
                if (instances?.Items == null || !instances.Items.Any()) continue;

                var instance = instances.Items.First();

                // Fetch 90-day data in 30-day chunks (LM rate limits)
                var chunkDays = 30;
                var currentStart = start;

                while (currentStart < end)
                {
                    var chunkEnd = currentStart.AddDays(chunkDays);
                    if (chunkEnd > end) chunkEnd = end;

                    try
                    {
                        var data = await lmService.GetInstanceDataAsync(deviceId, matchedDs.Id, instance.Id, currentStart, chunkEnd);

                        if (data?.DataPoints != null && data.Values.HasValue)
                        {
                            // Find matching datapoint index
                            for (int dpIdx = 0; dpIdx < data.DataPoints.Count; dpIdx++)
                            {
                                var dpName = data.DataPoints[dpIdx];
                                if (!dpPatterns.Any(p => dpName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                                    continue;

                                // Process values into daily aggregates
                                var dailyData = ProcessDailyAggregates(data, dpIdx, currentStart, chunkEnd);

                                foreach (var daily in dailyData)
                                {
                                    // Upsert into history table
                                    var existing = await db.LMDeviceMetricHistory
                                        .FirstOrDefaultAsync(h => h.CustomerId == customerId &&
                                                                   h.DeviceId == deviceId &&
                                                                   h.MetricName == mapping.MetricName &&
                                                                   h.MetricDate == daily.Date);

                                    if (existing == null)
                                    {
                                        db.LMDeviceMetricHistory.Add(new LMDeviceMetricHistory
                                        {
                                            CustomerId = customerId,
                                            DeviceId = deviceId,
                                            MetricName = mapping.MetricName,
                                            MetricDate = daily.Date,
                                            AvgValue = daily.Avg,
                                            MaxValue = daily.Max,
                                            MinValue = daily.Min,
                                            P95Value = daily.P95,
                                            SampleCount = daily.SampleCount,
                                            CreatedAt = DateTime.UtcNow
                                        });
                                        recordsCreated++;
                                    }
                                    else
                                    {
                                        existing.AvgValue = daily.Avg;
                                        existing.MaxValue = daily.Max;
                                        existing.MinValue = daily.Min;
                                        existing.P95Value = daily.P95;
                                        existing.SampleCount = daily.SampleCount;
                                    }
                                }

                                break; // Found matching datapoint
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error fetching chunk {Start} to {End} for device {DeviceId}", currentStart, chunkEnd, deviceId);
                    }

                    currentStart = chunkEnd;
                    await Task.Delay(500); // Rate limiting - 500ms between chunks
                }

                await Task.Delay(200); // Rate limiting between metrics
            }

            await db.SaveChangesAsync();

            // Update 90-day aggregates and SKU recommendation
            await UpdateDevice90DayAggregatesInternal(db, device);
            await DetectDeviceSkuInternal(db, device, lmService);
            await db.SaveChangesAsync();

            // Update sync status with data count
            var syncStatus = await db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == customerId);
            if (syncStatus != null && recordsCreated > 0)
            {
                syncStatus.HistorySyncWithData = (syncStatus.HistorySyncWithData ?? 0) + 1;
                await db.SaveChangesAsync();
            }

            _logger.LogInformation("Synced {Records} history records for device {DeviceId}", recordsCreated, deviceId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing history for device {DeviceId}", deviceId);
        }
    }

    /// <summary>
    /// Update 90-day aggregates (internal version with provided DbContext)
    /// </summary>
    private async Task UpdateDevice90DayAggregatesInternal(TievaDbContext db, LMDeviceMetricsV2 device)
    {
        var cutoff = DateTime.UtcNow.Date.AddDays(-90);

        var history = await db.LMDeviceMetricHistory
            .Where(h => h.CustomerId == device.CustomerId && h.DeviceId == device.DeviceId && h.MetricDate >= cutoff)
            .ToListAsync();

        var cpuData = history.Where(h => h.MetricName == "CPU" && h.AvgValue.HasValue).ToList();
        var memData = history.Where(h => h.MetricName == "Memory" && h.AvgValue.HasValue).ToList();
        var diskData = history.Where(h => h.MetricName == "Disk" && h.AvgValue.HasValue).ToList();

        var metrics = new Metrics90Day
        {
            CpuAvg = cpuData.Any() ? (decimal)cpuData.Average(c => (double)c.AvgValue!.Value) : null,
            CpuMax = cpuData.Any() ? cpuData.Max(c => c.MaxValue) : null,
            CpuP95 = cpuData.Any() ? CalculateP95(cpuData.Select(c => c.P95Value ?? c.AvgValue!.Value)) : null,
            MemAvg = memData.Any() ? (decimal)memData.Average(c => (double)c.AvgValue!.Value) : null,
            MemMax = memData.Any() ? memData.Max(c => c.MaxValue) : null,
            MemP95 = memData.Any() ? CalculateP95(memData.Select(c => c.P95Value ?? c.AvgValue!.Value)) : null,
            DiskAvg = diskData.Any() ? (decimal)diskData.Average(c => (double)c.AvgValue!.Value) : null,
            DiskMax = diskData.Any() ? diskData.Max(c => c.MaxValue) : null,
            DiskP95 = diskData.Any() ? CalculateP95(diskData.Select(c => c.P95Value ?? c.AvgValue!.Value)) : null,
            DataPoints = history.Count,
            PeriodStart = history.Any() ? history.Min(h => h.MetricDate) : null,
            PeriodEnd = history.Any() ? history.Max(h => h.MetricDate) : null
        };

        device.SetMetrics90Day(metrics);

        // Update recommendations based on 90-day data
        if (metrics.DataPoints > 7)
        {
            await UpdateSkuRecommendationInternal(db, device, metrics);
        }
    }

    /// <summary>
    /// Detect device SKU (internal version)
    /// </summary>
    private async Task DetectDeviceSkuInternal(TievaDbContext db, LMDeviceMetricsV2 device, LogicMonitorService lmService)
    {
        try
        {
            var details = await lmService.GetDeviceDetailsAsync(device.DeviceId);
            if (details == null) return;

            var vmSize = details.GetProperty("system.azure.vmSize")
                      ?? details.GetProperty("auto.azure.vmSize")
                      ?? details.GetProperty("azure.vm.size");

            if (!string.IsNullOrEmpty(vmSize))
            {
                device.CurrentSku = vmSize;
                device.SkuFamily = ExtractSkuFamily(vmSize);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not detect SKU for device {DeviceId}", device.DeviceId);
        }
    }

    /// <summary>
    /// Update SKU recommendation (internal version)
    /// </summary>
    private async Task UpdateSkuRecommendationInternal(TievaDbContext db, LMDeviceMetricsV2 device, Metrics90Day metrics)
    {
        if (string.IsNullOrEmpty(device.CurrentSku)) return;

        var currentSkuInfo = await db.AzureSkuFamilies
            .FirstOrDefaultAsync(s => s.SkuName == device.CurrentSku && s.IsActive);

        if (currentSkuInfo == null) return;

        // Get all SKUs in same family for recommendation
        var familySkus = await db.AzureSkuFamilies
            .Where(s => s.ResourceType == currentSkuInfo.ResourceType && s.SkuFamily == currentSkuInfo.SkuFamily && s.IsActive)
            .OrderBy(s => s.SizeOrder)
            .ToListAsync();

        var recommendation = CalculateSkuRecommendationFromList(currentSkuInfo, familySkus, metrics);

        device.RecommendedSku = recommendation.RecommendedSku?.SkuName;
        device.SkuRecommendationReason = recommendation.Reason;
        device.PotentialMonthlySavings = recommendation.PotentialSavings;
    }

    /// <summary>
    /// Calculate SKU recommendation from pre-loaded list
    /// </summary>
    private (AzureSkuFamily? RecommendedSku, string Reason, decimal? PotentialSavings) CalculateSkuRecommendationFromList(
        AzureSkuFamily currentSku, List<AzureSkuFamily> familySkus, Metrics90Day metrics)
    {
        if (familySkus.Count <= 1)
        {
            return (currentSku, "Only one SKU in family - no alternative available", null);
        }

        var currentIndex = familySkus.FindIndex(s => s.Id == currentSku.Id);
        if (currentIndex < 0)
        {
            return (currentSku, "Current SKU not found in family", null);
        }

        const decimal cpuOversizedThreshold = 30;
        const decimal cpuUndersizedThreshold = 85;
        const decimal memOversizedThreshold = 40;
        const decimal memUndersizedThreshold = 90;

        bool isOversized = false;
        bool isUndersized = false;
        var reasons = new List<string>();

        if (metrics.CpuP95.HasValue)
        {
            if (metrics.CpuP95 < cpuOversizedThreshold)
            {
                isOversized = true;
                reasons.Add($"CPU P95 is {metrics.CpuP95:F1}% (below {cpuOversizedThreshold}%)");
            }
            else if (metrics.CpuP95 > cpuUndersizedThreshold)
            {
                isUndersized = true;
                reasons.Add($"CPU P95 is {metrics.CpuP95:F1}% (above {cpuUndersizedThreshold}%)");
            }
        }

        if (metrics.MemP95.HasValue)
        {
            if (metrics.MemP95 < memOversizedThreshold)
            {
                isOversized = isOversized && true;
                reasons.Add($"Memory P95 is {metrics.MemP95:F1}% (below {memOversizedThreshold}%)");
            }
            else if (metrics.MemP95 > memUndersizedThreshold)
            {
                isUndersized = true;
                reasons.Add($"Memory P95 is {metrics.MemP95:F1}% (above {memUndersizedThreshold}%)");
            }
        }

        if (isUndersized)
        {
            if (currentIndex >= familySkus.Count - 1)
            {
                return (currentSku, "Already at largest SKU in family. " + string.Join("; ", reasons), null);
            }

            var nextSku = familySkus[currentIndex + 1];
            var costIncrease = nextSku.MonthlyCostEstimate - currentSku.MonthlyCostEstimate;
            return (nextSku, "Undersized - recommend upsizing. " + string.Join("; ", reasons), -costIncrease);
        }

        if (isOversized)
        {
            if (currentIndex <= 0)
            {
                return (currentSku, "Already at smallest SKU in family. " + string.Join("; ", reasons), null);
            }

            var prevSku = familySkus[currentIndex - 1];
            var savings = currentSku.MonthlyCostEstimate - prevSku.MonthlyCostEstimate;
            return (prevSku, "Oversized - recommend downsizing. " + string.Join("; ", reasons), savings);
        }

        return (currentSku, "Right-sized based on 90-day P95 metrics", null);
    }

    #endregion
}
