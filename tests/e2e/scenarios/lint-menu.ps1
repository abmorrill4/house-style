# The Lint Topic and Help menus: Help > What's New opens, Lint Topic's Rules Only and Rules and AI Review are remembered
# and followed to the next topic (#253, no AI in this run; ai-no-double covers a review that runs), and Close then
# Reopen Issues brings the same results back without a new lint (0.3.4, reports #5).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$ending = "These issues come from the rules only. To add the AI review, choose Rules and AI Review on Lint Topic."
function WhatsNew { (Get-E2EState).dialogs | Where-Object { $_.title -like "*What's new*" } }

Invoke-RibbonMenu -Button "Help" -Item "What's New"
Wait-Until { WhatsNew } 30 "What's New to open from Help's menu" | Out-Null
Send-E2E dialogClick "Close" -Extra @{ title = "What's new" } | Out-Null
Wait-Until { -not (WhatsNew) } 15 "What's New to close" | Out-Null

Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-RibbonMenu -Button "Lint Topic" -Item "Rules Only"
$p = (Wait-PaneDone).pane.panel
Assert-E2E ($p.rows.Count -gt 0) "Rules Only found issues in faq.htm"
Assert-E2E ($p.rulesOnly) "the pane is a Rules Only pane"
Assert-E2E ($p.status.EndsWith($ending)) "the status ends with the Rules Only sentence (status: $($p.status))"
Assert-E2E ((Get-E2ESetting LintChecks) -eq "rules") "Rules Only is remembered"
Save-E2EScreenshot "lint-menu-rules-only"
$firstId = $p.id

Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone).pane.panel
Assert-E2E ($p.id -ne $firstId) "Lint Topic made a new pane"
Assert-E2E ($p.rulesOnly -and $p.status.EndsWith($ending)) "Lint Topic kept the remembered Rules Only choice"

Send-E2E open (Join-Path $run.Project "Content\billpay\troubleshoot.htm") | Out-Null
$p = (Wait-Until { $s = (Get-E2EState).pane; if ($s -and $s.panel.title -like "*troubleshoot.htm" -and -not $s.panel.reviewing -and $s.panel.status -notmatch '^(Linting|Reviewing)') { $s.panel } } 120 "the pane to follow to troubleshoot.htm")
Assert-E2E ($p.rulesOnly -and $p.status.EndsWith($ending)) "the pane that followed to the next topic is Rules Only (status: $($p.status))"

Invoke-RibbonMenu -Button "Lint Topic" -Item "Rules and AI Review"
$p = (Wait-PaneDone).pane.panel
Assert-E2E ((Get-E2ESetting LintChecks) -eq "rules-ai") "Rules and AI Review is remembered"
Assert-E2E (-not $p.rulesOnly -and $p.status -match "No AI was used") "No AI in Settings still wins (status: $($p.status))"

# Close, then Reopen Issues: the same panel comes back.
Assert-E2E (Test-PaneOnScreen) "the issues pane is on screen"
Send-E2E click "Close" | Out-Null
Wait-Until { -not (Get-E2EState).pane -and -not (Test-PaneOnScreen) } 10 "the pane's Close button to take the pane off screen" | Out-Null
Invoke-Ribbon "Reopen Issues"
$again = (Wait-PaneDone).pane.panel
Assert-E2E (Test-PaneOnScreen) "Reopen Issues put the pane back on screen"
Assert-E2E ($again.id -eq $p.id) "Reopen Issues showed the last results (a new lint makes a new panel)"
Assert-E2E (($again.rows.text -join "`n") -eq ($p.rows.text -join "`n")) "the same issues came back"
