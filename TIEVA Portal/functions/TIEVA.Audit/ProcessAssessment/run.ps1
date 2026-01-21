param($QueueItem, $TriggerMetadata)

# Generate unique context name for this invocation
$contextName = "audit-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Write-Host "ProcessAssessment triggered (Context: $contextName)"
Write-Host "QueueItem type: $($QueueItem.GetType().FullName)"
Write-Host "QueueItem raw: $QueueItem"

Disable-AzContextAutosave -Scope Process -ErrorAction SilentlyContinue | Out-Null

# Parse queue message - handle both string and object
try {
    if ($QueueItem -is [string]) {
        Write-Host "Parsing as string"
        $message = $QueueItem | ConvertFrom-Json
    } else {
        Write-Host "Using as object"
        $message = $QueueItem
    }
} catch {
    Write-Host "Parse error: $_"
    throw "Failed to parse queue message: $_"
}

Write-Host "Message parsed successfully"

$assessmentId = $message.assessmentId
$connectionId = $message.connectionId
$customerId = $message.customerId
$module = $message.module
$tenantId = $message.tenantId
$clientId = $message.clientId
$secretKeyVaultRef = $message.secretKeyVaultRef
$subscriptionId = $message.subscriptionId
$subscriptionName = $message.subscriptionName
$totalSubscriptions = $message.totalSubscriptions

Write-Host "Processing Assessment: $assessmentId"
Write-Host "Module: $module"
Write-Host "Subscription: $subscriptionName ($subscriptionId)"
Write-Host "Total Subscriptions: $totalSubscriptions"

# Script mapping
$scriptMap = @{
    'NETWORK'     = 'NetworkAudit.ps1'
    'BACKUP'      = 'BackupAudit.ps1'
    'COST'        = 'CostManagementAudit.ps1'
    'IDENTITY'    = 'IdentityAudit.ps1'
    'POLICY'      = 'PolicyAudit.ps1'
    'RESOURCE'    = 'ResourceAudit.ps1'
    'RESERVATION' = 'ReservationAudit.ps1'
    'SECURITY'    = 'SecurityAudit.ps1'
    'PATCH'       = 'PatchAudit.ps1'
    'PERFORMANCE' = 'PerformanceAudit.ps1'
    'COMPLIANCE'  = 'ComplianceAudit.ps1'
}

$outputFileMap = @{
    'NETWORK'     = 'Network_Audit.xlsx'
    'BACKUP'      = 'Backup_Audit.xlsx'
    'COST'        = 'Cost_Management_Audit.xlsx'
    'IDENTITY'    = 'Identity_Audit.xlsx'
    'POLICY'      = 'Policy_Audit.xlsx'
    'RESOURCE'    = 'Resource_Audit.xlsx'
    'RESERVATION' = 'Reservation_Audit.xlsx'
    'SECURITY'    = 'Security_Audit.xlsx'
    'PATCH'       = 'Patch_Audit.xlsx'
    'PERFORMANCE' = 'Performance_Audit.xlsx'
    'COMPLIANCE'  = 'Compliance_Audit.xlsx'
}

$scriptName = $scriptMap[$module]

# SQL Database access
$sqlServer = "sql-tievaPortal-3234.database.windows.net"
$sqlDatabase = "TievaPortal"

function Get-SqlConnection {
    param([string]$ContextName)
    $token = Get-AzAccessToken -ResourceUrl "https://database.windows.net/" -DefaultProfile (Get-AzContext -Name $ContextName)
    $connectionString = "Server=tcp:$sqlServer,1433;Database=$sqlDatabase;Encrypt=True;TrustServerCertificate=False;"
    $connection = New-Object System.Data.SqlClient.SqlConnection($connectionString)
    $connection.AccessToken = $token.Token
    $connection.Open()
    return $connection
}

function Invoke-SqlQuery {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Query, [hashtable]$Parameters = @{})
    $command = $Connection.CreateCommand()
    $command.CommandText = $Query
    $command.CommandTimeout = 60
    foreach ($key in $Parameters.Keys) {
        $param = $command.CreateParameter()
        $param.ParameterName = "@$key"
        $param.Value = if ($null -eq $Parameters[$key]) { [DBNull]::Value } else { $Parameters[$key] }
        $command.Parameters.Add($param) | Out-Null
    }
    $reader = $command.ExecuteReader()
    $results = @()
    while ($reader.Read()) {
        $row = @{}
        for ($i = 0; $i -lt $reader.FieldCount; $i++) {
            $row[$reader.GetName($i)] = if ($reader.IsDBNull($i)) { $null } else { $reader.GetValue($i) }
        }
        $results += [PSCustomObject]$row
    }
    $reader.Close()
    return $results
}

function Invoke-SqlNonQuery {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Query, [hashtable]$Parameters = @{})
    $command = $Connection.CreateCommand()
    $command.CommandText = $Query
    $command.CommandTimeout = 60
    foreach ($key in $Parameters.Keys) {
        $param = $command.CreateParameter()
        $param.ParameterName = "@$key"
        $param.Value = if ($null -eq $Parameters[$key]) { [DBNull]::Value } else { $Parameters[$key] }
        $command.Parameters.Add($param) | Out-Null
    }
    return $command.ExecuteNonQuery()
}

function Invoke-SqlScalar {
    param([System.Data.SqlClient.SqlConnection]$Connection, [string]$Query, [hashtable]$Parameters = @{})
    $command = $Connection.CreateCommand()
    $command.CommandText = $Query
    $command.CommandTimeout = 60
    foreach ($key in $Parameters.Keys) {
        $param = $command.CreateParameter()
        $param.ParameterName = "@$key"
        $param.Value = if ($null -eq $Parameters[$key]) { [DBNull]::Value } else { $Parameters[$key] }
        $command.Parameters.Add($param) | Out-Null
    }
    return $command.ExecuteScalar()
}

function Get-FindingHash {
    param([string]$ResourceId, [string]$ResourceName, [string]$FindingText, [string]$Category)
    $input = "$ResourceId|$ResourceName|$FindingText|$Category".ToLowerInvariant()
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($input))
    return [Convert]::ToBase64String($bytes).Substring(0, 16)
}

try {
    # Connect with Managed Identity
    Write-Host "Connecting with Managed Identity..."
    Connect-AzAccount -Identity -ContextName "$contextName-identity" -Force -WarningAction SilentlyContinue | Out-Null
    Set-AzContext -Context (Get-AzContext -Name "$contextName-identity") | Out-Null
    
    # Connect to SQL
    $sqlConn = Get-SqlConnection -ContextName "$contextName-identity"
    
    # Validate assessment exists before processing
    Write-Host "Validating assessment exists..."
    $assessmentCheck = Invoke-SqlQuery -Connection $sqlConn -Query "SELECT Id, Status FROM Assessments WHERE Id = @Id" -Parameters @{ Id = $assessmentId }
    if (-not $assessmentCheck -or $assessmentCheck.Count -eq 0) {
        Write-Host "Assessment $assessmentId does not exist in database. Message will be consumed without processing."
        $sqlConn.Close()
        return
    }
    Write-Host "Assessment validated: Status = $($assessmentCheck[0].Status)"
    
    # Update status to Running (only if still Queued)
    $startedAt = (Get-Date).ToUniversalTime()
    Invoke-SqlNonQuery -Connection $sqlConn -Query "UPDATE Assessments SET Status = 'Running', StartedAt = COALESCE(StartedAt, @StartedAt) WHERE Id = @Id AND Status IN ('Queued', 'Running')" -Parameters @{
        Id = $assessmentId
        StartedAt = $startedAt
    }
    Write-Host "Assessment status set to Running"
    
    # Get secret from Key Vault
    Write-Host "Retrieving secret from Key Vault..."
    $secretName = if ($secretKeyVaultRef) { $secretKeyVaultRef } else { "sp-$connectionId" }
    
    try {
        $secret = Get-AzKeyVaultSecret -VaultName "kv-tievaPortal-874" -Name $secretName -AsPlainText -ErrorAction Stop
        Write-Host "Secret retrieved successfully"
    }
    catch {
        Write-Host "Key Vault error: $_"
        throw "Failed to retrieve secret '$secretName' from Key Vault: $_"
    }
    
    if (-not $secret) {
        throw "Secret '$secretName' is empty or null"
    }
    
    # Connect to customer tenant
    Write-Host "Connecting to customer tenant..."
    $secureSecret = ConvertTo-SecureString $secret -AsPlainText -Force
    $credential = New-Object System.Management.Automation.PSCredential($clientId, $secureSecret)
    Connect-AzAccount -ServicePrincipal -Credential $credential -Tenant $tenantId -ContextName "$contextName-customer" -Force -ErrorAction Stop | Out-Null
    Set-AzContext -Context (Get-AzContext -Name "$contextName-customer") | Out-Null
    Write-Host "Connected to tenant $tenantId"
    
    # Create output directory (unique per subscription to avoid conflicts)
    $outPath = Join-Path $env:TEMP "$assessmentId-$subscriptionId"
    New-Item -ItemType Directory -Path $outPath -Force | Out-Null
    
    # Run audit script for this single subscription
    $scriptRoot = Join-Path $PSScriptRoot "..\Scripts"
    $scriptPath = Join-Path $scriptRoot $scriptName
    
    Write-Host "Running $module audit for subscription $subscriptionName..."
    $moduleStartedAt = (Get-Date).ToUniversalTime()
    $subscriptionStatus = "Completed"
    
    try {
        # Pass single subscription as array (audit scripts expect array)
        & $scriptPath -SubscriptionIds @($subscriptionId) -OutPath $outPath
        Write-Host "$module audit completed for $subscriptionName"
    }
    catch {
        Write-Host "$module audit failed for $subscriptionName : $_"
        $subscriptionStatus = "Failed"
    }
    
    $completedAt = (Get-Date).ToUniversalTime()
    
    # Parse and store findings
    $expectedFile = $outputFileMap[$module]
    $outputFile = Join-Path $outPath $expectedFile
    $blobPath = $null
    $findingsCount = 0
    $highCount = 0
    $mediumCount = 0
    $lowCount = 0
    
    if (Test-Path $outputFile) {
        Write-Host "Uploading to Blob Storage..."
        Set-AzContext -Context (Get-AzContext -Name "$contextName-identity") | Out-Null
        
        $storageAccount = "sttievaaudit"
        $container = "audit-results"
        # Include subscription ID in blob path
        $blobPath = "$connectionId/$assessmentId/$subscriptionId/$expectedFile"
        
        $ctx = New-AzStorageContext -StorageAccountName $storageAccount -UseConnectedAccount
        Set-AzStorageBlobContent -File $outputFile -Container $container -Blob $blobPath -Context $ctx -Force | Out-Null
        Write-Host "Uploaded: $blobPath"
        
        # Parse findings from Excel
        Write-Host "Parsing findings from Excel..."
        try {
            if (-not (Get-Module -Name ImportExcel -ErrorAction SilentlyContinue)) {
                Import-Module ImportExcel -ErrorAction Stop
            }
            
            $findings = Import-Excel -Path $outputFile -WorksheetName "Findings" -ErrorAction Stop
            
            if ($findings -and $findings.Count -gt 0) {
                Write-Host "Found $($findings.Count) findings to parse"
                
                # Get existing CustomerFindings for tracking new vs recurring
                $existingCfQuery = "SELECT Hash, Id, FirstSeenAt, OccurrenceCount FROM CustomerFindings WHERE CustomerId = @CustomerId AND ModuleCode = @ModuleCode"
                $existingCf = Invoke-SqlQuery -Connection $sqlConn -Query $existingCfQuery -Parameters @{
                    CustomerId = $customerId
                    ModuleCode = $module
                }
                $cfByHash = @{}
                if ($existingCf) {
                    foreach ($cf in $existingCf) {
                        if ($cf -and $cf.Hash) {
                            $cfByHash[$cf.Hash] = $cf
                        }
                    }
                }
                
                $seenHashes = @{}
                $newCount = 0
                $recurringCount = 0
                
                foreach ($finding in $findings) {
                    $severity = $finding.Severity
                    if (-not $severity -or $severity -eq "Info") { continue }
                    
                    $resourceId = $finding.ResourceId
                    $resourceName = $finding.ResourceName
                    $findingText = $finding.Detail
                    $category = $finding.Category
                    
                    $hash = Get-FindingHash -ResourceId $resourceId -ResourceName $resourceName -FindingText $findingText -Category $category
                    
                    $isRecurring = $cfByHash.ContainsKey($hash) -or $seenHashes.ContainsKey($hash)
                    $changeStatus = if ($isRecurring) { "Recurring" } else { "New" }
                    $firstSeenAt = if ($cfByHash.ContainsKey($hash)) { $cfByHash[$hash].FirstSeenAt } else { $startedAt }
                    $occurrenceCount = if ($cfByHash.ContainsKey($hash)) { $cfByHash[$hash].OccurrenceCount + 1 } else { 1 }
                    
                    # Insert finding
                    $findingId = [Guid]::NewGuid().ToString()
                    $insertFindingQuery = @"
                        INSERT INTO Findings (Id, AssessmentId, ModuleCode, SubscriptionId, Severity, Category, ResourceType, ResourceName, ResourceId, Finding, Recommendation, Status, ChangeStatus, Hash, FirstSeenAt, LastSeenAt, OccurrenceCount)
                        VALUES (@Id, @AssessmentId, @ModuleCode, @SubscriptionId, @Severity, @Category, @ResourceType, @ResourceName, @ResourceId, @Finding, @Recommendation, @Status, @ChangeStatus, @Hash, @FirstSeenAt, @LastSeenAt, @OccurrenceCount)
"@
                    Invoke-SqlNonQuery -Connection $sqlConn -Query $insertFindingQuery -Parameters @{
                        Id = $findingId
                        AssessmentId = $assessmentId
                        ModuleCode = $module
                        SubscriptionId = $subscriptionId
                        Severity = $severity
                        Category = $category
                        ResourceType = $finding.ResourceType
                        ResourceName = $resourceName
                        ResourceId = $resourceId
                        Finding = $findingText
                        Recommendation = $finding.Recommendation
                        Status = "Open"
                        ChangeStatus = $changeStatus
                        Hash = $hash
                        FirstSeenAt = $firstSeenAt
                        LastSeenAt = $startedAt
                        OccurrenceCount = $occurrenceCount
                    }
                    
                    # Update or create CustomerFinding
                    if ($cfByHash.ContainsKey($hash)) {
                        Invoke-SqlNonQuery -Connection $sqlConn -Query "UPDATE CustomerFindings SET LastSeenAt = @LastSeenAt, OccurrenceCount = OccurrenceCount + 1, LastAssessmentId = @LastAssessmentId, Status = 'Open', ResolvedAt = NULL WHERE Id = @Id" -Parameters @{
                            Id = $cfByHash[$hash].Id
                            LastSeenAt = $startedAt
                            LastAssessmentId = $assessmentId
                        }
                        $recurringCount++
                    }
                    elseif (-not $seenHashes.ContainsKey($hash)) {
                        $cfId = [Guid]::NewGuid().ToString()
                        $insertCfQuery = @"
                            INSERT INTO CustomerFindings (Id, CustomerId, ModuleCode, Hash, Severity, Category, ResourceType, ResourceId, Finding, Recommendation, Status, FirstSeenAt, LastSeenAt, OccurrenceCount, LastAssessmentId)
                            VALUES (@Id, @CustomerId, @ModuleCode, @Hash, @Severity, @Category, @ResourceType, @ResourceId, @Finding, @Recommendation, @Status, @FirstSeenAt, @LastSeenAt, @OccurrenceCount, @LastAssessmentId)
"@
                        Invoke-SqlNonQuery -Connection $sqlConn -Query $insertCfQuery -Parameters @{
                            Id = $cfId
                            CustomerId = $customerId
                            ModuleCode = $module
                            Hash = $hash
                            Severity = $severity
                            Category = $category
                            ResourceType = $finding.ResourceType
                            ResourceId = $resourceId
                            Finding = $findingText
                            Recommendation = $finding.Recommendation
                            Status = "Open"
                            FirstSeenAt = $startedAt
                            LastSeenAt = $startedAt
                            OccurrenceCount = 1
                            LastAssessmentId = $assessmentId
                        }
                        $newCount++
                    }
                    
                    $seenHashes[$hash] = $true
                    $findingsCount++
                    
                    switch ($severity.ToLower()) {
                        "high" { $highCount++ }
                        "medium" { $mediumCount++ }
                        "low" { $lowCount++ }
                    }
                }
                
                Write-Host "Parsed $findingsCount findings: $newCount new, $recurringCount recurring"
            }
            else {
                Write-Host "No findings found in Excel"
            }
        }
        catch {
            Write-Host "Error parsing findings: $_"
        }
    }
    
    # Increment CompletedSubscriptions and check if all done
    Write-Host "Updating completion status..."
    $updateResult = Invoke-SqlScalar -Connection $sqlConn -Query @"
        UPDATE Assessments 
        SET CompletedSubscriptions = CompletedSubscriptions + 1
        OUTPUT INSERTED.CompletedSubscriptions
        WHERE Id = @Id
"@ -Parameters @{ Id = $assessmentId }
    
    $completedSubs = [int]$updateResult
    Write-Host "Completed $completedSubs of $totalSubscriptions subscriptions"
    
    # If all subscriptions complete, finalize the assessment
    if ($completedSubs -ge $totalSubscriptions) {
        Write-Host "All subscriptions complete. Finalizing assessment..."
        
        # Aggregate findings from database
        $totals = Invoke-SqlQuery -Connection $sqlConn -Query @"
            SELECT 
                COUNT(*) as Total,
                SUM(CASE WHEN Severity = 'High' THEN 1 ELSE 0 END) as High,
                SUM(CASE WHEN Severity = 'Medium' THEN 1 ELSE 0 END) as Medium,
                SUM(CASE WHEN Severity = 'Low' THEN 1 ELSE 0 END) as Low
            FROM Findings 
            WHERE AssessmentId = @AssessmentId
"@ -Parameters @{ AssessmentId = $assessmentId }
        
        $totalFindings = [int]$totals[0].Total
        $totalHigh = [int]$totals[0].High
        $totalMedium = [int]$totals[0].Medium
        $totalLow = [int]$totals[0].Low
        
        # Calculate score
        $weightedFindings = ($totalHigh * 3.0) + ($totalMedium * 1.5) + ($totalLow * 0.5)
        $score = if ($totalFindings -gt 0) { [math]::Round(100.0 / (1.0 + ($weightedFindings / 20.0)), 0) } else { 100 }
        
        # Mark unseen CustomerFindings as Resolved
        $seenHashesQuery = "SELECT DISTINCT Hash FROM Findings WHERE AssessmentId = @AssessmentId AND ModuleCode = @ModuleCode"
        $seenHashes = Invoke-SqlQuery -Connection $sqlConn -Query $seenHashesQuery -Parameters @{
            AssessmentId = $assessmentId
            ModuleCode = $module
        }
        $hashList = ($seenHashes | ForEach-Object { "'$($_.Hash)'" }) -join ","
        
        if ($hashList) {
            $resolveQuery = "UPDATE CustomerFindings SET Status = 'Resolved', ResolvedAt = @ResolvedAt WHERE CustomerId = @CustomerId AND ModuleCode = @ModuleCode AND Status = 'Open' AND Hash NOT IN ($hashList)"
            Invoke-SqlNonQuery -Connection $sqlConn -Query $resolveQuery -Parameters @{
                CustomerId = $customerId
                ModuleCode = $module
                ResolvedAt = $completedAt
            }
        }
        
        # Update assessment as complete
        Invoke-SqlNonQuery -Connection $sqlConn -Query @"
            UPDATE Assessments 
            SET Status = 'Completed', CompletedAt = @CompletedAt, 
                FindingsTotal = @FindingsTotal, FindingsHigh = @FindingsHigh, 
                FindingsMedium = @FindingsMedium, FindingsLow = @FindingsLow,
                ScoreOverall = @ScoreOverall
            WHERE Id = @Id
"@ -Parameters @{
            Id = $assessmentId
            CompletedAt = $completedAt
            FindingsTotal = $totalFindings
            FindingsHigh = $totalHigh
            FindingsMedium = $totalMedium
            FindingsLow = $totalLow
            ScoreOverall = $score
        }
        
        # Add module result (aggregate)
        $moduleResultId = [Guid]::NewGuid().ToString()
        $moduleScore = if ($totalFindings -gt 0) { [math]::Round(100.0 / (1.0 + ($weightedFindings / 10.0)), 0) } else { 100 }
        
        Invoke-SqlNonQuery -Connection $sqlConn -Query @"
            INSERT INTO AssessmentModuleResults (Id, AssessmentId, ModuleCode, Status, FindingsCount, Score, StartedAt, CompletedAt)
            VALUES (@Id, @AssessmentId, @ModuleCode, @Status, @FindingsCount, @Score, @StartedAt, @CompletedAt)
"@ -Parameters @{
            Id = $moduleResultId
            AssessmentId = $assessmentId
            ModuleCode = $module
            Status = "Completed"
            FindingsCount = $totalFindings
            Score = $moduleScore
            StartedAt = $startedAt
            CompletedAt = $completedAt
        }
        
        Write-Host "Assessment finalized - $totalFindings findings, score: $score"
    }
    
    $sqlConn.Close()
    Write-Host "Subscription $subscriptionName processing complete - $findingsCount findings"
}
catch {
    Write-Host "Error: $_"
    Write-Host $_.ScriptStackTrace
    
    # Try to update status to Failed
    try {
        if ($sqlConn) {
            Invoke-SqlNonQuery -Connection $sqlConn -Query "UPDATE Assessments SET Status = 'Failed', CompletedAt = @CompletedAt WHERE Id = @Id" -Parameters @{
                Id = $assessmentId
                CompletedAt = (Get-Date).ToUniversalTime()
            }
            $sqlConn.Close()
        }
    } catch { }
    
    throw $_
}
finally {
    try {
        Remove-AzContext -Name "$contextName-identity" -Force -ErrorAction SilentlyContinue | Out-Null
        Remove-AzContext -Name "$contextName-customer" -Force -ErrorAction SilentlyContinue | Out-Null
    } catch { }
}
