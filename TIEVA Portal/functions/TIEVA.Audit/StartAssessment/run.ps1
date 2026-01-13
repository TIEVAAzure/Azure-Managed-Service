using namespace System.Net

param($Request, $TriggerMetadata)

$contextName = "audit-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
Write-Host "StartAssessment function triggered (Context: $contextName)"

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

Write-Host "Connection ID: $connectionId, Module: $module"

# Valid modules
$validModules = @('NETWORK', 'BACKUP', 'COST', 'IDENTITY', 'POLICY', 'RESOURCE', 'RESERVATION', 'SECURITY', 'PATCH', 'PERFORMANCE', 'COMPLIANCE')
if ($module -notin $validModules) {
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::BadRequest
        Body = @{ error = "Unknown module: $module" } | ConvertTo-Json
        ContentType = "application/json"
    })
    return
}

# SQL helpers
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

try {
    # Connect with Managed Identity
    Write-Host "Connecting with Managed Identity..."
    Connect-AzAccount -Identity -ContextName "$contextName-identity" -Force -WarningAction SilentlyContinue | Out-Null
    Set-AzContext -Context (Get-AzContext -Name "$contextName-identity") | Out-Null
    
    $sqlConn = Get-SqlConnection -ContextName "$contextName-identity"
    Write-Host "SQL connected"
    
    # Get connection details
    $connResult = Invoke-SqlQuery -Connection $sqlConn -Query @"
        SELECT c.Id, c.CustomerId, c.TenantId, c.ClientId, c.SecretKeyVaultRef, cust.Name as CustomerName
        FROM AzureConnections c
        INNER JOIN Customers cust ON c.CustomerId = cust.Id
        WHERE c.Id = @ConnectionId AND c.IsActive = 1
"@ -Parameters @{ ConnectionId = $connectionId }
    
    if ($connResult.Count -eq 0) {
        throw "Connection not found: $connectionId"
    }
    
    $conn = $connResult[0]
    Write-Host "Customer: $($conn.CustomerName)"
    
    # Get module ID
    $moduleResult = Invoke-SqlQuery -Connection $sqlConn -Query "SELECT Id FROM AssessmentModules WHERE Code = @ModuleCode AND IsActive = 1" -Parameters @{ ModuleCode = $module }
    if ($moduleResult.Count -eq 0) {
        throw "Module not found: $module"
    }
    $moduleId = $moduleResult[0].Id
    
    # Get subscriptions
    $subsResult = Invoke-SqlQuery -Connection $sqlConn -Query @"
        SELECT s.SubscriptionId, s.SubscriptionName
        FROM CustomerSubscriptions s
        INNER JOIN ServiceTiers t ON s.TierId = t.Id
        INNER JOIN TierModules tm ON tm.TierId = t.Id AND tm.ModuleId = @ModuleId AND tm.IsIncluded = 1
        WHERE s.ConnectionId = @ConnectionId AND s.IsInScope = 1 AND s.TierId IS NOT NULL
"@ -Parameters @{ ConnectionId = $connectionId; ModuleId = $moduleId }
    
    if ($subsResult.Count -eq 0) {
        $sqlConn.Close()
        Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
            StatusCode = [HttpStatusCode]::BadRequest
            Body = @{ error = "No subscriptions configured for $module audit" } | ConvertTo-Json
            ContentType = "application/json"
        })
        return
    }
    
    $subscriptionIds = @($subsResult | ForEach-Object { $_.SubscriptionId })
    Write-Host "Found $($subscriptionIds.Count) subscriptions"
    
    # Create assessment record with Queued status
    $assessmentId = [Guid]::NewGuid().ToString()
    $createdAt = (Get-Date).ToUniversalTime()
    $totalSubscriptions = $subscriptionIds.Count
    
    Invoke-SqlNonQuery -Connection $sqlConn -Query @"
        INSERT INTO Assessments (Id, CustomerId, ConnectionId, Status, CreatedAt, TotalSubscriptions, CompletedSubscriptions)
        VALUES (@Id, @CustomerId, @ConnectionId, @Status, @CreatedAt, @TotalSubscriptions, 0)
"@ -Parameters @{
        Id = $assessmentId
        CustomerId = $conn.CustomerId
        ConnectionId = $connectionId
        Status = "Queued"
        CreatedAt = $createdAt
        TotalSubscriptions = $totalSubscriptions
    }
    Write-Host "Assessment created: $assessmentId with $totalSubscriptions subscriptions"
    
    $sqlConn.Close()
    
    # Queue one message per subscription
    foreach ($subId in $subscriptionIds) {
        $subName = ($subsResult | Where-Object { $_.SubscriptionId -eq $subId }).SubscriptionName
        $queueMessage = @{
            assessmentId = $assessmentId
            connectionId = $connectionId
            customerId = $conn.CustomerId.ToString()
            module = $module
            tenantId = $conn.TenantId
            clientId = $conn.ClientId
            secretKeyVaultRef = $conn.SecretKeyVaultRef
            subscriptionId = $subId
            subscriptionName = $subName
            totalSubscriptions = $totalSubscriptions
        } | ConvertTo-Json -Compress
        
        Push-OutputBinding -Name AssessmentQueue -Value $queueMessage
        Write-Host "Queued subscription: $subName ($subId)"
    }
    
    # Return immediately
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::Accepted
        Body = @{
            assessmentId = $assessmentId
            status = "Queued"
            message = "Assessment queued for processing"
            module = $module
            subscriptionCount = $subscriptionIds.Count
        } | ConvertTo-Json
        ContentType = "application/json"
    })
}
catch {
    Write-Host "Error: $_"
    try { $sqlConn.Close() } catch { }
    
    Push-OutputBinding -Name Response -Value ([HttpResponseContext]@{
        StatusCode = [HttpStatusCode]::InternalServerError
        Body = @{ error = $_.Exception.Message } | ConvertTo-Json
        ContentType = "application/json"
    })
}
finally {
    try { Remove-AzContext -Name "$contextName-identity" -Force -ErrorAction SilentlyContinue | Out-Null } catch { }
}
