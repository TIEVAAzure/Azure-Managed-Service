# Update-AuthLevels.ps1
# Run from: C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions\TIEVA.Functions
# This changes all functions from Anonymous to Function auth (except HealthCheck)

$functionsPath = ".\Functions"

Write-Host "Updating authorization levels..." -ForegroundColor Cyan

Get-ChildItem -Path $functionsPath -Filter "*.cs" | ForEach-Object {
    $content = Get-Content $_.FullName -Raw
    $originalContent = $content
    
    # Replace Anonymous with Function
    $content = $content -replace 'AuthorizationLevel\.Anonymous', 'AuthorizationLevel.Function'
    
    if ($content -ne $originalContent) {
        Set-Content -Path $_.FullName -Value $content -NoNewline
        $count = ([regex]::Matches($content, 'AuthorizationLevel\.Function')).Count
        Write-Host "  Updated: $($_.Name) ($count functions)" -ForegroundColor Green
    }
}

# Fix HealthCheck to remain Anonymous (for monitoring tools)
$dashboardPath = Join-Path $functionsPath "DashboardFunctions.cs"
if (Test-Path $dashboardPath) {
    $content = Get-Content $dashboardPath -Raw
    
    # Find the HealthCheck function and change it back to Anonymous
    $pattern = '(\[Function\("HealthCheck"\)\]\s*public async Task<HttpResponseData> HealthCheck\(\s*\[HttpTrigger\()AuthorizationLevel\.Function'
    $replacement = '$1AuthorizationLevel.Anonymous'
    
    $newContent = $content -replace $pattern, $replacement
    
    if ($content -ne $newContent) {
        Set-Content -Path $dashboardPath -Value $newContent -NoNewline
        Write-Host "  Fixed: DashboardFunctions.cs (HealthCheck remains Anonymous)" -ForegroundColor Yellow
    }
}

Write-Host "`nDone! All functions now require Function-level auth." -ForegroundColor Cyan
Write-Host "HealthCheck endpoint remains Anonymous for monitoring." -ForegroundColor Yellow
Write-Host "`nNext steps:" -ForegroundColor White
Write-Host "1. Delete Services\AuthenticationMiddleware.cs (if it exists)" -ForegroundColor Gray
Write-Host "2. Deploy: func azure functionapp publish func-tievaportal-6612" -ForegroundColor Gray
Write-Host "3. Get the host key from Azure Portal and set it as TIEVA_API_KEY in func-tieva-audit" -ForegroundColor Gray
