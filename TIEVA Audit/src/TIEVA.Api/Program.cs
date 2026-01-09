// =====================================================
// TIEVA.Api - Program.cs
// Minimal API with all endpoints
// =====================================================

using Microsoft.EntityFrameworkCore;
using TIEVA.Core.Models;
using TIEVA.Core.DTOs;
using TIEVA.Infrastructure.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "TIEVA Portal API", Version = "v1" });
});

// Database
builder.Services.AddDbContext<TievaDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// CORS for frontend
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.WithOrigins("http://localhost:3000", "http://localhost:5173")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// Configure pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors();

// =====================================================
// HEALTH CHECK
// =====================================================

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }))
   .WithTags("Health");

// =====================================================
// SERVICE TIERS
// =====================================================

app.MapGet("/api/tiers", async (TievaDbContext db) =>
{
    var tiers = await db.ServiceTiers
        .Include(t => t.TierModules)
            .ThenInclude(tm => tm.Module)
        .Include(t => t.Subscriptions)
        .Where(t => t.IsActive)
        .OrderBy(t => t.SortOrder)
        .Select(t => new ServiceTierDto(
            t.Id,
            t.Name,
            t.DisplayName,
            t.Description,
            t.Color,
            t.TierModules.Count(tm => tm.IsIncluded),
            t.Subscriptions.Count(s => s.IsInScope),
            t.TierModules.Where(tm => tm.IsIncluded).Select(tm => new TierModuleDto(
                tm.ModuleId,
                tm.Module.Code,
                tm.Module.Name,
                tm.Module.Icon,
                tm.IsIncluded,
                tm.Frequency
            )).ToList()
        ))
        .ToListAsync();
    
    return Results.Ok(tiers);
})
.WithName("GetTiers")
.WithTags("Tiers");

app.MapGet("/api/tiers/{id:guid}", async (Guid id, TievaDbContext db) =>
{
    var tier = await db.ServiceTiers
        .Include(t => t.TierModules)
            .ThenInclude(tm => tm.Module)
        .FirstOrDefaultAsync(t => t.Id == id);
    
    if (tier == null) return Results.NotFound();
    
    return Results.Ok(tier);
})
.WithName("GetTier")
.WithTags("Tiers");

// =====================================================
// ASSESSMENT MODULES
// =====================================================

app.MapGet("/api/modules", async (TievaDbContext db) =>
{
    var modules = await db.AssessmentModules
        .Where(m => m.IsActive)
        .OrderBy(m => m.SortOrder)
        .ToListAsync();
    
    return Results.Ok(modules);
})
.WithName("GetModules")
.WithTags("Modules");

// =====================================================
// CUSTOMERS
// =====================================================

app.MapGet("/api/customers", async (TievaDbContext db) =>
{
    var customers = await db.Customers
        .Include(c => c.Connections)
        .Include(c => c.Subscriptions)
        .Include(c => c.Assessments)
        .Where(c => c.IsActive)
        .OrderBy(c => c.Name)
        .Select(c => new CustomerDto(
            c.Id,
            c.Name,
            c.Code,
            c.Industry,
            c.PrimaryContact,
            c.Email,
            c.IsActive,
            c.Connections.Count(conn => conn.IsActive),
            c.Subscriptions.Count(s => s.IsInScope),
            c.Assessments.Where(a => a.Status == "Completed").Max(a => (DateTime?)a.CompletedAt),
            c.Assessments.Where(a => a.Status == "Completed").OrderByDescending(a => a.CompletedAt).Select(a => a.ScoreOverall).FirstOrDefault()
        ))
        .ToListAsync();
    
    return Results.Ok(customers);
})
.WithName("GetCustomers")
.WithTags("Customers");

app.MapGet("/api/customers/{id:guid}", async (Guid id, TievaDbContext db) =>
{
    var customer = await db.Customers
        .Include(c => c.Connections)
        .Include(c => c.Subscriptions)
            .ThenInclude(s => s.Tier)
        .FirstOrDefaultAsync(c => c.Id == id);
    
    if (customer == null) return Results.NotFound();
    
    return Results.Ok(customer);
})
.WithName("GetCustomer")
.WithTags("Customers");

app.MapPost("/api/customers", async (CustomerCreateDto dto, TievaDbContext db) =>
{
    var customer = new Customer
    {
        Id = Guid.NewGuid(),
        Name = dto.Name,
        Code = dto.Code,
        Industry = dto.Industry,
        PrimaryContact = dto.PrimaryContact,
        Email = dto.Email,
        Notes = dto.Notes,
        CreatedAt = DateTime.UtcNow
    };
    
    db.Customers.Add(customer);
    await db.SaveChangesAsync();
    
    return Results.Created($"/api/customers/{customer.Id}", customer);
})
.WithName("CreateCustomer")
.WithTags("Customers");

app.MapPut("/api/customers/{id:guid}", async (Guid id, CustomerCreateDto dto, TievaDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer == null) return Results.NotFound();
    
    customer.Name = dto.Name;
    customer.Code = dto.Code;
    customer.Industry = dto.Industry;
    customer.PrimaryContact = dto.PrimaryContact;
    customer.Email = dto.Email;
    customer.Notes = dto.Notes;
    customer.UpdatedAt = DateTime.UtcNow;
    
    await db.SaveChangesAsync();
    
    return Results.Ok(customer);
})
.WithName("UpdateCustomer")
.WithTags("Customers");

app.MapDelete("/api/customers/{id:guid}", async (Guid id, TievaDbContext db) =>
{
    var customer = await db.Customers.FindAsync(id);
    if (customer == null) return Results.NotFound();
    
    customer.IsActive = false;
    customer.UpdatedAt = DateTime.UtcNow;
    
    await db.SaveChangesAsync();
    
    return Results.NoContent();
})
.WithName("DeleteCustomer")
.WithTags("Customers");

// =====================================================
// CONNECTIONS
// =====================================================

app.MapGet("/api/customers/{customerId:guid}/connections", async (Guid customerId, TievaDbContext db) =>
{
    var connections = await db.AzureConnections
        .Include(c => c.Subscriptions)
        .Where(c => c.CustomerId == customerId)
        .Select(c => new ConnectionDto(
            c.Id,
            c.CustomerId,
            c.DisplayName,
            c.TenantId,
            c.TenantName,
            c.ClientId,
            c.SecretExpiryDate,
            c.IsActive,
            c.LastValidated,
            c.LastValidationError,
            c.Subscriptions.Count
        ))
        .ToListAsync();
    
    return Results.Ok(connections);
})
.WithName("GetConnections")
.WithTags("Connections");

app.MapPost("/api/customers/{customerId:guid}/connections", async (Guid customerId, ConnectionCreateDto dto, TievaDbContext db) =>
{
    // TODO: Store ClientSecret in Key Vault and get URI
    var secretUri = $"https://your-keyvault.vault.azure.net/secrets/conn-{Guid.NewGuid():N}";
    
    var connection = new AzureConnection
    {
        Id = Guid.NewGuid(),
        CustomerId = customerId,
        DisplayName = dto.DisplayName,
        TenantId = dto.TenantId,
        TenantName = dto.TenantName,
        ClientId = dto.ClientId,
        SecretKeyVaultUri = secretUri,
        SecretExpiryDate = dto.SecretExpiryDate,
        CreatedAt = DateTime.UtcNow
    };
    
    db.AzureConnections.Add(connection);
    await db.SaveChangesAsync();
    
    return Results.Created($"/api/customers/{customerId}/connections/{connection.Id}", connection);
})
.WithName("CreateConnection")
.WithTags("Connections");

app.MapPost("/api/connections/{id:guid}/validate", async (Guid id, TievaDbContext db) =>
{
    var connection = await db.AzureConnections.FindAsync(id);
    if (connection == null) return Results.NotFound();
    
    // TODO: Actually validate against Azure
    // For now, simulate validation
    connection.LastValidated = DateTime.UtcNow;
    connection.LastValidationError = null;
    
    await db.SaveChangesAsync();
    
    return Results.Ok(new ValidationResult(true, new List<string> { "sub-1", "sub-2" }, null));
})
.WithName("ValidateConnection")
.WithTags("Connections");

// =====================================================
// SUBSCRIPTIONS
// =====================================================

app.MapGet("/api/customers/{customerId:guid}/subscriptions", async (Guid customerId, TievaDbContext db) =>
{
    var subscriptions = await db.CustomerSubscriptions
        .Include(s => s.Tier)
        .Where(s => s.CustomerId == customerId)
        .OrderBy(s => s.SubscriptionName)
        .Select(s => new SubscriptionDto(
            s.Id,
            s.SubscriptionId,
            s.SubscriptionName,
            s.TierId,
            s.Tier != null ? s.Tier.Name : null,
            s.Environment,
            s.IsInScope,
            null // TODO: Get from assessments
        ))
        .ToListAsync();
    
    return Results.Ok(subscriptions);
})
.WithName("GetSubscriptions")
.WithTags("Subscriptions");

app.MapPut("/api/subscriptions/{id:guid}", async (Guid id, SubscriptionUpdateDto dto, TievaDbContext db) =>
{
    var subscription = await db.CustomerSubscriptions.FindAsync(id);
    if (subscription == null) return Results.NotFound();
    
    subscription.TierId = dto.TierId;
    subscription.Environment = dto.Environment;
    subscription.IsInScope = dto.IsInScope;
    subscription.UpdatedAt = DateTime.UtcNow;
    
    await db.SaveChangesAsync();
    
    return Results.Ok(subscription);
})
.WithName("UpdateSubscription")
.WithTags("Subscriptions");

app.MapPost("/api/customers/{customerId:guid}/subscriptions/sync", async (Guid customerId, TievaDbContext db) =>
{
    // TODO: Connect to Azure and discover subscriptions
    // For now, return mock data
    
    var connection = await db.AzureConnections
        .FirstOrDefaultAsync(c => c.CustomerId == customerId && c.IsActive);
    
    if (connection == null)
        return Results.BadRequest("No active connection for customer");
    
    // Simulate discovered subscriptions
    var discovered = new[]
    {
        new { Id = "sub-001", Name = "Production-001" },
        new { Id = "sub-002", Name = "Production-002" },
        new { Id = "sub-003", Name = "Development" }
    };
    
    var existingSubIds = await db.CustomerSubscriptions
        .Where(s => s.CustomerId == customerId)
        .Select(s => s.SubscriptionId)
        .ToListAsync();
    
    var newCount = 0;
    foreach (var sub in discovered)
    {
        if (!existingSubIds.Contains(sub.Id))
        {
            db.CustomerSubscriptions.Add(new CustomerSubscription
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                ConnectionId = connection.Id,
                SubscriptionId = sub.Id,
                SubscriptionName = sub.Name,
                CreatedAt = DateTime.UtcNow
            });
            newCount++;
        }
    }
    
    await db.SaveChangesAsync();
    
    return Results.Ok(new { discovered = discovered.Length, added = newCount });
})
.WithName("SyncSubscriptions")
.WithTags("Subscriptions");

// =====================================================
// ASSESSMENTS
// =====================================================

app.MapGet("/api/assessments", async (Guid? customerId, int? limit, TievaDbContext db) =>
{
    var query = db.Assessments
        .Include(a => a.Customer)
        .AsQueryable();
    
    if (customerId.HasValue)
        query = query.Where(a => a.CustomerId == customerId.Value);
    
    var assessments = await query
        .OrderByDescending(a => a.CreatedAt)
        .Take(limit ?? 50)
        .Select(a => new AssessmentDto(
            a.Id,
            a.CustomerId,
            a.Customer.Name,
            a.Status,
            a.StartedAt,
            a.CompletedAt,
            a.SubscriptionCount,
            a.FindingCount,
            a.HighFindings,
            a.MediumFindings,
            a.LowFindings,
            a.ScoreOverall
        ))
        .ToListAsync();
    
    return Results.Ok(assessments);
})
.WithName("GetAssessments")
.WithTags("Assessments");

app.MapGet("/api/assessments/{id:guid}", async (Guid id, TievaDbContext db) =>
{
    var assessment = await db.Assessments
        .Include(a => a.Customer)
        .Include(a => a.ModuleResults)
        .Include(a => a.Findings)
        .FirstOrDefaultAsync(a => a.Id == id);
    
    if (assessment == null) return Results.NotFound();
    
    return Results.Ok(assessment);
})
.WithName("GetAssessment")
.WithTags("Assessments");

app.MapPost("/api/assessments", async (AssessmentStartDto dto, TievaDbContext db) =>
{
    // Create assessment record
    var assessment = new Assessment
    {
        Id = Guid.NewGuid(),
        CustomerId = dto.CustomerId,
        ConnectionId = dto.ConnectionId,
        Status = "Pending",
        CreatedAt = DateTime.UtcNow
    };
    
    db.Assessments.Add(assessment);
    await db.SaveChangesAsync();
    
    // TODO: Trigger Azure Function to run assessment
    // For now, just return the created assessment
    
    return Results.Accepted($"/api/assessments/{assessment.Id}", new
    {
        assessmentId = assessment.Id,
        status = "Pending",
        message = "Assessment queued for execution"
    });
})
.WithName("StartAssessment")
.WithTags("Assessments");

// =====================================================
// DASHBOARD / STATS
// =====================================================

app.MapGet("/api/dashboard", async (TievaDbContext db) =>
{
    var stats = new
    {
        customerCount = await db.Customers.CountAsync(c => c.IsActive),
        subscriptionCount = await db.CustomerSubscriptions.CountAsync(s => s.IsInScope),
        assessmentCount30d = await db.Assessments
            .CountAsync(a => a.CreatedAt > DateTime.UtcNow.AddDays(-30)),
        avgScore = await db.Assessments
            .Where(a => a.Status == "Completed" && a.ScoreOverall.HasValue)
            .AverageAsync(a => (double?)a.ScoreOverall) ?? 0,
        recentAssessments = await db.Assessments
            .Include(a => a.Customer)
            .OrderByDescending(a => a.CreatedAt)
            .Take(5)
            .Select(a => new
            {
                a.Id,
                customerName = a.Customer.Name,
                a.Status,
                a.CompletedAt,
                a.ScoreOverall,
                a.FindingCount,
                a.HighFindings
            })
            .ToListAsync()
    };
    
    return Results.Ok(stats);
})
.WithName("GetDashboard")
.WithTags("Dashboard");

app.Run();
