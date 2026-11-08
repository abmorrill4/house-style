# Keys in the issues pane, sent as a writer types them (SendKeys through Flare's WPF dock): Ctrl+Enter goes to the issue
# in the topic, Enter changes it, Delete opens Ignore's reasons and one is picked by name. Proves the e2e hook's paneFocus,
# paneMenu and paneType verbs and the editor field (#257, for #12's keyboard pieces).
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$run = Start-FlareE2E -Fixture $Fixture -Force
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 500 }
function Rows { @((Get-E2EState).pane.panel.rows) }

Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
# faq.htm's own issues are all Rewrite ones, which Change can't fix without AI: add a word with a plain replacement.
Send-E2E replace "Most changes to a payee appear" -Extra @{ text = "Most changes to a payee utilize the queue and appear" } | Out-Null
Invoke-Ribbon "Lint Topic"
$rows = @((Wait-PaneDone).pane.panel.rows)
# A rule row that Change can fix: not fixed yet, with words to go to and a replacement (not Rewrite, not a manual fix).
$fixable = [Predicate[object]]{ param($r) $r.excerpt -and $r.text -notlike "*$([char]0x2713)*" -and $r.text -like "*Change*" }
$i = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $fixable.Invoke($r) -and $r.excerpt -like "*utiliz*" })
if ($i -lt 0) { $i = [array]::FindIndex([object[]]$rows, $fixable) }
Assert-E2E ($i -ge 0) "Lint Topic found an issue Change can fix in faq.htm (rows: $(($rows.text) -join ' | '))"
$row = $rows[$i]
Write-Host "   row $i`: $($row.text)"

Send-E2E rowMenu "(select only)" -Extra @{ index = $i } | Out-Null   # no item starts with that, so it only selects the row
$f = (Send-E2E paneFocus).value
Assert-E2E $f.focused "paneFocus put the focus in the issues list"

Keys "^{ENTER}"   # Go To
$ed = Wait-Until { $e = (Get-E2EState).editor; if ($e -and $e.selection -like "*$($row.excerpt)*") { $e } } 15 "Ctrl+Enter to select the issue's words in the topic"
Assert-E2E ($ed.path -like "*faq.htm") "the editor shows faq.htm (path: $($ed.path))"
if ($ed.selection -ne $row.excerpt) { Write-Host "   (Go To selected '$($ed.selection)', wider than the excerpt '$($row.excerpt)' so it's unique in its text)" }
Save-E2EScreenshot "pane-keys-1-goto"

Assert-E2E ((Send-E2E paneFocus).value.focused) "paneFocus put the focus back in the list after Go To"
Keys "{ENTER}"   # Change
Wait-Until { @(Rows | Where-Object { $_.excerpt -eq $row.excerpt -and $_.text -like "*$([char]0x2713)*" }).Count } 15 "Enter to change the issue (its row starts with a check mark)" | Out-Null
Save-E2EScreenshot "pane-keys-2-change"

$open = @(Rows | Where-Object { $_.text -notlike "*$([char]0x2713)*" }).Count
if ($open -gt 0) {
    Assert-E2E ((Send-E2E paneFocus).value.focused) "paneFocus before Delete"
    Keys "{DELETE}"   # opens Ignore's reasons
    Send-E2E paneMenu "It's correct" | Out-Null
    Wait-Until { @(Rows).Count -eq $rows.Count - 1 } 15 "It's correct in this context to take the issue off the list" | Out-Null
    Save-E2EScreenshot "pane-keys-3-ignore"
} else { Write-Host "   (no open issue left to ignore)" }

try { Send-E2E paneType "x" -Extra @{ name = "No such box" } | Out-Null; $err = "" } catch { $err = $_.Exception.Message }
Assert-E2E ($err -like "*No box named No such box*") "paneType on a missing box fails with No box named (got: $err)"

# Plain letters typed in the topic never switch the pane's View or tab, and always reach the topic (#446).
# r, R, f and F used to hit the View radios' letter shortcuts, so the pane switched and took the keys.
Add-Type -AssemblyName System.Windows.Forms
$faq = Join-Path $run.Project "Content\billpay\faq.htm"
function Panel { (Get-E2EState).pane.panel }
function View { $r = @((Get-E2EState).pane.panel.rows); if (@($r | Where-Object { $_.heading -or $_.file }).Count) { "file" } else { "rule" } }
function BodyLen { ((Send-E2E bodyXml).value.xml).Length }
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
Send-E2E click "By file" | Out-Null
$phrases = 'How long does a payee take', 'Who can see a payee', 'Only the account owner', 'Contact your bank', 'people they share access with', 'If the user deletes', 'appear within a few', 'Bill pay questions'
$i = 0
foreach ($k in 'r', 'f', 'p', 't', 'R', 'F', 'P', 'T') {
    Send-E2E open $faq | Out-Null; Start-Sleep -Milliseconds 800
    Send-E2E select $phrases[$i++] | Out-Null; Start-Sleep -Milliseconds 600
    $before = BodyLen
    [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 1200
    Assert-E2E ((View) -eq "file") "typing '$k' in the topic left the pane in By file (view: $(View))"
    Assert-E2E ((Panel).tab -eq "project") "typing '$k' in the topic left the Project tab shown"
    Assert-E2E ((BodyLen) -ne $before) "typing '$k' reached the topic (body length $before -> $(BodyLen))"
}
Save-E2EScreenshot "pane-keys-4-typing"
