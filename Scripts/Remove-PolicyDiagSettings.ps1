# ==============================================
# Remove Policy-Set Diagnostic Settings (Per-Subscription Summary)
# Cloud Shell compatible (Az module preloaded)
# ==============================================

# ---- Inputs: edit these ----
$TargetSubscriptions = @(
  '5961d092-dde6-40e2-90e5-2b6dab00b739',
  'c9400257-0d1b-4a59-b227-36b3aea73b0a',
  '5eac3012-879c-4bd6-81a7-e5cc0a531901',
  '961271eb-fe4d-455e-9f53-5b0a5a931bcb',
  '03127f90-4f1f-4f83-b9a7-a23b39ecc91e',
  '00f0e475-b85a-4f4e-a4df-84a44d547d95',
  '6ee22078-9979-48d2-996d-2c14734618dc',
  '1e08993c-9844-43ed-bdbc-ef2587524458',
  '68d0c954-0825-43ce-ac75-a906c966bd25',
  'fcaca3a0-0f22-4db3-9f41-248ba1e9be20'
)

# Diagnostic setting deployed by policy
$DiagSettingName = 'setByPolicy-EventHub'

# (Optional) Only match settings wired to this EH auth rule and EH name
$Match_EventHubAuthorizationRuleId = '/subscriptions/fcaca3a0-0f22-4db3-9f41-248ba1e9be20/resourcegroups/lm-logs-tieva-uksouth-group/providers/microsoft.eventhub/namespaces/lmlogsovarrouksouth1/authorizationrules/rootmanagesharedaccesskey'
$Match_EventHubName                = 'Monitoring'

# (Optional) Limit to resource types; leave empty for all types
# Example: @('Microsoft.KeyVault/vaults','Microsoft.Storage/storageAccounts')
$IncludeResourceTypes = @()

# Dry run: set to $false to actually remove
$WhatIf = $true

# Export CSVs (one per subscription) for audit
$ExportCsv       = $true
$ExportBasePath  = "$HOME/diag-removal-reports-$(Get-Date -Format 'yyyyMMdd_HHmmss')"
# ----------------------------

# Silence breaking-change messages
Update-AzConfig -DisplayBreakingChangeWarning:$false | Out-Null
Set-Item Env:SuppressAzurePowerShellBreakingChangeWarnings 'true'

# Ensure signed in (Cloud Shell usually is)
try { $null = Get-AzContext -ErrorAction Stop } catch { Connect-AzAccount | Out-Null }

# Helpers
function As-List($value) {
    if ($null -eq $value) { @() }
    elseif ($value -is [System.Collections.IEnumerable] -and -not ($value -is [string])) { @($value) }
    else { @($value) }
}

# Storage for reporting
$AllRemovals  = @()
$Summaries    = @()

if ($ExportCsv) { New-Item -ItemType Directory -Path $ExportBasePath -Force | Out-Null }

foreach ($subId in $TargetSubscriptions) {
    Write-Host "`n===== Subscription: $subId =====" -ForegroundColor Cyan
    Set-AzContext -SubscriptionId $subId -ErrorAction Stop | Out-Null

    $subInfo     = Get-AzSubscription -SubscriptionId $subId -ErrorAction SilentlyContinue
    $subName     = if ($subInfo) { $subInfo.Name } else { $subId }

    $checked     = 0
    $matched     = 0
    $removed     = 0
    $subRemovals = @()

    # Enumerate resources (can be large)
    $resources = Get-AzResource -ErrorAction Stop
    if ($IncludeResourceTypes.Count -gt 0) {
        $resources = $resources | Where-Object { $IncludeResourceTypes -contains $_.ResourceType }
    }

    foreach ($res in $resources) {
        $checked++
        try {
            $ds = Get-AzDiagnosticSetting -ResourceId $res.Id -ErrorAction Stop
        } catch {
            # Resource type likely doesn't support diag settings; continue
            continue
        }

        foreach ($d in @($ds)) {
            $nameMatch   = ($d.Name -eq $DiagSettingName)
            $authMatch   = ([string]::IsNullOrEmpty($Match_EventHubAuthorizationRuleId) -or ($d.EventHubAuthorizationRuleId -eq $Match_EventHubAuthorizationRuleId))
            $ehNameMatch = ([string]::IsNullOrEmpty($Match_EventHubName) -or ($d.EventHubName -eq $Match_EventHubName))

            if ($nameMatch -and $authMatch -and $ehNameMatch) {
                $matched++
                Write-Host ("  {0} :: {1} / {2} (RG: {3})  ->  {4}" -f `
                    $res.ResourceType,$res.Name,($res.Location ?? ''),$res.ResourceGroupName,$d.Name) -ForegroundColor Yellow

                $action = 'WouldRemove'
                if (-not $WhatIf) {
                    try {
                        Remove-AzDiagnosticSetting -ResourceId $res.Id -Name $d.Name -ErrorAction Stop
                        $action = 'Removed'
                        $removed++
                    } catch {
                        $action = "Failed: $($_.Exception.Message)"
                        Write-Warning "    Failed to remove on $($res.Id): $($_.Exception.Message)"
                    }
                }

                $subRemovals += [PSCustomObject]@{
                    Timestamp                   = (Get-Date).ToString('s')
                    SubscriptionName            = $subName
                    SubscriptionId              = $subId
                    ResourceGroup               = $res.ResourceGroupName
                    ResourceType                = $res.ResourceType
                    ResourceName                = $res.Name
                    Location                    = $res.Location
                    ResourceId                  = $res.Id
                    DiagnosticSettingName       = $d.Name
                    EventHubAuthorizationRuleId = $d.EventHubAuthorizationRuleId
                    EventHubName                = $d.EventHubName
                    Action                      = $action
                }
            }
        }
    }

    # Save per-subscription details
    $AllRemovals += $subRemovals

    # Summary row
    $Summaries += [PSCustomObject]@{
        SubscriptionName = $subName
        SubscriptionId   = $subId
        Checked          = $checked
        Matched          = $matched
        Removed          = if ($WhatIf) { 0 } else { $removed }
        Mode_WhatIf      = $WhatIf
    }

    # Output per-subscription summary + details
    Write-Host ("Summary for {0} ({1}): Checked={2}, Matched={3}, {4}={5}" -f `
        $subName,$subId,$checked,$matched, ($WhatIf ? 'WouldRemove' : 'Removed'), ($WhatIf ? $matched : $removed)) -ForegroundColor Green

    if ($subRemovals.Count -gt 0) {
        $subRemovals | Sort-Object ResourceType, ResourceGroup, ResourceName |
            Select-Object SubscriptionName, ResourceGroup, ResourceType, ResourceName, Location, DiagnosticSettingName, EventHubName, Action |
            Format-Table -AutoSize

        if ($ExportCsv) {
            $csvPath = Join-Path $ExportBasePath ("{0}_{1}.csv" -f ($subName -replace '[^\w\-]','_'), $subId)
            $subRemovals | Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
            Write-Host "  -> Exported CSV: $csvPath" -ForegroundColor DarkCyan
        }
    } else {
        Write-Host "  No matching diagnostic settings found in this subscription." -ForegroundColor Magenta
    }
}

# Grand totals
Write-Host "`n===== Overall Summary =====" -ForegroundColor Cyan
$Summaries | Sort-Object SubscriptionName | Format-Table -AutoSize

if ($ExportCsv) {
    $allCsv = Join-Path $ExportBasePath "AllSubscriptions_Detail.csv"
    $AllRemovals | Export-Csv -Path $allCsv -NoTypeInformation -Encoding UTF8
    Write-Host "`nAll details exported to: $allCsv" -ForegroundColor DarkCyan
}

Write-Host @"
NOTES:
- This script removes diagnostic settings named '$DiagSettingName' that (optionally) point to:
    EventHubAuthorizationRuleId = '$Match_EventHubAuthorizationRuleId'
    EventHubName                = '$Match_EventHubName'
- To broaden/narrow scope:
    * Clear those match variables to remove any '$DiagSettingName' regardless of Event Hub.
    * Populate `$IncludeResourceTypes` to limit resource kinds.
- This is a dry run by default (WhatIf=$WhatIf). Set `$WhatIf = $false` to actually remove.
- If a DeployIfNotExists policy is still assigned, it may redeploy the settings. Consider exclusions or disabling the assignment.
"@
