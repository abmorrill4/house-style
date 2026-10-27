# With AI on: Rules Only shows no AI rows and Rules and AI Review runs the review (#253).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
$ending = "These issues come from the rules only. To add the AI review, choose Rules and AI Review on Lint Topic."
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null

Invoke-RibbonMenu -Button "Lint Topic" -Item "Rules Only"
$p = (Wait-PaneDone 300).pane.panel
Assert-E2E (@($p.rows | Where-Object { $_.layer -eq "ai" }).Count -eq 0) "Rules Only shows no AI rows"
Assert-E2E ($p.status.EndsWith($ending)) "the status ends with the Rules Only sentence (status: $($p.status))"

Invoke-RibbonMenu -Button "Lint Topic" -Item "Rules and AI Review"
$p = (Wait-PaneDone 300).pane.panel
Assert-E2E ($p.status -notmatch "No AI was used" -and -not $p.status.EndsWith($ending)) "the AI review ran (status: $($p.status))"
# The AI may not flag anything on every run, so zero is not a failure.
Write-Host "   AI rows: $(@($p.rows | Where-Object { $_.layer -eq 'ai' }).Count)"

# Back to the default, so later scenarios start as they do today.
$file = Join-Path $run.AppData "settings.json"
$s = Get-Content $file -Raw -Encoding UTF8 | ConvertFrom-Json
Assert-E2E ($s.LintChecks -eq "rules-ai") "the run ends on rules-ai"
