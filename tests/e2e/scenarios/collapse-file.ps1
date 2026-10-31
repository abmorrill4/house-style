# Lint Project, By file: click a file heading to collapse its issues, then expand it with the keyboard (#305).
param($Fixture)
$run = Start-FlareE2E -Fixture $Fixture -Force
Send-E2E open (Join-Path $run.Project "Content\billpay\faq.htm") | Out-Null
Invoke-Ribbon "Lint Project"
Wait-PaneDone 300 | Out-Null
Send-E2E click "By file" | Out-Null

function Get-FaqRows {
    $rows = @((Get-E2EState).pane.panel.rows)
    $h = -1
    for ($k = 0; $k -lt $rows.Count; $k++) { if ($rows[$k].heading -and $rows[$k].text -match 'billpay\\faq\.htm') { $h = $k; break } }
    $own = @()
    if ($h -ge 0) { $own = @($rows | Where-Object { -not $_.heading -and $_.file -eq $rows[$h].file }) }
    return @{ Index = $h; Heading = $(if ($h -ge 0) { $rows[$h] } else { $null }); Issues = $own }
}

$before = Get-FaqRows
Assert-E2E ($before.Index -ge 0) "the By file view has a heading for billpay\faq.htm"
Assert-E2E ($before.Issues.Count -gt 0) "billpay\faq.htm has issues under its heading ($($before.Issues.Count))"
Assert-E2E (-not $before.Heading.collapsed -and $before.Heading.text.StartsWith([string][char]0x25BE)) "the heading starts expanded"
$countText = $before.Heading.text.Substring(1)

Send-E2E paneRow -Extra @{ index = $before.Index } | Out-Null
$after = Get-FaqRows
Assert-E2E ($after.Heading.collapsed -eq $true) "the heading is collapsed after the click"
Assert-E2E ($after.Heading.text.StartsWith([string][char]0x25B8)) "the collapsed heading shows the right-pointing arrow"
Assert-E2E ($after.Heading.text.Substring(1) -eq $countText) "the heading still names the same count (got: $($after.Heading.text))"
Assert-E2E ($after.Issues.Count -eq 0) "no issue rows for billpay\faq.htm remain"

Assert-E2E ((Send-E2E paneFocus).value.focused) "list focused"
[System.Windows.Forms.SendKeys]::SendWait("{RIGHT}")
Start-Sleep -Milliseconds 500
$back = Get-FaqRows
Assert-E2E ($back.Issues.Count -eq $before.Issues.Count) "the issues are back after Right ($($back.Issues.Count) of $($before.Issues.Count))"
Assert-E2E ($back.Heading.text.StartsWith([string][char]0x25BE) -and -not $back.Heading.collapsed) "the heading shows expanded again"
