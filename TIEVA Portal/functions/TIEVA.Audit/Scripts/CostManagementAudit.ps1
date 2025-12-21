<#
.SYNOPSIS
  TIEVA Cost & Spend Management Audit Script v3
  Fixed for guest accounts, SecureString tokens, and correct API endpoint
  
.DESCRIPTION
  Audits Azure subscriptions for budget configurations, alerts, and cost management posture.
  Uses Microsoft.CostManagement API (not deprecated Consumption API).
  Handles SecureString tokens in newer Az module versions.
  Supports guest user access to customer tenants.
  
.PARAMETER SubscriptionIds
  Array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for Excel file. Defaults to current directory.
  
.PARAMETER TenantId
  Target tenant ID for guest user access.
  
.PARAMETER IncludeSpendHistory
  Include historical spend data (optional - increases runtime)
  
.EXAMPLE
  .\CostManagementAudit.ps1 -SubscriptionIds @("sub1","sub2") -TenantId "tenant-guid"
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = ".",
  [string]$TenantId,
  [switch]$IncludeSpendHistory
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n======================================" -ForegroundColor Cyan
Write-Host "TIEVA Cost Management Audit v3.0" -ForegroundColor Cyan
Write-Host "======================================`n" -ForegroundColor Cyan

# Check authentication
$context = Get-AzContext
if ($null -eq $context) {
  Write-Host "Not authenticated. Run: Connect-AzAccount -TenantId <tenant-id>" -ForegroundColor Red
  exit 1
}

Write-Host "✓ Authenticated as: $($context.Account.Id)" -ForegroundColor Green
Write-Host "  Tenant: $($context.Tenant.Id)" -ForegroundColor Gray

# Use tenant from context if not specified
if (-not $TenantId) {
  $TenantId = $context.Tenant.Id
}

# Function to get token (handles SecureString in newer Az modules)
function Get-PlainToken {
  param([string]$TenantId)
  
  $tokenResponse = Get-AzAccessToken -ResourceUrl "https://management.azure.com" -TenantId $TenantId
  $token = $tokenResponse.Token
  
  # Handle SecureString (newer Az module versions)
  if ($token -is [System.Security.SecureString]) {
    $token = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
      [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($token)
    )
  }
  
  return $token
}

# Get subscriptions
function Get-SubscriptionList {
  if ($SubscriptionIds -and $SubscriptionIds.Count -gt 0) {
    $subs = @()
    foreach ($id in $SubscriptionIds) {
      try {
        $sub = Get-AzSubscription -SubscriptionId $id -TenantId $TenantId -ErrorAction Stop
        $subs += $sub
      } catch {
        Write-Warning "Could not access subscription $id"
      }
    }
    return $subs
  } else {
    return Get-AzSubscription -TenantId $TenantId | Where-Object { $_.State -eq 'Enabled' }
  }
}

$subscriptions = Get-SubscriptionList

if (-not $subscriptions -or $subscriptions.Count -eq 0) {
  Write-Error "No accessible subscriptions found."
  exit 1
}

Write-Host "Found $($subscriptions.Count) subscription(s) to audit`n" -ForegroundColor Green

$budgetReport = @()
$subscriptionReport = @()
$alertReport = @()
$findings = @()
$spendHistoryReport = @()

foreach ($sub in $subscriptions) {
  Write-Host "Processing: $($sub.Name)" -ForegroundColor Yellow
  
  try {
    $null = Set-AzContext -SubscriptionId $sub.Id -TenantId $TenantId -ErrorAction Stop
  } catch {
    Write-Warning "  Could not set context"
    continue
  }
  
  # Get fresh token for this tenant
  $token = Get-PlainToken -TenantId $TenantId
  $headers = @{
    'Authorization' = "Bearer $token"
    'Content-Type'  = 'application/json'
  }
  
  # Use the CORRECT API: Microsoft.CostManagement (not Consumption)
  Write-Host "  → Checking budgets..." -NoNewline
  $budgets = @()
  
  $uri = "https://management.azure.com/subscriptions/$($sub.Id)/providers/Microsoft.CostManagement/budgets?api-version=2024-08-01"
  
  try {
    $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction Stop
    
    if ($response.value) {
      $budgets = $response.value
    }
  } catch {
    # Silent fail - will show 0 budgets
  }
  
  Write-Host " $($budgets.Count) found" -ForegroundColor $(if ($budgets.Count -gt 0) { "Green" } else { "Yellow" })
  
  # Finding if no budgets
  if ($budgets.Count -eq 0) {
    $findings += [PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'High'
      Category         = 'Cost Management'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceId       = "/subscriptions/$($sub.Id)"
      Detail           = 'No budgets configured for this subscription'
      Recommendation   = 'Configure monthly budget with forecast and actual spend alerts'
    }
  }
  
  foreach ($budget in $budgets) {
    $props = $budget.properties
    
    $forecastThreshold = $null
    $actualThreshold = $null
    $alertEmails = @()
    
    if ($props.notifications) {
      foreach ($notifName in $props.notifications.PSObject.Properties.Name) {
        $notif = $props.notifications.$notifName
        if ($notif.enabled) {
          $threshold = $notif.threshold
          $thresholdType = $notif.thresholdType
          
          if ($thresholdType -eq 'Forecasted') {
            if ($null -eq $forecastThreshold -or $threshold -lt $forecastThreshold) {
              $forecastThreshold = $threshold
            }
          } elseif ($thresholdType -eq 'Actual') {
            if ($null -eq $actualThreshold -or $threshold -lt $actualThreshold) {
              $actualThreshold = $threshold
            }
          }
          
          if ($notif.contactEmails) {
            # Handle both string and array formats
            if ($notif.contactEmails -is [string]) {
              $alertEmails += $notif.contactEmails -split '\s+'
            } else {
              $alertEmails += $notif.contactEmails
            }
          }
        }
      }
    }
    
    $alertEmailsStr = ($alertEmails | Select-Object -Unique) -join '; '
    $budgetAmount = $props.amount
    $currentSpend = if ($props.currentSpend) { $props.currentSpend.amount } else { 0 }
    $forecastSpend = if ($props.forecastSpend) { $props.forecastSpend.amount } else { 0 }
    $currency = if ($props.currentSpend) { $props.currentSpend.unit } else { 'GBP' }
    
    $budgetReport += [PSCustomObject]@{
      SubscriptionName      = $sub.Name
      SubscriptionId        = $sub.Id
      BudgetName            = $budget.name
      BudgetAmount          = [math]::Round($budgetAmount, 2)
      Currency              = $currency
      BudgetPeriod          = $props.timeGrain
      StartDate             = $props.timePeriod.startDate
      EndDate               = $props.timePeriod.endDate
      ForecastThreshold     = $forecastThreshold
      ActualThreshold       = $actualThreshold
      AlertRecipients       = $alertEmailsStr
      CurrentSpend          = [math]::Round($currentSpend, 2)
      ForecastSpend         = [math]::Round($forecastSpend, 2)
      BudgetRemaining       = [math]::Round(($budgetAmount - $currentSpend), 2)
      PercentUsed           = if ($budgetAmount -gt 0) { [math]::Round(($currentSpend / $budgetAmount) * 100, 1) } else { 0 }
      PercentForecasted     = if ($budgetAmount -gt 0) { [math]::Round(($forecastSpend / $budgetAmount) * 100, 1) } else { 0 }
      Status                = if ($currentSpend -lt $budgetAmount) { 'Under Budget' } else { 'Over Budget' }
    }
    
    # Findings for missing configurations
    if ($null -eq $forecastThreshold) {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.name
        ResourceId       = $budget.id
        Detail           = 'Budget missing forecast threshold alert'
        Recommendation   = 'Add forecast alert at 80% threshold'
      }
    } else {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Info'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.name
        ResourceId       = $budget.id
        Detail           = "Forecast alert configured at $($forecastThreshold)% threshold"
        Recommendation   = 'No action required'
      }
    }
    
    if ($null -eq $actualThreshold) {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.name
        ResourceId       = $budget.id
        Detail           = 'Budget missing actual spend threshold alert'
        Recommendation   = 'Add actual spend alert at 100% threshold'
      }
    } else {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Info'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.name
        ResourceId       = $budget.id
        Detail           = "Actual spend alert configured at $($actualThreshold)% threshold"
        Recommendation   = 'No action required'
      }
    }
    
    if (-not $alertEmailsStr) {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.name
        ResourceId       = $budget.id
        Detail           = 'Budget has no email recipients'
        Recommendation   = 'Configure alert recipients'
      }
    } else {
      $findings += [PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Info'
        Category         = 'Budget Configuration'
        ResourceType     = 'Budget'
        ResourceName     = $budget.name
        ResourceId       = $budget.id
        Detail           = "Alert recipients configured: $alertEmailsStr"
        Recommendation   = 'No action required'
      }
    }
  }
  
  # Action Groups
  Write-Host "  → Checking action groups..." -NoNewline
  $actionGroups = @()
  try {
    $actionGroups = @(Get-AzActionGroup -ErrorAction SilentlyContinue)
  } catch { }
  Write-Host " $($actionGroups.Count) found" -ForegroundColor Cyan
  
  foreach ($ag in $actionGroups) {
    $emailReceivers = @()
    if ($ag.EmailReceiver) {
      $emailReceivers = $ag.EmailReceiver | ForEach-Object { $_.EmailAddress }
    }
    
    $alertReport += [PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      ActionGroupName  = $ag.Name
      ResourceGroup    = $ag.ResourceGroupName
      Enabled          = $ag.Enabled
      EmailReceivers   = ($emailReceivers -join '; ')
      ITSMConnected    = [bool]($ag.ItsmReceiver -or $ag.WebhookReceiver)
    }
  }
  
  # Spend History (if requested)
  if ($IncludeSpendHistory) {
    Write-Host "  → Fetching spend history..." -NoNewline
    
    try {
      # Get last 3 months of spend data using Cost Management Query API
      $endDate = Get-Date
      $startDate = $endDate.AddMonths(-3)
      
      $queryUri = "https://management.azure.com/subscriptions/$($sub.Id)/providers/Microsoft.CostManagement/query?api-version=2023-11-01"
      
      $queryBody = @{
        type = "ActualCost"
        timeframe = "Custom"
        timePeriod = @{
          from = $startDate.ToString("yyyy-MM-dd")
          to = $endDate.ToString("yyyy-MM-dd")
        }
        dataset = @{
          granularity = "Monthly"
          aggregation = @{
            totalCost = @{
              name = "Cost"
              function = "Sum"
            }
          }
        }
      } | ConvertTo-Json -Depth 10
      
      $costResponse = Invoke-RestMethod -Uri $queryUri -Headers $headers -Method Post -Body $queryBody -ErrorAction Stop
      
      if ($costResponse.properties.rows) {
        foreach ($row in $costResponse.properties.rows) {
          $spendHistoryReport += [PSCustomObject]@{
            SubscriptionName = $sub.Name
            SubscriptionId   = $sub.Id
            Period           = $row[1]  # Date
            Cost             = [math]::Round($row[0], 2)  # Cost
            Currency         = $costResponse.properties.columns[2].name  # Currency info
          }
        }
        Write-Host " $($costResponse.properties.rows.Count) months" -ForegroundColor Green
      } else {
        Write-Host " no data" -ForegroundColor Yellow
      }
    } catch {
      Write-Host " failed" -ForegroundColor Red
    }
  }
  
  # Subscription summary
  $subscriptionReport += [PSCustomObject]@{
    SubscriptionName = $sub.Name
    SubscriptionId   = $sub.Id
    State            = $sub.State
    BudgetCount      = $budgets.Count
    BudgetConfigured = if ($budgets.Count -gt 0) { 'Yes' } else { 'No' }
    ActionGroupCount = $actionGroups.Count
    FindingsCount    = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id }).Count
  }
}

Write-Host "`n========================================" -ForegroundColor Green
Write-Host "AUDIT COMPLETE" -ForegroundColor Green
Write-Host "========================================`n" -ForegroundColor Green

Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Subscriptions: $($subscriptions.Count)" -ForegroundColor White
Write-Host "  Budgets: $($budgetReport.Count)" -ForegroundColor White
Write-Host "  Findings: $($findings.Count)" -ForegroundColor White

if ($findings.Count -gt 0) {
  $high = ($findings | Where-Object Severity -eq 'High').Count
  $med = ($findings | Where-Object Severity -eq 'Medium').Count
  Write-Host "    High: $high | Medium: $med" -ForegroundColor Yellow
}

# Excel export
Write-Host "`nExporting to Excel..." -ForegroundColor Yellow

if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
  Write-Host "  Installing ImportExcel module..." -ForegroundColor Yellow
  Install-Module ImportExcel -Scope CurrentUser -Force
}

Import-Module ImportExcel

$XlsxPath = Join-Path $OutPath 'Cost_Management_Audit.xlsx'

if (Test-Path $XlsxPath) {
  Remove-Item $XlsxPath -Force
}

function Export-Sheet {
  param($Data, $WorksheetName, $TableName)
  if ($Data.Count -eq 0) { return }
  $Data | Export-Excel -Path $XlsxPath -WorksheetName $WorksheetName -TableName $TableName `
    -TableStyle 'Medium9' -AutoSize -FreezeTopRow -BoldTopRow
  Write-Host "  ✓ $WorksheetName ($($Data.Count) rows)" -ForegroundColor Green
}

Export-Sheet -Data $subscriptionReport -WorksheetName 'Subscription_Summary' -TableName 'Subscriptions'
Export-Sheet -Data $budgetReport -WorksheetName 'Budgets' -TableName 'Budgets'
Export-Sheet -Data $alertReport -WorksheetName 'Action_Groups' -TableName 'ActionGroups'

# Always create Findings sheet (even if empty) for consistent parsing
if ($findings.Count -eq 0) {
  # Create placeholder row that indicates no findings
  $findings = @([PSCustomObject]@{
    SubscriptionName = 'N/A'
    SubscriptionId   = 'N/A'
    Severity         = 'Info'
    Category         = 'Cost Management'
    ResourceType     = 'Audit'
    ResourceName     = 'Cost Management Audit'
    ResourceId       = 'N/A'
    Detail           = 'No cost management findings - all budgets properly configured'
    Recommendation   = 'Continue monitoring budget utilization'
  })
}
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

if ($IncludeSpendHistory -and $spendHistoryReport.Count -gt 0) {
  Export-Sheet -Data $spendHistoryReport -WorksheetName 'Spend_History' -TableName 'SpendHistory'
}

$summary = @(
  [PSCustomObject]@{ Metric = 'Subscriptions Audited'; Value = $subscriptions.Count }
  [PSCustomObject]@{ Metric = 'Budgets Found'; Value = $budgetReport.Count }
  [PSCustomObject]@{ Metric = 'High Findings'; Value = ($findings | Where-Object Severity -eq 'High').Count }
  [PSCustomObject]@{ Metric = 'Medium Findings'; Value = ($findings | Where-Object Severity -eq 'Medium').Count }
)
Export-Sheet -Data $summary -WorksheetName 'Summary' -TableName 'Summary'

Write-Host "`n✓ Complete: $XlsxPath" -ForegroundColor Green
Write-Host "`nNext: Upload to TIEVA_Cost_Management_Analyzer.html`n" -ForegroundColor Cyan
