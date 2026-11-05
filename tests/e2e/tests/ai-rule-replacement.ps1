# Issue 228: with AI on, a suggestion under a word rule uses the rule's words or the project's, and says when the writer should choose.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Ai -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone 300).pane.panel
Assert-E2E ($p.status -notmatch "No AI was used") "the AI review ran (status: $($p.status))"
$aiRows = @($p.rows | Where-Object { $_.layer -eq "ai" })
# The lint row for "the user" hides an AI row that repeats it (ai-no-double), so the word-rule check below may have no rows.
$rows = @($aiRows | Where-Object { $_.excerpt -match "end user|the user" })
foreach ($r in $rows) {
    $fixes = $r.text -match "Change "
    Assert-E2E ((-not $fixes) -or $r.text -match "administrator") "an AI fix for '$($r.excerpt)' uses the rule's word (row: $($r.text))"
    if (-not $fixes) { Assert-E2E ($r.text -match "Choose") "the row for '$($r.excerpt)' asks the writer to choose (row: $($r.text))" }
}
Assert-E2E ($aiRows.Count -gt 0) "at least one AI row was checked (status: $($p.status); rows: $($p.rows.text -join ' | '))"
Write-Host "   AI rows: $($aiRows.Count), of them under the word rule: $($rows.Count)"
