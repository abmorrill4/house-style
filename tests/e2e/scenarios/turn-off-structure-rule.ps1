# Turn off a built-in structure rule: Lint Topic lists Structure.HeadingOrder, Disable Rule for this project writes
# structure:heading-order = off into overrides.ini, and the row stays gone after a save, a new lint and Reopen Issues (#19).
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$run = Start-FlareE2E -Fixture $Fixture -Force
$topic = Join-Path $run.Project "Content\structure\heading-order.htm"
New-Item -ItemType Directory -Force (Split-Path $topic) | Out-Null
Set-Content $topic -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html><body><h2>Sign in</h2>
<h4>Start a session</h4><p>Sign in with your work account.</p>
</body></html>
"@
$ini = Join-Path $run.Project "Content\Resources\StyleGuide\overrides.ini"
# The app creates overrides.ini when the rule is turned off; make sure its folder exists.
New-Item -ItemType Directory -Force (Split-Path $ini) | Out-Null

function HeadingRows { @((Get-E2EState).pane.panel.rows | Where-Object { $_.rule -eq "Structure.HeadingOrder" }) }

# Lint Topic lists the heading-order row once; select it and Disable Rule for this project.
Send-E2E open $topic | Out-Null
Invoke-Ribbon "Lint Topic"
$pane = (Wait-PaneDone).pane.panel
$rows = @($pane.rows | Where-Object { $_.rule -eq "Structure.HeadingOrder" })
Assert-E2E ($rows.Count -eq 1) "Lint Topic lists exactly one Structure.HeadingOrder row (got $($rows.Count))"
$i = (0..($pane.rows.Count - 1) | Where-Object { $pane.rows[$_].rule -eq "Structure.HeadingOrder" })[0]
Send-E2E rowMenu "(select only)" -Extra @{ index = $i } | Out-Null
Send-E2E click "Disable Rule" -NoWait
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Disable Rule*" } } 15 "the Disable Rule dialog"
Assert-E2E ($d.text -like "*Headings go down one level at a time.*") "the Disable Rule dialog explains the rule (text: $($d.text))"
Send-E2E dialogClick "For this project only" -Extra @{ title = "Disable Rule" } | Out-Null
Send-E2E dialogType "Our style allows h4 under h2 in glossaries." -Extra @{ title = "Disable Rule"; name = "Reason" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Disable Rule" } | Out-Null
Wait-Until { (HeadingRows).Count -eq 0 } 30 "the heading-order row to leave the list" | Out-Null
Assert-E2E ((Get-Content $ini -Raw) -like "*structure:heading-order = off*") "overrides.ini turns structure:heading-order off"

# Save the topic in the editor, then lint again: the row stays off.
Send-E2E open $topic | Out-Null
[System.Windows.Forms.SendKeys]::SendWait("x^s")
Wait-PaneDone | Out-Null
Assert-E2E ((HeadingRows).Count -eq 0) "after a save there is still no heading-order row"
Invoke-Ribbon "Lint Topic"
Wait-PaneDone | Out-Null
Assert-E2E ((HeadingRows).Count -eq 0) "a new lint still has no heading-order row"

# Close the pane, then Reopen Issues shows the same results: still no heading-order row.
Send-E2E click "Close" | Out-Null
Wait-Until { -not (Test-PaneOnScreen) } 10 "the issues pane to close" | Out-Null
Invoke-Ribbon "Reopen Issues"
Wait-PaneDone | Out-Null
Assert-E2E ((HeadingRows).Count -eq 0) "Reopen Issues still has no heading-order row"
