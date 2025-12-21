# Configuration - change these if needed
$projectName = "tievaPortal"
$location = "uksouth"
$resourceGroup = "rg-$projectName-prod"
$sqlServerName = "sql-$projectName-$(Get-Random -Maximum 9999)"
$sqlDbName = "TievaPortal"
$kvName = "kv-$projectName-$(Get-Random -Maximum 9999)"
$funcAppName = "func-$projectName-$(Get-Random -Maximum 9999)"
$storageName = "sttieva$(Get-Random -Maximum 9999)"
$staticWebAppName = "swa-$projectName-portal"

# Display what we'll create
Write-Host "Will create:" -ForegroundColor Cyan
Write-Host "  Resource Group: $resourceGroup"
Write-Host "  SQL Server: $sqlServerName"
Write-Host "  Key Vault: $kvName"
Write-Host "  Function App: $funcAppName"
Write-Host "  Static Web App: $staticWebAppName"

az group create --name $resourceGroup --location $location

az keyvault create `
  --name $kvName `
  --resource-group $resourceGroup `
  --location $location `
  --enable-rbac-authorization true

  # Get your current user details for SQL admin
$currentUser = az ad signed-in-user show --query "{name:displayName, objectId:id}" -o json | ConvertFrom-Json

$adminName = "Chris Thompson (Admin)"
$adminSid = "93c50bfb-0cd9-4470-a490-6ddb42460b9a"

az sql server create `
  --name $sqlServerName `
  --resource-group $resourceGroup `
  --location $location `
  --enable-ad-only-auth `
  --external-admin-principal-type User `
  --external-admin-name "$adminName" `
  --external-admin-sid $adminSid

  az sql db create `
  --resource-group $resourceGroup `
  --server $sqlServerName `
  --name $sqlDbName `
  --service-objective Basic

  az sql server firewall-rule create `
  --resource-group $resourceGroup `
  --server $sqlServerName `
  --name "AllowAzureServices" `
  --start-ip-address 0.0.0.0 `
  --end-ip-address 0.0.0.0

  az storage account create `
  --name $storageName `
  --resource-group $resourceGroup `
  --location $location `
  --sku Standard_LRS

  az functionapp create `
  --name $funcAppName `
  --resource-group $resourceGroup `
  --storage-account $storageName `
  --consumption-plan-location $location `
  --runtime dotnet-isolated `
  --runtime-version 8 `
  --functions-version 4 `
  --assign-identity [system]

  $funcIdentity = az functionapp identity show `
  --name $funcAppName `
  --resource-group $resourceGroup `
  --query principalId -o tsv

Write-Host "Function App Managed Identity: $funcIdentity"

az role assignment create `
  --role "Key Vault Secrets User" `
  --assignee $funcIdentity `
  --scope $(az keyvault show --name $kvName --resource-group $resourceGroup --query id -o tsv)

$myIp = (Invoke-RestMethod -Uri "https://api.ipify.org")

az sql server firewall-rule create `
  --resource-group $resourceGroup `
  --server $sqlServerName `
  --name "MyIP" `
  --start-ip-address $myIp `
  --end-ip-address $myIp

Write-Host "Your IP $myIp added to SQL firewall"

Write-Host "Your Function App name is: $funcAppName"

az staticwebapp create `
  --name $staticWebAppName `
  --resource-group $resourceGroup `
  --location "westeurope" `
  --sku Free

  $swaUrl = az staticwebapp show `
  --name $staticWebAppName `
  --resource-group $resourceGroup `
  --query "defaultHostname" -o tsv

Write-Host "Static Web App URL: https://$swaUrl"

az functionapp cors add `
  --name $funcAppName `
  --resource-group $resourceGroup `
  --allowed-origins "https://yellow-beach-0dfcd8103.3.azurestaticapps.net"

  $config = @{
  ResourceGroup = $resourceGroup
  SqlServer = "$sqlServerName.database.windows.net"
  SqlDatabase = $sqlDbName
  KeyVault = $kvName
  FunctionApp = $funcAppName
  StorageAccount = $storageName
  StaticWebApp = $staticWebAppName
  StaticWebAppUrl = "https://yellow-beach-0dfcd8103.3.azurestaticapps.net"
  FunctionIdentity = $funcIdentity
}

$config | ConvertTo-Json | Out-File "tieva-config.json"
Write-Host "Configuration saved to tieva-config.json" -ForegroundColor Green
$config | Format-Table -AutoSize

cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal"
mkdir functions
mkdir portal
cd functions

npm install -g azure-functions-core-tools@4 --unsafe-perm true

$env:Path += ";$env:APPDATA\npm"
func --version

cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\functions"
func init TIEVA.Functions --dotnet-isolated --target-framework net8.0
cd TIEVA.Functions

dotnet add package Microsoft.Azure.Functions.Worker.Extensions.Http
dotnet add package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.0
dotnet add package Azure.Identity
dotnet add package Azure.Security.KeyVault.Secrets
dotnet add package Azure.ResourceManager
dotnet add package Azure.ResourceManager.Resources
dotnet add package Microsoft.Azure.Functions.Worker.Extensions.DurableTask
dotnet add package Microsoft.Azure.Functions.Worker.Extensions.Http.AspNetCore



mkdir Models
mkdir Services
mkdir Functions

func azure functionapp publish func-tievaPortal-6612 --dotnet-isolated

cd "C:\VS Code\Azure-Managed-Service\TIEVA Portal\portal"

az account show --query tenantId -o tsv

az ad app create `
  --display-name "TIEVA Portal" `
  --web-redirect-uris "https://yellow-beach-0dfcd8103.3.azurestaticapps.net/.auth/login/aad/callback" `
  --sign-in-audience "AzureADMyOrg"

  az ad app credential reset `
  --id 5edd71d4-a519-4900-924c-78c3f0d24fdf `
  --display-name "TIEVA Portal Secret" `
  --years 2

  az staticwebapp appsettings set `
  --name swa-tievaPortal-portal `
  --resource-group rg-tievaPortal-prod `
  --setting-names `
    AAD_CLIENT_ID=5edd71d4-a519-4900-924c-78c3f0d24fdf `
    AAD_CLIENT_SECRET=YOUR_SECRET_HERE

    $token = az staticwebapp secrets list --name swa-tievaPortal-portal --resource-group rg-tievaPortal-prod --query "properties.apiKey" -o tsv

    npm install -g @azure/static-web-apps-cli
swa deploy ./  --deployment-token $token --env production