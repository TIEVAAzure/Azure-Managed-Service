param($eventGridEvent, $TriggerMetadata)

Import-Module Az.Accounts
Import-Module Az.Compute
Import-Module Az.Resources

Write-Host "PreSnapshot VERSION 2025-08-29-2204 (multi-sub, idempotent)"

# Log in with the Function App's managed identity
Connect-AzAccount -Identity | Out-Null

# ---------- Read Maintenance Configuration and tags ----------
$data = $eventGridEvent.data
$mcId = $data.MaintenanceConfigurationId
if ([string]::IsNullOrWhiteSpace($mcId)) {
    Write-Warning "No MaintenanceConfigurationId in event; aborting."
    return
}

# MC lives in a specific subscription; select it so we can read its tags
$mcParts = $mcId -split '/'
$mcSubId = $mcParts[2]

try {
    Select-AzSubscription -SubscriptionId $mcSubId -ErrorAction Stop | Out-Null
    Write-Host ("[MC] Using Maintenance Configuration subscription {0}" -f $mcSubId)
}
catch {
    Write-Warning ("[MC] Could not select MC subscription {0}: {1}" -f $mcSubId, $_.Exception.Message)
    # we still try to get the MC; if that fails the script will stop below
}

$mc   = Get-AzResource -ResourceId $mcId -ErrorAction Stop
$tags = $mc.Tags
if (-not $tags) { $tags = @{} }

# Selection keys from MC tags (match VM tags)
$groupVal    = $tags.ContainsKey('Group')    ? $tags['Group']    : $null
$scheduleVal = $tags.ContainsKey('Schedule') ? $tags['Schedule'] : $null

Write-Host "[MC] Tags:"
Write-Host ("  Group        = '{0}'"    -f $groupVal)
Write-Host ("  Schedule     = '{0}'"    -f $scheduleVal)

if ([string]::IsNullOrWhiteSpace($groupVal) -and [string]::IsNullOrWhiteSpace($scheduleVal)) {
    Write-Warning "MC has no Group/Schedule tags; nothing to select."
    return
}

# Optional controls
$retain = 24
if ($tags.ContainsKey('Snapshot.RetainHours')) {
    $tmp = 0
    if ([int]::TryParse($tags['Snapshot.RetainHours'], [ref]$tmp)) {
        $retain = $tmp
    }
}
$limit  = 200
if ($tags.ContainsKey('Snapshot.Max')) {
    $tmp2 = 0
    if ([int]::TryParse($tags['Snapshot.Max'], [ref]$tmp2)) {
        $limit = $tmp2
    }
}
$rgList = @()
if ($tags.ContainsKey('Snapshot.RGs')) {
    $rgList = ($tags['Snapshot.RGs'] -split ',') | ForEach-Object { $_.Trim() } | Where-Object { $_ }
}

Write-Host ("[MC] Snapshot.RetainHours = {0}" -f $retain)
Write-Host ("[MC] Snapshot.Max         = {0}" -f $limit)
Write-Host ("[MC] Snapshot.RGs         = {0}" -f ($rgList -join ', '))

# ---------- Helpers ----------
function Parse-DiskArmId {
    param([Parameter(Mandatory)][string]$Id)
    $p = $Id -split '/'
    # /subscriptions/<2>/resourceGroups/<4>/.../disks/<last>
    [pscustomobject]@{
        SubscriptionId = $p[2]
        ResourceGroup  = $p[4]
        Name           = $p[-1]
    }
}

function New-DeterministicName {
    param(
        [string]$VmName,
        [string]$DiskKind,
        [string]$DiskName,
        [string]$EventId
    )

    $suffix = ($EventId -replace '[^a-zA-Z0-9]', '')
    if ($suffix.Length -gt 12) { $suffix = $suffix.Substring(0, 12) }

    $name = ("{0}-{1}-{2}-{3}" -f $VmName, $DiskKind.ToLower(), $DiskName, $suffix)
    if ($name.Length -gt 80) { $name = $name.Substring(0, 80) }
    return $name
}

function Ensure-Tags {
    param(
        [Parameter(Mandatory)][string]$ResourceId,
        [Parameter(Mandatory)][hashtable]$Tags
    )
    try {
        Update-AzTag -ResourceId $ResourceId -Tag $Tags -Operation Merge | Out-Null
    }
    catch {
        try {
            Set-AzResource -ResourceId $ResourceId -Tag $Tags -Force | Out-Null
        }
        catch {
            Write-Warning ("Ensure-Tags failed for {0}: {1}" -f $ResourceId, $_.Exception.Message)
        }
    }
}

function New-DiskSnapshot {
    param(
        [Parameter(Mandatory)][string]$DiskResourceId,
        [Parameter(Mandatory)][string]$SnapshotRG,      # kept for compatibility, but we always use disk's RG
        [Parameter(Mandatory)][string]$VmName,
        [Parameter(Mandatory)][string]$DiskName,
        [Parameter(Mandatory)][string]$DiskKind,        # 'OS' or 'Data'
        [Parameter(Mandatory)][int]$RetainHours,
        [Parameter(Mandatory)][string]$MaintenanceConfigId,
        [Parameter(Mandatory)][string]$EventId,
        [Parameter()][string]$GroupValue,
        [Parameter()][string]$ScheduleValue
    )

    # Parse disk ARM ID -> get subscription + RG + name
    $meta = Parse-DiskArmId -Id $DiskResourceId

    # Always work in the DISK's subscription
    try {
        Select-AzSubscription -SubscriptionId $meta.SubscriptionId -ErrorAction Stop | Out-Null
        $ctx = Get-AzContext
        Write-Host ("[New-DiskSnapshot] DiskSub={0}, DiskRG={1}, CurrentSub={2}" -f $meta.SubscriptionId, $meta.ResourceGroup, $ctx.Subscription.Id)
    }
    catch {
        Write-Warning ("[New-DiskSnapshot] Cannot select subscription {0} for disk {1}: {2}" -f $meta.SubscriptionId, $DiskResourceId, $_.Exception.Message)
        return [pscustomobject]@{ Status = 'Failed' }
    }

    # Always use the DISK's RG for the snapshot
    $snapshotRgName = $meta.ResourceGroup

    try {
        $disk = Get-AzDisk -ResourceGroupName $meta.ResourceGroup -DiskName $meta.Name -ErrorAction Stop
    }
    catch {
        Write-Warning ("Get-AzDisk failed for {0}: {1}" -f $DiskResourceId, $_.Exception.Message)
        return [pscustomobject]@{ Status = 'Failed' }
    }

    # Deterministic name (idempotent per event)
    $snapName = New-DeterministicName -VmName $VmName -DiskKind $DiskKind -DiskName $DiskName -EventId $EventId

    # Pre-check by name (idempotency)
    $existing = Get-AzSnapshot -ResourceGroupName $snapshotRgName -SnapshotName $snapName -ErrorAction SilentlyContinue
    if ($existing) {
        $expires = (Get-Date).ToUniversalTime().AddHours($RetainHours).ToString('o')
        $tagsToSet = @{
            Purpose  = 'PrePatch'
            VMName   = $VmName
            DiskName = $DiskName
            DiskKind = $DiskKind
            ExpireOnUtc = $expires
            CreatedBy   = 'UpdateManager-EventGrid'
            MaintenanceConfigurationId = $MaintenanceConfigId
            EventId     = $EventId
        }
        if ($GroupValue)    { $tagsToSet['Group']    = $GroupValue }
        if ($ScheduleValue) { $tagsToSet['Schedule'] = $ScheduleValue }

        Ensure-Tags -ResourceId $existing.Id -Tags $tagsToSet
        Write-Host ("Duplicate detected (by name) -> skipping. Existing snapshot: {0}" -f $snapName)
        return [pscustomobject]@{ Status = 'Skipped' }
    }

    $expires = (Get-Date).ToUniversalTime().AddHours($RetainHours).ToString('o')

    # Build snapshot config
    $cfg = New-AzSnapshotConfig -Location $disk.Location -CreateOption Copy -SourceUri $disk.Id -Incremental

    try {
        $snap = New-AzSnapshot -ResourceGroupName $snapshotRgName -SnapshotName $snapName -Snapshot $cfg
    }
    catch {
        if ($_.Exception.Message -match 'already exists|Conflict') {
            Write-Host ("Create raced; snapshot {0} exists. Treating as Skipped." -f $snapName)
            return [pscustomobject]@{ Status = 'Skipped' }
        }
        Write-Warning ("New-AzSnapshot failed for {0} disk {1}: {2}" -f $DiskKind, $DiskName, $_.Exception.Message)
        return [pscustomobject]@{ Status = 'Failed' }
    }

    # Tag after creation
    $sTags = @{
        Purpose  = 'PrePatch'
        VMName   = $VmName
        DiskName = $DiskName
        DiskKind = $DiskKind
        ExpireOnUtc = $expires
        CreatedBy   = 'UpdateManager-EventGrid'
        MaintenanceConfigurationId = $MaintenanceConfigId
        EventId     = $EventId
    }
    if ($GroupValue)    { $sTags['Group']    = $GroupValue }
    if ($ScheduleValue) { $sTags['Schedule'] = $ScheduleValue }

    Ensure-Tags -ResourceId $snap.Id -Tags $sTags

    Write-Host ("Snapshot {0} created for disk {1}" -f $snapName, $DiskName)
    return [pscustomobject]@{ Status = 'Created' }
}

function New-PrePatchSnapshots-ForVM {
    param(
        [Parameter(Mandatory)][Microsoft.Azure.Commands.Compute.Models.PSVirtualMachine]$VM,
        [Parameter(Mandatory)][int]$RetainHours,
        [Parameter(Mandatory)][string]$MaintenanceConfigId,
        [Parameter(Mandatory)][string]$EventId,
        [Parameter()][string]$GroupValue,
        [Parameter()][string]$ScheduleValue
    )

    $rg = $VM.ResourceGroupName
    $created = 0
    $skipped = 0
    $failed  = 0

    Write-Host ("[VM] Processing {0} in RG {1}" -f $VM.Name, $rg)

    # ----- OS disk -----
    $os = $VM.StorageProfile.OsDisk
    $isEphemeral = ($os.DiffDiskSettings -and $os.DiffDiskSettings.Option -eq 'Local')
    if (-not $isEphemeral) {
        $osId = $null
        if ($os.ManagedDisk -and $os.ManagedDisk.Id) {
            $osId = $os.ManagedDisk.Id
        }
        elseif ($os.Name) {
            try {
                $d = Get-AzDisk -ResourceGroupName $rg -DiskName $os.Name -ErrorAction Stop
                $osId = $d.Id
            }
            catch {
                Write-Warning ("[VM {0}] Could not resolve OS disk {1}: {2}" -f $VM.Name, $os.Name, $_.Exception.Message)
            }
        }

        if ($osId) {
            $osDiskNameForSnap = if ($os.Name) { $os.Name } else { '(os)' }
            $res = New-DiskSnapshot -DiskResourceId $osId -SnapshotRG $rg -VmName $VM.Name -DiskName $osDiskNameForSnap -DiskKind 'OS' -RetainHours $RetainHours -MaintenanceConfigId $MaintenanceConfigId -EventId $EventId -GroupValue $GroupValue -ScheduleValue $ScheduleValue
            switch ($res.Status) {
                'Created' { $created++ }
                'Skipped' { $skipped++ }
                default   { $failed++  }
            }
        }
        else {
            Write-Warning ("VM {0} OS disk not resolvable; skipping OS snapshot." -f $VM.Name)
            $failed++
        }
    }
    else {
        Write-Host ("VM {0} uses Ephemeral OS; skipping OS snapshot." -f $VM.Name)
    }

    # ----- Data disks -----
    $dataDisks = $VM.StorageProfile.DataDisks
    if ($dataDisks -and $dataDisks.Count -gt 0) {
        foreach ($d in $dataDisks) {
            $ddId = $null
            if ($d.ManagedDisk -and $d.ManagedDisk.Id) {
                $ddId = $d.ManagedDisk.Id
            }
            elseif ($d.Name) {
                try {
                    $dd = Get-AzDisk -ResourceGroupName $rg -DiskName $d.Name -ErrorAction Stop
                    $ddId = $dd.Id
                }
                catch {
                    Write-Warning ("[VM {0}] Could not resolve data disk {1}: {2}" -f $VM.Name, $d.Name, $_.Exception.Message)
                }
            }

            if ($ddId) {
                $res = New-DiskSnapshot -DiskResourceId $ddId -SnapshotRG $rg -VmName $VM.Name -DiskName $d.Name -DiskKind 'Data' -RetainHours $RetainHours -MaintenanceConfigId $MaintenanceConfigId -EventId $EventId -GroupValue $GroupValue -ScheduleValue $ScheduleValue
                switch ($res.Status) {
                    'Created' { $created++ }
                    'Skipped' { $skipped++ }
                    default   { $failed++  }
                }
            }
            else {
                Write-Warning ("Could not resolve data disk {0} on VM {1}" -f $d.Name, $VM.Name)
                $failed++
            }
        }
    }

    [pscustomobject]@{
        Created = $created
        Skipped = $skipped
        Failed  = $failed
    }
}

# ---------- Find target VMs across ALL subscriptions (match MC tags on VMs) ----------
$targets = @()

# All enabled subscriptions that the MI can see
$allSubs = Get-AzSubscription | Where-Object { $_.State -eq 'Enabled' }

foreach ($sub in $allSubs) {
    try {
        Select-AzSubscription -SubscriptionId $sub.Id -ErrorAction Stop | Out-Null
        Write-Host ("[VM Discovery] Scanning subscription {0} ({1})" -f $sub.Name, $sub.Id)
    }
    catch {
        Write-Warning ("[VM Discovery] Cannot select subscription {0}: {1}" -f $sub.Id, $_.Exception.Message)
        continue
    }

    if ($rgList.Count -gt 0) {
        foreach ($rg in $rgList) {
            $vms = Get-AzVM -ResourceGroupName $rg -Status -ErrorAction SilentlyContinue
            if (-not $vms) { continue }

            if ($groupVal) {
                $vms = $vms | Where-Object { $_.Tags -and $_.Tags.ContainsKey('Group') -and $_.Tags['Group'] -eq $groupVal }
            }
            if ($scheduleVal) {
                $vms = $vms | Where-Object { $_.Tags -and $_.Tags.ContainsKey('Schedule') -and $_.Tags['Schedule'] -eq $scheduleVal }
            }

            if ($vms) { $targets += $vms }
            if ($targets.Count -ge $limit) { break }
        }
    }
    else {
        $vms = Get-AzVM -Status -ErrorAction SilentlyContinue
        if (-not $vms) { continue }

        if ($groupVal) {
            $vms = $vms | Where-Object { $_.Tags -and $_.Tags.ContainsKey('Group') -and $_.Tags['Group'] -eq $groupVal }
        }
        if ($scheduleVal) {
            $vms = $vms | Where-Object { $_.Tags -and $_.Tags.ContainsKey('Schedule') -and $_.Tags['Schedule'] -eq $scheduleVal }
        }

        if ($vms) { $targets += $vms }
    }

    if ($targets.Count -ge $limit) { break }
}

$targets = $targets | Sort-Object Id -Unique | Select-Object -First $limit

if (-not $targets -or $targets.Count -eq 0) {
    Write-Host ("No VMs matched Group='{0}' Schedule='{1}' in any subscription." -f $groupVal, $scheduleVal)
    return
}

Write-Host ("[VM Discovery] Total VMs selected across all subscriptions: {0}" -f $targets.Count)
foreach ($t in $targets) {
    Write-Host ("[VM Discovery] Selected VM: {0} (RG: {1}, Id: {2})" -f $t.Name, $t.ResourceGroupName, $t.Id)
}

# ---------- Execute and summarize ----------
$globalCreated = 0
$globalSkipped = 0
$globalFailed  = 0

foreach ($vm in $targets) {
    $res = New-PrePatchSnapshots-ForVM -VM $vm -RetainHours $retain -MaintenanceConfigId $mcId -EventId $eventGridEvent.id -GroupValue $groupVal -ScheduleValue $scheduleVal
    $globalCreated += $res.Created
    $globalSkipped += $res.Skipped
    $globalFailed  += $res.Failed
}

Write-Host ("Snapshot summary: created={0}, skipped(dupe)={1}, failed={2}, targets={3}, group='{4}', schedule='{5}'." -f $globalCreated, $globalSkipped, $globalFailed, $targets.Count, $groupVal, $scheduleVal)

# Optional hard-fail if nothing created nor skipped:
# if ($globalCreated -eq 0 -and $globalSkipped -eq 0) { throw "No snapshots were created or recognized as duplicates. Failed=$globalFailed." }
