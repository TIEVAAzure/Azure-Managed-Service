using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TIEVA.Functions.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Database connection using Managed Identity
        var sqlServer = Environment.GetEnvironmentVariable("SQL_SERVER") 
            ?? "sql-tievaPortal-3234.database.windows.net";
        var sqlDatabase = Environment.GetEnvironmentVariable("SQL_DATABASE") 
            ?? "TievaPortal";

        var connectionString = $"Server={sqlServer};Database={sqlDatabase};Authentication=Active Directory Default;TrustServerCertificate=True;";

        services.AddDbContext<TievaDbContext>(options =>
            options.UseSqlServer(connectionString));
        
        // HttpClient for calling TIEVA.Audit function
        services.AddHttpClient();
    })
    .Build();

host.Run();
