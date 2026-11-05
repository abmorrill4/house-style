# The spike for Insert at the Cursor (design S1 to S3): what Flare reports for the caret, and whether Undo and Redo of a plain
# tracked insert of new blocks survive. The last step is a control: inserting a variable is known to crash on Redo, which proves
# this test can see a crash. Results are written to the host output and into the hook folder.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$topic = Join-Path $run.Project "Content\billpay\change-a-payee.htm"
Send-E2E open $topic | Out-Null
Start-Sleep -Seconds 2
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 800 }
function Body { (Send-E2E bodyXml).value.xml }
function Alive { $null = Get-E2EState; $true }
$log = @()
function Note($s) { Write-Host "   SPIKE $s"; $script:log += $s }

# S1: what does Flare report for a caret in a paragraph
Send-E2E select "You can change a payee" | Out-Null
$c = (Send-E2E caret).value
Note ("S1 selected=[{0}] nodes={1}" -f $c.selected, (($c.nodes | ForEach-Object { "$($_.name)<$($_.parent)" }) -join ","))

# A caret with nothing selected: collapse the selection with the Right key, then ask again.
Keys "{RIGHT}"
$c2 = (Send-E2E caret).value
Note ("S1 caret only: selected=[{0}] nodes={1}" -f $c2.selected, (($c2.nodes | ForEach-Object { "$($_.name)<$($_.parent)" }) -join ","))

$before = Body
Send-E2E insertBlocks '<p>New <strong>draft</strong> paragraph with <code>code</code>.</p><ul><li>One</li><li>Two</li></ul>' -Extra @{ after = "You can change a payee" } | Out-Null
$added = Body
Assert-E2E ($added -ne $before) "the insert changed the topic"
Set-Content (Join-Path $run.Hook "body-added.xml") $added
Assert-E2E ($added -match "draft") "the new paragraph is in the topic"
Note ("S2 inserted; tracked marks present: {0}" -f ($added -match "MadCap:change"))

Keys "^z"
$undone = Body
Note ("S2 after Undo: body equals the original = {0}" -f ($undone -eq $before))
Keys "^y"
$redone = Body
Note ("S2 after Redo: body equals the inserted version = {0}" -f ($redone -eq $added))
Keys "^z"; Keys "^y"
Assert-E2E (Alive) "Flare is still responding after Undo, Redo, Undo, Redo of a plain insert"
Assert-E2E ((Body) -match "draft") "the draft is in the topic after the second Redo"
Assert-E2E ($undone -eq $before) "Undo restored the original topic exactly"
Assert-E2E ($redone -eq $added) "Redo restored the inserted version exactly"

# Control: the same insert with a variable in it is known to undo fine and crash Flare on Redo. If this does not, the test above
# could not have seen a crash either.
Send-E2E insertRaw '<p>Welcome to <MadCap:variable name="General.ProductName" xmlns:MadCap="http://www.madcapsoftware.com/Schemas/MadCap.xsd" />.</p>' -Extra @{ after = "Select" } | Out-Null
$hasVar = (Body) -match "variable"
Note ("CONTROL after the raw insert: the variable is in the body = {0}" -f $hasVar)
Keys "^z"
$gone = -not ((Body) -match "General.ProductName")
Note ("CONTROL after Undo: the variable insert is gone = {0}" -f $gone)
Keys "^y"
Start-Sleep -Seconds 3
$back = (Body) -match "General.ProductName"
Note ("CONTROL after Redo: the variable insert is back = {0}" -f $back)
$crashed = $false
try { $null = Get-E2EState } catch { $crashed = $true }
Note ("CONTROL variable insert then Undo and Redo: Flare crashed or stopped responding = {0}" -f $crashed)
Set-Content (Join-Path $run.Hook "insert-spike.txt") $log

# Saving and reopening keeps the topic well formed
Send-E2E save | Out-Null
$xml = [xml](Get-Content $topic -Raw -Encoding UTF8)
Assert-E2E ($xml.DocumentElement -ne $null) "the saved topic is well formed XML"
Set-Content (Join-Path $run.Hook "insert-spike.txt") $log
Save-E2EScreenshot "insert-spike"
