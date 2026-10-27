# The e2e hook's editor and settings verbs in real Flare: replace makes a tracked change, select selects, save writes
# the file as Ctrl+S does, saveAs only takes a path inside the run folder, checked shows on check boxes, and paneSize
# resizes the pane or says Flare didn't (#257).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
function Fails([scriptblock]$Do) { try { & $Do | Out-Null; "" } catch { $_.Exception.Message } }
$topic = Join-Path $run.Project "Content\billpay\faq.htm"

Send-E2E open $topic | Out-Null
Wait-Until { $e = (Get-E2EState).editor; $e -and $e.path -like "*faq.htm" } 15 "the editor field to name faq.htm" | Out-Null

Send-E2E replace "within a few minutes" -Extra @{ text = "within a minute" } | Out-Null
Send-E2E select "within a minute" | Out-Null
$sel = (Get-E2EState).editor.selection
Assert-E2E ($sel -like "*within a minute*") "select selected the new words (selection: $sel)"
$saved = (Send-E2E save).value.saved
Write-Host "   save returned $saved"
Wait-Until { (Get-Content $topic -Raw) -like "*within a minute*" } 15 "the saved file to hold the new words" | Out-Null

$err = Fails { Send-E2E replace "No such words here" -Extra @{ text = "x" } }
Assert-E2E ($err -like "*No text No such words here*") "replace of missing text fails with No text (got: $err)"
$err = Fails { Send-E2E select "No such words here" }
Assert-E2E ($err -like "*No text No such words here*") "select of missing text fails with No text (got: $err)"

$err = Fails { Send-E2E saveAs "$env:SystemRoot\issues.csv" }
Assert-E2E ($err -like "*Save path must be inside the run folder*") "saveAs outside the run folder fails (got: $err)"
Send-E2E saveAs (Join-Path $run.Hook "issues.csv") | Out-Null   # a later Export would write here; nothing reads it in this scenario

Send-E2E command settings -NoWait   # a dialog: the command answers when it closes
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Settings" } } 30 "the Settings dialog" | Out-Null
Send-E2E dialogTab "Linting" -Extra @{ title = "Settings" } | Out-Null
$d = (Get-E2EState).dialogs | Where-Object { $_.title -like "*Settings" }
$boxes = @($d.buttons | Where-Object { $_.PSObject.Properties["checked"] })
Assert-E2E ($boxes.Count -gt 0) "check boxes on the Linting tab carry checked (buttons: $(($d.buttons | ForEach-Object { $_.text }) -join ' | '))"
Write-Host "   $($boxes[0].text): checked $($boxes[0].checked)"
Send-E2E dialogClick "Cancel" -Extra @{ title = "Settings" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Settings" }) } 15 "Settings to close" | Out-Null

Invoke-Ribbon "Lint Topic"
$before = (Wait-PaneDone).pane.size
try {
    $after = (Send-E2E paneSize -Extra @{ width = [Math]::Max(200, $before.width - 80); height = $before.height }).value
    Assert-E2E ($after.width -ne $before.width) "paneSize changed the pane's width ($($before.width) to $($after.width))"
    Write-Host "   paneSize resized the pane: $($before.width)x$($before.height) to $($after.width)x$($after.height)"
} catch {
    Assert-E2E ($_.Exception.Message -like "*Flare didn't resize the docked pane.*") "paneSize either resizes or says Flare didn't (got: $($_.Exception.Message))"
    Write-Host "   paneSize: Flare keeps the docked pane's size ($($before.width)x$($before.height))"
}
Assert-E2E ((Get-E2EState).pane.size.width -gt 0) "the state carries pane.size"
