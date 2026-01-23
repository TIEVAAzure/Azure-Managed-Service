using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class TeamFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;

    public TeamFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<TeamFunctions>();
        _db = db;
    }

    /// <summary>
    /// Get all active team members
    /// </summary>
    [Function("GetTeamMembers")]
    public async Task<HttpResponseData> GetTeamMembers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "team")] HttpRequestData req)
    {
        var members = await _db.TeamMembers
            .Where(m => m.IsActive)
            .OrderBy(m => m.Name)
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.Email,
                m.Role,
                m.AzureAdObjectId,
                m.CreatedAt,
                CustomerCount = _db.Customers.Count(c => c.TeamLeadId == m.Id && c.IsActive)
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(members);
        return response;
    }

    /// <summary>
    /// Get a single team member by ID
    /// </summary>
    [Function("GetTeamMember")]
    public async Task<HttpResponseData> GetTeamMember(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "team/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var memberId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid team member ID");
            return badRequest;
        }

        var member = await _db.TeamMembers
            .Where(m => m.Id == memberId)
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.Email,
                m.Role,
                m.AzureAdObjectId,
                m.IsActive,
                m.CreatedAt,
                m.UpdatedAt,
                Customers = _db.Customers
                    .Where(c => c.TeamLeadId == m.Id && c.IsActive)
                    .Select(c => new { c.Id, c.Name, c.Code })
                    .ToList()
            })
            .FirstOrDefaultAsync();

        if (member == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Team member not found");
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(member);
        return response;
    }

    /// <summary>
    /// Create a new team member
    /// </summary>
    [Function("CreateTeamMember")]
    public async Task<HttpResponseData> CreateTeamMember(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "team")] HttpRequestData req)
    {
        try
        {
            var body = await req.ReadAsStringAsync();
            var input = JsonSerializer.Deserialize<TeamMemberInput>(body ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (input == null || string.IsNullOrWhiteSpace(input.Name) || string.IsNullOrWhiteSpace(input.Email))
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Name and Email are required");
                return badRequest;
            }

            // Check if email already exists
            var existingMember = await _db.TeamMembers.FirstOrDefaultAsync(m => m.Email == input.Email);
            if (existingMember != null)
            {
                var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                await conflict.WriteStringAsync("A team member with this email already exists");
                return conflict;
            }

            var member = new TeamMember
            {
                Id = Guid.NewGuid(),
                Name = input.Name.Trim(),
                Email = input.Email.Trim().ToLower(),
                Role = input.Role?.Trim(),
                AzureAdObjectId = input.AzureAdObjectId,
                IsActive = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _db.TeamMembers.Add(member);
            await _db.SaveChangesAsync();

            _logger.LogInformation("Created team member: {Name} ({Email})", member.Name, member.Email);

            var response = req.CreateResponse(HttpStatusCode.Created);
            await response.WriteAsJsonAsync(new { member.Id, member.Name, member.Email, member.Role });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating team member");
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync($"Error creating team member: {ex.Message}");
            return error;
        }
    }

    /// <summary>
    /// Update an existing team member
    /// </summary>
    [Function("UpdateTeamMember")]
    public async Task<HttpResponseData> UpdateTeamMember(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "team/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var memberId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid team member ID");
            return badRequest;
        }

        var member = await _db.TeamMembers.FindAsync(memberId);
        if (member == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Team member not found");
            return notFound;
        }

        try
        {
            var body = await req.ReadAsStringAsync();
            var input = JsonSerializer.Deserialize<TeamMemberInput>(body ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (input == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Invalid request body");
                return badRequest;
            }

            // Check if new email already exists (for a different member)
            if (!string.IsNullOrWhiteSpace(input.Email) && input.Email.Trim().ToLower() != member.Email)
            {
                var existingMember = await _db.TeamMembers.FirstOrDefaultAsync(m => m.Email == input.Email.Trim().ToLower() && m.Id != memberId);
                if (existingMember != null)
                {
                    var conflict = req.CreateResponse(HttpStatusCode.Conflict);
                    await conflict.WriteStringAsync("A team member with this email already exists");
                    return conflict;
                }
            }

            if (!string.IsNullOrWhiteSpace(input.Name)) member.Name = input.Name.Trim();
            if (!string.IsNullOrWhiteSpace(input.Email)) member.Email = input.Email.Trim().ToLower();
            if (input.Role != null) member.Role = input.Role.Trim();
            if (input.AzureAdObjectId != null) member.AzureAdObjectId = input.AzureAdObjectId;
            member.UpdatedAt = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            _logger.LogInformation("Updated team member: {Id} ({Name})", member.Id, member.Name);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { member.Id, member.Name, member.Email, member.Role });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating team member {Id}", memberId);
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync($"Error updating team member: {ex.Message}");
            return error;
        }
    }

    /// <summary>
    /// Delete (soft-delete) a team member
    /// </summary>
    [Function("DeleteTeamMember")]
    public async Task<HttpResponseData> DeleteTeamMember(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "team/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var memberId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid team member ID");
            return badRequest;
        }

        var member = await _db.TeamMembers.FindAsync(memberId);
        if (member == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Team member not found");
            return notFound;
        }

        // Soft delete - mark as inactive
        member.IsActive = false;
        member.UpdatedAt = DateTime.UtcNow;

        // Remove team lead assignments for this member
        var customersWithThisLead = await _db.Customers.Where(c => c.TeamLeadId == memberId).ToListAsync();
        foreach (var customer in customersWithThisLead)
        {
            customer.TeamLeadId = null;
            customer.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted team member: {Id} ({Name}), unassigned from {Count} customers", member.Id, member.Name, customersWithThisLead.Count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { success = true, unassignedCustomers = customersWithThisLead.Count });
        return response;
    }

    /// <summary>
    /// Assign a team lead to a customer
    /// </summary>
    [Function("AssignTeamLead")]
    public async Task<HttpResponseData> AssignTeamLead(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "customers/{customerId}/team-lead")] HttpRequestData req,
        string customerId)
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
            var body = await req.ReadAsStringAsync();
            var input = JsonSerializer.Deserialize<AssignTeamLeadInput>(body ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (input?.TeamLeadId != null)
            {
                // Verify the team member exists and is active
                var teamMember = await _db.TeamMembers.FirstOrDefaultAsync(m => m.Id == input.TeamLeadId && m.IsActive);
                if (teamMember == null)
                {
                    var notFoundMember = req.CreateResponse(HttpStatusCode.NotFound);
                    await notFoundMember.WriteStringAsync("Team member not found or inactive");
                    return notFoundMember;
                }

                customer.TeamLeadId = input.TeamLeadId;
                _logger.LogInformation("Assigned team lead {TeamLeadName} to customer {CustomerName}", teamMember.Name, customer.Name);
            }
            else
            {
                // Unassign team lead
                customer.TeamLeadId = null;
                _logger.LogInformation("Unassigned team lead from customer {CustomerName}", customer.Name);
            }

            customer.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, teamLeadId = customer.TeamLeadId });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error assigning team lead to customer {CustomerId}", customerId);
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync($"Error assigning team lead: {ex.Message}");
            return error;
        }
    }

    // Input DTOs
    private class TeamMemberInput
    {
        public string? Name { get; set; }
        public string? Email { get; set; }
        public string? Role { get; set; }
        public string? AzureAdObjectId { get; set; }
    }

    private class AssignTeamLeadInput
    {
        public Guid? TeamLeadId { get; set; }
    }
}
