<#
.SYNOPSIS
  TIEVA Security Posture Auditor
  
.DESCRIPTION
  Focused Azure security audit - high-level posture only:
  - Secure Score percentage
  - Defender plan coverage (what's enabled/disabled)
  - Active security alerts (threats requiring attention)
  - Security contact configuration
  
  Does NOT include: Individual security recommendations (too noisy, use Azure Portal)
  
  Outputs multi-sheet Excel workbook: Security_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.EXAMPLE
  .\SecurityAudit.ps1 -SubscriptionIds @("sub-id-1")
  
.NOTES
  Requires: Az.Accounts, Az.Security, ImportExcel modules
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
Write-Host "TIEVA Security Posture Auditor" -ForegroundColor Cyan
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
# DATA COLLECTION
# ============================================================================

$subscriptions = Get-SubscriptionList
Write-Host "Auditing $($subscriptions.Count) subscription(s)..." -ForegroundColor Green

# Initialize result collections
$secureScores = [System.Collections.ArrayList]::new()
$defenderPlans = [System.Collections.ArrayList]::new()
$activeAlerts = [System.Collections.ArrayList]::new()
$securityContacts = [System.Collections.ArrayList]::new()
$findings = [System.Collections.ArrayList]::new()
$summary = [System.Collections.ArrayList]::new()

# Critical Defender plans that should be enabled
$criticalPlans = @('VirtualMachines', 'SqlServers', 'AppServices', 'StorageAccounts', 'KeyVaults', 'Containers', 'Dns', 'Arm')

foreach ($sub in $subscriptions) {
  Write-Host "`nProcessing: $($sub.Name)" -ForegroundColor Yellow
  
  try {
    Set-AzContext -SubscriptionId $sub.Id -TenantId $sub.TenantId -ErrorAction Stop | Out-Null
  }
  catch {
    Write-Host "  Skipping - Cannot access subscription: $($_.Exception.Message)" -ForegroundColor Red
    continue
  }
  
  $subSecureScore = $null
  $subDefenderEnabled = 0
  $subDefenderTotal = 0
  $subActiveAlerts = 0
  $subHasContact = $false
  
  # -------------------------------------------------------------------------
  # SECURE SCORE
  # -------------------------------------------------------------------------
  Write-Host "  Getting Secure Score..."
  try {
    $scores = Get-AzSecuritySecureScore -ErrorAction SilentlyContinue
    foreach ($score in $scores) {
      $percentage = if ($score.MaxScore -gt 0) { [math]::Round(($score.CurrentScore / $score.MaxScore) * 100, 0) } else { 0 }
      $subSecureScore = $percentage
      
      $secureScores.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        CurrentScore     = [math]::Round($score.CurrentScore, 2)
        MaxScore         = [math]::Round($score.MaxScore, 2)
        Percentage       = $percentage
      }) | Out-Null
      
      # Finding: Low secure score
      if ($percentage -lt 50) {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Severity         = 'High'
          Category         = 'Secure Score'
          ResourceType     = 'Subscription'
          ResourceName     = $sub.Name
          ResourceId       = "/subscriptions/$($sub.Id)"
          Detail           = "Secure Score is critically low at $percentage%"
          Recommendation   = 'Review Defender for Cloud recommendations in Azure Portal'
        }) | Out-Null
      } elseif ($percentage -lt 70) {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Severity         = 'Medium'
          Category         = 'Secure Score'
          ResourceType     = 'Subscription'
          ResourceName     = $sub.Name
          ResourceId       = "/subscriptions/$($sub.Id)"
          Detail           = "Secure Score is below target at $percentage%"
          Recommendation   = 'Review Defender for Cloud recommendations in Azure Portal'
        }) | Out-Null
      }
    }
  }
  catch {
    Write-Host "    Could not get Secure Score: $_" -ForegroundColor Gray
  }
  
  # -------------------------------------------------------------------------
  # DEFENDER PLANS
  # -------------------------------------------------------------------------
  Write-Host "  Getting Defender Plans..."
  try {
    $pricing = Get-AzSecurityPricing -ErrorAction SilentlyContinue
    foreach ($plan in $pricing) {
      $isEnabled = $plan.PricingTier -eq 'Standard'
      $isCritical = $plan.Name -in $criticalPlans
      
      if ($isCritical) {
        $subDefenderTotal++
        if ($isEnabled) { $subDefenderEnabled++ }
      }
      
      $defenderPlans.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        PlanName         = $plan.Name
        Status           = if ($isEnabled) { 'Enabled' } else { 'Disabled' }
        IsCritical       = if ($isCritical) { 'Yes' } else { 'No' }
        Subplan          = $plan.Subplan
      }) | Out-Null
      
      # Finding: Critical Defender plan disabled
      if (-not $isEnabled -and $isCritical) {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Severity         = 'Medium'
          Category         = 'Defender Coverage'
          ResourceType     = 'Defender Plan'
          ResourceName     = "Defender for $($plan.Name)"
          ResourceId       = "/subscriptions/$($sub.Id)/providers/Microsoft.Security/pricings/$($plan.Name)"
          Detail           = "Defender for $($plan.Name) is not enabled"
          Recommendation   = "Enable Defender for $($plan.Name) for threat protection"
        }) | Out-Null
      }
    }
  }
  catch {
    Write-Host "    Could not get Defender plans: $_" -ForegroundColor Gray
  }
  
  # -------------------------------------------------------------------------
  # ACTIVE ALERTS ONLY
  # -------------------------------------------------------------------------
  Write-Host "  Getting Active Alerts..."
  try {
    $alerts = Get-AzSecurityAlert -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq 'Active' }
    foreach ($alert in $alerts) {
      $subActiveAlerts++
      
      $activeAlerts.Add([PSCustomObject]@{
        SubscriptionName  = $sub.Name
        SubscriptionId    = $sub.Id
        AlertName         = $alert.AlertDisplayName
        Severity          = $alert.Severity
        Status            = $alert.Status
        StartTime         = $alert.StartTimeUtc
        CompromisedEntity = $alert.CompromisedEntity
        Description       = $alert.Description
      }) | Out-Null
      
      # Finding: Active alert
      $findingSeverity = switch ($alert.Severity) {
        'High'   { 'High' }
        'Medium' { 'Medium' }
        default  { 'Low' }
      }
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = $findingSeverity
        Category         = 'Active Threat'
        ResourceType     = 'Security Alert'
        ResourceName     = $alert.AlertDisplayName
        ResourceId       = $alert.CompromisedEntity
        Detail           = $alert.Description
        Recommendation   = 'Investigate and resolve this security alert immediately'
      }) | Out-Null
    }
  }
  catch {
    Write-Host "    Could not get alerts: $_" -ForegroundColor Gray
  }
  
  # -------------------------------------------------------------------------
  # SECURITY CONTACTS
  # -------------------------------------------------------------------------
  Write-Host "  Getting Security Contacts..."
  try {
    $contacts = Get-AzSecurityContact -ErrorAction SilentlyContinue
    if ($contacts) {
      foreach ($contact in $contacts) {
        $subHasContact = $true
        $securityContacts.Add([PSCustomObject]@{
          SubscriptionName   = $sub.Name
          SubscriptionId     = $sub.Id
          Email              = $contact.Email
          AlertNotifications = $contact.AlertNotifications
          AlertsToAdmins     = $contact.AlertsToAdmins
        }) | Out-Null
      }
    } else {
      # Finding: No security contact
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Configuration'
        ResourceType     = 'Security Contact'
        ResourceName     = $sub.Name
        ResourceId       = "/subscriptions/$($sub.Id)"
        Detail           = "No security contact email configured"
        Recommendation   = "Configure security contact to receive alert notifications"
      }) | Out-Null
    }
  }
  catch {
    Write-Host "    Could not get security contacts: $_" -ForegroundColor Gray
  }
  
  # -------------------------------------------------------------------------
  # SUBSCRIPTION SUMMARY
  # -------------------------------------------------------------------------
  $subFindings = $findings | Where-Object { $_.SubscriptionId -eq $sub.Id }
  
  $summary.Add([PSCustomObject]@{
    SubscriptionName     = $sub.Name
    SubscriptionId       = $sub.Id
    SecureScore          = if ($subSecureScore) { "$subSecureScore%" } else { 'N/A' }
    DefenderCoverage     = "$subDefenderEnabled / $subDefenderTotal critical plans"
    ActiveAlerts         = $subActiveAlerts
    SecurityContact      = if ($subHasContact) { 'Configured' } else { 'Missing' }
    HighFindings         = ($subFindings | Where-Object { $_.Severity -eq 'High' }).Count
    MediumFindings       = ($subFindings | Where-Object { $_.Severity -eq 'Medium' }).Count
    LowFindings          = ($subFindings | Where-Object { $_.Severity -eq 'Low' }).Count
    AuditDate            = Get-Date -Format 'yyyy-MM-dd HH:mm'
  }) | Out-Null
}

# ============================================================================
# EXPORT TO EXCEL
# ============================================================================

$outputFile = Join-Path $OutPath "Security_Audit.xlsx"
Write-Host "`nExporting to Excel: $outputFile" -ForegroundColor Cyan

if (Test-Path $outputFile) { Remove-Item $outputFile -Force }

function Export-Sheet { param($Data, $WorksheetName, $TableName)
  if (-not $Data -or $Data.Count -eq 0) { Write-Host "  Skipping empty: $WorksheetName" -ForegroundColor Gray; return }
  $Data | Export-Excel -Path $outputFile -WorksheetName $WorksheetName -TableName $TableName -TableStyle 'Medium9' -AutoSize -FreezeTopRow -BoldTopRow
  Write-Host "  + $WorksheetName ($($Data.Count) rows)" -ForegroundColor Green
}

Export-Sheet -Data $summary -WorksheetName 'Summary' -TableName 'Summary'
Export-Sheet -Data $secureScores -WorksheetName 'Secure Scores' -TableName 'SecureScores'
Export-Sheet -Data $defenderPlans -WorksheetName 'Defender Plans' -TableName 'DefenderPlans'
Export-Sheet -Data $activeAlerts -WorksheetName 'Active Alerts' -TableName 'ActiveAlerts'
Export-Sheet -Data $securityContacts -WorksheetName 'Security Contacts' -TableName 'SecurityContacts'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Security Audit Complete!" -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host "Subscriptions Audited: $($subscriptions.Count)"
Write-Host "Active Alerts: $($activeAlerts.Count)"
Write-Host "Total Findings: $($findings.Count)"
Write-Host "  High: $(($findings | Where-Object { $_.Severity -eq 'High' }).Count)"
Write-Host "  Medium: $(($findings | Where-Object { $_.Severity -eq 'Medium' }).Count)"
Write-Host "  Low: $(($findings | Where-Object { $_.Severity -eq 'Low' }).Count)"
Write-Host "Output: $outputFile"
Write-Host ""
