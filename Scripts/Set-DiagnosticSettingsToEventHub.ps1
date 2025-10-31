# ==============================================
# Bulk enable Diagnostic Settings -> Event Hub
# - Compatible with Az.Monitor 14.5.0
# - Uses New-AzDiagnosticSetting (-Log/-Metric)
# - UK South only (configurable)
# - Supports single-resource test mode
# - Skips known unsupported resource types (e.g. disks)
# - Probes Get-AzDiagnosticSetting for true support
# - Produces summary table at end
# ==============================================

# -------- Inputs ----------
$TargetSubscriptions = @(
  '5961d092-dde6-40e2-90e5-2b6dab00b739',
  'c9400257-0d1b-4a59-b227-36b3aea73b0a',
  '5eac3012-879c-4bd6-81a7-e5cc0a531901',
  '961271eb-fe4d-455e-9f53-5b0a5a931bcb',
  '03127f90-4f1f-4f83-b9a7-a23b39ecc91e',
  '00f0e475-b85a-4f4e-a4df-84a44d547d95',
  '6ee22078-9979-48d2-996d-2c14734618dc',
  '1e08993c-9844-43ed-bdbc-ef2587524458',
  '68d0c954-0825-43ce-ac75-a906c966bd25',
  'fcaca3a0-0f22-4db3-9f41-248ba1e9be20'
)

$DiagSettingName = 'setByPSScript-EventHub'

# Destination = Event Hub
$EventHubAuthorizationRuleId = '/subscriptions/fcaca3a0-0f22-4db3-9f41-248ba1e9be20/resourcegroups/lm-logs-tieva-uksouth-group/providers/microsoft.eventhub/namespaces/lmlogsovarrouksouth1/authorizationrules/rootmanagesharedaccesskey'
$EventHubName                = 'log-hub'   # ensure this hub exists in that namespace

# Filters
$IncludeResourceTypes = @()           # e.g. @('Microsoft.Storage/storageAccounts')
$IncludeLocations     = @('uksouth')  # region filter (normalized, case-insensitive)

# Single-resource test mode
$TestSingleResourceId          = ''      # paste full ResourceId to test only one
$BypassLocationFilterForSingle = $false  # set true to ignore location filter for single resource

# Safety: dry-run?
$WhatIf = $true                         # set to $false to apply

# Export?
$ExportCsv = $true
$ExportBasePath = "$HOME/diag-summary-$(Get-Date -Format 'yyyyMMdd_HHmmss')"
# ------------------------------------------

# Silence breaking-change messages
Update-AzConfig -DisplayBreakingChangeWarning:$false | Out-Null
Set-Item Env:SuppressAzurePowerShellBreakingChangeWarnings 'true'

# Ensure signed in
try { $null = Get-AzContext -ErrorAction Stop } catch { Connect-AzAccount | Out-Null }

function As-List($v) {
  if ($null -eq $v) { @() }
  elseif ($v -is [System.Collections.IEnumerable] -and -not ($v -is [string])) { @($v) }
  else { @($v) }
}

# Validate module capability
$newCmd = Get-Command New-AzDiagnosticSetting -ErrorAction Stop
if (-not ($newCmd.Parameters.ContainsKey('Log') -and $newCmd.Parameters.ContainsKey('Metric'))) {
  throw "This Az.Monitor version doesn't expose -Log/-Metric on New-AzDiagnosticSetting. Update modules."
}

# Create export directory if required
if ($ExportCsv) { New-Item -ItemType Directory -Path $ExportBasePath -Force | Out-Null }

$Results = @()

foreach ($subId in $TargetSubscriptions) {
  Write-Host "`n===== Subscription: $subId =====" -ForegroundColor Cyan
  Set-AzContext -SubscriptionId $subId -ErrorAction Stop | Out-Null
  $subInfo = Get-AzSubscription -SubscriptionId $subId -ErrorAction SilentlyContinue
  $subName = if ($subInfo) { $subInfo.Name } else { $subId }

  # Collect resources (single or all)
  if ([string]::IsNullOrWhiteSpace($TestSingleResourceId)) {
    $resources = Get-AzResource -ErrorAction Stop
  } else {
    try {
      $one = Get-AzResource -ResourceId $TestSingleResourceId -ErrorAction Stop
      if (-not $BypassLocationFilterForSingle -and $IncludeLocations.Count -gt 0) {
        $normIncl = $IncludeLocations | ForEach-Object { ($_ -replace '\s','').ToLowerInvariant() }
        $resLoc   = (($one.Location) -replace '\s','').ToLowerInvariant()
        if (-not ($normIncl -contains $resLoc)) {
          Write-Warning "Single resource is in '$($one.Location)' which is not in IncludeLocations. Set `$BypassLocationFilterForSingle = `$true to proceed anyway."
          continue
        }
      }
      $resources = @($one)
    } catch {
      Write-Warning "TestSingleResourceId not found in this subscription: $($_.Exception.Message)"
      continue
    }
  }

  # Optional filters
  if ($IncludeResourceTypes.Count -gt 0 -and [string]::IsNullOrWhiteSpace($TestSingleResourceId)) {
    $resources = $resources | Where-Object { $IncludeResourceTypes -contains $_.ResourceType }
  }

  if ($IncludeLocations.Count -gt 0 -and ([string]::IsNullOrWhiteSpace($TestSingleResourceId) -or -not $BypassLocationFilterForSingle)) {
    $normIncl = $IncludeLocations | ForEach-Object { ($_ -replace '\s','').ToLowerInvariant() }
    $resources = $resources | Where-Object {
      $resLoc = (($_.Location) -replace '\s','').ToLowerInvariant()
      $normIncl -contains $resLoc
    }
  }

  foreach ($res in $resources) {
    $action = "Skipped"
    $note = ""

    # ---- Skip known unsupported resource types ----
    $skipTypes = @(
      'microsoft.compute/disks',
      'microsoft.compute/snapshots'
    )

    if ($skipTypes -contains ($res.ResourceType.ToLower())) {
      Write-Host "  Skipping (known unsupported type): $($res.ResourceType) $($res.Name)" -ForegroundColor Magenta
      $note = "KnownUnsupportedType"
      $Results += [pscustomobject]@{
        SubscriptionName       = $subName
        ResourceGroup          = $res.ResourceGroupName
        ResourceType           = $res.ResourceType
        ResourceName           = $res.Name
        Location               = $res.Location
        DiagnosticSettingName  = $DiagSettingName
        EventHubName           = $EventHubName
        Action                 = $note
      }
      continue
    }

    # ---- Check diagnostic support safely ----
    $cats = $null
    try {
      $cats = Get-AzDiagnosticSettingCategory -ResourceId $res.Id -ErrorAction Stop
    } catch {
      Write-Host "  Skipping (no diagnostics supported): $($res.ResourceType) $($res.Name)" -ForegroundColor Magenta
      $note = "NoDiagnosticsSupported"
      $Results += [pscustomobject]@{
        SubscriptionName       = $subName
        ResourceGroup          = $res.ResourceGroupName
        ResourceType           = $res.ResourceType
        ResourceName           = $res.Name
        Location               = $res.Location
        DiagnosticSettingName  = $DiagSettingName
        EventHubName           = $EventHubName
        Action                 = $note
      }
      continue
    }

    # Lightweight probe to confirm provider truly supports diagnostics
    try {
      $null = Get-AzDiagnosticSetting -ResourceId $res.Id -ErrorAction Stop
    } catch {
      if ($_.Exception.Message -match 'not support diagnostic settings') {
        Write-Host "  Skipping (provider rejects diagnostics): $($res.ResourceType) $($res.Name)" -ForegroundColor Magenta
        $note = "ProviderRejectsDiagnostics"
        $Results += [pscustomobject]@{
          SubscriptionName       = $subName
          ResourceGroup          = $res.ResourceGroupName
          ResourceType           = $res.ResourceType
          ResourceName           = $res.Name
          Location               = $res.Location
          DiagnosticSettingName  = $DiagSettingName
          EventHubName           = $EventHubName
          Action                 = $note
        }
        continue
      }
    }

    # ---- Build valid category lists ----
    $logCats    = As-List(($cats | Where-Object { $_.CategoryType -eq 'Logs'    }).Name)
    $metricCats = As-List(($cats | Where-Object { $_.CategoryType -eq 'Metrics' }).Name)
    $hasAllLogs = ($cats.Name -contains 'AllLogs')
    $hasAllMet  = ($cats.Name -contains 'AllMetrics')

    if ((-not $hasAllLogs -and -not $hasAllMet) -and ($logCats.Count -eq 0 -and $metricCats.Count -eq 0)) {
      Write-Host "  Skipping (no usable diagnostic categories): $($res.ResourceType) $($res.Name)" -ForegroundColor Magenta
      $note = "NoUsableCategories"
      $Results += [pscustomobject]@{
        SubscriptionName       = $subName
        ResourceGroup          = $res.ResourceGroupName
        ResourceType           = $res.ResourceType
        ResourceName           = $res.Name
        Location               = $res.Location
        DiagnosticSettingName  = $DiagSettingName
        EventHubName           = $EventHubName
        Action                 = $note
      }
      continue
    }

    # ---- Build Log/Metric arrays ----
    $logObjs = @()
    if ($hasAllLogs) {
      $logObjs += New-AzDiagnosticSettingLogSettingsObject -Enabled $true -Category 'AllLogs'
    } elseif ($logCats.Count -gt 0) {
      foreach ($lc in $logCats) {
        $logObjs += New-AzDiagnosticSettingLogSettingsObject -Enabled $true -Category $lc
      }
    }

    $metricObjs = @()
    if ($hasAllMet) {
      $metricObjs += New-AzDiagnosticSettingMetricSettingsObject -Enabled $true -Category 'AllMetrics'
    } elseif ($metricCats.Count -gt 0) {
      foreach ($mc in $metricCats) {
        $metricObjs += New-AzDiagnosticSettingMetricSettingsObject -Enabled $true -Category $mc
      }
    }

    Write-Host ("  {0} :: {1} (RG:{2}, {3}) -> Apply '{4}' to EH '{5}'" -f `
      $res.ResourceType, $res.Name, $res.ResourceGroupName, $res.Location, $DiagSettingName, $EventHubName) -ForegroundColor Yellow

    if ($WhatIf) {
      $action = "WouldApply"
    } else {
      try {
        New-AzDiagnosticSetting `
          -Name $DiagSettingName `
          -ResourceId $res.Id `
          -EventHubAuthorizationRuleId $EventHubAuthorizationRuleId `
          -EventHubName $EventHubName `
          -Log $logObjs `
          -Metric $metricObjs `
          -ErrorAction Stop | Out-Null

        Write-Host "    Success" -ForegroundColor Green
        $action = "Applied"
      } catch {
        Write-Warning "    Failed: $($_.Exception.Message)"
        $action = "Failed"
      }
    }

    $Results += [pscustomobject]@{
      SubscriptionName       = $subName
      ResourceGroup          = $res.ResourceGroupName
      ResourceType           = $res.ResourceType
      ResourceName           = $res.Name
      Location               = $res.Location
      DiagnosticSettingName  = $DiagSettingName
      EventHubName           = $EventHubName
      Action                 = $action
    }
  }
}

Write-Host "`n===== Summary Table =====" -ForegroundColor Cyan
$Results | Sort-Object SubscriptionName, ResourceGroup, ResourceType | Format-Table -AutoSize

if ($ExportCsv -and $Results.Count -gt 0) {
  $csv = Join-Path $ExportBasePath "DiagnosticSettings_Summary.csv"
  $Results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
  Write-Host "`nExported summary to: $csv" -ForegroundColor DarkCyan
}

Write-Host "`nAll done." -ForegroundColor Cyan
