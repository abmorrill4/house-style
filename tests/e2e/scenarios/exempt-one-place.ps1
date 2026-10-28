# Lint Topic, right-click an issue, Exempt This Text: that one place leaves the list, Exemptions (1) appears, and it stays
# gone after a new lint. Remove Exemption in Exemptions brings the issue back (#29).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
$rows = @((Wait-PaneDone).pane.panel.rows)
Assert-E2E ($rows.Count -gt 0) "Lint Topic found issues in faq.htm"

# The first issue whose rule lets this text be exempted: its row menu offers Exempt This Text.
$i = -1
for ($k = 0; $k -lt $rows.Count -and $i -lt 0; $k++) {
    $menu = (Send-E2E rowMenu "(select only)" -Extra @{ index = $k }).value
    if (@($menu | Where-Object { $_ -like "Exempt This Text*" }).Count -gt 0) { $i = $k }
}
Assert-E2E ($i -ge 0) "an issue in faq.htm offers Exempt This Text"
$text = $rows[$i].text
$before = @($rows | Where-Object { $_.text -eq $text }).Count

Send-E2E rowMenu "Exempt This Text" -Extra @{ index = $i } -NoWait | Out-Null   # a dialog: the click answers when it closes
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" } } 10 "the Exempt This Text reason box" | Out-Null
Send-E2E dialogType "Reviewed with the team" -Extra @{ title = "Exempt This Text"; name = "Why is this text exempt?" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Exempt This Text" } | Out-Null
Wait-Until { $s = (Get-E2EState).pane; if (@($s.buttons | Where-Object { $_.text -eq "Exemptions (1)" }).Count -eq 1) { $s } } 10 "Exemptions (1) to appear" | Out-Null

$s = (Get-E2EState).pane
Assert-E2E (@($s.panel.rows).Count -eq $rows.Count - 1) "the exempted issue left the list ($(@($s.panel.rows).Count) left, was $($rows.Count))"

# A new lint still doesn't flag that place.
Invoke-Ribbon "Lint Topic"
$again = @((Wait-PaneDone).pane.panel.rows)
Assert-E2E (@($again | Where-Object { $_.text -eq $text }).Count -eq $before - 1) "the exempted issue stays gone after a new lint"

Send-E2E click "Exemptions" -NoWait | Out-Null   # a dialog: the click answers when it closes
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" } } 10 "the Exemptions dialog" | Out-Null
Send-E2E dialogSelect -Extra @{ title = "Exemptions"; text = "faq" } | Out-Null
Send-E2E dialogClick "Remove Exemption" -Extra @{ title = "Exemptions" } | Out-Null
Send-E2E dialogClick "Close" -Extra @{ title = "Exemptions" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" }) } 10 "the Exemptions dialog to close" | Out-Null

# Removing the exemption lets the rule flag that text again.
Invoke-Ribbon "Lint Topic"
$back = @((Wait-PaneDone).pane.panel.rows)
Assert-E2E (@($back | Where-Object { $_.text -eq $text }).Count -eq $before) "Remove Exemption brings the issue back after a new lint"
Remove-Item (Join-Path $run.Project "Project\HouseStyle\Exemptions.txt") -Force -ErrorAction SilentlyContinue
