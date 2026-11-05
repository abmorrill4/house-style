# Change on a "use the variable" issue inserts the variable as one tracked change (#421): Lint Topic flags the product name typed
# out in the text, Change puts the project variable there, the body then holds a MadCap:variable, and Undo, Redo and Undo again
# leave Flare alive with the text going back and forth. Before, Change only selected the text and asked the writer to use Insert > Variable.
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$run = Start-FlareE2E -Fixture $Fixture -Force
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 900 }
function Body { (Send-E2E bodyXml).value.xml }
$topic = Join-Path $run.Project "Content\alerts\review-alert-history.htm"
Send-E2E open $topic | Out-Null
$before = [regex]::Matches((Body), 'MadCap:variable').Count
Invoke-Ribbon "Lint Topic"
$rows = @((Wait-PaneDone).pane.panel.rows)
$i = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.excerpt -like "*Acme Online Banking*" })
Assert-E2E ($i -ge 0) "Lint Topic flagged the typed-out product name (rows: $(($rows.excerpt) -join ' | '))"
Send-E2E paneRow "Acme Online Banking" | Out-Null
Send-E2E click "Change" | Out-Null
Wait-Until { [regex]::Matches((Body), 'MadCap:variable').Count -gt $before } 15 "a variable in the body" | Out-Null
Save-E2EScreenshot "variable-insert-1-inserted"
Keys "^z"
Assert-E2E ([regex]::Matches((Body), 'MadCap:variable').Count -eq $before) "Undo took the variable out"
Keys "^y"
Assert-E2E ([regex]::Matches((Body), 'MadCap:variable').Count -gt $before) "Redo put the variable back"
Keys "^z"
Keys "^y"
Assert-E2E ([regex]::Matches((Body), 'MadCap:variable').Count -gt $before) "a second Undo and Redo still work"
Save-E2EScreenshot "variable-insert-2-redone"
$null = Get-E2EState
