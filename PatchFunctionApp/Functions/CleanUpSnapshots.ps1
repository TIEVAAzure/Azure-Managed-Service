param($Timer)

Import-Module Az.Accounts
Import-Module Az.Compute

# Auth with managed identity
Connect-AzAccount -Identity | Out-Null

# -------- App settings (read safely; no $env:) --------
$subsCsv    = [Environment]::GetEnvironmentVariable('Cleanup_Subscriptions')
$rgsCsv     = [Environment]::GetEnvironmentVariable('Cleanup_ResourceGroups')
$safetyStr  = [Environment]::GetEnvironmentVariable('Cleanup_SafetyMinutes')
$maxDelStr  = [Environment]::GetEnvironmentVariable('Cleanup_MaxDeletesPerRun')
$dryRunStr  = [Environment]::GetEnvironmentVariable('Cleanup_DryRun')

if ([string]::IsNullOrWhiteSpace($safetyStr)) { $safetyStr = '5' }
if ([string]::IsNullOrWhiteSpace($maxDelStr)) { $maxDelStr = '0' }
if ([string]::IsNullOrWhiteSpace($dryRunStr)) { $dryRunStr = 'false' }

[int]$safetyMin = $safetyStr
[int]$maxDel    = $maxDelStr
$dryRun = $false
[bool]::TryParse($dryRunStr, [ref]$dryRun) | Out-Null

# -------- Scope & time --------
$subs = if ([string]::IsNullOrWhiteSpace($subsCsv)) { @((Get-AzContext).Subscription.Id) } else { ($subsCsv -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ } }
$rgs  = if ([string]::IsNullOrWhiteSpace($rgsCsv))  { @() } else { ($rgsCsv  -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ } }

$nowUtc    = [DateTimeOffset]::UtcNow
$cutoffUtc = $nowUtc.AddMinutes(-$safetyMin)

$deleted = 0
$skipped = 0
$errors  = 0

foreach ($sub in $subs) {
    try { Select-AzSubscription -SubscriptionId $sub | Out-Null }
    catch { Write-Warning ("Cannot access subscription {0}: {1}" -f $sub, $_.Exception.Message); continue }

    # Gather snapshots in-scope
    $snapshots = @()
    if ($rgs.Count -gt 0) {
        foreach ($rg in $rgs) {
            try { $snapshots += Get-AzSnapshot -ResourceGroupName $rg -ErrorAction SilentlyContinue } catch {}
        }
    } else {
        try { $snapshots = Get-AzSnapshot } catch { Write-Warning ("Get-AzSnapshot failed in sub {0}: {1}" -f $sub, $_.Exception.Message); continue }
    }

    foreach ($s in $snapshots) {
        $t = $s.Tags
        if (-not $t) { continue }

        # Must be OUR snapshots (safe filter)
        if (-not ($t.ContainsKey('Purpose')   -and $t['Purpose']   -eq 'PrePatch'))               { continue }
        if (-not ($t.ContainsKey('CreatedBy') -and $t['CreatedBy'] -eq 'UpdateManager-EventGrid')) { continue } # remove this line to catch older snapshots too
        if (-not  $t.ContainsKey('ExpireOnUtc')) { continue }

        # Parse expiry as UTC
        $dto = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParse($t['ExpireOnUtc'], [ref]$dto)) {
            Write-Warning ("Could not parse ExpireOnUtc on snapshot {0}; skipping." -f $s.Name)
            $skipped++; continue
        }
        $expUtc = $dto.ToUniversalTime()
        if ($expUtc -ge $cutoffUtc) { $skipped++; continue }  # not yet expired (or within safety window)

        $msg = ('Deleting expired snapshot {0} in RG {1} (expired {2})' -f $s.Name, $s.ResourceGroupName, $t['ExpireOnUtc'])

        if ($dryRun) {
            Write-Host ('[DryRun] {0}' -f $msg)
            $skipped++; continue
        }

        try {
            Remove-AzSnapshot -ResourceGroupName $s.ResourceGroupName -SnapshotName $s.Name -Force -ErrorAction Stop
            Write-Host $msg
            $deleted++
            if ($maxDel -gt 0 -and $deleted -ge $maxDel) {
                Write-Host ('Reached max deletions per run ({0}); stopping.' -f $maxDel)
                break
            }
        } catch {
            Write-Warning ('Failed to delete {0}: {1}' -f $s.Name, $_.Exception.Message)
            $errors++
        }
    }

    if ($maxDel -gt 0 -and $deleted -ge $maxDel) { break }
}

Write-Host ('Cleanup summary: deleted={0}, skipped={1}, errors={2}, subsScanned={3}{4}' -f $deleted, $skipped, $errors, $subs.Count, ($rgs.Count -gt 0 ? (', rgsFiltered=' + ($rgs -join ',')) : ''))
# Uncomment to fail the run on errors:
# if ($errors -gt 0) { throw ("Cleanup finished with {0} error(s)." -f $errors) }
