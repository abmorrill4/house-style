# Lint Topic, Exempt This Text on one open issue: that place's issue goes, Exemptions (1) appears, a second Lint Topic
# still hides it, and Remove Exemption in Exemptions brings it back (issue #29).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$check = [string][char]0x2713
function Rows { @((Get-E2EState).pane.panel.rows) }
$faq = Join-Path $run.Project "Content\billpay\faq.htm"

Send-E2E open $faq | Out-Null
Invoke-Ribbon "Lint Topic"
Wait-PaneDone | Out-Null
$rows = Rows
$i = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.excerpt -and $r.text -notlike "*$check*" })
Assert-E2E ($i -ge 0) "Lint Topic listed an open issue in faq.htm (rows: $(($rows.text) -join ' | '))"
$excerpt = $rows[$i].excerpt

# Exempt This Text asks why: the reason goes in, and the place stops being flagged.
Send-E2E rowMenu "Exempt This Text" -NoWait -Extra @{ index = $i }   # a dialog: the click answers when it closes
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" } } 10 "the Exempt This Text dialog" | Out-Null
Send-E2E dialogType "Our billing team signs off on this wording." -Extra @{ title = "Exempt This Text"; name = "Why is this text exempt?" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Exempt This Text" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" }) } 10 "the Exempt This Text dialog to close" | Out-Null
$left = Wait-Until { $s = (Get-E2EState).pane; if (-not @($s.panel.rows | Where-Object { $_.excerpt -eq $excerpt -and $_.text -notlike "*$check*" }).Count) { $s } } 15 "the exempted issue to leave the pane"
Assert-E2E (@($left.panel.rows | Where-Object { $_.excerpt -eq $excerpt -and $_.text -notlike "*$check*" }).Count -eq 0) "the exempted issue left the pane"

# A second Lint Topic keeps it gone, and the Exemptions (1) button is there after Lint Topic.
Invoke-Ribbon "Lint Topic"
$s = (Wait-PaneDone).pane
Assert-E2E (@($s.buttons | Where-Object { $_.text -eq "Exemptions (1)" }).Count -eq 1) "Exemptions (1) appears after Lint Topic (buttons: $($s.buttons.text -join ' | '))"
Assert-E2E (@($s.panel.rows | Where-Object { $_.excerpt -eq $excerpt -and $_.text -notlike "*$check*" }).Count -eq 0) "a second Lint Topic still hides the exempted issue"

# Exemptions lists it; Remove Exemption puts it back, and the next Lint Topic shows it again.
Send-E2E click "Exemptions" -NoWait   # a dialog: the click answers when it closes
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" } } 10 "the Exemptions dialog" | Out-Null
Send-E2E dialogSelect -Extra @{ title = "Exemptions"; text = $excerpt } | Out-Null
Send-E2E dialogClick "Remove Exemption" -Extra @{ title = "Exemptions" } | Out-Null
Send-E2E dialogClick "Close" -Extra @{ title = "Exemptions" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exemptions*" }) } 10 "the Exemptions dialog to close" | Out-Null

Invoke-Ribbon "Lint Topic"
$s = (Wait-PaneDone).pane
Assert-E2E (@($s.panel.rows | Where-Object { $_.excerpt -eq $excerpt -and $_.text -notlike "*$check*" }).Count -eq 1) "Remove Exemption brings the issue back after Lint Topic"
