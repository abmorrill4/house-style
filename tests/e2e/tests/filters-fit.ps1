# The Issues pane's Level and Source filters and the Rules window's filters and Level, State and Changed by columns
# show their text in full at the display scaling Flare runs at. At 200% the old fixed widths read "All" and "Rul",
# "Any stat" and "Enabl…". Run it at 200% too (the VM's scale is set before Flare starts).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force

Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
Save-E2EScreenshot "filters-fit-1-pane"
$cut = @((Get-E2EState).clipped)
Assert-E2E ($cut.Count -eq 0) "the pane's filters show every choice in full (cut off: $($cut -join ' | '))"

Invoke-Ribbon "Rules"
Wait-Until { $d = (Get-E2EState).dialogs | Where-Object { $_.title -like "*Rules" }; $d -and @($d.rows).Count -gt 0 } 60 "the Rules window with its list" | Out-Null
Save-E2EScreenshot "filters-fit-2-rules"
$cut = @((Get-E2EState).clipped)
Assert-E2E ($cut.Count -eq 0) "the Rules filters and columns show their text in full (cut off: $($cut -join ' | '))"
