# A team style guide (a copy of the starter project plus one rule of its own): its issues say Our rules and none say
# Starter rules. The folder goes missing: Lint Topic says it can't reach it, the team's own rule is gone and the starter
# rules stand in, labeled Starter rules. The folder comes back: the notice goes and the team's rules are back (#235).
param($Fixture)
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$guide = Join-Path (Get-E2ERoot) ("team-guide-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$starter = Join-Path $repo "styleguide"
foreach ($f in Get-ChildItem $starter -Recurse -File) {
    $rel = $f.FullName.Substring($starter.Length + 1)
    if ($rel -like "Content\Resources\StyleGuide\Google\*") { continue }
    $dest = Join-Path $guide $rel
    New-Item -ItemType Directory -Force (Split-Path $dest) | Out-Null
    Copy-Item $f.FullName $dest
}
# The team's own rule, in its own topic of the copied guide.
Set-Content (Join-Path $guide "Content\Resources\StyleGuide\Company\Team-words.htm") -Encoding UTF8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd"><head><title>Team words</title></head><body><h1>Team words</h1>
<table><tr><th>Avoid</th><th>Use</th><th>Example</th></tr><tr><td>frobnicate</td><td>adjust</td><td>We frobnicate the limit.</td></tr></table>
</body></html>
'@

$run = Start-FlareE2E -Fixture $Fixture -Force -Settings @{ StyleLayers = $guide }
$topic = Join-Path $run.Project "Content\team-guide-source.htm"
Set-Content $topic -Encoding UTF8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd"><head><title>Transfer limits</title></head><body><h1>Transfer limits</h1>
<p>You can frobnicate your daily limit. We utilize your settings to do this.</p>
</body></html>
'@
Send-E2E open $topic | Out-Null

$script:last = $null
function Lint-Topic {
    Invoke-Ribbon "Lint Topic"
    $p = Wait-Until { $s = Wait-PaneDone; if ($s.pane.panel.id -ne $script:last) { $s.pane.panel } } 180 "a new Lint Topic result"
    $script:last = $p.id
    return $p
}
function Row($p, $word) { @($p.rows | Where-Object { $_.excerpt -like "*$word*" }) | Select-Object -First 1 }
function Sources($p) { ($p.rows | ForEach-Object { $_.source }) -join " | " }

# 1. The team's guide is in use: its rules say Our rules, nothing says Starter rules.
$p = Lint-Topic
$team = Row $p "frobnicate"
Assert-E2E ($null -ne $team) "Lint Topic found the team's own rule (rows: $(Sources $p))"
Assert-E2E ($team.source -like "Our rules*") "the team's rule says Our rules (got: $($team.source))"
$copied = Row $p "utilize"
Assert-E2E ($null -ne $copied -and $copied.source -like "Our rules*") "a rule the team copied from the starter says Our rules (got: $($copied.source))"
Assert-E2E (@($p.rows | Where-Object { $_.source -like "Starter rules*" }).Count -eq 0) "no issue says Starter rules with a team guide (rows: $(Sources $p))"
Assert-E2E ($p.status -notmatch "couldn't be read") "no guide notice while the folder is there (status: $($p.status))"

# 2. The folder goes missing (offline, renamed): the pane says so, and the starter rules stand in.
$away = "$guide-offline"
Rename-Item $guide (Split-Path $away -Leaf)
try {
    $p = Lint-Topic
    Assert-E2E ($p.status -match "couldn't be read" -and $p.status -match "Can't reach") "the status says the team's guide can't be reached (status: $($p.status))"
    Assert-E2E ($null -eq (Row $p "frobnicate")) "the team's own rule is gone while its folder is missing (rows: $(Sources $p))"
    $starterRow = Row $p "utilize"
    Assert-E2E ($null -ne $starterRow -and $starterRow.source -like "Starter rules*") "the starter rule says Starter rules (got: $($starterRow.source))"
}
finally { if (Test-Path $away) { Rename-Item $away (Split-Path $guide -Leaf) } }

# 3. The folder is back: no notice, the team's rules again.
$p = Lint-Topic
Assert-E2E ($p.status -notmatch "couldn't be read") "the notice is gone once the folder is back (status: $($p.status))"
$team = Row $p "frobnicate"
Assert-E2E ($null -ne $team -and $team.source -like "Our rules*") "the team's rule is back and says Our rules (rows: $(Sources $p))"
Assert-E2E (@($p.rows | Where-Object { $_.source -like "Starter rules*" }).Count -eq 0) "no issue says Starter rules once the folder is back (rows: $(Sources $p))"
