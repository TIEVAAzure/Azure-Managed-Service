using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class TierFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;

    public TierFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<TierFunctions>();
        _db = db;
    }

    [Function("GetTiers")]
    public async Task<HttpResponseData> GetTiers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "tiers")] HttpRequestData req)
    {
        var tiers = await _db.ServiceTiers
            .Where(t => t.IsActive)
            .OrderBy(t => t.SortOrder)
            .Select(t => new
            {
                t.Id,
                t.Name,
                t.DisplayName,
                t.Description,
                t.Color,
                t.SortOrder,
                ModuleCount = t.TierModules.Count(tm => tm.IsIncluded),
                SubscriptionCount = _db.CustomerSubscriptions.Count(s => s.TierId == t.Id && s.IsInScope),
                TierModules = t.TierModules
                    .OrderBy(tm => tm.Module!.SortOrder)
                    .Select(tm => new
                    {
                        ModuleId = tm.ModuleId,
                        tm.Module!.Code,
                        tm.Module.Name,
                        tm.Module.Icon,
                        tm.IsIncluded,
                        tm.Frequency
                    }).ToList()
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(tiers);
        return response;
    }

    [Function("GetModules")]
    public async Task<HttpResponseData> GetModules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "modules")] HttpRequestData req)
    {
        var modules = await _db.AssessmentModules
            .Where(m => m.IsActive)
            .OrderBy(m => m.SortOrder)
            .Select(m => new
            {
                m.Id,
                m.Code,
                m.Name,
                m.Description,
                m.Icon,
                m.Category,
                m.EstimatedMinutes
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(modules);
        return response;
    }

    [Function("UpdateTier")]
    public async Task<HttpResponseData> UpdateTier(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "tiers/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var tierId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid tier ID");
            return badRequest;
        }

        var tier = await _db.ServiceTiers.FindAsync(tierId);
        if (tier == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Tier not found");
            return notFound;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<UpdateTierRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input != null)
        {
            if (!string.IsNullOrWhiteSpace(input.DisplayName)) tier.DisplayName = input.DisplayName;
            if (input.Description != null) tier.Description = input.Description;
            if (input.Color != null) tier.Color = input.Color;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { tier.Id, tier.Name, tier.DisplayName });
        return response;
    }

    [Function("UpdateTierModules")]
    public async Task<HttpResponseData> UpdateTierModules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "tiers/{id}/modules")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var tierId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid tier ID");
            return badRequest;
        }

        var tier = await _db.ServiceTiers.FindAsync(tierId);
        if (tier == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Tier not found");
            return notFound;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<List<TierModuleUpdate>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body");
            return badRequest;
        }

        // Get existing tier modules
        var existingModules = await _db.TierModules
            .Where(tm => tm.TierId == tierId)
            .ToListAsync();

        foreach (var update in input)
        {
            var existing = existingModules.FirstOrDefault(tm => tm.ModuleId == update.ModuleId);
            if (existing != null)
            {
                existing.IsIncluded = update.IsIncluded;
                existing.Frequency = update.Frequency ?? existing.Frequency;
            }
            else if (update.IsIncluded)
            {
                _db.TierModules.Add(new TierModule
                {
                    Id = Guid.NewGuid(),
                    TierId = tierId,
                    ModuleId = update.ModuleId,
                    IsIncluded = true,
                    Frequency = update.Frequency ?? "Monthly"
                });
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Updated modules for tier {TierId}", tierId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Tier modules updated");
        return response;
    }

    [Function("BulkUpdateTierModules")]
    public async Task<HttpResponseData> BulkUpdateTierModules(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "tiers/modules")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<List<BulkTierModuleUpdate>>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input == null || input.Count == 0)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body");
            return badRequest;
        }

        // Get all existing tier modules
        var existingModules = await _db.TierModules.ToListAsync();

        foreach (var update in input)
        {
            var existing = existingModules.FirstOrDefault(tm => 
                tm.TierId == update.TierId && tm.ModuleId == update.ModuleId);
            
            if (existing != null)
            {
                existing.IsIncluded = update.IsIncluded;
                existing.Frequency = update.Frequency ?? existing.Frequency;
            }
            else if (update.IsIncluded)
            {
                _db.TierModules.Add(new TierModule
                {
                    Id = Guid.NewGuid(),
                    TierId = update.TierId,
                    ModuleId = update.ModuleId,
                    IsIncluded = true,
                    Frequency = update.Frequency ?? "Monthly"
                });
            }
        }

        await _db.SaveChangesAsync();
        _logger.LogInformation("Bulk updated {Count} tier module configurations", input.Count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { updated = input.Count });
        return response;
    }
}

public class UpdateTierRequest
{
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
}

public class TierModuleUpdate
{
    public Guid ModuleId { get; set; }
    public bool IsIncluded { get; set; }
    public string? Frequency { get; set; }
}

public class BulkTierModuleUpdate
{
    public Guid TierId { get; set; }
    public Guid ModuleId { get; set; }
    public bool IsIncluded { get; set; }
    public string? Frequency { get; set; }
}