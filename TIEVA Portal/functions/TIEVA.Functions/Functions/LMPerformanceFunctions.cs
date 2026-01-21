using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

/// <summary>
/// Azure Functions for LogicMonitor Performance Metrics
/// Used for right-sizing recommendations and resource optimization
/// </summary>
public class LMPerformanceFunctions
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TievaDbContext _db;
    private readonly string _keyVaultUrl;

    // Fallback patterns if database has no mappings (will be used initially)
    // These are the SAME as what's seeded in the database
    // NOTE: Azure Monitor cloud metrics have LIMITED coverage - Memory % and Disk % require collector agent
    private static readonly Dictionary<string, string[]> FallbackPatterns = new()
    {
        ["CPU"] = new[] {
            // Windows/Linux Servers (with collector agent)
            "WinCPU", "Microsoft_Windows_CPU", "LinuxCPU", "snmp64_cpu",
            // Virtualization
            "VMware_vSphere_VMperformance", "ESXi_Host_CPU", "Hyper-V_Host_CPU",
            // Azure IaaS (via Azure Monitor)
            "Microsoft_Azure_VMs",
            // Azure PaaS with CPU metrics
            "Microsoft_Azure_AppServicePlan", "Microsoft_Azure_AppServices",
            "Microsoft_Azure_FunctionApps", "Microsoft_Azure_SQLDatabases",
            "Microsoft_Azure_RedisCache", "Microsoft_Azure_KubernetesService",
            // Additional Azure PaaS
            "Microsoft_Azure_CosmosDB", "Microsoft_Azure_PostgreSQL",
            "Microsoft_Azure_MySQL", "Microsoft_Azure_MariaDB",
            "Microsoft_Azure_EventHubs", "Microsoft_Azure_ServiceBus"
        },
        ["Memory"] = new[] {
            // Windows/Linux Servers (with collector agent)
            "WinOS", "WinMemory64", "WinMemory", "Microsoft_Windows_Memory",
            "LinuxMemoryPerformance", "snmp64_memory",
            // Virtualization
            "VMware_vSphere_VMperformance", "Win2k12_HyperV_HypervisorMemory",
            "HyperV_HypervisorMemory", "ESXi_Host_Memory",
            // Azure IaaS (NOTE: requires collector agent for memory %)
            "Microsoft_Azure_VMs",
            // Azure PaaS with Memory metrics
            "Microsoft_Azure_AppServicePlan", "Microsoft_Azure_AppServices",
            "Microsoft_Azure_RedisCache", "Microsoft_Azure_KubernetesService",
            // Additional Azure PaaS
            "Microsoft_Azure_CosmosDB", "Microsoft_Azure_PostgreSQL",
            "Microsoft_Azure_MySQL", "Microsoft_Azure_MariaDB"
        },
        ["Disk"] = new[] {
            // Windows/Linux Servers (with collector agent)
            "WinVolumeUsage", "WinLogicalDisk", "WinLogicalDrivePerformance",
            "WinPhysicalDrive", "LinuxDiskSpace", "snmp64_disk",
            // Azure IaaS (NOTE: disk % requires collector agent)
            "Microsoft_Azure_VMs", "Microsoft_Azure_Disk",
            // Azure Storage (capacity metrics)
            "Microsoft_Azure_StorageAccount", "Microsoft_Azure_StorageAccount_Capacity",
            "Microsoft_Azure_BlobStorage", "Microsoft_Azure_BlobStorage_Capacity",
            "Microsoft_Azure_FileStorage", "Microsoft_Azure_FileStorage_Capacity",
            // Azure SQL/DB (storage metrics)
            "Microsoft_Azure_SQLDatabases", "Microsoft_Azure_PostgreSQL",
            "Microsoft_Azure_MySQL", "Microsoft_Azure_CosmosDB"
        }
    };

    private static readonly Dictionary<string, string[]> FallbackDatapoints = new()
    {
        ["CPU"] = new[] {
            // Windows (with collector)
            "CPUBusyPercent", "PercentProcessorTime", "ProcessorTimePercent",
            // Linux (with collector)
            "cpu_usage", "CPUUsage",
            // VMware
            "usage_average",
            // Azure VMs (via Azure Monitor) - "Percentage CPU" is the Azure Monitor metric name
            "PercentageCPU", "Percentage CPU",
            // Azure App Service Plan (has CpuPercentage - percentage metric)
            "CpuPercentage",
            // Azure SQL/DB (percentage metrics)
            "cpu_percent", "CPUPercent", "dtu_consumption_percent",
            // Azure Redis
            "serverLoad", "percentProcessorTime",
            // Azure AKS
            "node_cpu_usage_percentage",
            // Azure Cosmos DB
            "TotalRequestUnits", "NormalizedRUConsumption",
            // Azure PostgreSQL/MySQL/MariaDB
            "cpu_percent", "CPUPercent"
        },
        ["Memory"] = new[] {
            // Windows (with collector) - percentage metrics
            "MemoryUtilizationPercent", "PercentMemoryUsed", "UsedMemoryPercent",
            // Windows (with collector) - for CALC:MEMORY calculation
            "FreePhysicalMemory", "TotalVisibleMemorySize",
            // Linux (with collector)
            "mem_usage", "MemUsedPercent",
            // VMware
            "usage_average",
            // Azure App Service Plan (has MemoryPercentage - percentage metric)
            "MemoryPercentage",
            // Azure Redis (percentage metric)
            "usedmemorypercentage",
            // Azure AKS
            "node_memory_working_set_percentage",
            // Azure PostgreSQL/MySQL/MariaDB (percentage metric)
            "memory_percent", "MemoryPercent"
            // NOTE: Azure VMs via Azure Monitor do NOT provide memory % - requires collector agent
            // NOTE: AvailableMemoryBytes and MemoryWorkingSet are raw bytes, NOT percentages
        },
        ["Disk"] = new[] {
            // Windows (with collector) - percentage metrics
            "PercentUsed", "PercentFull", "UsedPercent",
            // Windows (with collector) - for CALC:DISK calculation
            "FreeSpace", "Capacity",
            // Linux (with collector)
            "disk_usage",
            // Azure SQL/DB (percentage metrics)
            "storage_percent", "allocated_data_storage_percent",
            // Azure PostgreSQL/MySQL (percentage metric)
            "storage_percent",
            // Azure Cosmos DB (percentage metric - if available)
            "DataUsage"
            // NOTE: Azure VMs via Azure Monitor do NOT provide disk % - requires collector agent
            // NOTE: DiskIOPSConsumedPercentage/DiskBandwidthConsumedPercentage are NOT 0-100 percentages
            // NOTE: UsedCapacity/BlobCapacity/FileCapacity are raw bytes, NOT percentages
        }
    };
    
    // Global exclusion patterns
    private static readonly string[] ExcludedPatterns = { "LogicMonitor_Collector", "LogicMonitor_Portal" };

    /// <summary>
    /// Detect resource type by looking at all available datasources
    /// </summary>
    private static string DetectResourceTypeFromDatasources(List<string> datasources)
    {
        // Check for Azure-specific datasources (most specific first)
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_SQLDatabases", StringComparison.OrdinalIgnoreCase)))
            return "AzureSQL";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_AppService", StringComparison.OrdinalIgnoreCase)))
            return "AppService";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_FunctionApps", StringComparison.OrdinalIgnoreCase)))
            return "FunctionApp";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_StorageAccount", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Azure_BlobStorage", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Azure_FileStorage", StringComparison.OrdinalIgnoreCase)))
            return "AzureStorage";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_RedisCache", StringComparison.OrdinalIgnoreCase)))
            return "Redis";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_CosmosDB", StringComparison.OrdinalIgnoreCase)))
            return "CosmosDB";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_KubernetesService", StringComparison.OrdinalIgnoreCase)))
            return "AKS";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_VMs", StringComparison.OrdinalIgnoreCase)))
            return "AzureVM";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_Disk", StringComparison.OrdinalIgnoreCase)))
            return "AzureDisk";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_ApplicationInsights", StringComparison.OrdinalIgnoreCase)))
            return "AppInsights";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_NetworkInterface", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Azure_VirtualNetworks", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Azure_LoadBalancer", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Azure_ApplicationGateway", StringComparison.OrdinalIgnoreCase)))
            return "AzureNetwork";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_RecoveryService", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Azure_BackupJobStatus", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Azure_LogAnalyticsReplication", StringComparison.OrdinalIgnoreCase)))
            return "AzureBackup";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_ActiveDirectory", StringComparison.OrdinalIgnoreCase)))
            return "AzureAD";
        // Azure Database services (PaaS) - PostgreSQL, MySQL, MariaDB
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_PostgreSQL", StringComparison.OrdinalIgnoreCase)))
            return "AzurePostgreSQL";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_MySQL", StringComparison.OrdinalIgnoreCase)))
            return "AzureMySQL";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_MariaDB", StringComparison.OrdinalIgnoreCase)))
            return "AzureMariaDB";
        // Azure Messaging services
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_EventHubs", StringComparison.OrdinalIgnoreCase)))
            return "AzureEventHubs";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_ServiceBus", StringComparison.OrdinalIgnoreCase)))
            return "AzureServiceBus";
        // Azure Container services
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_ContainerInstances", StringComparison.OrdinalIgnoreCase)))
            return "AzureContainerInstance";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_ContainerRegistry", StringComparison.OrdinalIgnoreCase)))
            return "AzureContainerRegistry";
        // Azure Analytics/AI services
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_CognitiveServices", StringComparison.OrdinalIgnoreCase)))
            return "AzureCognitive";
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_MachineLearning", StringComparison.OrdinalIgnoreCase)))
            return "AzureML";
        // Generic Azure resource (fallback)
        if (datasources.Any(d => d.StartsWith("Microsoft_Azure_", StringComparison.OrdinalIgnoreCase)))
            return "AzureResource";
            
        // Check for VMware
        if (datasources.Any(d => d.StartsWith("VMware_", StringComparison.OrdinalIgnoreCase)))
            return "VMware";
            
        // Check for Hyper-V
        if (datasources.Any(d => d.Contains("HyperV", StringComparison.OrdinalIgnoreCase) || 
                                 d.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)))
            return "HyperV";
            
        // Check for ESXi
        if (datasources.Any(d => d.StartsWith("ESX", StringComparison.OrdinalIgnoreCase)))
            return "ESXi";
            
        // Check for Windows Server (has WinCPU, WinOS, WinMemory, etc.)
        if (datasources.Any(d => d.StartsWith("WinCPU", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("WinOS", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Microsoft_Windows_CPU", StringComparison.OrdinalIgnoreCase)))
            return "WindowsServer";
            
        // Check for Linux Server
        if (datasources.Any(d => d.StartsWith("Linux", StringComparison.OrdinalIgnoreCase)))
            return "LinuxServer";
            
        // Check for SNMP devices
        if (datasources.Any(d => d.StartsWith("snmp", StringComparison.OrdinalIgnoreCase)))
            return "NetworkDevice";
            
        // Check for LogicMonitor Collector (internal)
        if (datasources.Any(d => d.StartsWith("LogicMonitor_Collector", StringComparison.OrdinalIgnoreCase)))
            return "LMCollector";
            
        // If it has SSL/Certificate/HTTP monitoring but no server metrics
        if (datasources.Any(d => d.Contains("SSL_", StringComparison.OrdinalIgnoreCase) ||
                                 d.Contains("HTTP", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("Ping", StringComparison.OrdinalIgnoreCase)))
            return "MonitoredEndpoint";
        
        // Only has generic monitoring like Whois_TTL_Expiry
        if (datasources.All(d => d.StartsWith("Whois_", StringComparison.OrdinalIgnoreCase) ||
                                 d.StartsWith("LogUsage", StringComparison.OrdinalIgnoreCase) ||
                                 d.Contains("ServiceLimits", StringComparison.OrdinalIgnoreCase)))
            return "MetadataOnly";  // No actual performance metrics available
            
        return "Unknown";
    }

    public LMPerformanceFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LMPerformanceFunctions>();
        _db = db;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    /// <summary>
    /// Find matching datasource using database patterns (with fallback to hardcoded)
    /// Returns: (matched datasource, mapping that matched, resource type)
    /// </summary>
    private async Task<(LMDeviceDatasource? ds, LMDatasourceMapping? mapping, string resourceType)> FindMatchingDatasourceAsync(
        List<LMDeviceDatasource> datasources,
        string metricCategory,
        List<LMDatasourceMapping>? dbMappings = null)
    {
        // Try database mappings first (sorted by priority)
        if (dbMappings != null && dbMappings.Any())
        {
            var categoryMappings = dbMappings
                .Where(m => m.MetricCategory.Equals(metricCategory, StringComparison.OrdinalIgnoreCase) && m.IsActive)
                .OrderByDescending(m => m.Priority)
                .ToList();

            foreach (var mapping in categoryMappings)
            {
                var match = datasources.FirstOrDefault(ds =>
                {
                    var dsName = ds.DataSourceName ?? "";
                    
                    // Check if excluded
                    if (!string.IsNullOrEmpty(mapping.ExcludePattern) && 
                        dsName.StartsWith(mapping.ExcludePattern, StringComparison.OrdinalIgnoreCase))
                        return false;
                    
                    // Check global exclusions
                    if (ExcludedPatterns.Any(e => dsName.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    
                    // Check if pattern matches
                    return dsName.Contains(mapping.DatasourcePattern, StringComparison.OrdinalIgnoreCase);
                });

                if (match != null)
                {
                    return (match, mapping, mapping.ResourceType);
                }
            }
        }

        // Fallback to hardcoded patterns
        if (FallbackPatterns.TryGetValue(metricCategory, out var fallbackPatterns))
        {
            foreach (var pattern in fallbackPatterns)
            {
                var match = datasources.FirstOrDefault(ds =>
                {
                    var dsName = ds.DataSourceName ?? "";
                    if (ExcludedPatterns.Any(e => dsName.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    return dsName.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                });

                if (match != null)
                {
                    // Determine resource type from datasource name
                    var resourceType = DetermineResourceType(match.DataSourceName ?? "");
                    return (match, null, resourceType);
                }
            }
        }

        return (null, null, "Unknown");
    }

    /// <summary>
    /// Determine resource type from datasource name
    /// </summary>
    private static string DetermineResourceType(string datasourceName)
    {
        if (datasourceName.StartsWith("Microsoft_Azure_SQLDatabases", StringComparison.OrdinalIgnoreCase))
            return "AzureSQL";
        if (datasourceName.StartsWith("Microsoft_Azure_AppServices", StringComparison.OrdinalIgnoreCase))
            return "AppService";
        if (datasourceName.StartsWith("Microsoft_Azure_FunctionApps", StringComparison.OrdinalIgnoreCase))
            return "FunctionApp";
        if (datasourceName.StartsWith("Microsoft_Azure_VMs", StringComparison.OrdinalIgnoreCase))
            return "AzureVM";
        if (datasourceName.StartsWith("Microsoft_Azure_StorageAccounts", StringComparison.OrdinalIgnoreCase))
            return "Storage";
        if (datasourceName.StartsWith("Microsoft_Azure_RedisCache", StringComparison.OrdinalIgnoreCase))
            return "Redis";
        if (datasourceName.StartsWith("Microsoft_Azure_CosmosDB", StringComparison.OrdinalIgnoreCase))
            return "CosmosDB";
        if (datasourceName.StartsWith("Microsoft_Azure_KubernetesService", StringComparison.OrdinalIgnoreCase))
            return "AKS";
        if (datasourceName.StartsWith("Microsoft_Azure_", StringComparison.OrdinalIgnoreCase))
            return "Azure";
        if (datasourceName.StartsWith("VMware_", StringComparison.OrdinalIgnoreCase))
            return "VMware";
        if (datasourceName.StartsWith("Win", StringComparison.OrdinalIgnoreCase) ||
            datasourceName.StartsWith("Microsoft_Windows", StringComparison.OrdinalIgnoreCase))
            return "WindowsServer";
        if (datasourceName.StartsWith("Linux", StringComparison.OrdinalIgnoreCase))
            return "LinuxServer";
        if (datasourceName.StartsWith("snmp", StringComparison.OrdinalIgnoreCase))
            return "NetworkDevice";
        if (datasourceName.StartsWith("ESX", StringComparison.OrdinalIgnoreCase) ||
            datasourceName.StartsWith("Hyper", StringComparison.OrdinalIgnoreCase))
            return "Hypervisor";
        return "Server";
    }

    /// <summary>
    /// Get datapoints to search for a metric category
    /// </summary>
    private string[] GetDatapoints(string metricCategory, LMDatasourceMapping? mapping)
    {
        // If we have a mapping with specific datapoints, use those
        if (mapping != null && !string.IsNullOrEmpty(mapping.DatapointPatterns))
        {
            return mapping.GetDatapointArray();
        }
        
        // Fallback to default datapoints
        return FallbackDatapoints.TryGetValue(metricCategory, out var datapoints) 
            ? datapoints 
            : Array.Empty<string>();
    }

    private async Task<LogicMonitorService?> GetLogicMonitorServiceAsync(Guid? customerId = null)
    {
        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            
            if (customerId.HasValue)
            {
                try
                {
                    var prefix = $"LM-{customerId.Value}";
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

    /// <summary>
    /// DEBUG: Get raw LM API response for instance data
    /// </summary>
    [Function("DebugLMInstanceData")]
    public async Task<HttpResponseData> DebugInstanceData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/debug/instance-data")] 
        HttpRequestData req)
    {
        // Parse query string manually
        var queryString = req.Url.Query.TrimStart('?');
        var queryParams = queryString.Split('&')
            .Select(p => p.Split('='))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
        
        queryParams.TryGetValue("customerId", out var customerId);
        queryParams.TryGetValue("deviceId", out var deviceId);
        queryParams.TryGetValue("hdsId", out var hdsId);
        queryParams.TryGetValue("instanceId", out var instanceId);
        
        if (string.IsNullOrEmpty(customerId) || string.IsNullOrEmpty(deviceId) || 
            string.IsNullOrEmpty(hdsId) || string.IsNullOrEmpty(instanceId))
        {
            var badReq = req.CreateResponse(HttpStatusCode.BadRequest);
            await badReq.WriteStringAsync($"Required: customerId, deviceId, hdsId, instanceId. Got: customerId={customerId}, deviceId={deviceId}, hdsId={hdsId}, instanceId={instanceId}");
            return badReq;
        }
        
        var lmService = await GetLogicMonitorServiceAsync(Guid.Parse(customerId));
        if (lmService == null)
        {
            var errResp = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errResp.WriteStringAsync("LM service unavailable");
            return errResp;
        }
        
        try
        {
            // Get raw response
            var rawJson = await lmService.GetRawInstanceDataAsync(
                int.Parse(deviceId), int.Parse(hdsId), int.Parse(instanceId));
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(rawJson);
            return response;
        }
        catch (Exception ex)
        {
            var errResp = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errResp.WriteStringAsync($"Error: {ex.Message}");
            return errResp;
        }
    }

    /// <summary>
    /// Get performance metrics summary for a customer (from cache)
    /// </summary>
    [Function("GetLMPerformanceSummary")]
    public async Task<HttpResponseData> GetPerformanceSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/performance/summary")] 
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

        // Get metrics from cache
        var metrics = await _db.LMDeviceMetrics
            .Where(m => m.CustomerId == custId)
            .ToListAsync();

        var summary = new
        {
            customerId = custId,
            customerName = customer.Name,
            totalDevices = metrics.Count,
            lastSyncedAt = metrics.Any() ? metrics.Max(m => m.LastSyncedAt) : (DateTime?)null,
            
            // Right-sizing summary
            rightSizing = new
            {
                oversized = metrics.Count(m => m.OverallRecommendation == "Oversized"),
                rightSized = metrics.Count(m => m.OverallRecommendation == "Right-sized"),
                undersized = metrics.Count(m => m.OverallRecommendation == "Undersized"),
                unknown = metrics.Count(m => m.OverallRecommendation == "Unknown" || m.OverallRecommendation == null)
            },
            
            // Average utilization across all devices (7-day)
            averageUtilization = new
            {
                cpu = metrics.Where(m => m.CpuAvg7Day.HasValue).Select(m => m.CpuAvg7Day!.Value).DefaultIfEmpty(0).Average(),
                memory = metrics.Where(m => m.MemAvg7Day.HasValue).Select(m => m.MemAvg7Day!.Value).DefaultIfEmpty(0).Average(),
                disk = metrics.Where(m => m.DiskAvg7Day.HasValue).Select(m => m.DiskAvg7Day!.Value).DefaultIfEmpty(0).Average()
            },
            
            // Top oversized devices (for quick wins)
            topOversized = metrics
                .Where(m => m.OverallRecommendation == "Oversized")
                .OrderBy(m => Math.Min(m.CpuAvg7Day ?? 100, Math.Min(m.MemAvg7Day ?? 100, m.DiskAvg7Day ?? 100)))
                .Take(10)
                .Select(m => new
                {
                    deviceId = m.DeviceId,
                    deviceName = m.DeviceName,
                    cpuAvg = m.CpuAvg7Day,
                    memAvg = m.MemAvg7Day,
                    diskAvg = m.DiskAvg7Day,
                    cpuRecommendation = m.CpuRecommendation,
                    memRecommendation = m.MemRecommendation,
                    diskRecommendation = m.DiskRecommendation,
                    potentialSavings = m.PotentialSavings
                }),
            
            // Top undersized devices (for attention)
            topUndersized = metrics
                .Where(m => m.OverallRecommendation == "Undersized")
                .OrderByDescending(m => Math.Max(m.CpuAvg7Day ?? 0, Math.Max(m.MemAvg7Day ?? 0, m.DiskAvg7Day ?? 0)))
                .Take(10)
                .Select(m => new
                {
                    deviceId = m.DeviceId,
                    deviceName = m.DeviceName,
                    cpuAvg = m.CpuAvg7Day,
                    cpuMax = m.CpuMax7Day,
                    memAvg = m.MemAvg7Day,
                    memMax = m.MemMax7Day,
                    diskAvg = m.DiskAvg7Day,
                    diskMax = m.DiskMax7Day
                })
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(summary);
        return response;
    }

    /// <summary>
    /// Get devices with unknown/unmapped datasources for pattern discovery
    /// This helps identify what new patterns need to be added
    /// </summary>
    [Function("GetUnmappedDatasources")]
    public async Task<HttpResponseData> GetUnmappedDatasources(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/performance/unmapped")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        // Get devices with Unknown recommendation that have datasources
        var unknownDevices = await _db.LMDeviceMetrics
            .Where(m => m.CustomerId == custId && 
                        (m.OverallRecommendation == "Unknown" || m.OverallRecommendation == null) &&
                        m.AvailableDatasources != null)
            .OrderBy(m => m.DetectedResourceType)
            .ThenBy(m => m.DeviceName)
            .Select(m => new
            {
                deviceId = m.DeviceId,
                deviceName = m.DeviceName,
                detectedType = m.DetectedResourceType,
                matchedDatasources = m.MatchedDatasources,
                unmatchedDatasources = m.UnmatchedDatasources,
                availableDatasources = m.AvailableDatasources,
                lastSyncedAt = m.LastSyncedAt
            })
            .ToListAsync();

        // Group by resource type for easier analysis
        var byType = unknownDevices
            .GroupBy(d => d.detectedType ?? "Unknown")
            .Select(g => new
            {
                resourceType = g.Key,
                count = g.Count(),
                devices = g.Take(10).ToList(), // Limit to 10 per type
                // Find common datasources across all devices of this type
                commonDatasources = g
                    .Where(d => d.availableDatasources != null)
                    .SelectMany(d => {
                        try { return JsonSerializer.Deserialize<List<string>>(d.availableDatasources!) ?? new List<string>(); }
                        catch { return new List<string>(); }
                    })
                    .GroupBy(ds => ds)
                    .Where(dsg => dsg.Count() > 1) // Only show datasources that appear in multiple devices
                    .OrderByDescending(dsg => dsg.Count())
                    .Take(20)
                    .Select(dsg => new { datasource = dsg.Key, count = dsg.Count() })
                    .ToList()
            })
            .OrderByDescending(t => t.count)
            .ToList();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            totalUnknownDevices = unknownDevices.Count,
            byResourceType = byType,
            message = "Use this data to identify which datasource patterns need to be added. Common datasources across multiple devices are good candidates for new patterns."
        });
        return response;
    }

    /// <summary>
    /// Get all performance metrics for a customer (paginated)
    /// </summary>
    [Function("GetLMPerformanceMetrics")]
    public async Task<HttpResponseData> GetPerformanceMetrics(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/performance")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        // Parse query params
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var filter = query["filter"];
        var sortBy = query["sortBy"] ?? "name";
        var page = int.TryParse(query["page"], out var p) ? p : 1;
        var pageSize = int.TryParse(query["pageSize"], out var ps) ? ps : 50;

        var metricsQuery = _db.LMDeviceMetrics.Where(m => m.CustomerId == custId);

        // Apply filter
        if (!string.IsNullOrEmpty(filter))
        {
            metricsQuery = filter.ToLower() switch
            {
                "oversized" => metricsQuery.Where(m => m.OverallRecommendation == "Oversized"),
                "undersized" => metricsQuery.Where(m => m.OverallRecommendation == "Undersized"),
                "rightsized" => metricsQuery.Where(m => m.OverallRecommendation == "Right-sized"),
                "unknown" => metricsQuery.Where(m => m.OverallRecommendation == "Unknown" || m.OverallRecommendation == null),
                _ => metricsQuery
            };
        }

        // Apply sorting
        metricsQuery = sortBy.ToLower() switch
        {
            "cpu" => metricsQuery.OrderByDescending(m => m.CpuAvg7Day),
            "memory" => metricsQuery.OrderByDescending(m => m.MemAvg7Day),
            "disk" => metricsQuery.OrderByDescending(m => m.DiskAvg7Day),
            _ => metricsQuery.OrderBy(m => m.DeviceName)
        };

        var total = await metricsQuery.CountAsync();
        var metrics = await metricsQuery
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new
            {
                deviceId = m.DeviceId,
                deviceName = m.DeviceName,
                cpu = new { avg1h = m.CpuAvg1Hr, max1h = m.CpuMax1Hr, avg24h = m.CpuAvg24Hr, avg7d = m.CpuAvg7Day, max7d = m.CpuMax7Day, recommendation = m.CpuRecommendation },
                memory = new { avg1h = m.MemAvg1Hr, max1h = m.MemMax1Hr, avg24h = m.MemAvg24Hr, avg7d = m.MemAvg7Day, max7d = m.MemMax7Day, totalGB = m.MemTotalGB, recommendation = m.MemRecommendation },
                disk = new { avg1h = m.DiskAvg1Hr, max1h = m.DiskMax1Hr, avg24h = m.DiskAvg24Hr, avg7d = m.DiskAvg7Day, max7d = m.DiskMax7Day, totalGB = m.DiskTotalGB, usedGB = m.DiskUsedGB, recommendation = m.DiskRecommendation },
                network = new { inAvg1h = m.NetInAvg1Hr, outAvg1h = m.NetOutAvg1Hr, inAvg24h = m.NetInAvg24Hr, outAvg24h = m.NetOutAvg24Hr },
                overallRecommendation = m.OverallRecommendation,
                potentialSavings = m.PotentialSavings,
                lastSyncedAt = m.LastSyncedAt
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            total,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling(total / (double)pageSize),
            metrics
        });
        return response;
    }

    /// <summary>
    /// Get detailed performance metrics for a single device
    /// </summary>
    [Function("GetLMDevicePerformance")]
    public async Task<HttpResponseData> GetDevicePerformance(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/devices/{deviceId}/performance")] 
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

        // Get cached metrics
        var cachedMetrics = await _db.LMDeviceMetrics
            .FirstOrDefaultAsync(m => m.CustomerId == custId && m.DeviceId == devId);

        if (cachedMetrics == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("No metrics found for this device");
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            source = "cache",
            deviceId = cachedMetrics.DeviceId,
            deviceName = cachedMetrics.DeviceName,
            cpu = new { 
                avg1h = cachedMetrics.CpuAvg1Hr, 
                max1h = cachedMetrics.CpuMax1Hr,
                avg24h = cachedMetrics.CpuAvg24Hr, 
                max24h = cachedMetrics.CpuMax24Hr,
                avg7d = cachedMetrics.CpuAvg7Day, 
                max7d = cachedMetrics.CpuMax7Day,
                recommendation = cachedMetrics.CpuRecommendation 
            },
            memory = new { 
                avg1h = cachedMetrics.MemAvg1Hr, 
                max1h = cachedMetrics.MemMax1Hr,
                avg24h = cachedMetrics.MemAvg24Hr, 
                avg7d = cachedMetrics.MemAvg7Day, 
                max7d = cachedMetrics.MemMax7Day,
                totalGB = cachedMetrics.MemTotalGB, 
                recommendation = cachedMetrics.MemRecommendation 
            },
            disk = new { 
                avg1h = cachedMetrics.DiskAvg1Hr, 
                avg24h = cachedMetrics.DiskAvg24Hr, 
                avg7d = cachedMetrics.DiskAvg7Day, 
                max7d = cachedMetrics.DiskMax7Day,
                totalGB = cachedMetrics.DiskTotalGB,
                usedGB = cachedMetrics.DiskUsedGB,
                recommendation = cachedMetrics.DiskRecommendation 
            },
            overallRecommendation = cachedMetrics.OverallRecommendation,
            potentialSavings = cachedMetrics.PotentialSavings,
            lastSyncedAt = cachedMetrics.LastSyncedAt
        });
        return response;
    }

    /// <summary>
    /// Start async performance metrics sync - queues the job and returns immediately
    /// </summary>
    [Function("StartLMPerformanceSync")]
    public async Task<HttpResponseData> StartPerformanceSync(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/customers/{customerId}/performance/sync")] 
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
        if (syncStatus?.Status == "SyncingPerformance")
        {
            var inProgress = req.CreateResponse(HttpStatusCode.OK);
            await inProgress.WriteAsJsonAsync(new
            {
                status = "in_progress",
                message = "Performance sync already in progress",
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

        // Update sync status to indicate we're starting
        if (syncStatus == null)
        {
            syncStatus = new LMSyncStatus { CustomerId = custId };
            _db.LMSyncStatuses.Add(syncStatus);
        }
        syncStatus.Status = "SyncingPerformance";
        syncStatus.PerformanceSyncProgress = 0;
        syncStatus.DeviceCount = deviceCount;
        syncStatus.LastSyncStarted = DateTime.UtcNow;
        syncStatus.ErrorMessage = null;
        await _db.SaveChangesAsync();

        // Queue the background job
        var connectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");
        if (!string.IsNullOrEmpty(connectionString))
        {
            var queueClient = new Azure.Storage.Queues.QueueClient(connectionString, "lm-performance-sync");
            await queueClient.CreateIfNotExistsAsync();
            await queueClient.SendMessageAsync(Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(custId.ToString())));
        }
        else
        {
            // Fallback: run in background task (less reliable but works for dev)
            _ = Task.Run(() => ProcessPerformanceSyncAsync(custId));
        }

        var response = req.CreateResponse(HttpStatusCode.Accepted);
        await response.WriteAsJsonAsync(new
        {
            status = "started",
            message = $"Performance sync started for {deviceCount} devices",
            totalDevices = deviceCount
        });
        return response;
    }

    /// <summary>
    /// Get performance sync status
    /// </summary>
    [Function("GetLMPerformanceSyncStatus")]
    public async Task<HttpResponseData> GetPerformanceSyncStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/performance/sync/status")] 
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
            status = syncStatus?.Status ?? "idle",
            progress = syncStatus?.PerformanceSyncProgress ?? 0,
            totalDevices = syncStatus?.DeviceCount ?? 0,
            devicesWithData = syncStatus?.PerformanceDevicesWithData ?? 0,
            lastSyncStarted = syncStatus?.LastSyncStarted,
            lastSyncCompleted = syncStatus?.LastSyncCompleted,
            errorMessage = syncStatus?.ErrorMessage
        });
        return response;
    }

    /// <summary>
    /// Queue-triggered background processor for performance sync
    /// </summary>
    [Function("ProcessLMPerformanceSyncQueue")]
    public async Task ProcessPerformanceSyncQueue(
        [QueueTrigger("lm-performance-sync", Connection = "AzureWebJobsStorage")] string message)
    {
        var customerId = Guid.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(message)));
        await ProcessPerformanceSyncAsync(customerId);
    }

    /// <summary>
    /// Core sync logic - called by queue or background task
    /// </summary>
    private async Task ProcessPerformanceSyncAsync(Guid custId)
    {
        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
        
        try
        {
            // Get devices
            var devices = await _db.LMDevices
                .Where(d => d.CustomerId == custId)
                .ToListAsync();

            if (!devices.Any())
            {
                if (syncStatus != null)
                {
                    syncStatus.Status = "Error";
                    syncStatus.ErrorMessage = "No devices found";
                    await _db.SaveChangesAsync();
                }
                return;
            }

            var lmService = await GetLogicMonitorServiceAsync(custId);
            if (lmService == null)
            {
                if (syncStatus != null)
                {
                    syncStatus.Status = "Error";
                    syncStatus.ErrorMessage = "LogicMonitor service unavailable";
                    await _db.SaveChangesAsync();
                }
                return;
            }

            var now = DateTime.UtcNow;
            var synced = 0;
            var withData = 0;
            var totalDevices = devices.Count;
            var errors = new List<string>();
            var diagnostics = new List<string>();

            // Time ranges for data fetch
            var end = now;
            var start7Day = now.AddDays(-7);

            // Process all devices
            foreach (var device in devices)
        {
            try
            {
                _logger.LogInformation("Syncing metrics for device {DeviceId}: {DeviceName}", device.Id, device.DisplayName);

                // Get or create metrics record
                var metrics = await _db.LMDeviceMetrics
                    .FirstOrDefaultAsync(m => m.CustomerId == custId && m.DeviceId == device.Id);

                if (metrics == null)
                {
                    metrics = new LMDeviceMetrics
                    {
                        CustomerId = custId,
                        DeviceId = device.Id,
                        DeviceName = device.DisplayName,
                        CreatedAt = now
                    };
                    _db.LMDeviceMetrics.Add(metrics);
                }

                metrics.DeviceName = device.DisplayName;
                metrics.LastSyncedAt = now;
                metrics.UpdatedAt = now;

                // Get datasources for this device
                var datasources = await lmService.GetDeviceDatasourcesAsync(device.Id);
                if (datasources?.Items == null || !datasources.Items.Any())
                {
                    _logger.LogWarning("No datasources found for device {DeviceId}", device.Id);
                    metrics.AvailableDatasources = "[]";
                    metrics.DetectedResourceType = "Unknown";
                    metrics.CalculateRecommendations();
                    synced++;
                    continue;
                }

                // Store ALL datasources for this device (for analysis)
                var allDsNames = datasources.Items
                    .Select(ds => ds.DataSourceName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
                    
                metrics.AvailableDatasources = JsonSerializer.Serialize(allDsNames);

                // Detect resource type from available datasources
                metrics.DetectedResourceType = DetectResourceTypeFromDatasources(allDsNames);

                // Collect diagnostics for first 5 devices
                if (synced < 5)
                {
                    diagnostics.Add($"[{device.DisplayName}] Type: {metrics.DetectedResourceType}, Total: {allDsNames.Count} datasources");
                    diagnostics.Add($"  All datasources: {string.Join(", ", allDsNames.Take(25))}");
                }

                _logger.LogInformation("Found {Count} datasources for device {DeviceId} (Type: {ResourceType})", 
                    datasources.Items.Count, device.Id, metrics.DetectedResourceType);

                bool hasAnyData = false;
                var matchedDsList = new List<string>();

                // Find and fetch CPU data
                var cpuDs = datasources.Items.FirstOrDefault(ds => 
                    FallbackPatterns["CPU"].Any(c => ds.DataSourceName?.Contains(c, StringComparison.OrdinalIgnoreCase) == true) &&
                    !ExcludedPatterns.Any(e => ds.DataSourceName?.StartsWith(e, StringComparison.OrdinalIgnoreCase) == true));
                
                if (synced < 5) diagnostics.Add($"  CPU match: {(cpuDs != null ? cpuDs.DataSourceName : "NONE FOUND")}");
                
                if (cpuDs != null)
                {
                    matchedDsList.Add(cpuDs.DataSourceName!);
                    var cpuData = await FetchMetricDataAsync(lmService, device.Id, cpuDs.Id, FallbackDatapoints["CPU"], start7Day, end);
                    if (cpuData.HasValue)
                    {
                        metrics.CpuAvg7Day = (decimal)cpuData.Value.avg;
                        metrics.CpuMax7Day = (decimal)cpuData.Value.max;
                        metrics.CpuAvg1Hr = (decimal)cpuData.Value.avg;
                        metrics.CpuMax1Hr = (decimal)cpuData.Value.max;
                        metrics.CpuAvg24Hr = (decimal)cpuData.Value.avg;
                        hasAnyData = true;
                        _logger.LogInformation("CPU for {DeviceName}: Avg={Avg:F1}%, Max={Max:F1}%", device.DisplayName, cpuData.Value.avg, cpuData.Value.max);
                    }
                }

                // Find and fetch Memory data
                var memDs = datasources.Items.FirstOrDefault(ds => 
                    FallbackPatterns["Memory"].Any(c => ds.DataSourceName?.Contains(c, StringComparison.OrdinalIgnoreCase) == true) &&
                    !ExcludedPatterns.Any(e => ds.DataSourceName?.StartsWith(e, StringComparison.OrdinalIgnoreCase) == true));
                
                if (synced < 5) diagnostics.Add($"  Memory match: {(memDs != null ? memDs.DataSourceName : "NONE FOUND")}");
                
                if (memDs != null)
                {
                    if (!matchedDsList.Contains(memDs.DataSourceName!)) matchedDsList.Add(memDs.DataSourceName!);
                    var memData = await FetchMetricDataAsync(lmService, device.Id, memDs.Id, FallbackDatapoints["Memory"], start7Day, end);
                    if (memData.HasValue)
                    {
                        metrics.MemAvg7Day = (decimal)memData.Value.avg;
                        metrics.MemMax7Day = (decimal)memData.Value.max;
                        metrics.MemAvg1Hr = (decimal)memData.Value.avg;
                        metrics.MemMax1Hr = (decimal)memData.Value.max;
                        metrics.MemAvg24Hr = (decimal)memData.Value.avg;
                        hasAnyData = true;
                        _logger.LogInformation("Memory for {DeviceName}: Avg={Avg:F1}%, Max={Max:F1}%", device.DisplayName, memData.Value.avg, memData.Value.max);
                    }
                }

                // Find and fetch Disk data
                var diskDs = datasources.Items.FirstOrDefault(ds => 
                    FallbackPatterns["Disk"].Any(c => ds.DataSourceName?.Contains(c, StringComparison.OrdinalIgnoreCase) == true) &&
                    !ExcludedPatterns.Any(e => ds.DataSourceName?.StartsWith(e, StringComparison.OrdinalIgnoreCase) == true));
                
                if (synced < 5) diagnostics.Add($"  Disk match: {(diskDs != null ? diskDs.DataSourceName : "NONE FOUND")}");
                
                if (diskDs != null)
                {
                    if (!matchedDsList.Contains(diskDs.DataSourceName!)) matchedDsList.Add(diskDs.DataSourceName!);
                    var diskData = await FetchMetricDataAsync(lmService, device.Id, diskDs.Id, FallbackDatapoints["Disk"], start7Day, end);
                    if (diskData.HasValue)
                    {
                        metrics.DiskAvg7Day = (decimal)diskData.Value.avg;
                        metrics.DiskMax7Day = (decimal)diskData.Value.max;
                        metrics.DiskAvg1Hr = (decimal)diskData.Value.avg;
                        metrics.DiskMax1Hr = (decimal)diskData.Value.max;
                        metrics.DiskAvg24Hr = (decimal)diskData.Value.avg;
                        hasAnyData = true;
                        _logger.LogInformation("Disk for {DeviceName}: Avg={Avg:F1}%, Max={Max:F1}%", device.DisplayName, diskData.Value.avg, diskData.Value.max);
                    }
                }

                // Store what we matched vs what we didn't
                metrics.MatchedDatasources = matchedDsList.Any() ? string.Join(", ", matchedDsList) : null;
                
                // Calculate unmatched (all datasources minus matched ones, excluding LogicMonitor internal ones)
                var unmatchedDs = allDsNames
                    .Where(ds => !matchedDsList.Contains(ds) && 
                                 !ExcludedPatterns.Any(e => ds.StartsWith(e, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                metrics.UnmatchedDatasources = unmatchedDs.Any() ? string.Join(", ", unmatchedDs.Take(20)) : null;

                // Calculate recommendations
                metrics.CalculateRecommendations();
                
                if (hasAnyData) withData++;
                synced++;

                // Update progress periodically
                if (synced % 10 == 0)
                {
                    await _db.SaveChangesAsync();
                    if (syncStatus != null)
                    {
                        syncStatus.PerformanceSyncProgress = synced;
                        syncStatus.PerformanceDevicesWithData = withData;
                        await _db.SaveChangesAsync();
                    }
                }

                // Small delay to avoid rate limits
                await Task.Delay(200);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing metrics for device {DeviceId}: {DeviceName}", device.Id, device.DisplayName);
                errors.Add($"{device.DisplayName}: {ex.Message}");
                synced++;
            }
        }

        await _db.SaveChangesAsync();

        // Update final status
        if (syncStatus != null)
        {
            syncStatus.Status = "Completed";
            syncStatus.PerformanceSyncProgress = synced;
            syncStatus.PerformanceDevicesWithData = withData;
            syncStatus.LastSyncCompleted = DateTime.UtcNow;
            syncStatus.ErrorMessage = errors.Any() ? string.Join("; ", errors.Take(5)) : null;
            await _db.SaveChangesAsync();
        }

        _logger.LogInformation("Performance sync completed for customer {CustomerId}: {Synced}/{Total} devices, {WithData} with data",
            custId, synced, totalDevices, withData);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Performance sync failed for customer {CustomerId}", custId);
            if (syncStatus != null)
            {
                syncStatus.Status = "Error";
                syncStatus.ErrorMessage = ex.Message;
                await _db.SaveChangesAsync();
            }
        }
    }

    /// <summary>
    /// Fetch metric data for a device datasource
    /// </summary>
    private async Task<(double avg, double max)?> FetchMetricDataAsync(
        LogicMonitorService lmService, 
        int deviceId, 
        int hdsId, 
        string[] datapointNames,
        DateTime start,
        DateTime end)
    {
        try
        {
            // Get instances for this datasource
            var instances = await lmService.GetDatasourceInstancesAsync(deviceId, hdsId);
            if (instances?.Items == null || !instances.Items.Any())
            {
                _logger.LogDebug("No instances found for device {DeviceId} datasource {HdsId}", deviceId, hdsId);
                return null;
            }

            // Use first instance (or could aggregate across all)
            var instance = instances.Items.First();
            
            // Fetch data for this instance
            var data = await lmService.GetInstanceDataAsync(deviceId, hdsId, instance.Id, start, end);
            if (data?.DataPoints == null || !data.Values.HasValue || 
                data.Values.Value.ValueKind != System.Text.Json.JsonValueKind.Array ||
                data.Values.Value.GetArrayLength() == 0)
            {
                _logger.LogDebug("No data returned for device {DeviceId} instance {InstanceId}", deviceId, instance.Id);
                return null;
            }

            // Find the datapoint index that matches our expected names
            int datapointIndex = -1;
            for (int i = 0; i < data.DataPoints.Count; i++)
            {
                var dpName = data.DataPoints[i];
                if (datapointNames.Any(n => dpName.Contains(n, StringComparison.OrdinalIgnoreCase)))
                {
                    datapointIndex = i;
                    break;
                }
            }

            // If no match found, try to find any "percent" or "usage" datapoint
            if (datapointIndex == -1)
            {
                for (int i = 0; i < data.DataPoints.Count; i++)
                {
                    var dpName = data.DataPoints[i].ToLower();
                    if (dpName.Contains("percent") || dpName.Contains("usage") || dpName.Contains("util"))
                    {
                        datapointIndex = i;
                        break;
                    }
                }
            }

            if (datapointIndex == -1)
            {
                _logger.LogDebug("No matching datapoint found for device {DeviceId}. Available: {DataPoints}", 
                    deviceId, string.Join(", ", data.DataPoints));
                return null;
            }

            // Extract values using the helper method
            var values = data.GetDatapointValues(datapointIndex);

            if (!values.Any())
            {
                _logger.LogDebug("No valid values for device {DeviceId} datapoint {Datapoint}", deviceId, data.DataPoints[datapointIndex]);
                return null;
            }

            return (values.Average(), values.Max());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching metric data for device {DeviceId} datasource {HdsId}", deviceId, hdsId);
            return null;
        }
    }
}
