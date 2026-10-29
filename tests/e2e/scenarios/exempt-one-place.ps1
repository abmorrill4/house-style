# Lint Topic, Exempt This Text on one issue: that text stops being flagged, and it stays gone after a re-lint with
# Exemptions (1) in the pane. Remove Exemption in Exemptions brings it back on the next lint (#29). The rule stays on
# everywhere else, so the other issues in the topic are not touched.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$before = @((Get-E2EState).pane.panel.rows)
Assert-E2E ($before.Count -gt 0) "Lint Topic lists issues to exempt"

# Exempt This Text on the first issue, with a reason. The item opens a dialog, so the verb answers only when it closes.
Send-E2E rowMenu "Exempt This Text" -Extra @{ index = 0 } -NoWait
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" } } 15 "the Exempt This Text dialog" | Out-Null
Send-E2E dialogType "The product name is always written this way." -Extra @{ title = "Exempt This Text"; name = "Why is this text exempt?" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Exempt This Text" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" }) } 15 "Exempt This Text to close" | Out-Null
$after = @((Get-E2EState).pane.panel.rows)
Assert-E2E ($after.Count -eq $before.Count - 1) "the exempt text left the rows ($($before.Count) before, $($after.Count) after)"

# Re-lint: the exempt text is still gone, and the pane counts one exemption.
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$s = (Get-E2EState).pane
Assert-E2E (@($s.buttons | Where-Object { $_.text -eq "Exemptions (1)" }).Count -eq 1) "Exemptions (1) appears after re-lint (buttons: $($s.buttons.text -join ' | '))"
Assert-E2E (@($s.panel.rows).Count -eq $after.Count) "the exempt text is still gone after re-lint ($($after.Count) rows, got $(@($s.panel.rows).Count))"

# Remove Exemption: the text is flagged again on the next lint.
Send-E2E click "Exemptions" -NoWait   # a dialog: the click answers when it closes
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" } } 10 "the Exemptions dialog"
Assert-E2E (@($d.items).Count -ge 1 -or @($d.rows).Count -ge 1) "the Exemptions dialog lists the exempt text"
Send-E2E dialogSelect -Extra @{ title = "Exemptions"; index = 0 } | Out-Null
Send-E2E dialogClick "Remove Exemption" -Extra @{ title = "Exemptions" } | Out-Null
Send-E2E dialogClick "Close" -Extra @{ title = "Exemptions" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" }) } 10 "Exemptions to close" | Out-Null

Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$back = @((Get-E2EState).pane.panel.rows)
Assert-E2E ($back.Count -eq $before.Count) "removing the exemption brings the issue back ($($before.Count) rows before, $($back.Count) now)"
