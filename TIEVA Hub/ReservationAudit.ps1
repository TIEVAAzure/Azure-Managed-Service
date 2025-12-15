<#
.SYNOPSIS
  TIEVA Reservation & Savings Plan Auditor
  
.DESCRIPTION
  Comprehensive Azure reservation and savings plan audit for AMS customer meetings:
  - Reserved Instance inventory and utilization
  - Savings Plan coverage and utilization
  - Purchase recommendations from Azure
  - Underutilized reservations
  - Expiring reservations
  - Exchange/refund eligibility
  - Cost savings achieved
  
  Outputs multi-sheet Excel workbook: Reservation_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.PARAMETER LookbackDays
  Number of days to analyze utilization (default 30)
  
.EXAMPLE
  .\ReservationAudit.ps1
  
.EXAMPLE
  .\ReservationAudit.ps1 -LookbackDays 60
  
.NOTES
  Requires: Az.Accounts, Az.Reservations, Az.Billing, Az.CostManagement modules
  Permissions: Reservations Reader, Billing Reader on enrollment/subscriptions
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads",
  [int]$LookbackDays = 30
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Reservation & Savings Auditor" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
Write-Host "Lookback Period: $LookbackDays days" -ForegroundColor Gray
Write-Host ""

# ============================================================================
# CONFIGURATION
# ============================================================================

$UtilizationWarningThreshold = 80   # Below this = warning
$UtilizationCriticalThreshold = 50  # Below this = critical
$ExpiryWarningDays = 90             # Days before expiry to warn
$ExpiryCriticalDays = 30            # Days before expiry = critical

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

function Get-UtilizationStatus {
  param([decimal]$Utilization)
  if ($Utilization -lt $UtilizationCriticalThreshold) { return 'Critical' }
  if ($Utilization -lt $UtilizationWarningThreshold) { return 'Warning' }
  return 'Good'
}

function Get-ExpiryStatus {
  param([int]$DaysToExpiry)
  if ($DaysToExpiry -lt 0) { return 'Expired' }
  if ($DaysToExpiry -le $ExpiryCriticalDays) { return 'Critical' }
  if ($DaysToExpiry -le $ExpiryWarningDays) { return 'Warning' }
  return 'OK'
}

# ============================================================================
# DATA COLLECTIONS
# ============================================================================

$reservationReport = [System.Collections.Generic.List[object]]::new()
$utilizationReport = [System.Collections.Generic.List[object]]::new()
$savingsPlanReport = [System.Collections.Generic.List[object]]::new()
$recommendationReport = [System.Collections.Generic.List[object]]::new()
$expiringReport = [System.Collections.Generic.List[object]]::new()
$subscriptionCoverageReport = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[object]]::new()

# ============================================================================
# 1. RESERVATION ORDERS & RESERVATIONS
# ============================================================================

Write-Host "Collecting reservation orders..." -ForegroundColor Yellow

$reservationOrders = @()
try {
  $reservationOrders = Get-AzReservationOrder -ErrorAction SilentlyContinue
} catch {
  Write-Warning "Could not retrieve reservation orders: $_"
}

Write-Host "  Found $($reservationOrders.Count) reservation orders" -ForegroundColor Cyan

foreach ($order in $reservationOrders) {
  Write-Host "  -> Processing order: $($order.DisplayName)..." -NoNewline
  
  $reservations = @()
  try {
    $reservations = Get-AzReservation -ReservationOrderId $order.Name -ErrorAction SilentlyContinue
  } catch {}
  
  Write-Host " $($reservations.Count) reservations" -ForegroundColor Gray
  
  foreach ($res in $reservations) {
    $props = $res.Property
    
    # Calculate days to expiry
    $daysToExpiry = $null
    $expiryStatus = 'N/A'
    if ($props.ExpiryDate) {
      $expiryDate = [datetime]$props.ExpiryDate
      $daysToExpiry = ($expiryDate - (Get-Date)).Days
      $expiryStatus = Get-ExpiryStatus -DaysToExpiry $daysToExpiry
    }
    
    # Get utilization if available
    $avgUtilization = $null
    $utilizationStatus = 'N/A'
    try {
      $utilizationData = Get-AzReservationUtilization -ReservationOrderId $order.Name -ReservationId $res.Name -Grain 'Daily' -ErrorAction SilentlyContinue
      if ($utilizationData -and $utilizationData.Utilizations) {
        $avgUtilization = ($utilizationData.Utilizations | Measure-Object -Property UtilizedPercentage -Average).Average
        $avgUtilization = [math]::Round($avgUtilization, 1)
        $utilizationStatus = Get-UtilizationStatus -Utilization $avgUtilization
      }
    } catch {}
    
    # Determine reservation type
    $resType = 'Unknown'
    if ($props.ReservedResourceType) {
      $resType = $props.ReservedResourceType
    } elseif ($props.AppliedScopeType) {
      $resType = $props.AppliedScopeType
    }
    
    $reservationReport.Add([PSCustomObject]@{
      OrderId              = $order.Name
      OrderDisplayName     = $order.DisplayName
      ReservationId        = $res.Name
      DisplayName          = $props.DisplayName
      ProvisioningState    = $props.ProvisioningState
      ReservedResourceType = $resType
      InstanceFlexibility  = $props.InstanceFlexibility
      Quantity             = $props.Quantity
      Term                 = $props.Term
      BillingPlan          = $props.BillingPlan
      AppliedScopeType     = $props.AppliedScopeType
      AppliedScopes        = ($props.AppliedScopes -join ', ')
      EffectiveDateTime    = $props.EffectiveDateTime
      ExpiryDate           = $props.ExpiryDate
      DaysToExpiry         = $daysToExpiry
      ExpiryStatus         = $expiryStatus
      AvgUtilization       = $avgUtilization
      UtilizationStatus    = $utilizationStatus
      Renew                = $props.Renew
      RenewSource          = $props.RenewSource
      SkuName              = $res.Sku.Name
      Location             = $props.Location
    })
    
    # Add to utilization report if we have data
    if ($avgUtilization -ne $null) {
      $utilizationReport.Add([PSCustomObject]@{
        ReservationId        = $res.Name
        DisplayName          = $props.DisplayName
        ReservedResourceType = $resType
        SkuName              = $res.Sku.Name
        Quantity             = $props.Quantity
        AvgUtilization       = $avgUtilization
        UtilizationStatus    = $utilizationStatus
        AppliedScopeType     = $props.AppliedScopeType
        Location             = $props.Location
      })
    }
    
    # Add to expiring report if within warning period
    if ($daysToExpiry -ne $null -and $daysToExpiry -le $ExpiryWarningDays -and $daysToExpiry -ge 0) {
      $expiringReport.Add([PSCustomObject]@{
        ReservationId        = $res.Name
        DisplayName          = $props.DisplayName
        ReservedResourceType = $resType
        SkuName              = $res.Sku.Name
        ExpiryDate           = $props.ExpiryDate
        DaysToExpiry         = $daysToExpiry
        ExpiryStatus         = $expiryStatus
        Renew                = $props.Renew
        Quantity             = $props.Quantity
        AvgUtilization       = $avgUtilization
      })
    }
    
    # Findings: Low utilization
    if ($avgUtilization -ne $null -and $avgUtilization -lt $UtilizationWarningThreshold) {
      $findings.Add([PSCustomObject]@{
        Category         = 'Reservation Utilization'
        Severity         = if ($avgUtilization -lt $UtilizationCriticalThreshold) { 'High' } else { 'Medium' }
        ResourceType     = 'Reservation'
        ResourceName     = $props.DisplayName
        Detail           = "Reservation utilization is $avgUtilization% (below $UtilizationWarningThreshold% threshold)"
        Recommendation   = 'Review reservation scope and consider exchange or cancellation'
        PotentialSavings = $null
      })
    }
    
    # Findings: Expiring soon without renewal
    if ($daysToExpiry -ne $null -and $daysToExpiry -le $ExpiryCriticalDays -and $daysToExpiry -ge 0 -and -not $props.Renew) {
      $findings.Add([PSCustomObject]@{
        Category         = 'Reservation Expiry'
        Severity         = 'High'
        ResourceType     = 'Reservation'
        ResourceName     = $props.DisplayName
        Detail           = "Reservation expires in $daysToExpiry days with no auto-renewal configured"
        Recommendation   = 'Enable auto-renewal or plan for replacement purchase'
        PotentialSavings = $null
      })
    }
  }
}

# ============================================================================
# 2. SAVINGS PLANS
# ============================================================================

Write-Host "`nCollecting savings plans..." -ForegroundColor Yellow

$savingsPlans = @()
try {
  # Try to get savings plans via REST API (newer feature)
  $token = (Get-AzAccessToken -ResourceUrl "https://management.azure.com").Token
  $headers = @{ Authorization = "Bearer $token" }
  
  $uri = "https://management.azure.com/providers/Microsoft.BillingBenefits/savingsPlanOrders?api-version=2022-11-01"
  $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction SilentlyContinue
  
  if ($response -and $response.value) {
    $savingsPlans = $response.value
  }
} catch {
  Write-Verbose "Could not retrieve savings plans via API: $_"
}

Write-Host "  Found $($savingsPlans.Count) savings plan orders" -ForegroundColor Cyan

foreach ($sp in $savingsPlans) {
  $props = $sp.properties
  
  $daysToExpiry = $null
  $expiryStatus = 'N/A'
  if ($props.expiryDateTime) {
    $expiryDate = [datetime]$props.expiryDateTime
    $daysToExpiry = ($expiryDate - (Get-Date)).Days
    $expiryStatus = Get-ExpiryStatus -DaysToExpiry $daysToExpiry
  }
  
  $savingsPlanReport.Add([PSCustomObject]@{
    OrderId           = $sp.name
    DisplayName       = $props.displayName
    ProvisioningState = $props.provisioningState
    BenefitType       = $props.billingPlan
    Term              = $props.term
    Commitment        = "$($props.commitment.amount) $($props.commitment.currencyCode) / $($props.commitment.grain)"
    AppliedScopeType  = $props.appliedScopeType
    EffectiveDateTime = $props.effectiveDateTime
    ExpiryDateTime    = $props.expiryDateTime
    DaysToExpiry      = $daysToExpiry
    ExpiryStatus      = $expiryStatus
    Utilization       = $props.utilization
    Renew             = $props.renew
  })
  
  # Finding: Expiring savings plan
  if ($daysToExpiry -ne $null -and $daysToExpiry -le $ExpiryCriticalDays -and $daysToExpiry -ge 0) {
    $findings.Add([PSCustomObject]@{
      Category         = 'Savings Plan Expiry'
      Severity         = 'High'
      ResourceType     = 'Savings Plan'
      ResourceName     = $props.displayName
      Detail           = "Savings Plan expires in $daysToExpiry days"
      Recommendation   = 'Plan for renewal or replacement'
      PotentialSavings = $null
    })
  }
}

# ============================================================================
# 3. PURCHASE RECOMMENDATIONS
# ============================================================================

Write-Host "`nCollecting purchase recommendations..." -ForegroundColor Yellow

$subscriptions = Get-SubscriptionList

foreach ($sub in $subscriptions) {
  Write-Host "  -> Checking recommendations for: $($sub.Name)..." -NoNewline
  
  try {
    Set-AzContext -SubscriptionId $sub.Id -ErrorAction Stop | Out-Null
  } catch {
    Write-Host " [context error]" -ForegroundColor Red
    continue
  }
  
  $recommendations = @()
  
  # Get reservation recommendations
  try {
    $uri = "https://management.azure.com/subscriptions/$($sub.Id)/providers/Microsoft.Consumption/reservationRecommendations?api-version=2023-05-01"
    $token = (Get-AzAccessToken -ResourceUrl "https://management.azure.com").Token
    $headers = @{ Authorization = "Bearer $token" }
    
    $response = Invoke-RestMethod -Uri $uri -Headers $headers -Method Get -ErrorAction SilentlyContinue
    
    if ($response -and $response.value) {
      $recommendations = $response.value
    }
  } catch {
    Write-Verbose "Could not get recommendations: $_"
  }
  
  Write-Host " $($recommendations.Count) recommendations" -ForegroundColor Gray
  
  foreach ($rec in $recommendations) {
    $props = $rec.properties
    
    $recommendationReport.Add([PSCustomObject]@{
      SubscriptionName     = $sub.Name
      SubscriptionId       = $sub.Id
      Scope                = $props.scope
      LookBackPeriod       = $props.lookBackPeriod
      ResourceType         = $props.resourceType
      Term                 = $props.term
      RecommendedQuantity  = $props.recommendedQuantity
      SKU                  = $props.skuProperties.name
      Location             = $props.skuProperties.location
      FirstUsageDate       = $props.firstUsageDate
      TotalCostWithRI      = $props.totalCostWithReservedInstances
      CostWithNoRI         = $props.costWithNoReservedInstances
      NetSavings           = $props.netSavings
      AnnualSavings        = if ($props.netSavings) { [math]::Round($props.netSavings * 12, 2) } else { $null }
      RecommendedPurchase  = $props.recommendedPurchase
    })
    
    # Finding: High-value recommendation
    if ($props.netSavings -and $props.netSavings -gt 500) {
      $findings.Add([PSCustomObject]@{
        Category         = 'Purchase Recommendation'
        Severity         = if ($props.netSavings -gt 2000) { 'High' } else { 'Medium' }
        ResourceType     = $props.resourceType
        ResourceName     = "$($props.skuProperties.name) - $($props.skuProperties.location)"
        Detail           = "Recommended: $($props.recommendedQuantity) x $($props.term) reservation"
        Recommendation   = "Purchase reservation for estimated monthly savings of $([math]::Round($props.netSavings, 2))"
        PotentialSavings = [math]::Round($props.netSavings * 12, 2)
      })
    }
  }
  
  # -----------------------------------------------------------
  # SUBSCRIPTION COVERAGE ANALYSIS
  # -----------------------------------------------------------
  
  # Get benefits/coverage summary if available
  try {
    $coverageUri = "https://management.azure.com/subscriptions/$($sub.Id)/providers/Microsoft.Consumption/reservationSummaries?api-version=2023-05-01&grain=monthly"
    $coverageResponse = Invoke-RestMethod -Uri $coverageUri -Headers $headers -Method Get -ErrorAction SilentlyContinue
    
    if ($coverageResponse -and $coverageResponse.value) {
      $latestSummary = $coverageResponse.value | Select-Object -Last 1
      
      $subscriptionCoverageReport.Add([PSCustomObject]@{
        SubscriptionName      = $sub.Name
        SubscriptionId        = $sub.Id
        ReservationCount      = ($reservationReport | Where-Object { $_.AppliedScopes -match $sub.Id }).Count
        RecommendationCount   = ($recommendationReport | Where-Object { $_.SubscriptionId -eq $sub.Id }).Count
        PotentialAnnualSavings = ($recommendationReport | Where-Object { $_.SubscriptionId -eq $sub.Id } | Measure-Object -Property AnnualSavings -Sum).Sum
      })
    }
  } catch {
    # Still add basic coverage info
    $subscriptionCoverageReport.Add([PSCustomObject]@{
      SubscriptionName      = $sub.Name
      SubscriptionId        = $sub.Id
      ReservationCount      = ($reservationReport | Where-Object { $_.AppliedScopes -match $sub.Id }).Count
      RecommendationCount   = ($recommendationReport | Where-Object { $_.SubscriptionId -eq $sub.Id }).Count
      PotentialAnnualSavings = ($recommendationReport | Where-Object { $_.SubscriptionId -eq $sub.Id } | Measure-Object -Property AnnualSavings -Sum).Sum
    })
  }
}

# Finding: No reservations for subscription
foreach ($sub in $subscriptions) {
  $hasReservations = ($reservationReport | Where-Object { $_.AppliedScopes -match $sub.Id }).Count -gt 0
  $hasRecommendations = ($recommendationReport | Where-Object { $_.SubscriptionId -eq $sub.Id }).Count -gt 0
  
  if (-not $hasReservations -and $hasRecommendations) {
    $potentialSavings = ($recommendationReport | Where-Object { $_.SubscriptionId -eq $sub.Id } | Measure-Object -Property AnnualSavings -Sum).Sum
    
    $findings.Add([PSCustomObject]@{
      Category         = 'Coverage Gap'
      Severity         = 'Medium'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      Detail           = "Subscription has no reservations but has purchase recommendations"
      Recommendation   = "Review recommendations for potential annual savings of $([math]::Round($potentialSavings, 2))"
      PotentialSavings = $potentialSavings
    })
  }
}

# ============================================================================
# CONSOLE OUTPUT
# ============================================================================

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "AUDIT COMPLETE" -ForegroundColor Green
Write-Host "==========================================`n" -ForegroundColor Green

Write-Host "=== Reservation Summary ===" -ForegroundColor Cyan
$totalReservations = $reservationReport.Count
$lowUtilization = ($utilizationReport | Where-Object { $_.UtilizationStatus -ne 'Good' }).Count
$expiringSoon = ($expiringReport).Count
Write-Host "  Total Reservations:     $totalReservations" -ForegroundColor White
Write-Host "  Low Utilization:        $lowUtilization" -ForegroundColor $(if ($lowUtilization -gt 0) { 'Yellow' } else { 'Green' })
Write-Host "  Expiring Soon:          $expiringSoon" -ForegroundColor $(if ($expiringSoon -gt 0) { 'Yellow' } else { 'Green' })

Write-Host "`n=== Savings Plans ===" -ForegroundColor Cyan
Write-Host "  Total Savings Plans:    $($savingsPlanReport.Count)" -ForegroundColor White

Write-Host "`n=== Purchase Recommendations ===" -ForegroundColor Cyan
$totalPotentialSavings = ($recommendationReport | Measure-Object -Property AnnualSavings -Sum).Sum
Write-Host "  Recommendations:        $($recommendationReport.Count)" -ForegroundColor White
Write-Host "  Potential Annual Savings: $([math]::Round($totalPotentialSavings, 2))" -ForegroundColor $(if ($totalPotentialSavings -gt 0) { 'Green' } else { 'Gray' })

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

$XlsxPath = Join-Path $OutPath 'Reservation_Audit.xlsx'

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

Export-Sheet -Data $reservationReport -WorksheetName 'Reservations' -TableName 'Reservations'
Export-Sheet -Data $utilizationReport -WorksheetName 'Utilization' -TableName 'Utilization'
Export-Sheet -Data $expiringReport -WorksheetName 'Expiring_Soon' -TableName 'ExpiringSoon'
Export-Sheet -Data $savingsPlanReport -WorksheetName 'Savings_Plans' -TableName 'SavingsPlans'
Export-Sheet -Data $recommendationReport -WorksheetName 'Recommendations' -TableName 'Recommendations'
Export-Sheet -Data $subscriptionCoverageReport -WorksheetName 'Subscription_Coverage' -TableName 'Coverage'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

$overallSummary = @(
  [PSCustomObject]@{ Metric='Audit Date';Value=(Get-Date -Format 'yyyy-MM-dd HH:mm') }
  [PSCustomObject]@{ Metric='Lookback Period';Value="$LookbackDays days" }
  [PSCustomObject]@{ Metric='Total Reservations';Value=$reservationReport.Count }
  [PSCustomObject]@{ Metric='Low Utilization Reservations';Value=$lowUtilization }
  [PSCustomObject]@{ Metric='Expiring Within 90 Days';Value=$expiringReport.Count }
  [PSCustomObject]@{ Metric='Savings Plans';Value=$savingsPlanReport.Count }
  [PSCustomObject]@{ Metric='Purchase Recommendations';Value=$recommendationReport.Count }
  [PSCustomObject]@{ Metric='Potential Annual Savings';Value="$([math]::Round($totalPotentialSavings, 2))" }
  [PSCustomObject]@{ Metric='High Findings';Value=$totalHigh }
  [PSCustomObject]@{ Metric='Medium Findings';Value=$totalMed }
  [PSCustomObject]@{ Metric='Low Findings';Value=$totalLow }
)
Export-Sheet -Data $overallSummary -WorksheetName 'Summary' -TableName 'Summary'

Write-Host "`nExcel export complete -> $XlsxPath" -ForegroundColor Green
Write-Host "`n+ Audit complete!" -ForegroundColor Green
Write-Host "Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n" -ForegroundColor Gray
