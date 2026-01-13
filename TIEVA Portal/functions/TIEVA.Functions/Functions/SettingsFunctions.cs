using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class SettingsFunctions
{
    private readonly TievaDbContext _db;
    private readonly ILogger<SettingsFunctions> _logger;

    public SettingsFunctions(TievaDbContext db, ILogger<SettingsFunctions> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ========================================================================
    // FINDING METADATA CRUD (unified effort, impact, and operational metadata)
    // ========================================================================

    [Function("GetFindingMetadata")]
    public async Task<HttpResponseData> GetFindingMetadata(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/metadata")] HttpRequestData req)
    {
        var metadata = await _db.FindingMetadata
            .OrderByDescending(m => m.MatchPriority)
            .ThenBy(m => m.ModuleCode)
            .ThenBy(m => m.Category)
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(metadata);
        return response;
    }

    [Function("GetFindingMetadataById")]
    public async Task<HttpResponseData> GetFindingMetadataById(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/metadata/{id:int}")] HttpRequestData req,
        int id)
    {
        var metadata = await _db.FindingMetadata.FindAsync(id);
        if (metadata == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Finding metadata not found" });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(metadata);
        return response;
    }

    [Function("CreateFindingMetadata")]
    public async Task<HttpResponseData> CreateFindingMetadata(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "settings/metadata")] HttpRequestData req)
    {
        var metadata = await req.ReadFromJsonAsync<FindingMetadata>();
        if (metadata == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = "Invalid request body" });
            return badRequest;
        }

        metadata.CreatedAt = DateTime.UtcNow;
        metadata.UpdatedAt = DateTime.UtcNow;

        _db.FindingMetadata.Add(metadata);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created finding metadata {Id}: {Module}/{Category}", metadata.Id, metadata.ModuleCode, metadata.Category);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(metadata);
        return response;
    }

    [Function("UpdateFindingMetadata")]
    public async Task<HttpResponseData> UpdateFindingMetadata(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "settings/metadata/{id:int}")] HttpRequestData req,
        int id)
    {
        var existing = await _db.FindingMetadata.FindAsync(id);
        if (existing == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Finding metadata not found" });
            return notFound;
        }

        var update = await req.ReadFromJsonAsync<FindingMetadata>();
        if (update == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = "Invalid request body" });
            return badRequest;
        }

        // Update fields - Pattern Matching
        existing.ModuleCode = update.ModuleCode;
        existing.Category = update.Category;
        existing.FindingPattern = update.FindingPattern;
        existing.RecommendationPattern = update.RecommendationPattern;
        
        // Update fields - Effort & Impact
        existing.BaseHours = update.BaseHours;
        existing.PerResourceHours = update.PerResourceHours;
        existing.ImpactOverride = update.ImpactOverride;
        existing.DefaultOwner = update.DefaultOwner;
        
        // Update fields - Operational Metadata
        existing.DowntimeRequired = update.DowntimeRequired;
        existing.DowntimeMinutes = update.DowntimeMinutes;
        existing.ChangeControlRequired = update.ChangeControlRequired;
        existing.MaintenanceWindowRequired = update.MaintenanceWindowRequired;
        existing.AffectsProduction = update.AffectsProduction;
        existing.CostImplication = update.CostImplication;
        existing.Complexity = update.Complexity;
        existing.RiskLevel = update.RiskLevel;
        existing.Notes = update.Notes;
        
        // Update fields - Control
        existing.MatchPriority = update.MatchPriority;
        existing.IsActive = update.IsActive;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedBy = update.UpdatedBy;

        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated finding metadata {Id}", id);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(existing);
        return response;
    }

    [Function("DeleteFindingMetadata")]
    public async Task<HttpResponseData> DeleteFindingMetadata(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "settings/metadata/{id:int}")] HttpRequestData req,
        int id)
    {
        var metadata = await _db.FindingMetadata.FindAsync(id);
        if (metadata == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "Finding metadata not found" });
            return notFound;
        }

        _db.FindingMetadata.Remove(metadata);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted finding metadata {Id}", id);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    [Function("GetDistinctModuleCodes")]
    public async Task<HttpResponseData> GetDistinctModuleCodes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "settings/metadata/modules")] HttpRequestData req)
    {
        // Get distinct module codes from AssessmentModules
        var modules = await _db.AssessmentModules
            .Where(m => m.IsActive)
            .Select(m => new { m.Code, m.Name })
            .OrderBy(m => m.Code)
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(modules);
        return response;
    }
}
