# With AI on, Lint Topic on a topic with "the user": the issue is listed once, not twice (0.3.4, reports #7).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone 300).pane.panel
Assert-E2E ($p.status -notmatch "No AI was used") "the AI review ran (status: $($p.status))"
# By what the row quotes, not its wording: an AI row rewrites the sentence and may not repeat the words.
# An AI row on the wider sentence with no fix ("Rewrite ...") is a different problem and stays by design
# (ActionRunner.Duplicates); only one that proposes a change to it repeats the lint row.
$hits = @($p.rows | Where-Object { ($_.text -match "the user" -or $_.excerpt -match "the user") -and ($_.layer -ne "ai" -or $_.text -match '^\W*Change ') })
Assert-E2E ($hits.Count -eq 1) "'the user' is listed once (rows: $($p.rows.text -join ' | '))"
# The AI may not flag that sentence on every run; say whether the duplicate filter was exercised this time.
$log = Join-Path $run.Hook "appdata\diag.log"
$dropped = @(Select-String -Path $log -Pattern "AI finding dropped" -ErrorAction SilentlyContinue).Count
Write-Host "   AI findings dropped as duplicates: $dropped$(if (-not $dropped) { ' (the AI did not repeat a lint row this run, so the filter was not exercised)' })"
