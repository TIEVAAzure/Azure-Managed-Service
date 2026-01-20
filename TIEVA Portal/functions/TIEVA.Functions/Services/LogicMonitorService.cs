using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TIEVA.Functions.Services;

/// <summary>
/// Service for interacting with LogicMonitor REST API v3
/// Security: All credentials retrieved from Azure Key Vault
/// </summary>
public class LogicMonitorService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;
    private readonly string _company;
    private readonly string _accessId;
    private readonly string _accessKey;
    private readonly string _baseUrl;

    // Rate limit tracking (thread-safe)
    private static int _remainingRequests = 100;
    private static DateTime _rateLimitReset = DateTime.UtcNow;
    private static readonly object _rateLimitLock = new object();

    // Concurrency control - limit parallel API calls
    private static readonly SemaphoreSlim _apiSemaphore = new SemaphoreSlim(3, 3);

    // Retry settings
    private const int MaxRetries = 3;
    private static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) };

    public LogicMonitorService(
        ILoggerFactory loggerFactory,
        string company,
        string accessId,
        string accessKey)
    {
        _logger = loggerFactory.CreateLogger<LogicMonitorService>();
        _httpClient = new HttpClient();
        _company = company;
        _accessId = accessId;
        _accessKey = accessKey;
        _baseUrl = $"https://{company}.logicmonitor.com/santaba/rest";
    }

    #region LMv1 Authentication
    
    /// <summary>
    /// Generates LMv1 authentication header using HMAC-SHA256
    /// </summary>
    private string GenerateAuthHeader(string httpVerb, string resourcePath, string data = "")
    {
        var epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        
        // Concatenate request details (query params NOT included)
        var requestVars = $"{httpVerb}{epoch}{data}{resourcePath}";
        
        // Calculate HMAC-SHA256 signature
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_accessKey));
        var signatureBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(requestVars));
        var signatureHex = BitConverter.ToString(signatureBytes).Replace("-", "").ToLower();
        var signature = Convert.ToBase64String(Encoding.UTF8.GetBytes(signatureHex));
        
        return $"LMv1 {_accessId}:{signature}:{epoch}";
    }

    /// <summary>
    /// Executes an authenticated request to LogicMonitor API with retry and rate limiting
    /// </summary>
    private async Task<T?> ExecuteRequestAsync<T>(
        string method,
        string resourcePath,
        string? queryParams = null,
        object? body = null) where T : class
    {
        var uri = $"{_baseUrl}{resourcePath}";
        if (!string.IsNullOrEmpty(queryParams))
        {
            uri += $"?{queryParams}";
        }

        // Acquire semaphore to limit concurrent requests
        await _apiSemaphore.WaitAsync();
        try
        {
            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                // Thread-safe rate limit check
                lock (_rateLimitLock)
                {
                    if (_remainingRequests <= 5 && DateTime.UtcNow < _rateLimitReset)
                    {
                        var waitTime = _rateLimitReset - DateTime.UtcNow;
                        if (waitTime.TotalSeconds > 0)
                        {
                            _logger.LogWarning("Rate limit approaching ({Remaining} left), waiting {WaitSeconds}s", _remainingRequests, waitTime.TotalSeconds);
                            Thread.Sleep(waitTime); // Use sync sleep inside lock for short waits
                        }
                    }
                }

                var bodyJson = body != null ? JsonSerializer.Serialize(body) : "";
                var authHeader = GenerateAuthHeader(method, resourcePath, bodyJson);

                using var request = new HttpRequestMessage(new HttpMethod(method), uri);
                request.Headers.Add("Authorization", authHeader);
                request.Headers.Add("X-Version", "3");

                if (!string.IsNullOrEmpty(bodyJson))
                {
                    request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                }

                try
                {
                    if (attempt == 0)
                    {
                        _logger.LogInformation("LM API Request: {Method} {Uri}", method, uri);
                    }
                    else
                    {
                        _logger.LogInformation("LM API Request (retry {Attempt}): {Method} {Uri}", attempt, method, uri);
                    }

                    var response = await _httpClient.SendAsync(request);

                    // Update rate limit tracking from response headers (thread-safe)
                    lock (_rateLimitLock)
                    {
                        if (response.Headers.TryGetValues("X-Rate-Limit-Remaining", out var remaining))
                        {
                            int.TryParse(remaining.FirstOrDefault(), out _remainingRequests);
                        }
                        if (response.Headers.TryGetValues("X-Rate-Limit-Window", out var window))
                        {
                            if (int.TryParse(window.FirstOrDefault(), out var windowSeconds))
                            {
                                _rateLimitReset = DateTime.UtcNow.AddSeconds(windowSeconds);
                            }
                        }
                    }

                    // Handle 429 Too Many Requests with retry
                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        if (attempt < MaxRetries)
                        {
                            // Check for Retry-After header
                            TimeSpan retryDelay = RetryDelays[attempt];
                            if (response.Headers.TryGetValues("Retry-After", out var retryAfter))
                            {
                                if (int.TryParse(retryAfter.FirstOrDefault(), out var retrySeconds))
                                {
                                    retryDelay = TimeSpan.FromSeconds(retrySeconds);
                                }
                            }

                            _logger.LogWarning("Rate limited (429), retrying in {Seconds}s (attempt {Attempt}/{MaxRetries})",
                                retryDelay.TotalSeconds, attempt + 1, MaxRetries);
                            await Task.Delay(retryDelay);
                            continue; // Retry
                        }

                        _logger.LogError("Rate limited (429) - max retries exceeded for {Uri}", uri);
                        return null;
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        var errorContent = await response.Content.ReadAsStringAsync();
                        _logger.LogError("LM API error: {StatusCode} - {Error} for {Uri}", response.StatusCode, errorContent, uri);
                        return null;
                    }

                    var content = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                }
                catch (HttpRequestException ex) when (attempt < MaxRetries)
                {
                    _logger.LogWarning("HTTP error, retrying in {Seconds}s: {Message}", RetryDelays[attempt].TotalSeconds, ex.Message);
                    await Task.Delay(RetryDelays[attempt]);
                    continue;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Exception calling LogicMonitor API: {Path} - {Message}", resourcePath, ex.Message);
                    return null;
                }
            }

            return null; // Should not reach here
        }
        finally
        {
            _apiSemaphore.Release();
        }
    }

    #endregion

    #region Device Operations

    /// <summary>
    /// Get all devices for a specific device group (customer) - direct children only
    /// </summary>
    public async Task<LMDeviceListResponse?> GetDevicesForGroupAsync(int groupId, int size = 100, int offset = 0)
    {
        return await ExecuteRequestAsync<LMDeviceListResponse>(
            "GET",
            $"/device/groups/{groupId}/devices",
            $"size={size}&offset={offset}&fields=id,displayName,hostStatus,alertStatus,currentCollectorId,systemProperties,customProperties"
        );
    }

    /// <summary>
    /// Get all devices in a group INCLUDING subgroups by recursively fetching from all subgroups
    /// </summary>
    public async Task<LMDeviceListResponse?> GetAllDevicesInGroupAsync(int groupId, int size = 1000, int offset = 0)
    {
        // Get all subgroup IDs recursively
        var allGroupIds = new List<int> { groupId };
        await CollectSubgroupIdsAsync(groupId, allGroupIds);
        
        _logger.LogInformation("Found {Count} groups (including subgroups) for group {GroupId}", allGroupIds.Count, groupId);

        // Fetch devices from all groups in parallel (batch of 3 at a time - matches semaphore limit)
        var allDevices = new List<LMDevice>();

        foreach (var batch in allGroupIds.Chunk(3))
        {
            var tasks = batch.Select(gid => ExecuteRequestAsync<LMDeviceListResponse>(
                "GET",
                $"/device/groups/{gid}/devices",
                "size=1000&fields=id,displayName,hostStatus,alertStatus"
            ));
            
            var results = await Task.WhenAll(tasks);
            foreach (var result in results)
            {
                if (result?.Items != null)
                {
                    allDevices.AddRange(result.Items);
                }
            }
        }
        
        _logger.LogInformation("Found {Count} total devices across all groups", allDevices.Count);
        
        return new LMDeviceListResponse
        {
            Total = allDevices.Count,
            Items = allDevices.Skip(offset).Take(size).ToList()
        };
    }
    
    /// <summary>
    /// Recursively collect all subgroup IDs
    /// </summary>
    private async Task CollectSubgroupIdsAsync(int parentGroupId, List<int> collected)
    {
        var subgroups = await ExecuteRequestAsync<LMDeviceGroupListResponse>(
            "GET",
            "/device/groups",
            $"size=100&filter=parentId:{parentGroupId}&fields=id,name"
        );
        
        if (subgroups?.Items != null)
        {
            foreach (var group in subgroups.Items)
            {
                collected.Add(group.Id);
                await CollectSubgroupIdsAsync(group.Id, collected);
            }
        }
    }

    /// <summary>
    /// Get device group details including custom properties
    /// </summary>
    public async Task<LMDeviceGroupResponse?> GetDeviceGroupAsync(int groupId)
    {
        return await ExecuteRequestAsync<LMDeviceGroupResponse>(
            "GET",
            $"/device/groups/{groupId}",
            "fields=id,name,fullPath,numOfHosts,customProperties"
        );
    }

    /// <summary>
    /// Get all device groups under the Customers root (ID: 406) - for TIEVA shared portal
    /// </summary>
    public async Task<LMDeviceGroupListResponse?> GetCustomerGroupsAsync()
    {
        // Filter to direct children of Customers group (ID: 406)
        return await ExecuteRequestAsync<LMDeviceGroupListResponse>(
            "GET",
            "/device/groups",
            "size=100&filter=parentId:406&fields=id,name,fullPath,numOfHosts,customProperties"
        );
    }

    /// <summary>
    /// Get all device groups - for customer's own portal (no parentId filter)
    /// </summary>
    public async Task<LMDeviceGroupListResponse?> GetAllGroupsAsync()
    {
        // Get all groups (no filter) - useful for customer's own LM portal
        return await ExecuteRequestAsync<LMDeviceGroupListResponse>(
            "GET",
            "/device/groups",
            "size=300&fields=id,name,fullPath,numOfHosts,customProperties"
        );
    }

    /// <summary>
    /// Get subgroups of a specific group by parentId
    /// </summary>
    public async Task<LMDeviceGroupListResponse?> GetSubgroupsAsync(int parentGroupId)
    {
        return await ExecuteRequestAsync<LMDeviceGroupListResponse>(
            "GET",
            "/device/groups",
            $"size=100&filter=parentId:{parentGroupId}&fields=id,name,fullPath,numOfHosts"
        );
    }

    /// <summary>
    /// Initialize Delta tracking - returns all devices and a deltaId for future calls
    /// </summary>
    public async Task<LMDeltaResponse?> InitializeDeltaAsync(int? groupId = null, string? filter = null)
    {
        var queryParams = "size=1000&fields=id,displayName,hostStatus,alertStatus,updatedOn";
        if (groupId.HasValue)
        {
            queryParams += $"&filter=hostGroupIds~{groupId}";
        }
        else if (!string.IsNullOrEmpty(filter))
        {
            queryParams += $"&filter={filter}";
        }

        return await ExecuteRequestAsync<LMDeltaResponse>("GET", "/device/devices/delta", queryParams);
    }

    /// <summary>
    /// Get devices changed since last delta call
    /// </summary>
    public async Task<LMDeltaResponse?> GetDeltaChangesAsync(string deltaId, int size = 1000, int offset = 0)
    {
        return await ExecuteRequestAsync<LMDeltaResponse>(
            "GET",
            $"/device/devices/delta/{deltaId}",
            $"size={size}&offset={offset}"
        );
    }

    #endregion

    #region Alert Operations

    /// <summary>
    /// Get active alerts filtered by device group path (including subgroups)
    /// </summary>
    public async Task<LMAlertListResponse?> GetActiveAlertsAsync(int? groupId = null, int size = 100)
    {
        var filter = "cleared:false";
        
        if (groupId.HasValue)
        {
            // Get the group path first
            var groupInfo = await GetDeviceGroupAsync(groupId.Value);
            if (groupInfo != null)
            {
                var groupPath = groupInfo.FullPath; // e.g. "Customers/Ovarro"
                // Use monitorObjectGroups filter with * to include subgroups
                filter = $"cleared:false,monitorObjectGroups:\"{groupPath}*\"";
                _logger.LogInformation("Getting alerts for group path: {GroupPath}*", groupPath);
            }
        }

        return await ExecuteRequestAsync<LMAlertListResponse>(
            "GET", 
            "/alert/alerts", 
            $"size={size}&filter={Uri.EscapeDataString(filter)}&fields=id,monitorObjectName,alertValue,severity,startEpoch,resourceTemplateName,inSDT,acked,deviceId,deviceDisplayName"
        );
    }

    /// <summary>
    /// Get alert history for a time period, filtered by device group path (including subgroups)
    /// </summary>
    public async Task<LMAlertListResponse?> GetAlertHistoryAsync(
        int? groupId = null, 
        DateTime? since = null, 
        int size = 100)
    {
        var sinceEpoch = since?.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds 
            ?? DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
        
        var filter = $"startEpoch>:{sinceEpoch}";

        if (groupId.HasValue)
        {
            // Get the group path first
            var groupInfo = await GetDeviceGroupAsync(groupId.Value);
            if (groupInfo != null)
            {
                var groupPath = groupInfo.FullPath;
                filter = $"startEpoch>:{sinceEpoch},monitorObjectGroups:\"{groupPath}*\"";
            }
        }

        return await ExecuteRequestAsync<LMAlertListResponse>(
            "GET", 
            "/alert/alerts", 
            $"size={size}&filter={Uri.EscapeDataString(filter)}&fields=id,monitorObjectName,alertValue,severity,startEpoch,endEpoch,cleared,acked,deviceId,deviceDisplayName,resourceTemplateName"
        );
    }

    /// <summary>
    /// Acknowledge an alert
    /// </summary>
    public async Task<bool> AcknowledgeAlertAsync(string alertId, string comment)
    {
        var result = await ExecuteRequestAsync<object>(
            "POST",
            $"/alert/alerts/{alertId}/ack",
            body: new { ackComment = comment }
        );
        return result != null;
    }

    #endregion

    #region Report Operations

    /// <summary>
    /// Get available reports
    /// </summary>
    public async Task<LMReportListResponse?> GetReportsAsync(int size = 50)
    {
        return await ExecuteRequestAsync<LMReportListResponse>(
            "GET",
            "/report/reports",
            $"size={size}&fields=id,name,type,description,groupId,lastRunOn"
        );
    }

    /// <summary>
    /// Run a report
    /// </summary>
    public async Task<bool> RunReportAsync(int reportId)
    {
        var result = await ExecuteRequestAsync<object>("POST", $"/report/reports/{reportId}/run");
        return result != null;
    }

    #endregion

    #region Performance Data

    /// <summary>
    /// Get performance data for a device's datasource
    /// </summary>
    public async Task<LMDeviceDataResponse?> GetDeviceDataAsync(
        int deviceId, 
        int datasourceId, 
        DateTime? start = null, 
        DateTime? end = null)
    {
        var startEpoch = start?.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds 
            ?? DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var endEpoch = end?.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds 
            ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return await ExecuteRequestAsync<LMDeviceDataResponse>(
            "GET",
            $"/device/devices/{deviceId}/devicedatasources/{datasourceId}/data",
            $"start={startEpoch}&end={endEpoch}"
        );
    }

    /// <summary>
    /// Get datasources applied to a device
    /// </summary>
    public async Task<LMDeviceDatasourceListResponse?> GetDeviceDatasourcesAsync(int deviceId)
    {
        return await ExecuteRequestAsync<LMDeviceDatasourceListResponse>(
            "GET",
            $"/device/devices/{deviceId}/devicedatasources",
            "size=100&fields=id,dataSourceId,dataSourceName,dataSourceDisplayName,instanceNumber"
        );
    }

    /// <summary>
    /// Get instances for a device datasource (e.g., individual disks, network interfaces)
    /// </summary>
    public async Task<LMDatasourceInstanceListResponse?> GetDatasourceInstancesAsync(int deviceId, int hdsId)
    {
        return await ExecuteRequestAsync<LMDatasourceInstanceListResponse>(
            "GET",
            $"/device/devices/{deviceId}/devicedatasources/{hdsId}/instances",
            "size=100&fields=id,displayName,wildValue,wildValue2"
        );
    }

    /// <summary>
    /// Get data for a specific instance of a datasource
    /// </summary>
    public async Task<LMInstanceDataResponse?> GetInstanceDataAsync(
        int deviceId, 
        int hdsId, 
        int instanceId,
        DateTime? start = null, 
        DateTime? end = null)
    {
        var startEpoch = (long)(start?.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds 
            ?? DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds());
        var endEpoch = (long)(end?.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds 
            ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        return await ExecuteRequestAsync<LMInstanceDataResponse>(
            "GET",
            $"/device/devices/{deviceId}/devicedatasources/{hdsId}/instances/{instanceId}/data",
            $"start={startEpoch}&end={endEpoch}"
        );
    }

    /// <summary>
    /// Get aggregated device metrics data using LM's built-in aggregation
    /// </summary>
    public async Task<LMGraphDataResponse?> GetDeviceGraphDataAsync(
        int deviceId,
        int hdsId,
        int instanceId,
        string dataPointName,
        DateTime start,
        DateTime end)
    {
        var startEpoch = (long)start.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
        var endEpoch = (long)end.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds;
        
        return await ExecuteRequestAsync<LMGraphDataResponse>(
            "GET",
            $"/device/devices/{deviceId}/devicedatasources/{hdsId}/instances/{instanceId}/graphs/{dataPointName}/data",
            $"start={startEpoch}&end={endEpoch}"
        );
    }

    /// <summary>
    /// Get complete device details including system properties
    /// </summary>
    public async Task<LMDeviceDetailResponse?> GetDeviceDetailsAsync(int deviceId)
    {
        return await ExecuteRequestAsync<LMDeviceDetailResponse>(
            "GET",
            $"/device/devices/{deviceId}",
            "fields=id,displayName,hostStatus,alertStatus,systemProperties,customProperties,inheritedProperties"
        );
    }

    /// <summary>
    /// Get device performance metrics using device/devicedatasourceinstances endpoint
    /// This is more efficient for getting metrics across multiple datasources
    /// </summary>
    public async Task<LMDeviceInstancesResponse?> GetDeviceAllInstancesAsync(int deviceId)
    {
        return await ExecuteRequestAsync<LMDeviceInstancesResponse>(
            "GET",
            $"/device/devices/{deviceId}/devicedatasourceinstances",
            "size=200&fields=id,displayName,deviceDataSourceId,dataSourceName,dataSourceDisplayName"
        );
    }

    #endregion

    #region Debug Methods

    /// <summary>
    /// Get raw JSON response from instance data endpoint for debugging
    /// </summary>
    public async Task<string> GetRawInstanceDataAsync(int deviceId, int hdsId, int instanceId)
    {
        var startEpoch = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds();
        var endEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var resourcePath = $"/device/devices/{deviceId}/devicedatasources/{hdsId}/instances/{instanceId}/data";
        var queryParams = $"start={startEpoch}&end={endEpoch}";
        
        var authHeader = GenerateAuthHeader("GET", resourcePath, "");
        var uri = $"{_baseUrl}{resourcePath}?{queryParams}";
        
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Authorization", authHeader);
        request.Headers.Add("X-Version", "3");
        
        var response = await _httpClient.SendAsync(request);
        return await response.Content.ReadAsStringAsync();
    }

    #endregion
}

#region Response Models

public class LMBaseResponse
{
    public int Total { get; set; }
    public string? SearchId { get; set; }
}

public class LMDeviceListResponse : LMBaseResponse
{
    public List<LMDevice> Items { get; set; } = new();
}

public class LMDevice
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? HostStatus { get; set; }
    public string? AlertStatus { get; set; }
    public int CurrentCollectorId { get; set; }
    public string? HostGroupIds { get; set; }
    public List<LMProperty>? SystemProperties { get; set; }
    public List<LMProperty>? CustomProperties { get; set; }
}

public class LMProperty
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

public class LMDeviceGroupResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public int NumOfHosts { get; set; }
    public List<LMProperty>? CustomProperties { get; set; }
}

public class LMDeviceGroupListResponse : LMBaseResponse
{
    public List<LMDeviceGroupResponse> Items { get; set; } = new();
}

public class LMDeltaResponse : LMBaseResponse
{
    public string? DeltaId { get; set; }
    public List<LMDeltaDevice> Items { get; set; } = new();
}

public class LMDeltaDevice : LMDevice
{
    public string? Status { get; set; }  // added, updated, deleted
    public long UpdatedOn { get; set; }
}

public class LMAlertListResponse : LMBaseResponse
{
    public List<LMAlert> Items { get; set; } = new();
}

public class LMAlert
{
    public string Id { get; set; } = string.Empty;
    public int DeviceId { get; set; }
    public string? DeviceDisplayName { get; set; }
    public string? MonitorObjectName { get; set; }
    public string? AlertValue { get; set; }
    public int Severity { get; set; }  // 4=Critical, 3=Error, 2=Warning, 1=Info
    public long StartEpoch { get; set; }
    public long? EndEpoch { get; set; }
    public bool Cleared { get; set; }
    public bool Acked { get; set; }
    public bool InSDT { get; set; }
    public string? ResourceTemplateName { get; set; }
    
    public string SeverityText => Severity switch
    {
        4 => "Critical",
        3 => "Error",
        2 => "Warning",
        _ => "Info"
    };
    
    public DateTime StartTime => DateTimeOffset.FromUnixTimeSeconds(StartEpoch).DateTime;
    public DateTime? EndTime => EndEpoch.HasValue 
        ? DateTimeOffset.FromUnixTimeSeconds(EndEpoch.Value).DateTime 
        : null;
}

public class LMReportListResponse : LMBaseResponse
{
    public List<LMReport> Items { get; set; } = new();
}

public class LMReport
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Type { get; set; }
    public string? Description { get; set; }
    public int? GroupId { get; set; }
    public long? LastRunOn { get; set; }
}

public class LMDeviceDatasourceListResponse : LMBaseResponse
{
    public List<LMDeviceDatasource> Items { get; set; } = new();
}

public class LMDeviceDatasource
{
    public int Id { get; set; }
    public int DataSourceId { get; set; }
    public string? DataSourceName { get; set; }
    public string? DataSourceDisplayName { get; set; }
    public int InstanceNumber { get; set; }
}

public class LMDeviceDataResponse
{
    public List<string>? DataPoints { get; set; }
    public List<LMDataValue>? Values { get; set; }
    public long? Time { get; set; }
}

public class LMDataValue
{
    public long Time { get; set; }
    public List<double?>? Values { get; set; }
}

// Additional response models for performance metrics
public class LMDatasourceInstanceListResponse : LMBaseResponse
{
    public List<LMDatasourceInstance> Items { get; set; } = new();
}

public class LMDatasourceInstance
{
    public int Id { get; set; }
    public string? DisplayName { get; set; }
    public string? WildValue { get; set; }      // e.g., "C:" for disk, "eth0" for network
    public string? WildValue2 { get; set; }
    public int DeviceDataSourceId { get; set; }
}

public class LMInstanceDataResponse
{
    public List<string>? DataPoints { get; set; }  // e.g., ["CPUBusyPercent", "CPUIdlePercent"]
    
    // Values is an array of arrays: [[timestamp, val1, val2], [timestamp, val1, val2], ...]
    // Using JsonElement to handle mixed types (long timestamps, double/null values)
    public System.Text.Json.JsonElement? Values { get; set; }
    
    // Time can be various formats - using JsonElement
    public System.Text.Json.JsonElement? Time { get; set; }
    
    /// <summary>
    /// Get all values for a specific datapoint index
    /// </summary>
    public List<double> GetDatapointValues(int datapointIndex)
    {
        var result = new List<double>();
        if (!Values.HasValue || DataPoints == null) return result;
        
        try
        {
            if (Values.Value.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            
            // Values format: [[timestamp, dp0, dp1, ...], [timestamp, dp0, dp1, ...]]
            // datapointIndex 0 = first datapoint (index 1 in the array since index 0 is timestamp)
            var valueIndex = datapointIndex + 1;
            
            foreach (var row in Values.Value.EnumerateArray())
            {
                if (row.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
                if (row.GetArrayLength() <= valueIndex) continue;
                
                var element = row[valueIndex];
                if (element.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    var val = element.GetDouble();
                    // Accept any non-NaN numeric value
                    if (!double.IsNaN(val) && !double.IsInfinity(val))
                    {
                        result.Add(val);
                    }
                }
            }
        }
        catch
        {
            // Silently handle parsing errors
        }
        
        return result;
    }

    /// <summary>
    /// Get the actual number of data columns in the values array (excluding timestamp)
    /// </summary>
    public int GetActualDataColumnCount()
    {
        if (!Values.HasValue) return 0;
        
        try
        {
            if (Values.Value.ValueKind != System.Text.Json.JsonValueKind.Array) return 0;
            
            foreach (var row in Values.Value.EnumerateArray())
            {
                if (row.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    // Row length minus 1 (for timestamp) = number of data columns
                    return row.GetArrayLength() - 1;
                }
            }
        }
        catch { }
        
        return 0;
    }

    /// <summary>
    /// Debug method: Get raw sample values for a datapoint to diagnose filtering issues
    /// </summary>
    public List<object> GetRawDatapointSamples(int datapointIndex, int maxSamples = 5)
    {
        var result = new List<object>();
        if (!Values.HasValue || DataPoints == null) return result;
        
        try
        {
            if (Values.Value.ValueKind != System.Text.Json.JsonValueKind.Array) return result;
            
            var valueIndex = datapointIndex + 1;
            var count = 0;
            
            foreach (var row in Values.Value.EnumerateArray())
            {
                if (count >= maxSamples) break;
                
                if (row.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    result.Add(new { issue = "row_not_array", kind = row.ValueKind.ToString() });
                    count++;
                    continue;
                }
                
                var rowLength = row.GetArrayLength();
                if (rowLength <= valueIndex)
                {
                    result.Add(new { issue = "row_too_short", length = rowLength, needed = valueIndex + 1 });
                    count++;
                    continue;
                }
                
                var element = row[valueIndex];
                result.Add(new { 
                    kind = element.ValueKind.ToString(), 
                    rawValue = element.ValueKind == System.Text.Json.JsonValueKind.Number 
                        ? element.GetDouble() 
                        : element.ValueKind == System.Text.Json.JsonValueKind.Null 
                            ? (object)"null" 
                            : element.ToString()
                });
                count++;
            }
        }
        catch (Exception ex)
        {
            result.Add(new { error = ex.Message });
        }
        
        return result;
    }
}

public class LMGraphDataResponse
{
    public string? Timestamps { get; set; }
    public List<LMGraphLine>? Lines { get; set; }
}

public class LMGraphLine
{
    public string? Label { get; set; }
    public string? ColorName { get; set; }
    public List<double?>? Data { get; set; }
}

public class LMDeviceDetailResponse
{
    public int Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string? HostStatus { get; set; }
    public string? AlertStatus { get; set; }
    public List<LMProperty>? SystemProperties { get; set; }
    public List<LMProperty>? CustomProperties { get; set; }
    public List<LMProperty>? InheritedProperties { get; set; }
    
    // Helper to get property value
    public string? GetProperty(string name)
    {
        return SystemProperties?.FirstOrDefault(p => p.Name == name)?.Value
            ?? CustomProperties?.FirstOrDefault(p => p.Name == name)?.Value
            ?? InheritedProperties?.FirstOrDefault(p => p.Name == name)?.Value;
    }
}

public class LMDeviceInstancesResponse : LMBaseResponse
{
    public List<LMDeviceInstance> Items { get; set; } = new();
}

public class LMDeviceInstance
{
    public int Id { get; set; }
    public string? DisplayName { get; set; }
    public int DeviceDataSourceId { get; set; }
    public string? DataSourceName { get; set; }           // e.g., "WinCPU"
    public string? DataSourceDisplayName { get; set; }    // e.g., "Windows CPU"
}

#endregion
