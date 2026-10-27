# The issue card shows the whole rule (#247): Lint Topic, select each issue in turn, and every card shows all of its
# why text, with no scroll box of its own. Fails if no issue has a rule longer than the old 160 px box, so it can't pass
# without a long rule. The pane is as Flare docks it (there is no pane-size verb yet).
param($Fixture)
Add-Type -AssemblyName System.Drawing
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Topic"
$p = (Wait-PaneDone 300).pane.panel
$rows = @($p.rows)
Assert-E2E ($rows.Count -gt 0) "Lint Topic found issues in faq.htm (status: $($p.status))"

function Select-Row([int]$Index) {
    Send-E2E rowMenu "(select only)" -Extra @{ index = $Index } | Out-Null   # no menu item starts with this: selects the row only
    Wait-Until { $c = (Get-E2EState).pane.panel.card; if ($c -and $c.row -eq $Index -and $c.whyContentHeight -gt 0) { $c } } 15 "the card for row $Index" | Out-Null
    Start-Sleep -Milliseconds 800   # the card is laid out after the selection settles
    (Get-E2EState).pane.panel.card
}

$longest = $null; $at = -1
for ($i = 0; $i -lt [Math]::Min(10, $rows.Count); $i++) {
    $card = Select-Row $i
    Write-Host "   row ${i}: why $($card.whyContentHeight) px in a $($card.whyHeight) px box, card scrolls: $($card.cardScrolls)"
    Assert-E2E (-not $card.whyClipped) "row ${i}: the why text ($($card.whyContentHeight) px) fits its box ($($card.whyHeight) px)"
    if (-not $longest -or $card.whyContentHeight -gt $longest.whyContentHeight) { $longest = $card; $at = $i }
}

# The old box stopped at 160 logical px; the pane reports device pixels.
$g = [System.Drawing.Graphics]::FromHwnd([IntPtr]::Zero)
$old = [Math]::Round(160 * $g.DpiY / 96); $g.Dispose()
Assert-E2E ($longest.whyContentHeight -gt $old) "an issue in faq.htm has a rule longer than the old $old px box (longest: $($longest.whyContentHeight) px). Without one this check proves nothing"
$card = Select-Row $at
Assert-E2E (-not $card.whyClipped) "the longest why text ($($card.whyContentHeight) px) fits its box ($($card.whyHeight) px)"
if ($card.ruleLine) { Assert-E2E ($card.why.Contains($card.ruleLine)) "the card shows the whole rule: '$($card.ruleLine)'" }
Write-Host "   longest why (row $at): $($card.whyContentHeight) px, box $($card.whyHeight) px, card $($card.cardHeight) px, pane $($card.paneWidth)x$($card.paneHeight), card scrolls: $($card.cardScrolls)"
Save-E2EScreenshot "card-full-rule"
