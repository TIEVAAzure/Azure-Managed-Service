using namespace System.Net

param($Request, $TriggerMetadata)

$contextName = "finops-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Write-Host "SetupFinOps function triggered (Context: $contextName)"

Disable-AzContextAutosave -Scope Process -ErrorAction SilentlyContinue | Out-Null

$body = $Request.Body

if (-not $body.connectionId) {
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::BadRequest
        Body = @{ error = "connectionId is required" } | ConvertTo-Json
        ContentType = "application/json"
    })
    return
}

$connectionId = $body.connectionId
$location = if ($body.location) { $body.location } else { "uksouth" }
$resourceGroupName = $body.resourceGroupName
$storageAccountName = $body.storageAccountName
$targetSubscriptionId = $body.subscriptionId
$sasExpiryDays = if ($body.sasExpiryDays) { [int]$body.sasExpiryDays } else { 365 }

Write-Host "========================================"
Write-Host "INPUT PARAMETERS RECEIVED"
Write-Host "========================================"
Write-Host "Connection ID:     $connectionId"
Write-Host "Target Sub ID:     $targetSubscriptionId"
Write-Host "Location:          $location"
Write-Host "Resource Group:    $resourceGroupName"
Write-Host "Storage Account:   $storageAccountName"
Write-Host "========================================"

try {
    $apiBase = if ($env:TIEVA_API_URL) { $env:TIEVA_API_URL } else { "https://func-tievaportal-6612.azurewebsites.net/api" }
    
    Write-Host "Getting connection details..."
    $connResponse = Invoke-RestMethod -Uri "$apiBase/connections/$connectionId" -Method Get
    
    $tenantId = if ($connResponse.tenantId) { $connResponse.tenantId } else { $connResponse.TenantId }
    $clientId = if ($connResponse.clientId) { $connResponse.clientId } else { $connResponse.ClientId }
    $customerName = if ($connResponse.customerName) { $connResponse.customerName } else { $connResponse.CustomerName }
    $customerId = if ($connResponse.customerId) { $connResponse.customerId } else { $connResponse.CustomerId }
    
    Write-Host "Customer: $customerName ($customerId), Tenant: $tenantId"
    
    $cleanName = ($customerName -replace '[^a-zA-Z0-9]', '').ToLower()
    if ($cleanName.Length -gt 10) { $cleanName = $cleanName.Substring(0, 10) }
    
    $allSubs = $connResponse.subscriptions
    if (-not $allSubs) { throw "No subscriptions found in connection response" }
    
    $allSubscriptions = @($allSubs | Where-Object { $_.isInScope -or $_.IsInScope })
    if ($allSubscriptions.Count -eq 0) { throw "No in-scope subscriptions found" }
    
    Write-Host "In-scope subscriptions: $($allSubscriptions.Count)"
    
    # Filter for Premium and Advanced tiers ONLY - Standard subscriptions excluded from FinOps
    Write-Host ""
    Write-Host "========================================"
    Write-Host "SUBSCRIPTION TIER FILTERING"
    Write-Host "========================================"
    
    $premiumAdvancedSubs = @()
    $standardSubs = @()
    $unknownTierSubs = @()
    
    foreach ($sub in $allSubscriptions) {
        $subId = if ($sub.SubscriptionId) { $sub.SubscriptionId } else { $sub.subscriptionId }
        $subName = if ($sub.SubscriptionName) { $sub.SubscriptionName } else { $sub.subscriptionName }
        $tier = if ($sub.tierName) { $sub.tierName } else { $sub.TierName }
        
        if ($tier -match 'Premium|Advanced') {
            $premiumAdvancedSubs += $sub
            Write-Host "  [INCLUDED] $subName ($tier)"
        } elseif ($tier -match 'Standard') {
            $standardSubs += $sub
            Write-Host "  [EXCLUDED] $subName ($tier) - Standard tier"
        } else {
            $unknownTierSubs += $sub
            Write-Host "  [UNKNOWN]  $subName (Tier: '$tier')"
        }
    }
    
    Write-Host "========================================"
    Write-Host "Premium/Advanced: $($premiumAdvancedSubs.Count)"
    Write-Host "Standard (excluded): $($standardSubs.Count)"
    Write-Host "Unknown tier: $($unknownTierSubs.Count)"
    Write-Host "========================================"
    Write-Host ""
    
    # Determine which subscriptions to create exports for
    if ($premiumAdvancedSubs.Count -gt 0) {
        $exportSubscriptions = $premiumAdvancedSubs
        Write-Host "Creating cost exports for $($exportSubscriptions.Count) Premium/Advanced subscription(s) ONLY"
    } elseif ($unknownTierSubs.Count -gt 0) {
        # If no Premium/Advanced but unknown tiers exist, include those (might be misconfigured)
        $exportSubscriptions = $unknownTierSubs
        Write-Host "WARNING: No Premium/Advanced subs found. Using $($unknownTierSubs.Count) subscription(s) with unknown tier."
    } else {
        # All subscriptions are Standard - don't create exports
        Write-Host "WARNING: All subscriptions are Standard tier - no cost exports will be created."
        Write-Host "FinOps cost exports require Premium or Advanced tier subscriptions."
        $exportSubscriptions = @()
    }
    
    $primarySubId = if ($targetSubscriptionId) { $targetSubscriptionId } else {
        $ps = $allSubscriptions | Select-Object -First 1
        if ($ps.SubscriptionId) { $ps.SubscriptionId } else { $ps.subscriptionId }
    }
    if (-not $primarySubId) { throw "Could not determine subscription ID" }
    
    # Get secret from Key Vault
    $secretName = "sp-$connectionId"
    Write-Host "Retrieving secret from Key Vault..."
    Connect-AzAccount -Identity -ContextName "$contextName-identity" -Force -WarningAction SilentlyContinue | Out-Null
    Set-AzContext -Context (Get-AzContext -Name "$contextName-identity") | Out-Null
    $secret = Get-AzKeyVaultSecret -VaultName "kv-tievaPortal-874" -Name $secretName -AsPlainText
    if (-not $secret) { throw "Could not retrieve secret from Key Vault" }
    
    # Connect to customer tenant WITH SPECIFIC SUBSCRIPTION
    Write-Host "Connecting to customer tenant..."
    Write-Host "  Tenant: $tenantId"
    Write-Host "  Target Subscription: $primarySubId"
    $secureSecret = ConvertTo-SecureString $secret -AsPlainText -Force
    $credential = New-Object System.Management.Automation.PSCredential($clientId, $secureSecret)
    
    # Connect directly to the target subscription to avoid Azure picking a default
    try {
        Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $tenantId -Subscription $primarySubId -ContextName "$contextName-customer" -Force -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null
        Write-Host "  Connected directly to subscription $primarySubId"
    }
    catch {
        Write-Host "  Could not connect directly to target subscription: $($_.Exception.Message)"
        Write-Host "  Trying connect without subscription specification..."
        
        Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $tenantId -ContextName "$contextName-customer" -Force -ErrorAction Stop -WarningAction SilentlyContinue | Out-Null
        Write-Host "  Connected to tenant (default subscription selected by Azure)"
    }
    
    # Verify and set subscription context
    $currentCtx = Get-AzContext
    Write-Host "  Current context after connect: Sub=$($currentCtx.Subscription.Id) ($($currentCtx.Subscription.Name))"
    
    if ($currentCtx.Subscription.Id -ne $primarySubId) {
        Write-Host "  Context is NOT on target subscription - attempting to switch..."
        try {
            Set-AzContext -Subscription $primarySubId -Tenant $tenantId -ErrorAction Stop | Out-Null
            $currentCtx = Get-AzContext
            Write-Host "  Switched to: $($currentCtx.Subscription.Id) ($($currentCtx.Subscription.Name))"
        }
        catch {
            Write-Host "  ERROR: Cannot switch to subscription $primarySubId"
            Write-Host "  Error: $($_.Exception.Message)"
            
            # List available subscriptions
            Write-Host "  Listing available subscriptions..."
            $availableSubs = Get-AzSubscription -TenantId $tenantId -ErrorAction SilentlyContinue
            if ($availableSubs) {
                Write-Host "  Available subscriptions ($($availableSubs.Count)):"
                $availableSubs | ForEach-Object { Write-Host "    - $($_.Id) ($($_.Name))" }
                
                # Check if target is in the list
                $targetInList = $availableSubs | Where-Object { $_.Id -eq $primarySubId }
                if (-not $targetInList) {
                    Write-Host "  !!! Target subscription $primarySubId is NOT in available list !!!"
                    Write-Host "  The App Registration does not have access to this subscription."
                    throw "Subscription $primarySubId is not accessible. Please grant the App Registration at least Reader role on this subscription."
                }
            }
            throw "Cannot set subscription context to $primarySubId"
        }
    } else {
        Write-Host "  Context is correctly set to target subscription"
    }
    
    # Generate names if not provided
    if (-not $resourceGroupName) { $resourceGroupName = "rg-finops-$cleanName" }
    if (-not $storageAccountName) {
        $uniqueSuffix = [Guid]::NewGuid().ToString('N').Substring(0, 6)
        $storageAccountName = "stfinops$cleanName$uniqueSuffix"
        if ($storageAccountName.Length -gt 24) { $storageAccountName = $storageAccountName.Substring(0, 24) }
    }
    $storageAccountName = $storageAccountName.ToLower() -replace '[^a-z0-9]', ''
    
    Write-Host "Resource Group: $resourceGroupName, Storage Account: $storageAccountName"
    
    $results = @{
        customerId = $customerId
        customerName = $customerName
        connectionId = $connectionId
        subscriptionId = $primarySubId
        resourceGroupName = $resourceGroupName
        storageAccountName = $storageAccountName
        location = $location
        subscriptionsProcessed = @()
        exportsCreated = @()
        errors = @()
        resourceGroupCreated = $false
        storageAccountCreated = $false
        containerCreated = $false
        tierFiltering = @{
            premiumAdvancedCount = $premiumAdvancedSubs.Count
            standardExcludedCount = $standardSubs.Count
            unknownTierCount = $unknownTierSubs.Count
            exportSubscriptionCount = $exportSubscriptions.Count
        }
    }
    
    # Preview what will be done
    Write-Host ""
    Write-Host "========================================"
    Write-Host "FINOPS SETUP - PREVIEW"
    Write-Host "========================================"
    Write-Host "Customer:           $customerName"
    Write-Host "Subscription:       $primarySubId"
    Write-Host "Resource Group:     $resourceGroupName"
    Write-Host "Storage Account:    $storageAccountName"
    Write-Host "Location:           $location"
    Write-Host "Container:          ingestion"
    Write-Host "----------------------------------------"
    Write-Host "Export Subscriptions: $($exportSubscriptions.Count) (Premium/Advanced ONLY)"
    Write-Host "Standard Excluded:    $($standardSubs.Count)"
    Write-Host "========================================"
    Write-Host ""
    Write-Host "SAFETY: This script only CREATES resources if they don't exist."
    Write-Host "        It will NEVER delete any existing resources."
    Write-Host "        Standard tier subscriptions are EXCLUDED from cost exports."
    Write-Host ""
    
    # Step 0: Register required resource providers on STORAGE ACCOUNT subscription
    # This is CRITICAL - exports can only be delivered to a subscription that has CostManagementExports registered
    Write-Host "Step 0: Registering resource providers on STORAGE ACCOUNT subscription $primarySubId..."
    Write-Host "  This is required for cost exports to work - exports are delivered TO this subscription"
    try {
        $token = (Get-AzAccessToken -ResourceUrl "https://management.azure.com").Token
        $headers = @{ "Authorization" = "Bearer $token"; "Content-Type" = "application/json" }
        
        $providersToRegister = @("Microsoft.CostManagement", "Microsoft.CostManagementExports")
        foreach ($providerName in $providersToRegister) {
            Write-Host "  Checking $providerName..."
            $providerUri = "https://management.azure.com/subscriptions/$primarySubId/providers/$providerName`?api-version=2021-04-01"
            $provider = Invoke-RestMethod -Uri $providerUri -Method Get -Headers $headers -ErrorAction SilentlyContinue
            
            if ($provider.registrationState -ne "Registered") {
                Write-Host "  Registering $providerName..."
                $registerUri = "https://management.azure.com/subscriptions/$primarySubId/providers/$providerName/register?api-version=2021-04-01"
                Invoke-RestMethod -Uri $registerUri -Method Post -Headers $headers -ErrorAction SilentlyContinue | Out-Null
                
                # Wait for registration to complete (poll up to 60 seconds)
                Write-Host "  Waiting for $providerName registration to complete..."
                $maxWait = 60
                $waited = 0
                while ($waited -lt $maxWait) {
                    Start-Sleep -Seconds 5
                    $waited += 5
                    $provider = Invoke-RestMethod -Uri $providerUri -Method Get -Headers $headers -ErrorAction SilentlyContinue
                    Write-Host "    Status: $($provider.registrationState) ($waited`s)"
                    if ($provider.registrationState -eq "Registered") {
                        Write-Host "  ✓ $providerName registered successfully"
                        break
                    }
                }
                if ($provider.registrationState -ne "Registered") {
                    Write-Host "  WARNING: $providerName may not be fully registered yet (state: $($provider.registrationState))"
                }
            } else {
                Write-Host "  ✓ $providerName already registered"
            }
        }
    }
    catch {
        Write-Host "  Warning: Could not register providers: $($_.Exception.Message)"
        $results.errors += "Provider registration: $($_.Exception.Message)"
    }
    
    # Step 1: Create Resource Group (SAFE: checks if exists first)
    Write-Host "Step 1: Checking resource group..."
    Write-Host "  Looking for '$resourceGroupName' in subscription $primarySubId"
    
    $rg = Get-AzResourceGroup -Name $resourceGroupName -ErrorAction SilentlyContinue
    if (-not $rg) {
        Write-Host "  Resource group '$resourceGroupName' does not exist - CREATING"
        $rg = New-AzResourceGroup -Name $resourceGroupName -Location $location -ErrorAction Stop
        Write-Host "  ✓ Created resource group"
        $results.resourceGroupCreated = $true
    } else {
        Write-Host "  ✓ Resource group already exists - SKIPPING creation"
        $results.resourceGroupCreated = $false
    }
    
    # Step 2: Create Storage Account (SAFE: checks if exists first)
    Write-Host "Step 2: Checking storage account..."
    Write-Host "  Looking for '$storageAccountName' in RG '$resourceGroupName'"
    
    $sa = $null
    $storageAccountSubId = $primarySubId  # Track which subscription the SA is actually in
    
    try {
        $sa = Get-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName -ErrorAction Stop
        Write-Host "  ✓ Found storage account in target subscription"
        $storageAccountSubId = $primarySubId
    } catch {
        Write-Host "  Storage account not found in target subscription"
        
        # Check if it exists anywhere (might have been created in wrong subscription on previous run)
        Write-Host "  Searching across all accessible subscriptions..."
        $allSubs = Get-AzSubscription -TenantId $tenantId -ErrorAction SilentlyContinue
        foreach ($sub in $allSubs) {
            if ($sub.Id -eq $primarySubId) { continue } # Already checked
            try {
                Set-AzContext -Subscription $sub.Id -Tenant $tenantId -ErrorAction Stop | Out-Null
                $found = Get-AzStorageAccount -ErrorAction SilentlyContinue | Where-Object { $_.StorageAccountName -eq $storageAccountName }
                if ($found) {
                    Write-Host "  !!! FOUND storage account in DIFFERENT subscription: $($sub.Name) ($($sub.Id)) !!!"
                    Write-Host "  The storage account exists but in the wrong subscription."
                    Write-Host "  Using existing storage account location..."
                    $sa = $found
                    $storageAccountSubId = $sub.Id
                    $resourceGroupName = $sa.ResourceGroupName
                    $results.resourceGroupName = $resourceGroupName
                    $results.storageAccountSubscriptionId = $storageAccountSubId
                    break
                }
            } catch { }
        }
        
        # Switch back to target subscription for creation if not found elsewhere
        if (-not $sa) {
            Write-Host "  Storage account not found anywhere - will create in target subscription"
            Set-AzContext -Subscription $primarySubId -Tenant $tenantId -ErrorAction Stop | Out-Null
            $storageAccountSubId = $primarySubId
        }
    }
    
    # IMPORTANT: Register providers on the STORAGE ACCOUNT subscription (where exports are delivered TO)
    if ($storageAccountSubId -ne $primarySubId -or $sa) {
        Write-Host "  Ensuring providers are registered on storage account subscription: $storageAccountSubId"
        try {
            Set-AzContext -Subscription $storageAccountSubId -Tenant $tenantId -ErrorAction Stop | Out-Null
            $token = (Get-AzAccessToken -ResourceUrl "https://management.azure.com").Token
            $headers = @{ "Authorization" = "Bearer $token"; "Content-Type" = "application/json" }
            
            $providersToRegister = @("Microsoft.CostManagement", "Microsoft.CostManagementExports")
            foreach ($providerName in $providersToRegister) {
                $providerUri = "https://management.azure.com/subscriptions/$storageAccountSubId/providers/$providerName`?api-version=2021-04-01"
                $provider = Invoke-RestMethod -Uri $providerUri -Method Get -Headers $headers -ErrorAction SilentlyContinue
                
                if ($provider.registrationState -ne "Registered") {
                    Write-Host "    Registering $providerName on SA subscription..."
                    $registerUri = "https://management.azure.com/subscriptions/$storageAccountSubId/providers/$providerName/register?api-version=2021-04-01"
                    Invoke-RestMethod -Uri $registerUri -Method Post -Headers $headers -ErrorAction SilentlyContinue | Out-Null
                    
                    # Wait for registration
                    $maxWait = 60
                    $waited = 0
                    while ($waited -lt $maxWait) {
                        Start-Sleep -Seconds 5
                        $waited += 5
                        $provider = Invoke-RestMethod -Uri $providerUri -Method Get -Headers $headers -ErrorAction SilentlyContinue
                        Write-Host "      Status: $($provider.registrationState) ($waited`s)"
                        if ($provider.registrationState -eq "Registered") { break }
                    }
                } else {
                    Write-Host "    $providerName already registered on SA subscription"
                }
            }
        } catch {
            Write-Host "    Warning: Could not register providers on SA subscription: $($_.Exception.Message)"
        }
    }
    if (-not $sa) {
        Write-Host "  Storage account '$storageAccountName' does not exist - CREATING"
        Write-Host "  Settings: Location=$location, SKU=Standard_LRS, Kind=StorageV2, HNS=true"
        $sa = New-AzStorageAccount -ResourceGroupName $resourceGroupName -Name $storageAccountName -Location $location `
            -SkuName "Standard_LRS" -Kind "StorageV2" -EnableHierarchicalNamespace $true `
            -AllowBlobPublicAccess $false -MinimumTlsVersion "TLS1_2" -ErrorAction Stop
        Write-Host "  ✓ Created storage account (Data Lake Gen2)"
        $results.storageAccountCreated = $true
        Start-Sleep -Seconds 10
    } else {
        Write-Host "  ✓ Storage account already exists - SKIPPING creation"
        $results.storageAccountCreated = $false
    }
    
    # Step 3: Get storage context
    Write-Host "Step 3: Getting storage context..."
    $keys = Get-AzStorageAccountKey -ResourceGroupName $resourceGroupName -Name $storageAccountName -ErrorAction Stop
    if (-not $keys -or $keys.Count -eq 0) { throw "Failed to get storage account keys" }
    $ctx = New-AzStorageContext -StorageAccountName $storageAccountName -StorageAccountKey $keys[0].Value
    Write-Host "Got storage context"
    
    # Step 4: Create container (SAFE: checks if exists first)
    Write-Host "Step 4: Checking ingestion container..."
    $container = Get-AzStorageContainer -Name "ingestion" -Context $ctx -ErrorAction SilentlyContinue
    if (-not $container) {
        Write-Host "  Container 'ingestion' does not exist - CREATING"
        $container = New-AzStorageContainer -Name "ingestion" -Context $ctx -Permission Off -ErrorAction Stop
        Write-Host "  ✓ Created container (private access)"
        $results.containerCreated = $true
    } else {
        Write-Host "  ✓ Container already exists - SKIPPING creation"
        $results.containerCreated = $false
    }
    
    # Step 5: Generate SAS Token
    Write-Host "Step 5: Generating SAS token..."
    $sasStartTime = (Get-Date).ToUniversalTime()
    $sasExpiryTime = $sasStartTime.AddDays($sasExpiryDays)
    
    $sasToken = New-AzStorageAccountSASToken -Context $ctx -Service Blob -ResourceType Container,Object `
        -Permission "rl" -StartTime $sasStartTime -ExpiryTime $sasExpiryTime -Protocol HttpsOnly
    
    if ($sasToken.StartsWith("?")) { $sasToken = $sasToken.Substring(1) }
    Write-Host "Generated SAS token, expires: $sasExpiryTime"
    
    $results.sasToken = $sasToken
    $results.sasExpiry = $sasExpiryTime.ToString("o")
    $results.storageUrl = "https://$storageAccountName.dfs.core.windows.net/ingestion"
    
    # Step 6: Save to portal
    Write-Host "Step 6: Saving configuration to portal..."
    try {
        $saveBody = @{ finOpsStorageAccount = $storageAccountName; finOpsContainer = "ingestion" } | ConvertTo-Json
        Invoke-RestMethod -Uri "$apiBase/customers/$customerId" -Method Put -Body $saveBody -ContentType "application/json" | Out-Null
        
        $sasBody = @{ sasToken = $sasToken } | ConvertTo-Json
        Invoke-RestMethod -Uri "$apiBase/customers/$customerId/finops/sas" -Method Put -Body $sasBody -ContentType "application/json" | Out-Null
        Write-Host "Saved configuration and SAS token to portal"
    }
    catch {
        Write-Host "Warning: Could not save to portal: $($_.Exception.Message)"
        $results.errors += "Save to portal: $($_.Exception.Message)"
    }
    
    # Step 7: Create Cost Management Exports
    Write-Host "Step 7: Creating cost exports..."
    
    $storageResourceId = "/subscriptions/$primarySubId/resourceGroups/$resourceGroupName/providers/Microsoft.Storage/storageAccounts/$storageAccountName"
    $token = (Get-AzAccessToken -ResourceUrl "https://management.azure.com").Token
    $headers = @{ "Authorization" = "Bearer $token"; "Content-Type" = "application/json" }
    
    foreach ($sub in $exportSubscriptions) {
        $subId = if ($sub.SubscriptionId) { $sub.SubscriptionId } else { $sub.subscriptionId }
        $subName = if ($sub.SubscriptionName) { $sub.SubscriptionName } else { $sub.subscriptionName }
        if (-not $subId) { continue }
        
        Write-Host "Processing: $subName ($subId)"
        $results.subscriptionsProcessed += @{ subscriptionId = $subId; subscriptionName = $subName }
        
        # Step 7a: Register required resource providers on this subscription
        Write-Host "  Registering resource providers..."
        $providersForExport = @("Microsoft.CostManagement", "Microsoft.CostManagementExports")
        foreach ($provName in $providersForExport) {
            try {
                $providerUri = "https://management.azure.com/subscriptions/$subId/providers/$provName`?api-version=2021-04-01"
                $provider = Invoke-RestMethod -Uri $providerUri -Method Get -Headers $headers -ErrorAction SilentlyContinue
                
                if ($provider.registrationState -ne "Registered") {
                    Write-Host "    Registering $provName..."
                    $registerUri = "https://management.azure.com/subscriptions/$subId/providers/$provName/register?api-version=2021-04-01"
                    Invoke-RestMethod -Uri $registerUri -Method Post -Headers $headers -ErrorAction SilentlyContinue | Out-Null
                } else {
                    Write-Host "    $provName already registered"
                }
            }
            catch {
                Write-Host "    Warning: Could not register $provName`: $($_.Exception.Message)"
            }
        }
        # Wait for registrations to propagate
        Start-Sleep -Seconds 5
        
        # Step 7b: Check if FOCUS exports are supported by querying available export types
        Write-Host "  Checking FOCUS export support..."
        $focusSupported = $false
        try {
            # Try to get export metadata to check supported types
            $metadataUri = "https://management.azure.com/subscriptions/$subId/providers/Microsoft.CostManagement/exports?api-version=2025-03-01"
            $existingExports = Invoke-RestMethod -Uri $metadataUri -Method Get -Headers $headers -ErrorAction SilentlyContinue
            # If we get here without error with 2025-03-01 API, FOCUS is likely supported
            $focusSupported = $true
            Write-Host "  FOCUS exports supported (API 2025-03-01 available)"
        }
        catch {
            Write-Host "  FOCUS may not be supported, will try anyway: $($_.Exception.Message)"
            $focusSupported = $true  # Try anyway
        }
        
        # Daily Export - FocusCost with Parquet format (SAFE: creates or updates, never deletes)
        $dailyExportName = "tieva-daily-focus-cost"
        $dailyRetries = 0
        $dailyMaxRetries = 3
        $dailySuccess = $false
        
        Write-Host "  Creating/updating daily FOCUS export..."
        while (-not $dailySuccess -and $dailyRetries -lt $dailyMaxRetries) {
            try {
                $exportUri = "https://management.azure.com/subscriptions/$subId/providers/Microsoft.CostManagement/exports/${dailyExportName}?api-version=2025-03-01"
                
                $exportBody = @{
                    properties = @{
                        schedule = @{
                            status = "Active"
                            recurrence = "Daily"
                            recurrencePeriod = @{
                                from = (Get-Date -Day 1 -Hour 0 -Minute 0 -Second 0).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                                to = (Get-Date -Day 1 -Hour 0 -Minute 0 -Second 0).AddYears(1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                            }
                        }
                        format = "Parquet"
                        partitionData = $true
                        deliveryInfo = @{
                            destination = @{
                                resourceId = $storageResourceId
                                container = "ingestion"
                                rootFolderPath = "focus-exports"
                            }
                        }
                        definition = @{
                            type = "FocusCost"
                            timeframe = "MonthToDate"
                            dataSet = @{
                                granularity = "Daily"
                            }
                        }
                    }
                } | ConvertTo-Json -Depth 10
                
                Write-Host "  Creating daily FOCUS export..."
                $response = Invoke-RestMethod -Uri $exportUri -Method Put -Headers $headers -Body $exportBody -ErrorAction Stop
                Write-Host "  Created daily FOCUS export successfully"
                $results.exportsCreated += @{ subscriptionId = $subId; subscriptionName = $subName; type = "Daily FOCUS" }
                $dailySuccess = $true
            }
            catch {
                $errMsg = $_.Exception.Message
                $errDetail = ""
                try {
                    if ($_.ErrorDetails.Message) {
                        $errJson = $_.ErrorDetails.Message | ConvertFrom-Json
                        $errDetail = $errJson.error.message
                    }
                } catch { }
                
                $fullError = if ($errDetail) { "$errMsg - $errDetail" } else { $errMsg }
                
                if ($fullError -like "*already exists*" -or $fullError -like "*Conflict*" -or $fullError -like "*ExportNameExists*") {
                    Write-Host "  Daily export already exists"
                    $results.exportsCreated += @{ subscriptionId = $subId; subscriptionName = $subName; type = "Daily (existing)" }
                    $dailySuccess = $true
                } elseif ($fullError -like "*RP Not Registered*" -and $dailyRetries -lt ($dailyMaxRetries - 1)) {
                    $dailyRetries++
                    Write-Host "  Daily export failed (RP not ready), retrying in 10s... (attempt $dailyRetries/$dailyMaxRetries)"
                    Start-Sleep -Seconds 10
                } else {
                    Write-Host "  Daily FOCUS export failed: $fullError"
                    $results.errors += "Daily export for $subName : $fullError"
                    $dailySuccess = $true  # Exit loop, we've logged the error
                }
            }
        }
        
        # Monthly Export - FocusCost with Parquet format (last month's costs)
        $monthlyExportName = "tieva-monthly-focus-cost"
        $monthlyRetries = 0
        $monthlyMaxRetries = 3
        $monthlySuccess = $false
        
        while (-not $monthlySuccess -and $monthlyRetries -lt $monthlyMaxRetries) {
            try {
                $exportUri = "https://management.azure.com/subscriptions/$subId/providers/Microsoft.CostManagement/exports/${monthlyExportName}?api-version=2025-03-01"
                
                $exportBody = @{
                    properties = @{
                        schedule = @{
                            status = "Active"
                            recurrence = "Monthly"
                            recurrencePeriod = @{
                                from = (Get-Date -Day 1 -Hour 0 -Minute 0 -Second 0).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                                to = (Get-Date -Day 1 -Hour 0 -Minute 0 -Second 0).AddYears(1).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
                            }
                        }
                        format = "Parquet"
                        partitionData = $true
                        deliveryInfo = @{
                            destination = @{
                                resourceId = $storageResourceId
                                container = "ingestion"
                                rootFolderPath = "focus-exports"
                            }
                        }
                        definition = @{
                            type = "FocusCost"
                            timeframe = "TheLastMonth"
                        }
                    }
                } | ConvertTo-Json -Depth 10
                
                Write-Host "  Creating monthly FOCUS export..."
                $response = Invoke-RestMethod -Uri $exportUri -Method Put -Headers $headers -Body $exportBody -ErrorAction Stop
                Write-Host "  Created monthly FOCUS export successfully"
                $results.exportsCreated += @{ subscriptionId = $subId; subscriptionName = $subName; type = "Monthly FOCUS" }
                $monthlySuccess = $true
            }
            catch {
                $errMsg = $_.Exception.Message
                $errDetail = ""
                try {
                    if ($_.ErrorDetails.Message) {
                        $errJson = $_.ErrorDetails.Message | ConvertFrom-Json
                        $errDetail = $errJson.error.message
                    }
                } catch { }
                
                $fullError = if ($errDetail) { "$errMsg - $errDetail" } else { $errMsg }
                
                if ($fullError -like "*already exists*" -or $fullError -like "*Conflict*" -or $fullError -like "*ExportNameExists*") {
                    Write-Host "  Monthly export already exists"
                    $results.exportsCreated += @{ subscriptionId = $subId; subscriptionName = $subName; type = "Monthly (existing)" }
                    $monthlySuccess = $true
                } elseif ($fullError -like "*RP Not Registered*" -and $monthlyRetries -lt ($monthlyMaxRetries - 1)) {
                    $monthlyRetries++
                    Write-Host "  Monthly export failed (RP not ready), retrying in 10s... (attempt $monthlyRetries/$monthlyMaxRetries)"
                    Start-Sleep -Seconds 10
                } else {
                    Write-Host "  Monthly FOCUS export failed: $fullError"
                    $results.errors += "Monthly export for $subName : $fullError"
                    $monthlySuccess = $true  # Exit loop, we've logged the error
                }
            }
        }
        
        # Trigger daily export to run now
        try {
            $runUri = "https://management.azure.com/subscriptions/$subId/providers/Microsoft.CostManagement/exports/$dailyExportName/run?api-version=2025-03-01"
            Invoke-RestMethod -Uri $runUri -Method Post -Headers $headers -ErrorAction SilentlyContinue | Out-Null
            Write-Host "  Triggered export run"
        }
        catch {
            Write-Host "  Could not trigger export (will run on schedule)"
        }
    }
    
    $results.status = if ($results.errors.Count -eq 0) { "Success" } else { "CompletedWithWarnings" }
    $results.message = "Storage: $storageAccountName, Exports: $($results.exportsCreated.Count)"
    
    # Add summary to results
    $results.summary = @{
        resourceGroup = if ($results.resourceGroupCreated) { "Created" } else { "Already existed" }
        storageAccount = if ($results.storageAccountCreated) { "Created" } else { "Already existed" }
        container = if ($results.containerCreated) { "Created" } else { "Already existed" }
        sasToken = "Generated"
        sasExpiry = $sasExpiryTime.ToString("o")
        exportsCreated = $results.exportsCreated.Count
        warnings = $results.errors.Count
        safetyNote = "This setup only creates resources - nothing was deleted"
    }
    
    # Summary
    Write-Host ""
    Write-Host "========================================"
    Write-Host "SETUP COMPLETE - SUMMARY"
    Write-Host "========================================"
    Write-Host "Resource Group:  $(if ($results.resourceGroupCreated) { 'CREATED' } else { 'Already existed (skipped)' })"
    Write-Host "Storage Account: $(if ($results.storageAccountCreated) { 'CREATED' } else { 'Already existed (skipped)' })"
    Write-Host "Container:       $(if ($results.containerCreated) { 'CREATED' } else { 'Already existed (skipped)' })"
    Write-Host "SAS Token:       GENERATED (expires $sasExpiryTime)"
    Write-Host "Exports Created: $($results.exportsCreated.Count)"
    Write-Host "Warnings:        $($results.errors.Count)"
    Write-Host "========================================"
    Write-Host ""
    Write-Host "NOTE: This script only CREATES resources, it never DELETES anything."
    
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::OK
        Body = $results | ConvertTo-Json -Depth 10
        ContentType = "application/json"
    })
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    Write-Host "Stack: $($_.ScriptStackTrace)"
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::InternalServerError
        Body = @{ error = $_.Exception.Message; details = $_.ScriptStackTrace } | ConvertTo-Json
        ContentType = "application/json"
    })
}
finally {
    try {
        Remove-AzContext -Name "$contextName-identity" -Force -ErrorAction SilentlyContinue | Out-Null
        Remove-AzContext -Name "$contextName-customer" -Force -ErrorAction SilentlyContinue | Out-Null
    } catch { }
}
