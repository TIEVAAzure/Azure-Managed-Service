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
    
    // Rate limit tracking
    private static int _remainingRequests = 100;
    private static DateTime _rateLimitReset = DateTime.UtcNow;

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
    /// Executes an authenticated request to LogicMonitor API
    /// </summary>
    private async Task<T?> ExecuteRequestAsync<T>(
        string method,
        string resourcePath,
        string? queryParams = null,
        object? body = null) where T : class
    {
        // Rate limit protection
        if (_remainingRequests <= 5 && DateTime.UtcNow < _rateLimitReset)
        {
            var waitTime = _rateLimitReset - DateTime.UtcNow;
            _logger.LogWarning("Rate limit approaching, waiting {WaitSeconds}s", waitTime.TotalSeconds);
            await Task.Delay(waitTime);
        }

        var bodyJson = body != null ? JsonSerializer.Serialize(body) : "";
        var authHeader = GenerateAuthHeader(method, resourcePath, bodyJson);
        
        var uri = $"{_baseUrl}{resourcePath}";
        if (!string.IsNullOrEmpty(queryParams))
        {
            uri += $"?{queryParams}";
        }

        using var request = new HttpRequestMessage(new HttpMethod(method), uri);
        request.Headers.Add("Authorization", authHeader);
        request.Headers.Add("X-Version", "3");
        
        if (!string.IsNullOrEmpty(bodyJson))
        {
            request.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
        }

        try
        {
            var response = await _httpClient.SendAsync(request);
            
            // Update rate limit tracking from response headers
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

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _logger.LogError("LM API error: {StatusCode} - {Error}", response.StatusCode, errorContent);
                return null;
            }

            var content = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(content, new JsonSerializerOptions 
            { 
                PropertyNameCaseInsensitive = true 
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling LogicMonitor API: {Path}", resourcePath);
            return null;
        }
    }

    #endregion

    #region Device Operations

    /// <summary>
    /// Get all devices for a specific device group (customer)
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
    /// Get all device groups under the Customers root (ID: 406)
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
    /// Get active alerts, optionally filtered by device group
    /// </summary>
    public async Task<LMAlertListResponse?> GetActiveAlertsAsync(int? groupId = null, int size = 100)
    {
        var queryParams = $"size={size}&filter=cleared:false&fields=id,monitorObjectName,alertValue,severity,startEpoch,resourceTemplateName,inSDT,acked";
        
        if (groupId.HasValue)
        {
            queryParams += $",deviceId,deviceDisplayName&filter=cleared:false,deviceGroups~{groupId}";
        }

        return await ExecuteRequestAsync<LMAlertListResponse>("GET", "/alert/alerts", queryParams);
    }

    /// <summary>
    /// Get alert history for a time period
    /// </summary>
    public async Task<LMAlertListResponse?> GetAlertHistoryAsync(
        int? groupId = null, 
        DateTime? since = null, 
        int size = 100)
    {
        var sinceEpoch = since?.ToUniversalTime().Subtract(DateTime.UnixEpoch).TotalSeconds 
            ?? DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeSeconds();
        
        var queryParams = $"size={size}&filter=startEpoch>:{sinceEpoch}&fields=id,monitorObjectName,alertValue,severity,startEpoch,endEpoch,cleared,acked,deviceId,deviceDisplayName,resourceTemplateName";

        if (groupId.HasValue)
        {
            queryParams += $",deviceGroups~{groupId}";
        }

        return await ExecuteRequestAsync<LMAlertListResponse>("GET", "/alert/alerts", queryParams);
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

#endregion
