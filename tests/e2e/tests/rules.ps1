# Rules, on a team guide the writer can change. With the keyboard only (real keystrokes, no clicks, #99; run it at 125%
# scaling too): Search has focus when Rules opens, Enter moves to the list, Tab goes list, details, then the buttons
# left to right, Alt+I opens Edit Rule, Esc closes it, Ctrl+F goes back to search, Disable Rule on a built-in check says
# what the check is, Esc closes Rules. Then Edit Rule rewrites a rule in the guide topic and the list shows the new
# wording, and Delete Rule removes another after the writer confirms, and the rules after it keep their references (#43).
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$guide = Join-Path (Get-E2ERoot) ("guide-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$topic = Join-Path $guide "Content\Resources\StyleGuide\Words.htm"
New-Item -ItemType Directory -Force (Split-Path $topic) | Out-Null
Set-Content (Join-Path $guide "Team.flprj") "<CatapultProject />"
Set-Content $topic -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html><body><h1>Our words</h1>
<ul><li>Say members when you mean our customers.<ul><li>Bad: Our users can sign in.</li></ul></li><li>Say sign in for the start of a session.</li><li>Spell out numbers below ten in running text.</li></ul>
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

# ---- Edit Rule and Delete Rule ----
Send-E2E command "rules" -NoWait   # a dialog: the command answers when it closes
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Rules*" -and @($_.rows).Count -gt 0 } } 60 "the Rules window with its list"
$row = @($d.rows | Where-Object { $_.text -like "Spell out numbers*" })[0]
Assert-E2E ($null -ne $row) "Rules lists the team guide's rules"
$numbersKey = $row.tip

# Edit Rule: new wording and a Good example, saved into the guide topic.
Send-E2E dialogSelect -Extra @{ title = "Rules"; text = "Say members" } | Out-Null
$d = (Get-E2EState).dialogs | Where-Object { $_.title -like "*Rules*" }
Assert-E2E (@($d.buttons | Where-Object { $_.text -eq "Edit Rule" -and $_.enabled }).Count -eq 1) "Edit Rule is enabled for a team rule (buttons: $(($d.buttons | ForEach-Object { "$($_.text)=$($_.enabled)" }) -join ' | '))"
Send-E2E dialogClick "Edit Rule" -Extra @{ title = "Rules" } -NoWait
$e = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Edit Rule*" } } 15 "the Edit Rule dialog"
Assert-E2E ((@($e.boxes | Where-Object { $_.name -eq "Bad" })[0].text) -eq "Our users can sign in.") "Edit Rule shows the rule's Bad example"
Send-E2E dialogType "Say members, not users, for our customers." -Extra @{ title = "Edit Rule"; name = "Rule" } | Out-Null
Send-E2E dialogType "Our members can sign in." -Extra @{ title = "Edit Rule"; name = "Good" } | Out-Null
Send-E2E dialogClick "Save" -Extra @{ title = "Edit Rule" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Edit Rule*" }) } 15 "Edit Rule to close" | Out-Null
$text = Get-Content $topic -Raw
Assert-E2E ($text -like "*Say members, not users, for our customers.*" -and $text -like "*Good: Our members can sign in.*" -and $text -like "*Bad: Our users can sign in.*") "the guide topic has the new wording and both examples"
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Rules*" -and @($_.rows | Where-Object { $_.text -like "Say members, not users*" }).Count -eq 1 } } 30 "the list to show the new wording"

# Delete Rule: asks first, then the rule is gone and the rule after it keeps its reference.
Send-E2E dialogSelect -Extra @{ title = "Rules"; text = "Say sign in" } | Out-Null
Send-E2E dialogClick "Delete Rule" -Extra @{ title = "Rules" } -NoWait
Confirm-MessageBox "Delete Rule" "OK"
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Rules*" -and @($_.rows).Count -gt 0 -and -not @($_.rows | Where-Object { $_.text -like "Say sign in*" }).Count } } 30 "the deleted rule to leave the list"
Assert-E2E ((Get-Content $topic -Raw) -notlike "*Say sign in*") "the guide topic no longer has the deleted rule"
$after = @($d.rows | Where-Object { $_.text -like "Spell out numbers*" })[0]
Assert-E2E ($after.tip -eq $numbersKey) "the next rule keeps its reference ($numbersKey, now $($after.tip))"
Send-E2E dialogClick "Close" -Extra @{ title = "Rules" } | Out-Null
Remove-Item $guide -Recurse -Force -ErrorAction SilentlyContinue

