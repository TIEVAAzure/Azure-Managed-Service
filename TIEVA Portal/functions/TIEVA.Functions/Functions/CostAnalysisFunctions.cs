using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Parquet;
using Parquet.Schema;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class CostAnalysisFunctions
{
    private readonly TievaDbContext _db;
    private readonly ILogger<CostAnalysisFunctions> _logger;
    private readonly string _keyVaultUrl;

    public CostAnalysisFunctions(TievaDbContext db, ILogger<CostAnalysisFunctions> logger)
    {
        _db = db;
        _logger = logger;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    /// <summary>
    /// Debug endpoint to show parquet schema and sample data
    /// </summary>
    [Function("DebugParquetSchema")]
    public async Task<HttpResponseData> DebugParquetSchema(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/finops/debug-schema")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
            if (customer == null)
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");

            if (string.IsNullOrEmpty(customer.FinOpsStorageAccount) || string.IsNullOrEmpty(customer.FinOpsSasKeyVaultRef))
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "FinOps not configured");

            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secretResponse = await kvClient.GetSecretAsync(customer.FinOpsSasKeyVaultRef);
            var sasToken = secretResponse.Value.Value;

            var containerName = customer.FinOpsContainer ?? "ingestion";
            var containerUri = new Uri($"https://{customer.FinOpsStorageAccount}.blob.core.windows.net/{containerName}?{sasToken}");
            var containerClient = new BlobContainerClient(containerUri);

            // Find first parquet file
            string? parquetPath = null;
            await foreach (var blob in containerClient.GetBlobsAsync())
            {
                if (blob.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                {
                    parquetPath = blob.Name;
                    break;
                }
            }

            if (parquetPath == null)
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "No parquet files found");

            var blobClient = containerClient.GetBlobClient(parquetPath);
            using var memoryStream = new MemoryStream();
            await blobClient.DownloadToAsync(memoryStream);
            memoryStream.Position = 0;

            using var parquetReader = await ParquetReader.CreateAsync(memoryStream);
            var schema = parquetReader.Schema;

            var columnInfo = new List<object>();
            var sampleData = new Dictionary<string, object?>();

            foreach (var field in schema.Fields)
            {
                if (field is DataField dataField)
                {
                    columnInfo.Add(new { name = dataField.Name, type = dataField.ClrType.Name });
                }
            }

            // Read first row as sample
            if (parquetReader.RowGroupCount > 0)
            {
                using var groupReader = parquetReader.OpenRowGroupReader(0);
                foreach (var field in schema.Fields)
                {
                    if (field is DataField dataField)
                    {
                        try
                        {
                            var columnData = await groupReader.ReadColumnAsync(dataField);
                            var data = columnData.Data.Cast<object?>().ToArray();
                            if (data.Length > 0)
                                sampleData[dataField.Name] = data[0]?.ToString() ?? "(null)";
                        }
                        catch { }
                    }
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                file = parquetPath,
                columnCount = columnInfo.Count,
                columns = columnInfo,
                sampleRow = sampleData
            });
            return response;
        }
        catch (Exception ex)
        {
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, ex.Message);
        }
    }

    /// <summary>
    /// Debug endpoint to list blobs in customer's FinOps storage
    /// </summary>
    [Function("DebugFinOpsStorage")]
    public async Task<HttpResponseData> DebugFinOpsStorage(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/finops/debug")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers
                .FirstOrDefaultAsync(c => c.Id == customerId);

            if (customer == null)
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");

            if (string.IsNullOrEmpty(customer.FinOpsStorageAccount))
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "FinOps storage not configured");

            if (string.IsNullOrEmpty(customer.FinOpsSasKeyVaultRef))
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "SAS token not configured");

            // Get SAS token from Key Vault
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secretResponse = await kvClient.GetSecretAsync(customer.FinOpsSasKeyVaultRef);
            var sasToken = secretResponse.Value.Value;

            var containerName = customer.FinOpsContainer ?? "ingestion";
            var containerUri = new Uri($"https://{customer.FinOpsStorageAccount}.blob.core.windows.net/{containerName}?{sasToken}");
            var containerClient = new BlobContainerClient(containerUri);

            var blobs = new List<object>();
            var parquetFiles = new List<string>();
            
            await foreach (var blobItem in containerClient.GetBlobsAsync())
            {
                blobs.Add(new { name = blobItem.Name, size = blobItem.Properties.ContentLength });
                if (blobItem.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                {
                    parquetFiles.Add(blobItem.Name);
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                storageAccount = customer.FinOpsStorageAccount,
                container = containerName,
                totalBlobs = blobs.Count,
                parquetFileCount = parquetFiles.Count,
                parquetFiles = parquetFiles.Take(20),
                allBlobs = blobs.Take(50)
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Debug FinOps storage failed for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Get cost analysis data from customer's FOCUS exports
    /// </summary>
    [Function("GetCostAnalysis")]
    public async Task<HttpResponseData> GetCostAnalysis(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/finops/cost-analysis")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers
                .FirstOrDefaultAsync(c => c.Id == customerId);

            if (customer == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");
            }

            if (string.IsNullOrEmpty(customer.FinOpsStorageAccount))
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "FinOps storage not configured");
            }

            if (string.IsNullOrEmpty(customer.FinOpsSasKeyVaultRef))
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "SAS token not configured - please save a SAS token in the Config tab");
            }

            // Get date range from query params (default: last 30 days)
            var queryParams = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var daysBack = int.TryParse(queryParams["days"], out var d) ? d : 30;
            var startDate = DateTime.UtcNow.AddDays(-daysBack);

            // Get SAS token from Key Vault
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secretResponse = await kvClient.GetSecretAsync(customer.FinOpsSasKeyVaultRef);
            var sasToken = secretResponse.Value.Value;

            // Build the container URL with SAS token
            var containerName = customer.FinOpsContainer ?? "ingestion";
            var containerUri = new Uri($"https://{customer.FinOpsStorageAccount}.blob.core.windows.net/{containerName}?{sasToken}");
            var containerClient = new BlobContainerClient(containerUri);

            // Read cost data from Parquet files
            _logger.LogInformation("Reading cost data from storage account {StorageAccount}, container {Container}", 
                customer.FinOpsStorageAccount, containerName);
            
            var costData = await ReadCostDataAsync(containerClient, startDate);

            if (!costData.Any())
            {
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    message = "No cost data found for the specified period",
                    data = new CostAnalysisResult()
                });
                return response;
            }

            // Aggregate the data
            var result = AggregateCostData(costData, daysBack);

            var okResponse = req.CreateResponse(HttpStatusCode.OK);
            await okResponse.WriteAsJsonAsync(new { success = true, data = result });
            return okResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cost analysis for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to analyze costs: {ex.Message}");
        }
    }

    private async Task<List<CostRecord>> ReadCostDataAsync(BlobContainerClient containerClient, DateTime startDate)
    {
        var costRecords = new List<CostRecord>();
        
        // Try multiple possible prefixes where FOCUS exports might be stored
        var prefixes = new[] { "focus-exports/", "tieva-daily-focus-cost/", "tieva-monthly-focus-cost/", "" };

        try
        {
            foreach (var prefix in prefixes)
            {
                _logger.LogInformation("Searching for parquet files with prefix '{Prefix}'", prefix);
                
                // First, collect all parquet files and group by subscription (GUID folder)
                var allParquetFiles = new List<(string Path, string Timestamp, string SubscriptionGuid)>();
                
                await foreach (var blobItem in containerClient.GetBlobsAsync(prefix: prefix))
                {
                    if (!blobItem.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase))
                        continue;
                    
                    // Parse path: focus-exports/tieva-daily-focus-cost/20260101-20260131/202601071640/GUID/part_0_0001.parquet
                    var parts = blobItem.Name.Split('/');
                    if (parts.Length >= 5)
                    {
                        var timestamp = parts[^3]; // e.g., "202601071640"
                        var subscriptionGuid = parts[^2]; // The GUID folder represents a subscription's export
                        allParquetFiles.Add((blobItem.Name, timestamp, subscriptionGuid));
                    }
                    else
                    {
                        // Fallback for different path structures
                        allParquetFiles.Add((blobItem.Name, "000000000000", blobItem.Name));
                    }
                }
                
                if (!allParquetFiles.Any())
                {
                    _logger.LogInformation("No parquet files found with prefix '{Prefix}'", prefix);
                    continue;
                }
                
                // Group by subscription GUID, then get the LATEST timestamp for EACH subscription
                // This ensures we get data from ALL subscriptions, not just the one with the latest export
                var filesToRead = allParquetFiles
                    .GroupBy(f => f.SubscriptionGuid)
                    .SelectMany(subGroup => {
                        // For each subscription, get only files from ITS latest timestamp
                        var latestTimestampForSub = subGroup
                            .Select(f => f.Timestamp)
                            .OrderByDescending(t => t)
                            .First();
                        return subGroup.Where(f => f.Timestamp == latestTimestampForSub);
                    })
                    .Select(f => f.Path)
                    .ToList();
                
                var subscriptionCount = allParquetFiles.Select(f => f.SubscriptionGuid).Distinct().Count();
                _logger.LogInformation("Found {TotalFiles} total parquet files across {SubCount} subscriptions, reading {LatestFiles} files (latest per subscription)", 
                    allParquetFiles.Count, subscriptionCount, filesToRead.Count);
                
                // Log which subscriptions we're reading
                var subsToRead = filesToRead.Select(f => {
                    var parts = f.Split('/');
                    return parts.Length >= 2 ? parts[^2] : "unknown";
                }).Distinct().ToList();
                _logger.LogInformation("Reading data for subscription GUIDs: {Subs}", string.Join(", ", subsToRead));
                
                foreach (var blobPath in filesToRead)
                {
                    _logger.LogInformation("Reading parquet file: {BlobName}", blobPath);

                    try
                    {
                        var blobClient = containerClient.GetBlobClient(blobPath);
                        
                        using var memoryStream = new MemoryStream();
                        await blobClient.DownloadToAsync(memoryStream);
                        memoryStream.Position = 0;

                        // Read parquet file using Parquet.Net
                        using var parquetReader = await ParquetReader.CreateAsync(memoryStream);
                        var schema = parquetReader.Schema;
                        
                        for (int rowGroup = 0; rowGroup < parquetReader.RowGroupCount; rowGroup++)
                        {
                            using var groupReader = parquetReader.OpenRowGroupReader(rowGroup);
                            
                            // Read each column we need
                            var columns = new Dictionary<string, object?[]>();
                            foreach (var field in schema.Fields)
                            {
                                if (field is DataField dataField)
                                {
                                    try
                                    {
                                        var columnData = await groupReader.ReadColumnAsync(dataField);
                                        columns[dataField.Name.ToLower()] = columnData.Data.Cast<object?>().ToArray();
                                    }
                                    catch { }
                                }
                            }

                            if (!columns.Any()) continue;
                            
                            var rowCount = columns.Values.First().Length;
                            
                            // Find columns by possible names (FOCUS 1.0 schema)
                            var dateCol = FindColumn(columns, "chargeperiodstart");
                            var costCol = FindColumn(columns, "billedcost"); // Only use BilledCost, not EffectiveCost
                            var serviceCol = FindColumn(columns, "servicename");
                            var serviceCatCol = FindColumn(columns, "servicecategory");
                            var resourceCol = FindColumn(columns, "resourcename");
                            var resourceGroupCol = FindColumn(columns, "x_resourcegroupname");
                            var subscriptionCol = FindColumn(columns, "subaccountname");
                            var regionCol = FindColumn(columns, "regionname", "regionid");
                            var resourceTypeCol = FindColumn(columns, "resourcetype", "x_resourcetype");

                            for (int i = 0; i < rowCount; i++)
                            {
                                try
                                {
                                    DateTime? chargeDate = ParseDate(dateCol, i);

                                    // Skip if before start date
                                    if (chargeDate.HasValue && chargeDate.Value < startDate)
                                        continue;

                                    decimal cost = ParseDecimal(costCol, i);

                                    costRecords.Add(new CostRecord
                                    {
                                        Date = chargeDate ?? DateTime.UtcNow,
                                        Cost = cost,
                                        ServiceName = GetStringValue(serviceCol, i) ?? "Unknown",
                                        ServiceCategory = GetStringValue(serviceCatCol, i) ?? "Other",
                                        ResourceName = GetStringValue(resourceCol, i) ?? "Unknown",
                                        ResourceGroup = GetStringValue(resourceGroupCol, i) ?? "Unknown",
                                        Subscription = GetStringValue(subscriptionCol, i) ?? "Unknown",
                                        Region = GetStringValue(regionCol, i) ?? "Unknown",
                                        ResourceType = GetStringValue(resourceTypeCol, i) ?? "Unknown"
                                    });
                                }
                                catch { }
                            }
                        }
                        
                        _logger.LogInformation("Read {Count} total records so far from {BlobName}", costRecords.Count, blobPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to read parquet file: {BlobName}", blobPath);
                    }
                }
                
                // If we found records with this prefix, stop searching
                _logger.LogInformation("Prefix '{Prefix}': read {RecordCount} cost records from {FileCount} files", 
                    prefix, costRecords.Count, filesToRead.Count);
                
                if (costRecords.Any())
                {
                    _logger.LogInformation("Found {Count} cost records using prefix '{Prefix}'", costRecords.Count, prefix);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to list blobs in container");
        }

        // Deduplicate records - same billing account data may appear in multiple export files
        // Dedupe on: Date + ResourceName + Subscription + Cost (rounded to avoid float issues)
        var beforeDedupeCount = costRecords.Count;
        costRecords = costRecords
            .GroupBy(r => new { 
                Date = r.Date.Date, 
                r.ResourceName, 
                r.Subscription, 
                Cost = Math.Round(r.Cost, 4) 
            })
            .Select(g => g.First())
            .ToList();
        
        if (beforeDedupeCount != costRecords.Count)
        {
            _logger.LogInformation("Deduplicated cost records: {Before} -> {After} (removed {Removed} duplicates)",
                beforeDedupeCount, costRecords.Count, beforeDedupeCount - costRecords.Count);
        }
        
        // Log summary of what subscriptions we actually have data for
        var loadedSubs = costRecords.Select(r => r.Subscription).Distinct().ToList();
        _logger.LogInformation("Final data contains {RecordCount} records across {SubCount} subscriptions: {Subs}",
            costRecords.Count, loadedSubs.Count, string.Join(", ", loadedSubs));

        return costRecords;
    }

    private object?[]? FindColumn(Dictionary<string, object?[]> columns, params string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            if (columns.TryGetValue(name.ToLower(), out var col))
                return col;
        }
        return null;
    }

    private DateTime? ParseDate(object?[]? column, int index)
    {
        if (column == null || index >= column.Length) return null;
        var val = column[index];
        if (val == null) return null;
        
        if (val is DateTime dt) return dt;
        if (val is DateTimeOffset dto) return dto.DateTime;
        if (val is string s && DateTime.TryParse(s, out var parsed)) return parsed;
        if (val is DateOnly dateOnly) return dateOnly.ToDateTime(TimeOnly.MinValue);
        
        return null;
    }

    private decimal ParseDecimal(object?[]? column, int index)
    {
        if (column == null || index >= column.Length) return 0;
        var val = column[index];
        if (val == null) return 0;
        
        if (val is decimal dec) return dec;
        if (val is double dbl) return (decimal)dbl;
        if (val is float flt) return (decimal)flt;
        if (val is int intVal) return intVal;
        if (val is long longVal) return longVal;
        if (decimal.TryParse(val.ToString(), out var parsed)) return parsed;
        
        return 0;
    }

    private string? GetStringValue(object?[]? column, int index)
    {
        if (column == null || index >= column.Length) return null;
        return column[index]?.ToString();
    }

    private CostAnalysisResult AggregateCostData(List<CostRecord> records, int daysBack)
    {
        var result = new CostAnalysisResult();

        if (!records.Any()) return result;

        // Total cost
        result.TotalCost = records.Sum(r => r.Cost);
        
        // Daily trend
        result.DailyTrend = records
            .GroupBy(r => r.Date.Date)
            .OrderBy(g => g.Key)
            .Select(g => new DailyCost { Date = g.Key, Cost = g.Sum(r => r.Cost) })
            .ToList();

        // This month vs last month
        var now = DateTime.UtcNow;
        var thisMonthStart = new DateTime(now.Year, now.Month, 1);
        var lastMonthStart = thisMonthStart.AddMonths(-1);
        
        result.ThisMonthCost = records.Where(r => r.Date >= thisMonthStart).Sum(r => r.Cost);
        result.LastMonthCost = records.Where(r => r.Date >= lastMonthStart && r.Date < thisMonthStart).Sum(r => r.Cost);

        // Top services (by category - Compute, Storage, Databases, etc.)
        result.TopServices = records
            .GroupBy(r => r.ServiceCategory)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(10)
            .Select(g => new CostBreakdown { Name = g.Key, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();

        // Top resource groups (case-insensitive grouping)
        result.TopResourceGroups = records
            .GroupBy(r => r.ResourceGroup.ToLowerInvariant())
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(10)
            .Select(g => new CostBreakdown { Name = g.First().ResourceGroup, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();

        // Top resources
        result.TopResources = records
            .GroupBy(r => r.ResourceName)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(10)
            .Select(g => new CostBreakdown 
            { 
                Name = g.Key, 
                Cost = g.Sum(r => r.Cost), 
                Count = g.Count(),
                ResourceType = g.First().ResourceType,
                ResourceGroup = g.First().ResourceGroup
            })
            .ToList();

        // By subscription
        result.BySubscription = records
            .GroupBy(r => r.Subscription)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Select(g => new CostBreakdown { Name = g.Key, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();

        // By region
        result.ByRegion = records
            .GroupBy(r => r.Region)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Select(g => new CostBreakdown { Name = g.Key, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();

        // By service category (like Databases, Storage, Web, Compute, etc.)
        result.ByServiceCategory = records
            .GroupBy(r => r.ServiceCategory)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Select(g => new CostBreakdown { Name = g.Key, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();

        // All resources with details (top 100 by cost)
        result.AllResources = records
            .GroupBy(r => r.ResourceName)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(100)
            .Select(g => new ResourceDetail
            {
                Name = g.Key,
                ResourceType = g.First().ResourceType,
                ResourceGroup = g.First().ResourceGroup,
                Region = g.First().Region,
                Subscription = g.First().Subscription,
                ServiceName = g.First().ServiceName,
                ServiceCategory = g.First().ServiceCategory,
                Cost = g.Sum(r => r.Cost)
            })
            .ToList();

        // Unique counts (case-insensitive for resource groups)
        result.UniqueResourceCount = records.Select(r => r.ResourceName).Distinct().Count();
        result.UniqueResourceTypes = records.Select(r => r.ResourceType).Distinct().Count();
        result.UniqueResourceGroups = records.Select(r => r.ResourceGroup.ToLowerInvariant()).Distinct().Count();

        // Weekly comparison - last 7 days vs previous 7 days
        var today = DateTime.UtcNow.Date;
        var thisWeekStart = today.AddDays(-6);
        var lastWeekStart = today.AddDays(-13);
        var lastWeekEnd = today.AddDays(-7);
        
        var thisWeekRecords = records.Where(r => r.Date.Date >= thisWeekStart).ToList();
        var lastWeekRecords = records.Where(r => r.Date.Date >= lastWeekStart && r.Date.Date < thisWeekStart).ToList();
        
        // This week by category
        result.ThisWeekByCategory = thisWeekRecords
            .GroupBy(r => r.ServiceCategory)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Select(g => new CostBreakdown { Name = g.Key, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();
            
        // Last week by category
        result.LastWeekByCategory = lastWeekRecords
            .GroupBy(r => r.ServiceCategory)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Select(g => new CostBreakdown { Name = g.Key, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();
            
        // This week by resource group (case-insensitive)
        result.ThisWeekByResourceGroup = thisWeekRecords
            .GroupBy(r => r.ResourceGroup.ToLowerInvariant())
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(20)
            .Select(g => new CostBreakdown { Name = g.First().ResourceGroup, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();
            
        // Last week by resource group (case-insensitive)
        result.LastWeekByResourceGroup = lastWeekRecords
            .GroupBy(r => r.ResourceGroup.ToLowerInvariant())
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(20)
            .Select(g => new CostBreakdown { Name = g.First().ResourceGroup, Cost = g.Sum(r => r.Cost), Count = g.Count() })
            .ToList();
            
        // This week by resource
        result.ThisWeekByResource = thisWeekRecords
            .GroupBy(r => r.ResourceName)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(20)
            .Select(g => new CostBreakdown { 
                Name = g.Key, 
                Cost = g.Sum(r => r.Cost), 
                Count = g.Count(),
                ResourceType = g.First().ResourceType,
                ResourceGroup = g.First().ResourceGroup
            })
            .ToList();
            
        // Last week by resource
        result.LastWeekByResource = lastWeekRecords
            .GroupBy(r => r.ResourceName)
            .OrderByDescending(g => g.Sum(r => r.Cost))
            .Take(20)
            .Select(g => new CostBreakdown { 
                Name = g.Key, 
                Cost = g.Sum(r => r.Cost), 
                Count = g.Count(),
                ResourceType = g.First().ResourceType,
                ResourceGroup = g.First().ResourceGroup
            })
            .ToList();

        // Period info
        result.StartDate = records.Min(r => r.Date);
        result.EndDate = records.Max(r => r.Date);
        result.RecordCount = records.Count;

        return result;
    }

    private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
    {
        var response = req.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }
}

public class CostRecord
{
    public DateTime Date { get; set; }
    public decimal Cost { get; set; }
    public string ServiceName { get; set; } = "";
    public string ServiceCategory { get; set; } = "";
    public string ResourceName { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Subscription { get; set; } = "";
    public string Region { get; set; } = "";
    public string ResourceType { get; set; } = "";
}

public class CostAnalysisResult
{
    public decimal TotalCost { get; set; }
    public decimal ThisMonthCost { get; set; }
    public decimal LastMonthCost { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int RecordCount { get; set; }
    public int UniqueResourceCount { get; set; }
    public int UniqueResourceTypes { get; set; }
    public int UniqueResourceGroups { get; set; }
    public List<DailyCost> DailyTrend { get; set; } = new();
    public List<CostBreakdown> TopServices { get; set; } = new();
    public List<CostBreakdown> TopResourceGroups { get; set; } = new();
    public List<CostBreakdown> TopResources { get; set; } = new();
    public List<CostBreakdown> BySubscription { get; set; } = new();
    public List<CostBreakdown> ByRegion { get; set; } = new();
    public List<CostBreakdown> ByServiceCategory { get; set; } = new();
    public List<ResourceDetail> AllResources { get; set; } = new();
    
    // Weekly comparison data
    public List<CostBreakdown> ThisWeekByCategory { get; set; } = new();
    public List<CostBreakdown> LastWeekByCategory { get; set; } = new();
    public List<CostBreakdown> ThisWeekByResourceGroup { get; set; } = new();
    public List<CostBreakdown> LastWeekByResourceGroup { get; set; } = new();
    public List<CostBreakdown> ThisWeekByResource { get; set; } = new();
    public List<CostBreakdown> LastWeekByResource { get; set; } = new();
}

public class DailyCost
{
    public DateTime Date { get; set; }
    public decimal Cost { get; set; }
}

public class CostBreakdown
{
    public string Name { get; set; } = "";
    public decimal Cost { get; set; }
    public int Count { get; set; }
    public string? ResourceType { get; set; }
    public string? ResourceGroup { get; set; }
}

public class ResourceDetail
{
    public string Name { get; set; } = "";
    public string ResourceType { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Region { get; set; } = "";
    public string Subscription { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public string ServiceCategory { get; set; } = "";
    public decimal Cost { get; set; }
}
