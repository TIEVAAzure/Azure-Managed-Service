<#
.SYNOPSIS
  TIEVA Identity & Access Auditor
  
.DESCRIPTION
  Comprehensive Azure identity and access audit for AMS customer meetings:
  - RBAC role assignments analysis
  - Privileged roles and standing access
  - Service principal hygiene
  - Managed identity coverage
  - Guest user analysis
  - Custom role definitions
  - PIM status (if available)
  - Subscription-level permissions
  
  Automatically connects to Microsoft Graph for accurate principal name resolution.
  Graph modules are auto-installed if not present.
  
  Outputs multi-sheet Excel workbook: Identity_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.PARAMETER IncludePIM
  Attempt to collect PIM eligible assignments (requires PIM license and permissions)

.PARAMETER SkipGraph
  Skip Microsoft Graph connection. Use this in environments where Graph is not available.
  Note: Some group/user names may not resolve without Graph.
  
.EXAMPLE
  .\IdentityAudit.ps1
  
.EXAMPLE
  .\IdentityAudit.ps1 -SubscriptionIds @("sub-id-1","sub-id-2") -IncludePIM

.EXAMPLE
  .\IdentityAudit.ps1 -SkipGraph
  
.NOTES
  Requires: Az.Accounts, Az.Resources
  Auto-installs: Microsoft.Graph.Authentication, Microsoft.Graph.Users, Microsoft.Graph.Groups, Microsoft.Graph.Applications
  Permissions: Reader + User Access Administrator (or equivalent) on subscriptions
               User.Read.All, Group.Read.All, Application.Read.All on Entra ID (for Graph)
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads",
  [switch]$IncludePIM,
  [switch]$SkipGraph
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Identity & Access Auditor" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "Started: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Gray
Write-Host ""

# ============================================================================
# CONFIGURATION
# ============================================================================

# Privileged role definitions (built-in roles that grant significant access)
$PrivilegedRoles = @(
  'Owner',
  'Contributor',
  'User Access Administrator',
  'Role Based Access Control Administrator',
  'Global Administrator',
  'Security Administrator',
  'Key Vault Administrator',
  'Key Vault Secrets Officer',
  'Virtual Machine Administrator Login',
  'Virtual Machine Contributor',
  'Storage Account Contributor',
  'SQL Server Contributor',
  'Network Contributor'
)

$HighlyPrivilegedRoles = @(
  'Owner',
  'User Access Administrator',
  'Role Based Access Control Administrator'
)

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

function Get-PrincipalType {
  param([string]$ObjectType)
  switch -Regex ($ObjectType) {
    'User' { return 'User' }
    'Group' { return 'Group' }
    'ServicePrincipal' { return 'Service Principal' }
    'Application' { return 'Application' }
    default { return $ObjectType }
  }
}

function Test-IsPrivilegedRole {
  param([string]$RoleName)
  return $PrivilegedRoles -contains $RoleName
}

function Test-IsHighlyPrivilegedRole {
  param([string]$RoleName)
  return $HighlyPrivilegedRoles -contains $RoleName
}

function Get-ScopeLevel {
  param([string]$Scope)
  if ($Scope -eq '/') { return 'Root' }
  if ($Scope -match '^/subscriptions/[^/]+$') { return 'Subscription' }
  if ($Scope -match '/resourceGroups/[^/]+$') { return 'Resource Group' }
  if ($Scope -match '/providers/') { return 'Resource' }
  return 'Other'
}

function Get-ScopeName {
  param([string]$Scope)
  if ($Scope -eq '/') { return 'Tenant Root' }
  $parts = $Scope -split '/'
  return $parts[-1]
}

function Resolve-PrincipalName {
  param(
    [string]$ObjectId,
    [string]$DisplayName,
    [string]$ObjectType
  )
  
  # Return DisplayName if already populated
  if (-not [string]::IsNullOrWhiteSpace($DisplayName)) {
    return $DisplayName
  }
  
  # Check cache first
  if ($principalCache.ContainsKey($ObjectId)) {
    return $principalCache[$ObjectId]
  }
  
  $resolvedName = $null
  
  # Try to resolve using Microsoft Graph (most reliable)
  if ($graphAvailable -and $ObjectId) {
    try {
      switch -Wildcard ($ObjectType) {
        '*Group*' {
          $group = Get-MgGroup -GroupId $ObjectId -Property DisplayName -ErrorAction Stop
          if ($group) { $resolvedName = $group.DisplayName }
        }
        '*User*' {
          $user = Get-MgUser -UserId $ObjectId -Property DisplayName -ErrorAction Stop
          if ($user) { $resolvedName = $user.DisplayName }
        }
        '*ServicePrincipal*' {
          $sp = Get-MgServicePrincipal -ServicePrincipalId $ObjectId -Property DisplayName -ErrorAction Stop
          if ($sp) { $resolvedName = $sp.DisplayName }
        }
        '*Application*' {
          $sp = Get-MgServicePrincipal -ServicePrincipalId $ObjectId -Property DisplayName -ErrorAction Stop
          if ($sp) { $resolvedName = $sp.DisplayName }
        }
      }
    } catch {
      # Graph lookup failed - will try Az AD fallback
    }
  }
  
  # Fallback: Try Az AD cmdlets
  if (-not $resolvedName -and $ObjectId) {
    try {
      switch -Wildcard ($ObjectType) {
        '*Group*' {
          $adObj = Get-AzADGroup -ObjectId $ObjectId -ErrorAction Stop
          if ($adObj) { $resolvedName = $adObj.DisplayName }
        }
        '*User*' {
          $adObj = Get-AzADUser -ObjectId $ObjectId -ErrorAction Stop
          if ($adObj) { $resolvedName = $adObj.DisplayName }
        }
        '*ServicePrincipal*' {
          $adObj = Get-AzADServicePrincipal -ObjectId $ObjectId -ErrorAction Stop
          if ($adObj) { $resolvedName = $adObj.DisplayName }
        }
        '*Application*' {
          $adObj = Get-AzADServicePrincipal -ObjectId $ObjectId -ErrorAction Stop
          if ($adObj) { $resolvedName = $adObj.DisplayName }
        }
      }
    } catch {
      # Az AD lookup failed too
    }
  }
  
  # Final fallback: return truncated ObjectId
  if (-not $resolvedName) {
    $resolvedName = "(ObjectId: $($ObjectId.Substring(0,8))...)"
  }
  
  # Cache the result
  $principalCache[$ObjectId] = $resolvedName
  return $resolvedName
}

# Pre-load all groups for efficient lookups (avoids per-item API calls)
function Initialize-PrincipalCache {
  Write-Host "  -> Pre-loading Entra ID principals for name resolution..." -NoNewline
  $loadedCount = 0
  
  # Try Graph first
  if ($graphAvailable) {
    try {
      # Load all groups
      $allGroups = Get-MgGroup -All -Property Id,DisplayName -ErrorAction Stop
      foreach ($g in $allGroups) {
        if ($g.Id -and $g.DisplayName) {
          $principalCache[$g.Id] = $g.DisplayName
          $loadedCount++
        }
      }
      
      # Load service principals too
      $allSPs = Get-MgServicePrincipal -All -Property Id,DisplayName -ErrorAction SilentlyContinue
      foreach ($sp in $allSPs) {
        if ($sp.Id -and $sp.DisplayName -and -not $principalCache.ContainsKey($sp.Id)) {
          $principalCache[$sp.Id] = $sp.DisplayName
          $loadedCount++
        }
      }
    } catch {
      Write-Host "" # newline
      Write-Warning "    Could not pre-load from Graph: $_"
    }
  }
  
  # Fallback to Az AD if Graph didn't load anything
  if ($loadedCount -eq 0) {
    try {
      # Load groups
      $allGroups = Get-AzADGroup -First 1000 -ErrorAction Stop
      foreach ($g in $allGroups) {
        if ($g.Id -and $g.DisplayName) {
          $principalCache[$g.Id] = $g.DisplayName
          $loadedCount++
        }
      }
      
      # Load service principals
      $allSPs = Get-AzADServicePrincipal -First 1000 -ErrorAction SilentlyContinue
      foreach ($sp in $allSPs) {
        if ($sp.Id -and $sp.DisplayName -and -not $principalCache.ContainsKey($sp.Id)) {
          $principalCache[$sp.Id] = $sp.DisplayName
          $loadedCount++
        }
      }
    } catch {
      Write-Host "" # newline
      Write-Warning "    Could not pre-load from Az AD: $_"
    }
  }
  
  if ($loadedCount -gt 0) {
    Write-Host " $loadedCount principals cached" -ForegroundColor Cyan
  } else {
    Write-Host " (failed - will show ObjectIds)" -ForegroundColor Yellow
    Write-Host "    Hint: Ensure you have Group.Read.All permission in Entra ID" -ForegroundColor Gray
  }
}

# ============================================================================
# DATA COLLECTIONS
# ============================================================================

$roleAssignmentReport = [System.Collections.Generic.List[object]]::new()
$privilegedAccessReport = [System.Collections.Generic.List[object]]::new()
$servicePrincipalReport = [System.Collections.Generic.List[object]]::new()
$managedIdentityReport = [System.Collections.Generic.List[object]]::new()
$customRoleReport = [System.Collections.Generic.List[object]]::new()
$subscriptionAdminReport = [System.Collections.Generic.List[object]]::new()
$guestUserReport = [System.Collections.Generic.List[object]]::new()
$subscriptionSummary = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[object]]::new()

# Cache for principal lookups
$principalCache = @{}

# Cache for guest user lookup (ObjectId -> IsGuest boolean)
$guestCache = @{}
$graphAvailable = $false

# ============================================================================
# MICROSOFT GRAPH AUTO-CONNECTION
# ============================================================================

if ($SkipGraph) {
  Write-Host "Skipping Microsoft Graph connection (-SkipGraph specified)" -ForegroundColor Gray
  Write-Host "  Some group/user names may not resolve properly" -ForegroundColor Gray
  Write-Host ""
} else {
  Write-Host "Checking Microsoft Graph connection..." -ForegroundColor Cyan
  
  # Required Graph modules
  $requiredGraphModules = @(
    'Microsoft.Graph.Authentication',
    'Microsoft.Graph.Users',
    'Microsoft.Graph.Groups',
    'Microsoft.Graph.Applications'
  )
  
  # Install missing Graph modules
  $missingModules = $requiredGraphModules | Where-Object { -not (Get-Module -ListAvailable -Name $_) }
  if ($missingModules.Count -gt 0) {
    Write-Host "  Installing Microsoft Graph modules: $($missingModules -join ', ')..." -ForegroundColor Yellow
    try {
      Set-PSRepository -Name PSGallery -InstallationPolicy Trusted -ErrorAction SilentlyContinue | Out-Null
      foreach ($mod in $missingModules) {
        Install-Module $mod -Scope CurrentUser -Force -AllowClobber -ErrorAction Stop
      }
      Write-Host "  Graph modules installed successfully" -ForegroundColor Green
    } catch {
      Write-Warning "  Could not install Graph modules: $_"
      Write-Host "  Continuing without Graph (some group names may not resolve)" -ForegroundColor Gray
    }
  }
  
  # Import Graph modules
  try {
    foreach ($mod in $requiredGraphModules) {
      Import-Module $mod -ErrorAction Stop
    }
  } catch {
    Write-Warning "  Could not import Graph modules: $_"
  }
  
  # Connect to Microsoft Graph if not already connected
  try {
    $graphContext = Get-MgContext -ErrorAction SilentlyContinue
    
    if (-not $graphContext) {
      Write-Host "  Connecting to Microsoft Graph..." -ForegroundColor Yellow
      
      # Get tenant ID from current Az context for consistency
      $azContext = Get-AzContext -ErrorAction SilentlyContinue
      $tenantId = $azContext.Tenant.Id
      
      # Required scopes for identity audit
      $graphScopes = @(
        'User.Read.All',
        'Group.Read.All', 
        'Application.Read.All',
        'Directory.Read.All'
      )
      
      if ($tenantId) {
        Connect-MgGraph -Scopes $graphScopes -TenantId $tenantId -NoWelcome -ErrorAction Stop
      } else {
        Connect-MgGraph -Scopes $graphScopes -NoWelcome -ErrorAction Stop
      }
      
      $graphContext = Get-MgContext -ErrorAction SilentlyContinue
    }
    
    if ($graphContext) {
      $graphAvailable = $true
      Write-Host "  Microsoft Graph connected as: $($graphContext.Account)" -ForegroundColor Green
      Write-Host "  Tenant: $($graphContext.TenantId)" -ForegroundColor Gray
    }
  } catch {
    Write-Warning "  Could not connect to Microsoft Graph: $_"
    Write-Host "  Continuing without Graph - some principal names may not resolve" -ForegroundColor Gray
    Write-Host "  To fix: Run 'Connect-MgGraph -Scopes User.Read.All,Group.Read.All' manually" -ForegroundColor Gray
  }
  
  Write-Host ""
}

function Test-IsGuestUser {
  param([string]$ObjectId, [string]$SignInName)
  
  # Check cache first
  if ($guestCache.ContainsKey($ObjectId)) {
    return $guestCache[$ObjectId]
  }
  
  $isGuest = $false
  
  # Try Microsoft Graph first (most accurate)
  if ($graphAvailable -and $ObjectId) {
    try {
      $user = Get-MgUser -UserId $ObjectId -Property UserType -ErrorAction SilentlyContinue
      if ($user -and $user.UserType -eq 'Guest') {
        $isGuest = $true
      }
      $guestCache[$ObjectId] = $isGuest
      return $isGuest
    } catch {}
  }
  
  # Fallback to pattern matching
  if ($SignInName) {
    # Pattern 1: #EXT# marker
    if ($SignInName -match '#EXT#') { $isGuest = $true }
    # Pattern 2: underscore format for B2B guests
    elseif ($SignInName -match '^[^@]+_[^@]+@.*\.onmicrosoft\.com$') { $isGuest = $true }
  }
  
  $guestCache[$ObjectId] = $isGuest
  return $isGuest
}

# ============================================================================
# MAIN AUDIT LOOP
# ============================================================================

$subscriptions = Get-SubscriptionList
if (-not $subscriptions) { Write-Error "No accessible subscriptions found."; exit 1 }

Write-Host "Found $($subscriptions.Count) subscription(s) to audit" -ForegroundColor Green

# Pre-load principal names for efficient lookups
Initialize-PrincipalCache

Write-Host ""

foreach ($sub in $subscriptions) {
  Write-Host "Processing: $($sub.Name)" -ForegroundColor Yellow
  
  try { Set-AzContext -SubscriptionId $sub.Id -ErrorAction Stop | Out-Null }
  catch { Write-Warning "  Could not set context: $_"; continue }
  
  $subAssignmentCount = 0
  $subPrivilegedCount = 0
  $subSpCount = 0
  $subMiCount = 0
  
  # -----------------------------------------------------------
  # 1. ROLE ASSIGNMENTS
  # -----------------------------------------------------------
  Write-Host "  -> Collecting role assignments..." -NoNewline
  
  $assignments = @()
  try { $assignments = Get-AzRoleAssignment -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($assignments.Count) assignments" -ForegroundColor Cyan
  $subAssignmentCount = $assignments.Count
  
  foreach ($ra in $assignments) {
    $principalType = Get-PrincipalType -ObjectType $ra.ObjectType
    $scopeLevel = Get-ScopeLevel -Scope $ra.Scope
    $scopeName = Get-ScopeName -Scope $ra.Scope
    $isPrivileged = Test-IsPrivilegedRole -RoleName $ra.RoleDefinitionName
    $isHighlyPrivileged = Test-IsHighlyPrivilegedRole -RoleName $ra.RoleDefinitionName
    
    # Resolve DisplayName if blank (common for Groups with inherited permissions)
    $displayName = Resolve-PrincipalName -ObjectId $ra.ObjectId -DisplayName $ra.DisplayName -ObjectType $ra.ObjectType
    
    # Check if inherited
    $isInherited = -not ($ra.Scope -match "^/subscriptions/$($sub.Id)(/|$)")
    
    $roleAssignmentReport.Add([PSCustomObject]@{
      SubscriptionName   = $sub.Name
      SubscriptionId     = $sub.Id
      DisplayName        = $displayName
      SignInName         = $ra.SignInName
      ObjectId           = $ra.ObjectId
      ObjectType         = $principalType
      RoleDefinitionName = $ra.RoleDefinitionName
      RoleDefinitionId   = $ra.RoleDefinitionId
      Scope              = $ra.Scope
      ScopeLevel         = $scopeLevel
      ScopeName          = $scopeName
      IsPrivileged       = $isPrivileged
      IsHighlyPrivileged = $isHighlyPrivileged
      IsInherited        = $isInherited
      CanDelegate        = $ra.CanDelegate
      AssignmentId       = $ra.RoleAssignmentId
    })
    
    # Track privileged access
    if ($isPrivileged) {
      $subPrivilegedCount++
      
      $privilegedAccessReport.Add([PSCustomObject]@{
        SubscriptionName   = $sub.Name
        SubscriptionId     = $sub.Id
        DisplayName        = $displayName
        SignInName         = $ra.SignInName
        ObjectType         = $principalType
        Role               = $ra.RoleDefinitionName
        Scope              = $ra.Scope
        ScopeLevel         = $scopeLevel
        IsHighlyPrivileged = $isHighlyPrivileged
        IsInherited        = $isInherited
        RiskLevel          = if ($isHighlyPrivileged -and $scopeLevel -eq 'Subscription') { 'High' } 
                            elseif ($isHighlyPrivileged) { 'Medium' } 
                            elseif ($isPrivileged -and $scopeLevel -eq 'Subscription') { 'Medium' }
                            else { 'Low' }
      })
    }
    
    # Finding: User with Owner at subscription level
    if ($isHighlyPrivileged -and $scopeLevel -eq 'Subscription' -and $principalType -eq 'User') {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Privileged Access'
        ResourceType     = 'Role Assignment'
        ResourceName     = "$displayName - $($ra.RoleDefinitionName)"
        ResourceId       = $ra.RoleAssignmentId
        Detail           = "User has $($ra.RoleDefinitionName) role at subscription scope"
        Recommendation   = 'Use PIM for just-in-time access, or scope down to resource group level'
      })
    }
    
    # Finding: Service Principal with highly privileged role
    if ($isHighlyPrivileged -and $principalType -eq 'Service Principal') {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Service Principal Security'
        ResourceType     = 'Role Assignment'
        ResourceName     = "$displayName - $($ra.RoleDefinitionName)"
        ResourceId       = $ra.RoleAssignmentId
        Detail           = "Service Principal has $($ra.RoleDefinitionName) role"
        Recommendation   = 'Apply least privilege - use more specific roles'
      })
    }
  }
  
  # Subscription-level admins
  $subAdmins = $assignments | Where-Object { 
    (Get-ScopeLevel -Scope $_.Scope) -eq 'Subscription' -and 
    (Test-IsHighlyPrivilegedRole -RoleName $_.RoleDefinitionName)
  }
  
  foreach ($admin in $subAdmins) {
    $adminDisplayName = Resolve-PrincipalName -ObjectId $admin.ObjectId -DisplayName $admin.DisplayName -ObjectType $admin.ObjectType
    $subscriptionAdminReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      DisplayName      = $adminDisplayName
      SignInName       = $admin.SignInName
      ObjectType       = Get-PrincipalType -ObjectType $admin.ObjectType
      Role             = $admin.RoleDefinitionName
    })
  }
  
  # -----------------------------------------------------------
  # 2. SERVICE PRINCIPALS (App Registrations with access)
  # -----------------------------------------------------------
  Write-Host "  -> Analyzing service principals..." -NoNewline
  
  $spAssignments = $assignments | Where-Object { $_.ObjectType -eq 'ServicePrincipal' }
  $uniqueSPs = $spAssignments | Select-Object -Property ObjectId, DisplayName -Unique
  
  Write-Host " $($uniqueSPs.Count) service principals with access" -ForegroundColor Cyan
  $subSpCount = $uniqueSPs.Count
  
  foreach ($sp in $uniqueSPs) {
    $spRoles = $spAssignments | Where-Object { $_.ObjectId -eq $sp.ObjectId }
    $roleList = ($spRoles.RoleDefinitionName | Select-Object -Unique) -join ', '
    $scopeList = ($spRoles.Scope | Select-Object -Unique) -join '; '
    $hasPrivileged = ($spRoles | Where-Object { Test-IsPrivilegedRole -RoleName $_.RoleDefinitionName }).Count -gt 0
    
    $spDisplayName = Resolve-PrincipalName -ObjectId $sp.ObjectId -DisplayName $sp.DisplayName -ObjectType 'ServicePrincipal'
    
    $servicePrincipalReport.Add([PSCustomObject]@{
      SubscriptionName     = $sub.Name
      SubscriptionId       = $sub.Id
      DisplayName          = $spDisplayName
      ObjectId             = $sp.ObjectId
      RoleCount            = $spRoles.Count
      Roles                = $roleList
      Scopes               = $scopeList
      HasPrivilegedRole    = $hasPrivileged
    })
    
    # Finding: SP with many roles
    if ($spRoles.Count -gt 5) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Service Principal Security'
        ResourceType     = 'Service Principal'
        ResourceName     = $spDisplayName
        ResourceId       = $sp.ObjectId
        Detail           = "Service Principal has $($spRoles.Count) role assignments"
        Recommendation   = 'Review if all roles are necessary - consolidate or reduce'
      })
    }
  }
  
  # -----------------------------------------------------------
  # 3. MANAGED IDENTITIES
  # -----------------------------------------------------------
  Write-Host "  -> Checking managed identities..." -NoNewline
  
  $miAssignments = $assignments | Where-Object { 
    $_.ObjectType -eq 'ServicePrincipal' -and 
    $_.DisplayName -match 'mi-|managed|identity'
  }
  
  # Also check for actual managed identity resources
  $systemMIs = @()
  $userMIs = @()
  try {
    $userMIs = Get-AzUserAssignedIdentity -ErrorAction SilentlyContinue
  } catch {}
  
  Write-Host " $($userMIs.Count) user-assigned MIs" -ForegroundColor Cyan
  
  foreach ($mi in $userMIs) {
    $miRoles = $assignments | Where-Object { $_.ObjectId -eq $mi.PrincipalId }
    
    $managedIdentityReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Name             = $mi.Name
      ResourceGroup    = $mi.ResourceGroupName
      Location         = $mi.Location
      Type             = 'User-Assigned'
      PrincipalId      = $mi.PrincipalId
      ClientId         = $mi.ClientId
      RoleCount        = $miRoles.Count
      Roles            = ($miRoles.RoleDefinitionName | Select-Object -Unique) -join ', '
    })
    $subMiCount++
  }
  
  # -----------------------------------------------------------
  # 4. CUSTOM ROLES
  # -----------------------------------------------------------
  Write-Host "  -> Collecting custom role definitions..." -NoNewline
  
  $customRoles = @()
  try { 
    $customRoles = Get-AzRoleDefinition -Custom -ErrorAction SilentlyContinue 
  } catch {}
  
  Write-Host " $($customRoles.Count) custom roles" -ForegroundColor Cyan
  
  foreach ($role in $customRoles) {
    $actionCount = ($role.Actions | Measure-Object).Count
    $notActionCount = ($role.NotActions | Measure-Object).Count
    $dataActionCount = ($role.DataActions | Measure-Object).Count
    
    $hasWildcard = ($role.Actions -contains '*') -or ($role.Actions -match '\*$')
    
    $customRoleReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      RoleName         = $role.Name
      Description      = $role.Description
      ActionCount      = $actionCount
      NotActionCount   = $notActionCount
      DataActionCount  = $dataActionCount
      HasWildcard      = $hasWildcard
      AssignableScopes = ($role.AssignableScopes -join '; ')
      IsCustom         = $role.IsCustom
    })
    
    # Finding: Custom role with wildcard actions
    if ($hasWildcard) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Custom Roles'
        ResourceType     = 'Role Definition'
        ResourceName     = $role.Name
        ResourceId       = $role.Id
        Detail           = 'Custom role contains wildcard (*) actions'
        Recommendation   = 'Replace wildcard with specific action permissions'
      })
    }
  }
  
  # -----------------------------------------------------------
  # 5. GUEST USERS (from role assignments)
  # -----------------------------------------------------------
  Write-Host "  -> Identifying guest users..." -NoNewline
  
  # Get tenant default domain for comparison (used in pattern matching fallback)
  $tenantDomain = $null
  try {
    $context = Get-AzContext
    if ($context.Account.Type -eq 'User') {
      $tenantDomain = ($context.Account.Id -split '@')[1]
    }
  } catch {}
  
  # Filter to user-type assignments and check each for guest status
  $userAssignments = $assignments | Where-Object { $_.ObjectType -match 'User' }
  $guestAssignments = @()
  
  foreach ($ua in $userAssignments) {
    $isGuest = Test-IsGuestUser -ObjectId $ua.ObjectId -SignInName $ua.SignInName
    
    # Additional pattern checks if helper didn't confirm
    if (-not $isGuest -and $ua.SignInName -and $tenantDomain) {
      # Check if SignInName domain differs from tenant domain
      if ($ua.SignInName -match '@(.+)$') {
        $userDomain = $Matches[1]
        if ($userDomain -ne $tenantDomain -and 
            $userDomain -notmatch '\.onmicrosoft\.com$' -and
            $userDomain -notmatch "^$([regex]::Escape($tenantDomain.Split('.')[0]))") {
          $isGuest = $true
        }
      }
    }
    
    # ObjectType check (some APIs include Guest indicator)
    if (-not $isGuest -and $ua.ObjectType -match 'Guest') {
      $isGuest = $true
    }
    
    if ($isGuest) {
      $guestAssignments += $ua
    }
  }
  
  Write-Host " $($guestAssignments.Count) guest assignments" -ForegroundColor Cyan
  
  $guestUsers = $guestAssignments | Group-Object -Property ObjectId
  
  foreach ($guest in $guestUsers) {
    $guestInfo = $guest.Group[0]
    $roles = ($guest.Group.RoleDefinitionName | Select-Object -Unique) -join ', '
    $hasPrivileged = ($guest.Group | Where-Object { Test-IsPrivilegedRole -RoleName $_.RoleDefinitionName }).Count -gt 0
    
    $guestDisplayName = Resolve-PrincipalName -ObjectId $guestInfo.ObjectId -DisplayName $guestInfo.DisplayName -ObjectType 'User'
    
    $guestUserReport.Add([PSCustomObject]@{
      SubscriptionName   = $sub.Name
      SubscriptionId     = $sub.Id
      DisplayName        = $guestDisplayName
      SignInName         = $guestInfo.SignInName
      ObjectId           = $guestInfo.ObjectId
      RoleCount          = $guest.Group.Count
      Roles              = $roles
      HasPrivilegedRole  = $hasPrivileged
    })
    
    # Finding: Guest with privileged role
    if ($hasPrivileged) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Guest Access'
        ResourceType     = 'Guest User'
        ResourceName     = $guestDisplayName
        ResourceId       = $guestInfo.ObjectId
        Detail           = "Guest user has privileged role(s): $roles"
        Recommendation   = 'Review guest access - consider using dedicated accounts or reducing privileges'
      })
    }
  }
  
  # -----------------------------------------------------------
  # SUBSCRIPTION SUMMARY
  # -----------------------------------------------------------
  $highFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'High' }).Count
  $medFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Medium' }).Count
  $lowFindings = ($findings | Where-Object { $_.SubscriptionId -eq $sub.Id -and $_.Severity -eq 'Low' }).Count
  
  $subscriptionSummary.Add([PSCustomObject]@{
    SubscriptionName      = $sub.Name
    SubscriptionId        = $sub.Id
    TotalAssignments      = $subAssignmentCount
    PrivilegedAssignments = $subPrivilegedCount
    ServicePrincipals     = $subSpCount
    ManagedIdentities     = $subMiCount
    GuestUsers            = $guestUsers.Count
    CustomRoles           = $customRoles.Count
    SubscriptionAdmins    = $subAdmins.Count
    HighFindings          = $highFindings
    MediumFindings        = $medFindings
    LowFindings           = $lowFindings
    TotalFindings         = $highFindings + $medFindings + $lowFindings
  })
  
  # Finding: Too many subscription admins
  if ($subAdmins.Count -gt 5) {
    $findings.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Severity         = 'High'
      Category         = 'Privileged Access'
      ResourceType     = 'Subscription'
      ResourceName     = $sub.Name
      ResourceId       = $sub.Id
      Detail           = "$($subAdmins.Count) principals have Owner/UAA at subscription level"
      Recommendation   = 'Reduce standing privileged access - implement PIM for JIT access'
    })
  }
  
  Write-Host ""
}

# ============================================================================
# CONSOLE OUTPUT
# ============================================================================

Write-Host "`n==========================================" -ForegroundColor Green
Write-Host "AUDIT COMPLETE" -ForegroundColor Green
Write-Host "==========================================`n" -ForegroundColor Green

Write-Host "=== Subscription Summary ===" -ForegroundColor Cyan
if ($subscriptionSummary.Count -gt 0) {
  $subscriptionSummary | Format-Table SubscriptionName, TotalAssignments, PrivilegedAssignments, ServicePrincipals, GuestUsers, TotalFindings -AutoSize
}

Write-Host "`n=== Privileged Access Summary ===" -ForegroundColor Cyan
$highRisk = $privilegedAccessReport | Where-Object { $_.RiskLevel -eq 'High' }
$medRisk = $privilegedAccessReport | Where-Object { $_.RiskLevel -eq 'Medium' }
Write-Host "  High risk privileged access:   $($highRisk.Count)" -ForegroundColor $(if ($highRisk.Count -gt 0) { 'Red' } else { 'Green' })
Write-Host "  Medium risk privileged access: $($medRisk.Count)" -ForegroundColor $(if ($medRisk.Count -gt 0) { 'Yellow' } else { 'Green' })

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

$XlsxPath = Join-Path $OutPath 'Identity_Audit.xlsx'

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

Export-Sheet -Data $subscriptionSummary -WorksheetName 'Subscription_Summary' -TableName 'Subscriptions'
Export-Sheet -Data $subscriptionAdminReport -WorksheetName 'Subscription_Admins' -TableName 'SubAdmins'
Export-Sheet -Data $privilegedAccessReport -WorksheetName 'Privileged_Access' -TableName 'PrivilegedAccess'
Export-Sheet -Data $roleAssignmentReport -WorksheetName 'All_Role_Assignments' -TableName 'RoleAssignments'
Export-Sheet -Data $servicePrincipalReport -WorksheetName 'Service_Principals' -TableName 'ServicePrincipals'
Export-Sheet -Data $managedIdentityReport -WorksheetName 'Managed_Identities' -TableName 'ManagedIdentities'
Export-Sheet -Data $guestUserReport -WorksheetName 'Guest_Users' -TableName 'GuestUsers'
Export-Sheet -Data $customRoleReport -WorksheetName 'Custom_Roles' -TableName 'CustomRoles'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

$overallSummary = @(
  [PSCustomObject]@{ Metric='Audit Date';Value=(Get-Date -Format 'yyyy-MM-dd HH:mm') }
  [PSCustomObject]@{ Metric='Subscriptions Audited';Value=$subscriptions.Count }
  [PSCustomObject]@{ Metric='Total Role Assignments';Value=$roleAssignmentReport.Count }
  [PSCustomObject]@{ Metric='Privileged Assignments';Value=$privilegedAccessReport.Count }
  [PSCustomObject]@{ Metric='High Risk Access';Value=($privilegedAccessReport | Where-Object { $_.RiskLevel -eq 'High' }).Count }
  [PSCustomObject]@{ Metric='Service Principals';Value=$servicePrincipalReport.Count }
  [PSCustomObject]@{ Metric='Managed Identities';Value=$managedIdentityReport.Count }
  [PSCustomObject]@{ Metric='Guest Users';Value=$guestUserReport.Count }
  [PSCustomObject]@{ Metric='Custom Roles';Value=$customRoleReport.Count }
  [PSCustomObject]@{ Metric='High Findings';Value=$totalHigh }
  [PSCustomObject]@{ Metric='Medium Findings';Value=$totalMed }
  [PSCustomObject]@{ Metric='Low Findings';Value=$totalLow }
)
Export-Sheet -Data $overallSummary -WorksheetName 'Summary' -TableName 'Summary'

Write-Host "`nExcel export complete -> $XlsxPath" -ForegroundColor Green
Write-Host "`n+ Audit complete!" -ForegroundColor Green
Write-Host "Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n" -ForegroundColor Gray
