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
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "subscriptions")] HttpRequestData req)
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
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "subscriptions/{id}")] HttpRequestData req,
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
        var input = JsonSerializer.Deserialize<UpdateSubscriptionRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input != null)
        {
            if (input.TierId.HasValue)
                subscription.TierId = input.TierId.Value == Guid.Empty ? null : input.TierId.Value;
            if (input.Environment != null)
                subscription.Environment = input.Environment;
            if (input.IsInScope.HasValue)
                subscription.IsInScope = input.IsInScope.Value;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { subscription.Id, subscription.TierId, subscription.Environment, subscription.IsInScope });
        return response;
    }

    [Function("BulkUpdateSubscriptions")]
    public async Task<HttpResponseData> BulkUpdateSubscriptions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "connections/{connectionId}/subscriptions")] HttpRequestData req,
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