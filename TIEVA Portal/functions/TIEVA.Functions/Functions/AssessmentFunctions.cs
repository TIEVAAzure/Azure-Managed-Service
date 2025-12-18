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
            .Where(a => a.Id == assessmentId)
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
                }).ToList(),
                Findings = a.Findings.Select(f => new
                {
                    f.Id,
                    f.ModuleCode,
                    f.Category,
                    f.Severity,
                    f.FindingText,
                    f.Recommendation,
                    f.ResourceType,
                    f.ResourceName,
                    f.SubscriptionId,
                    f.Status,
                    f.FirstSeenAt
                }).ToList()
            })
            .FirstOrDefaultAsync();

        if (assessment == null)
        {
            var notFound = req.CreateResponse(HttpStatusCode.NotFound);
            await notFound.WriteStringAsync("Assessment not found");
            return notFound;
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(assessment);
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

        // Find the module result
        var moduleResult = await _db.AssessmentModuleResults
            .FirstOrDefaultAsync(r => r.AssessmentId == assId && r.ModuleCode == moduleCode.ToUpper());

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

        // Find the module result
        var moduleResult = await _db.AssessmentModuleResults
            .FirstOrDefaultAsync(r => r.AssessmentId == assId && r.ModuleCode == moduleCode.ToUpper());

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

            // Remove existing findings for this module
            var existingFindings = await _db.Findings
                .Where(f => f.AssessmentId == assId && f.ModuleCode == moduleCode.ToUpper())
                .ToListAsync();
            _db.Findings.RemoveRange(existingFindings);

            // Parse findings from Excel
            var newFindings = new List<Finding>();
            int highCount = 0, mediumCount = 0, lowCount = 0;

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

                var finding = new Finding
                {
                    Id = Guid.NewGuid(),
                    AssessmentId = assId,
                    ModuleCode = moduleCode.ToUpper(),
                    SubscriptionId = GetCellValue(findingsSheet, row, headers, "SubscriptionId"),
                    Severity = severity,
                    Category = GetCellValue(findingsSheet, row, headers, "Category"),
                    ResourceType = GetCellValue(findingsSheet, row, headers, "ResourceType"),
                    ResourceName = GetCellValue(findingsSheet, row, headers, "ResourceName"),
                    FindingText = GetCellValue(findingsSheet, row, headers, "Detail") ?? "",
                    Recommendation = GetCellValue(findingsSheet, row, headers, "Recommendation"),
                    Status = "Open",
                    FirstSeenAt = DateTime.UtcNow
                };

                newFindings.Add(finding);

                switch (severity.ToLower())
                {
                    case "high": highCount++; break;
                    case "medium": mediumCount++; break;
                    case "low": lowCount++; break;
                }
            }

            // Save findings
            _db.Findings.AddRange(newFindings);

            // Update module result counts
            moduleResult.FindingsCount = newFindings.Count;

            // Update assessment totals
            var assessment = await _db.Assessments.FindAsync(assId);
            if (assessment != null)
            {
                // Recalculate totals from all module findings
                var allFindings = await _db.Findings.Where(f => f.AssessmentId == assId).ToListAsync();
                allFindings.AddRange(newFindings); // Include new ones not yet saved
                allFindings = allFindings.Where(f => !existingFindings.Any(e => e.Id == f.Id)).ToList(); // Exclude removed

                assessment.FindingsHigh = allFindings.Count(f => f.Severity.ToLower() == "high");
                assessment.FindingsMedium = allFindings.Count(f => f.Severity.ToLower() == "medium");
                assessment.FindingsLow = allFindings.Count(f => f.Severity.ToLower() == "low");
                assessment.FindingsTotal = assessment.FindingsHigh + assessment.FindingsMedium + assessment.FindingsLow;

                // Calculate score using a more balanced formula
                // Diminishing returns: score decreases but never quite hits 0
                // weightedFindings gives relative severity weight
                var weightedFindings = (assessment.FindingsHigh * 3.0) + (assessment.FindingsMedium * 1.5) + (assessment.FindingsLow * 0.5);
                // Divisor of 20 means ~50% score at 20 weighted findings
                var score = 100.0 / (1.0 + (weightedFindings / 20.0));
                assessment.ScoreOverall = (decimal)Math.Round(score, 0);
            }

            await _db.SaveChangesAsync();

            _logger.LogInformation("Parsed {Count} findings from {BlobPath}", newFindings.Count, moduleResult.BlobPath);

            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(new
            {
                parsed = newFindings.Count,
                high = highCount,
                medium = mediumCount,
                low = lowCount,
                score = assessment?.ScoreOverall
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

    private static string? GetCellValue(ExcelWorksheet sheet, int row, Dictionary<string, int> headers, string columnName)
    {
        if (headers.TryGetValue(columnName, out int col))
        {
            return sheet.Cells[row, col].Text?.Trim();
        }
        return null;
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