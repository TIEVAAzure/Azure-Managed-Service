using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class CustomerFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;

    public CustomerFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<CustomerFunctions>();
        _db = db;
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

        var customer = await _db.Customers.FindAsync(customerId);
        if (customer == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Customer not found");
            return notFound;
        }

        customer.IsActive = false;
        customer.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteStringAsync("Customer deleted");
        return response;
    }
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
}