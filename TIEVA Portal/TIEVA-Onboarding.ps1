# TIEVA Customer Onboarding Script
# Run this script in Azure Cloud Shell (PowerShell) or local PowerShell with Az module

param(
    [string]$AppName = "TIEVA-Assessment-Reader"
)

Write-Host "TIEVA Customer Onboarding Script" -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan

# Check if logged in
$context = Get-AzContext
if (-not $context) {
    Write-Host "Please log in to Azure..." -ForegroundColor Yellow
    Connect-AzAccount
    $context = Get-AzContext
}

Write-Host "Logged in as: $($context.Account.Id)" -ForegroundColor Green
Write-Host "Tenant: $($context.Tenant.Id)" -ForegroundColor Green

# Get subscriptions
Write-Host ""
Write-Host "Available Subscriptions:" -ForegroundColor Cyan
$subs = Get-AzSubscription | Where-Object { $_.State -eq "Enabled" }
for ($i = 0; $i -lt $subs.Count; $i++) {
    Write-Host "  [$i] $($subs[$i].Name) ($($subs[$i].Id))"
}

Write-Host ""
Write-Host "Select subscriptions to include (comma-separated numbers, or all):" -ForegroundColor Yellow
$selection = Read-Host

$selectedSubs = @()
if ($selection -eq "all") {
    $selectedSubs = $subs
} else {
    $indices = $selection -split "," | ForEach-Object { [int]$_.Trim() }
    $selectedSubs = $indices | ForEach-Object { $subs[$_] }
}

Write-Host ""
Write-Host "Selected subscriptions:" -ForegroundColor Green
$selectedSubs | ForEach-Object { Write-Host "  - $($_.Name)" }

# Create App Registration
Write-Host ""
Write-Host "Creating App Registration..." -ForegroundColor Cyan
$app = New-AzADApplication -DisplayName $AppName
$sp = New-AzADServicePrincipal -ApplicationId $app.AppId

# Create secret (2 year expiry)
$endDate = (Get-Date).AddYears(2)
$secret = New-AzADAppCredential -ObjectId $app.Id -EndDate $endDate

Write-Host "App Registration created: $($app.AppId)" -ForegroundColor Green

# Assign Reader role to selected subscriptions
Write-Host ""
Write-Host "Assigning Reader role to subscriptions..." -ForegroundColor Cyan
Start-Sleep -Seconds 10  # Wait for SP to propagate

foreach ($sub in $selectedSubs) {
    try {
        New-AzRoleAssignment -ApplicationId $app.AppId -RoleDefinitionName "Reader" -Scope "/subscriptions/$($sub.Id)" -ErrorAction Stop
        Write-Host "  [OK] $($sub.Name)" -ForegroundColor Green
    } catch {
        Write-Host "  [FAIL] $($sub.Name): $($_.Exception.Message)" -ForegroundColor Red
    }
}

# Generate output
$output = @{
    tenantId = $context.Tenant.Id
    clientId = $app.AppId
    clientSecret = $secret.SecretText
    secretExpiry = $endDate.ToString("yyyy-MM-dd")
    subscriptions = $selectedSubs | ForEach-Object {
        @{
            id = $_.Id
            name = $_.Name
            include = $true
        }
    }
}

$filename = "tieva-connection-$($context.Tenant.Id.Substring(0,8)).json"
$output | ConvertTo-Json -Depth 3 | Out-File $filename -Encoding UTF8

Write-Host ""
Write-Host "=================================" -ForegroundColor Cyan
Write-Host "Onboarding complete!" -ForegroundColor Green
Write-Host "Connection file saved to: $filename" -ForegroundColor Yellow
Write-Host ""
Write-Host "Please send this file to your TIEVA contact." -ForegroundColor Cyan
Write-Host "=================================" -ForegroundColor Cyan