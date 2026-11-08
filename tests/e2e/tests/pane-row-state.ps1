# A row has one state in the This Topic and Project tabs (#450). Change in This Topic, then Save, shows the same row fixed in
# Project and drops its count. Mark as Fixed and Reopen Issue carry over both ways (#447), and so does Exempt, which stays
# listed as Exempt until Remove Exemption (#448).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$check = [string][char]0x2713
$ex = [string][char]0x2298 + " Exempt"   # the mark and word on an exempt row, which no rule text uses
function Panel { (Get-E2EState).pane.panel }
function Rows { @((Panel).rows) }
function Wait-Tab([string]$Tab, [string]$What) { Wait-Until { $p = Panel; if ($p -and $p.tab -eq $Tab) { $p } } 20 $What }
function CaptionCount([string]$caption) { if ($caption -match '\((\d+)\)\s*$') { [int]$Matches[1] } else { -1 } }
function Row-Of([string]$excerpt) { return ,@(Rows | Where-Object { $_.excerpt -eq $excerpt -and -not $_.heading }) }
$faq = Join-Path $run.Project "Content\billpay\faq.htm"

Send-E2E open $faq | Out-Null
Send-E2E replace "Most changes to a payee appear" -Extra @{ text = "Most changes to a payee utilize the queue and appear" } | Out-Null
Send-E2E save | Out-Null   # Lint Project reads files from disk
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
Send-E2E click "By file" | Out-Null
Invoke-Ribbon "Lint Topic"
Wait-PaneDone 120 | Out-Null
Wait-Tab "topic" "the This Topic tab" | Out-Null
$rows = Rows
$i = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.excerpt -like "utiliz*" -and $r.text -notlike "*$check*" })
Assert-E2E ($i -ge 0) "Lint Topic flagged utilize (rows: $(($rows.text) -join ' | '))"
$word = $rows[$i].excerpt

# Project tab count before.
Send-E2E click "Project" | Out-Null
$p = Wait-Tab "project" "the Project tab"
$projBefore = CaptionCount $p.tabs[1]
Assert-E2E ($projBefore -gt 0) "the Project tab counts open issues (tabs: $(@($p.tabs) -join ' | '))"
Send-E2E click "This Topic" | Out-Null
Wait-Tab "topic" "the This Topic tab" | Out-Null

# Change in This Topic, then Save: the Project row is fixed and the count drops.
Send-E2E rowMenu "(select only)" -Extra @{ index = $i } | Out-Null
Send-E2E click "Change" | Out-Null
Wait-Until { $r = Row-Of $word; if ($r.Count -ge 1 -and $r[0].text -like "*$check*") { $r } } 15 "the topic row to show as changed" | Out-Null
Send-E2E save | Out-Null
Start-Sleep -Seconds 3
Send-E2E click "Project" | Out-Null
$p = Wait-Tab "project" "the Project tab"
$r = @(Rows | Where-Object { $_.file -like "*billpay\faq.htm" -and $_.excerpt -eq $word })
Assert-E2E ($r.Count -ge 1 -and $r[0].text -like "*$check*") "the Project row shows as fixed after Change in This Topic and Save (rows: $(($r.text) -join ' | '))"
Assert-E2E ((CaptionCount $p.tabs[1]) -eq $projBefore - 1) "the Project count dropped by one (was $projBefore, tabs: $(@($p.tabs) -join ' | '))"
Save-E2EScreenshot "pane-row-state-1-change"

# Mark as Fixed in Project shows in This Topic, and Reopen Issue in This Topic reopens the Project row.
$rows = Rows
$k = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.file -like "*billpay\faq.htm" -and $r.excerpt -and $r.text -notlike "*$check*" -and $r.excerpt -ne $word })
Assert-E2E ($k -ge 0) "an open faq.htm row is left in Project"
$other = $rows[$k].excerpt
Send-E2E rowMenu "Mark as Fixed" -Extra @{ index = $k } | Out-Null
Send-E2E click "This Topic" | Out-Null
Wait-Tab "topic" "the This Topic tab" | Out-Null
$t = Row-Of $other
Assert-E2E ($t.Count -ge 1 -and $t[0].text -like "*$check*") "the topic row is fixed after Mark as Fixed in Project (rows: $(($t.text) -join ' | '))"
$ti = [array]::FindIndex([object[]](Rows), [Predicate[object]]{ param($r) $r.excerpt -eq $other })
$items = Send-E2E rowMenu "Reopen Issue" -Extra @{ index = $ti }
Assert-E2E ((($items.value | Out-String) -match "Reopen Issue")) "Reopen Issue is in the menu of a row marked as fixed"
Wait-Until { $t = Row-Of $other; if ($t.Count -ge 1 -and $t[0].text -notlike "*$check*") { $t } } 15 "the topic row to be open again" | Out-Null
Assert-E2E ((Panel).status -like "*Reopened.*") "the status line says Reopened (status: $((Panel).status))"
Send-E2E click "Project" | Out-Null
Wait-Tab "project" "the Project tab" | Out-Null
$r = @(Rows | Where-Object { $_.file -like "*billpay\faq.htm" -and $_.excerpt -eq $other })
Assert-E2E ($r.Count -ge 1 -and $r[0].text -notlike "*$check*") "the Project row is open again after Reopen Issue in This Topic"

# Exempt in This Topic shows Exempt in Project, and Remove Exemption in Project reopens both.
Send-E2E click "This Topic" | Out-Null
Wait-Tab "topic" "the This Topic tab" | Out-Null
$ti = [array]::FindIndex([object[]](Rows), [Predicate[object]]{ param($r) $r.excerpt -eq $other })
Send-E2E rowMenu "Exempt This Text" -Extra @{ index = $ti } -NoWait
Wait-Until { (Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" } } 15 "the Exempt This Text dialog" | Out-Null
Send-E2E dialogType "Intended here." -Extra @{ title = "Exempt This Text"; name = "Why is this text exempt?" } | Out-Null
Send-E2E dialogClick "OK" -Extra @{ title = "Exempt This Text" } | Out-Null
Wait-Until { -not ((Get-E2EState).dialogs | Where-Object { $_.title -like "*Exempt This Text*" }) } 15 "Exempt This Text to close" | Out-Null
$t = Row-Of $other
Assert-E2E ($t.Count -ge 1 -and $t[0].text -like "*$ex*") "the topic row is marked Exempt (rows: $(($t.text) -join ' | '))"
# A live check after an edit elsewhere keeps the row Exempt.
Send-E2E replace "within a few minutes" -Extra @{ text = "within a minute" } | Out-Null
Start-Sleep -Seconds 4
$t = Row-Of $other
Assert-E2E ($t.Count -ge 1 -and $t[0].text -like "*$ex*") "the row is still Exempt after a live check (rows: $(($t.text) -join ' | '))"
Send-E2E click "Project" | Out-Null
Wait-Tab "project" "the Project tab" | Out-Null
$r = @(Rows | Where-Object { $_.file -like "*billpay\faq.htm" -and $_.excerpt -eq $other })
Assert-E2E ($r.Count -ge 1 -and $r[0].text -like "*$ex*") "the Project row is marked Exempt too (rows: $(($r.text) -join ' | '))"
Save-E2EScreenshot "pane-row-state-2-exempt"
$ri = [array]::FindIndex([object[]](Rows), [Predicate[object]]{ param($x) $x.file -like "*billpay\faq.htm" -and $x.excerpt -eq $other })
Send-E2E rowMenu "Remove Exemption" -Extra @{ index = $ri } | Out-Null
Wait-Until { $r = @(Rows | Where-Object { $_.file -like "*billpay\faq.htm" -and $_.excerpt -eq $other }); if ($r.Count -ge 1 -and $r[0].text -notlike "*$ex*") { $r } } 15 "the Project row to be open again" | Out-Null
Send-E2E click "This Topic" | Out-Null
Wait-Tab "topic" "the This Topic tab" | Out-Null
$t = Row-Of $other
Assert-E2E ($t.Count -ge 1 -and $t[0].text -notlike "*$ex*" -and $t[0].text -notlike "*$check*") "the topic row is open again after Remove Exemption in Project"
Assert-E2E ((Panel).status -notmatch "is exempt\.|are exempt\.") "the This Topic status no longer says anything is exempt (status: $((Panel).status))"
Assert-E2E (-not @((Get-E2EState).pane.buttons | Where-Object { $_.text -like "Exemptions (*" }).Count) "the Exemptions button is gone with no exemptions left"
