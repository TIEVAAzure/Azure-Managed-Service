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
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "assessments/start")] HttpRequestData req)
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
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = "finops/setup")] HttpRequestData req)
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
        
        // Build the target URL with function key
        var targetUrl = $"{_auditApiUrl}/{route}";
        if (!string.IsNullOrEmpty(_auditApiKey))
        {
            targetUrl += $"?code={_auditApiKey}";
        }

        _logger.LogInformation("Forwarding request to: {Url}", targetUrl.Split('?')[0]); // Don't log the key

        // Read the request body
        var requestBody = await req.ReadAsStringAsync();

        // Create the proxied request
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Post, targetUrl);
        
        if (!string.IsNullOrEmpty(requestBody))
        {
            proxyRequest.Content = new StringContent(requestBody, System.Text.Encoding.UTF8, "application/json");
        }

        // Forward relevant headers
        if (req.Headers.TryGetValues("Content-Type", out var contentTypes))
        {
            // Content-Type is set via StringContent above
        }

        // Make the request
        var proxyResponse = await client.SendAsync(proxyRequest);
        
        // Build response
        var response = req.CreateResponse(proxyResponse.StatusCode);
        
        // Copy response content
        var responseContent = await proxyResponse.Content.ReadAsStringAsync();
        if (!string.IsNullOrEmpty(responseContent))
        {
            response.Headers.Add("Content-Type", "application/json");
            await response.WriteStringAsync(responseContent);
        }

        return response;
    }
}
