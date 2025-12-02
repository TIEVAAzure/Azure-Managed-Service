param($eventGridEvent, $TriggerMetadata)

Import-Module Az.Accounts
Import-Module Az.Compute
Import-Module Az.Resources

Write-Host "LogicMonitorSDT (per-VM DeviceSDT) starting..."

# ==========================
# Helper: Invoke LMv1 API
# ==========================
function Invoke-LMRequest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][ValidateSet('GET','POST','PUT','PATCH','DELETE')]
        [string]$Method,

        [Parameter(Mandatory)]
        [string]$BaseUri,       # e.g. https://company.logicmonitor.com/santaba/rest

        [Parameter(Mandatory)]
        [string]$ResourcePath,  # e.g. /device/devices

        [Parameter()]
        [string]$QueryString,   # e.g. "?filter=displayName:\"vm01\"&size=5"

        [Parameter()]
        $Body,                  # will be JSON-ified for POST/PUT/PATCH

        [Parameter(Mandatory)]
        [string]$AccessId,

        [Parameter(Mandatory)]
        [string]$AccessKey
    )

    if (-not $QueryString) { $QueryString = "" }

    $dataToSign = ""
    $bodyJson   = $null

    if ($Method -in @('POST','PUT','PATCH') -and $null -ne $Body) {
        if ($Body -is [string]) {
            $bodyJson = $Body
        } else {
            $bodyJson = $Body | ConvertTo-Json -Depth 10 -Compress
        }
        $dataToSign = $bodyJson
    }

    # Epoch in ms
    $epoch = [string]([DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds())

    # LMv1 signature: httpVerb + epoch + data + resourcePath
    $requestVars = $Method + $epoch + $dataToSign + $ResourcePath

    $encoding = [System.Text.Encoding]::UTF8
    $hmac     = New-Object System.Security.Cryptography.HMACSHA256
    $hmac.Key = $encoding.GetBytes($AccessKey)

    $hashBytes = $hmac.ComputeHash($encoding.GetBytes($requestVars))
    $hashHex   = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })
    $signature = [Convert]::ToBase64String($encoding.GetBytes($hashHex))

    $authHeader = "LMv1 $AccessId:$signature:$epoch"

    $uri = "$BaseUri$ResourcePath$QueryString"

    $headers = @{
        "Authorization" = $authHeader
        "Content-Type"  = "application/json"
    }

    Write-Host "LM API: $Method $uri"

    try {
        if ($Method -in @('GET','DELETE')) {
            return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ErrorAction Stop
        } else {
            return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -Body $bodyJson -ErrorAction Stop
        }
    }
    catch {
        Write-Error "LogicMonitor API call failed: $($_.Exception.Message)"
        throw
    }
}

# ==========================
# Read env vars
# ==========================
$lmAuth      = $env:lm_auth         # Expected format: "<accessId>:<accessKey>"
$lmCompany   = $env:lm_company      # e.g. "tieva"
$lmDomain    = $env:lm_domain_name  # e.g. "logicmonitor.com" or "logicmonitor.eu"
$tenantId    = $env:LM_tenant_id    # For logging only (multi-tenant context)
$durationStr = $env:LM_sdt_minutes  # OPTIONAL, default if blank

if (-not $lmCompany) { throw "Env var 'lm_company' is not set." }
if (-not $lmAuth)    { throw "Env var 'lm_auth' is not set (expected '<accessId>:<accessKey>')." }

if (-not $lmDomain -or $lmDomain.Trim() -eq "") {
    $lmDomain = "logicmonitor.com"
}

$durationMinutes = 0
if (-not [int]::TryParse($durationStr, [ref]$durationMinutes) -or $durationMinutes -le 0) {
    $durationMinutes = 120   # default 2 hours
}

$baseUri = "https://$lmCompany.$lmDomain/santaba/rest"

Write-Host "Using LogicMonitor base URI: $baseUri"
Write-Host "SDT duration (minutes): $durationMinutes"

# Split lm_auth into accessId and accessKey
$lmParts = $lmAuth.Split(":", 2)
if ($lmParts.Count -lt 2) {
    throw "Env var 'lm_auth' must be in the format '<accessId>:<accessKey>'."
}
$accessId  = $lmParts[0]
$accessKey = $lmParts[1]

# ==========================
# Filter event type
# ==========================
$eventType = $eventGridEvent.eventType
Write-Host "EventType: $eventType"

if ($eventType -and $eventType -notlike "*PreMaintenanceEvent*") {
    Write-Host "Not a Pre-Maintenance event. Exiting without creating SDTs."
    return
}

$maintenanceRunId = $eventGridEvent.data.CorrelationId
Write-Host "MaintenanceRunId (CorrelationId): $maintenanceRunId"
Write-Host "TenantId (LM_tenant_id): $tenantId"

# ==========================
# Azure: find VMs in scope
# (same logic as PreSnapshot)
# ==========================
Connect-AzAccount -Identity | Out-Null

$data = $eventGridEvent.data
$mcId = $data.MaintenanceConfigurationId
if ([string]::IsNullOrWhiteSpace($mcId)) {
    Write-Warning "No MaintenanceConfigurationId in event; aborting LM SDTs."
    return
}

Write-Host "MaintenanceConfigurationId from event: $mcId"

# Switch to MC subscription
$mcParts = $mcId -split '/'
$subId   = $mcParts[2]
Select-AzSubscription -SubscriptionId $subId | Out-Null
Write-Host "Selected subscription: $subId"

$mc   = Get-AzResource -ResourceId $mcId -ErrorAction Stop
$tags = $mc.Tags; if (-not $tags) { $tags = @{} }

$groupVal    = $tags.ContainsKey('Group')    ? $tags['Group']    : $null
$scheduleVal = $tags.ContainsKey('Schedule') ? $tags['Schedule'] : $null

Write-Host "MC tags:"
Write-Host ("  Group    = {0}" -f $groupVal)
Write-Host ("  Schedule = {0}" -f $scheduleVal)

if ([string]::IsNullOrWhiteSpace($groupVal) -and [string]::IsNullOrWhiteSpace($scheduleVal)) {
    Write-Warning "MC has no Group/Schedule tags; nothing to select for SDT."
    return
}

$limit = 200

$vms = Get-AzVM -Status

if ($groupVal) {
    $vms = $vms | Where-Object { $_.Tags['Group'] -eq $groupVal }
}
if ($scheduleVal) {
    $vms = $vms | Where-Object { $_.Tags['Schedule'] -eq $scheduleVal }
}

$vms = $vms | Sort-Object Id -Unique | Select-Object -First $limit

if (-not $vms -or $vms.Count -eq 0) {
    Write-Warning "No VMs matched Group/Schedule tags for this Maintenance Configuration; no SDTs will be created."
    return
}

Write-Host "VMs in this maintenance run:"
foreach ($vm in $vms) {
    Write-Host ("  - {0} (RG: {1})" -f $vm.Name, $vm.ResourceGroupName)
}

# ==========================
# SDT window
# ==========================
$nowUtc     = [DateTimeOffset]::UtcNow
$startEpoch = $nowUtc.ToUnixTimeMilliseconds()
$endEpoch   = $nowUtc.AddMinutes($durationMinutes).ToUnixTimeMilliseconds()

Write-Host ("SDT window: {0} for {1} minutes" -f $nowUtc.ToString("u"), $durationMinutes)

# ==========================
# For each VM -> find LM device -> create DeviceSDT
# ==========================
$createdCount = 0
$skippedNoDevice = 0
$failedCount  = 0

foreach ($vm in $vms) {

    $vmName = $vm.Name
    Write-Host "----"
    Write-Host "Processing VM '$vmName' for DeviceSDT..."

    # ---- 1) Find LM device by displayName ----
    # We use /device/devices?filter=displayName:"vmName"&size=5
    $filterValue = 'displayName:"{0}"' -f $vmName
    $encodedFilter = [System.Net.WebUtility]::UrlEncode($filterValue)
    $queryString = "?filter=$encodedFilter&size=5"

    try {
        $deviceSearch = Invoke-LMRequest `
            -Method 'GET' `
            -BaseUri $baseUri `
            -ResourcePath "/device/devices" `
            -QueryString $queryString `
            -Body $null `
            -AccessId $accessId `
            -AccessKey $accessKey
    }
    catch {
        Write-Warning "Failed to search LM device for VM '$vmName': $($_.Exception.Message)"
        $failedCount++
        continue
    }

    if (-not $deviceSearch -or -not $deviceSearch.items -or $deviceSearch.items.Count -eq 0) {
        Write-Warning "No LogicMonitor device found matching VM '$vmName'. Skipping SDT for this VM."
        $skippedNoDevice++
        continue
    }

    if ($deviceSearch.items.Count -gt 1) {
        Write-Warning "Multiple devices found for VM '$vmName'. Using the first match (id=$($deviceSearch.items[0].id))."
    }

    $device = $deviceSearch.items[0]
    $deviceId = $device.id

    Write-Host "Matched VM '$vmName' -> LM deviceId = $deviceId"

    # ---- 2) Create DeviceSDT for this device ----
    $bodyObj = @{
        sdtType       = 1  # one-time
        type          = "DeviceSDT"
        deviceId      = [int]$deviceId
        dataSourceId  = 0  # all datasources
        comment       = "Azure Update Manager Pre-Maintenance run $maintenanceRunId (VM $vmName, tenant $tenantId)"
        startDateTime = [int64]$startEpoch
        endDateTime   = [int64]$endEpoch
    }

    try {
        $sdtResponse = Invoke-LMRequest `
            -Method 'POST' `
            -BaseUri $baseUri `
            -ResourcePath "/sdt/sdts" `
            -QueryString "" `
            -Body $bodyObj `
            -AccessId $accessId `
            -AccessKey $accessKey

        Write-Host "Created DeviceSDT for VM '$vmName' (deviceId=$deviceId)."
        $createdCount++
    }
    catch {
        Write-Warning "Failed to create DeviceSDT for VM '$vmName' (deviceId=$deviceId): $($_.Exception.Message)"
        $failedCount++
        continue
    }
}

Write-Host "=========================="
Write-Host ("DeviceSDT summary: created={0}, skippedNoDevice={1}, failed={2}" -f $createdCount, $skippedNoDevice, $failedCount)
Write-Host "=========================="
