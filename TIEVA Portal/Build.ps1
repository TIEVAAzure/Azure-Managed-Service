# Configuration - change these if needed
$projectName = "tievaPortal"
$location = "uksouth"
$resourceGroup = "rg-$projectName-prod"
$sqlServerName = "sql-$projectName-$(Get-Random -Maximum 9999)"
$sqlDbName = "TievaPortal"
$kvName = "kv-$projectName-$(Get-Random -Maximum 9999)"
$funcAppName = "func-$projectName-$(Get-Random -Maximum 9999)"
$storageName = "st$projectName$(Get-Random -Maximum 9999)"
$staticWebAppName = "swa-$projectName-portal"

# Display what we'll create
Write-Host "Will create:" -ForegroundColor Cyan
Write-Host "  Resource Group: $resourceGroup"
Write-Host "  SQL Server: $sqlServerName"
Write-Host "  Key Vault: $kvName"
Write-Host "  Function App: $funcAppName"
Write-Host "  Static Web App: $staticWebAppName"