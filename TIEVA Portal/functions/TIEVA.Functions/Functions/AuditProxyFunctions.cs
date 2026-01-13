using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace TIEVA.Functions.Functions;

/// <summary>
/// Proxy functions that forward requests to the PowerShell TIEVA.Audit function app.
/// This allows all API calls to go through the main function app (and SWA), 
/// keeping the audit function app secured and not directly accessible.
/// </summary>
public class AuditProxyFunctions
{
    private readonly ILogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _auditApiUrl;
    private readonly string _auditApiKey;

    public AuditProxyFunctions(ILoggerFactory loggerFactory, IHttpClientFactory httpClientFactory)
    {
        _logger = loggerFactory.CreateLogger<AuditProxyFunctions>();
        _httpClientFactory = httpClientFactory;
        
        // Get audit API configuration from environment variables
        _auditApiUrl = Environment.GetEnvironmentVariable("AUDIT_API_URL") 
            ?? "https://func-tieva-audit.azurewebsites.net/api";
        _auditApiKey = Environment.GetEnvironmentVariable("AUDIT_API_KEY") ?? "";
    }

    /// <summary>
    /// Proxy for starting an assessment - forwards to PowerShell function app
    /// </summary>
    [Function("StartAssessmentProxy")]
    public async Task<HttpResponseData> StartAssessment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "assessments/start")] HttpRequestData req)
    {
        _logger.LogInformation("Proxying StartAssessment request to audit function app");

        try
        {
            var result = await ProxyRequest(req, "assessments/start");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying StartAssessment request");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to start assessment", details = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Proxy for FinOps setup - forwards to PowerShell function app
    /// </summary>
    [Function("SetupFinOpsProxy")]
    public async Task<HttpResponseData> SetupFinOps(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "finops/setup")] HttpRequestData req)
    {
        _logger.LogInformation("Proxying SetupFinOps request to audit function app");

        try
        {
            var result = await ProxyRequest(req, "finops/setup");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error proxying SetupFinOps request");
            var errorResponse = req.CreateResponse(HttpStatusCode.InternalServerError);
            await errorResponse.WriteAsJsonAsync(new { error = "Failed to setup FinOps", details = ex.Message });
            return errorResponse;
        }
    }

    /// <summary>
    /// Generic proxy method that forwards requests to the audit function app
    /// </summary>
    private async Task<HttpResponseData> ProxyRequest(HttpRequestData req, string route)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(6); // FinOps setup can take 3-5 minutes
        
        // Validate configuration
        if (string.IsNullOrEmpty(_auditApiKey))
        {
            _logger.LogError("AUDIT_API_KEY environment variable is not configured");
            var configError = req.CreateResponse(HttpStatusCode.InternalServerError);
            await configError.WriteAsJsonAsync(new { error = "Audit API key not configured. Please set AUDIT_API_KEY environment variable." });
            return configError;
        }
        
        // Build the target URL with function key
        var targetUrl = $"{_auditApiUrl}/{route}?code={_auditApiKey}";

        _logger.LogInformation("Forwarding request to: {Url}", targetUrl.Split('?')[0]); // Don't log the key

        // Read the request body
        var requestBody = await req.ReadAsStringAsync();

        // Create the proxied request
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl);
        
        if (!string.IsNullOrEmpty(requestBody))
        {
            proxyRequest.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
        }

        // Make the request
        HttpResponseMessage proxyResponse;
        try
        {
            proxyResponse = await client.SendAsync(proxyRequest);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Request to audit function app timed out after 6 minutes");
            var timeoutResponse = req.CreateResponse(HttpStatusCode.GatewayTimeout);
            await timeoutResponse.WriteAsJsonAsync(new { error = "Request timed out. The operation may still be running in the background." });
            return timeoutResponse;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to connect to audit function app at {Url}", _auditApiUrl);
            var connError = req.CreateResponse(HttpStatusCode.BadGateway);
            await connError.WriteAsJsonAsync(new { error = $"Failed to connect to audit service: {ex.Message}" });
            return connError;
        }
        
        // Read response content
        var responseContent = await proxyResponse.Content.ReadAsStringAsync();
        
        // Build response
        var response = req.CreateResponse(proxyResponse.StatusCode);
        
        // Ensure we always return valid JSON
        if (!string.IsNullOrEmpty(responseContent))
        {
            // Check if response is valid JSON
            try
            {
                System.Text.Json.JsonDocument.Parse(responseContent);
                // It's valid JSON, forward as-is
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync(responseContent);
            }
            catch (System.Text.Json.JsonException)
            {
                // Not valid JSON - wrap it in a JSON error object
                _logger.LogWarning("Audit function returned non-JSON response (status {Status}): {Content}", 
                    proxyResponse.StatusCode, responseContent.Length > 200 ? responseContent.Substring(0, 200) : responseContent);
                await response.WriteAsJsonAsync(new { 
                    error = "Unexpected response from audit service",
                    details = responseContent,
                    statusCode = (int)proxyResponse.StatusCode
                });
            }
        }
        else if (!proxyResponse.IsSuccessStatusCode)
        {
            // Empty response with error status
            await response.WriteAsJsonAsync(new { 
                error = $"Audit service returned {proxyResponse.StatusCode}",
                statusCode = (int)proxyResponse.StatusCode
            });
        }

        return response;
    }
}
