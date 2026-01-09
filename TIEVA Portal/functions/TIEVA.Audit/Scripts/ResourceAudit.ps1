<#
.SYNOPSIS
  TIEVA Resource Inventory Audit Script
  
.DESCRIPTION
  Azure resource inventory audit:
  - Full resource inventory with metadata
  - VM inventory (basic info, no utilization metrics)
  - Orphaned resources (unattached disks, unused PIPs, empty RGs)
  - Tag compliance analysis
  - Hybrid Benefit opportunities
  - Expiring secrets/certificates
  
  Note: For VM performance metrics and right-sizing, use PerformanceAudit.ps1
  
  Outputs multi-sheet Excel workbook: Resource_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.EXAMPLE
  .\ResourceAudit.ps1
  
.EXAMPLE
  .\ResourceAudit.ps1 -SubscriptionIds @("sub-id-1","sub-id-2")
  
.NOTES
  Requires: Az.Accounts, Az.Resources, Az.Compute, Az.Network, Az.KeyVault, ImportExcel modules
  Permissions: Reader on subscriptions, Key Vault Reader for certificate expiry checks
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads"
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Resource Inventory Audit" -ForegroundColor Cyan
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
  
  # VM DETAILS (basic inventory only - no utilization metrics)
  Write-Host "  -> Collecting VM inventory..." -NoNewline
  $vms = @()
  try { $vms = Get-AzVM -Status -ErrorAction SilentlyContinue } catch {}
  Write-Host " $($vms.Count) VMs" -ForegroundColor Cyan
  $subVMCount = $vms.Count
  
  foreach ($vm in $vms) {
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
    
    $vmDetails.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      ResourceGroup    = $vm.ResourceGroupName
      VMName           = $vm.Name
      Location         = $vm.Location
      VMSize           = $vmSize
      OSType           = $osType
      PowerState       = $powerState
      OSDiskSizeGB     = $osDiskSize
      DataDiskCount    = $dataDiskCount
      TotalDataDiskGB  = $totalDataDiskSize
      AHUBEligible     = $ahubEligible
      AHUBEnabled      = $ahubEnabled
      LicenseType      = $licenseType
    })
    
    # Finding: AHUB not enabled
    if ($ahubEligible -and -not $ahubEnabled -and $powerState -match 'running') {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Cost Optimization'
        ResourceType     = 'Virtual Machine'
        ResourceName     = $vm.Name
        ResourceId       = $vm.Id
        Detail           = 'Windows VM not using Azure Hybrid Benefit'
        Recommendation   = 'Enable AHUB to save up to 40% on Windows Server licensing'
      })
    }
  }
  
  # Hybrid Benefit summary
  if ($subVMCount -gt 0) {
    $windowsVMs = $vms | Where-Object { $_.StorageProfile.OsDisk.OsType -eq 'Windows' }
    $windowsCount = $windowsVMs.Count
    $ahubCount = ($windowsVMs | Where-Object { $_.LicenseType -eq 'Windows_Server' -or $_.LicenseType -eq 'Windows_Client' }).Count
    if ($windowsCount -gt 0) {
      $hybridBenefit.Add([PSCustomObject]@{
        SubscriptionName    = $sub.Name
        SubscriptionId      = $sub.Id
        TotalWindowsVMs     = $windowsCount
        AHUBEnabled         = $ahubCount
        AHUBNotEnabled      = $windowsCount - $ahubCount
        AHUBCoveragePercent = [math]::Round(($ahubCount / $windowsCount) * 100, 1)
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
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        ResourceGroup    = $disk.ResourceGroupName
        ResourceName     = $disk.Name
        ResourceType     = 'Managed Disk'
        SizeGB           = $disk.DiskSizeGB
        SKU              = $disk.Sku.Name
        State            = $disk.DiskState
        EstimatedCost    = "~GBP$([math]::Round($disk.DiskSizeGB * 0.04, 2))/month"
      })
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Orphaned Resource'
        ResourceType     = 'Managed Disk'
        ResourceName     = $disk.Name
        ResourceId       = $disk.Id
        Detail           = "Unattached disk ($($disk.DiskSizeGB) GB, $($disk.Sku.Name))"
        Recommendation   = 'Delete disk or attach to VM'
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
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        ResourceGroup    = $pip.ResourceGroupName
        ResourceName     = $pip.Name
        ResourceType     = 'Public IP'
        SizeGB           = $null
        SKU              = $pip.Sku.Name
        State            = 'Unassociated'
        EstimatedCost    = "~GBP$monthlyCost/month"
      })
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Low'
        Category         = 'Orphaned Resource'
        ResourceType     = 'Public IP'
        ResourceName     = $pip.Name
        ResourceId       = $pip.Id
        Detail           = "Unassociated public IP ($($pip.Sku.Name))"
        Recommendation   = 'Delete if not needed'
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
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        ResourceGroup    = $nic.ResourceGroupName
        ResourceName     = $nic.Name
        ResourceType     = 'Network Interface'
        SizeGB           = $null
        SKU              = $null
        State            = 'Unattached'
        EstimatedCost    = 'Free (clutters environment)'
      })
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Low'
        Category         = 'Orphaned Resource'
        ResourceType     = 'Network Interface'
        ResourceName     = $nic.Name
        ResourceId       = $nic.Id
        Detail           = "Unattached Network Interface - not associated with any VM"
        Recommendation   = 'Delete if no longer needed to reduce environment clutter'
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
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          ResourceGroup    = $rg.ResourceGroupName
          ResourceName     = $rg.ResourceGroupName
          ResourceType     = 'Empty Resource Group'
          SizeGB           = $null
          SKU              = $null
          State            = 'Empty'
          EstimatedCost    = 'Free (clutters environment)'
        })
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Severity         = 'Low'
          Category         = 'Orphaned Resource'
          ResourceType     = 'Resource Group'
          ResourceName     = $rg.ResourceGroupName
          ResourceId       = $rg.ResourceId
          Detail           = "Empty Resource Group - contains no resources"
          Recommendation   = 'Delete if no longer needed to reduce environment clutter'
        })
      }
    }
  } catch {}
  
  Write-Host " $orphanCount found" -ForegroundColor $(if ($orphanCount -gt 0) { 'Yellow' } else { 'Green' })
  
  # TAG COMPLIANCE
  Write-Host "  -> Analyzing tag compliance..." -NoNewline
  $taggedPct = if ($subResourceCount -gt 0) { [math]::Round(($subTaggedCount / $subResourceCount) * 100, 1) } else { 0 }
  
  $tagCounts = @{}
  foreach ($tag in $standardTags) {
    $count = ($resourceInventory | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Tags -match $tag }).Count
    $tagCounts[$tag] = $count
  }
  
  $tagCompliance.Add([PSCustomObject]@{
    SubscriptionName    = $sub.Name
    SubscriptionId      = $sub.Id
    TotalResources      = $subResourceCount
    ResourcesWithTags   = $subTaggedCount
    TagCoveragePercent  = $taggedPct
    EnvironmentTag      = $tagCounts['Environment']
    OwnerTag            = $tagCounts['Owner']
    CostCenterTag       = $tagCounts['CostCenter']
    ProjectTag          = $tagCounts['Project']
  })
  
  Write-Host " $taggedPct% coverage" -ForegroundColor $(if ($taggedPct -ge 80) { 'Green' } elseif ($taggedPct -ge 50) { 'Yellow' } else { 'Red' })
  
  if ($taggedPct -lt 50) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'High'
      Category         = 'Governance'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceId       = "/subscriptions/$($sub.Id)"
      Detail           = "Only $taggedPct% of resources have tags"
      Recommendation   = 'Implement Azure Policy to enforce tagging'
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
                SubscriptionName = $sub.Name
                SubscriptionId   = $sub.Id
                KeyVaultName     = $kv.VaultName
                ResourceGroup    = $kv.ResourceGroupName
                SecretType       = 'Certificate'
                SecretName       = $cert.Name
                ExpiryDate       = $certDetails.Expires
                DaysToExpiry     = $daysToExpiry
                Status           = if ($daysToExpiry -le 0) { 'EXPIRED' } elseif ($daysToExpiry -le 30) { 'Critical' } else { 'Warning' }
              })
              $findings.Add([PSCustomObject]@{
                SubscriptionName = $sub.Name
                SubscriptionId   = $sub.Id
                Severity         = $severity
                Category         = 'Security'
                ResourceType     = 'Key Vault Certificate'
                ResourceName     = "$($kv.VaultName)/$($cert.Name)"
                ResourceId       = $kv.ResourceId
                Detail           = if ($daysToExpiry -le 0) { "Certificate EXPIRED" } else { "Certificate expires in $daysToExpiry days" }
                Recommendation   = 'Renew certificate before expiry'
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
    SubscriptionName    = $sub.Name
    SubscriptionId      = $sub.Id
    TotalResources      = $subResourceCount
    VirtualMachines     = $subVMCount
    TagCoveragePercent  = $taggedPct
    OrphanedResources   = $subOrphanedCount
    AHUBEnabled         = $subAHUBCount
    ExpiringSecrets     = $expiryCount
    HighFindings        = $highFindings
    MediumFindings      = $medFindings
    LowFindings         = $lowFindings
    TotalFindings       = $highFindings + $medFindings + $lowFindings
  })
  
  Write-Host ""
}

# ============================================================================
# CONSOLE OUTPUT
# ============================================================================

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "AUDIT COMPLETE" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green

Write-Host "=== Subscription Summary ===" -ForegroundColor Cyan
if ($subscriptionSummary.Count -gt 0) {
  $subscriptionSummary | Format-Table SubscriptionName, TotalResources, VirtualMachines, TagCoveragePercent, OrphanedResources, TotalFindings -AutoSize
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

Export-Sheet -Data $subscriptionSummary -WorksheetName 'Summary' -TableName 'Subscriptions'
Export-Sheet -Data $vmDetails -WorksheetName 'VM_Inventory' -TableName 'VMs'
Export-Sheet -Data $resourceInventory -WorksheetName 'Resource_Inventory' -TableName 'Resources'
Export-Sheet -Data $orphanedResources -WorksheetName 'Orphaned_Resources' -TableName 'Orphaned'
Export-Sheet -Data $tagCompliance -WorksheetName 'Tag_Compliance' -TableName 'Tags'
Export-Sheet -Data $hybridBenefit -WorksheetName 'Hybrid_Benefit' -TableName 'AHUB'
Export-Sheet -Data $expiringSecrets -WorksheetName 'Expiring_Secrets' -TableName 'Secrets'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

Write-Host "`nExcel export complete -> $XlsxPath" -ForegroundColor Green
Write-Host "`n+ Audit complete!" -ForegroundColor Green
Write-Host "Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
Write-Host ""
