<# =======================================================================
   Azure Backup Posture Auditor (RSV + Backup Vaults) — tenant-agnostic
   - Enumerates subscriptions (optionally limited via $TargetSubscriptions
     and/or name/ID regex filters)
   - Reports vault posture + VM/DB backup coverage
   - Uses RSV **root GET** (2025-08-01) first, then backupResourceConfig / backupconfig
   - Handles Backup Vaults returned without ResourceGroupName
   - REST with retry, pagination for BackupInstances (x-ms-continuation) and
     RSV backupProtectedItems (nextLink)
   - VMs: backup cadence (policy → fallback to RP spacing), optional window,
     last backup time/status, observed RPO, RpoSource, ProtectionState
   - SQL in VM (workload): full/diff/log cadence + optional window (policy),
     last backup status/time, observed RPO from RPs (prefers log)
   - Azure SQL (PaaS): observed RPO from PITR (latest restorable time)
   - Produces:
       * Console tables
       * XLSX with multiple sheets (RSV, BackupVaults, VM/DB coverage,
         VM & SQL detail, Findings, Subscription_Summary, Summary)
======================================================================= #>

$ErrorActionPreference = 'Stop'

# --- Control which subscriptions to include ---
$TargetSubscriptions = @(
  'a2274ccd-57b8-4d72-a94a-c38a235cf56e',  # identity-tieva-dev
  'df82ae5c-8de4-4e5d-a976-a75d56fc3b9c'   # learn-shared-tieva-dev
  # Add more subscription IDs or names as needed
)

# ---------------- Settings ----------------
$ExportCsv   = $false                  # reserved if you later want CSV
$OutPath     = "$HOME\Downloads"
$Diagnostics = $true                   # set $false to silence warnings

# Optional subscription include filters (regex). Leave blank to include all.
$SubIncludeNameRegex = ''              # e.g. 'Prod|Contoso'
$SubIncludeIdRegex   = ''              # e.g. '^[0-9a-f-]{36}$'

# RPO thresholds (hours)
$VmRpoWarningHours    = 26
$VmRpoCriticalHours   = 50
$SqlRpoWarningHours   = 26
$SqlRpoCriticalHours  = 50

# Centralized API versions
$Api = @{
  RSV_VaultRoot_New       = '2025-08-01' # vault **root** returns securitySettings/redundancy/restoreSettings
  RSV_BackupResourceCfg   = '2023-02-01' # Microsoft.RecoveryServices/vaults/<name>/backupResourceConfig
  RSV_BackupConfig        = '2023-02-01' # Microsoft.RecoveryServices/vaults/<name>/backupconfig
  RSV_BackupConfig_Legacy = '2016-12-01'
  RSV_ProtectedItems      = '2023-02-01' # Microsoft.RecoveryServices/vaults/<name>/backupProtectedItems
  RSV_RecoveryPoints      = '2023-02-01' # .../protectedItems/<id>/recoveryPoints
  DP_BackupVault          = '2024-04-01' # Microsoft.DataProtection/backupVaults
  DP_BackupInstances      = '2024-04-01' # Microsoft.DataProtection/backupVaults/<name>/backupInstances
}

# ---------------- Module sanity check ----------------
$required = @('Az.Accounts','Az.Resources','Az.RecoveryServices','Az.DataProtection','Az.Compute','Az.Sql')
$missing = $required | Where-Object { -not (Get-Module -ListAvailable -Name $_) }
if ($missing) {
  Write-Warning "Missing modules: $($missing -join ', '). Install with: Install-Module Az -Scope CurrentUser"
}

# ---------------- Helpers ----------------
function Derive-SecurityLevel {
  param(
    [string]$EnhancedSecurityState,
    [bool]$IsMUAEnabled,
    [string]$SoftDeleteState
  )
  if ($EnhancedSecurityState -in @('Enabled','AlwaysON') -or
      $IsMUAEnabled -or
      $SoftDeleteState -in @('Enabled','AlwaysON')) {
    'Enhanced'
  } else {
    'Standard'
  }
}

function To-AlwaysOnBool {
  param([string]$A,[string]$B)
  (($A -eq 'AlwaysON') -or ($B -eq 'AlwaysON'))
}

# Normalize "On/Off" → "Enabled/Disabled"
function Normalize-SD {
  param([string]$Value)
  if ([string]::IsNullOrWhiteSpace($Value)) { return $null }
  if ($Value -match '^(?i)on$')  { return 'Enabled' }
  if ($Value -match '^(?i)off$') { return 'Disabled' }
  return $Value
}

function Normalize-Redundancy {
  param([string]$Value)
  if (-not $Value) { return $null }
  $map = @{
    'LocallyRedundant'='LRS'; 'GeoRedundant'='GRS'; 'ZoneRedundant'='ZRS'; 'ReadAccessGeoRedundant'='RA-GRS'
    'LRS'='LRS'; 'GRS'='GRS'; 'ZRS'='ZRS'; 'RA-GRS'='RA-GRS'
  }
  if ($map.ContainsKey($Value)) { $map[$Value] } else { $Value }
}

# ---------- REST helpers (retry) ----------
function Invoke-ArmGet {
  param(
    [Parameter(Mandatory)] [string]$SubscriptionId,
    [Parameter(Mandatory)] [string]$ResourceGroup,
    [Parameter(Mandatory)] [string]$ProviderTypePath, # e.g. Microsoft.DataProtection/backupVaults/<name>[/...]
    [string]$ApiVersion
  )
  if ([string]::IsNullOrWhiteSpace($ApiVersion)) {
    if ($ProviderTypePath -like 'Microsoft.RecoveryServices/*') { $ApiVersion = $Api.RSV_BackupResourceCfg }
    elseif ($ProviderTypePath -like 'Microsoft.DataProtection/*') { $ApiVersion = $Api.DP_BackupVault }
  }
  if ([string]::IsNullOrWhiteSpace($ApiVersion)) { $ApiVersion = '2023-02-01' }

  $base = "https://management.azure.com"
  $rel  = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroup/providers/$ProviderTypePath"
  $uri  = '{0}{1}?api-version={2}' -f $base, $rel, $ApiVersion
  Invoke-ArmGetUri -Uri $uri
}

function Invoke-ArmGetById {
  param(
    [Parameter(Mandatory)][string]$ResourceId,     # /subscriptions/.../providers/Microsoft.RecoveryServices/vaults/<name>
    [string]$SuffixPath = '',                      # '/backupResourceConfig' | '/backupconfig' | ''
    [Parameter(Mandatory)][string]$ApiVersion
  )
  $base = "https://management.azure.com"
  $rid = $ResourceId.TrimEnd('/')
  $suffix = if ($SuffixPath) {
    if ($SuffixPath.StartsWith('/')) { $SuffixPath } else { "/$SuffixPath" }
  } else { '' }
  $uri = '{0}{1}{2}?api-version={3}' -f $base, $rid, $suffix, $ApiVersion
  Invoke-ArmGetUri -Uri $uri
}

function Invoke-ArmGetUri {
  param([Parameter(Mandatory)][string]$Uri)
  for ($i=0; $i -lt 4; $i++) {
    try {
      $resp = Invoke-AzRestMethod -Uri $Uri -Method GET -ErrorAction Stop
      if ($resp.Content) {
        return ($resp.Content | ConvertFrom-Json)
      } else {
        return $null
      }
    } catch {
      $status=$null;$reason=$null
      try {
        $status=$_.Exception.Response.StatusCode.value__
        $reason=$_.Exception.Response.ReasonPhrase
      } catch {}
      if ($Diagnostics) { Write-Warning "ARM GET failed ($status $reason): $Uri" }
      if ($status -in 429,500,502,503,504) {
        Start-Sleep -Seconds ([math]::Pow(2,$i))
        continue
      }
      break
    }
  }
  $null
}

function Get-ResourceGroupFromId {
  param([Parameter(Mandatory)][string]$ResourceId)
  if ([string]::IsNullOrWhiteSpace($ResourceId)) { return $null }
  $m = [regex]::Match($ResourceId, '/resourceGroups/([^/]+)/', 'IgnoreCase')
  if ($m.Success) { $m.Groups[1].Value } else { $null }
}

# Pagination for Microsoft.DataProtection/backupInstances via x-ms-continuation
function Get-AllBackupInstances {
  param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroupName,
    [Parameter(Mandatory)][string]$VaultName,
    [string]$ApiVersion = $Api.DP_BackupInstances
  )
  $base    = "https://management.azure.com"
  $relBase = "/subscriptions/$SubscriptionId/resourceGroups/$ResourceGroupName/providers/Microsoft.DataProtection/backupVaults/$VaultName/backupInstances"
  $results = @()
  $cont    = $null

  do {
    $qs   = if ($cont) { '{0}&{1}' -f ('api-version=' + $ApiVersion), $cont } else { 'api-version=' + $ApiVersion }
    $uri  = '{0}{1}?{2}' -f $base, $relBase, $qs
    $resp = $null

    for ($i=0; $i -lt 4; $i++) {
      try {
        $resp = Invoke-AzRestMethod -Uri $uri -Method GET -ErrorAction Stop
        break
      } catch {
        $status=$null;$reason=$null
        try {
          $status=$_.Exception.Response.StatusCode.value__
          $reason=$_.Exception.Response.ReasonPhrase
        } catch {}
        if ($Diagnostics) { Write-Warning "backupInstances GET failed ($status $reason): $uri" }
        if ($status -in 429,500,502,503,504) {
          Start-Sleep -Seconds ([math]::Pow(2,$i))
          continue
        }
        break
      }
    }

    if ($resp -and $resp.Content) {
      $json = $resp.Content | ConvertFrom-Json
      if ($json.value) { $results += $json.value }
      $cont = $null
      foreach ($hdr in $resp.Headers.GetEnumerator()) {
        if ($hdr.Key -ieq 'x-ms-continuation') { $cont = $hdr.Value; break }
      }
    } else {
      $cont = $null
    }
  } while ($cont)

  $results
}

# ---------- Formatting / parsing ----------
function Parse-Iso8601Duration {
  param([string]$s)
  if ([string]::IsNullOrWhiteSpace($s)) { return $null }
  $m = [regex]::Match($s, '^P(?:(?<d>\d+)D)?(?:T(?:(?<h>\d+)H)?(?:(?<m>\d+)M)?(?:(?<s>\d+)S)?)?$')
  if (-not $m.Success) { return $null }
  $days=[int]($m.Groups['d'].Value)
  $hrs =[int]($m.Groups['h'].Value)
  $mins=[int]($m.Groups['m'].Value)
  $secs=[int]($m.Groups['s'].Value)
  New-TimeSpan -Days $days -Hours $hrs -Minutes $mins -Seconds $secs
}

# Snap cadence to nice values (avoid 3.76h; allow ±20m tolerance)
function Format-TimeSpanText {
  param($ts)
  if ($null -eq $ts) { return $null }
  if ($ts -isnot [TimeSpan]) { return [string]$ts }

  $toleranceMinutes = 20
  $commonHours = @(1,2,3,4,6,8,12,24)

  foreach ($h in $commonHours) {
    if ([math]::Abs($ts.TotalMinutes - ($h*60)) -le $toleranceMinutes) {
      return "Every $h hour(s)"
    }
  }

  $nearestHr = [math]::Round($ts.TotalHours)
  if ([math]::Abs(($ts.TotalHours - $nearestHr)*60) -le $toleranceMinutes) {
    return "Every $nearestHr hour(s)"
  }

  if ($ts.TotalHours -ge 1) {
    "Every $([math]::Round($ts.TotalHours,2)) hour(s)"
  } elseif ($ts.TotalMinutes -ge 1) {
    "Every $([math]::Round($ts.TotalMinutes,0)) minute(s)"
  } else {
    "Every $([math]::Round($ts.TotalSeconds,0)) second(s)"
  }
}

function Format-WindowText {
  param($ts)
  if ($null -eq $ts) { return $null }
  if ($ts -isnot [TimeSpan]) { return [string]$ts }
  if ($ts.TotalDays -ge 1 -and [math]::Abs($ts.TotalDays - [math]::Round($ts.TotalDays)) -lt 1e-9) {
    "$([int]$ts.TotalDays) day(s)"
  } elseif ($ts.TotalHours -ge 1) {
    "$([math]::Round($ts.TotalHours,2)) hour(s)"
  } elseif ($ts.TotalMinutes -ge 1) {
    "$([math]::Round($ts.TotalMinutes,0)) minute(s)"
  } else {
    "$([math]::Round($ts.TotalSeconds,0)) second(s)"
  }
}

# ---------- VM policy schedule (enhanced + classic) ----------
function Get-PolicyScheduleInfo {
  param($Policy)

  if (-not $Policy) {
    return [pscustomobject]@{
      CadenceTS   = $null
      CadenceText = $null
      WindowTS    = $null
      WindowText  = $null
    }
  }

  $p = if ($Policy.properties) { $Policy.properties } else { $Policy }

  # Enhanced policy shape (often under "backupSchedule")
  $bs = $null
  foreach ($name in 'backupSchedule','BackupSchedule') {
    if ($p.PSObject.Properties.Name -contains $name) { $bs = $p.$name; break }
  }

  if ($bs) {
    $cadTs = $null
    foreach ($n in 'repeatingTimeIntervals','RepeatingTimeIntervals') {
      if ($bs.PSObject.Properties.Name -contains $n) {
        $arr = $bs.$n
        if ($arr -and $arr.Count -gt 0) {
          $m = [regex]::Match([string]$arr[0], '(P(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?(?:\d+S)?)?)$')
          if ($m.Success) { $cadTs = Parse-Iso8601Duration $m.Groups[1].Value }
        }
      }
    }

    $winTs = $null
    foreach ($n in 'scheduleWindowDuration','ScheduleWindowDuration','duration','Duration') {
      if ($bs.PSObject.Properties.Name -contains $n) {
        $winTs = Parse-Iso8601Duration $bs.$n
        if ($winTs) { break }
      }
    }
    if (-not $winTs) {
      foreach ($n in 'scheduleWindowDurationInHours','ScheduleWindowDurationInHours') {
        if ($bs.PSObject.Properties.Name -contains $n) {
          $h = [int]$bs.$n
          if ($h -gt 0) { $winTs = New-TimeSpan -Hours $h; break }
        }
      }
    }

    return [pscustomobject]@{
      CadenceTS   = $cadTs
      CadenceText = (Format-TimeSpanText $cadTs)
      WindowTS    = $winTs
      WindowText  = (Format-WindowText $winTs)
    }
  }

  # Classic policy
  $sp = $null
  foreach ($name in 'SchedulePolicy','schedulePolicy') {
    if ($p.PSObject.Properties.Name -contains $name) { $sp = $p.$name; break }
  }
  if (-not $sp) {
    return [pscustomobject]@{
      CadenceTS   = $null
      CadenceText = $null
      WindowTS    = $null
      WindowText  = $null
    }
  }

  function _pick_iso([string]$s) {
    if (-not $s) { return $null }
    $m=[regex]::Match($s,'(P(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?(?:\d+S)?)?)$')
    if($m.Success){$m.Groups[1].Value}else{$null}
  }

  $cadTs = $null
  foreach ($propName in 'repeatingTimeIntervals','RepeatingTimeIntervals') {
    if ($sp.PSObject.Properties.Name -contains $propName) {
      $arr = $sp.$propName
      if ($arr -and $arr.Count -gt 0) {
        $iso = _pick_iso ([string]$arr[0])
        if ($iso) { $cadTs = Parse-Iso8601Duration $iso; break }
      }
    }
  }
  if (-not $cadTs) {
    foreach ($propName in 'RepeatingTimeInterval','repeatingTimeInterval','RepetitionInterval','ScheduleInterval') {
      if ($sp.PSObject.Properties.Name -contains $propName) {
        $cadTs = Parse-Iso8601Duration $sp.$propName
        if ($cadTs) { break }
      }
    }
  }
  if (-not $cadTs) {
    foreach ($propName in 'scheduleIntervalInMins','ScheduleIntervalInMins') {
      if ($sp.PSObject.Properties.Name -contains $propName) {
        $mins = [int]$sp.$propName
        if ($mins -gt 0) { $cadTs = New-TimeSpan -Minutes $mins; break }
      }
    }
  }
  if (-not $cadTs) {
    foreach ($propName in 'backupIntervalInHours','BackupIntervalInHours','repetitionInHours','RepeatIntervalInHours') {
      if ($sp.PSObject.Properties.Name -contains $propName) {
        $hrs = [int]$sp.$propName
        if ($hrs -gt 0) { $cadTs = New-TimeSpan -Hours $hrs; break }
      }
    }
  }

  # derive for daily/weekly classic schedules if needed
  if (-not $cadTs) {
    $freq  = $sp.ScheduleRunFrequency
    $times = $sp.ScheduleRunTimes
    if ($freq -eq 'Daily' -and $times -and $times.Count -gt 0) {
      $ofs = $times | ForEach-Object { ([datetime]$_).TimeOfDay } | Sort-Object
      if ($ofs.Count -eq 1) {
        $cadTs = New-TimeSpan -Hours 24
      } else {
        $maxGap = [TimeSpan]::Zero
        for ($i=0; $i -lt $ofs.Count-1; $i++) {
          $gap = $ofs[$i+1] - $ofs[$i]
          if ($gap -gt $maxGap) { $maxGap = $gap }
        }
        $wrap = (New-TimeSpan -Days 1) - $ofs[$ofs.Count-1] + $ofs[0]
        if ($wrap -gt $maxGap) { $maxGap = $wrap }
        $cadTs = $maxGap
      }
    } elseif ($freq -eq 'Weekly') {
      $days = $sp.ScheduleRunDays
      if ($days -and $days.Count -gt 0) {
        $map = @{ Sunday=0; Monday=1; Tuesday=2; Wednesday=3; Thursday=4; Friday=5; Saturday=6 }
        $idx = $days | ForEach-Object { $map[$_] } | Sort-Object
        if ($idx.Count -eq 1) {
          $cadTs = New-TimeSpan -Days 7
        } else {
          $maxGapDays = 0
          for ($i=0; $i -lt $idx.Count-1; $i++) {
            $gap = $idx[$i+1] - $idx[$i]
            if ($gap -gt $maxGapDays) { $maxGapDays = $gap }
          }
          $wrapDays = 7 - $idx[$idx.Count-1] + $idx[0]
          if ($wrapDays -gt $maxGapDays) { $maxGapDays = $wrapDays }
          $cadTs = New-TimeSpan -Days $maxGapDays
        }
      } else {
        $cadTs = New-TimeSpan -Days 7
      }
    }
  }

  # window (classic sometimes surfaces enhanced-like props)
  $winTs = $null
  foreach ($propName in 'scheduleWindowDuration','ScheduleWindowDuration','duration','Duration') {
    if ($sp.PSObject.Properties.Name -contains $propName) {
      $winTs = Parse-Iso8601Duration $sp.$propName
      if ($winTs) { break }
    }
  }
  if (-not $winTs) {
    foreach ($propName in 'scheduleWindowDurationInHours','ScheduleWindowDurationInHours') {
      if ($sp.PSObject.Properties.Name -contains $propName) {
        $hrs=[int]$sp.$propName
        if ($hrs -gt 0) { $winTs = New-TimeSpan -Hours $hrs; break }
      }
    }
  }

  [pscustomobject]@{
    CadenceTS   = $cadTs
    CadenceText = (Format-TimeSpanText $cadTs)
    WindowTS    = $winTs
    WindowText  = (Format-WindowText $winTs)
  }
}

# If policy unknown, estimate VM cadence from last two recovery points
function Get-RpoFromRecoveryPoints {
  param([Parameter(Mandatory)][string]$ProtectedItemId)

  $base = "https://management.azure.com"
  $uri  = "$base$ProtectedItemId/recoveryPoints?api-version=$($Api.RSV_RecoveryPoints)"
  $times = @()

  do {
    $j = $null
    try {
      $j = Invoke-ArmGetUri -Uri $uri
    } catch {}

    if ($j -and $j.value) {
      $times += ($j.value | ForEach-Object {
        try { [datetime]$_.properties.recoveryPointTime } catch { $null }
      } | Where-Object { $_ })
    }

    $uri = if ($j -and $j.nextLink) { $j.nextLink } else { $null }

    if ($times.Count -ge 2 -and $uri) { $uri = $null } # only need two
  } while ($uri)

  if ($times.Count -lt 2) { return $null }

  $sorted = $times | Sort-Object
  ($sorted[-1] - $sorted[-2])
}

function Split-VmFromResourceId {
  param([string]$ResourceId)

  if ([string]::IsNullOrWhiteSpace($ResourceId)) { return $null }

  $m = [regex]::Match(
    $ResourceId,
    '/resourceGroups/([^/]+)/providers/Microsoft\.Compute/virtualMachines/([^/]+)',
    'IgnoreCase'
  )

  if ($m.Success) {
    [pscustomobject]@{
      ResourceGroup = $m.Groups[1].Value
      VMName        = $m.Groups[2].Value
    }
  } else {
    $null
  }
}

# ---------- SQL helpers ----------
function Get-SqlVmPolicyScheduleInfo {
  param($Policy)  # REST or cmdlet object

  if (-not $Policy) {
    return [pscustomobject]@{
      FullCadenceTS=$null; FullCadenceText=$null
      DiffCadenceTS=$null; DiffCadenceText=$null
      LogCadenceTS=$null;  LogCadenceText=$null
      WindowTS=$null;      WindowText=$null
    }
  }

  $p = if ($Policy.properties) { $Policy.properties } else { $Policy }

  $full = $p.FullBackupSchedulePolicy ?? $p.fullBackupSchedulePolicy
  $diff = $p.DifferentialBackupSchedulePolicy ?? $p.differentialBackupSchedulePolicy
  $log  = $p.LogBackupSchedulePolicy ?? $p.logBackupSchedulePolicy

  function _cad([object]$node) {
    if (-not $node) { return $null }
    foreach ($n in 'repeatingTimeIntervals','RepeatingTimeIntervals') {
      if ($node.PSObject.Properties.Name -contains $n) {
        $v = $node.$n
        if ($v -and $v.Count -gt 0) {
          $m = [regex]::Match([string]$v[0], '(P(?:\d+D)?(?:T(?:\d+H)?(?:\d+M)?(?:\d+S)?)?)$')
          if ($m.Success) { return (Parse-Iso8601Duration $m.Groups[1].Value) }
        }
      }
    }
    foreach ($n in 'RepetitionInterval','ScheduleInterval','repeatingTimeInterval','RepeatingTimeInterval') {
      if ($node.PSObject.Properties.Name -contains $n) {
        $ts = Parse-Iso8601Duration $node.$n
        if ($ts) { return $ts }
      }
    }
    foreach ($n in 'scheduleIntervalInMins','ScheduleIntervalInMins') {
      if ($node.PSObject.Properties.Name -contains $n) {
        $m = [int]$node.$n
        if ($m -gt 0) { return (New-TimeSpan -Minutes $m) }
      }
    }
    foreach ($n in 'backupIntervalInHours','BackupIntervalInHours','repetitionInHours','RepeatIntervalInHours') {
      if ($node.PSObject.Properties.Name -contains $n) {
        $h = [int]$node.$n
        if ($h -gt 0) { return (New-TimeSpan -Hours $h) }
      }
    }
    $null
  }

  function _win([object]$node) {
    if (-not $node) { return $null }
    foreach ($n in 'scheduleWindowDuration','ScheduleWindowDuration','duration','Duration') {
      if ($node.PSObject.Properties.Name -contains $n) {
        $ts = Parse-Iso8601Duration $node.$n
        if ($ts) { return $ts }
      }
    }
    foreach ($n in 'scheduleWindowDurationInHours','ScheduleWindowDurationInHours') {
      if ($node.PSObject.Properties.Name -contains $n) {
        $h = [int]$node.$n
        if ($h -gt 0) { return (New-TimeSpan -Hours $h) }
      }
    }
    $null
  }

  $winTs = (_win $full)
  if (-not $winTs) { $winTs = (_win $diff) }
  if (-not $winTs) { $winTs = (_win $log) }

  $fc = _cad $full
  $dc = _cad $diff
  $lc = _cad $log

  [pscustomobject]@{
    FullCadenceTS   = $fc; FullCadenceText   = (Format-TimeSpanText $fc)
    DiffCadenceTS   = $dc; DiffCadenceText   = (Format-TimeSpanText $dc)
    LogCadenceTS    = $lc; LogCadenceText    = (Format-TimeSpanText $lc)
    WindowTS        = $winTs
    WindowText      = (Format-WindowText $winTs)
  }
}

function Get-SqlVmObservedRpo {
  param(
    [Parameter(Mandatory)][string]$ProtectedItemId,
    [Parameter(Mandatory)][string]$ApiVersion
  )

  $base = "https://management.azure.com"
  $uri  = "$base$ProtectedItemId/recoveryPoints?api-version=$ApiVersion"
  $j = $null

  try {
    $j = Invoke-AzRestMethod -Uri $uri -Method GET -ErrorAction Stop
  } catch {
    return $null
  }

  if (-not $j.Content) { return $null }
  $rp = ($j.Content | ConvertFrom-Json)
  if (-not $rp.value) { return $null }

  $all = $rp.value | ForEach-Object {
    $t = $null
    try { $t = [datetime]$_.properties.recoveryPointTime } catch {}
    if ($t) {
      [pscustomobject]@{
        Type = $_.properties.recoveryPointType
        Time = $t
      }
    }
  }

  if (-not $all) { return $null }

  $pref = @('Log','TransactionLog','Differential','CopyOnly','Full','AppConsistent')
  foreach ($kind in $pref) {
    $hit = $all | Where-Object { $_.Type -eq $kind } | Sort-Object Time -Descending | Select-Object -First 1
    if ($hit) { return [math]::Round(((Get-Date) - $hit.Time).TotalHours,2) }
  }

  $latest = $all | Sort-Object Time -Descending | Select-Object -First 1
  if ($latest) { return [math]::Round(((Get-Date) - $latest.Time).TotalHours,2) }

  $null
}

function Get-AzureSqlPitrObservedRpo {
  param(
    [Parameter(Mandatory)][string]$ResourceGroupName,
    [Parameter(Mandatory)][string]$ServerName,
    [Parameter(Mandatory)][string]$DatabaseName
  )

  $rp = $null
  try {
    $rp = Get-AzSqlDatabaseRestorePoint `
            -ResourceGroupName $ResourceGroupName `
            -ServerName $ServerName `
            -DatabaseName $DatabaseName `
            -ErrorAction Stop |
          Where-Object { $_.RestorePointType -eq 'CONTINUOUS' } |
          Sort-Object RestorePointCreationDate -Descending |
          Select-Object -First 1
  } catch {
    return $null
  }

  if (-not $rp -or -not $rp.RestorePointCreationDate) { return $null }

  [math]::Round(((Get-Date) - [datetime]$rp.RestorePointCreationDate).TotalHours,2)
}

# ---------------- Collections ----------------
$rsvReport       = [System.Collections.Generic.List[object]]::new()
$bvReport        = [System.Collections.Generic.List[object]]::new()
$vmReport        = [System.Collections.Generic.List[object]]::new()
$dbReport        = [System.Collections.Generic.List[object]]::new()
$vmDetailReport  = [System.Collections.Generic.List[object]]::new()
$sqlDetailReport = [System.Collections.Generic.List[object]]::new()
$protectedIds    = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# New: posture / gap findings
$findings        = [System.Collections.Generic.List[object]]::new()

# === RSV VM discovery (cmdlets + REST fallback) ===
function Get-RsvVmItems {
  param(
    [Parameter()][string]$VaultResourceId,
    [switch]$VerboseDiag
  )

  $all = @()
  $pathCounts = @{direct=0; containers=0; rest=0}

  # Path 1: Cmdlet by management type
  try {
    $items = Get-AzRecoveryServicesBackupItem -BackupManagementType AzureIaasVM -ErrorAction SilentlyContinue
    if ($items) { $all += $items; $pathCounts.direct = ($items | Measure-Object).Count }
  } catch {}

  # Path 2: Containers → items
  if (($all | Measure-Object).Count -eq 0) {
    foreach ($ctype in 'AzureVM','AzureVMAppContainer') {
      try {
        $cons = Get-AzRecoveryServicesBackupContainer -ContainerType $ctype -Status Registered -ErrorAction SilentlyContinue
        foreach ($c in ($cons | Where-Object { $_ })) {
          try {
            $its = Get-AzRecoveryServicesBackupItem -Container $c -ErrorAction SilentlyContinue
            if ($its) { $all += $its; $pathCounts.containers += ($its | Measure-Object).Count }
          } catch {}
        }
      } catch {}
    }
  }

  # Path 3: REST fallback — use known vault resourceId
  if (($all | Measure-Object).Count -eq 0 -and $VaultResourceId) {
    foreach ($vApi in @($Api.RSV_ProtectedItems, '2023-08-01', '2021-12-01') | Select-Object -Unique) {
      foreach ($uri0 in @(
        "https://management.azure.com$VaultResourceId/backupProtectedItems?api-version=$vApi&`$filter=backupManagementType%20eq%20'AzureIaasVM'",
        "https://management.azure.com$VaultResourceId/backupProtectedItems?api-version=$vApi"
      )) {
        $rs = @()
        $uri = $uri0

        do {
          $j = Invoke-ArmGetUri -Uri $uri
          if ($j -and $j.value) { $rs += $j.value }
          $uri = $null
          if ($j -and $j.nextLink) { $uri = $j.nextLink }
        } while ($uri)

        if ($rs.Count -gt 0) {
          $pathCounts.rest += $rs.Count
          foreach ($row in $rs) {
            $p = $row.properties
            if (-not $p) { continue }

            # Keep the protectedItem id for recoveryPoints; adapt shape to cmdlet-like
            $obj = [pscustomobject]@{
              Id         = $row.id
              Properties = [pscustomobject]@{
                SourceResourceId         = $p.sourceResourceId
                PolicyId                 = $p.policyId
                PolicyName               = $p.policyName
                LastBackupTime           = $p.lastBackupTime
                LastSuccessfulBackupTime = $p.lastSuccessfulBackupTime
                LastBackupStatus         = $p.lastBackupStatus
                ProtectionStatus         = $p.protectionStatus
                protectionState          = $p.protectionState
                FriendlyName             = $p.friendlyName
                lastRecoveryPoint        = $p.lastRecoveryPoint
                virtualMachineId         = $p.virtualMachineId
              }
            }
            $all += $obj
          }
          break
        }
      }
      if (($all | Measure-Object).Count -gt 0) { break }
    }
  }

  # Normalize to VM items (covers Enhanced)
  $filtered = @()
  foreach ($it in $all) {
    $p = if ($it.properties) { $it.properties } else { $it.Properties }
    if (-not $p) { continue }

    $isVm = ($p.backupManagementType -eq 'AzureIaasVM' -or
             $p.workloadType -in @('AzureVM','VM') -or
             $p.protectedItemType -match 'IaasVMProtectedItem' -or
             $p.protectedItemType -eq 'Microsoft.Compute/virtualMachines' -or
             $p.virtualMachineId -or
             $p.SourceResourceId)

    if (-not $isVm) { continue }

    if (-not ($it.PSObject.Properties.Name -contains 'Properties')) {
      $it = [pscustomobject]@{ Properties = $p }
    }
    $filtered += $it
  }

  if ($VerboseDiag) {
    Write-Host ("[Diag] VM discovery paths ⇒ direct={0} containers={1} rest={2}" -f `
      $pathCounts.direct, $pathCounts.containers, $pathCounts.rest) -ForegroundColor DarkCyan
  }

  $filtered
}

# Extract RSV posture with **root-first** strategy
function Get-RsvPosture {
  param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroupName,
    [Parameter(Mandatory)][string]$VaultName,
    [Parameter(Mandatory)][string]$VaultId,   # full resourceId
    [string]$Location
  )

  $redundancy = $null
  $csr        = $null
  $cloudState = $null
  $hybridState= $null
  $retention  = $null
  $mua        = $null
  $immutable  = $null
  $softDelCompat = $null
  $secureScore=$null
  $bcdr       = $null
  $pna        = $null
  $peB        = $null
  $peSR       = $null
  $crossRegion= $null

  $root = $null
  try {
    $root = Invoke-ArmGetById -ResourceId $VaultId -ApiVersion $Api.RSV_VaultRoot_New
  } catch {}

  if ($root -and $root.properties) {
    $p = $root.properties
    $sec = $p.securitySettings
    if ($sec) {
      $sds = $sec.softDeleteSettings
      if ($sds) {
        $cloudState  = Normalize-SD ($sds.softDeleteState)
        if (-not $retention)   { $retention   = $sds.softDeleteRetentionPeriodInDays }
        if (-not $hybridState) { $hybridState = $sds.enhancedSecurityState }
      }
      if ($sec.multiUserAuthorization) {
        $mua = ($sec.multiUserAuthorization -match '^(?i)enabled$')
      }
    }

    $red = $p.redundancySettings
    if ($red -and $red.standardTierStorageRedundancy) {
      $redundancy = Normalize-Redundancy $red.standardTierStorageRedundancy
    }
    if ($red -and $red.crossRegionRestore) {
      $crossRegion = $red.crossRegionRestore
    }

    $rs = $p.restoreSettings
    if ($rs -and $rs.crossSubscriptionRestoreSettings -and $rs.crossSubscriptionRestoreSettings.crossSubscriptionRestoreState) {
      $csr = $rs.crossSubscriptionRestoreSettings.crossSubscriptionRestoreState
    }

    $secureScore = $p.secureScore
    $bcdr        = $p.bcdrSecurityLevel
    $pna         = $p.publicNetworkAccess
    $peB         = $p.privateEndpointStateForBackup
    $peSR        = $p.privateEndpointStateForSiteRecovery
    if (-not $redundancy -and $p.storageModelType) {
      $redundancy = Normalize-Redundancy $p.storageModelType
    }
  }

  if (-not $cloudState -and -not $hybridState) {
    $rc = Invoke-ArmGetById -ResourceId $VaultId -SuffixPath '/backupResourceConfig' -ApiVersion $Api.RSV_BackupResourceCfg
    if ($rc -and $rc.properties) {
      $q = $rc.properties
      $cloudState  = Normalize-SD ($q.softDeleteFeatureState ?? $q.softDeleteState)
      $hybridState = $q.enhancedSecurityState
      $retention   = $q.softDeleteRetentionPeriodInDays
      if ($q.isMUAEnabled -ne $null) { $mua = [bool]$q.isMUAEnabled }
      if (-not $immutable) { $immutable = $q.immutabilityState }
      if (-not $csr -and $q.crossSubscriptionRestoreState) { $csr = $q.crossSubscriptionRestoreState }
    }
  }

  foreach ($vApi in @($Api.RSV_BackupConfig, $Api.RSV_BackupConfig_Legacy)) {
    if ($cloudState -and $retention -and $redundancy -and $csr) { break }

    $br = Invoke-ArmGetById -ResourceId $VaultId -SuffixPath '/backupconfig' -ApiVersion $vApi
    if ($br -and $br.properties) {
      if (-not $cloudState) { $cloudState = Normalize-SD ($br.properties.softDeleteFeatureState ?? $br.properties.softDeleteState) }
      if (-not $retention)  { $retention  = $br.properties.softDeleteRetentionPeriodInDays }
      if (-not $csr -and $br.properties.crossSubscriptionRestoreState) { $csr = $br.properties.crossSubscriptionRestoreState }
      if (-not $redundancy -and $br.properties.storageModelType) { $redundancy = Normalize-Redundancy $br.properties.storageModelType }
    }
  }

  if (-not $immutable) { $immutable = 'NotApplicable' }
  $softDeleteStateForLevel = if ($cloudState) { $cloudState } else { $softDelCompat }
  $alwaysOn = To-AlwaysOnBool $cloudState $hybridState
  $enhState = if ($hybridState) {
    $hybridState
  } else {
    if ($mua -eq $true -or $softDeleteStateForLevel -in @('Enabled','AlwaysON')) { 'Enabled' } else { 'Disabled' }
  }
  $secLevel = Derive-SecurityLevel -EnhancedSecurityState $enhState -IsMUAEnabled:([bool]$mua) -SoftDeleteState $softDeleteStateForLevel

  [pscustomobject]@{
    SubscriptionId          = $SubscriptionId
    VaultType               = 'RecoveryServicesVault'
    VaultName               = $VaultName
    ResourceGroup           = $ResourceGroupName
    Location                = $Location
    StorageRedundancy       = $redundancy
    CrossRegionRestore      = $crossRegion
    CrossSubRestore         = $csr
    CloudSoftDeleteState    = $cloudState
    HybridSecurityState     = $hybridState
    SoftDeleteRetentionDays = $retention
    AlwaysOnSoftDelete      = $alwaysOn
    SecurityLevel           = $secLevel
    BcdrSecurityLevel       = $bcdr
    SecureScore             = $secureScore
    PublicNetworkAccess     = $pna
    PeStateBackup           = $peB
    PeStateSiteRecovery     = $peSR
    MUAEnabled              = $mua
    Immutable               = $immutable
  }
}

# ---------------- Iterate subscriptions ----------------
$useExplicitTargets = $TargetSubscriptions -and $TargetSubscriptions.Count -gt 0

$subs = Get-AzSubscription | Where-Object {
  # If explicit targets are defined, only include subs whose Id OR Name is in $TargetSubscriptions
  (-not $useExplicitTargets -or ($TargetSubscriptions -contains $_.Id -or $TargetSubscriptions -contains $_.Name)) -and
  (-not $SubIncludeNameRegex -or $_.Name -match $SubIncludeNameRegex) -and
  (-not $SubIncludeIdRegex   -or $_.Id   -match $SubIncludeIdRegex)
} | Sort-Object Name

foreach ($sub in $subs) {
  Set-AzContext -SubscriptionId $sub.Id | Out-Null

  # Ensure providers registered (idempotent)
  foreach ($ns in 'Microsoft.RecoveryServices','Microsoft.DataProtection') {
    try {
      $reg = Get-AzResourceProvider -ProviderNamespace $ns -ErrorAction SilentlyContinue
      if ($reg.RegistrationState -ne 'Registered') {
        Register-AzResourceProvider -ProviderNamespace $ns | Out-Null
      }
    } catch {}
  }

  # =========================
  # Recovery Services Vaults
  # =========================
  $rsvs = @()
  try {
    $rsvs = Get-AzRecoveryServicesVault -ErrorAction SilentlyContinue
  } catch {}

  foreach ($v in $rsvs) {
    try {
      Set-AzRecoveryServicesVaultContext -Vault $v -ErrorAction Stop
    } catch {}

    # Posture (root-first)
    $r = Get-RsvPosture -SubscriptionId $sub.Id `
                        -ResourceGroupName $v.ResourceGroupName `
                        -VaultName $v.Name `
                        -VaultId $v.Id `
                        -Location $v.Location

    $rsvReport.Add([pscustomobject]@{
      SubscriptionName        = $sub.Name
      SubscriptionId          = $r.SubscriptionId
      VaultType               = $r.VaultType
      VaultName               = $r.VaultName
      ResourceGroup           = $r.ResourceGroup
      Location                = $r.Location
      StorageRedundancy       = $r.StorageRedundancy
      CrossRegionRestore      = $r.CrossRegionRestore
      CrossSubRestore         = $r.CrossSubRestore
      CloudSoftDeleteState    = $r.CloudSoftDeleteState
      HybridSecurityState     = $r.HybridSecurityState
      SoftDeleteRetentionDays = $r.SoftDeleteRetentionDays
      AlwaysOnSoftDelete      = $r.AlwaysOnSoftDelete
      SecurityLevel           = $r.SecurityLevel
      BcdrSecurityLevel       = $r.BcdrSecurityLevel
      SecureScore             = $r.SecureScore
      PublicNetworkAccess     = $r.PublicNetworkAccess
      PeStateBackup           = $r.PeStateBackup
      PeStateSiteRecovery     = $r.PeStateSiteRecovery
      MUAEnabled              = $r.MUAEnabled
      Immutable               = $r.Immutable
    })

    # Findings for vault config
    if (-not $r.AlwaysOnSoftDelete) {
      $findings.Add([pscustomobject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Vault Configuration'
        ResourceType     = 'Microsoft.RecoveryServices/vaults'
        ResourceName     = $r.VaultName
        ResourceGroup    = $r.ResourceGroup
        Detail           = "Always-On soft delete is not enabled on Recovery Services vault."
      })
    }
    if ($r.SecurityLevel -ne 'Enhanced') {
      $findings.Add([pscustomobject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Vault Configuration'
        ResourceType     = 'Microsoft.RecoveryServices/vaults'
        ResourceName     = $r.VaultName
        ResourceGroup    = $r.ResourceGroup
        Detail           = "Vault security level is not Enhanced."
      })
    }

    # ---- Backed-up VM items (robust discovery) ----
    $vmItemsAll = Get-RsvVmItems -VaultResourceId $v.Id -VerboseDiag:$Diagnostics
    if ($Diagnostics) {
      Write-Host ("[Diag] RSV '{0}': found {1} VM item(s)" -f $v.Name, ($vmItemsAll | Measure-Object | Select-Object -ExpandProperty Count)) -ForegroundColor DarkCyan
    }

    # Preload VM policies for this vault (name => object)
    $vmPoliciesByName = @{}
    try {
      $vmPolicies = Get-AzRecoveryServicesBackupProtectionPolicy -WorkloadType AzureVM -ErrorAction SilentlyContinue
      foreach ($pp in ($vmPolicies | Where-Object { $_ })) {
        $vmPoliciesByName[$pp.Name] = $pp
      }
    } catch {}

    foreach ($it in $vmItemsAll) {
      # ResourceId extraction
      $rid = $it.Properties.SourceResourceId
      if (-not $rid) { $rid = $it.Properties.sourceResourceId }
      if (-not $rid) { $rid = $it.Properties.virtualMachineId }
      if (-not $rid) { $rid = $it.Properties.VirtualMachineId }
      if (-not $rid) { $rid = $it.Properties.dataSourceId }
      if (-not $rid) { $rid = $it.Properties.datasourceId }
      if ($rid) { $null = $protectedIds.Add($rid) }

      $ridInfo = Split-VmFromResourceId -ResourceId $rid
      $vmName  = if ($ridInfo) { $ridInfo.VMName } else { $it.Properties.FriendlyName }
      $vmRG    = if ($ridInfo) { $ridInfo.ResourceGroup } else { $null }

      # Policy lookup (cmdlet cache → REST by policyId)
      $policyName = $it.Properties.PolicyName
      $policyObj  = $null

      if ($policyName -and $vmPoliciesByName.ContainsKey($policyName)) {
        $policyObj = $vmPoliciesByName[$policyName]
      } else {
        $policyId = $it.Properties.PolicyId
        if (-not $policyId) { $policyId = $it.Properties.policyId }
        if ($policyId) {
          foreach ($vPol in '2023-02-01','2021-12-01') {
            try {
              $policyObj = Invoke-ArmGetById -ResourceId $policyId -ApiVersion $vPol
              if ($policyObj -and $policyObj.properties) { break }
            } catch {}
          }
        }
      }

      # Configured cadence/window (policy) — fallback to recovery points
      $sched = Get-PolicyScheduleInfo -Policy $policyObj
      $cadenceTs   = $sched.CadenceTS
      $cadenceText = $sched.CadenceText
      $windowTs    = $sched.WindowTS
      $windowText  = $sched.WindowText
      $rpoSource   = $null

      if ($cadenceTs) {
        $rpoSource = 'Policy'
      } elseif ($it.Id) {
        $cadenceTs = Get-RpoFromRecoveryPoints -ProtectedItemId $it.Id
        if ($cadenceTs) {
          $cadenceText = Format-TimeSpanText $cadenceTs
          $rpoSource   = 'RecoveryPoints'
        }
      }

      # Last successful backup time (Enhanced may expose lastRecoveryPoint)
      $lastOk = $null
      try {
        if     ($it.Properties.LastSuccessfulBackupTime) { $lastOk = [datetime]$it.Properties.LastSuccessfulBackupTime }
        elseif ($it.Properties.LastBackupTime)           { $lastOk = [datetime]$it.Properties.LastBackupTime }
        elseif ($it.Properties.lastRecoveryPoint)        { $lastOk = [datetime]$it.Properties.lastRecoveryPoint }
      } catch {}

      $observedRpoHrs = if ($lastOk) { [math]::Round(((Get-Date) - $lastOk).TotalHours,2) } else { $null }

      # Capture protection state + status
      $protState = $it.Properties.ProtectionState
      if (-not $protState) { $protState = $it.Properties.protectionState }

      # Findings for VM RPO (if we can see it)
      if ($observedRpoHrs -ne $null) {
        $severity = $null
        if ($observedRpoHrs -ge $VmRpoCriticalHours) {
          $severity = 'High'
        } elseif ($observedRpoHrs -ge $VmRpoWarningHours) {
          $severity = 'Medium'
        }
        if ($severity) {
          $findings.Add([pscustomobject]@{
            SubscriptionName = $sub.Name
            SubscriptionId   = $sub.Id
            Severity         = $severity
            Category         = 'RPO'
            ResourceType     = 'Microsoft.Compute/virtualMachines'
            ResourceName     = $vmName
            ResourceGroup    = $vmRG
            Detail           = "Observed VM RPO is ${observedRpoHrs}h, above the $severity threshold."
          })
        }
      }

      $vmDetailReport.Add([pscustomobject]@{
        SubscriptionName       = $sub.Name
        SubscriptionId         = $sub.Id
        VaultName              = $v.Name
        VaultResourceGroup     = $v.ResourceGroupName
        VMName                 = $vmName
        VMResourceGroup        = $vmRG
        Location               = $v.Location
        PolicyName             = $policyName
        ConfiguredCadence      = $cadenceText
        ConfiguredWindow       = $windowText
        LastBackupTime         = $lastOk
        LastBackupStatus       = $it.Properties.LastBackupStatus
        ObservedRPOHours       = $observedRpoHrs
        RpoSource              = $rpoSource
        ProtectionStatus       = $it.Properties.ProtectionStatus
        ProtectionState        = $protState
      })
    }

    # --- SQL in VM (workload) — detailed cadence + RPO (per database) ---
    try {
      $awContainers = Get-AzRecoveryServicesBackupContainer -ContainerType AzureWorkload -Status Registered -ErrorAction SilentlyContinue
      foreach ($aw in $awContainers) {
        $sqlItems = Get-AzRecoveryServicesBackupItem -Container $aw -WorkloadType MSSQL -ErrorAction SilentlyContinue
        foreach ($it in ($sqlItems | Where-Object { $_ })) {
          $p = $it.Properties ?? $it.properties

          # Policy lookup (name → cmdlet; else policyId via REST)
          $polObj  = $null
          $polName = $p.PolicyName ?? $p.policyName

          if ($polName) {
            try {
              $polObj = Get-AzRecoveryServicesBackupProtectionPolicy -WorkloadType MSSQL -Name $polName -ErrorAction SilentlyContinue
            } catch {}
          }

          if (-not $polObj) {
            $polId = $p.PolicyId ?? $p.policyId
            if ($polId) {
              foreach ($vPol in '2023-02-01','2021-12-01') {
                try {
                  $polObj = Invoke-AzRestMethod -Uri ("https://management.azure.com{0}?api-version={1}" -f $polId,$vPol) -Method GET -ErrorAction Stop
                  if ($polObj.Content) {
                    $polObj = $polObj.Content | ConvertFrom-Json
                    break
                  }
                } catch {}
              }
            }
          }

          # Configured cadence/window (policy) — tenant-agnostic
          $sched   = Get-SqlVmPolicyScheduleInfo -Policy $polObj
          $fullCad = $sched.FullCadenceText
          $diffCad = $sched.DiffCadenceText
          $logCad  = $sched.LogCadenceText
          $winTxt  = $sched.WindowText

          # Observed RPO from workload recovery points (prefer log)
          $obsRpo = $null
          if ($it.Id) {
            $obsRpo = Get-SqlVmObservedRpo -ProtectedItemId $it.Id -ApiVersion $Api.RSV_RecoveryPoints
          }

          # Findings for SQL in VM RPO
          if ($obsRpo -ne $null) {
            $severity = $null
            if ($obsRpo -ge $SqlRpoCriticalHours) {
              $severity = 'High'
            } elseif ($obsRpo -ge $SqlRpoWarningHours) {
              $severity = 'Medium'
            }
            if ($severity) {
              $findings.Add([pscustomobject]@{
                SubscriptionName = $sub.Name
                SubscriptionId   = $sub.Id
                Severity         = $severity
                Category         = 'RPO'
                ResourceType     = 'MSSQL in VM'
                ResourceName     = $p.FriendlyName
                ResourceGroup    = $v.ResourceGroupName
                Detail           = "Observed SQL-in-VM RPO is ${obsRpo}h, above the $severity threshold."
              })
            }
          }

          # Last backup time/status if present
          $lastOk = $null
          try {
            if     ($p.LastSuccessfulBackupTime) { $lastOk = [datetime]$p.LastSuccessfulBackupTime }
            elseif ($p.LastBackupTime)           { $lastOk = [datetime]$p.LastBackupTime }
          } catch {}
          $lastStatus = $p.LastBackupStatus

          $sqlDetailReport.Add([pscustomobject]@{
            SubscriptionName      = $sub.Name
            VaultName             = $v.Name
            Workload              = 'SQL in VM'
            ServerOrInstance      = $p.ContainerName
            DatabaseName          = $p.FriendlyName
            PolicyName            = $polName
            ConfiguredCadenceFull = $fullCad
            ConfiguredCadenceDiff = $diffCad
            ConfiguredCadenceLog  = $logCad
            ConfiguredWindow      = $winTxt
            LastBackupTime        = $lastOk
            LastBackupStatus      = $lastStatus
            ObservedRPOHours      = $obsRpo
            RpoSource             = if ($logCad -or $diffCad -or $fullCad) { 'Policy' } else { if ($obsRpo -ne $null) { 'RecoveryPoints' } else { $null } }
            ProtectionState       = $p.ProtectionState ?? $p.protectionState
          })
        }
      }
    } catch {}
  }

  # ==============
  # Backup Vaults
  # ==============
  $bvs = @()
  try {
    $bvs = Get-AzDataProtectionBackupVault -ErrorAction SilentlyContinue
  } catch {}

  if (-not $bvs -or $bvs.Count -eq 0) {
    try {
      $bvs = Get-AzResource -ResourceType 'Microsoft.DataProtection/backupVaults' -ErrorAction SilentlyContinue |
        ForEach-Object {
          [pscustomobject]@{
            Name              = $_.Name
            ResourceGroupName = $_.ResourceGroupName
            Location          = $_.Location
            Id                = $_.ResourceId
            Properties        = $_.Properties
          }
        }
    } catch {}
  }

  foreach ($bv in $bvs) {
    $rgName = $bv.ResourceGroupName
    if ([string]::IsNullOrWhiteSpace($rgName)) {
      $rgName = Get-ResourceGroupFromId -ResourceId $bv.Id
      if ($Diagnostics -and [string]::IsNullOrWhiteSpace($rgName)) {
        Write-Warning "Skipping Backup Vault '$($bv.Name)' (could not determine Resource Group from Id '$($bv.Id)')"
        continue
      }
    }

    $bvName = $bv.Name
    $restBV = Invoke-ArmGetById -ResourceId $bv.Id -ApiVersion $Api.DP_BackupVault

    $sec = $null
    $stor= $null

    if ($restBV -and $restBV.properties) {
      $sec  = $restBV.properties.securitySettings
      $stor = $restBV.properties.storageSettings
    } elseif ($bv.Properties) {
      $sec  = $bv.Properties.SecuritySettings
      $stor = $bv.Properties.StorageSettings
    }

    # storageSettings → redundancy
    $redundancy = $null
    if ($stor) {
      if ($stor -is [System.Collections.IEnumerable] -and -not ($stor -is [string])) {
        $vaultStore = $stor | Where-Object { $_.datastoreType -eq 'VaultStore' -or $_.DataStoreType -eq 'VaultStore' } | Select-Object -First 1
        if (-not $vaultStore) {
          $vaultStore = $stor | Select-Object -First 1
        }
        if ($vaultStore) {
          if     ($vaultStore.redundancy)         { $redundancy = $vaultStore.redundancy }
          elseif ($vaultStore.type)               { $redundancy = $vaultStore.type }
          elseif ($vaultStore.ReplicationSetting) { $redundancy = $vaultStore.ReplicationSetting }
        }
      } else {
        if     ($stor.redundancy)             { $redundancy = $stor.redundancy }
        elseif ($stor.type)                   { $redundancy = $stor.type }
        elseif ($stor.ReplicationSetting)     { $redundancy = $stor.ReplicationSetting }
      }
    }
    $redundancy = Normalize-Redundancy -Value $redundancy

    # securitySettings (nested + legacy shapes)
    $cloudState  = $null
    $hybridState = $null
    $retention   = $null
    $mua         = $null
    $immutable   = $null
    $csr         = $null

    if ($sec) {
      if     ($sec.softDeleteState)     { $cloudState = $sec.softDeleteState }
      elseif ($sec.SoftDeleteState)     { $cloudState = $sec.SoftDeleteState }

      if (-not $cloudState) {
        $sds = $sec.softDeleteSettings
        if (-not $sds) { $sds = $sec.SoftDeleteSettings }

        if ($sds) {
          $sdState = $sds.state
          if ($sdState) {
            if     ($sdState -match '^(?i)on$')  { $cloudState = 'Enabled' }
            elseif ($sdState -match '^(?i)off$') { $cloudState = 'Disabled' }
            else { $cloudState = $sdState }
          }
          if (-not $retention) {
            $r = $sds.retentionDurationInDays
            if (-not $r) { $r = $sds.RetentionDurationInDays }
            if ($r) { $retention = [int]$r }
          }
        }
      }

      if     ($sec.immutabilityState)     { $immutable = $sec.immutabilityState }
      elseif ($sec.ImmutabilityState)     { $immutable = $sec.ImmutabilityState }
      else {
        $ims = $sec.immutabilitySettings
        if (-not $ims) { $ims = $sec.ImmutabilitySettings }
        if ($ims -and $ims.state) { $immutable = $ims.state }
      }

      if     ($sec.isMUAEnabled -ne $null) { $mua = [bool]$sec.isMUAEnabled }
      elseif ($sec.IsMUAEnabled -ne $null) { $mua = [bool]$sec.IsMUAEnabled }
      else {
        $muaSettings = $sec.multiUserAuthorizationSettings
        if (-not $muaSettings) { $muaSettings = $sec.MultiUserAuthorizationSettings }
        if ($muaSettings -and $muaSettings.state) {
          $mua = ($muaSettings.state -match '^(?i)enabled|on$')
        }
      }

      if     ($sec.enhancedSecurityState) { $hybridState = $sec.enhancedSecurityState }
      elseif ($sec.EnhancedSecurityState) { $hybridState = $sec.EnhancedSecurityState }

      if     ($sec.crossSubscriptionRestoreState) { $csr = $sec.crossSubscriptionRestoreState }
      elseif ($sec.CrossSubscriptionRestoreState) { $csr = $sec.CrossSubscriptionRestoreState }

      if (-not $csr -and $restBV -and $restBV.properties -and $restBV.properties.featureSettings) {
        $fs = $restBV.properties.featureSettings
        $csrSet = $fs.crossSubscriptionRestoreSettings
        if (-not $csrSet) { $csrSet = $fs.CrossSubscriptionRestoreSettings }
        if ($csrSet -and $csrSet.state) { $csr = $csrSet.state }
      }
    }

    $alwaysOn = To-AlwaysOnBool $cloudState $hybridState
    $enhState = if ($hybridState -in @('Enabled','AlwaysON') -or $mua -eq $true -or $cloudState -in @('Enabled','AlwaysON')) {
      'Enabled'
    } else {
      'Disabled'
    }
    $secLevel = Derive-SecurityLevel -EnhancedSecurityState $enhState -IsMUAEnabled:([bool]$mua) -SoftDeleteState $cloudState

    $bvReport.Add([pscustomobject]@{
      SubscriptionName        = $sub.Name
      SubscriptionId          = $sub.Id
      VaultType               = 'BackupVault'
      VaultName               = $bvName
      ResourceGroup           = $rgName
      Location                = $bv.Location
      StorageRedundancy       = $redundancy
      CrossSubRestore         = $csr
      CloudSoftDeleteState    = $cloudState
      HybridSecurityState     = $hybridState
      SoftDeleteRetentionDays = $retention
      AlwaysOnSoftDelete      = $alwaysOn
      SecurityLevel           = $secLevel
      MUAEnabled              = $mua
      Immutable               = $immutable
    })

    # Findings for Backup Vault config
    if (-not $alwaysOn) {
      $findings.Add([pscustomobject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Vault Configuration'
        ResourceType     = 'Microsoft.DataProtection/backupVaults'
        ResourceName     = $bvName
        ResourceGroup    = $rgName
        Detail           = "Always-On soft delete is not effectively enabled on Backup Vault."
      })
    }
    if ($secLevel -ne 'Enhanced') {
      $findings.Add([pscustomobject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Vault Configuration'
        ResourceType     = 'Microsoft.DataProtection/backupVaults'
        ResourceName     = $bvName
        ResourceGroup    = $rgName
        Detail           = "Backup Vault security level is not Enhanced."
      })
    }

    # Backup Instances (coverage map + protectedIds)
    $instances = @()
    try {
      $instances = Get-AzDataProtectionBackupInstance -ResourceGroupName $rgName -VaultName $bvName -ErrorAction SilentlyContinue
    } catch {}

    if (-not $instances -or $instances.Count -eq 0) {
      $instances = Get-AllBackupInstances -SubscriptionId $sub.Id -ResourceGroupName $rgName -VaultName $bvName -ApiVersion $Api.DP_BackupInstances
    }

    foreach ($bi in $instances) {
      $props = if ($bi.PSObject.Properties.Name -contains 'Properties') { $bi.Properties } else { $bi.properties }
      $dsi   = if ($props) {
        if ($props.PSObject.Properties.Name -contains 'DataSourceInfo') { $props.DataSourceInfo } else { $props.dataSourceInfo }
      }

      if ($dsi) {
        $dsId   = if ($dsi.ResourceId) { $dsi.ResourceId } else { $dsi.resourceId }
        if ($dsId) { $null = $protectedIds.Add($dsId) }

        # Optional: add known non-VMs to DB coverage
        $dsType = if ($dsi.DataSourceType) { $dsi.DataSourceType } else { $dsi.dataSourceType }
        $name   = if ($dsi.ResourceName)   { $dsi.ResourceName }   else { $dsi.resourceName }

        switch -Regex ($dsType) {
          'PostgreSQL' {
            $dbReport.Add([pscustomobject]@{
              SubscriptionName=$sub.Name; SubscriptionId=$sub.Id
              DbType='PostgreSQL Flexible Server'; ServerOrInstance=$name; DatabaseName='* (server-level)'
              IsBackedUp=$true; Method='Backup Vault'; VaultName=$bvName; Location=$bv.Location
            })
          }
          'AzureFileShare' {
            $dbReport.Add([pscustomobject]@{
              SubscriptionName=$sub.Name; SubscriptionId=$sub.Id
              DbType='Azure Files'; ServerOrInstance=$name; DatabaseName='Share'
              IsBackedUp=$true; Method='Backup Vault'; VaultName=$bvName; Location=$bv.Location
            })
          }
        }
      }
    }
  }

  # ============================
  # VM backup coverage (all VMs)
  # ============================
  $vms = @()
  try {
    $vms = Get-AzVM -Status -ErrorAction SilentlyContinue
  } catch {}

  foreach ($vm in $vms) {
    $isProtected = $protectedIds.Contains($vm.Id)
    $ps = $vm.PowerState
    if (-not $ps) { $ps = 'Unknown' } else { $ps = ($ps -replace '^VM ','') }

    $vmReport.Add([pscustomobject]@{
      SubscriptionName=$sub.Name
      SubscriptionId=$sub.Id
      VMName=$vm.Name
      ResourceGroup=$vm.ResourceGroupName
      Location=$vm.Location
      PowerState=$ps
      IsBackedUp=$isProtected
      Method = if ($isProtected) { 'RSV or BV' } else { 'Not protected' }
    })

    # Finding: unprotected, non-Unknown powerstate VM
    if (-not $isProtected -and $ps -ne 'Unknown') {
      $findings.Add([pscustomobject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'VM Coverage'
        ResourceType     = 'Microsoft.Compute/virtualMachines'
        ResourceName     = $vm.Name
        ResourceGroup    = $vm.ResourceGroupName
        Detail           = "VM is not protected by any backup vault."
      })
    }
  }

  # ==================================================
  # Azure SQL DB (PaaS) — observed RPO from PITR
  # ==================================================
  $sqlServers = @()
  try {
    $sqlServers = Get-AzSqlServer -ErrorAction SilentlyContinue
  } catch {}

  foreach ($srv in $sqlServers) {
    $dbs = @()
    try {
      $dbs = Get-AzSqlDatabase -ResourceGroupName $srv.ResourceGroupName -ServerName $srv.ServerName -ErrorAction SilentlyContinue
    } catch {}

    foreach ($db in $dbs | Where-Object { $_.DatabaseName -ne 'master' }) {
      $obsRpo = Get-AzureSqlPitrObservedRpo -ResourceGroupName $srv.ResourceGroupName -ServerName $srv.ServerName -DatabaseName $db.DatabaseName
      $hasLTR = $false

      try {
        $ltr = Get-AzSqlDatabaseLongTermRetentionPolicy `
                -ResourceGroupName $srv.ResourceGroupName `
                -ServerName $srv.ServerName `
                -DatabaseName $db.DatabaseName `
                -ErrorAction SilentlyContinue
        if ($ltr -and $ltr.MonthlyRetention) { $hasLTR = $true }
      } catch {}

      # Coverage entry
      $dbReport.Add([pscustomobject]@{
        SubscriptionName=$sub.Name
        SubscriptionId=$sub.Id
        DbType='Azure SQL (PaaS)'
        ServerOrInstance=$srv.ServerName
        DatabaseName=$db.DatabaseName
        IsBackedUp=$true
        Method = if ($hasLTR) { 'Native PITR + LTR' } else { 'Native PITR only' }
        VaultName='-'
        Location=$db.Location
      })

      # SQL detail entry
      $sqlDetailReport.Add([pscustomobject]@{
        SubscriptionName      = $sub.Name
        VaultName             = '-'
        Workload              = 'Azure SQL (PaaS)'
        ServerOrInstance      = $srv.ServerName
        DatabaseName          = $db.DatabaseName
        PolicyName            = if ($hasLTR) { 'Native PITR + LTR' } else { 'Native PITR only' }
        ConfiguredCadenceFull = $null
        ConfiguredCadenceDiff = $null
        ConfiguredCadenceLog  = $null
        ConfiguredWindow      = $null
        LastBackupTime        = $null
        LastBackupStatus      = $null
        ObservedRPOHours      = $obsRpo
        RpoSource             = if ($obsRpo -ne $null) { 'PITR (platform)' } else { $null }
        ProtectionState       = 'PlatformManaged'
      })

      # Findings for Azure SQL RPO
      if ($obsRpo -ne $null) {
        $severity = $null
        if ($obsRpo -ge $SqlRpoCriticalHours) {
          $severity = 'High'
        } elseif ($obsRpo -ge $SqlRpoWarningHours) {
          $severity = 'Medium'
        }
        if ($severity) {
          $findings.Add([pscustomobject]@{
            SubscriptionName = $sub.Name
            SubscriptionId   = $sub.Id
            Severity         = $severity
            Category         = 'RPO'
            ResourceType     = 'Azure SQL (PaaS)'
            ResourceName     = $db.DatabaseName
            ResourceGroup    = $srv.ResourceGroupName
            Detail           = "Observed Azure SQL RPO is ${obsRpo}h, above the $severity threshold."
          })
        }
      }
    }
  }
}

# ===========================
# FINAL OUTPUT (all at bottom)
# ===========================
function Print-Section($title, $data, $columns, $sortBy) {
  Write-Host "`n=== $title ===" -ForegroundColor Cyan
  if ($data.Count -gt 0) {
    $data | Sort-Object $sortBy | Format-Table $columns -AutoSize
  } else {
    Write-Host "No $title found."
  }
}

Print-Section "Recovery Services Vaults" $rsvReport @(
  'SubscriptionName','VaultName','ResourceGroup','Location',
  'StorageRedundancy','CrossRegionRestore','CrossSubRestore',
  'CloudSoftDeleteState','HybridSecurityState','SoftDeleteRetentionDays','AlwaysOnSoftDelete',
  'SecurityLevel','BcdrSecurityLevel','SecureScore','PublicNetworkAccess','MUAEnabled','Immutable'
) @('SubscriptionName','ResourceGroup','VaultName')

Print-Section "Backup Vaults" $bvReport @(
  'SubscriptionName','VaultName','ResourceGroup','Location',
  'StorageRedundancy','CrossSubRestore',
  'CloudSoftDeleteState','HybridSecurityState','SoftDeleteRetentionDays','AlwaysOnSoftDelete',
  'SecurityLevel','MUAEnabled','Immutable'
) @('SubscriptionName','ResourceGroup','VaultName')

Print-Section "Virtual Machines – Backup Coverage" $vmReport @(
  'SubscriptionName','VMName','ResourceGroup','Location','PowerState','IsBackedUp','Method'
) @('SubscriptionName','ResourceGroup','VMName')

Print-Section "Backed-up VMs — Cadence & RPO" $vmDetailReport @(
  'SubscriptionName','VaultName','VMName','VMResourceGroup','PolicyName',
  'ConfiguredCadence','ConfiguredWindow','LastBackupTime','LastBackupStatus',
  'ObservedRPOHours','RpoSource','ProtectionState'
) @('SubscriptionName','VaultName','VMResourceGroup','VMName')

Print-Section "SQL — Cadence & RPO" $sqlDetailReport @(
  'SubscriptionName','VaultName','Workload','ServerOrInstance','DatabaseName','PolicyName',
  'ConfiguredCadenceFull','ConfiguredCadenceDiff','ConfiguredCadenceLog','ConfiguredWindow',
  'LastBackupTime','LastBackupStatus','ObservedRPOHours','RpoSource','ProtectionState'
) @('SubscriptionName','VaultName','Workload','ServerOrInstance','DatabaseName')

# ======== QUICK SUMMARY ========
Write-Host "`n=== Summary ===" -ForegroundColor Yellow

$vaultsTotal = $rsvReport.Count + $bvReport.Count
$vmTotal     = $vmReport.Count
$vmProtected = ($vmReport | Where-Object { $_.IsBackedUp }).Count
$vmPct       = if ($vmTotal) { [math]::Round(100*$vmProtected/$vmTotal, 1) } else { 0 }

$dbTotal     = $dbReport.Count
$dbProtected = ($dbReport | Where-Object { $_.IsBackedUp }).Count
$dbPct       = if ($dbTotal) { [math]::Round(100*$dbProtected/$dbTotal,1) } else { 0 }

$alwaysOnVaults = @($rsvReport + $bvReport) | Where-Object { $_.AlwaysOnSoftDelete -eq $true }
$enhancedVaults = @($rsvReport + $bvReport) | Where-Object { $_.SecurityLevel -eq 'Enhanced' }

"{0,-30} {1}" -f "Vaults (total):", $vaultsTotal
"{0,-30} {1}" -f "  RSVs:", $rsvReport.Count
"{0,-30} {1}" -f "  Backup Vaults:", $bvReport.Count
"{0,-30} {1}" -f "Vaults with Always-On SD:", ($alwaysOnVaults | Measure-Object | Select-Object -ExpandProperty Count)
"{0,-30} {1}" -f "Vaults with Enhanced Sec:", ($enhancedVaults | Measure-Object | Select-Object -ExpandProperty Count)
"{0,-30} {1}" -f "VMs (total):", $vmTotal
"{0,-30} {1}" -f "VMs protected:", "$vmProtected ($vmPct`%)"
"{0,-30} {1}" -f "Databases/Services (total):", $dbTotal
"{0,-30} {1}" -f "Databases protected:", "$dbProtected ($dbPct`%)"

# ------------ Multi-sheet Excel export ------------
$ExportXlsx = $true
$XlsxPath   = Join-Path $OutPath 'Backup_Audit.xlsx'

if ($ExportXlsx) {
  # Ensure ImportExcel is available (no Excel app required)
  if (-not (Get-Module -ListAvailable -Name ImportExcel)) {
    try {
      if (-not (Get-PSRepository -Name PSGallery -ErrorAction SilentlyContinue)) {
        Register-PSRepository -Default
      }
      Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue | Out-Null
      Install-Module ImportExcel -Scope CurrentUser -Force -ErrorAction Stop
    } catch {
      Write-Warning "Could not install ImportExcel automatically. Please run: Set-PSRepository PSGallery -InstallationPolicy Trusted; Install-Module ImportExcel -Scope CurrentUser"
      throw
    }
  }
  Import-Module ImportExcel -ErrorAction Stop

  # Helper for consistent export (no AutoSize for CloudShell)
  function Export-Sheet {
    param(
      [Parameter(Mandatory)] [object]$Data,
      [Parameter(Mandatory)] [string]$WorksheetName,
      [Parameter()] [string]$TableName = $WorksheetName,
      [Parameter()] [string[]]$Columns = $null,
      [switch]$FirstSheet
    )

    $pipe = $Data
    if ($Columns) { $pipe = $Data | Select-Object $Columns }

    $common = @{
      Path               = $XlsxPath
      WorksheetName      = $WorksheetName
      TableName          = $TableName
      TableStyle         = 'Medium2'
      FreezeTopRow       = $true
      BoldTopRow         = $true
      NoNumberConversion = @('VMName','VaultName','PolicyName','ServerOrInstance','DatabaseName','ResourceGroup')
    }
    if ($FirstSheet) { $common.ClearSheet = $true }  # wipe only once

    $pipe | Export-Excel @common
  }

  # ===== Sheets =====

  # 1) RSV posture
  $rsvCols = @(
    'SubscriptionName','SubscriptionId','VaultType','VaultName','ResourceGroup','Location',
    'StorageRedundancy','CrossRegionRestore','CrossSubRestore',
    'CloudSoftDeleteState','HybridSecurityState','SoftDeleteRetentionDays','AlwaysOnSoftDelete',
    'SecurityLevel','BcdrSecurityLevel','SecureScore','PublicNetworkAccess',
    'PeStateBackup','PeStateSiteRecovery','MUAEnabled','Immutable'
  )
  Export-Sheet -Data $rsvReport -WorksheetName 'RSV' -TableName 'RSV' -Columns $rsvCols -FirstSheet

  # 2) Backup Vault posture
  $bvCols = @(
    'SubscriptionName','SubscriptionId','VaultType','VaultName','ResourceGroup','Location',
    'StorageRedundancy','CrossSubRestore',
    'CloudSoftDeleteState','HybridSecurityState','SoftDeleteRetentionDays','AlwaysOnSoftDelete',
    'SecurityLevel','MUAEnabled','Immutable'
  )
  Export-Sheet -Data $bvReport -WorksheetName 'BackupVaults' -TableName 'BackupVaults' -Columns $bvCols

  # 3) VM Coverage
  $vmCovCols = @('SubscriptionName','SubscriptionId','VMName','ResourceGroup','Location','PowerState','IsBackedUp','Method')
  Export-Sheet -Data $vmReport -WorksheetName 'VM_Coverage' -TableName 'VM_Coverage' -Columns $vmCovCols

  # 4) VM Details (Cadence & RPO)
  $vmDetCols = @(
    'SubscriptionName','SubscriptionId','VaultName','VaultResourceGroup','Location',
    'VMName','VMResourceGroup','PolicyName','ConfiguredCadence','ConfiguredWindow',
    'LastBackupTime','LastBackupStatus','ObservedRPOHours','RpoSource',
    'ProtectionStatus','ProtectionState'
  )
  Export-Sheet -Data $vmDetailReport -WorksheetName 'VM_Detail' -TableName 'VM_Detail' -Columns $vmDetCols

  # 5) SQL (RSV workload + PaaS) Details
  $sqlDetCols = @(
    'SubscriptionName','VaultName','Workload','ServerOrInstance','DatabaseName','PolicyName',
    'ConfiguredCadenceFull','ConfiguredCadenceDiff','ConfiguredCadenceLog','ConfiguredWindow',
    'LastBackupTime','LastBackupStatus','ObservedRPOHours','RpoSource','ProtectionState'
  )
  Export-Sheet -Data $sqlDetailReport -WorksheetName 'SQL_Detail' -TableName 'SQL_Detail' -Columns $sqlDetCols

  # 6) DB Coverage
  $dbCovCols = @('SubscriptionName','SubscriptionId','DbType','ServerOrInstance','DatabaseName','IsBackedUp','Method','VaultName','Location')
  Export-Sheet -Data $dbReport -WorksheetName 'DB_Coverage' -TableName 'DB_Coverage' -Columns $dbCovCols

  # 7) Findings (gaps / risks)
  if ($findings.Count -gt 0) {
    $findingCols = @('SubscriptionName','SubscriptionId','Severity','Category','ResourceType','ResourceName','ResourceGroup','Detail')
    Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings' -Columns $findingCols
  }

  # 8) Subscription summary / protection score
  $subscriptionSummary = @()

  $subsById = @($vmReport + $dbReport + $rsvReport + $bvReport) |
    Where-Object { $_ -and $_.SubscriptionId } |
    Select-Object -ExpandProperty SubscriptionId -Unique

  foreach ($sid in $subsById) {
    $name = (
      @($vmReport + $dbReport + $rsvReport + $bvReport) |
        Where-Object { $_.SubscriptionId -eq $sid } |
        Select-Object -First 1
    ).SubscriptionName

    $vmsSub    = $vmReport  | Where-Object SubscriptionId -eq $sid
    $dbsSub    = $dbReport  | Where-Object SubscriptionId -eq $sid
    $findSub   = $findings  | Where-Object SubscriptionId -eq $sid
    $vaultsSub = @($rsvReport + $bvReport) | Where-Object SubscriptionId -eq $sid

    $vmTotal   = $vmsSub.Count
    $vmProt    = ($vmsSub | Where-Object IsBackedUp).Count
    $vmPct     = if ($vmTotal) { [math]::Round(100*$vmProt/$vmTotal,1) } else { 0 }

    $dbTotal   = $dbsSub.Count
    $dbProt    = ($dbsSub | Where-Object IsBackedUp).Count
    $dbPct     = if ($dbTotal) { [math]::Round(100*$dbProt/$dbTotal,1) } else { 0 }

    $highFind  = ($findSub | Where-Object Severity -eq 'High').Count
    $medFind   = ($findSub | Where-Object Severity -eq 'Medium').Count

    # Simple score: average of VM% & DB% minus penalty for findings
    $baseScore = [math]::Round(($vmPct + $dbPct)/2,1)
    $score     = [math]::Max(0, $baseScore - (5 * $highFind) - (2 * $medFind))

    $subscriptionSummary += [pscustomobject]@{
      SubscriptionName = $name
      SubscriptionId   = $sid
      VMTotal          = $vmTotal
      VMProtected      = $vmProt
      VMProtectedPct   = $vmPct
      DbTotal          = $dbTotal
      DbProtected      = $dbProt
      DbProtectedPct   = $dbPct
      VaultCount       = $vaultsSub.Count
      HighFindings     = $highFind
      MediumFindings   = $medFind
      ProtectionScore  = $score
    }
  }

  if ($subscriptionSummary.Count -gt 0) {
    Export-Sheet -Data $subscriptionSummary -WorksheetName 'Subscription_Summary' -TableName 'Subscription_Summary'
  }

  # 9) Summary sheet
  $vaultsTotal = $rsvReport.Count + $bvReport.Count
  $vmTotal     = $vmReport.Count
  $vmProtected = ($vmReport | Where-Object { $_.IsBackedUp }).Count
  $vmPct       = if ($vmTotal) { [math]::Round(100*$vmProtected/$vmTotal, 1) } else { 0 }
  $dbTotal     = $dbReport.Count
  $dbProtected = ($dbReport | Where-Object { $_.IsBackedUp }).Count
  $dbPct       = if ($dbTotal) { [math]::Round(100*$dbProtected/$dbTotal, 1) } else { 0 }
  $alwaysOnVaults = @($rsvReport + $bvReport) | Where-Object { $_.AlwaysOnSoftDelete -eq $true }
  $enhancedVaults = @($rsvReport + $bvReport) | Where-Object { $_.SecurityLevel -eq 'Enhanced' }

  $summary = @(
    [pscustomobject]@{ Metric = 'Vaults (total)';             Value = $vaultsTotal }
    [pscustomobject]@{ Metric = '  RSVs';                     Value = $rsvReport.Count }
    [pscustomobject]@{ Metric = '  Backup Vaults';            Value = $bvReport.Count }
    [pscustomobject]@{ Metric = 'Vaults with Always-On SD';   Value = ($alwaysOnVaults | Measure-Object | Select-Object -ExpandProperty Count) }
    [pscustomobject]@{ Metric = 'Vaults with Enhanced Sec';   Value = ($enhancedVaults | Measure-Object | Select-Object -ExpandProperty Count) }
    [pscustomobject]@{ Metric = 'VMs (total)';                Value = $vmTotal }
    [pscustomobject]@{ Metric = 'VMs protected';              Value = "$vmProtected ($vmPct`%)" }
    [pscustomobject]@{ Metric = 'Databases/Services (total)'; Value = $dbTotal }
    [pscustomobject]@{ Metric = 'Databases protected';        Value = "$dbProtected ($dbPct`%)" }
  )

  Export-Sheet -Data $summary -WorksheetName 'Summary' -TableName 'Summary' -Columns @('Metric','Value')

  Write-Host "`nExcel export complete → $XlsxPath" -ForegroundColor Green
}
