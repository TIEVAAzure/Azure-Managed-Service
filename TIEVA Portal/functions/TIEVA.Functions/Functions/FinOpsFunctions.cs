using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Net;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using OfficeOpenXml;
using System.Text.Json;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;

namespace TIEVA.Functions.Functions;

public class FinOpsFunctions
{
    private readonly TievaDbContext _db;
    private readonly ILogger<FinOpsFunctions> _logger;
    private readonly string _keyVaultUrl;

    public FinOpsFunctions(TievaDbContext db, ILogger<FinOpsFunctions> logger)
    {
        _db = db;
        _logger = logger;
        _keyVaultUrl = Environment.GetEnvironmentVariable("KEY_VAULT_URL") 
            ?? "https://kv-tievaPortal-874.vault.azure.net/";
    }

    /// <summary>
    /// Generate a SAS URL for the customer's FinOps storage account
    /// </summary>
    [Function("GenerateFinOpsSas")]
    public async Task<HttpResponseData> GenerateFinOpsSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers/{customerId:guid}/finops/generate-sas")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            // Get customer with connection
            var customer = await _db.Customers
                .Include(c => c.Connections)
                .FirstOrDefaultAsync(c => c.Id == customerId);

            if (customer == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");
            }

            if (string.IsNullOrEmpty(customer.FinOpsStorageAccount))
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Storage account not configured for this customer");
            }

            var containerName = customer.FinOpsContainer ?? "ingestion";

            // Get the active connection for this customer
            var connection = customer.Connections.FirstOrDefault(c => c.IsActive);
            if (connection == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "No active Azure connection for this customer");
            }

            // Get client secret from Key Vault
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secretResponse = await kvClient.GetSecretAsync(connection.SecretKeyVaultRef);
            var clientSecret = secretResponse.Value.Value;

            // Create credential using customer's App Registration
            var credential = new ClientSecretCredential(
                connection.TenantId,
                connection.ClientId,
                clientSecret
            );

            // Create blob service client
            var blobServiceUri = new Uri($"https://{customer.FinOpsStorageAccount}.blob.core.windows.net");
            var blobServiceClient = new BlobServiceClient(blobServiceUri, credential);

            // Get user delegation key (valid for up to 7 days)
            var delegationKeyStart = DateTimeOffset.UtcNow;
            var delegationKeyExpiry = DateTimeOffset.UtcNow.AddDays(7);
            
            var userDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(delegationKeyStart, delegationKeyExpiry);

            // Create SAS for the container
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                Resource = "c",
                StartsOn = delegationKeyStart,
                ExpiresOn = delegationKeyExpiry
            };
            sasBuilder.SetPermissions(BlobContainerSasPermissions.Read | BlobContainerSasPermissions.List);

            // Generate SAS token
            var sasToken = sasBuilder.ToSasQueryParameters(userDelegationKey.Value, customer.FinOpsStorageAccount).ToString();
            var sasUrl = $"https://{customer.FinOpsStorageAccount}.blob.core.windows.net/{containerName}?{sasToken}";

            // Store SAS URL in Key Vault
            var sasSecretName = $"finops-sas-{customerId:N}";
            await kvClient.SetSecretAsync(sasSecretName, sasUrl);

            // Update customer record
            customer.FinOpsSasKeyVaultRef = sasSecretName;
            customer.FinOpsSasExpiry = delegationKeyExpiry.UtcDateTime;
            customer.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Generated FinOps SAS for customer {CustomerId}, expires {Expiry}", customerId, delegationKeyExpiry);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                sasUrl = sasUrl,
                sasToken = sasToken,  // Just the token part for Power BI
                storageUrl = $"https://{customer.FinOpsStorageAccount}.dfs.core.windows.net/{containerName}",
                expiry = delegationKeyExpiry,
                message = "SAS URL generated successfully. Copy the token into Power BI."
            });
            return response;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 403)
        {
            _logger.LogError(ex, "Permission denied generating SAS for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.Forbidden, 
                "Permission denied. The App Registration needs 'Storage Blob Data Contributor' and 'Storage Blob Delegator' roles on the storage account.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate FinOps SAS for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to generate SAS: {ex.Message}");
        }
    }

    /// <summary>
    /// Get the current SAS URL from Key Vault
    /// </summary>
    [Function("GetFinOpsSas")]
    public async Task<HttpResponseData> GetFinOpsSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/finops/sas")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers.FindAsync(customerId);
            if (customer == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");
            }

            if (string.IsNullOrEmpty(customer.FinOpsSasKeyVaultRef))
            {
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "No SAS configured for this customer");
            }

            // Get SAS from Key Vault
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secretResponse = await kvClient.GetSecretAsync(customer.FinOpsSasKeyVaultRef);
            var sasToken = secretResponse.Value.Value;

            var containerName = customer.FinOpsContainer ?? "ingestion";
            var storageUrl = $"https://{customer.FinOpsStorageAccount}.dfs.core.windows.net/{containerName}";

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                sasToken = sasToken,
                storageUrl = storageUrl,
                expiry = customer.FinOpsSasExpiry,
                isExpired = customer.FinOpsSasExpiry.HasValue && customer.FinOpsSasExpiry.Value < DateTime.UtcNow,
                expiresInDays = customer.FinOpsSasExpiry.HasValue 
                    ? (int)(customer.FinOpsSasExpiry.Value - DateTime.UtcNow).TotalDays 
                    : 0
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get FinOps SAS for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to get SAS: {ex.Message}");
        }
    }

    /// <summary>
    /// Save a manually-generated SAS token to Key Vault
    /// </summary>
    [Function("SaveFinOpsSas")]
    public async Task<HttpResponseData> SaveFinOpsSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "customers/{customerId:guid}/finops/sas")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers.FindAsync(customerId);
            if (customer == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");
            }

            // Parse request body
            var body = await new StreamReader(req.Body).ReadToEndAsync();
            var input = System.Text.Json.JsonSerializer.Deserialize<SaveSasRequest>(body, 
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (input == null || string.IsNullOrEmpty(input.SasToken))
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "SAS token is required");
            }

            var sasToken = input.SasToken.Trim();
            
            // Remove leading ? if present
            if (sasToken.StartsWith("?"))
            {
                sasToken = sasToken.Substring(1);
            }
            
            // Validate it looks like a SAS token (contains sv= somewhere)
            if (!sasToken.Contains("sv="))
            {
                _logger.LogWarning("Invalid SAS token format received: {Token}", sasToken.Substring(0, Math.Min(50, sasToken.Length)));
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "Invalid SAS token format - must contain sv= parameter");
            }

            // Parse expiry from token
            DateTime? expiry = null;
            var parts = sasToken.Split('&');
            foreach (var part in parts)
            {
                if (part.StartsWith("se="))
                {
                    var expiryStr = Uri.UnescapeDataString(part.Substring(3));
                    if (DateTime.TryParse(expiryStr, out var parsedExpiry))
                    {
                        expiry = parsedExpiry;
                    }
                    break;
                }
            }

            // Store SAS token in Key Vault
            var sasSecretName = $"finops-sas-{customerId:N}";
            try
            {
                var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
                
                // Try to set the secret - if it fails due to soft-delete conflict, recover and retry
                try
                {
                    await kvClient.SetSecretAsync(sasSecretName, sasToken);
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 409)
                {
                    // Secret is in deleted state - try to purge it first
                    _logger.LogInformation("Secret {SecretName} is in deleted state, attempting to purge...", sasSecretName);
                    try
                    {
                        await kvClient.PurgeDeletedSecretAsync(sasSecretName);
                        await Task.Delay(2000); // Wait for purge to complete
                    }
                    catch (Exception purgeEx)
                    {
                        _logger.LogWarning(purgeEx, "Could not purge deleted secret, trying to recover instead");
                        // If purge fails (no permission), try to recover it instead
                        try
                        {
                            await kvClient.StartRecoverDeletedSecretAsync(sasSecretName);
                            await Task.Delay(2000); // Wait for recovery to complete
                        }
                        catch (Exception recoverEx)
                        {
                            _logger.LogError(recoverEx, "Could not recover deleted secret either");
                            throw new Exception($"Secret is in deleted state and cannot be purged or recovered: {ex.Message}");
                        }
                    }
                    
                    // Now try to set the secret again
                    await kvClient.SetSecretAsync(sasSecretName, sasToken);
                }
                
                _logger.LogInformation("Stored SAS token in Key Vault as {SecretName}", sasSecretName);
            }
            catch (Exception kvEx)
            {
                _logger.LogError(kvEx, "Failed to store SAS in Key Vault: {Message}", kvEx.Message);
                return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to store SAS in Key Vault: {kvEx.Message}");
            }

            // Update customer record
            customer.FinOpsSasKeyVaultRef = sasSecretName;
            customer.FinOpsSasExpiry = expiry;
            customer.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Saved FinOps SAS for customer {CustomerId}, expires {Expiry}", customerId, expiry);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                expiry = expiry,
                message = "SAS token saved successfully"
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save FinOps SAS for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to save SAS: {ex.Message}");
        }
    }

    /// <summary>
    /// Delete SAS token from Key Vault and clear customer config
    /// </summary>
    [Function("DeleteFinOpsSas")]
    public async Task<HttpResponseData> DeleteFinOpsSas(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "customers/{customerId:guid}/finops/sas")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers.FindAsync(customerId);
            if (customer == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");
            }

            // Delete from Key Vault if exists
            if (!string.IsNullOrEmpty(customer.FinOpsSasKeyVaultRef))
            {
                try
                {
                    var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
                    await kvClient.StartDeleteSecretAsync(customer.FinOpsSasKeyVaultRef);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete SAS from Key Vault (may not exist)");
                }
            }

            // Clear customer fields
            customer.FinOpsSasKeyVaultRef = null;
            customer.FinOpsSasExpiry = null;
            customer.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            _logger.LogInformation("Deleted FinOps SAS for customer {CustomerId}", customerId);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new { success = true, message = "SAS deleted" });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete FinOps SAS for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to delete SAS: {ex.Message}");
        }
    }

    /// <summary>
    /// Run cost exports immediately for a customer
    /// </summary>
    [Function("RunFinOpsExport")]
    public async Task<HttpResponseData> RunFinOpsExport(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers/{customerId:guid}/finops/run-export")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers
                .Include(c => c.Connections)
                    .ThenInclude(conn => conn.Subscriptions)
                .FirstOrDefaultAsync(c => c.Id == customerId);

            if (customer == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");
            }

            if (string.IsNullOrEmpty(customer.FinOpsStorageAccount))
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "FinOps storage not configured for this customer");
            }

            var connection = customer.Connections.FirstOrDefault(c => c.IsActive);
            if (connection == null)
            {
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "No active Azure connection for this customer");
            }

            // Get client secret from Key Vault
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secretResponse = await kvClient.GetSecretAsync(connection.SecretKeyVaultRef);
            var clientSecret = secretResponse.Value.Value;

            // Create credential using customer's App Registration
            var credential = new ClientSecretCredential(
                connection.TenantId,
                connection.ClientId,
                clientSecret
            );

            // Get access token
            var tokenRequestContext = new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" });
            var accessToken = await credential.GetTokenAsync(tokenRequestContext);

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var exportResults = new List<object>();
            var errors = new List<string>();
            var subscriptionResults = new List<object>(); // Track what we found per subscription

            // Try to run exports at subscription level for each in-scope subscription
            var subscriptions = connection.Subscriptions.Where(s => s.IsInScope).ToList();
            
            if (!subscriptions.Any())
            {
                // No subscriptions, try listing exports at tenant level
                _logger.LogInformation("No subscriptions found, cannot run exports");
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "No in-scope subscriptions found");
            }

            foreach (var sub in subscriptions)
            {
                var scope = $"subscriptions/{sub.SubscriptionId}";
                
                try
                {
                    // List all exports for this subscription
                    var listUrl = $"https://management.azure.com/{scope}/providers/Microsoft.CostManagement/exports?api-version=2025-03-01";
                    _logger.LogInformation("Calling exports API: {Url}", listUrl);
                    
                    var listResponse = await httpClient.GetAsync(listUrl);
                    var listBody = await listResponse.Content.ReadAsStringAsync();
                    
                    _logger.LogInformation("Response for {SubName}: Status={Status}, Body={Body}", 
                        sub.SubscriptionName, listResponse.StatusCode, 
                        listBody.Length > 500 ? listBody.Substring(0, 500) : listBody);
                    
                    if (!listResponse.IsSuccessStatusCode)
                    {
                        errors.Add($"{sub.SubscriptionName ?? sub.SubscriptionId}: {listResponse.StatusCode} - {listBody.Substring(0, Math.Min(200, listBody.Length))}");
                        subscriptionResults.Add(new { 
                            subscription = sub.SubscriptionName ?? sub.SubscriptionId,
                            subscriptionId = sub.SubscriptionId,
                            status = listResponse.StatusCode.ToString(),
                            exportsFound = 0,
                            exportNames = new List<string>(),
                            rawResponse = listBody.Length > 300 ? listBody.Substring(0, 300) : listBody
                        });
                        continue;
                    }
                    
                    var listResult = System.Text.Json.JsonDocument.Parse(listBody);
                    
                    if (listResult.RootElement.TryGetProperty("value", out var exports))
                    {
                        var exportCount = 0;
                        var exportNames = new List<string>();
                        foreach (var export in exports.EnumerateArray())
                        {
                            exportCount++;
                            var name = export.TryGetProperty("name", out var n) ? n.GetString() : "unknown";
                            exportNames.Add(name ?? "unknown");
                        }
                        
                        subscriptionResults.Add(new { 
                            subscription = sub.SubscriptionName ?? sub.SubscriptionId,
                            subscriptionId = sub.SubscriptionId,
                            status = "OK",
                            exportsFound = exportCount,
                            exportNames = exportNames
                        });
                        
                        _logger.LogInformation("Found {Count} exports in subscription {SubName}: {Names}", 
                            exportCount, sub.SubscriptionName ?? sub.SubscriptionId, string.Join(", ", exportNames));
                        
                        foreach (var export in exports.EnumerateArray())
                        {
                            var exportName = export.GetProperty("name").GetString();
                            if (string.IsNullOrEmpty(exportName)) continue;
                            
                            // Log export details for debugging
                            string? targetStorage = null;
                            if (export.TryGetProperty("properties", out var props) &&
                                props.TryGetProperty("deliveryInfo", out var delivery) &&
                                delivery.TryGetProperty("destination", out var dest) &&
                                dest.TryGetProperty("resourceId", out var resourceId))
                            {
                                targetStorage = resourceId.GetString() ?? "";
                            }
                            
                            _logger.LogInformation("Export '{ExportName}' targets storage: {Storage}, looking for: {Expected}", 
                                exportName, targetStorage ?? "unknown", customer.FinOpsStorageAccount);
                            
                            // Check if this export targets our storage account
                            if (!string.IsNullOrEmpty(targetStorage) && 
                                !targetStorage.Contains(customer.FinOpsStorageAccount, StringComparison.OrdinalIgnoreCase))
                            {
                                errors.Add($"Skipped '{exportName}' - targets different storage");
                                continue; // Not our export
                            }
                            
                            // Run this export
                            var runUrl = $"https://management.azure.com/{scope}/providers/Microsoft.CostManagement/exports/{exportName}/run?api-version=2025-03-01";
                            var runResponse = await httpClient.PostAsync(runUrl, null);
                            
                            if (runResponse.IsSuccessStatusCode || runResponse.StatusCode == HttpStatusCode.OK || runResponse.StatusCode == HttpStatusCode.Accepted)
                            {
                                _logger.LogInformation("Triggered export {ExportName} for subscription {SubId}", exportName, sub.SubscriptionId);
                                exportResults.Add(new { export = exportName, subscription = sub.SubscriptionName ?? sub.SubscriptionId, status = "triggered" });
                            }
                            else
                            {
                                var errorBody = await runResponse.Content.ReadAsStringAsync();
                                _logger.LogWarning("Failed to run export {ExportName}: {Status} - {Error}", exportName, runResponse.StatusCode, errorBody);
                                errors.Add($"{exportName}: {runResponse.StatusCode}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing exports for subscription {SubId}", sub.SubscriptionId);
                    errors.Add($"{sub.SubscriptionId}: {ex.Message}");
                }
            }

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = exportResults.Any(),
                exports = exportResults,
                errors = errors,
                diagnostics = new
                {
                    subscriptionsSearched = subscriptions.Select(s => s.SubscriptionName ?? s.SubscriptionId).ToList(),
                    expectedStorageAccount = customer.FinOpsStorageAccount,
                    subscriptionCount = subscriptions.Count,
                    subscriptionResults = subscriptionResults
                },
                message = exportResults.Any() 
                    ? $"Triggered {exportResults.Count} export(s). Data should appear within 5-15 minutes."
                    : $"No exports found targeting storage account '{customer.FinOpsStorageAccount}'. Searched {subscriptions.Count} subscription(s). The Service Principal may need 'Cost Management Reader' role on subscriptions with exports."
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to run FinOps exports for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to run exports: {ex.Message}");
        }
    }

    private async Task<HttpResponseData> CreateErrorResponse(HttpRequestData req, HttpStatusCode statusCode, string message)
    {
        var response = req.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message });
        return response;
    }

    /// <summary>
    /// Get cached reservation data - returns cached data with refresh status
    /// </summary>
    [Function("GetReservationData")]
    public async Task<HttpResponseData> GetReservationData(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/finops/reservations")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
            if (customer == null)
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");

            // Get cached data
            var cache = await _db.CustomerReservationCache.FirstOrDefaultAsync(c => c.CustomerId == customerId);
            
            if (cache == null)
            {
                // No cache exists - tell frontend to trigger refresh
                var response = req.CreateResponse(HttpStatusCode.OK);
                await response.WriteAsJsonAsync(new
                {
                    success = true,
                    status = "NoData",
                    hasData = false,
                    message = "No reservation data cached. Click Refresh to load data from Azure."
                });
                return response;
            }

            // Return cached data with status
            var responseObj = new Dictionary<string, object?>
            {
                ["success"] = true,
                ["status"] = cache.Status,
                ["lastRefreshed"] = cache.LastRefreshed,
                ["hasData"] = !string.IsNullOrEmpty(cache.ReservationsJson) || !string.IsNullOrEmpty(cache.PurchaseRecommendationsJson)
            };

            // If running, just return status
            if (cache.Status == "Running")
            {
                responseObj["message"] = "Reservation data is being refreshed from Azure...";
            }
            else if (cache.Status == "Failed")
            {
                responseObj["errorMessage"] = cache.ErrorMessage;
            }
            else if (cache.Status == "Completed")
            {
                // Parse and return cached JSON data
                if (!string.IsNullOrEmpty(cache.SummaryJson))
                    responseObj["summary"] = JsonSerializer.Deserialize<object>(cache.SummaryJson);
                if (!string.IsNullOrEmpty(cache.ReservationsJson))
                    responseObj["reservations"] = JsonSerializer.Deserialize<object>(cache.ReservationsJson);
                if (!string.IsNullOrEmpty(cache.InsightsJson))
                    responseObj["insights"] = JsonSerializer.Deserialize<object>(cache.InsightsJson);
                if (!string.IsNullOrEmpty(cache.PurchaseRecommendationsJson))
                    responseObj["purchaseRecommendations"] = JsonSerializer.Deserialize<object>(cache.PurchaseRecommendationsJson);
                if (!string.IsNullOrEmpty(cache.ErrorsJson))
                    responseObj["errors"] = JsonSerializer.Deserialize<object>(cache.ErrorsJson);
                
                responseObj["lastUpdated"] = cache.LastRefreshed;
            }

            var httpResponse = req.CreateResponse(HttpStatusCode.OK);
            await httpResponse.WriteAsJsonAsync(responseObj);
            return httpResponse;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get reservation data for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Start reservation data refresh - queues background processing
    /// </summary>
    [Function("StartReservationRefresh")]
    public async Task<ReservationRefreshResult> StartReservationRefresh(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "customers/{customerId:guid}/finops/reservations/refresh")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            var customer = await _db.Customers
                .Include(c => c.Connections)
                .FirstOrDefaultAsync(c => c.Id == customerId);

            if (customer == null)
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.NotFound);
                await errorResponse.WriteAsJsonAsync(new { error = "Customer not found" });
                return new ReservationRefreshResult { HttpResponse = errorResponse, QueueMessage = null };
            }

            var connection = customer.Connections.FirstOrDefault(c => c.IsActive);
            if (connection == null)
            {
                var errorResponse = req.CreateResponse(HttpStatusCode.BadRequest);
                await errorResponse.WriteAsJsonAsync(new { error = "No active Azure connection" });
                return new ReservationRefreshResult { HttpResponse = errorResponse, QueueMessage = null };
            }

            // Get or create cache record
            var cache = await _db.CustomerReservationCache.FirstOrDefaultAsync(c => c.CustomerId == customerId);
            
            if (cache == null)
            {
                cache = new CustomerReservationCache
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerId,
                    Status = "Running",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                _db.CustomerReservationCache.Add(cache);
            }
            else
            {
                // Don't restart if already running
                if (cache.Status == "Running")
                {
                    var runningResponse = req.CreateResponse(HttpStatusCode.OK);
                    await runningResponse.WriteAsJsonAsync(new { success = true, status = "Running", message = "Refresh already in progress" });
                    return new ReservationRefreshResult { HttpResponse = runningResponse, QueueMessage = null };
                }
                
                cache.Status = "Running";
                cache.ErrorMessage = null;
                cache.UpdatedAt = DateTime.UtcNow;
            }
            
            await _db.SaveChangesAsync();
            
            _logger.LogInformation("Queued reservation refresh for customer {CustomerId}", customerId);

            var httpResponse = req.CreateResponse(HttpStatusCode.Accepted);
            await httpResponse.WriteAsJsonAsync(new 
            { 
                success = true, 
                status = "Running", 
                message = "Reservation refresh started. Poll GET endpoint for results." 
            });
            
            // Return both HTTP response and queue message
            return new ReservationRefreshResult 
            { 
                HttpResponse = httpResponse, 
                QueueMessage = JsonSerializer.Serialize(new { customerId = customerId.ToString() })
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start reservation refresh for customer {CustomerId}", customerId);
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = ex.Message });
            return new ReservationRefreshResult { HttpResponse = errorResponse, QueueMessage = null };
        }
    }

    // Result class for multi-output binding
    public class ReservationRefreshResult
    {
        [HttpResult]
        public HttpResponseData? HttpResponse { get; set; }
        
        [QueueOutput("reservation-refresh", Connection = "AzureWebJobsStorage")]
        public string? QueueMessage { get; set; }
    }

    /// <summary>
    /// Process reservation refresh - queue triggered background job
    /// </summary>
    [Function("ProcessReservationRefresh")]
    public async Task ProcessReservationRefresh(
        [QueueTrigger("reservation-refresh", Connection = "AzureWebJobsStorage")] string message)
    {
        var payload = JsonSerializer.Deserialize<Dictionary<string, string>>(message);
        var customerId = Guid.Parse(payload!["customerId"]);
        
        _logger.LogInformation("Processing reservation refresh for customer {CustomerId}", customerId);

        CustomerReservationCache? cache = null;
        
        try
        {
            var customer = await _db.Customers
                .Include(c => c.Connections)
                    .ThenInclude(conn => conn.Subscriptions)
                .FirstOrDefaultAsync(c => c.Id == customerId);

            if (customer == null)
            {
                _logger.LogWarning("Customer {CustomerId} not found for reservation refresh", customerId);
                return;
            }

            cache = await _db.CustomerReservationCache.FirstOrDefaultAsync(c => c.CustomerId == customerId);
            if (cache == null)
            {
                _logger.LogWarning("Cache record not found for customer {CustomerId}", customerId);
                return;
            }

            var connection = customer.Connections.FirstOrDefault(c => c.IsActive);
            if (connection == null)
            {
                cache.Status = "Failed";
                cache.ErrorMessage = "No active Azure connection";
                cache.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                return;
            }

            // Get Azure credentials
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            var secretResponse = await kvClient.GetSecretAsync(connection.SecretKeyVaultRef);
            var credential = new ClientSecretCredential(connection.TenantId, connection.ClientId, secretResponse.Value.Value);
            var accessToken = await credential.GetTokenAsync(new Azure.Core.TokenRequestContext(new[] { "https://management.azure.com/.default" }));

            var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken.Token);

            var reservations = new List<Dictionary<string, object>>();
            var insights = new List<Dictionary<string, object>>();
            var purchaseRecommendations = new List<Dictionary<string, object>>();
            var errors = new List<string>();

            // Fetch reservation orders with utilization data
            try
            {
                var ordersUrl = "https://management.azure.com/providers/Microsoft.Capacity/reservationOrders?api-version=2022-11-01";
                var ordersResponse = await httpClient.GetAsync(ordersUrl);
                
                if (ordersResponse.IsSuccessStatusCode)
                {
                    var ordersJson = System.Text.Json.JsonDocument.Parse(await ordersResponse.Content.ReadAsStringAsync());
                    
                    if (ordersJson.RootElement.TryGetProperty("value", out var orders))
                    {
                        foreach (var order in orders.EnumerateArray())
                        {
                            var orderId = order.GetProperty("name").GetString();
                            
                            // Get reservations in this order
                            var resUrl = $"https://management.azure.com/providers/Microsoft.Capacity/reservationOrders/{orderId}/reservations?api-version=2022-11-01";
                            var resResponse = await httpClient.GetAsync(resUrl);
                            
                            if (resResponse.IsSuccessStatusCode)
                            {
                                var resJson = System.Text.Json.JsonDocument.Parse(await resResponse.Content.ReadAsStringAsync());
                                if (resJson.RootElement.TryGetProperty("value", out var resList))
                                {
                                    foreach (var res in resList.EnumerateArray())
                                    {
                                        var resId = res.GetProperty("name").GetString();
                                        var resData = ParseReservationBasicInfo(res, order);
                                        
                                        // Get utilization summaries (daily history for last 30 days)
                                        await GetReservationUtilizationHistory(httpClient, orderId!, resId!, resData, errors);
                                        
                                        // Get what's actually using this reservation
                                        await GetReservationUsageDetails(httpClient, orderId!, resId!, resData, errors);
                                        
                                        // Calculate cost-benefit analysis
                                        CalculateCostBenefit(resData);
                                        
                                        reservations.Add(resData);
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    var errorBody = await ordersResponse.Content.ReadAsStringAsync();
                    _logger.LogWarning("Reservations API failed: {Status} - {Body}", ordersResponse.StatusCode, errorBody);
                    errors.Add($"Reservations API: {ordersResponse.StatusCode} - Service Principal may need Reservations Reader at tenant/billing level");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching reservations");
                errors.Add($"Reservations error: {ex.Message}");
            }

            // Get purchase recommendations
            var subscriptions = connection.Subscriptions.Where(s => s.IsInScope).ToList();
            
            _logger.LogInformation("Fetching recommendations for {Count} in-scope subscriptions", subscriptions.Count);
            
            foreach (var sub in subscriptions)
            {
                var subName = sub.SubscriptionName ?? sub.SubscriptionId;
                try
                {
                    var recUrl = $"https://management.azure.com/subscriptions/{sub.SubscriptionId}/providers/Microsoft.Consumption/reservationRecommendations?api-version=2023-05-01";
                    var recResponse = await httpClient.GetAsync(recUrl);
                    
                    if (recResponse.IsSuccessStatusCode)
                    {
                        var recBody = await recResponse.Content.ReadAsStringAsync();
                        var recJson = System.Text.Json.JsonDocument.Parse(recBody);
                        
                        if (recJson.RootElement.TryGetProperty("value", out var recs))
                        {
                            foreach (var rec in recs.EnumerateArray())
                            {
                                purchaseRecommendations.Add(ParsePurchaseRecommendation(rec, subName));
                            }
                        }
                    }
                    else
                    {
                        errors.Add($"Recommendations for {subName}: {recResponse.StatusCode}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Recommendations for {subName}: {ex.Message}");
                }
            }

            // Generate intelligent insights with cost analysis
            insights = GenerateReservationInsights(reservations, purchaseRecommendations);

            // Calculate summary stats
            var active = reservations.Where(r => IsActiveReservation(r)).ToList();
            var withUtil = active.Where(r => HasUtilizationData(r)).ToList();
            var expiringSoon = active.Where(r => GetInt(r, "DaysToExpiry") >= 0 && GetInt(r, "DaysToExpiry") <= 90).ToList();
            var lowUtil = withUtil.Where(r => GetDouble(r, "Utilization30Day") > 0 && GetDouble(r, "Utilization30Day") < 80).ToList();
            var fullUtil = withUtil.Where(r => GetDouble(r, "Utilization30Day") >= 95).ToList();
            var zeroUtil = withUtil.Where(r => GetDouble(r, "Utilization30Day") == 0).ToList();
            var totalEstimatedSavings = active.Sum(r => GetDouble(r, "EstimatedMonthlySavings"));
            var totalWaste = active.Sum(r => GetDouble(r, "MonthlyWaste"));
            
            _logger.LogInformation("Reservation refresh complete: {Total} total, {Active} active, {Recommendations} recommendations",
                reservations.Count, active.Count, purchaseRecommendations.Count);

            // Build summary object
            var summary = new
            {
                TotalReservations = reservations.Count,
                ActiveReservations = active.Count,
                ExpiringSoon = expiringSoon.Count,
                LowUtilization = lowUtil.Count,
                FullUtilization = fullUtil.Count,
                ZeroUtilization = zeroUtil.Count,
                PurchaseRecommendations = purchaseRecommendations.Count,
                PotentialAnnualSavings = Math.Round(purchaseRecommendations.Sum(r => GetDouble(r, "AnnualSavings")), 2),
                EstimatedMonthlySavings = Math.Round(totalEstimatedSavings, 2),
                MonthlyWaste = Math.Round(totalWaste, 2)
            };

            // Update cache with results
            cache.Status = "Completed";
            cache.LastRefreshed = DateTime.UtcNow;
            cache.SummaryJson = JsonSerializer.Serialize(summary);
            cache.ReservationsJson = JsonSerializer.Serialize(reservations.OrderBy(r => GetInt(r, "DaysToExpiry", 9999)));
            cache.InsightsJson = JsonSerializer.Serialize(insights);
            cache.PurchaseRecommendationsJson = JsonSerializer.Serialize(purchaseRecommendations.OrderByDescending(r => GetDouble(r, "AnnualSavings")));
            cache.ErrorsJson = errors.Any() ? JsonSerializer.Serialize(errors) : null;
            cache.ErrorMessage = null;
            cache.UpdatedAt = DateTime.UtcNow;
            
            await _db.SaveChangesAsync();
            _logger.LogInformation("Reservation cache updated for customer {CustomerId}", customerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process reservation refresh for customer {CustomerId}", customerId);
            
            // Update cache with error
            if (cache != null)
            {
                cache.Status = "Failed";
                cache.ErrorMessage = ex.Message;
                cache.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
            }
        }
    }
    
    private async Task GetReservationUtilizationHistory(HttpClient httpClient, string orderId, string reservationId, Dictionary<string, object> resData, List<string> errors)
    {
        try
        {
            // Get daily utilization summaries for the last 30 days
            var startDate = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
            var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            
            var url = $"https://management.azure.com/providers/Microsoft.Capacity/reservationOrders/{orderId}/reservations/{reservationId}/providers/Microsoft.Consumption/reservationSummaries?api-version=2023-05-01&grain=daily&$filter=properties/usageDate ge {startDate} and properties/usageDate le {endDate}";
            
            var response = await httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                
                if (json.RootElement.TryGetProperty("value", out var summaries))
                {
                    var dailyUtilization = new List<Dictionary<string, object>>();
                    double totalUsedHours = 0;
                    double totalReservedHours = 0;
                    double avgUtil = 0;
                    double minUtil = 100;
                    double maxUtil = 0;
                    int dayCount = 0;
                    
                    foreach (var summary in summaries.EnumerateArray())
                    {
                        if (summary.TryGetProperty("properties", out var props))
                        {
                            var usageDate = props.TryGetProperty("usageDate", out var ud) ? ud.GetString() : null;
                            var usedHours = props.TryGetProperty("usedHours", out var uh) ? uh.GetDouble() : 0;
                            var reservedHours = props.TryGetProperty("reservedHours", out var rh) ? rh.GetDouble() : 0;
                            var utilPercent = props.TryGetProperty("avgUtilizationPercentage", out var ap) ? ap.GetDouble() 
                                : (reservedHours > 0 ? (usedHours / reservedHours) * 100 : 0);
                            var usedQuantity = props.TryGetProperty("usedQuantity", out var uq) ? uq.GetDouble() : 0;
                            var reservedQuantity = props.TryGetProperty("reservedQuantity", out var rq) ? rq.GetDouble() : 0;
                            
                            dailyUtilization.Add(new Dictionary<string, object>
                            {
                                ["Date"] = usageDate ?? "",
                                ["UsedHours"] = Math.Round(usedHours, 2),
                                ["ReservedHours"] = Math.Round(reservedHours, 2),
                                ["UtilizationPercent"] = Math.Round(utilPercent, 1),
                                ["UsedQuantity"] = usedQuantity,
                                ["ReservedQuantity"] = reservedQuantity
                            });
                            
                            totalUsedHours += usedHours;
                            totalReservedHours += reservedHours;
                            avgUtil += utilPercent;
                            minUtil = Math.Min(minUtil, utilPercent);
                            maxUtil = Math.Max(maxUtil, utilPercent);
                            dayCount++;
                        }
                    }
                    
                    resData["DailyUtilization"] = dailyUtilization.OrderByDescending(d => d["Date"]).ToList();
                    resData["TotalUsedHours30Day"] = Math.Round(totalUsedHours, 2);
                    resData["TotalReservedHours30Day"] = Math.Round(totalReservedHours, 2);
                    
                    if (dayCount > 0)
                    {
                        resData["Utilization30Day"] = Math.Round(avgUtil / dayCount, 1);
                        resData["MinUtilization30Day"] = Math.Round(minUtil, 1);
                        resData["MaxUtilization30Day"] = Math.Round(maxUtil, 1);
                    }
                    
                    // Calculate 7-day average from last 7 entries
                    var last7Days = dailyUtilization.Take(7).ToList();
                    if (last7Days.Any())
                    {
                        resData["Utilization7Day"] = Math.Round(last7Days.Average(d => Convert.ToDouble(d["UtilizationPercent"])), 1);
                    }
                    
                    // Calculate 1-day (yesterday or most recent)
                    if (dailyUtilization.Any())
                    {
                        resData["Utilization1Day"] = dailyUtilization.First()["UtilizationPercent"];
                    }
                    
                    _logger.LogInformation("Got {Days} days of utilization history for reservation {ResId}, avg util: {Util}%", 
                        dayCount, reservationId, resData.TryGetValue("Utilization30Day", out var u) ? u : "N/A");
                }
            }
            else
            {
                _logger.LogWarning("Failed to get utilization summaries for {ResId}: {Status}", reservationId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting utilization history for {ResId}", reservationId);
        }
    }
    
    private async Task GetReservationUsageDetails(HttpClient httpClient, string orderId, string reservationId, Dictionary<string, object> resData, List<string> errors)
    {
        try
        {
            // Get what resources are actually using this reservation (last 7 days)
            var startDate = DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-dd");
            var endDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            
            var url = $"https://management.azure.com/providers/Microsoft.Capacity/reservationOrders/{orderId}/reservations/{reservationId}/providers/Microsoft.Consumption/reservationDetails?api-version=2023-05-01&$filter=properties/usageDate ge {startDate} and properties/usageDate le {endDate}";
            
            var response = await httpClient.GetAsync(url);
            
            if (response.IsSuccessStatusCode)
            {
                var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                
                if (json.RootElement.TryGetProperty("value", out var details))
                {
                    var resourceUsage = new Dictionary<string, (string ResourceName, string ResourceId, double UsedHours, int DaysUsed)>();
                    
                    foreach (var detail in details.EnumerateArray())
                    {
                        if (detail.TryGetProperty("properties", out var props))
                        {
                            var instanceId = props.TryGetProperty("instanceId", out var ii) ? ii.GetString() ?? "" : "";
                            var instanceName = props.TryGetProperty("instanceName", out var iname) ? iname.GetString() ?? "" : "";
                            var usedHours = props.TryGetProperty("usedHours", out var uh) ? uh.GetDouble() : 0;
                            var skuName = props.TryGetProperty("skuName", out var sn) ? sn.GetString() ?? "" : "";
                            
                            // Extract resource name from instance ID if name not provided
                            if (string.IsNullOrEmpty(instanceName) && !string.IsNullOrEmpty(instanceId))
                            {
                                var parts = instanceId.Split('/');
                                instanceName = parts.Length > 0 ? parts[^1] : instanceId;
                            }
                            
                            var key = !string.IsNullOrEmpty(instanceId) ? instanceId : instanceName;
                            if (string.IsNullOrEmpty(key)) continue;
                            
                            if (resourceUsage.ContainsKey(key))
                            {
                                var existing = resourceUsage[key];
                                resourceUsage[key] = (existing.ResourceName, existing.ResourceId, existing.UsedHours + usedHours, existing.DaysUsed + 1);
                            }
                            else
                            {
                                resourceUsage[key] = (instanceName, instanceId, usedHours, 1);
                            }
                        }
                    }
                    
                    // Convert to list sorted by usage
                    var coveredResources = resourceUsage.Values
                        .OrderByDescending(r => r.UsedHours)
                        .Select(r => new Dictionary<string, object>
                        {
                            ["ResourceName"] = r.ResourceName,
                            ["ResourceId"] = r.ResourceId,
                            ["TotalUsedHours"] = Math.Round(r.UsedHours, 2),
                            ["DaysUsed"] = r.DaysUsed,
                            ["AvgHoursPerDay"] = Math.Round(r.UsedHours / Math.Max(r.DaysUsed, 1), 2)
                        })
                        .ToList();
                    
                    resData["CoveredResources"] = coveredResources;
                    resData["CoveredResourceCount"] = coveredResources.Count;
                    
                    _logger.LogInformation("Reservation {ResId} is covering {Count} resources", reservationId, coveredResources.Count);
                }
            }
            else
            {
                _logger.LogWarning("Failed to get usage details for {ResId}: {Status}", reservationId, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error getting usage details for {ResId}", reservationId);
        }
    }
    
    private void CalculateCostBenefit(Dictionary<string, object> resData)
    {
        try
        {
            var util = GetDouble(resData, "Utilization30Day");
            var term = GetStr(resData, "Term");
            var quantity = GetInt(resData, "Quantity");
            var resourceType = GetStr(resData, "ResourceType");
            
            // Typical RI discounts (these are approximate - actual varies by SKU/region)
            // 1-year: ~30-40% discount
            // 3-year: ~50-60% discount
            double discountPercent = term switch
            {
                "P1Y" => 35,  // 1 year
                "P3Y" => 55,  // 3 year
                _ => 35
            };
            
            // Breakeven utilization - below this, PAYG would be cheaper
            double breakevenUtil = 100 - discountPercent;
            
            resData["DiscountPercent"] = discountPercent;
            resData["BreakevenUtilization"] = Math.Round(breakevenUtil, 1);
            
            // Is this RI beneficial?
            bool isBeneficial = util >= breakevenUtil;
            resData["IsBeneficial"] = isBeneficial;
            
            // Calculate estimated costs (using rough estimates - actual prices vary)
            // Assume average VM costs ~£100/month per instance as baseline
            double estimatedMonthlyPAYGPerUnit = 100; // Placeholder - would need Price Sheet API for accurate data
            
            // Try to estimate based on resource type
            if (resourceType.Contains("VirtualMachines", StringComparison.OrdinalIgnoreCase))
            {
                estimatedMonthlyPAYGPerUnit = 150; // Higher for VMs
            }
            else if (resourceType.Contains("SqlDatabase", StringComparison.OrdinalIgnoreCase))
            {
                estimatedMonthlyPAYGPerUnit = 200; // SQL can be expensive
            }
            else if (resourceType.Contains("Storage", StringComparison.OrdinalIgnoreCase))
            {
                estimatedMonthlyPAYGPerUnit = 50; // Storage is cheaper
            }
            
            // Calculate costs
            double monthlyPAYGCost = estimatedMonthlyPAYGPerUnit * quantity;
            double monthlyRICost = monthlyPAYGCost * (1 - discountPercent / 100);
            double actualUtilizedCost = monthlyRICost * (util / 100);
            double wastedCost = monthlyRICost * ((100 - util) / 100);
            
            // If you had paid PAYG for just the utilized portion
            double paygForUtilizedPortion = monthlyPAYGCost * (util / 100);
            
            // Net savings = What PAYG would have cost for used portion - what we actually paid (RI cost)
            // If we use 70% of an RI, we compare:
            // - PAYG for 70% usage vs Full RI cost
            double netSavings = paygForUtilizedPortion - monthlyRICost;
            
            // Alternative calculation: effective savings vs PAYG for same usage
            double effectiveSavingsPercent = 0;
            if (paygForUtilizedPortion > 0)
            {
                effectiveSavingsPercent = ((paygForUtilizedPortion - monthlyRICost) / paygForUtilizedPortion) * 100;
            }
            
            resData["EstimatedMonthlyPAYG"] = Math.Round(monthlyPAYGCost, 2);
            resData["EstimatedMonthlyRICost"] = Math.Round(monthlyRICost, 2);
            resData["PAYGForUtilizedPortion"] = Math.Round(paygForUtilizedPortion, 2);
            resData["EstimatedMonthlySavings"] = Math.Round(Math.Max(netSavings, 0), 2);
            resData["MonthlyWaste"] = isBeneficial ? 0 : Math.Round(Math.Abs(netSavings), 2);
            resData["EffectiveSavingsPercent"] = Math.Round(effectiveSavingsPercent, 1);
            
            // Clear recommendation
            if (util == 0)
            {
                resData["Recommendation"] = "CANCEL - Zero utilization. This RI is completely wasted. Exchange or cancel immediately.";
                resData["RecommendationSeverity"] = "Critical";
            }
            else if (util < breakevenUtil)
            {
                var savings = Math.Abs(netSavings);
                resData["Recommendation"] = $"CONSIDER PAYG - At {util:F0}% utilization, you're losing ~£{savings:F0}/month vs PAYG. Breakeven is {breakevenUtil:F0}%.";
                resData["RecommendationSeverity"] = "High";
            }
            else if (util < 80)
            {
                resData["Recommendation"] = $"OPTIMIZE - RI is saving money but could save more. At {util:F0}% util, you save £{netSavings:F0}/month. At 100% you'd save £{(monthlyPAYGCost - monthlyRICost):F0}/month.";
                resData["RecommendationSeverity"] = "Medium";
            }
            else if (util < 95)
            {
                resData["Recommendation"] = $"GOOD - RI is working well at {util:F0}% utilization. Saving ~£{netSavings:F0}/month vs PAYG.";
                resData["RecommendationSeverity"] = "Low";
            }
            else
            {
                resData["Recommendation"] = $"EXCELLENT - Fully utilized at {util:F0}%. Maximum savings of ~£{netSavings:F0}/month vs PAYG.";
                resData["RecommendationSeverity"] = "Info";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error calculating cost benefit");
        }
    }

    private Dictionary<string, object> ParseReservationBasicInfo(System.Text.Json.JsonElement res, System.Text.Json.JsonElement order)
    {
        var data = new Dictionary<string, object>();
        
        try
        {
            data["ReservationId"] = res.GetProperty("name").GetString() ?? "";
            data["OrderId"] = order.GetProperty("name").GetString() ?? "";
            
            if (res.TryGetProperty("properties", out var props))
            {
                data["DisplayName"] = props.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                data["Status"] = props.TryGetProperty("provisioningState", out var ps) ? ps.GetString() ?? "" : "";
                data["Quantity"] = props.TryGetProperty("quantity", out var qty) ? qty.GetInt32() : 0;
                data["Term"] = props.TryGetProperty("term", out var term) ? term.GetString() ?? "" : "";
                data["AppliedScopeType"] = props.TryGetProperty("appliedScopeType", out var ast) ? ast.GetString() ?? "" : "";
                data["Renew"] = props.TryGetProperty("renew", out var renew) && renew.GetBoolean();
                data["ResourceType"] = props.TryGetProperty("reservedResourceType", out var rrt) ? rrt.GetString() ?? "" : "";
                data["Location"] = props.TryGetProperty("location", out var loc) ? loc.GetString() ?? "" : "";
                data["BillingPlan"] = props.TryGetProperty("billingPlan", out var bp) ? bp.GetString() ?? "" : "";
                
                // Applied scopes (what subscriptions/resource groups this RI applies to)
                if (props.TryGetProperty("appliedScopes", out var scopes))
                {
                    var scopeList = new List<string>();
                    foreach (var scope in scopes.EnumerateArray())
                    {
                        scopeList.Add(scope.GetString() ?? "");
                    }
                    data["AppliedScopes"] = scopeList;
                }
                
                // Purchase date
                if (props.TryGetProperty("purchaseDate", out var pd))
                {
                    data["PurchaseDate"] = pd.GetString() ?? "";
                    if (DateTime.TryParse(pd.GetString(), out var purchaseDate))
                    {
                        data["MonthsOwned"] = (int)((DateTime.UtcNow - purchaseDate).TotalDays / 30);
                        data["PurchaseDateFormatted"] = purchaseDate.ToString("dd MMM yyyy");
                    }
                }
                
                // Expiry date
                if (props.TryGetProperty("expiryDate", out var exp))
                {
                    data["ExpiryDate"] = exp.GetString() ?? "";
                    if (DateTime.TryParse(exp.GetString(), out var expiryDate))
                    {
                        data["DaysToExpiry"] = (int)(expiryDate - DateTime.UtcNow).TotalDays;
                        data["ExpiryDateFormatted"] = expiryDate.ToString("dd MMM yyyy");
                    }
                }
                
                // Effective date
                if (props.TryGetProperty("effectiveDateTime", out var eff))
                {
                    data["EffectiveDate"] = eff.GetString() ?? "";
                }
                
                // Benefit start date
                if (props.TryGetProperty("benefitStartTime", out var bst))
                {
                    data["BenefitStartTime"] = bst.GetString() ?? "";
                }
            }
            
            if (res.TryGetProperty("sku", out var sku) && sku.TryGetProperty("name", out var skuName))
                data["SkuName"] = skuName.GetString() ?? "";
                
            // Get display term
            var termStr = GetStr(data, "Term");
            data["TermDisplay"] = termStr switch
            {
                "P1Y" => "1 Year",
                "P3Y" => "3 Years",
                "P5Y" => "5 Years",
                _ => termStr
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parsing reservation basic info");
        }
        
        return data;
    }

    private Dictionary<string, object> ParsePurchaseRecommendation(System.Text.Json.JsonElement rec, string subscriptionName)
    {
        // Initialize ALL fields with defaults upfront - ensures fields always exist in output
        var data = new Dictionary<string, object>
        {
            ["SubscriptionName"] = subscriptionName,
            ["SkuName"] = "",
            ["Location"] = "",
            ["ResourceType"] = "",
            ["Term"] = "",
            ["Quantity"] = 0.0,
            ["LookBackPeriod"] = "",
            ["MonthlySavings"] = 0.0,
            ["AnnualSavings"] = 0.0,
            ["CostWithRI"] = 0.0,
            ["CostWithoutRI"] = 0.0,
            ["SavingsPercent"] = 0.0
        };
        
        try
        {
            // SKU - Azure returns this as a STRING at root level (not an object!)
            // e.g. "sku": "Standard_D2s_v3"
            if (rec.TryGetProperty("sku", out var skuElem))
            {
                if (skuElem.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    // SKU is a direct string value
                    data["SkuName"] = skuElem.GetString() ?? "";
                }
                else if (skuElem.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    // SKU is an object with name property (different API version)
                    if (skuElem.TryGetProperty("name", out var skuNameElem))
                        data["SkuName"] = skuNameElem.GetString() ?? "";
                }
            }
            
            // Location - at root level as string
            if (rec.TryGetProperty("location", out var locElem))
            {
                data["Location"] = locElem.GetString() ?? "";
            }
            
            // Parse properties section
            if (rec.TryGetProperty("properties", out var props))
            {
                // Resource type
                if (props.TryGetProperty("resourceType", out var rt))
                    data["ResourceType"] = rt.GetString() ?? "";
                
                // Term
                if (props.TryGetProperty("term", out var term))
                    data["Term"] = term.GetString() ?? "";
                
                // Quantity - try different property names
                if (props.TryGetProperty("recommendedQuantity", out var qty))
                    data["Quantity"] = qty.GetDouble();
                else if (props.TryGetProperty("quantity", out var qty2))
                    data["Quantity"] = qty2.GetDouble();
                
                // Look back period
                if (props.TryGetProperty("lookBackPeriod", out var lb))
                    data["LookBackPeriod"] = lb.GetString() ?? "";
                
                // Fallback: check properties for SKU if not found at root
                if (string.IsNullOrEmpty((string)data["SkuName"]))
                {
                    if (props.TryGetProperty("normalizedSize", out var ns))
                        data["SkuName"] = ns.GetString() ?? "";
                    else if (props.TryGetProperty("skuName", out var sn))
                        data["SkuName"] = sn.GetString() ?? "";
                }
                
                // Cost savings calculations
                double netSavings = 0;
                if (props.TryGetProperty("netSavings", out var nsVal))
                    netSavings = nsVal.GetDouble();
                
                double costWithRI = 0;
                if (props.TryGetProperty("totalCostWithReservedInstances", out var cw))
                    costWithRI = cw.GetDouble();
                
                double costWithoutRI = 0;
                if (props.TryGetProperty("costWithNoReservedInstances", out var cwout))
                    costWithoutRI = cwout.GetDouble();
                
                data["MonthlySavings"] = Math.Round(netSavings, 2);
                data["AnnualSavings"] = Math.Round(netSavings * 12, 2);
                data["CostWithRI"] = Math.Round(costWithRI, 2);
                data["CostWithoutRI"] = Math.Round(costWithoutRI, 2);
                data["SavingsPercent"] = costWithoutRI > 0 ? Math.Round((netSavings / costWithoutRI) * 100, 1) : 0;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing purchase recommendation. SubscriptionName={Sub}", subscriptionName);
        }
        
        return data;
    }

    /// <summary>
    /// Get cost analysis data from FOCUS parquet files
    /// </summary>
    [Function("GetFinOpsCostAnalysis")]
    public async Task<HttpResponseData> GetFinOpsCostAnalysis(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "customers/{customerId:guid}/finops/cost-analysis")] HttpRequestData req,
        Guid customerId)
    {
        try
        {
            // Parse query parameters
            var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
            var period = query["period"]?.ToLowerInvariant() ?? "";
            var daysStr = query["days"];
            var days = 30;
            
            // Period-based date selection (takes precedence over days)
            // Supported: mtd (month-to-date), lastmonth, last30, last60, last90
            DateTime startDate;
            DateTime endDate = DateTime.UtcNow;
            bool useDailyOnly = false;  // For MTD, only use daily exports
            bool useMonthlyOnly = false; // For lastmonth, only use monthly exports
            
            switch (period)
            {
                case "mtd":
                    startDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
                    useDailyOnly = true;  // MTD = current month only = daily export only
                    _logger.LogInformation("Period: MTD - {Start} to {End}, daily exports only", startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));
                    break;
                case "lastmonth":
                    startDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
                    endDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddDays(-1);
                    useMonthlyOnly = true;  // Last month = completed month = monthly export only
                    _logger.LogInformation("Period: Last Month - {Start} to {End}, monthly exports only", startDate.ToString("yyyy-MM-dd"), endDate.ToString("yyyy-MM-dd"));
                    break;
                case "last60":
                    startDate = DateTime.UtcNow.AddDays(-60);
                    useDailyOnly = true;  // Use daily exports only
                    break;
                case "last90":
                    startDate = DateTime.UtcNow.AddDays(-90);
                    useDailyOnly = true;  // Use daily exports only
                    break;
                default:
                    // Fall back to days parameter (30 days default)
                    if (!string.IsNullOrEmpty(daysStr) && int.TryParse(daysStr, out var parsedDays))
                    {
                        days = Math.Max(1, Math.Min(365, parsedDays));
                    }
                    startDate = DateTime.UtcNow.AddDays(-days);
                    useDailyOnly = true;  // Use daily exports only
                    break;
            }

            var customer = await _db.Customers
                .Include(c => c.Connections)
                    .ThenInclude(conn => conn.Subscriptions)
                .FirstOrDefaultAsync(c => c.Id == customerId);

            if (customer == null)
                return await CreateErrorResponse(req, HttpStatusCode.NotFound, "Customer not found");

            if (string.IsNullOrEmpty(customer.FinOpsStorageAccount))
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "FinOps storage not configured for this customer");

            if (string.IsNullOrEmpty(customer.FinOpsSasKeyVaultRef))
                return await CreateErrorResponse(req, HttpStatusCode.BadRequest, "FinOps SAS token not configured. Please configure a SAS token first.");

            // Get SAS token from Key Vault
            var kvClient = new SecretClient(new Uri(_keyVaultUrl), new DefaultAzureCredential());
            string sasToken;
            try
            {
                var secretResponse = await kvClient.GetSecretAsync(customer.FinOpsSasKeyVaultRef);
                sasToken = secretResponse.Value.Value;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve SAS token from Key Vault for customer {CustomerId}", customerId);
                return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, "Failed to retrieve SAS token from Key Vault");
            }

            var containerName = customer.FinOpsContainer ?? "ingestion";
            
            // Build container URI with SAS token
            var containerUri = sasToken.StartsWith("http") 
                ? new Uri(sasToken)  // Full URL stored
                : new Uri($"https://{customer.FinOpsStorageAccount}.blob.core.windows.net/{containerName}?{sasToken}");

            var containerClient = new BlobContainerClient(containerUri);

            // Check if container is accessible
            try
            {
                await containerClient.GetPropertiesAsync();
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 403 || ex.Status == 401)
            {
                _logger.LogWarning("SAS token expired or invalid for customer {CustomerId}", customerId);
                return await CreateErrorResponse(req, HttpStatusCode.Forbidden, "SAS token has expired or is invalid. Please generate a new SAS token.");
            }

            // Azure Cost Management exports data into monthly folders:
            // - tieva-daily-focus-cost/YYYYMMDD-YYYYMMDD/ (current month MTD, updated daily)
            // - tieva-monthly-focus-export/YYYYMMDD-YYYYMMDD/ (completed months)
            // We need to read from multiple folders based on the date range requested
            
            var allParquetFiles = new List<(string Name, DateTimeOffset? Modified, string FolderPath)>();
            
            // DIAGNOSTIC: Parse SAS token to show permissions (NOT the signature)
            var sasPermissions = "unknown";
            var sasExpiry = "unknown";
            var sasResourceType = "unknown";
            var sasService = "unknown";
            try
            {
                var tokenToAnalyze = sasToken.StartsWith("http") 
                    ? new Uri(sasToken).Query.TrimStart('?') 
                    : sasToken;
                var sasParts = tokenToAnalyze.Split('&');
                foreach (var part in sasParts)
                {
                    if (part.StartsWith("sp=")) sasPermissions = part.Substring(3);
                    if (part.StartsWith("se=")) sasExpiry = Uri.UnescapeDataString(part.Substring(3));
                    if (part.StartsWith("srt=")) sasResourceType = part.Substring(4);
                    if (part.StartsWith("ss=")) sasService = part.Substring(3);
                }
                _logger.LogInformation("SAS Token Info: Permissions={Perms}, ResourceTypes={RT}, Services={Svc}, Expiry={Exp}", 
                    sasPermissions, sasResourceType, sasService, sasExpiry);
            }
            catch (Exception ex) 
            { 
                _logger.LogWarning("Could not parse SAS token for diagnostics: {Err}", ex.Message);
            }
            
            // List ALL blobs to see what exists in container
            var allBlobPaths = new List<string>();
            var blobListingError = "";
            try
            {
                await foreach (var blob in containerClient.GetBlobsAsync())
                {
                    allBlobPaths.Add(blob.Name);
                    if (blob.Name.EndsWith(".parquet", StringComparison.OrdinalIgnoreCase) ||
                        blob.Name.EndsWith(".snappy.parquet", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = blob.Name.Split('/');
                        var folderPath = parts.Length >= 3 ? string.Join("/", parts.Take(parts.Length - 2)) : "";
                        allParquetFiles.Add((blob.Name, blob.Properties.LastModified, folderPath));
                    }
                }
            }
            catch (Exception ex)
            {
                blobListingError = ex.Message;
                _logger.LogError(ex, "Error listing blobs from storage");
            }
            
            // Get ALL unique folder paths (3 segments) to see both daily and monthly
            var allUniqueFolders = allBlobPaths
                .Where(p => p.Contains('/'))
                .Select(p => {
                    var parts = p.Split('/');
                    // Get up to 3 segments to capture focus-exports/tieva-XXX-focus-cost/YYYYMMDD-YYYYMMDD
                    return parts.Length >= 3 ? string.Join("/", parts.Take(3)) : string.Join("/", parts);
                })
                .Distinct()
                .OrderBy(x => x)
                .ToList();
            
            // Get unique top-level folders for diagnostics (2 segments)
            var topLevelFolders = allBlobPaths
                .Select(p => string.Join("/", p.Split('/').Take(2)))
                .Distinct()
                .ToList();
            
            // Check if monthly folder is present
            var hasMonthlyFolder = topLevelFolders.Any(f => f.Contains("monthly", StringComparison.OrdinalIgnoreCase));
            var hasDailyFolder = topLevelFolders.Any(f => f.Contains("daily", StringComparison.OrdinalIgnoreCase));
            
            _logger.LogInformation("Blob listing complete: {Count} blobs, {ParquetCount} parquet files, Monthly folder exists: {HasMonthly}, Daily folder exists: {HasDaily}", 
                allBlobPaths.Count, allParquetFiles.Count, hasMonthlyFolder, hasDailyFolder);
            
            // Sample paths for debugging - get first few from different folders
            var samplePaths = allBlobPaths
                .GroupBy(p => string.Join("/", p.Split('/').Take(2)))
                .SelectMany(g => g.Take(2))
                .Take(20)
                .ToList();

            _logger.LogInformation("Found {Count} total parquet files for customer {CustomerId}", allParquetFiles.Count, customerId);

            // Log sample file paths for debugging - include some from different folders
            var sampleFiles = allParquetFiles
                .GroupBy(f => f.Name.Split('/').Take(3).LastOrDefault() ?? "unknown")
                .SelectMany(g => g.Take(2))
                .Take(10)
                .ToList();
            foreach (var sample in sampleFiles)
            {
                _logger.LogInformation("Sample parquet file: {Name}", sample.Name);
            }

            if (!allParquetFiles.Any())
            {
                var emptyResponse = req.CreateResponse(HttpStatusCode.OK);
                await emptyResponse.WriteAsJsonAsync(new
                {
                    success = true,
                    data = new
                    {
                        totalCost = 0,
                        thisMonthCost = 0,
                        lastMonthCost = 0,
                        recordCount = 0,
                        message = "No cost data found. Ensure Cost Management exports are configured and running."
                    }
                });
                return emptyResponse;
            }

            // Azure Cost Management exports structure:
            // focus-exports/tieva-daily-focus-cost/20260101-20260131/202601121200/run-guid/part_0.parquet
            //                                      ^date-range       ^timestamp   ^run-id
            // 
            // IMPORTANT: Each subscription runs its own export at slightly different times!
            // So for 4 subscriptions on Jan 12, you might have:
            //   202601121200/ (Sub 1), 202601121201/ (Sub 2), 202601121202/ (Sub 3), 202601121203/ (Sub 4)
            //
            // Each file contains COMPLETE MTD data for that subscription.
            // We need to:
            // 1. Find the most recent DATE (not timestamp) for each date-range
            // 2. Get ALL files from that date (all subscriptions)
            // 3. Deduplicate records by unique key
            
            var dateRangePattern = new System.Text.RegularExpressions.Regex(@"(\d{8})-(\d{8})");
            var timestampPattern = new System.Text.RegularExpressions.Regex(@"/(\d{12})/"); // 202601121200 format (YYYYMMDDHHmm)
            
            // For "Last Month" comparison, we need data from the previous month regardless of days parameter
            var lastMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
            
            // Parse each file to extract date-range, timestamp, timestamp DATE, and export type (daily vs monthly)
            var parsedFiles = allParquetFiles
                .Select(f => {
                    var dateMatch = dateRangePattern.Match(f.Name);
                    var timestampMatch = timestampPattern.Match(f.Name);
                    if (dateMatch.Success && timestampMatch.Success)
                    {
                        var folderStart = DateTime.ParseExact(dateMatch.Groups[1].Value, "yyyyMMdd", null);
                        var folderEnd = DateTime.ParseExact(dateMatch.Groups[2].Value, "yyyyMMdd", null);
                        var dateRangeKey = dateMatch.Value; // e.g. "20260101-20260131"
                        var timestamp = timestampMatch.Groups[1].Value; // e.g. "202601121200"
                        var timestampDate = timestamp.Substring(0, 8); // e.g. "20260112" (just the date part)
                        
                        // Determine if this is a daily or monthly export based on folder path
                        var isMonthlyExport = f.Name.Contains("monthly", StringComparison.OrdinalIgnoreCase);
                        
                        return new { 
                            File = f, 
                            DateRangeKey = dateRangeKey, 
                            FolderStart = folderStart, 
                            FolderEnd = folderEnd,
                            Timestamp = timestamp,
                            TimestampDate = timestampDate, // Date portion only (YYYYMMDD)
                            IsMonthlyExport = isMonthlyExport
                        };
                    }
                    return null;
                })
                .Where(x => x != null)
                .ToList();
            
            // IMPORTANT: File selection logic:
            // - Daily exports contain MTD (month-to-date) data for CURRENT MONTH ONLY
            // - Monthly exports contain COMPLETE data for FINISHED months
            // 
            // For date range queries:
            // - Current month data → Daily export
            // - Previous month data → Monthly export (if available)
            // - Older months → Monthly exports only
            //
            // This prevents double-counting when both daily and monthly exist for the same period.
            
            var thisMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
            
            // Apply export type filtering based on period selection
            var dailyExportFilesList = new List<object>();
            var monthlyExportFilesList = new List<object>();
            
            if (useDailyOnly)
            {
                // Daily exports only - MTD uses current month, others use startDate
                var dailyFilterDate = period == "mtd" ? thisMonthStart : startDate;
                dailyExportFilesList = parsedFiles
                    .Where(x => !x!.IsMonthlyExport && x.FolderStart >= dailyFilterDate)
                    .Cast<object>()
                    .ToList();
                _logger.LogInformation("Daily exports mode: Using {DailyCount} daily files from {FilterDate}", 
                    dailyExportFilesList.Count, dailyFilterDate.ToString("yyyy-MM-dd"));
            }
            else if (useMonthlyOnly)
            {
                // Last Month: Only monthly exports for previous month
                monthlyExportFilesList = parsedFiles
                    .Where(x => x!.IsMonthlyExport && x.FolderStart >= startDate && x.FolderEnd <= endDate)
                    .Cast<object>()
                    .ToList();
                _logger.LogInformation("Last Month mode: Using {MonthlyCount} monthly files only", monthlyExportFilesList.Count);
            }
            
            // ALWAYS load monthly files for LastMonth comparison (regardless of period selection)
            // This is needed for the Month-over-Month comparison section
            var lastMonthForComparison = thisMonthStart.AddMonths(-1);
            var lastMonthEndForComparison = thisMonthStart.AddDays(-1);
            var monthlyFilesForComparison = parsedFiles
                .Where(x => x!.IsMonthlyExport && 
                           x.FolderStart >= lastMonthForComparison && 
                           x.FolderEnd <= lastMonthEndForComparison)
                .Cast<object>()
                .ToList();
            _logger.LogInformation("Monthly files for MoM comparison: {Count} files for {Start} to {End}",
                monthlyFilesForComparison.Count, lastMonthForComparison.ToString("yyyy-MM-dd"), lastMonthEndForComparison.ToString("yyyy-MM-dd"));
            
            // Log all unique date ranges found (before and after filtering) for debugging
            var allDateRanges = allParquetFiles
                .Select(f => {
                    var match = dateRangePattern.Match(f.Name);
                    return match.Success ? match.Value : null;
                })
                .Where(x => x != null)
                .Distinct()
                .ToList();
            _logger.LogInformation("All date ranges found in storage: {Ranges}", string.Join(", ", allDateRanges!));
            _logger.LogInformation("Parsed files count (after date filter): {Count}, startDate: {Date}", parsedFiles.Count, startDate.ToString("yyyy-MM-dd"));
            
            // For each date-range in DAILY exports, find the MOST RECENT DATE and get ALL files from that date
            // This ensures we get all subscription exports from the same day
            var dailyFilesToRead = dailyExportFilesList
                .Cast<dynamic>()
                .GroupBy(f => (string)f.DateRangeKey)
                .SelectMany(dateRangeGroup => {
                    // Find the most recent DATE (not timestamp)
                    var mostRecentDate = dateRangeGroup
                        .OrderByDescending(f => (string)f.TimestampDate)
                        .First().TimestampDate;
                    
                    // Return ALL files from that DATE (all subscriptions that ran on that day)
                    return dateRangeGroup.Where(f => f.TimestampDate == mostRecentDate);
                })
                .ToList();
            
            // For each date-range in MONTHLY exports, find the MOST RECENT DATE and get ALL files from that date
            var monthlyFilesToRead = monthlyExportFilesList
                .Cast<dynamic>()
                .GroupBy(f => (string)f.DateRangeKey)
                .SelectMany(dateRangeGroup => {
                    var mostRecentDate = dateRangeGroup
                        .OrderByDescending(f => (string)f.TimestampDate)
                        .First().TimestampDate;
                    return dateRangeGroup.Where(f => f.TimestampDate == mostRecentDate);
                })
                .ToList();
            
            // Process monthly files for MoM comparison (always needed, separate from main view)
            var comparisonMonthlyFilesToRead = monthlyFilesForComparison
                .Cast<dynamic>()
                .GroupBy(f => (string)f.DateRangeKey)
                .SelectMany(dateRangeGroup => {
                    if (!dateRangeGroup.Any()) return Enumerable.Empty<dynamic>();
                    var mostRecentDate = dateRangeGroup
                        .OrderByDescending(f => (string)f.TimestampDate)
                        .First().TimestampDate;
                    return dateRangeGroup.Where(f => f.TimestampDate == mostRecentDate);
                })
                .ToList();
            _logger.LogInformation("Comparison monthly files to read: {Count}", comparisonMonthlyFilesToRead.Count);
            
            // COMBINE daily and monthly files - both are needed for the full date range
            var filesToRead = dailyFilesToRead.Concat(monthlyFilesToRead).ToList();
            
            _logger.LogInformation("Combined files: {DailyCount} daily + {MonthlyCount} monthly = {TotalCount} total files to read",
                (int)dailyFilesToRead.Count, (int)monthlyFilesToRead.Count, (int)filesToRead.Count);
            
            // Group for logging
            var folderSummary = filesToRead
                .GroupBy(f => f!.DateRangeKey)
                .Select(g => new {
                    DateRange = g.Key,
                    Start = g.First()!.FolderStart,
                    End = g.First()!.FolderEnd,
                    TimestampDate = g.First()!.TimestampDate,
                    Timestamps = g.Select(f => f!.Timestamp).Distinct().OrderBy(t => t).ToList(),
                    FileCount = g.Count()
                })
                .OrderByDescending(f => f.Start)
                .ToList();
            
            _logger.LogInformation("Selected {Count} date-range folders, {FileCount} files (most recent date, all subscriptions), covering date range starting {StartDate}", 
                folderSummary.Count, (int)filesToRead.Count, startDate.ToString("yyyy-MM-dd"));
            
            foreach (var folder in folderSummary)
            {
                _logger.LogInformation("  {DateRange} ({Start} to {End}) - date {Date}, {TimestampCount} timestamps, {FileCount} files", 
                    (string)folder.DateRange, ((DateTime)folder.Start).ToString("yyyy-MM-dd"), ((DateTime)folder.End).ToString("yyyy-MM-dd"), 
                    (string)folder.TimestampDate, folder.Timestamps.Count, folder.FileCount);
            }

            // Parse parquet files
            var allRecords = new List<CostRecord>();
            var availableColumns = new List<string>();
            var filesProcessed = new List<string>();
            
            var sortedFiles = filesToRead.Select(f => f!.File).ToList();

            foreach (var fileInfo in sortedFiles)
            {
                try
                {
                    // fileInfo is a tuple (Name, Modified, FolderPath) - access via Item1 due to dynamic
                    var fileName = (string)fileInfo.Item1;
                    var blobClient = containerClient.GetBlobClient(fileName);
                    filesProcessed.Add(fileName);
                    using var stream = new MemoryStream();
                    await blobClient.DownloadToAsync(stream);
                    stream.Position = 0;

                    var (records, columns) = await ParseParquetFile(stream);
                    allRecords.AddRange(records);
                    
                    // Capture column names from first file for diagnostics
                    if (!availableColumns.Any() && columns.Any())
                    {
                        availableColumns.AddRange(columns);
                        _logger.LogInformation("Parquet columns found: {Columns}", string.Join(", ", columns.Take(30)));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error parsing parquet file {FileName}", (string)fileInfo.Item1);
                }
            }

            _logger.LogInformation("Parsed {Count} cost records from DAILY exports for customer {CustomerId} (before deduplication)", allRecords.Count, customerId);

            // Now read MONTHLY export files separately (for LastMonthCost comparison only)
            var monthlyRecords = new List<CostRecord>();
            var monthlyFilesProcessed = new List<string>();
            
            var sortedMonthlyFiles = comparisonMonthlyFilesToRead.Select(f => f!.File).ToList();
            foreach (var fileInfo in sortedMonthlyFiles)
            {
                try
                {
                    // fileInfo is a tuple (Name, Modified, FolderPath) - access via Item1 due to dynamic
                    var fileName = (string)fileInfo.Item1;
                    var blobClient = containerClient.GetBlobClient(fileName);
                    monthlyFilesProcessed.Add(fileName);
                    using var stream = new MemoryStream();
                    await blobClient.DownloadToAsync(stream);
                    stream.Position = 0;

                    var (records, _) = await ParseParquetFile(stream);
                    monthlyRecords.AddRange(records);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error parsing monthly parquet file {FileName}", (string)fileInfo.Item1);
                }
            }
            
            _logger.LogInformation("Parsed {Count} cost records from MONTHLY exports (for LastMonthCost only)", monthlyRecords.Count);

            // DEDUPLICATION for daily records: When multiple exports run on the same day (manual runs), we may have duplicate records
            // Deduplicate by unique key: Date + SubscriptionId + ResourceId + ServiceName + BilledCost
            var recordCountBefore = allRecords.Count;
            allRecords = allRecords
                .GroupBy(r => new { 
                    r.Date, 
                    r.SubscriptionId, 
                    ResourceId = r.ResourceName, // ResourceName often contains the resource ID
                    r.ServiceName,
                    r.BilledCost // Include cost to handle edge cases where same resource has different line items
                })
                .Select(g => g.First()) // Take first occurrence of each unique record
                .ToList();
            
            var duplicatesRemoved = recordCountBefore - allRecords.Count;
            if (duplicatesRemoved > 0)
            {
                _logger.LogInformation("Removed {Count} duplicate records from daily exports (from {Before} to {After})", 
                    duplicatesRemoved, recordCountBefore, allRecords.Count);
            }
            
            // Also deduplicate monthly records
            var monthlyCountBefore = monthlyRecords.Count;
            monthlyRecords = monthlyRecords
                .GroupBy(r => new { 
                    r.Date, 
                    r.SubscriptionId, 
                    ResourceId = r.ResourceName,
                    r.ServiceName,
                    r.BilledCost
                })
                .Select(g => g.First())
                .ToList();
            var monthlyDuplicatesRemoved = monthlyCountBefore - monthlyRecords.Count;

            // Filter daily records to requested date range
            // Note: endDate already defined at top of function based on period selection
            var filteredRecords = allRecords.Where(r => r.Date >= startDate).ToList();
            
            // Monthly records are already filtered to last month by file selection
            // Pass them separately to the aggregation function
            var result = AggregateCostData(filteredRecords, startDate, endDate, monthlyRecords);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                success = true,
                data = result,
                diagnostics = new
                {
                    // Daily export stats (used for rolling date queries)
                    dailyFilesProcessed = filesProcessed.Count,
                    dailyRecordsLoaded = allRecords.Count,
                    dailyRecordsInDateRange = filteredRecords.Count,
                    // Monthly export stats (used ONLY for LastMonthCost)
                    monthlyFilesProcessed = monthlyFilesProcessed.Count,
                    monthlyRecordsLoaded = monthlyRecords.Count,
                    monthlyDuplicatesRemoved = monthlyDuplicatesRemoved,
                    // General stats
                    totalParquetFilesFound = allParquetFiles.Count,
                    foldersSelected = folderSummary.Select(f => new { 
                        folder = f.DateRange, 
                        dateRange = $"{f.Start:yyyy-MM-dd} to {f.End:yyyy-MM-dd}",
                        exportDate = f.TimestampDate,
                        subscriptionCount = f.Timestamps.Count,
                        fileCount = f.FileCount,
                        exportType = "daily"
                    }),
                    recordsBeforeDeduplication = recordCountBefore,
                    duplicatesRemoved = duplicatesRemoved,
                    dateRangeDays = days,
                    requestedStartDate = startDate.ToString("yyyy-MM-dd"),
                    allDateRangesInStorage = allDateRanges,
                    topLevelFolders = topLevelFolders,
                    totalBlobsInContainer = allBlobPaths.Count,
                    uniqueFolderPaths = allUniqueFolders,
                    filesUsed = filesProcessed.Select(f => f.Split('/').Last()).ToList(),
                    availableColumns = availableColumns.Take(50).ToList(),
                    // NEW: Enhanced diagnostics for debugging
                    hasMonthlyFolder = hasMonthlyFolder,
                    hasDailyFolder = hasDailyFolder,
                    sasTokenInfo = new {
                        permissions = sasPermissions,
                        resourceType = sasResourceType,
                        service = sasService,
                        expiry = sasExpiry
                    },
                    sampleBlobPaths = samplePaths.Take(10),
                    blobListingError = blobListingError
                }
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get cost analysis for customer {CustomerId}", customerId);
            return await CreateErrorResponse(req, HttpStatusCode.InternalServerError, $"Failed to get cost analysis: {ex.Message}");
        }
    }

    private async Task<(List<CostRecord> Records, List<string> Columns)> ParseParquetFile(Stream stream)
    {
        var records = new List<CostRecord>();
        var columnNames = new List<string>();
        
        using var reader = await Parquet.ParquetReader.CreateAsync(stream);
        
        for (int i = 0; i < reader.RowGroupCount; i++)
        {
            using var rowGroupReader = reader.OpenRowGroupReader(i);
            var dataFields = reader.Schema.GetDataFields();
            
            // Capture column names (only from first row group)
            if (i == 0)
            {
                columnNames = dataFields.Select(f => f.Name).ToList();
            }
            
            // Read columns we need
            var columns = new Dictionary<string, object[]>();
            foreach (var field in dataFields)
            {
                try
                {
                    var column = await rowGroupReader.ReadColumnAsync(field);
                    columns[field.Name.ToLowerInvariant()] = column.Data.Cast<object>().ToArray();
                }
                catch { }
            }

            var rowCount = columns.Values.FirstOrDefault()?.Length ?? 0;
            
            for (int row = 0; row < rowCount; row++)
            {
                try
                {
                    var record = new CostRecord();
                    
                    // FOCUS column names (case-insensitive)
                    record.BilledCost = GetColumnValue<decimal>(columns, row, "billedcost", "effectivecost", "cost");
                    record.ServiceName = GetColumnStringValue(columns, row, "servicename", "metercategory", "service");
                    record.ServiceCategory = GetColumnStringValue(columns, row, "servicecategory", "consumedservice", "category");
                    record.ResourceName = GetColumnStringValue(columns, row, "resourcename", "resourceid", "resource");
                    record.ResourceType = GetColumnStringValue(columns, row, "resourcetype", "metersubcategory");
                    // FOCUS doesn't have ResourceGroup - extract from ResourceId if available
                    var resourceId = GetColumnStringValue(columns, row, "resourceid");
                    record.ResourceGroup = ExtractResourceGroupFromId(resourceId);
                    record.Region = GetColumnStringValue(columns, row, "regionname", "region", "resourcelocation", "location");
                    record.SubscriptionName = GetColumnStringValue(columns, row, "subaccountname", "subscriptionname", "subscription");
                    record.SubscriptionId = GetColumnStringValue(columns, row, "subaccountid", "subscriptionid");
                    
                    // Parse date
                    var dateStr = GetColumnStringValue(columns, row, "chargeperiodstart", "billingperiodstartdate", "usagedatetime", "date");
                    if (DateTime.TryParse(dateStr, out var date))
                    {
                        record.Date = date.Date;
                    }
                    else
                    {
                        record.Date = DateTime.UtcNow.Date;
                    }

                    // Include all records with non-zero cost
                    if (record.BilledCost != 0)
                    {
                        records.Add(record);
                    }
                }
                catch { }
            }
        }
        
        return (records, columnNames);
    }

    private T GetColumnValue<T>(Dictionary<string, object[]> columns, int row, params string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            if (columns.TryGetValue(name, out var col) && row < col.Length && col[row] != null)
            {
                try
                {
                    return (T)Convert.ChangeType(col[row], typeof(T));
                }
                catch { }
            }
        }
        return default!;
    }

    private string GetColumnStringValue(Dictionary<string, object[]> columns, int row, params string[] possibleNames)
    {
        foreach (var name in possibleNames)
        {
            if (columns.TryGetValue(name, out var col) && row < col.Length && col[row] != null)
            {
                return col[row].ToString() ?? "";
            }
        }
        return "";
    }

    private string ExtractResourceGroupFromId(string resourceId)
    {
        // ResourceId format: /subscriptions/{sub}/resourceGroups/{rg}/providers/{provider}/{type}/{name}
        if (string.IsNullOrEmpty(resourceId)) return "";
        
        var lower = resourceId.ToLowerInvariant();
        var rgIndex = lower.IndexOf("/resourcegroups/");
        if (rgIndex < 0) return "";
        
        var startIndex = rgIndex + "/resourcegroups/".Length;
        var endIndex = resourceId.IndexOf('/', startIndex);
        
        if (endIndex < 0) return resourceId.Substring(startIndex);
        return resourceId.Substring(startIndex, endIndex - startIndex);
    }

    private object AggregateCostData(List<CostRecord> records, DateTime startDate, DateTime endDate, List<CostRecord>? monthlyRecords = null)
    {
        // Filter to requested date range for main metrics
        // 'records' contains ONLY daily export data - used for rolling date queries
        var rangeRecords = records.Where(r => r.Date >= startDate).ToList();
        var totalCost = rangeRecords.Sum(r => (double)r.BilledCost);
        
        // Calendar month calculations
        var thisMonthStart = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1);
        var lastMonthStart = thisMonthStart.AddMonths(-1);
        var lastMonthEnd = thisMonthStart.AddDays(-1);
        
        // This month cost comes from daily exports (current MTD data)
        var thisMonthCost = records.Where(r => r.Date >= thisMonthStart).Sum(r => (double)r.BilledCost);
        
        // Last month cost: use MONTHLY export if available, otherwise fall back to daily exports
        // Monthly exports contain complete data for the previous month
        double lastMonthCost;
        double lastMonthSamePeriodCost = 0;
        var currentDayOfMonth = DateTime.UtcNow.Day;
        
        if (monthlyRecords != null && monthlyRecords.Any())
        {
            // Use monthly export data - this is the authoritative source for completed months
            lastMonthCost = monthlyRecords.Sum(r => (double)r.BilledCost);
            
            // Same period comparison: Dec 1-13 if today is Jan 13
            var lastMonthSamePeriodEnd = lastMonthStart.AddDays(currentDayOfMonth - 1);
            lastMonthSamePeriodCost = monthlyRecords
                .Where(r => r.Date >= lastMonthStart && r.Date <= lastMonthSamePeriodEnd)
                .Sum(r => (double)r.BilledCost);
        }
        else
        {
            // Fallback: try to get from daily exports (may be incomplete if daily export doesn't cover last month)
            lastMonthCost = records.Where(r => r.Date >= lastMonthStart && r.Date <= lastMonthEnd).Sum(r => (double)r.BilledCost);
            
            // Same period comparison from daily exports
            var lastMonthSamePeriodEnd = lastMonthStart.AddDays(currentDayOfMonth - 1);
            lastMonthSamePeriodCost = records
                .Where(r => r.Date >= lastMonthStart && r.Date <= lastMonthSamePeriodEnd)
                .Sum(r => (double)r.BilledCost);
        }

        // Daily trend - use range records
        var dailyTrend = rangeRecords
            .GroupBy(r => r.Date.Date)
            .Select(g => new { Date = g.Key.ToString("yyyy-MM-dd"), Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2) })
            .OrderBy(d => d.Date)
            .ToList();

        // Top services - use range records
        var topServices = rangeRecords
            .GroupBy(r => string.IsNullOrEmpty(r.ServiceName) ? "Other" : r.ServiceName)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(s => s.Cost)
            .Take(10)
            .ToList();

        // Top resource groups - use range records
        var topResourceGroups = rangeRecords
            .GroupBy(r => string.IsNullOrEmpty(r.ResourceGroup) ? "Other" : r.ResourceGroup)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(rg => rg.Cost)
            .Take(10)
            .ToList();

        // Top resources - use range records
        var topResources = rangeRecords
            .GroupBy(r => string.IsNullOrEmpty(r.ResourceName) ? "Unknown" : r.ResourceName)
            .Select(g => new { 
                Name = g.Key, 
                Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), 
                Count = g.Count(),
                ResourceType = g.First().ResourceType,
                ResourceGroup = g.First().ResourceGroup
            })
            .OrderByDescending(r => r.Cost)
            .Take(20)
            .ToList();

        // By region - use range records
        var byRegion = rangeRecords
            .GroupBy(r => string.IsNullOrEmpty(r.Region) ? "Unknown" : r.Region)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(r => r.Cost)
            .ToList();

        // By subscription - use range records
        var bySubscription = rangeRecords
            .GroupBy(r => string.IsNullOrEmpty(r.SubscriptionName) ? r.SubscriptionId : r.SubscriptionName)
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(s => s.Cost)
            .ToList();

        // By service category - use range records
        var byServiceCategory = rangeRecords
            .GroupBy(r => string.IsNullOrEmpty(r.ServiceCategory) ? "Other" : r.ServiceCategory)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(c => c.Cost)
            .ToList();

        // Weekly comparison
        var thisWeekStart = DateTime.UtcNow.Date.AddDays(-(int)DateTime.UtcNow.DayOfWeek);
        var lastWeekStart = thisWeekStart.AddDays(-7);
        var lastWeekEnd = thisWeekStart.AddDays(-1);

        var thisWeekByCategory = rangeRecords
            .Where(r => r.Date >= thisWeekStart)
            .GroupBy(r => string.IsNullOrEmpty(r.ServiceCategory) ? "Other" : r.ServiceCategory)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(c => c.Cost)
            .ToList();

        var lastWeekByCategory = rangeRecords
            .Where(r => r.Date >= lastWeekStart && r.Date <= lastWeekEnd)
            .GroupBy(r => string.IsNullOrEmpty(r.ServiceCategory) ? "Other" : r.ServiceCategory)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(c => c.Cost)
            .ToList();

        var thisWeekByResourceGroup = rangeRecords
            .Where(r => r.Date >= thisWeekStart)
            .GroupBy(r => string.IsNullOrEmpty(r.ResourceGroup) ? "Other" : r.ResourceGroup)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(rg => rg.Cost)
            .Take(10)
            .ToList();

        var lastWeekByResourceGroup = rangeRecords
            .Where(r => r.Date >= lastWeekStart && r.Date <= lastWeekEnd)
            .GroupBy(r => string.IsNullOrEmpty(r.ResourceGroup) ? "Other" : r.ResourceGroup)
            .Select(g => new { Name = g.Key, Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2), Count = g.Count() })
            .OrderByDescending(rg => rg.Cost)
            .Take(10)
            .ToList();

        // All resources (limited) - use range records
        var allResources = rangeRecords
            .GroupBy(r => new { r.ResourceName, r.ResourceType, r.ResourceGroup, r.Region, r.SubscriptionName, r.ServiceName, r.ServiceCategory })
            .Select(g => new {
                Name = string.IsNullOrEmpty(g.Key.ResourceName) ? "Unknown" : g.Key.ResourceName,
                ResourceType = g.Key.ResourceType ?? "Unknown",
                ResourceGroup = g.Key.ResourceGroup ?? "Unknown",
                Region = g.Key.Region ?? "Unknown",
                Subscription = g.Key.SubscriptionName ?? "Unknown",
                ServiceName = g.Key.ServiceName ?? "Unknown",
                ServiceCategory = g.Key.ServiceCategory ?? "Other",
                Cost = Math.Round(g.Sum(r => (double)r.BilledCost), 2)
            })
            .OrderByDescending(r => r.Cost)
            .Take(500)
            .ToList();

        return new
        {
            TotalCost = Math.Round(totalCost, 2),
            ThisMonthCost = Math.Round(thisMonthCost, 2),
            LastMonthCost = Math.Round(lastMonthCost, 2),
            LastMonthSamePeriodCost = Math.Round(lastMonthSamePeriodCost, 2),
            StartDate = startDate.ToString("yyyy-MM-dd"),
            EndDate = endDate.ToString("yyyy-MM-dd"),
            RecordCount = rangeRecords.Count,
            DailyTrend = dailyTrend,
            TopServices = topServices,
            TopResourceGroups = topResourceGroups,
            TopResources = topResources,
            ByRegion = byRegion,
            BySubscription = bySubscription,
            ByServiceCategory = byServiceCategory,
            AllResources = allResources,
            UniqueResourceCount = rangeRecords.Select(r => r.ResourceName).Distinct().Count(),
            UniqueResourceTypes = rangeRecords.Select(r => r.ResourceType).Where(t => !string.IsNullOrEmpty(t)).Distinct().Count(),
            UniqueResourceGroups = rangeRecords.Select(r => r.ResourceGroup).Where(rg => !string.IsNullOrEmpty(rg)).Distinct().Count(),
            ThisWeekByCategory = thisWeekByCategory,
            LastWeekByCategory = lastWeekByCategory,
            ThisWeekByResourceGroup = thisWeekByResourceGroup,
            LastWeekByResourceGroup = lastWeekByResourceGroup
        };
    }

    private class CostRecord
    {
        public decimal BilledCost { get; set; }
        public string ServiceName { get; set; } = "";
        public string ServiceCategory { get; set; } = "";
        public string ResourceName { get; set; } = "";
        public string ResourceType { get; set; } = "";
        public string ResourceGroup { get; set; } = "";
        public string Region { get; set; } = "";
        public string SubscriptionName { get; set; } = "";
        public string SubscriptionId { get; set; } = "";
        public DateTime Date { get; set; }
    }

    private List<Dictionary<string, object>> GenerateReservationInsights(List<Dictionary<string, object>> reservations, List<Dictionary<string, object>> purchaseRecs)
    {
        var insights = new List<Dictionary<string, object>>();
        var active = reservations.Where(r => IsActiveReservation(r)).ToList();
        var processedIds = new HashSet<string>(); // Track which reservations have insights
        
        // 1. CRITICAL: Zero utilization - paying for nothing (only if we HAVE utilization data)
        var zeroUtil = active.Where(r => HasUtilizationData(r) && GetDouble(r, "Utilization30Day") == 0).ToList();
        foreach (var res in zeroUtil)
        {
            processedIds.Add(GetStr(res, "ReservationId"));
            insights.Add(new Dictionary<string, object>
            {
                ["Priority"] = "Critical",
                ["Type"] = "ZeroUtilization",
                ["Icon"] = "\U0001F6A8", // 🚨
                ["Title"] = $"{GetStr(res, "DisplayName")} has 0% utilization",
                ["Description"] = "This reservation is completely unused. You're paying for capacity that isn't being used.",
                ["Recommendation"] = "Exchange or cancel this reservation immediately. Consider if the workload was decommissioned or moved.",
                ["Action"] = "Exchange/Cancel",
                ["ReservationId"] = GetStr(res, "ReservationId")
            });
        }
        
        // 2. HIGH: Expiring soon with high utilization - renew!
        var expiringHighUtil = active.Where(r => 
            GetInt(r, "DaysToExpiry") >= 0 && GetInt(r, "DaysToExpiry") <= 90 && 
            HasUtilizationData(r) && GetDouble(r, "Utilization30Day") >= 95).ToList();
        foreach (var res in expiringHighUtil)
        {
            processedIds.Add(GetStr(res, "ReservationId"));
            var autoRenew = res.TryGetValue("Renew", out var r) && r is bool b && b;
            var util = GetDouble(res, "Utilization30Day");
            insights.Add(new Dictionary<string, object>
            {
                ["Priority"] = "High",
                ["Type"] = "RenewHighUtilization",
                ["Icon"] = "\u2705", // ✅
                ["Title"] = $"{GetStr(res, "DisplayName")} expires in {GetInt(res, "DaysToExpiry")} days - {util:F0}% utilized",
                ["Description"] = $"This reservation is well utilized and expiring soon. {(autoRenew ? "Auto-renew is ON." : "Auto-renew is OFF!")}",
                ["Recommendation"] = autoRenew ? "Review renewal terms to ensure best pricing." : "ENABLE AUTO-RENEW or manually renew to maintain savings.",
                ["Action"] = autoRenew ? "Review" : "Enable Auto-Renew",
                ["ReservationId"] = GetStr(res, "ReservationId")
            });
        }
        
        // 3. HIGH/MEDIUM: Expiring soon with low utilization - don't renew!
        var expiringLowUtil = active.Where(r => 
            GetInt(r, "DaysToExpiry") >= 0 && GetInt(r, "DaysToExpiry") <= 90 && 
            HasUtilizationData(r) && GetDouble(r, "Utilization30Day") > 0 && GetDouble(r, "Utilization30Day") < 80).ToList();
        foreach (var res in expiringLowUtil)
        {
            processedIds.Add(GetStr(res, "ReservationId"));
            var autoRenew = res.TryGetValue("Renew", out var r) && r is bool b && b;
            var util = GetDouble(res, "Utilization30Day");
            insights.Add(new Dictionary<string, object>
            {
                ["Priority"] = autoRenew ? "High" : "Medium",
                ["Type"] = "DontRenew",
                ["Icon"] = "\U0001F6AB", // 🚫
                ["Title"] = $"{GetStr(res, "DisplayName")} expires in {GetInt(res, "DaysToExpiry")} days - only {util:F0}% used",
                ["Description"] = $"Low utilization means renewal would waste money. {(autoRenew ? "WARNING: Auto-renew is ON!" : "Auto-renew is off.")}",
                ["Recommendation"] = autoRenew ? "DISABLE AUTO-RENEW immediately to avoid wasting money." : "Do not renew. Switch to PAYG or right-size first.",
                ["Action"] = autoRenew ? "Disable Auto-Renew" : "Let Expire",
                ["WastePercent"] = Math.Round(100 - util, 0),
                ["ReservationId"] = GetStr(res, "ReservationId")
            });
        }
        
        // 4. NEW: Expiring soon WITHOUT utilization data - needs review
        var expiringNoUtil = active.Where(r => 
            GetInt(r, "DaysToExpiry") >= 0 && GetInt(r, "DaysToExpiry") <= 90 && 
            !HasUtilizationData(r)).ToList();
        foreach (var res in expiringNoUtil)
        {
            if (processedIds.Contains(GetStr(res, "ReservationId"))) continue;
            processedIds.Add(GetStr(res, "ReservationId"));
            var autoRenew = res.TryGetValue("Renew", out var r) && r is bool b && b;
            var daysLeft = GetInt(res, "DaysToExpiry");
            insights.Add(new Dictionary<string, object>
            {
                ["Priority"] = daysLeft <= 30 ? "High" : "Medium",
                ["Type"] = "ExpiringNoUtilization",
                ["Icon"] = "\u26A0\uFE0F", // ⚠️
                ["Title"] = $"{GetStr(res, "DisplayName")} expires in {daysLeft} days - utilization unknown",
                ["Description"] = $"This reservation is expiring soon but utilization data is not available. {(autoRenew ? "Auto-renew is ON." : "Auto-renew is OFF.")}",
                ["Recommendation"] = "Review actual usage in Azure Portal before deciding whether to renew. Check Cost Analysis to see if this reservation is being used.",
                ["Action"] = "Review in Azure Portal",
                ["ReservationId"] = GetStr(res, "ReservationId")
            });
        }
        
        // 5. MEDIUM: Low utilization (not expiring) - consider PAYG
        var lowUtil = active.Where(r => 
            HasUtilizationData(r) && 
            GetDouble(r, "Utilization30Day") > 0 && GetDouble(r, "Utilization30Day") < 80 &&
            !processedIds.Contains(GetStr(r, "ReservationId"))).ToList();
        foreach (var res in lowUtil)
        {
            processedIds.Add(GetStr(res, "ReservationId"));
            var util = GetDouble(res, "Utilization30Day");
            // RI savings are typically 30-40% vs PAYG. If util < 70%, PAYG might be cheaper
            var breakeven = 65; // Rough breakeven point
            var shouldSwitch = util < breakeven;
            
            insights.Add(new Dictionary<string, object>
            {
                ["Priority"] = shouldSwitch ? "High" : "Medium",
                ["Type"] = "LowUtilization",
                ["Icon"] = "\u26A0\uFE0F", // ⚠️
                ["Title"] = $"{GetStr(res, "DisplayName")} only {util:F0}% utilized",
                ["Description"] = $"At {util:F0}% utilization, you're wasting ~{100-util:F0}% of this reservation's value. " +
                    (shouldSwitch ? "PAYG would likely be cheaper." : "Consider right-sizing or consolidating workloads."),
                ["Recommendation"] = shouldSwitch 
                    ? "Consider exchanging for smaller reservation or switching to PAYG for this workload."
                    : "Monitor utilization. Consider exchanging for a different SKU if workload has changed.",
                ["Action"] = shouldSwitch ? "Exchange to PAYG" : "Monitor",
                ["WastePercent"] = Math.Round(100 - util, 0),
                ["ReservationId"] = GetStr(res, "ReservationId")
            });
        }
        
        // 6. Purchase recommendations
        foreach (var rec in purchaseRecs.OrderByDescending(r => GetDouble(r, "AnnualSavings")).Take(5))
        {
            var savings = GetDouble(rec, "AnnualSavings");
            if (savings > 100) // Only show if meaningful savings
            {
                insights.Add(new Dictionary<string, object>
                {
                    ["Priority"] = savings > 1000 ? "High" : "Medium",
                    ["Type"] = "PurchaseRecommendation",
                    ["Icon"] = "\U0001F4B0", // 💰
                    ["Title"] = $"Buy {GetStr(rec, "SkuName")} reservation - save \u00A3{savings:N0}/year",
                    ["Description"] = $"Azure recommends purchasing {GetDouble(rec, "Quantity")} x {GetStr(rec, "SkuName")} ({GetStr(rec, "Term")}) " +
                        $"based on your usage. This would save {GetDouble(rec, "SavingsPercent")}% vs PAYG.",
                    ["Recommendation"] = "Review workload stability before purchasing. 1-year term recommended if uncertain about long-term need.",
                    ["Action"] = "Purchase RI",
                    ["AnnualSavings"] = savings
                });
            }
        }
        
        // 7. INFO: Healthy reservations summary
        var healthy = active.Where(r => HasUtilizationData(r) && GetDouble(r, "Utilization30Day") >= 80 && GetInt(r, "DaysToExpiry") > 90).ToList();
        if (healthy.Any())
        {
            insights.Add(new Dictionary<string, object>
            {
                ["Priority"] = "Info",
                ["Type"] = "HealthySummary",
                ["Icon"] = "\U0001F7E2", // 🟢
                ["Title"] = $"{healthy.Count} reservation(s) are healthy",
                ["Description"] = "These reservations have good utilization (>80%) and aren't expiring soon.",
                ["Recommendation"] = "No action needed. Continue monitoring utilization.",
                ["Action"] = "None"
            });
        }
        
        // 8. INFO: If no utilization data at all, warn about it
        var noUtilCount = active.Count(r => !HasUtilizationData(r));
        if (noUtilCount > 0 && noUtilCount == active.Count)
        {
            insights.Insert(0, new Dictionary<string, object>
            {
                ["Priority"] = "Medium",
                ["Type"] = "NoUtilizationData",
                ["Icon"] = "\u2139\uFE0F", // ℹ️
                ["Title"] = "Utilization data not available",
                ["Description"] = $"Could not retrieve utilization data for {noUtilCount} reservation(s). The Service Principal may need additional permissions or the data may take 24-48 hours to populate for new reservations.",
                ["Recommendation"] = "Check that the Service Principal has 'Reservations Reader' role at tenant root or billing account level. Utilization data updates daily.",
                ["Action"] = "Check Permissions"
            });
        }
        
        return insights.OrderBy(i => i["Priority"] switch { "Critical" => 0, "High" => 1, "Medium" => 2, _ => 3 }).ToList();
    }

    private bool IsActiveReservation(Dictionary<string, object> res)
    {
        var status = GetStr(res, "Status");
        return status == "Succeeded" || status == "Expiring";
    }

    private string GetStr(Dictionary<string, object> d, string key) => d.TryGetValue(key, out var v) ? v?.ToString() ?? "" : "";
    
    private double GetDouble(Dictionary<string, object> d, string key)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return 0;
        return v switch
        {
            double dv => dv,
            int iv => iv,
            long lv => lv,
            float fv => fv,
            decimal dec => (double)dec,
            _ => double.TryParse(v.ToString(), out var parsed) ? parsed : 0
        };
    }
    
    private int GetInt(Dictionary<string, object> d, string key, int def = 0)
    {
        if (!d.TryGetValue(key, out var v) || v == null) return def;
        return v switch
        {
            int iv => iv,
            long lv => (int)lv,
            double dv => (int)dv,
            float fv => (int)fv,
            _ => int.TryParse(v.ToString(), out var parsed) ? parsed : def
        };
    }
    
    private bool HasUtilizationData(Dictionary<string, object> d)
    {
        return d.ContainsKey("Utilization30Day") || d.ContainsKey("Utilization7Day") || d.ContainsKey("Utilization1Day");
    }
}

public class SaveSasRequest
{
    public string SasToken { get; set; } = string.Empty;
}
