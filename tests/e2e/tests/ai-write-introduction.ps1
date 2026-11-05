# Write an Introduction with the real AI: select House Style > Write an Introduction with the caret in the topic, check the preview and
# select Insert. The new paragraph is in the topic right under the heading as a tracked change, one Undo removes it exactly, and Redo
# puts it back.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
$topic = Join-Path $run.Project "Content\billpay\change-a-payee.htm"
Send-E2E open $topic | Out-Null
Start-Sleep -Seconds 2
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 800 }
function Body { (Send-E2E bodyXml).value.xml }
function Dialog { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Write an Introduction*" } }

Send-E2E select "On the main menu" | Out-Null
Keys "{RIGHT}"
$before = Body
Invoke-Ribbon "Write an Introduction"
Wait-Until { $d = Dialog; $d -and ($d.buttons | Where-Object { $_.text -eq "Insert" -and $_.enabled }) } 180 "the draft and its Insert button" | Out-Null
Save-E2EScreenshot "ai-introduction-preview"
Send-E2E dialogClick "Insert" -Extra @{ title = "Write an Introduction" } | Out-Null
Wait-Until { -not (Dialog) } 15 "the preview to close" | Out-Null

$added = Body
Assert-E2E ($added -ne $before) "the topic changed"
Assert-E2E ($added -match "MadCap:change") "the introduction is a tracked change"
$h = $added.IndexOf("</h1>")
$p = $added.IndexOf("<p", $h)
$old = $added.IndexOf("You can change a payee")
Assert-E2E ($h -ge 0 -and $p -gt $h -and $p -lt $old) "the new paragraph sits right under the heading, above the original first paragraph"

Keys "^z"
Assert-E2E ((Body) -eq $before) "one Undo took the introduction out and restored the topic exactly"
Keys "^y"
Assert-E2E ((Body) -eq $added) "Redo put the introduction back exactly"
$null = Get-E2EState
Save-E2EScreenshot "ai-write-introduction"
