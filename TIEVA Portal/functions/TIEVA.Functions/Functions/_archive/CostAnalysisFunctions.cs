using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Azure.Identity;
using Azure.Storage.Blobs;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class CostAnalysisFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;

    public CostAnalysisFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<CostAnalysisFunctions>();
        _db = db;
    }

    [Function("GetCostAnalysisData")]
    public async Task<HttpResponseData> GetCostAnalysisData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost-analysis/{connectionId}")] HttpRequestData req,
        string connectionId)
    {
        if (!Guid.TryParse(connectionId, out var connId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        try
        {
            // Get connection details
            var connection = await _db.AzureConnections
                .Include(c => c.Subscriptions)
                .FirstOrDefaultAsync(c => c.Id == connId);

            if (connection == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("Connection not found");
                return notFound;
            }

            // Get subscriptions in scope
            var subscriptions = connection.Subscriptions
                .Where(s => s.IsInScope)
                .Select(s => new { s.SubscriptionId, s.SubscriptionName })
                .ToList();

            if (!subscriptions.Any())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { 
                    message = "No subscriptions in scope",
                    subscriptions = new List<object>(),
                    files = new List<object>()
                });
                return response;
            }

            // Get storage account info
            var storageAccountName = Environment.GetEnvironmentVariable("AuditStorageAccount") ?? "sttievaaudit";
            var containerName = "cost-analysis";

            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            
            // Check if container exists
            if (!await containerClient.ExistsAsync())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { 
                    message = "No cost analysis data available",
                    subscriptions = subscriptions,
                    files = new List<object>()
                });
                return response;
            }

            // List files for each subscription - group by subscription and get latest for each
            var filesBySubscription = new Dictionary<string, List<(string Name, DateTimeOffset? LastModified, long Size)>>();
            
            foreach (var sub in subscriptions)
            {
                var prefix = $"{sub.SubscriptionId}/";
                var subFiles = new List<(string Name, DateTimeOffset? LastModified, long Size)>();
                
                await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix))
                {
                    subFiles.Add((blob.Name, blob.Properties.LastModified, blob.Properties.ContentLength ?? 0));
                }
                
                if (subFiles.Any())
                {
                    filesBySubscription[sub.SubscriptionId] = subFiles;
                }
            }

            // Get the latest file for each subscription
            var latestFiles = filesBySubscription
                .Select(kvp => {
                    var latest = kvp.Value.OrderByDescending(f => f.LastModified).First();
                    var sub = subscriptions.First(s => s.SubscriptionId == kvp.Key);
                    return new {
                        subscriptionId = kvp.Key,
                        subscriptionName = sub.SubscriptionName,
                        fileName = latest.Name,
                        lastModified = latest.LastModified,
                        size = latest.Size,
                        fileCount = kvp.Value.Count
                    };
                })
                .ToList();

            var okResponse = req.CreateResponse(HttpStatusCode.OK);
            await okResponse.WriteAsJsonAsync(new { 
                subscriptions = subscriptions,
                files = latestFiles,
                totalFiles = filesBySubscription.Values.Sum(v => v.Count)
            });
            return okResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cost analysis data");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }

    [Function("GetCostAnalysisFile")]
    public async Task<HttpResponseData> GetCostAnalysisFile(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost-analysis/{connectionId}/file")] HttpRequestData req,
        string connectionId)
    {
        if (!Guid.TryParse(connectionId, out var connId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        // Get filename from query string
        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var fileName = query["file"];

        if (string.IsNullOrEmpty(fileName))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("File name is required");
            return badRequest;
        }

        try
        {
            var storageAccountName = Environment.GetEnvironmentVariable("AuditStorageAccount") ?? "sttievaaudit";
            var containerName = "cost-analysis";

            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(fileName);

            if (!await blobClient.ExistsAsync())
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("File not found");
                return notFound;
            }

            // Download and return the file content
            var downloadResult = await blobClient.DownloadContentAsync();
            var content = downloadResult.Value.Content.ToString();

            var response = req.CreateResponse(HttpStatusCode.OK);
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(content);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting cost analysis file");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }

    [Function("GetAllCostAnalysisFiles")]
    public async Task<HttpResponseData> GetAllCostAnalysisFiles(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "cost-analysis/{connectionId}/all-files")] HttpRequestData req,
        string connectionId)
    {
        if (!Guid.TryParse(connectionId, out var connId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        try
        {
            // Get connection details
            var connection = await _db.AzureConnections
                .Include(c => c.Subscriptions)
                .FirstOrDefaultAsync(c => c.Id == connId);

            if (connection == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("Connection not found");
                return notFound;
            }

            // Get subscriptions in scope
            var subscriptions = connection.Subscriptions
                .Where(s => s.IsInScope)
                .ToDictionary(s => s.SubscriptionId, s => s.SubscriptionName);

            if (!subscriptions.Any())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { files = new List<object>() });
                return response;
            }

            var storageAccountName = Environment.GetEnvironmentVariable("AuditStorageAccount") ?? "sttievaaudit";
            var containerName = "cost-analysis";

            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            
            if (!await containerClient.ExistsAsync())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new { files = new List<object>() });
                return response;
            }

            // Get the latest file for EACH subscription (not just the overall latest)
            var allData = new List<object>();
            
            foreach (var sub in subscriptions)
            {
                var prefix = $"{sub.Key}/";
                var subFiles = new List<(string Name, DateTimeOffset? LastModified)>();
                
                await foreach (var blob in containerClient.GetBlobsAsync(prefix: prefix))
                {
                    subFiles.Add((blob.Name, blob.Properties.LastModified));
                }
                
                if (subFiles.Any())
                {
                    // Get the latest file for this subscription
                    var latestFile = subFiles.OrderByDescending(f => f.LastModified).First();
                    var blobClient = containerClient.GetBlobClient(latestFile.Name);
                    
                    try
                    {
                        var downloadResult = await blobClient.DownloadContentAsync();
                        var content = downloadResult.Value.Content.ToString();
                        var jsonData = JsonSerializer.Deserialize<JsonElement>(content);
                        
                        allData.Add(new {
                            subscriptionId = sub.Key,
                            subscriptionName = sub.Value,
                            fileName = latestFile.Name,
                            lastModified = latestFile.LastModified,
                            data = jsonData
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading file {FileName}", latestFile.Name);
                    }
                }
            }

            var okResponse = req.CreateResponse(HttpStatusCode.OK);
            await okResponse.WriteAsJsonAsync(new { files = allData });
            return okResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting all cost analysis files");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteStringAsync($"Error: {ex.Message}");
            return errorResponse;
        }
    }
}
