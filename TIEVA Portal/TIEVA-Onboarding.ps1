# TIEVA Customer Onboarding Script
# Run this script in Azure Cloud Shell (PowerShell) or local PowerShell with Az module

param(
    [string]$AppName = "TIEVA-Assessment-Reader"
)

Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  TIEVA Customer Onboarding Script" -ForegroundColor Cyan  
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

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
Write-Host "Select subscriptions to include (comma-separated numbers, or 'all'):" -ForegroundColor Yellow
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

# Choose permission level
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Permission Level Selection" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Choose the permission level:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  [1] Standard (Reader only)" -ForegroundColor White
Write-Host "      - Run assessments and view resources" -ForegroundColor Gray
Write-Host "      - Manual FinOps setup required" -ForegroundColor Gray
Write-Host ""
Write-Host "  [2] FinOps Enabled (Recommended)" -ForegroundColor White
Write-Host "      - Everything in Standard, plus:" -ForegroundColor Gray
Write-Host "      - Auto-create storage accounts for cost exports" -ForegroundColor Gray
Write-Host "      - Auto-create Cost Management exports" -ForegroundColor Gray
Write-Host "      - Auto-generate SAS tokens" -ForegroundColor Gray
Write-Host ""
Write-Host "Enter choice (1 or 2):" -ForegroundColor Yellow
$permChoice = Read-Host

$enableFinOps = $permChoice -eq "2"

if ($enableFinOps) {
    Write-Host ""
    Write-Host "FinOps Enabled - will assign additional permissions" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "Standard Mode - Reader only" -ForegroundColor Green
}

# Create App Registration
Write-Host ""
Write-Host "Creating App Registration..." -ForegroundColor Cyan
$app = New-AzADApplication -DisplayName $AppName
$sp = New-AzADServicePrincipal -ApplicationId $app.AppId

# Create secret (2 year expiry)
$endDate = (Get-Date).AddYears(2)
$secret = New-AzADAppCredential -ObjectId $app.Id -EndDate $endDate

Write-Host "App Registration created: $($app.AppId)" -ForegroundColor Green

# Wait for SP to propagate
Write-Host ""
Write-Host "Waiting for Service Principal to propagate..." -ForegroundColor Cyan
Start-Sleep -Seconds 15

# Assign roles to selected subscriptions
Write-Host ""
Write-Host "Assigning roles to subscriptions..." -ForegroundColor Cyan

foreach ($sub in $selectedSubs) {
    $scope = "/subscriptions/$($sub.Id)"
    Write-Host ""
    Write-Host "Subscription: $($sub.Name)" -ForegroundColor White
    
    # Always assign Reader
    try {
        New-AzRoleAssignment -ApplicationId $app.AppId -RoleDefinitionName "Reader" -Scope $scope -ErrorAction Stop | Out-Null
        Write-Host "  [OK] Reader" -ForegroundColor Green
    } catch {
        if ($_.Exception.Message -like "*already exists*") {
            Write-Host "  [OK] Reader (already assigned)" -ForegroundColor Green
        } else {
            Write-Host "  [FAIL] Reader: $($_.Exception.Message)" -ForegroundColor Red
        }
    }
    
    if ($enableFinOps) {
        # Contributor - for creating Resource Groups, Storage Accounts
        try {
            New-AzRoleAssignment -ApplicationId $app.AppId -RoleDefinitionName "Contributor" -Scope $scope -ErrorAction Stop | Out-Null
            Write-Host "  [OK] Contributor" -ForegroundColor Green
        } catch {
            if ($_.Exception.Message -like "*already exists*") {
                Write-Host "  [OK] Contributor (already assigned)" -ForegroundColor Green
            } else {
                Write-Host "  [FAIL] Contributor: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
        
        # Cost Management Contributor - for creating Cost Exports
        try {
            New-AzRoleAssignment -ApplicationId $app.AppId -RoleDefinitionName "Cost Management Contributor" -Scope $scope -ErrorAction Stop | Out-Null
            Write-Host "  [OK] Cost Management Contributor" -ForegroundColor Green
        } catch {
            if ($_.Exception.Message -like "*already exists*") {
                Write-Host "  [OK] Cost Management Contributor (already assigned)" -ForegroundColor Green
            } else {
                Write-Host "  [FAIL] Cost Management Contributor: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
        
        # Storage Blob Data Contributor - for generating SAS tokens with data plane access
        try {
            New-AzRoleAssignment -ApplicationId $app.AppId -RoleDefinitionName "Storage Blob Data Contributor" -Scope $scope -ErrorAction Stop | Out-Null
            Write-Host "  [OK] Storage Blob Data Contributor" -ForegroundColor Green
        } catch {
            if ($_.Exception.Message -like "*already exists*") {
                Write-Host "  [OK] Storage Blob Data Contributor (already assigned)" -ForegroundColor Green
            } else {
                Write-Host "  [FAIL] Storage Blob Data Contributor: $($_.Exception.Message)" -ForegroundColor Red
            }
        }
    }
}

# Generate output
$output = @{
    tenantId = $context.Tenant.Id
    clientId = $app.AppId
    clientSecret = $secret.SecretText
    secretExpiry = $endDate.ToString("yyyy-MM-dd")
    finOpsEnabled = $enableFinOps
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
Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Onboarding Complete!" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "App Registration Details:" -ForegroundColor White
Write-Host "  Display Name: $AppName" -ForegroundColor Gray
Write-Host "  Client ID: $($app.AppId)" -ForegroundColor Gray
Write-Host "  Secret Expiry: $($endDate.ToString('yyyy-MM-dd'))" -ForegroundColor Gray
Write-Host ""
Write-Host "Roles Assigned:" -ForegroundColor White
Write-Host "  - Reader (all subscriptions)" -ForegroundColor Gray
if ($enableFinOps) {
    Write-Host "  - Contributor (all subscriptions)" -ForegroundColor Gray
    Write-Host "  - Cost Management Contributor (all subscriptions)" -ForegroundColor Gray
    Write-Host "  - Storage Blob Data Contributor (all subscriptions)" -ForegroundColor Gray
}
Write-Host ""
Write-Host "Connection file saved to: $filename" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next Steps:" -ForegroundColor Cyan
Write-Host "  1. Send the connection file to your TIEVA contact" -ForegroundColor White
Write-Host "  2. Or import directly in the TIEVA Portal" -ForegroundColor White
Write-Host ""
Write-Host "======================================" -ForegroundColor Cyan
