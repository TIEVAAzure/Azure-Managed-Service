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
        }
        else {
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

    $authHeader = "LMv1 {0}:{1}:{2}" -f $AccessId, $signature, $epoch

    $uri = "$BaseUri$ResourcePath$QueryString"

    $headers = @{
        "Authorization" = $authHeader
        "Content-Type"  = "application/json"
    }

    Write-Host "LM API: $Method $uri"

    try {
        if ($Method -in @('GET','DELETE')) {
            return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ErrorAction Stop
        }
        else {
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
$lmCompany   = $env:lm_company      # e.g. "tievasandbox"
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
    throw "Env var 'lm_auth' must be in the format '<accessId>:<accessKey>'. Current value: '$lmAuth'"
}
$accessId  = $lmParts[0].Trim()
$accessKey = $lmParts[1].Trim()

Write-Host ("Using LM AccessId prefix: {0}" -f $accessId.Substring(0, [Math]::Min(6, $accessId.Length)))

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
$mcId             = $eventGridEvent.data.MaintenanceConfigurationId

Write-Host "Pre-Maintenance event detected."
Write-Host "MaintenanceRunId (CorrelationId): $maintenanceRunId"
Write-Host "MaintenanceConfigurationId: $mcId"
Write-Host "TenantId (LM_tenant_id): $tenantId"

if ([string]::IsNullOrWhiteSpace($mcId)) {
    Write-Warning "No MaintenanceConfigurationId in event; aborting LM SDTs."
    return
}

# ==========================
# Connect to Azure & read MC Schedule tag
# ==========================
Connect-AzAccount -Identity | Out-Null

# Select the subscription that the MC lives in (for the Get-AzResource call)
$mcParts = $mcId -split '/'
$mcSubId = $mcParts[2]
Select-AzSubscription -SubscriptionId $mcSubId | Out-Null
Write-Host "Selected subscription for MC: $mcSubId"

$mc   = Get-AzResource -ResourceId $mcId -ErrorAction Stop
$tags = $mc.Tags
if (-not $tags) { $tags = @{} }

$scheduleVal = $null
if ($tags.ContainsKey('Schedule')) {
    $scheduleVal = $tags['Schedule']
}

Write-Host "MC tags:"
Write-Host ("  Schedule = {0}" -f $scheduleVal)

if ([string]::IsNullOrWhiteSpace($scheduleVal)) {
    Write-Warning "MC has no 'Schedule' tag; nothing to select for SDT."
    return
}

# ==========================
# Find VMs in ALL subscriptions with matching Schedule tag
# ==========================
$limit = 200   # global cap across all subs
$vms   = @()

$allSubs = Get-AzSubscription
Write-Host ("Found {0} subscriptions in tenant. Scanning for VMs with Schedule = '{1}'." -f $allSubs.Count, $scheduleVal)

foreach ($sub in $allSubs) {
    $subId = $sub.Id
    Write-Host "Scanning subscription: $subId ($($sub.Name))"

    try {
        Select-AzSubscription -SubscriptionId $subId | Out-Null
    }
    catch {
        Write-Warning ("Failed to select subscription {0}: {1}" -f $subId, $_.Exception.Message)
        continue
    }

    try {
        $subVms = Get-AzVM -Status
    }
    catch {
        Write-Warning ("Failed to list VMs in subscription {0}: {1}" -f $subId, $_.Exception.Message)
        continue
    }

    if (-not $subVms) {
        Write-Host "  No VMs in this subscription."
        continue
    }

    $matched = $subVms | Where-Object { $_.Tags['Schedule'] -eq $scheduleVal }

    $count = if ($matched) { $matched.Count } else { 0 }
    Write-Host ("  Matched {0} VMs with Schedule='{1}'." -f $count, $scheduleVal)

    if ($matched) {
        $vms += $matched
    }

    # Optional: stop early if you hit global limit
    if ($vms.Count -ge $limit) {
        Write-Warning ("Global VM limit {0} reached; stopping subscription scan." -f $limit)
        break
    }
}

if (-not $vms -or $vms.Count -eq 0) {
    Write-Warning "No VMs matched Schedule tag across any subscription; no SDTs will be created."
    return
}

$vms = $vms | Sort-Object Id -Unique | Select-Object -First $limit

Write-Host "VMs in this maintenance run (across subscriptions):"
foreach ($vm in $vms) {
    Write-Host ("  - {0} (RG: {1}, Sub: {2})" -f $vm.Name, $vm.ResourceGroupName, $vm.Id.Split('/')[2])
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
$createdCount    = 0
$skippedNoDevice = 0
$failedCount     = 0

foreach ($vm in $vms) {

    $vmName = $vm.Name

    Write-Host "----"
    Write-Host ("Processing VM '{0}' for DeviceSDT..." -f $vmName)

    # 1) Find LM devices for this VM
    $allDevices = @()

    # ---- 1a) Cloud-discovered device: match system.azure.resourcename via basic systemProperties filter ----
    # filter=systemProperties.name:"system.azure.resourcename",systemProperties.value:"<vmName>"
    $filterAzureRaw = 'systemProperties.name:"system.azure.resourcename",systemProperties.value:"{0}"' -f $vmName
    Write-Host ("Azure device filter (raw): {0}" -f $filterAzureRaw)

    $encodedAzure = [System.Net.WebUtility]::UrlEncode($filterAzureRaw)
    $queryAzure   = ("?filter={0}&size=50&v=3" -f $encodedAzure)

    try {
        $azureSearch = Invoke-LMRequest `
            -Method 'GET' `
            -BaseUri $baseUri `
            -ResourcePath "/device/devices" `
            -QueryString $queryAzure `
            -Body $null `
            -AccessId $accessId `
            -AccessKey $accessKey

        if ($azureSearch -and $azureSearch.items) {
            $allDevices += $azureSearch.items
            Write-Host ("Found {0} devices via system.azure.resourcename for VM '{1}'." -f $azureSearch.items.Count, $vmName)
        }
        else {
            Write-Host ("No devices found via system.azure.resourcename for VM '{0}'." -f $vmName)
        }
    }
    catch {
        Write-Warning ("Failed LM search (system.azure.resourcename) for VM '{0}': {1}" -f $vmName, $_.Exception.Message)
    }

    # ---- 1b) Collector-discovered device: fallback by displayName ----
    $filterDisplay   = 'displayName:"{0}"' -f $vmName
    Write-Host ("DisplayName device filter (raw): {0}" -f $filterDisplay)

    $encodedDisplay  = [System.Net.WebUtility]::UrlEncode($filterDisplay)
    $queryDisplay    = ("?filter={0}&size=50&v=3" -f $encodedDisplay)

    try {
        $displaySearch = Invoke-LMRequest `
            -Method 'GET' `
            -BaseUri $baseUri `
            -ResourcePath "/device/devices" `
            -QueryString $queryDisplay `
            -Body $null `
            -AccessId $accessId `
            -AccessKey $accessKey

        if ($displaySearch -and $displaySearch.items) {
            $allDevices += $displaySearch.items
            Write-Host ("Found {0} devices via displayName for VM '{1}'." -f $displaySearch.items.Count, $vmName)
        }
        else {
            Write-Host ("No devices found via displayName for VM '{0}'." -f $vmName)
        }
    }
    catch {
        Write-Warning ("Failed LM search (displayName) for VM '{0}': {1}" -f $vmName, $_.Exception.Message)
    }

    # ---- 1c) De-duplicate by device id ----
    if (-not $allDevices -or $allDevices.Count -eq 0) {
        Write-Warning ("No LogicMonitor devices found for VM '{0}' via system.azure.resourcename or displayName. Skipping SDT for this VM." -f $vmName)
        $skippedNoDevice++
        continue
    }

    $uniqueDevices = $allDevices | Sort-Object id -Unique
    Write-Host ("Total unique LM devices for VM '{0}': {1}" -f $vmName, $uniqueDevices.Count)

    # ==========================
    # 2) Create SDT for each device
    # ==========================
    foreach ($device in $uniqueDevices) {
        $deviceId = $device.id
        Write-Host ("Creating DeviceSDT for VM '{0}' -> deviceId {1}" -f $vmName, $deviceId)

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
            [void](Invoke-LMRequest `
                -Method 'POST' `
                -BaseUri $baseUri `
                -ResourcePath "/sdt/sdts" `
                -QueryString "" `
                -Body $bodyObj `
                -AccessId $accessId `
                -AccessKey $accessKey)

            Write-Host ("Created DeviceSDT for VM '{0}' (deviceId={1})." -f $vmName, $deviceId)
            $createdCount++
        }
        catch {
            Write-Warning ("Failed to create DeviceSDT for VM '{0}' (deviceId={1}): {2}" -f $vmName, $deviceId, $_.Exception.Message)
            $failedCount++
            continue
        }
    }
}

Write-Host "=========================="
Write-Host ("DeviceSDT summary: created={0}, skippedNoDevice={1}, failed={2}" -f $createdCount, $skippedNoDevice, $failedCount)
Write-Host "=========================="
