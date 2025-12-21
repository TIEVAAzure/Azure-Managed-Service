using namespace System.Net

param($Request, $TriggerMetadata)

Write-Host "StartAssessment function triggered"

# Get request body
$body = $Request.Body

if (-not $body.connectionId) {
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::BadRequest
        Body = @{ error = "connectionId is required" } | ConvertTo-Json
        ContentType = "application/json"
    })
    return
}

if (-not $body.module) {
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::BadRequest
        Body = @{ error = "module is required (e.g., NETWORK, BACKUP, COST)" } | ConvertTo-Json
        ContentType = "application/json"
    })
    return
}

$connectionId = $body.connectionId
$module = $body.module.ToUpper()

Write-Host "Connection ID: $connectionId"
Write-Host "Module: $module"

# Script mapping
$scriptMap = @{
    'NETWORK'     = 'NetworkAudit.ps1'
    'BACKUP'      = 'BackupAudit.ps1'
    'COST'        = 'CostManagementAudit.ps1'
    'IDENTITY'    = 'IdentityAudit.ps1'
    'POLICY'      = 'PolicyAudit.ps1'
    'RESOURCE'    = 'ResourceAudit.ps1'
    'RESERVATION' = 'ReservationAudit.ps1'
}

# Output file mapping
$outputFileMap = @{
    'NETWORK'     = 'Network_Audit.xlsx'
    'BACKUP'      = 'Backup_Audit.xlsx'
    'COST'        = 'Cost_Management_Audit.xlsx'
    'IDENTITY'    = 'Identity_Audit.xlsx'
    'POLICY'      = 'Policy_Audit.xlsx'
    'RESOURCE'    = 'Resource_Audit.xlsx'
    'RESERVATION' = 'Reservation_Audit.xlsx'
}

$scriptName = $scriptMap[$module]
if (-not $scriptName) {
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::BadRequest
        Body = @{ error = "Unknown module: $module" } | ConvertTo-Json
        ContentType = "application/json"
    })
    return
}

try {
    $apiBase = if ($env:TIEVA_API_URL) { $env:TIEVA_API_URL } else { "https://func-tievaportal-6612.azurewebsites.net/api" }
    
    # Get subscriptions that should be audited for this module
    Write-Host "Getting subscriptions for audit..."
    $auditSubs = Invoke-RestMethod -Uri "$apiBase/connections/$connectionId/audit-subscriptions/$module" -Method Get
    
    if (-not $auditSubs -or $auditSubs.Count -eq 0) {
        # Get more detail about why no subscriptions found
        $allSubs = Invoke-RestMethod -Uri "$apiBase/connections/$connectionId" -Method Get -ErrorAction SilentlyContinue
        $subCount = if ($allSubs.subscriptions) { $allSubs.subscriptions.Count } else { 0 }
        $inScopeCount = if ($allSubs.subscriptions) { ($allSubs.subscriptions | Where-Object { $_.isInScope }).Count } else { 0 }
        $withTierCount = if ($allSubs.subscriptions) { ($allSubs.subscriptions | Where-Object { $_.tierId }).Count } else { 0 }
        
        $errorDetail = @{
            error = "No subscriptions configured for $module audit"
            module = $module
            connectionId = $connectionId
            totalSubscriptions = $subCount
            inScopeSubscriptions = $inScopeCount
            subscriptionsWithTier = $withTierCount
            resolution = if ($subCount -eq 0) {
                "No subscriptions found for this connection. Run 'Sync Subscriptions' first."
            } elseif ($inScopeCount -eq 0) {
                "No subscriptions are marked 'In Scope'. Edit subscriptions and enable 'In Scope' checkbox."
            } elseif ($withTierCount -eq 0) {
                "No subscriptions have a Service Tier assigned. Assign a tier to subscriptions."
            } else {
                "Subscriptions have tiers, but those tiers don't include the $module module. Go to Service Tiers and enable $module for the relevant tier(s)."
            }
        }
        
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
            StatusCode = [HttpStatusCode]::BadRequest
            Body = $errorDetail | ConvertTo-Json -Depth 5
            ContentType = "application/json"
        })
        return
    }
    
    Write-Host "Found $($auditSubs.Count) subscriptions to audit"
    
    # Get connection details
    $connResponse = Invoke-RestMethod -Uri "$apiBase/connections/$connectionId" -Method Get
    
    $tenantId = if ($connResponse.tenantId) { $connResponse.tenantId } else { $connResponse.TenantId }
    $clientId = if ($connResponse.clientId) { $connResponse.clientId } else { $connResponse.ClientId }
    $customerName = if ($connResponse.customerName) { $connResponse.customerName } else { $connResponse.CustomerName }
    
    Write-Host "Tenant: $tenantId, ClientId: $clientId, Customer: $customerName"
    
    # Get secret from Key Vault
    $secretName = "sp-$connectionId"
    Write-Host "Retrieving secret from Key Vault..."
    $secret = Get-AzKeyVaultSecret -VaultName "kv-tievaPortal-874" -Name $secretName -AsPlainText
    
    if (-not $secret) {
        throw "Could not retrieve secret for connection"
    }
    
    # Connect to customer tenant
    Write-Host "Connecting to customer tenant..."
    $secureSecret = ConvertTo-SecureString $secret -AsPlainText -Force
    $credential = New-Object System.Management.Automation.PSCredential($clientId, $secureSecret)
    
    Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $tenantId -ErrorAction Stop | Out-Null
    Write-Host "Connected successfully to tenant $tenantId"
    
    # Extract subscription IDs
    $subscriptionIds = @($auditSubs | ForEach-Object { 
        if ($_.SubscriptionId) { $_.SubscriptionId } else { $_.subscriptionId } 
    })
    
    Write-Host "Subscriptions to audit: $($subscriptionIds -join ', ')"
    
    # Generate assessment ID
    $assessmentId = [Guid]::NewGuid().ToString()
    $startedAt = (Get-Date).ToUniversalTime()
    
    # Create assessment record in database
    Write-Host "Creating assessment record..."
    $assessmentBody = @{
        assessmentId = $assessmentId
        connectionId = $connectionId
        status = "Running"
        startedAt = $startedAt.ToString("o")
    } | ConvertTo-Json
    
    try {
        Invoke-RestMethod -Uri "$apiBase/assessments" -Method Post -Body $assessmentBody -ContentType "application/json" | Out-Null
        Write-Host "Assessment record created: $assessmentId"
    }
    catch {
        Write-Host "Warning: Could not create assessment record: $_"
    }
    
    # Create output directory
    $outPath = Join-Path $env:TEMP $assessmentId
    New-Item -ItemType Directory -Path $outPath -Force | Out-Null
    
    # Script root
    $scriptRoot = Join-Path $PSScriptRoot "..\Scripts"
    $scriptPath = Join-Path $scriptRoot $scriptName
    
    if (-not (Test-Path $scriptPath)) {
        throw "Script not found: $scriptPath"
    }
    
    # Build results
    $results = @{
        assessmentId = $assessmentId
        connectionId = $connectionId
        customerName = $customerName
        module = $module
        tenantId = $tenantId
        subscriptionCount = $auditSubs.Count
        subscriptions = @($auditSubs | ForEach-Object {
            @{
                id = if ($_.Id) { $_.Id } else { $_.id }
                subscriptionId = if ($_.SubscriptionId) { $_.SubscriptionId } else { $_.subscriptionId }
                subscriptionName = if ($_.SubscriptionName) { $_.SubscriptionName } else { $_.subscriptionName }
                tierName = if ($_.TierName) { $_.TierName } else { $_.tierName }
                frequency = if ($_.Frequency) { $_.Frequency } else { $_.frequency }
            }
        })
        status = "Running"
        startedAt = $startedAt.ToString("o")
    }
    
    Write-Host "Running $module audit..."
    $moduleStartedAt = (Get-Date).ToUniversalTime()
    $findingsCount = 0
    
    try {
        # Execute the audit script
        & $scriptPath -SubscriptionIds $subscriptionIds -OutPath $outPath
        
        $results.status = "Completed"
        Write-Host "$module audit completed"
    }
    catch {
        Write-Host "$module audit failed: $_"
        $results.status = "Failed"
        $results.error = $_.Exception.Message
    }
    
    $completedAt = (Get-Date).ToUniversalTime()
    $results.completedAt = $completedAt.ToString("o")
    
    # Upload to Blob Storage
    $expectedFile = $outputFileMap[$module]
    $outputFile = Join-Path $outPath $expectedFile
    $blobPath = $null
    
    if (Test-Path $outputFile) {
        Write-Host "Uploading $expectedFile to Blob Storage..."
        
        # Connect back to TIEVA subscription for blob upload
        Connect-AzAccount -Identity | Out-Null
        
        $storageAccount = "sttievaaudit"
        $container = "audit-results"
        $blobPath = "$connectionId/$assessmentId/$expectedFile"
        
        $ctx = New-AzStorageContext -StorageAccountName $storageAccount -UseConnectedAccount
        Set-AzStorageBlobContent -File $outputFile -Container $container -Blob $blobPath -Context $ctx -Force | Out-Null
        
        $results.blobPath = $blobPath
        $results.outputFile = $expectedFile
        
        Write-Host "Uploaded to blob: $blobPath"
    }
    else {
        Write-Host "Output file not found: $outputFile"
        $results.outputFile = $null
    }
    
    # Update assessment record with results
    Write-Host "Updating assessment record..."
    $updateBody = @{
        status = $results.status
        completedAt = $completedAt.ToString("o")
    } | ConvertTo-Json
    
    try {
        Invoke-RestMethod -Uri "$apiBase/assessments/$assessmentId" -Method Put -Body $updateBody -ContentType "application/json" | Out-Null
    }
    catch {
        Write-Host "Warning: Could not update assessment: $_"
    }
    
    # Add module result
    Write-Host "Adding module result..."
    $moduleResultBody = @{
        moduleCode = $module
        status = $results.status
        blobPath = $blobPath
        findingsCount = $findingsCount
        startedAt = $moduleStartedAt.ToString("o")
        completedAt = $completedAt.ToString("o")
    } | ConvertTo-Json
    
    try {
        Invoke-RestMethod -Uri "$apiBase/assessments/$assessmentId/modules" -Method Post -Body $moduleResultBody -ContentType "application/json" | Out-Null
        Write-Host "Module result added"
    }
    catch {
        Write-Host "Warning: Could not add module result: $_"
    }
    
    # Auto-parse findings from Excel into database
    if ($blobPath -and $results.status -eq "Completed") {
        Write-Host "Parsing findings from Excel..."
        try {
            $parseResult = Invoke-RestMethod -Uri "$apiBase/assessments/$assessmentId/modules/$module/parse" -Method Post -ContentType "application/json"
            Write-Host "Parsed $($parseResult.parsed) findings (High: $($parseResult.high), Medium: $($parseResult.medium), Low: $($parseResult.low)) - Score: $($parseResult.score)%"
            $results.findingsParsed = $parseResult.parsed
            $results.findingsHigh = $parseResult.high
            $results.findingsMedium = $parseResult.medium
            $results.findingsLow = $parseResult.low
            $results.score = $parseResult.score
        }
        catch {
            Write-Host "Warning: Could not parse findings: $_"
        }
    }
    
    Write-Host "Assessment completed"
    
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::OK
        Body = $results | ConvertTo-Json -Depth 10
        ContentType = "application/json"
    })
}
catch {
    Write-Host "Error: $_"
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::InternalServerError
        Body = @{ error = $_.Exception.Message } | ConvertTo-Json
        ContentType = "application/json"
    })
}