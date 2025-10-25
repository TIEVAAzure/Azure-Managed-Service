# ==================== CONFIG ====================
$DaysBack        = 30
$OutCsv          = ""   # e.g. "$HOME/Downloads/storage_audit.csv" (leave empty to skip)
$CheckDiagWriters = $true  # true = check ONLY this SA's diag settings -> LAW (no global scan)
# =================================================

# -------- Helpers --------
function Invoke-AzCliJson {
  param([Parameter(Mandatory)][string[]]$Args)
  $out = & az @Args 2>$null
  if ([string]::IsNullOrWhiteSpace($out)) { return $null }
  try { $out | ConvertFrom-Json } catch { return $null }
}
function To-IsoUtc([datetime]$dt) { $dt.ToUniversalTime().ToString('o') }

function Get-MetricSum($metricObj){
  if (-not $metricObj) { return 0 }
  $total = 0.0
  foreach ($ts in @($metricObj.timeseries)) {
    foreach ($p in @($ts.data)) {
      $v = $p.total; if ($null -eq $v) { $v = $p.Total }
      if ($null -ne $v) { $total += [double]$v }
    }
  }
  return $total
}

function Get-BlobMetrics {
  param([string]$BlobSvcId,[string]$StartIso,[string]$EndIso)
  $m = Invoke-AzCliJson @(
    'monitor','metrics','list',
    '--resource', $BlobSvcId,
    '--metric','Transactions','Ingress','Egress',
    '--aggregation','Total',
    '--interval','PT1H',
    '--start-time', $StartIso,
    '--end-time',   $EndIso,
    '-o','json'
  )
  if (-not $m -or -not $m.value) { return @{ TX=0; IN_MB=0; OUT_MB=0 } }
  $tx = Get-MetricSum ($m.value | Where-Object { $_.name.value -eq 'Transactions' })
  $ing = Get-MetricSum ($m.value | Where-Object { $_.name.value -eq 'Ingress' })
  $egr = Get-MetricSum ($m.value | Where-Object { $_.name.value -eq 'Egress' })
  return @{
    TX     = [int][math]::Round($tx,0)
    IN_MB  = [math]::Round($ing / 1MB, 3)
    OUT_MB = [math]::Round($egr / 1MB, 3)
  }
}

function Get-ApiBreakdown {
  param([string]$BlobSvcId,[string]$StartIso,[string]$EndIso)
  $m = Invoke-AzCliJson @(
    'monitor','metrics','list',
    '--resource', $BlobSvcId,
    '--metric','Transactions',
    '--dimension','ApiName',
    '--aggregation','Total',
    '--interval','PT1H',
    '--start-time', $StartIso,
    '--end-time',   $EndIso,
    '-o','json'
  )
  $rows = @()
  if ($m -and $m.value -and $m.value[0].timeseries) {
    foreach ($ts in $m.value[0].timeseries) {
      $api = ($ts.metadatavalues | Where-Object { $_.name.value -eq 'ApiName' }).value
      if (-not $api) { $api = 'Unknown' }
      $sum = 0
      foreach ($p in @($ts.data)) {
        $v = $p.total; if ($null -eq $v) { $v = $p.Total }
        if ($v) { $sum += [double]$v }
      }
      $rows += [pscustomobject]@{ Api = $api; Total = [int][math]::Round($sum,0) }
    }
  }
  return $rows
}

function Get-References {
  param($AllVMs,$AllVMSS,$AllDisks,[string]$AccountName)
  $vmHits   = New-Object System.Collections.Generic.List[string]
  $vmssHits = New-Object System.Collections.Generic.List[string]
  $diskHits = New-Object System.Collections.Generic.List[string]
  foreach ($vm in @($AllVMs)) {
    $bd = $vm.DiagnosticsProfile.BootDiagnostics
    $uri = if ($bd) { $bd.StorageUri ?? $bd.storageUri } else { $null }
    if ($uri -and $uri -match "(^|\/\/|\.)$([Regex]::Escape($AccountName))(\.|\/)") { $vmHits.Add($vm.name) }
  }
  foreach ($ss in @($AllVMSS)) {
    $bd = $ss.VirtualMachineProfile.DiagnosticsProfile.BootDiagnostics
    $uri = if ($bd) { $bd.StorageUri ?? $bd.storageUri } else { $null }
    if ($uri -and $uri -match "(^|\/\/|\.)$([Regex]::Escape($AccountName))(\.|\/)") { $vmssHits.Add($ss.name) }
  }
  foreach ($d in @($AllDisks)) {
    $src = $d.creationData.sourceUri
    if ($src -and $src -match "(^|\/\/|\.)$([Regex]::Escape($AccountName))(\.|\/)") { $diskHits.Add($d.name) }
  }
  return @{
    VMs   = ($vmHits -join ', ')
    VMSS  = ($vmssHits -join ', ')
    Disks = ($diskHits -join ', ')
  }
}

# -------- Time window --------
$startIso = To-IsoUtc ((Get-Date).AddDays(-1 * $DaysBack))
$endIso   = To-IsoUtc (Get-Date)

# -------- Subscriptions --------
$subs = Invoke-AzCliJson @('account','list','--query',"[?state=='Enabled']",'-o','json')
if (-not $subs) {
  $current = Invoke-AzCliJson @('account','show','-o','json')
  if ($current) { $subs = @($current) }
}
$subs = @($subs) | ForEach-Object {
  [pscustomobject]@{ id = $_.id ?? $_.subscriptionId; name = $_.name ?? $_.displayName }
} | Where-Object { $_.id }

if (-not $subs -or $subs.Count -eq 0) {
  Write-Error "No subscriptions visible. Confirm Cloud Shell context with 'az account show'."
  return
}

# -------- Scan --------
$AllResults = New-Object System.Collections.Generic.List[object]

foreach ($sub in $subs) {
  Write-Host "`n== Subscription: $($sub.name) ($($sub.id)) =" -ForegroundColor Cyan
  & az account set --subscription $sub.id | Out-Null

  $sas = Invoke-AzCliJson @('storage','account','list','-o','json')
  if (-not $sas) { Write-Host "  (no storage accounts)"; continue }

  $allVMs   = Invoke-AzCliJson @('vm','list','-d','-o','json'); if (-not $allVMs) { $allVMs = @() }
  $allVMSS  = Invoke-AzCliJson @('vmss','list','-o','json');   if (-not $allVMSS) { $allVMSS = @() }
  $allDisks = Invoke-AzCliJson @('disk','list','-o','json');   if (-not $allDisks) { $allDisks = @() }

  foreach ($sa in $sas) {
    $saName = $sa.name
    Write-Host "  → Auditing: $saName" -ForegroundColor Yellow

    $blobSvcId = "$($sa.id)/blobServices/default"

    # Metrics & API breakdown
    $metrics  = Get-BlobMetrics -BlobSvcId $blobSvcId -StartIso $startIso -EndIso $endIso
    $apiRows  = Get-ApiBreakdown -BlobSvcId $blobSvcId -StartIso $startIso -EndIso $endIso
    $apiTop3  = ($apiRows | Sort-Object Total -Descending | Select-Object -First 3 | ForEach-Object { "$($_.Api):$($_.Total)" }) -join ', '
    $hasDataOps = $apiRows | Where-Object { $_.Api -match '^(GetBlob|PutBlob|PutPage|AppendBlock)$' -and $_.Total -gt 0 }
    $metadataOnly = if (($apiRows.Count -gt 0) -and -not $hasDataOps) { 'Yes' } else { 'No' }

    # References
    $refs = Get-References -AllVMs $allVMs -AllVMSS $allVMSS -AllDisks $allDisks -AccountName $saName

    # === CHANGED DIAG SECTION (no global scan) ===
    $diagWriters = ''
    if ($CheckDiagWriters) {
      # Only check THIS storage account's diagnostic settings and list any LAW targets
      $ds = Invoke-AzCliJson @('monitor','diagnostic-settings','list','--resource',$sa.id,'-o','json')
      $laws = @()
      if ($ds) {
        $list = if ($ds.value) { $ds.value } else { $ds }
        foreach ($d in $list) {
          $hasStorageLogs = $false
          foreach ($l in @($d.logs)) {
            if ($l.enabled -and ($l.category -like 'Storage*' -or $l.category -like 'StorageBlob*')) { $hasStorageLogs = $true; break }
          }
          if ($hasStorageLogs -and $d.workspaceId) { $laws += $d.workspaceId }
        }
      }
      if ($laws.Count -gt 0) { $diagWriters = "LAW: " + ($laws -join '; ') }
    }

    # Heuristic
    $probablyUnused = ( ($metadataOnly -eq 'Yes') -and
                        ($metrics.TX -lt 500) -and
                        ($metrics.OUT_MB -lt 1) -and
                        [string]::IsNullOrWhiteSpace($refs.VMs) -and
                        [string]::IsNullOrWhiteSpace($refs.VMSS) -and
                        [string]::IsNullOrWhiteSpace($refs.Disks) -and
                        ([string]::IsNullOrWhiteSpace($diagWriters) -or -not $CheckDiagWriters)
                      ) ? 'Yes' : 'No'

    $AllResults.Add([pscustomobject]@{
      Subscription          = $sub.name
      ResourceGroup         = $sa.resourceGroup
      StorageAccount        = $saName
      Location              = $sa.location
      Sku                   = $sa.sku.name
      Transactions_30d      = $metrics.TX
      Ingress_MB_30d        = $metrics.IN_MB
      Egress_MB_30d         = $metrics.OUT_MB
      ApiTop3               = $apiTop3
      MetadataOnly          = $metadataOnly
      ReferencedByVMs       = $refs.VMs
      ReferencedByVMSS      = $refs.VMSS
      ReferencedByDisks     = $refs.Disks
      DiagWritersToSA       = $diagWriters
      ProbablyUnused        = $probablyUnused
    })
  }
}

# -------- Output --------
Write-Host "`n=== Storage Account Audit Summary (last $DaysBack days) ===" -ForegroundColor Cyan
$AllResults |
  Sort-Object Subscription, ResourceGroup, StorageAccount |
  Format-Table Subscription, ResourceGroup, StorageAccount, Sku,
               Transactions_30d, Ingress_MB_30d, Egress_MB_30d,
               MetadataOnly, ApiTop3, ReferencedByVMs, ReferencedByVMSS, ReferencedByDisks,
               DiagWritersToSA, ProbablyUnused -AutoSize

if ($OutCsv) {
  $dir = Split-Path -Parent $OutCsv
  if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
  $AllResults | Export-Csv -Path $OutCsv -NoTypeInformation -Encoding UTF8
  Write-Host "`nCSV exported to: $OutCsv" -ForegroundColor Green
}

Write-Host "`n✅ Completed scan." -ForegroundColor Cyan
