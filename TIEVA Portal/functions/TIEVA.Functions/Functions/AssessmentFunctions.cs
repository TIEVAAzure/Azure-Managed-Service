using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TIEVA.Functions.Models;
using TIEVA.Functions.Services;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Azure.Identity;
using OfficeOpenXml;

namespace TIEVA.Functions.Functions;

public class AssessmentFunctions
{
    private readonly ILogger _logger;
    private readonly TievaDbContext _db;

    public AssessmentFunctions(ILoggerFactory loggerFactory, TievaDbContext db)
    {
        _logger = loggerFactory.CreateLogger<AssessmentFunctions>();
        _db = db;
    }

    [Function("GetAssessments")]
    public async Task<HttpResponseData> GetAssessments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assessments")] HttpRequestData req)
    {
        var assessments = await _db.Assessments
            .OrderByDescending(a => a.CreatedAt)
            .Take(50)
            .Select(a => new
            {
                a.Id,
                a.CustomerId,
                CustomerName = a.Customer!.Name,
                a.ConnectionId,
                a.Status,
                a.StartedAt,
                a.CompletedAt,
                a.ScoreOverall,
                a.FindingsTotal,
                a.FindingsHigh,
                a.FindingsMedium,
                a.FindingsLow,
                a.CreatedAt,
                ModuleResults = a.ModuleResults.Select(mr => new
                {
                    mr.Id,
                    mr.ModuleCode,
                    mr.Status,
                    mr.BlobPath,
                    mr.FindingsCount,
                    mr.StartedAt,
                    mr.CompletedAt
                }).ToList()
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(assessments);
        return response;
    }

    [Function("GetAssessment")]
    public async Task<HttpResponseData> GetAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assessments/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var assessmentId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        var assessment = await _db.Assessments
            .Include(a => a.Customer)
            .Include(a => a.ModuleResults)
            .Include(a => a.Findings)
            .FirstOrDefaultAsync(a => a.Id == assessmentId);

        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        // Get ONLY findings from THIS assessment
        var findings = assessment.Findings.ToList();
        var moduleResults = assessment.ModuleResults.ToList();

        // Calculate stats from THIS assessment only
        var totalHigh = findings.Count(f => f.Severity.ToLower() == "high");
        var totalMedium = findings.Count(f => f.Severity.ToLower() == "medium");
        var totalLow = findings.Count(f => f.Severity.ToLower() == "low");
        var totalFindings = totalHigh + totalMedium + totalLow;

        // Calculate score from THIS assessment's findings
        var weightedFindings = (totalHigh * 3.0) + (totalMedium * 1.5) + (totalLow * 0.5);
        var calculatedScore = totalFindings > 0 ? (decimal)Math.Round(100.0 / (1.0 + (weightedFindings / 20.0)), 0) : 100m;

        // Use stored score if available, otherwise calculated
        var scoreToUse = assessment.ScoreOverall ?? calculatedScore;

        var result = new
        {
            assessment.Id,
            assessment.CustomerId,
            CustomerName = assessment.Customer?.Name,
            assessment.ConnectionId,
            assessment.Status,
            assessment.StartedAt,
            assessment.CompletedAt,
            ScoreOverall = scoreToUse,
            FindingsTotal = totalFindings,
            FindingsHigh = totalHigh,
            FindingsMedium = totalMedium,
            FindingsLow = totalLow,
            assessment.CreatedAt,
            ModulesAnalyzed = moduleResults.Count,
            ModuleResults = moduleResults.Select(mr => new
            {
                mr.Id,
                mr.ModuleCode,
                mr.Status,
                mr.BlobPath,
                mr.FindingsCount,
                mr.Score,
                mr.StartedAt,
                mr.CompletedAt,
                mr.AssessmentId
            }).ToList(),
            Findings = findings.Select(f => new
            {
                f.Id,
                f.ModuleCode,
                f.Category,
                f.Severity,
                f.FindingText,
                f.Recommendation,
                f.ResourceType,
                f.ResourceName,
                f.ResourceId,
                f.SubscriptionId,
                f.Status,
                f.ChangeStatus,
                f.FirstSeenAt,
                f.LastSeenAt,
                f.OccurrenceCount
            }).ToList()
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(result);
        return response;
    }

    [Function("CreateAssessment")]
    public async Task<HttpResponseData> CreateAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "assessments")] HttpRequestData req)
    {
        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<CreateAssessmentRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input == null || input.ConnectionId == Guid.Empty)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("ConnectionId is required");
            return badRequest;
        }

        // Get connection to find customer
        var connection = await _db.AzureConnections.FindAsync(input.ConnectionId);
        if (connection == null)
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Connection not found");
            return badRequest;
        }

        var assessment = new Assessment
        {
            Id = input.AssessmentId != Guid.Empty ? input.AssessmentId : Guid.NewGuid(),
            CustomerId = connection.CustomerId,
            ConnectionId = input.ConnectionId,
            Status = input.Status ?? "Running",
            StartedAt = input.StartedAt ?? DateTime.UtcNow,
            CompletedAt = input.CompletedAt,
            StartedBy = input.StartedBy ?? "System",
            CreatedAt = DateTime.UtcNow
        };

        _db.Assessments.Add(assessment);
        await _db.SaveChangesAsync();

        _logger.LogInformation("Created assessment {Id}", assessment.Id);

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { assessment.Id });
        return response;
    }

    [Function("UpdateAssessment")]
    public async Task<HttpResponseData> UpdateAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "assessments/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var assessmentId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        var assessment = await _db.Assessments.FindAsync(assessmentId);
        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<UpdateAssessmentRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input != null)
        {
            if (input.Status != null) assessment.Status = input.Status;
            if (input.CompletedAt.HasValue) assessment.CompletedAt = input.CompletedAt;
            if (input.ScoreOverall.HasValue) assessment.ScoreOverall = input.ScoreOverall;
            if (input.FindingsTotal.HasValue) assessment.FindingsTotal = input.FindingsTotal.Value;
            if (input.FindingsHigh.HasValue) assessment.FindingsHigh = input.FindingsHigh.Value;
            if (input.FindingsMedium.HasValue) assessment.FindingsMedium = input.FindingsMedium.Value;
            if (input.FindingsLow.HasValue) assessment.FindingsLow = input.FindingsLow.Value;
        }

        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { assessment.Id, assessment.Status });
        return response;
    }

    [Function("AddModuleResult")]
    public async Task<HttpResponseData> AddModuleResult(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "assessments/{id}/modules")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var assessmentId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        var assessment = await _db.Assessments.FindAsync(assessmentId);
        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        var body = await new StreamReader(req.Body).ReadToEndAsync();
        var input = JsonSerializer.Deserialize<AddModuleResultRequest>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (input == null || string.IsNullOrEmpty(input.ModuleCode))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("ModuleCode is required");
            return badRequest;
        }

        var moduleResult = new AssessmentModuleResult
        {
            Id = Guid.NewGuid(),
            AssessmentId = assessmentId,
            ModuleCode = input.ModuleCode.ToUpper(),
            Status = input.Status ?? "Completed",
            BlobPath = input.BlobPath,
            FindingsCount = input.FindingsCount ?? 0,
            StartedAt = input.StartedAt,
            CompletedAt = input.CompletedAt ?? DateTime.UtcNow
        };

        _db.AssessmentModuleResults.Add(moduleResult);
        await _db.SaveChangesAsync();

        var response = req.CreateResponse(HttpStatusCode.Created);
        await response.WriteAsJsonAsync(new { moduleResult.Id, moduleResult.ModuleCode });
        return response;
    }

    [Function("GetAssessmentsByConnection")]
    public async Task<HttpResponseData> GetAssessmentsByConnection(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "connections/{connectionId}/assessments")] HttpRequestData req,
        string connectionId)
    {
        if (!Guid.TryParse(connectionId, out var connId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid connection ID");
            return badRequest;
        }

        var assessments = await _db.Assessments
            .Where(a => a.ConnectionId == connId)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id,
                a.Status,
                a.StartedAt,
                a.CompletedAt,
                a.FindingsTotal,
                a.FindingsHigh,
                a.FindingsMedium,
                a.FindingsLow,
                a.CreatedAt,
                ModuleResults = a.ModuleResults.Select(mr => new
                {
                    mr.ModuleCode,
                    mr.Status,
                    mr.BlobPath,
                    mr.FindingsCount
                }).ToList()
            })
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(assessments);
        return response;
    }

    [Function("GetModuleResultDownload")]
    public async Task<HttpResponseData> GetModuleResultDownload(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assessments/{assessmentId}/modules/{moduleCode}/download")] HttpRequestData req,
        string assessmentId,
        string moduleCode)
    {
        if (!Guid.TryParse(assessmentId, out var assId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        // Find the module result - first try the specified assessment
        var moduleResult = await _db.AssessmentModuleResults
            .FirstOrDefaultAsync(r => r.AssessmentId == assId && r.ModuleCode == moduleCode.ToUpper());

        // If not found, look for the latest module result for this connection (consolidated view support)
        if (moduleResult == null)
        {
            var assessment = await _db.Assessments.FindAsync(assId);
            if (assessment?.ConnectionId != null)
            {
                moduleResult = await _db.AssessmentModuleResults
                    .Where(r => r.ModuleCode == moduleCode.ToUpper())
                    .Where(r => _db.Assessments
                        .Where(a => a.ConnectionId == assessment.ConnectionId)
                        .Select(a => a.Id)
                        .Contains(r.AssessmentId))
                    .OrderByDescending(r => r.CompletedAt)
                    .FirstOrDefaultAsync();
            }
        }

        if (moduleResult == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Module result not found");
            return notFound;
        }

        if (string.IsNullOrEmpty(moduleResult.BlobPath))
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("No file available for download");
            return notFound;
        }

        try
        {
            var storageAccountName = Environment.GetEnvironmentVariable("AuditStorageAccount") ?? "sttievaaudit";
            var containerName = "audit-results";

            // Create blob service client using managed identity
            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(moduleResult.BlobPath);

            // Check blob exists
            if (!await blobClient.ExistsAsync())
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("File not found in storage");
                return notFound;
            }

            // Create user delegation key (required for SAS with managed identity)
            var userDelegationKey = await blobServiceClient.GetUserDelegationKeyAsync(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1));

            // Build SAS token
            var sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = containerName,
                BlobName = moduleResult.BlobPath,
                Resource = "b",
                StartsOn = DateTimeOffset.UtcNow.AddMinutes(-5),
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
            };
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            var sasToken = sasBuilder.ToSasQueryParameters(userDelegationKey, storageAccountName).ToString();
            var downloadUrl = $"{blobClient.Uri}?{sasToken}";

            _logger.LogInformation("Generated download URL for {BlobPath}", moduleResult.BlobPath);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                downloadUrl,
                fileName = Path.GetFileName(moduleResult.BlobPath),
                expiresAt = DateTimeOffset.UtcNow.AddHours(1)
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating download URL for {BlobPath}", moduleResult.BlobPath);
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync($"Error generating download URL: {ex.Message}");
            return error;
        }
    }

    [Function("ParseModuleFindings")]
    public async Task<HttpResponseData> ParseModuleFindings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "assessments/{assessmentId}/modules/{moduleCode}/parse")] HttpRequestData req,
        string assessmentId,
        string moduleCode)
    {
        if (!Guid.TryParse(assessmentId, out var assId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        // Find the module result - first try the specified assessment
        var moduleResult = await _db.AssessmentModuleResults
            .FirstOrDefaultAsync(r => r.AssessmentId == assId && r.ModuleCode == moduleCode.ToUpper());

        // If not found, look for the latest module result for this connection (consolidated view support)
        if (moduleResult == null)
        {
            var tempAssessment = await _db.Assessments.FindAsync(assId);
            if (tempAssessment?.ConnectionId != null)
            {
                moduleResult = await _db.AssessmentModuleResults
                    .Where(r => r.ModuleCode == moduleCode.ToUpper())
                    .Where(r => _db.Assessments
                        .Where(a => a.ConnectionId == tempAssessment.ConnectionId)
                        .Select(a => a.Id)
                        .Contains(r.AssessmentId))
                    .OrderByDescending(r => r.CompletedAt)
                    .FirstOrDefaultAsync();
                
                // Update assId to the actual assessment where this module ran
                if (moduleResult != null)
                {
                    assId = moduleResult.AssessmentId;
                }
            }
        }

        if (moduleResult == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Module result not found");
            return notFound;
        }

        if (string.IsNullOrEmpty(moduleResult.BlobPath))
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("No file available to parse");
            return notFound;
        }

        // Get assessment to find customer
        var assessment = await _db.Assessments.FindAsync(assId);
        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        try
        {
            var storageAccountName = Environment.GetEnvironmentVariable("AuditStorageAccount") ?? "sttievaaudit";
            var containerName = "audit-results";

            var blobServiceClient = new BlobServiceClient(
                new Uri($"https://{storageAccountName}.blob.core.windows.net"),
                new DefaultAzureCredential());

            var containerClient = blobServiceClient.GetBlobContainerClient(containerName);
            var blobClient = containerClient.GetBlobClient(moduleResult.BlobPath);

            // Download to memory stream
            using var memoryStream = new MemoryStream();
            await blobClient.DownloadToAsync(memoryStream);
            memoryStream.Position = 0;

            // Set EPPlus license context (required for EPPlus 5+)
            ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

            // Parse Excel
            using var package = new ExcelPackage(memoryStream);
            var findingsSheet = package.Workbook.Worksheets["Findings"];

            if (findingsSheet == null)
            {
                var notFound = req.CreateResponse(HttpStatusCode.NotFound);
                await notFound.WriteStringAsync("No 'Findings' sheet in Excel file");
                return notFound;
            }

            // Remove existing findings for this module in this assessment
            var existingFindings = await _db.Findings
                .Where(f => f.AssessmentId == assId && f.ModuleCode == moduleCode.ToUpper())
                .ToListAsync();
            _db.Findings.RemoveRange(existingFindings);

            // Get existing CustomerFindings for this customer and module (for tracking new vs recurring)
            var customerFindings = await _db.CustomerFindings
                .Where(cf => cf.CustomerId == assessment.CustomerId && cf.ModuleCode == moduleCode.ToUpper())
                .ToListAsync();
            var customerFindingsByHash = customerFindings.ToDictionary(cf => cf.Hash, cf => cf);

            // Track which CustomerFindings we see in this assessment
            var seenHashes = new HashSet<string>();
            
            // Track new CustomerFindings created in this parse (to avoid duplicates within same Excel)
            var newCustomerFindingsThisParse = new Dictionary<string, CustomerFinding>();

            // Parse findings from Excel
            var newFindings = new List<Finding>();
            int highCount = 0, mediumCount = 0, lowCount = 0;
            int newCount = 0, recurringCount = 0;

            // Get header row to find column indices
            var headers = new Dictionary<string, int>();
            for (int col = 1; col <= findingsSheet.Dimension?.End.Column; col++)
            {
                var header = findingsSheet.Cells[1, col].Text?.Trim();
                if (!string.IsNullOrEmpty(header))
                    headers[header] = col;
            }

            // Read data rows
            for (int row = 2; row <= findingsSheet.Dimension?.End.Row; row++)
            {
                var severity = GetCellValue(findingsSheet, row, headers, "Severity");
                if (string.IsNullOrEmpty(severity)) continue;
                
                // Skip Info severity findings (these are confirmations, not issues)
                if (severity.Equals("Info", StringComparison.OrdinalIgnoreCase)) continue;

                var resourceId = GetCellValue(findingsSheet, row, headers, "ResourceId") ?? "";
                var resourceName = GetCellValue(findingsSheet, row, headers, "ResourceName") ?? "";
                var findingText = GetCellValue(findingsSheet, row, headers, "Detail") ?? "";
                var category = GetCellValue(findingsSheet, row, headers, "Category");

                // Generate hash for matching across assessments
                var hash = GenerateFindingHash(resourceId, resourceName, findingText, category);
                seenHashes.Add(hash);

                // Check if this finding existed before (in DB or already created this parse)
                var existsInDb = customerFindingsByHash.ContainsKey(hash);
                var existsInThisParse = newCustomerFindingsThisParse.ContainsKey(hash);
                var isRecurring = existsInDb || existsInThisParse;
                var changeStatus = isRecurring ? "Recurring" : "New";

                var finding = new Finding
                {
                    Id = Guid.NewGuid(),
                    AssessmentId = assId,
                    ModuleCode = moduleCode.ToUpper(),
                    SubscriptionId = GetCellValue(findingsSheet, row, headers, "SubscriptionId"),
                    Severity = severity,
                    Category = category,
                    ResourceType = GetCellValue(findingsSheet, row, headers, "ResourceType"),
                    ResourceName = resourceName,
                    ResourceId = resourceId,
                    FindingText = findingText,
                    Recommendation = GetCellValue(findingsSheet, row, headers, "Recommendation"),
                    Status = "Open",
                    ChangeStatus = changeStatus,
                    Hash = hash,
                    FirstSeenAt = existsInDb ? customerFindingsByHash[hash].FirstSeenAt : DateTime.UtcNow,
                    LastSeenAt = DateTime.UtcNow,
                    OccurrenceCount = existsInDb ? customerFindingsByHash[hash].OccurrenceCount + 1 : 1
                };

                newFindings.Add(finding);

                // Update or create CustomerFinding (only one per unique hash)
                if (existsInDb)
                {
                    var cf = customerFindingsByHash[hash];
                    cf.LastSeenAt = DateTime.UtcNow;
                    cf.OccurrenceCount++;
                    cf.LastAssessmentId = assId;
                    cf.Status = "Open";
                    cf.ResolvedAt = null;
                    recurringCount++;
                }
                else if (existsInThisParse)
                {
                    // Already created CustomerFinding for this hash in this parse, just update it
                    var cf = newCustomerFindingsThisParse[hash];
                    cf.OccurrenceCount++;
                    recurringCount++;
                }
                else
                {
                    // New unique finding - create CustomerFinding
                    var newCf = new CustomerFinding
                    {
                        Id = Guid.NewGuid(),
                        CustomerId = assessment.CustomerId,
                        ModuleCode = moduleCode.ToUpper(),
                        Hash = hash,
                        Severity = severity,
                        Category = category,
                        ResourceType = GetCellValue(findingsSheet, row, headers, "ResourceType"),
                        ResourceId = resourceId,
                        FindingText = findingText,
                        Recommendation = GetCellValue(findingsSheet, row, headers, "Recommendation"),
                        Status = "Open",
                        FirstSeenAt = DateTime.UtcNow,
                        LastSeenAt = DateTime.UtcNow,
                        OccurrenceCount = 1,
                        LastAssessmentId = assId
                    };
                    _db.CustomerFindings.Add(newCf);
                    newCustomerFindingsThisParse[hash] = newCf; // Track to avoid duplicates
                    newCount++;
                }

                switch (severity.ToLower())
                {
                    case "high": highCount++; break;
                    case "medium": mediumCount++; break;
                    case "low": lowCount++; break;
                }
            }

            // Mark CustomerFindings NOT seen in this assessment as Resolved
            int resolvedCount = 0;
            foreach (var cf in customerFindings.Where(cf => !seenHashes.Contains(cf.Hash) && cf.Status == "Open"))
            {
                cf.Status = "Resolved";
                cf.ResolvedAt = DateTime.UtcNow;
                resolvedCount++;
            }

            // Save findings
            _db.Findings.AddRange(newFindings);

            // Update module result counts
            moduleResult.FindingsCount = newFindings.Count;
            moduleResult.Score = CalculateModuleScore(newFindings);

            // Update assessment totals
            // Recalculate totals from all module findings
            var allFindings = await _db.Findings.Where(f => f.AssessmentId == assId).ToListAsync();
            allFindings.AddRange(newFindings); // Include new ones not yet saved
            allFindings = allFindings.Where(f => !existingFindings.Any(e => e.Id == f.Id)).ToList(); // Exclude removed

            assessment.FindingsHigh = allFindings.Count(f => f.Severity.ToLower() == "high");
            assessment.FindingsMedium = allFindings.Count(f => f.Severity.ToLower() == "medium");
            assessment.FindingsLow = allFindings.Count(f => f.Severity.ToLower() == "low");
            assessment.FindingsTotal = assessment.FindingsHigh + assessment.FindingsMedium + assessment.FindingsLow;

            // Calculate overall score
            var weightedFindings = (assessment.FindingsHigh * 3.0) + (assessment.FindingsMedium * 1.5) + (assessment.FindingsLow * 0.5);
            var score = 100.0 / (1.0 + (weightedFindings / 20.0));
            assessment.ScoreOverall = (decimal)Math.Round(score, 0);

            await _db.SaveChangesAsync();

            _logger.LogInformation("Parsed {Count} findings from {BlobPath}: {New} new, {Recurring} recurring, {Resolved} resolved",
                newFindings.Count, moduleResult.BlobPath, newCount, recurringCount, resolvedCount);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                parsed = newFindings.Count,
                high = highCount,
                medium = mediumCount,
                low = lowCount,
                newFindings = newCount,
                recurring = recurringCount,
                resolved = resolvedCount,
                score = assessment.ScoreOverall,
                moduleScore = moduleResult.Score
            });
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error parsing findings from {BlobPath}", moduleResult.BlobPath);
            var error = req.CreateResponse(HttpStatusCode.InternalServerError);
            await error.WriteStringAsync($"Error parsing findings: {ex.Message}");
            return error;
        }
    }

    private static string GenerateFindingHash(string resourceId, string resourceName, string findingText, string? category)
    {
        // Create a stable hash from the key identifying fields
        var input = $"{resourceId}|{resourceName}|{findingText}|{category ?? ""}".ToLowerInvariant();
        using var sha = System.Security.Cryptography.SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
        return Convert.ToBase64String(bytes)[..16]; // Short hash for readability
    }

    private static decimal CalculateModuleScore(List<Finding> findings)
    {
        if (findings.Count == 0) return 100;
        var high = findings.Count(f => f.Severity.ToLower() == "high");
        var med = findings.Count(f => f.Severity.ToLower() == "medium");
        var low = findings.Count(f => f.Severity.ToLower() == "low");
        var weighted = (high * 3.0) + (med * 1.5) + (low * 0.5);
        var score = 100.0 / (1.0 + (weighted / 10.0));
        return (decimal)Math.Round(score, 0);
    }

    private static string? GetCellValue(ExcelWorksheet sheet, int row, Dictionary<string, int> headers, string columnName)
    {
        if (headers.TryGetValue(columnName, out int col))
        {
            return sheet.Cells[row, col].Text?.Trim();
        }
        return null;
    }

    [Function("GetResolvedFindings")]
    public async Task<HttpResponseData> GetResolvedFindings(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assessments/{assessmentId}/resolved")] HttpRequestData req,
        string assessmentId)
    {
        if (!Guid.TryParse(assessmentId, out var assId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        // Get the assessment to find the customer
        var assessment = await _db.Assessments
            .Include(a => a.ModuleResults)
            .FirstOrDefaultAsync(a => a.Id == assId);
        
        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        // Get modules that were actually run in this assessment
        var modulesRun = assessment.ModuleResults
            .Where(mr => mr.Status == "Completed")
            .Select(mr => mr.ModuleCode)
            .ToHashSet();

        if (modulesRun.Count == 0)
        {
            // No modules completed yet, return empty
            var emptyResponse = req.CreateResponse(HttpStatusCode.OK);
            await emptyResponse.WriteAsJsonAsync(new List<object>());
            return emptyResponse;
        }

        // Get CustomerFindings that were resolved around or before this assessment
        // ONLY for modules that were actually run in this assessment
        var resolved = await _db.CustomerFindings
            .Where(cf => cf.CustomerId == assessment.CustomerId 
                && cf.Status == "Resolved"
                && cf.ResolvedAt != null
                && modulesRun.Contains(cf.ModuleCode))
            .OrderByDescending(cf => cf.ResolvedAt)
            .Select(cf => new
            {
                cf.Id,
                cf.ModuleCode,
                cf.Category,
                cf.Severity,
                cf.FindingText,
                cf.Recommendation,
                cf.ResourceType,
                cf.ResourceId,
                cf.FirstSeenAt,
                cf.LastSeenAt,
                cf.ResolvedAt,
                cf.OccurrenceCount
            })
            .Take(100)
            .ToListAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(resolved);
        return response;
    }

    [Function("GetAssessmentChangeSummary")]
    public async Task<HttpResponseData> GetAssessmentChangeSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "assessments/{assessmentId}/changes")] HttpRequestData req,
        string assessmentId)
    {
        if (!Guid.TryParse(assessmentId, out var assId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        // Get findings for this assessment
        var findings = await _db.Findings
            .Where(f => f.AssessmentId == assId)
            .ToListAsync();

        // Get the assessment with module results
        var assessment = await _db.Assessments
            .Include(a => a.Customer)
            .Include(a => a.ModuleResults)
            .FirstOrDefaultAsync(a => a.Id == assId);

        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        // Get modules that were actually completed in this assessment
        var modulesRun = assessment.ModuleResults
            .Where(mr => mr.Status == "Completed")
            .Select(mr => mr.ModuleCode)
            .ToHashSet();

        // Get recently resolved findings for this customer - ONLY for modules that were run
        var recentlyResolved = 0;
        if (modulesRun.Count > 0 && assessment.StartedAt.HasValue)
        {
            recentlyResolved = await _db.CustomerFindings
                .Where(cf => cf.CustomerId == assessment.CustomerId
                    && cf.Status == "Resolved"
                    && cf.ResolvedAt >= assessment.StartedAt.Value.AddDays(-1)
                    && modulesRun.Contains(cf.ModuleCode))
                .CountAsync();
        }

        var summary = new
        {
            assessmentId = assId,
            customerName = assessment.Customer?.Name,
            assessmentDate = assessment.StartedAt,
            modulesAnalyzed = modulesRun.Count,
            totalFindings = findings.Count,
            newFindings = findings.Count(f => f.ChangeStatus == "New"),
            recurringFindings = findings.Count(f => f.ChangeStatus == "Recurring"),
            resolvedFindings = recentlyResolved,
            byModule = findings
                .GroupBy(f => f.ModuleCode)
                .Select(g => new
                {
                    module = g.Key,
                    total = g.Count(),
                    newCount = g.Count(f => f.ChangeStatus == "New"),
                    recurringCount = g.Count(f => f.ChangeStatus == "Recurring")
                })
                .ToList(),
            bySeverity = new
            {
                high = new
                {
                    total = findings.Count(f => f.Severity.ToLower() == "high"),
                    newCount = findings.Count(f => f.Severity.ToLower() == "high" && f.ChangeStatus == "New"),
                    recurring = findings.Count(f => f.Severity.ToLower() == "high" && f.ChangeStatus == "Recurring")
                },
                medium = new
                {
                    total = findings.Count(f => f.Severity.ToLower() == "medium"),
                    newCount = findings.Count(f => f.Severity.ToLower() == "medium" && f.ChangeStatus == "New"),
                    recurring = findings.Count(f => f.Severity.ToLower() == "medium" && f.ChangeStatus == "Recurring")
                },
                low = new
                {
                    total = findings.Count(f => f.Severity.ToLower() == "low"),
                    newCount = findings.Count(f => f.Severity.ToLower() == "low" && f.ChangeStatus == "New"),
                    recurring = findings.Count(f => f.Severity.ToLower() == "low" && f.ChangeStatus == "Recurring")
                }
            }
        };

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(summary);
        return response;
    }

    [Function("DeleteAssessment")]
    public async Task<HttpResponseData> DeleteAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "assessments/{id}")] HttpRequestData req,
        string id)
    {
        if (!Guid.TryParse(id, out var assessmentId))
        {
            var badRequest = req.CreateResponse(HttpStatusCode.BadRequest);
            await badRequest.WriteStringAsync("Invalid assessment ID");
            return badRequest;
        }

        var assessment = await _db.Assessments
            .Include(a => a.ModuleResults)
            .Include(a => a.Findings)
            .FirstOrDefaultAsync(a => a.Id == assessmentId);

        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        // Cascade delete: Remove all findings
        _db.Findings.RemoveRange(assessment.Findings);

        // Cascade delete: Remove all module results
        _db.AssessmentModuleResults.RemoveRange(assessment.ModuleResults);

        // Delete the assessment
        _db.Assessments.Remove(assessment);

        await _db.SaveChangesAsync();

        _logger.LogInformation("Deleted assessment {Id} with {FindingsCount} findings and {ModulesCount} module results",
            assessmentId, assessment.Findings.Count, assessment.ModuleResults.Count);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new
        {
            message = "Assessment deleted",
            findingsDeleted = assessment.Findings.Count,
            moduleResultsDeleted = assessment.ModuleResults.Count
        });
        return response;
    }
}

public class CreateAssessmentRequest
{
    public Guid AssessmentId { get; set; }
    public Guid ConnectionId { get; set; }
    public string? Status { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? StartedBy { get; set; }
}

public class UpdateAssessmentRequest
{
    public string? Status { get; set; }
    public DateTime? CompletedAt { get; set; }
    public decimal? ScoreOverall { get; set; }
    public int? FindingsTotal { get; set; }
    public int? FindingsHigh { get; set; }
    public int? FindingsMedium { get; set; }
    public int? FindingsLow { get; set; }
}

public class AddModuleResultRequest
{
    public string ModuleCode { get; set; } = string.Empty;
    public string? Status { get; set; }
    public string? BlobPath { get; set; }
    public int? FindingsCount { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}