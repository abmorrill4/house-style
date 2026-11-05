# Lint Project, By file: clicking a file heading collapses the file, and Right on the heading expands it again (#305).
param($Fixture)
Add-Type -AssemblyName System.Windows.Forms
$run = Start-FlareE2E -Fixture $Fixture -Force
function Rows { @((Get-E2EState).pane.panel.rows) }
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
Send-E2E click "By file" | Out-Null

$rows = Rows
$head = @($rows | Where-Object { $_.heading -eq $true -and $_.text -like '*billpay\faq.htm*' })
Assert-E2E ($head.Count -eq 1) "By file has a heading for billpay\faq.htm"
$issues = @($rows | Where-Object { $_.file -like '*billpay\faq.htm' })
$n = $issues.Count
Assert-E2E ($n -gt 0) "billpay\faq.htm has issue rows under its heading"
Assert-E2E (-not $head[0].collapsed) "the heading starts expanded"
$before = $head[0].text
Assert-E2E ($before.StartsWith([string][char]0x25BE)) "the expanded heading starts with the down arrow (got: $before)"

Send-E2E paneRow "billpay\faq.htm" | Out-Null
$rows = Rows
$head = @($rows | Where-Object { $_.heading -eq $true -and $_.text -like '*billpay\faq.htm*' })
Assert-E2E ($head.Count -eq 1 -and $head[0].collapsed -eq $true) "the heading shows collapsed"
Assert-E2E (@($rows | Where-Object { $_.file -like '*billpay\faq.htm' }).Count -eq 0) "the file's issue rows are gone"
Assert-E2E ($head[0].text.StartsWith([string][char]0x25B8)) "the collapsed heading starts with the right arrow (got: $($head[0].text))"
$countOf = { param($t) if ($t -match '(\d[\d,.]*) issues?\s*$') { $Matches[1] } else { $null } }
Assert-E2E ((& $countOf $head[0].text) -eq (& $countOf $before)) "the issue count is the same collapsed ($before, then $($head[0].text))"

Assert-E2E ((Send-E2E paneFocus).value.focused) "list focused"
[System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
Start-Sleep -Milliseconds 500
$rows = Rows
$head = @($rows | Where-Object { $_.heading -eq $true -and $_.text -like '*billpay\faq.htm*' })
Assert-E2E ($head.Count -eq 1 -and -not $head[0].collapsed) "Right expands the file"
Assert-E2E (@($rows | Where-Object { $_.file -like '*billpay\faq.htm' }).Count -eq $n) "the file's $n issue rows are back"
Assert-E2E ($head[0].text.StartsWith([string][char]0x25BE)) "the expanded heading starts with the down arrow again (got: $($head[0].text))"
