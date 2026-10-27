# Lint Topic's menu: Rules Only and Rules and AI Review. The choice is remembered, Lint Topic uses it, and it follows
# the writer to the next topic (#253). No AI in this run (No AI is on), so the AI row case is in ai-choose-checks.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$ending = "These issues come from the rules only. To add the AI review, choose Rules and AI Review on Lint Topic."
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null

Invoke-RibbonMenu -Button "Lint Topic" -Item "Rules Only"
$p = (Wait-PaneDone).pane.panel
Assert-E2E ($p.rows.Count -gt 0) "Rules Only found issues in faq.htm"
Assert-E2E ($p.rulesOnly) "the pane is a Rules Only pane"
Assert-E2E ($p.status.EndsWith($ending)) "the status ends with the Rules Only sentence (status: $($p.status))"
$choice = Get-E2ESetting LintChecks
Write-Host "   LintChecks: $choice"
Assert-E2E ($choice -eq "rules") "Rules Only is remembered"
Save-E2EScreenshot "choose-checks-rules-only"
$firstId = $p.id

Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone).pane.panel
Assert-E2E ($p.id -ne $firstId) "Lint Topic made a new pane"
Assert-E2E ($p.rulesOnly -and $p.status.EndsWith($ending)) "Lint Topic kept the remembered Rules Only choice"

Send-E2E open (Join-Path $run.Project "Content\billpay\troubleshoot.htm") | Out-Null
$p = (Wait-Until { $s = (Get-E2EState).pane; if ($s -and $s.panel.title -like "*troubleshoot.htm" -and -not $s.panel.reviewing -and $s.panel.status -notmatch '^(Linting|Reviewing)') { $s.panel } } 120 "the pane to follow to troubleshoot.htm")
Assert-E2E ($p.rulesOnly) "the pane that followed to the next topic is Rules Only"
Assert-E2E (@($p.rows | Where-Object { $_.layer -eq "ai" }).Count -eq 0) "no AI rows on the next topic"
Assert-E2E ($p.status.EndsWith($ending)) "the next topic says Rules Only too (status: $($p.status))"

Invoke-RibbonMenu -Button "Lint Topic" -Item "Rules and AI Review"
$p = (Wait-PaneDone).pane.panel
$choice = Get-E2ESetting LintChecks
Write-Host "   LintChecks: $choice"
Assert-E2E ($choice -eq "rules-ai") "Rules and AI Review is remembered"
Assert-E2E (-not $p.rulesOnly) "the pane is no longer Rules Only"
Assert-E2E (-not $p.status.EndsWith($ending) -and $p.status -match "No AI was used") "No AI in Settings still wins (status: $($p.status))"

# The tooltip: set at Flare start (the default) and again when Rules Only was chosen (the log says which).
$log = Join-Path $run.Hook "appdata\diag.log"
$set = @(Select-String -Path $log -Pattern "lint choice tooltip set on (\w+): (.+)$" -ErrorAction SilentlyContinue | ForEach-Object { $_.Matches[0].Groups[2].Value.Trim() })
Write-Host "   tooltip log: $($set -join ' | ')"
Assert-E2E ($set -contains "default" -and $set -contains "Rules Only") "Lint Topic's tooltip was set for both choices (log: $($set -join ', '))"
