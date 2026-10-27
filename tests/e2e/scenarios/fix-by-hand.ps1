# Fixing an issue by typing marks it fixed without a new lint, Mark as Fixed works from the row menu and stays through a
# later edit and Reopen Issues, and the same holds for Lint Project rows (#237).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$check = [string][char]0x2713
function Rows { @((Get-E2EState).pane.panel.rows) }
$faq = Join-Path $run.Project "Content\billpay\faq.htm"

Send-E2E open $faq | Out-Null
Send-E2E replace "Most changes to a payee appear" -Extra @{ text = "Most changes to a payee utilize the queue and appear" } | Out-Null
Invoke-Ribbon "Lint Topic"
$rows = @((Wait-PaneDone).pane.panel.rows)
$u = @($rows | Where-Object { $_.excerpt -like "*utiliz*" })
Assert-E2E ($u.Count -ge 1) "Lint Topic flagged utilize (rows: $(($rows.text) -join ' | '))"

# The writer's own edit: the e2e hook's replace doesn't go through House Style's write path.
Send-E2E replace "utilize" -Extra @{ text = "use" } | Out-Null
Wait-Until {
    $s = (Get-E2EState).pane.panel
    $r = @($s.rows | Where-Object { $_.excerpt -like "*utiliz*" })
    if ($r.Count -gt 0 -and $r[0].text.StartsWith($check) -and $s.status -like "*You fixed 1 issue.*") { $s }
} 15 "the utilize row to show as fixed with 'You fixed 1 issue.' in the status" | Out-Null
Save-E2EScreenshot "fix-by-hand-1-typed"

# Mark as Fixed from the row menu, on an open row that isn't the one just fixed.
$rows = Rows
$i = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.excerpt -and -not $r.text.StartsWith($check) -and $r.excerpt -notlike "*utiliz*" })
Assert-E2E ($i -ge 0) "an open issue is left to mark (rows: $(($rows.text) -join ' | '))"
$marked = $rows[$i].excerpt
$items = Send-E2E rowMenu "Mark as Fixed" -Extra @{ index = $i }
Assert-E2E ((($items.value | Out-String) -match "Go To[\s\S]*Mark as Fixed")) "Mark as Fixed is in the row menu after Go To"
Wait-Until { $s = (Get-E2EState).pane.panel; $r = @($s.rows | Where-Object { $_.excerpt -eq $marked }); if ($r.Count -ge 1 -and $r[0].text.StartsWith($check) -and $s.status -like "*Marked as fixed.*") { $s } } 15 "the marked row to show as fixed" | Out-Null

# A later edit elsewhere in the topic leaves it marked, with no second row.
Send-E2E replace "within a few minutes" -Extra @{ text = "within a minute" } | Out-Null
Start-Sleep -Seconds 3
$r = @(Rows | Where-Object { $_.excerpt -eq $marked })
Assert-E2E ($r.Count -eq 1) "exactly one row has the marked excerpt after another edit (found $($r.Count))"
Assert-E2E ($r[0].text.StartsWith($check)) "the marked row is still fixed after another edit"
Save-E2EScreenshot "fix-by-hand-2-marked"

# Close and reopen: both fixed rows stay fixed.
Send-E2E click "Close" | Out-Null
Wait-Until { -not (Get-E2EState).pane -and -not (Test-PaneOnScreen) } 10 "the pane's Close button to take the pane off screen" | Out-Null
Invoke-Ribbon "Reopen Issues"
Wait-PaneDone | Out-Null
$fixed = @(Rows | Where-Object { $_.text.StartsWith($check) })
Assert-E2E ($fixed.Count -ge 2) "both fixed rows are still fixed after Reopen Issues (fixed rows: $($fixed.Count))"

# Lint Project: a hand fix in an open file marks its row.
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
Send-E2E click "By file" | Out-Null
$faqText = Get-Content -Raw -Path $faq
$rows = Rows
$k = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r)
    $r.file -like "*faq.htm" -and $r.excerpt -and $r.excerpt -ne $marked -and -not $r.text.StartsWith($check) -and
    ([regex]::Matches($faqText, [regex]::Escape($r.excerpt))).Count -eq 1 })
Assert-E2E ($k -ge 0) "Lint Project listed an issue in faq.htm whose text occurs once (rows: $(($rows.text) -join ' | '))"
$target = $rows[$k].excerpt
Send-E2E replace $target -Extra @{ text = "zz" } | Out-Null
Wait-Until { $r = @(Rows | Where-Object { $_.file -like "*faq.htm" -and $_.excerpt -eq $target }); if ($r.Count -ge 1 -and $r[0].text.StartsWith($check)) { $r } } 15 "the project row to show as fixed after the hand edit" | Out-Null
Save-E2EScreenshot "fix-by-hand-3-project"

$rows = Rows
$j = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.file -and $r.excerpt -and -not $r.text.StartsWith($check) })
Assert-E2E ($j -ge 0) "an open project row is left to mark"
$pe = $rows[$j].excerpt; $pf = $rows[$j].file
Send-E2E rowMenu "Mark as Fixed" -Extra @{ index = $j } | Out-Null
Wait-Until { $r = @(Rows | Where-Object { $_.file -eq $pf -and $_.excerpt -eq $pe }); if ($r.Count -ge 1 -and $r[0].text.StartsWith($check)) { $r } } 15 "the project row marked as fixed" | Out-Null
