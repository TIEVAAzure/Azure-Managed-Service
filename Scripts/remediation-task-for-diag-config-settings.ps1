# ==================== Config ====================
# One or more subscription IDs to process
$SubscriptionIds = @(
  "5961d092-dde6-40e2-90e5-2b6dab00b739"
)

# If the Policy Assignment display name is identical across subs, set it here:
$DisplayNameExact = "Enable allLogs category group resource logging for supported resources to Event Hub"

# If display names differ per sub (e.g., contain the sub name), set a LIKE pattern instead
# and set $DisplayNameExact = $null. Example:
# $DisplayNameLike = "Enable allLogs category group resource logging for supported resources to Event Hub*UK South"
$DisplayNameLike  = $null

# Location for DINE deployments (e.g., LAW region)
$Location = "uksouth"

# Optional: auto-approve remediation creation (skip prompts)
$AutoApprove = $false   # set to $true to create remediations without prompting


# ==================== Function ====================
function Invoke-DineRemediationForSubscription {
  param(
    [Parameter(Mandatory=$true)][string]$SubscriptionId,
    [string]$DisplayNameExact,
    [string]$DisplayNameLike,
    [Parameter(Mandatory=$true)][string]$Location,
    [Parameter(Mandatory=$true)][bool]$AutoApprove
  )

  Write-Host ""
  Write-Host "===== Subscription: $SubscriptionId =====" -ForegroundColor Cyan

  az account set --subscription $SubscriptionId | Out-Null

  # ----- Resolve assignment NAME (initiative assignment) -----
  $AssignmentName = $null
  $ResolvedDisplayName = $null

  if ($DisplayNameExact) {
    $AssignmentName = az policy assignment list `
      --scope "/subscriptions/$SubscriptionId" `
      --query "[?displayName=='$DisplayNameExact']|[0].name" -o tsv
    $ResolvedDisplayName = $DisplayNameExact
  } elseif ($DisplayNameLike) {
    $assignments = az policy assignment list `
      --scope "/subscriptions/$SubscriptionId" -o json | ConvertFrom-Json
    $candidate = $assignments |
      Where-Object { $_.displayName -like $DisplayNameLike } |
      Select-Object -First 1
    if ($candidate) {
      $AssignmentName = $candidate.name
      $ResolvedDisplayName = $candidate.displayName
    }
  }

  if (-not $AssignmentName) {
    Write-Host "Assignment not found at subscription scope." -ForegroundColor Red
    az policy assignment list --scope "/subscriptions/$SubscriptionId" `
      --query "[].{DisplayName:displayName,Name:name,Id:id}" -o table
    return [pscustomobject]@{
      SubscriptionId        = $SubscriptionId
      AssignmentDisplayName = ($DisplayNameExact ?? $DisplayNameLike ?? "(not found)")
      AssignmentName        = $null
      NonCompliantCount     = 0
      RemediationsStarted   = 0
      Status                = "No assignment"
    }
  }

  Write-Host "Using assignment name: $AssignmentName" -ForegroundColor Green

  # ----- OPTIONAL: trigger a fresh scan (fire-and-forget) -----
  az policy state trigger-scan --subscription $SubscriptionId --no-wait | Out-Null

  # ----- Pull states for THIS assignment, then keep only DINE + NonCompliant -----
  $states = az policy state list `
    --subscription $SubscriptionId `
    --policy-assignment $AssignmentName `
    --all -o json | ConvertFrom-Json

  $nonCompliantDINE =
    $states | Where-Object {
      $_.complianceState -eq 'NonCompliant' -and
      ( $_.policyDefinitionAction -eq 'deployIfNotExists' -or $_.policyDefinitionEffect -eq 'deployIfNotExists' )
    }

  if (-not $nonCompliantDINE -or $nonCompliantDINE.Count -eq 0) {
    Write-Host "No NON-COMPLIANT DINE rows listed. You can still remediate per policy ref; the service re-evaluates during run." -ForegroundColor Yellow
    $ncCount = az policy state summarize `
      --subscription $SubscriptionId `
      --policy-assignment $AssignmentName `
      --query "results.nonCompliantResources" -o tsv
    if ($ncCount) { Write-Host "📊 Summarize reports NonCompliant resources: $ncCount" -ForegroundColor Yellow }
  }

  # ----- Group by policyDefinitionReferenceId (needed for remediations) -----
  $refs = $nonCompliantDINE |
    Group-Object policyDefinitionReferenceId |
    ForEach-Object {
      [pscustomobject]@{
        DefRefId = $_.Name
        Count    = $_.Count
        Policy   = ($_.Group | Select-Object -First 1).policyDefinitionName
      }
    } | Sort-Object Policy

  if ($refs -and $refs.Count -gt 0) {
    Write-Host "`n==> Non-compliant DINE policies inside initiative:" -ForegroundColor Cyan
    $refs | Format-Table -AutoSize
  } else {
    Write-Host "`n(No grouped DINE refs to show.)" -ForegroundColor DarkYellow
  }

  # ----- Create remediations -----
  $started = 0
  $shouldProceed = $AutoApprove
  if (-not $AutoApprove) {
    $ans = Read-Host "`nCreate a remediation per policy (definitionReferenceId) found above? (y/N)"
    $shouldProceed = ($ans -eq 'y')
  }

  if ($shouldProceed -and $refs -and $refs.Count -gt 0) {
    foreach ($r in $refs) {
      $name = "remediate-$($r.DefRefId)-$([DateTimeOffset]::Now.ToUnixTimeSeconds())"
      Write-Host "→ Starting remediation for $($r.DefRefId) ($($r.Policy)) as ${name}" -ForegroundColor Green
      az policy remediation create `
        --name $name `
        --policy-assignment $AssignmentName `
        --definition-reference-id $($r.DefRefId) `
        --resource-discovery-mode ReEvaluateCompliance `
        --location $Location | Out-Null
      $started++
    }
    Write-Host "Remediations submitted." -ForegroundColor Green
  } elseif (-not $shouldProceed) {
    $one = Read-Host "Enter a single definitionReferenceId to remediate (blank to skip)"
    if ($one) {
      $name = "remediate-$one-$([DateTimeOffset]::Now.ToUnixTimeSeconds())"
      Write-Host "→ Starting remediation for ${one} as ${name}" -ForegroundColor Green
      az policy remediation create `
        --name $name `
        --policy-assignment $AssignmentName `
        --definition-reference-id $one `
        --resource-discovery-mode ReEvaluateCompliance `
        --location $Location | Out-Null
      Write-Host "Remediation submitted." -ForegroundColor Green
      $started++
    } else {
      Write-Host "Skipped." -ForegroundColor DarkYellow
    }
  }

  return [pscustomobject]@{
    SubscriptionId        = $SubscriptionId
    AssignmentDisplayName = $ResolvedDisplayName
    AssignmentName        = $AssignmentName
    NonCompliantCount     = ($nonCompliantDINE | Measure-Object).Count
    RemediationsStarted   = $started
    Status                = "OK"
  }
}


# ==================== Run across all subscriptions ====================
$summary = @()

foreach ($sub in $SubscriptionIds) {
  try {
    $summary += Invoke-DineRemediationForSubscription `
      -SubscriptionId $sub `
      -DisplayNameExact $DisplayNameExact `
      -DisplayNameLike $DisplayNameLike `
      -Location $Location `
      -AutoApprove $AutoApprove
  } catch {
    Write-Host ("Error in subscription {0}: {1}" -f $sub, $_.Exception.Message) -ForegroundColor Red
    $summary += [pscustomobject]@{
      SubscriptionId        = $sub
      AssignmentDisplayName = ($DisplayNameExact ?? $DisplayNameLike)
      AssignmentName        = $null
      NonCompliantCount     = $null
      RemediationsStarted   = 0
      Status                = "Error"
    }
  }
}

# ==================== Summary ====================
# Clean in case anything leaked into $summary earlier

$summaryClean = $summary | Where-Object { $_.PSObject.Properties['SubscriptionId'] }
 
$summaryClean | Sort-Object SubscriptionId | Select-Object SubscriptionId, AssignmentDisplayName, AssignmentName, NonCompliantCount, RemediationsStarted, Status
 
 
