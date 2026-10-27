# Lint Topic and Lint Project keep their issues side by side: the This Topic and Project tabs switch between them, neither
# lint wipes the other, and the live paths (topic switch, Go To, typing a fix and undoing it) reach both (#248).
# Flare can't resize the docked pane, so the narrow wrap is proven in NarrowPaneTests, not here.
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
$check = [string][char]0x2713
function Panel { (Get-E2EState).pane.panel }
function Rows { @((Panel).rows) }
function CaptionCount([string]$caption) { if ($caption -match '\((\d+)\)\s*$') { [int]$Matches[1] } else { -1 } }
function Wait-Tab([string]$Tab, [string]$What) { Wait-Until { $p = Panel; if ($p -and $p.tab -eq $Tab) { $p } } 20 $What }
$faq = Join-Path $run.Project "Content\billpay\faq.htm"
$settings = Join-Path $run.Project "Content\billpay\settings.htm"

# Lint Topic alone shows no tabs.
Send-E2E open $faq | Out-Null
Invoke-Ribbon "Lint Topic"
$topic = (Wait-PaneDone).pane.panel
Assert-E2E ($topic.tab -eq "topic") "Lint Topic shows the topic tab (tab: $($topic.tab))"
Assert-E2E (@($topic.tabs).Count -eq 0) "no tab bar with only Lint Topic's issues (tabs: $(@($topic.tabs) -join ' | '))"
$topicOpen = @($topic.rows | Where-Object { $_.text -notlike "*$check*" }).Count
Assert-E2E ($topicOpen -gt 0) "Lint Topic found issues in faq.htm"

# Lint Project keeps the topic's issues and shows both tabs.
Invoke-Ribbon "Lint Project"
$proj = (Wait-PaneDone 300).pane.panel
Assert-E2E ($proj.tab -eq "project") "Lint Project shows the project tab (tab: $($proj.tab))"
Assert-E2E ($proj.id -ne $topic.id) "Lint Project made its own panel"
Assert-E2E (@($proj.tabs).Count -eq 2) "both tabs show after Lint Project (tabs: $(@($proj.tabs) -join ' | '))"
Assert-E2E ($proj.tabs[0] -eq "This Topic ($topicOpen)") "the first tab counts the topic's open issues (tabs: $(@($proj.tabs) -join ' | '), expected This Topic ($topicOpen))"
Assert-E2E ($proj.tabs[1] -like "Project (*)") "the second tab counts the project's issues (tabs: $(@($proj.tabs) -join ' | '))"
Save-E2EScreenshot "pane-tabs"

# This Topic still has its issues, unchanged.
Send-E2E click "This Topic" | Out-Null
$s = Wait-Tab "topic" "the This Topic tab to show the topic's issues"
Assert-E2E ($s.id -eq $topic.id) "the This Topic tab shows the same results Lint Topic made (no new lint)"
Assert-E2E ((($s.rows.text) -join "`n") -eq (($topic.rows.text) -join "`n")) "the topic's rows are unchanged after Lint Project"

# Project still has its issues, and a Change there drops its count by one.
Send-E2E click "Project" | Out-Null
$s = Wait-Tab "project" "the Project tab to show the project's issues"
Assert-E2E ($s.id -eq $proj.id) "the Project tab shows the same results Lint Project made"
Send-E2E click "By file" | Out-Null
$rows = Rows
$i = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r)
    $r.file -and $r.file -notlike "*faq.htm" -and $r.file -notlike "*settings.htm" -and $r.excerpt -and $r.text -notlike "*$check*" -and $r.text -like "*Change*" })
Assert-E2E ($i -ge 0) "Lint Project listed an issue Change can fix in another file (rows: $(($rows.text) -join ' | '))"
$changedFile = $rows[$i].file
$before = CaptionCount (Panel).tabs[1]
Send-E2E rowMenu "(select only)" -Extra @{ index = $i } | Out-Null
Send-E2E click "Change" | Out-Null
Wait-Until { (CaptionCount (Panel).tabs[1]) -eq $before - 1 } 30 "the Project tab's count to drop from $before after Change" | Out-Null

# Close and Reopen Issues bring back the tab that was shown, with both lists.
Send-E2E click "Close" | Out-Null
Wait-Until { -not (Get-E2EState).pane -and -not (Test-PaneOnScreen) } 10 "the pane's Close button to take the pane off screen" | Out-Null
Invoke-Ribbon "Reopen Issues"
$again = (Wait-PaneDone).pane.panel
Assert-E2E ($again.tab -eq "project") "Reopen Issues shows the Project tab again (tab: $($again.tab))"
Assert-E2E ($again.id -eq $proj.id) "Reopen Issues showed the last project results"
Send-E2E click "This Topic" | Out-Null
$s = Wait-Tab "topic" "the This Topic tab after Reopen Issues"
Assert-E2E ($s.id -eq $topic.id) "the topic's results came back with Reopen Issues"

# A topic switch follows the open topic on This Topic and leaves Project alone.
Send-E2E open $settings | Out-Null
$s = Wait-Until { $p = (Wait-PaneDone).pane.panel; if ($p.title -like "*settings.htm*") { $p } } 60 "the This Topic tab to follow the open topic to settings.htm"
Assert-E2E ($s.tab -eq "topic") "the topic switch stayed on the This Topic tab (tab: $($s.tab))"
Send-E2E click "Project" | Out-Null
Wait-Tab "project" "the Project tab after the topic switch" | Out-Null
Assert-E2E ((Panel).id -eq $proj.id) "a topic switch left the project's results alone"
Send-E2E click "This Topic" | Out-Null
$s = Wait-Tab "topic" "the This Topic tab again"
Assert-E2E ($s.title -like "*settings.htm*") "This Topic still follows settings.htm (title: $($s.title))"
$openNow = @($s.rows | Where-Object { $_.text -notlike "*$check*" }).Count
Assert-E2E ((CaptionCount $s.tabs[0]) -eq $openNow) "the This Topic count ($($s.tabs[0])) matches that panel's $openNow open rows"

# Go To from the Project tab opens the file, and the topic follows without leaving Project.
Send-E2E click "Project" | Out-Null
Wait-Tab "project" "the Project tab before Go To" | Out-Null
$rows = Rows
$g = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r)
    $r.file -and $r.file -notlike "*settings.htm" -and $r.file -ne $changedFile -and $r.excerpt -and $r.text -notlike "*$check*" })
Assert-E2E ($g -ge 0) "Lint Project listed an open issue in another file to go to"
$goFile = $rows[$g].file
$name = [IO.Path]::GetFileName($goFile)
Send-E2E rowMenu "Go To" -Extra @{ index = $g } | Out-Null
Wait-Until { $e = (Get-E2EState).editor; if ($e -and $e.path -like "*$name") { $e } } 30 "Go To to open $name" | Out-Null
$p = Wait-Tab "project" "the Project tab to stay shown after Go To"
Assert-E2E ($p.id -eq $proj.id) "Go To left the project's results alone"
Send-E2E click "This Topic" | Out-Null
Wait-Until { $p = (Wait-PaneDone).pane.panel; if ($p.tab -eq "topic" -and $p.title -like "*$name*") { $p } } 60 "This Topic to follow Go To to $name" | Out-Null
Send-E2E click "Project" | Out-Null
Wait-Tab "project" "the Project tab after checking the topic" | Out-Null

# Typing a fix and undoing it, with Project shown, marks the project row fixed and then open again.
# Live Check is off in e2e runs, so only the project's own recheck can do this.
$goText = Get-Content -Raw -Path $goFile
$rows = Rows
$k = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r)
    $r.file -eq $goFile -and $r.excerpt -and $r.text -notlike "*$check*" -and ([regex]::Matches($goText, [regex]::Escape($r.excerpt))).Count -eq 1 })
Assert-E2E ($k -ge 0) "Lint Project listed an open issue in $name whose text occurs once (rows: $(($rows.text) -join ' | '))"
$target = $rows[$k].excerpt
Send-E2E replace $target -Extra @{ text = "zz" } | Out-Null
Wait-Until { $r = @(Rows | Where-Object { $_.file -eq $goFile -and $_.excerpt -eq $target }); if ($r.Count -ge 1 -and $r[0].text -like "*$check*") { $r } } 15 "the project row to show as fixed after the typed fix" | Out-Null
Assert-E2E ((Panel).tab -eq "project") "the Project tab stayed shown while the fix was typed"
Send-E2E replace "zz" -Extra @{ text = $target } | Out-Null   # a tracked edit standing in for Ctrl+Z
Wait-Until { $r = @(Rows | Where-Object { $_.file -eq $goFile -and $_.excerpt -eq $target }); if ($r.Count -ge 1 -and $r[0].text -notlike "*$check*") { $r } } 15 "the project row to be open again after the fix was reverted" | Out-Null

# Lint Project with no topic lint shows no tabs, and Go To doesn't add one.
$run = Start-FlareE2E -Fixture $Fixture -Force
Invoke-Ribbon "Lint Project"
$only = (Wait-PaneDone 300).pane.panel
Assert-E2E ($only.tab -eq "project") "Lint Project alone shows the project tab"
Send-E2E click "By file" | Out-Null
$rows = Rows
$g = [array]::FindIndex([object[]]$rows, [Predicate[object]]{ param($r) $r.file -and $r.text -notlike "*$check*" })
Assert-E2E ($g -ge 0) "Lint Project listed an issue to go to"
$name = [IO.Path]::GetFileName($rows[$g].file)
Send-E2E rowMenu "Go To" -Extra @{ index = $g } | Out-Null
Wait-Until { $e = (Get-E2EState).editor; if ($e -and $e.path -like "*$name") { $e } } 30 "Go To to open $name" | Out-Null
Start-Sleep -Seconds 2
$p = Panel
Assert-E2E (@($p.tabs).Count -eq 0) "Go To alone doesn't make a This Topic tab (tabs: $(@($p.tabs) -join ' | '))"
Assert-E2E ($p.tab -eq "project") "Project stays shown after Go To"
