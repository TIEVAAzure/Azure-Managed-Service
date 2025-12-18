<#
.SYNOPSIS
  TIEVA Policy & Compliance Auditor v2
  
.DESCRIPTION
  Comprehensive Azure Policy and compliance audit matching Azure Portal view:
  - Policy/Initiative assignment inventory with per-assignment compliance
  - Compliance state showing "X% (Y out of Z)" format
  - All exemptions including those inherited from Management Groups
  - Non-compliant resources detail
  - Exemption tracking with expiry analysis
  - Policy remediation status
  
  Outputs multi-sheet Excel workbook: Policy_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.PARAMETER IncludeNonCompliantDetails
  Include detailed list of non-compliant resources (can be large)
  
.EXAMPLE
  .\PolicyAudit_v2.ps1
  
.EXAMPLE
  .\PolicyAudit_v2.ps1 -SubscriptionIds @("sub-id-1") -IncludeNonCompliantDetails
  
.NOTES
  Requires: Az.Accounts, Az.Resources, Az.PolicyInsights, Az.ResourceGraph modules
  Permissions: Reader + Policy Insights Reader on subscriptions/management groups
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
Write-Host "TIEVA Policy & Compliance Auditor v2" -ForegroundColor Cyan
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

function Get-ScopeLevel {
  param([string]$Scope)
  if ($Scope -match '/managementGroups/') { return 'Management Group' }
  if ($Scope -match '^/subscriptions/[^/]+$') { return 'Subscription' }
  if ($Scope -match '/resourceGroups/[^/]+$') { return 'Resource Group' }
  if ($Scope -match '/providers/') { return 'Resource' }
  return 'Other'
}

function Get-ScopeName {
  param([string]$Scope)
  if ($Scope -match '/managementGroups/([^/]+)') { return $Matches[1] }
  if ($Scope -match '/subscriptions/([^/]+)') { 
    $subId = $Matches[1]
    $sub = Get-AzSubscription -SubscriptionId $subId -ErrorAction SilentlyContinue
    if ($sub) { return $sub.Name }
    return $subId
  }
  return $Scope
}

function Format-ComplianceString {
  param([int]$Compliant, [int]$NonCompliant)
  $total = $Compliant + $NonCompliant
  if ($total -eq 0) { return "100% (0 out of 0)" }
  $pct = [math]::Round(($Compliant / $total) * 100, 0)
  return "$pct% ($Compliant out of $total)"
}

function Get-CompliancePercent {
  param([int]$Compliant, [int]$NonCompliant)
  $total = $Compliant + $NonCompliant
  if ($total -eq 0) { return 100 }
  return [math]::Round(($Compliant / $total) * 100, 1)
}

# ============================================================================
# DATA COLLECTIONS
# ============================================================================

$complianceReport = [System.Collections.Generic.List[object]]::new()
$exemptionReport = [System.Collections.Generic.List[object]]::new()
$assignmentReport = [System.Collections.Generic.List[object]]::new()
$nonCompliantReport = [System.Collections.Generic.List[object]]::new()
$subscriptionSummary = [System.Collections.Generic.List[object]]::new()
$customPolicyReport = [System.Collections.Generic.List[object]]::new()
$remediationReport = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[object]]::new()

# Track processed items to avoid duplicates
$processedAssignments = @{}
$processedExemptions = @{}

# ============================================================================
# MAIN AUDIT LOOP - SUBSCRIPTIONS
# ============================================================================

$subscriptions = Get-SubscriptionList
if (-not $subscriptions) { Write-Error "No accessible subscriptions found."; exit 1 }

Write-Host "Found $($subscriptions.Count) subscription(s) to audit`n" -ForegroundColor Green

# ============================================================================
# COLLECT ALL EXEMPTIONS VIA RESOURCE GRAPH (includes MG-level)
# ============================================================================

Write-Host "Collecting all policy exemptions (including Management Group level)..." -ForegroundColor Yellow

# Check if Az.ResourceGraph is available, install if not
$useResourceGraph = $false
if (-not (Get-Module -ListAvailable -Name Az.ResourceGraph)) {
  Write-Host "  -> Installing Az.ResourceGraph module..." -ForegroundColor Yellow
  try {
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue | Out-Null
    Install-Module Az.ResourceGraph -Scope CurrentUser -Force -ErrorAction Stop
  } catch {
    Write-Warning "Could not install Az.ResourceGraph: $_"
  }
}

if (Get-Module -ListAvailable -Name Az.ResourceGraph) {
  Import-Module Az.ResourceGraph -ErrorAction SilentlyContinue
  $useResourceGraph = $true
}

if ($useResourceGraph) {
  try {
    # Query all exemptions across all scopes
    $exemptionQuery = @"
policyresources
| where type == 'microsoft.authorization/policyexemptions'
| extend displayName = properties.displayName
| extend exemptionCategory = properties.exemptionCategory
| extend policyAssignmentId = properties.policyAssignmentId
| extend expiresOn = properties.expiresOn
| extend createdBy = properties.metadata.createdBy
| extend resourceSelectors = properties.resourceSelectors
| extend scope = properties.scope
| project id, name, displayName, exemptionCategory, policyAssignmentId, expiresOn, createdBy, resourceSelectors, subscriptionId, scope
"@
    
    $subIds = $subscriptions | ForEach-Object { $_.Id }
    $allExemptions = Search-AzGraph -Query $exemptionQuery -Subscription $subIds -First 1000 -ErrorAction Stop
    
    Write-Host "  -> Found $($allExemptions.Count) exemptions via Resource Graph" -ForegroundColor Cyan
    
    foreach ($ex in $allExemptions) {
      $exId = $ex.id
      if (-not $exId) { continue }
      if ($processedExemptions.ContainsKey($exId)) { continue }
      $processedExemptions[$exId] = $true
      
      $isExpired = $false
      $daysToExpiry = $null
      $expiresOnFormatted = $null
      
      if ($ex.expiresOn) {
        try {
          $expiryDate = [datetime]$ex.expiresOn
          $daysToExpiry = ($expiryDate - (Get-Date)).Days
          $isExpired = $daysToExpiry -lt 0
          $expiresOnFormatted = $expiryDate.ToString("dd/MM/yyyy, HH:mm:ss")
        } catch {}
      }
      
      # Determine scope name and type
      $scopeName = ''
      $scopeType = 'Unknown'
      $scopePath = if ($ex.scope) { $ex.scope } else { $ex.id }
      
      if ($scopePath -match '/managementGroups/([^/]+)') {
        $scopeName = $Matches[1]
        $scopeType = 'Management Group'
      } elseif ($scopePath -match '/subscriptions/([^/]+)') {
        $subId = $Matches[1]
        $scopeType = 'Subscription'
        $matchedSub = $subscriptions | Where-Object { $_.Id -eq $subId } | Select-Object -First 1
        $scopeName = if ($matchedSub) { $matchedSub.Name } else { $subId }
      }
      
      # Get assignment display name
      $assignmentName = ($ex.policyAssignmentId -split '/')[-1]
      
      $exemptionReport.Add([PSCustomObject]@{
        Name              = if ($ex.displayName) { $ex.displayName } else { $ex.name }
        ExemptionName     = $ex.name
        Assignments       = $assignmentName
        Scope             = $scopeName
        ScopeType         = $scopeType
        ExemptionCategory = $ex.exemptionCategory
        CreatedBy         = $ex.createdBy
        ExpirationDate    = if ($expiresOnFormatted) { $expiresOnFormatted } else { '--' }
        DaysToExpiry      = $daysToExpiry
        IsExpired         = $isExpired
        ResourceSelectors = if ($ex.resourceSelectors) { ($ex.resourceSelectors | ConvertTo-Json -Compress) } else { '--' }
      })
      
      if (-not $ex.expiresOn) {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $scopeName
          SubscriptionId   = if ($scopeType -eq 'Subscription') { $ex.subscriptionId } else { $scopeType }
          Severity         = 'Medium'
          Category         = 'Policy Exemption'
          ResourceType     = 'Policy Exemption'
          ResourceName     = if ($ex.displayName) { $ex.displayName } else { $ex.name }
          ResourceGroup    = 'N/A'
          Detail           = 'Policy exemption has no expiry date'
          Recommendation   = 'Set an expiry date or review if exemption is still needed'
        })
      }
      
      if ($isExpired) {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $scopeName
          SubscriptionId   = if ($scopeType -eq 'Subscription') { $ex.subscriptionId } else { $scopeType }
          Severity         = 'Low'
          Category         = 'Policy Exemption'
          ResourceType     = 'Policy Exemption'
          ResourceName     = if ($ex.displayName) { $ex.displayName } else { $ex.name }
          ResourceGroup    = 'N/A'
          Detail           = 'Policy exemption has expired but still exists'
          Recommendation   = 'Remove expired exemption or renew if still needed'
        })
      }
    }
    
  } catch {
    Write-Warning "Resource Graph query failed: $_ - falling back to per-subscription query"
    $useResourceGraph = $false
  }
} else {
  Write-Host "  -> Az.ResourceGraph not available - will query per subscription (may miss MG-level exemptions)" -ForegroundColor Yellow
}

Write-Host ""

foreach ($sub in $subscriptions) {
  Write-Host "Processing: $($sub.Name)" -ForegroundColor Yellow
  
  try { Set-AzContext -SubscriptionId $sub.Id -ErrorAction Stop | Out-Null }
  catch { Write-Warning "  Could not set context: $_"; continue }
  
  $subScope = "/subscriptions/$($sub.Id)"
  
  # -----------------------------------------------------------
  # 1. POLICY ASSIGNMENTS WITH COMPLIANCE STATE
  # -----------------------------------------------------------
  Write-Host "  -> Collecting policy assignments with compliance..." -NoNewline
  
  $assignments = @()
  try { $assignments = Get-AzPolicyAssignment -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($assignments.Count) assignments" -ForegroundColor Cyan
  
  foreach ($pa in $assignments) {
    # Skip if already processed (inherited from MG)
    $paId = $pa.ResourceId
    if (-not $paId) { $paId = $pa.Name }
    if (-not $paId) { continue }
    if ($processedAssignments.ContainsKey($paId)) { continue }
    $processedAssignments[$paId] = $true
    
    $isInitiative = $pa.Properties.PolicyDefinitionId -match '/policySetDefinitions/'
    $displayName = $pa.Properties.DisplayName
    
    # Determine scope - use current subscription if scope matches, otherwise resolve
    $scopeName = $sub.Name
    $scopeLevel = 'Subscription'
    if ($pa.Properties.Scope) {
      if ($pa.Properties.Scope -match '/managementGroups/([^/]+)') {
        $scopeName = $Matches[1]
        $scopeLevel = 'Management Group'
      } elseif ($pa.Properties.Scope -match '/resourceGroups/([^/]+)') {
        $scopeLevel = 'Resource Group'
      }
    }
    
    # Get policy/initiative definition for display name if not set
    if (-not $displayName) {
      try {
        if ($isInitiative) {
          $def = Get-AzPolicySetDefinition -Id $pa.Properties.PolicyDefinitionId -ErrorAction SilentlyContinue
        } else {
          $def = Get-AzPolicyDefinition -Id $pa.Properties.PolicyDefinitionId -ErrorAction SilentlyContinue
        }
        if ($def -and $def.Properties.DisplayName) {
          $displayName = $def.Properties.DisplayName
        }
      } catch {}
    }
    
    if (-not $displayName) { $displayName = $pa.Name }
    
    # Get compliance state for this specific assignment
    $compliantCount = 0
    $nonCompliantCount = 0
    $complianceState = 'Unknown'
    $policyDisplayNameFromState = $null
    
    try {
      $policyStates = Get-AzPolicyState -PolicyAssignmentName $pa.Name -Filter "ComplianceState eq 'NonCompliant' or ComplianceState eq 'Compliant'" -Top 5000 -ErrorAction SilentlyContinue
      
      if ($policyStates) {
        $compliantCount = ($policyStates | Where-Object { $_.ComplianceState -eq 'Compliant' }).Count
        $nonCompliantCount = ($policyStates | Where-Object { $_.ComplianceState -eq 'NonCompliant' }).Count
        $complianceState = if ($nonCompliantCount -gt 0) { 'Non-compliant' } else { 'Compliant' }
        
        # Try to get display name from policy state if not already set
        if (-not $displayName -or $displayName -eq $pa.Name) {
          $firstState = $policyStates | Select-Object -First 1
          if ($firstState.PolicyAssignmentDisplayName) {
            $policyDisplayNameFromState = $firstState.PolicyAssignmentDisplayName
          } elseif ($firstState.PolicySetDefinitionDisplayName) {
            $policyDisplayNameFromState = $firstState.PolicySetDefinitionDisplayName
          } elseif ($firstState.PolicyDefinitionDisplayName) {
            $policyDisplayNameFromState = $firstState.PolicyDefinitionDisplayName
          }
        }
      }
    } catch {
      Write-Verbose "Could not get compliance for $($pa.Name): $_"
    }
    
    # Use the best available display name
    if ($policyDisplayNameFromState) {
      $displayName = $policyDisplayNameFromState
    }
    
    # Also count non-compliant policies within initiatives
    $nonCompliantPolicies = 0
    if ($isInitiative -and $nonCompliantCount -gt 0) {
      try {
        $policyGroups = $policyStates | Where-Object { $_.ComplianceState -eq 'NonCompliant' } | Group-Object PolicyDefinitionName
        $nonCompliantPolicies = $policyGroups.Count
      } catch {}
    } else {
      $nonCompliantPolicies = if ($nonCompliantCount -gt 0) { 1 } else { 0 }
    }
    
    $complianceReport.Add([PSCustomObject]@{
      Name                  = $displayName
      Scope                 = $scopeName
      ScopeLevel            = $scopeLevel
      SubscriptionName      = $sub.Name
      Type                  = if ($isInitiative) { 'Initiative' } else { 'Policy' }
      ComplianceState       = $complianceState
      ResourceCompliance    = Format-ComplianceString -Compliant $compliantCount -NonCompliant $nonCompliantCount
      CompliancePercent     = Get-CompliancePercent -Compliant $compliantCount -NonCompliant $nonCompliantCount
      NonCompliantResources = $nonCompliantCount
      NonCompliantPolicies  = $nonCompliantPolicies
      CompliantResources    = $compliantCount
      TotalResources        = $compliantCount + $nonCompliantCount
      AssignmentName        = $pa.Name
      AssignmentId          = $pa.ResourceId
    })
    
    $assignmentReport.Add([PSCustomObject]@{
      AssignmentName   = $displayName
      Scope            = $scopeName
      SubscriptionName = $sub.Name
      Type             = if ($isInitiative) { 'Initiative' } else { 'Policy' }
    })
    
    # Collect non-compliant resource details if requested
    if ($IncludeNonCompliantDetails -and $nonCompliantCount -gt 0) {
      try {
        $ncStates = Get-AzPolicyState -PolicyAssignmentName $pa.Name -Filter "ComplianceState eq 'NonCompliant'" -Top 500 -ErrorAction SilentlyContinue
        foreach ($state in $ncStates) {
          $nonCompliantReport.Add([PSCustomObject]@{
            SubscriptionName    = $sub.Name
            SubscriptionId      = $sub.Id
            ResourceId          = $state.ResourceId
            ResourceName        = ($state.ResourceId -split '/')[-1]
            ResourceType        = $state.ResourceType
            ResourceGroup       = $state.ResourceGroup
            PolicyName          = $state.PolicyDefinitionName
            PolicyDisplayName   = $state.PolicyDefinitionDisplayName
            PolicyCategory      = $state.PolicyDefinitionCategory
            AssignmentName      = $displayName
            ComplianceState     = $state.ComplianceState
            Timestamp           = $state.Timestamp
          })
        }
      } catch {}
    }
  }
  
  # -----------------------------------------------------------
  # 2. POLICY EXEMPTIONS (fallback if Resource Graph not used)
  # -----------------------------------------------------------
  if (-not $useResourceGraph) {
    Write-Host "  -> Checking policy exemptions..." -NoNewline
    
    $exemptions = @()
    try { $exemptions = Get-AzPolicyExemption -ErrorAction SilentlyContinue } catch {}
    
    Write-Host " $($exemptions.Count) exemptions" -ForegroundColor $(if ($exemptions.Count -gt 0) { 'Yellow' } else { 'Green' })
    
    foreach ($ex in $exemptions) {
      $exId = $ex.ResourceId
      if (-not $exId) { $exId = $ex.Name }
      if (-not $exId) { continue }
      if ($processedExemptions.ContainsKey($exId)) { continue }
      $processedExemptions[$exId] = $true
      
      $isExpired = $false
      $daysToExpiry = $null
      $expiresOnFormatted = $null
      
      if ($ex.Properties.ExpiresOn) {
        $expiryDate = [datetime]$ex.Properties.ExpiresOn
        $daysToExpiry = ($expiryDate - (Get-Date)).Days
        $isExpired = $daysToExpiry -lt 0
        $expiresOnFormatted = $expiryDate.ToString("dd/MM/yyyy, HH:mm:ss")
      }
      
      # Get the assignment display name
      $assignmentDisplayName = ($ex.Properties.PolicyAssignmentId -split '/')[-1]
      try {
        $linkedAssignment = Get-AzPolicyAssignment -Id $ex.Properties.PolicyAssignmentId -ErrorAction SilentlyContinue
        if ($linkedAssignment.Properties.DisplayName) {
          $assignmentDisplayName = $linkedAssignment.Properties.DisplayName
        }
      } catch {}
      
      $exemptionReport.Add([PSCustomObject]@{
        Name              = if ($ex.Properties.DisplayName) { $ex.Properties.DisplayName } else { $ex.Name }
        ExemptionName     = $ex.Name
        Assignments       = $assignmentDisplayName
        Scope             = $sub.Name
        ScopeType         = 'Subscription'
        ExemptionCategory = $ex.Properties.ExemptionCategory
        CreatedBy         = $ex.Properties.Metadata.createdBy
        ExpirationDate    = if ($expiresOnFormatted) { $expiresOnFormatted } else { '--' }
        DaysToExpiry      = $daysToExpiry
        IsExpired         = $isExpired
        ResourceSelectors = if ($ex.Properties.ResourceSelectors) { ($ex.Properties.ResourceSelectors | ConvertTo-Json -Compress) } else { '--' }
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
  }
  
  # Count exemptions for this subscription (from either method)
  $subExemptionCount = ($exemptionReport | Where-Object { $_.Scope -eq $sub.Name }).Count
  
  # -----------------------------------------------------------
  # 3. CUSTOM POLICY DEFINITIONS
  # -----------------------------------------------------------
  Write-Host "  -> Collecting custom policies..." -NoNewline
  
  $customPolicies = @()
  try { $customPolicies = Get-AzPolicyDefinition -Custom -ErrorAction SilentlyContinue } catch {}
  
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
  # 4. REMEDIATION TASKS
  # -----------------------------------------------------------
  Write-Host "  -> Checking remediation tasks..." -NoNewline
  
  $remediations = @()
  try { $remediations = Get-AzPolicyRemediation -ErrorAction SilentlyContinue } catch {}
  
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
  $subCompliance = $complianceReport | Where-Object { $_.SubscriptionName -eq $sub.Name }
  $subNonCompliant = ($subCompliance | Measure-Object -Property NonCompliantResources -Sum).Sum
  if ($null -eq $subNonCompliant) { $subNonCompliant = 0 }
  $subCompliant = ($subCompliance | Measure-Object -Property CompliantResources -Sum).Sum
  if ($null -eq $subCompliant) { $subCompliant = 0 }
  $subTotal = $subCompliant + $subNonCompliant
  $subCompliancePercent = if ($subTotal -gt 0) { [math]::Round(($subCompliant / $subTotal) * 100, 1) } else { 100 }
  
  $highFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'High' }).Count
  $medFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Medium' }).Count
  $lowFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Low' }).Count
  
  $subscriptionSummary.Add([PSCustomObject]@{
    SubscriptionName      = $sub.Name
    SubscriptionId        = $sub.Id
    PolicyAssignments     = ($subCompliance | Where-Object { $_.Type -eq 'Policy' }).Count
    InitiativeAssignments = ($subCompliance | Where-Object { $_.Type -eq 'Initiative' }).Count
    CompliancePercent     = $subCompliancePercent
    ResourceCompliance    = Format-ComplianceString -Compliant $subCompliant -NonCompliant $subNonCompliant
    NonCompliantResources = $subNonCompliant
    Exemptions            = $subExemptionCount
    CustomPolicies        = $customPolicies.Count
    HighFindings          = $highFindings
    MediumFindings        = $medFindings
    LowFindings           = $lowFindings
    TotalFindings         = $highFindings + $medFindings + $lowFindings
  })
  
  # Add findings for poor compliance
  if ($subCompliancePercent -lt 70) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'High'
      Category         = 'Compliance'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = "Overall compliance is $subCompliancePercent% - below 70% threshold"
      Recommendation   = 'Review and remediate non-compliant resources'
    })
  } elseif ($subCompliancePercent -lt 90) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'Medium'
      Category         = 'Compliance'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = "Overall compliance is $subCompliancePercent% - below 90% target"
      Recommendation   = 'Continue remediation efforts to improve compliance'
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
  $subscriptionSummary | Format-Table SubscriptionName, CompliancePercent, ResourceCompliance, NonCompliantResources, Exemptions, TotalFindings -AutoSize
}

Write-Host "`n=== Compliance Overview ===" -ForegroundColor Cyan
$totalCompliant = ($complianceReport | Measure-Object -Property CompliantResources -Sum).Sum
$totalNonCompliant = ($complianceReport | Measure-Object -Property NonCompliantResources -Sum).Sum
$totalResources = $totalCompliant + $totalNonCompliant
$avgCompliance = if ($totalResources -gt 0) { [math]::Round(($totalCompliant / $totalResources) * 100, 1) } else { 100 }

Write-Host "  Overall compliance: $(Format-ComplianceString -Compliant $totalCompliant -NonCompliant $totalNonCompliant)" -ForegroundColor $(if ($avgCompliance -ge 90) { 'Green' } elseif ($avgCompliance -ge 70) { 'Yellow' } else { 'Red' })
Write-Host "  Total non-compliant resources: $totalNonCompliant" -ForegroundColor $(if ($totalNonCompliant -eq 0) { 'Green' } else { 'Yellow' })

Write-Host "`n=== Top Non-Compliant Assignments ===" -ForegroundColor Cyan
$topNonCompliant = $complianceReport | Where-Object { $_.NonCompliantResources -gt 0 } | Sort-Object -Property NonCompliantResources -Descending | Select-Object -First 10
if ($topNonCompliant) {
  $topNonCompliant | Format-Table Name, Scope, ResourceCompliance, NonCompliantResources -AutoSize
}

Write-Host "`n=== Exemption Summary ===" -ForegroundColor Cyan
$noExpiry = ($exemptionReport | Where-Object { $_.ExpirationDate -eq '--' }).Count
$withExpiry = ($exemptionReport | Where-Object { $_.ExpirationDate -ne '--' }).Count
Write-Host "  Total exemptions: $($exemptionReport.Count)" -ForegroundColor $(if ($exemptionReport.Count -gt 0) { 'Yellow' } else { 'Green' })
Write-Host "  With expiry date: $withExpiry" -ForegroundColor Green
Write-Host "  WITHOUT expiry date: $noExpiry" -ForegroundColor $(if ($noExpiry -gt 0) { 'Red' } else { 'Green' })

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

# Export sheets matching portal format
Export-Sheet -Data $subscriptionSummary -WorksheetName 'Subscription_Summary' -TableName 'Subscriptions'
Export-Sheet -Data $complianceReport -WorksheetName 'Compliance' -TableName 'Compliance'
Export-Sheet -Data $assignmentReport -WorksheetName 'Assignments' -TableName 'Assignments'
Export-Sheet -Data $exemptionReport -WorksheetName 'Exemptions' -TableName 'Exemptions'
Export-Sheet -Data $nonCompliantReport -WorksheetName 'Non_Compliant_Resources' -TableName 'NonCompliantResources'
Export-Sheet -Data $customPolicyReport -WorksheetName 'Custom_Policies' -TableName 'CustomPolicies'
Export-Sheet -Data $remediationReport -WorksheetName 'Remediations' -TableName 'Remediations'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

$overallSummary = @(
  [PSCustomObject]@{ Metric='Audit Date';Value=(Get-Date -Format 'yyyy-MM-dd HH:mm') }
  [PSCustomObject]@{ Metric='Subscriptions Audited';Value=$subscriptions.Count }
  [PSCustomObject]@{ Metric='Overall Compliance';Value="$(Format-ComplianceString -Compliant $totalCompliant -NonCompliant $totalNonCompliant)" }
  [PSCustomObject]@{ Metric='Compliance Percent';Value="$avgCompliance%" }
  [PSCustomObject]@{ Metric='Total Assignments';Value=$complianceReport.Count }
  [PSCustomObject]@{ Metric='Initiative Assignments';Value=($complianceReport | Where-Object { $_.Type -eq 'Initiative' }).Count }
  [PSCustomObject]@{ Metric='Policy Assignments';Value=($complianceReport | Where-Object { $_.Type -eq 'Policy' }).Count }
  [PSCustomObject]@{ Metric='Total Non-Compliant Resources';Value=$totalNonCompliant }
  [PSCustomObject]@{ Metric='Policy Exemptions';Value=$exemptionReport.Count }
  [PSCustomObject]@{ Metric='Exemptions Without Expiry';Value=$noExpiry }
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
