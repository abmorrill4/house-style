# The notices over the editor (#449). Show Issues opens the results already in the pane, at the right tab, without
# checking again; it clears the Level and Source filters so the notice's count is the list's; a notice can be closed and
# goes by itself within 8 seconds.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force -Settings @{ CheckOnSave = $true }
function Panel { (Get-E2EState).pane.panel }
function Rows { @((Panel).rows) }
function Toast { (Send-E2E toast).value }
$faq = Join-Path $run.Project "Content\billpay\faq.htm"

Invoke-Ribbon "Lint Project"
$proj = (Wait-PaneDone 300).pane.panel
Assert-E2E ($proj.tab -eq "project") "Lint Project shows the project tab"
Send-E2E open $faq | Out-Null
Send-E2E replace "Most changes to a payee appear" -Extra @{ text = "Most changes to a payee utilize the queue and appear" } | Out-Null
Send-E2E save | Out-Null
$t = Wait-Until { $x = Toast; if ($x.text -like "*Saved anyway*" -and $x.open) { $x } } 30 "the save notice"
Assert-E2E ($t.action -eq "Show Issues") "the save notice offers Show Issues"
Save-E2EScreenshot "toast-1-save"
$projRows = ((Panel).rows.text) -join "`n"

# Show Issues on the save notice goes to the topic results, and does not touch the project's.
Send-E2E toastClick | Out-Null
$s = Wait-Until { $p = Panel; if ($p -and $p.tab -eq "topic") { $p } } 30 "Show Issues to open the This Topic tab"
Send-E2E click "Project" | Out-Null
$back = Wait-Until { $p = Panel; if ($p -and $p.tab -eq "project") { $p } } 20 "the Project tab"
Assert-E2E ($back.id -eq $proj.id) "the project results are the ones Lint Project made (no new check)"
Assert-E2E ((($back.rows.text) -join "`n") -eq $projRows) "the project rows are unchanged by Show Issues"

# A close mark ends the notice.
Send-E2E save | Out-Null
Wait-Until { $x = Toast; if ($x.open) { $x } } 30 "the save notice again" | Out-Null
Send-E2E toastClose | Out-Null
Assert-E2E (-not (Toast).open) "closing the notice hides it"

# Esc closes it too, when Flare is the active window.
Send-E2E save | Out-Null
Wait-Until { $x = Toast; if ($x.open) { $x } } 30 "the save notice for Esc" | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("{ESC}"); Start-Sleep -Milliseconds 600
Assert-E2E (-not (Toast).open) "Esc closes the notice"

# It also goes by itself within 8 seconds.
Send-E2E save | Out-Null
Wait-Until { $x = Toast; if ($x.open) { $x } } 30 "the save notice a third time" | Out-Null
$gone = Wait-Until { if (-not (Toast).open) { $true } } 12 "the notice to go by itself"
Assert-E2E $gone "the notice went by itself"

# Show Issues does not wipe the writer's remembered Level.
Send-E2E click "This Topic" | Out-Null
Send-E2E paneType "Suggestions" -Extra @{ name = "Level" } | Out-Null
Send-E2E save | Out-Null
Wait-Until { $x = Toast; if ($x.open) { $x } } 30 "the save notice for the Level check" | Out-Null
Send-E2E toastClick | Out-Null
Wait-Until { $p = Panel; if ($p -and $p.tab -eq "topic" -and $p.filter.level -eq "All Levels") { $p } } 30 "Show Issues to clear the Level" | Out-Null
Invoke-Ribbon "Lint Topic"
$t2 = (Wait-PaneDone 120).pane.panel
Assert-E2E ($t2.filter.level -eq "Suggestions") "a new topic report still has the Level the writer chose (Level: $($t2.filter.level))"
Send-E2E click "Clear Filters" | Out-Null

# A remembered Level does not hide the build notice's issues: Show Issues clears it.
Send-E2E click "Project" | Out-Null
Send-E2E paneType "Suggestions" -Extra @{ name = "Level" } | Out-Null
Send-E2E command "compileStart" | Out-Null   # as Flare starting a build
$b = Wait-Until { $x = Toast; if ($x.text -like "Building.*" -and $x.open) { $x } } 300 "the build notice"
Assert-E2E ($b.action -eq "Show Issues") "the build notice offers Show Issues"
$count = if ($b.text -match '^Building\. (\d+) issues?') { [int]$Matches[1] } else { -1 }
Save-E2EScreenshot "toast-2-build"
$before = Panel
Send-E2E toastClick | Out-Null
$p = Wait-Until { $x = Panel; if ($x -and $x.tab -eq "project" -and $x.filter.level -eq "All Levels") { $x } } 30 "Show Issues to open the Project tab on All Levels"
Assert-E2E ($p.id -eq $before.id) "Show Issues used the project results already there (no new check)"
Assert-E2E ($p.status -notlike "Showing*of*") "no issue is hidden (status: $($p.status))"
Send-E2E click "By file" | Out-Null
$rows = @((Panel).rows | Where-Object { $_.file -and -not $_.heading })
Save-E2EScreenshot "toast-3-shown"
Assert-E2E ($rows.Count -eq $count) "the notice's count ($count) is the number of rows in the list ($($rows.Count))"
