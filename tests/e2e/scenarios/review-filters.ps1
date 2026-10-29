# Level and Source filter the Issues pane: Level Errors shows only errors, By rule in a project check lists only error rules,
# Clear Filters brings every issue back, Source AI Only with no AI issues says so, and the chosen Source comes back when the
# pane reopens (#249).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$dot = [string][char]0x25CF   # the error glyph
function Wait-Tab([string]$Tab, [string]$What) { Wait-Until { $p = (Get-E2EState).pane.panel; if ($p -and $p.tab -eq $Tab) { $p } } 20 $What }

# Lint Topic: count the errors and the other issues from each row's glyph.
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$all = @((Get-E2EState).pane.panel.rows)
$errors = @($all | Where-Object { $_.text.StartsWith($dot) }).Count
$others = $all.Count - $errors
Assert-E2E ($errors -gt 0) "Lint Topic lists at least one error ($errors of $($all.Count) issues)"
Assert-E2E ($others -gt 0) "Lint Topic lists at least one issue that isn't an error ($others of $($all.Count))"

# Level Errors: only the error rows, the status counts them, and Clear Filters appears.
Send-E2E paneType "Errors" -Extra @{ name = "Level" } | Out-Null
$s = (Get-E2EState).pane
$shown = @($s.panel.rows)
Assert-E2E ($shown.Count -eq $errors) "Level Errors shows the $errors error rows (got $($shown.Count))"
Assert-E2E (@($shown | Where-Object { -not $_.text.StartsWith($dot) }).Count -eq 0) "every row is an error under Level Errors"
Assert-E2E ($s.panel.status.StartsWith("Showing $errors of $($all.Count) issues")) "the status starts Showing $errors of $($all.Count) issues (status: $($s.panel.status))"
Assert-E2E (@($s.buttons | Where-Object { $_.text -eq "Clear Filters" }).Count -eq 1) "Clear Filters is in the pane under a filter (buttons: $($s.buttons.text -join ' | '))"

# By rule in a project check (the only place the By rule radio shows): every rule row is an error, and there is one.
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
Send-E2E click "By rule" | Out-Null
$rules = @((Get-E2EState).pane.panel.rows)
Assert-E2E ($rules.Count -ge 1) "By rule lists at least one rule under Level Errors"
Assert-E2E (@($rules | Where-Object { -not $_.text.StartsWith($dot) }).Count -eq 0) "every rule row is an error under Level Errors"
Send-E2E click "This Topic" | Out-Null
Wait-Tab "topic" "the This Topic tab to show the topic's issues" | Out-Null

# Clear Filters: every issue is back, as in step 1.
Send-E2E click "Clear Filters" | Out-Null
$s = (Get-E2EState).pane
Assert-E2E (@($s.panel.rows).Count -eq $all.Count) "Clear Filters brings back all $($all.Count) issues (got $(@($s.panel.rows).Count))"

# Source AI Only: this fixture has no AI issues, so no rows and the status says no issues match.
Send-E2E paneType "AI Only" -Extra @{ name = "Source" } | Out-Null
$s = (Get-E2EState).pane
Assert-E2E (@($s.panel.rows).Count -eq 0) "Source AI Only shows no rows (got $(@($s.panel.rows).Count))"
Assert-E2E ($s.panel.status.StartsWith("No issues match these filters.")) "the status says No issues match these filters. (status: $($s.panel.status))"

# The pane closes; Lint Topic again brings it back with the Source filter still set. Then clear it.
Send-E2E click "Close" | Out-Null
Wait-Until { -not (Get-E2EState).pane } 10 "the issues pane to close" | Out-Null
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 300 | Out-Null
$f = (Get-E2EState).pane.panel.filter
Assert-E2E ($f.source -eq "AI Only") "the Source filter is remembered after the pane reopens (source: $($f.source))"
Send-E2E click "Clear Filters" | Out-Null
Assert-E2E ((Get-E2EState).pane.panel.filter.source -eq "Rules and AI") "Clear Filters puts Source back to Rules and AI"

Save-E2EScreenshot "review-filters"
