<#
.SYNOPSIS Builds the shipped whats-new.md (or one version's release notes) from the hand-written file plus the per-PR
notes in changes/.
.DESCRIPTION Every PR that changes what writers see adds changes/<name>.md with one or two "- " bullets. A release's
notes are the changes/ files added between the previous v* tag and the release commit, so nothing ever edits a shared
section and nothing conflicts. Hand-written sections in src/FlareAI.Flare/whats-new.md stay; a generated version that
already has a hand-written section gets its bullets added to the end of it. Notes are grouped under New, Improved and
Fixed. A note may start with "Kind: New", "Kind: Improved" or "Kind: Fixed"; with no kind it counts as Improved. -Check
lists bullets that break the copy rules and notes in one release that open the same way.
  Build-WhatsNew.ps1 -Version 0.3.5 -Out dist/stage/whats-new.md      # the file the plugin ships
  Build-WhatsNew.ps1 -Version 0.3.5 -NotesOnly                         # one version's notes, for the GitHub release
#>
param([Parameter(Mandatory)][string]$Version, [string]$Out, [switch]$NotesOnly, [switch]$Check,[string]$Repo = (Split-Path (Split-Path $PSScriptRoot)), [string]$Head = "HEAD")
$ErrorActionPreference = "Stop"

# Bullets from the changes/ files added in (from, to], in the order they were added. Each has Kind, Lines (as written),
# Text (one line, no "- "), File and BadKind (the text of an unrecognised Kind line, or $null).
function Get-Bullets([string]$from, [string]$to) {
    $range = if ($from) { "$from..$to" } else { $to }
    $files = @(git -C $Repo log --reverse --diff-filter=A --name-only --format= $range -- changes/ | Where-Object { $_ -like "changes/*.md" -and $_ -ne "changes/README.md" })
    $list = New-Object System.Collections.Generic.List[object]
    foreach ($f in $files) {
        # Added and later removed: not shipped. (Windows PowerShell 5.1 turns git's stderr into an error, hence the try.)
        $text = try { $ErrorActionPreference = "Continue"; git -C $Repo show "${to}:$f" 2>$null } catch { $null }
        if ($LASTEXITCODE -or -not $text) { continue }
        $all = @($text)
        $all[0] = ([string]$all[0]) -replace '^(﻿|ï»¿)', ''
        $all[0] = ([string]$all[0]) -replace '^(﻿|ï»¿)|^(﻿|ï»¿)', ''   # a byte order mark, however git's output was decoded
        $kind = "Improved"; $bad = $null
        $first = -1
        for ($i = 0; $i -lt $all.Count; $i++) { if ($all[$i].Trim()) { $first = $i; break } }
        if ($first -ge 0 -and $all[$first] -match '^\s*Kind:') {
            if ($all[$first] -match '^Kind:\s*(New|Improved|Fixed)\s*$') { $k = $Matches[1]; $kind = $k.Substring(0, 1).ToUpper() + $k.Substring(1).ToLower() }
            else { $bad = $all[$first].Trim() }
            $all = @(for ($i = 0; $i -lt $all.Count; $i++) { if ($i -ne $first) { $all[$i] } })
        }
        $cur = $null
        foreach ($l in $all) {
            if ($l -match '^\s*- ') { $cur = New-Object System.Collections.Generic.List[string]; $cur.Add($l); $list.Add(@{ Lines = $cur; File = $f; Kind = $kind; BadKind = $bad }) }
            elseif ($l -match '^  \S' -and $cur) { $cur.Add($l) }
        }
    }
    foreach ($b in $list) {
        $lines = [string[]]$b.Lines.ToArray()
        $parts = @(for ($i = 0; $i -lt $lines.Count; $i++) { $t = $lines[$i].Trim(); if ($i -eq 0) { $t = $t -replace '^- ', '' }; $t })
        [pscustomobject]@{ Kind = $b.Kind; Lines = $lines; Text = ($parts -join ' '); File = $b.File; BadKind = $b.BadKind }
    }
}

# The grouped lines: ### New, ### Improved, ### Fixed, each with its bullets in the order added. Empty groups are left out.
function Format-Groups($items) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($kind in "New", "Improved", "Fixed") {
        $g = @(@($items) | Where-Object { $_.Kind -eq $kind })
        if (-not $g.Count) { continue }
        $out.Add("### $kind"); $out.Add("")
        foreach ($b in $g) { foreach ($l in $b.Lines) { $out.Add($l) } }
        $out.Add("")
    }
    while ($out.Count -and $out[$out.Count - 1] -eq "") { $out.RemoveAt($out.Count - 1) }
    $out.ToArray()
}

# Problems with the notes of one release, each starting with the file name. Nothing when the notes are clean.
function Test-Notes($items) {
    $problems = New-Object System.Collections.Generic.List[string]
    $arrow = [string][char]0x2192
    $dash = " " + [char]0x2014 + " "
    $seen = @{}; $badSeen = @{}
    foreach ($b in @($items)) {
        $name = Split-Path $b.File -Leaf
        if ($b.BadKind -and -not $badSeen.ContainsKey($b.File)) { $badSeen[$b.File] = 1; $problems.Add("${name}: unrecognised kind '$($b.BadKind)'. Use Kind: New, Kind: Improved or Kind: Fixed.") }
        $t = $b.Text -replace '`[^`]*`', '' -replace '\]\([^)]*\)', ']'
        if ($t -cnotmatch '^[A-Z]') { $problems.Add("${name}: does not start with a capital letter: $($b.Text)") }
        if ($t -notmatch '\.$') { $problems.Add("${name}: does not end with a full stop: $($b.Text)") }
        if ($t.Contains(':') -or $t.Contains(';') -or $t.Contains('->') -or $t.Contains($arrow)) { $problems.Add("${name}: has a colon, semicolon or arrow: $($b.Text)") }
        if ($t.StartsWith('**') -or $t.Contains($dash)) { $problems.Add("${name}: has a tagline: $($b.Text)") }
        if ([regex]::Matches($t, '[.!?](\s|$)').Count -gt 2) { $problems.Add("${name}: has more than two sentences: $($b.Text)") }
        $words = @(($b.Text.ToLower() -replace '[^\p{L}\p{N}\s]', '') -split '\s+' | Where-Object { $_ })
        $key = ($words | Select-Object -First 5) -join ' '
        if ($seen.ContainsKey($key)) { $problems.Add("${name}: same opening as $(Split-Path $seen[$key] -Leaf): $key") }
        else { $seen[$key] = $b.File }
    }
    $problems.ToArray()
}

function Compare-Version($a, $b) { [version]($a -replace '-.*$', '') -ge [version]($b -replace '-.*$', '') }

# Every released version, newest first, plus the one being built.
$tags = @(git -C $Repo tag -l "v*" --sort=-v:refname | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' })
# A CI build (0.0.0-ci) shows what the next release would: the last tag's next patch version.
if ($Version -notmatch '^\d+\.\d+\.\d+$' -and $tags.Count) { $p = $tags[0].Substring(1).Split('.'); $Version = "$($p[0]).$($p[1]).$([int]$p[2] + 1)" }
$versions = @(@{ Version = $Version; To = $Head; From = ($tags | Where-Object { -not (Compare-Version ($_.Substring(1)) $Version) } | Select-Object -First 1) })
for ($i = 0; $i -lt $tags.Count; $i++) {
    if (Compare-Version ($tags[$i].Substring(1)) $Version) { continue }   # the version being built, or newer: not shipped yet
    $versions += @{ Version = $tags[$i].Substring(1); To = $tags[$i]; From = $(if ($i + 1 -lt $tags.Count) { $tags[$i + 1] } else { $null }) }
}
$generated = [ordered]@{}
$items = @{}
foreach ($v in $versions) {
    $b = @(Get-Bullets $v.From $v.To)
    if ($b.Count) { $items[$v.Version] = $b; $generated[$v.Version] = @(Format-Groups $b) }
}

if ($Check) {
    if ($items.ContainsKey($Version)) { Test-Notes $items[$Version] }
    return
}

if ($NotesOnly) {
    $handFile = Join-Path $Repo "docs/release-notes/$Version.md"
    $new = if ($generated.Contains($Version)) { @($generated[$Version]) } else { @() }
    $head = @(); $tail = @()
    if (Test-Path $handFile) {
        # The hand-written file gives the summary and any sections; its closing "## Install or update" section stays last.
        $text = @(Get-Content $handFile)
        $at = [array]::FindLastIndex([string[]]$text, [Predicate[string]]{ param($l) $l -match '^##\s+(Install|Update)' })
        if ($at -lt 0) { $head = $text }
        else {
            if ($at -gt 0) { $head = $text[0..($at - 1)] }
            $tail = $text[$at..($text.Count - 1)]
        }
        while ($head.Count -and -not "$($head[$head.Count - 1])".Trim()) { $head = if ($head.Count -gt 1) { $head[0..($head.Count - 2)] } else { @() } }
    }
    $lines = @($head)
    if ($new.Count) { if ($lines.Count) { $lines += "" }; $lines += $new }
    elseif (-not $lines.Count) { $lines = @("Small fixes to make House Style more reliable.") }
    if ($lines.Count) { $lines += "" }
    if ($tail.Count) { $lines += $tail }
    else {
        $lines += "## Install or update"
        $lines += ""
        $lines += 'House Style 0.3.0 and later update themselves when you close Flare. For a new install, see [Install House Style](https://gethousestyle.com/install.html).'
        $lines += ""
        $lines += 'If you download the zip, right-click it, select Properties, select Unblock, and then unzip it.'
    }
    $lines -join "`n"
    return
}

# Merge into the hand-written file: generated bullets join an existing "## version" section, or a new section goes in
# above the first hand-written one older than it.
$source = Get-Content (Join-Path $Repo "src/FlareAI.Flare/whats-new.md")
$heading = '^##\s+v?(\d+\.\d+\.\d+)\b'
$result = New-Object System.Collections.Generic.List[string]
$pending = [System.Collections.Generic.List[string]]@($generated.Keys)
$current = $null
function Flush-Section { if ($current -and $generated.Contains($current)) { $result.Add(""); $generated[$current] | ForEach-Object { $result.Add($_) } } }
foreach ($line in $source) {
    if ($line -match $heading) {
        Flush-Section
        $v = $Matches[1]
        foreach ($p in @($pending)) {
            if ($p -ne $v -and (Compare-Version $p $v)) { $result.Add("## $p"); $result.Add(""); $generated[$p] | ForEach-Object { $result.Add($_) }; $result.Add(""); [void]$pending.Remove($p) }
        }
        [void]$pending.Remove($v)
        $current = $v
    }
    $result.Add($line)
}
Flush-Section
foreach ($p in $pending) { $result.Add(""); $result.Add("## $p"); $result.Add(""); $generated[$p] | ForEach-Object { $result.Add($_) } }
$text = ($result -join "`n").TrimEnd() + "`n"
if ($Out) { [IO.File]::WriteAllText($Out, $text, (New-Object Text.UTF8Encoding($false))) } else { $text }
