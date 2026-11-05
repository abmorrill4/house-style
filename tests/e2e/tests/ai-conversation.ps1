# Ask AI as a conversation with the real AI: select a paragraph, choose Ask AI > Have a conversation, and ask for a shorter version.
# The reply arrives, a second message refines it, and Apply makes the latest proposed change as one tracked change that one Undo removes
# exactly. An answer-only first message changes nothing.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
$topic = Join-Path $run.Project "Content\billpay\change-a-payee.htm"
Send-E2E open $topic | Out-Null
Start-Sleep -Seconds 2
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 800 }
function Body { (Send-E2E bodyXml).value.xml }
function Dialog { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Ask AI*" } }
function Transcript { $d = Dialog; if ($d) { ($d.boxes | Where-Object { $_.name -eq "Conversation" }).text } }

Send-E2E select "You can change a payee" | Out-Null
$before = Body

Invoke-Ribbon "Ask AI"
Wait-Until { Dialog } 30 "the Ask AI box" | Out-Null
Send-E2E dialogClick "Have a conversation" -Extra @{ title = "Ask AI" } | Out-Null
Send-E2E dialogType "What does this sentence tell the reader?" -Extra @{ title = "Ask AI"; name = "Instruction" } | Out-Null
Send-E2E dialogClick "Run" -Extra @{ title = "Ask AI" } -NoWait

# The first reply arrives in the transcript.
Wait-Until { (Transcript) -match "Reply 1" } 180 "the first reply" | Out-Null
Assert-E2E ((Body) -eq $before) "a question changes nothing in the topic"
Save-E2EScreenshot "ai-conversation-first"

# A second message, asking for a change.
Send-E2E dialogType "Now make it shorter. Keep the word payee." -Extra @{ title = "Ask AI"; name = "Message" } | Out-Null
Send-E2E dialogClick "Send" -Extra @{ title = "Ask AI" } | Out-Null
Wait-Until { (Transcript) -match "Reply 2" } 180 "the second reply" | Out-Null
$t = Transcript
Assert-E2E ($t -match "Reply 2 \(a change") "the second reply is a proposed change (transcript: $t)"
Wait-Until { $d = Dialog; $d -and ($d.buttons | Where-Object { $_.text -eq "Apply" -and $_.enabled }) } 20 "Apply to be enabled" | Out-Null
Save-E2EScreenshot "ai-conversation-second"

Send-E2E dialogClick "Apply" -Extra @{ title = "Ask AI" } | Out-Null
$added = Body
Assert-E2E ($added -ne $before) "Apply changed the topic"
Assert-E2E ($added -match "MadCap:change") "the change is tracked"
Send-E2E dialogClick "Close" -Extra @{ title = "Ask AI" } | Out-Null
Wait-Until { -not (Dialog) } 15 "the conversation to close" | Out-Null

Keys "^z"
Assert-E2E ((Body) -eq $before) "one Undo took the whole change out and restored the topic exactly"
Keys "^y"
Assert-E2E ((Body) -eq $added) "Redo put the change back exactly"
$null = Get-E2EState
Save-E2EScreenshot "ai-conversation"
