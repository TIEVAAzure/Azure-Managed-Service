param()

Import-Module Az.Accounts
Import-Module Az.Compute

# Auth with managed identity of the Automation Account
Connect-AzAccount -Identity | Out-Null

# -------- Simple script-level settings (not from variables) --------
# Safety window: how many minutes past ExpireOnUtc before we actually delete
[int]$SafetyMinutes       = 5        # adjust if you want a bigger buffer
[int]$MaxDeletesPerRun    = 0        # 0 = no limit
[bool]$DryRun             = $false   # set $true to log but not delete

# -------- Scope & time --------
# Scan all subscriptions the identity can see
$subs = Get-AzSubscription | Select-Object -ExpandProperty Id

$nowUtc    = [DateTimeOffset]::UtcNow
$cutoffUtc = $nowUtc.AddMinutes(-$SafetyMinutes)

$deleted = 0
$skipped = 0
$errors  = 0

foreach ($sub in $subs) {
    try {
        Select-AzSubscription -SubscriptionId $sub | Out-Null
    }
    catch {
        Write-Warning ("Cannot access subscription {0}: {1}" -f $sub, $_.Exception.Message)
        continue
    }

    # Get all snapshots in this subscription
    $snapshots = @()
    try {
        $snapshots = Get-AzSnapshot
    } catch {
        Write-Warning ("Get-AzSnapshot failed in sub {0}: {1}" -f $sub, $_.Exception.Message)
        continue
    }

    foreach ($s in $snapshots) {
        $t = $s.Tags
        if (-not $t) { continue }

        # Must be OUR snapshots (identified by tags)
        if (-not ($t.ContainsKey('Purpose')   -and $t['Purpose']   -eq 'PrePatch'))               { continue }
        if (-not ($t.ContainsKey('CreatedBy') -and $t['CreatedBy'] -eq 'UpdateManager-EventGrid')) { continue }
        if (-not  $t.ContainsKey('ExpireOnUtc')) { continue }

        # Parse ExpireOnUtc from tag
        $dto = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($t['ExpireOnUtc'], [ref]$dto)) {
            Write-Warning ("Could not parse ExpireOnUtc on snapshot {0}; skipping." -f $s.Name)
            $skipped++
            continue
        }
        $expUtc = $dto.ToUniversalTime()

        # Only delete if snapshot expired + outside safety window
        if ($expUtc -ge $cutoffUtc) {
            $skipped++
            continue
        }

        $msg = ('Deleting expired snapshot {0} in RG {1} (expired {2})' -f $s.Name, $s.ResourceGroupName, $t['ExpireOnUtc'])

        if ($DryRun) {
            Write-Host ('[DryRun] {0}' -f $msg)
            $skipped++
            continue
        }

        try {
            Remove-AzSnapshot -ResourceGroupName $s.ResourceGroupName -SnapshotName $s.Name -Force -ErrorAction Stop
            Write-Host $msg
            $deleted++

            if ($MaxDeletesPerRun -gt 0 -and $deleted -ge $MaxDeletesPerRun) {
                Write-Host ('Reached max deletions per run ({0}); stopping.' -f $MaxDeletesPerRun)
                break
            }
        } catch {
            Write-Warning ('Failed to delete {0}: {1}' -f $s.Name, $_.Exception.Message)
            $errors++
        }
    }

    if ($MaxDeletesPerRun -gt 0 -and $deleted -ge $MaxDeletesPerRun) { break }
}

Write-Host ('Cleanup summary: deleted={0}, skipped={1}, errors={2}, subsScanned={3}' -f $deleted, $skipped, $errors, $subs.Count)
# Uncomment to fail the run on errors:
# if ($errors -gt 0) { throw ("Cleanup finished with {0} error(s)." -f $errors) }
