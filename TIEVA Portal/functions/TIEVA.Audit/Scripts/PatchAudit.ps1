<#
.SYNOPSIS
  TIEVA Patch Management Auditor
  
.DESCRIPTION
  Comprehensive Azure patch compliance audit using Azure Update Manager:
  - VM patch assessment status
  - Missing updates by classification (Critical, Security, etc.)
  - Maintenance configurations
  - Update schedules
  - Patch compliance summary
  
  Outputs multi-sheet Excel workbook: Patch_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.EXAMPLE
  .\PatchAudit.ps1 -SubscriptionIds @("sub-id-1")
  
.NOTES
  Requires: Az.Accounts, Az.Compute, Az.ResourceGraph, ImportExcel modules
  Permissions: Reader on subscriptions
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads"
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Patch Management Auditor" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
Write-Host ""

# ============================================================================
# HELPER FUNCTIONS
# ============================================================================

function Get-SubscriptionList {
  if ($SubscriptionIds -and $SubscriptionIds.Count -gt 0) {
    $subs = @()
    foreach ($id in $SubscriptionIds) {
      try { $subs += Get-AzSubscription -SubscriptionId $id -TenantId (Get-AzContext).Tenant.Id -ErrorAction Stop } 
      catch { Write-Warning "Could not access subscription $id : $_" }
    }
    return $subs
  } else {
    return Get-AzSubscription -TenantId (Get-AzContext).Tenant.Id | Where-Object { $_.State -eq 'Enabled' }
  }
}

function Get-PatchComplianceStatus {
  param([int]$Critical, [int]$Security, [int]$Other)
  $total = $Critical + $Security + $Other
  if ($Critical -gt 0) { return "Critical" }
  if ($Security -gt 0) { return "Non-Compliant" }
  if ($Other -gt 0) { return "Updates Available" }
  return "Compliant"
}

# ============================================================================
# DATA COLLECTION
# ============================================================================

$subscriptions = Get-SubscriptionList
Write-Host "Auditing $($subscriptions.Count) subscription(s)..." -ForegroundColor Green

# Initialize result collections
$vmPatchStatus = [System.Collections.ArrayList]::new()
$missingPatches = [System.Collections.ArrayList]::new()
$maintenanceConfigs = [System.Collections.ArrayList]::new()
$findings = [System.Collections.ArrayList]::new()
$summary = [System.Collections.ArrayList]::new()

foreach ($sub in $subscriptions) {
  Write-Host "`nProcessing: $($sub.Name)" -ForegroundColor Yellow
  
  try {
    Set-AzContext -SubscriptionId $sub.Id -TenantId $sub.TenantId -ErrorAction Stop | Out-Null
  }
  catch {
    Write-Host "  Skipping - Cannot access subscription: $($_.Exception.Message)" -ForegroundColor Red
    continue
  }
  
  # Get all VMs
  Write-Host "  Getting Virtual Machines..."
  $vms = Get-AzVM -Status -ErrorAction SilentlyContinue
  
  foreach ($vm in $vms) {
    Write-Host "    Checking: $($vm.Name)"
    $powerState = ($vm.Statuses | Where-Object { $_.Code -like 'PowerState/*' }).DisplayStatus
    
    # Get patch assessment
    $critical = 0
    $security = 0
    $other = 0
    $lastAssessment = $null
    $osType = $vm.StorageProfile.OsDisk.OsType
    
    try {
      # Use Resource Graph for patch assessment data
      $query = @"
patchassessmentresources
| where type == 'microsoft.compute/virtualmachines/patchassessmentresults'
| where id contains '$($vm.Id)'
| project properties
"@
      $patchData = Search-AzGraph -Query $query -Subscription $sub.Id -ErrorAction SilentlyContinue
      
      if ($patchData -and $patchData.properties) {
        $props = $patchData.properties
        $critical = if ($props.criticalAndSecurityPatchCount) { $props.criticalAndSecurityPatchCount } else { 0 }
        $other = if ($props.otherPatchCount) { $props.otherPatchCount } else { 0 }
        $lastAssessment = $props.lastModifiedDateTime
      }
    }
    catch {
      # Fallback - just note we couldn't get assessment
    }
    
    # Get automatic updates setting
    $autoUpdates = "Unknown"
    if ($vm.OSProfile) {
      if ($osType -eq "Windows" -and $vm.OSProfile.WindowsConfiguration) {
        $autoUpdates = if ($vm.OSProfile.WindowsConfiguration.EnableAutomaticUpdates) { "Enabled" } else { "Disabled" }
      }
      elseif ($osType -eq "Linux" -and $vm.OSProfile.LinuxConfiguration) {
        $autoUpdates = if ($vm.OSProfile.LinuxConfiguration.PatchSettings) { 
          $vm.OSProfile.LinuxConfiguration.PatchSettings.PatchMode 
        } else { "Manual" }
      }
    }
    
    $complianceStatus = Get-PatchComplianceStatus -Critical $critical -Security $security -Other $other
    
    $vmPatchStatus.Add([PSCustomObject]@{
      SubscriptionName     = $sub.Name
      SubscriptionId       = $sub.Id
      ResourceGroup        = $vm.ResourceGroupName
      VMName               = $vm.Name
      OSType               = $osType
      PowerState           = $powerState
      ComplianceStatus     = $complianceStatus
      CriticalPatches      = $critical
      SecurityPatches      = $security
      OtherPatches         = $other
      TotalMissing         = $critical + $security + $other
      AutomaticUpdates     = $autoUpdates
      LastAssessment       = $lastAssessment
      Location             = $vm.Location
    }) | Out-Null
    
    # Add findings based on compliance status
    if ($complianceStatus -eq "Critical") {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Patch Compliance'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = "VM has $critical critical/security patches missing"
        Recommendation   = 'Apply critical and security patches immediately'
      }) | Out-Null
    }
    elseif ($complianceStatus -eq "Non-Compliant") {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Patch Compliance'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = "VM has $security security patches missing"
        Recommendation   = 'Schedule security patch deployment'
      }) | Out-Null
    }
    elseif ($complianceStatus -eq "Updates Available") {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Low'
        Category         = 'Patch Compliance'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = "VM has $other non-critical updates available"
        Recommendation   = 'Plan update deployment during next maintenance window'
      }) | Out-Null
    }
    
    # Check for disabled automatic updates on Windows
    if ($osType -eq "Windows" -and $autoUpdates -eq "Disabled") {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Update Configuration'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = "Automatic Windows Updates are disabled"
        Recommendation   = 'Enable automatic updates or configure Azure Update Manager'
      }) | Out-Null
    }
    
    # If there are missing patches, add to detailed list
    if ($critical + $security + $other -gt 0) {
      if ($critical -gt 0) {
        $missingPatches.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          VMName           = $vm.Name
          ResourceGroup    = $vm.ResourceGroupName
          Classification   = "Critical/Security"
          Count            = $critical
          OSType           = $osType
          Severity         = "High"
        }) | Out-Null
      }
      
      if ($other -gt 0) {
        $missingPatches.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          VMName           = $vm.Name
          ResourceGroup    = $vm.ResourceGroupName
          Classification   = "Other Updates"
          Count            = $other
          OSType           = $osType
          Severity         = "Low"
        }) | Out-Null
      }
    }
  }
  
  # Get Maintenance Configurations
  Write-Host "  Getting Maintenance Configurations..."
  try {
    $configs = Get-AzMaintenanceConfiguration -ErrorAction SilentlyContinue
    foreach ($config in $configs) {
      $maintenanceConfigs.Add([PSCustomObject]@{
        SubscriptionName     = $sub.Name
        SubscriptionId       = $sub.Id
        ConfigName           = $config.Name
        ResourceGroup        = $config.ResourceGroupName
        MaintenanceScope     = $config.MaintenanceScope
        RecurrenceInterval   = $config.RecurEvery
        StartTime            = $config.StartDateTime
        TimeZone             = $config.TimeZone
        Duration             = $config.Duration
        Visibility           = $config.Visibility
        Location             = $config.Location
      }) | Out-Null
    }
  }
  catch {
    Write-Host "    Could not get maintenance configurations: $_" -ForegroundColor Gray
  }
  
  # Check if subscription has no maintenance configuration
  $subConfigs = $maintenanceConfigs | Where-Object { $_.SubscriptionId -eq $sub.Id }
  $subVMs = $vmPatchStatus | Where-Object { $_.SubscriptionId -eq $sub.Id }
  if ($subConfigs.Count -eq 0 -and $subVMs.Count -gt 0) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'Low'
      Category         = 'Maintenance Configuration'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceId       = "/subscriptions/$($sub.Id)"
      Detail           = "No maintenance configuration defined for this subscription"
      Recommendation   = 'Configure Azure Update Manager maintenance schedules'
    }) | Out-Null
  }
  
  # Build summary for this subscription
  $subVMs = $vmPatchStatus | Where-Object { $_.SubscriptionId -eq $sub.Id }
  $subFindings = $findings | Where-Object { $_.SubscriptionId -eq $sub.Id }
  
  $summary.Add([PSCustomObject]@{
    SubscriptionName    = $sub.Name
    SubscriptionId      = $sub.Id
    TotalVMs            = $subVMs.Count
    Compliant           = ($subVMs | Where-Object { $_.ComplianceStatus -eq 'Compliant' }).Count
    NonCompliant        = ($subVMs | Where-Object { $_.ComplianceStatus -ne 'Compliant' }).Count
    CriticalPatches     = ($subVMs | Measure-Object -Property CriticalPatches -Sum).Sum
    SecurityPatches     = ($subVMs | Measure-Object -Property SecurityPatches -Sum).Sum
    OtherPatches        = ($subVMs | Measure-Object -Property OtherPatches -Sum).Sum
    AutoUpdatesEnabled  = ($subVMs | Where-Object { $_.AutomaticUpdates -eq 'Enabled' }).Count
    MaintenanceConfigs  = $subConfigs.Count
    HighFindings        = ($subFindings | Where-Object { $_.Severity -eq 'High' }).Count
    MediumFindings      = ($subFindings | Where-Object { $_.Severity -eq 'Medium' }).Count
    LowFindings         = ($subFindings | Where-Object { $_.Severity -eq 'Low' }).Count
    CompliancePercent   = if ($subVMs.Count -gt 0) { 
      [math]::Round((($subVMs | Where-Object { $_.ComplianceStatus -eq 'Compliant' }).Count / $subVMs.Count) * 100, 0) 
    } else { 100 }
    AuditDate           = Get-Date -Format 'yyyy-MM-dd HH:mm'
  }) | Out-Null
}

# ============================================================================
# EXPORT TO EXCEL
# ============================================================================

$outputFile = Join-Path $OutPath "Patch_Audit.xlsx"
Write-Host "`nExporting to Excel: $outputFile" -ForegroundColor Cyan

# Remove existing file
if (Test-Path $outputFile) { Remove-Item $outputFile -Force }

# Export helper function
function Export-Sheet { param($Data, $WorksheetName, $TableName)
  if (-not $Data -or $Data.Count -eq 0) { Write-Host "  Skipping empty: $WorksheetName" -ForegroundColor Gray; return }
  $Data | Export-Excel -Path $outputFile -WorksheetName $WorksheetName -TableName $TableName -TableStyle 'Medium9' -AutoSize -FreezeTopRow -BoldTopRow
  Write-Host "  + $WorksheetName ($($Data.Count) rows)" -ForegroundColor Green
}

# Export each dataset
Export-Sheet -Data $summary -WorksheetName 'Summary' -TableName 'Summary'
Export-Sheet -Data $vmPatchStatus -WorksheetName 'VM Patch Status' -TableName 'VMPatchStatus'
Export-Sheet -Data $missingPatches -WorksheetName 'Missing Patches' -TableName 'MissingPatches'
Export-Sheet -Data $maintenanceConfigs -WorksheetName 'Maintenance Configs' -TableName 'MaintenanceConfigs'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Patch Audit Complete!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Subscriptions Audited: $($subscriptions.Count)"
Write-Host "Total VMs: $($vmPatchStatus.Count)"
Write-Host "  Compliant: $(($vmPatchStatus | Where-Object { $_.ComplianceStatus -eq 'Compliant' }).Count)"
Write-Host "  Non-Compliant: $(($vmPatchStatus | Where-Object { $_.ComplianceStatus -ne 'Compliant' }).Count)"
Write-Host "Total Findings: $($findings.Count)"
Write-Host "  High: $(($findings | Where-Object { $_.Severity -eq 'High' }).Count)"
Write-Host "  Medium: $(($findings | Where-Object { $_.Severity -eq 'Medium' }).Count)"
Write-Host "  Low: $(($findings | Where-Object { $_.Severity -eq 'Low' }).Count)"
Write-Host "Output: $outputFile"
Write-Host ""
