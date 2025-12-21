<#
.SYNOPSIS
  TIEVA Network Topology Auditor
  
.DESCRIPTION
  Comprehensive Azure network audit for AMS customer meetings:
  - VNet inventory and peering topology
  - NSG rule analysis (overly permissive, unused)
  - Private endpoint coverage
  - Public IP exposure audit
  - ExpressRoute/VPN Gateway status
  - Load balancer and Application Gateway health
  - DNS configuration
  - Network Watcher status
  
  Outputs multi-sheet Excel workbook: Network_Audit.xlsx
  
.PARAMETER SubscriptionIds
  Optional array of subscription IDs to audit. If not specified, audits all accessible subscriptions.
  
.PARAMETER OutPath
  Output directory for the Excel file. Defaults to current user's Downloads folder.
  
.EXAMPLE
  .\NetworkAudit.ps1
  
.EXAMPLE
  .\NetworkAudit.ps1 -SubscriptionIds @("sub-id-1","sub-id-2")
  
.NOTES
  Requires: Az.Accounts, Az.Network, Az.Resources, ImportExcel modules
  Permissions: Network Reader on subscriptions
#>

[CmdletBinding()]
param(
  [string[]]$SubscriptionIds,
  [string]$OutPath = "$HOME\Downloads"
)

$ErrorActionPreference = 'Continue'
$WarningPreference = 'SilentlyContinue'

Write-Host "`n==========================================" -ForegroundColor Cyan
Write-Host "TIEVA Network Topology Auditor" -ForegroundColor Cyan
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

function Test-OverlyPermissiveRule {
  param($Rule)
  
  # Check for overly permissive inbound rules
  if ($Rule.Direction -ne 'Inbound') { return $false }
  if ($Rule.Access -ne 'Allow') { return $false }
  
  $dominated = $false
  
  # Any source
  if ($Rule.SourceAddressPrefix -eq '*' -or $Rule.SourceAddressPrefix -eq 'Internet' -or $Rule.SourceAddressPrefix -eq '0.0.0.0/0') {
    # Any port or dangerous ports
    if ($Rule.DestinationPortRange -eq '*') {
      $dominated = $true
    }
    elseif ($Rule.DestinationPortRange -match '^(22|3389|1433|3306|5432|27017|6379|9200|445|139|135)$') {
      $dominated = $true
    }
  }
  
  return $dominated
}

function Get-RiskLevel {
  param($Rule)
  
  if ($Rule.Access -ne 'Allow') { return 'None' }
  if ($Rule.Direction -ne 'Inbound') { return 'Low' }
  
  $srcAny = $Rule.SourceAddressPrefix -eq '*' -or $Rule.SourceAddressPrefix -eq 'Internet' -or $Rule.SourceAddressPrefix -eq '0.0.0.0/0'
  $dstAny = $Rule.DestinationPortRange -eq '*'
  $dangerousPorts = @('22','3389','1433','3306','5432','27017','6379','9200','445','139','135','23','21','25')
  $isDangerous = $dangerousPorts -contains $Rule.DestinationPortRange
  
  if ($srcAny -and $dstAny) { return 'Critical' }
  if ($srcAny -and $isDangerous) { return 'High' }
  if ($srcAny) { return 'Medium' }
  return 'Low'
}

# ============================================================================
# DATA COLLECTIONS
# ============================================================================

$vnetReport = [System.Collections.Generic.List[object]]::new()
$subnetReport = [System.Collections.Generic.List[object]]::new()
$peeringReport = [System.Collections.Generic.List[object]]::new()
$nsgReport = [System.Collections.Generic.List[object]]::new()
$nsgRuleReport = [System.Collections.Generic.List[object]]::new()
$publicIpReport = [System.Collections.Generic.List[object]]::new()
$privateEndpointReport = [System.Collections.Generic.List[object]]::new()
$gatewayReport = [System.Collections.Generic.List[object]]::new()
$loadBalancerReport = [System.Collections.Generic.List[object]]::new()
$appGatewayReport = [System.Collections.Generic.List[object]]::new()
$dnsReport = [System.Collections.Generic.List[object]]::new()
$networkWatcherReport = [System.Collections.Generic.List[object]]::new()
$subscriptionSummary = [System.Collections.Generic.List[object]]::new()
$findings = [System.Collections.Generic.List[object]]::new()

# ============================================================================
# MAIN AUDIT LOOP
# ============================================================================

$subscriptions = Get-SubscriptionList
if (-not $subscriptions) { Write-Error "No accessible subscriptions found."; exit 1 }

Write-Host "Found $($subscriptions.Count) subscription(s) to audit`n" -ForegroundColor Green

foreach ($sub in $subscriptions) {
  Write-Host "Processing: $($sub.Name)" -ForegroundColor Yellow
  
  try { Set-AzContext -SubscriptionId $sub.Id -ErrorAction Stop | Out-Null }
  catch { Write-Warning "  Could not set context: $_"; continue }
  
  $subVnetCount = 0; $subNsgCount = 0; $subPipCount = 0; $subPeCount = 0
  
  # -----------------------------------------------------------
  # 1. VIRTUAL NETWORKS & SUBNETS
  # -----------------------------------------------------------
  Write-Host "  -> Collecting VNets and subnets..." -NoNewline
  
  $vnets = @()
  try { $vnets = Get-AzVirtualNetwork -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($vnets.Count) VNets" -ForegroundColor Cyan
  $subVnetCount = $vnets.Count
  
  foreach ($vnet in $vnets) {
    $addressSpace = ($vnet.AddressSpace.AddressPrefixes -join ', ')
    $subnetCount = $vnet.Subnets.Count
    $peeredCount = $vnet.VirtualNetworkPeerings.Count
    
    # Check for DNS configuration
    $dnsServers = if ($vnet.DhcpOptions -and $vnet.DhcpOptions.DnsServers) {
      $vnet.DhcpOptions.DnsServers -join ', '
    } else { 'Azure Default' }
    
    $vnetReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      VNetName         = $vnet.Name
      ResourceGroup    = $vnet.ResourceGroupName
      Location         = $vnet.Location
      AddressSpace     = $addressSpace
      SubnetCount      = $subnetCount
      PeeringCount     = $peeredCount
      DnsServers       = $dnsServers
      EnableDdosProtection = $vnet.EnableDdosProtection
      ProvisioningState = $vnet.ProvisioningState
    })
    
    # Finding: No DDoS protection on VNet
    if (-not $vnet.EnableDdosProtection) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Network Security'
        ResourceType     = 'Virtual Network'
        ResourceName     = $vnet.Name
        ResourceId       = $vnet.Id
        Detail           = 'DDoS Protection not enabled on VNet'
        Recommendation   = 'Consider enabling DDoS Protection Standard for production workloads'
      })
    }
    
    # Subnets
    foreach ($subnet in $vnet.Subnets) {
      $nsgName = if ($subnet.NetworkSecurityGroup) { 
        ($subnet.NetworkSecurityGroup.Id -split '/')[-1] 
      } else { $null }
      
      $routeTableName = if ($subnet.RouteTable) {
        ($subnet.RouteTable.Id -split '/')[-1]
      } else { $null }
      
      $serviceEndpoints = if ($subnet.ServiceEndpoints) {
        ($subnet.ServiceEndpoints.Service -join ', ')
      } else { $null }
      
      $delegations = if ($subnet.Delegations) {
        ($subnet.Delegations.ServiceName -join ', ')
      } else { $null }
      
      $privateEndpointPolicy = $subnet.PrivateEndpointNetworkPolicies
      $privateLinkPolicy = $subnet.PrivateLinkServiceNetworkPolicies
      
      $subnetReport.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        VNetName         = $vnet.Name
        SubnetName       = $subnet.Name
        AddressPrefix    = ($subnet.AddressPrefix -join ', ')
        NSG              = $nsgName
        RouteTable       = $routeTableName
        ServiceEndpoints = $serviceEndpoints
        Delegations      = $delegations
        PrivateEndpointPolicy = $privateEndpointPolicy
        PrivateLinkPolicy = $privateLinkPolicy
      })
      
      # Finding: Subnet without NSG
      if (-not $nsgName -and $subnet.Name -notmatch 'GatewaySubnet|AzureFirewallSubnet|AzureBastionSubnet') {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Severity         = 'High'
          Category         = 'Network Security'
          ResourceType     = 'Subnet'
          ResourceName     = "$($vnet.Name)/$($subnet.Name)"
          ResourceId       = $subnet.Id
          Detail           = 'Subnet has no Network Security Group attached'
          Recommendation   = 'Attach an NSG to control traffic flow'
        })
      }
    }
    
    # Peerings
    foreach ($peering in $vnet.VirtualNetworkPeerings) {
      $remoteVnet = ($peering.RemoteVirtualNetwork.Id -split '/')[-1]
      $remoteSub = if ($peering.RemoteVirtualNetwork.Id -match '/subscriptions/([^/]+)/') {
        $matches[1]
      } else { 'Same' }
      
      $peeringReport.Add([PSCustomObject]@{
        SubscriptionName    = $sub.Name
        LocalVNet           = $vnet.Name
        RemoteVNet          = $remoteVnet
        RemoteSubscription  = $remoteSub
        PeeringState        = $peering.PeeringState
        AllowVNetAccess     = $peering.AllowVirtualNetworkAccess
        AllowForwardedTraffic = $peering.AllowForwardedTraffic
        AllowGatewayTransit = $peering.AllowGatewayTransit
        UseRemoteGateways   = $peering.UseRemoteGateways
      })
      
      # Finding: Peering not connected
      if ($peering.PeeringState -ne 'Connected') {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Severity         = 'High'
          Category         = 'Network Connectivity'
          ResourceType     = 'VNet Peering'
          ResourceName     = "$($vnet.Name) -> $remoteVnet"
          ResourceId       = $peering.Id
          Detail           = "Peering state is $($peering.PeeringState), not Connected"
          Recommendation   = 'Investigate and repair peering connection'
        })
      }
    }
  }
  
  # -----------------------------------------------------------
  # 2. NETWORK SECURITY GROUPS
  # -----------------------------------------------------------
  Write-Host "  -> Analyzing NSG rules..." -NoNewline
  
  $nsgs = @()
  try { $nsgs = Get-AzNetworkSecurityGroup -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($nsgs.Count) NSGs" -ForegroundColor Cyan
  $subNsgCount = $nsgs.Count
  
  $riskyRuleCount = 0
  
  foreach ($nsg in $nsgs) {
    $customRuleCount = ($nsg.SecurityRules | Measure-Object).Count
    $defaultRuleCount = ($nsg.DefaultSecurityRules | Measure-Object).Count
    
    # Check what the NSG is attached to
    $attachedSubnets = @()
    $attachedNics = @()
    
    if ($nsg.Subnets) {
      $attachedSubnets = $nsg.Subnets | ForEach-Object { ($_.Id -split '/')[-1] }
    }
    if ($nsg.NetworkInterfaces) {
      $attachedNics = $nsg.NetworkInterfaces | ForEach-Object { ($_.Id -split '/')[-1] }
    }
    
    $nsgReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      NSGName          = $nsg.Name
      ResourceGroup    = $nsg.ResourceGroupName
      Location         = $nsg.Location
      CustomRuleCount  = $customRuleCount
      AttachedSubnets  = ($attachedSubnets -join ', ')
      AttachedNICs     = ($attachedNics -join ', ')
      IsAttached       = ($attachedSubnets.Count -gt 0 -or $attachedNics.Count -gt 0)
    })
    
    # Finding: Unattached NSG
    if ($attachedSubnets.Count -eq 0 -and $attachedNics.Count -eq 0) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Low'
        Category         = 'Resource Hygiene'
        ResourceType     = 'NSG'
        ResourceName     = $nsg.Name
        ResourceId       = $nsg.Id
        Detail           = 'NSG is not attached to any subnet or NIC'
        Recommendation   = 'Delete if unused or attach to appropriate resources'
      })
    }
    
    # Analyze rules
    foreach ($rule in $nsg.SecurityRules) {
      $riskLevel = Get-RiskLevel -Rule $rule
      $isOverlyPermissive = Test-OverlyPermissiveRule -Rule $rule
      
      if ($riskLevel -in @('Critical','High')) { $riskyRuleCount++ }
      
      $nsgRuleReport.Add([PSCustomObject]@{
        SubscriptionName    = $sub.Name
        NSGName             = $nsg.Name
        RuleName            = $rule.Name
        Priority            = $rule.Priority
        Direction           = $rule.Direction
        Access              = $rule.Access
        Protocol            = $rule.Protocol
        SourceAddress       = $rule.SourceAddressPrefix
        SourcePort          = $rule.SourcePortRange
        DestinationAddress  = $rule.DestinationAddressPrefix
        DestinationPort     = $rule.DestinationPortRange
        RiskLevel           = $riskLevel
        OverlyPermissive    = $isOverlyPermissive
      })
      
      # Finding: Overly permissive rule
      if ($isOverlyPermissive) {
        $findings.Add([PSCustomObject]@{
          SubscriptionName = $sub.Name
          SubscriptionId   = $sub.Id
          Severity         = if ($riskLevel -eq 'Critical') { 'High' } else { 'Medium' }
          Category         = 'Network Security'
          ResourceType     = 'NSG Rule'
          ResourceName     = "$($nsg.Name)/$($rule.Name)"
          ResourceId       = $rule.Id
          Detail           = "Overly permissive rule: $($rule.SourceAddressPrefix) -> Port $($rule.DestinationPortRange)"
          Recommendation   = 'Restrict source addresses and ports to minimum required'
        })
      }
    }
  }
  
  # -----------------------------------------------------------
  # 3. PUBLIC IPs
  # -----------------------------------------------------------
  Write-Host "  -> Checking public IP exposure..." -NoNewline
  
  $pips = @()
  try { $pips = Get-AzPublicIpAddress -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($pips.Count) Public IPs" -ForegroundColor Cyan
  $subPipCount = $pips.Count
  
  foreach ($pip in $pips) {
    $associatedTo = if ($pip.IpConfiguration) {
      ($pip.IpConfiguration.Id -split '/')[-3,-1] -join '/'
    } else { 'Unassociated' }
    
    $publicIpReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      Name             = $pip.Name
      ResourceGroup    = $pip.ResourceGroupName
      Location         = $pip.Location
      IpAddress        = $pip.IpAddress
      AllocationMethod = $pip.PublicIpAllocationMethod
      SKU              = $pip.Sku.Name
      AssociatedTo     = $associatedTo
      DnsLabel         = $pip.DnsSettings.DomainNameLabel
      IdleTimeoutMin   = $pip.IdleTimeoutInMinutes
      Zones            = ($pip.Zones -join ', ')
    })
    
    # Finding: Standard SKU without zones
    if ($pip.Sku.Name -eq 'Standard' -and -not $pip.Zones) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Low'
        Category         = 'Reliability'
        ResourceType     = 'Public IP'
        ResourceName     = $pip.Name
        ResourceId       = $pip.Id
        Detail           = 'Standard SKU Public IP not zone-redundant'
        Recommendation   = 'Consider zone-redundant deployment for high availability'
      })
    }
  }
  
  # -----------------------------------------------------------
  # 4. PRIVATE ENDPOINTS
  # -----------------------------------------------------------
  Write-Host "  -> Collecting private endpoints..." -NoNewline
  
  $pes = @()
  try { $pes = Get-AzPrivateEndpoint -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($pes.Count) Private Endpoints" -ForegroundColor Cyan
  $subPeCount = $pes.Count
  
  foreach ($pe in $pes) {
    $targetResource = if ($pe.PrivateLinkServiceConnections) {
      ($pe.PrivateLinkServiceConnections[0].PrivateLinkServiceId -split '/')[-1]
    } elseif ($pe.ManualPrivateLinkServiceConnections) {
      ($pe.ManualPrivateLinkServiceConnections[0].PrivateLinkServiceId -split '/')[-1]
    } else { 'Unknown' }
    
    $targetType = if ($pe.PrivateLinkServiceConnections) {
      $pe.PrivateLinkServiceConnections[0].GroupIds -join ', '
    } else { 'Unknown' }
    
    $connectionState = if ($pe.PrivateLinkServiceConnections) {
      $pe.PrivateLinkServiceConnections[0].PrivateLinkServiceConnectionState.Status
    } elseif ($pe.ManualPrivateLinkServiceConnections) {
      $pe.ManualPrivateLinkServiceConnections[0].PrivateLinkServiceConnectionState.Status
    } else { 'Unknown' }
    
    $privateEndpointReport.Add([PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      Name              = $pe.Name
      ResourceGroup     = $pe.ResourceGroupName
      Location          = $pe.Location
      TargetResource    = $targetResource
      TargetType        = $targetType
      VNet              = ($pe.Subnet.Id -split '/')[8]
      Subnet            = ($pe.Subnet.Id -split '/')[-1]
      ConnectionState   = $connectionState
      ProvisioningState = $pe.ProvisioningState
    })
    
    # Finding: Private endpoint not approved
    if ($connectionState -ne 'Approved') {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Network Connectivity'
        ResourceType     = 'Private Endpoint'
        ResourceName     = $pe.Name
        ResourceId       = $pe.Id
        Detail           = "Private endpoint connection state is $connectionState"
        Recommendation   = 'Approve the private endpoint connection on the target resource'
      })
    }
  }
  
  # -----------------------------------------------------------
  # 5. VPN & EXPRESSROUTE GATEWAYS
  # -----------------------------------------------------------
  Write-Host "  -> Checking gateways..." -NoNewline
  
  $vnetGateways = @()
  try {
    # Use Get-AzResource to find gateways without requiring ResourceGroupName
    $gwResources = Get-AzResource -ResourceType 'Microsoft.Network/virtualNetworkGateways' -ErrorAction SilentlyContinue
    foreach ($gwRes in $gwResources) {
      try {
        $gw = Get-AzVirtualNetworkGateway -Name $gwRes.Name -ResourceGroupName $gwRes.ResourceGroupName -ErrorAction SilentlyContinue
        if ($gw) { $vnetGateways += $gw }
      } catch {}
    }
  } catch {}
  
  $erGateways = @()
  try { $erGateways = Get-AzExpressRouteCircuit -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($vnetGateways.Count) VNet GWs, $($erGateways.Count) ER circuits" -ForegroundColor Cyan
  
  foreach ($gw in $vnetGateways) {
    $gatewayReport.Add([PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      Name              = $gw.Name
      ResourceGroup     = $gw.ResourceGroupName
      Location          = $gw.Location
      GatewayType       = $gw.GatewayType
      VpnType           = $gw.VpnType
      SKU               = $gw.Sku.Name
      ActiveActive      = $gw.ActiveActive
      EnableBgp         = $gw.EnableBgp
      ProvisioningState = $gw.ProvisioningState
      VNet              = ($gw.IpConfigurations[0].Subnet.Id -split '/')[8]
    })
    
    # Finding: Gateway not active-active
    if (-not $gw.ActiveActive -and $gw.Sku.Name -notmatch 'Basic') {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Reliability'
        ResourceType     = 'VPN Gateway'
        ResourceName     = $gw.Name
        ResourceId       = $gw.Id
        Detail           = 'VPN Gateway is not configured for Active-Active'
        Recommendation   = 'Enable Active-Active for higher availability'
      })
    }
  }
  
  foreach ($er in $erGateways) {
    $gatewayReport.Add([PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      Name              = $er.Name
      ResourceGroup     = $er.ResourceGroupName
      Location          = $er.Location
      GatewayType       = 'ExpressRoute'
      VpnType           = 'N/A'
      SKU               = $er.Sku.Name
      ActiveActive      = 'N/A'
      EnableBgp         = 'N/A'
      ProvisioningState = $er.ProvisioningState
      VNet              = 'N/A'
    })
  }
  
  # -----------------------------------------------------------
  # 6. LOAD BALANCERS
  # -----------------------------------------------------------
  Write-Host "  -> Checking load balancers..." -NoNewline
  
  $lbs = @()
  try { $lbs = Get-AzLoadBalancer -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($lbs.Count) Load Balancers" -ForegroundColor Cyan
  
  foreach ($lb in $lbs) {
    $frontendCount = ($lb.FrontendIpConfigurations | Measure-Object).Count
    $backendPoolCount = ($lb.BackendAddressPools | Measure-Object).Count
    $ruleCount = ($lb.LoadBalancingRules | Measure-Object).Count
    $probeCount = ($lb.Probes | Measure-Object).Count
    
    $loadBalancerReport.Add([PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      Name              = $lb.Name
      ResourceGroup     = $lb.ResourceGroupName
      Location          = $lb.Location
      SKU               = $lb.Sku.Name
      Type              = if ($lb.FrontendIpConfigurations[0].PublicIpAddress) { 'Public' } else { 'Internal' }
      FrontendCount     = $frontendCount
      BackendPoolCount  = $backendPoolCount
      RuleCount         = $ruleCount
      ProbeCount        = $probeCount
      ProvisioningState = $lb.ProvisioningState
    })
    
    # Finding: Basic SKU load balancer
    if ($lb.Sku.Name -eq 'Basic') {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Medium'
        Category         = 'Reliability'
        ResourceType     = 'Load Balancer'
        ResourceName     = $lb.Name
        ResourceId       = $lb.Id
        Detail           = 'Basic SKU Load Balancer detected'
        Recommendation   = 'Upgrade to Standard SKU for SLA, zone redundancy, and enhanced features'
      })
    }
  }
  
  # -----------------------------------------------------------
  # 7. APPLICATION GATEWAYS
  # -----------------------------------------------------------
  Write-Host "  -> Checking Application Gateways..." -NoNewline
  
  $appGws = @()
  try { $appGws = Get-AzApplicationGateway -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($appGws.Count) App Gateways" -ForegroundColor Cyan
  
  foreach ($appGw in $appGws) {
    $wafEnabled = $appGw.WebApplicationFirewallConfiguration -ne $null -or $appGw.FirewallPolicy -ne $null
    
    $appGatewayReport.Add([PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      Name              = $appGw.Name
      ResourceGroup     = $appGw.ResourceGroupName
      Location          = $appGw.Location
      SKU               = $appGw.Sku.Name
      Tier              = $appGw.Sku.Tier
      Capacity          = $appGw.Sku.Capacity
      WAFEnabled        = $wafEnabled
      HttpListeners     = ($appGw.HttpListeners | Measure-Object).Count
      BackendPools      = ($appGw.BackendAddressPools | Measure-Object).Count
      ProvisioningState = $appGw.ProvisioningState
    })
    
    # Finding: WAF not enabled
    if (-not $wafEnabled) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'High'
        Category         = 'Network Security'
        ResourceType     = 'Application Gateway'
        ResourceName     = $appGw.Name
        ResourceId       = $appGw.Id
        Detail           = 'Web Application Firewall (WAF) is not enabled'
        Recommendation   = 'Enable WAF to protect against common web vulnerabilities'
      })
    }
  }
  
  # -----------------------------------------------------------
  # 8. DNS ZONES
  # -----------------------------------------------------------
  Write-Host "  -> Collecting DNS zones..." -NoNewline
  
  $dnsZones = @()
  try { $dnsZones = Get-AzDnsZone -ErrorAction SilentlyContinue } catch {}
  
  $privateDnsZones = @()
  try { $privateDnsZones = Get-AzPrivateDnsZone -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($dnsZones.Count) public, $($privateDnsZones.Count) private" -ForegroundColor Cyan
  
  foreach ($zone in $dnsZones) {
    $dnsReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      ZoneName         = $zone.Name
      ResourceGroup    = $zone.ResourceGroupName
      ZoneType         = 'Public'
      RecordSetCount   = $zone.NumberOfRecordSets
      LinkedVNets      = 'N/A'
    })
  }
  
  foreach ($zone in $privateDnsZones) {
    $linkedVnets = @()
    try {
      $links = Get-AzPrivateDnsVirtualNetworkLink -ResourceGroupName $zone.ResourceGroupName -ZoneName $zone.Name -ErrorAction SilentlyContinue
      $linkedVnets = $links | ForEach-Object { ($_.VirtualNetworkId -split '/')[-1] }
    } catch {}
    
    $dnsReport.Add([PSCustomObject]@{
      SubscriptionName = $sub.Name
      SubscriptionId   = $sub.Id
      ZoneName         = $zone.Name
      ResourceGroup    = $zone.ResourceGroupName
      ZoneType         = 'Private'
      RecordSetCount   = $zone.NumberOfRecordSets
      LinkedVNets      = ($linkedVnets -join ', ')
    })
  }
  
  # -----------------------------------------------------------
  # 9. NETWORK WATCHER
  # -----------------------------------------------------------
  Write-Host "  -> Checking Network Watcher status..." -NoNewline
  
  $watchers = @()
  try { $watchers = Get-AzNetworkWatcher -ErrorAction SilentlyContinue } catch {}
  
  Write-Host " $($watchers.Count) watchers" -ForegroundColor Cyan
  
  foreach ($watcher in $watchers) {
    $networkWatcherReport.Add([PSCustomObject]@{
      SubscriptionName  = $sub.Name
      SubscriptionId    = $sub.Id
      Name              = $watcher.Name
      ResourceGroup     = $watcher.ResourceGroupName
      Location          = $watcher.Location
      ProvisioningState = $watcher.ProvisioningState
    })
  }
  
  # Check for regions without Network Watcher
  $vnetLocations = $vnets | Select-Object -ExpandProperty Location -Unique
  $watcherLocations = $watchers | Select-Object -ExpandProperty Location -Unique
  
  foreach ($loc in $vnetLocations) {
    if ($loc -notin $watcherLocations) {
      $findings.Add([PSCustomObject]@{
        SubscriptionName = $sub.Name
        SubscriptionId   = $sub.Id
        Severity         = 'Low'
        Category         = 'Monitoring'
        ResourceType     = 'Network Watcher'
        ResourceName     = "Missing in $loc"
        ResourceId       = $sub.Id
        Detail           = "No Network Watcher in region $loc where VNets exist"
        Recommendation   = 'Enable Network Watcher for network diagnostics and monitoring'
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
    SubscriptionName  = $sub.Name
    SubscriptionId    = $sub.Id
    VNetCount         = $subVnetCount
    NSGCount          = $subNsgCount
    PublicIPCount     = $subPipCount
    PrivateEndpoints  = $subPeCount
    RiskyNSGRules     = $riskyRuleCount
    HighFindings      = $highFindings
    MediumFindings    = $medFindings
    LowFindings       = $lowFindings
    TotalFindings     = $highFindings + $medFindings + $lowFindings
  })
  
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
  $subscriptionSummary | Format-Table SubscriptionName, VNetCount, NSGCount, PublicIPCount, PrivateEndpoints, RiskyNSGRules, TotalFindings -AutoSize
}

Write-Host "`n=== NSG Risk Analysis ===" -ForegroundColor Cyan
$criticalRules = $nsgRuleReport | Where-Object { $_.RiskLevel -eq 'Critical' }
$highRiskRules = $nsgRuleReport | Where-Object { $_.RiskLevel -eq 'High' }
Write-Host "  Critical risk rules: $($criticalRules.Count)" -ForegroundColor $(if ($criticalRules.Count -gt 0) { 'Red' } else { 'Green' })
Write-Host "  High risk rules:     $($highRiskRules.Count)" -ForegroundColor $(if ($highRiskRules.Count -gt 0) { 'Yellow' } else { 'Green' })

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

$XlsxPath = Join-Path $OutPath 'Network_Audit.xlsx'

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
Export-Sheet -Data $vnetReport -WorksheetName 'VNets' -TableName 'VNets'
Export-Sheet -Data $subnetReport -WorksheetName 'Subnets' -TableName 'Subnets'
Export-Sheet -Data $peeringReport -WorksheetName 'Peerings' -TableName 'Peerings'
Export-Sheet -Data $nsgReport -WorksheetName 'NSGs' -TableName 'NSGs'
Export-Sheet -Data $nsgRuleReport -WorksheetName 'NSG_Rules' -TableName 'NSGRules'
Export-Sheet -Data $publicIpReport -WorksheetName 'Public_IPs' -TableName 'PublicIPs'
Export-Sheet -Data $privateEndpointReport -WorksheetName 'Private_Endpoints' -TableName 'PrivateEndpoints'
Export-Sheet -Data $gatewayReport -WorksheetName 'Gateways' -TableName 'Gateways'
Export-Sheet -Data $loadBalancerReport -WorksheetName 'Load_Balancers' -TableName 'LoadBalancers'
Export-Sheet -Data $appGatewayReport -WorksheetName 'App_Gateways' -TableName 'AppGateways'
Export-Sheet -Data $dnsReport -WorksheetName 'DNS_Zones' -TableName 'DNSZones'
Export-Sheet -Data $networkWatcherReport -WorksheetName 'Network_Watchers' -TableName 'NetworkWatchers'
Export-Sheet -Data $findings -WorksheetName 'Findings' -TableName 'Findings'

$overallSummary = @(
  [PSCustomObject]@{ Metric='Audit Date';Value=(Get-Date -Format 'yyyy-MM-dd HH:mm') }
  [PSCustomObject]@{ Metric='Subscriptions Audited';Value=$subscriptions.Count }
  [PSCustomObject]@{ Metric='Virtual Networks';Value=$vnetReport.Count }
  [PSCustomObject]@{ Metric='Subnets';Value=$subnetReport.Count }
  [PSCustomObject]@{ Metric='VNet Peerings';Value=$peeringReport.Count }
  [PSCustomObject]@{ Metric='NSGs';Value=$nsgReport.Count }
  [PSCustomObject]@{ Metric='NSG Rules Analyzed';Value=$nsgRuleReport.Count }
  [PSCustomObject]@{ Metric='Critical/High Risk Rules';Value=($nsgRuleReport | Where-Object { $_.RiskLevel -in @('Critical','High') }).Count }
  [PSCustomObject]@{ Metric='Public IPs';Value=$publicIpReport.Count }
  [PSCustomObject]@{ Metric='Private Endpoints';Value=$privateEndpointReport.Count }
  [PSCustomObject]@{ Metric='VPN/ER Gateways';Value=$gatewayReport.Count }
  [PSCustomObject]@{ Metric='Load Balancers';Value=$loadBalancerReport.Count }
  [PSCustomObject]@{ Metric='Application Gateways';Value=$appGatewayReport.Count }
  [PSCustomObject]@{ Metric='High Findings';Value=$totalHigh }
  [PSCustomObject]@{ Metric='Medium Findings';Value=$totalMed }
  [PSCustomObject]@{ Metric='Low Findings';Value=$totalLow }
)
Export-Sheet -Data $overallSummary -WorksheetName 'Summary' -TableName 'Summary'

Write-Host "`nExcel export complete -> $XlsxPath" -ForegroundColor Green
Write-Host "`n+ Audit complete!" -ForegroundColor Green
Write-Host "Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')`n" -ForegroundColor Gray
