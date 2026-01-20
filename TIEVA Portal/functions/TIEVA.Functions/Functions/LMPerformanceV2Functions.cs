using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

/// <summary>
/// Performance Monitoring V2 - Data-driven approach with flexible metrics
/// </summary>
public class LMPerformanceV2Functions
{
    private readonly TievaDbContext _db;
    private readonly ILogger<LMPerformanceV2Functions> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly string _keyVaultUrl;

    public LMPerformanceV2Functions(TievaDbContext db, ILogger<LMPerformanceV2Functions> logger, ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _db = db;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _serviceProvider = serviceProvider;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    /// <summary>
    /// Get performance summary grouped by resource type
    /// </summary>
    [Function("GetPerformanceV2Summary")]
    public async Task<HttpResponseData> GetPerformanceSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/summary")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        // Get all resource types with their metrics
        var resourceTypes = await _db.LMResourceTypes
            .Where(rt => rt.IsActive && rt.ShowInDashboard)
            .OrderBy(rt => rt.SortOrder)
            .ToListAsync();

        // Get device metrics for this customer
        var deviceMetrics = await _db.LMDeviceMetricsV2
            .Where(m => m.CustomerId == custId)
            .ToListAsync();

        // Group by resource type
        var summary = new List<object>();
        
        foreach (var rt in resourceTypes)
        {
            var devices = deviceMetrics.Where(d => d.ResourceTypeCode == rt.Code).ToList();
            if (devices.Count == 0) continue;

            var healthyCount = devices.Count(d => d.OverallStatus == "Healthy");
            var warningCount = devices.Count(d => d.OverallStatus == "Warning");
            var criticalCount = devices.Count(d => d.OverallStatus == "Critical");
            var unknownCount = devices.Count(d => d.OverallStatus == "Unknown" || string.IsNullOrEmpty(d.OverallStatus));

            summary.Add(new
            {
                code = rt.Code,
                displayName = rt.DisplayName,
                category = rt.Category,
                icon = rt.Icon,
                hasPerformanceMetrics = rt.HasPerformanceMetrics,
                totalDevices = devices.Count,
                healthy = healthyCount,
                warning = warningCount,
                critical = criticalCount,
                unknown = unknownCount
            });
        }

        // Add unmapped devices
        var unmappedDevices = deviceMetrics.Where(d => string.IsNullOrEmpty(d.ResourceTypeCode) || d.ResourceTypeCode == "Unknown").ToList();
        if (unmappedDevices.Any())
        {
            summary.Add(new
            {
                code = "Unmapped",
                displayName = "Unmapped Resources",
                category = "Other",
                icon = "❓",
                hasPerformanceMetrics = false,
                totalDevices = unmappedDevices.Count,
                healthy = 0,
                warning = 0,
                critical = 0,
                unknown = unmappedDevices.Count
            });
        }

        // Get last sync time
        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = custId,
            totalDevices = deviceMetrics.Count,
            lastSyncedAt = deviceMetrics.Any() ? deviceMetrics.Max(d => d.LastSyncedAt) : (DateTime?)null,
            syncStatus = syncStatus?.Status,
            resourceTypes = summary
        });
        return response;
    }

    /// <summary>
    /// Get devices for a specific resource type
    /// </summary>
    [Function("GetPerformanceV2ByType")]
    public async Task<HttpResponseData> GetPerformanceByType(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/types/{typeCode}")] 
        HttpRequestData req, string customerId, string typeCode)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        // Get resource type and its metric mappings
        var resourceType = await _db.LMResourceTypes
            .Include(rt => rt.MetricMappings.Where(m => m.IsActive))
            .FirstOrDefaultAsync(rt => rt.Code == typeCode);

        if (resourceType == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync($"Resource type '{typeCode}' not found");
            return notFound;
        }

        // Get devices of this type
        var devices = await _db.LMDeviceMetricsV2
            .Where(d => d.CustomerId == custId && d.ResourceTypeCode == typeCode)
            .OrderBy(d => d.DeviceName)
            .ToListAsync();

        // Build response with parsed metrics
        var deviceList = devices.Select(d => 
        {
            var metrics = d.GetMetrics();
            return new
            {
                deviceId = d.DeviceId,
                deviceName = d.DeviceName,
                overallStatus = d.OverallStatus,
                recommendation = d.Recommendation,
                statusDetails = d.StatusDetails,
                lastSyncedAt = d.LastSyncedAt,
                metrics = resourceType.MetricMappings
                    .OrderBy(m => m.SortOrder)
                    .Select(m => new
                    {
                        name = m.MetricName,
                        displayName = m.DisplayName,
                        unit = m.Unit,
                        avg = metrics.TryGetValue(m.MetricName, out var mv) ? mv.Avg : null,
                        max = metrics.TryGetValue(m.MetricName, out var mv2) ? mv2.Max : null,
                        status = metrics.TryGetValue(m.MetricName, out var mv3) ? mv3.Status : null,
                        recommendation = metrics.TryGetValue(m.MetricName, out var mv4) ? mv4.Recommendation : null
                    })
            };
        }).ToList();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            resourceType = new
            {
                code = resourceType.Code,
                displayName = resourceType.DisplayName,
                category = resourceType.Category,
                icon = resourceType.Icon,
                metrics = resourceType.MetricMappings.OrderBy(m => m.SortOrder).Select(m => new
                {
                    name = m.MetricName,
                    displayName = m.DisplayName,
                    unit = m.Unit,
                    warningThreshold = m.WarningThreshold,
                    criticalThreshold = m.CriticalThreshold,
                    oversizedBelow = m.OversizedBelow,
                    undersizedAbove = m.UndersizedAbove
                })
            },
            devices = deviceList,
            summary = new
            {
                total = devices.Count,
                healthy = devices.Count(d => d.OverallStatus == "Healthy"),
                warning = devices.Count(d => d.OverallStatus == "Warning"),
                critical = devices.Count(d => d.OverallStatus == "Critical"),
                unknown = devices.Count(d => d.OverallStatus == "Unknown" || string.IsNullOrEmpty(d.OverallStatus))
            }
        });
        return response;
    }

    /// <summary>
    /// Get performance metrics for a single device (V2 - reads from LMDeviceMetricsV2)
    /// </summary>
    [Function("GetPerformanceV2Device")]
    public async Task<HttpResponseData> GetDevicePerformanceV2(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/devices/{deviceId}")] 
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

        // Get metrics from V2 table
        var deviceMetrics = await _db.LMDeviceMetricsV2
            .FirstOrDefaultAsync(m => m.CustomerId == custId && m.DeviceId == devId);

        if (deviceMetrics == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("No metrics found for this device");
            return notFound;
        }

        // Parse metrics from JSON
        var metrics = deviceMetrics.GetMetrics();
        
        // Extract common metrics (CPU, Memory, Disk) for compatibility with frontend
        var cpu = metrics.TryGetValue("CPU", out var cpuVal) ? cpuVal : null;
        var memory = metrics.TryGetValue("Memory", out var memVal) ? memVal : null;
        var disk = metrics.TryGetValue("Disk", out var diskVal) ? diskVal : null;

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            source = "v2",
            deviceId = deviceMetrics.DeviceId,
            deviceName = deviceMetrics.DeviceName,
            resourceType = deviceMetrics.ResourceTypeCode,
            detectedType = deviceMetrics.DetectedTypeCode,
            overallStatus = deviceMetrics.OverallStatus,
            overallRecommendation = deviceMetrics.Recommendation,
            statusDetails = deviceMetrics.StatusDetails,
            lastSyncedAt = deviceMetrics.LastSyncedAt,
            // SKU info
            currentSku = deviceMetrics.CurrentSku,
            recommendedSku = deviceMetrics.RecommendedSku,
            skuRecommendationReason = deviceMetrics.SkuRecommendationReason,
            potentialMonthlySavings = deviceMetrics.PotentialMonthlySavings,
            // Standard CPU/Memory/Disk format for frontend compatibility
            cpu = cpu != null ? new { avg = cpu.Avg, max = cpu.Max, recommendation = cpu.Recommendation, status = cpu.Status, avg7d = cpu.Avg, max7d = cpu.Max } : null,
            memory = memory != null ? new { avg = memory.Avg, max = memory.Max, recommendation = memory.Recommendation, status = memory.Status, avg7d = memory.Avg, max7d = memory.Max } : null,
            disk = disk != null ? new { avg = disk.Avg, max = disk.Max, recommendation = disk.Recommendation, status = disk.Status, avg7d = disk.Avg, max7d = disk.Max } : null,
            // All metrics from V2 (flexible)
            allMetrics = metrics.Select(kvp => new
            {
                name = kvp.Key,
                avg = kvp.Value.Avg,
                max = kvp.Value.Max,
                status = kvp.Value.Status,
                recommendation = kvp.Value.Recommendation
            })
        });
        return response;
    }

    /// <summary>
    /// Bulk discover datapoints and generate SQL for metric mappings
    /// </summary>
    [Function("BulkDiscoverAndGenerateSQL")]
    public async Task<HttpResponseData> BulkDiscoverAndGenerateSQL(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/bulk-discover")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var lmService = await GetLogicMonitorServiceAsync(custId, _db);
        if (lmService == null)
        {
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("LogicMonitor service unavailable");
            return err;
        }

        try
        {
            // Get all resource types (not just performance ones)
            var resourceTypes = await _db.LMResourceTypes
                .Include(rt => rt.MetricMappings)
                .Where(rt => rt.IsActive)
                .OrderBy(rt => rt.SortOrder)
                .ToListAsync();

            // Get devices grouped by type
            var devicesByType = await _db.LMDeviceMetricsV2
                .Where(d => d.CustomerId == custId && !string.IsNullOrEmpty(d.ResourceTypeCode))
                .GroupBy(d => d.ResourceTypeCode)
                .Select(g => new { TypeCode = g.Key, DeviceId = g.First().DeviceId, DeviceName = g.First().DeviceName })
                .ToListAsync();

            var results = new List<object>();
            var sqlStatements = new List<string>();
            
            sqlStatements.Add("-- Auto-generated metric mappings from bulk discovery");
            sqlStatements.Add($"-- Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sqlStatements.Add("");

            foreach (var rt in resourceTypes)
            {
                var deviceInfo = devicesByType.FirstOrDefault(d => d.TypeCode == rt.Code);
                if (deviceInfo == null) continue;

                var typeResult = new
                {
                    resourceType = rt.Code,
                    resourceTypeId = rt.Id,
                    displayName = rt.DisplayName,
                    existingMappings = rt.MetricMappings.Count,
                    sampleDevice = deviceInfo.DeviceName,
                    datasources = new List<object>()
                };

                try
                {
                    // Get datasources for sample device
                    var datasources = await lmService.GetDeviceDatasourcesAsync(deviceInfo.DeviceId);
                    if (datasources?.Items == null) continue;

                    // Filter to relevant datasources (skip monitoring-only ones)
                    var perfDatasources = datasources.Items
                        .Where(ds => !string.IsNullOrEmpty(ds.DataSourceName) &&
                                    !ds.DataSourceName.Contains("Whois") &&
                                    !ds.DataSourceName.Contains("SSL_") &&
                                    !ds.DataSourceName.Contains("HostStatus") &&
                                    !ds.DataSourceName.Contains("Ping") &&
                                    !ds.DataSourceName.Contains("SNMP"))
                        .OrderBy(ds => ds.DataSourceName)
                        .Take(10) // Limit per type
                        .ToList();

                    foreach (var ds in perfDatasources)
                    {
                        var dsResult = new
                        {
                            datasourceName = ds.DataSourceName,
                            datapoints = new List<string>()
                        };

                        try
                        {
                            var instances = await lmService.GetDatasourceInstancesAsync(deviceInfo.DeviceId, ds.Id);
                            if (instances?.Items != null && instances.Items.Any())
                            {
                                var instance = instances.Items.First();
                                var end = DateTime.UtcNow;
                                var start = end.AddHours(-1);
                                var data = await lmService.GetInstanceDataAsync(deviceInfo.DeviceId, ds.Id, instance.Id, start, end);
                                
                                if (data?.DataPoints != null)
                                {
                                    foreach (var dp in data.DataPoints)
                                    {
                                        ((List<string>)dsResult.datapoints).Add(dp);
                                        
                                        // Check if this datapoint is already mapped
                                        var alreadyMapped = rt.MetricMappings.Any(m => 
                                            m.GetDatapointPatterns().Any(p => dp.Contains(p, StringComparison.OrdinalIgnoreCase)));
                                        
                                        if (!alreadyMapped)
                                        {
                                            // Generate SQL for this mapping
                                            var metricName = SuggestMetricName(dp);
                                            var unit = SuggestUnit(dp);
                                            var (warn, crit, invert) = SuggestThresholds(dp);
                                            
                                            sqlStatements.Add($"-- {rt.DisplayName}: {ds.DataSourceName} -> {dp}");
                                            sqlStatements.Add($"INSERT INTO LMMetricMappings (ResourceTypeId, MetricName, DisplayName, Unit, DatasourcePatternsJson, DatapointPatternsJson, WarningThreshold, CriticalThreshold, InvertThreshold, SortOrder, IsActive, CreatedAt, UpdatedAt)");
                                            sqlStatements.Add($"VALUES ({rt.Id}, '{metricName}', '{metricName}', '{unit}', '[\"{ds.DataSourceName}\"]', '[\"{dp}\"]', {warn?.ToString() ?? "NULL"}, {crit?.ToString() ?? "NULL"}, {(invert ? 1 : 0)}, 0, 1, GETUTCDATE(), GETUTCDATE());");
                                            sqlStatements.Add("");
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error getting datapoints for {Ds}", ds.DataSourceName);
                        }

                        ((List<object>)typeResult.datasources).Add(dsResult);
                        await Task.Delay(100); // Rate limiting
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error discovering for type {Type}", rt.Code);
                }

                results.Add(typeResult);
            }

            // Now discover unmapped devices and suggest new resource types
            var unmappedResults = new List<object>();
            var unmappedDevices = await _db.LMDeviceMetricsV2
                .Where(d => d.CustomerId == custId && 
                       (string.IsNullOrEmpty(d.ResourceTypeCode) || 
                        d.ResourceTypeCode == "Unknown" || 
                        d.ResourceTypeCode == "MetadataOnly"))
                .Take(20) // Limit to avoid long processing
                .ToListAsync();

            if (unmappedDevices.Any())
            {
                sqlStatements.Add("-- ========================================");
                sqlStatements.Add("-- UNMAPPED DEVICES - Suggested New Resource Types");
                sqlStatements.Add("-- ========================================");
                sqlStatements.Add("");

                // Group by common datasources to suggest new types
                var datasourceGroups = new Dictionary<string, List<string>>();

                foreach (var device in unmappedDevices.Take(10))
                {
                    try
                    {
                        var datasources = await lmService.GetDeviceDatasourcesAsync(device.DeviceId);
                        if (datasources?.Items == null) continue;

                        var deviceDs = new List<string>();
                        foreach (var ds in datasources.Items.Where(d => 
                            !string.IsNullOrEmpty(d.DataSourceName) &&
                            !d.DataSourceName.Contains("Whois") &&
                            !d.DataSourceName.Contains("SSL_") &&
                            !d.DataSourceName.Contains("HostStatus") &&
                            !d.DataSourceName.Contains("Ping")))
                        {
                            deviceDs.Add(ds.DataSourceName);
                            if (!datasourceGroups.ContainsKey(ds.DataSourceName))
                                datasourceGroups[ds.DataSourceName] = new List<string>();
                            datasourceGroups[ds.DataSourceName].Add(device.DeviceName);
                        }

                        unmappedResults.Add(new
                        {
                            deviceId = device.DeviceId,
                            deviceName = device.DeviceName,
                            currentType = device.ResourceTypeCode,
                            datasources = deviceDs
                        });

                        await Task.Delay(100);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error discovering unmapped device {DeviceId}", device.DeviceId);
                    }
                }

                // Suggest new resource types based on common datasources
                var commonDatasources = datasourceGroups
                    .Where(g => g.Value.Count >= 2)
                    .OrderByDescending(g => g.Value.Count)
                    .Take(5);

                foreach (var ds in commonDatasources)
                {
                    // Generate a suggested type code from datasource name
                    var suggestedCode = ds.Key
                        .Replace("Microsoft_Azure_", "Azure")
                        .Replace("Microsoft_", "")
                        .Replace("_", "");
                    
                    sqlStatements.Add($"-- Datasource '{ds.Key}' found on {ds.Value.Count} unmapped devices: {string.Join(", ", ds.Value.Take(3))}");
                    sqlStatements.Add($"-- Suggested new resource type:");
                    sqlStatements.Add($"INSERT INTO LMResourceTypes (Code, DisplayName, Category, Icon, DetectionPatternsJson, SortOrder, ShowInDashboard, HasPerformanceMetrics, IsActive, CreatedAt, UpdatedAt)");
                    sqlStatements.Add($"VALUES ('{suggestedCode}', '{suggestedCode}', 'Other', '📦', '[\"{ds.Key}\"]', 100, 1, 1, 1, GETUTCDATE(), GETUTCDATE());");
                    sqlStatements.Add("");
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                summary = $"Discovered datapoints for {results.Count} resource types",
                generatedSqlStatements = sqlStatements.Count - 3, // minus header lines
                resourceTypes = results,
                unmappedDevices = unmappedResults,
                sql = string.Join("\n", sqlStatements)
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk discovery failed");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    private bool IsPerformanceDatapoint(string name)
    {
        var perfKeywords = new[] { "CPU", "Memory", "Disk", "IOPS", "Bandwidth", "Utilization", "Usage", "Percent", "Capacity", "Latency", "Throughput", "Queue", "Read", "Write", "Free", "Used", "Available" };
        return perfKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    private string SuggestMetricName(string datapoint)
    {
        // Clean up datapoint name to create a friendly metric name
        var name = datapoint
            .Replace("Percentage", "")
            .Replace("Percent", "")
            .Replace("Consumed", "")
            .Replace("Average", "");
        
        // Insert spaces before capitals
        name = System.Text.RegularExpressions.Regex.Replace(name, "([A-Z])", " $1").Trim();
        
        // Take first 2-3 meaningful words
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3);
        return string.Join("", words);
    }

    private string SuggestUnit(string datapoint)
    {
        if (datapoint.Contains("Percent", StringComparison.OrdinalIgnoreCase)) return "%";
        if (datapoint.Contains("IOPS", StringComparison.OrdinalIgnoreCase)) return "IOPS";
        if (datapoint.Contains("Bytes", StringComparison.OrdinalIgnoreCase)) return "bytes";
        if (datapoint.Contains("MB", StringComparison.OrdinalIgnoreCase)) return "MB";
        if (datapoint.Contains("GB", StringComparison.OrdinalIgnoreCase)) return "GB";
        if (datapoint.Contains("Latency", StringComparison.OrdinalIgnoreCase)) return "ms";
        if (datapoint.Contains("Throughput", StringComparison.OrdinalIgnoreCase)) return "MB/s";
        return "%";
    }

    private (decimal? warn, decimal? crit, bool invert) SuggestThresholds(string datapoint)
    {
        var lowerName = datapoint.ToLower();
        
        // Availability metrics - lower is worse
        if (lowerName.Contains("availability") || lowerName.Contains("uptime"))
            return (99, 95, true);
        
        // Free space metrics - lower is worse
        if (lowerName.Contains("free") || lowerName.Contains("available"))
            return (20, 10, true);
        
        // Usage/utilization metrics - higher is worse
        if (lowerName.Contains("usage") || lowerName.Contains("utilization") || 
            lowerName.Contains("percent") || lowerName.Contains("cpu") || lowerName.Contains("memory"))
            return (70, 90, false);
        
        // Queue depth - higher is worse
        if (lowerName.Contains("queue"))
            return (50, 100, false);
        
        // Default - assume percentage where higher is worse
        return (70, 90, false);
    }

    /// <summary>
    /// Discover available datasources and datapoints for a device
    /// Used by admins to find correct mapping patterns
    /// </summary>
    [Function("DiscoverDeviceDatapoints")]
    public async Task<HttpResponseData> DiscoverDatapoints(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/devices/{deviceId}/discover")] 
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

        var lmService = await GetLogicMonitorServiceAsync(custId, _db);
        if (lmService == null)
        {
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("LogicMonitor service unavailable");
            return err;
        }

        try
        {
            // Get all datasources for this device
            var datasources = await lmService.GetDeviceDatasourcesAsync(devId);
            if (datasources?.Items == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("No datasources found");
                return notFound;
            }

            var results = new List<object>();

            foreach (var ds in datasources.Items.OrderBy(d => d.DataSourceName))
            {
                var dsInfo = new
                {
                    datasourceId = ds.Id,
                    datasourceName = ds.DataSourceName,
                    datapoints = new List<object>()
                };

                // Get instances for this datasource
                try
                {
                    var instances = await lmService.GetDatasourceInstancesAsync(devId, ds.Id);
                    if (instances?.Items != null && instances.Items.Any())
                    {
                        var instance = instances.Items.First();
                        
                        // Get data to discover datapoint names
                        var end = DateTime.UtcNow;
                        var start = end.AddHours(-1); // Just 1 hour for discovery
                        var data = await lmService.GetInstanceDataAsync(devId, ds.Id, instance.Id, start, end);
                        
                        if (data?.DataPoints != null)
                        {
                            foreach (var dp in data.DataPoints)
                            {
                                ((List<object>)dsInfo.datapoints).Add(new { name = dp });
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error getting datapoints for datasource {DsName}", ds.DataSourceName);
                }

                results.Add(dsInfo);
                
                await Task.Delay(100); // Rate limiting
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                deviceId = devId,
                datasourceCount = results.Count,
                datasources = results
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering datapoints for device {DeviceId}", devId);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Get all resource types with their mappings (for admin UI)
    /// </summary>
    [Function("GetResourceTypes")]
    public async Task<HttpResponseData> GetResourceTypes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/resource-types")] 
        HttpRequestData req)
    {
        var types = await _db.LMResourceTypes
            .Include(rt => rt.MetricMappings.Where(m => m.IsActive))
            .OrderBy(rt => rt.SortOrder)
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(types.Select(rt => new
        {
            id = rt.Id,
            code = rt.Code,
            displayName = rt.DisplayName,
            category = rt.Category,
            icon = rt.Icon,
            detectionPatterns = rt.GetDetectionPatterns(),
            sortOrder = rt.SortOrder,
            showInDashboard = rt.ShowInDashboard,
            hasPerformanceMetrics = rt.HasPerformanceMetrics,
            isActive = rt.IsActive,
            metrics = rt.MetricMappings.OrderBy(m => m.SortOrder).Select(m => new
            {
                id = m.Id,
                metricName = m.MetricName,
                displayName = m.DisplayName,
                unit = m.Unit,
                datasourcePatterns = m.GetDatasourcePatterns(),
                datapointPatterns = m.GetDatapointPatterns(),
                warningThreshold = m.WarningThreshold,
                criticalThreshold = m.CriticalThreshold,
                invertThreshold = m.InvertThreshold,
                oversizedBelow = m.OversizedBelow,
                undersizedAbove = m.UndersizedAbove,
                sortOrder = m.SortOrder
            })
        }));
        return response;
    }

    /// <summary>
    /// Update a resource type
    /// </summary>
    [Function("UpdateResourceType")]
    public async Task<HttpResponseData> UpdateResourceType(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v2/performance/resource-types/{id}")] 
        HttpRequestData req, string id)
    {
        if (!int.TryParse(id, out var rtId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid resource type ID");
            return badRequest;
        }

        var resourceType = await _db.LMResourceTypes.FindAsync(rtId);
        if (resourceType == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Resource type not found");
            return notFound;
        }

        try
        {
            var body = await req.ReadAsStringAsync();
            var update = JsonSerializer.Deserialize<JsonElement>(body ?? "{}");

            if (update.TryGetProperty("displayName", out var displayName))
                resourceType.DisplayName = displayName.GetString() ?? resourceType.DisplayName;
            if (update.TryGetProperty("category", out var category))
                resourceType.Category = category.GetString() ?? resourceType.Category;
            if (update.TryGetProperty("icon", out var icon))
                resourceType.Icon = icon.GetString();
            if (update.TryGetProperty("detectionPatterns", out var patterns))
                resourceType.DetectionPatternsJson = JsonSerializer.Serialize(patterns);
            if (update.TryGetProperty("sortOrder", out var sortOrder))
                resourceType.SortOrder = sortOrder.GetInt32();
            if (update.TryGetProperty("showInDashboard", out var showInDashboard))
                resourceType.ShowInDashboard = showInDashboard.GetBoolean();
            if (update.TryGetProperty("hasPerformanceMetrics", out var hasPerf))
                resourceType.HasPerformanceMetrics = hasPerf.GetBoolean();
            if (update.TryGetProperty("isActive", out var isActive))
                resourceType.IsActive = isActive.GetBoolean();

            resourceType.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, id = resourceType.Id });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating resource type {Id}", rtId);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Create a new metric mapping
    /// </summary>
    [Function("CreateMetricMapping")]
    public async Task<HttpResponseData> CreateMetricMapping(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/resource-types/{resourceTypeId}/mappings")] 
        HttpRequestData req, string resourceTypeId)
    {
        if (!int.TryParse(resourceTypeId, out var rtId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid resource type ID");
            return badRequest;
        }

        var resourceType = await _db.LMResourceTypes.FindAsync(rtId);
        if (resourceType == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Resource type not found");
            return notFound;
        }

        try
        {
            var body = await req.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(body ?? "{}");

            var mapping = new LMMetricMapping
            {
                ResourceTypeId = rtId,
                MetricName = data.GetProperty("metricName").GetString() ?? "NewMetric",
                DisplayName = data.TryGetProperty("displayName", out var dn) ? dn.GetString() : null,
                Unit = data.TryGetProperty("unit", out var u) ? u.GetString() : "%",
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            if (data.TryGetProperty("datasourcePatterns", out var dsPatterns))
                mapping.DatasourcePatternsJson = JsonSerializer.Serialize(dsPatterns);
            if (data.TryGetProperty("datapointPatterns", out var dpPatterns))
                mapping.DatapointPatternsJson = JsonSerializer.Serialize(dpPatterns);
            if (data.TryGetProperty("warningThreshold", out var wt) && wt.ValueKind == JsonValueKind.Number)
                mapping.WarningThreshold = wt.GetDecimal();
            if (data.TryGetProperty("criticalThreshold", out var ct) && ct.ValueKind == JsonValueKind.Number)
                mapping.CriticalThreshold = ct.GetDecimal();
            if (data.TryGetProperty("invertThreshold", out var it))
                mapping.InvertThreshold = it.GetBoolean();
            if (data.TryGetProperty("oversizedBelow", out var ob) && ob.ValueKind == JsonValueKind.Number)
                mapping.OversizedBelow = ob.GetDecimal();
            if (data.TryGetProperty("undersizedAbove", out var ua) && ua.ValueKind == JsonValueKind.Number)
                mapping.UndersizedAbove = ua.GetDecimal();
            if (data.TryGetProperty("sortOrder", out var so))
                mapping.SortOrder = so.GetInt32();

            _db.LMMetricMappings.Add(mapping);
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(new { success = true, id = mapping.Id });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating metric mapping");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Update a metric mapping
    /// </summary>
    [Function("UpdateMetricMapping")]
    public async Task<HttpResponseData> UpdateMetricMapping(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "v2/performance/mappings/{id}")] 
        HttpRequestData req, string id)
    {
        if (!int.TryParse(id, out var mappingId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid mapping ID");
            return badRequest;
        }

        var mapping = await _db.LMMetricMappings.FindAsync(mappingId);
        if (mapping == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Mapping not found");
            return notFound;
        }

        try
        {
            var body = await req.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(body ?? "{}");

            if (data.TryGetProperty("metricName", out var mn))
                mapping.MetricName = mn.GetString() ?? mapping.MetricName;
            if (data.TryGetProperty("displayName", out var dn))
                mapping.DisplayName = dn.GetString();
            if (data.TryGetProperty("unit", out var u))
                mapping.Unit = u.GetString();
            if (data.TryGetProperty("datasourcePatterns", out var dsPatterns))
                mapping.DatasourcePatternsJson = JsonSerializer.Serialize(dsPatterns);
            if (data.TryGetProperty("datapointPatterns", out var dpPatterns))
                mapping.DatapointPatternsJson = JsonSerializer.Serialize(dpPatterns);
            if (data.TryGetProperty("warningThreshold", out var wt))
                mapping.WarningThreshold = wt.ValueKind == JsonValueKind.Number ? wt.GetDecimal() : null;
            if (data.TryGetProperty("criticalThreshold", out var ct))
                mapping.CriticalThreshold = ct.ValueKind == JsonValueKind.Number ? ct.GetDecimal() : null;
            if (data.TryGetProperty("invertThreshold", out var it))
                mapping.InvertThreshold = it.GetBoolean();
            if (data.TryGetProperty("oversizedBelow", out var ob))
                mapping.OversizedBelow = ob.ValueKind == JsonValueKind.Number ? ob.GetDecimal() : null;
            if (data.TryGetProperty("undersizedAbove", out var ua))
                mapping.UndersizedAbove = ua.ValueKind == JsonValueKind.Number ? ua.GetDecimal() : null;
            if (data.TryGetProperty("sortOrder", out var so))
                mapping.SortOrder = so.GetInt32();
            if (data.TryGetProperty("isActive", out var ia))
                mapping.IsActive = ia.GetBoolean();

            mapping.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, id = mapping.Id });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating metric mapping {Id}", mappingId);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Delete a metric mapping
    /// </summary>
    [Function("DeleteMetricMapping")]
    public async Task<HttpResponseData> DeleteMetricMapping(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "v2/performance/mappings/{id}")] 
        HttpRequestData req, string id)
    {
        if (!int.TryParse(id, out var mappingId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid mapping ID");
            return badRequest;
        }

        var mapping = await _db.LMMetricMappings.FindAsync(mappingId);
        if (mapping == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Mapping not found");
            return notFound;
        }

        try
        {
            _db.LMMetricMappings.Remove(mapping);
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, deleted = mappingId });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting metric mapping {Id}", mappingId);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Get sync status
    /// </summary>
    [Function("GetPerformanceV2SyncStatus")]
    public async Task<HttpResponseData> GetSyncStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/sync/status")] 
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
            progress = syncStatus?.PerformanceSyncProgress ?? 0,
            totalDevices = syncStatus?.DeviceCount ?? 0,
            devicesWithData = syncStatus?.PerformanceDevicesWithData ?? 0,
            lastStarted = syncStatus?.LastSyncStarted,
            lastCompleted = syncStatus?.LastSyncCompleted,
            errorMessage = syncStatus?.ErrorMessage
        });
        return response;
    }

    /// <summary>
    /// Start async V2 sync
    /// </summary>
    [Function("StartPerformanceV2Sync")]
    public async Task<HttpResponseData> StartSync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/customers/{customerId}/sync")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var customer = await _db.Customers.FindAsync(custId);
        if (customer == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Customer not found");
            return notFound;
        }

        // Check if sync already in progress
        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
        if (syncStatus?.Status == "SyncingPerformanceV2")
        {
            var inProgress = req.CreateResponse(HttpStatusCode.OK);
            await inProgress.WriteAsJsonAsync(new
            {
                status = "in_progress",
                message = "Performance V2 sync already in progress",
                progress = syncStatus.PerformanceSyncProgress ?? 0,
                totalDevices = syncStatus.DeviceCount ?? 0
            });
            return inProgress;
        }

        // Get device count
        var deviceCount = await _db.LMDevices.CountAsync(d => d.CustomerId == custId);
        if (deviceCount == 0)
        {
            var noDevices = req.CreateResponse(HttpStatusCode.BadRequest);
            await noDevices.WriteStringAsync("No devices found. Please sync devices first.");
            return noDevices;
        }

        // Update sync status
        if (syncStatus == null)
        {
            syncStatus = new LMSyncStatus { CustomerId = custId };
            _db.LMSyncStatuses.Add(syncStatus);
        }
        syncStatus.Status = "SyncingPerformanceV2";
        syncStatus.PerformanceSyncProgress = 0;
        syncStatus.PerformanceDevicesWithData = 0;
        syncStatus.DeviceCount = deviceCount;
        syncStatus.LastSyncStarted = DateTime.UtcNow;
        syncStatus.ErrorMessage = null;
        await _db.SaveChangesAsync();

        // Run sync directly with a new DbContext scope (synchronous)
        // This will take time but the frontend handles timeout gracefully
        _ = Task.Run(async () => {
            try {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<TievaDbContext>();
                await ProcessSyncWithDbAsync(custId, db);
            } catch (Exception e) {
                _logger.LogError(e, "Background sync failed for {CustomerId}", custId);
                // Update status to error
                try {
                    using var scope = _serviceProvider.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<TievaDbContext>();
                    var status = await db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
                    if (status != null) {
                        status.Status = "Error";
                        status.ErrorMessage = e.Message;
                        await db.SaveChangesAsync();
                    }
                } catch { }
            }
        });

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            status = "started",
            message = $"Performance V2 sync started for {deviceCount} devices",
            totalDevices = deviceCount
        });
        return response;
    }

    /// <summary>
    /// Manual sync trigger - bypasses queue for debugging
    /// </summary>
    [Function("RunPerformanceV2SyncManual")]
    public async Task<HttpResponseData> RunSyncManual(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/customers/{customerId}/sync/run")] 
        HttpRequestData req, string customerId)
    {
        _logger.LogInformation("Manual sync trigger called for {CustomerId}", customerId);
        
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        // Run sync directly using a new scope
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TievaDbContext>();
            await ProcessSyncWithDbAsync(custId, db);
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { status = "completed", message = "Sync completed successfully" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual sync failed");
            var errResp = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errResp.WriteAsJsonAsync(new { status = "error", message = ex.Message });
            return errResp;
        }
    }

    /// <summary>
    /// Queue processor for V2 sync
    /// </summary>
    [Function("ProcessPerformanceV2SyncQueue")]
    public async Task ProcessSyncQueue(
        [QueueTrigger("lm-performance-v2-sync", Connection = "AzureWebJobsStorage")] string message)
    {
        _logger.LogInformation("Queue message received: {Message}", message);
        
        try
        {
            // Try to parse as plain GUID first (Azure SDK may auto-decode)
            Guid customerId;
            if (Guid.TryParse(message, out customerId))
            {
                _logger.LogInformation("Parsed as plain GUID: {CustomerId}", customerId);
            }
            else
            {
                // Try Base64 decode
                try
                {
                    var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(message));
                    customerId = Guid.Parse(decoded);
                    _logger.LogInformation("Parsed from Base64: {CustomerId}", customerId);
                }
                catch
                {
                    _logger.LogError("Failed to parse message as GUID or Base64: {Message}", message);
                    throw new ArgumentException($"Invalid message format: {message}");
                }
            }
            
            await ProcessSyncWithDbAsync(customerId, _db);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ProcessPerformanceV2SyncQueue failed for message: {Message}", message);
            throw; // Re-throw so message goes to poison queue
        }
    }

    /// <summary>
    /// Core sync logic - data-driven approach
    /// </summary>
    private async Task ProcessSyncWithDbAsync(Guid custId, TievaDbContext db)
    {
        var syncStatus = await db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
        
        try
        {
            _logger.LogInformation("Starting Performance V2 sync for customer {CustomerId}", custId);

            // Load resource types with metric mappings
            var resourceTypes = await db.LMResourceTypes
                .Include(rt => rt.MetricMappings.Where(m => m.IsActive))
                .Where(rt => rt.IsActive)
                .OrderBy(rt => rt.SortOrder) // Lower SortOrder = higher priority (WindowsServer=10 before MetadataOnly=900)
                .ToListAsync();

            // Get devices
            var devices = await db.LMDevices
                .Where(d => d.CustomerId == custId)
                .ToListAsync();

            if (!devices.Any())
            {
                if (syncStatus != null)
                {
                    syncStatus.Status = "Error";
                    syncStatus.ErrorMessage = "No devices found";
                    await db.SaveChangesAsync();
                }
                return;
            }

            // Get LM service
            var lmService = await GetLogicMonitorServiceAsync(custId, db);
            if (lmService == null)
            {
                if (syncStatus != null)
                {
                    syncStatus.Status = "Error";
                    syncStatus.ErrorMessage = "LogicMonitor service unavailable";
                    await db.SaveChangesAsync();
                }
                return;
            }

            var now = DateTime.UtcNow;
            var synced = 0;
            var withData = 0;
            var totalDevices = devices.Count;
            var errors = new List<string>();

            // Time ranges
            var end = now;
            var start7Day = now.AddDays(-7);

            foreach (var device in devices)
            {
                try
                {
                    _logger.LogDebug("Processing device {DeviceId}: {DeviceName}", device.Id, device.DisplayName);

                    // Get or create metrics record
                    var metrics = await db.LMDeviceMetricsV2
                        .FirstOrDefaultAsync(m => m.CustomerId == custId && m.DeviceId == device.Id);

                    if (metrics == null)
                    {
                        metrics = new LMDeviceMetricsV2
                        {
                            CustomerId = custId,
                            DeviceId = device.Id,
                            DeviceName = device.DisplayName,
                            CreatedAt = now
                        };
                        db.LMDeviceMetricsV2.Add(metrics);
                    }

                    metrics.DeviceName = device.DisplayName;
                    metrics.LastSyncedAt = now;
                    metrics.UpdatedAt = now;

                    // Get datasources for this device
                    var datasources = await lmService.GetDeviceDatasourcesAsync(device.Id);
                    if (datasources?.Items == null || !datasources.Items.Any())
                    {
                        metrics.OverallStatus = "Unknown";
                        metrics.StatusDetails = "No datasources found";
                        metrics.ResourceTypeCode = "Unknown";
                        synced++;
                        continue;
                    }

                    // Store available datasources
                    var allDsNames = datasources.Items
                        .Select(ds => ds.DataSourceName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Select(n => n!)
                        .Distinct()
                        .OrderBy(n => n)
                        .ToList();
                    metrics.AvailableDatasourcesJson = JsonSerializer.Serialize(allDsNames);

                    // Match to resource type
                    var matchedType = MatchResourceType(allDsNames, resourceTypes);
                    metrics.DetectedTypeCode = matchedType?.Code ?? "Unknown";
                    metrics.ResourceTypeCode = matchedType?.Code ?? "Unknown";
                    metrics.ResourceTypeId = matchedType?.Id;

                    if (matchedType == null || !matchedType.HasPerformanceMetrics || !matchedType.MetricMappings.Any())
                    {
                        metrics.OverallStatus = "Unknown";
                        metrics.StatusDetails = matchedType == null 
                            ? "No matching resource type" 
                            : "Resource type has no performance metrics";
                        synced++;
                        continue;
                    }

                    // Fetch metrics based on mappings
                    var metricValues = new Dictionary<string, MetricValue>();
                    var hasAnyData = false;
                    var metricDebugInfo = new List<string>();

                    foreach (var mapping in matchedType.MetricMappings.OrderBy(m => m.SortOrder))
                    {
                        var dsPatterns = mapping.GetDatasourcePatterns();
                        var dpPatterns = mapping.GetDatapointPatterns();

                        _logger.LogDebug("Device {DeviceId}: Looking for {Metric} - DS patterns: [{DsPatterns}], DP patterns: [{DpPatterns}]",
                            device.Id, mapping.MetricName, string.Join(", ", dsPatterns), string.Join(", ", dpPatterns));

                        // Find matching datasource - try each pattern
                        LMDeviceDatasource? matchedDs = null;
                        string? matchedPattern = null;
                        
                        foreach (var pattern in dsPatterns)
                        {
                            matchedDs = datasources.Items.FirstOrDefault(ds =>
                                ds.DataSourceName?.Contains(pattern, StringComparison.OrdinalIgnoreCase) == true);
                            if (matchedDs != null)
                            {
                                matchedPattern = pattern;
                                break;
                            }
                        }

                        if (matchedDs != null)
                        {
                            _logger.LogDebug("Device {DeviceId}: Found datasource '{DsName}' (ID: {DsId}) matching pattern '{Pattern}' for {Metric}",
                                device.Id, matchedDs.DataSourceName, matchedDs.Id, matchedPattern, mapping.MetricName);
                            
                            // Special handling for Disk - fetch ALL disk instances
                            if (mapping.MetricName.Equals("Disk", StringComparison.OrdinalIgnoreCase) && 
                                dpPatterns.Any(p => p.StartsWith("CALC:", StringComparison.OrdinalIgnoreCase)))
                            {
                                var allDisks = await FetchAllDiskMetricsAsync(lmService, device.Id, matchedDs.Id, start7Day, end);
                                
                                if (allDisks.Any())
                                {
                                    // Store each disk as a separate metric
                                    foreach (var disk in allDisks)
                                    {
                                        var diskMetricName = $"Disk ({disk.Key})";
                                        var diskMv = new MetricValue
                                        {
                                            Avg = (decimal)disk.Value.avg,
                                            Max = (decimal)disk.Value.max
                                        };
                                        diskMv.Status = CalculateMetricStatus(diskMv.Avg, mapping);
                                        diskMv.Recommendation = CalculateMetricRecommendation(diskMv.Avg, mapping);
                                        metricValues[diskMetricName] = diskMv;
                                        metricDebugInfo.Add($"{diskMetricName}={diskMv.Avg:F1}%");
                                    }
                                    
                                    // Also store overall "Disk" with the WORST (highest usage) disk
                                    var worstDisk = allDisks.OrderByDescending(d => d.Value.avg).First();
                                    var overallMv = new MetricValue
                                    {
                                        Avg = (decimal)worstDisk.Value.avg,
                                        Max = (decimal)worstDisk.Value.max
                                    };
                                    overallMv.Status = CalculateMetricStatus(overallMv.Avg, mapping);
                                    overallMv.Recommendation = CalculateMetricRecommendation(overallMv.Avg, mapping);
                                    metricValues["Disk"] = overallMv;
                                    
                                    hasAnyData = true;
                                    _logger.LogInformation("Device {DeviceId} {DeviceName}: Found {Count} disks. Worst: {Drive}={Avg:F1}%",
                                        device.Id, device.DisplayName, allDisks.Count, worstDisk.Key, worstDisk.Value.avg);
                                }
                                else
                                {
                                    metricDebugInfo.Add($"{mapping.MetricName}=NO_DISKS");
                                    _logger.LogWarning("Device {DeviceId}: No disk data found", device.Id);
                                }
                            }
                            else
                            {
                                // Standard metric processing
                                var data = await FetchMetricDataAsync(lmService, device.Id, matchedDs.Id, dpPatterns, start7Day, end, mapping.MetricName);
                                if (data.HasValue)
                                {
                                    var mv = new MetricValue
                                    {
                                        Avg = (decimal)data.Value.avg,
                                        Max = (decimal)data.Value.max
                                    };

                                    mv.Status = CalculateMetricStatus(mv.Avg, mapping);
                                    mv.Recommendation = CalculateMetricRecommendation(mv.Avg, mapping);

                                    metricValues[mapping.MetricName] = mv;
                                    hasAnyData = true;
                                    metricDebugInfo.Add($"{mapping.MetricName}={mv.Avg:F1}%");
                                    _logger.LogInformation("Device {DeviceId} {DeviceName}: {Metric} = Avg:{Avg:F1}%, Max:{Max:F1}%",
                                        device.Id, device.DisplayName, mapping.MetricName, mv.Avg, mv.Max);
                                }
                                else
                                {
                                    metricDebugInfo.Add($"{mapping.MetricName}=NO_DATA");
                                    _logger.LogWarning("Device {DeviceId}: Datasource '{DsName}' found but no datapoint data for {Metric}",
                                        device.Id, matchedDs.DataSourceName, mapping.MetricName);
                                }
                            }
                        }
                        else
                        {
                            // Log which datasources were available
                            var availableDs = datasources.Items.Select(d => d.DataSourceName).Take(10);
                            metricDebugInfo.Add($"{mapping.MetricName}=NO_DS");
                            _logger.LogWarning("Device {DeviceId}: No datasource found for {Metric}. Patterns: [{Patterns}]. Available: [{Available}]",
                                device.Id, mapping.MetricName, string.Join(", ", dsPatterns), string.Join(", ", availableDs));
                        }
                    }
                    
                    if (metricDebugInfo.Any())
                    {
                        _logger.LogInformation("Device {DeviceId} {DeviceName} metrics summary: {Summary}",
                            device.Id, device.DisplayName, string.Join(", ", metricDebugInfo));
                    }

                    // Store metrics
                    metrics.SetMetrics(metricValues);
                    metrics.LastMetricDataAt = hasAnyData ? now : null;

                    // Calculate overall status
                    if (hasAnyData)
                    {
                        var statuses = metricValues.Values.Select(v => v.Status).Where(s => !string.IsNullOrEmpty(s)).ToList();
                        if (statuses.Contains("Critical"))
                            metrics.OverallStatus = "Critical";
                        else if (statuses.Contains("Warning"))
                            metrics.OverallStatus = "Warning";
                        else if (statuses.Any())
                            metrics.OverallStatus = "Healthy";
                        else
                            metrics.OverallStatus = "Unknown";

                        var recs = metricValues.Values.Select(v => v.Recommendation).Where(r => !string.IsNullOrEmpty(r)).ToList();
                        if (recs.Contains("Undersized"))
                            metrics.Recommendation = "Undersized";
                        else if (recs.Contains("Oversized"))
                            metrics.Recommendation = "Oversized";
                        else if (recs.Any(r => r == "Right-sized"))
                            metrics.Recommendation = "Right-sized";
                        else
                            metrics.Recommendation = "Unknown";

                        withData++;
                    }
                    else
                    {
                        metrics.OverallStatus = "Unknown";
                        metrics.Recommendation = "Unknown";
                        metrics.StatusDetails = "No metric data available";
                    }

                    synced++;

                    // Update progress every 10 devices
                    if (synced % 10 == 0)
                    {
                        await db.SaveChangesAsync();
                        if (syncStatus != null)
                        {
                            syncStatus.PerformanceSyncProgress = synced;
                            syncStatus.PerformanceDevicesWithData = withData;
                            await db.SaveChangesAsync();
                        }
                        _logger.LogInformation("Progress: {Synced}/{Total} devices processed", synced, totalDevices);
                    }

                    await Task.Delay(200); // Rate limiting
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error syncing device {DeviceId}", device.Id);
                    errors.Add($"{device.DisplayName}: {ex.Message}");
                    synced++;
                }
            }

            await db.SaveChangesAsync();

            // Update final status
            if (syncStatus != null)
            {
                syncStatus.Status = "Completed";
                syncStatus.PerformanceSyncProgress = synced;
                syncStatus.PerformanceDevicesWithData = withData;
                syncStatus.LastSyncCompleted = DateTime.UtcNow;
                syncStatus.ErrorMessage = errors.Any() ? string.Join("; ", errors.Take(5)) : null;
                await db.SaveChangesAsync();
            }

            _logger.LogInformation("Performance V2 sync completed: {Synced}/{Total} devices, {WithData} with data",
                synced, totalDevices, withData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Performance V2 sync failed for customer {CustomerId}", custId);
            if (syncStatus != null)
            {
                syncStatus.Status = "Error";
                syncStatus.ErrorMessage = ex.Message;
                await db.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// Match device to resource type based on datasources
    /// </summary>
    private LMResourceType? MatchResourceType(List<string> deviceDatasources, List<LMResourceType> resourceTypes)
    {
        foreach (var rt in resourceTypes)
        {
            var patterns = rt.GetDetectionPatterns();
            if (patterns.Length == 0) continue;

            // Check if any detection pattern matches any datasource
            if (patterns.Any(pattern =>
                deviceDatasources.Any(ds => ds.Contains(pattern, StringComparison.OrdinalIgnoreCase))))
            {
                return rt;
            }
        }
        return null;
    }

    /// <summary>
    /// Calculate metric status based on thresholds
    /// </summary>
    private string? CalculateMetricStatus(decimal? value, LMMetricMapping mapping)
    {
        if (!value.HasValue) return null;

        var val = value.Value;
        
        if (mapping.InvertThreshold)
        {
            // Lower is worse (e.g., free space)
            if (mapping.CriticalThreshold.HasValue && val <= mapping.CriticalThreshold)
                return "Critical";
            if (mapping.WarningThreshold.HasValue && val <= mapping.WarningThreshold)
                return "Warning";
        }
        else
        {
            // Higher is worse (e.g., CPU usage)
            if (mapping.CriticalThreshold.HasValue && val >= mapping.CriticalThreshold)
                return "Critical";
            if (mapping.WarningThreshold.HasValue && val >= mapping.WarningThreshold)
                return "Warning";
        }

        return "Healthy";
    }

    /// <summary>
    /// Calculate right-sizing recommendation
    /// </summary>
    private string? CalculateMetricRecommendation(decimal? value, LMMetricMapping mapping)
    {
        if (!value.HasValue) return null;
        if (!mapping.OversizedBelow.HasValue && !mapping.UndersizedAbove.HasValue) return null;

        var val = value.Value;

        if (mapping.OversizedBelow.HasValue && val < mapping.OversizedBelow)
            return "Oversized";
        if (mapping.UndersizedAbove.HasValue && val > mapping.UndersizedAbove)
            return "Undersized";

        return "Right-sized";
    }

    /// <summary>
    /// Fetch metric data from LogicMonitor
    /// Supports both direct datapoint values AND calculated metrics (e.g., percentage from Free/Total)
    /// </summary>
    private async Task<(double avg, double max)?> FetchMetricDataAsync(
        LogicMonitorService lmService,
        int deviceId,
        int hdsId,
        string[] datapointNames,
        DateTime start,
        DateTime end,
        string metricName = "Unknown")
    {
        try
        {
            var instances = await lmService.GetDatasourceInstancesAsync(deviceId, hdsId);
            if (instances?.Items == null || !instances.Items.Any())
            {
                _logger.LogDebug("Device {DeviceId} {Metric}: No instances found for hdsId {HdsId}", deviceId, metricName, hdsId);
                return null;
            }

            // For most metrics, use the first instance
            // For Disk metrics with multiple instances, we handle this specially in ProcessSyncWithDbAsync
            var instance = instances.Items.First();
            
            _logger.LogDebug("Device {DeviceId} {Metric}: Found {Count} instances, using: {InstanceName}",
                deviceId, metricName, instances.Items.Count, instance.DisplayName);

            var data = await lmService.GetInstanceDataAsync(deviceId, hdsId, instance.Id, start, end);
            
            if (data?.DataPoints == null)
            {
                _logger.LogDebug("Device {DeviceId} {Metric}: No DataPoints in response", deviceId, metricName);
                return null;
            }
            
            if (!data.Values.HasValue ||
                data.Values.Value.ValueKind != System.Text.Json.JsonValueKind.Array ||
                data.Values.Value.GetArrayLength() == 0)
            {
                _logger.LogDebug("Device {DeviceId} {Metric}: No values in response. DataPoints: [{DataPoints}]",
                    deviceId, metricName, string.Join(", ", data.DataPoints));
                return null;
            }

            // Get actual number of data columns available
            var actualColumnCount = data.GetActualDataColumnCount();
            _logger.LogDebug("Device {DeviceId} {Metric}: DataPoints listed: {Listed}, Actual data columns: {Actual}",
                deviceId, metricName, data.DataPoints.Count, actualColumnCount);

            // ================================================================
            // SPECIAL HANDLING: Check for calculated metrics (CALC: prefix)
            // Format: "CALC:MEMORY" or "CALC:DISK"
            // ================================================================
            var calcPattern = datapointNames.FirstOrDefault(p => p.StartsWith("CALC:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(calcPattern))
            {
                var result = TryCalculateMetric(data, calcPattern, deviceId, metricName, actualColumnCount);
                if (result.HasValue)
                    return result;
            }

            // ================================================================
            // STANDARD: Try to find a matching datapoint that's within range
            // ================================================================
            for (int i = 0; i < data.DataPoints.Count; i++)
            {
                var dpName = data.DataPoints[i];
                
                // Check if this datapoint matches any of our patterns (skip CALC patterns)
                if (!datapointNames.Where(p => !p.StartsWith("CALC:")).Any(p => dpName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    continue;
                
                // Check if this datapoint index is within the actual data range
                if (i >= actualColumnCount)
                {
                    _logger.LogDebug("Device {DeviceId} {Metric}: Matched '{DpName}' at index {Index} but only {Actual} columns available - skipping",
                        deviceId, metricName, dpName, i, actualColumnCount);
                    continue;
                }
                    
                _logger.LogDebug("Device {DeviceId} {Metric}: Matched datapoint '{DpName}' at index {Index} (within range)",
                    deviceId, metricName, dpName, i);
                
                var values = data.GetDatapointValues(i);
                if (values != null && values.Any())
                {
                    var avg = values.Average();
                    var max = values.Max();
                    
                    // ================================================================
                    // VALIDATION: Check if values look like percentages (0-100 range)
                    // If not, the datapoint is returning raw counters, not percentages
                    // ================================================================
                    if (avg > 100 || max > 100)
                    {
                        _logger.LogWarning("Device {DeviceId} {Metric}: Values appear to be raw counters, not percentages. Avg={Avg:F0}, Max={Max:F0}. Skipping this datapoint.",
                            deviceId, metricName, avg, max);
                        continue; // Try next matching datapoint
                    }
                    
                    _logger.LogDebug("Device {DeviceId} {Metric}: Got {Count} values, Avg={Avg:F2}%, Max={Max:F2}%",
                        deviceId, metricName, values.Count, avg, max);
                    return (avg, max);
                }
                else
                {
                    _logger.LogDebug("Device {DeviceId} {Metric}: Datapoint '{DpName}' matched but GetDatapointValues returned empty",
                        deviceId, metricName, dpName);
                }
            }
            
            // Log what we looked for vs what was available
            _logger.LogWarning("Device {DeviceId} {Metric}: No usable datapoint found (within 0-100% range). Patterns: [{Patterns}], Available (within range): [{Available}]",
                deviceId, metricName, string.Join(", ", datapointNames), 
                string.Join(", ", data.DataPoints.Take(actualColumnCount)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching metric data for device {DeviceId} {Metric}", deviceId, metricName);
        }

        return null;
    }

    /// <summary>
    /// Try to calculate a metric from multiple datapoints using a formula
    /// Supports: CALC:MEMORY (100 - Free/Total*100) and CALC:DISK (100 - Free/Capacity*100)
    /// </summary>
    private (double avg, double max)? TryCalculateMetric(
        LMInstanceDataResponse data, 
        string calcPattern, 
        int deviceId, 
        string metricName,
        int actualColumnCount)
    {
        try
        {
            var formula = calcPattern.Substring(5).ToUpperInvariant(); // Remove "CALC:" prefix
            
            _logger.LogDebug("Device {DeviceId} {Metric}: Attempting calculated metric with formula: {Formula}",
                deviceId, metricName, formula);

            // ================================================================
            // MEMORY: First try MemoryUtilizationPercent directly, then calculate
            // ================================================================
            if (formula == "MEMORY" || formula.Contains("FREEPHYSICALMEMORY"))
            {
                _logger.LogInformation("Device {DeviceId} {Metric}: Starting MEMORY calculation. DataPoints count={DpCount}, actualColumnCount={ActualCols}",
                    deviceId, metricName, data.DataPoints?.Count ?? 0, actualColumnCount);
                
                // First try: Use MemoryUtilizationPercent directly if available
                var memUtilIdx = FindDatapointIndex(data.DataPoints, "MemoryUtilizationPercent", actualColumnCount);
                if (memUtilIdx >= 0)
                {
                    var memValues = data.GetDatapointValues(memUtilIdx);
                    if (memValues != null && memValues.Any())
                    {
                        var validPercents = memValues.Where(v => v >= 0 && v <= 100).ToList();
                        if (validPercents.Any())
                        {
                            var avg = validPercents.Average();
                            var max = validPercents.Max();
                            _logger.LogInformation("Device {DeviceId} {Metric}: Using MemoryUtilizationPercent directly. Avg={Avg:F1}%, Max={Max:F1}%",
                                deviceId, metricName, avg, max);
                            return (avg, max);
                        }
                    }
                }
                _logger.LogDebug("Device {DeviceId} {Metric}: MemoryUtilizationPercent not available at valid index, trying calculation",
                    deviceId, metricName);
                
                // Fallback: Calculate from FreePhysicalMemory / TotalVisibleMemorySize
                var freeIdx = FindDatapointIndex(data.DataPoints, "FreePhysicalMemory", actualColumnCount);
                var totalIdx = FindDatapointIndex(data.DataPoints, "TotalVisibleMemorySize", actualColumnCount);
                
                _logger.LogInformation("Device {DeviceId} {Metric}: MEMORY indices - FreePhysicalMemory={FreeIdx}, TotalVisibleMemorySize={TotalIdx}",
                    deviceId, metricName, freeIdx, totalIdx);
                
                if (freeIdx >= 0 && totalIdx >= 0)
                {
                    var freeValues = data.GetDatapointValues(freeIdx);
                    var totalValues = data.GetDatapointValues(totalIdx);
                    
                    _logger.LogInformation("Device {DeviceId} {Metric}: MEMORY values - FreeValues count={FreeCount}, TotalValues count={TotalCount}",
                        deviceId, metricName, freeValues?.Count ?? 0, totalValues?.Count ?? 0);
                    
                    if (freeValues != null && freeValues.Any() && totalValues != null && totalValues.Any())
                    {
                        // Log sample values for debugging
                        _logger.LogInformation("Device {DeviceId} {Metric}: MEMORY sample - Free[0]={Free}, Total[0]={Total}",
                            deviceId, metricName, freeValues.First(), totalValues.First());
                        
                        // Calculate memory usage percentage for each time point
                        var minCount = Math.Min(freeValues.Count, totalValues.Count);
                        var percentages = new List<double>();
                        
                        for (int i = 0; i < minCount; i++)
                        {
                            var total = totalValues[i];
                            var free = freeValues[i];
                            if (total > 0)
                            {
                                var usedPercent = 100.0 - (free / total * 100.0);
                                if (usedPercent >= 0 && usedPercent <= 100)
                                    percentages.Add(usedPercent);
                            }
                        }
                        
                        _logger.LogInformation("Device {DeviceId} {Metric}: MEMORY calculated {Count} valid percentages from {MinCount} data points",
                            deviceId, metricName, percentages.Count, minCount);
                        
                        if (percentages.Any())
                        {
                            var avg = percentages.Average();
                            var max = percentages.Max();
                            _logger.LogInformation("Device {DeviceId} {Metric}: CALCULATED from Free/Total. Avg={Avg:F1}%, Max={Max:F1}%",
                                deviceId, metricName, avg, max);
                            return (avg, max);
                        }
                        else
                        {
                            _logger.LogWarning("Device {DeviceId} {Metric}: MEMORY calculation produced zero valid percentages",
                                deviceId, metricName);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Device {DeviceId} {Metric}: MEMORY GetDatapointValues returned empty - Free is null/empty: {FreeEmpty}, Total is null/empty: {TotalEmpty}",
                            deviceId, metricName, freeValues == null || !freeValues.Any(), totalValues == null || !totalValues.Any());
                    }
                }
                else
                {
                    _logger.LogWarning("Device {DeviceId} {Metric}: MEMORY indices not found - FreePhysicalMemory={FreeIdx}, TotalVisibleMemorySize={TotalIdx}. Available datapoints: [{Datapoints}]",
                        deviceId, metricName, freeIdx, totalIdx, string.Join(", ", data.DataPoints?.Take(10) ?? Array.Empty<string>()));
                }
            }
            
            // ================================================================
            // DISK CALCULATION: 100 - (FreeSpace / Capacity * 100)
            // ================================================================
            if (formula == "DISK" || formula.Contains("FREESPACE"))
            {
                var freeIdx = FindDatapointIndex(data.DataPoints, "FreeSpace", actualColumnCount);
                var capacityIdx = FindDatapointIndex(data.DataPoints, "Capacity", actualColumnCount);
                
                if (freeIdx >= 0 && capacityIdx >= 0)
                {
                    var freeValues = data.GetDatapointValues(freeIdx);
                    var capacityValues = data.GetDatapointValues(capacityIdx);
                    
                    if (freeValues.Any() && capacityValues.Any())
                    {
                        var minCount = Math.Min(freeValues.Count, capacityValues.Count);
                        var percentages = new List<double>();
                        
                        for (int i = 0; i < minCount; i++)
                        {
                            var capacity = capacityValues[i];
                            var free = freeValues[i];
                            if (capacity > 0)
                            {
                                var usedPercent = 100.0 - (free / capacity * 100.0);
                                if (usedPercent >= 0 && usedPercent <= 100)
                                    percentages.Add(usedPercent);
                            }
                        }
                        
                        if (percentages.Any())
                        {
                            var avg = percentages.Average();
                            var max = percentages.Max();
                            _logger.LogInformation("Device {DeviceId} {Metric}: CALCULATED from Free/Capacity. Avg={Avg:F1}%, Max={Max:F1}%",
                                deviceId, metricName, avg, max);
                            return (avg, max);
                        }
                    }
                }
                
                _logger.LogDebug("Device {DeviceId} {Metric}: DISK calculation failed - FreeSpace index={FreeIdx}, Capacity index={CapIdx}",
                    deviceId, metricName, freeIdx, capacityIdx);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device {DeviceId} {Metric}: Calculated metric failed", deviceId, metricName);
        }
        
        return null;
    }

    /// <summary>
    /// Find datapoint index by name (partial match, case-insensitive)
    /// </summary>
    private int FindDatapointIndex(List<string>? datapoints, string searchName, int maxIndex)
    {
        if (datapoints == null) return -1;
        
        for (int i = 0; i < Math.Min(datapoints.Count, maxIndex); i++)
        {
            if (datapoints[i].Contains(searchName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Fetch disk metrics for ALL instances (all drives like C:, D:, etc.)
    /// Returns a dictionary with drive letter as key and (avg, max) as value
    /// </summary>
    private async Task<Dictionary<string, (double avg, double max)>> FetchAllDiskMetricsAsync(
        LogicMonitorService lmService,
        int deviceId,
        int hdsId,
        DateTime start,
        DateTime end)
    {
        var results = new Dictionary<string, (double avg, double max)>();
        
        try
        {
            var instances = await lmService.GetDatasourceInstancesAsync(deviceId, hdsId);
            if (instances?.Items == null || !instances.Items.Any())
            {
                _logger.LogDebug("Device {DeviceId} AllDisks: No instances found", deviceId);
                return results;
            }

            _logger.LogInformation("Device {DeviceId} AllDisks: Processing {Count} disk instances",
                deviceId, instances.Items.Count);

            foreach (var instance in instances.Items)
            {
                try
                {
                    // Get drive letter from DisplayName (e.g., "C:\" or "D:\")
                    var driveLetter = instance.DisplayName?.TrimEnd('\\', '/') ?? instance.WildValue?.TrimEnd('\\', '/') ?? "Unknown";
                    if (string.IsNullOrEmpty(driveLetter)) driveLetter = $"Disk{instance.Id}";
                    
                    var data = await lmService.GetInstanceDataAsync(deviceId, hdsId, instance.Id, start, end);
                    
                    if (data?.DataPoints == null || !data.Values.HasValue ||
                        data.Values.Value.ValueKind != System.Text.Json.JsonValueKind.Array ||
                        data.Values.Value.GetArrayLength() == 0)
                    {
                        _logger.LogDebug("Device {DeviceId} Disk {Drive}: No data available", deviceId, driveLetter);
                        continue;
                    }

                    var actualColumnCount = data.GetActualDataColumnCount();
                    
                    // First try: Use PercentUsed directly if available (most accurate)
                    var percentUsedIdx = FindDatapointIndex(data.DataPoints, "PercentUsed", actualColumnCount);
                    if (percentUsedIdx >= 0)
                    {
                        var percentValues = data.GetDatapointValues(percentUsedIdx);
                        if (percentValues != null && percentValues.Any())
                        {
                            var validPercents = percentValues.Where(v => v >= 0 && v <= 100).ToList();
                            if (validPercents.Any())
                            {
                                var avg = validPercents.Average();
                                var max = validPercents.Max();
                                results[driveLetter] = (avg, max);
                                _logger.LogInformation("Device {DeviceId} Disk {Drive}: Using PercentUsed directly. Avg={Avg:F1}%, Max={Max:F1}%",
                                    deviceId, driveLetter, avg, max);
                                continue; // Move to next disk instance
                            }
                        }
                    }
                    
                    // Fallback: Calculate from FreeSpace / Capacity
                    var freeIdx = FindDatapointIndex(data.DataPoints, "FreeSpace", actualColumnCount);
                    var capacityIdx = FindDatapointIndex(data.DataPoints, "Capacity", actualColumnCount);
                    
                    if (freeIdx >= 0 && capacityIdx >= 0)
                    {
                        var freeValues = data.GetDatapointValues(freeIdx);
                        var capacityValues = data.GetDatapointValues(capacityIdx);
                        
                        _logger.LogInformation("Device {DeviceId} Disk {Drive}: FreeIdx={FreeIdx}, CapIdx={CapIdx}, FreeCount={FreeCount}, CapCount={CapCount}",
                            deviceId, driveLetter, freeIdx, capacityIdx, freeValues?.Count ?? 0, capacityValues?.Count ?? 0);
                        
                        if (freeValues != null && freeValues.Any() && capacityValues != null && capacityValues.Any())
                        {
                            // Log sample raw values for debugging
                            _logger.LogInformation("Device {DeviceId} Disk {Drive}: RAW VALUES - Capacity[0]={Cap}, FreeSpace[0]={Free}",
                                deviceId, driveLetter, capacityValues.First(), freeValues.First());
                            
                            var minCount = Math.Min(freeValues.Count, capacityValues.Count);
                            var percentages = new List<double>();
                            
                            // Detect unit mismatch: if Capacity >> FreeSpace by factor of ~1 billion,
                            // Capacity is in bytes and FreeSpace is in GB
                            var sampleCap = capacityValues.First();
                            var sampleFree = freeValues.First();
                            var needsConversion = sampleCap > 1_000_000_000 && sampleFree < 100_000;
                            
                            if (needsConversion)
                            {
                                _logger.LogInformation("Device {DeviceId} Disk {Drive}: Unit mismatch detected - converting Capacity from bytes to GB",
                                    deviceId, driveLetter);
                            }
                            
                            for (int i = 0; i < minCount; i++)
                            {
                                var capacity = capacityValues[i];
                                var free = freeValues[i];
                                
                                // Convert capacity from bytes to GB if needed
                                if (needsConversion)
                                {
                                    capacity = capacity / 1073741824.0; // bytes to GB
                                }
                                
                                if (capacity > 0)
                                {
                                    var usedPercent = 100.0 - (free / capacity * 100.0);
                                    
                                    // Log first calculation for debugging
                                    if (i == 0)
                                    {
                                        _logger.LogInformation("Device {DeviceId} Disk {Drive}: CALC - 100 - ({Free}/{Cap}*100) = {Result:F1}%",
                                            deviceId, driveLetter, free, capacity, usedPercent);
                                    }
                                    
                                    if (usedPercent >= 0 && usedPercent <= 100)
                                        percentages.Add(usedPercent);
                                }
                            }
                            
                            _logger.LogInformation("Device {DeviceId} Disk {Drive}: Calculated {Count} valid percentages from {Total} data points",
                                deviceId, driveLetter, percentages.Count, minCount);
                            
                            if (percentages.Any())
                            {
                                var avg = percentages.Average();
                                var max = percentages.Max();
                                results[driveLetter] = (avg, max);
                                _logger.LogInformation("Device {DeviceId} Disk {Drive}: RESULT Avg={Avg:F1}%, Max={Max:F1}%",
                                    deviceId, driveLetter, avg, max);
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Device {DeviceId} Disk {Drive}: GetDatapointValues returned empty",
                                deviceId, driveLetter);
                        }
                    }
                    else
                    {
                        _logger.LogDebug("Device {DeviceId} Disk {Drive}: FreeSpace/Capacity datapoints not found",
                            deviceId, driveLetter);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Device {DeviceId}: Error processing disk instance {InstanceId}",
                        deviceId, instance.Id);
                }
                
                await Task.Delay(50); // Rate limiting between instances
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device {DeviceId}: Error fetching all disk metrics", deviceId);
        }
        
        return results;
    }

    /// <summary>
    /// Get LogicMonitor service for customer (using Key Vault credentials)
    /// </summary>
    private async Task<LogicMonitorService?> GetLogicMonitorServiceAsync(Guid customerId, TievaDbContext db)
    {
        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            
            // Try per-customer credentials first
            try
            {
                var prefix = $"LM-{customerId}";
                var companyTask = client.GetSecretAsync($"{prefix}-Company");
                var accessIdTask = client.GetSecretAsync($"{prefix}-AccessId");
                var accessKeyTask = client.GetSecretAsync($"{prefix}-AccessKey");
                
                await Task.WhenAll(companyTask, accessIdTask, accessKeyTask);
                
                _logger.LogInformation("Using per-customer LM credentials for {CustomerId}", customerId);
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
            
            // Fall back to global credentials
            var globalCompanyTask = client.GetSecretAsync("LM-Company");
            var globalAccessIdTask = client.GetSecretAsync("LM-AccessId");
            var globalAccessKeyTask = client.GetSecretAsync("LM-AccessKey");
            
            await Task.WhenAll(globalCompanyTask, globalAccessIdTask, globalAccessKeyTask);
            
            _logger.LogInformation("Using global LM credentials, company: {Company}", globalCompanyTask.Result.Value.Value);
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

    /// <summary>
    /// Auto-fix resource type configuration issues
    /// Detects and fixes mismatches between HasPerformanceMetrics and actual mappings
    /// </summary>
    [Function("AutoFixResourceTypeConfig")]
    public async Task<HttpResponseData> AutoFixResourceTypeConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/admin/auto-fix")] 
        HttpRequestData req)
    {
        _logger.LogInformation("Auto-fix resource type configuration started");

        try
        {
            // Load all resource types with their mapping counts
            var resourceTypes = await _db.LMResourceTypes
                .Include(rt => rt.MetricMappings)
                .Where(rt => rt.IsActive)
                .ToListAsync();

            var fixes = new List<object>();
            var warnings = new List<object>();

            // Known non-performance types (metadata, logging, status-only)
            var nonPerformancePatterns = new[] {
                "Metadata", "LogUsage", "ServiceLimits", "Authentication", 
                "Capacity", "BackupProtected", "BackupJob", "Health"
            };

            // Known performance types that should have mappings
            var corePerformanceTypes = new[] {
                "WindowsServer", "LinuxServer", "AzureVM", "HyperV",
                "AzureSQL", "AppService", "AppServicePlan", "AzureStorage",
                "AzureDisk", "Redis", "AzureNetwork", "AzureFileStorage"
            };

            foreach (var rt in resourceTypes)
            {
                var activeMappings = rt.MetricMappings.Count(m => m.IsActive);
                var totalMappings = rt.MetricMappings.Count;

                // Issue 1: HasPerformanceMetrics=true but no active mappings
                if (rt.HasPerformanceMetrics && activeMappings == 0)
                {
                    // Check if this looks like a non-performance type
                    var looksLikeNonPerf = nonPerformancePatterns.Any(p => 
                        rt.Code.Contains(p, StringComparison.OrdinalIgnoreCase));

                    if (looksLikeNonPerf || !corePerformanceTypes.Contains(rt.Code))
                    {
                        // Auto-fix: disable HasPerformanceMetrics
                        rt.HasPerformanceMetrics = false;
                        rt.UpdatedAt = DateTime.UtcNow;
                        fixes.Add(new
                        {
                            action = "disabled_performance_metrics",
                            resourceType = rt.Code,
                            reason = "HasPerformanceMetrics=true but no active mappings and not a core performance type",
                            totalMappingsInDb = totalMappings
                        });
                    }
                    else
                    {
                        // Core type with no mappings - warn, don't auto-fix
                        warnings.Add(new
                        {
                            issue = "core_type_no_mappings",
                            resourceType = rt.Code,
                            displayName = rt.DisplayName,
                            message = "Core performance type has no active mappings - needs manual configuration",
                            totalMappingsInDb = totalMappings,
                            suggestion = totalMappings > 0 
                                ? $"Re-enable some of the {totalMappings} disabled mappings" 
                                : "Add metric mappings for this resource type"
                        });
                    }
                }

                // Issue 2: HasPerformanceMetrics=false but HAS active mappings (waste)
                if (!rt.HasPerformanceMetrics && activeMappings > 0)
                {
                    // Check if it's a type that should have performance metrics
                    var looksLikePerf = corePerformanceTypes.Contains(rt.Code) ||
                        rt.Code.Contains("VM", StringComparison.OrdinalIgnoreCase) ||
                        rt.Code.Contains("Server", StringComparison.OrdinalIgnoreCase) ||
                        rt.Code.Contains("SQL", StringComparison.OrdinalIgnoreCase);

                    if (looksLikePerf)
                    {
                        // Auto-fix: enable HasPerformanceMetrics
                        rt.HasPerformanceMetrics = true;
                        rt.UpdatedAt = DateTime.UtcNow;
                        fixes.Add(new
                        {
                            action = "enabled_performance_metrics",
                            resourceType = rt.Code,
                            reason = $"HasPerformanceMetrics=false but has {activeMappings} active mappings",
                            activeMappings = activeMappings
                        });
                    }
                    else
                    {
                        // Non-core type with mappings - warn
                        warnings.Add(new
                        {
                            issue = "unused_mappings",
                            resourceType = rt.Code,
                            displayName = rt.DisplayName,
                            message = $"Has {activeMappings} active mappings but HasPerformanceMetrics=false (mappings not being used)",
                            suggestion = "Either enable HasPerformanceMetrics or disable the mappings"
                        });
                    }
                }
            }

            // Save fixes
            if (fixes.Any())
            {
                await _db.SaveChangesAsync();
            }

            // Get summary stats
            var summary = await _db.LMResourceTypes
                .Where(rt => rt.IsActive)
                .Select(rt => new
                {
                    rt.Code,
                    rt.HasPerformanceMetrics,
                    ActiveMappings = rt.MetricMappings.Count(m => m.IsActive)
                })
                .ToListAsync();

            var healthyTypes = summary.Count(s => 
                (s.HasPerformanceMetrics && s.ActiveMappings > 0) || 
                (!s.HasPerformanceMetrics && s.ActiveMappings == 0));

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                fixesApplied = fixes.Count,
                warningsFound = warnings.Count,
                fixes = fixes,
                warnings = warnings,
                summary = new
                {
                    totalActiveTypes = summary.Count,
                    healthyConfigured = healthyTypes,
                    withPerformanceMetrics = summary.Count(s => s.HasPerformanceMetrics),
                    withActiveMappings = summary.Count(s => s.ActiveMappings > 0)
                },
                nextSteps = warnings.Any() 
                    ? "Review warnings above and configure mappings for core types that need them"
                    : "Configuration looks good! Run a sync to update device statuses."
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Auto-fix failed");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Get resource type health/configuration status
    /// </summary>
    [Function("GetResourceTypeHealth")]
    public async Task<HttpResponseData> GetResourceTypeHealth(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/admin/health")] 
        HttpRequestData req)
    {
        try
        {
            // Get resource types with mapping counts
            var types = await _db.LMResourceTypes
                .Where(rt => rt.IsActive)
                .Select(rt => new
                {
                    id = rt.Id,
                    code = rt.Code,
                    displayName = rt.DisplayName,
                    category = rt.Category,
                    hasPerformanceMetrics = rt.HasPerformanceMetrics,
                    sortOrder = rt.SortOrder,
                    activeMappings = rt.MetricMappings.Count(m => m.IsActive),
                    totalMappings = rt.MetricMappings.Count
                })
                .OrderBy(rt => rt.sortOrder)
                .ToListAsync();

            // Get device counts per type
            var deviceCounts = await _db.LMDeviceMetricsV2
                .GroupBy(d => d.ResourceTypeCode)
                .Select(g => new { typeCode = g.Key, count = g.Count() })
                .ToListAsync();

            // Get unknown/problem device counts
            var problemDevices = await _db.LMDeviceMetricsV2
                .Where(d => d.OverallStatus == "Unknown" || d.OverallStatus == null)
                .GroupBy(d => new { d.ResourceTypeCode, d.StatusDetails })
                .Select(g => new { 
                    typeCode = g.Key.ResourceTypeCode, 
                    reason = g.Key.StatusDetails,
                    count = g.Count() 
                })
                .ToListAsync();

            // Build health report
            var healthReport = types.Select(t =>
            {
                var devices = deviceCounts.FirstOrDefault(d => d.typeCode == t.code)?.count ?? 0;
                var problems = problemDevices.Where(p => p.typeCode == t.code).ToList();
                
                var status = "healthy";
                var issues = new List<string>();

                if (t.hasPerformanceMetrics && t.activeMappings == 0)
                {
                    status = "error";
                    issues.Add("HasPerformanceMetrics=true but no active mappings");
                }
                else if (!t.hasPerformanceMetrics && t.activeMappings > 0)
                {
                    status = "warning";
                    issues.Add($"{t.activeMappings} mappings exist but not being used");
                }
                
                if (problems.Any(p => p.reason == "No metric data available"))
                {
                    status = status == "healthy" ? "warning" : status;
                    issues.Add($"{problems.First(p => p.reason == "No metric data available").count} devices have no metric data");
                }

                return new
                {
                    id = t.id,
                    code = t.code,
                    displayName = t.displayName,
                    category = t.category,
                    hasPerformanceMetrics = t.hasPerformanceMetrics,
                    activeMappings = t.activeMappings,
                    totalMappings = t.totalMappings,
                    deviceCount = devices,
                    problemDevices = problems.Sum(p => p.count),
                    status,
                    issues
                };
            }).ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                generatedAt = DateTime.UtcNow,
                summary = new
                {
                    totalTypes = types.Count,
                    healthy = healthReport.Count(h => h.status == "healthy"),
                    warnings = healthReport.Count(h => h.status == "warning"),
                    errors = healthReport.Count(h => h.status == "error"),
                    totalDevices = deviceCounts.Sum(d => d.count),
                    totalProblemDevices = problemDevices.Sum(p => p.count)
                },
                resourceTypes = healthReport
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get resource type health failed");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Toggle HasPerformanceMetrics for a resource type
    /// </summary>
    [Function("ToggleResourceTypePerformance")]
    public async Task<HttpResponseData> ToggleResourceTypePerformance(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/resource-types/{id}/toggle-performance")] 
        HttpRequestData req, string id)
    {
        if (!int.TryParse(id, out var rtId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid resource type ID");
            return badRequest;
        }

        var resourceType = await _db.LMResourceTypes.FindAsync(rtId);
        if (resourceType == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Resource type not found");
            return notFound;
        }

        resourceType.HasPerformanceMetrics = !resourceType.HasPerformanceMetrics;
        resourceType.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            success = true,
            resourceType = resourceType.Code,
            hasPerformanceMetrics = resourceType.HasPerformanceMetrics,
            message = resourceType.HasPerformanceMetrics 
                ? "Performance metrics enabled - ensure mappings are configured"
                : "Performance metrics disabled - devices will show as Unknown"
        });
        return response;
    }

    /// <summary>
    /// Diagnose metric extraction for a single device - shows exactly why metrics are/aren't captured
    /// </summary>
    [Function("DiagnoseSingleDevice")]
    public async Task<HttpResponseData> DiagnoseSingleDevice(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "v2/performance/customers/{customerId}/devices/{deviceId}/diagnose")] 
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

        var lmService = await GetLogicMonitorServiceAsync(custId, _db);
        if (lmService == null)
        {
            var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await err.WriteStringAsync("LogicMonitor service unavailable");
            return err;
        }

        try
        {
            var diagnostics = new List<object>();
            var now = DateTime.UtcNow;
            var start7Day = now.AddDays(-7);

            // Get device info from DB
            var deviceMetrics = await _db.LMDeviceMetricsV2
                .FirstOrDefaultAsync(d => d.CustomerId == custId && d.DeviceId == devId);

            var deviceInfo = new
            {
                deviceId = devId,
                deviceName = deviceMetrics?.DeviceName ?? "Unknown",
                currentResourceType = deviceMetrics?.ResourceTypeCode,
                currentMetricsJson = deviceMetrics?.MetricsJson,
                overallStatus = deviceMetrics?.OverallStatus,
                statusDetails = deviceMetrics?.StatusDetails,
                lastSyncedAt = deviceMetrics?.LastSyncedAt
            };

            // Get datasources from LogicMonitor
            var datasources = await lmService.GetDeviceDatasourcesAsync(devId);
            if (datasources?.Items == null || !datasources.Items.Any())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { device = deviceInfo, error = "No datasources found from LogicMonitor" });
                return response;
            }

            var allDsNames = datasources.Items.Select(ds => ds.DataSourceName).Where(n => !string.IsNullOrEmpty(n)).ToList();

            // Load resource types with mappings
            var resourceTypes = await _db.LMResourceTypes
                .Include(rt => rt.MetricMappings.Where(m => m.IsActive))
                .Where(rt => rt.IsActive)
                .OrderBy(rt => rt.SortOrder)
                .ToListAsync();

            // Match resource type
            var matchedType = MatchResourceType(allDsNames!, resourceTypes);
            
            var typeInfo = matchedType != null ? new
            {
                code = matchedType.Code,
                displayName = matchedType.DisplayName,
                hasPerformanceMetrics = matchedType.HasPerformanceMetrics,
                activeMappings = matchedType.MetricMappings.Count,
                detectionPatterns = matchedType.GetDetectionPatterns()
            } : null;

            if (matchedType == null || !matchedType.HasPerformanceMetrics || !matchedType.MetricMappings.Any())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    device = deviceInfo,
                    availableDatasources = allDsNames,
                    matchedResourceType = typeInfo,
                    diagnosis = matchedType == null 
                        ? "No resource type matched" 
                        : !matchedType.HasPerformanceMetrics 
                            ? "Resource type has HasPerformanceMetrics=false" 
                            : "Resource type has no active metric mappings"
                });
                return response;
            }

            // Diagnose each metric mapping
            var metricDiagnostics = new List<object>();

            foreach (var mapping in matchedType.MetricMappings.OrderBy(m => m.SortOrder))
            {
                var dsPatterns = mapping.GetDatasourcePatterns();
                var dpPatterns = mapping.GetDatapointPatterns();

                var diag = new Dictionary<string, object>
                {
                    ["metricName"] = mapping.MetricName,
                    ["datasourcePatterns"] = dsPatterns,
                    ["datapointPatterns"] = dpPatterns
                };

                // Find matching datasource on device
                var matchedDs = datasources.Items.FirstOrDefault(ds =>
                    dsPatterns.Any(p => ds.DataSourceName?.Contains(p, StringComparison.OrdinalIgnoreCase) == true));

                if (matchedDs == null)
                {
                    diag["status"] = "NO_DATASOURCE_MATCH";
                    diag["availableDatasourcesChecked"] = datasources.Items
                        .Select(ds => ds.DataSourceName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .Take(20)
                        .ToList();
                    metricDiagnostics.Add(diag);
                    continue;
                }

                diag["matchedDatasource"] = new { id = matchedDs.Id, name = matchedDs.DataSourceName };

                // Get instances
                var instances = await lmService.GetDatasourceInstancesAsync(devId, matchedDs.Id);
                if (instances?.Items == null || !instances.Items.Any())
                {
                    diag["status"] = "NO_INSTANCES";
                    metricDiagnostics.Add(diag);
                    continue;
                }

                diag["instanceCount"] = instances.Items.Count;
                diag["instances"] = instances.Items.Select(i => new { i.Id, i.DisplayName, i.WildValue }).Take(5).ToList();

                // Get data from first instance
                var instance = instances.Items.First();
                var data = await lmService.GetInstanceDataAsync(devId, matchedDs.Id, instance.Id, start7Day, now);

                if (data?.DataPoints == null)
                {
                    diag["status"] = "NO_DATA_RESPONSE";
                    metricDiagnostics.Add(diag);
                    continue;
                }

                diag["availableDatapoints"] = data.DataPoints;
                
                // Show actual available datapoints (within data range)
                var actualColCount = data.GetActualDataColumnCount();
                diag["actualDataColumns"] = actualColCount;
                diag["datapointsWithData"] = data.DataPoints.Take(actualColCount).ToList();

                // Check values
                var hasValues = data.Values.HasValue && 
                    data.Values.Value.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    data.Values.Value.GetArrayLength() > 0;

                if (!hasValues)
                {
                    diag["status"] = "NO_VALUES_IN_DATA";
                    metricDiagnostics.Add(diag);
                    continue;
                }

                diag["valueArrayLength"] = data.Values.Value.GetArrayLength();

                // Find matching datapoint
                int matchedDpIndex = -1;
                string? matchedDpName = null;
                for (int i = 0; i < data.DataPoints.Count; i++)
                {
                    var dpName = data.DataPoints[i];
                    if (dpPatterns.Any(p => dpName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    {
                        matchedDpIndex = i;
                        matchedDpName = dpName;
                        break;
                    }
                }

                if (matchedDpIndex < 0)
                {
                    diag["status"] = "NO_DATAPOINT_MATCH";
                    diag["patternsTried"] = dpPatterns;
                    diag["datapointsAvailable"] = data.DataPoints;
                    metricDiagnostics.Add(diag);
                    continue;
                }

                diag["matchedDatapoint"] = new { index = matchedDpIndex, name = matchedDpName };

                // Get values
                var values = data.GetDatapointValues(matchedDpIndex);
                if (values == null || !values.Any())
                {
                    diag["status"] = "NO_VALID_VALUES";
                    // Add raw sample values to diagnose why filtering failed
                    diag["rawSampleValues"] = data.GetRawDatapointSamples(matchedDpIndex, 5);
                    metricDiagnostics.Add(diag);
                    continue;
                }

                diag["status"] = "SUCCESS";
                diag["valueCount"] = values.Count;
                diag["avg"] = Math.Round(values.Average(), 2);
                diag["max"] = Math.Round(values.Max(), 2);
                diag["min"] = Math.Round(values.Min(), 2);
                diag["sampleValues"] = values.Take(5).Select(v => Math.Round(v, 2)).ToList();

                metricDiagnostics.Add(diag);
                await Task.Delay(100); // Rate limiting
            }

            var response2 = req.CreateResponse(HttpStatusCode.OK);
            await response2.WriteAsJsonAsync(new
            {
                device = deviceInfo,
                availableDatasources = allDsNames.Take(30),
                matchedResourceType = typeInfo,
                metricDiagnostics = metricDiagnostics,
                summary = new
                {
                    totalMappings = metricDiagnostics.Count,
                    successful = metricDiagnostics.Count(d => ((Dictionary<string, object>)d)["status"]?.ToString() == "SUCCESS"),
                    failed = metricDiagnostics.Count(d => ((Dictionary<string, object>)d)["status"]?.ToString() != "SUCCESS")
                }
            });
            return response2;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Diagnose failed for device {DeviceId}", devId);
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }

    /// <summary>
    /// Bulk enable/disable mappings for a resource type
    /// </summary>
    [Function("BulkToggleMappings")]
    public async Task<HttpResponseData> BulkToggleMappings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "v2/performance/resource-types/{id}/bulk-toggle-mappings")] 
        HttpRequestData req, string id)
    {
        if (!int.TryParse(id, out var rtId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid resource type ID");
            return badRequest;
        }

        try
        {
            var body = await req.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<JsonElement>(body ?? "{}");
            
            var enable = data.TryGetProperty("enable", out var enableProp) && enableProp.GetBoolean();
            var mappingIds = data.TryGetProperty("mappingIds", out var idsProp) 
                ? idsProp.EnumerateArray().Select(x => x.GetInt32()).ToList()
                : null;

            var query = _db.LMMetricMappings.Where(m => m.ResourceTypeId == rtId);
            
            // If specific IDs provided, filter to those
            if (mappingIds != null && mappingIds.Any())
            {
                query = query.Where(m => mappingIds.Contains(m.Id));
            }

            var mappings = await query.ToListAsync();
            
            foreach (var m in mappings)
            {
                m.IsActive = enable;
                m.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                resourceTypeId = rtId,
                action = enable ? "enabled" : "disabled",
                mappingsAffected = mappings.Count
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Bulk toggle mappings failed");
            var err = req.CreateResponse(HttpStatusCode.InternalServerError);
            await err.WriteStringAsync($"Error: {ex.Message}");
            return err;
        }
    }
}
