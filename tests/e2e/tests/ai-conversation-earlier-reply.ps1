# Ask AI as a conversation, three turns with the real AI. The EARLIEST reply is applied first, though it is not the one selected for
# Apply (the latest is): the paragraph then reads exactly as that reply wrote it. A later reply is then refused with a clear message, not
# placed over the pending tracked changes (real Flare garbled the sentence when it was), and the topic is left as it was. One Undo takes
# the applied reply out and restores the original sentence.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
$topic = Join-Path $run.Project "Content\billpay\change-a-payee.htm"
Send-E2E open $topic | Out-Null
Start-Sleep -Seconds 2
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 800 }
function Dialog { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Ask AI*" } }
function Box([string]$name) { $d = Dialog; if ($d) { ($d.boxes | Where-Object { $_.name -eq $name }).text } }
function Paragraph { (Send-E2E acceptedText "payee").value.text }
function Norm([string]$t) { ($t -replace '\s+', ' ').Trim() }
function Suggested([int]$n) {
    $m = [regex]::Match((Box "Conversation"), "Reply $n \(a change[^\r\n]*\r?\n[\s\S]*?Suggested text: ([^\r\n]+)")
    if ($m.Success) { Norm $m.Groups[1].Value } else { $null }
}
function SendMessage([string]$text, [int]$replyNumber) {
    Send-E2E dialogType $text -Extra @{ title = "Ask AI"; name = "Message" } | Out-Null
    Send-E2E dialogClick "Send" -Extra @{ title = "Ask AI" } | Out-Null
    Wait-Until { (Box "Conversation") -match "Reply $replyNumber" } 180 "reply $replyNumber" | Out-Null
    Start-Sleep -Milliseconds 500
}

Send-E2E select "You can change a payee" | Out-Null
$original = Paragraph
Invoke-Ribbon "Ask AI"
Wait-Until { Dialog } 30 "the Ask AI box" | Out-Null
Send-E2E dialogClick "Have a conversation" -Extra @{ title = "Ask AI" } | Out-Null
Send-E2E dialogType "Make this sentence shorter." -Extra @{ title = "Ask AI"; name = "Instruction" } | Out-Null
Send-E2E dialogClick "Run" -Extra @{ title = "Ask AI" } -NoWait
Wait-Until { (Box "Conversation") -match "Reply 1" } 180 "reply 1" | Out-Null
SendMessage "Now say it a different way, keeping the word payee." 2
SendMessage "Now make it formal." 3

$one = Suggested 1; $two = Suggested 2; $three = Suggested 3
Assert-E2E ($one -and $two -and $three) "three replies proposed text (1: $one | 2: $two | 3: $three)"
Assert-E2E ($one -ne $two -and $two -ne $three) "the replies differ, so applying an earlier one is a real test"

# Reply 3 is selected by default. Choose reply 1 instead and apply it.
Send-E2E dialogType "Reply 1:" -Extra @{ title = "Ask AI"; name = "Reply to apply" } | Out-Null
Send-E2E dialogClick "Apply" -Extra @{ title = "Ask AI" } | Out-Null
Wait-Until { (Paragraph) -ne $original } 15 "reply 1 to be applied" | Out-Null
$afterOne = Paragraph
Assert-E2E ($afterOne -eq $one) "applying the earliest reply gave exactly its text (got: $afterOne | wanted: $one)"
Save-E2EScreenshot "ai-conversation-earlier-applied"

# A later reply can't be placed over reply 1's pending tracked changes: refused, with a clear message, and nothing changes.
Send-E2E dialogType "Reply 3:" -Extra @{ title = "Ask AI"; name = "Reply to apply" } | Out-Null
Send-E2E dialogClick "Apply" -Extra @{ title = "Ask AI" } | Out-Null
Start-Sleep -Seconds 1
$status = ((Dialog).text | Where-Object { $_ -match "tracked changes" })
Assert-E2E ([bool]$status) "the window says the text still has tracked changes (labels: $(((Dialog).text) -join ' | '))"
Assert-E2E ((Paragraph) -eq $afterOne) "the refused reply changed nothing"
Save-E2EScreenshot "ai-conversation-earlier-refused"
Send-E2E dialogClick "Close" -Extra @{ title = "Ask AI" } | Out-Null
Wait-Until { -not (Dialog) } 15 "the conversation to close" | Out-Null

Keys "^z"
Assert-E2E ((Paragraph) -eq $original) "one Undo took reply 1 out and restored the original sentence"
$null = Get-E2EState
