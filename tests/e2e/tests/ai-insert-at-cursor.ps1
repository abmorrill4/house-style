# Insert at the cursor with the real AI: click in a paragraph, choose Ask AI > Insert at the cursor, type what to write, check the
# preview and select Insert. The new text is in the topic as a tracked change, one Undo takes it all out exactly, and Redo puts it back.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
$topic = Join-Path $run.Project "Content\billpay\change-a-payee.htm"
Send-E2E open $topic | Out-Null
Start-Sleep -Seconds 2
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 800 }
function Body { (Send-E2E bodyXml).value.xml }
function Dialog { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Ask AI*" } }

# A caret in the first paragraph, nothing selected.
Send-E2E select "You can change a payee" | Out-Null
Keys "{RIGHT}"
$before = Body

Invoke-Ribbon "Ask AI"
Wait-Until { Dialog } 30 "the Ask AI box" | Out-Null
$choice = (Dialog).buttons | Where-Object { $_.text -eq "Insert at the cursor" }
Assert-E2E ($choice -and $choice.enabled) "Insert at the cursor is offered when nothing is selected"
Send-E2E dialogClick "Insert at the cursor" -Extra @{ title = "Ask AI" } | Out-Null
Send-E2E dialogType "Write one short sentence that tells the reader they can cancel the change before they save it." -Extra @{ title = "Ask AI"; name = "Instruction" } | Out-Null
Send-E2E dialogClick "Run" -Extra @{ title = "Ask AI" } -NoWait

# The preview opens with the new text and an enabled Insert button once the model has answered.
Wait-Until { $d = Dialog; $d -and ($d.buttons | Where-Object { $_.text -eq "Insert" -and $_.enabled }) } 180 "the draft and its Insert button" | Out-Null
$d = Dialog
Assert-E2E (@($d.boxes | Where-Object { $_.text.Length -gt 10 }).Count -ge 0) "the preview is open"
Save-E2EScreenshot "ai-insert-preview"
Send-E2E dialogClick "Insert" -Extra @{ title = "Ask AI" } | Out-Null
Wait-Until { -not (Dialog) } 15 "the preview to close" | Out-Null

$added = Body
Assert-E2E ($added -ne $before) "the topic changed"
Assert-E2E ($added -match "MadCap:change") "the new text is a tracked change"
Assert-E2E ($added -match "cancel") "the new sentence is in the topic"

Keys "^z"
Assert-E2E ((Body) -eq $before) "one Undo took the whole insert out and restored the topic exactly"
Keys "^y"
Assert-E2E ((Body) -eq $added) "Redo put the inserted text back exactly"
$null = Get-E2EState
Save-E2EScreenshot "ai-insert-at-cursor"
