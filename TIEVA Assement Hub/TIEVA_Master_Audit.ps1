<#
.SYNOPSIS
  TIEVA Master Audit Orchestrator
  
.DESCRIPTION
  Runs all TIEVA audit scripts from a single entry point with unified configuration.
  Supports guest user access to customer tenants via -TenantId parameter.
  
  Available audits:
  - Backup Posture (RSV + Backup Vaults, VM/DB coverage)
  - Cost Management (Budgets, alerts, tagging, spend history)
  - Identity & Access (RBAC, privileged access, service principals, PIM)
  - Network Topology (VNets, NSGs, peerings, public IPs)
  - Policy Compliance (Policy assignments, compliance state, exemptions)
  - Reservation Savings (Reserved instances, savings plans, utilization)
  - Resource Inventory (Resources, utilization, right-sizing, orphans)
  
.PARAMETER TenantId
  Target tenant ID for guest user access. If specified, will authenticate to this tenant.
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for all Excel files. Defaults to current user's Downloads folder.
  
.PARAMETER Audits
  Array of audit types to run. Valid values: All, Backup, Cost, Identity, Network, Policy, Reservation, Resource
  Default: All
  
.PARAMETER SkipAudits
  Array of audit types to skip. Useful when running 'All' but want to exclude specific audits.
  
.PARAMETER LookbackDays
  Number of days for utilization/reservation analysis. Default: 30
  
.PARAMETER UtilizationDays
  Number of days for VM utilization metrics. Default: 30
  
.EXAMPLE
  .\TIEVA_Master_Audit.ps1 -TenantId "customer-tenant-guid"
  Runs all audits against customer tenant
  
.EXAMPLE
  .\TIEVA_Master_Audit.ps1 -TenantId "customer-tenant-guid" -SubscriptionIds @("sub1","sub2")
  Runs all audits against specific subscriptions in customer tenant
  
.EXAMPLE
  .\TIEVA_Master_Audit.ps1 -Audits Backup,Cost,Identity
  Runs only Backup, Cost, and Identity audits
  
.EXAMPLE
  .\TIEVA_Master_Audit.ps1 -SkipAudits Reservation
  Runs all audits except Reservation
  
.NOTES
  Requires: Az PowerShell modules, ImportExcel module
  For guest access: User must be invited to target tenant with appropriate permissions
#>

[CmdletBinding()]
param(
  [Parameter(HelpMessage = "Target tenant ID for guest user access")]
  [string]$TenantId,
  
  [Parameter(HelpMessage = "Subscription IDs to audit (optional - defaults to all accessible)")]
  [string[]]$SubscriptionIds,
  
  [Parameter(HelpMessage = "Output directory for audit files")]
  [string]$OutPath = "$HOME\Downloads\TIEVA_Audit_$(Get-Date -Format 'yyyyMMdd_HHmmss')",
  
  [Parameter(HelpMessage = "Which audits to run")]
  [ValidateSet('All', 'Backup', 'Cost', 'Identity', 'Network', 'Policy', 'Reservation', 'Resource')]
  [string[]]$Audits = @('All'),
  
  [Parameter(HelpMessage = "Which audits to skip")]
  [ValidateSet('Backup', 'Cost', 'Identity', 'Network', 'Policy', 'Reservation', 'Resource')]
  [string[]]$SkipAudits = @(),
  
  [Parameter(HelpMessage = "Lookback days for reservation/spend analysis")]
  [int]$LookbackDays = 30,
  
  [Parameter(HelpMessage = "Days for VM utilization metrics")]
  [int]$UtilizationDays = 30,
  
  # Individual audit toggles (all ON by default as requested)
  [switch]$NoSpendHistory,
  [switch]$NoPIM,
  [switch]$NoNonCompliantDetails,
  [switch]$NoUtilization
)

$ErrorActionPreference = 'Continue'
$scriptStartTime = Get-Date

# ============================================================================
# BANNER
# ============================================================================

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║                                                                  ║" -ForegroundColor Cyan
Write-Host "║              TIEVA MASTER AUDIT ORCHESTRATOR                     ║" -ForegroundColor Cyan
Write-Host "║                                                                  ║" -ForegroundColor Cyan
Write-Host "║     Backup │ Cost │ Identity │ Network │ Policy │ Resource       ║" -ForegroundColor Cyan
Write-Host "║                                                                  ║" -ForegroundColor Cyan
Write-Host "╚══════════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
Write-Host ""

# ============================================================================
# DETERMINE SCRIPT LOCATION
# ============================================================================

$ScriptRoot = $PSScriptRoot
if (-not $ScriptRoot) {
  $ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $ScriptRoot) {
  $ScriptRoot = Get-Location
}

Write-Host "Script directory: $ScriptRoot" -ForegroundColor Gray

# ============================================================================
# AUTHENTICATION - GUEST USER SUPPORT
# ============================================================================

Write-Host "`n=== Authentication ===" -ForegroundColor Cyan

# Check current context
$currentContext = Get-AzContext -ErrorAction SilentlyContinue

if ($TenantId) {
  Write-Host "Target Tenant: $TenantId" -ForegroundColor Yellow
  
  # Check if we need to authenticate or switch tenant
  if (-not $currentContext) {
    Write-Host "No Azure context found. Authenticating..." -ForegroundColor Yellow
    try {
      Connect-AzAccount -TenantId $TenantId -ErrorAction Stop
      $currentContext = Get-AzContext
    } catch {
      Write-Error "Failed to authenticate to tenant $TenantId : $_"
      exit 1
    }
  } elseif ($currentContext.Tenant.Id -ne $TenantId) {
    Write-Host "Switching to tenant $TenantId..." -ForegroundColor Yellow
    try {
      # Try to switch context first (if already authenticated to this tenant)
      $existingContext = Get-AzContext -ListAvailable | Where-Object { $_.Tenant.Id -eq $TenantId } | Select-Object -First 1
      if ($existingContext) {
        Set-AzContext -Context $existingContext -ErrorAction Stop | Out-Null
      } else {
        # Need to authenticate fresh
        Connect-AzAccount -TenantId $TenantId -ErrorAction Stop
      }
      $currentContext = Get-AzContext
    } catch {
      Write-Error "Failed to switch to tenant $TenantId : $_"
      exit 1
    }
  } else {
    Write-Host "Already connected to target tenant" -ForegroundColor Green
  }
} else {
  if (-not $currentContext) {
    Write-Host "No Azure context found. Please authenticate..." -ForegroundColor Yellow
    try {
      Connect-AzAccount -ErrorAction Stop
      $currentContext = Get-AzContext
    } catch {
      Write-Error "Failed to authenticate: $_"
      exit 1
    }
  } else {
    Write-Host "Using existing Azure context" -ForegroundColor Green
  }
}

Write-Host "  Account:      $($currentContext.Account.Id)" -ForegroundColor White
Write-Host "  Tenant:       $($currentContext.Tenant.Id)" -ForegroundColor White
Write-Host "  Subscription: $($currentContext.Subscription.Name)" -ForegroundColor White

# ============================================================================
# SUBSCRIPTION DISCOVERY
# ============================================================================

Write-Host "`n=== Subscription Discovery ===" -ForegroundColor Cyan

# Get the current tenant ID to limit subscription queries
$currentTenantId = $currentContext.Tenant.Id

if ($SubscriptionIds -and $SubscriptionIds.Count -gt 0) {
  Write-Host "Using specified subscriptions: $($SubscriptionIds.Count)" -ForegroundColor Yellow
  $targetSubs = @()
  foreach ($subId in $SubscriptionIds) {
    try {
      # Limit query to current tenant to avoid cross-tenant auth warnings
      $sub = Get-AzSubscription -SubscriptionId $subId -TenantId $currentTenantId -ErrorAction Stop
      $targetSubs += $sub
      Write-Host "  [OK] $($sub.Name)" -ForegroundColor Green
    } catch {
      Write-Warning "  ✗ Could not access subscription $subId"
    }
  }
} else {
  Write-Host "Discovering all accessible subscriptions in tenant..." -ForegroundColor Yellow
  # Limit to current tenant only to avoid cross-tenant auth warnings
  $targetSubs = Get-AzSubscription -TenantId $currentTenantId | Where-Object { $_.State -eq 'Enabled' }
  Write-Host "  Found $($targetSubs.Count) enabled subscription(s)" -ForegroundColor Green
}

if ($targetSubs.Count -eq 0) {
  Write-Error "No accessible subscriptions found. Exiting."
  exit 1
}

# Store subscription IDs for passing to scripts
$subIdArray = $targetSubs | Select-Object -ExpandProperty Id

# ============================================================================
# OUTPUT DIRECTORY
# ============================================================================

Write-Host "`n=== Output Configuration ===" -ForegroundColor Cyan

if (-not (Test-Path $OutPath)) {
  New-Item -ItemType Directory -Path $OutPath -Force | Out-Null
  Write-Host "Created output directory: $OutPath" -ForegroundColor Green
} else {
  Write-Host "Output directory: $OutPath" -ForegroundColor Green
}

# ============================================================================
# DETERMINE WHICH AUDITS TO RUN
# ============================================================================

$allAuditTypes = @('Backup', 'Cost', 'Identity', 'Network', 'Policy', 'Reservation', 'Resource')

if ($Audits -contains 'All') {
  $auditsToRun = $allAuditTypes
} else {
  $auditsToRun = $Audits
}

# Remove skipped audits
$auditsToRun = $auditsToRun | Where-Object { $_ -notin $SkipAudits }

Write-Host "`n=== Audits to Execute ===" -ForegroundColor Cyan
foreach ($audit in $allAuditTypes) {
  if ($audit -in $auditsToRun) {
    Write-Host "  [[OK]] $audit" -ForegroundColor Green
  } else {
    Write-Host "  [ ] $audit (skipped)" -ForegroundColor Gray
  }
}

# ============================================================================
# AUDIT EXECUTION
# ============================================================================

$auditResults = @{}
$auditScripts = @{
  'Backup'      = @{ Script = 'BackupAudit.ps1';        Output = 'Backup_Audit.xlsx' }
  'Cost'        = @{ Script = 'CostManagementAudit.ps1'; Output = 'Cost_Management_Audit.xlsx' }
  'Identity'    = @{ Script = 'IdentityAudit.ps1';      Output = 'Identity_Audit.xlsx' }
  'Network'     = @{ Script = 'NetworkAudit.ps1';       Output = 'Network_Audit.xlsx' }
  'Policy'      = @{ Script = 'PolicyAudit.ps1';        Output = 'Policy_Audit.xlsx' }
  'Reservation' = @{ Script = 'ReservationAudit.ps1';   Output = 'Reservation_Audit.xlsx' }
  'Resource'    = @{ Script = 'ResourceAudit.ps1';      Output = 'Resource_Audit.xlsx' }
}

Write-Host "`n" -NoNewline
Write-Host "═══════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "                      EXECUTING AUDITS                              " -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════════════" -ForegroundColor Cyan

$auditIndex = 0
$totalAudits = $auditsToRun.Count

foreach ($auditType in $auditsToRun) {
  $auditIndex++
  $auditInfo = $auditScripts[$auditType]
  $scriptPath = Join-Path $ScriptRoot $auditInfo.Script
  
  Write-Host "`n[$auditIndex/$totalAudits] " -NoNewline -ForegroundColor White
  Write-Host "$auditType Audit" -ForegroundColor Yellow
  Write-Host ("─" * 50) -ForegroundColor Gray
  
  if (-not (Test-Path $scriptPath)) {
    Write-Warning "  Script not found: $scriptPath"
    $auditResults[$auditType] = @{ Status = 'NotFound'; Duration = 0 }
    continue
  }
  
  $auditStartTime = Get-Date
  
  try {
    # Build parameters based on audit type
    $params = @{
      SubscriptionIds = $subIdArray
      OutPath = $OutPath
    }
    
    switch ($auditType) {
      'Backup' {
        # BackupAudit now uses standard SubscriptionIds parameter
      }
      'Cost' {
        if (-not $NoSpendHistory) {
          $params['IncludeSpendHistory'] = $true
        }
      }
      'Identity' {
        if (-not $NoPIM) {
          $params['IncludePIM'] = $true
        }
      }
      'Network' {
        # No additional params
      }
      'Policy' {
        if (-not $NoNonCompliantDetails) {
          $params['IncludeNonCompliantDetails'] = $true
        }
      }
      'Reservation' {
        $params['LookbackDays'] = $LookbackDays
      }
      'Resource' {
        if (-not $NoUtilization) {
          $params['IncludeUtilization'] = $true
        }
        $params['UtilizationDays'] = $UtilizationDays
      }
    }
    
    # Execute the script
    & $scriptPath @params
    
    $auditEndTime = Get-Date
    $duration = ($auditEndTime - $auditStartTime).TotalMinutes
    
    # Check if output file was created
    $outputFile = Join-Path $OutPath $auditInfo.Output
    if (Test-Path $outputFile) {
      $auditResults[$auditType] = @{ 
        Status = 'Success'
        Duration = [math]::Round($duration, 2)
        OutputFile = $outputFile
      }
      Write-Host "  [OK] Completed in $([math]::Round($duration, 2)) minutes" -ForegroundColor Green
    } else {
      $auditResults[$auditType] = @{ 
        Status = 'NoOutput'
        Duration = [math]::Round($duration, 2)
      }
      Write-Host "  ⚠ Completed but no output file found" -ForegroundColor Yellow
    }
    
  } catch {
    $auditEndTime = Get-Date
    $duration = ($auditEndTime - $auditStartTime).TotalMinutes
    $auditResults[$auditType] = @{ 
      Status = 'Failed'
      Duration = [math]::Round($duration, 2)
      Error = $_.Exception.Message
    }
    Write-Host "  ✗ Failed: $($_.Exception.Message)" -ForegroundColor Red
  }
}

# ============================================================================
# SUMMARY
# ============================================================================

$scriptEndTime = Get-Date
$totalDuration = ($scriptEndTime - $scriptStartTime).TotalMinutes

Write-Host "`n"
Write-Host "═══════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "                        AUDIT SUMMARY                               " -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════════════" -ForegroundColor Cyan

Write-Host "`nExecution Results:" -ForegroundColor White

$successCount = 0
$failedCount = 0

foreach ($auditType in $auditsToRun) {
  $result = $auditResults[$auditType]
  $statusIcon = switch ($result.Status) {
    'Success'  { '[OK]'; $successCount++ }
    'Failed'   { '✗'; $failedCount++ }
    'NoOutput' { '⚠'; $successCount++ }
    'NotFound' { '?'; $failedCount++ }
    default    { '?' }
  }
  $statusColor = switch ($result.Status) {
    'Success'  { 'Green' }
    'Failed'   { 'Red' }
    'NoOutput' { 'Yellow' }
    'NotFound' { 'Yellow' }
    default    { 'Gray' }
  }
  
  Write-Host "  [$statusIcon] " -NoNewline -ForegroundColor $statusColor
  Write-Host "$auditType".PadRight(15) -NoNewline
  Write-Host "$($result.Duration) min" -ForegroundColor Gray
}

Write-Host "`n─────────────────────────────────────────" -ForegroundColor Gray
Write-Host "Total Duration: " -NoNewline
Write-Host "$([math]::Round($totalDuration, 2)) minutes" -ForegroundColor Cyan
Write-Host "Successful:     " -NoNewline
Write-Host "$successCount" -ForegroundColor Green
Write-Host "Failed:         " -NoNewline
Write-Host "$failedCount" -ForegroundColor $(if ($failedCount -gt 0) { 'Red' } else { 'Green' })

Write-Host "`nOutput Files:" -ForegroundColor White
foreach ($auditType in $auditsToRun) {
  $result = $auditResults[$auditType]
  if ($result.OutputFile -and (Test-Path $result.OutputFile)) {
    Write-Host "  • $($auditScripts[$auditType].Output)" -ForegroundColor Green
  }
}

Write-Host "`nOutput Directory:" -ForegroundColor White
Write-Host "  $OutPath" -ForegroundColor Cyan

Write-Host "`n═══════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  TIEVA Master Audit Complete - $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# ============================================================================
# OPEN OUTPUT FOLDER (optional)
# ============================================================================

if ($IsWindows -or $env:OS -match 'Windows') {
  $openFolder = Read-Host "Open output folder? (Y/n)"
  if ($openFolder -ne 'n' -and $openFolder -ne 'N') {
    Start-Process explorer.exe -ArgumentList $OutPath
  }
}
