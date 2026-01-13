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

    private async Task<LogicMonitorService?> GetLogicMonitorServiceAsync()
    {
        try
        {
            var client = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            
            var companyTask = client.GetSecretAsync("LM-Company");
            var accessIdTask = client.GetSecretAsync("LM-AccessId");
            var accessKeyTask = client.GetSecretAsync("LM-AccessKey");
            
            await Task.WhenAll(companyTask, accessIdTask, accessKeyTask);
            
            return new LogicMonitorService(
                _loggerFactory,
                companyTask.Result.Value.Value,
                accessIdTask.Result.Value.Value,
                accessKeyTask.Result.Value.Value
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve LogicMonitor credentials from Key Vault");
            return null;
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

        var allowedTiers = new[] { "Premium", "Standard", "AdHoc" };
        var hasAccess = customer.Connections
            .SelectMany(c => c.Subscriptions)
            .Any(s => s.Tier != null && allowedTiers.Contains(s.Tier.Name));

        if (!hasAccess)
            return (null, "LogicMonitor access requires Advanced or Premium tier");

        if (!customer.LogicMonitorGroupId.HasValue)
            return (null, "LogicMonitor not configured for this customer");

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

        var lmService = await GetLogicMonitorServiceAsync();
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var devices = await lmService.GetDevicesForGroupAsync(customer.LogicMonitorGroupId!.Value);
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            lmGroupId = customer.LogicMonitorGroupId,
            totalDevices = devices?.Total ?? 0,
            devices = devices?.Items.Select(d => new { d.Id, d.DisplayName, d.HostStatus, d.AlertStatus }) ?? Enumerable.Empty<object>()
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

        var lmService = await GetLogicMonitorServiceAsync();
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var groupInfo = await lmService.GetDeviceGroupAsync(customer.LogicMonitorGroupId!.Value);
        
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

        var lmService = await GetLogicMonitorServiceAsync();
        if (lmService == null)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await errorResponse.WriteStringAsync("LogicMonitor service unavailable");
            return errorResponse;
        }

        var alerts = await lmService.GetActiveAlertsAsync(customer.LogicMonitorGroupId);
        
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            customerId = customer.Id,
            customerName = customer.Name,
            totalAlerts = alerts?.Total ?? 0,
            summary = new
            {
                critical = alerts?.Items.Count(a => a.Severity == 4) ?? 0,
                error = alerts?.Items.Count(a => a.Severity == 3) ?? 0,
                warning = alerts?.Items.Count(a => a.Severity == 2) ?? 0,
                info = alerts?.Items.Count(a => a.Severity <= 1) ?? 0
            },
            alerts = alerts?.Items.Select(a => new
            {
                a.Id, a.DeviceDisplayName, a.MonitorObjectName, a.AlertValue,
                severity = a.SeverityText, severityLevel = a.Severity,
                startTime = a.StartTime, a.ResourceTemplateName, a.Acked, a.InSDT
            }) ?? Enumerable.Empty<object>()
        });
        return response;
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

        var lmService = await GetLogicMonitorServiceAsync();
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
        var payload = JsonSerializer.Deserialize<SetMappingRequest>(body ?? "{}");
        
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
}

public class AckAlertRequest { public string? Comment { get; set; } }
public class SetMappingRequest { public int? LmGroupId { get; set; } }
