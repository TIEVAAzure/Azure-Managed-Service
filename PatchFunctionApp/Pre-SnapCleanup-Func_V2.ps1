az logout
az login --scope "https://appservice.azure.com/.default"

# ================================
# CONFIG – EDIT THESE VALUES
# ================================
# Subscription where the Function App + RG + Storage live
$DEPLOY_SUB_ID = "27459e10-b37a-4b3b-9fa8-12f226dacd30"

# Resource details
$RG_NAME      = "rg-presnapshot-func2"
$LOCATION     = "uksouth"
$FUNC_NAME    = "fa-presnapcleanup-flex-uks-001-tieva"   # must be globally unique
$STORAGE_NAME = "stpresnapchsuks001tieva"               # 3–24 chars, lowercase, unique in Azure

# ================================
# LogicMonitor settings from environment variables
# ================================
$LM_AUTH        = $env:LM_AUTH
$LM_COMPANY     = $env:LM_COMPANY
$LM_DOMAIN_NAME = $env:LM_DOMAIN_NAME
$LM_TENANT_ID   = $env:LM_TENANT_ID
$LM_SDT_MINUTES = $env:LM_SDT_MINUTES

if (-not $LM_DOMAIN_NAME -or $LM_DOMAIN_NAME.Trim() -eq "") {
    $LM_DOMAIN_NAME = "logicmonitor.com"
}

if (-not $LM_SDT_MINUTES -or $LM_SDT_MINUTES.Trim() -eq "") {
    $LM_SDT_MINUTES = "120"
}
# ================================


# Subscriptions where the MI should have Contributor
# (can include the deploy sub + any others, e.g. Lighthouse customer subs)
$TargetSubscriptions = @(
    "3175995f-faf4-467e-8c38-ea866d1c9cdd",
    "4b944335-f142-4bb7-8bd6-8e868318c401",
    "a2274ccd-57b8-4d72-a94a-c38a235cf56e",
    "df82ae5c-8de4-4e5d-a976-a75d56fc3b9c",
    "27459e10-b37a-4b3b-9fa8-12f226dacd30"
)

# If you forget to populate TargetSubscriptions, default to the deploy sub
if (-not $TargetSubscriptions -or $TargetSubscriptions.Count -eq 0) {
    $TargetSubscriptions = @($DEPLOY_SUB_ID)
}

# Your GitHub ZIP with both functions (PreSnapshot + CleanupSnapshots)
$ZIP_URL = "https://github.com/TIEVAAzure/Azure-Managed-Service/raw/refs/heads/main/PatchFunctionApp/Pre-SnapCleanup-Func_v2.1.zip"

# Local filename in Cloud Shell HOME directory (NO uploads needed)
$LOCAL_ZIP_NAME = "Pre-SnapCleanup-Func_fixed.zip"

# Optional cleanup defaults (these are app settings the cleanup function reads)
$CLEANUP_SUBSCRIPTIONS       = ""      # e.g. "sub1,sub2" or leave empty
$CLEANUP_RESOURCE_GROUPS     = ""      # e.g. "rg1,rg2" or leave empty
$CLEANUP_SAFETY_MINUTES      = "5"     # string
$CLEANUP_MAX_DELETES_PER_RUN = "0"     # "0" = no limit
$CLEANUP_DRY_RUN             = "false" # "true" or "false"
# ================================

$ErrorActionPreference = "Stop"

Write-Host "==> Setting deployment subscription: $DEPLOY_SUB_ID"
az account set --subscription $DEPLOY_SUB_ID

# ---------------------------
# Infra: RG + Storage + App
# ---------------------------
Write-Host "==> Creating resource group: $RG_NAME in $LOCATION (idempotent)"
az group create `
  --name $RG_NAME `
  --location $LOCATION | Out-Null

Write-Host "==> Creating storage account: $STORAGE_NAME (required for Flex Consumption, idempotent)"
az storage account create `
  --name $STORAGE_NAME `
  --location $LOCATION `
  --resource-group $RG_NAME `
  --sku Standard_LRS `
  --allow-blob-public-access false 2>$null | Out-Null

Write-Host "==> Creating Flex Consumption PowerShell Function App: $FUNC_NAME (idempotent)"
$faCreateResult = az functionapp create `
  --resource-group $RG_NAME `
  --name $FUNC_NAME `
  --storage-account $STORAGE_NAME `
  --flexconsumption-location $LOCATION `
  --runtime powershell `
  --runtime-version 7.4 2>&1

$faCreateResult | Out-String | Write-Host

# ---------------------------
# Managed identity + roles
# ---------------------------
Write-Host "==> Enabling system-assigned managed identity"
$identityJson = az functionapp identity assign `
  --name $FUNC_NAME `
  --resource-group $RG_NAME 2>&1

$identityJson | Out-String | Write-Host

$identity     = $identityJson | ConvertFrom-Json
$PRINCIPAL_ID = $identity.principalId
if (-not $PRINCIPAL_ID) {
    throw "Failed to get principalId from identity assign output."
}
Write-Host "Managed identity principalId: $PRINCIPAL_ID"

Write-Host "==> Assigning Contributor role on target subscriptions"
foreach ($subId in $TargetSubscriptions) {
    Write-Host "   - Assigning Contributor on subscription $subId ..."
    $result = az role assignment create `
      --assignee-object-id $PRINCIPAL_ID `
      --assignee-principal-type ServicePrincipal `
      --role "Contributor" `
      --scope "/subscriptions/$subId" 2>&1

    if ($LASTEXITCODE -ne 0) {
        Write-Warning "      Failed to assign role on /subscriptions/$subId. Output:"
        $result | Out-String | Write-Host
    } else {
        Write-Host "      Role assignment created (or already exists)."
    }
}

# --------------------------------------------
# Download ZIP into $HOME and deploy via config-zip
# --------------------------------------------
Write-Host "==> Downloading ZIP package from GitHub"
$zipPath = Join-Path $HOME $LOCAL_ZIP_NAME

if (Test-Path $zipPath) {
    Write-Host "   Removing existing $zipPath"
    Remove-Item $zipPath -Force
}

Write-Host "   Downloading $ZIP_URL -> $zipPath"
Invoke-WebRequest -Uri $ZIP_URL -OutFile $zipPath -UseBasicParsing

if (-not (Test-Path $zipPath)) {
    throw "ZIP download failed; file not found at $zipPath"
}

Write-Host "   Saved to $zipPath"

Write-Host "==> Deploying ZIP to Function App via config-zip (showing raw output)..."
$deployResult = az functionapp deployment source config-zip `
  --resource-group $RG_NAME `
  --name $FUNC_NAME `
  --src $zipPath 2>&1

$deployResult | Out-String | Write-Host
Write-Host "   config-zip exit code: $LASTEXITCODE"

if ($LASTEXITCODE -ne 0) {
    throw "config-zip deployment failed. See output above."
}

# -----------------------------------
# Configure cleanup app settings only
# (no WEBSITE_RUN_FROM_PACKAGE / no FUNCTIONS_WORKER_RUNTIME for Flex)
# -----------------------------------
Write-Host "==> Configuring cleanup + LogicMonitor app settings"
az functionapp config appsettings set `
  --name $FUNC_NAME `
  --resource-group $RG_NAME `
  --settings `
    "Cleanup_Subscriptions=$CLEANUP_SUBSCRIPTIONS" `
    "Cleanup_ResourceGroups=$CLEANUP_RESOURCE_GROUPS" `
    "Cleanup_SafetyMinutes=$CLEANUP_SAFETY_MINUTES" `
    "Cleanup_MaxDeletesPerRun=$CLEANUP_MAX_DELETES_PER_RUN" `
    "Cleanup_DryRun=$CLEANUP_DRY_RUN" `
    "lm_auth=$LM_AUTH" `
    "lm_company=$LM_COMPANY" `
    "lm_domain_name=$LM_DOMAIN_NAME" `
    "LM_tenant_id=$LM_TENANT_ID" `
    "LM_sdt_minutes=$LM_SDT_MINUTES" | Out-Null


# Optional: clean up any legacy settings that break Flex
Write-Host "==> Removing legacy WEBSITE_RUN_FROM_PACKAGE and FUNCTIONS_WORKER_RUNTIME (if present)"
az functionapp config appsettings delete `
  --name $FUNC_NAME `
  --resource-group $RG_NAME `
  --setting-names WEBSITE_RUN_FROM_PACKAGE FUNCTIONS_WORKER_RUNTIME `
  --yes 2>$null | Out-Null

# -----------------------------------
# List functions so we can see them
# -----------------------------------
Write-Host "`n==> Listing functions in $FUNC_NAME" -ForegroundColor Cyan
$funcList = az functionapp function list `
  --resource-group $RG_NAME `
  --name $FUNC_NAME `
  -o table 2>&1

$funcList | Out-String | Write-Host

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "Function App:   $FUNC_NAME"
Write-Host "Resource Group: $RG_NAME"
Write-Host "Storage:        $STORAGE_NAME"
Write-Host "ZIP URL:        $ZIP_URL"
Write-Host ""
Write-Host "Next steps (in portal):"
Write-Host " - Go to the Function App → Functions blade; you should see 'PreSnapshot' and 'CleanupSnapshots'."
Write-Host " - Wire your Maintenance Configuration Pre-Maintenance Event to the 'PreSnapshot' function."
Write-Host " - 'CleanupSnapshots' will run on its timer as defined in the function code."
