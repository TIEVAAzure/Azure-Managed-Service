<#
.SYNOPSIS
  TIEVA Resource Inventory & Utilization Audit Script
  
.DESCRIPTION
  Comprehensive Azure resource audit for AMS customer meetings:
  - Full resource inventory with metadata
  - VM utilization metrics (CPU, Memory, Disk IOPS, Network)
  - Right-sizing assessment with confidence levels
  - Orphaned resources (unattached disks, unused PIPs, empty RGs)
  - Tag compliance analysis
  - Hybrid Benefit opportunities
  - Expiring secrets/certificates
  
  Outputs multi-sheet Excel workbook: Resource_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.PARAMETER IncludeUtilization
  Include VM utilization metrics from Azure Monitor (last 14 days). Requires additional API calls.
  
.PARAMETER UtilizationDays
  Number of days to look back for utilization metrics. Default 14, recommend 30 for better accuracy.
  
.EXAMPLE
  .\ResourceAudit.ps1
  
.EXAMPLE
  .\ResourceAudit.ps1 -SubscriptionIds @("sub-id-1","sub-id-2") -IncludeUtilization -UtilizationDays 30
  
.NOTES
  Requires: Az.Accounts, Az.Resources, Az.Compute, Az.Network, Az.Monitor, Az.KeyVault, ImportExcel modules
  Permissions: Reader on subscriptions, Key Vault Reader for certificate expiry checks
  
  For memory metrics: Azure Monitor Agent must be installed on VMs
  For accurate right-sizing: Recommend 30 days of data collection
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads",
  [switch]$IncludeUtilization,
  [int]$UtilizationDays = 14
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Resource Inventory & Utilization Audit" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
if ($IncludeUtilization) {
  Write-Host "Utilization collection: ENABLED ($UtilizationDays days)" -ForegroundColor Green
} else {
  Write-Host "Utilization collection: DISABLED (use -IncludeUtilization to enable)" -ForegroundColor Yellow
}
Write-Host ""

# ============================================================================
# MODULE CHECK
# ============================================================================

$requiredModules = @('Az.Accounts','Az.Resources','Az.Compute','Az.Network','Az.Monitor','Az.KeyVault','Az.Storage')
$missing = $requiredModules | Where-Object { -not (Get-Module -ListAvailable -Name $_) }
if ($missing) {
  Write-Warning "Missing modules: $($missing -join ', '). Some features may be limited."
}

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

function Get-ResourceAge {
  param([datetime]$CreatedTime)
  if (-not $CreatedTime) { return $null }
  $age = (Get-Date) - $CreatedTime
  if ($age.TotalDays -lt 1) { return "< 1 day" }
  if ($age.TotalDays -lt 30) { return "$([math]::Floor($age.TotalDays)) days" }
  if ($age.TotalDays -lt 365) { return "$([math]::Floor($age.TotalDays / 30)) months" }
  return "$([math]::Floor($age.TotalDays / 365)) years"
}

function Get-Percentile {
  param([array]$Values, [int]$Percentile = 95)
  if (-not $Values -or $Values.Count -eq 0) { return $null }
  $sorted = $Values | Sort-Object
  $index = [math]::Ceiling(($Percentile / 100) * $sorted.Count) - 1
  $index = [math]::Max(0, [math]::Min($index, $sorted.Count - 1))
  return $sorted[$index]
}

function Get-MetricValues {
  param($MetricResult)
  $values = @()
  if ($MetricResult) {
    if ($MetricResult.Data) {
      $values = $MetricResult.Data | Where-Object { $_.Average -ne $null } | ForEach-Object { $_.Average }
    }
    elseif ($MetricResult.Timeseries) {
      foreach ($ts in $MetricResult.Timeseries) {
        if ($ts.Data) {
          $values += $ts.Data | Where-Object { $_.Average -ne $null } | ForEach-Object { $_.Average }
        }
      }
    }
  }
  return $values
}

function Get-VMUtilizationMetrics {
  param(
    [Parameter(Mandatory)][string]$ResourceId,
    [Parameter(Mandatory)][string]$VMName,
    [int]$Days = 14
  )
  
  $endTime = Get-Date
  $startTime = $endTime.AddDays(-$Days)
  
  $metrics = [PSCustomObject]@{
    CpuAvgPercent = $null; CpuMaxPercent = $null; CpuP95Percent = $null; CpuDataPoints = 0
    MemAvgPercent = $null; MemMaxPercent = $null; MemAvailableGB = $null; MemDataPoints = 0
    DiskReadIOPS = $null; DiskWriteIOPS = $null; DiskTotalIOPS = $null
    DiskReadMBps = $null; DiskWriteMBps = $null; DiskDataPoints = 0
    NetworkInMBps = $null; NetworkOutMBps = $null; NetworkDataPoints = 0
    DataQuality = 'None'; DataDays = 0; MetricsCollected = $false
  }
  
  try {
    # CPU METRICS
    $cpuMetric = Get-AzMetric -ResourceId $ResourceId -MetricName "Percentage CPU" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    
    $cpuValues = Get-MetricValues $cpuMetric
    if ($cpuValues -and $cpuValues.Count -gt 0) {
      $metrics.CpuAvgPercent = [math]::Round(($cpuValues | Measure-Object -Average).Average, 1)
      $metrics.CpuMaxPercent = [math]::Round(($cpuValues | Measure-Object -Maximum).Maximum, 1)
      $metrics.CpuP95Percent = [math]::Round((Get-Percentile -Values $cpuValues -Percentile 95), 1)
      $metrics.CpuDataPoints = $cpuValues.Count
      $metrics.MetricsCollected = $true
    }
    
    # MEMORY METRICS (percentage)
    $memPctMetric = Get-AzMetric -ResourceId $ResourceId -MetricName "Available Memory Percentage" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    
    $memPctValues = Get-MetricValues $memPctMetric
    if ($memPctValues -and $memPctValues.Count -gt 0) {
      $avgAvail = ($memPctValues | Measure-Object -Average).Average
      $minAvail = ($memPctValues | Measure-Object -Minimum).Minimum
      $metrics.MemAvgPercent = [math]::Round(100 - $avgAvail, 1)
      $metrics.MemMaxPercent = [math]::Round(100 - $minAvail, 1)
      $metrics.MemDataPoints = $memPctValues.Count
    }
    
    # MEMORY METRICS (bytes - for reference)
    $memBytesMetric = Get-AzMetric -ResourceId $ResourceId -MetricName "Available Memory Bytes" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    
    $memBytesValues = Get-MetricValues $memBytesMetric
    if ($memBytesValues -and $memBytesValues.Count -gt 0) {
      $metrics.MemAvailableGB = [math]::Round(($memBytesValues | Measure-Object -Average).Average / 1GB, 2)
      if ($metrics.MemDataPoints -eq 0) { $metrics.MemDataPoints = $memBytesValues.Count }
    }
    
    # DISK METRICS
    $diskReadOps = Get-AzMetric -ResourceId $ResourceId -MetricName "Disk Read Operations/Sec" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    $diskReadOpsValues = Get-MetricValues $diskReadOps
    if ($diskReadOpsValues -and $diskReadOpsValues.Count -gt 0) {
      $metrics.DiskReadIOPS = [math]::Round(($diskReadOpsValues | Measure-Object -Average).Average, 0)
      $metrics.DiskDataPoints = $diskReadOpsValues.Count
    }
    
    $diskWriteOps = Get-AzMetric -ResourceId $ResourceId -MetricName "Disk Write Operations/Sec" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    $diskWriteOpsValues = Get-MetricValues $diskWriteOps
    if ($diskWriteOpsValues -and $diskWriteOpsValues.Count -gt 0) {
      $metrics.DiskWriteIOPS = [math]::Round(($diskWriteOpsValues | Measure-Object -Average).Average, 0)
    }
    
    if ($metrics.DiskReadIOPS -ne $null -or $metrics.DiskWriteIOPS -ne $null) {
      $metrics.DiskTotalIOPS = ($metrics.DiskReadIOPS ?? 0) + ($metrics.DiskWriteIOPS ?? 0)
    }
    
    $diskReadBytes = Get-AzMetric -ResourceId $ResourceId -MetricName "Disk Read Bytes/Sec" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    $diskReadBytesValues = Get-MetricValues $diskReadBytes
    if ($diskReadBytesValues -and $diskReadBytesValues.Count -gt 0) {
      $metrics.DiskReadMBps = [math]::Round(($diskReadBytesValues | Measure-Object -Average).Average / 1MB, 2)
    }
    
    $diskWriteBytes = Get-AzMetric -ResourceId $ResourceId -MetricName "Disk Write Bytes/Sec" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    $diskWriteBytesValues = Get-MetricValues $diskWriteBytes
    if ($diskWriteBytesValues -and $diskWriteBytesValues.Count -gt 0) {
      $metrics.DiskWriteMBps = [math]::Round(($diskWriteBytesValues | Measure-Object -Average).Average / 1MB, 2)
    }
    
    # NETWORK METRICS
    $netIn = Get-AzMetric -ResourceId $ResourceId -MetricName "Network In Total" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    $netInValues = Get-MetricValues $netIn
    if ($netInValues -and $netInValues.Count -gt 0) {
      $metrics.NetworkInMBps = [math]::Round((($netInValues | Measure-Object -Average).Average / 3600) / 1MB, 2)
      $metrics.NetworkDataPoints = $netInValues.Count
    }
    
    $netOut = Get-AzMetric -ResourceId $ResourceId -MetricName "Network Out Total" `
      -StartTime $startTime -EndTime $endTime -TimeGrain 01:00:00 `
      -AggregationType Average -ErrorAction SilentlyContinue
    $netOutValues = Get-MetricValues $netOut
    if ($netOutValues -and $netOutValues.Count -gt 0) {
      $metrics.NetworkOutMBps = [math]::Round((($netOutValues | Measure-Object -Average).Average / 3600) / 1MB, 2)
    }
    
    # DATA QUALITY ASSESSMENT
    $maxDataPoints = $Days * 24
    $cpuCoverage = if ($metrics.CpuDataPoints -gt 0) { $metrics.CpuDataPoints / $maxDataPoints } else { 0 }
    
    if ($cpuCoverage -ge 0.8) {
      $metrics.DataQuality = 'High'
      $metrics.DataDays = [math]::Round($metrics.CpuDataPoints / 24, 0)
    } elseif ($cpuCoverage -ge 0.5) {
      $metrics.DataQuality = 'Medium'
      $metrics.DataDays = [math]::Round($metrics.CpuDataPoints / 24, 0)
    } elseif ($cpuCoverage -gt 0) {
      $metrics.DataQuality = 'Low'
      $metrics.DataDays = [math]::Round($metrics.CpuDataPoints / 24, 0)
    }
    
  } catch {
    Write-Verbose "Could not retrieve metrics for $VMName : $_"
  }
  
  return $metrics
}

function Get-RightSizeAssessment {
  param(
    [Parameter(Mandatory)]$Metrics,
    [Parameter(Mandatory)][string]$VMSize,
    [string]$OSType = 'Unknown'
  )
  
  $flags = @()
  $reviewReasons = @()
  $confidence = 'None'
  
  if (-not $Metrics.MetricsCollected -or $Metrics.DataQuality -eq 'None') {
    return [PSCustomObject]@{
      ReviewRecommended = $false
      Confidence = 'None'
      Assessment = 'Insufficient Data'
      Flags = 'NO_DATA'
      Reasons = 'No metrics data available. Ensure VM is running and has been running during the collection period.'
    }
  }
  
  $hasMemoryData = $Metrics.MemAvgPercent -ne $null
  
  $confidence = switch ($Metrics.DataQuality) {
    'High'   { if ($hasMemoryData) { 'High' } else { 'Medium' } }
    'Medium' { if ($hasMemoryData) { 'Medium' } else { 'Low' } }
    'Low'    { 'Low' }
    default  { 'None' }
  }
  
  $reviewRecommended = $false
  
  # CPU ANALYSIS
  if ($Metrics.CpuAvgPercent -ne $null) {
    if ($Metrics.CpuAvgPercent -lt 5 -and $Metrics.CpuMaxPercent -lt 20) {
      $flags += 'CPU_VERY_LOW'
      $reviewReasons += "CPU critically underutilized (Avg: $($Metrics.CpuAvgPercent)%, Max: $($Metrics.CpuMaxPercent)%, P95: $($Metrics.CpuP95Percent)%)"
      $reviewRecommended = $true
    }
    elseif ($Metrics.CpuAvgPercent -lt 20 -and $Metrics.CpuMaxPercent -lt 50 -and $Metrics.CpuP95Percent -lt 40) {
      $flags += 'CPU_LOW'
      $reviewReasons += "CPU low utilization (Avg: $($Metrics.CpuAvgPercent)%, Max: $($Metrics.CpuMaxPercent)%, P95: $($Metrics.CpuP95Percent)%)"
      $reviewRecommended = $true
    }
    elseif ($Metrics.CpuAvgPercent -gt 80 -or $Metrics.CpuP95Percent -gt 90) {
      $flags += 'CPU_HIGH'
      $reviewReasons += "CPU high utilization - potential upsize needed (Avg: $($Metrics.CpuAvgPercent)%, Max: $($Metrics.CpuMaxPercent)%, P95: $($Metrics.CpuP95Percent)%)"
      $reviewRecommended = $true
    }
  }
  
  # MEMORY ANALYSIS
  if ($Metrics.MemAvgPercent -ne $null) {
    if ($Metrics.MemAvgPercent -lt 20 -and $Metrics.MemMaxPercent -lt 40) {
      $flags += 'MEM_LOW'
      $reviewReasons += "Memory low utilization (Avg: $($Metrics.MemAvgPercent)%, Max: $($Metrics.MemMaxPercent)%)"
      $reviewRecommended = $true
    }
    elseif ($Metrics.MemAvgPercent -gt 85 -or $Metrics.MemMaxPercent -gt 95) {
      $flags += 'MEM_HIGH'
      $reviewReasons += "Memory high utilization - potential upsize needed (Avg: $($Metrics.MemAvgPercent)%, Max: $($Metrics.MemMaxPercent)%)"
      $reviewRecommended = $true
    }
  } else {
    $flags += 'MEM_NO_DATA'
    $reviewReasons += "Memory metrics unavailable (Azure Monitor Agent required)"
  }
  
  # DISK ANALYSIS
  if ($Metrics.DiskTotalIOPS -ne $null -and $Metrics.DiskTotalIOPS -lt 50) {
    $flags += 'DISK_VERY_LOW_IOPS'
    $reviewReasons += "Disk IOPS very low (Total: $($Metrics.DiskTotalIOPS) IOPS) - review if Premium disks are justified"
  }
  
  # BURSTABLE VM CHECK
  if ($VMSize -match '^Standard_B') {
    $flags += 'BURSTABLE_VM'
    $reviewReasons += "B-series (burstable) VM - verify CPU credits are not being depleted"
  }
  
  # DETERMINE ASSESSMENT
  $assessment = 'OK - No Action Required'
  
  if ($flags -contains 'CPU_VERY_LOW') {
    if ($flags -contains 'MEM_LOW') { $assessment = 'Strong Downsize Candidate' }
    elseif ($flags -contains 'MEM_NO_DATA') { $assessment = 'Likely Downsize Candidate (verify memory)' }
    else { $assessment = 'Review - CPU Low but Memory OK' }
  }
  elseif ($flags -contains 'CPU_LOW') {
    if ($flags -contains 'MEM_LOW') { $assessment = 'Moderate Downsize Candidate' }
    elseif ($flags -contains 'MEM_NO_DATA') { $assessment = 'Possible Downsize (verify memory)' }
    else { $assessment = 'Review - CPU Low but Memory OK' }
  }
  elseif ($flags -contains 'CPU_HIGH' -and $flags -contains 'MEM_HIGH') {
    $assessment = 'Strong Upsize Candidate'
  }
  elseif ($flags -contains 'CPU_HIGH' -or $flags -contains 'MEM_HIGH') {
    $assessment = 'Possible Upsize Candidate'
  }
  elseif ($flags -contains 'MEM_NO_DATA') {
    $assessment = 'Incomplete Data - Install Azure Monitor Agent'
  }
  
  return [PSCustomObject]@{
    ReviewRecommended = $reviewRecommended
    Confidence = $confidence
    Assessment = $assessment
    Flags = ($flags -join ', ')
    Reasons = ($reviewReasons -join ' | ')
  }
}

# ============================================================================
# DATA COLLECTIONS
# ============================================================================

$resourceInventory = [System.Collections.Generic.List[object]]::new()
$vmDetails = [System.Collections.Generic.List[object]]::new()
$orphanedResources = [System.Collections.Generic.List[object]]::new()
$tagCompliance = [System.Collections.Generic.List[object]]::new()
$hybridBenefit = [System.Collections.Generic.List[object]]::new()
$expiringSecrets = [System.Collections.Generic.List[object]]::new()
$subscriptionSummary = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[object]]::new()

$standardTags = @('Environment', 'Owner', 'CostCenter', 'Project', 'Department', 'Application', 'BusinessUnit')

# ============================================================================
# MAIN AUDIT LOOP
# ============================================================================

$subscriptions = Get-SubscriptionList
if (-not $subscriptions) { Write-Error "No accessible subscriptions found."; exit 1 }

Write-Host "Found $($subscriptions.Count) subscription(s) to audit`n" -ForegroundColor Green

foreach ($sub in $subscriptions) {
  Write-Host "Processing: $($sub.Name)" -ForegroundColor Yellow
  
  try { Set-AzContext -SubscriptionId $sub.Id -ErrorAction Stop | Out-Null }
  catch { Write-Warning "  Could not set context: $_"; continue }
  
  $subResourceCount = 0; $subTaggedCount = 0; $subOrphanedCount = 0
  $subVMCount = 0; $subAHUBCount = 0
  
  # RESOURCE INVENTORY
  Write-Host "  -> Collecting resource inventory..." -NoNewline
  $resources = @()
  try { $resources = Get-AzResource -ErrorAction SilentlyContinue } catch {}
  Write-Host " $($resources.Count) resources" -ForegroundColor Cyan
  $subResourceCount = $resources.Count
  
  foreach ($res in $resources) {
    $tagCount = if ($res.Tags) { $res.Tags.Count } else { 0 }
    $tagKeys = if ($res.Tags) { ($res.Tags.Keys -join ', ') } else { '' }
    if ($tagCount -gt 0) { $subTaggedCount++ }
    
    $hasStandardTags = $false; $missingTags = @()
    if ($res.Tags) {
      foreach ($tag in $standardTags) {
        if ($res.Tags.ContainsKey($tag)) { $hasStandardTags = $true } else { $missingTags += $tag }
      }
    } else { $missingTags = $standardTags }
    
    $resourceInventory.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name; SubscriptionId = $sub.Id
      ResourceGroup = $res.ResourceGroupName; ResourceName = $res.Name
      ResourceType = $res.ResourceType; Location = $res.Location
      SKU = $res.Sku.Name; Kind = $res.Kind; TagCount = $tagCount
      Tags = $tagKeys; HasStandardTags = $hasStandardTags
      MissingTags = ($missingTags -join ', '); ResourceId = $res.ResourceId
    })
  }
  
  # VM DETAILS & UTILIZATION
  Write-Host "  -> Analyzing virtual machines..." -NoNewline
  $vms = @()
  try { $vms = Get-AzVM -Status -ErrorAction SilentlyContinue } catch {}
  Write-Host " $($vms.Count) VMs" -ForegroundColor Cyan
  $subVMCount = $vms.Count
  
  $vmCounter = 0
  foreach ($vm in $vms) {
    $vmCounter++
    
    # Power state
    $powerState = $null
    if ($vm.PowerState) { $powerState = $vm.PowerState -replace '^VM ', '' }
    elseif ($vm.Statuses) {
      $psStatus = $vm.Statuses | Where-Object { $_.Code -like 'PowerState/*' } | Select-Object -First 1
      if ($psStatus) {
        $powerState = $psStatus.DisplayStatus
        if (-not $powerState) { $powerState = $psStatus.Code -replace 'PowerState/', '' }
      }
    }
    if (-not $powerState) { $powerState = 'Unknown' }
    
    $osType = $vm.StorageProfile.OsDisk.OsType
    $vmSize = $vm.HardwareProfile.VmSize
    
    # Hybrid Benefit
    $licenseType = $vm.LicenseType
    $ahubEnabled = $false; $ahubEligible = $false
    if ($osType -eq 'Windows') {
      $ahubEligible = $true
      if ($licenseType -eq 'Windows_Server' -or $licenseType -eq 'Windows_Client') {
        $ahubEnabled = $true; $subAHUBCount++
      }
    }
    
    $osDiskSize = $vm.StorageProfile.OsDisk.DiskSizeGB
    $dataDiskCount = $vm.StorageProfile.DataDisks.Count
    $totalDataDiskSize = ($vm.StorageProfile.DataDisks | Measure-Object -Property DiskSizeGB -Sum).Sum
    
    $metrics = $null; $assessment = $null
    
    if ($IncludeUtilization) {
      if ($powerState -match 'running|started') {
        Write-Host "`r  -> Analyzing virtual machines... $vmCounter/$($vms.Count) - $($vm.Name)          " -NoNewline
        $metrics = Get-VMUtilizationMetrics -ResourceId $vm.Id -VMName $vm.Name -Days $UtilizationDays
        $assessment = Get-RightSizeAssessment -Metrics $metrics -VMSize $vmSize -OSType $osType
      } else {
        $metrics = [PSCustomObject]@{
          CpuAvgPercent=$null;CpuMaxPercent=$null;CpuP95Percent=$null;CpuDataPoints=0
          MemAvgPercent=$null;MemMaxPercent=$null;MemAvailableGB=$null;MemDataPoints=0
          DiskReadIOPS=$null;DiskWriteIOPS=$null;DiskTotalIOPS=$null
          DiskReadMBps=$null;DiskWriteMBps=$null;DiskDataPoints=0
          NetworkInMBps=$null;NetworkOutMBps=$null;NetworkDataPoints=0
          DataQuality='N/A';DataDays=0;MetricsCollected=$false
        }
        $assessment = [PSCustomObject]@{
          ReviewRecommended=$false;Confidence='None'
          Assessment="VM Not Running ($powerState)";Flags='VM_STOPPED'
          Reasons="VM is not running - no metrics available"
        }
      }
    } else {
      $metrics = [PSCustomObject]@{
        CpuAvgPercent=$null;CpuMaxPercent=$null;CpuP95Percent=$null;CpuDataPoints=0
        MemAvgPercent=$null;MemMaxPercent=$null;MemAvailableGB=$null;MemDataPoints=0
        DiskReadIOPS=$null;DiskWriteIOPS=$null;DiskTotalIOPS=$null
        DiskReadMBps=$null;DiskWriteMBps=$null;DiskDataPoints=0
        NetworkInMBps=$null;NetworkOutMBps=$null;NetworkDataPoints=0
        DataQuality='Not Collected';DataDays=0;MetricsCollected=$false
      }
      $assessment = [PSCustomObject]@{
        ReviewRecommended=$false;Confidence='None'
        Assessment='Metrics Not Collected';Flags='NO_COLLECTION'
        Reasons='Run with -IncludeUtilization to collect metrics'
      }
    }
    
    $vmDetails.Add([PSCustomObject]@{
      SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
      ResourceGroup=$vm.ResourceGroupName;VMName=$vm.Name;Location=$vm.Location
      VMSize=$vmSize;OSType=$osType;PowerState=$powerState
      OSDiskSizeGB=$osDiskSize;DataDiskCount=$dataDiskCount;TotalDataDiskGB=$totalDataDiskSize
      AHUBEligible=$ahubEligible;AHUBEnabled=$ahubEnabled;LicenseType=$licenseType
      CpuAvgPercent=$metrics.CpuAvgPercent;CpuMaxPercent=$metrics.CpuMaxPercent;CpuP95Percent=$metrics.CpuP95Percent
      MemAvgPercent=$metrics.MemAvgPercent;MemMaxPercent=$metrics.MemMaxPercent;MemAvailableGB=$metrics.MemAvailableGB
      DiskReadIOPS=$metrics.DiskReadIOPS;DiskWriteIOPS=$metrics.DiskWriteIOPS;DiskTotalIOPS=$metrics.DiskTotalIOPS
      DiskReadMBps=$metrics.DiskReadMBps;DiskWriteMBps=$metrics.DiskWriteMBps
      NetworkInMBps=$metrics.NetworkInMBps;NetworkOutMBps=$metrics.NetworkOutMBps
      DataQuality=$metrics.DataQuality;DataDays=$metrics.DataDays
      ReviewRecommended=$assessment.ReviewRecommended
      AssessmentConfidence=$assessment.Confidence;Assessment=$assessment.Assessment
      AssessmentFlags=$assessment.Flags;AssessmentReasons=$assessment.Reasons
    })
    
    # Finding: Review recommended
    if ($assessment.ReviewRecommended) {
      $severity = switch -Regex ($assessment.Assessment) {
        'Strong.*Downsize|Strong.*Upsize' { 'High' }
        'Moderate|Possible|Likely' { 'Medium' }
        default { 'Low' }
      }
      $findings.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id;Severity=$severity
        Category='Right-Sizing';ResourceType='Virtual Machine'
        ResourceName=$vm.Name;ResourceGroup=$vm.ResourceGroupName
        Detail=$assessment.Assessment;Recommendation=$assessment.Reasons
      })
    }
    
    # Finding: AHUB not enabled
    if ($ahubEligible -and -not $ahubEnabled -and $powerState -match 'running') {
      $findings.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id;Severity='Medium'
        Category='Cost Optimization';ResourceType='Virtual Machine'
        ResourceName=$vm.Name;ResourceGroup=$vm.ResourceGroupName
        Detail='Windows VM not using Azure Hybrid Benefit'
        Recommendation='Enable AHUB to save up to 40% on Windows Server licensing'
      })
    }
  }
  
  if ($IncludeUtilization -and $vms.Count -gt 0) {
    Write-Host "`r  -> Analyzing virtual machines... $($vms.Count) VMs completed          " -ForegroundColor Green
  }
  
  # Hybrid Benefit summary
  if ($subVMCount -gt 0) {
    $windowsVMs = $vms | Where-Object { $_.StorageProfile.OsDisk.OsType -eq 'Windows' }
    $windowsCount = $windowsVMs.Count
    $ahubCount = ($windowsVMs | Where-Object { $_.LicenseType -eq 'Windows_Server' -or $_.LicenseType -eq 'Windows_Client' }).Count
    if ($windowsCount -gt 0) {
      $hybridBenefit.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
        TotalWindowsVMs=$windowsCount;AHUBEnabled=$ahubCount
        AHUBNotEnabled=$windowsCount-$ahubCount
        AHUBCoveragePercent=[math]::Round(($ahubCount/$windowsCount)*100,1)
      })
    }
  }
  
  # ORPHANED RESOURCES
  Write-Host "  -> Checking for orphaned resources..." -NoNewline
  $orphanCount = 0
  
  # Unattached disks
  try {
    $disks = Get-AzDisk -ErrorAction SilentlyContinue
    $unattachedDisks = $disks | Where-Object { $_.DiskState -eq 'Unattached' -or ($_.ManagedBy -eq $null -and $_.DiskState -ne 'Reserved') }
    foreach ($disk in $unattachedDisks) {
      $orphanCount++; $subOrphanedCount++
      $orphanedResources.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
        ResourceGroup=$disk.ResourceGroupName;ResourceName=$disk.Name
        ResourceType='Managed Disk';SizeGB=$disk.DiskSizeGB;SKU=$disk.Sku.Name
        State=$disk.DiskState;EstimatedCost="~GBP$([math]::Round($disk.DiskSizeGB*0.04,2))/month"
      })
      $findings.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id;Severity='Medium'
        Category='Orphaned Resource';ResourceType='Managed Disk'
        ResourceName=$disk.Name;ResourceGroup=$disk.ResourceGroupName
        Detail="Unattached disk ($($disk.DiskSizeGB) GB, $($disk.Sku.Name))"
        Recommendation='Delete disk or attach to VM'
      })
    }
  } catch {}
  
  # Unused public IPs
  try {
    $pips = Get-AzPublicIpAddress -ErrorAction SilentlyContinue
    $unusedPips = $pips | Where-Object { $_.IpConfiguration -eq $null }
    foreach ($pip in $unusedPips) {
      $orphanCount++; $subOrphanedCount++
      $monthlyCost = if ($pip.Sku.Name -eq 'Standard') { 3.65 } else { 2.92 }
      $orphanedResources.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
        ResourceGroup=$pip.ResourceGroupName;ResourceName=$pip.Name
        ResourceType='Public IP';SizeGB=$null;SKU=$pip.Sku.Name
        State='Unassociated';EstimatedCost="~GBP$monthlyCost/month"
      })
      $findings.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id;Severity='Low'
        Category='Orphaned Resource';ResourceType='Public IP'
        ResourceName=$pip.Name;ResourceGroup=$pip.ResourceGroupName
        Detail="Unassociated public IP ($($pip.Sku.Name))"
        Recommendation='Delete if not needed'
      })
    }
  } catch {}
  
  # Unused NICs
  try {
    $nics = Get-AzNetworkInterface -ErrorAction SilentlyContinue
    $unusedNics = $nics | Where-Object { $_.VirtualMachine -eq $null }
    foreach ($nic in $unusedNics) {
      $orphanCount++; $subOrphanedCount++
      $orphanedResources.Add([PSCustomObject]@{
        SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
        ResourceGroup=$nic.ResourceGroupName;ResourceName=$nic.Name
        ResourceType='Network Interface';SizeGB=$null;SKU=$null
        State='Unattached';EstimatedCost='Free (clutters environment)'
      })
    }
  } catch {}
  
  # Empty Resource Groups
  try {
    $rgs = Get-AzResourceGroup -ErrorAction SilentlyContinue
    foreach ($rg in $rgs) {
      $rgResources = Get-AzResource -ResourceGroupName $rg.ResourceGroupName -ErrorAction SilentlyContinue
      if (-not $rgResources -or $rgResources.Count -eq 0) {
        $orphanCount++; $subOrphanedCount++
        $orphanedResources.Add([PSCustomObject]@{
          SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
          ResourceGroup=$rg.ResourceGroupName;ResourceName=$rg.ResourceGroupName
          ResourceType='Empty Resource Group';SizeGB=$null;SKU=$null
          State='Empty';EstimatedCost='Free (clutters environment)'
        })
      }
    }
  } catch {}
  
  Write-Host " $orphanCount found" -ForegroundColor $(if ($orphanCount -gt 0) { 'Yellow' } else { 'Green' })
  
  # TAG COMPLIANCE
  Write-Host "  -> Analyzing tag compliance..." -NoNewline
  $taggedPct = if ($subResourceCount -gt 0) { [math]::Round(($subTaggedCount/$subResourceCount)*100,1) } else { 0 }
  
  $tagCounts = @{}
  foreach ($tag in $standardTags) {
    $count = ($resourceInventory | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Tags -match $tag }).Count
    $tagCounts[$tag] = $count
  }
  
  $tagCompliance.Add([PSCustomObject]@{
    SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
    TotalResources=$subResourceCount;ResourcesWithTags=$subTaggedCount
    TagCoveragePercent=$taggedPct
    EnvironmentTag=$tagCounts['Environment'];OwnerTag=$tagCounts['Owner']
    CostCenterTag=$tagCounts['CostCenter'];ProjectTag=$tagCounts['Project']
  })
  
  Write-Host " $taggedPct% coverage" -ForegroundColor $(if ($taggedPct -ge 80) { 'Green' } elseif ($taggedPct -ge 50) { 'Yellow' } else { 'Red' })
  
  if ($taggedPct -lt 50) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName=$sub.Name;SubscriptionId=$sub.Id;Severity='High'
      Category='Governance';ResourceType='Subscription'
      ResourceName=$sub.Name;ResourceGroup='N/A'
      Detail="Only $taggedPct% of resources have tags"
      Recommendation='Implement Azure Policy to enforce tagging'
    })
  }
  
  # EXPIRING SECRETS
  Write-Host "  -> Checking for expiring secrets..." -NoNewline
  $expiryCount = 0
  
  try {
    $keyVaults = Get-AzKeyVault -ErrorAction SilentlyContinue
    foreach ($kv in $keyVaults) {
      try {
        $certs = Get-AzKeyVaultCertificate -VaultName $kv.VaultName -ErrorAction SilentlyContinue
        foreach ($cert in $certs) {
          $certDetails = Get-AzKeyVaultCertificate -VaultName $kv.VaultName -Name $cert.Name -ErrorAction SilentlyContinue
          if ($certDetails.Expires) {
            $daysToExpiry = ($certDetails.Expires - (Get-Date)).Days
            if ($daysToExpiry -le 90) {
              $expiryCount++
              $severity = if ($daysToExpiry -le 30) { 'High' } else { 'Medium' }
              $expiringSecrets.Add([PSCustomObject]@{
                SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
                KeyVaultName=$kv.VaultName;ResourceGroup=$kv.ResourceGroupName
                SecretType='Certificate';SecretName=$cert.Name
                ExpiryDate=$certDetails.Expires;DaysToExpiry=$daysToExpiry
                Status=if ($daysToExpiry -le 0) { 'EXPIRED' } elseif ($daysToExpiry -le 30) { 'Critical' } else { 'Warning' }
              })
              $findings.Add([PSCustomObject]@{
                SubscriptionName=$sub.Name;SubscriptionId=$sub.Id;Severity=$severity
                Category='Security';ResourceType='Key Vault Certificate'
                ResourceName="$($kv.VaultName)/$($cert.Name)";ResourceGroup=$kv.ResourceGroupName
                Detail=if ($daysToExpiry -le 0) { "Certificate EXPIRED" } else { "Certificate expires in $daysToExpiry days" }
                Recommendation='Renew certificate before expiry'
              })
            }
          }
        }
      } catch {}
    }
  } catch {}
  
  Write-Host " $expiryCount expiring soon" -ForegroundColor $(if ($expiryCount -gt 0) { 'Yellow' } else { 'Green' })
  
  # SUBSCRIPTION SUMMARY
  $highFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'High' }).Count
  $medFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Medium' }).Count
  $lowFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Low' }).Count
  
  $subscriptionSummary.Add([PSCustomObject]@{
    SubscriptionName=$sub.Name;SubscriptionId=$sub.Id
    TotalResources=$subResourceCount;VirtualMachines=$subVMCount
    TagCoveragePercent=$taggedPct;OrphanedResources=$subOrphanedCount
    AHUBEnabled=$subAHUBCount;ExpiringSecrets=$expiryCount
    HighFindings=$highFindings;MediumFindings=$medFindings;LowFindings=$lowFindings
    TotalFindings=$highFindings+$medFindings+$lowFindings
  })
  
  Write-Host ""
}

# ============================================================================
# CONSOLE OUTPUT
# ============================================================================

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "AUDIT COMPLETE" -ForegroundColor Green
Write-Host "==========================================`n" -ForegroundColor Green

Write-Host "=== Subscription Summary ===" -ForegroundColor Cyan
if ($subscriptionSummary.Count -gt 0) {
  $subscriptionSummary | Format-Table SubscriptionName, TotalResources, VirtualMachines, TagCoveragePercent, OrphanedResources, TotalFindings -AutoSize
}

Write-Host "`n=== VM Right-Sizing Summary ===" -ForegroundColor Cyan
if ($IncludeUtilization) {
  $reviewVMs = $vmDetails | Where-Object { $_.ReviewRecommended -eq $true }
  $strongDownsize = $vmDetails | Where-Object { $_.Assessment -match 'Strong Downsize' }
  Write-Host "  VMs requiring review:    $($reviewVMs.Count)" -ForegroundColor $(if ($reviewVMs.Count -gt 0) { 'Yellow' } else { 'Gray' })
  Write-Host "  Strong downsize:         $($strongDownsize.Count)" -ForegroundColor $(if ($strongDownsize.Count -gt 0) { 'Red' } else { 'Gray' })
  
  if ($strongDownsize.Count -gt 0) {
    Write-Host "`n  Top Strong Downsize Candidates:" -ForegroundColor Yellow
    $strongDownsize | Select-Object -First 5 | ForEach-Object {
      Write-Host "    - $($_.VMName) ($($_.VMSize)): CPU Avg $($_.CpuAvgPercent)%, Max $($_.CpuMaxPercent)%" -ForegroundColor White
    }
  }
} else {
  Write-Host "  Run with -IncludeUtilization to see right-sizing recommendations" -ForegroundColor Yellow
}

Write-Host "`n=== Findings Summary ===" -ForegroundColor Cyan
$totalHigh = ($findings | Where-Object Severity -eq 'High').Count
$totalMed = ($findings | Where-Object Severity -eq 'Medium').Count
$totalLow = ($findings | Where-Object Severity -eq 'Low').Count
Write-Host "  High Priority:   $totalHigh" -ForegroundColor $(if ($totalHigh -gt 0) { 'Red' } else { 'Gray' })
Write-Host "  Medium Priority: $totalMed" -ForegroundColor $(if ($totalMed -gt 0) { 'Yellow' } else { 'Gray' })
Write-Host "  Low Priority:    $totalLow" -ForegroundColor Gray

# ============================================================================
# EXCEL EXPORT
# ============================================================================

$XlsxPath = Join-Path $OutPath 'Resource_Audit.xlsx'

Write-Host "`n=== Exporting to Excel ===" -ForegroundColor Cyan

if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
  Write-Host "Installing ImportExcel module..." -ForegroundColor Yellow
  try {
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue | Out-Null
    Install-Module ImportExcel -Scope CurrentUser -Force -ErrorAction Stop
  } catch {
    Write-Warning "Could not install ImportExcel: $_"
    exit 1
  }
}
Import-Module ImportExcel -ErrorAction Stop

if (Test-Path $XlsxPath) { Remove-Item $XlsxPath -Force }

function Export-Sheet { param($Data, $WorksheetName, $TableName)
  if (-not $Data -or $Data.Count -eq 0) { Write-Host "  Skipping empty: $WorksheetName" -ForegroundColor Gray; return }
  $Data | Export-Excel -Path $XlsxPath -WorksheetName $WorksheetName -TableName $TableName -TableStyle 'Medium9' -AutoSize -FreezeTopRow -BoldTopRow
  Write-Host "  + $WorksheetName ($($Data.Count) rows)" -ForegroundColor Green
}

Export-Sheet -Data $subscriptionSummary -WorksheetName 'Subscription_Summary' -TableName 'Subscriptions'
Export-Sheet -Data $vmDetails -WorksheetName 'VM_Details' -TableName 'VMs'
Export-Sheet -Data $resourceInventory -WorksheetName 'Resource_Inventory' -TableName 'Resources'
Export-Sheet -Data $orphanedResources -WorksheetName 'Orphaned_Resources' -TableName 'Orphaned'
Export-Sheet -Data $tagCompliance -WorksheetName 'Tag_Compliance' -TableName 'Tags'
Export-Sheet -Data $hybridBenefit -WorksheetName 'Hybrid_Benefit' -TableName 'AHUB'
Export-Sheet -Data $expiringSecrets -WorksheetName 'Expiring_Secrets' -TableName 'Secrets'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

$overallSummary = @(
  [PSCustomObject]@{ Metric='Audit Date';Value=(Get-Date -Format 'yyyy-MM-dd HH:mm') }
  [PSCustomObject]@{ Metric='Utilization Period';Value=if ($IncludeUtilization) { "$UtilizationDays days" } else { "Not collected" } }
  [PSCustomObject]@{ Metric='Subscriptions Audited';Value=$subscriptions.Count }
  [PSCustomObject]@{ Metric='Total Resources';Value=$resourceInventory.Count }
  [PSCustomObject]@{ Metric='Virtual Machines';Value=$vmDetails.Count }
  [PSCustomObject]@{ Metric='VMs Needing Review';Value=($vmDetails | Where-Object { $_.ReviewRecommended }).Count }
  [PSCustomObject]@{ Metric='Orphaned Resources';Value=$orphanedResources.Count }
  [PSCustomObject]@{ Metric='High Findings';Value=$totalHigh }
  [PSCustomObject]@{ Metric='Medium Findings';Value=$totalMed }
  [PSCustomObject]@{ Metric='Low Findings';Value=$totalLow }
)
Export-Sheet -Data $overallSummary -WorksheetName 'Summary' -TableName 'Summary'

Write-Host "`nExcel export complete -> $XlsxPath" -ForegroundColor Green
Write-Host "`n+ Audit complete!" -ForegroundColor Green
Write-Host "Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
Write-Host "`nFor better right-sizing confidence:" -ForegroundColor Yellow
Write-Host "  - Use -UtilizationDays 30 for more data" -ForegroundColor White
Write-Host "  - Install Azure Monitor Agent for memory metrics`n" -ForegroundColor White
