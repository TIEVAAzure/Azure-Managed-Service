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
/// Azure Functions for LogicMonitor integration
/// Security: All LM credentials retrieved from Key Vault, never exposed to frontend
/// Tier restriction: Advanced and Premium customers only
/// </summary>
public class LogicMonitorFunctions
{
    private readonly ILogger _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TievaDbContext _db;
    private readonly string _keyVaultUrl;

    public LogicMonitorFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LogicMonitorFunctions>();
        _db = db;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    /// <summary>
    /// Gets LogicMonitor service with per-customer credentials if available, otherwise falls back to global
    /// Security: All credentials retrieved from Key Vault, never exposed
    /// </summary>
    private async Task<LogicMonitorService?> GetLogicMonitorServiceAsync(Guid? customerId = null)
    {
        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            
            // Try per-customer credentials first if customerId provided
            if (customerId.HasValue)
            {
                try
                {
                    var prefix = $"LM-{customerId.Value}";
                    var companyTask = client.GetSecretAsync($"{prefix}-Company");
                    var accessIdTask = client.GetSecretAsync($"{prefix}-AccessId");
                    var accessKeyTask = client.GetSecretAsync($"{prefix}-AccessKey");
                    
                    await Task.WhenAll(companyTask, accessIdTask, accessKeyTask);
                    
                    var company = companyTask.Result.Value.Value;
                    var accessId = accessIdTask.Result.Value.Value;
                    var accessKey = accessKeyTask.Result.Value.Value;
                    
                    _logger.LogInformation("Using per-customer LM credentials for customer {CustomerId}, company: {Company}", 
                        customerId, company);
                    
                    return new LogicMonitorService(
                        _loggerFactory,
                        company,
                        accessId,
                        accessKey
                    );
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 404)
                {
                    // Per-customer secrets don't exist, fall through to global
                    _logger.LogInformation("No per-customer LM credentials for {CustomerId}, using global", customerId);
                }
                catch (Exception ex)
                {
                    // Other error retrieving customer credentials
                    _logger.LogError(ex, "Error retrieving per-customer LM credentials for {CustomerId}: {Message}", 
                        customerId, ex.Message);
                    // Fall through to global
                }
            }
            
            // Fall back to global TIEVA credentials
            var globalCompanyTask = client.GetSecretAsync("LM-Company");
            var globalAccessIdTask = client.GetSecretAsync("LM-AccessId");
            var globalAccessKeyTask = client.GetSecretAsync("LM-AccessKey");
            
            await Task.WhenAll(globalCompanyTask, globalAccessIdTask, globalAccessKeyTask);
            
            _logger.LogInformation("Using global TIEVA LM credentials, company: {Company}", 
                globalCompanyTask.Result.Value.Value);
            
            return new LogicMonitorService(
                _loggerFactory,
                globalCompanyTask.Result.Value.Value,
                globalAccessIdTask.Result.Value.Value,
                globalAccessKeyTask.Result.Value.Value
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve LogicMonitor credentials from Key Vault: {Message}", ex.Message);
            return null;
        }
    }
    
    /// <summary>
    /// Checks if customer has per-customer LM credentials configured
    /// </summary>
    private async Task<bool> HasPerCustomerCredentialsAsync(Guid customerId)
    {
        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var prefix = $"LM-{customerId}";
            
            // Just check if the Company secret exists
            await client.GetSecretAsync($"{prefix}-Company");
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private async Task<(Customer? customer, string? error)> ValidateCustomerAccessAsync(Guid customerId)
    {
        var customer = await _db.Customers
            .Where(c => c.Id == customerId && c.IsActive)
            .Include(c => c.Connections)
                .ThenInclude(conn => conn.Subscriptions)
                    .ThenInclude(sub => sub.Tier)
            .FirstOrDefaultAsync();

        if (customer == null)
            return (null, "Customer not found");

        // LM access available for all customers - no tier restriction
        // Each customer can be configured with either:
        //   - TIEVA's shared LM portal (using LogicMonitorGroupId)
        //   - Customer's own LM portal (using per-customer credentials in Key Vault)

        // Check LM is enabled for this customer
        if (!customer.LMEnabled)
            return (null, "LogicMonitor not enabled for this customer");

        // For customers using global credentials, they need a GroupId
        // For customers with per-customer credentials, GroupId is optional (they access their whole portal)
        // This will be checked at runtime when we try to get credentials

        return (customer, null);
    }

    [Function("GetLMDevices")]
    public async Task<HttpResponseData> GetDevices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/devices")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var (customer, error) = await ValidateCustomerAccessAsync(custId);
        if (customer == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await errorResponse.WriteStringAsync(error ?? "Access denied");
            return errorResponse;
        }

        // Read from SQL cache - fast!
        var devices = await _db.LMDevices
            .Where(d => d.CustomerId == custId)
            .OrderBy(d => d.DisplayName)
            .Select(d => new { d.Id, d.DisplayName, d.HostStatus, d.AlertStatus })
            .ToListAsync();

        // Get sync status
        var syncStatus = await _db.LMSyncStatuses
            .Where(s => s.CustomerId == custId)
            .Select(s => new { s.Status, s.LastSyncCompleted, s.DeviceCount })
            .FirstOrDefaultAsync();
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            lmGroupId = customer.LogicMonitorGroupId,
            totalDevices = devices.Count,
            lastSynced = syncStatus?.LastSyncCompleted,
            syncStatus = syncStatus?.Status ?? "Never",
            devices
        });
        return response;
    }

    [Function("SyncLMDevices")]
    public async Task<HttpResponseData> SyncDevices(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/customers/{customerId}/devices/sync")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var (customer, error) = await ValidateCustomerAccessAsync(custId);
        if (customer == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await errorResponse.WriteStringAsync(error ?? "Access denied");
            return errorResponse;
        }

        // Get or create sync status record
        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
        if (syncStatus == null)
        {
            syncStatus = new LMSyncStatus
            {
                Id = Guid.NewGuid(),
                CustomerId = custId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.LMSyncStatuses.Add(syncStatus);
        }

        // Mark as running
        syncStatus.Status = "Running";
        syncStatus.LastSyncStarted = DateTime.UtcNow;
        syncStatus.ErrorMessage = null;
        syncStatus.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        try
        {
            var lmService = await GetLogicMonitorServiceAsync(custId);
            if (lmService == null)
            {
                syncStatus.Status = "Failed";
                syncStatus.ErrorMessage = "LogicMonitor service unavailable";
                syncStatus.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
                return errorResponse;
            }

            // Fetch all devices from LogicMonitor
            _logger.LogInformation("Starting LM device sync for customer {CustomerId}, group {GroupId}", custId, customer.LogicMonitorGroupId);
            var lmDevices = await lmService.GetAllDevicesInGroupAsync(customer.LogicMonitorGroupId!.Value, 5000, 0);

            if (lmDevices?.Items == null)
            {
                syncStatus.Status = "Failed";
                syncStatus.ErrorMessage = "No devices returned from LogicMonitor";
                syncStatus.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteStringAsync("No devices returned from LogicMonitor");
                return errorResponse;
            }

            // Delete existing devices for this customer
            var existingDevices = await _db.LMDevices.Where(d => d.CustomerId == custId).ToListAsync();
            _db.LMDevices.RemoveRange(existingDevices);

            // Insert new devices (deduplicate by ID - devices can appear in multiple subgroups)
            var now = DateTime.UtcNow;
            var newDevices = lmDevices.Items
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .Select(d => new LMDeviceCache
                {
                    Id = d.Id,
                    CustomerId = custId,
                    LMGroupId = customer.LogicMonitorGroupId!.Value,
                    DisplayName = d.DisplayName ?? "Unknown",
                    HostStatus = d.HostStatus,
                    AlertStatus = d.AlertStatus,
                    LastSyncedAt = now,
                    CreatedAt = now
                }).ToList();

            _db.LMDevices.AddRange(newDevices);

            // Update sync status
            syncStatus.Status = "Completed";
            syncStatus.LastSyncCompleted = now;
            syncStatus.DeviceCount = newDevices.Count;
            syncStatus.ErrorMessage = null;
            syncStatus.UpdatedAt = now;

            await _db.SaveChangesAsync();

            _logger.LogInformation("LM device sync completed for customer {CustomerId}: {DeviceCount} devices", custId, newDevices.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                customerId = customer.Id,
                customerName = customer.Name,
                deviceCount = newDevices.Count,
                syncedAt = now
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LM device sync failed for customer {CustomerId}", custId);

            syncStatus.Status = "Failed";
            syncStatus.ErrorMessage = ex.Message;
            syncStatus.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Sync failed: {ex.Message}");
            return errorResponse;
        }
    }

    [Function("GetLMSyncStatus")]
    public async Task<HttpResponseData> GetSyncStatus(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/devices/sync")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var syncStatus = await _db.LMSyncStatuses
            .Where(s => s.CustomerId == custId)
            .FirstOrDefaultAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = custId,
            status = syncStatus?.Status ?? "Never",
            lastSyncStarted = syncStatus?.LastSyncStarted,
            lastSyncCompleted = syncStatus?.LastSyncCompleted,
            deviceCount = syncStatus?.DeviceCount,
            errorMessage = syncStatus?.ErrorMessage
        });
        return response;
    }

    [Function("GetLMDeviceSummary")]
    public async Task<HttpResponseData> GetDeviceSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/devices/summary")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var (customer, error) = await ValidateCustomerAccessAsync(custId);
        if (customer == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await errorResponse.WriteStringAsync(error ?? "Access denied");
            return errorResponse;
        }

        var lmService = await GetLogicMonitorServiceAsync(custId);
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var groupInfo = customer.LogicMonitorGroupId.HasValue 
            ? await lmService.GetDeviceGroupAsync(customer.LogicMonitorGroupId.Value)
            : null;
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            lmGroupId = customer.LogicMonitorGroupId,
            lmGroupPath = groupInfo?.FullPath,
            totalDevices = groupInfo?.NumOfHosts ?? 0
        });
        return response;
    }

    [Function("GetLMAlerts")]
    public async Task<HttpResponseData> GetAlerts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/alerts")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var (customer, error) = await ValidateCustomerAccessAsync(custId);
        if (customer == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await errorResponse.WriteStringAsync(error ?? "Access denied");
            return errorResponse;
        }

        // Read from SQL cache - fast!
        var alerts = await _db.LMAlerts
            .Where(a => a.CustomerId == custId && !a.Cleared)
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.StartTime)
            .ToListAsync();

        // Get sync status
        var syncStatus = await _db.LMSyncStatuses
            .Where(s => s.CustomerId == custId)
            .Select(s => new { s.Status, s.LastSyncCompleted, s.AlertCount })
            .FirstOrDefaultAsync();
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            totalAlerts = alerts.Count,
            lastSynced = syncStatus?.LastSyncCompleted,
            syncStatus = syncStatus?.Status ?? "Never",
            summary = new
            {
                critical = alerts.Count(a => a.Severity == 4),
                error = alerts.Count(a => a.Severity == 3),
                warning = alerts.Count(a => a.Severity == 2),
                info = alerts.Count(a => a.Severity <= 1)
            },
            alerts = alerts.Select(a => new
            {
                a.Id, a.DeviceDisplayName, a.MonitorObjectName, a.AlertValue,
                severity = a.SeverityText, severityLevel = a.Severity,
                startTime = a.StartTime, a.ResourceTemplateName, a.Acked, a.InSDT
            })
        });
        return response;
    }

    [Function("SyncLMAlerts")]
    public async Task<HttpResponseData> SyncAlerts(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/customers/{customerId}/alerts/sync")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var (customer, error) = await ValidateCustomerAccessAsync(custId);
        if (customer == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await errorResponse.WriteStringAsync(error ?? "Access denied");
            return errorResponse;
        }

        // Get or create sync status record
        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
        if (syncStatus == null)
        {
            syncStatus = new LMSyncStatus
            {
                Id = Guid.NewGuid(),
                CustomerId = custId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.LMSyncStatuses.Add(syncStatus);
        }

        try
        {
            var lmService = await GetLogicMonitorServiceAsync(custId);
            if (lmService == null)
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
                return errorResponse;
            }

            // Fetch alerts from LogicMonitor
            _logger.LogInformation("Starting LM alert sync for customer {CustomerId}", custId);
            var lmAlerts = await lmService.GetActiveAlertsAsync(customer.LogicMonitorGroupId);

            // Delete existing alerts for this customer
            var existingAlerts = await _db.LMAlerts.Where(a => a.CustomerId == custId).ToListAsync();
            _db.LMAlerts.RemoveRange(existingAlerts);

            // Insert new alerts (deduplicate by ID)
            var now = DateTime.UtcNow;
            var newAlerts = (lmAlerts?.Items ?? new List<LMAlert>())
                .GroupBy(a => a.Id)
                .Select(g => g.First())
                .Select(a => new LMAlertCache
                {
                    Id = a.Id,
                    CustomerId = custId,
                    DeviceId = a.DeviceId,
                    DeviceDisplayName = a.DeviceDisplayName,
                    MonitorObjectName = a.MonitorObjectName,
                    AlertValue = a.AlertValue,
                    Severity = a.Severity,
                    SeverityText = a.SeverityText,
                    StartTime = a.StartTime,
                    EndTime = a.EndTime,
                    Cleared = a.Cleared,
                    Acked = a.Acked,
                    InSDT = a.InSDT,
                    ResourceTemplateName = a.ResourceTemplateName,
                    LastSyncedAt = now,
                    CreatedAt = now
                }).ToList();

            _db.LMAlerts.AddRange(newAlerts);

            // Update sync status
            syncStatus.AlertCount = newAlerts.Count;
            syncStatus.LastSyncCompleted = now;
            syncStatus.Status = "Completed";
            syncStatus.ErrorMessage = null;
            syncStatus.UpdatedAt = now;

            await _db.SaveChangesAsync();

            _logger.LogInformation("LM alert sync completed for customer {CustomerId}: {AlertCount} alerts", custId, newAlerts.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                customerId = customer.Id,
                customerName = customer.Name,
                alertCount = newAlerts.Count,
                syncedAt = now
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LM alert sync failed for customer {CustomerId}", custId);

            syncStatus.Status = "Failed";
            syncStatus.ErrorMessage = ex.Message;
            syncStatus.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Sync failed: {ex.Message}");
            return errorResponse;
        }
    }

    [Function("SyncLMAll")]
    public async Task<HttpResponseData> SyncAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/customers/{customerId}/sync")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var (customer, error) = await ValidateCustomerAccessAsync(custId);
        if (customer == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await errorResponse.WriteStringAsync(error ?? "Access denied");
            return errorResponse;
        }

        // Get or create sync status record
        var syncStatus = await _db.LMSyncStatuses.FirstOrDefaultAsync(s => s.CustomerId == custId);
        if (syncStatus == null)
        {
            syncStatus = new LMSyncStatus
            {
                Id = Guid.NewGuid(),
                CustomerId = custId,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _db.LMSyncStatuses.Add(syncStatus);
        }

        // Mark as running
        syncStatus.Status = "Running";
        syncStatus.LastSyncStarted = DateTime.UtcNow;
        syncStatus.ErrorMessage = null;
        syncStatus.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        try
        {
            var lmService = await GetLogicMonitorServiceAsync(custId);
            if (lmService == null)
            {
                syncStatus.Status = "Failed";
                syncStatus.ErrorMessage = "LogicMonitor service unavailable";
                syncStatus.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();

                var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
                return errorResponse;
            }

            var now = DateTime.UtcNow;

            // Sync Devices - only if groupId is set (for customers using global credentials)
            _logger.LogInformation("Starting LM full sync for customer {CustomerId}", custId);
            var lmDevices = customer.LogicMonitorGroupId.HasValue
                ? await lmService.GetAllDevicesInGroupAsync(customer.LogicMonitorGroupId.Value, 5000, 0)
                : null;
            
            var existingDevices = await _db.LMDevices.Where(d => d.CustomerId == custId).ToListAsync();
            _db.LMDevices.RemoveRange(existingDevices);

            var newDevices = (lmDevices?.Items ?? new List<LMDevice>())
                .GroupBy(d => d.Id)
                .Select(g => g.First())
                .Select(d => new LMDeviceCache
                {
                    Id = d.Id,
                    CustomerId = custId,
                    LMGroupId = customer.LogicMonitorGroupId!.Value,
                    DisplayName = d.DisplayName ?? "Unknown",
                    HostStatus = d.HostStatus,
                    AlertStatus = d.AlertStatus,
                    LastSyncedAt = now,
                    CreatedAt = now
                }).ToList();

            _db.LMDevices.AddRange(newDevices);

            // Sync Alerts
            var lmAlerts = await lmService.GetActiveAlertsAsync(customer.LogicMonitorGroupId);
            
            var existingAlerts = await _db.LMAlerts.Where(a => a.CustomerId == custId).ToListAsync();
            _db.LMAlerts.RemoveRange(existingAlerts);

            var newAlerts = (lmAlerts?.Items ?? new List<LMAlert>())
                .GroupBy(a => a.Id)
                .Select(g => g.First())
                .Select(a => new LMAlertCache
                {
                    Id = a.Id,
                    CustomerId = custId,
                    DeviceId = a.DeviceId,
                    DeviceDisplayName = a.DeviceDisplayName,
                    MonitorObjectName = a.MonitorObjectName,
                    AlertValue = a.AlertValue,
                    Severity = a.Severity,
                    SeverityText = a.SeverityText,
                    StartTime = a.StartTime,
                    EndTime = a.EndTime,
                    Cleared = a.Cleared,
                    Acked = a.Acked,
                    InSDT = a.InSDT,
                    ResourceTemplateName = a.ResourceTemplateName,
                    LastSyncedAt = now,
                    CreatedAt = now
                }).ToList();

            _db.LMAlerts.AddRange(newAlerts);

            // Update sync status
            syncStatus.Status = "Completed";
            syncStatus.LastSyncCompleted = now;
            syncStatus.DeviceCount = newDevices.Count;
            syncStatus.AlertCount = newAlerts.Count;
            syncStatus.ErrorMessage = null;
            syncStatus.UpdatedAt = now;

            await _db.SaveChangesAsync();

            _logger.LogInformation("LM full sync completed for customer {CustomerId}: {DeviceCount} devices, {AlertCount} alerts", 
                custId, newDevices.Count, newAlerts.Count);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                customerId = customer.Id,
                customerName = customer.Name,
                deviceCount = newDevices.Count,
                alertCount = newAlerts.Count,
                syncedAt = now
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LM full sync failed for customer {CustomerId}", custId);

            syncStatus.Status = "Failed";
            syncStatus.ErrorMessage = ex.Message;
            syncStatus.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Sync failed: {ex.Message}");
            return errorResponse;
        }
    }

    [Function("GetLMAlertHistory")]
    public async Task<HttpResponseData> GetAlertHistory(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/alerts/history")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var days = int.TryParse(query["days"], out var d) ? Math.Min(d, 30) : 7;

        var (customer, error) = await ValidateCustomerAccessAsync(custId);
        if (customer == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.Forbidden);
            await errorResponse.WriteStringAsync(error ?? "Access denied");
            return errorResponse;
        }

        var lmService = await GetLogicMonitorServiceAsync(custId);
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var since = DateTime.UtcNow.AddDays(-days);
        var alerts = await lmService.GetAlertHistoryAsync(customer.LogicMonitorGroupId, since, 200);
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            days, since,
            totalAlerts = alerts?.Total ?? 0,
            summary = new
            {
                total = alerts?.Total ?? 0,
                cleared = alerts?.Items.Count(a => a.Cleared) ?? 0,
                active = alerts?.Items.Count(a => !a.Cleared) ?? 0,
                critical = alerts?.Items.Count(a => a.Severity == 4) ?? 0,
                error = alerts?.Items.Count(a => a.Severity == 3) ?? 0,
                warning = alerts?.Items.Count(a => a.Severity == 2) ?? 0
            },
            alerts = alerts?.Items.Select(a => new
            {
                a.Id, a.DeviceDisplayName, a.MonitorObjectName, a.AlertValue,
                severity = a.SeverityText, severityLevel = a.Severity,
                startTime = a.StartTime, endTime = a.EndTime,
                a.Cleared, a.Acked, a.ResourceTemplateName
            }) ?? Enumerable.Empty<object>()
        });
        return response;
    }

    [Function("AcknowledgeLMAlert")]
    public async Task<HttpResponseData> AcknowledgeAlert(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/alerts/{alertId}/acknowledge")] 
        HttpRequestData req, string alertId)
    {
        var lmService = await GetLogicMonitorServiceAsync();
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var body = await req.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<AckAlertRequest>(body ?? "{}");
        var comment = payload?.Comment ?? "Acknowledged via TIEVA Portal";

        var success = await lmService.AcknowledgeAlertAsync(alertId, comment);
        
        if (success)
        {
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, alertId, comment });
            return response;
        }
        else
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
            await errorResponse.WriteStringAsync("Failed to acknowledge alert");
            return errorResponse;
        }
    }

    [Function("GetLMGroups")]
    public async Task<HttpResponseData> GetGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/groups")] 
        HttpRequestData req)
    {
        var lmService = await GetLogicMonitorServiceAsync();
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var groups = await lmService.GetCustomerGroupsAsync();
        var portalCustomers = await _db.Customers
            .Where(c => c.IsActive)
            .Select(c => new { c.Id, c.Name, c.LogicMonitorGroupId })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            lmGroups = groups?.Items.Select(g => new
            {
                g.Id, g.Name, g.FullPath, g.NumOfHosts,
                mappedToPortalCustomer = portalCustomers.FirstOrDefault(c => c.LogicMonitorGroupId == g.Id)?.Name
            }) ?? Enumerable.Empty<object>(),
            portalCustomers = portalCustomers.Select(c => new { c.Id, c.Name, c.LogicMonitorGroupId })
        });
        return response;
    }

    /// <summary>
    /// Get subgroups of a specific LogicMonitor group
    /// </summary>
    [Function("GetLMSubgroups")]
    public async Task<HttpResponseData> GetSubgroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/groups/{groupId}/subgroups")] 
        HttpRequestData req, string groupId)
    {
        if (!int.TryParse(groupId, out var parentGroupId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid group ID");
            return badRequest;
        }

        var lmService = await GetLogicMonitorServiceAsync();
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var subgroups = await lmService.GetSubgroupsAsync(parentGroupId);
        var portalCustomers = await _db.Customers
            .Where(c => c.IsActive)
            .Select(c => new { c.Id, c.Name, c.LogicMonitorGroupId })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            parentGroupId,
            subgroups = subgroups?.Items.Select(g => new
            {
                g.Id, g.Name, g.FullPath, g.NumOfHosts,
                mappedToPortalCustomer = portalCustomers.FirstOrDefault(c => c.LogicMonitorGroupId == g.Id)?.Name
            }) ?? Enumerable.Empty<object>(),
            totalSubgroups = subgroups?.Total ?? 0
        });
        return response;
    }

    /// <summary>
    /// Get LogicMonitor groups for a specific customer using their credentials (if configured)
    /// Falls back to global credentials if customer doesn't have their own
    /// </summary>
    [Function("GetCustomerLMGroups")]
    public async Task<HttpResponseData> GetCustomerGroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/groups")] 
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

        // Use customer-specific credentials if available
        var hasCustomCreds = await HasPerCustomerCredentialsAsync(custId);
        var lmService = await GetLogicMonitorServiceAsync(custId);
        
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        // For customer's own portal: get ALL groups (no parentId filter)
        // For TIEVA shared portal: filter to children of Customers group (ID: 406)
        var groups = hasCustomCreds 
            ? await lmService.GetAllGroupsAsync()
            : await lmService.GetCustomerGroupsAsync();
        
        // Get company name if using custom creds
        string? companyName = null;
        if (hasCustomCreds)
        {
            try
            {
                var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
                var secret = await client.GetSecretAsync($"LM-{custId}-Company");
                companyName = secret.Value.Value;
            }
            catch { /* Ignore */ }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            credentialsMode = hasCustomCreds ? "CustomerPortal" : "TIEVAShared",
            lmCompany = companyName,
            lmGroups = groups?.Items.Select(g => new
            {
                g.Id, g.Name, g.FullPath, g.NumOfHosts
            }) ?? Enumerable.Empty<object>(),
            totalGroups = groups?.Total ?? 0
        });
        return response;
    }

    /// <summary>
    /// Get subgroups for a specific group using customer-specific credentials
    /// </summary>
    [Function("GetCustomerLMSubgroups")]
    public async Task<HttpResponseData> GetCustomerSubgroups(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/groups/{groupId}/subgroups")]
        HttpRequestData req, string customerId, string groupId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        if (!int.TryParse(groupId, out var parentGroupId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid group ID");
            return badRequest;
        }

        var customer = await _db.Customers.FindAsync(custId);
        if (customer == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Customer not found");
            return notFound;
        }

        var lmService = await GetLogicMonitorServiceAsync(custId);
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var subgroups = await lmService.GetSubgroupsAsync(parentGroupId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            parentGroupId,
            subgroups = subgroups?.Items.Select(g => new
            {
                g.Id, g.Name, g.FullPath, g.NumOfHosts
            }) ?? Enumerable.Empty<object>(),
            totalSubgroups = subgroups?.Total ?? 0
        });
        return response;
    }

    [Function("SetLMMapping")]
    public async Task<HttpResponseData> SetMapping(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "logicmonitor/customers/{customerId}/mapping")] 
        HttpRequestData req, string customerId)
    {
        if (!Guid.TryParse(customerId, out var custId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var body = await req.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<SetMappingRequest>(body ?? "{}", 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (payload?.LmGroupId == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("lmGroupId is required");
            return badRequest;
        }

        var customer = await _db.Customers.FindAsync(custId);
        if (customer == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Customer not found");
            return notFound;
        }

        customer.LogicMonitorGroupId = payload.LmGroupId;
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        _logger.LogInformation("Mapped customer {CustomerName} to LM group {GroupId}", customer.Name, payload.LmGroupId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            success = true,
            customerId = customer.Id,
            customerName = customer.Name,
            lmGroupId = customer.LogicMonitorGroupId
        });
        return response;
    }

    /// <summary>
    /// Get LogicMonitor configuration for a customer (credentials are NOT returned)
    /// </summary>
    [Function("GetLMConfig")]
    public async Task<HttpResponseData> GetLMConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/customers/{customerId}/config")] 
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

        // Check if per-customer credentials exist
        var hasPerCustomerCreds = await HasPerCustomerCredentialsAsync(custId);
        
        // Get company name and accessId if credentials exist (but NOT accessKey)
        string? companyName = null;
        string? accessId = null;
        if (hasPerCustomerCreds)
        {
            try
            {
                var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
                var companySecret = await client.GetSecretAsync($"LM-{custId}-Company");
                companyName = companySecret.Value.Value;
                var accessIdSecret = await client.GetSecretAsync($"LM-{custId}-AccessId");
                accessId = accessIdSecret.Value.Value;
            }
            catch { /* Ignore - just won't show values */ }
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            lmEnabled = customer.LMEnabled,
            lmGroupId = customer.LogicMonitorGroupId,
            hasPerCustomerCredentials = hasPerCustomerCreds,
            lmCompany = companyName,    // Company subdomain
            lmAccessId = accessId,       // Access ID (for display only)
            // Note: accessKey is NEVER returned for security
            configMode = hasPerCustomerCreds ? "PerCustomer" : 
                         customer.LogicMonitorGroupId.HasValue ? "GlobalWithGroup" : "None"
        });
        return response;
    }

    /// <summary>
    /// Save LogicMonitor configuration for a customer
    /// Credentials are stored securely in Key Vault, never in the database
    /// </summary>
    [Function("SaveLMConfig")]
    public async Task<HttpResponseData> SaveLMConfig(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "logicmonitor/customers/{customerId}/config")] 
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

        var body = await req.ReadAsStringAsync();
        var payload = JsonSerializer.Deserialize<SaveLMConfigRequest>(body ?? "{}", 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        if (payload == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body");
            return badRequest;
        }

        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var prefix = $"LM-{custId}";

            // If credentials are provided, save them to Key Vault
            if (!string.IsNullOrEmpty(payload.Company) && 
                !string.IsNullOrEmpty(payload.AccessId) && 
                !string.IsNullOrEmpty(payload.AccessKey))
            {
                _logger.LogInformation("Saving per-customer LM credentials for {CustomerId}", custId);
                
                // Save all three secrets
                await client.SetSecretAsync($"{prefix}-Company", payload.Company);
                await client.SetSecretAsync($"{prefix}-AccessId", payload.AccessId);
                await client.SetSecretAsync($"{prefix}-AccessKey", payload.AccessKey);
                
                // Track that customer has custom credentials
                customer.LMHasCustomCredentials = true;
                
                _logger.LogInformation("Successfully saved LM credentials to Key Vault for {CustomerId}", custId);
            }

            // Update database fields
            customer.LMEnabled = payload.Enabled ?? customer.LMEnabled;
            customer.LogicMonitorGroupId = payload.GroupId ?? customer.LogicMonitorGroupId;
            customer.UpdatedAt = DateTime.UtcNow;
            
            await _db.SaveChangesAsync();

            var hasPerCustomerCreds = await HasPerCustomerCredentialsAsync(custId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                customerId = customer.Id,
                customerName = customer.Name,
                lmEnabled = customer.LMEnabled,
                lmGroupId = customer.LogicMonitorGroupId,
                hasPerCustomerCredentials = hasPerCustomerCreds,
                configMode = hasPerCustomerCreds ? "PerCustomer" : 
                             customer.LogicMonitorGroupId.HasValue ? "GlobalWithGroup" : "None"
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save LM config for customer {CustomerId}", custId);
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Failed to save configuration: {ex.Message}");
            return errorResponse;
        }
    }

    /// <summary>
    /// Delete LogicMonitor credentials for a customer (removes from Key Vault)
    /// </summary>
    [Function("DeleteLMCredentials")]
    public async Task<HttpResponseData> DeleteLMCredentials(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "logicmonitor/customers/{customerId}/credentials")] 
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

        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var prefix = $"LM-{custId}";

            // Delete all three secrets (soft delete)
            var deleteTasks = new[]
            {
                client.StartDeleteSecretAsync($"{prefix}-Company"),
                client.StartDeleteSecretAsync($"{prefix}-AccessId"),
                client.StartDeleteSecretAsync($"{prefix}-AccessKey")
            };

            foreach (var task in deleteTasks)
            {
                try { await task; } catch { /* Ignore if secret doesn't exist */ }
            }
            
            // Clear the flag in database
            customer.LMHasCustomCredentials = false;
            customer.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Deleted LM credentials from Key Vault for {CustomerId}", custId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                customerId = customer.Id,
                message = "Credentials deleted from Key Vault"
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete LM credentials for customer {CustomerId}", custId);
            
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Failed to delete credentials: {ex.Message}");
            return errorResponse;
        }
    }

    [Function("TestLogicMonitorConnectionGlobal")]
    public async Task<HttpResponseData> TestLogicMonitorConnectionGlobal(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "logicmonitor/test")] 
        HttpRequestData req)
    {
        try
        {
            var lmService = await GetLogicMonitorServiceAsync();
            if (lmService == null)
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Could not create LogicMonitor service - check Key Vault credentials"
                });
                return response;
            }

            // Try to get customer groups as a simple test
            var groups = await lmService.GetCustomerGroupsAsync();
            var devices = groups?.Items?.Sum(g => g.NumOfHosts) ?? 0;
            
            var testResponse = req.CreateResponse(HttpStatusCode.OK);
            await testResponse.WriteAsJsonAsync(new
            {
                success = true,
                message = "Connection successful",
                deviceCount = devices,
                groupCount = groups?.Total ?? 0
            });
            return testResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Global LM connection test failed");
            
            var errorResponse = req.CreateResponse(HttpStatusCode.OK);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                error = ex.Message
            });
            return errorResponse;
        }
    }

    /// <summary>
    /// Test LogicMonitor connection for a customer
    /// </summary>
    [Function("TestLMConnection")]
    public async Task<HttpResponseData> TestLMConnection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/customers/{customerId}/test")] 
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

        try
        {
            var hasCustomCreds = await HasPerCustomerCredentialsAsync(custId);
            var lmService = await GetLogicMonitorServiceAsync(custId);
            if (lmService == null)
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = false,
                    error = "Could not create LogicMonitor service - check credentials"
                });
                return response;
            }

            // Try to get groups as a simple test
            // For customer's own portal: get ALL groups
            // For TIEVA shared portal: filter to Customers group children
            var groups = hasCustomCreds 
                ? await lmService.GetAllGroupsAsync()
                : await lmService.GetCustomerGroupsAsync();
            
            var testResponse = req.CreateResponse(HttpStatusCode.OK);
            await testResponse.WriteAsJsonAsync(new
            {
                success = true,
                message = "Connection successful",
                groupsFound = groups?.Total ?? 0,
                hasPerCustomerCredentials = hasCustomCreds
            });
            return testResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LM connection test failed for customer {CustomerId}", custId);

            var errorResponse = req.CreateResponse(HttpStatusCode.OK);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                error = ex.Message
            });
            return errorResponse;
        }
    }

    /// <summary>
    /// Test LogicMonitor connection with provided credentials (before saving)
    /// </summary>
    [Function("TestLMConnectionWithCredentials")]
    public async Task<HttpResponseData> TestLMConnectionWithCredentials(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/test-credentials")]
        HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<TestCredentialsRequest>(body ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload == null || string.IsNullOrEmpty(payload.Company) ||
                string.IsNullOrEmpty(payload.AccessId) || string.IsNullOrEmpty(payload.AccessKey))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Company, AccessId, and AccessKey are required");
                return badRequest;
            }

            // Create a temporary LM service with the provided credentials
            var lmService = new LogicMonitorService(_loggerFactory, payload.Company, payload.AccessId, payload.AccessKey);

            // Test by getting groups
            var groups = await lmService.GetAllGroupsAsync();

            string? groupPath = null;
            if (payload.GroupId.HasValue && payload.GroupId.Value > 0)
            {
                var group = groups?.Items?.FirstOrDefault(g => g.Id == payload.GroupId.Value);
                groupPath = group?.FullPath ?? group?.Name;
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                message = "Connection successful",
                groupsFound = groups?.Total ?? 0,
                groupPath = groupPath
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LM credential test failed");

            var errorResponse = req.CreateResponse(HttpStatusCode.OK);
            await errorResponse.WriteAsJsonAsync(new
            {
                success = false,
                error = ex.Message
            });
            return errorResponse;
        }
    }

    /// <summary>
    /// Browse LogicMonitor groups with provided credentials (before saving)
    /// </summary>
    [Function("BrowseLMGroupsWithCredentials")]
    public async Task<HttpResponseData> BrowseLMGroupsWithCredentials(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "logicmonitor/browse-groups")]
        HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var payload = JsonSerializer.Deserialize<TestCredentialsRequest>(body ?? "{}",
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (payload == null || string.IsNullOrEmpty(payload.Company) ||
                string.IsNullOrEmpty(payload.AccessId) || string.IsNullOrEmpty(payload.AccessKey))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Company, AccessId, and AccessKey are required");
                return badRequest;
            }

            // Create a temporary LM service with the provided credentials
            var lmService = new LogicMonitorService(_loggerFactory, payload.Company, payload.AccessId, payload.AccessKey);

            // Get all groups
            var groups = await lmService.GetAllGroupsAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                lmGroups = groups?.Items?.Select(g => new
                {
                    id = g.Id,
                    name = g.Name,
                    fullPath = g.FullPath,
                    numOfHosts = g.NumOfHosts
                }).ToList() ?? new List<object>()
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LM browse groups failed");

            var errorResponse = req.CreateResponse(HttpStatusCode.OK);
            await errorResponse.WriteAsJsonAsync(new
            {
                lmGroups = new List<object>(),
                error = ex.Message
            });
            return errorResponse;
        }
    }
}

public class TestCredentialsRequest
{
    public string? Company { get; set; }
    public string? AccessId { get; set; }
    public string? AccessKey { get; set; }
    public int? GroupId { get; set; }
}

public class AckAlertRequest { public string? Comment { get; set; } }
public class SetMappingRequest { public int? LmGroupId { get; set; } }
public class SaveLMConfigRequest 
{ 
    public bool? Enabled { get; set; }
    public int? GroupId { get; set; }
    public string? Company { get; set; }    // LM portal subdomain
    public string? AccessId { get; set; }   // LM API access ID
    public string? AccessKey { get; set; }  // LM API access key (will be stored in Key Vault)
}
