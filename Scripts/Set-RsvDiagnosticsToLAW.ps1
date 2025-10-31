# ==============================================
# Bulk enable Diagnostic Settings -> Log Analytics (AzureDiagnostics table)
# - Scope: Microsoft.RecoveryServices/vaults (RSV only)
# - Creates TWO settings per vault: Backup + ASR (separate to avoid data loss)
# - Uses Azure Diagnostics table (legacy, dynamic schema)
# - Intersects categories with what's supported on each vault
# - Compatible with Az.Monitor 14.5.0 (uses -Log / -Metric)
# - Summary table + CSV export
# ==============================================

# -------- Inputs ----------
$TargetSubscriptions = @(
'114d5336-f893-485e-aae0-96b0e01ccbfb',
'92de7176-56c0-41a7-ab4d-36234bac5924',
'17085a62-1ef1-4573-900b-00be3108a362',
'17d40906-46eb-420b-a3d0-431fd0195f1f',
'6b466176-6fc1-42dc-bb30-31166c203305'

)

# Names for the two distinct diagnostic settings
$BackupSettingName = 'DS-Backup-AzureDiagnostics'
$AsrSettingName    = 'DS-ASR-AzureDiagnostics'

# Destination LAW (full ARM ID)
$WorkspaceResourceId = '/subscriptions/17d40906-46eb-420b-a3d0-431fd0195f1f/resourcegroups/lm-logs-tieva-uksouth-group/providers/microsoft.operationalinsights/workspaces/lmlogsmoorhouseuksouth1'

# Region filter (normalized, case-insensitive)
$IncludeLocations = @('uksouth')

# Single-resource test mode
$TestSingleResourceId          = ''
$BypassLocationFilterForSingle = $false

# Safety: dry-run?
$WhatIf = $true

# Export?
$ExportCsv      = $true
$ExportBasePath = "$HOME/diag-summary-$(Get-Date -Format 'yyyyMMdd_HHmmss')"
# ------------------------------------------

# Silence breaking-change messages
Update-AzConfig -DisplayBreakingChangeWarning:$false | Out-Null
Set-Item Env:SuppressAzurePowerShellBreakingChangeWarnings 'true' | Out-Null

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
if (-not $newCmd.Parameters.ContainsKey('WorkspaceId')) {
  throw "This Az.Monitor version doesn't support -WorkspaceId on New-AzDiagnosticSetting. Update modules."
}

# Desired categories (from MS docs)
# Backup: when using resource-specific, MS recommend these; AzureBackupReport is AzureDiagnostics-only.
$DesiredBackupCats = @(
  'CoreAzureBackup',
  'AddonAzureBackupJobs',
  'AddonAzureBackupAlerts',
  'AddonAzureBackupPolicy',
  'AddonAzureBackupStorage',
  'AddonAzureBackupProtectedInstance',
  'AzureBackupReport' # lives in AzureDiagnostics table
)

# ASR categories (many land in AzureDiagnostics table; ASRJobs in ASRJobs table)
$DesiredAsrCats = @(
  'AzureSiteRecoveryJobs',
  'AzureSiteRecoveryEvents',
  'AzureSiteRecoveryReplicatedItems',
  'AzureSiteRecoveryRecoveryPoints',
  'AzureSiteRecoveryReplicationStats',
  'AzureSiteRecoveryProtectedDiskDataChurn',
  'AzureSiteRecoveryReplicationDataUploadRate'
)

# Create export directory if required
if ($ExportCsv) { New-Item -ItemType Directory -Path $ExportBasePath -Force | Out-Null }

$Results = @()

foreach ($subId in $TargetSubscriptions) {
  Write-Host "`n===== Subscription: $subId =====" -ForegroundColor Cyan
  Set-AzContext -SubscriptionId $subId -ErrorAction Stop | Out-Null
  $subInfo = Get-AzSubscription -SubscriptionId $subId -ErrorAction SilentlyContinue
  $subName = if ($subInfo) { $subInfo.Name } else { $subId }

  # Collect RSVs (or single-resource)
  if ([string]::IsNullOrWhiteSpace($TestSingleResourceId)) {
    $resources = Get-AzResource -ResourceType 'Microsoft.RecoveryServices/vaults' -ErrorAction Stop
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

  # Region filter
  if ($IncludeLocations.Count -gt 0 -and ([string]::IsNullOrWhiteSpace($TestSingleResourceId) -or -not $BypassLocationFilterForSingle)) {
    $normIncl = $IncludeLocations | ForEach-Object { ($_ -replace '\s','').ToLowerInvariant() }
    $resources = $resources | Where-Object {
      $resLoc = (($_.Location) -replace '\s','').ToLowerInvariant()
      $normIncl -contains $resLoc
    }
  }

  foreach ($res in $resources) {

    # Get supported categories for this vault
    $cats = $null
    try {
      $cats = Get-AzDiagnosticSettingCategory -ResourceId $res.Id -ErrorAction Stop
    } catch {
      Write-Host "  Skipping (no diagnostics supported): $($res.ResourceType) $($res.Name)" -ForegroundColor Magenta
      foreach ($setName in @($BackupSettingName, $AsrSettingName)) {
        $Results += [pscustomobject]@{
          SubscriptionName      = $subName
          ResourceGroup         = $res.ResourceGroupName
          ResourceType          = $res.ResourceType
          ResourceName          = $res.Name
          Location              = $res.Location
          DiagnosticSettingName = $setName
          Destination           = 'LAW(AzureDiagnostics)'
          CategoryGroup         = $(if ($setName -eq $BackupSettingName){'Backup'}else{'ASR'})
          CategoriesApplied     = ''
          Action                = 'NoDiagnosticsSupported'
        }
      }
      continue
    }

    $availableCats = As-List($cats.Name)

    # Build arrays per group (only categories that exist on this resource)
    $backupCatsToApply = $DesiredBackupCats | Where-Object { $availableCats -contains $_ }
    $asrCatsToApply    = $DesiredAsrCats    | Where-Object { $availableCats -contains $_ }

    # Helper to create log settings objects
    function New-LogObjs($catList) {
      $list = @()
      foreach ($c in $catList) {
        $list += New-AzDiagnosticSettingLogSettingsObject -Enabled $true -Category $c
      }
      ,$list
    }

    # ----- Apply BACKUP setting -----
    if ($backupCatsToApply.Count -gt 0) {
      Write-Host ("  RSV {0} :: {1} -> Apply '{2}' (Backup {3} cats) to LAW (AzureDiagnostics)" -f `
        $res.ResourceGroupName, $res.Name, $BackupSettingName, $backupCatsToApply.Count) -ForegroundColor Yellow

      $action = 'WouldApply'
      if (-not $WhatIf) {
        try {
          New-AzDiagnosticSetting `
            -Name $BackupSettingName `
            -ResourceId $res.Id `
            -WorkspaceId $WorkspaceResourceId `
            -LogAnalyticsDestinationType 'AzureDiagnostics' `
            -Log (New-LogObjs $backupCatsToApply) `
            -ErrorAction Stop | Out-Null
          Write-Host "    Success (Backup)" -ForegroundColor Green
          $action = 'Applied'
        } catch {
          Write-Warning "    Failed (Backup): $($_.Exception.Message)"
          $action = 'Failed'
        }
      }
      $Results += [pscustomobject]@{
        SubscriptionName      = $subName
        ResourceGroup         = $res.ResourceGroupName
        ResourceType          = $res.ResourceType
        ResourceName          = $res.Name
        Location              = $res.Location
        DiagnosticSettingName = $BackupSettingName
        Destination           = 'LAW(AzureDiagnostics)'
        CategoryGroup         = 'Backup'
        CategoriesApplied     = ($backupCatsToApply -join ',')
        Action                = $action
      }
    } else {
      $Results += [pscustomobject]@{
        SubscriptionName      = $subName
        ResourceGroup         = $res.ResourceGroupName
        ResourceType          = $res.ResourceType
        ResourceName          = $res.Name
        Location              = $res.Location
        DiagnosticSettingName = $BackupSettingName
        Destination           = 'LAW(AzureDiagnostics)'
        CategoryGroup         = 'Backup'
        CategoriesApplied     = ''
        Action                = 'NoUsableCategories'
      }
    }

    # ----- Apply ASR setting -----
    if ($asrCatsToApply.Count -gt 0) {
      Write-Host ("  RSV {0} :: {1} -> Apply '{2}' (ASR {3} cats) to LAW (AzureDiagnostics)" -f `
        $res.ResourceGroupName, $res.Name, $AsrSettingName, $asrCatsToApply.Count) -ForegroundColor Yellow

      $action = 'WouldApply'
      if (-not $WhatIf) {
        try {
          New-AzDiagnosticSetting `
            -Name $AsrSettingName `
            -ResourceId $res.Id `
            -WorkspaceId $WorkspaceResourceId `
            -LogAnalyticsDestinationType 'AzureDiagnostics' `
            -Log (New-LogObjs $asrCatsToApply) `
            -ErrorAction Stop | Out-Null
          Write-Host "    Success (ASR)" -ForegroundColor Green
          $action = 'Applied'
        } catch {
          Write-Warning "    Failed (ASR): $($_.Exception.Message)"
          $action = 'Failed'
        }
      }
      $Results += [pscustomobject]@{
        SubscriptionName      = $subName
        ResourceGroup         = $res.ResourceGroupName
        ResourceType          = $res.ResourceType
        ResourceName          = $res.Name
        Location              = $res.Location
        DiagnosticSettingName = $AsrSettingName
        Destination           = 'LAW(AzureDiagnostics)'
        CategoryGroup         = 'ASR'
        CategoriesApplied     = ($asrCatsToApply -join ',')
        Action                = $action
      }
    } else {
      $Results += [pscustomobject]@{
        SubscriptionName      = $subName
        ResourceGroup         = $res.ResourceGroupName
        ResourceType          = $res.ResourceType
        ResourceName          = $res.Name
        Location              = $res.Location
        DiagnosticSettingName = $AsrSettingName
        Destination           = 'LAW(AzureDiagnostics)'
        CategoryGroup         = 'ASR'
        CategoriesApplied     = ''
        Action                = 'NoUsableCategories'
      }
    }
  }
}

Write-Host "`n===== Summary Table =====" -ForegroundColor Cyan
$Results | Sort-Object SubscriptionName, ResourceGroup, ResourceType, DiagnosticSettingName |
  Format-Table -AutoSize SubscriptionName,ResourceGroup,ResourceType,ResourceName,Location,DiagnosticSettingName,CategoryGroup,CategoriesApplied,Action

if ($ExportCsv -and $Results.Count -gt 0) {
  $csv = Join-Path $ExportBasePath "DiagnosticSettings_Summary.csv"
  $Results | Export-Csv -Path $csv -NoTypeInformation -Encoding UTF8
  Write-Host "`nExported summary to: $csv" -ForegroundColor DarkCyan
}

Write-Host "`nAll done." -ForegroundColor Cyan
