<#
.SYNOPSIS
  TIEVA Regulatory Compliance Auditor
  
.DESCRIPTION
  Focused regulatory compliance audit - framework scores only:
  - Compliance percentage per regulatory standard (CIS, ISO, NIST, PCI, SOC2)
  - High-level pass/fail control counts
  
  Does NOT include: Individual controls, individual assessments, resource-level details
  (too noisy - use Azure Portal for drill-down)
  
  Outputs multi-sheet Excel workbook: Compliance_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.EXAMPLE
  .\ComplianceAudit.ps1 -SubscriptionIds @("sub-id-1")
  
.NOTES
  Requires: Az.Accounts, Az.ResourceGraph, ImportExcel modules
  Permissions: Security Reader on subscriptions
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads"
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Regulatory Compliance Auditor" -ForegroundColor Cyan
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

function Get-StandardDisplayName {
  param([string]$StandardName)
  $lower = $StandardName.ToLower()
  
  if ($lower -like "*cis*azure*2.0*") { return "CIS Azure 2.0" }
  if ($lower -like "*cis*azure*1.4*") { return "CIS Azure 1.4" }
  if ($lower -like "*cis*azure*1.3*") { return "CIS Azure 1.3" }
  if ($lower -like "*cis*") { return "CIS Benchmark" }
  if ($lower -like "*iso*27001*") { return "ISO 27001" }
  if ($lower -like "*nist*800-53*r5*") { return "NIST 800-53 R5" }
  if ($lower -like "*nist*800-53*r4*") { return "NIST 800-53 R4" }
  if ($lower -like "*nist*800-171*") { return "NIST 800-171" }
  if ($lower -like "*nist*") { return "NIST" }
  if ($lower -like "*pci*dss*4*") { return "PCI DSS 4.0" }
  if ($lower -like "*pci*dss*3*") { return "PCI DSS 3.2.1" }
  if ($lower -like "*pci*") { return "PCI DSS" }
  if ($lower -like "*soc*2*") { return "SOC 2" }
  if ($lower -like "*hipaa*") { return "HIPAA" }
  if ($lower -like "*microsoft*cloud*security*" -or $lower -like "*mcsb*") { return "Microsoft Cloud Security Benchmark" }
  if ($lower -like "*azure*security*benchmark*v3*") { return "Azure Security Benchmark v3" }
  if ($lower -like "*azure*security*benchmark*") { return "Azure Security Benchmark" }
  
  return $StandardName
}

function Get-FrameworkCategory {
  param([string]$StandardName)
  $lower = $StandardName.ToLower()
  
  if ($lower -like "*cis*") { return "CIS" }
  if ($lower -like "*iso*") { return "ISO" }
  if ($lower -like "*nist*") { return "NIST" }
  if ($lower -like "*pci*") { return "PCI" }
  if ($lower -like "*soc*") { return "SOC" }
  if ($lower -like "*hipaa*") { return "HIPAA" }
  if ($lower -like "*security*benchmark*" -or $lower -like "*mcsb*") { return "Azure" }
  
  return "Other"
}

# ============================================================================
# DATA COLLECTION
# ============================================================================

$subscriptions = Get-SubscriptionList
Write-Host "Auditing $($subscriptions.Count) subscription(s)..." -ForegroundColor Green

# Initialize result collections
$complianceScores = [System.Collections.ArrayList]::new()
$findings = [System.Collections.ArrayList]::new()
$summary = [System.Collections.ArrayList]::new()

# Build subscription ID list for Resource Graph
$subIds = @($subscriptions | ForEach-Object { $_.Id })

# -------------------------------------------------------------------------
# QUERY COMPLIANCE STANDARDS (scores only, not controls)
# -------------------------------------------------------------------------
Write-Host "`nQuerying Regulatory Compliance Standards..."

$standardsQuery = @"
securityresources
| where type == 'microsoft.security/regulatorycompliancestandards'
| extend standardName = name,
         state = properties.state,
         passedControls = toint(properties.passedControls),
         failedControls = toint(properties.failedControls),
         skippedControls = toint(properties.skippedControls)
| project subscriptionId, standardName, state, passedControls, failedControls, skippedControls
"@

try {
  $standards = Search-AzGraph -Query $standardsQuery -Subscription $subIds -ErrorAction SilentlyContinue
  Write-Host "  Found $($standards.Count) compliance standards"
  
  foreach ($std in $standards) {
    $subName = ($subscriptions | Where-Object { $_.Id -eq $std.subscriptionId }).Name
    $totalAssessed = $std.passedControls + $std.failedControls
    $compliancePercent = if ($totalAssessed -gt 0) { 
      [math]::Round(($std.passedControls / $totalAssessed) * 100, 0) 
    } else { 100 }
    
    $displayName = Get-StandardDisplayName -StandardName $std.standardName
    $category = Get-FrameworkCategory -StandardName $std.standardName
    
    $complianceScores.Add([PSCustomObject]@{
      SubscriptionName  = $subName
      SubscriptionId    = $std.subscriptionId
      Framework         = $category
      StandardName      = $displayName
      CompliancePercent = $compliancePercent
      PassedControls    = $std.passedControls
      FailedControls    = $std.failedControls
      SkippedControls   = $std.skippedControls
      Status            = if ($compliancePercent -ge 90) { 'Excellent' } 
                          elseif ($compliancePercent -ge 70) { 'Good' }
                          elseif ($compliancePercent -ge 50) { 'Needs Work' }
                          else { 'Critical' }
    }) | Out-Null
    
    # Finding: Low compliance score
    if ($compliancePercent -lt 50 -and $std.failedControls -gt 0) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $subName
        SubscriptionId   = $std.subscriptionId
        Severity         = 'High'
        Category         = 'Regulatory Compliance'
        ResourceType     = 'Compliance Standard'
        ResourceName     = $displayName
        ResourceId       = "/subscriptions/$($std.subscriptionId)/providers/Microsoft.Security/regulatoryComplianceStandards/$($std.standardName)"
        Detail           = "$displayName compliance is critically low at $compliancePercent% ($($std.failedControls) failed controls)"
        Recommendation   = 'Review failed controls in Azure Portal Regulatory Compliance dashboard'
      }) | Out-Null
    }
    elseif ($compliancePercent -lt 70 -and $std.failedControls -gt 0) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $subName
        SubscriptionId   = $std.subscriptionId
        Severity         = 'Medium'
        Category         = 'Regulatory Compliance'
        ResourceType     = 'Compliance Standard'
        ResourceName     = $displayName
        ResourceId       = "/subscriptions/$($std.subscriptionId)/providers/Microsoft.Security/regulatoryComplianceStandards/$($std.standardName)"
        Detail           = "$displayName compliance is below target at $compliancePercent% ($($std.failedControls) failed controls)"
        Recommendation   = 'Review failed controls in Azure Portal Regulatory Compliance dashboard'
      }) | Out-Null
    }
  }
}
catch {
  Write-Host "  Could not query compliance standards: $_" -ForegroundColor Yellow
}

# -------------------------------------------------------------------------
# BUILD SUMMARY PER SUBSCRIPTION
# -------------------------------------------------------------------------
Write-Host "`nBuilding Summary..."

foreach ($sub in $subscriptions) {
  $subScores = $complianceScores | Where-Object { $_.SubscriptionId -eq $sub.Id }
  $subFindings = $findings | Where-Object { $_.SubscriptionId -eq $sub.Id }
  
  # Get best score per framework category
  $azureScore = ($subScores | Where-Object { $_.Framework -eq 'Azure' } | Sort-Object CompliancePercent -Descending | Select-Object -First 1).CompliancePercent
  $cisScore = ($subScores | Where-Object { $_.Framework -eq 'CIS' } | Sort-Object CompliancePercent -Descending | Select-Object -First 1).CompliancePercent
  $isoScore = ($subScores | Where-Object { $_.Framework -eq 'ISO' } | Sort-Object CompliancePercent -Descending | Select-Object -First 1).CompliancePercent
  $nistScore = ($subScores | Where-Object { $_.Framework -eq 'NIST' } | Sort-Object CompliancePercent -Descending | Select-Object -First 1).CompliancePercent
  $pciScore = ($subScores | Where-Object { $_.Framework -eq 'PCI' } | Sort-Object CompliancePercent -Descending | Select-Object -First 1).CompliancePercent
  $socScore = ($subScores | Where-Object { $_.Framework -eq 'SOC' } | Sort-Object CompliancePercent -Descending | Select-Object -First 1).CompliancePercent
  
  $summary.Add([PSCustomObject]@{
    SubscriptionName       = $sub.Name
    SubscriptionId         = $sub.Id
    AzureSecurityBenchmark = if ($azureScore) { "$azureScore%" } else { 'N/A' }
    CIS                    = if ($cisScore) { "$cisScore%" } else { 'N/A' }
    ISO27001               = if ($isoScore) { "$isoScore%" } else { 'N/A' }
    NIST                   = if ($nistScore) { "$nistScore%" } else { 'N/A' }
    PCIDSS                 = if ($pciScore) { "$pciScore%" } else { 'N/A' }
    SOC2                   = if ($socScore) { "$socScore%" } else { 'N/A' }
    StandardsAssessed      = $subScores.Count
    HighFindings           = ($subFindings | Where-Object { $_.Severity -eq 'High' }).Count
    MediumFindings         = ($subFindings | Where-Object { $_.Severity -eq 'Medium' }).Count
    AuditDate              = Get-Date -Format 'yyyy-MM-dd HH:mm'
  }) | Out-Null
}

# ============================================================================
# EXPORT TO EXCEL
# ============================================================================

$outputFile = Join-Path $OutPath "Compliance_Audit.xlsx"
Write-Host "`nExporting to Excel: $outputFile" -ForegroundColor Cyan

if (Test-Path $outputFile) { Remove-Item $outputFile -Force }

function Export-Sheet { param($Data, $WorksheetName, $TableName)
  if (-not $Data -or $Data.Count -eq 0) { Write-Host "  Skipping empty: $WorksheetName" -ForegroundColor Gray; return }
  $Data | Export-Excel -Path $outputFile -WorksheetName $WorksheetName -TableName $TableName -TableStyle 'Medium9' -AutoSize -FreezeTopRow -BoldTopRow
  Write-Host "  + $WorksheetName ($($Data.Count) rows)" -ForegroundColor Green
}

Export-Sheet -Data $summary -WorksheetName 'Summary' -TableName 'Summary'
Export-Sheet -Data ($complianceScores | Sort-Object SubscriptionName, Framework, StandardName) -WorksheetName 'Compliance Scores' -TableName 'ComplianceScores'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Compliance Audit Complete!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Subscriptions Audited: $($subscriptions.Count)"
Write-Host "Standards Assessed: $($complianceScores.Count)"
Write-Host "Total Findings: $($findings.Count)"
Write-Host "  High: $(($findings | Where-Object { $_.Severity -eq 'High' }).Count)"
Write-Host "  Medium: $(($findings | Where-Object { $_.Severity -eq 'Medium' }).Count)"
Write-Host "Output: $outputFile"
Write-Host ""
