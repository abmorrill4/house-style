# The issue card shows the whole rule (#247): Lint Topic, select each issue in turn, and every card shows all of its
# why text, with no scroll box of its own. A team guide adds one rule with a long reason and an example that fires in
# faq.htm, and the check fails if no issue has a rule longer than the old 160 px box, so it can't pass without a long
# rule. It runs at the pane size Flare docks it at, then in a tall pane (paneSize) where the card must grow.
param($Fixture)
Add-Type -AssemblyName System.Drawing
$guide = Join-Path (Get-E2ERoot) ("guide-" + (Get-Date -Format "yyyyMMdd-HHmmss"))
$topic = Join-Path $guide "Content\Resources\StyleGuide\Words.htm"
New-Item -ItemType Directory -Force (Split-Path $topic) | Out-Null
Set-Content (Join-Path $guide "Team.flprj") "<CatapultProject />"
Set-Content $topic -Encoding UTF8 @"
<?xml version="1.0" encoding="utf-8"?>
<html><body><h1>Our words</h1>
<table>
<tr><th>Avoid</th><th>Use</th><th>Severity</th><th>Why</th><th>Example</th></tr>
<tr id="rule-1"><td>account owner</td><td>account holder</td><td>warning</td><td>Say account holder, the term our agreements, statements and the mobile app use for the person who opened the account. Account owner reads as a legal title that some members don't have, for example a joint holder, a custodian for a minor or someone with power of attorney. Use the same term in every topic, snippet and screen so members and support agents know they mean the same person, and check the glossary before you add a new term for a role.</td><td>Only the account owner can add a payee.</td></tr>
</table>
</body></html>
"@
$run = Start-FlareE2E -Fixture $Fixture -Force -Settings @{ StyleLayers = $guide }
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

# In a tall pane the card grows to show the long rule: it fits, or it stops at about 60% of the pane and scrolls.
$sized = $null
try { $sized = (Send-E2E paneSize -Extra @{ width = 300; height = 800 }).value } catch { Write-Host "   (no tall pane on this machine: $($_.Exception.Message))" }
if ($sized -and $sized.height -ge 700) {
    $card = Wait-Until { $c = (Get-E2EState).pane.panel.card; if ($c -and $c.paneHeight -ge 700 -and $c.cardHeight -gt 0) { $c } } 15 "the card in the tall pane"
    Start-Sleep -Milliseconds 800
    $card = (Get-E2EState).pane.panel.card
    Write-Host "   tall pane $($card.paneWidth)x$($card.paneHeight): card $($card.cardHeight) px, why $($card.whyContentHeight) px in $($card.whyHeight) px, card scrolls: $($card.cardScrolls)"
    Assert-E2E (-not $card.whyClipped) "in a tall pane the why text ($($card.whyContentHeight) px) fits its box ($($card.whyHeight) px)"
    Assert-E2E ($card.cardHeight -gt $card.whyContentHeight) "in a tall pane the card ($($card.cardHeight) px) grows past the why text ($($card.whyContentHeight) px)"
    Assert-E2E ((-not $card.cardScrolls) -or $card.cardHeight -ge 0.45 * $card.paneHeight) "a card that still scrolls has grown to about 60% of the list and card area ($($card.cardHeight) of $($card.paneHeight) px)"
    Save-E2EScreenshot "card-full-rule-tall"
}
Remove-Item $guide -Recurse -Force -ErrorAction SilentlyContinue
