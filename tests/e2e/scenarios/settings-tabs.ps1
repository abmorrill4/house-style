# Settings' tabs through the e2e hook's dialogTab verb: Style guide lists the team guide, Advanced shows Fast model, and
# a tab that doesn't exist fails (#257, for #91).
param($Fixture)
$guide = Join-Path (Get-E2ERoot) ("guide-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
New-Item -ItemType Directory -Force (Join-Path $guide "Content") | Out-Null
Set-Content (Join-Path $guide "Team.flprj") "<CatapultProject />"

$run = Start-FlareE2E -Fixture $Fixture -Force -Settings @{ StyleLayers = $guide }
function Settings { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Settings" } }

Send-E2E command settings -NoWait   # a dialog: the command answers when it closes
Wait-Until { Settings } 30 "the Settings dialog" | Out-Null

Send-E2E dialogTab "Style guide" -Extra @{ title = "Settings" } | Out-Null
$d = Settings
Assert-E2E (@($d.items) -contains $guide) "Style guide lists the team guide (items: $(@($d.items) -join ' | '))"

Send-E2E dialogTab "Advanced" -Extra @{ title = "Settings" } | Out-Null
$d = Settings
Assert-E2E (@($d.text | Where-Object { $_ -like "Fast model*" }).Count -gt 0) "Advanced shows Fast model (text: $(@($d.text) -join ' | '))"

try { Send-E2E dialogTab "Nope" -Extra @{ title = "Settings" } | Out-Null; $err = "" } catch { $err = $_.Exception.Message }
Assert-E2E ($err -like "*No tab Nope*") "dialogTab on a missing tab fails with No tab Nope (got: $err)"

Send-E2E dialogClick "Cancel" -Extra @{ title = "Settings" } | Out-Null
Wait-Until { -not (Settings) } 15 "Settings to close" | Out-Null
Remove-Item $guide -Recurse -Force -ErrorAction SilentlyContinue
