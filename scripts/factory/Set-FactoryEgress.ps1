<#
.SYNOPSIS Limits where the factory VM can connect: the Anthropic API, GitHub, NuGet and the Bifrost gateway, and nothing else.
.DESCRIPTION An owner step, run elevated inside the factory VM (for example over PowerShell Direct), never by the
factory. It sets Windows Firewall's default outbound action to Block on every profile and adds allow rules, each
named "House Style egress ...":
- DNS (port 53) for every account, only to the DNS servers the VM is configured with, so names still resolve.
- For the factory account only (-User): TCP 443 to the Anthropic API's published range, GitHub's published ranges
  (api.github.com/meta: web, api, git, actions), and the current addresses of NuGet and the other hosts in -Hosts.
Windows Firewall matches addresses, not names, and CDN addresses change, so run it again after a NuGet or GitHub
outage that looks like a block, and monthly with Reset-FactoryVM.ps1 -Refresh. Anything else the VM needs (Windows
Update, time sync) stays blocked; the nightly reset puts the VM back on its base anyway.
-DryRun lists the rules without changing anything. -Remove deletes the rules and sets outbound back to Allow.
.EXAMPLE scripts\factory\Set-FactoryEgress.ps1 -DryRun
.EXAMPLE scripts\factory\Set-FactoryEgress.ps1
#>
param(
    [string]$User = "factory",
    [string[]]$Hosts = @("api.anthropic.com", "console.anthropic.com", "claude.ai", "statsig.anthropic.com",
                         "api.nuget.org", "globalcdn.nuget.org", "www.nuget.org", "dotnetcli.azureedge.net", "builds.dotnet.microsoft.com",
                         "aigw.praeviderant.com"),   # the owner's Bifrost gateway, for the e2e AI scenarios
    [switch]$DryRun,
    [switch]$Remove
)
$ErrorActionPreference = "Stop"
$prefix = "House Style egress"
$anthropic = @("160.79.104.0/23")   # the Anthropic API's published address range (docs.anthropic.com, IP addresses)

if (-not $DryRun) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw "Run this elevated, inside the factory VM." }
}

if ($Remove) {
    if ($DryRun) { Write-Output "Would remove the '$prefix' rules and set outbound back to Allow."; return }
    Get-NetFirewallRule -DisplayName "$prefix*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Set-NetFirewallProfile -All -DefaultOutboundAction Allow
    Write-Output "Removed the '$prefix' rules. Outbound is allowed again."
    return
}

$sid = (New-Object Security.Principal.NTAccount $User).Translate([Security.Principal.SecurityIdentifier]).Value
$sddl = "O:LSD:(A;;CC;;;$sid)"   # the rule applies to this account's processes only

$meta = Invoke-RestMethod "https://api.github.com/meta" -UseBasicParsing
$github = @($meta.web + $meta.api + $meta.git + $meta.actions | Where-Object { $_ -and $_ -notmatch ':' } | Sort-Object -Unique)   # IPv4
$resolved = @(foreach ($h in $Hosts) {
    try { Resolve-DnsName $h -Type A -ErrorAction Stop | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress } }
    catch { Write-Warning "Couldn't resolve $h, so it gets no rule." }
}) | Sort-Object -Unique

# DNS only to the resolvers the VM is configured with, so port 53 can't reach anywhere else.
$resolvers = @(Get-DnsClientServerAddress -AddressFamily IPv4 | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ } | Sort-Object -Unique)
if (-not $resolvers.Count) { throw "The VM has no DNS servers configured, so there's nothing to allow DNS to." }
$rules = @(
    @{ Name = "$prefix DNS (UDP)"; Protocol = "UDP"; RemotePort = 53; RemoteAddress = $resolvers; Owner = $null },
    @{ Name = "$prefix DNS (TCP)"; Protocol = "TCP"; RemotePort = 53; RemoteAddress = $resolvers; Owner = $null },
    @{ Name = "$prefix Anthropic"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = $anthropic; Owner = $sddl },
    @{ Name = "$prefix GitHub"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = $github; Owner = $sddl },
    @{ Name = "$prefix hosts"; Protocol = "TCP"; RemotePort = 443; RemoteAddress = $resolved; Owner = $sddl }
)
if ($DryRun) {
    foreach ($r in $rules) { Write-Output "$($r.Name): $($r.Protocol) $($r.RemotePort) to $(@($r.RemoteAddress).Count) address ranges$(if ($r.Owner) { " for $User only" })" }
    Write-Output "Then: default outbound action Block on every profile."
    return
}

Get-NetFirewallRule -DisplayName "$prefix*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
foreach ($r in $rules) {
    if (-not @($r.RemoteAddress).Count) { continue }
    $a = @{ DisplayName = $r.Name; Direction = "Outbound"; Action = "Allow"; Protocol = $r.Protocol; RemotePort = $r.RemotePort; RemoteAddress = $r.RemoteAddress; Profile = "Any" }
    if ($r.Owner) { $a.LocalUser = $r.Owner }
    New-NetFirewallRule @a | Out-Null
}
Set-NetFirewallProfile -All -DefaultOutboundAction Block
Write-Output "Outbound is blocked except DNS, and HTTPS from $User to Anthropic, GitHub and $($Hosts.Count) more hosts. Undo with -Remove."
