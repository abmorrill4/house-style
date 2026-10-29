# Rules with the keyboard only: real keystrokes, no clicks. Search has focus when Rules opens, Enter moves to the list,
# Tab goes list, details, then the buttons left to right, Alt+I opens Edit Rule, Esc closes it, Ctrl+F goes back to
# search, Esc closes Rules. Screenshots of each step go next to the run (#99). Run it at 125% scaling too.
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$guide = Join-Path (Get-E2ERoot) ("guide-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$topic = Join-Path $guide "Content\Resources\StyleGuide\Words.htm"
New-Item -ItemType Directory -Force (Split-Path $topic) | Out-Null
Set-Content (Join-Path $guide "Team.flprj") "<CatapultProject />"
Set-Content $topic -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html><body><h1>Our words</h1>
<ul><li>Say members when you mean our customers.</li><li>Say sign in for the start of a session.</li></ul>
</body></html>
"@

$run = Start-FlareE2E -Fixture $Fixture -Force -Settings @{ StyleLayers = $guide }
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 400 }
function Rules { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Rules" } }
function Focused { (Rules).focus }

Invoke-Ribbon "Rules"
Wait-Until { $d = Rules; $d -and @($d.rows).Count -gt 0 } 60 "the Rules window with its list" | Out-Null
Save-E2EScreenshot "rules-1-open"
Assert-E2E ((Focused) -eq "Search rules") "search has focus when Rules opens (focus: $(Focused))"

Keys "say members"
Keys "{ENTER}"
$d = Wait-Until { $d = Rules; if ($d.focus -eq "Rules" -and @($d.rows | Where-Object { $_.selected -and $_.text -like "Say members*" }).Count) { $d } } 15 "Enter to select the matching rule in the list"
Save-E2EScreenshot "rules-2-search"

$order = @()
foreach ($i in 1..5) { Keys "{TAB}"; $order += Focused }
Save-E2EScreenshot "rules-3-buttons"
# Change Level and Enable Rule are off for an enabled bullet rule, so Tab skips them.
$want = "Details for the selected rule", "Edit Rule", "Delete Rule", "Disable Rule", "Close"
Assert-E2E (($order -join " > ") -eq ($want -join " > ")) "Tab goes details, then the buttons left to right (got: $($order -join ' > '))"

Keys "%i"
$e = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Edit Rule" } } 15 "Alt+I to open Edit Rule"
Save-E2EScreenshot "rules-4-edit"
Assert-E2E ($e.focus -eq "Rule") "Edit Rule opens with the wording box focused (focus: $($e.focus))"
Keys "{ESC}"
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Edit Rule" }) } 15 "Esc to close Edit Rule" | Out-Null
Assert-E2E ((Get-Content $topic -Raw) -like "*Say members when you mean our customers.*") "Esc changed nothing"

Keys "^f"
Assert-E2E ((Focused) -eq "Search rules") "Ctrl+F goes back to search (focus: $(Focused))"

# A built-in check: search for it, reach Disable Rule by Tab, and its dialog says what the check is. Esc closes it.
Keys "^a"
Keys "headings go down"
Keys "{ENTER}"
Wait-Until { $d = Rules; $d -and $d.focus -eq "Rules" -and @($d.rows | Where-Object { $_.selected -and $_.text -like "Headings go down*" }).Count } 15 "Enter to select the heading rule" | Out-Null
$tabs = 0
while ((Focused) -ne "Disable Rule" -and $tabs -lt 5) { Keys "{TAB}"; $tabs++ }
Assert-E2E ((Focused) -eq "Disable Rule") "Tab reaches Disable Rule for a built-in check (focus: $(Focused))"
Keys "{ENTER}"
$dis = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Disable Rule*" } } 15 "Enter on Disable Rule to open its dialog"
Assert-E2E ((@($dis.text) -join " ") -like "*Built-in checks*") "the Disable Rule dialog starts with Built-in checks (text: $($dis.text))"
Keys "{ESC}"
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Disable Rule*" }) } 15 "Esc to close Disable Rule" | Out-Null

Keys "{ESC}"
Wait-Until { -not (Rules) } 15 "Esc to close Rules" | Out-Null
Remove-Item $guide -Recurse -Force -ErrorAction SilentlyContinue
