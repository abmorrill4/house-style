# Lint Topic, right-click an issue, Exempt This Text with a reason: that text leaves the list, Exemptions (1) appears, and
# it stays gone on the next lint. Remove Exemption in Exemptions brings the issue back (#29).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$rows = (Get-E2EState).pane.panel.rows
Assert-E2E (@($rows).Count -gt 0) "Lint Topic listed issues"
$before = @($rows).Count
$i = 0

$menu = (Send-E2E rowMenu "Exempt This Text" -Extra @{ index = $i }).value
Assert-E2E (@($menu | Where-Object { $_ -like "Exempt This Text*" }).Count -eq 1) "the row menu offers Exempt This Text (got: $($menu -join ' | '))"
Send-E2E dialogType "Reviewed: this wording is intentional here." -Extra @{ title = "Exempt This Text" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Exempt This Text" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" }) } 15 "Exempt This Text to close" | Out-Null

$s = (Get-E2EState).pane
Assert-E2E (@($s.panel.rows).Count -eq ($before - 1)) "the exempted issue left the list ($(@($s.panel.rows).Count) left, was $before)"
Assert-E2E (@($s.buttons | Where-Object { $_.text -eq "Exemptions (1)" }).Count -eq 1) "Exemptions (1) appears (buttons: $($s.buttons.text -join ' | '))"

# Lint again: the exempted text stays gone.
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$s = (Get-E2EState).pane
Assert-E2E (@($s.panel.rows).Count -eq ($before - 1)) "the exempted issue is still gone after linting again (rows: $(@($s.panel.rows).Count))"

# Exemptions: remove the one exemption, and the issue comes back on the next lint.
Send-E2E click "Exemptions" -NoWait   # a dialog: the click answers when it closes
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" -and @($_.rows).Count -gt 0 } } 15 "the Exemptions list"
Assert-E2E (@($d.rows).Count -eq 1) "Exemptions lists the one exemption (rows: $(@($d.rows).Count))"
$firstText = @($d.rows)[0].text
Send-E2E dialogSelect -Extra @{ title = "Exemptions"; text = $firstText } | Out-Null
Send-E2E dialogClick "Remove Exemption" -Extra @{ title = "Exemptions" } | Out-Null
$d = Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" -and @($_.rows).Count -eq 0 } } 15 "the exemption to leave the list"
Send-E2E dialogClick "Close" -Extra @{ title = "Exemptions" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" }) } 10 "Exemptions to close" | Out-Null

Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$s = (Get-E2EState).pane
Assert-E2E (@($s.panel.rows).Count -eq $before) "the issue is back after Remove Exemption (rows: $(@($s.panel.rows).Count), was $before)"
Assert-E2E (@($s.buttons | Where-Object { $_.text -like "Exemptions (*" }).Count -eq 0) "no Exemptions button is left after removing the exemption"
