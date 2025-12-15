<#
.SYNOPSIS
  TIEVA Policy & Compliance Auditor
  
.DESCRIPTION
  Comprehensive Azure Policy and compliance audit for AMS customer meetings:
  - Policy assignment inventory
  - Compliance state by policy/initiative
  - Non-compliant resources detail
  - Exemption tracking
  - Built-in vs custom policies
  - Regulatory compliance coverage
  - Policy remediation status
  
  Outputs multi-sheet Excel workbook: Policy_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.PARAMETER IncludeNonCompliantDetails
  Include detailed list of non-compliant resources (can be large)
  
.EXAMPLE
  .\PolicyAudit.ps1
  
.EXAMPLE
  .\PolicyAudit.ps1 -SubscriptionIds @("sub-id-1") -IncludeNonCompliantDetails
  
.NOTES
  Requires: Az.Accounts, Az.Resources, Az.PolicyInsights modules
  Permissions: Reader + Policy Insights Reader on subscriptions
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads",
  [switch]$IncludeNonCompliantDetails
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Policy & Compliance Auditor" -ForegroundColor Cyan
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
      try { $subs += Get-AzSubscription -SubscriptionId $id -ErrorAction Stop } 
      catch { Write-Warning "Could not access subscription $id : $_" }
    }
    return $subs
  } else {
    return Get-AzSubscription | Where-Object { $_.State -eq 'Enabled' }
  }
}

function Get-CompliancePercentage {
  param($States)
  if (-not $States) { return 0 }
  
  $compliant = ($States | Where-Object { $_.ComplianceState -eq 'Compliant' } | Measure-Object -Property ResourceCount -Sum).Sum
  $nonCompliant = ($States | Where-Object { $_.ComplianceState -eq 'NonCompliant' } | Measure-Object -Property ResourceCount -Sum).Sum
  $total = $compliant + $nonCompliant
  
  if ($total -eq 0) { return 100 }
  return [math]::Round(($compliant / $total) * 100, 1)
}

function Get-ScopeLevel {
  param([string]$Scope)
  if ($Scope -match '/managementGroups/') { return 'Management Group' }
  if ($Scope -match '^/subscriptions/[^/]+$') { return 'Subscription' }
  if ($Scope -match '/resourceGroups/[^/]+$') { return 'Resource Group' }
  if ($Scope -match '/providers/') { return 'Resource' }
  return 'Other'
}

# ============================================================================
# DATA COLLECTIONS
# ============================================================================

$policyAssignmentReport = [System.Collections.Generic.List[object]]::new()
$initiativeAssignmentReport = [System.Collections.Generic.List[object]]::new()
$complianceReport = [System.Collections.Generic.List[object]]::new()
$nonCompliantReport = [System.Collections.Generic.List[object]]::new()
$exemptionReport = [System.Collections.Generic.List[object]]::new()
$customPolicyReport = [System.Collections.Generic.List[object]]::new()
$remediationReport = [System.Collections.Generic.List[object]]::new()
$subscriptionSummary = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[object]]::new()

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
  
  $subPolicyCount = 0
  $subInitiativeCount = 0
  $subNonCompliantCount = 0
  $subExemptionCount = 0
  $overallCompliance = 100
  
  # -----------------------------------------------------------
  # 1. POLICY ASSIGNMENTS
  # -----------------------------------------------------------
  Write-Host "  -> Collecting policy assignments..." -NoNewline
  
  $assignments = @()
  try { $assignments = Get-AzPolicyAssignment -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($assignments.Count) assignments" -ForegroundColor Cyan
  
  foreach ($pa in $assignments) {
    $scopeLevel = Get-ScopeLevel -Scope $pa.Properties.Scope
    $isInherited = $pa.Properties.Scope -notmatch "^/subscriptions/$($sub.Id)"
    
    $policyDef = $null
    $isInitiative = $false
    $isCustom = $false
    $displayName = $pa.Properties.DisplayName
    $description = $pa.Properties.Description
    
    if ($pa.Properties.PolicyDefinitionId -match '/policySetDefinitions/') {
      $isInitiative = $true
      try {
        $policyDef = Get-AzPolicySetDefinition -Id $pa.Properties.PolicyDefinitionId -ErrorAction SilentlyContinue
        if (-not $displayName) { $displayName = $policyDef.Properties.DisplayName }
        if (-not $description) { $description = $policyDef.Properties.Description }
        $isCustom = $policyDef.Properties.PolicyType -eq 'Custom'
      } catch {}
      $subInitiativeCount++
    } else {
      try {
        $policyDef = Get-AzPolicyDefinition -Id $pa.Properties.PolicyDefinitionId -ErrorAction SilentlyContinue
        if (-not $displayName) { $displayName = $policyDef.Properties.DisplayName }
        if (-not $description) { $description = $policyDef.Properties.Description }
        $isCustom = $policyDef.Properties.PolicyType -eq 'Custom'
      } catch {}
      $subPolicyCount++
    }
    
    $effect = $null
    if ($pa.Properties.Parameters -and $pa.Properties.Parameters.effect) {
      $effect = $pa.Properties.Parameters.effect.value
    } elseif ($policyDef -and $policyDef.Properties.Parameters -and $policyDef.Properties.Parameters.effect) {
      $effect = $policyDef.Properties.Parameters.effect.defaultValue
    }
    
    $report = [PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      AssignmentName    = $pa.Name
      DisplayName       = $displayName
      Description       = $description
      IsInitiative      = $isInitiative
      IsCustom          = $isCustom
      Effect            = $effect
      Scope             = $pa.Properties.Scope
      ScopeLevel        = $scopeLevel
      IsInherited       = $isInherited
      EnforcementMode   = $pa.Properties.EnforcementMode
      AssignmentId      = $pa.ResourceId
    }
    
    if ($isInitiative) {
      $initiativeAssignmentReport.Add($report)
    } else {
      $policyAssignmentReport.Add($report)
    }
  }
  
  # -----------------------------------------------------------
  # 2. COMPLIANCE STATE
  # -----------------------------------------------------------
  Write-Host "  -> Checking compliance state..." -NoNewline
  
  $complianceStates = @()
  try {
    $complianceStates = Get-AzPolicyStateSummary -ErrorAction SilentlyContinue
  } catch {}
  
  if ($complianceStates -and $complianceStates.Results) {
    $overallCompliance = Get-CompliancePercentage -States $complianceStates.Results
    $nonCompliantResources = ($complianceStates.Results | Where-Object { $_.ComplianceState -eq 'NonCompliant' } | Measure-Object -Property ResourceCount -Sum).Sum
    $subNonCompliantCount = $nonCompliantResources
    
    Write-Host " $overallCompliance% compliant, $nonCompliantResources non-compliant" -ForegroundColor $(if ($overallCompliance -ge 90) { 'Green' } elseif ($overallCompliance -ge 70) { 'Yellow' } else { 'Red' })
  } else {
    Write-Host " No compliance data" -ForegroundColor Gray
  }
  
  # Get per-policy compliance
  $policyStates = @()
  try {
    $policyStates = Get-AzPolicyState -Filter "ComplianceState eq 'NonCompliant'" -Top 1000 -ErrorAction SilentlyContinue
    
    $byPolicy = $policyStates | Group-Object -Property PolicyDefinitionName
    
    foreach ($group in $byPolicy) {
      $firstItem = $group.Group[0]
      
      $complianceReport.Add([PSCustomObject]@{
        SubscriptionName     = $sub.Name
        SubscriptionId       = $sub.Id
        PolicyName           = $group.Name
        PolicyDisplayName    = $firstItem.PolicyDefinitionDisplayName
        PolicyCategory       = $firstItem.PolicyDefinitionCategory
        NonCompliantCount    = $group.Count
        ResourceTypes        = ($group.Group.ResourceType | Select-Object -Unique) -join ', '
      })
    }
  } catch {
    Write-Verbose "Could not get policy states: $_"
  }
  
  # -----------------------------------------------------------
  # 3. NON-COMPLIANT RESOURCE DETAILS
  # -----------------------------------------------------------
  if ($IncludeNonCompliantDetails -and $policyStates) {
    Write-Host "  -> Collecting non-compliant resource details..." -NoNewline
    
    foreach ($state in ($policyStates | Select-Object -First 500)) {
      $nonCompliantReport.Add([PSCustomObject]@{
        SubscriptionName      = $sub.Name
        SubscriptionId        = $sub.Id
        ResourceId            = $state.ResourceId
        ResourceName          = ($state.ResourceId -split '/')[-1]
        ResourceType          = $state.ResourceType
        ResourceGroup         = $state.ResourceGroup
        PolicyName            = $state.PolicyDefinitionName
        PolicyDisplayName     = $state.PolicyDefinitionDisplayName
        PolicyCategory        = $state.PolicyDefinitionCategory
        ComplianceState       = $state.ComplianceState
        Timestamp             = $state.Timestamp
      })
    }
    
    Write-Host " $($nonCompliantReport.Count) details captured" -ForegroundColor Cyan
  }
  
  # -----------------------------------------------------------
  # 4. POLICY EXEMPTIONS
  # -----------------------------------------------------------
  Write-Host "  -> Checking policy exemptions..." -NoNewline
  
  $exemptions = @()
  try { $exemptions = Get-AzPolicyExemption -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($exemptions.Count) exemptions" -ForegroundColor $(if ($exemptions.Count -gt 0) { 'Yellow' } else { 'Green' })
  $subExemptionCount = $exemptions.Count
  
  foreach ($ex in $exemptions) {
    $isExpired = $false
    $daysToExpiry = $null
    
    if ($ex.Properties.ExpiresOn) {
      $expiryDate = [datetime]$ex.Properties.ExpiresOn
      $daysToExpiry = ($expiryDate - (Get-Date)).Days
      $isExpired = $daysToExpiry -lt 0
    }
    
    $exemptionReport.Add([PSCustomObject]@{
      SubscriptionName    = $sub.Name
      SubscriptionId      = $sub.Id
      ExemptionName       = $ex.Name
      DisplayName         = $ex.Properties.DisplayName
      Description         = $ex.Properties.Description
      Category            = $ex.Properties.ExemptionCategory
      PolicyAssignment    = ($ex.Properties.PolicyAssignmentId -split '/')[-1]
      Scope               = $ex.Properties.Scope
      ExpiresOn           = $ex.Properties.ExpiresOn
      DaysToExpiry        = $daysToExpiry
      IsExpired           = $isExpired
    })
    
    if (-not $ex.Properties.ExpiresOn) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Policy Exemption'
        ResourceType     = 'Policy Exemption'
        ResourceName     = $ex.Properties.DisplayName
        ResourceGroup    = 'N/A'
        Detail           = 'Policy exemption has no expiry date'
        Recommendation   = 'Set an expiry date or review if exemption is still needed'
      })
    }
    
    if ($isExpired) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Low'
        Category         = 'Policy Exemption'
        ResourceType     = 'Policy Exemption'
        ResourceName     = $ex.Properties.DisplayName
        ResourceGroup    = 'N/A'
        Detail           = 'Policy exemption has expired but still exists'
        Recommendation   = 'Remove expired exemption or renew if still needed'
      })
    }
  }
  
  # -----------------------------------------------------------
  # 5. CUSTOM POLICY DEFINITIONS
  # -----------------------------------------------------------
  Write-Host "  -> Collecting custom policies..." -NoNewline
  
  $customPolicies = @()
  try {
    $customPolicies = Get-AzPolicyDefinition -Custom -ErrorAction SilentlyContinue
  } catch {}
  
  Write-Host " $($customPolicies.Count) custom policies" -ForegroundColor Cyan
  
  foreach ($pol in $customPolicies) {
    $customPolicyReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      PolicyName       = $pol.Name
      DisplayName      = $pol.Properties.DisplayName
      Description      = $pol.Properties.Description
      Category         = $pol.Properties.Metadata.category
      Mode             = $pol.Properties.Mode
      PolicyType       = $pol.Properties.PolicyType
    })
  }
  
  # -----------------------------------------------------------
  # 6. REMEDIATION TASKS
  # -----------------------------------------------------------
  Write-Host "  -> Checking remediation tasks..." -NoNewline
  
  $remediations = @()
  try {
    $remediations = Get-AzPolicyRemediation -ErrorAction SilentlyContinue
  } catch {}
  
  Write-Host " $($remediations.Count) remediation tasks" -ForegroundColor Cyan
  
  foreach ($rem in $remediations) {
    $remediationReport.Add([PSCustomObject]@{
      SubscriptionName    = $sub.Name
      SubscriptionId      = $sub.Id
      RemediationName     = $rem.Name
      PolicyAssignment    = ($rem.PolicyAssignmentId -split '/')[-1]
      ProvisioningState   = $rem.ProvisioningState
      DeploymentStatus    = $rem.DeploymentStatus
      CreatedOn           = $rem.CreatedOn
      LastUpdatedOn       = $rem.LastUpdatedOn
      ResourceCount       = $rem.ResourceCount
      FailedResourceCount = $rem.FailedResourceCount
    })
    
    if ($rem.FailedResourceCount -gt 0) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Policy Remediation'
        ResourceType     = 'Remediation Task'
        ResourceName     = $rem.Name
        ResourceGroup    = 'N/A'
        Detail           = "$($rem.FailedResourceCount) resources failed remediation"
        Recommendation   = 'Investigate failed remediations and resolve blockers'
      })
    }
  }
  
  # -----------------------------------------------------------
  # SUBSCRIPTION SUMMARY
  # -----------------------------------------------------------
  $highFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'High' }).Count
  $medFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Medium' }).Count
  $lowFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Low' }).Count
  
  $subscriptionSummary.Add([PSCustomObject]@{
    SubscriptionName      = $sub.Name
    SubscriptionId        = $sub.Id
    PolicyAssignments     = $subPolicyCount
    InitiativeAssignments = $subInitiativeCount
    CompliancePercent     = $overallCompliance
    NonCompliantResources = $subNonCompliantCount
    Exemptions            = $subExemptionCount
    CustomPolicies        = $customPolicies.Count
    HighFindings          = $highFindings
    MediumFindings        = $medFindings
    LowFindings           = $lowFindings
    TotalFindings         = $highFindings + $medFindings + $lowFindings
  })
  
  if ($overallCompliance -lt 70) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'High'
      Category         = 'Compliance'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = "Overall compliance is $overallCompliance% - below 70% threshold"
      Recommendation   = 'Review and remediate non-compliant resources'
    })
  } elseif ($overallCompliance -lt 90) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'Medium'
      Category         = 'Compliance'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = "Overall compliance is $overallCompliance% - below 90% target"
      Recommendation   = 'Continue remediation efforts to improve compliance'
    })
  }
  
  if ($subInitiativeCount -eq 0) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'Medium'
      Category         = 'Policy Coverage'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = 'No policy initiatives assigned to subscription'
      Recommendation   = 'Assign Azure Security Benchmark or other relevant initiatives'
    })
  }
  
  if ($subExemptionCount -gt 10) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'Medium'
      Category         = 'Policy Exemption'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = "$subExemptionCount policy exemptions in place"
      Recommendation   = 'Review exemptions - high count may indicate policy/resource mismatch'
    })
  }
  
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
  $subscriptionSummary | Format-Table SubscriptionName, PolicyAssignments, InitiativeAssignments, CompliancePercent, NonCompliantResources, Exemptions, TotalFindings -AutoSize
}

Write-Host "`n=== Compliance Overview ===" -ForegroundColor Cyan
$avgCompliance = ($subscriptionSummary | Measure-Object -Property CompliancePercent -Average).Average
$totalNonCompliant = ($subscriptionSummary | Measure-Object -Property NonCompliantResources -Sum).Sum
Write-Host "  Average compliance: $([math]::Round($avgCompliance, 1))%" -ForegroundColor $(if ($avgCompliance -ge 90) { 'Green' } elseif ($avgCompliance -ge 70) { 'Yellow' } else { 'Red' })
Write-Host "  Total non-compliant resources: $totalNonCompliant" -ForegroundColor $(if ($totalNonCompliant -eq 0) { 'Green' } else { 'Yellow' })

Write-Host "`n=== Top Non-Compliant Policies ===" -ForegroundColor Cyan
$topNonCompliant = $complianceReport | Sort-Object -Property NonCompliantCount -Descending | Select-Object -First 10
if ($topNonCompliant) {
  $topNonCompliant | Format-Table PolicyDisplayName, NonCompliantCount, PolicyCategory -AutoSize
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

$XlsxPath = Join-Path $OutPath 'Policy_Audit.xlsx'

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
Export-Sheet -Data $initiativeAssignmentReport -WorksheetName 'Initiatives' -TableName 'Initiatives'
Export-Sheet -Data $policyAssignmentReport -WorksheetName 'Policy_Assignments' -TableName 'PolicyAssignments'
Export-Sheet -Data $complianceReport -WorksheetName 'Non_Compliant_Policies' -TableName 'NonCompliantPolicies'
Export-Sheet -Data $nonCompliantReport -WorksheetName 'Non_Compliant_Resources' -TableName 'NonCompliantResources'
Export-Sheet -Data $exemptionReport -WorksheetName 'Exemptions' -TableName 'Exemptions'
Export-Sheet -Data $customPolicyReport -WorksheetName 'Custom_Policies' -TableName 'CustomPolicies'
Export-Sheet -Data $remediationReport -WorksheetName 'Remediations' -TableName 'Remediations'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

$overallSummary = @(
  [PSCustomObject]@{ Metric='Audit Date';Value=(Get-Date -Format 'yyyy-MM-dd HH:mm') }
  [PSCustomObject]@{ Metric='Subscriptions Audited';Value=$subscriptions.Count }
  [PSCustomObject]@{ Metric='Average Compliance';Value="$([math]::Round($avgCompliance, 1))%" }
  [PSCustomObject]@{ Metric='Policy Assignments';Value=$policyAssignmentReport.Count }
  [PSCustomObject]@{ Metric='Initiative Assignments';Value=$initiativeAssignmentReport.Count }
  [PSCustomObject]@{ Metric='Total Non-Compliant Resources';Value=$totalNonCompliant }
  [PSCustomObject]@{ Metric='Policy Exemptions';Value=$exemptionReport.Count }
  [PSCustomObject]@{ Metric='Custom Policies';Value=$customPolicyReport.Count }
  [PSCustomObject]@{ Metric='Remediation Tasks';Value=$remediationReport.Count }
  [PSCustomObject]@{ Metric='High Findings';Value=$totalHigh }
  [PSCustomObject]@{ Metric='Medium Findings';Value=$totalMed }
  [PSCustomObject]@{ Metric='Low Findings';Value=$totalLow }
)
Export-Sheet -Data $overallSummary -WorksheetName 'Summary' -TableName 'Summary'

Write-Host "`nExcel export complete -> $XlsxPath" -ForegroundColor Green
Write-Host "`n+ Audit complete!" -ForegroundColor Green
Write-Host "Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n" -ForegroundColor Gray
