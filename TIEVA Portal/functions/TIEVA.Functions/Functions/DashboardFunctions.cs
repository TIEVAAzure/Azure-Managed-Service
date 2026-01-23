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

    [Function("GetDashboardStats")]
    public async Task<HttpResponseData> GetDashboardStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/stats")] HttpRequestData req)
    {
        // Get unique findings from CustomerFindings table (deduplicated across all assessments)
        var customerFindingsStats = await _db.CustomerFindings
            .Where(cf => cf.Status == "Open")
            .GroupBy(cf => cf.Severity.ToLower())
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync();

        var high = customerFindingsStats.FirstOrDefault(x => x.Severity == "high")?.Count ?? 0;
        var medium = customerFindingsStats.FirstOrDefault(x => x.Severity == "medium")?.Count ?? 0;
        var low = customerFindingsStats.FirstOrDefault(x => x.Severity == "low")?.Count ?? 0;

        // Get customer count and assessment count
        var customerCount = await _db.Customers.CountAsync();
        var assessmentCount = await _db.Assessments.CountAsync();

        // Get average score from latest assessment per customer
        var latestAssessmentScores = await _db.Assessments
            .Where(a => a.ScoreOverall != null)
            .GroupBy(a => a.CustomerId)
            .Select(g => g.OrderByDescending(a => a.CreatedAt).First().ScoreOverall)
            .ToListAsync();

        var avgScore = latestAssessmentScores.Any() 
            ? Math.Round(latestAssessmentScores.Average(s => s ?? 0), 0) 
            : 0;

        var stats = new
        {
            customers = customerCount,
            assessments = assessmentCount,
            findings = new
            {
                total = high + medium + low,
                high,
                medium,
                low
            },
            avgScore
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(stats);
        return response;
    }

    /// <summary>
    /// Get expiring reservations across all customers (for dashboard)
    /// </summary>
    [Function("GetExpiringReservations")]
    public async Task<HttpResponseData> GetExpiringReservations(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/expiring-reservations")] HttpRequestData req)
    {
        var expiringReservations = new List<object>();

        // Get all reservation caches
        var caches = await _db.CustomerReservationCache
            .Include(c => c.Customer)
            .Where(c => c.Customer!.IsActive && !string.IsNullOrEmpty(c.ReservationsJson))
            .ToListAsync();

        foreach (var cache in caches)
        {
            try
            {
                var reservations = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, object>>>(cache.ReservationsJson ?? "[]");
                if (reservations == null) continue;

                foreach (var res in reservations)
                {
                    // Get days to expiry
                    int daysToExpiry = 9999;
                    if (res.TryGetValue("DaysToExpiry", out var dte) && dte is System.Text.Json.JsonElement dteEl)
                    {
                        daysToExpiry = dteEl.TryGetInt32(out var d) ? d : 9999;
                    }

                    // Only include reservations expiring within 90 days (and not already expired beyond grace period)
                    if (daysToExpiry >= -30 && daysToExpiry <= 90)
                    {
                        string? displayName = null;
                        string? skuName = null;
                        string? expiryDate = null;
                        double? utilization = null;
                        string? term = null;
                        int? quantity = null;

                        if (res.TryGetValue("DisplayName", out var dn) && dn is System.Text.Json.JsonElement dnEl)
                            displayName = dnEl.GetString();
                        if (res.TryGetValue("SkuName", out var sn) && sn is System.Text.Json.JsonElement snEl)
                            skuName = snEl.GetString();
                        if (res.TryGetValue("ExpiryDate", out var ed) && ed is System.Text.Json.JsonElement edEl)
                            expiryDate = edEl.GetString();
                        if (res.TryGetValue("Utilization30Day", out var u30) && u30 is System.Text.Json.JsonElement u30El)
                            utilization = u30El.TryGetDouble(out var u) ? u : null;
                        if (res.TryGetValue("Term", out var t) && t is System.Text.Json.JsonElement tEl)
                            term = tEl.GetString();
                        if (res.TryGetValue("Quantity", out var q) && q is System.Text.Json.JsonElement qEl)
                            quantity = qEl.TryGetInt32(out var qi) ? qi : null;

                        expiringReservations.Add(new
                        {
                            customerId = cache.CustomerId,
                            customerName = cache.Customer?.Name,
                            displayName,
                            skuName,
                            expiryDate,
                            daysToExpiry,
                            utilization,
                            term,
                            quantity
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing reservations for customer {CustomerId}", cache.CustomerId);
            }
        }

        // Sort by days to expiry (most urgent first)
        var sorted = expiringReservations
            .OrderBy(r => ((dynamic)r).daysToExpiry)
            .ToList();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            total = sorted.Count,
            reservations = sorted
        });
        return response;
    }

    /// <summary>
    /// Get alert summary across all customers (for dashboard)
    /// </summary>
    [Function("GetAlertSummary")]
    public async Task<HttpResponseData> GetAlertSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "dashboard/alerts")] HttpRequestData req)
    {
        // Get alert counts by severity across all customers
        var alerts = await _db.LMAlerts
            .Include(a => a.Customer)
            .Where(a => a.Customer!.IsActive)
            .ToListAsync();

        var bySeverity = new
        {
            critical = alerts.Count(a => a.Severity == 4),
            error = alerts.Count(a => a.Severity == 3),
            warning = alerts.Count(a => a.Severity == 2),
            info = alerts.Count(a => a.Severity <= 1)
        };

        // Get alerts by customer (top 5 with most critical/error alerts)
        var byCustomer = alerts
            .GroupBy(a => new { a.CustomerId, CustomerName = a.Customer?.Name })
            .Select(g => new
            {
                customerId = g.Key.CustomerId,
                customerName = g.Key.CustomerName,
                critical = g.Count(a => a.Severity == 4),
                error = g.Count(a => a.Severity == 3),
                warning = g.Count(a => a.Severity == 2),
                total = g.Count()
            })
            .OrderByDescending(c => c.critical)
            .ThenByDescending(c => c.error)
            .Take(10)
            .ToList();

        // Get recent alerts (all severities for filtering)
        var recentAlerts = alerts
            .OrderByDescending(a => a.Severity)
            .ThenByDescending(a => a.StartTime)
            .Take(50)
            .Select(a => new
            {
                customerId = a.CustomerId,
                customerName = a.Customer?.Name,
                deviceName = a.DeviceDisplayName ?? a.MonitorObjectName,
                dataSourceName = a.ResourceTemplateName,
                alertValue = a.AlertValue,
                severity = a.Severity,
                severityText = a.Severity == 4 ? "Critical" : a.Severity == 3 ? "Error" : a.Severity == 2 ? "Warning" : "Info",
                startTime = a.StartTime
            })
            .ToList();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            total = alerts.Count,
            bySeverity,
            byCustomer,
            recentAlerts
        });
        return response;
    }
}
