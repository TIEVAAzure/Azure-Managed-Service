using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class SubscriptionFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;

    public SubscriptionFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<SubscriptionFunctions>();
        _db = db;
    }

    [Function("GetSubscriptions")]
    public async Task<HttpResponseData> GetSubscriptions(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "subscriptions")] HttpRequestData req)
    {
        var subscriptions = await _db.CustomerSubscriptions
            .Where(s => s.IsInScope)
            .OrderBy(s => s.SubscriptionName)
            .Select(s => new
            {
                s.Id,
                s.ConnectionId,
                s.SubscriptionId,
                s.SubscriptionName,
                s.TierId,
                TierName = s.Tier != null ? s.Tier.DisplayName : null,
                TierColor = s.Tier != null ? s.Tier.Color : null,
                s.Environment,
                s.IsInScope,
                CustomerName = s.Connection!.Customer!.Name
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(subscriptions);
        return response;
    }

    [Function("UpdateSubscription")]
    public async Task<HttpResponseData> UpdateSubscription(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "subscriptions/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var subId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid subscription ID");
            return badRequest;
        }

        var subscription = await _db.CustomerSubscriptions.FindAsync(subId);
        if (subscription == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Subscription not found");
            return notFound;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            
            // Handle tierId - can be null, empty string, or valid Guid
            if (root.TryGetProperty("tierId", out var tierIdElement))
            {
                if (tierIdElement.ValueKind == JsonValueKind.Null || 
                    (tierIdElement.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(tierIdElement.GetString())))
                {
                    subscription.TierId = null;
                }
                else if (tierIdElement.ValueKind == JsonValueKind.String && 
                         Guid.TryParse(tierIdElement.GetString(), out var tierId))
                {
                    subscription.TierId = tierId == Guid.Empty ? null : tierId;
                }
            }
            
            // Handle environment
            if (root.TryGetProperty("environment", out var envElement) && 
                envElement.ValueKind == JsonValueKind.String)
            {
                var env = envElement.GetString();
                if (!string.IsNullOrEmpty(env))
                    subscription.Environment = env;
            }
            
            // Handle isInScope
            if (root.TryGetProperty("isInScope", out var scopeElement))
            {
                if (scopeElement.ValueKind == JsonValueKind.True)
                    subscription.IsInScope = true;
                else if (scopeElement.ValueKind == JsonValueKind.False)
                    subscription.IsInScope = false;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse subscription update");
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid JSON: " + ex.Message);
            return badRequest;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { subscription.Id, subscription.TierId, subscription.Environment, subscription.IsInScope });
        return response;
    }

    [Function("BulkUpdateSubscriptions")]
    public async Task<HttpResponseData> BulkUpdateSubscriptions(
        [HttpTrigger(AuthorizationLevel.Function, "put", Route = "connections/{connectionId}/subscriptions")] HttpRequestData req,
        string connectionId)
    {
        if (!Guid.TryParse(connectionId, out var connId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var updates = JsonSerializer.Deserialize<List<SubscriptionUpdate>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (updates == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body");
            return badRequest;
        }

        var subscriptions = await _db.CustomerSubscriptions
            .Where(s => s.ConnectionId == connId)
            .ToListAsync();

        var updated = 0;
        foreach (var update in updates)
        {
            var sub = subscriptions.FirstOrDefault(s => s.Id == update.Id);
            if (sub != null)
            {
                if (update.TierId.HasValue)
                    sub.TierId = update.TierId.Value == Guid.Empty ? null : update.TierId.Value;
                if (update.Environment != null)
                    sub.Environment = update.Environment;
                if (update.IsInScope.HasValue)
                    sub.IsInScope = update.IsInScope.Value;
                updated++;
            }
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { updated });
        return response;
    }

    [Function("GetAuditSubs")]
    public async Task<HttpResponseData> GetAuditSubs(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "audit-subs/{connectionId}/{moduleCode}")] HttpRequestData req,
        string connectionId,
        string moduleCode)
    {
        if (!Guid.TryParse(connectionId, out var connId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        try
        {
            var moduleUpper = moduleCode.ToUpper();
            var module = await _db.AssessmentModules
                .FirstOrDefaultAsync(m => m.Code == moduleUpper);
            
            if (module == null)
            {
                var emptyResponse = req.CreateResponse(HttpStatusCode.OK);
                await emptyResponse.WriteAsJsonAsync(new List<object>());
                return emptyResponse;
            }

            var subscriptions = await _db.CustomerSubscriptions
                .Where(s => s.ConnectionId == connId && s.IsInScope && s.TierId != null)
                .Include(s => s.Tier)
                .ToListAsync();

            var tierModules = await _db.TierModules
                .Where(tm => tm.ModuleId == module.Id && tm.IsIncluded)
                .ToListAsync();

            var enabledTierIds = tierModules.Select(tm => tm.TierId).ToHashSet();

            var result = new List<object>();
            foreach (var s in subscriptions)
            {
                if (s.TierId.HasValue && enabledTierIds.Contains(s.TierId.Value))
                {
                    var tm = tierModules.FirstOrDefault(t => t.TierId == s.TierId);
                    result.Add(new
                    {
                        s.Id,
                        s.SubscriptionId,
                        s.SubscriptionName,
                        s.Environment,
                        TierId = s.TierId,
                        TierName = s.Tier?.DisplayName ?? "Unknown",
                        TierColor = s.Tier?.Color ?? "#6b7280",
                        Frequency = tm?.Frequency ?? "Monthly"
                    });
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(result);
            return response;
        }
        catch (Exception ex)
        {
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }

    // GetSubscriptionsForAudit moved to ConnectionFunctions.cs as GetAuditSubscriptions

    [Function("DebugAuditSubscriptions")]
    public async Task<HttpResponseData> DebugAuditSubscriptions(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "debug/audit-subs/{connectionId}/{moduleCode}")] HttpRequestData req,
        string connectionId,
        string moduleCode)
    {
        var debug = new Dictionary<string, object>();
        
        try
        {
            // 1. Parse connection ID
            if (!Guid.TryParse(connectionId, out var connId))
            {
                debug["error"] = "Invalid connection ID";
                var badResp = req.CreateResponse(HttpStatusCode.OK);
                await badResp.WriteAsJsonAsync(debug);
                return badResp;
            }
            debug["connectionId"] = connId.ToString();
            debug["moduleCode"] = moduleCode;

            // 2. Find module
            var moduleUpper = moduleCode.ToUpper();
            var allModules = await _db.AssessmentModules.ToListAsync();
            debug["allModules"] = allModules.Select(m => new { m.Id, m.Code, m.Name }).ToList();
            
            var module = allModules.FirstOrDefault(m => m.Code == moduleUpper);
            debug["foundModule"] = module != null ? new { module.Id, module.Code, module.Name } : null;

            if (module == null)
            {
                var resp = req.CreateResponse(HttpStatusCode.OK);
                await resp.WriteAsJsonAsync(debug);
                return resp;
            }

            // 3. Get all subscriptions for this connection
            var allSubs = await _db.CustomerSubscriptions
                .Where(s => s.ConnectionId == connId)
                .ToListAsync();
            debug["allSubscriptionsForConnection"] = allSubs.Select(s => new { 
                s.Id, s.SubscriptionName, s.TierId, s.IsInScope 
            }).ToList();

            // 4. Filter to in-scope with tier
            var inScopeSubs = allSubs.Where(s => s.IsInScope && s.TierId != null).ToList();
            debug["inScopeWithTier"] = inScopeSubs.Select(s => new { 
                s.Id, s.SubscriptionName, s.TierId 
            }).ToList();

            // 5. Get all tier modules
            var allTierModules = await _db.TierModules.ToListAsync();
            debug["allTierModulesCount"] = allTierModules.Count;

            // 6. Get tier modules for this specific module
            var tierModulesForModule = allTierModules
                .Where(tm => tm.ModuleId == module.Id && tm.IsIncluded)
                .ToList();
            debug["tierModulesForThisModule"] = tierModulesForModule.Select(tm => new { 
                tm.Id, tm.TierId, tm.ModuleId, tm.IsIncluded, tm.Frequency 
            }).ToList();

            // 7. Get enabled tier IDs
            var enabledTierIds = tierModulesForModule.Select(tm => tm.TierId).ToHashSet();
            debug["enabledTierIds"] = enabledTierIds.ToList();

            // 8. Match subscriptions
            var matchedSubs = inScopeSubs
                .Where(s => s.TierId.HasValue && enabledTierIds.Contains(s.TierId.Value))
                .ToList();
            debug["matchedSubscriptions"] = matchedSubs.Select(s => new { 
                s.Id, s.SubscriptionId, s.SubscriptionName, s.TierId 
            }).ToList();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(debug);
            return response;
        }
        catch (Exception ex)
        {
            debug["exception"] = ex.Message;
            debug["stackTrace"] = ex.StackTrace;
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(debug);
            return response;
        }
    }

}

public class UpdateSubscriptionRequest
{
    public Guid? TierId { get; set; }
    public string? Environment { get; set; }
    public bool? IsInScope { get; set; }
}

public class SubscriptionUpdate
{
    public Guid Id { get; set; }
    public Guid? TierId { get; set; }
    public string? Environment { get; set; }
    public bool? IsInScope { get; set; }
}
