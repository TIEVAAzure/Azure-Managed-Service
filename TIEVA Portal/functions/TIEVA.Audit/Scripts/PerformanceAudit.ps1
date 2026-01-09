<#
.SYNOPSIS
  TIEVA Performance Auditor
  
.DESCRIPTION
  Comprehensive Azure performance audit:
  - VM right-sizing recommendations from Azure Advisor
  - CPU/Memory utilization metrics
  - Underutilized resources
  - Storage performance (IOPS, throughput)
  - Disk utilization
  
  Outputs multi-sheet Excel workbook: Performance_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.PARAMETER DaysBack
  Number of days to look back for metrics. Default 14.
  
.EXAMPLE
  .\PerformanceAudit.ps1 -SubscriptionIds @("sub-id-1")
  
.NOTES
  Requires: Az.Accounts, Az.Compute, Az.Monitor, Az.Advisor, ImportExcel modules
  Permissions: Reader + Monitoring Reader on subscriptions
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads",
  [int]$DaysBack = 14
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Performance Auditor" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
Write-Host "Metrics Period: Last $DaysBack days" -ForegroundColor Gray
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

function Get-UtilizationCategory {
  param([double]$AvgCpu, [double]$MaxCpu)
  if ($AvgCpu -lt 5 -and $MaxCpu -lt 20) { return "Idle" }
  if ($AvgCpu -lt 20) { return "Underutilized" }
  if ($AvgCpu -lt 70) { return "Optimal" }
  if ($AvgCpu -lt 90) { return "High" }
  return "Critical"
}

function Get-RightsizingRecommendation {
  param([string]$Category, [string]$CurrentSize)
  switch ($Category) {
    "Idle"          { return "Consider deallocating or deleting" }
    "Underutilized" { return "Consider downsizing" }
    "Optimal"       { return "Appropriately sized" }
    "High"          { return "Monitor closely" }
    "Critical"      { return "Consider upsizing" }
    default         { return "Review required" }
  }
}

# ============================================================================
# DATA COLLECTION
# ============================================================================

$subscriptions = Get-SubscriptionList
Write-Host "Auditing $($subscriptions.Count) subscription(s)..." -ForegroundColor Green

$startTime = (Get-Date).AddDays(-$DaysBack)
$endTime = Get-Date

# Initialize result collections
$vmPerformance = [System.Collections.ArrayList]::new()
$advisorRecs = [System.Collections.ArrayList]::new()
$diskPerformance = [System.Collections.ArrayList]::new()
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
  
  # Get Azure Advisor recommendations (Performance category)
  Write-Host "  Getting Advisor Recommendations..."
  try {
    # Get all recommendations and filter to Performance category
    $allRecs = Get-AzAdvisorRecommendation -ErrorAction SilentlyContinue
    $recs = $allRecs | Where-Object { $_.Category -eq 'Performance' }
    foreach ($rec in $recs) {
      $advisorRecs.Add([PSCustomObject]@{
        SubscriptionName   = $sub.Name
        SubscriptionId     = $sub.Id
        ResourceName       = if ($rec.ImpactedValue) { $rec.ImpactedValue } else { $rec.ResourceId.Split('/')[-1] }
        ResourceType       = $rec.ImpactedField
        Category           = $rec.Category
        Impact             = $rec.Impact
        Problem            = if ($rec.ShortDescription) { $rec.ShortDescription.Problem } else { '' }
        Solution           = if ($rec.ShortDescription) { $rec.ShortDescription.Solution } else { '' }
        PotentialBenefits  = if ($rec.ExtendedProperties) { $rec.ExtendedProperties.potentialBenefits } else { '' }
        AnnualSavings      = if ($rec.ExtendedProperties) { $rec.ExtendedProperties.annualSavingsAmount } else { 0 }
        Currency           = if ($rec.ExtendedProperties) { $rec.ExtendedProperties.savingsCurrency } else { '' }
        ResourceId         = $rec.ResourceId
      }) | Out-Null
      
      # Add to findings with enhanced detail
      $severity = switch ($rec.Impact) {
        'High' { 'High' }
        'Medium' { 'Medium' }
        default { 'Low' }
      }
      
      # Build detailed finding text
      $problem = if ($rec.ShortDescription -and $rec.ShortDescription.Problem) { $rec.ShortDescription.Problem } else { 'Performance issue detected' }
      $solution = if ($rec.ShortDescription -and $rec.ShortDescription.Solution) { $rec.ShortDescription.Solution } else { 'Review Advisor recommendation in Azure Portal' }
      $benefits = if ($rec.ExtendedProperties -and $rec.ExtendedProperties.potentialBenefits) { $rec.ExtendedProperties.potentialBenefits } else { '' }
      $savings = if ($rec.ExtendedProperties -and $rec.ExtendedProperties.annualSavingsAmount) { "Potential savings: $($rec.ExtendedProperties.savingsCurrency)$($rec.ExtendedProperties.annualSavingsAmount)/year" } else { '' }
      
      $detailParts = @($problem)
      if ($benefits) { $detailParts += "Benefits: $benefits" }
      if ($savings) { $detailParts += $savings }
      
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = $severity
        Category         = 'Advisor Recommendation'
        ResourceType     = $rec.ImpactedField
        ResourceName     = if ($rec.ImpactedValue) { $rec.ImpactedValue } else { $rec.ResourceId.Split('/')[-1] }
        ResourceId       = $rec.ResourceId
        Detail           = ($detailParts -join ' | ')
        Recommendation   = $solution
      }) | Out-Null
    }
  }
  catch {
    Write-Host "    Could not get Advisor recommendations: $_" -ForegroundColor Gray
  }
  
  # Get VMs and their performance metrics
  Write-Host "  Getting VM Performance Metrics..."
  $vms = Get-AzVM -Status -ErrorAction SilentlyContinue
  
  foreach ($vm in $vms) {
    $powerState = ($vm.Statuses | Where-Object { $_.Code -like 'PowerState/*' }).DisplayStatus
    
    # Only get metrics for running VMs
    $avgCpu = 0
    $maxCpu = 0
    $avgMemory = 0
    $maxMemory = 0
    
    if ($powerState -eq "VM running") {
      try {
        # CPU metrics
        $cpuMetrics = Get-AzMetric -ResourceId $vm.Id -MetricName "Percentage CPU" `
          -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
          -AggregationType Average,Maximum -ErrorAction SilentlyContinue
        
        if ($cpuMetrics -and $cpuMetrics.Data) {
          $avgCpu = [math]::Round(($cpuMetrics.Data.Average | Where-Object { $_ -ne $null } | Measure-Object -Average).Average, 1)
          $maxCpu = [math]::Round(($cpuMetrics.Data.Maximum | Where-Object { $_ -ne $null } | Measure-Object -Maximum).Maximum, 1)
        }
      }
      catch {
        # Skip metrics if unavailable
      }
    }
    
    $utilizationCategory = Get-UtilizationCategory -AvgCpu $avgCpu -MaxCpu $maxCpu
    $recommendation = Get-RightsizingRecommendation -Category $utilizationCategory -CurrentSize $vm.HardwareProfile.VmSize
    
    $vmPerformance.Add([PSCustomObject]@{
      SubscriptionName    = $sub.Name
      SubscriptionId      = $sub.Id
      ResourceGroup       = $vm.ResourceGroupName
      VMName              = $vm.Name
      VMSize              = $vm.HardwareProfile.VmSize
      PowerState          = $powerState
      AvgCPUPercent       = $avgCpu
      MaxCPUPercent       = $maxCpu
      UtilizationCategory = $utilizationCategory
      Recommendation      = $recommendation
      Location            = $vm.Location
      OsType              = $vm.StorageProfile.OsDisk.OsType
    }) | Out-Null
    
    # Add findings for idle/underutilized VMs
    if ($utilizationCategory -eq "Idle" -and $powerState -eq "VM running") {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'VM Utilization'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = "VM is idle (Avg CPU: $avgCpu%, Max CPU: $maxCpu%) - wasting resources"
        Recommendation   = 'Consider deallocating, deleting, or rightsizing this VM'
      }) | Out-Null
    }
    elseif ($utilizationCategory -eq "Underutilized" -and $powerState -eq "VM running") {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'VM Utilization'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = "VM is underutilized (Avg CPU: $avgCpu%, Max CPU: $maxCpu%)"
        Recommendation   = 'Consider downsizing to a smaller VM size'
      }) | Out-Null
    }
    elseif ($utilizationCategory -eq "Critical" -and $powerState -eq "VM running") {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'VM Utilization'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = "VM CPU is critically high (Avg CPU: $avgCpu%, Max CPU: $maxCpu%)"
        Recommendation   = 'Consider upsizing to a larger VM size or optimizing workload'
      }) | Out-Null
    }
  }
  
  # Get Disk Performance (attached disks only - unattached disks are handled by Resource module)
  Write-Host "  Getting Disk Performance..."
  try {
    $disks = Get-AzDisk -ErrorAction SilentlyContinue | Where-Object { $_.DiskState -eq 'Attached' }
    foreach ($disk in $disks) {
      $diskPerformance.Add([PSCustomObject]@{
        SubscriptionName  = $sub.Name
        SubscriptionId    = $sub.Id
        ResourceGroup     = $disk.ResourceGroupName
        DiskName          = $disk.Name
        DiskSKU           = $disk.Sku.Name
        DiskSizeGB        = $disk.DiskSizeGB
        DiskState         = $disk.DiskState
        DiskIOPSReadWrite = $disk.DiskIOPSReadWrite
        DiskMBpsReadWrite = $disk.DiskMBpsReadWrite
        AttachedTo        = if ($disk.ManagedBy) { $disk.ManagedBy.Split('/')[-1] } else { '-' }
        Location          = $disk.Location
      }) | Out-Null
    }
  }
  catch {
    Write-Host "    Could not get disk info: $_" -ForegroundColor Gray
  }
  
  # Build summary
  $subVMs = $vmPerformance | Where-Object { $_.SubscriptionId -eq $sub.Id }
  $subDisks = $diskPerformance | Where-Object { $_.SubscriptionId -eq $sub.Id }
  $subRecs = $advisorRecs | Where-Object { $_.SubscriptionId -eq $sub.Id }
  $subFindings = $findings | Where-Object { $_.SubscriptionId -eq $sub.Id }
  
  $summary.Add([PSCustomObject]@{
    SubscriptionName       = $sub.Name
    SubscriptionId         = $sub.Id
    TotalVMs               = $subVMs.Count
    RunningVMs             = ($subVMs | Where-Object { $_.PowerState -eq 'VM running' }).Count
    IdleVMs                = ($subVMs | Where-Object { $_.UtilizationCategory -eq 'Idle' -and $_.PowerState -eq 'VM running' }).Count
    UnderutilizedVMs       = ($subVMs | Where-Object { $_.UtilizationCategory -eq 'Underutilized' -and $_.PowerState -eq 'VM running' }).Count
    OptimalVMs             = ($subVMs | Where-Object { $_.UtilizationCategory -eq 'Optimal' }).Count
    HighUtilizationVMs     = ($subVMs | Where-Object { $_.UtilizationCategory -in @('High','Critical') }).Count
    TotalDisks             = $subDisks.Count
    AdvisorRecommendations = $subRecs.Count
    HighFindings           = ($subFindings | Where-Object { $_.Severity -eq 'High' }).Count
    MediumFindings         = ($subFindings | Where-Object { $_.Severity -eq 'Medium' }).Count
    LowFindings            = ($subFindings | Where-Object { $_.Severity -eq 'Low' }).Count
    PotentialSavings       = ($subRecs | Where-Object { $_.AnnualSavings } | Measure-Object -Property AnnualSavings -Sum).Sum
    AuditDate              = Get-Date -Format 'yyyy-MM-dd HH:mm'
  }) | Out-Null
}

# ============================================================================
# EXPORT TO EXCEL
# ============================================================================

$outputFile = Join-Path $OutPath "Performance_Audit.xlsx"
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
Export-Sheet -Data $vmPerformance -WorksheetName 'VM Performance' -TableName 'VMPerformance'
Export-Sheet -Data $advisorRecs -WorksheetName 'Advisor Recommendations' -TableName 'AdvisorRecs'
Export-Sheet -Data $diskPerformance -WorksheetName 'Disk Performance' -TableName 'DiskPerformance'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Performance Audit Complete!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Subscriptions Audited: $($subscriptions.Count)"
Write-Host "Total VMs: $($vmPerformance.Count)"
Write-Host "  Idle/Underutilized: $(($vmPerformance | Where-Object { $_.UtilizationCategory -in @('Idle','Underutilized') -and $_.PowerState -eq 'VM running' }).Count)"
Write-Host "  Optimal: $(($vmPerformance | Where-Object { $_.UtilizationCategory -eq 'Optimal' }).Count)"
Write-Host "  High/Critical: $(($vmPerformance | Where-Object { $_.UtilizationCategory -in @('High','Critical') }).Count)"
Write-Host "Total Findings: $($findings.Count)"
Write-Host "  High: $(($findings | Where-Object { $_.Severity -eq 'High' }).Count)"
Write-Host "  Medium: $(($findings | Where-Object { $_.Severity -eq 'Medium' }).Count)"
Write-Host "  Low: $(($findings | Where-Object { $_.Severity -eq 'Low' }).Count)"
Write-Host "Advisor Recommendations: $($advisorRecs.Count)"
Write-Host "Output: $outputFile"
Write-Host ""
