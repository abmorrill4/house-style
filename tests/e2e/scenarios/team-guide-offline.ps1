# A team style guide that goes offline lints with the copy House Style saved last (#246). Lint Topic with the team guide
# in place: its own rule fires and a copy is saved in the background. The folder goes missing: Lint Topic still finds
# the team's own rule, labeled Our rules, and the status says it linted with the copy and when it was saved. The folder
# comes back: the notice goes.
param($Fixture)
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..\..")
$guide = Join-Path (Get-E2ERoot) ("team-guide-offline-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
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
$topic = Join-Path $run.Project "Content\team-guide-offline.htm"
Set-Content $topic -Encoding UTF8 -Value @'
<?xml version="1.0" encoding="utf-8"?>
<html xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd"><head><title>Transfer limits</title></head><body><h1>Transfer limits</h1>
<p>You can frobnicate your daily limit.</p>
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

# 1. Online: the team's rule fires, and a copy of the guide is saved in the background.
$p = Lint-Topic
$team = Row $p "frobnicate"
Assert-E2E ($null -ne $team -and $team.source -like "Our rules*") "Lint Topic found the team's own rule (rows: $(Sources $p))"
$copies = Join-Path $run.AppData "team-guide-copies"
Wait-Until { @(Get-ChildItem $copies -Recurse -Filter "house-style-copy.record" -File -ErrorAction SilentlyContinue).Count -gt 0 } 60 "the saved copy of the team guide" | Out-Null

# 2. Offline: the copy stands in, its rules say Our rules, and the status names the folder and the copy's date.
$away = "$guide-offline"
Rename-Item $guide (Split-Path $away -Leaf)
try {
    $p = Lint-Topic
    $team = Row $p "frobnicate"
    Assert-E2E ($null -ne $team) "the team's own rule still fires from the copy (rows: $(Sources $p))"
    Assert-E2E ($team.source -like "Our rules*") "the copied rule says Our rules (got: $($team.source))"
    Assert-E2E ($p.status -like "*linted with the copy it saved on*") "the status says it used the saved copy (status: $($p.status))"
    Assert-E2E ($p.status -like "*$guide*") "the status names the team's folder (status: $($p.status))"
    Assert-E2E ($p.status -notlike "*team-guide-copies*") "the status never names the copy's folder (status: $($p.status))"
    Assert-E2E (-not (Test-Path $guide)) "linting didn't recreate the team's folder"
}
finally { if (Test-Path $away) { Rename-Item $away (Split-Path $guide -Leaf) } }

# 3. Back online: no notice.
$p = Lint-Topic
Assert-E2E ($p.status -notlike "*linted with the copy*") "the notice is gone once the folder is back (status: $($p.status))"
