using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class DashboardFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;

    public DashboardFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<DashboardFunctions>();
        _db = db;
    }

    [Function("GetDashboard")]
    public async Task<HttpResponseData> GetDashboard(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard")] HttpRequestData req)
    {
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        var stats = new
        {
            CustomerCount = await _db.Customers.CountAsync(c => c.IsActive),
            SubscriptionCount = await _db.CustomerSubscriptions.CountAsync(s => s.IsInScope),
            ConnectionCount = await _db.AzureConnections.CountAsync(c => c.IsActive),
            AssessmentCount30d = await _db.Assessments.CountAsync(a => a.CreatedAt >= thirtyDaysAgo),
            TierCount = await _db.ServiceTiers.CountAsync(t => t.IsActive),
            ModuleCount = await _db.AssessmentModules.CountAsync(m => m.IsActive),

            RecentAssessments = await _db.Assessments
                .OrderByDescending(a => a.CreatedAt)
                .Take(10)
                .Select(a => new
                {
                    a.Id,
                    CustomerName = a.Customer!.Name,
                    a.Status,
                    a.StartedAt,
                    a.CompletedAt,
                    a.ScoreOverall,
                    a.FindingsHigh,
                    a.FindingsMedium,
                    a.FindingsLow
                })
                .ToListAsync(),

            ExpiringSecrets = await _db.AzureConnections
                .Where(c => c.IsActive && c.SecretExpiry != null && c.SecretExpiry < DateTime.UtcNow.AddDays(30))
                .Select(c => new
                {
                    c.Id,
                    CustomerName = c.Customer!.Name,
                    c.TenantName,
                    c.SecretExpiry
                })
                .ToListAsync(),

            TierSummary = await _db.ServiceTiers
                .Where(t => t.IsActive)
                .OrderBy(t => t.SortOrder)
                .Select(t => new
                {
                    t.Id,
                    t.Name,
                    t.DisplayName,
                    t.Color,
                    ModuleCount = t.TierModules.Count(tm => tm.IsIncluded),
                    SubscriptionCount = _db.CustomerSubscriptions.Count(s => s.TierId == t.Id && s.IsInScope)
                })
                .ToListAsync()
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(stats);
        return response;
    }

    [Function("HealthCheck")]
    public async Task<HttpResponseData> HealthCheck(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "health")] HttpRequestData req)
    {
        try
        {
            // Test database connection
            await _db.Database.CanConnectAsync();
            
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new 
            { 
                Status = "Healthy",
                Database = "Connected",
                Timestamp = DateTime.UtcNow
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health check failed");
            var response = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
            await response.WriteAsJsonAsync(new 
            { 
                Status = "Unhealthy",
                Database = "Disconnected",
                Error = ex.Message,
                Timestamp = DateTime.UtcNow
            });
            return response;
        }
    }
}