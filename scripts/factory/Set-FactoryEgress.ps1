<#
.SYNOPSIS Limits where either VM can connect (factory-runtime spec section 7, the egress decision).
.DESCRIPTION A setup step, run elevated inside the VM (for example over PowerShell Direct), never by the factory or a
task. It sets Windows Firewall's default outbound action to Block on every profile and adds allow rules, each named
"House Style egress ...". DNS (port 53) goes only to the VM's own resolvers, for every account (gap 2).
-Role agents, on the agents VM:
- the agent user (hsagent): TCP 443 to the Anthropic API's published range only. NuGet is reachable only during a
  restore job, through a rule Invoke-AgentJob adds and the sweep removes, holding NuGet's addresses only;
- the script user (factory): TCP 443 to GitHub's published ranges (api.github.com/meta).
-Role flare, on the Flare VM: as before, the factory account reaches Anthropic, GitHub and the hosts in -Hosts. The host
cuts the network while a fix's code builds and runs there.
Windows Firewall matches addresses, not names, and CDN addresses change, so run it again at each -Refresh. -DryRun
lists the rules without changing anything. -Remove deletes the rules and sets outbound back to Allow.
.EXAMPLE scripts\factory\Set-FactoryEgress.ps1 -Role agents -DryRun
#>
param(
    [ValidateSet("agents", "flare")][string]$Role = "flare",
    [string]$AgentUser = "hsagent",
    [string]$ScriptUser = "factory",
    [string[]]$Hosts = @("api.anthropic.com", "console.anthropic.com", "claude.ai", "statsig.anthropic.com",
                         "api.nuget.org", "globalcdn.nuget.org", "www.nuget.org", "dotnetcli.azureedge.net", "builds.dotnet.microsoft.com"),
    [switch]$DryRun,
    [switch]$Remove
)
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "Egress.psm1") -Force

if (-not $DryRun) {
    $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) { throw "Run this elevated, inside the VM." }
}
if ($Remove) {
    if ($DryRun) { Write-Output "Would remove the '$EgressPrefix' rules and set outbound back to Allow."; return }
    Get-NetFirewallRule -DisplayName "$EgressPrefix*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    Set-NetFirewallProfile -All -DefaultOutboundAction Allow
    Write-Output "Removed the '$EgressPrefix' rules. Outbound is allowed again."
    return
}

function Sid([string]$user) { (New-Object Security.Principal.NTAccount $user).Translate([Security.Principal.SecurityIdentifier]).Value }
$meta = Invoke-RestMethod "https://api.github.com/meta" -UseBasicParsing
$github = @($meta.web + $meta.api + $meta.git + $meta.actions | Where-Object { $_ -and $_ -notmatch ':' } | Sort-Object -Unique)   # IPv4
$resolvers = @(Get-DnsClientServerAddress -AddressFamily IPv4 | ForEach-Object { $_.ServerAddresses } | Where-Object { $_ } | Sort-Object -Unique)
if ($Role -eq "agents") {
    $rules = Get-EgressRules agents @{ agent = (Sid $AgentUser); script = (Sid $ScriptUser) } $resolvers $github
}
else {
    $resolved = @(foreach ($h in $Hosts) {
        try { Resolve-DnsName $h -Type A -ErrorAction Stop | Where-Object { $_.IPAddress } | ForEach-Object { $_.IPAddress } }
        catch { Write-Warning "Couldn't resolve $h, so it gets no rule." }
    }) | Sort-Object -Unique
    $rules = Get-EgressRules flare @{ factory = (Sid $ScriptUser) } $resolvers $github $resolved
}
if ($DryRun) {
    foreach ($r in $rules) { Write-Output "$($r.Name): $($r.Protocol) $($r.RemotePort) to $(@($r.RemoteAddress).Count) address ranges$(if ($r.LocalUser) { ' for one account' })" }
    Write-Output "Then: default outbound action Block on every profile."
    return
}

Get-NetFirewallRule -DisplayName "$EgressPrefix*" -ErrorAction SilentlyContinue | Remove-NetFirewallRule
foreach ($r in $rules) { if (@($r.RemoteAddress).Count) { New-EgressRule $r } }
Set-NetFirewallProfile -All -DefaultOutboundAction Block
if ($Role -eq "agents") { Write-Output "Outbound is blocked except DNS, HTTPS from $AgentUser to Anthropic's API range, and HTTPS from $ScriptUser to GitHub. Undo with -Remove." }
else { Write-Output "Outbound is blocked except DNS, and HTTPS from $ScriptUser to Anthropic, GitHub and $($Hosts.Count) more hosts. Undo with -Remove." }
