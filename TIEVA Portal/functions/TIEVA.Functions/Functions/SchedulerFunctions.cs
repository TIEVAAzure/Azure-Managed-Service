using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class SchedulerFunctions
{
    private readonly TievaDbContext _db;
    private readonly ILogger<SchedulerFunctions> _logger;

    public SchedulerFunctions(TievaDbContext db, ILogger<SchedulerFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    private int GetFrequencyDays(string frequency)
    {
        return frequency switch
        {
            "Weekly" => 7,
            "Monthly" => 30,
            "Quarterly" => 90,
            _ => 30
        };
    }

    /// <summary>
    /// Get scheduling status for all customers - shows what's due
    /// </summary>
    [Function("GetSchedulingStatus")]
    public async Task<HttpResponseData> GetSchedulingStatus(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = "scheduler/status")] HttpRequestData req)
    {
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);

        var customers = await _db.Customers
            .Include(c => c.Connections)
                .ThenInclude(conn => conn.Subscriptions)
                    .ThenInclude(s => s.Tier)
                        .ThenInclude(t => t!.TierModules)
                            .ThenInclude(tm => tm.Module)
            .Where(c => c.IsActive)
            .ToListAsync();

        var statuses = new List<object>();

        foreach (var customer in customers)
        {
            var connection = customer.Connections.FirstOrDefault(c => c.IsActive);
            
            int? daysUntilMeeting = null;
            bool preMeetingDue = false;
            if (customer.NextMeetingDate.HasValue)
            {
                var meetingDate = DateOnly.FromDateTime(customer.NextMeetingDate.Value);
                daysUntilMeeting = meetingDate.DayNumber - today.DayNumber;
                
                if (daysUntilMeeting <= 3 && daysUntilMeeting >= 0)
                {
                    var lastAny = await _db.Assessments
                        .Where(a => a.CustomerId == customer.Id && a.Status == "Completed")
                        .OrderByDescending(a => a.CompletedAt)
                        .FirstOrDefaultAsync();

                    var daysSinceLastAny = lastAny?.CompletedAt != null
                        ? (now - lastAny.CompletedAt.Value).TotalDays
                        : (double?)null;
                    
                    preMeetingDue = !daysSinceLastAny.HasValue || daysSinceLastAny.Value > 7;
                }
            }

            var subscriptionDetails = new List<object>();
            int totalModulesDue = 0;

            if (connection != null)
            {
                foreach (var sub in connection.Subscriptions.Where(s => s.IsInScope && s.Tier != null))
                {
                    var moduleStatuses = new List<object>();
                    
                    foreach (var tm in sub.Tier!.TierModules.Where(t => t.IsIncluded))
                    {
                        var moduleCode = tm.Module?.Code ?? "?";
                        var frequencyDays = GetFrequencyDays(tm.Frequency);

                        var lastResult = await _db.AssessmentModuleResults
                            .Where(mr => mr.Assessment!.CustomerId == customer.Id
                                && mr.SubscriptionId == sub.SubscriptionId
                                && mr.ModuleCode == moduleCode
                                && mr.Status == "Completed")
                            .OrderByDescending(mr => mr.CompletedAt)
                            .FirstOrDefaultAsync();

                        var daysSince = lastResult?.CompletedAt != null
                            ? (now - lastResult.CompletedAt.Value).TotalDays
                            : (double?)null;

                        var daysUntilDue = daysSince.HasValue ? frequencyDays - daysSince.Value : 0;
                        var isDue = !daysSince.HasValue || daysUntilDue <= 0;

                        if (isDue) totalModulesDue++;

                        moduleStatuses.Add(new
                        {
                            ModuleCode = moduleCode,
                            Frequency = tm.Frequency,
                            FrequencyDays = frequencyDays,
                            LastRun = lastResult?.CompletedAt,
                            DaysSinceLastRun = daysSince.HasValue ? Math.Round(daysSince.Value, 1) : (double?)null,
                            DaysUntilDue = Math.Round(daysUntilDue, 1),
                            IsDue = isDue
                        });
                    }

                    subscriptionDetails.Add(new
                    {
                        SubscriptionId = sub.SubscriptionId,
                        SubscriptionName = sub.SubscriptionName,
                        TierName = sub.Tier!.DisplayName ?? sub.Tier.Name,
                        Modules = moduleStatuses
                    });
                }
            }

            statuses.Add(new
            {
                CustomerId = customer.Id,
                CustomerName = customer.Name,
                SchedulingEnabled = customer.SchedulingEnabled,
                HasActiveConnection = connection != null,
                NextMeetingDate = customer.NextMeetingDate,
                DaysUntilMeeting = daysUntilMeeting,
                PreMeetingDue = preMeetingDue,
                TotalModulesDue = totalModulesDue,
                Subscriptions = subscriptionDetails
            });
        }

        var sorted = statuses
            .OrderByDescending(s => ((dynamic)s).TotalModulesDue)
            .ThenBy(s => ((dynamic)s).CustomerName)
            .ToList();

        var response = req.CreateResponse(System.Net.HttpStatusCode.OK);
        await response.WriteAsJsonAsync(sorted);
        return response;
    }
}
