# Walks one rule place by place (#252): By Rule, a rule in two files or more, Enter starts the walk, I and N move it on (Enter
# also changes a place), Next Rule starts the next rule, By File and By Rule keep the walk, Escape ends it, and the next Lint
# Project selects the last rule walked. Reads the walk from pane.panel.walk and the place from the card and the editor.
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$run = Start-FlareE2E -Fixture $Fixture -Force
function Keys([string]$k) { [System.Windows.Forms.SendKeys]::SendWait($k); Start-Sleep -Milliseconds 500 }
function Panel { (Get-E2EState).pane.panel }
function Rows { @((Panel).rows) }
function Editor { (Get-E2EState).editor }
function PaneButtons { @((Get-E2EState).pane.buttons | ForEach-Object { $_.text }) }
function WalkNow { (Panel).walk }
function Leaf([string]$p) { if ($p) { [IO.Path]::GetFileName($p) } }
# The file of the place the walk is on: the card's "In <file>" line.
function CardFile { if ((Panel).card.head -match '(?m)^In ([^\r\n]+)') { $Matches[1] } }
function CheckMarks { @(Rows | Where-Object { $_.text -like "*$([char]0x2713)*" }).Count }
function AssertProject([string]$When) { Assert-E2E ((Panel).tab -eq "project") "the pane stays on the Project tab ($When)" }
# A key with the issues list focused (N, I and Enter give the focus back, Go To takes it away).
function Press([string]$k) { Send-E2E paneFocus | Out-Null; Keys $k }
# Sends $Key until $Done holds, at most walk.total times; fails the run if it never holds.
function KeyUntil([string]$Key, [scriptblock]$Done, [string]$What) {
    $max = [int]((WalkNow).total)
    for ($k = 0; $k -lt $max; $k++) {
        if (& $Done) { return }
        Press $Key
    }
    Assert-E2E ([bool](& $Done)) "$What (after $max keys)"
}

# 1. Lint the project and show the rules.
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
Send-E2E click "By Rule" | Out-Null

# 2. A rule in two files or more: its source reads "N in M files" with M of 2 or more.
$rows = Rows
$i = -1
for ($k = 0; $k -lt $rows.Count; $k++) { if ($rows[$k].source -match ' in (\d+) files' -and [int]$Matches[1] -ge 2) { $i = $k; break } }
Assert-E2E ($i -ge 0) "By Rule lists a rule in 2 files or more (sources: $(($rows.source) -join ' | '))"
$row = $rows[$i]
Write-Host "   row $i`: $($row.text) ($($row.source))"
Send-E2E rowMenu "(select only)" -Extra @{ index = $i } | Out-Null   # no item starts with that, so it only selects the row

# 3. Enter starts the walk on the rule's first place.
Assert-E2E ((Send-E2E paneFocus).value.focused) "paneFocus put the focus in the rule list"
Keys "{ENTER}"
$st = Wait-Until { $s = Get-E2EState; if ($s.pane.panel.walk -and $s.pane.panel.walk.position -eq 1) { $s } } 15 "Enter to start the walk on its first place"
Assert-E2E ($st.pane.panel.walk.total -ge 2) "the walk has 2 places or more (total: $($st.pane.panel.walk.total))"
$firstFile = CardFile
Assert-E2E ((Leaf $st.editor.path) -eq $firstFile) "the editor shows the first place's file (editor: $(Leaf $st.editor.path), card: $firstFile)"
Assert-E2E ($st.editor.selection.Length -gt 0) "the editor selects the first place's words"

# 4. N goes on until the editor is in another file; the list keeps the focus and the walk follows.
KeyUntil "n" { $e = Editor; [bool]$e -and (Leaf $e.path) -ne $firstFile } "N to reach a place in another file"
$s4 = Get-E2EState
Assert-E2E ((Leaf $s4.editor.path) -ne $firstFile) "the editor followed the walk to another file"
Assert-E2E ([bool]$s4.pane.panel.walk.listFocused) "N gave the list the focus back"
AssertProject "after N to another file"
$pos4 = $s4.pane.panel.walk.position
$sel4 = $s4.editor.selection
Press "n"
$s4b = Wait-Until { $s = Get-E2EState; if ($s.pane.panel.walk.position -gt $pos4) { $s } } 15 "N to move to the next place"
Assert-E2E ($s4b.editor.selection.Length -gt 0 -and $s4b.editor.selection -ne $sel4) "N selects the next place's words"

# 5. I ignores the place. On a clean profile the first I opens Ignore's reasons, so that one is answered here.
$p5 = $s4b.pane.panel.walk
Press "+i"
Keys "{DOWN}"
Keys "{ENTER}"
$s5 = Wait-Until { $s = Get-E2EState; if ($s.pane.panel.walk -and $s.pane.panel.walk.position -eq $p5.position + 1) { $s } } 15 "Ignore to move the walk to the next place"
Assert-E2E ($s5.pane.panel.walk.total -eq $p5.total) "Ignore leaves the walk's total unchanged (was $($p5.total))"
AssertProject "after Ignore"

# 6. Change a place House Style can fix in another file: Enter changes it and the walk moves on.
KeyUntil "n" { $c = CardFile; [bool]$c -and $c -ne $firstFile -and (PaneButtons -contains "Change") } "N to reach a place House Style can change in another file"
$s6 = Get-E2EState
$fileHere = CardFile
$p6 = $s6.pane.panel.walk.position
$marks = CheckMarks
Press "{ENTER}"
$s6b = Wait-Until { $s = Get-E2EState; if ($s.pane.panel.walk.position -gt $p6) { $s } } 15 "Enter to change the place and move on"
Assert-E2E ((CheckMarks) -gt $marks) "Enter shows a check mark on the changed place"
Assert-E2E ((Leaf $s6b.editor.path) -eq $fileHere) "the editor is in the changed place's file ($fileHere)"
AssertProject "after Change"

# 7. The pane stays on the Project tab: checked after each move above.

# 8. N goes on to the end: the walk says it is finished, with Next Rule.
KeyUntil "n" { [bool]((WalkNow).finished) } "N to reach the end of the rule's places"
$p8 = Panel
Assert-E2E ($p8.status -match "You've reviewed every place this rule applies\.|still open") "the finished walk says so (status: $($p8.status))"
Assert-E2E ((PaneButtons) -contains "Next Rule") "the finished walk has a Next Rule button"
AssertProject "at the end of the walk"

# 9. Next Rule starts the next rule at its first place.
$ruleBefore = (WalkNow).rule
Send-E2E click "Next Rule" | Out-Null
$s9 = Wait-Until { $s = Get-E2EState; if ($s.pane.panel.walk -and $s.pane.panel.walk.rule -ne $ruleBefore) { $s } } 15 "Next Rule to walk the next rule"
Assert-E2E ($s9.pane.panel.walk.position -eq 1) "Next Rule starts the new rule at its first place (position: $($s9.pane.panel.walk.position))"
AssertProject "after Next Rule"

# 10. By File and By Rule keep the walk: the same rule and place.
$w10 = WalkNow
Send-E2E click "By File" | Out-Null
Send-E2E click "By Rule" | Out-Null
$w10b = WalkNow
Assert-E2E ($w10b.rule -eq $w10.rule -and $w10b.position -eq $w10.position) "By File and By Rule keep the walk on the same rule and place (was $($w10.position), now $($w10b.position))"
AssertProject "after switching views"

# 11. Escape ends the walk and the rule rows are back.
Press "{ESC}"
Wait-Until { $null -eq (WalkNow) } 15 "Escape to end the walk" | Out-Null
$s11 = Get-E2EState
Assert-E2E (@($s11.pane.panel.rows | Where-Object { $_.source -match ' in \d+ file' }).Count -gt 0) "Escape brings the rule rows back"
$lastRow = $s11.pane.panel.card.row
Assert-E2E ($lastRow -ge 0) "the walked rule's row is selected"
$lastText = $s11.pane.panel.rows[$lastRow].text

# 12. Close the pane and Lint Project again: the last walked rule is selected.
Send-E2E click "Close" | Out-Null
Wait-Until { -not (Get-E2EState).pane -and -not (Test-PaneOnScreen) } 10 "the pane's Close button to take the pane off screen" | Out-Null
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
$s12 = Get-E2EState
$sel12 = $s12.pane.panel.card.row
Assert-E2E ($sel12 -ge 0 -and $s12.pane.panel.rows[$sel12].text -eq $lastText) "the next Lint Project selects the last rule walked ($lastText)"

# 13. The screenshot CI uploads.
Save-E2EScreenshot "walk-rule"
