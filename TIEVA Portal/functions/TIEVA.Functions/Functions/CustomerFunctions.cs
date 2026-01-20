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

public class CustomerFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;
    private readonly string _keyVaultUrl;

    public CustomerFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<CustomerFunctions>();
        _db = db;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    [Function("GetCustomers")]
    public async Task<HttpResponseData> GetCustomers(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers")] HttpRequestData req)
    {
        var customers = await _db.Customers
            .Where(c => c.IsActive)
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Code,
                c.Industry,
                c.PrimaryContact,
                c.Email,
                c.IsActive,
                c.NextMeetingDate,
                c.SchedulingEnabled,
                c.FinOpsStorageAccount,
                c.FinOpsContainer,
                c.FinOpsPowerBIUrl,
                c.FinOpsSasExpiry,
                HasFinOpsSas = !string.IsNullOrEmpty(c.FinOpsSasKeyVaultRef),
                c.LogicMonitorGroupId,
                c.LMEnabled,
                c.LMHasCustomCredentials,
                ConnectionCount = c.Connections.Count(x => x.IsActive),
                SubscriptionCount = c.Connections.Where(x => x.IsActive).SelectMany(x => x.Subscriptions).Count(s => s.IsInScope),
                LastAssessment = c.Assessments.OrderByDescending(a => a.CompletedAt).Select(a => a.CompletedAt).FirstOrDefault(),
                LastScore = c.Assessments.OrderByDescending(a => a.CompletedAt).Select(a => a.ScoreOverall).FirstOrDefault()
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(customers);
        return response;
    }

    [Function("GetCustomer")]
    public async Task<HttpResponseData> GetCustomer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var customerId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var customer = await _db.Customers
            .Where(c => c.Id == customerId)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Code,
                c.Industry,
                c.PrimaryContact,
                c.Email,
                c.Phone,
                c.Notes,
                c.IsActive,
                c.CreatedAt,
                c.NextMeetingDate,
                c.SchedulingEnabled,
                c.FinOpsStorageAccount,
                c.FinOpsContainer,
                c.FinOpsPowerBIUrl,
                c.FinOpsSasExpiry,
                HasFinOpsSas = !string.IsNullOrEmpty(c.FinOpsSasKeyVaultRef),
                c.LogicMonitorGroupId,
                c.LMEnabled,
                c.LMHasCustomCredentials,
                Connections = c.Connections.Where(x => x.IsActive).Select(conn => new
                {
                    conn.Id,
                    conn.TenantId,
                    conn.TenantName,
                    conn.SecretExpiry,
                    conn.LastValidated,
                    conn.LastValidationStatus,
                    SubscriptionCount = conn.Subscriptions.Count(s => s.IsInScope)
                }).ToList(),
                RecentAssessments = c.Assessments.OrderByDescending(a => a.CreatedAt).Take(5).Select(a => new
                {
                    a.Id,
                    a.Status,
                    a.StartedAt,
                    a.CompletedAt,
                    a.ScoreOverall,
                    a.FindingsHigh,
                    a.FindingsMedium,
                    a.FindingsLow
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (customer == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Customer not found");
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(customer);
        return response;
    }

    [Function("CreateCustomer")]
    public async Task<HttpResponseData> CreateCustomer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<CreateCustomerRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input == null || string.IsNullOrWhiteSpace(input.Name))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Name is required");
            return badRequest;
        }

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            Code = input.Code,
            Industry = input.Industry,
            PrimaryContact = input.PrimaryContact,
            Email = input.Email,
            Phone = input.Phone,
            Notes = input.Notes,
            NextMeetingDate = input.NextMeetingDate,
            SchedulingEnabled = input.SchedulingEnabled ?? true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created customer {Name} with ID {Id}", customer.Name, customer.Id);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { customer.Id, customer.Name });
        return response;
    }

    [Function("UpdateCustomer")]
    public async Task<HttpResponseData> UpdateCustomer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "customers/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var customerId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var customer = await _db.Customers.FindAsync(customerId);
        if (customer == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Customer not found");
            return notFound;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<CreateCustomerRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input != null)
        {
            if (!string.IsNullOrWhiteSpace(input.Name)) customer.Name = input.Name;
            if (input.Code != null) customer.Code = input.Code;
            if (input.Industry != null) customer.Industry = input.Industry;
            if (input.PrimaryContact != null) customer.PrimaryContact = input.PrimaryContact;
            if (input.Email != null) customer.Email = input.Email;
            if (input.Phone != null) customer.Phone = input.Phone;
            if (input.Notes != null) customer.Notes = input.Notes;
            // Always update scheduling fields (allow null to clear NextMeetingDate)
            customer.NextMeetingDate = input.NextMeetingDate;
            if (input.SchedulingEnabled.HasValue) customer.SchedulingEnabled = input.SchedulingEnabled.Value;
            
            // FinOps fields (empty string clears the field)
            if (input.FinOpsStorageAccount != null) 
                customer.FinOpsStorageAccount = string.IsNullOrWhiteSpace(input.FinOpsStorageAccount) ? null : input.FinOpsStorageAccount;
            if (input.FinOpsContainer != null) 
                customer.FinOpsContainer = string.IsNullOrWhiteSpace(input.FinOpsContainer) ? null : input.FinOpsContainer;
            if (input.FinOpsPowerBIUrl != null) 
                customer.FinOpsPowerBIUrl = string.IsNullOrWhiteSpace(input.FinOpsPowerBIUrl) ? null : input.FinOpsPowerBIUrl;
            
            // LogicMonitor fields
            if (input.LogicMonitorGroupId.HasValue || input.LogicMonitorGroupId == null)
                customer.LogicMonitorGroupId = input.LogicMonitorGroupId;
            if (input.LMEnabled.HasValue)
                customer.LMEnabled = input.LMEnabled.Value;
            
            customer.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { customer.Id, customer.Name });
        return response;
    }

    [Function("DeleteCustomer")]
    public async Task<HttpResponseData> DeleteCustomer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "customers/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var customerId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        var customer = await _db.Customers
            .Include(c => c.Connections)
                .ThenInclude(conn => conn.Subscriptions)
            .FirstOrDefaultAsync(c => c.Id == customerId);

        if (customer == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Customer not found");
            return notFound;
        }

        // Track deletion counts
        var connectionsDeleted = customer.Connections.Count;
        var subscriptionsDeleted = 0;
        var assessmentsDeleted = 0;
        var findingsDeleted = 0;
        var moduleResultsDeleted = 0;
        var customerFindingsDeleted = 0;

        // Cascade delete: CustomerFindings
        var customerFindings = await _db.CustomerFindings
            .Where(cf => cf.CustomerId == customerId)
            .ToListAsync();
        customerFindingsDeleted = customerFindings.Count;
        _db.CustomerFindings.RemoveRange(customerFindings);

        // Cascade delete: Assessments (with their findings and module results)
        var assessments = await _db.Assessments
            .Include(a => a.ModuleResults)
            .Include(a => a.Findings)
            .Where(a => a.CustomerId == customerId)
            .ToListAsync();

        foreach (var assessment in assessments)
        {
            findingsDeleted += assessment.Findings.Count;
            moduleResultsDeleted += assessment.ModuleResults.Count;
            _db.Findings.RemoveRange(assessment.Findings);
            _db.AssessmentModuleResults.RemoveRange(assessment.ModuleResults);
            assessmentsDeleted++;
        }
        _db.Assessments.RemoveRange(assessments);

        // Cascade delete: Connections and their subscriptions
        var secretRefsToDelete = new List<string>();
        foreach (var connection in customer.Connections)
        {
            subscriptionsDeleted += connection.Subscriptions.Count;
            _db.CustomerSubscriptions.RemoveRange(connection.Subscriptions);
            if (!string.IsNullOrEmpty(connection.SecretKeyVaultRef))
                secretRefsToDelete.Add(connection.SecretKeyVaultRef);
        }
        _db.AzureConnections.RemoveRange(customer.Connections);

        // Delete the customer
        _db.Customers.Remove(customer);

        await _db.SaveChangesAsync();

        // Delete KeyVault secrets for all connections
        if (secretRefsToDelete.Count > 0)
        {
            try
            {
                var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
                foreach (var secretRef in secretRefsToDelete)
                {
                    try
                    {
                        await kvClient.StartDeleteSecretAsync(secretRef);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete secret {SecretRef} from Key Vault", secretRef);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to connect to Key Vault for secret deletion");
            }
        }

        _logger.LogInformation("Deleted customer {Id} ({Name}) with {Connections} connections, {Assessments} assessments, {Findings} findings",
            customerId, customer.Name, connectionsDeleted, assessmentsDeleted, findingsDeleted);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            message = "Customer deleted",
            connectionsDeleted,
            subscriptionsDeleted,
            assessmentsDeleted,
            findingsDeleted,
            moduleResultsDeleted,
            customerFindingsDeleted
        });
        return response;
    }

    [Function("GetCustomerFindings")]
    public async Task<HttpResponseData> GetCustomerFindings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{id}/findings")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var customerId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid customer ID");
            return badRequest;
        }

        // Get all open findings from CustomerFindings (deduplicated across all assessments)
        var findings = await _db.CustomerFindings
            .Where(cf => cf.CustomerId == customerId && cf.Status == "Open")
            .OrderBy(cf => cf.Severity == "High" ? 0 : cf.Severity == "Medium" ? 1 : 2)
            .ThenByDescending(cf => cf.LastSeenAt)
            .Select(cf => new
            {
                cf.Id,
                cf.CustomerId,
                cf.ModuleCode,
                cf.Category,
                cf.Severity,
                cf.FindingText,
                cf.ResourceId,
                cf.ResourceType,
                cf.Recommendation,
                cf.Status,
                cf.FirstSeenAt,
                cf.LastSeenAt,
                cf.OccurrenceCount
            })
            .ToListAsync();

        // Get summary stats
        var high = findings.Count(f => f.Severity?.ToLower() == "high");
        var medium = findings.Count(f => f.Severity?.ToLower() == "medium");
        var low = findings.Count(f => f.Severity?.ToLower() == "low");

        // Get latest module results across all assessments for this customer
        var latestModuleResults = await _db.AssessmentModuleResults
            .Where(mr => mr.Assessment!.CustomerId == customerId && mr.Status == "Completed")
            .GroupBy(mr => mr.ModuleCode)
            .Select(g => new
            {
                ModuleCode = g.Key,
                Score = g.OrderByDescending(mr => mr.CompletedAt).Select(mr => mr.Score).FirstOrDefault(),
                FindingsCount = g.OrderByDescending(mr => mr.CompletedAt).Select(mr => mr.FindingsCount).FirstOrDefault(),
                CompletedAt = g.OrderByDescending(mr => mr.CompletedAt).Select(mr => mr.CompletedAt).FirstOrDefault(),
                AssessmentId = g.OrderByDescending(mr => mr.CompletedAt).Select(mr => mr.AssessmentId).FirstOrDefault()
            })
            .ToListAsync();

        var result = new
        {
            customerId,
            summary = new { total = findings.Count, high, medium, low },
            moduleResults = latestModuleResults,
            findings
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(result);
        return response;
    }

    // ========================================================================
    // ROADMAP PLAN CRUD
    // ========================================================================

    [Function("GetCustomerRoadmapPlan")]
    public async Task<HttpResponseData> GetCustomerRoadmapPlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/roadmap-plan")] HttpRequestData req,
        Guid customerId)
    {
        var plan = await _db.CustomerRoadmapPlans
            .FirstOrDefaultAsync(p => p.CustomerId == customerId);

        if (plan == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "No roadmap plan found for this customer" });
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(plan);
        return response;
    }

    [Function("SaveCustomerRoadmapPlan")]
    public async Task<HttpResponseData> SaveCustomerRoadmapPlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers/{customerId:guid}/roadmap-plan")] HttpRequestData req,
        Guid customerId)
    {
        var input = await req.ReadFromJsonAsync<RoadmapPlanInput>();
        if (input == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = "Invalid request body" });
            return badRequest;
        }

        // Check if plan exists
        var existing = await _db.CustomerRoadmapPlans
            .FirstOrDefaultAsync(p => p.CustomerId == customerId);

        if (existing != null)
        {
            // Update existing
            existing.Wave1Findings = input.Wave1Findings;
            existing.Wave2Findings = input.Wave2Findings;
            existing.Wave3Findings = input.Wave3Findings;
            existing.SkippedFindings = input.SkippedFindings;
            existing.Notes = input.Notes;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedBy = input.UpdatedBy;
        }
        else
        {
            // Create new
            existing = new CustomerRoadmapPlan
            {
                CustomerId = customerId,
                Wave1Findings = input.Wave1Findings,
                Wave2Findings = input.Wave2Findings,
                Wave3Findings = input.Wave3Findings,
                SkippedFindings = input.SkippedFindings,
                Notes = input.Notes,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = input.UpdatedBy
            };
            _db.CustomerRoadmapPlans.Add(existing);
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Saved roadmap plan for customer {CustomerId}", customerId);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(existing);
        return response;
    }

    [Function("DeleteCustomerRoadmapPlan")]
    public async Task<HttpResponseData> DeleteCustomerRoadmapPlan(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "customers/{customerId:guid}/roadmap-plan")] HttpRequestData req,
        Guid customerId)
    {
        var plan = await _db.CustomerRoadmapPlans
            .FirstOrDefaultAsync(p => p.CustomerId == customerId);

        if (plan == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteAsJsonAsync(new { error = "No roadmap plan found" });
            return notFound;
        }

        _db.CustomerRoadmapPlans.Remove(plan);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted roadmap plan for customer {CustomerId}", customerId);

        return req.CreateResponse(HttpStatusCode.NoContent);
    }
}

public class RoadmapPlanInput
{
    public string? Wave1Findings { get; set; }
    public string? Wave2Findings { get; set; }
    public string? Wave3Findings { get; set; }
    public string? SkippedFindings { get; set; }
    public string? Notes { get; set; }
    public string? UpdatedBy { get; set; }
}

public class CreateCustomerRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Code { get; set; }
    public string? Industry { get; set; }
    public string? PrimaryContact { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Notes { get; set; }
    public DateTime? NextMeetingDate { get; set; }
    public bool? SchedulingEnabled { get; set; }
    
    // FinOps Configuration
    public string? FinOpsStorageAccount { get; set; }
    public string? FinOpsContainer { get; set; }
    public string? FinOpsPowerBIUrl { get; set; }
    
    // LogicMonitor Configuration (basic - credentials via separate API)
    public int? LogicMonitorGroupId { get; set; }
    public bool? LMEnabled { get; set; }
}
