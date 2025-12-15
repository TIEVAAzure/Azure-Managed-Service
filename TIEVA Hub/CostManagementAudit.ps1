<#
.SYNOPSIS
  TIEVA Cost & Spend Management Audit Script
  
.DESCRIPTION
  Scans Azure subscriptions for cost management configurations including:
  - Budgets (amounts, periods, thresholds, alerts)
  - Cost allocation tags
  - Alert configurations and ITSM integration
  - Anomaly detection settings (Premium tier)
  - Historical spend data
  
  Outputs multi-sheet Excel workbook: Cost_Management_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current directory.
  
.PARAMETER IncludeSpendHistory
  Include last 3 months of spend data. Requires additional API calls.
  
.EXAMPLE
  .\CostManagementAudit.ps1
  
.EXAMPLE
  .\CostManagementAudit.ps1 -SubscriptionIds @("sub-id-1","sub-id-2") -IncludeSpendHistory
  
.NOTES
  Requires: Az.Accounts, Az.CostManagement, Az.Monitor, ImportExcel modules
  Permissions: Cost Management Reader, Reader on subscriptions
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = ".",
  [switch]$IncludeSpendHistory
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n======================================" -ForegroundColor Cyan
Write-Host "TIEVA Cost Management Audit v1.0" -ForegroundColor Cyan
Write-Host "======================================`n" -ForegroundColor Cyan

# ============================================================================
# HELPER FUNCTIONS
# ============================================================================

function Get-SubscriptionList {
  if ($SubscriptionIds) {
    $subs = @()
    foreach ($id in $SubscriptionIds) {
      try {
        $sub = Get-AzSubscription -SubscriptionId $id -ErrorAction Stop
        $subs += $sub
      } catch {
        Write-Warning "Could not access subscription $id : $_"
      }
    }
    return $subs
  } else {
    return Get-AzSubscription | Where-Object { $_.State -eq 'Enabled' }
  }
}

function Get-MonthStartEnd {
  param([int]$MonthsAgo = 0)
  $now = Get-Date
  $start = $now.AddMonths(-$MonthsAgo).Date.AddDays(1 - $now.Day)
  if ($MonthsAgo -eq 0) {
    $end = $now
  } else {
    $end = $start.AddMonths(1).AddDays(-1).AddHours(23).AddMinutes(59).AddSeconds(59)
  }
  return @{
    Start = $start.ToString("yyyy-MM-ddTHH:mm:ss")
    End = $end.ToString("yyyy-MM-ddTHH:mm:ss")
  }
}

# ============================================================================
# DATA COLLECTION
# ============================================================================

$subscriptions = Get-SubscriptionList

if (-not $subscriptions) {
  Write-Error "No accessible subscriptions found."
  exit 1
}

Write-Host "Found $($subscriptions.Count) subscription(s) to audit`n" -ForegroundColor Green

$budgetReport = @()
$subscriptionReport = @()
$alertReport = @()
$tagReport = @()
$spendReport = @()
$findings = @()

foreach ($sub in $subscriptions) {
  Write-Host "Processing: $($sub.Name) ($($sub.Id))" -ForegroundColor Yellow
  
  try {
    Set-AzContext -SubscriptionId $sub.Id -ErrorAction Stop | Out-Null
  } catch {
    Write-Warning "  Could not set context: $_"
    continue
  }
  
  # -----------------------------------------------------------
  # 1. BUDGETS
  # -----------------------------------------------------------
  Write-Host "  → Checking budgets..." -NoNewline
  
  $budgets = @()
  try {
    # Get budgets at subscription scope
    $budgetScope = "/subscriptions/$($sub.Id)"
    $budgets = Get-AzConsumptionBudget -Scope $budgetScope -ErrorAction SilentlyContinue
    
    if (-not $budgets) {
      $budgets = @()
    }
  } catch {
    Write-Verbose "    Could not retrieve budgets: $_"
  }
  
  Write-Host " $($budgets.Count) found" -ForegroundColor Cyan
  
  if ($budgets.Count -eq 0) {
    # No budgets configured
    $findings += [PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'High'
      Category         = 'Cost Management'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = 'No budgets configured for this subscription'
      Recommendation   = 'Configure monthly budget with forecast and actual spend alerts'
    }
  }
  
  foreach ($budget in $budgets) {
    $forecastThreshold = $null
    $actualThreshold = $null
    $alertEmails = @()
    
    # Parse notifications/alerts
    if ($budget.Notification) {
      foreach ($notifKey in $budget.Notification.Keys) {
        $notif = $budget.Notification[$notifKey]
        
        if ($notif.Enabled) {
          $threshold = $notif.Threshold
          $operator = $notif.Operator
          $thresholdType = $notif.ThresholdType
          
          if ($thresholdType -eq 'Forecasted') {
            if ($null -eq $forecastThreshold -or $threshold -lt $forecastThreshold) {
              $forecastThreshold = $threshold
            }
          } elseif ($thresholdType -eq 'Actual') {
            if ($null -eq $actualThreshold -or $threshold -lt $actualThreshold) {
              $actualThreshold = $threshold
            }
          }
          
          # Collect contact emails
          if ($notif.ContactEmails) {
            $alertEmails += $notif.ContactEmails
          }
        }
      }
    }
    
    $alertEmailsStr = ($alertEmails | Select-Object -Unique) -join '; '
    
    # Budget period
    $timeGrain = $budget.TimeGrain
    $period = switch ($timeGrain) {
      'Monthly' { 'Monthly' }
      'Quarterly' { 'Quarterly' }
      'Annually' { 'Annually' }
      default { $timeGrain }
    }
    
    $budgetAmount = $budget.Amount
    $currentSpend = $budget.CurrentSpend
    
    $budgetReport += [PSCustomObject]@{
      SubscriptionName      = $sub.Name
      SubscriptionId        = $sub.Id
      BudgetName            = $budget.Name
      BudgetAmount          = $budgetAmount
      Currency              = 'GBP'
      BudgetPeriod          = $period
      StartDate             = $budget.TimePeriod.StartDate
      EndDate               = $budget.TimePeriod.EndDate
      ForecastThreshold     = $forecastThreshold
      ActualThreshold       = $actualThreshold
      AlertRecipients       = $alertEmailsStr
      CurrentSpend          = $currentSpend
      BudgetRemaining       = ($budgetAmount - $currentSpend)
      PercentUsed           = if ($budgetAmount -gt 0) { [math]::Round(($currentSpend / $budgetAmount) * 100, 1) } else { 0 }
      Status                = if ($currentSpend -lt $budgetAmount) { 'Under Budget' } else { 'Over Budget' }
    }
    
    # Findings: Missing thresholds
    if ($null -eq $forecastThreshold) {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.Name
        ResourceGroup    = 'N/A'
        Detail           = 'Budget does not have forecast threshold alert configured'
        Recommendation   = 'Add forecast alert at 80% threshold for proactive notification'
      }
    }
    
    if ($null -eq $actualThreshold) {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.Name
        ResourceGroup    = 'N/A'
        Detail           = 'Budget does not have actual spend threshold alert configured'
        Recommendation   = 'Add actual spend alert at 100% threshold'
      }
    }
    
    if (-not $alertEmailsStr) {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.Name
        ResourceGroup    = 'N/A'
        Detail           = 'Budget has no email recipients configured for alerts'
        Recommendation   = 'Configure alert recipients including customer budget owners and TIEVA CloudOps'
      }
    }
  }
  
  # -----------------------------------------------------------
  # 2. COST ALERTS (Action Groups)
  # -----------------------------------------------------------
  Write-Host "  → Checking alert configurations..." -NoNewline
  
  $actionGroups = @()
  try {
    $actionGroups = Get-AzActionGroup -ErrorAction SilentlyContinue
  } catch {
    Write-Verbose "    Could not retrieve action groups: $_"
  }
  
  Write-Host " $($actionGroups.Count) action group(s)" -ForegroundColor Cyan
  
  foreach ($ag in $actionGroups) {
    $emailReceivers = @()
    $webhookReceivers = @()
    $itsmConnected = $false
    
    if ($ag.EmailReceiver) {
      $emailReceivers = $ag.EmailReceiver | ForEach-Object { $_.EmailAddress }
    }
    
    if ($ag.WebhookReceiver) {
      $webhookReceivers = $ag.WebhookReceiver | ForEach-Object { $_.ServiceUri }
      
      # Check if any webhook points to ITSM (common patterns)
      foreach ($uri in $webhookReceivers) {
        if ($uri -match 'servicenow|jira|itsm|freshservice|zendesk') {
          $itsmConnected = $true
          break
        }
      }
    }
    
    if ($ag.ItsmReceiver) {
      $itsmConnected = $true
    }
    
    $alertReport += [PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      ActionGroupName   = $ag.Name
      ResourceGroup     = $ag.ResourceGroupName
      Enabled           = $ag.Enabled
      EmailReceivers    = ($emailReceivers -join '; ')
      WebhookCount      = $webhookReceivers.Count
      ITSMConnected     = $itsmConnected
      Location          = $ag.Location
    }
  }
  
  # -----------------------------------------------------------
  # 3. COST ALLOCATION TAGS
  # -----------------------------------------------------------
  Write-Host "  → Analyzing cost allocation tags..." -NoNewline
  
  $resources = @()
  try {
    $resources = Get-AzResource -ErrorAction SilentlyContinue | Select-Object -First 500
  } catch {
    Write-Verbose "    Could not retrieve resources: $_"
  }
  
  $taggedResources = 0
  $commonTags = @{}
  $costAllocationTags = @('CostCenter', 'Department', 'Project', 'Environment', 'Owner', 'Application', 'BusinessUnit')
  
  foreach ($res in $resources) {
    if ($res.Tags -and $res.Tags.Count -gt 0) {
      $taggedResources++
      
      foreach ($tagKey in $res.Tags.Keys) {
        if (-not $commonTags.ContainsKey($tagKey)) {
          $commonTags[$tagKey] = 0
        }
        $commonTags[$tagKey]++
      }
    }
  }
  
  $tagCoverage = if ($resources.Count -gt 0) { [math]::Round(($taggedResources / $resources.Count) * 100, 1) } else { 0 }
  
  # Check for cost allocation tags
  $hasCostAllocationTags = $false
  foreach ($tag in $costAllocationTags) {
    if ($commonTags.ContainsKey($tag)) {
      $hasCostAllocationTags = $true
      break
    }
  }
  
  Write-Host " $tagCoverage% coverage" -ForegroundColor Cyan
  
  $tagReport += [PSCustomObject]@{
    SubscriptionName      = $sub.Name
    SubscriptionId        = $sub.Id
    TotalResources        = $resources.Count
    TaggedResources       = $taggedResources
    TagCoveragePercent    = $tagCoverage
    CommonTags            = (($commonTags.Keys | Select-Object -First 10) -join ', ')
    HasCostAllocationTags = $hasCostAllocationTags
  }
  
  # Finding: Low tag coverage
  if ($tagCoverage -lt 50) {
    $findings += [PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'Medium'
      Category         = 'Cost Allocation'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = "Only $tagCoverage% of resources have tags configured"
      Recommendation   = 'Implement tagging policy with cost allocation tags (CostCenter, Department, Project)'
    }
  }
  
  if (-not $hasCostAllocationTags) {
    $findings += [PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'High'
      Category         = 'Cost Allocation'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceGroup    = 'N/A'
      Detail           = 'No standard cost allocation tags detected'
      Recommendation   = 'Define and apply cost allocation tagging strategy for financial accountability'
    }
  }
  
  # -----------------------------------------------------------
  # 4. HISTORICAL SPEND DATA (if requested)
  # -----------------------------------------------------------
  if ($IncludeSpendHistory) {
    Write-Host "  → Retrieving spend history..." -NoNewline
    
    for ($i = 2; $i -ge 0; $i--) {
      $period = Get-MonthStartEnd -MonthsAgo $i
      $monthLabel = (Get-Date).AddMonths(-$i).ToString("MMM yyyy")
      
      try {
        # Use Azure Cost Management API
        $costParams = @{
          Scope = "/subscriptions/$($sub.Id)"
          Timeframe = 'Custom'
          TimePeriodFrom = $period.Start
          TimePeriodTo = $period.End
          Granularity = 'None'
          ErrorAction = 'SilentlyContinue'
        }
        
        $usage = Get-AzConsumptionUsageDetail @costParams | Measure-Object -Property PretaxCost -Sum
        $totalCost = if ($usage.Sum) { [math]::Round($usage.Sum, 2) } else { 0 }
        
        $spendReport += [PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Month            = $monthLabel
          TotalSpend       = $totalCost
          Currency         = 'GBP'
        }
      } catch {
        Write-Verbose "    Could not retrieve spend for $monthLabel : $_"
      }
    }
    
    Write-Host " 3 months retrieved" -ForegroundColor Cyan
  }
  
  # -----------------------------------------------------------
  # 5. SUBSCRIPTION SUMMARY
  # -----------------------------------------------------------
  
  $subscriptionReport += [PSCustomObject]@{
    SubscriptionName    = $sub.Name
    SubscriptionId      = $sub.Id
    State               = $sub.State
    BudgetCount         = $budgets.Count
    BudgetConfigured    = if ($budgets.Count -gt 0) { 'Yes' } else { 'No' }
    ActionGroupCount    = $actionGroups.Count
    ITSMIntegrated      = ($actionGroups | Where-Object { $_.ITSMConnected }).Count -gt 0
    TagCoverage         = $tagCoverage
    HasCostTags         = $hasCostAllocationTags
    FindingsCount       = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id }).Count
  }
}

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "AUDIT COMPLETE" -ForegroundColor Green
Write-Host "========================================`n" -ForegroundColor Green

# ============================================================================
# CONSOLE OUTPUT
# ============================================================================

Write-Host "`n=== Subscription Summary ===" -ForegroundColor Cyan
if ($subscriptionReport.Count -gt 0) {
  $subscriptionReport | Format-Table SubscriptionName, BudgetConfigured, ActionGroupCount, ITSMIntegrated, TagCoverage, FindingsCount -AutoSize
}

Write-Host "`n=== Budgets ===" -ForegroundColor Cyan
if ($budgetReport.Count -gt 0) {
  $budgetReport | Format-Table SubscriptionName, BudgetName, BudgetAmount, BudgetPeriod, PercentUsed, Status -AutoSize
} else {
  Write-Host "No budgets found across any subscriptions." -ForegroundColor Yellow
}

Write-Host "`n=== Findings Summary ===" -ForegroundColor Cyan
if ($findings.Count -gt 0) {
  $highFindings = ($findings | Where-Object Severity -eq 'High').Count
  $medFindings = ($findings | Where-Object Severity -eq 'Medium').Count
  $lowFindings = ($findings | Where-Object Severity -eq 'Low').Count
  
  Write-Host "  High Priority:   $highFindings" -ForegroundColor Red
  Write-Host "  Medium Priority: $medFindings" -ForegroundColor Yellow
  Write-Host "  Low Priority:    $lowFindings" -ForegroundColor Gray
  
  Write-Host "`nTop Findings:" -ForegroundColor Cyan
  $findings | Select-Object -First 10 | Format-Table SubscriptionName, Severity, Category, Detail -AutoSize
} else {
  Write-Host "No findings - excellent cost management posture!" -ForegroundColor Green
}

# ============================================================================
# EXCEL EXPORT
# ============================================================================

$ExportXlsx = $true
$XlsxPath = Join-Path $OutPath 'Cost_Management_Audit.xlsx'

if ($ExportXlsx) {
  Write-Host "`n=== Exporting to Excel ===" -ForegroundColor Cyan
  
  # Ensure ImportExcel module is available
  if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
    Write-Host "Installing ImportExcel module..." -ForegroundColor Yellow
    try {
      if (-not (Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue)) {
        Register-PSRepository -Default
      }
      Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue | Out-Null
      Install-Module ImportExcel -Scope CurrentUser -Force -ErrorAction Stop
    } catch {
      Write-Warning "Could not install ImportExcel automatically: $_"
      Write-Host "Please run: Install-Module ImportExcel -Scope CurrentUser" -ForegroundColor Yellow
      exit 1
    }
  }
  
  Import-Module ImportExcel -ErrorAction Stop
  
  # Remove existing file
  if (Test-Path $XlsxPath) {
    Remove-Item $XlsxPath -Force
  }
  
  # Helper function to export sheets
  function Export-Sheet {
    param($Data, $WorksheetName, $TableName, $Columns = $null)
    
    if ($Data.Count -eq 0) {
      Write-Host "  Skipping empty sheet: $WorksheetName" -ForegroundColor Gray
      return
    }
    
    $exportParams = @{
      Path          = $XlsxPath
      WorksheetName = $WorksheetName
      TableName     = $TableName
      TableStyle    = 'Medium9'
      AutoSize      = $true
      FreezeTopRow  = $true
      BoldTopRow    = $true
    }
    
    if ($Columns) {
      $Data | Select-Object $Columns | Export-Excel @exportParams
    } else {
      $Data | Export-Excel @exportParams
    }
    
    Write-Host "  ✓ $WorksheetName" -ForegroundColor Green
  }
  
  # Export all sheets
  Export-Sheet -Data $subscriptionReport -WorksheetName 'Subscription_Summary' -TableName 'Subscriptions'
  Export-Sheet -Data $budgetReport -WorksheetName 'Budgets' -TableName 'Budgets'
  Export-Sheet -Data $alertReport -WorksheetName 'Action_Groups' -TableName 'ActionGroups'
  Export-Sheet -Data $tagReport -WorksheetName 'Tagging_Coverage' -TableName 'Tags'
  
  if ($spendReport.Count -gt 0) {
    Export-Sheet -Data $spendReport -WorksheetName 'Spend_History' -TableName 'SpendHistory'
  }
  
  if ($findings.Count -gt 0) {
    Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'
  }
  
  # Summary sheet
  $overallSummary = @(
    [PSCustomObject]@{ Metric = 'Subscriptions Audited';     Value = $subscriptions.Count }
    [PSCustomObject]@{ Metric = 'Subscriptions with Budgets'; Value = ($subscriptionReport | Where-Object BudgetConfigured -eq 'Yes').Count }
    [PSCustomObject]@{ Metric = 'Total Budgets';              Value = $budgetReport.Count }
    [PSCustomObject]@{ Metric = 'Action Groups';              Value = $alertReport.Count }
    [PSCustomObject]@{ Metric = 'ITSM Integrated';            Value = ($alertReport | Where-Object ITSMConnected -eq $true).Count }
    [PSCustomObject]@{ Metric = 'High Priority Findings';     Value = ($findings | Where-Object Severity -eq 'High').Count }
    [PSCustomObject]@{ Metric = 'Medium Priority Findings';   Value = ($findings | Where-Object Severity -eq 'Medium').Count }
    [PSCustomObject]@{ Metric = 'Low Priority Findings';      Value = ($findings | Where-Object Severity -eq 'Low').Count }
  )
  
  Export-Sheet -Data $overallSummary -WorksheetName 'Summary' -TableName 'Summary'
  
  Write-Host "`nExcel export complete → $XlsxPath" -ForegroundColor Green
}

Write-Host "`n✓ Audit complete!" -ForegroundColor Green
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Review findings in the Findings sheet" -ForegroundColor White
Write-Host "  2. Upload Cost_Management_Audit.xlsx to the HTML analyzer" -ForegroundColor White
Write-Host "  3. Engage customers on subscriptions without budgets" -ForegroundColor White
Write-Host "  4. Configure missing alerts and cost allocation tags`n" -ForegroundColor White
