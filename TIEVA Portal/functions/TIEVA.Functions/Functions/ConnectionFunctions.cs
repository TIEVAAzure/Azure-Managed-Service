using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class ConnectionFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;
    private readonly string _keyVaultUrl;

    public ConnectionFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<ConnectionFunctions>();
        _db = db;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    [Function("GetConnections")]
    public async Task<HttpResponseData> GetConnections(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "connections")] HttpRequestData req)
    {
        var connections = await _db.AzureConnections
            .Where(c => c.IsActive)
            .OrderBy(c => c.Customer!.Name)
            .Select(c => new
            {
                c.Id,
                c.CustomerId,
                CustomerName = c.Customer!.Name,
                c.TenantId,
                c.TenantName,
                c.ClientId,
                c.SecretExpiry,
                c.LastValidated,
                c.LastValidationStatus,
                c.IsActive,
                SubscriptionCount = c.Subscriptions.Count(s => s.IsInScope),
                Subscriptions = c.Subscriptions.Select(s => new
                {
                    s.Id,
                    s.SubscriptionId,
                    s.SubscriptionName,
                    s.TierId,
                    s.Environment,
                    s.IsInScope
                }).ToList()
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(connections);
        return response;
    }

    [Function("GetConnection")]
    public async Task<HttpResponseData> GetConnection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "connections/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var connectionId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        var connection = await _db.AzureConnections
            .Where(c => c.Id == connectionId)
            .Select(c => new
            {
                c.Id,
                c.CustomerId,
                CustomerName = c.Customer!.Name,
                c.TenantId,
                c.TenantName,
                c.ClientId,
                c.SecretExpiry,
                c.LastValidated,
                c.LastValidationStatus,
                c.IsActive,
                Subscriptions = c.Subscriptions.Select(s => new
                {
                    s.Id,
                    s.SubscriptionId,
                    s.SubscriptionName,
                    s.TierId,
                    TierName = s.Tier != null ? s.Tier.DisplayName : null,
                    TierColor = s.Tier != null ? s.Tier.Color : null,
                    s.Environment,
                    s.IsInScope
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (connection == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Connection not found");
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(connection);
        return response;
    }

    [Function("CreateConnection")]
    public async Task<HttpResponseData> CreateConnection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "connections")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<CreateConnectionRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input == null || string.IsNullOrWhiteSpace(input.TenantId) || 
            string.IsNullOrWhiteSpace(input.ClientId) || string.IsNullOrWhiteSpace(input.ClientSecret))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("TenantId, ClientId, and ClientSecret are required");
            return badRequest;
        }

        // Verify customer exists
        var customer = await _db.Customers.FindAsync(input.CustomerId);
        if (customer == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Customer not found");
            return badRequest;
        }

        // Test the connection first
        try
        {
            var credential = new ClientSecretCredential(input.TenantId, input.ClientId, input.ClientSecret);
            var armClient = new ArmClient(credential);
            var subs = armClient.GetSubscriptions();
            var subCount = 0;
            await foreach (var sub in subs)
            {
                subCount++;
                if (subCount > 0) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate connection");
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteAsJsonAsync(new { error = "Connection validation failed", details = ex.Message });
            return badRequest;
        }

        // Store secret in Key Vault
        var connectionId = Guid.NewGuid();
        var secretName = $"sp-{connectionId}";
        
        try
        {
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            await kvClient.SetSecretAsync(secretName, input.ClientSecret);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store secret in Key Vault");
            var serverError = req.CreateResponse(HttpStatusCode.InternalServerError);
            await serverError.WriteStringAsync("Failed to store credentials securely");
            return serverError;
        }

        // Create connection record
        var connection = new AzureConnection
        {
            Id = connectionId,
            CustomerId = input.CustomerId,
            TenantId = input.TenantId,
            TenantName = input.TenantName,
            ClientId = input.ClientId,
            SecretKeyVaultRef = secretName,
            SecretExpiry = input.SecretExpiry,
            IsActive = true,
            LastValidated = DateTime.UtcNow,
            LastValidationStatus = "Valid",
            CreatedAt = DateTime.UtcNow
        };

        _db.AzureConnections.Add(connection);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created connection {Id} for customer {CustomerId}", connection.Id, connection.CustomerId);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { connection.Id, connection.TenantId, connection.ClientId });
        return response;
    }

    [Function("UpdateConnection")]
    public async Task<HttpResponseData> UpdateConnection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "connections/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var connectionId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        var connection = await _db.AzureConnections.FindAsync(connectionId);
        if (connection == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Connection not found");
            return notFound;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<UpdateConnectionRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid request body");
            return badRequest;
        }

        // Update basic fields
        if (input.CustomerId.HasValue && input.CustomerId.Value != Guid.Empty)
        {
            var customer = await _db.Customers.FindAsync(input.CustomerId.Value);
            if (customer == null)
            {
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteStringAsync("Customer not found");
                return badRequest;
            }
            connection.CustomerId = input.CustomerId.Value;
        }

        if (!string.IsNullOrWhiteSpace(input.TenantId))
            connection.TenantId = input.TenantId;

        if (!string.IsNullOrWhiteSpace(input.TenantName))
            connection.TenantName = input.TenantName;

        if (!string.IsNullOrWhiteSpace(input.ClientId))
            connection.ClientId = input.ClientId;

        if (input.SecretExpiry.HasValue)
            connection.SecretExpiry = input.SecretExpiry;

        // Update secret if provided
        if (!string.IsNullOrWhiteSpace(input.ClientSecret))
        {
            // Validate new credentials first
            try
            {
                var credential = new ClientSecretCredential(
                    input.TenantId ?? connection.TenantId, 
                    input.ClientId ?? connection.ClientId, 
                    input.ClientSecret);
                var armClient = new ArmClient(credential);
                var subs = armClient.GetSubscriptions();
                var subCount = 0;
                await foreach (var sub in subs)
                {
                    subCount++;
                    if (subCount > 0) break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to validate new connection credentials");
                var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
                await badRequest.WriteAsJsonAsync(new { error = "Connection validation failed", details = ex.Message });
                return badRequest;
            }

            // Store new secret in Key Vault
            try
            {
                var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
                await kvClient.SetSecretAsync(connection.SecretKeyVaultRef, input.ClientSecret);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update secret in Key Vault");
                var serverError = req.CreateResponse(HttpStatusCode.InternalServerError);
                await serverError.WriteStringAsync("Failed to update credentials securely");
                return serverError;
            }

            connection.LastValidated = DateTime.UtcNow;
            connection.LastValidationStatus = "Valid";
        }

        await _db.SaveChangesAsync();

        _logger.LogInformation("Updated connection {Id}", connection.Id);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { connection.Id, connection.TenantId, connection.ClientId, message = "Connection updated" });
        return response;
    }

    [Function("ValidateConnection")]
    public async Task<HttpResponseData> ValidateConnection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "connections/{id}/validate")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var connectionId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        var connection = await _db.AzureConnections.FindAsync(connectionId);
        if (connection == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Connection not found");
            return notFound;
        }

        try
        {
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secret = await kvClient.GetSecretAsync(connection.SecretKeyVaultRef);

            var credential = new ClientSecretCredential(connection.TenantId, connection.ClientId, secret.Value.Value);
            var armClient = new ArmClient(credential);
            var subs = armClient.GetSubscriptions();
            var subCount = 0;
            await foreach (var sub in subs)
            {
                subCount++;
            }

            connection.LastValidated = DateTime.UtcNow;
            connection.LastValidationStatus = "Valid";
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { status = "Valid", subscriptionCount = subCount });
            return response;
        }
        catch (Exception ex)
        {
            connection.LastValidated = DateTime.UtcNow;
            connection.LastValidationStatus = $"Invalid: {ex.Message}";
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { status = "Invalid", error = ex.Message });
            return response;
        }
    }

    [Function("SyncSubscriptions")]
    public async Task<HttpResponseData> SyncSubscriptions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "connections/{id}/sync")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var connectionId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        var connection = await _db.AzureConnections
            .Include(c => c.Subscriptions)
            .FirstOrDefaultAsync(c => c.Id == connectionId);

        if (connection == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Connection not found");
            return notFound;
        }

        try
        {
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secret = await kvClient.GetSecretAsync(connection.SecretKeyVaultRef);

            var credential = new ClientSecretCredential(connection.TenantId, connection.ClientId, secret.Value.Value);
            var armClient = new ArmClient(credential);

            var existingSubIds = connection.Subscriptions.Select(s => s.SubscriptionId).ToHashSet();
            var foundSubIds = new HashSet<string>();
            var added = 0;
            var updated = 0;

            await foreach (var sub in armClient.GetSubscriptions())
            {
                var subId = sub.Data.SubscriptionId;
                foundSubIds.Add(subId);

                var existing = connection.Subscriptions.FirstOrDefault(s => s.SubscriptionId == subId);
                if (existing == null)
                {
                    _db.CustomerSubscriptions.Add(new CustomerSubscription
                    {
                        Id = Guid.NewGuid(),
                        ConnectionId = connectionId,
                        SubscriptionId = subId,
                        SubscriptionName = sub.Data.DisplayName,
                        Environment = "Production",
                        IsInScope = true,
                        CreatedAt = DateTime.UtcNow
                    });
                    added++;
                }
                else if (existing.SubscriptionName != sub.Data.DisplayName)
                {
                    existing.SubscriptionName = sub.Data.DisplayName;
                    updated++;
                }
            }

            await _db.SaveChangesAsync();

            connection.LastValidated = DateTime.UtcNow;
            connection.LastValidationStatus = "Valid";
            await _db.SaveChangesAsync();

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { added, updated, total = foundSubIds.Count });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync subscriptions");
            var serverError = req.CreateResponse(HttpStatusCode.InternalServerError);
            await serverError.WriteAsJsonAsync(new { error = ex.Message });
            return serverError;
        }
    }

    [Function("GetAuditSubscriptions")]
    public async Task<HttpResponseData> GetAuditSubscriptions(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "connections/{id}/audit-subscriptions/{moduleCode}")] HttpRequestData req,
        string id,
        string moduleCode)
    {
        if (!Guid.TryParse(id, out var connectionId))
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
                .Where(s => s.ConnectionId == connectionId && s.IsInScope && s.TierId != null)
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
            _logger.LogError(ex, "Error getting audit subscriptions");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }

    [Function("DeleteConnection")]
    public async Task<HttpResponseData> DeleteConnection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "connections/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var connectionId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        var connection = await _db.AzureConnections
            .Include(c => c.Subscriptions)
            .FirstOrDefaultAsync(c => c.Id == connectionId);

        if (connection == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Connection not found");
            return notFound;
        }

        // Track deletion counts
        var subscriptionsDeleted = connection.Subscriptions.Count;
        var assessmentsDeleted = 0;
        var findingsDeleted = 0;
        var moduleResultsDeleted = 0;

        // Cascade delete: Find and delete all assessments for this connection
        var assessments = await _db.Assessments
            .Include(a => a.ModuleResults)
            .Include(a => a.Findings)
            .Where(a => a.ConnectionId == connectionId)
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

        // Cascade delete: Remove all subscriptions
        _db.CustomerSubscriptions.RemoveRange(connection.Subscriptions);

        // Delete the connection
        _db.AzureConnections.Remove(connection);

        await _db.SaveChangesAsync();

        // Delete KeyVault secret
        try
        {
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            await kvClient.StartDeleteSecretAsync(connection.SecretKeyVaultRef);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete secret from Key Vault");
        }

        _logger.LogInformation("Deleted connection {Id} with {Subs} subscriptions, {Assessments} assessments, {Findings} findings",
            connectionId, subscriptionsDeleted, assessmentsDeleted, findingsDeleted);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            message = "Connection deleted",
            subscriptionsDeleted,
            assessmentsDeleted,
            findingsDeleted,
            moduleResultsDeleted
        });
        return response;
    }
}

public class CreateConnectionRequest
{
    public Guid CustomerId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string? TenantName { get; set; }
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public DateTime? SecretExpiry { get; set; }
}

public class UpdateConnectionRequest
{
    public Guid? CustomerId { get; set; }
    public string? TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public DateTime? SecretExpiry { get; set; }
}